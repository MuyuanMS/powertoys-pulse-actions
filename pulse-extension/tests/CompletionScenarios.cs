using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Crash-boundary completion recovery using isolated files and in-memory protocol frames.</summary>
internal static class CompletionScenarios
{
    internal static Task RunAllAsync()
    {
        EveryCommittedBoundaryRetainsEvidence();
        LockedProjectionsLeaveRecoverableJournals();
        ExistingJournalWinsOverDamagedProjections();
        LegacyCompletionRemainsReadable();
        ResultAloneDoesNotFinalizeALiveWorker();
        RejectsUnrelatedOrInvalidJournals();
        BoundedDetailsPreserveTheCanonicalResult();
        SummarySeparatesCompletionFromFindings();
        Console.WriteLine("PASS completion: durable embedded evidence, all projection crash boundaries, locked files, idempotent events, legacy recovery, worker separation and bounded detail framing (offline)");
        return Task.CompletedTask;
    }

    private static void EveryCommittedBoundaryRetainsEvidence()
    {
        foreach (var state in new[] { "succeeded", "failed", "cancelled", "interrupted" })
        foreach (var checkpoint in Enum.GetValues<CompletionCheckpoint>())
        {
            using var fixture = new Fixture();
            var error = state == "succeeded" ? null : new ProtocolException("FIXTURE_STOPPED", "The controlled run stopped.").ToJson();
            var exitCode = state == "succeeded" ? 0 : state == "failed" ? 17 : (int?)null;
            var input = state == "succeeded" ? Result() : WorkflowResult.RecoverExisting(Result(), state, "FIXTURE_STOPPED", "The controlled run stopped.", fixture.Task, exitCode);
            var expected = input.DeepClone();
            var interrupted = false;
            var crashing = new Store(fixture.Root, stage =>
            {
                if (stage != checkpoint) return;
                interrupted = true;
                throw new IOException("Controlled completion interruption at " + checkpoint);
            });
            ExpectIo(() => { using var held = crashing.AcquireLock("run-" + fixture.RunId); crashing.Complete(fixture.RunId, state, exitCode, input, error); });
            Check(interrupted, "The requested durable-write checkpoint must be reached.");
            Check(File.Exists(fixture.JournalPath), "The complete result and terminal status must be journaled before every vulnerable projection.");
            input["summary"] = "Caller mutation after the process stopped.";
            fixture.Store.RecoverCompletion(fixture.RunId);
            Check(JsonNode.DeepEquals(fixture.Store.ReadResult(fixture.RunId), expected), "Recovery must retain all findings, artifacts, checks and diagnostics at " + checkpoint);
            var status = fixture.Store.ReadStatus(fixture.RunId);
            Check(status["state"]!.GetValue<string>() == state && JsonNode.DeepEquals(status["error"], error) &&
                (status["exitCode"] is null ? exitCode is null : status["exitCode"]!.GetValue<int>() == exitCode), "Recovery retains the actual lifecycle, error and nullable exit code.");
            Check(CompletionEvents(fixture).Count == 1, "Recovery must not duplicate a terminal event after a lost event acknowledgement.");
            var before = fixture.Files();
            fixture.Store.RecoverCompletion(fixture.RunId);
            using (fixture.Store.AcquireLock("run-" + fixture.RunId))
                fixture.Store.Complete(fixture.RunId, "interrupted", null, new JsonObject { ["summary"] = "A later empty fallback must not win." });
            Check(SameFiles(before, fixture.Files()), "Repeated recovery and later fallback leave the committed result byte-for-byte unchanged.");
        }
    }

    private static void LockedProjectionsLeaveRecoverableJournals()
    {
        foreach (var name in new[] { "result.json", "events.jsonl", "status.json" })
        {
            using var fixture = new Fixture();
            var path = Path.Combine(fixture.Directory, name);
            if (!File.Exists(path)) fixture.Store.WriteJson(path, new JsonObject { ["summary"] = "Earlier partial evidence." });
            var expected = Result();
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ExpectIo(() => { using var held = fixture.Store.AcquireLock("run-" + fixture.RunId); fixture.Store.Complete(fixture.RunId, "succeeded", 0, expected); });
                Check(File.Exists(fixture.JournalPath), "A sharing violation must not discard the committed result.");
                var journal = fixture.Store.ReadJson(fixture.JournalPath)!;
                Check(JsonNode.DeepEquals(journal["result"], expected) && journal["status"]!["state"]!.GetValue<string>() == "succeeded", "The journal contains the complete evidence and intended terminal state even while a projection is locked.");
            }
            fixture.Store.RecoverCompletion(fixture.RunId);
            Check(JsonNode.DeepEquals(fixture.Store.ReadResult(fixture.RunId), expected) && fixture.Store.ReadStatus(fixture.RunId)["state"]!.GetValue<string>() == "succeeded", "Releasing a locked projection permits recovery without restarting work.");
            Check(CompletionEvents(fixture).Count == 1, "File-lock recovery retains one terminal event.");
        }
        using (var fixture = new Fixture())
        {
            FileStream? journalLock = null;
            var failing = new Store(fixture.Root, checkpoint =>
            {
                if (checkpoint == CompletionCheckpoint.EventAppended)
                    journalLock = new FileStream(fixture.JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            });
            try
            {
                ExpectIo(() => failing.Complete(fixture.RunId, "succeeded", 0, Result()));
                Check(fixture.Store.ReadJson(fixture.JournalPath)!["eventSequence"] is null && CompletionEvents(fixture).Count == 1,
                    "Failure replacing journal progress leaves its original full evidence and the flushed event intact.");
            }
            finally { journalLock?.Dispose(); }
            fixture.Store.RecoverCompletion(fixture.RunId);
            Check(CompletionEvents(fixture).Count == 1 && fixture.Store.ReadStatus(fixture.RunId)["state"]!.GetValue<string>() == "succeeded",
                "A journal replacement failure recovers the existing terminal event without duplication.");
        }
    }

    private static void ExistingJournalWinsOverDamagedProjections()
    {
        using var fixture = new Fixture();
        var expected = Result();
        fixture.Store.Complete(fixture.RunId, "succeeded", 0, expected);
        fixture.Store.WriteJson(Path.Combine(fixture.Directory, "result.json"), new JsonObject { ["summary"] = "An obsolete fallback" });
        var wrong = fixture.Store.ReadStatus(fixture.RunId); wrong["state"] = "interrupted"; wrong["exitCode"] = null;
        fixture.Store.WriteJson(Path.Combine(fixture.Directory, "status.json"), wrong);
        File.WriteAllText(Path.Combine(fixture.Directory, "completion.json"), "partial legacy projection");
        fixture.Store.RecoverCompletion(fixture.RunId);
        Check(JsonNode.DeepEquals(fixture.Store.ReadResult(fixture.RunId), expected) && fixture.Store.ReadStatus(fixture.RunId)["state"]!.GetValue<string>() == "succeeded", "A verified journal restores evidence overwritten by an obsolete projection.");
        File.Delete(Path.Combine(fixture.Directory, "result.json")); File.Delete(Path.Combine(fixture.Directory, "status.json"));
        fixture.Store.RecoverCompletion(fixture.RunId);
        Check(JsonNode.DeepEquals(fixture.Store.ReadResult(fixture.RunId), expected), "The embedded result can rebuild a missing result file and status file.");
    }

    private static void LegacyCompletionRemainsReadable()
    {
        using var fixture = new Fixture();
        var before = fixture.Store.ReadStatus(fixture.RunId);
        fixture.Store.Complete(fixture.RunId, "failed", 9, Result());
        File.Delete(fixture.JournalPath);
        var resultBytes = File.ReadAllBytes(Path.Combine(fixture.Directory, "result.json"));
        var legacyBytes = File.ReadAllBytes(Path.Combine(fixture.Directory, "completion.json"));
        fixture.Store.WriteJson(Path.Combine(fixture.Directory, "status.json"), before);
        fixture.Store.RecoverCompletion(fixture.RunId);
        Check(fixture.Store.ReadStatus(fixture.RunId)["state"]!.GetValue<string>() == "failed" && !File.Exists(fixture.JournalPath), "Historical status-only journals recover without requiring or manufacturing a new journal.");
        Check(File.ReadAllBytes(Path.Combine(fixture.Directory, "result.json")).SequenceEqual(resultBytes) && File.ReadAllBytes(Path.Combine(fixture.Directory, "completion.json")).SequenceEqual(legacyBytes), "Legacy result evidence and its journal remain unchanged.");
        File.Delete(Path.Combine(fixture.Directory, "result.json"));
        fixture.Store.WriteJson(Path.Combine(fixture.Directory, "status.json"), before);
        ExpectRecordError(() => fixture.Store.RecoverCompletion(fixture.RunId));
        Check(fixture.Store.ReadStatus(fixture.RunId)["state"]!.GetValue<string>() == "running", "A legacy journal with missing evidence must surface a storage problem, never fabricate completion.");
    }

    private static void ResultAloneDoesNotFinalizeALiveWorker()
    {
        using var fixture = new Fixture();
        using var current = Process.GetCurrentProcess();
        var status = fixture.Store.ReadStatus(fixture.RunId);
        status["worker"] = new JsonObject { ["pid"] = current.Id, ["startTimeUtc"] = current.StartTime.ToUniversalTime().ToString("O") };
        fixture.Store.WriteJson(Path.Combine(fixture.Directory, "status.json"), status);
        var evidence = Result(); fixture.Store.WriteJson(Path.Combine(fixture.Directory, "result.json"), evidence);
        var before = fixture.Files();
        using (fixture.Store.AcquireLock("run-" + fixture.RunId)) Check(!fixture.Store.RecoverCompletionLocked(fixture.RunId), "An orphan result is not authority to finalize a live worker.");
        Check(SameFiles(before, fixture.Files()), "Looking at partial result evidence changes neither process lifecycle nor task files.");
        // The runtime invokes this only after its separate exact-process and repository-lock checks.
        status.Remove("worker"); fixture.Store.WriteJson(Path.Combine(fixture.Directory, "status.json"), status);
        var recovered = WorkflowResult.RecoverExisting(evidence, "interrupted", "WORKER_INTERRUPTED", "The fixture worker ended without a terminal journal.", fixture.Task);
        fixture.Store.Complete(fixture.RunId, "interrupted", null, recovered, new ProtocolException("WORKER_INTERRUPTED", "The fixture worker ended without a terminal journal.").ToJson());
        var actual = fixture.Store.ReadResult(fixture.RunId)!;
        foreach (var field in new[] { "findings", "artifacts", "validation" }) Check(JsonNode.DeepEquals(actual[field], evidence[field]), "Interruption keeps previously written " + field);
        Check(actual["outcome"]!.GetValue<string>() == "interrupted" && actual["nextActions"]!.AsArray().OfType<JsonObject>().Any(action => action["kind"]!.GetValue<string>() == "comment"), "Recovery retains the original comment proposal as evidence of the completed work.");
        var projected = fixture.Store.Detail(fixture.RunId)["result"]!;
        Check(projected["nextActions"]!.AsArray().OfType<JsonObject>().Where(action => action["kind"]!.GetValue<string>() is "approve" or "comment" or "close" or "requestChanges" or "suggestChanges").All(action => action["availability"]?["enabled"]?.GetValue<bool>() == false), "Recovered interruption disables publishing while preserving proposed text and the reason it is unavailable.");
    }

    private static void RejectsUnrelatedOrInvalidJournals()
    {
        foreach (var change in new Action<JsonObject>[]
        {
            journal => journal["runId"] = Guid.NewGuid().ToString("D"),
            journal => journal["taskFingerprint"] = "different-task",
            journal => journal["version"] = 99,
            journal => journal["status"]!["state"] = "running",
            journal => journal["status"]!.AsObject().Remove("worker"),
            journal => journal["status"]!["worker"] = new JsonObject { ["pid"] = 999, ["startTimeUtc"] = "different-worker" },
            journal => journal["eventSequence"] = "invalid"
        })
        {
            using var fixture = new Fixture();
            var crashing = new Store(fixture.Root, stage => { if (stage == CompletionCheckpoint.JournalCommitted) throw new IOException("Controlled crash"); });
            ExpectIo(() => crashing.Complete(fixture.RunId, "succeeded", 0, Result()));
            var journal = fixture.Store.ReadJson(fixture.JournalPath)!; change(journal); fixture.Store.WriteJson(fixture.JournalPath, journal);
            ExpectRecordError(() => fixture.Store.RecoverCompletion(fixture.RunId));
            Check(fixture.Store.ReadStatus(fixture.RunId)["state"]!.GetValue<string>() == "running" && fixture.Store.ReadResult(fixture.RunId) is null, "Invalid/cross-task completion evidence cannot finalize or leak into this run.");
        }
    }

    private static void BoundedDetailsPreserveTheCanonicalResult()
    {
        using var fixture = new Fixture(largeContext: true);
        var model = Result();
        model.Remove("structured"); model.Remove("cliExitCode"); model.Remove("blockers"); model.Remove("nextSteps");
        model["nextActions"] = new JsonArray(Enumerable.Range(0, 3).Select(index => (JsonNode?)new JsonObject { ["kind"] = "inspectResult", ["reason"] = "Inspect evidence " + index, ["body"] = new string('界', 19000) + " \"quoted\"" }).ToArray());
        var relaxed = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 32 };
        var modelText = model.ToJsonString(relaxed);
        Check(Encoding.UTF8.GetByteCount(modelText) <= ResultLimits.MaximumModelBytes, "The large Unicode fixture is a valid bounded model response.");
        var normalized = WorkflowResult.FromModel(modelText, fixture.Task, "succeeded", 0, null, null, expectedSchemaVersion: 2);
        Check(normalized["outcome"]!.GetValue<string>() == "completed" && JsonSerializer.SerializeToUtf8Bytes(normalized, Protocol.JsonOptions).Length > 660 * 1024, "The fixture retains a valid near-limit canonical result and its compatibility projection.");
        fixture.Store.Complete(fixture.RunId, "succeeded", 0, normalized);
        var originalTask = File.ReadAllBytes(Path.Combine(fixture.Directory, "task.json"));
        var detail = fixture.Store.Detail(fixture.RunId);
        Check(detail["taskContextOmitted"]?.GetValue<bool>() == true && detail["task"]!["prompt"] is null && detail["task"]!["context"] is null, "Only repeated task input is omitted to fit the full result into a response.");
        var expected = WorkflowResult.WithProposalAvailability(normalized, fixture.Task, fixture.Store.ReadStatus(fixture.RunId));
        expected["findingSummary"] = new JsonObject { ["unresolved"] = 1, ["high"] = 0, ["medium"] = 0, ["low"] = 1 };
        Check(JsonNode.DeepEquals(detail["result"], expected), "Canonical evidence, proposal IDs and every proposal body must remain complete.");
        using var framed = new MemoryStream();
        NativeFraming.WriteAsync(framed, new JsonObject { ["id"] = "large-detail", ["protocolVersion"] = 1, ["ok"] = true, ["data"] = detail }).GetAwaiter().GetResult();
        var wire = framed.ToArray(); var count = BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(0, 4));
        Check(count == wire.Length - 4 && count <= Protocol.MaxOutputBytes, "Exact Protocol.JsonOptions serialization fits the actual Native Messaging response limit.");
        var response = JsonNode.Parse(wire.AsSpan(4), documentOptions: new JsonDocumentOptions { MaxDepth = 32 })!;
        Check(response["ok"]!.GetValue<bool>() && JsonNode.DeepEquals(response["data"]!["result"], expected), "Framing preserves Unicode, quotes, findings and the complete structured proposal payload.");
        Check(File.ReadAllBytes(Path.Combine(fixture.Directory, "task.json")).SequenceEqual(originalTask), "A bounded detail read must not truncate the immutable task on disk.");

        using var oversized = new Fixture();
        var oversizedResult = Result(); oversizedResult["summary"] = new string('x', Protocol.MaxOutputBytes + 8192);
        oversized.Store.Complete(oversized.RunId, "succeeded", 0, oversizedResult);
        try { oversized.Store.Detail(oversized.RunId); throw new Exception("Oversized details should fail explicitly."); }
        catch (ProtocolException error) when (error.Code == "OUTPUT_TOO_LARGE") { }
        Check(JsonNode.DeepEquals(oversized.Store.ReadResult(oversized.RunId), oversizedResult), "A still-oversized response never truncates saved workflow evidence.");
    }

    private static JsonArray CompletionEvents(Fixture fixture) => new(fixture.Store.Events(fixture.RunId, 0, 100)["events"]!.AsArray().OfType<JsonObject>().Where(item => item["completionId"] is not null).Select(item => (JsonNode?)item.DeepClone()).ToArray());

    private static void SummarySeparatesCompletionFromFindings()
    {
        using var fixture = new Fixture();
        var result = Result();
        result["assessment"] = new JsonObject { ["subject"] = "original-pr", ["status"] = "failed", ["summary"] = new string('x', 1200), ["revisionSha"] = new string('a', 40) };
        var high = result["findings"]![0]!.DeepClone().AsObject(); high["id"] = "high-unverified"; high["severity"] = "high"; high["status"] = "unverified";
        var fixedFinding = high.DeepClone().AsObject(); fixedFinding["id"] = "already-fixed"; fixedFinding["status"] = "fixed";
        result["findings"]!.AsArray().Add(high); result["findings"]!.AsArray().Add(fixedFinding);
        fixture.Store.Complete(fixture.RunId, "succeeded", 0, result);
        var before = fixture.Files();
        var compact = fixture.Store.Detail(fixture.RunId, summaryOnly: true)["result"]!.AsObject();
        Check(compact["outcome"]!.GetValue<string>() == "completed" && compact["assessment"]!["status"]!.GetValue<string>() == "failed", "A completed workflow retains its distinct negative product assessment in list summaries.");
        Check(compact["findingSummary"]!["unresolved"]!.GetValue<int>() == 2 && compact["findingSummary"]!["high"]!.GetValue<int>() == 1 && compact["findingSummary"]!["low"]!.GetValue<int>() == 1, "List attention counts include open and unverified findings without counting fixed findings.");
        Check(compact["assessment"]!["summary"]!.GetValue<string>().Length <= 513 && compact["findings"] is null && compact["nextActions"] is null, "List projections keep assessment summaries bounded and omit full evidence and proposals.");
        var detail = fixture.Store.Detail(fixture.RunId)["result"]!.AsObject();
        Check(detail["assessment"]!["summary"]!.GetValue<string>().Length == 1200 && detail["nextActions"]![0]!["availability"] is JsonObject, "Full details retain complete assessment content and Host-calculated proposal availability.");
        Check(SameFiles(before, fixture.Files()), "Projection does not rewrite historical results, configuration or completion evidence.");
    }
    private static JsonObject Result() => new()
    {
        ["schemaVersion"] = 2, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "Retained findings: 中文 and \"quotes\".", ["needsReview"] = true,
        ["findings"] = new JsonArray(new JsonObject { ["id"] = "finding-one", ["title"] = "Retained finding", ["severity"] = "low", ["status"] = "open", ["path"] = "src/example.cs", ["line"] = 12, ["details"] = "Observed evidence remains readable.", ["evidence"] = new JsonArray("Before/after fixture observation") }),
        ["artifacts"] = new JsonArray(new JsonObject { ["path"] = "evidence/记录.patch", ["label"] = "Retained patch" }),
        ["validation"] = new JsonArray(new[] { "context", "local-review", "verification" }.Select(id => (JsonNode?)new JsonObject { ["id"] = id, ["name"] = id, ["status"] = "passed", ["required"] = true, ["details"] = "Completed controlled check.", ["evidence"] = new JsonArray("Fixture evidence " + id) }).ToArray()),
        ["diagnostics"] = new JsonArray(), ["nextActions"] = new JsonArray(new JsonObject { ["kind"] = "comment", ["reason"] = "Review retained evidence", ["body"] = "Retained proposed comment" }),
        ["review"] = null, ["structured"] = true, ["cliExitCode"] = 0, ["blockers"] = new JsonArray(),
        ["nextSteps"] = new JsonArray(new JsonObject { ["kind"] = "comment", ["reason"] = "Review retained evidence", ["body"] = "Retained proposed comment" })
    };
    private static void ExpectIo(Action action)
    {
        try { action(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        throw new Exception("Expected an explicit completion write failure.");
    }
    private static void ExpectRecordError(Action action)
    {
        try { action(); } catch (ProtocolException error) when (error.Code == "RECORD_UNREADABLE") { return; }
        throw new Exception("Expected invalid completion evidence to be rejected.");
    }
    private static bool SameFiles(Dictionary<string, string> first, Dictionary<string, string> second) => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var hash) && pair.Value == hash);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PulseCompletionTests-" + Guid.NewGuid().ToString("N"));
        internal readonly string RunId = Guid.NewGuid().ToString("D");
        internal readonly Store Store;
        internal readonly JsonObject Task;
        internal string Directory => Store.RunDirectory(RunId);
        internal string JournalPath => Path.Combine(Directory, Pulse.Host.Store.CompletionJournalFile);
        internal Fixture(bool largeContext = false)
        {
            Store = new Store(Root);
            Task = Protocol.ValidateTask(new JsonObject
            {
                ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "completion-fixture", ["actionKind"] = "pr-review", ["repository"] = "microsoft/PowerToys",
                ["target"] = new JsonObject { ["type"] = "pr", ["number"] = 42 }, ["expectedHeadSha"] = new string('a', 40),
                ["prompt"] = largeContext ? new string('\\', 117000) + " \"quoted context\"" : "Controlled fixture task", ["context"] = new JsonObject { ["title"] = largeContext ? new string('文', 1000) : "Fixture title" }
            });
            Check(JsonSerializer.SerializeToUtf8Bytes(Task, Protocol.JsonOptions).Length < Protocol.MaxInputBytes, "The original request remains within Native Messaging input limits.");
            Store.CreateRun(RunId, Task, new JsonObject { ["agent"] = "codex", ["repositoryKey"] = "completion-fixture", ["repoFolder"] = Root }, Protocol.ProductionOrigin);
            var status = Store.ReadStatus(RunId); status["state"] = "running";
            status["worker"] = new JsonObject { ["pid"] = 0, ["startTimeUtc"] = "unverifiable-fixture" };
            Store.WriteJson(Path.Combine(Directory, "status.json"), status);
        }
        internal Dictionary<string, string> Files() => System.IO.Directory.EnumerateFiles(Directory).ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Check(Path.GetDirectoryName(absolute)!.Equals(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("PulseCompletionTests-", StringComparison.Ordinal), "Completion cleanup stays within the isolated fixture directory.");
            if (System.IO.Directory.Exists(absolute)) System.IO.Directory.Delete(absolute, recursive: true);
        }
    }
}
