using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>Host observations of repository boundaries; never inferred from agent prose or a model-supplied path.</summary>
public static class ReviewProvenance
{
    internal static async Task<JsonObject> CaptureAsync(string folder)
    {
        var captured = Protocol.Now();
        try
        {
            var git = RuntimeService.ResolveExecutable("git") ?? throw new IOException("Git is unavailable.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var head = (await ReadAsync(git, folder, ["rev-parse", "--verify", "HEAD"], deadline.Token)).Trim();
            if (!Regex.IsMatch(head, "^[a-fA-F0-9]{40}$")) throw new IOException("The repository revision is unknown.");
            var status = await ReadAsync(git, folder, ["status", "--porcelain=v1", "--untracked-files=normal"], deadline.Token);
            var diff = await ReadAsync(git, folder, ["diff", "--no-ext-diff", "--no-textconv", "--binary", "HEAD", "--"], deadline.Token);
            var finalHead = (await ReadAsync(git, folder, ["rev-parse", "--verify", "HEAD"], deadline.Token)).Trim();
            if (!Regex.IsMatch(finalHead, "^[a-fA-F0-9]{40}$")) throw new IOException("The repository revision is unknown.");
            return new JsonObject
            {
                ["capturedAt"] = captured, ["headSha"] = finalHead.ToLowerInvariant(),
                ["workingTree"] = status.Length == 0 && diff.Length == 0 && head == finalHead ? "clean" : "modified",
                ["diffHash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(status + "\n" + diff))).ToLowerInvariant()
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or ProtocolException)
        {
            return new JsonObject { ["capturedAt"] = captured, ["headSha"] = null, ["workingTree"] = "unknown", ["diffHash"] = null };
        }
    }

    internal static JsonObject Compose(string? expectedSha, JsonObject start, JsonObject end)
    {
        var bothKnown = Text(start, "workingTree") != "unknown" && Text(end, "workingTree") != "unknown";
        var original = !string.IsNullOrEmpty(expectedSha) && Text(start, "headSha") == expectedSha && Text(end, "headSha") == expectedSha &&
            Text(start, "workingTree") == "clean" && Text(end, "workingTree") == "clean";
        return new JsonObject
        {
            ["version"] = 1, ["source"] = "host", ["observation"] = "boundary-snapshots", ["expectedHeadSha"] = expectedSha,
            ["subject"] = original ? "original-pr" : bothKnown ? "local-candidate" : "unknown",
            ["start"] = start.DeepClone(), ["end"] = end.DeepClone()
        };
    }

    internal static bool MatchesOriginal(JsonObject? provenance, string? expectedSha) =>
        provenance?["version"] is JsonValue version && version.TryGetValue<int>(out var number) && number == 1 &&
        Text(provenance, "source") == "host" && Text(provenance, "subject") == "original-pr" &&
        !string.IsNullOrEmpty(expectedSha) && Text(provenance, "expectedHeadSha") == expectedSha &&
        Text(provenance?["start"] as JsonObject, "headSha") == expectedSha && Text(provenance?["end"] as JsonObject, "headSha") == expectedSha &&
        Text(provenance?["start"] as JsonObject, "workingTree") == "clean" && Text(provenance?["end"] as JsonObject, "workingTree") == "clean";

    private static async Task<string> ReadAsync(string git, string folder, string[] arguments, CancellationToken token)
    {
        using var process = WindowsProcess.Cli(git, arguments, folder,
            new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never", ["GIT_OPTIONAL_LOCKS"] = "0" });
        var output = ReadBoundedAsync(process.Output!, token); var error = ReadBoundedAsync(process.Error!, token);
        process.Input!.Close(); process.Resume();
        try
        {
            await process.WaitAsync(token);
            process.Kill();
            var text = await output; await error;
            if (process.ExitCode != 0) throw new IOException("Git could not inspect the verification source.");
            return text;
        }
        finally
        {
            process.Kill();
            try { await Task.WhenAll(output, error); } catch { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var builder = new StringBuilder(); var buffer = new char[8192]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (builder.Length + count > 2 * 1024 * 1024) throw new IOException("The repository observation exceeds its bounded budget.");
            builder.Append(buffer, 0, count);
        }
        return builder.ToString();
    }

    private static string? Text(JsonObject? value, string key) => value?[key] is JsonValue field && field.TryGetValue<string>(out var text) ? text : null;
}
