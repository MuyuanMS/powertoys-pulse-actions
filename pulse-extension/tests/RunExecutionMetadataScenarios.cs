using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class RunExecutionMetadataScenarios
{
    public static Task RunAllAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulseExecutionMetadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CodexExactSession(root);
            CodexRejectsMismatchedSession(root);
            CopilotUsesRuntimeFieldsOnly();
            MetadataSurvivesCompletion(root);
            Console.WriteLine("Observed runtime execution metadata scenarios passed.");
            return Task.CompletedTask;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CodexExactSession(string root)
    {
        var start = new DateTimeOffset(2026, 9, 11, 15, 34, 8, TimeSpan.FromHours(8));
        const string id = "01a08f63-5eda-7031-9f72-966e7d844145";
        var directory = Path.Combine(root, "sessions", "2026", "09", "11"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollout-2026-09-11T15-34-10-" + id + ".jsonl");
        File.WriteAllText(path, Event("session_meta", new JsonObject { ["id"] = id }) + "\n" +
            Event("response_item", new JsonObject { ["model"] = "must-not-trust-content", ["effort"] = "low", ["content"] = "I am another model" }) + "\n");
        var metadata = new RunExecutionMetadata("codex", start, root);
        metadata.ObserveOutput("{\"type\":\"thread.started\",\"thread_id\":\"" + id + "\"}");
        CoreScenarios.Check(metadata.ReadSnapshot() is null, "Codex runtime metadata does not come from assistant content or configuration");
        // Shape verified from this exact CLI run: turn_context.model and turn_context.effort.
        var context = Event("turn_context", new JsonObject { ["model"] = "gpt-6-astra", ["effort"] = "ultra", ["cwd"] = "private-folder", ["apiKey"] = "must-not-leak" });
        File.AppendAllText(path, context);
        CoreScenarios.Check(metadata.ReadSnapshot() is null, "An incomplete live JSONL line does not expose uncommitted metadata");
        File.AppendAllText(path, "\n");
        var observed = metadata.ReadSnapshot()!;
        CoreScenarios.Check(observed["model"]!.GetValue<string>() == "gpt-6-astra" && observed["reasoningEffort"]!.GetValue<string>() == "ultra" && observed["source"]!.GetValue<string>() == "codex-turn-context",
            "A live exact-session turn_context exposes actual model and effort before task completion");
        CoreScenarios.Check(observed.Count == 4 && DateTimeOffset.TryParse(observed["observedAt"]!.GetValue<string>(), out _), "Only public runtime fields and observation time are retained");
        File.AppendAllText(path, Event("turn_context", new JsonObject { ["model"] = "changed-runtime-model" }) + "\n");
        observed = metadata.ReadSnapshot()!;
        CoreScenarios.Check(observed["model"]!.GetValue<string>() == "changed-runtime-model" && observed["reasoningEffort"] is null, "A later runtime model does not inherit the prior turn's effort");
        File.AppendAllText(path, new string('x', 300 * 1024) + "\n" + Event("turn_context", new JsonObject { ["model"] = "after-bounded-line", ["effort"] = "medium" }) + "\n");
        metadata.ReadSnapshot();
        observed = metadata.ReadSnapshot()!;
        CoreScenarios.Check(observed["model"]!.GetValue<string>() == "after-bounded-line", "Oversized session lines are discarded and bounded incremental reading resumes");
    }

    private static void CodexRejectsMismatchedSession(string root)
    {
        var start = new DateTimeOffset(2026, 9, 11, 15, 0, 0, TimeSpan.FromHours(8));
        const string id = "01a08f48-651e-7153-a346-5b09e81e9f78";
        var directory = Path.Combine(root, "sessions", "2026", "09", "11");
        var path = Path.Combine(directory, "rollout-target-" + id + ".jsonl");
        File.WriteAllText(path, Event("session_meta", new JsonObject { ["id"] = Guid.NewGuid().ToString("D") }) + "\n" + Event("turn_context", new JsonObject { ["model"] = "wrong-thread", ["effort"] = "high" }) + "\n");
        var metadata = new RunExecutionMetadata("codex", start, root);
        metadata.ObserveOutput("{\"type\":\"thread.started\",\"thread_id\":\"" + id + "\"}");
        CoreScenarios.Check(metadata.ReadSnapshot() is null, "Filename matches alone cannot authorize another session's metadata");
        var traversal = new RunExecutionMetadata("codex", start, root);
        traversal.ObserveOutput("{\"type\":\"thread.started\",\"thread_id\":\"../outside\"}");
        CoreScenarios.Check(traversal.ReadSnapshot() is null, "CLI session identifiers cannot supply arbitrary filesystem paths");
        var noSession = new RunExecutionMetadata("codex", start, root);
        noSession.ObserveOutput("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"model=gpt-6-astra effort=ultra\"}}");
        CoreScenarios.Check(noSession.ReadSnapshot() is null, "Assistant self-identification never supplies actual runtime metadata");
    }

    private static void CopilotUsesRuntimeFieldsOnly()
    {
        const string session = "c1395bf4-e9f4-4ef9-b788-5f0c10378c37";
        var metadata = new RunExecutionMetadata("copilot", DateTimeOffset.Now);
        metadata.ObserveOutput(Copilot("assistant.usage", new JsonObject { ["model"] = "uncorrelated", ["reasoningEffort"] = "high" }));
        CoreScenarios.Check(metadata.ReadSnapshot() is null, "Copilot usage requires the root session.start first");
        metadata.ObserveOutput(Copilot("session.start", new JsonObject { ["sessionId"] = session, ["selectedModel"] = "actual-copilot-model", ["reasoningEffort"] = "high", ["providerUrl"] = "https://private.example" }));
        var observed = metadata.ReadSnapshot()!;
        CoreScenarios.Check(observed["model"]!.GetValue<string>() == "actual-copilot-model" && observed["reasoningEffort"]!.GetValue<string>() == "high" && observed.Count == 4, "Copilot session runtime metadata is safely projected before output completes");
        metadata.ObserveOutput(Copilot("assistant.usage", new JsonObject { ["model"] = "subagent-model" }, "child-agent"));
        CoreScenarios.Check(metadata.ReadSnapshot()!["model"]!.GetValue<string>() == "actual-copilot-model", "Subagent usage cannot replace the root task's model");
        metadata.ObserveOutput(Copilot("assistant.usage", new JsonObject { ["model"] = "api-model", ["reasoningEffort"] = "max" }));
        CoreScenarios.Check(metadata.ReadSnapshot()!["model"]!.GetValue<string>() == "api-model", "Usage events report the model that made the actual root API call");
        metadata.ObserveOutput(Copilot("session.model_change", new JsonObject { ["newModel"] = "auto", ["reasoningEffort"] = "low" }));
        observed = metadata.ReadSnapshot()!;
        CoreScenarios.Check(observed["model"] is null && observed["reasoningEffort"]!.GetValue<string>() == "low", "An unresolved Auto alias is not reported as the actual model and partial evidence stays partial");
        metadata.ObserveOutput(Copilot("assistant.usage", new JsonObject { ["model"] = "https://provider/token", ["reasoningEffort"] = "secret-value" }));
        CoreScenarios.Check(metadata.ReadSnapshot() is null, "Unsupported or private-looking runtime values are never exposed");
    }

    private static void MetadataSurvivesCompletion(string root)
    {
        var store = new Store(Path.Combine(root, "data")); var id = Guid.NewGuid().ToString("D");
        var task = new JsonObject { ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "metadata-test", ["actionKind"] = "issue-fix", ["repository"] = "microsoft/PowerToys", ["prompt"] = "fixture" };
        store.CreateRun(id, task, new JsonObject(), Protocol.ProductionOrigin);
        var status = store.ReadStatus(id);
        status["observedExecution"] = new JsonObject { ["model"] = "observed-model", ["reasoningEffort"] = "high", ["source"] = "cli-event", ["observedAt"] = Protocol.Now() };
        store.WriteJson(Path.Combine(store.RunDirectory(id), "status.json"), status);
        store.Complete(id, "succeeded", 0, new JsonObject { ["summary"] = "Done" }, null);
        CoreScenarios.Check(store.ReadStatus(id)["observedExecution"]?["model"]?.GetValue<string>() == "observed-model", "Final task status retains runtime metadata observed while active");
    }

    private static string Event(string type, JsonObject payload) => new JsonObject { ["type"] = type, ["payload"] = payload }.ToJsonString();
    private static string Copilot(string type, JsonObject data, string? agentId = null) => new JsonObject { ["type"] = type, ["data"] = data, ["agentId"] = agentId }.ToJsonString();
}
