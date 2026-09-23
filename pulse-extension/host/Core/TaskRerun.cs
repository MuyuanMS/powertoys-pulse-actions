using System.Text.Json.Nodes;

namespace Pulse.Host;

/// <summary>Creates a new submission from the immutable saved task without starting work or changing its target.</summary>
public static class TaskRerun
{
    public static JsonObject Prepare(Store store, JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "runId", "requestId", "execution", "expectedHeadSha", "reviewOptions");
        var runId = Protocol.RunId(Protocol.RequiredString(payload, "runId"));
        var saved = store.ReadTask(runId);
        var task = (JsonObject)saved["task"]!.DeepClone();
        var requestId = Protocol.RequiredString(payload, "requestId", 128);
        if (requestId == task["requestId"]?.GetValue<string>())
            throw new ProtocolException("INVALID_REQUEST", "A rerun requires a new requestId.", "Create a new rerun request. Retrying an unconfirmed rerun must reuse that rerun's original requestId and unchanged options.");
        task["requestId"] = requestId;

        // Omitted is the legacy behavior. Explicit null means discard previous task
        // overrides and let normal acceptance resolve the user's current defaults.
        if (payload.ContainsKey("execution"))
        {
            if (payload["execution"] is null) task.Remove("execution");
            else task["execution"] = ExecutionOptions.ValidateOverride(payload["execution"]);
        }

        // Only an explicitly displayed and confirmed revision may replace the old SHA.
        // Never fetch or silently upgrade HEAD here; submission still verifies this SHA.
        if (payload.ContainsKey("expectedHeadSha"))
            task["expectedHeadSha"] = Protocol.RequiredString(payload, "expectedHeadSha", 40);

        if (payload.ContainsKey("reviewOptions"))
            task["reviewOptions"] = Protocol.RequireObject(payload["reviewOptions"]).DeepClone();

        // Supplemental retries keep the exact recommendation and original revision.
        var followUp = task["actionKind"]?.GetValue<string>() == "pr-verify";
        if (followUp && (!JsonNode.DeepEquals(task["reviewOptions"], saved["task"]?["reviewOptions"]) ||
            !string.Equals(task["expectedHeadSha"]?.GetValue<string>(), saved["task"]?["expectedHeadSha"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)))
            throw new ProtocolException("INVALID_REQUEST", "A verification retry must retain its saved scope and original PR revision.", "Open the parent review to start a different verification.");

        return new JsonObject
        {
            ["task"] = Protocol.ValidateTask(task, allowFollowUp: followUp || task["planSource"] is JsonObject),
            ["sourceOrigin"] = saved["sourceOrigin"]!.DeepClone()
        };
    }
}
