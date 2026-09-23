using System.ComponentModel;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class DiscoveryScenarios
{
    internal static async Task RunAllAsync()
    {
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.Combine(temporary, "PulseDiscoveryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ExistingExecutablesAreSelectable(root);
            await OptionalVersionFailuresDoNotRejectSelection(root);
            await ExactSelectionDoesNotSwitchInstallations(root);
            await MissingAndInvalidPathsDoNotExecute(root);
        }
        finally
        {
            Check(Path.GetDirectoryName(Path.GetFullPath(root))!.Equals(temporary, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("PulseDiscoveryTests-", StringComparison.Ordinal), "Discovery fixtures remain inside their dedicated temporary directory.");
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("PASS discovery: existing native executables remain selectable, version reads are optional, and exact paths never switch installations (offline; no CLI processes)");
    }

    private static async Task ExistingExecutablesAreSelectable(string root)
    {
        foreach (var agent in new[] { "codex", "copilot" })
        foreach (var version in new[] { "0.154.0", "9.99.99", "1.0.83-5", "unknown" })
        {
            var path = Candidate(root, agent + "-" + version, agent);
            var calls = new List<string>();
            Task<string> Probe(string actual, IReadOnlyList<string> arguments)
            {
                Check(actual == path, "Only the chosen executable is queried.");
                calls.Add(string.Join(' ', arguments));
                return Task.FromResult((agent == "codex" ? "codex-cli " : "") + version);
            }
            var result = await RuntimeService.ProbeAgentCandidatesAsync(agent, [path], Probe);
            Check(result["available"]?.GetValue<bool>() == true && result["fileExists"]?.GetValue<bool>() == true && result["path"]?.GetValue<string>() == path,
                "An existing executable remains selectable regardless of release or adjacent helpers.");
            Check(calls.SequenceEqual(["--version"]) && result["capabilities"] is null && result["error"] is null,
                "Discovery may query display version only; it does not inspect help or classify compatibility.");
            Check(result["authentication"]?.GetValue<string>() == "not_probed" && result["executionValidated"]?.GetValue<bool>() == false,
                "A listed executable does not claim verified login or task execution.");
            Check(version == "unknown" ? result["version"] is null : result["version"]?.GetValue<string>() == version.Split('-')[0],
                "Version is optional display metadata and does not act as an allowlist.");
        }
    }

    private static async Task OptionalVersionFailuresDoNotRejectSelection(string root)
    {
        Exception[] failures = [new IOException("Version output unavailable."), new Win32Exception(5, "Version query could not launch."),
            new InvalidOperationException("Version process error."), new OperationCanceledException("Version query timed out.")];
        for (var index = 0; index < failures.Length; index++)
        {
            var path = Candidate(root, "failed-version-" + index);
            var result = await RuntimeService.ProbeAgentCandidatesAsync("codex", [path], (_, arguments) =>
            {
                Check(arguments.SequenceEqual(["--version"]), "A failing version query must not be followed by other probe commands.");
                return Task.FromException<string>(failures[index]);
            });
            Check(result["available"]?.GetValue<bool>() == true && result["fileExists"]?.GetValue<bool>() == true && result["version"] is null &&
                result["error"] is null && result["versionProbeError"] is not null,
                "An optional version failure cannot reject the user's existing executable selection.");
        }
    }

    private static async Task ExactSelectionDoesNotSwitchInstallations(string root)
    {
        var selected = Candidate(root, "selected");
        var alternate = Candidate(root, "alternate");
        var calls = new List<string>();
        Task<string> Probe(string path, IReadOnlyList<string> arguments)
        {
            calls.Add(path);
            return Task.FromException<string>(new IOException("Optional version query failed."));
        }
        var result = await RuntimeService.ProbeAgentCandidatesAsync("codex", [selected, alternate], Probe);
        Check(result["path"]?.GetValue<string>() == selected && result["available"]?.GetValue<bool>() == true && calls.SequenceEqual([selected]),
            "Version-query errors do not silently switch the selected installation.");
        File.Delete(selected);
        result = await RuntimeService.ProbeAgentCandidatesAsync("codex", [selected], Probe);
        Check(result["available"]?.GetValue<bool>() == false && result["error"]?["code"]?.GetValue<string>() == "CLI_NOT_FOUND" && calls.Count == 1,
            "A removed exact snapshot reports its absence without falling back to another executable.");
    }

    private static async Task MissingAndInvalidPathsDoNotExecute(string root)
    {
        var wrapper = Path.Combine(root, "codex.cmd");
        File.WriteAllText(wrapper, "fixture");
        var count = 0;
        foreach (var candidates in new[] { Array.Empty<string>(), new[] { Path.Combine(root, "missing.exe") }, new[] { wrapper }, new[] { "codex.exe" }, new[] { root } })
        {
            var result = await RuntimeService.ProbeAgentCandidatesAsync("codex", candidates, (_, _) => { count++; return Task.FromResult("9.99.99"); });
            Check(result["available"]?.GetValue<bool>() == false && result["error"]?["code"]?.GetValue<string>() == "CLI_NOT_FOUND",
                "Missing files, directories, relative paths and shell wrappers are not native executable selections.");
        }
        var unknown = await RuntimeService.ProbeAgentCandidatesAsync("other", [Candidate(root, "unknown")], (_, _) => { count++; return Task.FromResult("9.99.99"); });
        Check(count == 0 && unknown["error"]?["code"]?.GetValue<string>() == "CLI_NOT_SUPPORTED", "Only supported agent names can reach executable queries.");
    }

    private static string Candidate(string root, string name, string agent = "codex")
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, agent + ".exe");
        File.WriteAllBytes(path, []);
        return path;
    }

    private static void Check(bool condition, string message) => CoreScenarios.Check(condition, message);
}
