using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Pulse.Host;

/// <summary>Turns a saved structured proposal into the existing extension-confirmed GitHub workflow.</summary>
public sealed class ResultActions(Store store)
{
    public JsonObject Prepare(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "runId", "proposalId", "kind", "attemptId", "retry", "content");
        var runId = Protocol.RunId(Protocol.RequiredString(payload, "runId", 36));
        var manualKind = payload["kind"] is null ? "" : Protocol.RequiredString(payload, "kind", 30);
        if (manualKind.Length > 0 && (payload["proposalId"] is not null || manualKind is not ("merge-pr" or "trigger-ci")))
            throw new ProtocolException("INVALID_REQUEST", "Choose one saved proposal or one fixed PR merge/CI action.");
        var proposalId = manualKind.Length > 0 ? "manual-" + manualKind : Protocol.RequiredString(payload, "proposalId", 128);
        var attemptId = payload["attemptId"] is null ? "" : Protocol.RunId(Protocol.RequiredString(payload, "attemptId", 36));
        var retry = payload["retry"] is JsonValue retryValue && retryValue.TryGetValue<bool>(out var retryFlag) ? retryFlag : false;
        if (payload["retry"] is not null && (payload["retry"] is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
            throw new ProtocolException("INVALID_REQUEST", "retry must be a boolean.");
        if (retry && attemptId.Length == 0) throw new ProtocolException("INVALID_REQUEST", "A deliberate retry requires a new attemptId.");
        // Match task deletion's outer lock without taking the admission lock recursively
        // inside WebActions.Prepare. Terminal task/result snapshots are otherwise immutable.
        using var held = store.AcquireLock("operations:" + runId, TimeSpan.FromSeconds(90));
        var saved = store.ReadTask(runId);
        var task = saved["task"]!.AsObject();
        var result = store.ReadResult(runId);
        var status = store.ReadStatus(runId);
        if (manualKind.Length == 0 && (result is null || result["schemaVersion"]?.ToString() is not ("2" or "3") || result["structured"]?.GetValue<bool>() != true)) throw NotActionable();
        if (manualKind.Length > 0 && task["target"]?["type"]?.GetValue<string>() != "pr")
            throw new ProtocolException("INVALID_TARGET", "Fixed merge and CI actions require a saved PR target.");
        var proposal = manualKind.Length > 0 ? new JsonObject { ["kind"] = manualKind } : WorkflowResult.FindProposal(result!, proposalId)
            ?? throw new ProtocolException("RESULT_PROPOSAL_NOT_FOUND", "This proposal is not present in the saved task result.", "Refresh the task result and select its exact proposal again.");
        var kind = Protocol.RequiredString(proposal, "kind", 30);
        if (kind is not ("create-pr" or "merge-pr" or "trigger-ci" or "close-as-duplicate"))
            throw new ProtocolException("RESULT_ACTION_UNSUPPORTED", "This proposal uses the existing review or comment editor.", "Open the proposal's review controls in the task details.");
        var content = EditedContent(payload, kind, attemptId);
        var contentFingerprint = content is null ? "" : Protocol.Fingerprint(content);
        var webActions = new WebActions(store);
        var requestId = RequestId(runId, proposalId, attemptId);
        var attemptsDirectory = Path.Combine(store.RunDirectory(runId), "result-action-attempts");
        var attemptPath = Path.Combine(attemptsDirectory, requestId + ".json");
        var association = store.ReadJson(attemptPath);
        if (association is not null)
        {
            if (association["retry"]?.GetValue<bool>() != retry || Text(association, "contentFingerprint") != contentFingerprint) throw new ProtocolException("REQUEST_CONFLICT", "This attemptId already belongs to different content or retry intent.", "Keep the same content when recovering a preparation, or use a new attempt for a deliberate edit.");
            return webActions.Get(new JsonObject { ["operationId"] = association["operationId"]!.DeepClone() }, "pulse-task://" + runId);
        }
        var history = webActions.ListResult(runId).OfType<JsonObject>().Where(item => Text(item, "proposalId") == proposalId).ToList();
        var priorAttempt = history.FirstOrDefault(item => Text(item, "requestId") == requestId);
        JsonObject Remember(JsonObject operation)
        {
            store.WriteJson(attemptPath, new JsonObject { ["operationId"] = operation["operationId"]!.DeepClone(), ["retry"] = retry, ["contentFingerprint"] = contentFingerprint });
            return operation;
        }
        if (priorAttempt is not null)
        {
            if ((priorAttempt["retryRequested"]?.GetValue<bool>() ?? false) != retry || Text(priorAttempt, "resultContentFingerprint") != contentFingerprint) throw new ProtocolException("REQUEST_CONFLICT", "This attemptId already belongs to different content or retry intent.");
            return Remember(priorAttempt); // Recover a crash between recording the draft and indexing its attempt.
        }
        var unresolved = history.FirstOrDefault(item => Text(item, "status") is "submitting" or "unknown" or "partial" or "prepared");
        if (unresolved is not null) return Remember(unresolved);
        var successful = history.FirstOrDefault(item => Text(item, "status") == "succeeded");
        if (successful is not null && (kind != "trigger-ci" || !retry)) return Remember(successful);
        // A legacy caller without an attempt identity remains exactly idempotent, including a cancelled/failed attempt.
        if (attemptId.Length == 0 && history.Count > 0) return Remember(history[0]);
        if (kind is not ("merge-pr" or "trigger-ci"))
        {
            var effective = ReviewFollowUps.EffectiveResult(store, runId, result!);
            var availability = WorkflowResult.ProposalAvailability(effective, task, status, proposal);
            if (availability["enabled"]?.GetValue<bool>() != true) throw NotActionable(availability);
        }
        var target = Protocol.RequireObject(task["target"]);
        var draft = new JsonObject
        {
            ["requestId"] = requestId, ["actionId"] = runId + ":" + proposalId, ["kind"] = kind,
            ["target"] = new JsonObject { ["repository"] = task["repository"]!.DeepClone(), ["type"] = target["type"]!.DeepClone(), ["number"] = target["number"]!.DeepClone() }
        };
        if (target["type"]?.GetValue<string>() == "pr") draft["expectedHeadSha"] = task["expectedHeadSha"]?.DeepClone();
        if (kind == "create-pr")
        {
            // Editing changes only human-authored content. Source identity and candidate evidence stay bound to the saved result.
            var pullRequest = proposal["pullRequest"]!.DeepClone().AsObject();
            pullRequest["draft"] = true;
            if (content is not null)
            {
                pullRequest["title"] = content["title"]!.DeepClone();
                pullRequest["body"] = content["body"]!.DeepClone();
            }
            draft["pullRequest"] = pullRequest;
        }
        if (kind == "close-as-duplicate")
        {
            draft["duplicateOf"] = proposal["duplicateOf"]!.DeepClone();
            draft["body"] = proposal["body"]?.DeepClone();
        }
        // Merge method and CI command are fixed by WebActions, not executable model fields.
        // The private source association cannot be supplied or read by an external web origin.
        return Remember(webActions.PrepareResult(new JsonObject { ["draft"] = draft }, "pulse-task://" + runId, attemptId, retry, contentFingerprint));
    }

    public JsonObject List(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "runId");
        var runId = Protocol.RunId(Protocol.RequiredString(payload, "runId", 36));
        store.ReadTask(runId);
        var operations = new WebActions(store).ListResult(runId);
        return new JsonObject
        {
            ["operations"] = new JsonArray(operations.Take(100).Select(item => item!.DeepClone()).ToArray()),
            ["totalCount"] = operations.Count, ["truncated"] = operations.Count > 100
        };
    }

    public bool HasUnresolved(string runId) => new WebActions(store).HasUnresolvedResult(Protocol.RunId(runId));

    private static string RequestId(string runId, string proposalId, string attemptId)
    {
        var identity = "pulse-result-action:" + runId + ":" + proposalId + (attemptId.Length == 0 ? "" : ":" + attemptId);
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-8{hex[13..16]}-a{hex[17..20]}-{hex[20..32]}";
    }

    internal static void ValidatePreparedSource(Store store, JsonObject record)
    {
        const string prefix = "pulse-task://";
        var source = record["sourceOrigin"]?.GetValue<string>() ?? "";
        if (!source.StartsWith(prefix, StringComparison.Ordinal)) return; // Independent website drafts retain their own confirmation flow.
        var runId = Protocol.RunId(source[prefix.Length..]);
        var saved = store.ReadTask(runId);
        var status = store.ReadStatus(runId);
        var result = store.ReadResult(runId);
        var draft = record["draft"]!.AsObject();
        var actionId = draft["actionId"]?.GetValue<string>() ?? "";
        if (!actionId.StartsWith(runId + ":", StringComparison.Ordinal)) throw NotActionable();
        var proposalId = actionId[(runId.Length + 1)..];
        var kind = draft["kind"]!.GetValue<string>();
        if (kind is "merge-pr" or "trigger-ci")
        {
            ValidateManualTarget(saved["task"]!.AsObject(), draft);
            return;
        }
        if (result is null || result["schemaVersion"]?.ToString() is not ("2" or "3") || result["structured"]?.GetValue<bool>() != true) throw NotActionable();
        var proposal = WorkflowResult.FindProposal(result, proposalId);
        if (proposal is null || proposal["kind"]?.GetValue<string>() != kind) throw NotActionable();
        var task = saved["task"]!.AsObject();
        if (!string.Equals(task["repository"]?.GetValue<string>(), draft["target"]?["repository"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase) ||
            !JsonNode.DeepEquals(task["target"]?["number"], draft["target"]?["number"]) || !JsonNode.DeepEquals(task["target"]?["type"], draft["target"]?["type"]))
            throw new ProtocolException("TARGET_MISMATCH", "The prepared action does not match its source task target.");
        if (kind == "close-as-duplicate" && !SameIssueReference(proposal["duplicateOf"] as JsonObject, draft["duplicateOf"] as JsonObject))
            throw new ProtocolException("TARGET_MISMATCH", "The original issue differs from the saved duplicate proposal.");
        if (kind == "create-pr")
        {
            var savedSource = proposal["pullRequest"] as JsonObject;
            var prepared = draft["pullRequest"] as JsonObject;
            if (savedSource is null || prepared is null || prepared["draft"]?.GetValue<bool>() != true ||
                NormalizeHead(Text(savedSource, "head")) != NormalizeHead(Text(prepared, "head")) || Text(savedSource, "base") != Text(prepared, "base") ||
                !string.Equals(Text(savedSource, "sourceHeadSha"), Text(prepared, "sourceHeadSha"), StringComparison.OrdinalIgnoreCase))
                throw new ProtocolException("TARGET_MISMATCH", "The Draft PR no longer matches the saved source branch, base and candidate revision.", "Return to the source result and prepare a new Draft PR confirmation. Existing prepared content cannot be rebound to another candidate.");
        }
        var effective = ReviewFollowUps.EffectiveResult(store, runId, result);
        var availability = WorkflowResult.ProposalAvailability(effective, task, status, proposal);
        if (availability["enabled"]?.GetValue<bool>() != true) throw NotActionable(availability);
    }

    private static void ValidateManualTarget(JsonObject task, JsonObject draft)
    {
        var target = task["target"] as JsonObject;
        var selected = draft["target"] as JsonObject;
        if (target?["type"]?.GetValue<string>() != "pr" || selected?["type"]?.GetValue<string>() != "pr" ||
            !string.Equals(task["repository"]?.GetValue<string>(), selected?["repository"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase) ||
            !JsonNode.DeepEquals(target?["number"], selected?["number"]) ||
            !string.Equals(task["expectedHeadSha"]?.GetValue<string>(), draft["expectedHeadSha"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
            throw new ProtocolException("TARGET_MISMATCH", "The confirmation no longer matches its saved PR target and revision.");
    }

    private static bool SameIssueReference(JsonObject? saved, JsonObject? prepared) => saved is not null && prepared is not null &&
        string.Equals(saved["repository"]?.GetValue<string>(), prepared["repository"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase) &&
        JsonNode.DeepEquals(saved["number"], prepared["number"]) &&
        Uri.TryCreate(saved["url"]?.GetValue<string>(), UriKind.Absolute, out var savedUrl) && Uri.TryCreate(prepared["url"]?.GetValue<string>(), UriKind.Absolute, out var preparedUrl) &&
        Uri.Compare(savedUrl, preparedUrl, UriComponents.HttpRequestUrl, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static string Text(JsonObject row, string field) => row[field]?.GetValue<string>() ?? "";

    private static string NormalizeHead(string value)
    {
        var parts = value.Split(':', 2);
        return parts.Length == 2 ? parts[0].ToLowerInvariant() + ":" + parts[1] : value;
    }

    private static JsonObject? EditedContent(JsonObject payload, string kind, string attemptId)
    {
        if (!payload.ContainsKey("content")) return null;
        if (kind != "create-pr" || attemptId.Length == 0) throw new ProtocolException("INVALID_REQUEST", "Draft PR content edits require a create-pr proposal and an explicit preparation attempt.");
        var content = Protocol.RequireObject(payload["content"]);
        Protocol.OnlyKeys(content, "title", "body");
        var title = Protocol.RequiredString(content, "title", 256);
        if (content["body"] is not JsonValue bodyValue || !bodyValue.TryGetValue<string>(out var body) || body.Length > 60000 || body.Contains('\0'))
            throw new ProtocolException("INVALID_REQUEST", "The Draft PR description must be text of at most 60000 characters.");
        return new JsonObject { ["title"] = title, ["body"] = body };
    }

    private static ProtocolException NotActionable(JsonObject? availability = null) => new("RESULT_NOT_ACTIONABLE",
        availability?["reasons"] is JsonArray { Count: > 0 } reasons ? string.Join(" ", reasons.Select(item => item!.GetValue<string>())) : "This saved workflow result does not support a GitHub submission.",
        "Inspect this proposal's eligibility reasons and the recorded workflow evidence before preparing another confirmation.");
}
