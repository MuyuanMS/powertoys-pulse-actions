using System.Diagnostics;
using System.Text;

namespace Pulse.Host;

internal sealed record GhCommandResult(int ExitCode, string Output);
internal delegate Task<GhCommandResult> GhCommandRunner(ProcessStartInfo start, string? input, int maximumOutputBytes);

internal static class GhCli
{
    internal static ProcessStartInfo Start(params string[] arguments)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "gh.exe" : "gh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Saved gh accounts remain authoritative, even when Host inherits another product's PAT.
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_DEBUG", "DEBUG", "GH_HOST", "GH_REPO", "GH_FORCE_TTY" })
            start.Environment.Remove(name);
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["NO_COLOR"] = "1";
        return start;
    }

    internal static async Task<GhCommandResult> RunAsync(ProcessStartInfo start, string? input, int maximumOutputBytes)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("GitHub CLI could not start.");
        var output = ReadBoundedAsync(process.StandardOutput.BaseStream, maximumOutputBytes, process);
        // Credentials and raw CLI errors are never returned, persisted, or logged.
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(), output, error);
            return new GhCommandResult(process.ExitCode, await output);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(output, error); } catch (Exception) { }
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, int limit, Process process)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > limit)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new IOException("GitHub CLI output exceeded the supported limit.");
            }
            buffer.Write(chunk, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }
}
