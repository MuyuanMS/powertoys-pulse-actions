using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>Human follow-up decisions derived from saved review evidence, never model approval proposals.</summary>
public static class ReviewDecisions
{
    private const int MaximumConfirmationBytes = 48 * 1024;

    public static JsonObject? Summary(JsonObject task, JsonObject? result, JsonObject status)
    {
        if (Text(task, "actionKind") != "pr-review" || Text(task["target"] as JsonObject, "type") != "pr" ||
            result is null || !WorkflowResult.IsValidStoredV2(result)) return null;
        var expected = Text(task, "expectedHeadSha").ToLowerInvariant();
        var assessment = result["assessment"] as JsonObject;
        var diagnostics = result["diagnostics"]!.AsArray().OfType<JsonObject>().ToArray();
        var checks = result["validation"]!.AsArray().OfType<JsonObject>().ToArray();
        var reasons = new List<string>();
        if (Text(status, "state") != "succeeded" || !Zero(status["exitCode"]) || !Zero(result["cliExitCode"]))
            reasons.Add("The CLI must have finished successfully with a recorded exit code of zero.");
        if (!Bool(result, "structured") || diagnostics.Any(row => Text(row, "code") is "INVALID_RESULT" or "OUTPUT_INCOMPLETE"))
            reasons.Add("The final structured review is invalid or incomplete. Inspect its execution logs.");
        if (!Regex.IsMatch(expected, @"\A[a-f0-9]{40}\z") ||
            !((Text(assessment, "subject") == "original-pr" && SameSha(Text(assessment, "revisionSha"), expected)) ||
                result["review"] is JsonObject review && SameSha(Text(review, "headSha"), expected)) ||
            assessment is not null && (Text(assessment, "subject") != "original-pr" || !SameSha(Text(assessment, "revisionSha"), expected)) ||
            result["review"] is JsonObject suppliedReview && !SameSha(Text(suppliedReview, "headSha"), expected))
            reasons.Add("The saved review evidence is not bound to this task's original pull request SHA.");
        foreach (var id in new[] { "context", "local-review" })
            if (checks.Count(row => Text(row, "id") == id && PassedWithEvidence(row)) != 1)
                reasons.Add($"The {id} review check must be completed with supporting evidence.");

        var complete = reasons.Count == 0;
        var all = Limitations(result);
        var concreteGapCount = all.OfType<JsonObject>().Count(row => Text(row, "category") == "validation-gap");
        var gapCount = concreteGapCount > 0 ? concreteGapCount : all.OfType<JsonObject>().Any(row => Text(row, "category") == "assessment") ? 1 : 0;
        var observationCount = all.OfType<JsonObject>().Count(row => Text(row, "category") == "recorded-observation");
        var visible = new JsonArray();
        var bytes = 0;
        foreach (var entry in all)
        {
            var entryBytes = Encoding.UTF8.GetByteCount(entry!.ToJsonString(Protocol.JsonOptions));
            if (bytes + entryBytes > MaximumConfirmationBytes) break;
            visible.Add(entry.DeepClone());
            bytes += entryBytes;
        }
        var truncated = visible.Count != all.Count;
        if (truncated) reasons.Add("The review evidence exceeds the confirmation size limit. Inspect the full saved report before preparing a separate GitHub review.");
        var requestAllowed = complete && !truncated && gapCount > 0 && Text(result, "outcome") is "completed" or "blocked" or "failed";
        var approvalReasons = new List<string>(reasons);
        if (Text(result, "outcome") is not ("completed" or "blocked")) approvalReasons.Add("A failed, cancelled or interrupted workflow cannot be approved with limitations.");
        if (Text(assessment, "subject") != "original-pr" || Text(assessment, "status") != "inconclusive" || !SameSha(Text(assessment, "revisionSha"), expected))
            approvalReasons.Add("Approval with limitations requires an inconclusive assessment of the original pull request at its saved SHA.");
        if (result["findings"]!.AsArray().OfType<JsonObject>().Any(row => (Text(row, "severity") is "high" or "medium") && (Text(row, "status") is "open" or "unverified")))
            approvalReasons.Add("Resolve high or medium severity findings on the reviewed revision before approval.");
        if (checks.Any(row => Bool(row, "required") && Text(row, "status") == "failed"))
            approvalReasons.Add("A failed required check is not an unverified limitation and cannot be accepted through this decision.");
        if (gapCount == 0) approvalReasons.Add("No unverified validation limitation was recorded.");
        return new JsonObject
        {
            ["codeReview"] = complete ? "completed" : "incomplete", ["verification"] = gapCount > 0 ? "limited" : "complete",
            ["headSha"] = expected, ["assessmentStatus"] = Text(assessment, "status"), ["limitations"] = visible,
            ["limitationCount"] = gapCount, ["observationCount"] = observationCount, ["limitsTruncated"] = truncated,
            ["canRequestEvidence"] = requestAllowed, ["canApproveWithLimitations"] = approvalReasons.Count == 0,
            ["reasons"] = Strings(reasons), ["approvalReasons"] = Strings(approvalReasons)
        };
    }

    public static JsonArray Decisions(JsonObject task, JsonObject? result, JsonObject status)
    {
        var summary = Summary(task, result, status);
        if (summary is null || summary["limitationCount"]!.GetValue<int>() == 0) return [];
        var limits = summary["limitations"]!.AsArray();
        var expected = Text(summary, "headSha");
        var reference = new JsonObject { ["repository"] = task["repository"]?.DeepClone(), ["target"] = task["target"]?.DeepClone(),
            ["headSha"] = expected, ["result"] = result!.DeepClone() };
        var fingerprint = Protocol.Fingerprint(reference)[..32];
        var verification = Describe(limits, observations: false);
        var observations = Describe(limits, observations: true);
        var disclosure = "### Validation limitations accepted by the reviewer\n\n" +
            $"This approval applies to commit `{expected}`. Local code review is complete, but the original pull request's product behavior remains inconclusive. " +
            "The reviewer explicitly accepts the following verification limitations; this approval does not claim that these checks passed.\n\n" + verification +
            (observations.Length > 0 ? "\n\n### Recorded failed observations\n\nThese are retained observations, not additional claims that the issue remains unresolved.\n\n" + observations : "") +
            "\n\nThe saved workflow outcome and validation evidence are unchanged. Pulse does not perform a merge as part of this action; missing checks remain unverified.";
        var requestBody = $"The local code review of commit `{expected}` is complete. The following behavior or acceptance evidence is still unverified:\n\n" + verification +
            "\n\nCould you provide the environment and configuration, exact steps, expected and observed results, and relevant logs or recordings for these checks on this revision? " +
            "Please identify the tested commit if the branch changes. Successful builds or checks in other environments should be distinguished from evidence for the unverified behavior.";
        var approvalBody = "I have reviewed the source changes and am accepting the explicitly listed validation limitations for this revision.";
        var oversized = disclosure.Length > 40000 || requestBody.Length > 50000;
        var requestReasons = summary["reasons"]!.DeepClone().AsArray();
        var approvalReasons = summary["approvalReasons"]!.DeepClone().AsArray();
        if (oversized)
        {
            const string reason = "The full validation disclosure exceeds the GitHub confirmation size limit. Inspect the saved report and prepare a separate review.";
            requestReasons.Add(reason); approvalReasons.Add(reason);
            requestBody = ""; approvalBody = ""; disclosure = "";
        }
        return new JsonArray(
            Decision("request-evidence", "Ask the author for evidence", "Request evidence for the unverified behavior without repeating an unchanged local run.",
                requestBody, "", Bool(summary, "canRequestEvidence") && !oversized, requestReasons, false),
            Decision("approve-with-limitations", "Approve with validation limitations", "This sends an APPROVE review on GitHub with the listed limitations in its body. GitHub has no separate partial-approval state. The saved assessment remains inconclusive; Pulse does not merge as part of this action.",
                approvalBody, disclosure, Bool(summary, "canApproveWithLimitations") && !oversized, approvalReasons, true));

        JsonObject Decision(string kind, string title, string description, string body, string mandatoryDisclosure, bool enabled, JsonArray reasons, bool acknowledgement) => new()
        {
            ["id"] = "review-" + kind + "-" + fingerprint, ["kind"] = kind, ["title"] = title, ["description"] = description,
            ["body"] = body, ["disclosure"] = mandatoryDisclosure, ["limitations"] = limits.DeepClone(),
            ["enabled"] = enabled, ["reasons"] = reasons, ["requiresAcknowledgement"] = acknowledgement
        };
    }

    public static JsonObject Require(JsonObject task, JsonObject? result, JsonObject status, string id, string operationKind)
    {
        var decision = Decisions(task, result, status).OfType<JsonObject>().SingleOrDefault(row => Text(row, "id") == id);
        if (decision is null || OperationKind(Text(decision, "kind")) != operationKind)
            throw new ProtocolException("REVIEW_DECISION_NOT_FOUND", "This review decision does not match the saved result and revision.", "Refresh the review decision preview before confirming.");
        if (!Bool(decision, "enabled")) throw new ProtocolException("REVIEW_DECISION_UNAVAILABLE", "This review decision is unavailable for the saved evidence.",
            string.Join(" ", decision["reasons"]!.AsArray().Select(node => node!.GetValue<string>())));
        return decision;
    }

    public static string OperationKind(string kind) => kind switch { "request-evidence" => "comment", "approve-with-limitations" => "approve", _ => "" };

    private static JsonArray Limitations(JsonObject result)
    {
        var limitations = new JsonArray();
        foreach (var row in result["validation"]!.AsArray().OfType<JsonObject>())
        {
            if (PassedWithEvidence(row)) continue;
            var copy = row.DeepClone().AsObject();
            copy["category"] = !Bool(row, "required") && Text(row, "status") == "failed" ? "recorded-observation" : "validation-gap";
            limitations.Add(copy);
        }
        if (result["assessment"] is JsonObject assessment && Text(assessment, "status") == "inconclusive")
            limitations.Add(new JsonObject { ["id"] = "assessment", ["name"] = "Original pull request assessment", ["status"] = "inconclusive",
                ["required"] = false, ["details"] = Text(assessment, "summary"), ["evidence"] = new JsonArray(), ["category"] = "assessment" });
        return limitations;
    }

    private static string Describe(JsonArray limits, bool observations)
    {
        var rows = limits.OfType<JsonObject>().ToArray();
        var context = observations ? "" : string.Join("\n\n", rows.Where(row => Text(row, "category") == "assessment")
            .Select(row => "Assessment context: " + Text(row, "details")));
        var items = string.Join("\n\n", rows.Where(row => Text(row, "category") == (observations ? "recorded-observation" : "validation-gap"))
            .Select(row => $"- **{Text(row, "name")}** ({Text(row, "status")}{(Bool(row, "required") ? "; required" : "")}): {Text(row, "details")}"));
        return context + (context.Length > 0 && items.Length > 0 ? "\n\n" : "") + items;
    }

    private static bool PassedWithEvidence(JsonObject row) => Text(row, "status") == "passed" && !string.IsNullOrWhiteSpace(Text(row, "details")) &&
        row["evidence"] is JsonArray evidence && evidence.Count > 0 && evidence.All(node => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text));
    private static string Text(JsonObject? value, string field) => value?[field] is JsonValue node && node.TryGetValue<string>(out var text) ? text : "";
    private static bool Bool(JsonObject value, string field) => value[field] is JsonValue node && node.TryGetValue<bool>(out var boolean) && boolean;
    private static bool Zero(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) && number == 0;
    private static bool SameSha(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Distinct(StringComparer.Ordinal).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
