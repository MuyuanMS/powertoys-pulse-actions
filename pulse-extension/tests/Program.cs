using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--export-prompt-audit")
        {
            try
            {
                if (args.Length != 2) throw new ArgumentException("Usage: --export-prompt-audit <absolute empty directory>");
                await PromptAuditExporter.ExportAsync(args[1]);
                Console.WriteLine("Exported 26 synthetic assembled prompts and their schema, task snapshots and byte statistics. No CLI or GitHub task ran.");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        if (args.Length > 0 && args[0] == "--worker")
            return await RuntimeService.RunWorkerAsync(new Store(args[3]), args[1]);
        if (args.Length > 0 && args[0] == "--agent-test")
            return await RuntimeService.RunAgentTestWorkerAsync(new Store(args[3]), args[1]);
        if (args.Length > 0 && args[0] == "--live-log-test")
            return await RuntimeScenarios.LiveLogAsync(args.Length > 1 ? args[1] : "");
        if (args.Length > 0 && args[0] == "--live-command-test")
            return await RuntimeScenarios.LiveLogAsync(args.Length > 1 ? args[1] : "", validateCommand: true);
        if (args.Length > 0 && args[0] == "--live-agent-test")
        {
            var root = Path.Combine(Path.GetTempPath(), "PulseLiveAgentTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = await RuntimeService.TestAgentAsync(new Store(root), args[1], cliPath: args.Length > 2 ? args[2] : null);
                Console.WriteLine(result.ToJsonString());
                return result["success"]?.GetValue<bool>() == true ? 0 : 1;
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }
        if (args.Length > 0 && args[0] == "--fixture-launcher")
        {
            var detached = RuntimeScenarios.StartTestWorker(new Store(args[1]), args[2]);
            Console.WriteLine(detached ? "worker-started:detached" : "worker-started:guarded-fixture");
            await Console.Out.FlushAsync();
            await Task.Delay(TimeSpan.FromMinutes(2));
            return 0;
        }
        if (args.Contains("--version"))
        {
            Console.WriteLine(Environment.ProcessPath!.Contains("copilot", StringComparison.OrdinalIgnoreCase) ? "1.0.79" : "codex-cli 0.145.0");
            return 0;
        }
        if (args.Contains("--help"))
        {
            Console.WriteLine("--json --output-schema --sandbox --output-format --stream --allow-tool --deny-tool --available-tools --no-ask-user --ephemeral --skip-git-repo-check --no-custom-instructions --yolo --dangerously-bypass-approvals-and-sandbox --model --config --reasoning-effort");
            return 0;
        }
        if (args.Contains("--fixture-child"))
        {
            while (true) { await File.WriteAllTextAsync("child-heartbeat.txt", DateTimeOffset.UtcNow.ToString("O")); await Task.Delay(100); }
        }
        if (args.Contains("exec") || args.Contains("--output-format=json")) return await FakeCliAsync(args);
        try
        {
            await DiscoveryScenarios.RunAllAsync();
            await InstallationInventoryScenarios.RunAllAsync();
            await CliSelectionScenarios.RunAllAsync();
            await CoreScenarios.RunAllAsync();
            await PromptScenarios.RunAllAsync();
            await ActionScenarios.RunAllAsync();
            await TaskLookupScenarios.RunAllAsync();
            await TaskRerunScenarios.RunAllAsync();
            await ScopedReviewScenarios.RunAllAsync();
            await ReviewFollowUpScenarios.RunAllAsync();
            await WorkflowV3Scenarios.RunAllAsync();
            await ResultSchemaParityScenarios.RunAllAsync();
            await ResultContractRegressionScenarios.RunAllAsync();
            await TaskWorkflowV3Scenarios.RunAllAsync();
            await ResultTaskScenarios.RunAllAsync();
            await FinalProgressScenarios.RunAllAsync();
            await ResultPageScenarios.RunAllAsync();
            await CandidateSnapshotScenarios.RunAllAsync();
            await ExecutionScenarios.RunAllAsync();
            await WorkflowResultScenarios.RunAllAsync();
            await ResultTransportScenarios.RunAllAsync();
            await CompletionScenarios.RunAllAsync();
            await RunExecutionMetadataScenarios.RunAllAsync();
            await GitHubScenarios.RunAllAsync();
            await WebActionScenarios.RunAllAsync();
            await ResultActionScenarios.RunAllAsync();
            await RuntimeScenarios.RunAllAsync();
            await AgentTestScenarios.RunAllAsync();
            Console.WriteLine("All automated scenarios passed. Check NOTE lines for environment-limited acceptance. No model requests or real GitHub writes were made.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static async Task<int> FakeCliAsync(string[] args)
    {
        var copilot = args.Contains("--output-format=json");
        var promptPosition = Array.IndexOf(args, "--prompt");
        var prompt = promptPosition >= 0 ? args[promptPosition + 1] : await Console.In.ReadToEndAsync();
        if (prompt == RuntimeService.AgentTestPrompt) return await AgentTestScenarios.FakeModelAsync(args);
        await File.WriteAllTextAsync("received-prompt.txt", prompt);
        await File.WriteAllTextAsync("received-arguments.json", System.Text.Json.JsonSerializer.Serialize(args));
        if (prompt.Contains("fixture-child"))
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--fixture-child");
            using var child = Process.Start(start)!;
            await File.WriteAllTextAsync("child-identity.json", new JsonObject { ["pid"] = child.Id, ["startTimeUtc"] = child.StartTime.ToUniversalTime().ToString("O") }.ToJsonString());
        }
        Emit(copilot ? new JsonObject { ["type"] = "session.start", ["data"] = new JsonObject { ["sessionId"] = "fixture-session" } }
            : new JsonObject { ["type"] = "thread.started", ["thread_id"] = "fixture-thread" });
        var ticks = prompt.Contains("fixture-long") ? 600 : 12;
        for (var tick = 0; tick < ticks; tick++)
        {
            if (copilot) Emit(new JsonObject { ["type"] = "assistant.message_delta", ["data"] = new JsonObject { ["deltaContent"] = "Progress 中文 " + tick } });
            else Emit(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "command_execution", ["aggregated_output"] = "Progress 中文 " + tick } });
            Emit(new JsonObject { ["type"] = "fixture.raw", ["opaque"] = "stdout-marker-" + tick, ["credential"] = "ghp_1234567890abcdef1234567890abcdef123456" });
            Console.Error.WriteLine("Fixture diagnostic stderr-marker-" + tick + " 中文 ghp_1234567890abcdef1234567890abcdef123456");
            await Task.Delay(250);
        }
        if (prompt.Contains("fixture-fail"))
        {
            Emit(new JsonObject { ["type"] = "error", ["message"] = "permission denied" });
            return 7;
        }
        const string contextStart = "--- BEGIN TASK CONTEXT JSON ---";
        const string contextEnd = "--- END TASK CONTEXT JSON ---";
        var contextOffset = prompt.LastIndexOf(contextStart, StringComparison.Ordinal);
        var contextLimit = prompt.LastIndexOf(contextEnd, StringComparison.Ordinal);
        var taskContext = contextOffset >= 0 && contextLimit > contextOffset
            ? JsonNode.Parse(prompt[(contextOffset + contextStart.Length)..contextLimit])?.AsObject() : null;
        var checkIds = taskContext is null ? ["fixture"] : WorkflowResult.RequiredChecksForTask(taskContext).ToArray();
        var result = new JsonObject
        {
            ["schemaVersion"] = 2, ["outcome"] = "completed", ["phase"] = "reporting", ["findings"] = new JsonArray(),
            ["summary"] = "Fixture completed with Unicode 中文 and quoted input preserved.", ["needsReview"] = false,
            ["artifacts"] = new JsonArray(new JsonObject { ["label"] = "Captured prompt", ["path"] = "received-prompt.txt" }),
            ["validation"] = new JsonArray(checkIds.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["name"] = "Fixture " + id, ["status"] = "passed", ["required"] = true, ["details"] = "A controlled CLI fixture ran.", ["evidence"] = new JsonArray("received-prompt.txt") }).ToArray()),
            ["diagnostics"] = new JsonArray(), ["nextActions"] = new JsonArray(new JsonObject { ["kind"] = "none", ["reason"] = "No follow-up for fixture", ["body"] = "" }), ["review"] = null
        };
        if (prompt.Contains("Return schemaVersion: 3.", StringComparison.Ordinal) && taskContext is not null)
        {
            var previous = result;
            result = WorkflowV3Scenarios.Model(taskContext["actionKind"]!.GetValue<string>());
            foreach (var field in new[] { "summary", "validation", "artifacts" }) result[field] = previous[field]!.DeepClone();
            if (taskContext["expectedHeadSha"] is JsonValue revision)
            {
                result["assessment"]!["revisionSha"] = revision.DeepClone();
                if (result["reviewConclusion"] is JsonObject conclusion) conclusion["revisionSha"] = revision.DeepClone();
                if (result["review"] is JsonObject review) review["headSha"] = revision.DeepClone();
            }
        }
        // Fragment the final event deliberately to exercise StreamReader/JSONL assembly.
        var final = copilot ? new JsonObject { ["type"] = "result", ["subtype"] = "success", ["result"] = result.ToJsonString() }
            : new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = result.ToJsonString() } };
        var text = final.ToJsonString();
        Console.Write(text[..(text.Length / 2)]); await Console.Out.FlushAsync();
        await Task.Delay(50);
        Console.WriteLine(text[(text.Length / 2)..]); await Console.Out.FlushAsync();
        if (!copilot) Emit(new JsonObject { ["type"] = "turn.completed" });
        return 0;
    }

    private static void Emit(JsonObject value) { Console.WriteLine(value.ToJsonString()); Console.Out.Flush(); }
}
