using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>Versioned workflow results. Process lifecycle and workflow completion are independent.</summary>
public static partial class WorkflowResult
{
    public const int MaximumVerificationRecommendationBytes = 24 * 1024;
    private static readonly string[] Outcomes = ["completed", "blocked", "failed", "cancelled", "interrupted"];
    private static readonly string[] Phases = ["setup", "analysis", "implementation", "validation", "reporting"];
    private static readonly string[] Actions = ["viewChanges", "inspectResult", "openTarget", "configure", "rerun", "approve", "suggestChanges", "requestChanges", "comment", "close", "create-pr", "merge-pr", "trigger-ci", "none"];
    private static readonly string[] Recoveries = ["configure", "rerun", "inspectResult", "openTarget", "none"];
    private static readonly string[] PublicationActions = ["approve", "suggestChanges", "requestChanges", "comment", "close", "create-pr", "merge-pr", "trigger-ci"];
    private static readonly string[] Fields = ["schemaVersion", "outcome", "phase", "summary", "findings", "artifacts", "validation", "diagnostics", "nextActions", "review", "needsReview"];
    private static readonly string[] OptionalFields = ["assessment", "reviewConclusion", "verificationEvidence", "verificationRecommendation"];

    public static IReadOnlyList<string> RequiredChecksForTask(JsonObject? task) => ReviewModes.RequiredChecks(task);

    public static IReadOnlyList<string> RequiredChecks(string? actionKind) => actionKind switch
    {
        "pr-review" => ["context", "local-review", "verification"],
        "issue-fix" => ["reproduction", "implementation", "verification"],
        "e2e" => ["setup", "e2e"],
        "reproduction-setup" => ["reproduction", "instructions"],
        _ => []
    };

    public static JsonObject Schema()
    {
        var evidence = EvidenceSchema();
        var review = ObjectSchema(new JsonObject
        {
            ["headSha"] = TextSchema(40, nonempty: true, pattern: "^[a-fA-F0-9]{40}$"),
            ["body"] = TextSchema(32768),
            ["suggestions"] = ArraySchema(ObjectSchema(new JsonObject
            {
                ["id"] = IdentifierSchema(),
                ["path"] = TextSchema(1024, true), ["line"] = PositiveIntegerSchema(), ["startLine"] = PositiveIntegerSchema(),
                ["side"] = EnumSchema(["RIGHT"]), ["body"] = TextSchema(8192), ["replacement"] = TextSchema(32768)
            }), 100)
        });
        var schema = ObjectSchema(new JsonObject
        {
            ["schemaVersion"] = new JsonObject { ["type"] = "integer", ["const"] = 2 },
            ["outcome"] = EnumSchema(Outcomes), ["phase"] = EnumSchema(Phases), ["summary"] = TextSchema(32768, true),
            ["assessment"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "null" }, ObjectSchema(new JsonObject
            {
                ["subject"] = EnumSchema(["original-pr", "local-candidate", "target"]), ["status"] = EnumSchema(["passed", "failed", "inconclusive"]),
                ["summary"] = TextSchema(4096, true), ["revisionSha"] = new JsonObject { ["anyOf"] = new JsonArray(TextSchema(40, true, "^[a-fA-F0-9]{40}$"), new JsonObject { ["type"] = "null" }) }
            })) },
            ["reviewConclusion"] = NullableSchema(ObjectSchema(new JsonObject
            {
                ["status"] = EnumSchema(["no-blocking-findings", "changes-requested", "inconclusive"]), ["summary"] = TextSchema(4096, true),
                ["revisionSha"] = NullableSchema(TextSchema(40, true, "^[a-fA-F0-9]{40}$")), ["blockingUncertainties"] = ArraySchema(TextSchema(4096, true), 50)
            })),
            ["verificationEvidence"] = ArraySchema(VerificationEvidenceSchema(), 100),
            ["verificationRecommendation"] = NullableSchema(VerificationRecommendationSchema()),
            ["findings"] = ArraySchema(ObjectSchema(new JsonObject
            {
                ["id"] = IdentifierSchema(), ["title"] = TextSchema(512, true), ["severity"] = EnumSchema(["high", "medium", "low"]),
                ["status"] = EnumSchema(["open", "fixed", "unverified"]), ["path"] = TextSchema(4096),
                ["line"] = new JsonObject { ["anyOf"] = new JsonArray(PositiveIntegerSchema(), new JsonObject { ["type"] = "null" }) },
                ["details"] = TextSchema(4096), ["evidence"] = evidence.DeepClone()
            }), 200),
            ["artifacts"] = ArraySchema(ObjectSchema(new JsonObject { ["path"] = TextSchema(4096, true), ["label"] = TextSchema(512, true) }), 200),
            ["validation"] = ArraySchema(ObjectSchema(new JsonObject
            {
                ["id"] = IdentifierSchema(), ["name"] = TextSchema(512, true), ["status"] = EnumSchema(["passed", "failed", "not_run"]),
                ["required"] = new JsonObject { ["type"] = "boolean" }, ["details"] = TextSchema(4096), ["evidence"] = evidence.DeepClone()
            }), 200),
            ["diagnostics"] = ArraySchema(ObjectSchema(new JsonObject
            {
                ["code"] = TextSchema(64, true, "^[A-Z][A-Z0-9_]{0,63}$"), ["severity"] = EnumSchema(["warning", "error"]),
                ["message"] = TextSchema(8192, true), ["recovery"] = EnumSchema(Recoveries)
            }), 100),
            ["nextActions"] = ArraySchema(new JsonObject { ["anyOf"] = new JsonArray(
                ObjectSchema(new JsonObject { ["kind"] = EnumSchema(Actions.Where(kind => kind is not ("create-pr" or "merge-pr" or "trigger-ci" or "approve" or "suggestChanges" or "requestChanges")).ToArray()), ["reason"] = TextSchema(4096, true), ["body"] = TextSchema(32768) }),
                ObjectSchema(new JsonObject { ["kind"] = EnumSchema(["approve", "suggestChanges", "requestChanges"]), ["reason"] = TextSchema(4096, true), ["body"] = TextSchema(32768), ["suggestionIds"] = ArraySchema(IdentifierSchema(), 100) }),
                ObjectSchema(new JsonObject { ["kind"] = EnumSchema(["merge-pr"]), ["reason"] = TextSchema(4096, true), ["body"] = EnumSchema([""]) }),
                ObjectSchema(new JsonObject { ["kind"] = EnumSchema(["trigger-ci"]), ["reason"] = TextSchema(4096, true), ["body"] = EnumSchema(["", "/azp run"]) }),
                ObjectSchema(new JsonObject { ["kind"] = EnumSchema(["create-pr"]), ["reason"] = TextSchema(4096, true), ["body"] = EnumSchema([""]),
                    ["pullRequest"] = ObjectSchema(new JsonObject { ["head"] = TextSchema(240, true, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}:[^\u0000\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$"), ["sourceHeadSha"] = TextSchema(40, true, "^[a-fA-F0-9]{40}$"), ["base"] = TextSchema(200, true),
                        ["title"] = TextSchema(256, true), ["body"] = TextSchema(60000), ["draft"] = new JsonObject { ["type"] = "boolean" } }) })
            ) }, 50),
            ["review"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "null" }, review) },
            ["needsReview"] = new JsonObject { ["type"] = "boolean" }
        });
        schema["description"] = "Return one complete JSON object without duplicate property names. Field lengths use the Host's UTF-16 character counts. Emit integer tokens without decimal or exponent notation. Identifier, SHA and branch patterns match the complete value with no trailing newline. IDs are unique within findings, validation, verificationEvidence and review.suggestions. Every suggestionIds entry is unique and references review.suggestions. Each suggestion satisfies startLine <= line and line - startLine <= 999. Task scope, required checks, source revision and total serialized-byte limits are checked independently by the Host.";
        return schema;
    }

    public static JsonObject FromModel(string output, JsonObject? task, string executionState, int? exitCode,
        string? errorCode, string? errorMessage, bool outputComplete = true, int? expectedSchemaVersion = null,
        bool hasFinalResult = true, string? diagnosticOutput = null)
    {
        if (!hasFinalResult) return expectedSchemaVersion == 3 ? InvalidResultV3(diagnosticOutput ?? output, task, executionState, exitCode, errorCode, errorMessage, outputComplete)
            : InvalidResult(diagnosticOutput ?? output, task, executionState, exitCode, errorCode, errorMessage, outputComplete);
        var original = output;
        IReadOnlyList<ResultValidationIssue>? validationIssues = null;
        try
        {
            var candidate = output.Trim();
            if (candidate.StartsWith("```", StringComparison.Ordinal))
            {
                var newline = candidate.IndexOf('\n'); var end = candidate.LastIndexOf("```", StringComparison.Ordinal);
                if (newline >= 0 && end > newline) candidate = candidate[(newline + 1)..end];
            }
            var modelBytes = Encoding.UTF8.GetByteCount(candidate);
            var maximumBytes = expectedSchemaVersion is null or 3 ? ResultLimits.MaximumV3ModelBytes : ResultLimits.MaximumModelBytes;
            if ((expectedSchemaVersion is null or 1 or 2 or 3) && modelBytes <= maximumBytes)
            {
                using var document = JsonDocument.Parse(candidate, new JsonDocumentOptions { MaxDepth = 32 });
                if (!NoDuplicateFields(document.RootElement)) validationIssues = DuplicateFieldIssues(document.RootElement);
                else if (JsonNode.Parse(candidate) is JsonObject parsed)
                {
                    if ((expectedSchemaVersion is null or 3) && IsVersion(parsed, 3) && ValidV3(parsed))
                        return NormalizeV3(parsed, task, executionState, exitCode, errorCode, errorMessage, outputComplete, original);
                    if (expectedSchemaVersion != 3 && modelBytes <= ResultLimits.MaximumModelBytes && IsVersion(parsed, 2) && ValidV2(parsed))
                        return Normalize(parsed, task, executionState, exitCode, errorCode, errorMessage, outputComplete, original);
                    if (expectedSchemaVersion is not (2 or 3) && modelBytes <= ResultLimits.MaximumModelBytes && (!parsed.ContainsKey("schemaVersion") || IsVersion(parsed, 1)) && ValidLegacy(parsed))
                        return NormalizeLegacy(parsed, task, executionState, exitCode, errorCode, errorMessage, outputComplete);
                    var version = expectedSchemaVersion is 2 or 3 ? expectedSchemaVersion.Value : IsVersion(parsed, 3) ? 3 : IsVersion(parsed, 2) ? 2 : 0;
                    if (version != 0) validationIssues = ValidationIssues(parsed, version);
                }
                else validationIssues = [new("$", "The final result must be one JSON object.")];
            }
            else validationIssues = [new("$", "The final JSON exceeds the saved schema's " + maximumBytes + " UTF-8 byte limit or requests an unsupported schema version.")];
        }
        catch (JsonException error) { validationIssues = [new(error.Path ?? "$", "The final response must be valid JSON with no more than 32 levels of nesting.")]; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
        { validationIssues ??= [new("$", "The report contains a field type or value that cannot be read under the saved result contract.")]; }
        return expectedSchemaVersion == 3 ? InvalidResultV3(original, task, executionState, exitCode, errorCode, errorMessage, outputComplete, validationIssues)
            : InvalidResult(original, task, executionState, exitCode, errorCode, errorMessage, outputComplete, validationIssues);
    }

    /// <summary>Add stable Host-owned proposal identities without changing stored records or action text.</summary>
    public static JsonObject WithProposalIds(JsonObject result)
    {
        var copy = result.DeepClone().AsObject();
        ExplainRetainedInvalidResult(copy);
        var field = IsVersion(copy, 2) || IsVersion(copy, 3) ? "nextActions" : "nextSteps";
        if (copy[field] is JsonArray actions) AssignProposalIds(actions);
        if ((IsVersion(copy, 2) || IsVersion(copy, 3)) && copy["nextActions"] is JsonArray canonical) copy["nextSteps"] = canonical.DeepClone();
        return copy;
    }

    public static JsonObject? FindProposal(JsonObject result, string proposalId)
    {
        var projected = WithProposalIds(result);
        var field = IsVersion(projected, 2) || IsVersion(projected, 3) ? "nextActions" : "nextSteps";
        return (projected[field] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(row => Text(row, "proposalId") == proposalId)?.DeepClone().AsObject();
    }

    /// <summary>Validate the canonical part of a saved v2 result without trusting its display metadata.</summary>
    public static bool IsValidStoredV2(JsonObject result)
    {
        if (!IsVersion(result, 2)) return false;
        var canonical = new JsonObject();
        foreach (var field in Fields.Concat(OptionalFields))
            if (result.ContainsKey(field)) canonical[field] = result[field]?.DeepClone();
        if (canonical["nextActions"] is JsonArray actions)
            foreach (var action in actions.OfType<JsonObject>()) { action.Remove("proposalId"); action.Remove("availability"); }
        return ValidV2(canonical, allowHostChecks: true);
    }

    /// <summary>Recover interrupted persistence while retaining valid evidence and suppressing publication.</summary>
    public static JsonObject RecoverExisting(JsonObject existingResult, string executionState, string? code, string? message,
        JsonObject? task = null, int? exitCode = null)
    {
        var original = existingResult.ToJsonString();
        JsonObject? recovered = null;
        if (IsVersion(existingResult, 3))
        {
            var canonical = CanonicalV3(existingResult);
            if (ValidV3(canonical)) recovered = NormalizeV3(canonical, task, executionState, exitCode, code, message, true, original);
        }
        else if (IsVersion(existingResult, 2))
        {
            var canonical = new JsonObject();
            foreach (var field in Fields.Concat(OptionalFields)) if (existingResult.ContainsKey(field)) canonical[field] = existingResult[field]?.DeepClone();
            if (canonical["nextActions"] is JsonArray actions) foreach (var action in actions.OfType<JsonObject>()) { action.Remove("proposalId"); action.Remove("availability"); }
            if (ValidV2(canonical, allowHostChecks: true)) recovered = Normalize(canonical, task, executionState, exitCode, code, message, true, original);
        }
        else if ((!existingResult.ContainsKey("schemaVersion") || IsVersion(existingResult, 1)) && ValidLegacy(existingResult))
        {
            recovered = NormalizeLegacy(existingResult, task, executionState, exitCode, code, message, true);
            recovered["schemaVersion"] = 1;
            recovered["outcome"] = ProcessOutcome(executionState, exitCode, code) ?? "blocked";
            recovered["phase"] = Phases.Contains(Text(existingResult, "phase"), StringComparer.Ordinal) ? Text(existingResult, "phase") : "reporting";
            recovered["review"] = null;
        }
        recovered ??= IsVersion(existingResult, 3) ? InvalidResultV3(original, task, executionState, exitCode, code, message, false)
            : InvalidResult(original, task, executionState, exitCode, code, message, false);
        if (existingResult["rawOutput"] is JsonValue raw && raw.TryGetValue<string>(out var text))
            recovered["rawOutput"] = Limit(OutputRedactor.Redact(text), ResultLimits.RawDiagnosticCharacters);
        if (IsVersion(existingResult, 3) && existingResult["sourceStatusCorrections"] is JsonArray corrections)
            recovered["sourceStatusCorrections"] = corrections.DeepClone();
        return recovered;
    }

    public static JsonObject Failure(string executionState, string? code, string? message, JsonObject? task = null, int? exitCode = null, string phase = "setup", int? expectedSchemaVersion = null)
    {
        if (expectedSchemaVersion == 3) return FailureV3(executionState, code, message, task, exitCode, phase);
        var outcome = ProcessOutcome(executionState, exitCode, code) ?? "blocked";
        var result = Empty(outcome, Phases.Contains(phase, StringComparer.Ordinal) ? phase : "setup",
            Limit(message ?? "The workflow did not complete. Inspect task diagnostics and retained work before retrying.", 32768));
        AddDiagnostic(result, SafeCode(code, outcome.ToUpperInvariant()), message ?? "The workflow did not complete.", RecoveryFor(code));
        result["nextActions"] = new JsonArray(Action(RecoveryFor(code), "Inspect diagnostics and correct the blocker before starting another run."));
        AddRequiredChecks(result, task);
        Finish(result, task, exitCode);
        return result;
    }

    /// <summary>Additional result eligibility only; live account, remote state and confirmation remain required.</summary>
    public static bool CanPublish(JsonObject? result, JsonObject task, string kind)
    {
        if (result is not null && IsVersion(result, 3)) return CanPublishV3(result, task, kind);
        if (result is null || !PublicationActions.Contains(kind, StringComparer.Ordinal)) return false;
        var field = IsVersion(result, 2) ? "nextActions" : "nextSteps";
        var actions = (result[field] as JsonArray)?.OfType<JsonObject>().Where(row => Text(row, "kind") == kind).ToArray() ?? [];
        // Historical callers could offer a fixed action without a nextSteps proposal. Preserve that legacy behavior.
        if (!result.ContainsKey("schemaVersion") || IsVersion(result, 1))
            return PublicationReasons(result, task, new JsonObject { ["kind"] = kind }).Count == 0;
        return actions.Any(action => PublicationReasons(result, task, action).Count == 0);
    }

    /// <summary>Decorate a detached result with Host-owned eligibility for every retained proposal.</summary>
    public static JsonObject WithProposalAvailability(JsonObject result, JsonObject task, JsonObject status)
    {
        var copy = WithProposalIds(result);
        var field = IsVersion(copy, 2) || IsVersion(copy, 3) ? "nextActions" : "nextSteps";
        if (copy[field] is JsonArray actions)
            foreach (var action in actions.OfType<JsonObject>()) action["availability"] = ProposalAvailability(copy, task, status, action);
        if ((IsVersion(copy, 2) || IsVersion(copy, 3)) && copy["nextActions"] is JsonArray canonical) copy["nextSteps"] = canonical.DeepClone();
        return copy;
    }

    /// <summary>Shared result/lifecycle gate. This does not replace live GitHub preview and confirmation.</summary>
    public static JsonObject ProposalAvailability(JsonObject result, JsonObject task, JsonObject status, JsonObject action)
    {
        if (IsVersion(result, 3)) return ProposalAvailabilityV3(result, task, status, action);
        var kind = Text(action, "kind");
        var reasons = new List<string>();
        if (!Actions.Contains(kind, StringComparer.Ordinal)) reasons.Add("This action kind is not supported.");
        else if (kind == "none") reasons.Add("No follow-up action was proposed.");
        else if (PublicationActions.Contains(kind, StringComparer.Ordinal))
        {
            if (Text(status, "state") != "succeeded" || status["exitCode"] is not JsonValue exit || !exit.TryGetValue<int>(out var code) || code != 0)
                reasons.Add("GitHub actions require a finished CLI run with a recorded exit code of zero.");
            reasons.AddRange(PublicationReasons(result, task, action));
        }
        return new JsonObject { ["enabled"] = reasons.Count == 0, ["reasons"] = new JsonArray(reasons.Distinct(StringComparer.Ordinal).Select(reason => (JsonNode?)JsonValue.Create(reason)).ToArray()) };
    }

    private static List<string> PublicationReasons(JsonObject result, JsonObject task, JsonObject action)
    {
        var reasons = new List<string>();
        var kind = Text(action, "kind");
        if (!PublicationActions.Contains(kind, StringComparer.Ordinal)) { reasons.Add("This is not a GitHub action."); return reasons; }
        if (!ValidTarget(task)) reasons.Add("The saved task does not identify a supported GitHub target.");
        var targetType = Text(task["target"] as JsonObject, "type");
        var reviewAction = kind is "approve" or "suggestChanges" or "requestChanges";
        if (reviewAction && Text(task, "actionKind") is ("e2e" or "pr-verify"))
            reasons.Add("A verification-only task cannot submit a code-review decision; open its parent code review or start a review.");
        var expected = Text(task, "expectedHeadSha");
        var legacy = !result.ContainsKey("schemaVersion") || IsVersion(result, 1);
        if ((reviewAction || kind is "merge-pr" or "trigger-ci") && (targetType != "pr" || !Sha(expected, allowLegacy: legacy)))
            reasons.Add("This action requires a pull request and its recorded analysis SHA.");
        if (kind == "create-pr" && targetType != "issue") reasons.Add("Create PR requires an issue task.");
        if (legacy)
        {
            if (kind is "create-pr" or "merge-pr" or "trigger-ci") reasons.Add("This historical result does not contain a version 2 action contract.");
            return reasons;
        }
        if (!IsVersion(result, 2) || result["structured"] is not JsonValue structured || !structured.TryGetValue<bool>(out var parsed) || !parsed)
            reasons.Add("A valid structured workflow result is required.");
        if (result["cliExitCode"] is not JsonValue exit || !exit.TryGetValue<int>(out var exitCode) || exitCode != 0)
            reasons.Add("The recorded CLI execution did not complete successfully.");
        var field = result["nextActions"] as JsonArray;
        if (field is null || !field.OfType<JsonObject>().Any(row => SameProposal(row, action)))
            reasons.Add("This proposal is not part of the saved workflow result.");
        var reportAction = kind is "comment" or "trigger-ci";
        var outcome = Text(result, "outcome");
        if (reportAction ? outcome is not ("completed" or "blocked" or "failed") : outcome != "completed")
            reasons.Add(reportAction ? "Only a completed, blocked or failed report from a successful CLI run can be published." : "This action requires a completed workflow.");
        var diagnostics = result["diagnostics"] as JsonArray;
        if (diagnostics is null || diagnostics.OfType<JsonObject>().Any(row => Text(row, "code") is "INVALID_RESULT" or "OUTPUT_INCOMPLETE"))
            reasons.Add("The final structured output was invalid or incomplete; inspect the execution logs.");
        if (!reportAction && (result["validation"] is not JsonArray checks || !ChecksComplete(checks, task) ||
            diagnostics is null || diagnostics.OfType<JsonObject>().Any(row => Text(row, "severity") == "error")))
            reasons.Add("Required workflow checks and error diagnostics must be resolved before this action.");
        if (reviewAction || (kind is "merge-pr" or "trigger-ci") && result["review"] is JsonObject)
        {
            var draft = result["review"] as JsonObject;
            if (draft is null || !ValidReview(draft) || !Sha(expected) || !string.Equals(Text(draft, "headSha"), expected, StringComparison.OrdinalIgnoreCase))
                reasons.Add("The review draft does not match the task's original pull request SHA.");
        }
        if (kind is "approve" or "merge-pr")
        {
            if (result["findings"] is JsonArray findings && findings.OfType<JsonObject>().Any(row =>
                (Text(row, "severity") is "high" or "medium") && (Text(row, "status") is "open" or "unverified")))
                reasons.Add("Resolve the high or medium severity findings on the reviewed revision before approval or merge.");
            var scopedReview = Text(task, "actionKind") == "pr-review" && ReviewModes.Mode(task) is not null;
            if (scopedReview)
            {
                var conclusion = result["reviewConclusion"] as JsonObject;
                if (conclusion is null || Text(conclusion, "status") != "no-blocking-findings" ||
                    !string.Equals(Text(conclusion, "revisionSha"), expected, StringComparison.OrdinalIgnoreCase) ||
                    conclusion["blockingUncertainties"] is not JsonArray { Count: 0 })
                    reasons.Add("The code review must conclude that the original revision has no blocking findings or material unresolved questions.");
                if (result["assessment"] is JsonObject product && (Text(product, "status") == "failed" || Text(product, "subject") != "original-pr" ||
                    !string.Equals(Text(product, "revisionSha"), expected, StringComparison.OrdinalIgnoreCase)))
                    reasons.Add("A failed or different-source product assessment cannot support approval of this PR.");
                if ((result["verificationEvidence"] as JsonArray)?.OfType<JsonObject>().Any(row => Text(row, "status") == "failed" && Text(row, "subject") == "original-pr" &&
                    string.Equals(Text(row, "revisionSha"), expected, StringComparison.OrdinalIgnoreCase)) == true)
                    reasons.Add("Resolve the recorded failing verification of this PR revision before approval or merge.");
            }
            if ((!scopedReview || kind == "merge-pr") && result["assessment"] is JsonObject assessment && (Text(assessment, "status") != "passed" || Text(assessment, "subject") != "original-pr" ||
                !string.Equals(Text(assessment, "revisionSha"), expected, StringComparison.OrdinalIgnoreCase)))
                reasons.Add("This action requires a passing assessment of the original pull request revision.");
        }
        if (kind == "create-pr")
        {
            var pr = action["pullRequest"] as JsonObject;
            if (!ValidPullRequest(pr) || !Sha(Text(pr, "sourceHeadSha")))
                reasons.Add("This proposal lacks the verified source branch SHA. Run the workflow again to bind the PR to tested code.");
            if (result["assessment"] is JsonObject assessment && (Text(assessment, "status") != "passed" || Text(assessment, "subject") != "local-candidate" ||
                !string.Equals(Text(assessment, "revisionSha"), Text(pr, "sourceHeadSha"), StringComparison.OrdinalIgnoreCase)))
                reasons.Add("The proposed source SHA must match the passing local candidate assessment.");
        }
        return reasons;
    }

    private static bool SameProposal(JsonObject left, JsonObject right)
    {
        var a = left.DeepClone().AsObject(); var b = right.DeepClone().AsObject();
        foreach (var key in new[] { "proposalId", "availability" }) { a.Remove(key); b.Remove(key); }
        return JsonNode.DeepEquals(a, b);
    }

    private static JsonObject Normalize(JsonObject input, JsonObject? task, string state, int? exitCode, string? errorCode, string? errorMessage, bool outputComplete, string original)
    {
        var result = (JsonObject)input.DeepClone();
        AddRequiredChecks(result, task);
        ValidateAcceptedScope(result, task);
        var processOutcome = ProcessOutcome(state, exitCode, errorCode);
        if (processOutcome is not null)
        {
            result["outcome"] = processOutcome;
            AddDiagnostic(result, SafeCode(errorCode, processOutcome.ToUpperInvariant()), errorMessage ?? $"CLI execution ended with state {state} (exit code {exitCode?.ToString() ?? "unknown"}).", RecoveryFor(errorCode));
        }
        if (!outputComplete) AddDiagnostic(result, "OUTPUT_INCOMPLETE", "Some CLI output could not be retained or interpreted completely. Inspect the saved execution logs.", "inspectResult");
        var checksComplete = ChecksComplete(result["validation"]!.AsArray(), task);
        if (!checksComplete)
            AddDiagnostic(result, "WORKFLOW_CHECKS_INCOMPLETE", "One or more required workflow checks failed, were not completed, or lack supporting evidence. Inspect validation details and remaining checks.", "inspectResult");
        var errors = result["diagnostics"]!.AsArray().OfType<JsonObject>().Any(row => Text(row, "severity") == "error");
        if (Text(result, "outcome") == "completed" && (errors || !outputComplete || !checksComplete)) result["outcome"] = "blocked";
        if (Text(result, "outcome") != "completed" && !errors)
            AddDiagnostic(result, "WORKFLOW_" + Text(result, "outcome").ToUpperInvariant(), Text(result, "summary"), "inspectResult");
        if (Text(result, "outcome") != "completed" || result["assessment"] is JsonObject assessment && Text(assessment, "status") != "passed" ||
            result["findings"]!.AsArray().OfType<JsonObject>().Any(row => Text(row, "status") is "open" or "unverified")) result["needsReview"] = true;
        Finish(result, task, exitCode);
        if (result["validation"]!.AsArray().Count > 204 || result["diagnostics"]!.AsArray().Count > 104 || Encoding.UTF8.GetByteCount(result.ToJsonString()) > ResultLimits.MaximumNormalizedResultBytes)
            return InvalidResult(original, task, state, exitCode, errorCode, errorMessage, outputComplete);
        return result;
    }

    private static JsonObject InvalidResult(string original, JsonObject? task, string state, int? exitCode, string? code, string? message, bool outputComplete, IReadOnlyList<ResultValidationIssue>? validationIssues = null)
    {
        var result = Failure(state, code ?? "INVALID_RESULT", message ?? "The CLI did not return a valid structured workflow result. Inspect its original output.", task, exitCode, "reporting");
        if (ProcessOutcome(state, exitCode, code) is null) result["outcome"] = "blocked";
        AddDiagnostic(result, "INVALID_RESULT", "The final result did not match the workflow result contract.", "inspectResult");
        AddValidationDiagnostics(result, validationIssues);
        if (!outputComplete) AddDiagnostic(result, "OUTPUT_INCOMPLETE", "Some CLI output was incomplete. Inspect the saved execution logs.", "inspectResult");
        result["rawOutput"] = Limit(OutputRedactor.Redact(original), ResultLimits.RawDiagnosticCharacters);
        Finish(result, task, exitCode);
        if (Encoding.UTF8.GetByteCount(result.ToJsonString()) > ResultLimits.MaximumNormalizedResultBytes) result["rawOutput"] = Limit(Text(result, "rawOutput"), 8192);
        if (Encoding.UTF8.GetByteCount(result.ToJsonString()) > ResultLimits.MaximumNormalizedResultBytes) result["summary"] = Limit(Text(result, "summary"), 4096);
        return result;
    }

    private static void Finish(JsonObject result, JsonObject? task, int? exitCode)
    {
        result["structured"] = true;
        result["cliExitCode"] = exitCode;
        var actions = result["nextActions"]!.AsArray();
        // Retain every model proposal for review. Eligibility is a detached Host projection,
        // never a destructive rewrite of the model's evidence or proposed follow-up.
        if (actions.Count == 0) actions.Add(Action("inspectResult", "Inspect task findings, diagnostics, retained artifacts and validation evidence."));
        AssignProposalIds(actions);
        result["blockers"] = new JsonArray(result["diagnostics"]!.AsArray().OfType<JsonObject>()
            .Where(row => Text(row, "severity") == "error").Select(row => (JsonNode?)JsonValue.Create(Text(row, "message"))).ToArray());
        result["nextSteps"] = actions.DeepClone();
    }

    private static void AssignProposalIds(JsonArray actions)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var action in actions.OfType<JsonObject>())
        {
            var identity = action.DeepClone().AsObject(); identity.Remove("proposalId"); identity.Remove("availability");
            var fingerprint = Protocol.Fingerprint(identity)[..24];
            var occurrence = occurrences.TryGetValue(fingerprint, out var previous) ? previous + 1 : 1;
            occurrences[fingerprint] = occurrence;
            action["proposalId"] = "proposal-" + fingerprint + "-" + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static void AddRequiredChecks(JsonObject result, JsonObject? task)
    {
        var validation = result["validation"]!.AsArray();
        var requiredChecks = RequiredChecksForTask(task);
        // A scoped task's requirements are chosen by the user and frozen by the Host, not expanded by PR text.
        if (ReviewModes.Mode(task) is not null && Text(task, "actionKind") is ("pr-review" or "pr-verify"))
            foreach (var check in validation.OfType<JsonObject>()) check["required"] = requiredChecks.Contains(Text(check, "id"), StringComparer.Ordinal);
        foreach (var id in requiredChecks)
        {
            var existing = validation.OfType<JsonObject>().FirstOrDefault(row => Text(row, "id") == id);
            if (existing is not null) { existing["required"] = true; continue; }
            validation.Add(new JsonObject { ["id"] = id, ["name"] = id, ["status"] = "not_run", ["required"] = true,
                ["details"] = "This required workflow check was not reported as completed.", ["evidence"] = new JsonArray() });
        }
    }

    private static bool ChecksComplete(JsonArray validation, JsonObject? task)
    {
        var rows = validation.OfType<JsonObject>().ToArray();
        if (rows.Length != validation.Count || rows.Any(row => row["required"] is not JsonValue required || !required.TryGetValue<bool>(out _))) return false;
        if (rows.Any(row => (Text(row, "status") is "failed" or "not_run") && string.IsNullOrWhiteSpace(Text(row, "details")))) return false;
        if (rows.Any(row => row["required"]!.GetValue<bool>() && !PassedWithEvidence(row))) return false;
        return RequiredChecksForTask(task).All(id => rows.Count(row => Text(row, "id") == id && PassedWithEvidence(row)) == 1);
    }

    private static void ValidateAcceptedScope(JsonObject result, JsonObject? task)
    {
        var mode = ReviewModes.Mode(task);
        var kind = Text(task, "actionKind");
        if (mode is null || kind is not ("pr-review" or "pr-verify" or "e2e")) return;
        if ((result["verificationEvidence"] as JsonArray)?.OfType<JsonObject>().Any(row => Text(row, "source") == "current-run" &&
            Text(row, "status") is ("passed" or "failed") && (mode == "static" || mode == "build-tests" && Text(row, "kind") == "runtime")) == true)
            AddDiagnostic(result, "REVIEW_SCOPE_EXCEEDED", "The result reports execution outside the accepted review scope. Inspect the evidence before using this run.", "inspectResult");
        if (kind == "pr-review" && result["reviewConclusion"] is JsonObject conclusion && conclusion["revisionSha"] is not null &&
            !string.Equals(Text(conclusion, "revisionSha"), Text(task, "expectedHeadSha"), StringComparison.OrdinalIgnoreCase))
            AddDiagnostic(result, "REVIEW_REVISION_MISMATCH", "The code-review conclusion identifies a different revision from the accepted PR.", "inspectResult");
        if (kind is ("pr-verify" or "e2e") && (result["reviewConclusion"] is not null || result["review"] is not null))
            AddDiagnostic(result, "VERIFICATION_SCOPE_MISMATCH", "A verification-only task cannot replace the parent code-review conclusion or create a new review draft.", "inspectResult");
    }

    private static bool PassedWithEvidence(JsonObject check) => Text(check, "status") == "passed" && !string.IsNullOrWhiteSpace(Text(check, "details")) &&
        check["evidence"] is JsonArray evidence && evidence.Count > 0 && evidence.All(node => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text));

    private static void AddDiagnostic(JsonObject result, string code, string message, string recovery)
    {
        var diagnostics = result["diagnostics"]!.AsArray();
        code = SafeCode(code, "WORKFLOW_ERROR"); message = Limit(message, 8192);
        var existing = diagnostics.OfType<JsonObject>().FirstOrDefault(row => Text(row, "code") == code && Text(row, "message") == message);
        if (existing is not null)
        {
            // A model warning cannot shadow a Host-observed error with the same identifier/text.
            existing["severity"] = "error";
            existing["recovery"] = Recoveries.Contains(recovery, StringComparer.Ordinal) ? recovery : "inspectResult";
            return;
        }
        diagnostics.Add(new JsonObject { ["code"] = code, ["severity"] = "error", ["message"] = string.IsNullOrWhiteSpace(message) ? code : message,
            ["recovery"] = Recoveries.Contains(recovery, StringComparer.Ordinal) ? recovery : "inspectResult" });
    }

    private static JsonObject Empty(string outcome, string phase, string summary) => new()
    {
        ["schemaVersion"] = 2, ["outcome"] = outcome, ["phase"] = phase, ["summary"] = string.IsNullOrWhiteSpace(summary) ? "The workflow did not complete." : summary,
        ["assessment"] = null, ["reviewConclusion"] = null, ["verificationEvidence"] = new JsonArray(), ["verificationRecommendation"] = null,
        ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(), ["validation"] = new JsonArray(), ["diagnostics"] = new JsonArray(),
        ["nextActions"] = new JsonArray(Action("inspectResult", "Inspect diagnostics and existing work before starting another run.")), ["review"] = null, ["needsReview"] = true
    };

    private static string? ProcessOutcome(string state, int? exitCode, string? code) => state switch
    {
        "cancelled" => "cancelled", "interrupted" => "interrupted", "failed" => "failed",
        _ => state != "succeeded" || exitCode is not null and not 0 || !string.IsNullOrEmpty(code) ? "failed" : null
    };
    private static string RecoveryFor(string? code) => code is "AUTH_REQUIRED" or "PERMISSION_DENIED" or "CLI_NOT_FOUND" or "CLI_SELECTION_REQUIRED" or "CLI_SELECTION_UNAVAILABLE" or "REPO_NOT_CONFIGURED" or "PROMPT_NOT_CONFIGURED" ? "configure" : "inspectResult";
    private static JsonObject Action(string kind, string reason, string body = "") => new() { ["kind"] = kind, ["reason"] = reason, ["body"] = body };

    private static bool ValidV2(JsonObject result, bool allowHostChecks = false)
    {
        if (!ExactOptional(result, Fields, OptionalFields) || !IsVersion(result, 2) || !Enum(result, "outcome", Outcomes) || !Enum(result, "phase", Phases) ||
            result["assessment"] is not null && !ValidAssessment(result["assessment"]) ||
            result["reviewConclusion"] is not null && !ValidReviewConclusion(result["reviewConclusion"]) ||
            result.ContainsKey("verificationEvidence") && !Array(result, "verificationEvidence", 100, ValidVerificationEvidence) ||
            result["verificationRecommendation"] is not null && !ValidVerificationRecommendation(result["verificationRecommendation"]) ||
            !String(result, "summary", 32768, true) || !Bool(result, "needsReview") ||
            !Array(result, "findings", 200, node => node is JsonObject row && Exact(row, "id", "title", "severity", "status", "path", "line", "details", "evidence") && Identifier(row, "id") &&
                String(row, "title", 512, true) && Enum(row, "severity", ["high", "medium", "low"]) && Enum(row, "status", ["open", "fixed", "unverified"]) && String(row, "path", 4096) &&
                (row["line"] is null || PositiveInteger(row["line"])) && String(row, "details", 4096) && Evidence(row)) ||
            !Array(result, "artifacts", 200, node => node is JsonObject row && Exact(row, "path", "label") && String(row, "path", 4096, true) && String(row, "label", 512, true)) ||
            !Array(result, "validation", allowHostChecks ? 204 : 200, node => node is JsonObject row && Exact(row, "id", "name", "status", "required", "details", "evidence") && Identifier(row, "id") &&
                String(row, "name", 512, true) && Enum(row, "status", ["passed", "failed", "not_run"]) && Bool(row, "required") && String(row, "details", 4096) && Evidence(row)) ||
            !Array(result, "diagnostics", allowHostChecks ? 104 : 100, node => node is JsonObject row && Exact(row, "code", "severity", "message", "recovery") && String(row, "code", 64, true) &&
                Regex.IsMatch(Text(row, "code"), @"\A[A-Z][A-Z0-9_]{0,63}\z") && Enum(row, "severity", ["warning", "error"]) && String(row, "message", 8192, true) && Enum(row, "recovery", Recoveries)) ||
            !Array(result, "nextActions", 50, ValidNextAction) ||
            result["review"] is not null && !ValidReview(result["review"])) return false;
        if (!UniqueIds(result["findings"]!.AsArray()) || !UniqueIds(result["validation"]!.AsArray())) return false;
        if (result["verificationEvidence"] is JsonArray verification && !UniqueIds(verification)) return false;
        var suggestionIds = (result["review"]?["suggestions"] as JsonArray)?.OfType<JsonObject>().Where(row => row.ContainsKey("id"))
            .Select(row => Text(row, "id")).ToHashSet(StringComparer.Ordinal) ?? [];
        return result["nextActions"]!.AsArray().OfType<JsonObject>().All(row => row["suggestionIds"] is not JsonArray ids || ids.All(id => suggestionIds.Contains(id!.GetValue<string>())));
    }

    private static bool ValidAssessment(JsonNode? value) => value is JsonObject assessment && Exact(assessment, "subject", "status", "summary", "revisionSha") &&
        Enum(assessment, "subject", ["original-pr", "local-candidate", "target"]) && Enum(assessment, "status", ["passed", "failed", "inconclusive"]) &&
        String(assessment, "summary", 4096, true) && (assessment["revisionSha"] is null || Sha(Text(assessment, "revisionSha")));

    private static bool ValidReviewConclusion(JsonNode? value) => value is JsonObject row && Exact(row, "status", "summary", "revisionSha", "blockingUncertainties") &&
        Enum(row, "status", ["no-blocking-findings", "changes-requested", "inconclusive"]) && String(row, "summary", 4096, true) &&
        (row["revisionSha"] is null || Sha(Text(row, "revisionSha"))) && StringList(row, "blockingUncertainties", 50);

    private static bool ValidVerificationEvidence(JsonNode? value) => value is JsonObject row &&
        Exact(row, "id", "source", "kind", "status", "subject", "revisionSha", "summary", "evidence", "runId") && Identifier(row, "id") &&
        Enum(row, "source", ["current-run", "ci", "author", "prior-run"]) && Enum(row, "kind", ["build", "automated-tests", "runtime"]) &&
        Enum(row, "status", ["passed", "failed", "not_run"]) && Enum(row, "subject", ["original-pr", "local-candidate"]) &&
        (row["revisionSha"] is null || Sha(Text(row, "revisionSha"))) && String(row, "summary", 4096, true) && Evidence(row) &&
        (Text(row, "status") == "not_run" || row["evidence"]!.AsArray().Count > 0) &&
        (row["runId"] is null || Text(row, "source") == "prior-run" && Guid.TryParseExact(Text(row, "runId"), "D", out _));

    private static bool ValidVerificationRecommendation(JsonNode? value) => value is JsonObject row &&
        Encoding.UTF8.GetByteCount(row.ToJsonString(Protocol.JsonOptions)) <= MaximumVerificationRecommendationBytes &&
        Exact(row, "mode", "reason", "question", "scenarios", "prerequisites", "evidence", "readiness") &&
        Enum(row, "mode", ["build-tests", "ui-e2e"]) && String(row, "reason", 4096, true) && String(row, "question", 4096, true) &&
        StringList(row, "scenarios", 30) && row["scenarios"]!.AsArray().Count > 0 && StringList(row, "prerequisites", 30) && Evidence(row) &&
        Enum(row, "readiness", ["ready", "missing-prerequisites", "unknown"]) &&
        (Text(row, "readiness") != "missing-prerequisites" || row["prerequisites"]!.AsArray().Count > 0);

    private static bool StringList(JsonObject row, string field, int maximum) => Array(row, field, maximum,
        node => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length <= 4096 && !string.IsNullOrWhiteSpace(text) && !text.Contains('\0'));

    private static bool ValidReview(JsonNode? value) => value is JsonObject review && Exact(review, "headSha", "body", "suggestions") && Sha(Text(review, "headSha")) && String(review, "body", 32768) &&
        Array(review, "suggestions", 100, node => node is JsonObject row && ExactOptional(row, ["path", "line", "startLine", "side", "body", "replacement"], ["id"]) &&
            (!row.ContainsKey("id") || Identifier(row, "id")) && String(row, "path", 1024, true) &&
            PositiveInteger(row["line"]) && PositiveInteger(row["startLine"]) && row["startLine"]!.GetValue<int>() <= row["line"]!.GetValue<int>() &&
            (long)row["line"]!.GetValue<int>() - row["startLine"]!.GetValue<int>() <= 999 && Text(row, "side") == "RIGHT" && String(row, "body", 8192) && String(row, "replacement", 32768)) &&
        review["suggestions"]!.AsArray().OfType<JsonObject>().Where(row => row.ContainsKey("id")).GroupBy(row => Text(row, "id"), StringComparer.Ordinal).All(group => group.Count() == 1);

    private static bool ValidNextAction(JsonNode? value)
    {
        if (value is not JsonObject row || !Enum(row, "kind", Actions) || !String(row, "reason", 4096, true) || !String(row, "body", 32768)) return false;
        var kind = Text(row, "kind");
        if (kind == "create-pr") return Exact(row, "kind", "reason", "body", "pullRequest") && Text(row, "body").Length == 0 && ValidPullRequest(row["pullRequest"]);
        if (kind is "approve" or "suggestChanges" or "requestChanges") return ExactOptional(row, ["kind", "reason", "body"], ["suggestionIds"]) &&
            (!row.ContainsKey("suggestionIds") || Array(row, "suggestionIds", 100, node => node is JsonValue id && id.TryGetValue<string>(out var text) &&
                Regex.IsMatch(text, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")) && row["suggestionIds"]!.AsArray().Select(id => id!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() == row["suggestionIds"]!.AsArray().Count);
        return Exact(row, "kind", "reason", "body") && (kind != "merge-pr" || Text(row, "body").Length == 0) &&
            (kind != "trigger-ci" || Text(row, "body") is "" or "/azp run");
    }

    private static bool ValidPullRequest(JsonNode? value) => value is JsonObject pr && ExactOptional(pr, ["head", "base", "title", "body", "draft"], ["sourceHeadSha"]) &&
        (!pr.ContainsKey("sourceHeadSha") || Sha(Text(pr, "sourceHeadSha"))) &&
        String(pr, "head", 240, true) && Regex.IsMatch(Text(pr, "head"), @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}:[^\s\0]+\z") &&
        String(pr, "base", 200, true) && String(pr, "title", 256, true) && String(pr, "body", 60000) && Bool(pr, "draft");
    private static bool ValidTarget(JsonObject task) => task["target"] is JsonObject target && (Text(target, "type") is "issue" or "pr") && PositiveInteger(target["number"]);
    private static bool Sha(string value, bool allowLegacy = false) => Regex.IsMatch(value, allowLegacy ? @"\A(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})\z" : @"\A[a-fA-F0-9]{40}\z");
    private static bool UniqueIds(JsonArray rows) => rows.OfType<JsonObject>().Select(row => Text(row, "id")).Distinct(StringComparer.Ordinal).Count() == rows.Count;
    private static bool Identifier(JsonObject row, string key) => String(row, key, 64, true) && Regex.IsMatch(Text(row, key), @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z");
    private static bool Evidence(JsonObject row) => Array(row, "evidence", 50, node => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 4096 && !text.Contains('\0'));
    private static bool Exact(JsonObject value, params string[] fields) => value.Count == fields.Length && fields.All(value.ContainsKey);
    private static bool ExactOptional(JsonObject value, string[] required, string[] optional) => required.All(value.ContainsKey) && value.All(pair => required.Contains(pair.Key, StringComparer.Ordinal) || optional.Contains(pair.Key, StringComparer.Ordinal));
    private static bool Enum(JsonObject row, string field, string[] choices) => choices.Contains(Text(row, field), StringComparer.Ordinal);
    private static bool Bool(JsonObject row, string field) => row[field] is JsonValue value && value.TryGetValue<bool>(out _);
    private static bool String(JsonObject row, string field, int maximum, bool nonempty = false) => row[field] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length <= maximum && !text.Contains('\0') && (!nonempty || !string.IsNullOrWhiteSpace(text));
    private static bool Array(JsonObject row, string field, int maximum, Func<JsonNode?, bool> validate) => row[field] is JsonArray values && values.Count <= maximum && values.All(validate);
    private static bool PositiveInteger(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) && number > 0;
    private static bool IsVersion(JsonObject row, int expected) => row["schemaVersion"] is JsonValue value && value.TryGetValue<int>(out var version) && version == expected;
    private static string Text(JsonObject? row, string field) => row?[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static string SafeCode(string? code, string fallback) => code is not null && Regex.IsMatch(code, @"\A[A-Z][A-Z0-9_]{0,63}\z") ? code : fallback;
    private static string Limit(string value, int maximum)
    {
        value = value.Replace('\0', '\uFFFD');
        return value.Length <= maximum ? value : value[..maximum];
    }

    private static bool NoDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name) || !NoDuplicateFields(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!NoDuplicateFields(item)) return false;
        return true;
    }

    private static bool ValidLegacy(JsonObject result)
    {
        if (!String(result, "summary", 32768, true) || !Bool(result, "needsReview") ||
            !Array(result, "artifacts", 200, node => node is JsonObject row && LegacyString(row, "path") && LegacyString(row, "label")) ||
            !Array(result, "validation", 200, node => node is JsonObject row && LegacyString(row, "name") && Enum(row, "status", ["passed", "failed", "not_run"]) && LegacyString(row, "details")) ||
            !Array(result, "blockers", 100, node => node is JsonValue value && value.TryGetValue<string>(out _)) ||
            !Array(result, "nextSteps", 50, node => node is JsonObject row && LegacyString(row, "reason"))) return false;
        return true;
    }

    private static JsonObject NormalizeLegacy(JsonObject input, JsonObject? task, string state, int? exitCode, string? code, string? message, bool outputComplete)
    {
        var result = (JsonObject)input.DeepClone();
        foreach (var key in result.Select(pair => pair.Key).ToArray())
            if (key is not ("schemaVersion" or "summary" or "artifacts" or "validation" or "blockers" or "nextSteps" or "review" or "needsReview")) result.Remove(key);
        var next = result["nextSteps"]!.AsArray();
        for (var index = next.Count - 1; index >= 0; index--)
        {
            var step = next[index]!.AsObject(); var kind = Text(step, "kind");
            if (!Actions.Contains(kind, StringComparer.Ordinal) || task is not null && PublicationActions.Contains(kind, StringComparer.Ordinal) && !CanPublish(result, task, kind)) { next.RemoveAt(index); continue; }
            step.Remove("suggestions");
            if (step["body"] is not null && !LegacyString(step, "body")) { step.Remove("body"); result["needsReview"] = true; }
        }
        if (result["review"] is not null && !ValidLegacyReview(result["review"]))
        {
            result["review"] = null; result["needsReview"] = true;
            result["blockers"]!.AsArray().Add("The CLI review draft had invalid fields or line locations. The summary was retained and the invalid draft was removed.");
        }
        if (ProcessOutcome(state, exitCode, code) is not null || !outputComplete)
        {
            result["needsReview"] = true;
            result["blockers"]!.AsArray().Add(message ?? (!outputComplete ? "Some CLI output was incomplete. Inspect the execution logs." : $"CLI execution ended with state {state}."));
            result["nextSteps"] = new JsonArray(Action(RecoveryFor(code), "Inspect diagnostics and existing work before starting another run."));
        }
        else if (next.Count == 0) { next.Add(Action("inspectResult", "Inspect the result and incomplete validation.")); result["needsReview"] = true; }
        result["structured"] = true; result["cliExitCode"] = exitCode;
        return result;
    }

    private static bool LegacyString(JsonObject row, string field) => row[field] is JsonValue value && value.TryGetValue<string>(out _);
    private static bool ValidLegacyReview(JsonNode? value) => value is JsonObject review && Sha(Text(review, "headSha")) && LegacyString(review, "body") &&
        Array(review, "suggestions", 100, node => node is JsonObject row && Text(row, "path").Length > 0 && LegacyString(row, "body") && LegacyString(row, "replacement") &&
            Text(row, "side") == "RIGHT" && PositiveInteger(row["line"]) && PositiveInteger(row["startLine"]) && row["startLine"]!.GetValue<int>() <= row["line"]!.GetValue<int>());

    private static JsonObject ObjectSchema(JsonObject properties) => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new JsonArray(properties.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray()), ["properties"] = properties
    };
    private static JsonObject ArraySchema(JsonObject items, int maximum, int minimum = 0) => new() { ["type"] = "array", ["minItems"] = minimum, ["maxItems"] = maximum, ["items"] = items };
    private static JsonObject VerificationRecommendationSchema()
    {
        var schema = AnyOfSchema(new[] { false, true }.Select(missing => ObjectSchema(new JsonObject
        {
            ["mode"] = EnumSchema(["build-tests", "ui-e2e"]), ["reason"] = TextSchema(4096, true), ["question"] = TextSchema(4096, true),
            ["scenarios"] = ArraySchema(TextSchema(4096, true), 30, 1), ["prerequisites"] = ArraySchema(TextSchema(4096, true), 30, missing ? 1 : 0),
            ["evidence"] = EvidenceSchema(), ["readiness"] = EnumSchema(missing ? ["missing-prerequisites"] : ["ready", "unknown"])
        })));
        schema["description"] = "The entire recommendation must fit within 24576 UTF-8 bytes of compact JSON, including JSON escaping (non-ASCII characters count as their escaped form). Keep the actionable scenario list concise; put detailed supporting reports in artifacts. This aggregate Host-validated limit preserves room for immutable context when starting the verification task.";
        return schema;
    }
    private static JsonObject NullableSchema(JsonObject value) => new() { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "null" }, value) };
    private static JsonObject TextSchema(int maximum, bool nonempty = false, string? pattern = null)
    {
        var result = new JsonObject { ["type"] = "string", ["maxLength"] = maximum };
        if (nonempty) result["minLength"] = 1;
        // Spell out .NET's whitespace set instead of relying on a regex engine's differing \s definition.
        result["pattern"] = pattern ?? (nonempty ? @"^[^\u0000]*[^\u0000\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000][^\u0000]*$" : @"^[^\u0000]*$");
        return result;
    }
    private static JsonObject EnumSchema(string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
    private static JsonObject IdentifierSchema() => TextSchema(64, true, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$");
    private static JsonObject PositiveIntegerSchema() => new() { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = int.MaxValue };
}
