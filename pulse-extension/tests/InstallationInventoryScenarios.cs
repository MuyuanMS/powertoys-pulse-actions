using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Informational inventory contracts using empty files and injected version probes only.</summary>
internal static class InstallationInventoryScenarios
{
    internal static async Task RunAllAsync()
    {
        using var fixture = new Fixture();
        await EveryExecutableIsSelectable(fixture);
        await CanonicalAliasesPreserveProvenance(fixture);
        await ResolutionAndVersionFailuresRemainSelectable(fixture);
        await StandaloneCurrentAndHistoryMetadata(fixture);
        await BoundsParallelVersionProbes(fixture);
        await VersionTimeoutsRemainSelectable(fixture);
        await OverallDeadlineRetainsUnprobedRows(fixture);
        Console.WriteLine("PASS installation inventory: all executable versions selectable, informational version-only probes, canonical aliases, current/history metadata, concurrency and deadlines (offline)");
    }

    private static async Task EveryExecutableIsSelectable(Fixture fixture)
    {
        foreach (var agent in new[] { "codex", "copilot" })
        {
            string[] outputs = [agent + " 0.153.4", agent + " 0.145.0", agent + " 9.99.99", "custom development build"];
            var paths = Enumerable.Range(0, outputs.Length).Select(index => fixture.Executable(agent + "-version-" + index, agent)).ToArray();
            var calls = new ConcurrentQueue<(string Path, string Arguments)>();
            Task<string> Probe(string path, IReadOnlyList<string> arguments)
            {
                calls.Enqueue((path, string.Join(' ', arguments)));
                return Task.FromResult(outputs[Array.IndexOf(paths, path)]);
            }
            var rows = await RuntimeService.ListAgentInstallationsAsync(agent, Candidates(paths), Probe, path => path);
            Check(rows.Count == paths.Length, "Every existing executable must be listed even with no helper files or an unrecognized version.");
            Available(Find(rows, paths[0]), "0.153.4"); Available(Find(rows, paths[1]), "0.145.0"); Available(Find(rows, paths[2]), "9.99.99");
            Available(Find(rows, paths[3]));
            Check(Text(Find(rows, paths[3]), "version").Length == 0, "Unknown version text must not disable selection.");
            Check(calls.Count == paths.Length && calls.All(call => call.Arguments == "--version"), "Only one --version call is allowed per executable; inventory must never probe help interfaces.");
            foreach (var row in rows.OfType<JsonObject>())
                Check(Text(row, "source") == "fixture" && Text(row, "resolvedPath").Length > 0 && Strings(row, "sources").Contains("fixture") &&
                    Strings(row, "aliases").Contains(Text(row, "path"), StringComparer.OrdinalIgnoreCase) &&
                    row["authentication"]?.GetValue<string>() == "not_probed" && row["executionValidated"]?.GetValue<bool>() == false,
                    "Inventory must preserve provenance without claiming authentication or execution validation.");
        }
    }

    private static async Task CanonicalAliasesPreserveProvenance(Fixture fixture)
    {
        var first = fixture.Executable("alias-first"); var second = fixture.Executable("alias-second");
        var physical = fixture.Executable("alias-physical"); var distinct = fixture.Executable("alias-identical-bytes-distinct");
        var calls = new ConcurrentQueue<(string Path, string Arguments)>();
        Task<string> Probe(string path, IReadOnlyList<string> arguments) { calls.Enqueue((path, string.Join(' ', arguments))); return Task.FromResult("codex 9.99.99"); }
        AgentInstallationCandidate[] candidates = [new(first, "PATH"), new(second, "WinGet link"), new(physical, "package directory"), new(first.ToUpperInvariant(), "PATH"), new(distinct, "manual")];
        var rows = await RuntimeService.ListAgentInstallationsAsync("codex", candidates, Probe,
            path => path.Equals(distinct, StringComparison.OrdinalIgnoreCase) ? distinct : path.Equals(second, StringComparison.OrdinalIgnoreCase) ? physical.ToUpperInvariant() : physical);
        Check(rows.Count == 2, "Aliases must deduplicate by physical path while identical bytes in distinct executables remain separate installations.");
        var combined = Find(rows, first); Available(combined, "9.99.99"); Available(Find(rows, distinct), "9.99.99");
        Check(Text(combined, "path") == first && Text(combined, "source") == "PATH" && Text(combined, "resolvedPath").Equals(physical, StringComparison.OrdinalIgnoreCase),
            "Deduplication must preserve the first path/source and expose the canonical executable.");
        var aliases = Strings(combined, "aliases");
        Check(new[] { first, second, physical }.All(path => aliases.Contains(path, StringComparer.OrdinalIgnoreCase)) && aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() == aliases.Count,
            "All aliases must survive without case-only duplicates.");
        Check(Strings(combined, "sources").ToHashSet(StringComparer.Ordinal).SetEquals(["PATH", "WinGet link", "package directory"]), "All discovery sources must survive deduplication.");
        Check(calls.Count == 2 && calls.Count(call => call.Path.Equals(physical, StringComparison.OrdinalIgnoreCase)) == 1 && calls.All(call => call.Arguments == "--version"),
            "Each physical installation must receive exactly one informational version probe.");
    }

    private static async Task ResolutionAndVersionFailuresRemainSelectable(Fixture fixture)
    {
        var unresolved = fixture.Executable("resolution-null"); var denied = fixture.Executable("resolution-denied"); var invalid = fixture.Executable("resolution-invalid");
        var broken = fixture.Executable("version-failed"); var cancelled = fixture.Executable("version-cancelled"); var healthy = fixture.Executable("failure-neighbor");
        string[] paths = [unresolved, denied, invalid, broken, cancelled, healthy];
        var calls = new ConcurrentQueue<(string Path, string Arguments)>();
        Task<string> Probe(string path, IReadOnlyList<string> arguments)
        {
            calls.Enqueue((path, string.Join(' ', arguments)));
            if (path == broken) return Task.FromException<string>(new IOException("Synthetic version failure."));
            if (path == cancelled) return Task.FromException<string>(new OperationCanceledException("Synthetic version cancellation."));
            return Task.FromResult("codex 9.99.99");
        }
        var rows = await RuntimeService.ListAgentInstallationsAsync("codex", Candidates(paths), Probe,
            path => path == unresolved ? null : path == denied ? throw new UnauthorizedAccessException("Synthetic physical-path failure.") : path == invalid ? "relative.exe" : path);
        Check(rows.Count == paths.Length, "Canonical-path and version failures must retain every installed executable as selectable.");
        foreach (var path in paths)
        {
            var row = Find(rows, path); Available(row);
            Check(Text(row, "resolvedPath") == path && Strings(row, "aliases").Contains(path, StringComparer.OrdinalIgnoreCase),
                "Failed physical-path resolution must fall back to the original absolute executable path.");
            Check(calls.Count(call => call.Path == path && call.Arguments == "--version") == 1, "Each installation may receive only its informational version call.");
        }
        Check(Text(Find(rows, broken), "version").Length == 0 && Text(Find(rows, cancelled), "version").Length == 0,
            "Failed version information must remain unknown without blocking selection.");
        Available(Find(rows, healthy), "9.99.99");
        Check(calls.All(call => call.Arguments == "--version"), "Failures must never cause help probes or additional compatibility checks.");
    }

    private static async Task StandaloneCurrentAndHistoryMetadata(Fixture fixture)
    {
        var alias = fixture.Executable("standalone-current-link"); var physical = fixture.Executable("standalone-current-release");
        var older = fixture.Executable("standalone-previous-release"); var independent = fixture.Executable("standalone-independent");
        string? Resolve(string path) => path == alias ? physical : path;
        var current = RuntimeService.ClassifyStandaloneCandidate(alias, physical, false, Resolve);
        var currentRelease = RuntimeService.ClassifyStandaloneCandidate(physical, physical.ToUpperInvariant(), true, Resolve);
        var previous = RuntimeService.ClassifyStandaloneCandidate(older, physical, true, Resolve);
        var unknown = RuntimeService.ClassifyStandaloneCandidate(older, null, true, Resolve);
        var generic = RuntimeService.ClassifyStandaloneCandidate(independent, physical, false, Resolve);
        Check(current.Source == "Standalone (current)" && current.IsCurrent == true && current.Historical != true &&
            currentRelease.Source == "Standalone (current)" && currentRelease.IsCurrent == true && currentRelease.Historical != true,
            "A current alias and its matching release must both be classified as current by physical path.");
        Check(previous.Source == "Standalone (previous release)" && previous.IsCurrent != true && previous.Historical == true,
            "A distinct release is historical only when another current physical executable is known.");
        Check(unknown.Source == "Standalone release" && unknown.IsCurrent != true && unknown.Historical != true && generic.Source == "Standalone" && generic.Historical != true,
            "Unknown current targets and independent installations must not be guessed to be previous releases.");
        AgentInstallationCandidate[] candidates = [new(physical, "PATH"), new(alias, "cached release", false, true), current, currentRelease, previous];
        var rows = await RuntimeService.ListAgentInstallationsAsync("codex", candidates, (_, _) => Task.FromResult("codex 9.99.99"), Resolve);
        var currentRow = Find(rows, physical); var previousRow = Find(rows, older);
        Check(rows.Count == 2 && currentRow["isCurrent"]?.GetValue<bool>() == true && currentRow["historical"]?.GetValue<bool>() != true,
            "Merged current metadata must override a historical classification of the same physical executable.");
        Check(Strings(currentRow, "sources").Contains("Standalone (current)") && Strings(currentRow, "sources").Contains("PATH"), "Current status must merge independently from the primary discovery source.");
        Check(previousRow["historical"]?.GetValue<bool>() == true && previousRow["isCurrent"]?.GetValue<bool>() != true, "A previous release must retain historical metadata while remaining selectable.");
        Available(currentRow, "9.99.99"); Available(previousRow, "9.99.99");
    }

    private static async Task BoundsParallelVersionProbes(Fixture fixture)
    {
        var paths = Enumerable.Range(0, 7).Select(index => fixture.Executable("parallel-" + index)).ToArray();
        var entered = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var threeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0; var maximum = 0;
        var commands = new ConcurrentQueue<string>();
        async Task<string> Probe(string path, IReadOnlyList<string> arguments)
        {
            commands.Enqueue(string.Join(' ', arguments));
            var active = Interlocked.Increment(ref inFlight);
            int previous;
            do { previous = Volatile.Read(ref maximum); } while (active > previous && Interlocked.CompareExchange(ref maximum, active, previous) != previous);
            try { entered.TryAdd(path, 0); if (entered.Count >= 3) threeEntered.TrySetResult(); await release.Task; return "codex 9.99.99"; }
            finally { Interlocked.Decrement(ref inFlight); }
        }
        var pending = RuntimeService.ListAgentInstallationsAsync("codex", Candidates(paths), Probe, path => path);
        JsonArray rows;
        try { await threeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)); Check(entered.Count == 3 && Volatile.Read(ref maximum) == 3, "Only three version probes may enter concurrently before a slot is released."); }
        finally { release.TrySetResult(); rows = await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
        Check(rows.Count == paths.Length && Volatile.Read(ref maximum) == 3 && commands.Count == paths.Length && commands.All(command => command == "--version"),
            "Every executable must be probed once while respecting the three-probe concurrency bound and version-only interface.");
        foreach (var row in rows.OfType<JsonObject>()) Available(row, "9.99.99");
    }

    private static async Task VersionTimeoutsRemainSelectable(Fixture fixture)
    {
        var hanging = fixture.Executable("timeout-version"); var healthy = fixture.Executable("timeout-neighbor");
        var never = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<(string Path, string Arguments)>();
        Task<string> Probe(string path, IReadOnlyList<string> arguments) { calls.Enqueue((path, string.Join(' ', arguments))); return path == hanging ? never.Task : Task.FromResult("codex 9.99.99"); }
        JsonArray rows;
        try { rows = await RuntimeService.ListAgentInstallationsAsync("codex", Candidates([hanging, healthy]), Probe, path => path, TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { never.TrySetResult("fixture released"); }
        Available(Find(rows, hanging)); Available(Find(rows, healthy), "9.99.99");
        Check(Text(Find(rows, hanging), "version").Length == 0 && calls.Count == 2 && calls.All(call => call.Arguments == "--version"),
            "A bounded version timeout must retain selection without retries or help calls.");
    }

    private static async Task OverallDeadlineRetainsUnprobedRows(Fixture fixture)
    {
        var paths = Enumerable.Range(0, 7).Select(index => fixture.Executable("deadline-" + index)).ToArray();
        var never = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<(string Path, string Arguments)>();
        Task<string> Probe(string path, IReadOnlyList<string> arguments) { calls.Enqueue((path, string.Join(' ', arguments))); return never.Task; }
        JsonArray rows;
        try { rows = await RuntimeService.ListAgentInstallationsAsync("codex", Candidates(paths), Probe, path => path, TimeSpan.FromSeconds(30)).WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { never.TrySetResult("fixture released"); }
        // Timer scheduling can briefly admit a second batch immediately before the deadline.
        Check(rows.Count == paths.Length && calls.Count >= 3 && calls.Count < paths.Length && calls.All(call => call.Arguments == "--version"),
            "The overall deadline must finish before a long per-command timeout and retain rows that never obtained a probe slot.");
        foreach (var row in rows.OfType<JsonObject>()) Available(row);
    }

    private static IEnumerable<AgentInstallationCandidate> Candidates(IEnumerable<string> paths) => paths.Select(path => new AgentInstallationCandidate(path, "fixture"));
    private static JsonObject Find(JsonArray rows, string path) => rows.OfType<JsonObject>().Single(row => Text(row, "path").Equals(path, StringComparison.OrdinalIgnoreCase));
    private static string Text(JsonObject row, string field) => row[field]?.GetValue<string>() ?? "";
    private static List<string> Strings(JsonObject row, string field) => row[field]!.AsArray().Select(value => value!.GetValue<string>()).ToList();
    private static void Available(JsonObject row, string? version = null) => Check(row["available"]?.GetValue<bool>() == true && row["fileExists"]?.GetValue<bool>() == true && row["error"] is null &&
        (version is null || Text(row, "version") == version), "Every found executable must be selectable without a blocking error and with its version retained when known.");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Fixture : IDisposable
    {
        private readonly string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string root;
        public Fixture() { root = Path.GetFullPath(Path.Combine(temporary, "PulseInstallationInventory-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root); }
        public string Executable(string name, string agent = "codex")
        {
            var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, agent + ".exe"); File.WriteAllBytes(path, []); return path;
        }
        public void Dispose()
        {
            Check(string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("PulseInstallationInventory-", StringComparison.Ordinal), "Inventory cleanup must remain inside its new dedicated temp directory.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Check(!Directory.Exists(root), "Inventory fixture cleanup must remove all temporary executables.");
        }
    }
}
