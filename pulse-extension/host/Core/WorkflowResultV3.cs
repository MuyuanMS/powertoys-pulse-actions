using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

public static partial class WorkflowResult
{
    public const int MaximumPlanBytes = 24 * 1024;
    private static readonly string[] V3Fields = ["schemaVersion", "outcome", "phase", "summary", "assessment", "reviewConclusion", "verificationEvidence",
        "report", "findings", "artifacts", "validation", "diagnostics", "nextActions", "review", "needsReview", "e2eAssessment", "featureAssessment", "bugAssessment", "plans"];
    private static readonly string[] PlanKinds = ["feature-implement", "issue-fix", "reproduction-setup", "issue-verify"];
    private static readonly string[] Priorities = ["P0", "P1", "P2", "P3"];
    private static readonly string[] ManualPrKinds = ["approve", "suggestChanges", "requestChanges", "comment", "close", "merge-pr", "trigger-ci"];

    /// <summary>The v3 model contract. Array transport is bounded by bytes, never by the first N findings.</summary>
    public static JsonObject SchemaV3()
    {
        var schema = Schema(); var properties = schema["properties"]!.AsObject();
        properties["schemaVersion"] = new JsonObject { ["type"] = "integer", ["const"] = 3 };
        properties.Remove("verificationRecommendation");
        properties["report"] = ObjectSchema(new JsonObject { ["complete"] = BooleanSchema(), ["rechecked"] = BooleanSchema(),
            ["coverage"] = TextListSchema(), ["limitations"] = TextListSchema() });
        properties["findings"] = UnboundedArraySchema(FindingV3Schema());
        properties["findings"]!["description"] = "Return every final finding; never select a Top N or stop after discovering a high-priority issue. A proved defect remains reportable when part of its root cause is unknown: state that uncertainty in rootCause and preserve the known evidence.";
        foreach (var field in new[] { "artifacts", "validation", "diagnostics", "verificationEvidence" }) properties[field]!.AsObject().Remove("maxItems");
        properties["validation"]!["items"]!["properties"]!["evidence"] = TextListSchema();
        var review = properties["review"]!["anyOf"]!.AsArray().OfType<JsonObject>().Single(row => Text(row, "type") == "object");
        review["properties"]!["suggestions"]!.AsObject().Remove("maxItems");
        properties["e2eAssessment"] = NullableSchema(E2eAssessmentSchema());
        properties["featureAssessment"] = NullableSchema(FeatureAssessmentSchema());
        properties["bugAssessment"] = NullableSchema(BugAssessmentSchema());
        var plan = ObjectSchema(new JsonObject { ["id"] = IdentifierSchema(), ["kind"] = EnumSchema(PlanKinds), ["summary"] = TextSchema(8192, true),
            ["steps"] = TextListSchema(true), ["acceptanceCriteria"] = TextListSchema(true), ["prerequisites"] = TextListSchema(), ["evidence"] = TextListSchema(true) });
        plan["description"] = "Each complete plan must fit within 24576 bytes of compact UTF-8 JSON including escaping (non-ASCII counts as its escaped form), so it can be copied unchanged into the follow-up task. Plans are instructions for the next agent, not commands executed by the extension.";
        properties["plans"] = UnboundedArraySchema(plan);
        var variants = properties["nextActions"]!["items"]!["anyOf"]!.AsArray();
        foreach (var variant in variants.OfType<JsonObject>())
        {
            variant["properties"]!["recommended"] = BooleanSchema(); variant["required"]!.AsArray().Add("recommended");
        }
        variants.Add(ObjectSchema(new JsonObject { ["kind"] = EnumSchema(["start-task"]), ["reason"] = TextSchema(4096, true), ["body"] = EnumSchema([""]),
            ["recommended"] = BooleanSchema(), ["taskKind"] = EnumSchema(PlanKinds), ["planId"] = IdentifierSchema() }));
        variants.Add(ObjectSchema(new JsonObject { ["kind"] = EnumSchema(["close-as-duplicate"]), ["reason"] = TextSchema(4096, true), ["body"] = TextSchema(32768, true),
            ["recommended"] = BooleanSchema(), ["duplicateOf"] = RelatedIssueSchema() }));
        properties["nextActions"]!.AsObject().Remove("maxItems");
        schema["required"] = new JsonArray(V3Fields.Select(field => (JsonNode?)JsonValue.Create(field)).ToArray());
        schema["description"] = schema["description"]!.GetValue<string>() + " In v3, plan IDs are also unique. featureAssessment and bugAssessment cannot both be non-null. A confirmed bug assessment requires at least one confirmed finding. Finding feedback.suggestionId references review.suggestions. Every start-task planId references a plan whose kind equals taskKind; featureAssessment.planId references feature-implement, and bugAssessment.planId references one of the other plan kinds. close-as-duplicate requires a duplicate assessment and duplicateOf exactly equal to its relatedIssue. E2E scenarios and expectedResults have equal lengths and correspond by index. Plans and E2E assessments each fit within 24576 bytes of compact escaped UTF-8 JSON. Preserve these relationships without inventing evidence.";
        return schema;
    }

    public static bool IsValidStoredV3(JsonObject result) => IsVersion(result, 3) && ValidV3(CanonicalV3(result));

    /// <summary>Human PR choices are independent of report completeness and AI recommendations.</summary>
    public static bool CanPublishManualPr(JsonObject? result, JsonObject task, string kind) => IsPrManualAction(task, kind) && (kind != "approve" || !HasConfirmedP0(result, task));

    /// <summary>Only explicit, confirmed P0 findings on this original PR count. Legacy severities never imply P0.</summary>
    public static bool HasConfirmedP0(JsonObject? result, JsonObject? task)
    {
        if (result is null || task is null || !IsValidStoredV3(result) || Text(task["target"] as JsonObject, "type") != "pr" || !Sha(Text(task, "expectedHeadSha"))) return false;
        if (result["relatedConfirmedP0"] is JsonValue related && related.TryGetValue<bool>(out var confirmed) && confirmed) return true;
        var expected = Text(task, "expectedHeadSha");
        var assessment = result["assessment"] as JsonObject; var conclusion = result["reviewConclusion"] as JsonObject;
        var bound = Text(assessment, "subject") == "original-pr" && string.Equals(Text(assessment, "revisionSha"), expected, StringComparison.OrdinalIgnoreCase) ||
            Text(task, "actionKind") == "pr-review" && string.Equals(Text(conclusion, "revisionSha"), expected, StringComparison.OrdinalIgnoreCase);
        if (!bound) return false;
        return result["findings"]!.AsArray().OfType<JsonObject>().Any(row => Text(row, "priority") == "P0" && Text(row, "status") == "open" && row["confirmed"]!.GetValue<bool>());
    }

    public static bool IsFinalReportComplete(JsonObject? result) => result is not null && IsValidStoredV3(result) && Text(result, "outcome") == "completed" &&
        result["report"]?["complete"]?.GetValue<bool>() == true && result["report"]?["rechecked"]?.GetValue<bool>() == true;

    /// <summary>Derived advice, not an execution gate or another model-recommended-action field.</summary>
    public static JsonObject Recommendation(JsonObject? result, JsonObject? task)
    {
        if (!IsFinalReportComplete(result)) return Advice("incomplete", "The final analysis is incomplete or was not recorded; inspect the retained evidence and limitations.");
        if (Text(task?["target"] as JsonObject, "type") == "pr")
        {
            if (result!["relatedConfirmedP0"]?.GetValue<bool>() == true || result["relatedConfirmedP1"]?.GetValue<bool>() == true)
                return Advice("address-findings", "Compatible supplemental verification confirmed P0 or P1 findings. Inspect the linked original-revision evidence.");
            var findings = result!["findings"]!.AsArray().OfType<JsonObject>();
            if (findings.Any(row => row["confirmed"]!.GetValue<bool>() && Text(row, "status") == "open" && Text(row, "priority") is ("P0" or "P1")))
                return Advice("address-findings", "Confirmed P0 or P1 findings remain on this revision. Review and address the findings before following a positive recommendation.");
            if (result["e2eAssessment"] is not JsonObject e2e) return Advice("incomplete", "This report did not record the need for E2E verification; missing data does not mean it is unnecessary.");
            if (Text(e2e, "level") == "required" && result["e2eEvidenceComplete"]?.GetValue<bool>() != true) return Advice("run-e2e", "Required E2E evidence remains to be supplemented; see the recorded scenarios and prerequisites.");
            return Advice("approve", "The final review has no confirmed open P0 or P1 findings, and E2E is not required. Inspect the report before choosing a GitHub action.");
        }
        var decorated = WithProposalIds(result!);
        var proposal = decorated["nextActions"]!.AsArray().OfType<JsonObject>().FirstOrDefault(row => row["recommended"]?.GetValue<bool>() == true);
        if (proposal is null) return Advice("incomplete", "No default next action was proposed; inspect the final investigation and available actions.");
        var advice = Advice(Text(proposal, "kind"), Text(proposal, "reason")); advice["proposalId"] = proposal["proposalId"]!.DeepClone();
        if (proposal["planId"] is not null) advice["planId"] = proposal["planId"]!.DeepClone();
        return advice;
    }

    private static bool IsPrManualAction(JsonObject task, string kind) => Text(task["target"] as JsonObject, "type") == "pr" && ManualPrKinds.Contains(kind, StringComparer.Ordinal);
    private static JsonObject Advice(string kind, string reason) => new() { ["kind"] = kind, ["reason"] = reason };
    private static JsonObject AvailabilityV3(bool enabled, string reason) => new() { ["enabled"] = enabled, ["reasons"] = enabled ? new JsonArray() : new JsonArray(reason) };

    private static JsonObject CanonicalV3(JsonObject result)
    {
        var canonical = new JsonObject();
        foreach (var field in V3Fields) if (result.ContainsKey(field)) canonical[field] = result[field]?.DeepClone();
        if (canonical["nextActions"] is JsonArray actions)
            foreach (var action in actions.OfType<JsonObject>()) { action.Remove("proposalId"); action.Remove("availability"); }
        return canonical;
    }

    private static bool ValidV3(JsonObject result)
    {
        if (!Exact(result, V3Fields) || !IsVersion(result, 3) || !Enum(result, "outcome", Outcomes) || !Enum(result, "phase", Phases) || !String(result, "summary", 32768, true) || !Bool(result, "needsReview") ||
            result["assessment"] is not null && !ValidAssessment(result["assessment"]) || result["reviewConclusion"] is not null && !ValidReviewConclusion(result["reviewConclusion"]) ||
            !Array(result, "verificationEvidence", int.MaxValue, ValidVerificationEvidence) || !ValidReportV3(result["report"]) ||
            !Array(result, "findings", int.MaxValue, ValidFindingV3) || !Array(result, "artifacts", int.MaxValue, node => node is JsonObject row && Exact(row, "path", "label") && String(row, "path", 4096, true) && String(row, "label", 512, true)) ||
            !Array(result, "validation", int.MaxValue, node => node is JsonObject row && Exact(row, "id", "name", "status", "required", "details", "evidence") && Identifier(row, "id") && String(row, "name", 512, true) &&
                Enum(row, "status", ["passed", "failed", "not_run"]) && Bool(row, "required") && String(row, "details", 4096) && TextList(row, "evidence")) ||
            !Array(result, "diagnostics", int.MaxValue, node => node is JsonObject row && Exact(row, "code", "severity", "message", "recovery") && String(row, "code", 64, true) &&
                Regex.IsMatch(Text(row, "code"), @"\A[A-Z][A-Z0-9_]{0,63}\z") && Enum(row, "severity", ["warning", "error"]) && String(row, "message", 8192, true) && Enum(row, "recovery", Recoveries)) ||
            !Array(result, "plans", int.MaxValue, ValidPlanV3) || !Array(result, "nextActions", int.MaxValue, ValidNextActionV3) || result["review"] is not null && !ValidReviewV3(result["review"]) ||
            result["e2eAssessment"] is not null && !ValidE2eAssessmentV3(result["e2eAssessment"]) || result["featureAssessment"] is not null && !ValidFeatureV3(result["featureAssessment"]) ||
            result["bugAssessment"] is not null && !ValidBugV3(result["bugAssessment"])) return false;
        foreach (var field in new[] { "findings", "validation", "verificationEvidence", "plans" }) if (!UniqueIds(result[field]!.AsArray())) return false;
        if (result["featureAssessment"] is not null && result["bugAssessment"] is not null) return false;
        var plans = result["plans"]!.AsArray().OfType<JsonObject>().ToDictionary(row => Text(row, "id"), StringComparer.Ordinal);
        var suggestions = (result["review"]?["suggestions"] as JsonArray)?.OfType<JsonObject>().Select(row => Text(row, "id")).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var finding in result["findings"]!.AsArray().OfType<JsonObject>())
            if (finding["feedback"]?["suggestionId"] is JsonValue suggestion && !suggestions.Contains(suggestion.GetValue<string>())) return false;
        foreach (var action in result["nextActions"]!.AsArray().OfType<JsonObject>())
        {
            if (Text(action, "kind") == "start-task" && (!plans.TryGetValue(Text(action, "planId"), out var plan) || Text(plan, "kind") != Text(action, "taskKind"))) return false;
            if (action["suggestionIds"] is JsonArray ids && ids.Any(id => !suggestions.Contains(id!.GetValue<string>()))) return false;
            if (Text(action, "kind") == "close-as-duplicate")
            {
                var investigation = result["featureAssessment"] as JsonObject ?? result["bugAssessment"] as JsonObject;
                if (investigation is null || Text(investigation, "status") != "duplicate" || !JsonNode.DeepEquals(investigation["relatedIssue"], action["duplicateOf"])) return false;
            }
        }
        if (result["featureAssessment"] is JsonObject feature && feature["planId"] is not null && (!plans.TryGetValue(Text(feature, "planId"), out var featurePlan) || Text(featurePlan, "kind") != "feature-implement")) return false;
        if (result["bugAssessment"] is JsonObject bug)
        {
            if (bug["planId"] is not null && (!plans.TryGetValue(Text(bug, "planId"), out var bugPlan) || Text(bugPlan, "kind") == "feature-implement")) return false;
            if (Text(bug, "status") == "confirmed" && !result["findings"]!.AsArray().OfType<JsonObject>().Any(row => row["confirmed"]!.GetValue<bool>())) return false;
        }
        return true;
    }

    private static bool ValidReportV3(JsonNode? value) => value is JsonObject row && Exact(row, "complete", "rechecked", "coverage", "limitations") && Bool(row, "complete") && Bool(row, "rechecked") && TextList(row, "coverage") && TextList(row, "limitations");
    private static bool ValidFindingV3(JsonNode? value) => value is JsonObject row && Exact(row, "id", "title", "priority", "status", "confirmed", "path", "line", "details", "impact", "trigger", "rootCause", "fixSuggestion", "evidence", "feedback") &&
        Identifier(row, "id") && String(row, "title", 512, true) && Enum(row, "priority", Priorities) && Enum(row, "status", ["open", "fixed", "unverified"]) && Bool(row, "confirmed") &&
        !(row["confirmed"]!.GetValue<bool>() && Text(row, "status") == "unverified") && String(row, "path", 4096) && (row["line"] is null || PositiveInteger(row["line"])) &&
        String(row, "details", 8192, true) && String(row, "impact", 4096, true) && String(row, "trigger", 4096, true) && String(row, "rootCause", 8192, true) && String(row, "fixSuggestion", 8192, true) &&
        TextList(row, "evidence") && (!row["confirmed"]!.GetValue<bool>() || row["evidence"]!.AsArray().Count > 0) && row["feedback"] is JsonObject feedback && Exact(feedback, "body", "suggestionId") &&
        String(feedback, "body", 32768) && (feedback["suggestionId"] is null || Identifier(feedback, "suggestionId"));
    private static bool ValidPlanV3(JsonNode? value) => value is JsonObject row && EncodedBytes(row) <= MaximumPlanBytes && Exact(row, "id", "kind", "summary", "steps", "acceptanceCriteria", "prerequisites", "evidence") &&
        Identifier(row, "id") && Enum(row, "kind", PlanKinds) && String(row, "summary", 8192, true) && TextList(row, "steps", true) && TextList(row, "acceptanceCriteria", true) && TextList(row, "prerequisites") && TextList(row, "evidence", true);

    private static bool ValidE2eAssessmentV3(JsonNode? value) => value is JsonObject row && EncodedBytes(row) <= MaximumVerificationRecommendationBytes &&
        Exact(row, "level", "reason", "question", "scenarios", "expectedResults", "prerequisites", "evidence", "readiness") && Enum(row, "level", ["not_needed", "recommended", "required"]) &&
        String(row, "reason", 4096, true) && String(row, "question", 4096, Text(row, "level") != "not_needed") && TextList(row, "scenarios", Text(row, "level") != "not_needed") && TextList(row, "expectedResults") &&
        row["scenarios"]!.AsArray().Count == row["expectedResults"]!.AsArray().Count && TextList(row, "prerequisites") && TextList(row, "evidence", true) &&
        Enum(row, "readiness", ["ready", "missing-prerequisites", "unknown"]) && (Text(row, "readiness") != "missing-prerequisites" || row["prerequisites"]!.AsArray().Count > 0);

    private static bool ValidFeatureV3(JsonNode? value) => value is JsonObject row && Exact(row, "status", "summary", "reasons", "evidence", "acceptanceCriteria", "questions", "alternatives", "relatedIssue", "planId") &&
        Enum(row, "status", ["ready", "needs_information", "needs_decision", "already_supported", "duplicate", "not_feasible"]) && String(row, "summary", 8192, true) && TextList(row, "reasons", true) && TextList(row, "evidence", true) &&
        TextList(row, "acceptanceCriteria", Text(row, "status") == "ready") && TextList(row, "questions", Text(row, "status") == "needs_information") && TextList(row, "alternatives", Text(row, "status") == "needs_decision") &&
        (row["relatedIssue"] is null ? Text(row, "status") != "duplicate" : ValidRelatedIssueV3(row["relatedIssue"])) && (row["planId"] is null ? Text(row, "status") != "ready" : Identifier(row, "planId"));

    private static bool ValidBugV3(JsonNode? value) => value is JsonObject row && Exact(row, "status", "summary", "reasons", "evidence", "questions", "relatedIssue", "planId", "reproduction") &&
        Enum(row, "status", ["confirmed", "needs_information", "needs_verification", "already_fixed", "duplicate", "not_a_bug"]) && String(row, "summary", 8192, true) && TextList(row, "reasons", true) && TextList(row, "evidence", true) &&
        TextList(row, "questions", Text(row, "status") == "needs_information") && (row["relatedIssue"] is null ? Text(row, "status") != "duplicate" : ValidRelatedIssueV3(row["relatedIssue"])) &&
        (row["planId"] is null ? Text(row, "status") != "needs_verification" : Identifier(row, "planId")) && row["reproduction"] is JsonObject reproduction &&
        Exact(reproduction, "status", "revisionSha", "environment", "steps", "expected", "observed", "evidence") && Enum(reproduction, "status", ["reproduced", "not_reproduced", "not_run", "blocked"]) &&
        (reproduction["revisionSha"] is null || Sha(Text(reproduction, "revisionSha"))) && String(reproduction, "environment", 8192) && TextList(reproduction, "steps") &&
        String(reproduction, "expected", 8192) && String(reproduction, "observed", 8192) && TextList(reproduction, "evidence", Text(reproduction, "status") is "reproduced" or "not_reproduced");

    private static bool ValidRelatedIssueV3(JsonNode? value)
    {
        if (value is not JsonObject row || !Exact(row, "repository", "number", "url") || !String(row, "repository", 256, true) || !PositiveInteger(row["number"]) || !String(row, "url", 2048, true) ||
            !Regex.IsMatch(Text(row, "repository"), @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z")) return false;
        return Uri.TryCreate(Text(row, "url"), UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            uri.AbsolutePath.Equals("/" + Text(row, "repository") + "/issues/" + row["number"]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidNextActionV3(JsonNode? value)
    {
        if (value is not JsonObject row || !Bool(row, "recommended") || !String(row, "reason", 4096, true) || !String(row, "body", 32768)) return false;
        if (Text(row, "kind") == "start-task") return Exact(row, "kind", "reason", "body", "recommended", "taskKind", "planId") && Text(row, "body").Length == 0 && Enum(row, "taskKind", PlanKinds) && Identifier(row, "planId");
        if (Text(row, "kind") == "close-as-duplicate") return Exact(row, "kind", "reason", "body", "recommended", "duplicateOf") && String(row, "body", 32768, true) && ValidRelatedIssueV3(row["duplicateOf"]);
        var legacyShape = row.DeepClone().AsObject(); legacyShape.Remove("recommended");
        return ValidNextAction(legacyShape);
    }

    private static bool ValidReviewV3(JsonNode? value) => value is JsonObject review && Exact(review, "headSha", "body", "suggestions") && Sha(Text(review, "headSha")) && String(review, "body", 32768) &&
        Array(review, "suggestions", int.MaxValue, node => node is JsonObject row && Exact(row, "id", "path", "line", "startLine", "side", "body", "replacement") && Identifier(row, "id") && String(row, "path", 1024, true) &&
            PositiveInteger(row["line"]) && PositiveInteger(row["startLine"]) && row["startLine"]!.GetValue<int>() <= row["line"]!.GetValue<int>() && (long)row["line"]!.GetValue<int>() - row["startLine"]!.GetValue<int>() <= 999 &&
            Text(row, "side") == "RIGHT" && String(row, "body", 8192) && String(row, "replacement", 32768)) && UniqueIds(review["suggestions"]!.AsArray());

    private static JsonObject NormalizeV3(JsonObject input, JsonObject? task, string state, int? exitCode, string? code, string? message, bool outputComplete, string original)
    {
        var result = input.DeepClone().AsObject(); AddRequiredChecks(result, task); ValidateAcceptedScope(result, task);
        var processOutcome = ProcessOutcome(state, exitCode, code);
        if (processOutcome is not null) { result["outcome"] = processOutcome; AddDiagnostic(result, SafeCode(code, processOutcome.ToUpperInvariant()), message ?? "CLI execution ended before the task completed.", RecoveryFor(code)); }
        if (!outputComplete) AddDiagnostic(result, "OUTPUT_INCOMPLETE", "The complete final report could not be retained or interpreted. Existing evidence remains available.", "inspectResult");
        var checksComplete = ChecksComplete(result["validation"]!.AsArray(), task);
        if (!checksComplete) AddDiagnostic(result, "WORKFLOW_CHECKS_INCOMPLETE", "Selected workflow checks are incomplete; inspect the final coverage and remaining work.", "inspectResult");
        var report = result["report"]!.AsObject();
        var claimedComplete = report["complete"]!.GetValue<bool>();
        if (Text(task, "actionKind") == "pr-review" && Text(result["assessment"] as JsonObject, "subject") == "local-candidate" &&
            Sha(Text(task, "expectedHeadSha")) && string.Equals(Text(result["reviewConclusion"] as JsonObject, "revisionSha"), Text(task, "expectedHeadSha"), StringComparison.OrdinalIgnoreCase))
        {
            var corrected = new JsonArray();
            foreach (var finding in result["findings"]!.AsArray().OfType<JsonObject>().Where(row => row["confirmed"]!.GetValue<bool>() && Text(row, "status") == "fixed"))
            {
                corrected.Add(new JsonObject { ["id"] = finding["id"]!.DeepClone(), ["reportedStatus"] = "fixed", ["originalPrStatus"] = "open" });
                finding["status"] = "open";
            }
            if (corrected.Count > 0)
            {
                result["sourceStatusCorrections"] = corrected;
                report["complete"] = false;
                AddDiagnostic(result, "SOURCE_STATUS_MISMATCH", "Confirmed findings belong to the original reviewed PR, while the claimed fix was assessed only on a local candidate. Their original-PR status remains open. Reported statuses are retained in sourceStatusCorrections and the original final JSON remains in the execution log.", "inspectResult");
            }
        }
        if (claimedComplete && Text(task, "actionKind") is ("pr-review" or "bug-investigation") &&
            result["findings"]!.AsArray().OfType<JsonObject>().Any(row => !row["confirmed"]!.GetValue<bool>() || Text(row, "status") == "unverified"))
        {
            report["complete"] = false; report["rechecked"] = false;
            AddDiagnostic(result, "FINAL_FINDINGS_NOT_RECHECKED", "The report still contains unconfirmed candidate findings. The retained list is incomplete; unresolved hypotheses belong in limitations or verification plans until rechecked.", "inspectResult");
        }
        if (report["complete"]!.GetValue<bool>() && (!report["rechecked"]!.GetValue<bool>() || report["coverage"]!.AsArray().Count == 0))
            AddDiagnostic(result, "FINAL_REPORT_INCOMPLETE", "The report lacks completed rechecking or recorded coverage.", "inspectResult");
        if (Text(task, "actionKind") == "pr-review" && result["e2eAssessment"] is null)
            AddDiagnostic(result, "E2E_ASSESSMENT_MISSING", "The review did not record whether E2E is unnecessary, recommended or required.", "inspectResult");
        if (Text(task, "actionKind") is ("feature-investigate" or "feature-research") && result["featureAssessment"] is null)
            AddDiagnostic(result, "FEATURE_ASSESSMENT_MISSING", "The feature investigation did not provide its combined conclusion and supporting plan or questions.", "inspectResult");
        if (Text(task, "actionKind") is ("bug-investigate" or "bug-investigation") && result["bugAssessment"] is null)
            AddDiagnostic(result, "BUG_ASSESSMENT_MISSING", "The bug investigation did not provide its conclusion and reproduction evidence.", "inspectResult");
        var errors = result["diagnostics"]!.AsArray().OfType<JsonObject>().Any(row => Text(row, "severity") == "error");
        if (Text(result, "outcome") == "completed" && (errors || !outputComplete || !checksComplete || !report["complete"]!.GetValue<bool>() || !report["rechecked"]!.GetValue<bool>())) result["outcome"] = "blocked";
        if (Text(result, "outcome") != "completed") report["complete"] = false;
        if (Text(result, "outcome") != "completed" || result["findings"]!.AsArray().OfType<JsonObject>().Any(row => Text(row, "status") is "open" or "unverified")) result["needsReview"] = true;
        FinishV3(result, exitCode);
        return EncodedBytes(result) <= ResultLimits.MaximumV3NormalizedResultBytes ? result : InvalidResultV3(original, task, state, exitCode, code, message, outputComplete,
            [new("$", "The normalized report exceeds the " + ResultLimits.MaximumV3NormalizedResultBytes + " byte retention limit.")]);
    }

    private static JsonObject FailureV3(string state, string? code, string? message, JsonObject? task, int? exitCode, string phase)
    {
        var result = Empty(ProcessOutcome(state, exitCode, code) ?? "blocked", Phases.Contains(phase, StringComparer.Ordinal) ? phase : "setup", Limit(message ?? "The workflow did not complete.", 32768));
        result["schemaVersion"] = 3; result.Remove("verificationRecommendation"); result["e2eAssessment"] = null; result["featureAssessment"] = null; result["bugAssessment"] = null; result["plans"] = new JsonArray();
        result["report"] = new JsonObject { ["complete"] = false, ["rechecked"] = false, ["coverage"] = new JsonArray(), ["limitations"] = new JsonArray(Limit(message ?? "The workflow did not complete.", 4096)) };
        result["nextActions"] = new JsonArray(); AddRequiredChecks(result, task); AddDiagnostic(result, SafeCode(code, "WORKFLOW_INCOMPLETE"), message ?? "The workflow did not complete.", RecoveryFor(code));
        FinishV3(result, exitCode); return result;
    }

    private static JsonObject InvalidResultV3(string original, JsonObject? task, string state, int? exitCode, string? code, string? message, bool outputComplete, IReadOnlyList<ResultValidationIssue>? validationIssues = null)
    {
        var result = FailureV3(state, code ?? "INVALID_RESULT", message ?? "The final response did not match the version 3 result contract.", task, exitCode, "reporting");
        if (ProcessOutcome(state, exitCode, code) is null) result["outcome"] = "blocked";
        AddDiagnostic(result, "INVALID_RESULT", "The final result did not match the version 3 report contract; partial output is not a completed report.", "inspectResult");
        AddValidationDiagnostics(result, validationIssues);
        if (!outputComplete) AddDiagnostic(result, "OUTPUT_INCOMPLETE", "Some output is incomplete. The final report has not been established.", "inspectResult");
        result["rawOutput"] = Limit(OutputRedactor.Redact(original), ResultLimits.RawDiagnosticCharacters); FinishV3(result, exitCode); return result;
    }

    private static void FinishV3(JsonObject result, int? exitCode)
    {
        result["structured"] = true; result["cliExitCode"] = exitCode; AssignProposalIds(result["nextActions"]!.AsArray());
        result["blockers"] = new JsonArray(result["diagnostics"]!.AsArray().OfType<JsonObject>().Where(row => Text(row, "severity") == "error").Select(row => (JsonNode?)JsonValue.Create(Text(row, "message"))).ToArray());
        result["nextSteps"] = result["nextActions"]!.DeepClone();
    }

    private static bool CanPublishV3(JsonObject result, JsonObject task, string kind)
    {
        if (IsPrManualAction(task, kind)) return CanPublishManualPr(result, task, kind);
        if (!IsValidStoredV3(result) || !ValidTarget(task)) return false;
        if (kind is "comment" or "close") return true;
        if (kind == "close-as-duplicate") return Text(task["target"] as JsonObject, "type") == "issue" && result["nextActions"]!.AsArray().OfType<JsonObject>().Any(row => Text(row, "kind") == kind);
        if (kind != "create-pr" || Text(task["target"] as JsonObject, "type") != "issue" || !IsFinalReportComplete(result)) return false;
        var assessment = result["assessment"] as JsonObject;
        return Text(assessment, "subject") == "local-candidate" && Text(assessment, "status") == "passed" && result["nextActions"]!.AsArray().OfType<JsonObject>().Any(row =>
            Text(row, "kind") == "create-pr" && Sha(Text(row["pullRequest"] as JsonObject, "sourceHeadSha")) && string.Equals(Text(row["pullRequest"] as JsonObject, "sourceHeadSha"), Text(assessment, "revisionSha"), StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject ProposalAvailabilityV3(JsonObject result, JsonObject task, JsonObject status, JsonObject action)
    {
        var kind = Text(action, "kind");
        if (IsPrManualAction(task, kind)) return AvailabilityV3(CanPublishManualPr(result, task, kind), "Resolve the confirmed, still-open P0 on this original PR revision before approving.");
        if (kind == "none") return AvailabilityV3(false, "No action was proposed.");
        if (kind is "start-task" or "close-as-duplicate") return AvailabilityV3(IsValidStoredV3(result) && Text(task["target"] as JsonObject, "type") == "issue", "A valid issue report and saved plan or duplicate target are required.");
        if (PublicationActions.Contains(kind, StringComparer.Ordinal)) return AvailabilityV3(CanPublishV3(result, task, kind), "This action lacks the required fixed target or verified publication source.");
        return AvailabilityV3(Actions.Contains(kind, StringComparer.Ordinal), "This action kind is not supported.");
    }

    private static bool TextList(JsonObject row, string field, bool nonempty = false) => Array(row, field, int.MaxValue, node => node is JsonValue value && value.TryGetValue<string>(out var text) &&
        !string.IsNullOrWhiteSpace(text) && text.Length <= 4096 && !text.Contains('\0')) && (!nonempty || row[field]!.AsArray().Count > 0);
    private static int EncodedBytes(JsonNode value) => Encoding.UTF8.GetByteCount(value.ToJsonString(Protocol.JsonOptions));
    private static JsonObject BooleanSchema() => new() { ["type"] = "boolean" };
    private static JsonObject UnboundedArraySchema(JsonObject items) => new() { ["type"] = "array", ["items"] = items };
    private static JsonObject TextListSchema(bool nonempty = false)
    {
        var schema = UnboundedArraySchema(TextSchema(4096, true));
        if (nonempty) schema["minItems"] = 1;
        return schema;
    }
    private static JsonObject RelatedIssueSchema()
    {
        var schema = ObjectSchema(new JsonObject { ["repository"] = TextSchema(256, true, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$"), ["number"] = PositiveIntegerSchema(),
            ["url"] = TextSchema(2048, true, @"^[hH][tT][tT][pP][sS]://[gG][iI][tT][hH][uU][bB]\.[cC][oO][mM](:443)?/[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}/[iI][sS][sS][uU][eE][sS]/[1-9][0-9]*$") });
        schema["description"] = "The HTTPS GitHub Issue URL must match this repository and decimal issue number exactly (case-insensitive), without credentials, query, fragment or trailing slash. Only the default HTTPS port is allowed.";
        return schema;
    }
    private static JsonObject E2eAssessmentSchema()
    {
        var schema = AnyOfSchema(from needed in new[] { false, true } from missing in new[] { false, true }
            select ObjectSchema(new JsonObject { ["level"] = EnumSchema(needed ? ["recommended", "required"] : ["not_needed"]), ["reason"] = TextSchema(4096, true), ["question"] = TextSchema(4096, needed),
                ["scenarios"] = TextListSchema(needed), ["expectedResults"] = TextListSchema(needed), ["prerequisites"] = TextListSchema(missing), ["evidence"] = TextListSchema(true), ["readiness"] = EnumSchema(missing ? ["missing-prerequisites"] : ["ready", "unknown"]) }));
        schema["description"] = "Return an explicit level, including not_needed with supporting evidence. Scenarios and expectedResults correspond by index. Required/recommended levels need at least one scenario. The entire assessment must fit within 24576 bytes of compact UTF-8 JSON including escaping, so follow-up context is complete.";
        return schema;
    }
}
