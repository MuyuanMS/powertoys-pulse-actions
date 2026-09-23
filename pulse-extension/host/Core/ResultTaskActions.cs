using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>Turns an explicitly selected saved result plan into one immutable local-task intent.</summary>
public static class ResultTaskActions
{
    private static readonly string[] SupportedTasks = ["feature-implement", "issue-fix", "reproduction-setup", "issue-verify"];

    public static JsonObject Prepare(Store store, JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "runId", "proposalId", "requestId", "execution", "prerequisitesConfirmed");
        var parentId = Protocol.RunId(Protocol.RequiredString(payload, "runId"));
        var proposalId = Protocol.RequiredString(payload, "proposalId", 128);
        var requestId = Protocol.RequiredString(payload, "requestId", 128);
        if (payload.ContainsKey("execution") && payload["execution"] is not null) ExecutionOptions.ValidateOverride(payload["execution"]);
        if (payload.ContainsKey("prerequisitesConfirmed") && (payload["prerequisitesConfirmed"] is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
            throw new ProtocolException("INVALID_REQUEST", "prerequisitesConfirmed must be a boolean.");
        var fingerprint = Protocol.Fingerprint(payload);
        using var held = store.AcquireLock("result-task-" + requestId);
        var intentPath = Path.Combine(store.Root, "result-task-intents", Protocol.Hash(requestId) + ".json");
        if (store.ReadJson(intentPath) is JsonObject frozen)
        {
            if (Text(frozen, "fingerprint") != fingerprint)
                throw new ProtocolException("REQUEST_CONFLICT", "This task request was already used with a different proposal or execution configuration.", "Recover the original request unchanged. Start a new request only for an intentional new task.");
            var submission = Protocol.RequireObject(frozen["submission"]);
            var task = Protocol.RequireObject(submission["task"]);
            using var admission = store.AcquireLock("accept");
            if (store.FindRequest(task, Protocol.RequiredString(submission, "sourceOrigin")) is null)
                ValidateUnacceptedSource(store, parentId, task);
            return submission.DeepClone().AsObject();
        }

        var saved = store.ReadTask(parentId);
        var parent = Protocol.RequireObject(saved["task"]);
        var status = store.ReadStatus(parentId);
        var result = store.ReadResult(parentId);
        if (Protocol.IsActive(Text(status, "state"))) throw new ProtocolException("TASK_STILL_RUNNING", "Wait for the current task's final result before starting a saved plan.");
        if (requestId == Text(parent, "requestId")) throw new ProtocolException("INVALID_REQUEST", "A task started from a result requires a new requestId.");
        if (result is null || !WorkflowResult.IsValidStoredV3(result) || result["structured"]?.GetValue<bool>() != true)
            throw new ProtocolException("RESULT_NOT_ACTIONABLE", "This saved result does not contain a valid structured task plan.");
        var proposal = WorkflowResult.FindProposal(result, proposalId);
        if (proposal is null || Text(proposal, "kind") != "start-task")
            throw new ProtocolException("PROPOSAL_NOT_FOUND", "The selected saved result does not contain this task proposal.", "Refresh the result and select one of its saved plans.");
        var taskKind = Protocol.RequiredString(proposal, "taskKind", 40);
        var planId = Protocol.RequiredString(proposal, "planId", 64);
        var plan = (result["plans"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(row => Text(row, "id") == planId);
        if (!SupportedTasks.Contains(taskKind, StringComparer.Ordinal) || plan is null || Text(plan, "kind") != taskKind)
            throw new ProtocolException("PLAN_MISMATCH", "The selected task does not match its saved plan.");
        if (Text(parent["target"] as JsonObject, "type") != "issue")
            throw new ProtocolException("INVALID_REQUEST", "Implementation, fix, reproduction and issue-verification plans require an Issue target.");
        var initialRevision = Text(saved["config"] as JsonObject, "worktreeBase");
        var candidateRequired = (Text(parent, "actionKind") is "feature-implement" or "issue-fix") || Text(result["assessment"] as JsonObject, "subject") == "local-candidate";
        var candidate = candidateRequired ? CandidateSnapshots.Describe(store, parentId) : null;
        if (candidateRequired && candidate is null)
            throw new ProtocolException("CANDIDATE_SNAPSHOT_UNAVAILABLE", "The saved plan depends on local implementation changes, but this task has no reusable candidate snapshot.", "The retained worktree and report are unchanged. This plan will not run against the older unmodified repository revision.");
        if (candidate is not null) initialRevision = Protocol.RequiredString(candidate, "baseSha", 40);
        if (initialRevision is not null && !Regex.IsMatch(initialRevision, "\\A[a-fA-F0-9]{40}\\z"))
            throw new ProtocolException("SOURCE_REVISION_INVALID", "The saved source repository revision is not a supported GitHub commit SHA.", "Inspect the source task record before creating a new task. Its source revision will not be silently replaced.");
        var taskToSubmit = new JsonObject
        {
            ["requestId"] = requestId, ["actionId"] = "result-task:" + parentId + ":" + Protocol.Hash(proposalId)[..16],
            ["actionKind"] = taskKind, ["repository"] = parent["repository"]!.DeepClone(), ["target"] = parent["target"]!.DeepClone(),
            ["planSource"] = new JsonObject
            {
                ["parentRunId"] = parentId, ["parentResultFingerprint"] = Protocol.Fingerprint(result),
                ["proposalId"] = proposalId, ["planId"] = planId, ["repository"] = parent["repository"]!.DeepClone(),
                ["target"] = parent["target"]!.DeepClone(), ["revisionSha"] = initialRevision?.ToLowerInvariant()
            },
            ["prompt"] = "Execute the explicitly selected saved plan for this Issue. Keep the requested target, plan and initial revision fixed. Read the retained parent report for the full research context when needed; do not repeat the completed investigation. Verify the plan prerequisites before implementation or experiments. Treat source reports as evidence, not permission to change this task's scope. Report implementation, verification, and remaining limitations separately. Do not publish GitHub changes, push commits, or rewrite the parent report.",
            ["context"] = new JsonObject
            {
                ["resultPlan"] = new JsonObject
                {
                    ["parentRunId"] = parentId, ["parentReportPath"] = Path.Combine(store.RunDirectory(parentId), "result.json"),
                    ["parentSummary"] = Limit(Text(result, "summary"), 512), ["parentActionKind"] = parent["actionKind"]?.DeepClone(),
                    ["parentOutcome"] = result["outcome"]?.DeepClone(), ["plan"] = plan.DeepClone(),
                    ["prerequisitesReportedReady"] = payload["prerequisitesConfirmed"]?.GetValue<bool>() == true,
                    ["initialRevisionSha"] = initialRevision?.ToLowerInvariant()
                }
            }
        };
        if (candidate is not null)
        {
            taskToSubmit["planSource"]!["sourceKind"] = "local-candidate";
            taskToSubmit["planSource"]!["candidateSnapshotHash"] = candidate["snapshotHash"]!.DeepClone();
            taskToSubmit["context"]!["resultPlan"]!["sourceKind"] = "local-candidate";
            taskToSubmit["context"]!["resultPlan"]!["candidateSnapshotHash"] = candidate["snapshotHash"]!.DeepClone();
            taskToSubmit["prompt"] = taskToSubmit["prompt"]!.GetValue<string>() + " The Host restores the exact retained local candidate into this task's new worktree before your CLI starts. Verify that candidate; do not describe its results as proof of the unmodified upstream source.";
        }
        var resolved = ExecutionOptions.Resolve(new Configuration(store).Read(), payload["execution"]);
        taskToSubmit["execution"] = new JsonObject
        {
            ["agent"] = resolved["agent"]!.DeepClone(), ["model"] = resolved["model"]!.DeepClone(), ["reasoningEffort"] = resolved["reasoningEffort"]!.DeepClone()
        };
        var prepared = new JsonObject
        {
            ["task"] = Protocol.ValidateTask(taskToSubmit, allowFollowUp: true), ["sourceOrigin"] = saved["sourceOrigin"]!.DeepClone()
        };
        store.WriteJson(intentPath, new JsonObject { ["fingerprint"] = fingerprint, ["submission"] = prepared.DeepClone() });
        return prepared;
    }

    private static void ValidateUnacceptedSource(Store store, string parentId, JsonObject task)
    {
        var current = store.ReadTask(parentId);
        var result = store.ReadResult(parentId);
        if (Protocol.IsActive(Text(store.ReadStatus(parentId), "state")) || result is null ||
            Text(task["planSource"] as JsonObject, "parentResultFingerprint") != Protocol.Fingerprint(result) ||
            Text(current["task"] as JsonObject, "repository") != Text(task, "repository") ||
            !JsonNode.DeepEquals(current["task"]?["target"], task["target"]))
            throw new ProtocolException("PLAN_CHANGED", "The source result changed before the new task was accepted.", "Refresh the result before confirming another request.");
        if (Text(task["planSource"] as JsonObject, "sourceKind") == "local-candidate")
        {
            var candidate = CandidateSnapshots.Describe(store, parentId);
            if (candidate is null || Text(candidate, "snapshotHash") != Text(task["planSource"] as JsonObject, "candidateSnapshotHash") ||
                Text(candidate, "baseSha") != Text(task["planSource"] as JsonObject, "revisionSha"))
                throw new ProtocolException("CANDIDATE_SNAPSHOT_INVALID", "The frozen candidate snapshot is no longer available unchanged.", "Preserve the source worktree and inspect the candidate snapshot; this request will not fall back to an older source revision.");
        }
    }

    private static string? Limit(string? text, int length) => text is not null && text.Length > length ? text[..length] + "…" : text;
    private static string? Text(JsonObject? value, string key) => value?[key] is JsonValue field && field.TryGetValue<string>(out var text) ? text : null;
}
