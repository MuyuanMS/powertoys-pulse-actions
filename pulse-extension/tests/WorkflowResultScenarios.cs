using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Pure in-memory contracts for model results, Host outcomes and publication eligibility.</summary>
internal static class WorkflowResultScenarios
{
    private static readonly string[] Fields = ["schemaVersion", "outcome", "phase", "summary", "findings", "artifacts", "validation", "diagnostics", "nextActions", "review", "needsReview"];
    private static readonly string[] RemoteKinds = ["approve", "suggestChanges", "requestChanges", "comment", "close"];
    private static readonly IReadOnlyDictionary<string, string[]> RequiredChecks = new Dictionary<string, string[]>
    {
        ["pr-review"] = ["context", "local-review", "verification"],
        ["issue-fix"] = ["reproduction", "implementation", "verification"],
        ["e2e"] = ["setup", "e2e"],
        ["reproduction-setup"] = ["reproduction", "instructions"]
    };

    internal static Task RunAllAsync()
    {
        SchemaDescribesTheCanonicalEnvelope();
        RequiredWorkflowChecksAreHostOwned();
        CompletedReviewCanRetainOpenFindings();
        IncompleteEvidenceCannotClaimCompletion();
        ProcessOutcomesOverrideModelClaims();
        RejectsInvalidShapesAndBounds();
        PublicationRequiresCompletedBoundResults();
        ProductAssessmentIsIndependentFromWorkflowCompletion();
        CompletedReviewCanReportUntestedProductAcceptance();
        ProposalsRemainVisibleWithHostOwnedEligibility();
        SourceShaAndSuggestionAssociationsAreBound();
        LegacyResultsRemainLegacy();
        FailuresUseTheCanonicalEnvelope();
        RecoveryRetainsExistingEvidence();
        Console.WriteLine("PASS workflow results: v2 validation, required checks, process precedence, evidence limits, publication eligibility, v1 compatibility and canonical failures (offline)");
        return Task.CompletedTask;
    }

    private static void SchemaDescribesTheCanonicalEnvelope()
    {
        var schema = WorkflowResult.Schema();
        Check(Text(schema, "type") == "object" && schema["additionalProperties"]?.GetValue<bool>() == false,
            "The canonical result schema must describe a closed object.");
        Check(schema["required"]!.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(Fields.Concat(["assessment", "reviewConclusion", "verificationEvidence", "verificationRecommendation"])),
            "Every canonical model field must be required by the Host-owned schema.");
        var properties = schema["properties"]!.AsObject();
        Check(Fields.All(properties.ContainsKey), "The model schema must describe every required field.");
        Check(properties["outcome"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["completed", "blocked", "failed", "cancelled", "interrupted"]), "The schema must expose workflow outcomes independently from process success.");
    }

    private static void RequiredWorkflowChecksAreHostOwned()
    {
        foreach (var (action, ids) in RequiredChecks)
        {
            var task = TaskContext(action);
            var input = Model(action);
            input["validation"] = new JsonArray();
            var result = Normalize(input, task);
            Check(Text(result, "outcome") == "blocked", "Missing required workflow checks must prevent completion for " + action);
            var validations = result["validation"]!.AsArray().OfType<JsonObject>().ToArray();
            foreach (var id in ids)
            {
                var check = validations.Single(value => Text(value, "id") == id);
                Check(Text(check, "status") == "not_run" && check["required"]?.GetValue<bool>() == true,
                    "Host-required checks must be inserted as required and not_run without fabricated evidence: " + id);
            }
            Check(!validations.Any(value => Text(value, "status") == "passed"), "Absent validation must never be projected as successful.");
            NoPublishing(result, task, allowReports: true);

            input = Model(action);
            foreach (var check in input["validation"]!.AsArray().OfType<JsonObject>()) check["required"] = false;
            result = Normalize(input, task);
            Check(Text(result, "outcome") == "completed" && result["validation"]!.AsArray().OfType<JsonObject>().All(value => value["required"]?.GetValue<bool>() == true),
                "A model cannot make Host-required workflow checks optional.");
            Check(input["validation"]!.AsArray().OfType<JsonObject>().All(value => value["required"]?.GetValue<bool>() == false),
                "Normalizing checks must not mutate the caller's model fixture.");
        }
    }

    private static void CompletedReviewCanRetainOpenFindings()
    {
        var input = Model();
        input["needsReview"] = true;
        input["findings"] = new JsonArray(Finding("open-issue"), Finding("uncertain", "unverified"), Finding("fixed", "fixed"));
        input["diagnostics"] = new JsonArray(Diagnostic("LIMITED_SCOPE", "warning"));
        input["validation"]!.AsArray().Add(Validation("optional-hardware", "not_run", false));
        var result = Normalize(input);
        Check(Text(result, "outcome") == "completed" && result["needsReview"]?.GetValue<bool>() == true && result["findings"]!.AsArray().Count == 3,
            "Completed local review may retain open findings and human inspection needs when required work is complete.");
        Check(result["blockers"]!.AsArray().Count == 0, "Warning diagnostics and optional checks must not become error blockers.");
        Check(result["schemaVersion"]?.GetValue<int>() == 2 && result["structured"]?.GetValue<bool>() == true && result["cliExitCode"]?.GetValue<int>() == 0,
            "A valid v2 result must retain canonical schema, structured status and actual exit code.");
        Check(JsonNode.DeepEquals(result["nextActions"], result["nextSteps"]), "The legacy nextSteps projection must reflect normalized nextActions.");
    }

    private static void IncompleteEvidenceCannotClaimCompletion()
    {
        foreach (var status in new[] { "failed", "not_run" })
        {
            var input = Model();
            input["validation"]![0]!["status"] = status;
            input["validation"]![0]!["required"] = false;
            var result = Normalize(input);
            Check(Text(result, "outcome") != "completed", "A failed or unexecuted Host-required check must override claimed completion even when the model marks it optional.");
            NoPublishing(result, TaskContext(), allowReports: true);
        }
        var optionalFailure = Model();
        optionalFailure["validation"]!.AsArray().Add(Validation("optional-check", "failed", false));
        Check(Text(Normalize(optionalFailure), "outcome") == "completed", "A failed optional check must remain visible without inventing a required workflow blocker.");
        var diagnostic = Model();
        diagnostic["diagnostics"] = new JsonArray(Diagnostic("CHECK_BLOCKED", "error", "Required verification is unavailable."));
        var error = Normalize(diagnostic);
        Check(Text(error, "outcome") != "completed" && error["blockers"]!.AsArray().Any(value => value?.GetValue<string>() == "Required verification is unavailable."),
            "Error diagnostics must prevent completion and project their actual messages into legacy blockers.");
        NoPublishing(error, TaskContext(), allowReports: true);
        var lost = Normalize(Model(), outputComplete: false);
        Check(Text(lost, "outcome") != "completed" && lost["diagnostics"]!.AsArray().OfType<JsonObject>().Any(value => Text(value, "severity") == "error"),
            "Output loss must produce a workflow blocker even after a successful CLI exit and otherwise complete model result.");
        NoPublishing(lost, TaskContext());
        var shadowedLoss = Model();
        shadowedLoss["diagnostics"] = new JsonArray(Diagnostic("OUTPUT_INCOMPLETE", "warning", "Some CLI output could not be retained or interpreted completely. Inspect the saved execution logs."));
        var observedLoss = Normalize(shadowedLoss, outputComplete: false);
        Check(Text(observedLoss, "outcome") == "blocked" && observedLoss["diagnostics"]!.AsArray().OfType<JsonObject>().Any(value => Text(value, "code") == "OUTPUT_INCOMPLETE" && Text(value, "severity") == "error"),
            "A model warning cannot shadow a Host-observed output-loss error with the same code and message.");
        NoPublishing(observedLoss, TaskContext());
        var shadowedChecks = Model();
        shadowedChecks["validation"]!.AsArray().RemoveAt(0);
        shadowedChecks["diagnostics"] = new JsonArray(Diagnostic("WORKFLOW_CHECKS_INCOMPLETE", "warning", "One or more required workflow checks failed, were not completed, or lack supporting evidence. Inspect validation details and remaining checks."));
        var incomplete = Normalize(shadowedChecks);
        Check(Text(incomplete, "outcome") == "blocked" && incomplete["diagnostics"]!.AsArray().OfType<JsonObject>().Any(value => Text(value, "code") == "WORKFLOW_CHECKS_INCOMPLETE" && Text(value, "severity") == "error"),
            "A model warning cannot disguise the Host's missing-required-check decision as a completed workflow.");
        NoPublishing(incomplete, TaskContext(), allowReports: true);
        foreach (var field in new[] { "details", "evidence" })
        {
            var unsupportedPass = Model();
            unsupportedPass["validation"]![0]!["required"] = false;
            if (field == "details") unsupportedPass["validation"]![0]!["details"] = " ";
            else unsupportedPass["validation"]![0]!["evidence"] = new JsonArray();
            var unsupportedResult = Normalize(unsupportedPass);
            Check(Text(unsupportedResult, "outcome") == "blocked", "A required passing check needs nonempty details and supporting evidence, even when the model calls it optional.");
            NoPublishing(unsupportedResult, TaskContext(), allowReports: true);
        }
        var unexplainedCheck = Model();
        var optionalNotRun = Validation("unexplained-optional", "not_run", false); optionalNotRun["details"] = "";
        unexplainedCheck["validation"]!.AsArray().Add(optionalNotRun);
        Check(Text(Normalize(unexplainedCheck), "outcome") == "blocked", "Failed or not-run validation must explain its cause rather than silently omitting details.");
    }

    private static void ProcessOutcomesOverrideModelClaims()
    {
        foreach (var state in new[] { "failed", "cancelled", "interrupted" })
        {
            var code = state == "failed" ? "CLI_EXECUTION_FAILED" : state == "cancelled" ? "CANCELLED" : "WORKER_INTERRUPTED";
            int? exit = state == "failed" ? 7 : null;
            var input = Model();
            input["artifacts"] = new JsonArray(new JsonObject { ["path"] = "retained.patch", ["label"] = "Existing work" });
            var result = Normalize(input, state: state, exit: exit, code: code, message: "Host-observed " + state + ".");
            Check(Text(result, "outcome") == state && result["cliExitCode"]?.GetValue<int>() == exit && HasDiagnostic(result, code),
                "Host process state, stable diagnostic code and actual exit code must override model success: " + state);
            Check(result["artifacts"]!.AsArray().Count == 1 && result["validation"]!.AsArray().Count >= RequiredChecks["pr-review"].Length,
                "Host failure normalization must preserve actual completed checks and retained artifacts.");
            NoPublishing(result, TaskContext());
        }
        foreach (var outcome in new[] { "blocked", "failed", "cancelled", "interrupted" })
        {
            var input = Model(); input["outcome"] = outcome;
            Check(Text(Normalize(input), "outcome") == outcome, "CLI exit zero must not upgrade an explicitly incomplete workflow outcome to completed.");
        }
    }

    private static void RejectsInvalidShapesAndBounds()
    {
        foreach (var field in Fields)
        {
            var input = Model(); input.Remove(field); Invalid(input, "missing " + field);
        }
        Action<JsonObject>[] malformed =
        [
            value => value["schemaVersion"] = "2", value => value["schemaVersion"] = 3,
            value => value["outcome"] = "succeeded", value => value["phase"] = "executing", value => value["summary"] = "",
            value => value["needsReview"] = "true", value => value["findings"] = new JsonObject(), value => value["review"] = new JsonArray(),
            value => value["command"] = "run arbitrary command",
            value => value["findings"] = new JsonArray(Finding("duplicate"), Finding("duplicate")),
            value => value["validation"]!.AsArray().Add(Validation("context")),
            value => value["findings"] = new JsonArray(new JsonObject { ["id"] = "incomplete" }),
            value => value["findings"] = new JsonArray(Finding("wrong-severity", severity: "critical")),
            value => value["findings"] = new JsonArray(Finding("wrong-state", "dismissed")),
            value => { value["findings"] = new JsonArray(Finding("bad-line")); value["findings"]![0]!["line"] = 0; },
            value => { value["findings"] = new JsonArray(Finding("bad-evidence")); value["findings"]![0]!["evidence"] = new JsonArray(1); },
            value => value["validation"]![0]!["status"] = "skipped", value => value["validation"]![0]!["required"] = "true",
            value => value["diagnostics"] = new JsonArray(Diagnostic("lowercase-code")),
            value => value["diagnostics"] = new JsonArray(Diagnostic("INVALID_SEVERITY", "info")),
            value => { value["diagnostics"] = new JsonArray(Diagnostic("INVALID_RECOVERY")); value["diagnostics"]![0]!["recovery"] = "execute"; },
            value => value["nextActions"] = new JsonArray(Next("execute")),
            value => { value["nextActions"] = new JsonArray(Next("comment")); value["nextActions"]![0]!["target"] = new JsonObject { ["number"] = 999 }; },
            value => value["review"]!["headSha"] = "short",
            value => value["summary"] = new string('s', 32769),
            value => value["findings"] = new JsonArray(Finding(new string('x', 65))),
            value => { value["findings"] = new JsonArray(Finding("details-too-long")); value["findings"]![0]!["details"] = new string('x', 4097); },
            value => { value["findings"] = new JsonArray(Finding("evidence-too-long")); value["findings"]![0]!["evidence"] = new JsonArray(new string('x', 4097)); },
            value => { value["validation"]![0]!["evidence"] = Repeat(51, _ => JsonValue.Create("evidence")); },
            value => value["findings"] = Repeat(201, index => Finding("finding-" + index)),
            value => value["validation"] = Repeat(201, index => Validation("check-" + index)),
            value => value["diagnostics"] = Repeat(101, index => Diagnostic("DIAGNOSTIC_" + index, "warning")),
            value => value["nextActions"] = Repeat(51, _ => Next("inspectResult")),
            value => value["artifacts"] = Repeat(201, index => new JsonObject { ["path"] = "file-" + index, ["label"] = "Artifact" })
        ];
        for (var index = 0; index < malformed.Length; index++) { var input = Model(); malformed[index](input); Invalid(input, "malformed fixture " + index); }
        var boundary = Model();
        boundary["summary"] = new string('s', 32768);
        var finding = Finding(new string('x', 64)); finding["details"] = new string('d', 4096);
        finding["evidence"] = Repeat(50, _ => JsonValue.Create("Evidence captured."));
        boundary["findings"] = new JsonArray(finding);
        Check(Text(Normalize(boundary), "outcome") == "completed", "Documented maximum valid summary, ID, detail and evidence bounds must remain accepted.");
        var duplicateJson = Model().ToJsonString().Insert(1, "\"schemaVersion\":2,");
        var duplicate = WorkflowResult.FromModel(duplicateJson, TaskContext(), "succeeded", 0, null, null);
        Check(HasDiagnostic(duplicate, "INVALID_RESULT") && Text(duplicate, "outcome") == "blocked", "Duplicate JSON keys must not create ambiguous structured results.");
        var projectedOverflow = Model();
        projectedOverflow["nextActions"] = Repeat(6, _ => new JsonObject { ["kind"] = "inspectResult", ["reason"] = "Inspect retained evidence.", ["body"] = new string('b', 32768) });
        var bounded = Normalize(projectedOverflow);
        Check(Text(bounded, "outcome") == "completed" && !HasDiagnostic(bounded, "INVALID_RESULT") &&
            System.Text.Encoding.UTF8.GetByteCount(bounded.ToJsonString()) <= ResultLimits.MaximumNormalizedResultBytes &&
            bounded["nextActions"]!.AsArray().All(node => node?["proposalId"] is JsonValue),
            "Host proposal IDs and compatibility projections must retain a valid model result within the separate normalized budget.");
    }

    private static void PublicationRequiresCompletedBoundResults()
    {
        var task = TaskContext();
        var result = Normalize(Model(), task);
        foreach (var kind in RemoteKinds) Check(WorkflowResult.CanPublish(result, task, kind), "Completed required work and matching review SHA must permit the fixed eligible action: " + kind);
        Check(!WorkflowResult.CanPublish(result, task, "execute") && !WorkflowResult.CanPublish(null, task, "comment"), "Only explicit fixed publication actions may be eligible.");
        var staleTask = TaskContext(); staleTask["expectedHeadSha"] = new string('b', 40);
        var stale = Normalize(Model(), staleTask);
        foreach (var kind in new[] { "approve", "suggestChanges", "requestChanges" })
            Check(!WorkflowResult.CanPublish(stale, staleTask, kind) && Kinds(stale).Contains(kind), "A stale review proposal stays visible but cannot be published.");
        Check(WorkflowResult.CanPublish(stale, staleTask, "comment"), "A completed target-bound comment does not depend on review draft SHA.");
        var noReview = Model(); noReview["review"] = null;
        var withoutDraft = Normalize(noReview, task);
        foreach (var kind in new[] { "approve", "suggestChanges", "requestChanges" }) Check(!WorkflowResult.CanPublish(withoutDraft, task, kind), "Review actions require a verified review draft.");
        var issueTask = TaskContext("issue-fix");
        var issueResult = Normalize(Model("issue-fix"), issueTask);
        Check(WorkflowResult.CanPublish(issueResult, issueTask, "comment") && WorkflowResult.CanPublish(issueResult, issueTask, "close"), "Completed issue work may expose fixed-target comment and close actions.");
        foreach (var kind in new[] { "approve", "suggestChanges", "requestChanges" }) Check(!WorkflowResult.CanPublish(issueResult, issueTask, kind), "Issue targets cannot expose pull-request review publication.");
        var unbound = (JsonObject)task.DeepClone(); unbound.Remove("target");
        foreach (var kind in RemoteKinds) Check(!WorkflowResult.CanPublish(result, unbound, kind), "Publication cannot invent an alternative target when the immutable task has none.");
        var tamperedChecks = (JsonObject)result.DeepClone(); tamperedChecks["validation"]![0]!["status"] = "not_run";
        foreach (var kind in RemoteKinds.Where(kind => kind != "comment")) Check(!WorkflowResult.CanPublish(tamperedChecks, task, kind), "Decisions must independently check required evidence instead of trusting outcome alone.");
        Check(JsonNode.DeepEquals(result["nextActions"], result["nextSteps"]), "Publishing eligibility filtering must be reflected in the legacy action projection.");
        var changesOnly = Model(); changesOnly["nextActions"] = new JsonArray(Next("requestChanges"));
        var constrained = Normalize(changesOnly, task);
        Check(WorkflowResult.CanPublish(constrained, task, "requestChanges"), "The explicitly proposed fixed review action remains eligible.");
        foreach (var kind in new[] { "approve", "comment", "close", "suggestChanges" })
            Check(!WorkflowResult.CanPublish(constrained, task, kind), "A caller cannot promote the result's proposed action to a different publication: " + kind);
        foreach (var severity in new[] { "high", "medium" })
        foreach (var status in new[] { "open", "unverified" })
        {
            var findings = Model(); findings["findings"] = new JsonArray(Finding("remaining", status, severity));
            var reviewed = Normalize(findings, task);
            Check(Text(reviewed, "outcome") == "completed" && !WorkflowResult.CanPublish(reviewed, task, "approve") && Kinds(reviewed).Contains("approve") &&
                WorkflowResult.CanPublish(reviewed, task, "requestChanges"), "A completed review retains serious unresolved findings and may request changes, but cannot propose approval.");
        }
    }

    private static void ProductAssessmentIsIndependentFromWorkflowCompletion()
    {
        var task = TaskContext("e2e");
        var input = Model("e2e");
        input["assessment"] = Assessment("failed");
        input["review"] = null;
        input["findings"] = new JsonArray(Finding("runtime-regression"));
        input["validation"]!.AsArray().Add(Validation("runtime-scenario", "failed", false));
        input["nextActions"] = new JsonArray(Next("comment"), Next("merge-pr"), Next("trigger-ci"));
        var result = Normalize(input, task);
        Check(Text(result, "outcome") == "completed" && Text(result["assessment"]!.AsObject(), "status") == "failed" && result["needsReview"]?.GetValue<bool>() == true &&
            WorkflowResult.CanPublish(result, task, "comment") && !WorkflowResult.CanPublish(result, task, "merge-pr"),
            "A fully executed E2E report can complete with product failures, publish its report and prohibit merge.");
        foreach (var outcome in new[] { "blocked", "failed" })
        {
            var report = (JsonObject)input.DeepClone(); report["outcome"] = outcome; report["validation"]![0]!["status"] = "not_run";
            var incomplete = Normalize(report, task);
            Check(Text(incomplete, "outcome") == outcome && WorkflowResult.CanPublish(incomplete, task, "comment") && WorkflowResult.CanPublish(incomplete, task, "trigger-ci"),
                "A valid incomplete report from a successful CLI process can propose a factual comment or CI recovery request.");
            Check(!WorkflowResult.CanPublish(incomplete, task, "merge-pr"), "An incomplete report cannot enable merge.");
        }
        foreach (var state in new[] { "failed", "cancelled", "interrupted" })
        {
            var observed = Normalize(input, task, state, state == "failed" ? 7 : null, "CLI_EXECUTION_FAILED");
            Check(!WorkflowResult.CanPublish(observed, task, "comment") && !WorkflowResult.CanPublish(observed, task, "trigger-ci"),
                "Actual process failure, cancellation or interruption still prevents GitHub writes.");
        }
        var missing = (JsonObject)input.DeepClone(); missing["validation"]![1]!["status"] = "not_run";
        Check(Text(Normalize(missing, task), "outcome") == "blocked", "Product assessment cannot bypass missing required workflow execution.");
        foreach (var status in new[] { "failed", "inconclusive" })
        {
            var review = Model(); review["assessment"] = Assessment(status); review["nextActions"]!.AsArray().Add(Next("merge-pr"));
            var reviewed = Normalize(review);
            Check(!WorkflowResult.CanPublish(reviewed, TaskContext(), "approve") && !WorkflowResult.CanPublish(reviewed, TaskContext(), "merge-pr"),
                "A non-passing original-PR assessment blocks both approval and merge, even without classified findings.");
        }
        var candidate = Model(); candidate["assessment"] = Assessment("passed", "local-candidate"); candidate["nextActions"]!.AsArray().Add(Next("merge-pr"));
        var localPass = Normalize(candidate);
        Check(!WorkflowResult.CanPublish(localPass, TaskContext(), "approve") && !WorkflowResult.CanPublish(localPass, TaskContext(), "merge-pr"),
            "Passing locally repaired code cannot approve or merge the original PR revision.");
        var stale = Model(); stale["assessment"] = Assessment("passed", revision: new string('b', 40));
        Check(!WorkflowResult.CanPublish(Normalize(stale), TaskContext(), "approve"), "A passing judgment of another revision cannot approve the requested PR.");
        var passed = Model(); passed["assessment"] = Assessment("passed"); passed["nextActions"]!.AsArray().Add(Next("merge-pr"));
        Check(WorkflowResult.CanPublish(Normalize(passed), TaskContext(), "approve") && WorkflowResult.CanPublish(Normalize(passed), TaskContext(), "merge-pr"),
            "Completed checks and a passing assessment of the original revision retain action eligibility before live GitHub checks.");
        var wrongAssessment = Model(); wrongAssessment["assessment"] = Assessment("approved"); Invalid(wrongAssessment, "invalid product assessment status");
        wrongAssessment = Model(); wrongAssessment["assessment"] = Assessment("passed"); wrongAssessment["assessment"]!["revisionSha"] = "not-a-sha";
        Invalid(wrongAssessment, "invalid assessment revision");
        Check(!HasDiagnostic(Normalize(Model()), "INVALID_RESULT"), "Historical v2 results without assessment remain valid and readable.");
    }

    private static void ProposalsRemainVisibleWithHostOwnedEligibility()
    {
        var input = Model(); input["findings"] = new JsonArray(Finding("unresolved")); input["nextActions"]!.AsArray().Add(Next("merge-pr"));
        var result = Normalize(input); var original = result.ToJsonString();
        var status = new JsonObject { ["state"] = "succeeded", ["exitCode"] = 0 };
        var projected = WorkflowResult.WithProposalAvailability(result, TaskContext(), status);
        var actions = projected["nextActions"]!.AsArray().OfType<JsonObject>().ToArray();
        Check(actions.Length == input["nextActions"]!.AsArray().Count && result.ToJsonString() == original,
            "Eligibility must retain every model proposal, on a clone that does not rewrite saved evidence.");
        foreach (var kind in new[] { "approve", "merge-pr" })
        {
            var unavailable = actions.Single(row => Text(row, "kind") == kind)["availability"]!.AsObject();
            Check(unavailable["enabled"]?.GetValue<bool>() == false && unavailable["reasons"]!.AsArray().Any(node => node!.GetValue<string>().Contains("high or medium", StringComparison.Ordinal)),
                "Approve and Merge share a visible reason for unresolved high or medium findings.");
        }
        var comment = actions.Single(row => Text(row, "kind") == "comment");
        Check(comment["availability"]!["enabled"]?.GetValue<bool>() == true, "The factual report remains available when a review finds defects.");
        foreach (var code in new JsonNode?[] { null, JsonValue.Create(7), JsonValue.Create("0") })
        {
            var incompleteStatus = new JsonObject { ["state"] = "succeeded", ["exitCode"] = code?.DeepClone() };
            var availability = WorkflowResult.ProposalAvailability(result, TaskContext(), incompleteStatus, comment);
            Check(availability["enabled"]?.GetValue<bool>() == false && availability["reasons"]!.AsArray().Count > 0,
                "An absent, nonzero or incorrectly typed actual process exit code blocks publication consistently.");
        }
        var active = WorkflowResult.ProposalAvailability(result, TaskContext(), new JsonObject { ["state"] = "running", ["exitCode"] = 0 }, comment);
        Check(active["enabled"]?.GetValue<bool>() == false, "A running task cannot publish even with a misleading exit-code field.");
        var forged = comment.DeepClone().AsObject(); forged["body"] = "A different unpublished proposal";
        Check(WorkflowResult.ProposalAvailability(result, TaskContext(), status, forged)["enabled"]?.GetValue<bool>() == false,
            "A valid proposal ID cannot authorize a different proposed payload.");
        var identities = actions.Select(row => Text(row, "proposalId")).ToArray();
        foreach (var row in actions) row["availability"] = new JsonObject { ["enabled"] = true, ["reasons"] = new JsonArray("Invented remote state") };
        var reprojected = WorkflowResult.WithProposalAvailability(projected, TaskContext(), status);
        Check(reprojected["nextActions"]!.AsArray().OfType<JsonObject>().Select(row => Text(row, "proposalId")).SequenceEqual(identities),
            "Proposal IDs ignore changing Host availability metadata.");
        Check(reprojected["nextActions"]!.AsArray().OfType<JsonObject>().Single(row => Text(row, "kind") == "approve")["availability"]!["enabled"]?.GetValue<bool>() == false,
            "Host eligibility is recomputed instead of trusting caller or stale display metadata.");
        var invalid = input.DeepClone().AsObject(); invalid["nextActions"]![0]!["availability"] = new JsonObject { ["enabled"] = true, ["reasons"] = new JsonArray() };
        Invalid(invalid, "model-supplied host availability");
        var recovered = WorkflowResult.RecoverExisting(reprojected, "interrupted", "WORKER_INTERRUPTED", "Worker exited.", TaskContext());
        Check(!HasDiagnostic(recovered, "INVALID_RESULT") && recovered["findings"]!.AsArray().Count == 1,
            "Recovery removes Host projection metadata before validating and retaining the original structured evidence.");
    }

    private static void CompletedReviewCanReportUntestedProductAcceptance()
    {
        var input = Model();
        input["summary"] = "Code review and focused reviewer checks completed; CJK startup acceptance was outside this review's mandatory scope and was not exercised.";
        input["assessment"] = Assessment("inconclusive");
        input["assessment"]!["summary"] = "The reviewed code has no confirmed actionable finding, but this environment did not verify the CJK product claim.";
        input["validation"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "cjk-acceptance", ["name"] = "CJK startup acceptance", ["status"] = "not_run", ["required"] = false,
            ["details"] = "The defined source-review checks ran. The broader locale/elevation matrix needs evidence from an affected environment.",
            ["evidence"] = new JsonArray("Recorded English-only startup observation; no CJK result is claimed.")
        });
        input["diagnostics"] = new JsonArray(Diagnostic("REVIEW_COVERAGE_LIMITED", "warning", "The CJK startup fix remains unverified; obtain affected-environment evidence."));
        input["nextActions"]!.AsArray().Add(Next("merge-pr"));
        input["nextActions"]!.AsArray().OfType<JsonObject>().Single(action => Text(action, "kind") == "comment")["body"] = "Could the author provide startup results, exact language/elevation settings and a recording or log for the reviewed revision?";
        var before = input.ToJsonString();
        var task = TaskContext();
        var normalized = Normalize(input, task);
        Check(Text(normalized, "outcome") == "completed" && Text(normalized["assessment"]!.AsObject(), "status") == "inconclusive" && normalized["needsReview"]?.GetValue<bool>() == true,
            "Completed review work can retain an inconclusive product assessment without becoming a failed execution.");
        Check(!HasDiagnostic(normalized, "WORKFLOW_CHECKS_INCOMPLETE") && normalized["blockers"]!.AsArray().Count == 0 &&
            normalized["validation"]!.AsArray().OfType<JsonObject>().Single(check => Text(check, "id") == "cjk-acceptance")["status"]!.GetValue<string>() == "not_run",
            "Untested product acceptance remains explicitly untested rather than becoming a passing test or a mandatory workflow blocker.");
        Check(WorkflowResult.CanPublish(normalized, task, "comment") && !WorkflowResult.CanPublish(normalized, task, "approve") && !WorkflowResult.CanPublish(normalized, task, "merge-pr"),
            "Coverage-limited review supports an author-evidence comment while ordinary approval and merge remain unavailable.");
        Check(WorkflowResult.IsValidStoredV2(WorkflowResult.WithProposalAvailability(normalized, task, new JsonObject { ["state"] = "succeeded", ["exitCode"] = 0 })),
            "Saved review evidence remains valid after Host eligibility metadata is projected.");
        var malformed = normalized.DeepClone().AsObject(); malformed["validation"] = "not structured";
        Check(!WorkflowResult.IsValidStoredV2(malformed), "Display metadata cannot make malformed saved review evidence valid.");

        var mandatory = input.DeepClone().AsObject();
        mandatory["validation"]!.AsArray().OfType<JsonObject>().Single(check => Text(check, "id") == "cjk-acceptance")["required"] = true;
        mandatory["diagnostics"] = new JsonArray(Diagnostic("REQUIRED_ACCEPTANCE_UNAVAILABLE", "error", "The task explicitly requires CJK acceptance, which was not executed."));
        var blocked = Normalize(mandatory, task);
        Check(Text(blocked, "outcome") == "blocked" && HasDiagnostic(blocked, "WORKFLOW_CHECKS_INCOMPLETE"),
            "An explicitly mandatory unexecuted acceptance check is never silently downgraded to optional or completed.");
        var unfinishedReview = input.DeepClone().AsObject();
        unfinishedReview["validation"]!.AsArray().OfType<JsonObject>().Single(check => Text(check, "id") == "local-review")["status"] = "not_run";
        Check(Text(Normalize(unfinishedReview, task), "outcome") == "blocked", "A product coverage warning does not excuse unfinished code review.");
        Check(input.ToJsonString() == before, "Review interpretation preserves the input evidence and its original classification.");
    }

    private static void SourceShaAndSuggestionAssociationsAreBound()
    {
        var issue = Model("issue-fix"); issue["assessment"] = Assessment("passed", "local-candidate");
        var create = new JsonObject { ["kind"] = "create-pr", ["reason"] = "Publish the verified candidate.", ["body"] = "", ["pullRequest"] = new JsonObject
        { ["head"] = "fixture:reviewed-fix", ["sourceHeadSha"] = new string('a', 40), ["base"] = "main", ["title"] = "Fix the reproduced issue", ["body"] = "Verified candidate.", ["draft"] = true } };
        issue["nextActions"] = new JsonArray(create);
        var task = TaskContext("issue-fix");
        Check(WorkflowResult.CanPublish(Normalize(issue, task), task, "create-pr"), "Create PR can propose exactly the remote source SHA that was assessed.");
        create["pullRequest"]!["sourceHeadSha"] = new string('b', 40);
        var drifted = Normalize(issue, task);
        Check(Kinds(drifted).Contains("create-pr") && !WorkflowResult.CanPublish(drifted, task, "create-pr"), "A source SHA that differs from tested candidate evidence remains visible but unavailable.");
        create["pullRequest"]!.AsObject().Remove("sourceHeadSha");
        var historical = Normalize(issue, task);
        Check(!HasDiagnostic(historical, "INVALID_RESULT") && Kinds(historical).Contains("create-pr") && !WorkflowResult.CanPublish(historical, task, "create-pr"),
            "Historical v2 Create PR proposals without a source SHA remain readable but cannot publish unbound code.");
        create["pullRequest"]!["sourceHeadSha"] = "invalid"; Invalid(issue, "invalid create-pr source SHA");
        var review = Model();
        review["review"]!["suggestions"] = new JsonArray(Suggestion("first", 12), Suggestion("second", 24));
        review["nextActions"] = new JsonArray(Next("requestChanges"), Next("requestChanges"));
        review["nextActions"]![0]!["suggestionIds"] = new JsonArray("first");
        review["nextActions"]![1]!["suggestionIds"] = new JsonArray("second");
        var separate = Normalize(review);
        Check(!HasDiagnostic(separate, "INVALID_RESULT") && separate["nextActions"]!.AsArray().Count == 2 &&
            !string.Equals(Text(separate["nextActions"]![0]!.AsObject(), "proposalId"), Text(separate["nextActions"]![1]!.AsObject(), "proposalId"), StringComparison.Ordinal),
            "Distinct review proposals retain stable identities and their own suggestion associations.");
        review["nextActions"]![0]!["suggestionIds"] = new JsonArray("unknown"); Invalid(review, "missing referenced suggestion");
        review["nextActions"]![0]!["suggestionIds"] = new JsonArray("first", "first"); Invalid(review, "duplicate suggestion association");
        review["nextActions"]![0]!["suggestionIds"] = new JsonArray("first"); review["review"]!["suggestions"]![1]!["id"] = "first";
        Invalid(review, "duplicate suggestion ID");
        review = Model(); review["review"]!["suggestions"] = new JsonArray(Suggestion("old", 12)); review["review"]!["suggestions"]![0]!.AsObject().Remove("id");
        Check(!HasDiagnostic(Normalize(review), "INVALID_RESULT"), "Historical v2 review suggestions without IDs retain their original compatibility.");
    }

    private static JsonObject Assessment(string status, string subject = "original-pr", string? revision = null) => new()
    { ["subject"] = subject, ["status"] = status, ["summary"] = "Observed product behavior in the verified scope.", ["revisionSha"] = revision ?? new string('a', 40) };
    private static JsonObject Suggestion(string id, int line) => new()
    { ["id"] = id, ["path"] = "src/example.cs", ["line"] = line, ["startLine"] = line, ["side"] = "RIGHT", ["body"] = "Use the verified repair.", ["replacement"] = "fixed();" };

    private static void LegacyResultsRemainLegacy()
    {
        var legacy = new JsonObject
        {
            ["summary"] = "Historical result remains readable.", ["needsReview"] = true,
            ["artifacts"] = new JsonArray(new JsonObject { ["path"] = "legacy.patch", ["label"] = "Legacy changes" }),
            ["validation"] = new JsonArray(new JsonObject { ["name"] = "Legacy check", ["status"] = "passed", ["details"] = "Historical evidence." }),
            ["blockers"] = new JsonArray(), ["nextSteps"] = new JsonArray(Next("inspectResult")), ["review"] = null
        };
        var result = Normalize(legacy);
        Check(!result.ContainsKey("schemaVersion") && !result.ContainsKey("outcome") && !result.ContainsKey("findings") &&
            result["validation"]!.AsArray().Count == 1 && result["validation"]![0]!["id"] is null,
            "Valid v1 results must retain their historical shape without synthesized v2 workflow checks.");
        foreach (var field in new[] { "summary", "artifacts", "validation", "blockers", "nextSteps", "review", "needsReview" })
            Check(JsonNode.DeepEquals(result[field], legacy[field]), "Legacy fields must remain compatible: " + field);
        Check(result["structured"]?.GetValue<bool>() == true && result["cliExitCode"]?.GetValue<int>() == 0, "Legacy parsing must retain structured and process metadata.");
        foreach (var version in new int?[] { null, 1 })
        {
            var compatible = WorkflowResult.FromModel(legacy.ToJsonString(), TaskContext(), "succeeded", 0, null, null, expectedSchemaVersion: version);
            Check(!compatible.ContainsKey("schemaVersion") && !compatible.ContainsKey("outcome"), "A genuinely legacy snapshot retains v1 compatibility.");
        }
        foreach (var explicitVersion in new[] { false, true })
        {
            var attemptedDowngrade = (JsonObject)legacy.DeepClone();
            attemptedDowngrade["nextSteps"] = new JsonArray(Next("approve"));
            if (explicitVersion) attemptedDowngrade["schemaVersion"] = 1;
            var blocked = WorkflowResult.FromModel(attemptedDowngrade.ToJsonString(), TaskContext(), "succeeded", 0, null, null, expectedSchemaVersion: 2);
            Check(Text(blocked, "outcome") == "blocked" && HasDiagnostic(blocked, "INVALID_RESULT"), "A new v2 workflow cannot bypass required checks by returning a legacy-shaped result.");
            NoPublishing(blocked, TaskContext());
        }
        var modelAdapter = new CliAdapter("codex");
        modelAdapter.Consume(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = legacy.ToJsonString() } }.ToJsonString());
        Check(Text(modelAdapter.Result("succeeded", 0, null, null, TaskContext(), expectedSchemaVersion: 2), "outcome") == "blocked", "The CLI adapter forwards the accepted schema version instead of allowing a result downgrade.");
    }

    private static void FailuresUseTheCanonicalEnvelope()
    {
        foreach (var state in new[] { "failed", "cancelled", "interrupted" })
        {
            var task = TaskContext("issue-fix");
            var result = WorkflowResult.Failure(state, "REPO_NOT_CONFIGURED", "Choose the repository folder.", task, null, "setup");
            Check(result["schemaVersion"]?.GetValue<int>() == 2 && result["structured"]?.GetValue<bool>() == true && Text(result, "outcome") == state && Text(result, "phase") == "setup",
                "Host pre-launch failures must use the canonical v2 envelope and actual terminal outcome.");
            Check(Fields.All(result.ContainsKey) && HasDiagnostic(result, "REPO_NOT_CONFIGURED") && result["cliExitCode"] is null,
                "Canonical Host failures must retain the known diagnostic and unknown actual exit code.");
            Check(result["validation"]!.AsArray().OfType<JsonObject>().All(value => Text(value, "status") == "not_run") && result["artifacts"]!.AsArray().Count == 0,
                "A setup failure must not manufacture passed checks or produced artifacts.");
            Check(result["blockers"]!.AsArray().Any(value => value?.GetValue<string>() == "Choose the repository folder.") && JsonNode.DeepEquals(result["nextActions"], result["nextSteps"]),
                "Failure diagnostics and recovery actions must remain visible to legacy clients.");
            NoPublishing(result, task);
        }
    }

    private static void RecoveryRetainsExistingEvidence()
    {
        var task = TaskContext();
        var completed = Normalize(Model(), task);
        completed["rawOutput"] = "Retained original diagnostic output.";
        var original = completed.ToJsonString();
        var recovered = WorkflowResult.RecoverExisting(completed, "interrupted", "WORKER_INTERRUPTED", "Worker stopped after saving its result.", task);
        Check(Text(recovered, "outcome") == "interrupted" && Text(recovered, "summary") == Text(completed, "summary") &&
            JsonNode.DeepEquals(recovered["findings"], completed["findings"]) && JsonNode.DeepEquals(recovered["artifacts"], completed["artifacts"]) &&
            JsonNode.DeepEquals(recovered["validation"], completed["validation"]) && Text(recovered, "rawOutput") == Text(completed, "rawOutput"),
            "Recovery must preserve valid findings, artifacts, checks, summary and original diagnostic output while forcing the observed process outcome.");
        Check(HasDiagnostic(recovered, "WORKER_INTERRUPTED") && completed.ToJsonString() == original, "Recovery must add its actual diagnostic on a clone without mutating the earlier evidence record.");
        NoPublishing(recovered, task);
        var legacy = new JsonObject
        {
            ["summary"] = "Historical evidence", ["needsReview"] = false,
            ["artifacts"] = new JsonArray(new JsonObject { ["path"] = "retained.patch", ["label"] = "Patch" }),
            ["validation"] = new JsonArray(new JsonObject { ["name"] = "Old check", ["status"] = "passed", ["details"] = "Previously observed." }),
            ["blockers"] = new JsonArray(), ["nextSteps"] = new JsonArray(Next("comment")), ["review"] = null, ["rawOutput"] = "Original legacy output"
        };
        var legacyRecovered = WorkflowResult.RecoverExisting(legacy, "failed", "CLI_EXECUTION_FAILED", "Observed failure.", task, 7);
        Check(Text(legacyRecovered, "outcome") == "failed" && legacyRecovered["cliExitCode"]?.GetValue<int>() == 7 &&
            JsonNode.DeepEquals(legacyRecovered["artifacts"], legacy["artifacts"]) && JsonNode.DeepEquals(legacyRecovered["validation"], legacy["validation"]) &&
            Text(legacyRecovered, "rawOutput") == "Original legacy output" && legacyRecovered["nextSteps"]!.AsArray().OfType<JsonObject>().All(row => !RemoteKinds.Contains(Text(row, "kind"))),
            "Legacy recovery must preserve its evidence and real exit code while replacing publication proposals with recovery actions.");
    }

    private static JsonObject Model(string action = "pr-review") => new()
    {
        ["schemaVersion"] = 2, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "The requested local workflow completed.",
        ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(), ["validation"] = new JsonArray(RequiredChecks[action].Select(id => (JsonNode?)Validation(id)).ToArray()),
        ["diagnostics"] = new JsonArray(), ["nextActions"] = new JsonArray(RemoteKinds.Select(kind => (JsonNode?)Next(kind)).Append(Next("inspectResult")).ToArray()),
        ["review"] = action == "pr-review" ? new JsonObject { ["headSha"] = new string('a', 40), ["body"] = "Local review evidence.", ["suggestions"] = new JsonArray() } : null,
        ["needsReview"] = false
    };
    private static JsonObject TaskContext(string action = "pr-review") => new()
    {
        ["actionKind"] = action, ["repository"] = "microsoft/powertoys", ["actionId"] = "workflow-fixture", ["requestId"] = "workflow-fixture",
        ["prompt"] = "Complete the local fixture workflow.", ["target"] = new JsonObject { ["type"] = action is "pr-review" or "e2e" ? "pr" : "issue", ["number"] = 42 },
        ["expectedHeadSha"] = action is "pr-review" or "e2e" ? new string('a', 40) : null
    };
    private static JsonObject Finding(string id, string status = "open", string severity = "medium") => new()
    {
        ["id"] = id, ["title"] = "A local finding", ["severity"] = severity, ["status"] = status, ["path"] = "src/example.cs", ["line"] = 12,
        ["details"] = "Observed from local source evidence.", ["evidence"] = new JsonArray("Source inspection at the stated line.")
    };
    private static JsonObject Validation(string id, string status = "passed", bool required = true) => new()
    {
        ["id"] = id, ["name"] = "Check " + id, ["status"] = status, ["required"] = required, ["details"] = "Recorded local evidence.", ["evidence"] = new JsonArray("Fixture observation.")
    };
    private static JsonObject Diagnostic(string code, string severity = "error", string message = "A workflow diagnostic.") => new()
    { ["code"] = code, ["severity"] = severity, ["message"] = message, ["recovery"] = "inspectResult" };
    private static JsonObject Next(string kind) => new() { ["kind"] = kind, ["reason"] = "Inspect the recorded workflow evidence.", ["body"] = "" };
    private static JsonArray Repeat(int count, Func<int, JsonNode?> item) => new(Enumerable.Range(0, count).Select(item).ToArray());
    private static JsonObject Normalize(JsonObject input, JsonObject? task = null, string state = "succeeded", int? exit = 0, string? code = null, string? message = null, bool outputComplete = true) =>
        WorkflowResult.FromModel(input.ToJsonString(), task ?? TaskContext(), state, exit, code, message, outputComplete);
    private static string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
    private static bool HasDiagnostic(JsonObject result, string code) => result["diagnostics"]?.AsArray().OfType<JsonObject>().Any(value => Text(value, "code") == code) == true;
    private static HashSet<string> Kinds(JsonObject result) => result["nextActions"]!.AsArray().OfType<JsonObject>().Select(value => Text(value, "kind")).ToHashSet(StringComparer.Ordinal);
    private static void NoPublishing(JsonObject result, JsonObject task, bool allowReports = false)
    {
        foreach (var kind in RemoteKinds.Where(kind => !allowReports || kind != "comment")) Check(!WorkflowResult.CanPublish(result, task, kind), "Incomplete workflows must not enable publishing action " + kind);
        Check(JsonNode.DeepEquals(result["nextActions"], result["nextSteps"]), "Legacy nextSteps must retain the same proposed actions without destructive filtering.");
    }
    private static void Invalid(JsonObject input, string description)
    {
        var result = Normalize(input);
        Check(result["schemaVersion"]?.GetValue<int>() == 2 && result["structured"]?.GetValue<bool>() == true && Text(result, "outcome") == "blocked" &&
            HasDiagnostic(result, "INVALID_RESULT") && !string.IsNullOrEmpty(Text(result, "rawOutput")), "Invalid v2 output must become inspectable canonical blocked diagnostics: " + description);
        NoPublishing(result, TaskContext());
    }
    private static void Check(bool condition, string message) => CoreScenarios.Check(condition, message);
}
