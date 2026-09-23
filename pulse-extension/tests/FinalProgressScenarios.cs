using System.Text;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Phase-only progress uses real output pumps and isolated Store records, without launching a CLI.</summary>
internal static class FinalProgressScenarios
{
    internal static async Task RunAllAsync()
    {
        CheckTaskSelection();
        CheckSavedContractSelection();
        using var fixture = new Fixture();
        ReconcileUsesTheSavedContract(fixture);
        foreach (var agent in new[] { "codex", "copilot" })
        {
            foreach (var version in new[] { 2, 3 })
            foreach (var kind in new[] { "pr-review", "pr-verify", "bug-investigation", "feature-research", "reproduction-setup" })
                await OnlyPhasesReachActiveTasks(fixture, agent, kind, version);
            await UnknownAndDiagnosticTextCannotReplaceThePhase(fixture, agent);
            await ErrorsUseGenericProgress(fixture, agent);
            await RepeatedPhasesAreCollapsed(fixture, agent);
            await LegacyCallersKeepTheirExistingProgress(fixture, agent);
            await VersionedTransportBudgets(fixture, agent);
            await VersionedLogBudgets(fixture, agent);
        }
        Console.WriteLine("PASS final progress: v2/v3 PR/bug phase-only Activity and snapshots, versioned transport/log budgets, complete redacted raw logs and legacy pumps (offline)");
    }

    private static void CheckTaskSelection()
    {
        foreach (var kind in new[] { "pr-review", "pr-verify", "bug-investigation", "feature-research", "reproduction-setup" })
            Check(RuntimeService.UsesPhaseOnlyProgress(new JsonObject { ["actionKind"] = kind }), "Review, verification and bug-investigation tasks need phase-only live progress.");
        foreach (var kind in new[] { "feature-implement", "issue-fix", "e2e" })
            Check(!RuntimeService.UsesPhaseOnlyProgress(new JsonObject { ["actionKind"] = kind }), "Unchanged workflows must not be inferred from task titles or free-form context.");
        Check(!RuntimeService.UsesPhaseOnlyProgress(null) && !RuntimeService.UsesPhaseOnlyProgress(new JsonObject { ["prompt"] = "bug-investigation pr-review" }),
            "Legacy pump calls and untrusted prompt words cannot opt a task into a different progress policy.");
    }

    private static void CheckSavedContractSelection()
    {
        foreach (var version in new[] { 2, 3 })
        {
            var saved = new JsonObject { ["promptTemplate"] = new JsonObject { ["schemaVersion"] = version },
                ["task"] = new JsonObject { ["schemaVersion"] = version == 3 ? 2 : 3, ["context"] = new JsonObject { ["schemaVersion"] = 999 } } };
            Check(RuntimeService.ExpectedResultSchemaVersion(saved) == version,
                "Only the immutable accepted prompt template may select the result contract; external context cannot downgrade or upgrade it.");
            Check(CliAdapter.ResultSchema(version)["properties"]!["schemaVersion"]!["const"]!.GetValue<int>() == version,
                "The selected result schema handed to the CLI must match the saved template version.");
        }
        Check(RuntimeService.ExpectedResultSchemaVersion(new JsonObject { ["task"] = new JsonObject { ["schemaVersion"] = 3 } }) is null,
            "Missing legacy template versions retain legacy acceptance rather than inventing a v3 contract from task data.");
        Check(RuntimeService.ExpectedResultSchemaVersion(new JsonObject { ["promptTemplate"] = new JsonObject { ["schemaVersion"] = "3" } }) == -1,
            "A malformed saved version must remain invalid for final validation rather than silently selecting a valid contract.");
    }

    private static void ReconcileUsesTheSavedContract(Fixture fixture)
    {
        foreach (var version in new int?[] { null, 2, 3 })
        {
            var (id, _) = fixture.Create("codex", "bug-investigation", version);
            var status = fixture.Store.ReadStatus(id);
            status["createdAt"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
            fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(id), "status.json"), status);
            RuntimeService.Reconcile(fixture.Store, id);
            var result = fixture.Store.ReadResult(id)!;
            Check(fixture.Store.ReadStatus(id)["state"]?.GetValue<string>() == "interrupted" &&
                result["schemaVersion"]?.GetValue<int>() == (version == 3 ? 3 : 2) && result["outcome"]?.GetValue<string>() == "interrupted",
                "A verified unstarted worker interruption must produce diagnostics in its saved result contract, including v3 before any CLI output exists.");
        }
    }

    private static async Task OnlyPhasesReachActiveTasks(Fixture fixture, string agent, string kind, int schemaVersion)
    {
        var (id, task) = fixture.Create(agent, kind);
        var adapter = new CliAdapter(agent, resultSchemaVersion: schemaVersion);
        const string candidate = "CANDIDATE_FINDING: possible invalid title pointer; this is not a confirmed defect.";
        var final = new JsonObject { ["schemaVersion"] = schemaVersion, ["summary"] = "FINAL_ONLY: review converged.", ["findings"] = new JsonArray() }.ToJsonString();
        var messages = new[]
        {
            Start(agent), candidate,
            new JsonObject { ["summary"] = candidate, ["findings"] = new JsonArray(candidate), ["phase"] = candidate }.ToJsonString(),
            Tool(agent, candidate), Assistant(agent, candidate, final: false), Final(agent, final), Terminal(agent)
        };
        var wire = string.Join("\n", messages) + "\n";
        var observed = new List<string>();
        var tail = new StringBuilder();
        await Pump(fixture.Store, id, adapter, wire, structured: true, task, tail, observed.Add);
        var directory = fixture.Store.RunDirectory(id);
        Check(await File.ReadAllTextAsync(Path.Combine(directory, "stdout.jsonl")) == wire,
            "Every complete stdout event, candidate message and final JSON must remain in the raw diagnostic log.");
        Check(observed.SequenceEqual(messages), "Execution metadata must continue to receive complete redacted CLI events, rather than phase substitutes.");
        Check(adapter.AssistantReply == final && adapter.SawCompletion && adapter.OutputIsComplete,
            "Filtering user-facing progress must not change complete assistant-result parsing or the terminal marker.");
        Check(fixture.Store.ReadResult(id) is null && fixture.Store.ReadStatus(id)["state"]?.GetValue<string>() == "running",
            "Receiving final text in a running pump must not publish a provisional result or invent completion.");
        Check(fixture.Store.ReadStatus(id)["latestProgress"]?.GetValue<string>() == "Preparing the final task report.",
            "A terminal transport phase should replace earlier generic activity without exposing result JSON.");
        Check(fixture.Store.ReadStatus(id)["cliSessionId"]?.GetValue<string>() == "phase-fixture-session",
            "Session identity must still be captured while live message bodies are hidden.");
        var events = fixture.Store.Events(id, 0, 200)["events"]!.AsArray().OfType<JsonObject>().ToArray();
        Check(events.Any(item => item["type"]?.GetValue<string>() == "cli.progress") && events.All(item =>
            !item.ToJsonString().Contains("CANDIDATE_FINDING", StringComparison.Ordinal) && !item.ToJsonString().Contains("FINAL_ONLY", StringComparison.Ordinal)),
            "Activity must retain useful phase progress without candidate findings, tool content or complete final-result JSON.");
        Check(events.Where(item => item["type"]?.GetValue<string>() == "cli.progress").All(item => IsPhase(item["text"]!.GetValue<string>())),
            "All review Activity progress must come from the fixed transport-phase allowlist.");
        Check(tail.ToString().Contains(candidate, StringComparison.Ordinal) && tail.ToString().Contains("FINAL_ONLY", StringComparison.Ordinal),
            "Failure classification retains diagnostic context independently from the user-facing phase summary.");
    }

    private static async Task UnknownAndDiagnosticTextCannotReplaceThePhase(Fixture fixture, string agent)
    {
        var (id, task) = fixture.Create(agent, "bug-investigation");
        var adapter = new CliAdapter(agent);
        await Pump(fixture.Store, id, adapter, Start(agent) + "\n", structured: true, task);
        var before = fixture.Store.ReadStatus(id)["latestProgress"]?.GetValue<string>();
        var eventsBefore = fixture.Store.Events(id, 0, 200)["events"]!.AsArray().Count;
        const string credential = "ghp_1234567890abcdef1234567890abcdef123456";
        var unknown = new JsonObject { ["type"] = "unrecognized.candidate", ["message"] = "PROVISIONAL_P0 " + credential,
            ["phase"] = "PROVISIONAL_PHASE", ["findings"] = new JsonArray("PROVISIONAL_FINDING") }.ToJsonString();
        var stderr = "PROVISIONAL_DIAGNOSTIC 中文 " + credential + "\n";
        await Pump(fixture.Store, id, adapter, unknown + "\nnot JSON: PROVISIONAL_RAW\n", structured: true, task);
        await Pump(fixture.Store, id, adapter, stderr, structured: false, task);
        Check(fixture.Store.ReadStatus(id)["latestProgress"]?.GetValue<string>() == before,
            "Unknown event content and stderr must not overwrite the last recognized phase or clear it.");
        Check(fixture.Store.Events(id, 0, 200)["events"]!.AsArray().Count == eventsBefore,
            "Unknown and raw diagnostic lines must stay out of Activity instead of leaking via cli.diagnostic.");
        var directory = fixture.Store.RunDirectory(id);
        Check(await File.ReadAllTextAsync(Path.Combine(directory, "stderr.log")) == OutputRedactor.Redact(stderr),
            "The complete stderr text remains available in its explicit diagnostic stream with credentials redacted.");
        var stdout = await File.ReadAllTextAsync(Path.Combine(directory, "stdout.jsonl"));
        Check(stdout.Contains(OutputRedactor.Redact(unknown), StringComparison.Ordinal) && stdout.Contains("PROVISIONAL_RAW", StringComparison.Ordinal) && !stdout.Contains(credential, StringComparison.Ordinal),
            "Unknown stdout preserves the exact redacted evidence and is not discarded merely because it is hidden from progress.");
    }

    private static async Task RepeatedPhasesAreCollapsed(Fixture fixture, string agent)
    {
        var (id, task) = fixture.Create(agent, "pr-review");
        var adapter = new CliAdapter(agent);
        var messages = new[] { Start(agent) }.Concat(Enumerable.Range(0, 30).Select(index => Assistant(agent, "CANDIDATE_DELTA_" + index, final: false))).ToArray();
        await Pump(fixture.Store, id, adapter, string.Join("\n", messages) + "\n", structured: true, task);
        var events = fixture.Store.Events(id, 0, 200)["events"]!.AsArray().OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() == "cli.progress").ToArray();
        Check(events.Length == 2 && events[1]["text"]?.GetValue<string>() == "Reviewing the collected evidence.",
            "Repeated assistant deltas must produce one analysis phase rather than repeated candidate messages or an Activity flood.");
        Check((await File.ReadAllLinesAsync(Path.Combine(fixture.Store.RunDirectory(id), "stdout.jsonl"))).Length == messages.Length,
            "Collapsing repeated display phases must never collapse their raw log evidence.");
    }

    private static async Task ErrorsUseGenericProgress(Fixture fixture, string agent)
    {
        var (id, task) = fixture.Create(agent, "pr-review");
        var adapter = new CliAdapter(agent);
        const string detail = "CANDIDATE_ERROR_DETAILS: unable to verify the tentative result.";
        var error = new JsonObject { ["type"] = "error", ["message"] = detail }.ToJsonString();
        await Pump(fixture.Store, id, adapter, error + "\n", structured: true, task);
        Check(adapter.Failure == detail && fixture.Store.ReadStatus(id)["latestProgress"]?.GetValue<string>() ==
            "The agent reported an execution problem. See the execution logs.",
            "Actual CLI failures must remain available to terminal-result classification while progress uses a generic diagnostic phase.");
        var events = fixture.Store.Events(id, 0, 200)["events"]!.AsArray();
        Check(!events.ToJsonString().Contains("CANDIDATE_ERROR_DETAILS", StringComparison.Ordinal) &&
            (await File.ReadAllTextAsync(Path.Combine(fixture.Store.RunDirectory(id), "stdout.jsonl"))).Contains(detail, StringComparison.Ordinal),
            "CLI error details belong in the raw log and final diagnostics, not in interim Activity messages.");
        const string malformed = "{\"type\":\"item.completed\",\"candidate\":\"BROKEN_CANDIDATE\"";
        await Pump(fixture.Store, id, adapter, malformed + "\n", structured: true, task);
        Check(!adapter.OutputIsComplete && fixture.Store.Events(id, 0, 200)["events"]!.AsArray().Count == events.Count,
            "Malformed transport still marks the output incomplete without exposing a partial candidate JSON object as progress.");
    }

    private static async Task LegacyCallersKeepTheirExistingProgress(Fixture fixture, string agent)
    {
        var (id, _) = fixture.Create(agent, "issue-fix");
        const string message = "Legacy output remains visible 中文";
        await Pump(fixture.Store, id, new CliAdapter(agent), Assistant(agent, message, final: false) + "\n", structured: true, task: null);
        Check(fixture.Store.ReadStatus(id)["latestProgress"]?.GetValue<string>() == message,
            "The optional task parameter preserves existing output-pump callers and unrelated workflow display behavior.");
    }

    private static async Task VersionedTransportBudgets(Fixture fixture, string agent)
    {
        var oldAdapter = new CliAdapter(agent);
        var currentAdapter = new CliAdapter(agent, resultSchemaVersion: 3);
        Check(oldAdapter.MaximumEventCharacters == CliAdapter.MaximumLineCharacters && currentAdapter.MaximumEventCharacters > oldAdapter.MaximumEventCharacters,
            "Legacy adapters retain the established line cap while explicit v3 adapters have their own larger escaped-event budget.");
        var wire = new JsonObject { ["type"] = "unknown.large-event", ["opaque"] = new string('x', oldAdapter.MaximumEventCharacters + 1) }.ToJsonString() + "\n";
        Check(wire.Length < currentAdapter.MaximumEventCharacters, "The transport fixture must be too large only for the legacy contract.");
        foreach (var version in new[] { 2, 3 })
        {
            var (id, task) = fixture.Create(agent, "pr-review");
            var adapter = version == 2 ? oldAdapter : currentAdapter;
            await Pump(fixture.Store, id, adapter, wire, structured: true, task);
            var raw = await File.ReadAllTextAsync(Path.Combine(fixture.Store.RunDirectory(id), "stdout.jsonl"));
            if (version == 3)
                Check(adapter.OutputIsComplete && raw == wire, "A v3 event beyond the legacy cap must be retained completely, rather than falsely marking its final report incomplete.");
            else
                Check(!adapter.OutputIsComplete && raw.Contains("OUTPUT_LINE_TOO_LONG", StringComparison.Ordinal) && raw.Length < 1024,
                    "Legacy transport still emits its bounded discard marker for events beyond the old cap.");
            Check(!fixture.Store.Events(id, 0, 100)["events"]!.ToJsonString().Contains("unknown.large-event", StringComparison.Ordinal),
                "Raising the v3 transport budget must not stream raw event JSON into review Activity.");
        }
    }

    private static async Task VersionedLogBudgets(Fixture fixture, string agent)
    {
        const long legacyLimit = 8L * 1024 * 1024;
        foreach (var version in new[] { 2, 3 })
        foreach (var structured in new[] { true, false })
        {
            var (id, task) = fixture.Create(agent, "bug-investigation");
            var adapter = new CliAdapter(agent, resultSchemaVersion: version);
            var expectedLimit = (version == 3 ? 64L : 8L) * 1024 * 1024;
            Check(adapter.MaximumLogBytes == expectedLimit, "Accepted schema version selects the raw-log budget independently of the result and native frame budgets.");
            var path = Path.Combine(fixture.Store.RunDirectory(id), structured ? "stdout.jsonl" : "stderr.log");
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write)) file.SetLength(legacyLimit);
            var wire = structured ? Start(agent) + "\n" : "Retained diagnostic beyond the legacy cap.\n";
            await Pump(fixture.Store, id, adapter, wire, structured, task);
            if (version == 3)
            {
                var bytes = Encoding.UTF8.GetBytes(wire);
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read);
                Check(file.Length == legacyLimit + bytes.Length && adapter.OutputIsComplete && !File.Exists(path + ".truncated"),
                    "Both v3 raw streams must continue past 8 MiB without inventing output loss or a legacy-limit warning.");
                file.Seek(legacyLimit, SeekOrigin.Begin);
                var tail = new byte[bytes.Length]; await file.ReadExactlyAsync(tail);
                Check(tail.SequenceEqual(bytes), "The complete UTF-8 transport bytes beyond the legacy cap must be retained.");
            }
            else
                Check(new FileInfo(path).Length == legacyLimit && !adapter.OutputIsComplete && File.Exists(path + ".truncated"),
                    "Legacy streams must keep the established 8 MiB bound and its explicit truncation evidence.");

            var (cappedId, cappedTask) = fixture.Create(agent, "pr-review");
            var cappedAdapter = new CliAdapter(agent, resultSchemaVersion: version);
            var cappedPath = Path.Combine(fixture.Store.RunDirectory(cappedId), structured ? "stdout.jsonl" : "stderr.log");
            using (var file = new FileStream(cappedPath, FileMode.Create, FileAccess.Write)) file.SetLength(expectedLimit);
            await Pump(fixture.Store, cappedId, cappedAdapter, wire, structured, cappedTask);
            Check(new FileInfo(cappedPath).Length == expectedLimit && !cappedAdapter.OutputIsComplete && File.Exists(cappedPath + ".truncated"),
                "Each contract must stop persisting at its own limit while continuing to drain the pipe.");
            Check(fixture.Store.Events(cappedId, 0, 100)["events"]!.AsArray().OfType<JsonObject>().Any(row =>
                row["type"]?.GetValue<string>() == "log.truncated" && row["text"]!.GetValue<string>().Contains($"{expectedLimit / (1024 * 1024)} MiB", StringComparison.Ordinal)),
                "The durable log warning must name the actual selected version's limit.");
        }
    }

    private static async Task Pump(Store store, string id, CliAdapter adapter, string text, bool structured, JsonObject? task,
        StringBuilder? tail = null, Action<string>? observe = null)
    {
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        await RuntimeService.DrainOutputAsync(store, id, reader, structured ? "stdout.jsonl" : "stderr.log", structured,
            adapter, tail ?? new StringBuilder(), CancellationToken.None, observe, task);
    }

    private static string Start(string agent) => (agent == "codex"
        ? new JsonObject { ["type"] = "thread.started", ["thread_id"] = "phase-fixture-session" }
        : new JsonObject { ["type"] = "session.start", ["data"] = new JsonObject { ["sessionId"] = "phase-fixture-session" } }).ToJsonString();
    private static string Terminal(string agent) => new JsonObject { ["type"] = agent == "codex" ? "turn.completed" : "session.idle" }.ToJsonString();
    private static string Tool(string agent, string text) => (agent == "codex"
        ? new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "command_execution", ["aggregated_output"] = text } }
        : new JsonObject { ["type"] = "tool.execution_complete", ["data"] = new JsonObject { ["content"] = text } }).ToJsonString();
    private static string Assistant(string agent, string text, bool final) => (agent == "codex"
        ? new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["phase"] = final ? "final" : "commentary", ["text"] = text } }
        : new JsonObject { ["type"] = final ? "assistant.message" : "assistant.message_delta", ["data"] = new JsonObject { ["phase"] = final ? "final" : "commentary", [final ? "content" : "deltaContent"] = text } }).ToJsonString();
    private static string Final(string agent, string text) => agent == "codex" ? Assistant(agent, text, final: true)
        : new JsonObject { ["type"] = "result", ["subtype"] = "success", ["result"] = text }.ToJsonString();
    private static bool IsPhase(string value) => value is "Preparing task analysis." or "Reviewing the collected evidence."
        or "Collecting evidence within the selected scope." or "Preparing the final task report." or "Planning and tracking the selected work."
        or "Checking task workspace changes." or "The agent reported an execution problem. See the execution logs.";
    private static void Check(bool condition, string message) => CoreScenarios.Check(condition, message);

    private sealed class Fixture : IDisposable
    {
        private readonly string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string root;
        public Store Store { get; }
        public Fixture() { root = Path.Combine(temporary, "PulseFinalProgress-" + Guid.NewGuid().ToString("N")); Store = new Store(root); }
        public (string Id, JsonObject Task) Create(string agent, string kind, int? schemaVersion = null)
        {
            var id = Guid.NewGuid().ToString("D");
            var task = new JsonObject { ["requestId"] = id, ["actionId"] = "phase-only-fixture", ["actionKind"] = kind,
                ["repository"] = Configuration.PowerToysRepository, ["prompt"] = "Isolated offline stream fixture." };
            Store.CreateRun(id, task, new JsonObject { ["agent"] = agent, ["repoFolder"] = root, ["repositoryKey"] = id, ["permission"] = "read-only" }, Protocol.ProductionOrigin,
                schemaVersion is null ? null : new JsonObject { ["schemaVersion"] = schemaVersion.Value, ["name"] = "phase-fixture", ["body"] = "Offline fixture.", ["sha"] = Protocol.Hash("Offline fixture.") });
            var status = Store.ReadStatus(id); status["state"] = "running";
            Store.WriteJson(Path.Combine(Store.RunDirectory(id), "status.json"), status);
            return (id, task);
        }
        public void Dispose()
        {
            var absolute = Path.GetFullPath(root);
            var name = Path.GetFileName(absolute);
            Check(string.Equals(Path.GetDirectoryName(absolute), temporary, StringComparison.OrdinalIgnoreCase) &&
                name.StartsWith("PulseFinalProgress-", StringComparison.Ordinal) && Guid.TryParseExact(name["PulseFinalProgress-".Length..], "N", out _),
                "Final-progress fixture cleanup must stay inside its unique direct child of the system temporary directory.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }
}
