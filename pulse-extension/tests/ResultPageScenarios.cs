using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class ResultPageScenarios
{
    internal static Task RunAllAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulseResultPageTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(root); var id = Guid.NewGuid().ToString("D");
            var task = WorkflowV3Scenarios.TaskContext(); var model = WorkflowV3Scenarios.Model();
            const int count = 720;
            for (var i = 0; i < count; i++)
            {
                var finding = WorkflowV3Scenarios.Finding("finding-" + i, i == count - 1 ? "P0" : "P3");
                finding["details"] = string.Concat(Enumerable.Repeat("复核后的完整问题证据。", 80)) + i;
                model["findings"]!.AsArray().Add(finding);
            }
            var result = WorkflowResult.FromModel(model.ToJsonString(), task, "succeeded", 0, null, null, expectedSchemaVersion: 3);
            Check(WorkflowResult.IsValidStoredV3(result) && result["findings"]!.AsArray().Count == count, "All findings enter one complete structured report.");
            store.CreateRun(id, task, new JsonObject(), Protocol.ProductionOrigin);
            using (store.AcquireLock("run-" + id)) store.Complete(id, "succeeded", 0, result);
            var resultPath = Path.Combine(store.RunDirectory(id), "result.json");
            var original = File.ReadAllBytes(resultPath);
            Check(original.Length > 2 * 1024 * 1024, "The fixture exercises a durable result larger than the previous snapshot limit.");
            var detail = store.Detail(id);
            Check(JsonSerializer.SerializeToUtf8Bytes(detail, Protocol.JsonOptions).Length < Protocol.MaxOutputBytes, "Initial report frame stays bounded.");
            var paging = detail["resultPaging"]!.AsObject();
            var fingerprint = paging["fingerprint"]!.GetValue<string>();
            foreach (var section in paging["sections"]!.AsArray().OfType<JsonObject>())
            {
                var path = section["path"]!.GetValue<string>();
                var target = ArrayAt(detail["result"]!.AsObject(), path);
                var next = section["nextOffset"]?.GetValue<int>();
                while (next is not null)
                {
                    var page = ResultPages.Read(store, new JsonObject { ["runId"] = id, ["path"] = path, ["offset"] = next, ["limit"] = 71, ["fingerprint"] = fingerprint });
                    Check(JsonSerializer.SerializeToUtf8Bytes(page, Protocol.JsonOptions).Length < Protocol.MaxOutputBytes && page["fingerprint"]?.GetValue<string>() == fingerprint,
                        "Every page is bounded and tied to the same immutable report.");
                    foreach (var item in page["items"]!.AsArray()) target.Add(item?.DeepClone());
                    next = page["nextOffset"]?.GetValue<int>();
                }
                Check(target.Count == section["total"]!.GetValue<int>(), "Every page reconstructs its whole section.");
            }
            var restored = detail["result"]!["findings"]!.AsArray();
            Check(restored.Count == count && restored.Last()!["priority"]?.GetValue<string>() == "P0" && restored.Select(row => row!["id"]!.GetValue<string>()).Distinct().Count() == count,
                "A P0 after hundreds of findings stays visible; no duplicates or Top-N loss.");
            Check(WorkflowResult.HasConfirmedP0(store.ReadResult(id), task), "Approval decisions read the complete stored report, independent of pages.");
            Check(File.ReadAllBytes(resultPath).SequenceEqual(original), "Display pagination never rewrites the report.");
            foreach (var path in new[] { "__proto__.findings", "config", "task.prompt" })
                Reject("INVALID_REQUEST", () => ResultPages.Read(store, new JsonObject { ["runId"] = id, ["path"] = path, ["offset"] = 0, ["fingerprint"] = fingerprint }));
            Reject("RESULT_CHANGED", () => ResultPages.Read(store, new JsonObject { ["runId"] = id, ["path"] = "findings", ["offset"] = 0, ["fingerprint"] = new string('f', 64) }));
            Reject("INVALID_REQUEST", () => ResultPages.Read(store, new JsonObject { ["runId"] = id, ["path"] = "findings", ["offset"] = count + 1, ["fingerprint"] = fingerprint }));

            store.WriteJson(resultPath, new JsonObject());
            store.RecoverCompletion(id);
            Check(File.ReadAllBytes(resultPath).SequenceEqual(original), "The completion journal restores the full large report without running a task again.");
            var diagnosticId = Guid.NewGuid().ToString("D"); var diagnosticTask = WorkflowV3Scenarios.TaskContext(); var diagnosticModel = WorkflowV3Scenarios.Model();
            for (var i = 0; i < 520; i++) diagnosticModel["diagnostics"]!.AsArray().Add(new JsonObject
            {
                ["code"] = "FIXTURE_" + i, ["severity"] = "error", ["message"] = string.Concat(Enumerable.Repeat("完整诊断证据", 100)) + i, ["recovery"] = "inspectResult"
            });
            var diagnosticResult = WorkflowResult.FromModel(diagnosticModel.ToJsonString(), diagnosticTask, "succeeded", 0, null, null, expectedSchemaVersion: 3);
            store.CreateRun(diagnosticId, diagnosticTask, new JsonObject(), Protocol.ProductionOrigin);
            using (store.AcquireLock("run-" + diagnosticId)) store.Complete(diagnosticId, "succeeded", 0, diagnosticResult);
            var diagnosticDetail = store.Detail(diagnosticId);
            Check(diagnosticDetail["resultPaging"]?["sections"]?.AsArray().OfType<JsonObject>().Any(section => section["path"]?.GetValue<string>() == "diagnostics") == true,
                "Large diagnostic evidence and its blockers alias do not prevent a bounded initial frame.");
            Check(store.ReadResult(diagnosticId)!["diagnostics"]!.AsArray().Count == 520 && store.ReadResult(diagnosticId)!["blockers"]!.AsArray().Count == 520,
                "Paging preserves the full diagnostic evidence and compatibility projection on disk.");
            Console.WriteLine("PASS v3 report pages: complete large journal, bounded frames, immutable cursors, full findings including final P0, and unchanged history");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("PulseResultPageTests-", StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
        return Task.CompletedTask;
    }
    private static JsonArray ArrayAt(JsonObject result, string path)
    {
        JsonNode? current = result; foreach (var part in path.Split('.')) current = current![part];
        return current!.AsArray();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(string code, Func<JsonObject> operation)
    {
        try { operation(); } catch (ProtocolException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
