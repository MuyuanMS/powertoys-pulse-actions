using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class ResultTaskScenarios
{
    internal static Task RunAllAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulseResultTaskTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(root);
            foreach (var kind in new[] { "feature-implement", "issue-fix", "reproduction-setup", "issue-verify" }) VerifyPlan(store, kind);
            Console.WriteLine("PASS result task plans: four task kinds, exact plan/context/revision, prerequisites, frozen defaults, lost acknowledgements, spoof rejection and immutable reports");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("PulseResultTaskTests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        return Task.CompletedTask;
    }

    private static void VerifyPlan(Store store, string kind)
    {
        var parentKind = kind == "feature-implement" ? "feature-research" : "bug-investigation";
        var parent = WorkflowV3Scenarios.TaskContext(parentKind);
        var model = WorkflowV3Scenarios.Model(parentKind);
        model["plans"]![0]!["kind"] = kind; model["nextActions"]![0]!["taskKind"] = kind;
        model["plans"]![0]!["prerequisites"] = new JsonArray("Prepare the described local environment.");
        var result = WorkflowResult.FromModel(model.ToJsonString(), parent, "succeeded", 0, null, null, expectedSchemaVersion: 3);
        Check(WorkflowResult.IsValidStoredV3(result), "The source plan fixture is valid v3: " + kind);
        var parentId = Guid.NewGuid().ToString("D");
        store.CreateRun(parentId, parent, new JsonObject { ["worktreeBase"] = new string('a', 40) }, Protocol.ProductionOrigin);
        SaveResult(store, parentId, result);
        var parentBytes = File.ReadAllBytes(Path.Combine(store.RunDirectory(parentId), "task.json"));
        var resultBytes = File.ReadAllBytes(Path.Combine(store.RunDirectory(parentId), "result.json"));
        var proposalId = WorkflowResult.WithProposalIds(result)["nextActions"]![0]!["proposalId"]!.GetValue<string>();
        JsonObject Payload() => new()
        {
            ["runId"] = parentId, ["proposalId"] = proposalId, ["requestId"] = Guid.NewGuid().ToString("D"),
            ["prerequisitesConfirmed"] = true
        };
        var unmet = Payload(); unmet.Remove("prerequisitesConfirmed");
        Check(ResultTaskActions.Prepare(store, unmet)["task"]!["context"]!["resultPlan"]!["prerequisitesReportedReady"]!.GetValue<bool>() == false,
            "Listed plan prerequisites remain setup work and do not invent another mandatory approval gate.");
        var payload = Payload(); payload["execution"] = new JsonObject { ["agent"] = "codex", ["model"] = "selected-model", ["reasoningEffort"] = "high" };
        var prepared = ResultTaskActions.Prepare(store, payload); var task = prepared["task"]!.AsObject();
        Check(task["actionKind"]!.GetValue<string>() == kind && JsonNode.DeepEquals(task["target"], parent["target"]), "The saved plan controls task kind and exact Issue target.");
        Check(task["planSource"]!["revisionSha"]!.GetValue<string>() == new string('a', 40) && !task.ContainsKey("expectedHeadSha"), "An Issue plan freezes its initial source revision without pretending it is a PR HEAD.");
        Check(JsonNode.DeepEquals(task["context"]!["resultPlan"]!["plan"], result["plans"]![0]), "The complete chosen plan is carried without truncation or copying unrelated plans.");
        Check(File.Exists(task["context"]!["resultPlan"]!["parentReportPath"]!.GetValue<string>()), "The full retained parent report is linked for detailed research context.");
        Check(JsonNode.DeepEquals(ResultTaskActions.Prepare(store, payload), prepared), "An unconfirmed retry preserves the exact frozen task.");
        var changed = payload.DeepClone().AsObject(); changed["execution"]!["model"] = "changed-model";
        Expect("REQUEST_CONFLICT", () => ResultTaskActions.Prepare(store, changed));
        foreach (var field in new[] { "taskKind", "plan", "target", "sourceOrigin", "revisionSha", "planSource" })
        {
            var invalid = Payload(); invalid[field] = "injected";
            Expect("INVALID_REQUEST", () => ResultTaskActions.Prepare(store, invalid));
        }
        var wrong = Payload(); wrong["proposalId"] = "not-in-this-report";
        Expect("PROPOSAL_NOT_FOUND", () => ResultTaskActions.Prepare(store, wrong));
        Expect("INVALID_REQUEST", () => Protocol.ValidateTask(task));

        var childId = Guid.NewGuid().ToString("D"); store.CreateRun(childId, task, new JsonObject(), Protocol.ProductionOrigin);
        Check(store.FindRequest(ResultTaskActions.Prepare(store, payload)["task"]!.AsObject(), Protocol.ProductionOrigin) == childId, "Lost-ack recovery resolves the accepted task rather than starting another one.");
        var unaccepted = Payload(); ResultTaskActions.Prepare(store, unaccepted);
        var updated = result.DeepClone().AsObject(); updated["summary"] = "A different report after the request was prepared.";
        SaveResult(store, parentId, updated);
        Expect("PLAN_CHANGED", () => ResultTaskActions.Prepare(store, unaccepted));
        Check(JsonNode.DeepEquals(ResultTaskActions.Prepare(store, payload), prepared), "An accepted request remains recoverable when the source report later changes.");
        SaveResult(store, parentId, result);

        var defaults = new Configuration(store).Read(); defaults["agent"] = "codex";
        defaults["agentDefaults"]!["codex"]!["model"] = "first-default";
        store.WriteJson(Path.Combine(store.Root, "config.json"), defaults);
        var defaultsPayload = Payload(); var first = ResultTaskActions.Prepare(store, defaultsPayload);
        defaults["agentDefaults"]!["codex"]!["model"] = "later-default"; store.WriteJson(Path.Combine(store.Root, "config.json"), defaults);
        var recovered = ResultTaskActions.Prepare(store, defaultsPayload);
        Check(first["task"]!["execution"]!["model"]!.GetValue<string>() == "first-default" && JsonNode.DeepEquals(first, recovered), "Default CLI/model/effort are resolved once, not changed while recovering.");
        Check(File.ReadAllBytes(Path.Combine(store.RunDirectory(parentId), "task.json")).SequenceEqual(parentBytes) &&
            File.ReadAllBytes(Path.Combine(store.RunDirectory(parentId), "result.json")).SequenceEqual(resultBytes), "Result-to-task preparation preserves original task/report bytes.");

        if (kind == "feature-implement")
        {
            var largeModel = model.DeepClone().AsObject(); largeModel["summary"] = new string('调', 32768);
            largeModel["plans"]![0]!["steps"] = new JsonArray(Enumerable.Range(0, 7).Select(_ => (JsonNode?)JsonValue.Create(new string('测', 512))).ToArray());
            var largeResult = WorkflowResult.FromModel(largeModel.ToJsonString(), parent, "succeeded", 0, null, null, expectedSchemaVersion: 3);
            Check(WorkflowResult.IsValidStoredV3(largeResult), "A near-budget multilingual plan remains a valid complete model result.");
            SaveResult(store, parentId, largeResult);
            var largePayload = Payload(); largePayload["proposalId"] = WorkflowResult.WithProposalIds(largeResult)["nextActions"]![0]!["proposalId"]!.DeepClone();
            var large = ResultTaskActions.Prepare(store, largePayload);
            Check(JsonNode.DeepEquals(large["task"]!["context"]!["resultPlan"]!["plan"], largeResult["plans"]![0]), "Large plans preserve every implementation and acceptance step unchanged.");
            SaveResult(store, parentId, result);
            var record = store.ReadTask(parentId); var originalRecord = record.DeepClone();
            record["config"]!["worktreeBase"] = new string('b', 64); store.WriteJson(Path.Combine(store.RunDirectory(parentId), "task.json"), record);
            Expect("SOURCE_REVISION_INVALID", () => ResultTaskActions.Prepare(store, Payload()));
            store.WriteJson(Path.Combine(store.RunDirectory(parentId), "task.json"), originalRecord);
        }
    }

    private static void SaveResult(Store store, string id, JsonObject result)
    {
        store.WriteJson(Path.Combine(store.RunDirectory(id), "result.json"), result);
        var status = store.ReadStatus(id); status["state"] = "succeeded"; status["exitCode"] = 0;
        store.WriteJson(Path.Combine(store.RunDirectory(id), "status.json"), status);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (ProtocolException error) when (error.Code == code) { }
    }
}
