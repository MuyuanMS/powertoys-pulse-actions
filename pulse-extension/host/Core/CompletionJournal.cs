using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Host;

internal enum CompletionCheckpoint
{
    JournalCommitted,
    ResultProjected,
    EventAppended,
    EventSequenceCommitted,
    LegacyStatusProjected,
    StatusProjected
}

public sealed partial class Store
{
    internal const string CompletionJournalFile = "completion-journal.json";
    private static readonly JsonSerializerOptions JournalJsonOptions = new(Protocol.JsonOptions) { MaxDepth = 48 };
    private readonly Action<CompletionCheckpoint>? completionCheckpoint;

    // Controlled crash-boundary injection belongs only to the offline test assembly.
    internal Store(string root, Action<CompletionCheckpoint> checkpoint) : this(root) => completionCheckpoint = checkpoint;

    /// <summary>Caller holds the run lock. A committed journal always wins over a later fallback.</summary>
    public void Complete(string runId, string state, int? exitCode, JsonObject result, JsonObject? error = null)
    {
        if (!Terminal(state)) throw new ArgumentException("Complete requires a supported terminal state", nameof(state));
        runId = Protocol.RunId(runId);
        if (RecoverCompletionLocked(runId)) return;
        var status = ReadStatus(runId);
        if (!Protocol.IsActive(CompletionText(status, "state"))) return;
        var saved = ReadTask(runId);
        var finalResult = JsonNode.Parse(Redact(result.ToJsonString(Protocol.JsonOptions)), documentOptions: new JsonDocumentOptions { MaxDepth = 32 })!.AsObject();
        status["state"] = state;
        status["exitCode"] = exitCode;
        status["endedAt"] = Protocol.Now();
        status["updatedAt"] = status["endedAt"]!.DeepClone();
        status["resultFile"] = "result.json";
        status["error"] = error is null ? null : JsonNode.Parse(Redact(error.ToJsonString(Protocol.JsonOptions)));
        var outcome = CompletionText(finalResult, "outcome");
        var eventType = finalResult["schemaVersion"]?.ToString() is "2" or "3" &&
            outcome is "completed" or "blocked" or "failed" or "cancelled" or "interrupted" ? "workflow." + outcome : state;
        var summary = CompletionText(finalResult, "summary");
        if (summary.Length == 0) summary = state;
        var journal = new JsonObject
        {
            ["version"] = 1, ["runId"] = runId,
            ["taskFingerprint"] = Protocol.Fingerprint(saved["task"] ?? throw InvalidCompletion()),
            ["status"] = status, ["result"] = finalResult,
            ["event"] = new JsonObject { ["id"] = Guid.NewGuid().ToString("D"), ["type"] = eventType, ["text"] = summary.Length > 8192 ? summary[..8192] + "… [event truncated]" : summary },
            ["eventSequence"] = null
        };
        // This is the commit point. Result, log and status failures after it cannot erase evidence.
        WriteCompletionJournal(Path.Combine(RunDirectory(runId), CompletionJournalFile), journal);
        completionCheckpoint?.Invoke(CompletionCheckpoint.JournalCommitted);
        PublishCompletion(runId, journal);
    }

    public void RecoverCompletion(string runId)
    {
        using var held = AcquireLock("run-" + Protocol.RunId(runId));
        RecoverCompletionLocked(runId);
    }

    /// <summary>
    /// Caller holds the run lock. Only a verified completion journal authorizes finalization.
    /// A result.json without either journal returns false; worker identity must be checked first.
    /// </summary>
    internal bool RecoverCompletionLocked(string runId)
    {
        runId = Protocol.RunId(runId);
        var directory = RunDirectory(runId);
        var journal = ReadJson(Path.Combine(directory, CompletionJournalFile), JournalJsonOptions.MaxDepth, MaximumStoredResultBytes);
        if (journal is not null)
        {
            ValidateCompletion(runId, journal);
            PublishCompletion(runId, journal);
            return true;
        }
        // Older Hosts committed a status-only completion.json after saving result.json.
        var legacy = ReadJson(Path.Combine(directory, "completion.json"));
        if (legacy is null) return false;
        if (!ValidTerminalStatus(legacy) || ReadResult(runId) is null) throw InvalidCompletion();
        ReadTask(runId); // A completion cannot recreate a deleted task directory.
        CheckExecutionIdentity(ReadProjection(Path.Combine(directory, "status.json")), legacy);
        WriteProjection(Path.Combine(directory, "status.json"), legacy);
        return true;
    }

    private void ValidateCompletion(string runId, JsonObject journal)
    {
        if (journal["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 1 ||
            CompletionText(journal, "runId") != runId || journal["status"] is not JsonObject status || !ValidTerminalStatus(status) ||
            journal["result"] is not JsonObject || journal["event"] is not JsonObject terminalEvent ||
            !Guid.TryParseExact(CompletionText(terminalEvent, "id"), "D", out _) ||
            CompletionText(terminalEvent, "type") is not ("succeeded" or "failed" or "cancelled" or "interrupted" or "workflow.completed" or "workflow.blocked" or "workflow.failed" or "workflow.cancelled" or "workflow.interrupted") ||
            terminalEvent["text"] is not JsonValue text || !text.TryGetValue<string>(out var summary) || summary.Length is 0 or > 8300 ||
            !journal.ContainsKey("eventSequence") || journal["eventSequence"] is not null && !NonnegativeSequence(journal["eventSequence"], out _))
            throw InvalidCompletion();
        var saved = ReadTask(runId);
        if (saved["task"] is not JsonObject task || CompletionText(journal, "taskFingerprint") != Protocol.Fingerprint(task)) throw InvalidCompletion();
        if (journal["eventSequence"] is not null &&
            (!NonnegativeSequence(status["sequence"], out var statusSequence) || !NonnegativeSequence(journal["eventSequence"], out var eventSequence) || statusSequence != eventSequence)) throw InvalidCompletion();
        CheckExecutionIdentity(ReadProjection(Path.Combine(RunDirectory(runId), "status.json")), status);
    }

    private static void CheckExecutionIdentity(JsonObject? current, JsonObject terminal)
    {
        if (current is null || !Protocol.IsActive(CompletionText(current, "state"))) return;
        if (CompletionText(current, "createdAt") is { Length: > 0 } created && created != CompletionText(terminal, "createdAt")) throw InvalidCompletion();
        // Do not turn another live worker's active snapshot into this journal's terminal snapshot.
        if (current["worker"] is not null && !JsonNode.DeepEquals(current["worker"], terminal["worker"])) throw InvalidCompletion();
    }

    private void PublishCompletion(string runId, JsonObject journal)
    {
        var directory = RunDirectory(runId);
        var status = journal["status"]!.AsObject();
        WriteProjection(Path.Combine(directory, "result.json"), journal["result"]!.AsObject());
        completionCheckpoint?.Invoke(CompletionCheckpoint.ResultProjected);
        if (journal["eventSequence"] is null)
        {
            var terminalEvent = journal["event"]!.AsObject();
            var sequence = EnsureCompletionEvent(runId, terminalEvent);
            completionCheckpoint?.Invoke(CompletionCheckpoint.EventAppended);
            status["sequence"] = sequence;
            journal["eventSequence"] = sequence;
            WriteCompletionJournal(Path.Combine(directory, CompletionJournalFile), journal);
            completionCheckpoint?.Invoke(CompletionCheckpoint.EventSequenceCommitted);
        }
        // Preserve the old file format as a compatibility projection, never as the only result copy.
        WriteProjection(Path.Combine(directory, "completion.json"), status);
        completionCheckpoint?.Invoke(CompletionCheckpoint.LegacyStatusProjected);
        WriteProjection(Path.Combine(directory, "status.json"), status);
        completionCheckpoint?.Invoke(CompletionCheckpoint.StatusProjected);
    }

    private long EnsureCompletionEvent(string runId, JsonObject terminalEvent)
    {
        using var held = AcquireLock("events-" + runId);
        var path = Path.Combine(RunDirectory(runId), "events.jsonl");
        RepairPartialEventTail(path);
        var id = CompletionText(terminalEvent, "id");
        if (File.Exists(path))
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > MaxLogBytes + 64 * 1024) throw InvalidCompletion();
            using var reader = new StreamReader(file, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length > 64 * 1024) throw InvalidCompletion();
                if (!line.Contains(id, StringComparison.Ordinal)) continue;
                try
                {
                    if (JsonNode.Parse(line) is JsonObject item && CompletionText(item, "completionId") == id)
                        return NonnegativeSequence(item["sequence"], out var sequence) ? sequence : throw InvalidCompletion();
                }
                catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { throw InvalidCompletion(); }
            }
        }
        return AppendEventLocked(runId, CompletionText(terminalEvent, "type"), CompletionText(terminalEvent, "text"), new JsonObject { ["completionId"] = id });
    }

    private JsonObject? ReadProjection(string path)
    {
        try { return Path.GetFileName(path) == "result.json" ? ReadJson(path, 32, MaximumStoredResultBytes) : ReadJson(path); }
        catch (ProtocolException error) when (error.Code == "RECORD_UNREADABLE") { return null; }
    }

    private void WriteCompletionJournal(string path, JsonObject journal)
    {
        var maximum = journal["result"]?["schemaVersion"]?.ToString() == "3" ? MaximumStoredResultBytes : 2 * 1024 * 1024;
        if (JsonSerializer.SerializeToUtf8Bytes(journal, JournalJsonOptions).Length > maximum)
            throw new ProtocolException("OUTPUT_TOO_LARGE", "The completion evidence exceeds the supported journal size.", "Preserve the existing task records and execution logs. No unreadable completion journal was written.");
        WriteJson(path, journal, JournalJsonOptions);
    }

    private void WriteProjection(string path, JsonObject snapshot)
    {
        if (!JsonNode.DeepEquals(ReadProjection(path), snapshot)) WriteJson(path, snapshot);
    }

    private static bool ValidTerminalStatus(JsonObject status) => Terminal(CompletionText(status, "state")) &&
        CompletionText(status, "resultFile") == "result.json" &&
        DateTimeOffset.TryParse(CompletionText(status, "createdAt"), out _) && DateTimeOffset.TryParse(CompletionText(status, "endedAt"), out _) &&
        (status["exitCode"] is null || status["exitCode"] is JsonValue exit && exit.TryGetValue<int>(out _));
    private static bool Terminal(string state) => state is "succeeded" or "failed" or "cancelled" or "interrupted";
    private static bool NonnegativeSequence(JsonNode? node, out long sequence)
    {
        sequence = 0;
        return node is JsonValue value && (value.TryGetValue<long>(out sequence) || value.TryGetValue<int>(out var narrow) && (sequence = narrow) >= 0) && sequence >= 0;
    }
    private static string CompletionText(JsonObject value, string name) => value[name] is JsonValue node && node.TryGetValue<string>(out var text) ? text : "";
    private static ProtocolException InvalidCompletion() => new("RECORD_UNREADABLE", "The saved task completion could not be verified.", "Preserve the task directory and completion journals. Repair local file access or inspect the saved evidence before retrying; the task was not restarted.");
}
