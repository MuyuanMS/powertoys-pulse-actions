using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

public sealed partial class Store
{
    public const long MaxLogBytes = 16 * 1024 * 1024;
    internal const int MaximumStoredResultBytes = 32 * 1024 * 1024;
    public string Root { get; }
    public Store(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "runs"));
        Directory.CreateDirectory(Path.Combine(Root, "locks"));
        Directory.CreateDirectory(Path.Combine(Root, "requests"));
    }

    public string RunDirectory(string runId) => Path.Combine(Root, "runs", Protocol.RunId(runId));
    public JsonObject ReadTask(string runId) => ReadJson(Path.Combine(RunDirectory(runId), "task.json")) ?? throw new ProtocolException("TASK_NOT_FOUND", "This task record was not found.");
    public JsonObject ReadStatus(string runId) => ReadJson(Path.Combine(RunDirectory(runId), "status.json")) ?? throw new ProtocolException("RECORD_UNREADABLE", "The task status file is missing.", "Preserve the task folder and check disk access, permissions, and worker processes. Do not resubmit the same request.");
    public JsonObject ReadView(string runId) => ReadJson(Path.Combine(RunDirectory(runId), "view.json")) ?? new JsonObject { ["read"] = false, ["handled"] = false };
    public JsonObject? ReadResult(string runId) => ReadJson(Path.Combine(RunDirectory(runId), "result.json"), 32, MaximumStoredResultBytes);

    public JsonObject? ReadJson(string path) => ReadJson(path, 32);

    private JsonObject? ReadJson(string path, int maximumDepth, int maximumBytes = 2 * 1024 * 1024)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > maximumBytes) throw new ProtocolException("RECORD_UNREADABLE", "The task snapshot exceeds the size limit.");
            return JsonNode.Parse(file, documentOptions: new JsonDocumentOptions { MaxDepth = maximumDepth }) as JsonObject
                ?? throw new JsonException("Expected object");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new ProtocolException("RECORD_UNREADABLE", "Local records are temporarily unreadable.", "Check data-folder permissions and disk status. Keep the original files and reconnect.");
        }
    }

    public void WriteJson(string path, JsonNode value) => WriteJson(path, value, Protocol.JsonOptions);

    private void WriteJson(string path, JsonNode value, JsonSerializerOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // File handles coordinate independent native hosts, workers, Chrome and Edge.
    // Do not acquire the same key recursively. OS handle closure releases a crashed owner's lock.
    public IDisposable AcquireLock(string key, TimeSpan? timeout = null)
    {
        var path = Path.Combine(Root, "locks", Protocol.Hash(key) + ".lock");
        var until = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds;
        do
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < until) { Thread.Sleep(25); }
            catch (IOException) { throw new ProtocolException("RESOURCE_BUSY", "Another Host process is using these local records.", "Retry shortly. Do not delete execution lock files."); }
        } while (true);
    }

    public T WithLock<T>(string key, Func<T> action)
    {
        using var held = AcquireLock(key);
        return action();
    }

    public IEnumerable<string> RunIds() => Directory.EnumerateDirectories(Path.Combine(Root, "runs"))
        .Select(Path.GetFileName).Where(name => Guid.TryParseExact(name, "D", out _)).Select(name => name!);

    // Call while holding the admission lock, before creating any task/operation record.
    public void EnsureAvailable()
    {
        JsonObject? maintenance;
        try { maintenance = ReadJson(Path.Combine(Root, "maintenance.json")); }
        catch (ProtocolException) { throw new ProtocolException("HOST_MAINTENANCE", "The installation maintenance record cannot be read.", "Wait for installation to finish. If interrupted, repair the installation."); }
        if (maintenance is null) return;
        try
        {
            var pid = maintenance["pid"]!.GetValue<int>();
            var started = DateTimeOffset.Parse(maintenance["startTimeUtc"]!.GetValue<string>());
            using var owner = System.Diagnostics.Process.GetProcessById(pid);
            if (owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != started.UtcTicks) return;
        }
        catch (ArgumentException) { return; } // The recorded installer process no longer exists.
        catch (Exception) { throw new ProtocolException("HOST_MAINTENANCE", "The installation maintenance process could not be verified.", "Wait for installation to finish or repair it."); }
        throw new ProtocolException("HOST_MAINTENANCE", "The Host is being installed, upgraded, or removed.", "Retry when maintenance finishes. New runs and GitHub submissions are temporarily blocked.");
    }

    public string? FindRequest(JsonObject task, string sourceOrigin)
    {
        var requestId = Protocol.RequiredString(task, "requestId", 128);
        var indexPath = RequestPath(requestId);
        var index = ReadJson(indexPath);
        if (index is null)
        {
            // task.json is authoritative if a process died before committing the rebuildable index.
            foreach (var runId in RunIds())
            {
                var saved = ReadTask(runId);
                if (saved["task"]?["requestId"]?.GetValue<string>() == requestId)
                {
                    index = new JsonObject { ["runId"] = runId, ["fingerprint"] = Protocol.Fingerprint(saved["task"]!), ["sourceOrigin"] = saved["sourceOrigin"]?.DeepClone() };
                    WriteJson(indexPath, index);
                    break;
                }
            }
        }
        if (index is null) return null;
        if (index["fingerprint"]?.GetValue<string>() != Protocol.Fingerprint(task) || index["sourceOrigin"]?.GetValue<string>() != sourceOrigin)
            throw new ProtocolException("REQUEST_CONFLICT", "requestId was already used for different task content.", "Retransmit the original task unchanged. Use a new requestId for an intentional rerun.");
        if (index["deleted"]?.GetValue<bool>() == true) throw new ProtocolException("TASK_REMOVED", "This request's history has been removed.", "Create a new request if you want to run it again.");
        return Protocol.RunId(index["runId"]!.GetValue<string>());
    }

    public void CreateRun(string runId, JsonObject task, JsonObject config, string sourceOrigin, JsonObject? promptTemplate = null)
    {
        var directory = RunDirectory(runId);
        var staging = Path.Combine(Root, "runs", ".pending-" + Protocol.RunId(runId));
        Directory.CreateDirectory(staging);
        var now = Protocol.Now();
        WriteJson(Path.Combine(staging, "task.json"), new JsonObject { ["runId"] = runId, ["task"] = task.DeepClone(), ["config"] = config.DeepClone(), ["createdAt"] = now, ["sourceOrigin"] = sourceOrigin, ["promptTemplate"] = promptTemplate?.DeepClone() });
        WriteJson(Path.Combine(staging, "view.json"), new JsonObject { ["read"] = false, ["handled"] = false });
        WriteJson(Path.Combine(staging, "status.json"), new JsonObject { ["state"] = "accepted", ["createdAt"] = now, ["updatedAt"] = now, ["sequence"] = 0 });
        // Incomplete admission stays invisible. Publish the fully initialized directory atomically.
        Directory.Move(staging, directory);
        WriteJson(RequestPath(task["requestId"]!.GetValue<string>()), new JsonObject { ["runId"] = runId, ["sourceOrigin"] = sourceOrigin, ["fingerprint"] = Protocol.Fingerprint(task) });
        AppendEvent(runId, "accepted", "The task has been saved and local execution is starting.");
    }

    private string RequestPath(string requestId) => Path.Combine(Root, "requests", Protocol.Hash(requestId) + ".json");

    public long AppendEvent(string runId, string type, string text, JsonObject? extra = null)
    {
        using var held = AcquireLock("events-" + runId);
        return AppendEventLocked(runId, type, text, extra);
    }

    private long AppendEventLocked(string runId, string type, string text, JsonObject? extra = null)
    {
        var directory = RunDirectory(runId);
        var path = Path.Combine(directory, "events.jsonl");
        RepairPartialEventTail(path);
        var sequence = LastEventSequence(runId);
        var limitReached = File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes;
        if (limitReached && File.Exists(Path.Combine(directory, "log.truncated"))) return sequence;
        if (limitReached)
        {
            File.WriteAllText(Path.Combine(directory, "log.truncated"), Protocol.Now());
            type = "log.truncated";
            text = "The activity log reached its 16 MiB limit. Execution continues and the final result is stored separately.";
            extra = null;
        }
        var item = new JsonObject { ["sequence"] = sequence + 1, ["time"] = Protocol.Now(), ["type"] = type, ["text"] = Redact(text.Length > 8192 ? text[..8192] + "… [event truncated]" : text) };
        if (extra is not null && Encoding.UTF8.GetByteCount(extra.ToJsonString()) <= 16 * 1024)
            foreach (var pair in extra)
                if (!item.ContainsKey(pair.Key)) item[pair.Key] = JsonNode.Parse(Redact(pair.Value?.ToJsonString() ?? "null"));
        var bytes = Encoding.UTF8.GetBytes(item.ToJsonString(Protocol.JsonOptions) + "\n");
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
        return sequence + 1;
    }

    private static void RepairPartialEventTail(string path)
    {
        if (!File.Exists(path)) return;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length == 0) return;
        file.Seek(-1, SeekOrigin.End);
        if (file.ReadByte() == '\n') return;
        var length = (int)Math.Min(file.Length, 256 * 1024);
        var tail = new byte[length];
        var offset = file.Length - length;
        file.Seek(offset, SeekOrigin.Begin);
        file.ReadExactly(tail);
        var newline = Array.LastIndexOf(tail, (byte)'\n');
        if (newline < 0 && offset > 0) throw new ProtocolException("RECORD_UNREADABLE", "The incomplete event-log tail could not be recovered.");
        file.SetLength(newline < 0 ? 0 : offset + newline + 1);
        file.Flush(flushToDisk: true);
    }

    public long LastEventSequence(string runId)
    {
        var path = Path.Combine(RunDirectory(runId), "events.jsonl");
        if (!File.Exists(path)) return 0;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = Math.Max(0, file.Length - 256 * 1024);
        file.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(file, Encoding.UTF8);
        var tail = reader.ReadToEnd();
        var end = tail.LastIndexOf('\n');
        if (end < 0) return 0;
        var start = tail.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
        try { return JsonNode.Parse(tail[start..end])?["sequence"]?.GetValue<long>() ?? 0; }
        catch (JsonException) { throw new ProtocolException("RECORD_UNREADABLE", "A complete event record is corrupt; its cursor cannot be verified."); }
    }

    public JsonObject Events(string runId, long afterSequence, int limit)
    {
        ReadTask(runId);
        var path = Path.Combine(RunDirectory(runId), "events.jsonl");
        var events = new JsonArray();
        var cursor = afterSequence;
        var bytes = 0;
        if (File.Exists(path))
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8);
            var pending = new StringBuilder();
            var buffer = new char[8192];
            int count;
            var done = false;
            while (!done && (count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] != '\n')
                    {
                        pending.Append(buffer[i]);
                        if (pending.Length > 64 * 1024) throw new ProtocolException("RECORD_UNREADABLE", "An event record exceeds the per-line limit.");
                        continue;
                    }
                    var line = pending.ToString();
                    pending.Clear();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JsonObject item;
                    try { item = (JsonObject)JsonNode.Parse(line)!; }
                    catch (JsonException) { throw new ProtocolException("RECORD_UNREADABLE", "A complete line in the event log is corrupt."); }
                    var sequence = item["sequence"]!.GetValue<long>();
                    if (sequence <= afterSequence) continue;
                    if (events.Count >= limit || bytes + Encoding.UTF8.GetByteCount(line) > 400 * 1024) { done = true; break; }
                    events.Add(item);
                    bytes += Encoding.UTF8.GetByteCount(line);
                    cursor = sequence;
                }
            }
            // A partial final line is still being written; never consume its sequence.
        }
        return new JsonObject { ["events"] = events, ["nextSequence"] = cursor, ["truncated"] = File.Exists(Path.Combine(RunDirectory(runId), "log.truncated")) };
    }

    public JsonObject Detail(string runId, bool summaryOnly = false)
    {
        var saved = ReadTask(runId);
        var status = ReadStatus(runId);
        status["sequence"] = LastEventSequence(runId);
        var task = (JsonObject)saved["task"]!.DeepClone();
        var result = ReadResult(runId);
        var reviewSummary = task["reviewOptions"] is null ? ReviewDecisions.Summary(task, result, status) : null;
        if (summaryOnly && reviewSummary is not null)
        {
            // Task lists need only the review state/counts. Full limitations remain
            // in task details and the separately fetched confirmation preview.
            reviewSummary.Remove("limitations");
            reviewSummary.Remove("reasons");
            reviewSummary.Remove("approvalReasons");
        }
        if (result?["findings"] is JsonArray findings)
        {
            var unresolved = findings.OfType<JsonObject>().Where(item => item["status"]?.GetValue<string>() is "open" or "unverified").ToArray();
            result["findingSummary"] = new JsonObject
            {
                ["unresolved"] = unresolved.Length,
                ["high"] = unresolved.Count(item => item["severity"]?.GetValue<string>() == "high"),
                ["medium"] = unresolved.Count(item => item["severity"]?.GetValue<string>() == "medium"),
                ["low"] = unresolved.Count(item => item["severity"]?.GetValue<string>() == "low")
            };
            if (result["schemaVersion"]?.ToString() == "3")
                foreach (var priority in new[] { "P0", "P1", "P2", "P3" })
                    result["findingSummary"]![priority] = unresolved.Count(item => item["priority"]?.GetValue<string>() == priority && item["confirmed"]?.GetValue<bool>() == true);
        }
        if (summaryOnly)
        {
            task.Remove("prompt"); task.Remove("context");
            if (result is not null)
            {
                var summary = result["summary"]?.GetValue<string>() ?? "";
                var compact = new JsonObject { ["summary"] = summary.Length > 2000 ? summary[..2000] + "…" : summary, ["needsReview"] = result["needsReview"]?.DeepClone() };
                foreach (var field in new[] { "schemaVersion", "outcome", "phase", "structured", "findingSummary", "reviewConclusion" })
                    if (result[field] is not null) compact[field] = result[field]!.DeepClone();
                if (result["report"] is JsonObject report)
                    compact["report"] = new JsonObject { ["complete"] = report["complete"]?.DeepClone(), ["rechecked"] = report["rechecked"]?.DeepClone() };
                foreach (var field in new[] { "featureAssessment", "bugAssessment", "e2eAssessment" })
                    if (result[field] is JsonObject assessmentPart)
                        compact[field] = new JsonObject { ["status"] = assessmentPart["status"]?.DeepClone(), ["level"] = assessmentPart["level"]?.DeepClone(), ["summary"] = assessmentPart["summary"]?.DeepClone() };
                if (result["assessment"] is JsonObject assessment)
                {
                    var projected = assessment.DeepClone().AsObject();
                    var explanation = projected["summary"]?.GetValue<string>() ?? "";
                    if (explanation.Length > 512) projected["summary"] = explanation[..512] + "…";
                    compact["assessment"] = projected;
                }
                result = compact;
            }
        }
        var prompt = saved["promptTemplate"]?.DeepClone() as JsonObject;
        prompt?.Remove("body"); // The immutable full prompt stays in task.json, not in every list response.
        if (!summaryOnly && result is not null) result = ProjectResult(runId, task, status, result);
        var detail = new JsonObject { ["runId"] = runId, ["task"] = task, ["config"] = saved["config"]?.DeepClone(), ["status"] = status, ["view"] = ReadView(runId), ["result"] = result, ["promptTemplate"] = prompt };
        if (!summaryOnly) detail["provenance"] = ReadJson(Path.Combine(RunDirectory(runId), "provenance.json"));
        if (reviewSummary is not null) detail["reviewSummary"] = reviewSummary;
        if (!summaryOnly && result?["schemaVersion"]?.ToString() == "3") ResultPages.Apply(detail, ReadResult(runId)!);
        const int detailBudget = Protocol.MaxOutputBytes - 2048;
        if (JsonSerializer.SerializeToUtf8Bytes(detail, Protocol.JsonOptions).Length > detailBudget)
        {
            // Full workflow evidence and proposals take priority over repeated input context.
            // The immutable task.json retains both fields for inspection and exact request replay.
            task.Remove("prompt"); task.Remove("context"); detail["taskContextOmitted"] = true;
            if (JsonSerializer.SerializeToUtf8Bytes(detail, Protocol.JsonOptions).Length > detailBudget)
                throw new ProtocolException("OUTPUT_TOO_LARGE", "The task details exceed the supported response size even without input context.", "The full result and original task remain in local records. Inspect their saved files; no workflow evidence was truncated.");
        }
        return detail;
    }

    internal JsonObject ProjectResult(string runId, JsonObject task, JsonObject status, JsonObject result)
    {
        var shown = WorkflowResult.WithProposalAvailability(result, task, status);
        if (task["actionKind"]?.GetValue<string>() == "pr-review")
        {
            var effective = ReviewFollowUps.EffectiveResult(this, runId, ReadResult(runId) ?? result);
            var current = WorkflowResult.WithProposalAvailability(effective, task, status);
            if (shown["nextActions"] is JsonArray actions && current["nextActions"] is JsonArray updated)
                foreach (var action in actions.OfType<JsonObject>())
                {
                    var candidate = updated.OfType<JsonObject>().FirstOrDefault(item => item["proposalId"]?.GetValue<string>() == action["proposalId"]?.GetValue<string>());
                    if (candidate?["availability"] is not null) action["availability"] = candidate["availability"]!.DeepClone();
                }
            if (shown["schemaVersion"]?.ToString() == "3")
            {
                shown["recommendation"] = WorkflowResult.Recommendation(effective, task);
                foreach (var field in new[] { "relatedConfirmedP0", "relatedConfirmedP1", "e2eEvidenceComplete" })
                    if (effective[field] is not null) shown[field] = effective[field]!.DeepClone();
            }
        }
        if (shown["nextActions"] is not null) shown["nextSteps"] = shown["nextActions"]!.DeepClone();
        return shown;
    }

    public JsonObject SetView(string runId, string field, bool value)
    {
        using var held = AcquireLock("view-" + runId);
        ReadTask(runId);
        if (Protocol.IsActive(ReadStatus(runId)["state"]?.GetValue<string>())) return Detail(runId);
        var view = ReadView(runId);
        view[field] = value;
        view["updatedAt"] = Protocol.Now();
        WriteJson(Path.Combine(RunDirectory(runId), "view.json"), view);
        return Detail(runId);
    }

    public void DeleteRun(string runId)
    {
        using var operationLock = AcquireLock("operations:" + runId);
        using var held = AcquireLock("accept");
        using var viewLock = AcquireLock("view-" + runId);
        using var runLock = AcquireLock("run-" + runId);
        var saved = ReadTask(runId);
        if (Protocol.IsActive(ReadStatus(runId)["state"]?.GetValue<string>()) || ReadView(runId)["handled"]?.GetValue<bool>() != true || ReadView(runId)["read"]?.GetValue<bool>() != true)
            throw new ProtocolException("TASK_NOT_DISPOSABLE", "Only read, handled, finished runs can be removed.");
        var operationsPath = Path.Combine(RunDirectory(runId), "operations");
        if (Directory.Exists(operationsPath) && Directory.EnumerateFiles(operationsPath, "*.json").Any(path => ReadJson(path)?["status"]?.GetValue<string>() is "prepared" or "submitting" or "unknown"))
            throw new ProtocolException("OPERATION_UNRESOLVED", "This task has an unresolved GitHub submission and cannot be removed.");
        if (new ResultActions(this).HasUnresolved(runId))
            throw new ProtocolException("OPERATION_UNRESOLVED", "This task has an unresolved GitHub proposal and cannot be removed.", "Finish, cancel, or reconcile its pending confirmation before removing the task record.");
        var indexPath = RequestPath(saved["task"]!["requestId"]!.GetValue<string>());
        var index = ReadJson(indexPath) ?? new JsonObject { ["runId"] = runId, ["sourceOrigin"] = saved["sourceOrigin"]?.DeepClone(), ["fingerprint"] = Protocol.Fingerprint(saved["task"]!) };
        index["deleted"] = true;
        WriteJson(indexPath, index);
        // The run id is a GUID and RunDirectory is confined to Root/runs; repo artifacts live elsewhere.
        Directory.Delete(RunDirectory(runId), recursive: true);
    }

    public static string Redact(string text) => CredentialPattern().Replace(text, "[REDACTED]");
    [GeneratedRegex(@"(?:github_pat_[A-Za-z0-9_]{10,}|gh[pousr]_[A-Za-z0-9]{10,}|sk-(?:proj-)?[A-Za-z0-9_-]{20,}|(?i:Bearer)\s+[A-Za-z0-9._~+/=-]{10,})", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();
}
