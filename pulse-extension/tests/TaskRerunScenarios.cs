using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class TaskRerunScenarios
{
    internal static Task RunAllAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulseRerunTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(root);
            var oldRunId = Guid.NewGuid().ToString("D");
            var original = Request();
            store.CreateRun(oldRunId, original, Config(), Protocol.ProductionOrigin);
            var savedBytes = File.ReadAllBytes(Path.Combine(store.RunDirectory(oldRunId), "task.json"));
            JsonObject Payload() => new() { ["runId"] = oldRunId, ["requestId"] = Guid.NewGuid().ToString("D") };

            var legacy = TaskRerun.Prepare(store, Payload());
            var legacyTask = legacy["task"]!.AsObject();
            Check(JsonNode.DeepEquals(legacyTask["execution"], original["execution"]), "An omitted execution field keeps previous task overrides for older clients.");
            Check(legacyTask["expectedHeadSha"]!.GetValue<string>() == new string('a', 40), "A rerun keeps the original PR revision by default.");
            foreach (var field in new[] { "repository", "target", "actionId", "actionKind", "prompt", "context" })
                Check(JsonNode.DeepEquals(legacyTask[field], original[field]), "Rerun preserves the immutable target, prompt and context: " + field);
            Check(legacy["sourceOrigin"]!.GetValue<string>() == Protocol.ProductionOrigin && store.RunIds().Count() == 1, "Preparation neither starts work nor changes origin.");

            var defaultsPayload = Payload(); defaultsPayload["execution"] = null;
            var defaultsTask = TaskRerun.Prepare(store, defaultsPayload)["task"]!.AsObject();
            Check(!defaultsTask.ContainsKey("execution"), "Explicit null clears old overrides instead of serializing an invalid null task override.");
            var current = Config(); current["agent"] = "copilot";
            var resolved = ExecutionOptions.Resolve(current, defaultsTask["execution"]);
            Check(resolved["agent"]!.GetValue<string>() == "copilot" && resolved["model"]!.GetValue<string>() == "new-copilot-model" && resolved["reasoningEffort"]!.GetValue<string>() == "max", "Cleared overrides inherit the currently saved CLI, model and effort.");

            var customPayload = Payload();
            customPayload["reviewOptions"] = new JsonObject { ["mode"] = "static" };
            customPayload["execution"] = new JsonObject { ["agent"] = "codex", ["model"] = "", ["reasoningEffort"] = "" };
            customPayload["expectedHeadSha"] = new string('B', 40);
            var custom = TaskRerun.Prepare(store, customPayload);
            var customTask = custom["task"]!.AsObject();
            Check(customTask["reviewOptions"]?["mode"]?.GetValue<string>() == "static" && !legacyTask.ContainsKey("reviewOptions"), "New scoped reruns record the user's range, while recovery of legacy requests retains their unrecorded range.");
            Check(customTask["expectedHeadSha"]!.GetValue<string>() == new string('b', 40), "Only an explicit full SHA changes the analysis revision, with normal canonicalization.");
            var cliDefaults = ExecutionOptions.Resolve(current, customTask["execution"]);
            Check(cliDefaults["agent"]!.GetValue<string>() == "codex" && cliDefaults["model"]!.GetValue<string>() == "" && cliDefaults["reasoningEffort"]!.GetValue<string>() == "", "Explicit CLI defaults do not accidentally inherit saved nonempty profile values.");
            var acceptedId = Guid.NewGuid().ToString("D");
            store.CreateRun(acceptedId, customTask, cliDefaults, Protocol.ProductionOrigin);
            Check(store.FindRequest(TaskRerun.Prepare(store, customPayload)["task"]!.AsObject(), Protocol.ProductionOrigin) == acceptedId, "Retransmitting the same rerun request resolves the one already accepted run.");
            var changed = (JsonObject)customPayload.DeepClone(); changed["execution"]!["model"] = "another-model";
            Expect("REQUEST_CONFLICT", () => store.FindRequest(TaskRerun.Prepare(store, changed)["task"]!.AsObject(), Protocol.ProductionOrigin));
            changed = (JsonObject)customPayload.DeepClone(); changed["expectedHeadSha"] = new string('c', 40);
            Expect("REQUEST_CONFLICT", () => store.FindRequest(TaskRerun.Prepare(store, changed)["task"]!.AsObject(), Protocol.ProductionOrigin));
            changed = (JsonObject)customPayload.DeepClone(); changed["reviewOptions"]!["mode"] = "ui-e2e";
            Expect("REQUEST_CONFLICT", () => store.FindRequest(TaskRerun.Prepare(store, changed)["task"]!.AsObject(), Protocol.ProductionOrigin));
            var scopedRerun = new JsonObject { ["runId"] = acceptedId, ["requestId"] = Guid.NewGuid().ToString("D") };
            Check(TaskRerun.Prepare(store, scopedRerun)["task"]?["reviewOptions"]?["mode"]?.GetValue<string>() == "static", "Omitted scope preserves the accepted source mode rather than silently using a new default.");
            customTask["context"]!["title"] = "Caller mutation";
            Check(File.ReadAllBytes(Path.Combine(store.RunDirectory(oldRunId), "task.json")).SequenceEqual(savedBytes), "Preparing or mutating a rerun leaves the source task byte-for-byte unchanged.");

            foreach (var field in new[] { "task", "sourceOrigin", "target", "prompt", "permission", "cliPath" })
            {
                var invalid = Payload(); invalid[field] = "untrusted";
                Expect("INVALID_REQUEST", () => TaskRerun.Prepare(store, invalid));
            }
            foreach (var sha in new JsonNode?[] { null, JsonValue.Create("latest"), JsonValue.Create(new string('a', 39)) })
            {
                var invalid = Payload(); invalid["expectedHeadSha"] = sha;
                Expect("INVALID_REQUEST", () => TaskRerun.Prepare(store, invalid));
            }
            var sameId = Payload(); sameId["requestId"] = original["requestId"]!.DeepClone();
            Expect("INVALID_REQUEST", () => TaskRerun.Prepare(store, sameId));
            var invalidExecution = Payload(); invalidExecution["execution"] = new JsonObject { ["permission"] = "yolo" };
            Expect("INVALID_REQUEST", () => TaskRerun.Prepare(store, invalidExecution));
            foreach (var scope in new JsonNode?[] { null, JsonValue.Create("static"), new JsonObject { ["mode"] = "everything" } })
            {
                var invalid = Payload(); invalid["reviewOptions"] = scope;
                Expect("INVALID_REQUEST", () => TaskRerun.Prepare(store, invalid));
            }

            var issue = Request(); issue["actionKind"] = "issue-fix"; issue["target"]!["type"] = "issue"; issue.Remove("expectedHeadSha");
            var issueId = Guid.NewGuid().ToString("D"); store.CreateRun(issueId, issue, Config(), Protocol.ProductionOrigin);
            var issuePayload = Payload(); issuePayload["runId"] = issueId; issuePayload["expectedHeadSha"] = new string('a', 40);
            Expect("INVALID_REQUEST", () => TaskRerun.Prepare(store, issuePayload));
            Console.WriteLine("PASS task rerun: explicit defaults and overrides, frozen PR revision, new request identity, lost-ack deduplication and immutable history (offline)");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    private static JsonObject Request() => Protocol.ValidateTask(new JsonObject
    {
        ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "review-original", ["actionKind"] = "pr-review",
        ["repository"] = "microsoft/PowerToys", ["target"] = new JsonObject { ["type"] = "pr", ["number"] = 50493 },
        ["expectedHeadSha"] = new string('a', 40), ["prompt"] = "Review this original task. Preserve whitespace.\n",
        ["context"] = new JsonObject { ["title"] = "Original title" },
        ["execution"] = new JsonObject { ["agent"] = "codex", ["model"] = "old-task-model", ["reasoningEffort"] = "low" }
    });
    private static JsonObject Config() => new()
    {
        ["agent"] = "codex", ["agentDefaults"] = new JsonObject
        {
            ["codex"] = new JsonObject { ["model"] = "new-codex-model", ["reasoningEffort"] = "high" },
            ["copilot"] = new JsonObject { ["model"] = "new-copilot-model", ["reasoningEffort"] = "max" }
        }
    };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (ProtocolException error) when (error.Code == code) { }
    }
}
