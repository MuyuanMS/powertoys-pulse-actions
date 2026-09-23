using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class WorkflowV3Scenarios
{
    internal static readonly string Revision = new('a', 40);

    internal static Task RunAllAsync()
    {
        SchemaAndCompleteFindings();
        ReportAndPriorityAdvice();
        ManualActionsAreIndependent();
        InvestigationConclusionsAndPlanLinks();
        VersionBoundariesAndRecovery();
        Console.WriteLine("PASS v3 reports: complete unsliced findings, explicit E2E necessity, P0-only manual gate, investigation conclusions, plan references, version preservation and failures (offline)");
        return Task.CompletedTask;
    }

    private static void SchemaAndCompleteFindings()
    {
        var schema = WorkflowResult.SchemaV3(); var properties = schema["properties"]!.AsObject();
        Check(properties["schemaVersion"]?["const"]?.GetValue<int>() == 3 && !properties.ContainsKey("verificationRecommendation") &&
            properties["findings"]?["maxItems"] is null, "V3 uses one E2E source and does not specify a Top N finding limit.");
        foreach (var field in new[] { "report", "e2eAssessment", "featureAssessment", "bugAssessment", "plans" })
            Check(properties.ContainsKey(field) && schema["required"]!.AsArray().Any(value => value?.GetValue<string>() == field), "V3 describes every new report field.");
        var input = Model(); var findings = input["findings"]!.AsArray();
        for (var index = 0; index < 640; index++) findings.Add(Finding("finding-" + index, index == 639 ? "P0" : "P2"));
        Check(System.Text.Encoding.UTF8.GetByteCount(input.ToJsonString()) > ResultLimits.MaximumModelBytes, "The complete report exercises the separate v3 byte budget.");
        var result = Normalize(input); Check(WorkflowResult.IsValidStoredV3(result) && result["findings"]!.AsArray().Count == 640 && WorkflowResult.HasConfirmedP0(result, TaskContext()),
            "All findings survive normalization, including the last confirmed P0 beyond the former count and byte boundaries.");
        var uncertainCause = Model(); uncertainCause["findings"]!.AsArray().Add(Finding("proved-defect"));
        uncertainCause["findings"]![0]!["rootCause"] = "The observable defect is proved; the exact allocation failure cause is not yet established.";
        Check(WorkflowResult.IsValidStoredV3(Normalize(uncertainCause)), "Known defect evidence remains valid when its root cause has explicit uncertainty.");
        var invalid = uncertainCause.DeepClone().AsObject(); invalid["findings"]![0]!["evidence"] = new JsonArray(); Invalid(invalid, "A confirmed finding needs evidence.");
        invalid = uncertainCause.DeepClone().AsObject(); invalid["findings"]![0]!["feedback"]!["suggestionId"] = "missing-suggestion"; Invalid(invalid, "Finding feedback must reference an existing exact suggestion.");
        invalid = uncertainCause.DeepClone().AsObject(); invalid["findings"]![0]!["status"] = "unverified"; Invalid(invalid, "An unverified hypothesis cannot be marked confirmed.");
        var suggestion = Model(); suggestion["review"]!["suggestions"]!.AsArray().Add(new JsonObject { ["id"] = "replacement-1", ["path"] = "src/example.cs", ["line"] = 12,
            ["startLine"] = 12, ["side"] = "RIGHT", ["body"] = "Use the checked value.", ["replacement"] = "return checkedValue;" });
        suggestion["findings"]!.AsArray().Add(Finding("with-suggestion")); suggestion["findings"]![0]!["feedback"]!["suggestionId"] = "replacement-1";
        Check(WorkflowResult.IsValidStoredV3(Normalize(suggestion)), "Each finding can bind its comment and optional code replacement explicitly.");
    }

    private static void ReportAndPriorityAdvice()
    {
        foreach (var priority in new[] { "P0", "P1", "P2", "P3" })
        {
            var input = Model(); input["findings"]!.AsArray().Add(Finding("priority", priority)); var result = Normalize(input);
            Check(WorkflowResult.Recommendation(result, TaskContext())["kind"]?.GetValue<string>() == (priority is "P0" or "P1" ? "address-findings" : "approve"),
                "Default advice distinguishes severe feedback from nonblocking findings.");
        }
        foreach (var level in new[] { "not_needed", "recommended", "required" })
        {
            var input = Model(); input["e2eAssessment"] = E2e(level); var result = Normalize(input);
            Check(WorkflowResult.IsFinalReportComplete(result) && WorkflowResult.Recommendation(result, TaskContext())["kind"]?.GetValue<string>() == (level == "required" ? "run-e2e" : "approve"),
                "E2E necessity affects advice without turning a completed static review into a failure.");
        }
        var supplemented = Normalize(ModelWithE2e("required")); var original = supplemented.ToJsonString();
        supplemented["e2eEvidenceComplete"] = true;
        Check(WorkflowResult.Recommendation(supplemented, TaskContext())["kind"]?.GetValue<string>() == "approve" && supplemented["e2eAssessment"]?["level"]?.GetValue<string>() == "required",
            "Host-verified supplemental coverage changes advice without rewriting original E2E necessity.");
        var spoofed = ModelWithE2e("required"); spoofed["e2eEvidenceComplete"] = true; Invalid(spoofed, "The model cannot provide Host-only supplemented-evidence metadata.");
        Check(original.Contains("required", StringComparison.Ordinal), "The original report remains a separate immutable value.");
        var noCoverage = Model(); noCoverage["report"]!["coverage"] = new JsonArray(); var incomplete = Normalize(noCoverage);
        Check(!WorkflowResult.IsFinalReportComplete(incomplete) && WorkflowResult.Recommendation(incomplete, TaskContext())["kind"]?.GetValue<string>() == "incomplete", "Unrecorded coverage cannot claim a complete report.");
        var notRechecked = Model(); notRechecked["report"]!["rechecked"] = false;
        Check(!WorkflowResult.IsFinalReportComplete(Normalize(notRechecked)), "A loop result must report completed rechecking.");
        foreach (var kind in new[] { "pr-review", "bug-investigation" })
        {
            var unconfirmed = Model(kind); var hypothesis = Finding("pending-hypothesis"); hypothesis["confirmed"] = false; hypothesis["status"] = "unverified";
            unconfirmed["findings"]!.AsArray().Add(hypothesis); var before = unconfirmed.ToJsonString(); var report = Normalize(unconfirmed, TaskContext(kind));
            Check(!WorkflowResult.IsFinalReportComplete(report) && report["report"]?["rechecked"]?.GetValue<bool>() == false &&
                report["diagnostics"]!.AsArray().OfType<JsonObject>().Any(row => row["code"]?.GetValue<string>() == "FINAL_FINDINGS_NOT_RECHECKED") &&
                JsonNode.DeepEquals(report["findings"], unconfirmed["findings"]) && unconfirmed.ToJsonString() == before,
                "Unconfirmed final findings keep the review/investigation incomplete without losing evidence or mutating the input.");
        }
        var missingE2e = Model(); missingE2e["e2eAssessment"] = null;
        Check(!WorkflowResult.IsFinalReportComplete(Normalize(missingE2e)), "Missing E2E assessment is not interpreted as unnecessary.");
    }

    private static void ManualActionsAreIndependent()
    {
        foreach (var state in new[] { "succeeded", "failed", "cancelled", "interrupted" })
        {
            var input = ModelWithE2e("required"); input["findings"]!.AsArray().Add(Finding("p1", "P1")); input["nextActions"] = new JsonArray();
            var result = WorkflowResult.FromModel(input.ToJsonString(), TaskContext(), state, state == "succeeded" ? 0 : 7, state == "succeeded" ? null : "CLI_EXECUTION_FAILED", null, expectedSchemaVersion: 3);
            foreach (var kind in new[] { "approve", "comment", "suggestChanges", "requestChanges", "close", "merge-pr", "trigger-ci" })
                Check(WorkflowResult.CanPublishManualPr(result, TaskContext(), kind), "Manual PR choices remain present independently of report/lifecycle/AI proposal: " + state + " " + kind);
        }
        Check(WorkflowResult.CanPublishManualPr(null, TaskContext(), "approve"), "No final AI report is not an extra manual-approval prohibition.");
        var p0 = Model(); p0["findings"]!.AsArray().Add(Finding("p0", "P0")); var known = Normalize(p0);
        Check(!WorkflowResult.CanPublishManualPr(known, TaskContext(), "approve") && WorkflowResult.CanPublishManualPr(known, TaskContext(), "merge-pr"), "The new manual business gate applies only to Approve with confirmed P0.");
        var other = TaskContext(); other["expectedHeadSha"] = new string('b', 40);
        Check(!WorkflowResult.HasConfirmedP0(known, other), "A finding for a different revision is not a confirmed current P0.");
        var repairedCandidate = p0.DeepClone().AsObject(); repairedCandidate["assessment"]!["subject"] = "local-candidate";
        Check(WorkflowResult.HasConfirmedP0(Normalize(repairedCandidate), TaskContext()), "Passing candidate tests cannot erase a confirmed P0 in the explicitly bound original-PR code review.");
        repairedCandidate["findings"]![0]!["status"] = "fixed"; var reportedCandidate = repairedCandidate.ToJsonString(); var correctedCandidate = Normalize(repairedCandidate);
        Check(!WorkflowResult.IsFinalReportComplete(correctedCandidate) && correctedCandidate["findings"]![0]!["status"]?.GetValue<string>() == "open" &&
            WorkflowResult.HasConfirmedP0(correctedCandidate, TaskContext()) && !WorkflowResult.CanPublishManualPr(correctedCandidate, TaskContext(), "approve") &&
            correctedCandidate["diagnostics"]!.AsArray().OfType<JsonObject>().Any(row => row["code"]?.GetValue<string>() == "SOURCE_STATUS_MISMATCH") &&
            correctedCandidate["sourceStatusCorrections"]?[0]?["reportedStatus"]?.GetValue<string>() == "fixed" && repairedCandidate.ToJsonString() == reportedCandidate,
            "A candidate-only fix cannot mark the original PR P0 resolved; the source conflict is explicit and original reported status is retained.");
        var recoveredCandidate = WorkflowResult.RecoverExisting(correctedCandidate, "interrupted", "WORKER_INTERRUPTED", "Worker stopped.", TaskContext());
        Check(JsonNode.DeepEquals(recoveredCandidate["sourceStatusCorrections"], correctedCandidate["sourceStatusCorrections"]) && WorkflowResult.HasConfirmedP0(recoveredCandidate, TaskContext()),
            "Recovery preserves the source correction and confirmed original P0 without depending on successful execution.");
        var candidateOnly = Model("pr-verify"); candidateOnly["assessment"]!["subject"] = "local-candidate"; candidateOnly["findings"]!.AsArray().Add(Finding("candidate-p0", "P0"));
        Check(!WorkflowResult.HasConfirmedP0(Normalize(candidateOnly, TaskContext("pr-verify")), TaskContext("pr-verify")), "A verification-only finding on a local candidate cannot block approval of the original PR.");
        p0["findings"]![0]!["status"] = "fixed"; Check(!WorkflowResult.HasConfirmedP0(Normalize(p0), TaskContext()), "A resolved P0 is not an open blocker.");
        p0["findings"]![0]!["status"] = "unverified"; p0["findings"]![0]!["confirmed"] = false;
        Check(!WorkflowResult.HasConfirmedP0(Normalize(p0), TaskContext()), "An unconfirmed hypothesis cannot be a P0 approval prohibition.");
        var legacy = WorkflowResult.Failure("failed", "CHECK_FAILED", "An old failure.", TaskContext());
        Check(!WorkflowResult.HasConfirmedP0(legacy, TaskContext()) && WorkflowResult.CanPublishManualPr(legacy, TaskContext(), "approve"), "Old severity and execution fields never silently become a P0.");
    }

    private static void InvestigationConclusionsAndPlanLinks()
    {
        var feature = Model("feature-research"); var task = TaskContext("feature-research");
        Check(WorkflowResult.IsFinalReportComplete(Normalize(feature, task)), "A ready feature delivers one combined investigation with a valid plan.");
        foreach (var status in new[] { "ready", "needs_information", "needs_decision", "already_supported", "duplicate", "not_feasible" })
        {
            var model = feature.DeepClone().AsObject(); var assessment = model["featureAssessment"]!.AsObject(); assessment["status"] = status;
            if (status == "duplicate") assessment["relatedIssue"] = RelatedIssue();
            Check(WorkflowResult.IsValidStoredV3(Normalize(model, task)), "Feature conclusion remains structurally supported: " + status);
        }
        var bug = Model("bug-investigation"); var bugTask = TaskContext("bug-investigation");
        foreach (var status in new[] { "confirmed", "needs_information", "needs_verification", "already_fixed", "duplicate", "not_a_bug" })
        {
            var model = bug.DeepClone().AsObject(); var assessment = model["bugAssessment"]!.AsObject(); assessment["status"] = status;
            if (status == "duplicate") assessment["relatedIssue"] = RelatedIssue();
            Check(WorkflowResult.IsValidStoredV3(Normalize(model, bugTask)), "Bug conclusion remains structurally supported: " + status);
        }
        var malformed = feature.DeepClone().AsObject(); malformed["nextActions"]![0]!["planId"] = "missing"; Invalid(malformed, "Start task must reference a saved plan.", task);
        malformed = feature.DeepClone().AsObject(); malformed["nextActions"]![0]!["taskKind"] = "issue-fix"; Invalid(malformed, "The plan kind cannot be replaced by the proposal.", task);
        malformed = feature.DeepClone().AsObject(); malformed["plans"]![0]!["steps"] = new JsonArray(); Invalid(malformed, "An actionable plan needs actual steps.", task);
        var duplicate = feature.DeepClone().AsObject(); duplicate["featureAssessment"]!["status"] = "duplicate"; duplicate["featureAssessment"]!["relatedIssue"] = RelatedIssue();
        duplicate["nextActions"] = new JsonArray(new JsonObject { ["kind"] = "close-as-duplicate", ["reason"] = "Same requested behavior.", ["body"] = "Tracked by the original issue.", ["recommended"] = true, ["duplicateOf"] = RelatedIssue() });
        Check(WorkflowResult.IsValidStoredV3(Normalize(duplicate, task)), "Duplicate proposals retain one explicit immutable original issue.");
        duplicate["nextActions"]![0]!["duplicateOf"]!["url"] = "https://github.com/microsoft/powertoys/issues/999";
        Invalid(duplicate, "The displayed duplicate link must match its repository and number.", task);
    }

    private static void VersionBoundariesAndRecovery()
    {
        var input = Model(); var output = input.ToJsonString();
        Check(WorkflowResult.FromModel(output, TaskContext(), "succeeded", 0, null, null, expectedSchemaVersion: 2)["schemaVersion"]?.GetValue<int>() == 2,
            "A v2 accepted snapshot does not silently switch to v3 output semantics.");
        var old = WorkflowResult.Failure("failed", "CHECK_FAILED", "Old v2 result.", TaskContext());
        foreach (var field in new[] { "structured", "cliExitCode", "blockers", "nextSteps" }) old.Remove(field);
        foreach (var action in old["nextActions"]!.AsArray().OfType<JsonObject>()) action.Remove("proposalId");
        var downgrade = WorkflowResult.FromModel(old.ToJsonString(), TaskContext(), "succeeded", 0, null, null, expectedSchemaVersion: 3);
        Check(downgrade["schemaVersion"]?.GetValue<int>() == 3 && !WorkflowResult.IsFinalReportComplete(downgrade), "New v3 tasks cannot downgrade to an older contract.");
        var valid = Normalize(input); var bytes = valid.ToJsonString(); var recovered = WorkflowResult.RecoverExisting(valid, "interrupted", "WORKER_INTERRUPTED", "Worker stopped.", TaskContext());
        Check(recovered["schemaVersion"]?.GetValue<int>() == 3 && !WorkflowResult.IsFinalReportComplete(recovered) && JsonNode.DeepEquals(recovered["findings"], valid["findings"]) && valid.ToJsonString() == bytes,
            "Recovery preserves all v3 findings and the original record while reporting interruption honestly.");
        var failure = WorkflowResult.Failure("failed", "CLI_NOT_FOUND", "The selected CLI is unavailable.", TaskContext(), null, "setup", expectedSchemaVersion: 3);
        Check(WorkflowResult.IsValidStoredV3(failure) && !WorkflowResult.IsFinalReportComplete(failure), "Prelaunch errors use a canonical incomplete v3 report.");
    }

    internal static JsonObject TaskContext(string actionKind = "pr-review")
    {
        var pr = actionKind is "pr-review" or "pr-verify" or "e2e";
        var task = new JsonObject { ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "v3-fixture", ["actionKind"] = actionKind, ["repository"] = "microsoft/powertoys",
            ["target"] = new JsonObject { ["type"] = pr ? "pr" : "issue", ["number"] = 42 }, ["prompt"] = "Complete the accepted workflow." };
        if (pr) task["expectedHeadSha"] = Revision;
        if (actionKind == "pr-review") task["reviewOptions"] = new JsonObject { ["mode"] = "static" };
        if (actionKind == "pr-verify") task["reviewOptions"] = new JsonObject { ["mode"] = "ui-e2e" };
        return task;
    }

    internal static JsonObject Model(string actionKind = "pr-review")
    {
        var task = TaskContext(actionKind); var review = actionKind == "pr-review";
        var model = new JsonObject
        {
            ["schemaVersion"] = 3, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "The selected workflow and final recheck completed.",
            ["assessment"] = new JsonObject { ["subject"] = task["target"]?["type"]?.GetValue<string>() == "pr" ? "original-pr" : "target", ["status"] = "passed", ["summary"] = "Observed evidence supports the scoped assessment.", ["revisionSha"] = Revision },
            ["reviewConclusion"] = review ? new JsonObject { ["status"] = "no-blocking-findings", ["summary"] = "No confirmed blocking code findings.", ["revisionSha"] = Revision, ["blockingUncertainties"] = new JsonArray() } : null,
            ["verificationEvidence"] = new JsonArray(), ["report"] = new JsonObject { ["complete"] = true, ["rechecked"] = true, ["coverage"] = new JsonArray("All changed paths and relevant callers were examined and rechecked."), ["limitations"] = new JsonArray() },
            ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(), ["validation"] = new JsonArray(WorkflowResult.RequiredChecksForTask(task).Select(id => (JsonNode?)new JsonObject
            { ["id"] = id, ["name"] = id, ["status"] = "passed", ["required"] = true, ["details"] = "The required work was completed and interpreted.", ["evidence"] = new JsonArray("Recorded fixture observation.") }).ToArray()),
            ["diagnostics"] = new JsonArray(), ["nextActions"] = new JsonArray(), ["review"] = review ? new JsonObject { ["headSha"] = Revision, ["body"] = "Reviewed the original PR.", ["suggestions"] = new JsonArray() } : null,
            ["needsReview"] = true, ["e2eAssessment"] = review ? E2e("not_needed") : null, ["featureAssessment"] = null, ["bugAssessment"] = null, ["plans"] = new JsonArray()
        };
        if (actionKind is "feature-research" or "feature-investigate")
        {
            model["plans"]!.AsArray().Add(Plan("feature-implement")); model["nextActions"]!.AsArray().Add(StartTask("feature-implement"));
            model["featureAssessment"] = new JsonObject { ["status"] = "ready", ["summary"] = "The requested feature is understood and can be implemented.", ["reasons"] = new JsonArray("Existing architecture provides the needed extension point."),
                ["evidence"] = new JsonArray("Relevant code and existing capabilities inspected."), ["acceptanceCriteria"] = new JsonArray("The supported user scenario works."), ["questions"] = new JsonArray("Which optional behavior should be the default?"),
                ["alternatives"] = new JsonArray("Keep the existing default and offer an opt-in setting."), ["relatedIssue"] = null, ["planId"] = "plan-1" };
        }
        if (actionKind is "bug-investigation" or "bug-investigate")
        {
            model["findings"]!.AsArray().Add(Finding("confirmed-bug", "P1")); model["plans"]!.AsArray().Add(Plan("issue-fix")); model["nextActions"]!.AsArray().Add(StartTask("issue-fix"));
            model["bugAssessment"] = new JsonObject { ["status"] = "confirmed", ["summary"] = "Source evidence confirms the reported bug.", ["reasons"] = new JsonArray("The failing branch always bypasses initialization."), ["evidence"] = new JsonArray("Relevant branch and callers inspected."),
                ["questions"] = new JsonArray("Which optional runtime configuration was enabled?"), ["relatedIssue"] = null, ["planId"] = "plan-1", ["reproduction"] = new JsonObject { ["status"] = "not_run", ["revisionSha"] = Revision,
                    ["environment"] = "Static source inspection.", ["steps"] = new JsonArray(), ["expected"] = "The initialized value is used.", ["observed"] = "Source proves an uninitialized path.", ["evidence"] = new JsonArray("Inspected source path.") } };
        }
        return model;
    }

    internal static JsonObject Finding(string id, string priority = "P2") => new()
    {
        ["id"] = id, ["title"] = "A verified source defect", ["priority"] = priority, ["status"] = "open", ["confirmed"] = true, ["path"] = "src/example.cs", ["line"] = 12,
        ["details"] = "The changed path returns the incorrect value.", ["impact"] = "The affected request fails.", ["trigger"] = "Input reaches the changed branch.", ["rootCause"] = "The checked value is discarded by the return expression.",
        ["fixSuggestion"] = "Return the checked value and retain the relevant regression coverage.", ["evidence"] = new JsonArray("Inspected changed expression and caller."),
        ["feedback"] = new JsonObject { ["body"] = "Please use the checked value in this branch.", ["suggestionId"] = null }
    };
    internal static JsonObject E2e(string level) => new() { ["level"] = level, ["reason"] = "The evidence identifies the remaining runtime need.", ["question"] = level == "not_needed" ? "" : "Does the affected runtime path behave as intended?",
        ["scenarios"] = level == "not_needed" ? new JsonArray() : new JsonArray("Launch the affected original-revision component."), ["expectedResults"] = level == "not_needed" ? new JsonArray() : new JsonArray("The expected behavior occurs without the reported failure."),
        ["prerequisites"] = new JsonArray(), ["evidence"] = new JsonArray("The exact changed path and existing test coverage were inspected."), ["readiness"] = "ready" };
    internal static JsonObject Plan(string kind) => new() { ["id"] = "plan-1", ["kind"] = kind, ["summary"] = "Implement the supported scoped change.", ["steps"] = new JsonArray("Inspect the saved context and implement the affected behavior."),
        ["acceptanceCriteria"] = new JsonArray("The affected behavior matches the saved requirement."), ["prerequisites"] = new JsonArray(), ["evidence"] = new JsonArray("The parent investigation established the affected path.") };
    internal static JsonObject RelatedIssue() => new() { ["repository"] = "microsoft/powertoys", ["number"] = 21, ["url"] = "https://github.com/microsoft/powertoys/issues/21" };
    private static JsonObject StartTask(string kind) => new() { ["kind"] = "start-task", ["reason"] = "Follow the reviewed plan.", ["body"] = "", ["recommended"] = true, ["taskKind"] = kind, ["planId"] = "plan-1" };
    private static JsonObject ModelWithE2e(string level) { var model = Model(); model["e2eAssessment"] = E2e(level); return model; }
    private static JsonObject Normalize(JsonObject model, JsonObject? task = null) => WorkflowResult.FromModel(model.ToJsonString(), task ?? TaskContext(), "succeeded", 0, null, null, expectedSchemaVersion: 3);
    private static void Invalid(JsonObject input, string message, JsonObject? task = null) => Check(Normalize(input, task)["diagnostics"]!.AsArray().OfType<JsonObject>().Any(row => row["code"]?.GetValue<string>() == "INVALID_RESULT"), message);
    private static void Check(bool condition, string message) => CoreScenarios.Check(condition, message);
}
