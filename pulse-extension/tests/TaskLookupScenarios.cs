using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Lost-acknowledgement lookup through Native Messaging framing without submitting any work.</summary>
internal static class TaskLookupScenarios
{
    internal static async Task RunAllAsync()
    {
        using var fixture = new Fixture();
        await MissingRequestNeverCreatesWork(fixture);
        await FindsOnlyTheFrozenRequest(fixture);
        await RebuildsOnlyTheIndex(fixture);
        await HistoricalTargetsRemainRecoveryOnly();
        Console.WriteLine("PASS task lookup: missing/active/failed requests, targetless historical replay, new-target admission, native framing, source isolation, immutable fingerprint, maintenance reads and index recovery (offline)");
    }

    private static async Task MissingRequestNeverCreatesWork(Fixture fixture)
    {
        var task = Request();
        var before = fixture.TaskFiles();
        var missing = await fixture.LookupAsync(task);
        Check(missing["ok"]!.GetValue<bool>() && missing["data"]!.AsObject().ContainsKey("run") && missing["data"]!["run"] is null,
            "An unknown request must return an explicit null run instead of submitting it.");
        Check(fixture.TaskFiles() == before && !fixture.Store.RunIds().Any() && !Directory.EnumerateFiles(Path.Combine(fixture.Root, "requests")).Any(),
            "Missing lookup must leave runs and request records unchanged.");
        // A real admission would be blocked by this live maintenance owner. Lookup must remain usable.
        using var current = Process.GetCurrentProcess();
        fixture.Store.WriteJson(Path.Combine(fixture.Root, "maintenance.json"), new JsonObject
        {
            ["pid"] = current.Id, ["startTimeUtc"] = current.StartTime.ToUniversalTime().ToString("O"), ["operation"] = "lookup-fixture"
        });
        var duringMaintenance = await fixture.LookupAsync(task);
        Check(duringMaintenance["ok"]!.GetValue<bool>() && duringMaintenance["data"]!["run"] is null && fixture.TaskFiles() == before,
            "Missing lookup remains a read during maintenance and cannot create delayed work.");
        var invalid = (JsonObject)task.DeepClone();
        invalid["command"] = "not-allowed";
        var rejected = await fixture.LookupAsync(invalid);
        Check(rejected["error"]!["code"]!.GetValue<string>() == "INVALID_REQUEST" && fixture.TaskFiles() == before,
            "Task lookup applies the same strict task schema as submission.");
    }

    private static async Task FindsOnlyTheFrozenRequest(Fixture fixture)
    {
        foreach (var state in new[] { "accepted", "running", "failed" })
        {
            var task = Request();
            if (state == "failed")
            {
                task["actionKind"] = "e2e";
                task["target"]!["type"] = "pr";
                task["expectedHeadSha"] = new string('A', 40);
            }
            var id = fixture.Accept(task, state);
            var before = fixture.TaskFiles();
            var found = await fixture.LookupAsync(task);
            var run = found["data"]!["run"]!.AsObject();
            Check(found["ok"]!.GetValue<bool>() && run["runId"]!.GetValue<string>() == id && run["status"]!["state"]!.GetValue<string>() == state,
                "Lookup must recover the same existing active or failed run, including during maintenance.");
            Check(JsonNode.DeepEquals(run["task"], Protocol.ValidateTask(task)) && fixture.TaskFiles() == before,
                "Lookup must preserve the original task, execution overrides, status, and request identity.");
            if (state == "failed")
                Check(run["status"]!["error"]!["code"]!.GetValue<string>() == "CLI_EXECUTION_FAILED", "A recovered failed run must expose its recorded failure rather than appearing ready to start again.");

            var reordered = new JsonObject();
            foreach (var field in task.Reverse()) reordered[field.Key] = field.Value?.DeepClone();
            reordered["repository"] = "MICROSOFT/POWERTOYS";
            var same = await fixture.LookupAsync(reordered);
            Check(same["data"]!["run"]!["runId"]!.GetValue<string>() == id,
                "Property order and the existing repository normalization must not break lookup identity.");

            var conflict = (JsonObject)task.DeepClone();
            conflict["prompt"] = "A different prompt must not reuse the accepted request ID.";
            var changed = await fixture.LookupAsync(conflict);
            AssertNoRunLeak(changed, "REQUEST_CONFLICT", id);
            var otherOrigin = await fixture.LookupAsync(task, "http://localhost:8080");
            AssertNoRunLeak(otherOrigin, "REQUEST_CONFLICT", id);
            var rejectedOrigin = await fixture.LookupAsync(task, "https://evil.invalid");
            AssertNoRunLeak(rejectedOrigin, "ORIGIN_NOT_ALLOWED", id);
            var extra = await fixture.DispatchAsync(new JsonObject { ["task"] = task.DeepClone(), ["sourceOrigin"] = Protocol.ProductionOrigin, ["runId"] = id });
            AssertNoRunLeak(extra, "INVALID_REQUEST", id);
            Check(fixture.TaskFiles() == before, "Conflicting or cross-origin lookups must not change any task or request records.");
        }
    }

    private static async Task RebuildsOnlyTheIndex(Fixture fixture)
    {
        var task = Request();
        var id = fixture.Accept(task, "failed");
        var runFiles = fixture.RunFiles();
        var requestPath = Path.Combine(fixture.Root, "requests", Protocol.Hash(task["requestId"]!.GetValue<string>()) + ".json");
        File.Delete(requestPath);
        var found = await fixture.LookupAsync(task);
        Check(found["data"]!["run"]!["runId"]!.GetValue<string>() == id && File.Exists(requestPath),
            "An interrupted request-index write must recover the authoritative accepted run.");
        Check(fixture.RunFiles() == runFiles, "Rebuilding lookup identity must not create, rerun, or rewrite task records.");

        fixture.Store.SetView(id, "handled", true);
        fixture.Store.SetView(id, "read", true);
        fixture.Store.DeleteRun(id);
        var before = fixture.TaskFiles();
        var removed = await fixture.LookupAsync(task);
        AssertNoRunLeak(removed, "TASK_REMOVED", id);
        Check(fixture.TaskFiles() == before && !Directory.Exists(fixture.Store.RunDirectory(id)),
            "A removed request retains its tombstone and must never be recreated by lookup.");
    }

    private static async Task HistoricalTargetsRemainRecoveryOnly()
    {
        using var fixture = new Fixture();
        foreach (var kind in new[] { "issue-fix", "reproduction-setup", "e2e" })
        {
            var task = Request(); task["actionKind"] = kind; task.Remove("target");
            var frozen = task.ToJsonString();
            var before = fixture.TaskFiles();
            var missing = await fixture.LookupAsync(task);
            Check(missing["ok"]!.GetValue<bool>() && missing["data"]!["run"] is null && fixture.TaskFiles() == before,
                "An absent historical identity must remain absent without a new task or guessed target.");
            var rejected = await fixture.SubmitAsync(task);
            Check(rejected["error"]!["code"]!.GetValue<string>() == "INVALID_REQUEST" && fixture.TaskFiles() == before,
                "Bypassing the extension cannot admit a new targetless task or reach configuration, GitHub or a worker.");

            // Seed a saved record directly, as an older Host could have accepted it.
            var id = fixture.Accept(task, "failed");
            before = fixture.TaskFiles();
            var found = await fixture.LookupAsync(task);
            var replayed = await fixture.SubmitAsync(task);
            Check(found["data"]!["run"]!["runId"]!.GetValue<string>() == id && replayed["data"]!["runId"]!.GetValue<string>() == id,
                "The original targetless identity must still find and replay the one accepted run.");
            Check(found["data"]!["run"]!["task"]!["target"] is null && fixture.TaskFiles() == before && task.ToJsonString() == frozen,
                "Recovery preserves the original task and does not invent, mutate or run a target.");

            var conflict = (JsonObject)task.DeepClone(); conflict["prompt"] = "Changed historical request";
            AssertNoRunLeak(await fixture.LookupAsync(conflict), "REQUEST_CONFLICT", id);
            AssertNoRunLeak(await fixture.SubmitAsync(conflict), "REQUEST_CONFLICT", id);
            AssertNoRunLeak(await fixture.LookupAsync(task, "http://localhost:8080"), "REQUEST_CONFLICT", id);
            AssertNoRunLeak(await fixture.SubmitAsync(task, "http://localhost:8080"), "REQUEST_CONFLICT", id);
            var newIdentity = (JsonObject)task.DeepClone(); newIdentity["requestId"] = Guid.NewGuid().ToString("D");
            Check((await fixture.SubmitAsync(newIdentity))["error"]!["code"]!.GetValue<string>() == "INVALID_REQUEST" && fixture.TaskFiles() == before,
                "A new request ID is new admission, even when it copies a historical task.");
            var rerun = await fixture.DispatchAsync(new JsonObject { ["runId"] = id, ["requestId"] = Guid.NewGuid().ToString("D") }, "tasks.rerun");
            Check(rerun["error"]!["code"]!.GetValue<string>() == "INVALID_REQUEST" && fixture.TaskFiles() == before,
                "Historical rerun must not bypass the new target requirement.");

            var requestPath = Path.Combine(fixture.Root, "requests", Protocol.Hash(task["requestId"]!.GetValue<string>()) + ".json");
            var runFiles = fixture.RunFiles(); File.Delete(requestPath);
            Check((await fixture.SubmitAsync(task))["data"]!["runId"]!.GetValue<string>() == id && File.Exists(requestPath) && fixture.RunFiles() == runFiles,
                "Replaying an accepted historical identity may recover only its missing request index.");
            fixture.Store.SetView(id, "read", true); fixture.Store.SetView(id, "handled", true); fixture.Store.DeleteRun(id);
            before = fixture.TaskFiles();
            AssertNoRunLeak(await fixture.LookupAsync(task), "TASK_REMOVED", id);
            AssertNoRunLeak(await fixture.SubmitAsync(task), "TASK_REMOVED", id);
            Check(fixture.TaskFiles() == before, "Historical tombstones take precedence over new-target admission and cannot recreate a run.");
        }
    }

    private static void AssertNoRunLeak(JsonObject response, string code, string runId)
    {
        Check(response["ok"]!.GetValue<bool>() == false && response["error"]!["code"]!.GetValue<string>() == code && response["data"] is null,
            "Lookup must reject a conflicting or unauthorized identity without returning a run.");
        var text = response.ToJsonString();
        Check(!text.Contains(runId, StringComparison.Ordinal) && !text.Contains("PRIVATE_LOOKUP", StringComparison.Ordinal),
            "Lookup rejection must not reveal the saved run identifier, local settings, or prompt content.");
    }

    private static JsonObject Request() => new()
    {
        ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "lookup-fixture", ["actionKind"] = "issue-fix",
        ["repository"] = "Microsoft/PowerToys", ["target"] = new JsonObject { ["type"] = "issue", ["number"] = 42 },
        ["prompt"] = "PRIVATE_LOOKUP_PROMPT: preserved Unicode and quotes: 中文 \"original\".",
        ["context"] = new JsonObject { ["title"] = "Frozen original issue" },
        ["execution"] = new JsonObject { ["agent"] = "codex", ["model"] = "", ["reasoningEffort"] = "" }
    };

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PulseLookupTests-" + Guid.NewGuid().ToString("N"));
        internal readonly Store Store;
        private readonly Dispatcher dispatcher;
        internal Fixture()
        {
            Store = new Store(Root);
            dispatcher = new Dispatcher(Store, developmentOrigins: true);
        }
        internal string Accept(JsonObject input, string state)
        {
            var id = Guid.NewGuid().ToString("D");
            Store.CreateRun(id, Protocol.ValidateTask(input), new JsonObject { ["agent"] = "codex", ["repositoryKey"] = "lookup-fixture", ["repoFolder"] = "PRIVATE_LOOKUP_FOLDER" }, Protocol.ProductionOrigin);
            if (state == "failed")
                Store.Complete(id, state, 1, new JsonObject { ["summary"] = "PRIVATE_LOOKUP_RESULT: original CLI failure" }, new ProtocolException("CLI_EXECUTION_FAILED", "Original recorded fixture failure.").ToJson());
            else
            {
                var status = Store.ReadStatus(id);
                status["state"] = state;
                // Deliberately unverifiable list-only identity; reconciliation cannot target a process.
                status["worker"] = new JsonObject { ["pid"] = 0, ["startTimeUtc"] = "lookup-fixture" };
                Store.WriteJson(Path.Combine(Store.RunDirectory(id), "status.json"), status);
            }
            return id;
        }
        internal Task<JsonObject> LookupAsync(JsonObject task, string origin = Protocol.ProductionOrigin)
            => DispatchAsync(new JsonObject { ["task"] = task.DeepClone(), ["sourceOrigin"] = origin });
        internal Task<JsonObject> SubmitAsync(JsonObject task, string origin = Protocol.ProductionOrigin)
            => DispatchAsync(new JsonObject { ["task"] = task.DeepClone(), ["sourceOrigin"] = origin }, "tasks.submit");
        internal async Task<JsonObject> DispatchAsync(JsonObject payload, string type = "tasks.lookup")
        {
            using var input = new MemoryStream();
            await NativeFraming.WriteAsync(input, new JsonObject { ["id"] = "lookup", ["protocolVersion"] = 1, ["type"] = type, ["payload"] = payload });
            input.Position = 0;
            var request = await NativeFraming.ReadAsync(input) ?? throw new InvalidOperationException("Missing fixture request frame.");
            var response = await dispatcher.HandleAsync(request);
            using var output = new MemoryStream();
            await NativeFraming.WriteAsync(output, response);
            output.Position = 0;
            return await NativeFraming.ReadAsync(output) ?? throw new InvalidOperationException("Missing fixture response frame.");
        }
        internal string RunFiles() => Files("runs");
        internal string TaskFiles() => Files("runs") + "\n" + Files("requests");
        private string Files(string directory) => string.Join("\n", Directory.EnumerateFiles(Path.Combine(Root, directory), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => path + ":" + Protocol.Hash(File.ReadAllText(path))));
        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("PulseLookupTests-", StringComparison.Ordinal) && Directory.Exists(absolute))
                Directory.Delete(absolute, recursive: true);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
