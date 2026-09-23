using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

internal static class ReviewFollowUpScenarios
{
    internal static async Task RunAllAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulseReviewFollowUpTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(Path.Combine(root, "data"));
            var parentId = Guid.NewGuid().ToString("D"); var parent = ParentTask();
            store.CreateRun(parentId, parent, new JsonObject(), Protocol.ProductionOrigin);
            var parentResult = Result(parent, recommendation: true);
            Check(parentResult["structured"]?.GetValue<bool>() == true, "The parent fixture uses valid structured review output.");
            SaveResult(store, parentId, parentResult); SaveProvenance(store, parentId, original: true);
            var parentTaskBytes = File.ReadAllBytes(Path.Combine(store.RunDirectory(parentId), "task.json"));
            var parentResultBytes = File.ReadAllBytes(Path.Combine(store.RunDirectory(parentId), "result.json"));
            JsonObject Payload() => new()
            {
                ["parentRunId"] = parentId, ["requestId"] = Guid.NewGuid().ToString("D"),
                ["recommendationId"] = ReviewFollowUps.RecommendationId(parentResult["verificationRecommendation"]!.AsObject())
            };

            var payload = Payload(); payload["execution"] = new JsonObject { ["agent"] = "codex", ["model"] = "chosen-model", ["reasoningEffort"] = "high" };
            var prepared = ReviewFollowUps.Prepare(store, payload); var child = prepared["task"]!.AsObject();
            Check(child["actionKind"]!.GetValue<string>() == "pr-verify" && child["reviewOptions"]!["mode"]!.GetValue<string>() == "ui-e2e", "A recommendation creates verification-only work with its explicit scope.");
            Check(child["expectedHeadSha"]!.GetValue<string>() == Sha && child["followUp"]!["subject"]!.GetValue<string>() == "original-pr", "Supplemental verification freezes the original PR revision and subject.");
            Check(child["followUp"]!["parentResultFingerprint"]!.GetValue<string>() == Protocol.Fingerprint(parentResult), "The link binds the exact saved review report.");
            Check(JsonNode.DeepEquals(ReviewFollowUps.Prepare(store, payload), prepared) && store.RunIds().Count() == 1, "Lost-ack retries reuse the same frozen intent without starting work during preparation.");
            var changed = payload.DeepClone().AsObject(); changed["execution"]!["model"] = "changed";
            Expect("REQUEST_CONFLICT", () => ReviewFollowUps.Prepare(store, changed));
            foreach (var field in new[] { "prompt", "task", "sourceOrigin", "expectedHeadSha", "target", "followUp" })
            {
                var invalid = Payload(); invalid[field] = "caller override";
                Expect("INVALID_REQUEST", () => ReviewFollowUps.Prepare(store, invalid));
            }
            var stale = Payload(); stale["recommendationId"] = new string('0', 64);
            Expect("RECOMMENDATION_CHANGED", () => ReviewFollowUps.Prepare(store, stale));

            var childId = Guid.NewGuid().ToString("D"); store.CreateRun(childId, child, new JsonObject(), Protocol.ProductionOrigin);
            var childResult = Result(child, recommendation: false); SaveResult(store, childId, childResult); SaveProvenance(store, childId, original: true);
            var related = ReviewFollowUps.Related(store, new JsonObject { ["runId"] = parentId });
            Check(related["runs"]!.AsArray().Count == 1 && related["evidence"]!.AsArray().Count == 1 && related["currentConclusion"]!["status"]!.GetValue<string>() == "evidence-added", "Same-source supplemental verification contributes attributed evidence to the review.");
            Check(related["evidence"]![0]!["runId"]!.GetValue<string>() == childId && related["evidence"]![0]!["source"]!.GetValue<string>() == "prior-run", "Aggregation assigns the actual run identity instead of trusting a model-supplied source.");
            var effective = ReviewFollowUps.EffectiveResult(store, parentId, parentResult);
            Check(effective["assessment"]!["status"]!.GetValue<string>() == "passed" && parentResult["assessment"]!["status"]!.GetValue<string>() == "inconclusive", "Current action eligibility can use compatible completed evidence without mutating the original inconclusive report.");
            var importantUncertainty = parentResult.DeepClone().AsObject(); importantUncertainty["reviewConclusion"]!["blockingUncertainties"]!.AsArray().Add("A remaining code-level doubt");
            Check(ReviewFollowUps.EffectiveResult(store, parentId, importantUncertainty)["assessment"]!["status"]!.GetValue<string>() == "inconclusive", "Supplemental runtime success does not silently erase an unresolved code-review question.");
            Check(ReviewFollowUps.Related(store, new JsonObject { ["runId"] = childId })["parentRunId"]!.GetValue<string>() == parentId, "A verification detail can navigate to its parent review.");

            SaveProvenance(store, childId, original: false);
            related = ReviewFollowUps.Related(store, new JsonObject { ["runId"] = parentId });
            Check(related["evidence"]!.AsArray().Count == 0 && related["runs"]![0]!["compatibility"]!["eligible"]!.GetValue<bool>() == false, "Tests on a dirty candidate are visible but never fill the original PR evidence gap.");
            store.WriteJson(Path.Combine(store.RunDirectory(childId), "provenance.json"), new JsonObject { ["source"] = "model", ["subject"] = "original-pr" });
            Check(ReviewFollowUps.Related(store, new JsonObject { ["runId"] = parentId })["evidence"]!.AsArray().Count == 0, "Self-reported original-subject labels cannot replace Host provenance.");
            SaveProvenance(store, childId, original: true);
            childResult["verificationEvidence"]![0]!["subject"] = "local-candidate";
            SaveResult(store, childId, childResult);
            Check(ReviewFollowUps.Related(store, new JsonObject { ["runId"] = parentId })["evidence"]!.AsArray().Count == 0, "A local-candidate model assessment remains separate even when Git boundaries are clean.");
            childResult["verificationEvidence"]![0]!["subject"] = "original-pr";
            childResult["verificationEvidence"]![0]!["status"] = "failed";
            var passingRow = childResult["verificationEvidence"]![0]!.DeepClone().AsObject(); passingRow["id"] = "runtime-passed"; passingRow["status"] = "passed";
            childResult["verificationEvidence"]!.AsArray().Add(passingRow);
            childResult["assessment"]!["status"] = "passed";
            SaveResult(store, childId, childResult);
            var conflicting = ReviewFollowUps.EffectiveResult(store, parentId, parentResult);
            Check(conflicting["assessment"]!["status"]!.GetValue<string>() == "inconclusive" && conflicting["verificationEvidence"]!.AsArray().OfType<JsonObject>().Any(row => row["status"]!.GetValue<string>() == "failed"), "Mixed passing and failing linked evidence cannot promote the parent or bypass its failed-evidence approval guard, even if assessment prose says passed.");
            childResult["verificationEvidence"]!.AsArray().RemoveAt(1);
            childResult["assessment"]!["status"] = "inconclusive";
            SaveResult(store, childId, childResult);
            Check(ReviewFollowUps.Related(store, new JsonObject { ["runId"] = parentId })["currentConclusion"]!["status"]!.GetValue<string>() != "changes-requested", "Infrastructure or unexplained test failure alone does not become a confirmed product defect.");
            childResult["assessment"]!["status"] = "failed";
            SaveResult(store, childId, childResult);
            Check(ReviewFollowUps.Related(store, new JsonObject { ["runId"] = parentId })["currentConclusion"]!["status"]!.GetValue<string>() == "changes-requested", "A verified product failure updates the current recommendation instead of being hidden as a failed task.");
            Check(ReviewFollowUps.EffectiveResult(store, parentId, parentResult)["assessment"]!["status"]!.GetValue<string>() == "failed", "Compatible failing product evidence reaches the current ordinary action gate.");
            SaveResult(store, childId, childResult, state: "failed", exitCode: 1);
            related = ReviewFollowUps.Related(store, new JsonObject { ["runId"] = parentId });
            Check(related["evidence"]!.AsArray().Count == 0 && related["currentConclusion"]!["status"]!.GetValue<string>() == "verification-incomplete", "A nonzero CLI exit cannot contribute trusted supplemental evidence.");

            var missingId = Guid.NewGuid().ToString("D"); var missingTask = ParentTask();
            store.CreateRun(missingId, missingTask, new JsonObject(), Protocol.ProductionOrigin);
            var missingResult = Result(missingTask, recommendation: true); missingResult["verificationRecommendation"]!["readiness"] = "missing-prerequisites";
            SaveResult(store, missingId, missingResult);
            Expect("VERIFICATION_PREREQUISITES_MISSING", () => ReviewFollowUps.Prepare(store, new JsonObject
            {
                ["parentRunId"] = missingId, ["requestId"] = Guid.NewGuid().ToString("D"),
                ["recommendationId"] = ReviewFollowUps.RecommendationId(missingResult["verificationRecommendation"]!.AsObject())
            }));
            var readyPayload = new JsonObject
            {
                ["parentRunId"] = missingId, ["requestId"] = Guid.NewGuid().ToString("D"),
                ["recommendationId"] = ReviewFollowUps.RecommendationId(missingResult["verificationRecommendation"]!.AsObject()),
                ["prerequisitesConfirmed"] = true
            };
            var nowReady = ReviewFollowUps.Prepare(store, readyPayload);
            Check(nowReady["task"]!["context"]!["reviewVerification"]!["prerequisitesReportedReady"]!.GetValue<bool>(), "User-confirmed changed prerequisites allow a new setup check without changing the old review or rerunning its code analysis.");
            readyPayload["prerequisitesConfirmed"] = false;
            Expect("REQUEST_CONFLICT", () => ReviewFollowUps.Prepare(store, readyPayload));

            var largeId = Guid.NewGuid().ToString("D"); var largeTask = ParentTask();
            store.CreateRun(largeId, largeTask, new JsonObject(), Protocol.ProductionOrigin);
            var largeResult = Result(largeTask, recommendation: true);
            largeResult["summary"] = new string('审', 32768);
            largeResult["verificationRecommendation"]!["scenarios"] = new JsonArray(Enumerable.Range(0, 7).Select(_ => (JsonNode?)JsonValue.Create(new string('测', 512))).ToArray());
            Check(WorkflowResult.IsValidStoredV2(largeResult), "The near-budget multilingual recommendation is valid.");
            SaveResult(store, largeId, largeResult);
            var largePrepared = ReviewFollowUps.Prepare(store, new JsonObject
            {
                ["parentRunId"] = largeId, ["requestId"] = Guid.NewGuid().ToString("D"),
                ["recommendationId"] = ReviewFollowUps.RecommendationId(largeResult["verificationRecommendation"]!.AsObject())
            });
            Check(JsonNode.DeepEquals(largePrepared["task"]!["context"]!["reviewVerification"]!["recommendation"], largeResult["verificationRecommendation"]), "Every recommended scenario survives the smaller task-context budget without semantic truncation.");
            Check(System.Text.Encoding.UTF8.GetByteCount(largePrepared["task"]!["context"]!.ToJsonString()) <= 32 * 1024, "Escaped Unicode context fits the actual request limit despite a large parent summary.");
            largeResult["verificationRecommendation"]!["scenarios"]!.AsArray().Add(new string('测', 4096));
            Check(!WorkflowResult.IsValidStoredV2(largeResult), "Recommendations larger than the explicit actionable payload budget are rejected at the result contract, not unexpectedly when a user presses Run.");
            Check(File.ReadAllBytes(Path.Combine(store.RunDirectory(parentId), "task.json")).SequenceEqual(parentTaskBytes) &&
                File.ReadAllBytes(Path.Combine(store.RunDirectory(parentId), "result.json")).SequenceEqual(parentResultBytes), "Preparation and related-result projection preserve the original task and report bytes.");

            VerifyV3Scenarios(store);

            var repository = Path.Combine(root, "repository"); Directory.CreateDirectory(repository);
            var unknown = await ReviewProvenance.CaptureAsync(repository);
            Check(unknown["workingTree"]!.GetValue<string>() == "unknown", "Non-Git test folders are unknown rather than falsely clean.");
            await Git(repository, ["init", "--quiet", "--template="]);
            await File.WriteAllTextAsync(Path.Combine(repository, "source.txt"), "original\n");
            await Git(repository, ["add", "--", "source.txt"]);
            await Git(repository, ["-c", "user.name=Pulse Fixture", "-c", "user.email=fixture@invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "--quiet", "-m", "fixture"]);
            var clean = await ReviewProvenance.CaptureAsync(repository);
            Check(clean["workingTree"]!.GetValue<string>() == "clean" && clean["headSha"]!.GetValue<string>().Length == 40, "Host observations read actual clean Git source and SHA.");
            await File.AppendAllTextAsync(Path.Combine(repository, "source.txt"), "candidate\n");
            var dirty = await ReviewProvenance.CaptureAsync(repository);
            Check(dirty["workingTree"]!.GetValue<string>() == "modified" && dirty["diffHash"]!.GetValue<string>() != clean["diffHash"]!.GetValue<string>(), "Source modifications change the Host-observed fingerprint.");
            var composed = ReviewProvenance.Compose(clean["headSha"]!.GetValue<string>(), clean, dirty);
            Check(!ReviewProvenance.MatchesOriginal(composed, clean["headSha"]!.GetValue<string>()), "A changed end snapshot cannot attest to the original PR.");
            Console.WriteLine("PASS scoped review follow-ups: frozen intent, explicit verification scope, revision/source binding, related evidence, failures, immutable history and offline Git provenance");
        }
        finally { DeleteFixture(root); }
    }

    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static void VerifyV3Scenarios(Store store)
    {
        var parent = WorkflowV3Scenarios.TaskContext();
        var model = WorkflowV3Scenarios.Model();
        model["e2eAssessment"] = WorkflowV3Scenarios.E2e("required");
        model["e2eAssessment"]!["scenarios"]!.AsArray().Add("Exercise the second affected startup scenario.");
        model["e2eAssessment"]!["expectedResults"]!.AsArray().Add("The second startup scenario completes correctly.");
        var result = WorkflowResult.FromModel(model.ToJsonString(), parent, "succeeded", 0, null, null, expectedSchemaVersion: 3);
        Check(WorkflowResult.IsValidStoredV3(result), "The v3 E2E parent fixture is valid.");
        var parentId = Guid.NewGuid().ToString("D"); store.CreateRun(parentId, parent, new JsonObject(), Protocol.ProductionOrigin);
        SaveResult(store, parentId, result); SaveProvenance(store, parentId, true);
        var recommendation = ReviewFollowUps.VerificationRecommendation(result)!;
        var prepared = ReviewFollowUps.Prepare(store, new JsonObject
        {
            ["parentRunId"] = parentId, ["requestId"] = Guid.NewGuid().ToString("D"), ["recommendationId"] = ReviewFollowUps.RecommendationId(recommendation)
        });
        var task = prepared["task"]!.AsObject(); var scenarioIds = ReviewFollowUps.ScenarioIds(task);
        Check(scenarioIds.Count == 2 && task["context"]!["reviewVerification"]!["scenarioChecks"] is null, "V3 derives stable IDs from a compact prefix without duplicating the complete scenario list.");
        Check(JsonNode.DeepEquals(task["context"]!["reviewVerification"]!["recommendation"]!["expectedResults"], result["e2eAssessment"]!["expectedResults"]), "The linked task retains every expected result alongside the scenarios.");
        var childId = Guid.NewGuid().ToString("D"); store.CreateRun(childId, task, new JsonObject(), Protocol.ProductionOrigin);
        SaveProvenance(store, childId, true);
        var child = WorkflowV3Scenarios.Model("pr-verify");
        child["verificationEvidence"] = new JsonArray(new JsonObject
        {
            ["id"] = "runtime", ["source"] = "current-run", ["kind"] = "runtime", ["status"] = "passed", ["subject"] = "original-pr",
            ["revisionSha"] = parent["expectedHeadSha"]!.DeepClone(), ["summary"] = "Runtime observations retained.", ["evidence"] = new JsonArray("Saved run log"), ["runId"] = null
        });
        child["validation"]!.AsArray().Add(Scenario(scenarioIds[0], "passed"));
        SaveChild();
        Check(!Complete(), "A zero exit, passing aggregate assessment and one passing check do not fill a two-scenario requirement.");
        child["validation"]!.AsArray().Add(Scenario(scenarioIds[1], "passed"));
        SaveChild();
        Check(Complete() && ReviewFollowUps.Related(store, new JsonObject { ["runId"] = parentId })["currentConclusion"]!["completedRunId"]!.GetValue<string>() == childId, "Complete matching coverage exposes the existing verification result as the next primary destination.");
        child["verificationEvidence"] = new JsonArray(); SaveChild();
        Check(Complete(), "Actual passing evidence for every named scenario is sufficient without requiring duplicate generic evidence rows.");
        var effective = ReviewFollowUps.EffectiveResult(store, parentId, result);
        Check(effective["e2eAssessment"]!["level"]!.GetValue<string>() == "required" && effective["e2eEvidenceComplete"]!.GetValue<bool>() &&
            WorkflowResult.Recommendation(effective, parent)["kind"]!.GetValue<string>() == "approve", "The necessity level remains required while the current recommendation reflects supplied evidence.");
        child["validation"]!.AsArray().OfType<JsonObject>().Single(row => row["id"]!.GetValue<string>() == scenarioIds[1])["status"] = "failed";
        SaveChild();
        Check(!Complete(), "Mixed scenario outcomes never count as complete passing coverage.");
        child["validation"]!.AsArray().OfType<JsonObject>().Single(row => row["id"]!.GetValue<string>() == scenarioIds[1])["status"] = "passed";
        child["findings"]!.AsArray().Add(WorkflowV3Scenarios.Finding("linked-p0", "P0"));
        SaveChild();
        effective = ReviewFollowUps.EffectiveResult(store, parentId, result);
        Check(WorkflowResult.HasConfirmedP0(effective, parent) && !WorkflowResult.CanPublishManualPr(effective, parent, "approve"), "A confirmed same-source P0 in linked verification reaches the parent manual approval check.");
        SaveProvenance(store, childId, false);
        Check(!Complete() && !WorkflowResult.HasConfirmedP0(ReviewFollowUps.EffectiveResult(store, parentId, result), parent), "A local candidate cannot supply coverage or a confirmed current-original P0.");

        var noNeedId = Guid.NewGuid().ToString("D"); var noNeedTask = WorkflowV3Scenarios.TaskContext();
        var noNeed = WorkflowResult.FromModel(WorkflowV3Scenarios.Model().ToJsonString(), noNeedTask, "succeeded", 0, null, null, expectedSchemaVersion: 3);
        store.CreateRun(noNeedId, noNeedTask, new JsonObject(), Protocol.ProductionOrigin); SaveResult(store, noNeedId, noNeed);
        var noNeedRecommendation = ReviewFollowUps.VerificationRecommendation(noNeed)!;
        Expect("VERIFICATION_NOT_RECOMMENDED", () => ReviewFollowUps.Prepare(store, new JsonObject
        { ["parentRunId"] = noNeedId, ["requestId"] = Guid.NewGuid().ToString("D"), ["recommendationId"] = ReviewFollowUps.RecommendationId(noNeedRecommendation) }));
        Check(ReviewFollowUps.Related(store, new JsonObject { ["runId"] = noNeedId })["recommendation"]!["level"]!.GetValue<string>() == "not_needed", "An explicit not-needed E2E card remains visible and is not mistaken for missing data.");

        var manyId = Guid.NewGuid().ToString("D"); var manyTask = WorkflowV3Scenarios.TaskContext();
        var manyModel = WorkflowV3Scenarios.Model(); manyModel["e2eAssessment"] = WorkflowV3Scenarios.E2e("recommended");
        manyModel["e2eAssessment"]!["scenarios"] = new JsonArray(Enumerable.Range(0, 1500).Select(_ => (JsonNode?)JsonValue.Create("s")).ToArray());
        manyModel["e2eAssessment"]!["expectedResults"] = new JsonArray(Enumerable.Range(0, 1500).Select(_ => (JsonNode?)JsonValue.Create("e")).ToArray());
        var many = WorkflowResult.FromModel(manyModel.ToJsonString(), manyTask, "succeeded", 0, null, null, expectedSchemaVersion: 3);
        Check(WorkflowResult.IsValidStoredV3(many), "A bounded assessment may contain many concise scenario entries.");
        store.CreateRun(manyId, manyTask, new JsonObject(), Protocol.ProductionOrigin); SaveResult(store, manyId, many);
        var manyTaskPrepared = ReviewFollowUps.Prepare(store, new JsonObject
        { ["parentRunId"] = manyId, ["requestId"] = Guid.NewGuid().ToString("D"), ["recommendationId"] = ReviewFollowUps.RecommendationId(ReviewFollowUps.VerificationRecommendation(many)!) })["task"]!.AsObject();
        Check(ReviewFollowUps.ScenarioIds(manyTaskPrepared).Count == 1500 && System.Text.Encoding.UTF8.GetByteCount(manyTaskPrepared["context"]!.ToJsonString()) <= 32 * 1024,
            "Scenario identifiers do not inflate a valid bounded assessment beyond the task context budget or truncate its scope.");

        var directId = Guid.NewGuid().ToString("D"); var directTask = WorkflowV3Scenarios.TaskContext(); directTask["reviewOptions"]!["mode"] = "ui-e2e";
        var direct = WorkflowV3Scenarios.Model(); direct["e2eAssessment"] = WorkflowV3Scenarios.E2e("required");
        direct["e2eAssessment"]!["scenarios"]!.AsArray().Add("Second scenario"); direct["e2eAssessment"]!["expectedResults"]!.AsArray().Add("Second expected result");
        foreach (var id in WorkflowResult.RequiredChecksForTask(directTask))
            if (!direct["validation"]!.AsArray().OfType<JsonObject>().Any(row => row["id"]!.GetValue<string>() == id))
            { var row = Scenario(id, "passed"); row["required"] = true; direct["validation"]!.AsArray().Add(row); }
        direct["validation"]!.AsArray().Add(Scenario("e2e-scenario-1", "passed"));
        store.CreateRun(directId, directTask, new JsonObject(), Protocol.ProductionOrigin); SaveProvenance(store, directId, true);
        SaveDirect();
        Check(!DirectComplete(), "A direct UI review must cover every assessed scenario, not only its generic passing E2E check.");
        direct["validation"]!.AsArray().Add(Scenario("e2e-scenario-2", "passed")); SaveDirect();
        var directRelated = ReviewFollowUps.Related(store, new JsonObject { ["runId"] = directId });
        Check(DirectComplete() && directRelated["runs"]!.AsArray().Count == 0 && directRelated["currentConclusion"]!["completedRunId"]!.GetValue<string>() == directId,
            "A complete original-source UI review supplies its own exact scenario evidence without creating a redundant child run.");
        var directResult = store.ReadResult(directId)!;
        Check(ReviewFollowUps.EffectiveResult(store, directId, directResult)["e2eAssessment"]!["level"]!.GetValue<string>() == "required" &&
            WorkflowResult.Recommendation(ReviewFollowUps.EffectiveResult(store, directId, directResult), directTask)["kind"]!.GetValue<string>() == "approve",
            "The original required necessity remains intact while current-run coverage updates the advice.");
        SaveProvenance(store, directId, false);
        Check(!DirectComplete(), "Direct UI checks against a changed candidate do not establish the original PR scenarios.");

        var citedId = Guid.NewGuid().ToString("D"); var citedTask = WorkflowV3Scenarios.TaskContext(); var cited = WorkflowV3Scenarios.Model();
        cited["e2eAssessment"] = WorkflowV3Scenarios.E2e("required");
        cited["verificationEvidence"] = new JsonArray(new JsonObject
        {
            ["id"] = "e2e-scenario-1", ["source"] = "ci", ["kind"] = "runtime", ["status"] = "passed", ["subject"] = "original-pr",
            ["revisionSha"] = citedTask["expectedHeadSha"]!.DeepClone(), ["summary"] = "The cited CI scenario observed the expected startup behavior.",
            ["evidence"] = new JsonArray("https://github.com/microsoft/PowerToys/actions/runs/123#scenario-1"), ["runId"] = null
        });
        store.CreateRun(citedId, citedTask, new JsonObject(), Protocol.ProductionOrigin); SaveProvenance(store, citedId, true); SaveCited();
        var citedConclusion = ReviewFollowUps.Related(store, new JsonObject { ["runId"] = citedId })["currentConclusion"]!.AsObject();
        Check(citedConclusion["evidenceComplete"]!.GetValue<bool>() && citedConclusion["attributedEvidenceComplete"]!.GetValue<bool>() && !citedConclusion["currentRunEvidenceComplete"]!.GetValue<bool>(),
            "Exact-scenario same-revision cited CI coverage can supply evidence without pretending this static run executed UI tests.");
        cited["verificationEvidence"]![0]!["revisionSha"] = new string('b', 40); SaveCited();
        Check(!ReviewFollowUps.Related(store, new JsonObject { ["runId"] = citedId })["currentConclusion"]!["evidenceComplete"]!.GetValue<bool>(),
            "Existing CI or author evidence for a different revision cannot fill this assessment.");

        void SaveChild() => SaveResult(store, childId, WorkflowResult.FromModel(child.ToJsonString(), task, "succeeded", 0, null, null, expectedSchemaVersion: 3));
        bool Complete() => ReviewFollowUps.Related(store, new JsonObject { ["runId"] = parentId })["currentConclusion"]!["evidenceComplete"]!.GetValue<bool>();
        void SaveDirect() => SaveResult(store, directId, WorkflowResult.FromModel(direct.ToJsonString(), directTask, "succeeded", 0, null, null, expectedSchemaVersion: 3));
        bool DirectComplete() => ReviewFollowUps.Related(store, new JsonObject { ["runId"] = directId })["currentConclusion"]!["currentRunEvidenceComplete"]!.GetValue<bool>();
        void SaveCited() => SaveResult(store, citedId, WorkflowResult.FromModel(cited.ToJsonString(), citedTask, "succeeded", 0, null, null, expectedSchemaVersion: 3));
    }
    private static JsonObject Scenario(string id, string status) => new()
    { ["id"] = id, ["name"] = "Requested scenario", ["status"] = status, ["required"] = false, ["details"] = "Actual scenario observation.", ["evidence"] = new JsonArray("The recorded scenario output.") };
    private static JsonObject ParentTask() => Protocol.ValidateTask(new JsonObject
    {
        ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "review-parent", ["actionKind"] = "pr-review",
        ["repository"] = "microsoft/PowerToys", ["target"] = new JsonObject { ["type"] = "pr", ["number"] = 50027 },
        ["expectedHeadSha"] = Sha, ["reviewOptions"] = new JsonObject { ["mode"] = "static" }, ["prompt"] = "Review the code only."
    });
    private static JsonObject Result(JsonObject task, bool recommendation)
    {
        var model = new JsonObject
        {
            ["schemaVersion"] = 2, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "Scoped verification fixture.",
            ["assessment"] = new JsonObject { ["subject"] = "original-pr", ["status"] = recommendation ? "inconclusive" : "passed", ["summary"] = "Selected work completed.", ["revisionSha"] = Sha },
            ["reviewConclusion"] = recommendation ? new JsonObject { ["status"] = "no-blocking-findings", ["summary"] = "No code defect found.", ["revisionSha"] = Sha, ["blockingUncertainties"] = new JsonArray() } : null,
            ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(), ["validation"] = new JsonArray(), ["diagnostics"] = new JsonArray(),
            ["nextActions"] = new JsonArray(new JsonObject { ["kind"] = "none", ["reason"] = "Inspect the evidence.", ["body"] = "" }), ["review"] = null, ["needsReview"] = false,
            ["verificationEvidence"] = recommendation ? new JsonArray() : new JsonArray(new JsonObject
            {
                ["id"] = "runtime", ["source"] = "current-run", ["kind"] = "runtime", ["status"] = "passed", ["subject"] = "original-pr",
                ["revisionSha"] = Sha, ["summary"] = "The selected launch scenario completed.", ["evidence"] = new JsonArray("Preserved scenario log"), ["runId"] = null
            }),
            ["verificationRecommendation"] = recommendation ? new JsonObject
            {
                ["mode"] = "ui-e2e", ["reason"] = "The CJK startup behavior is runtime-dependent.", ["question"] = "Does the affected locale start correctly?",
                ["scenarios"] = new JsonArray("Start under the affected CJK locale"), ["prerequisites"] = new JsonArray("The matching CJK resources"),
                ["evidence"] = new JsonArray("The changed code assigns a localized startup title."), ["readiness"] = "ready"
            } : null
        };
        foreach (var id in WorkflowResult.RequiredChecksForTask(task)) model["validation"]!.AsArray().Add(new JsonObject
        {
            ["id"] = id, ["name"] = id, ["status"] = "passed", ["required"] = true, ["details"] = "Selected check completed.", ["evidence"] = new JsonArray("Retained proof")
        });
        if (task["context"]?["reviewVerification"]?["scenarioChecks"] is JsonArray scenarios)
            foreach (var check in scenarios.OfType<JsonObject>()) model["validation"]!.AsArray().Add(new JsonObject
            {
                ["id"] = check["id"]!.DeepClone(), ["name"] = "Selected scenario", ["status"] = "passed", ["required"] = false,
                ["details"] = "The exact selected scenario completed.", ["evidence"] = new JsonArray("Expected and observed behavior recorded")
            });
        return WorkflowResult.FromModel(model.ToJsonString(), task, "succeeded", 0, null, null, expectedSchemaVersion: 2);
    }
    private static void SaveResult(Store store, string id, JsonObject result, string state = "succeeded", int exitCode = 0)
    {
        store.WriteJson(Path.Combine(store.RunDirectory(id), "result.json"), result);
        var status = store.ReadStatus(id); status["state"] = state; status["exitCode"] = exitCode;
        store.WriteJson(Path.Combine(store.RunDirectory(id), "status.json"), status);
    }
    private static void SaveProvenance(Store store, string id, bool original)
    {
        var start = new JsonObject { ["headSha"] = Sha, ["workingTree"] = "clean", ["diffHash"] = "clean", ["capturedAt"] = Protocol.Now() };
        var end = start.DeepClone().AsObject(); if (!original) end["workingTree"] = "modified";
        store.WriteJson(Path.Combine(store.RunDirectory(id), "provenance.json"), ReviewProvenance.Compose(Sha, start, end));
    }
    private static async Task Git(string directory, string[] arguments)
    {
        var git = RuntimeService.ResolveExecutable("git") ?? throw new Exception("Git is required for offline provenance fixtures.");
        using var process = WindowsProcess.Cli(git, arguments, directory);
        var output = process.Output!.ReadToEndAsync(); var error = process.Error!.ReadToEndAsync();
        process.Input!.Close(); process.Resume(); await process.WaitAsync(); process.Kill();
        await Task.WhenAll(output, error); Check(process.ExitCode == 0, "The isolated offline Git fixture completed.");
    }
    private static void DeleteFixture(string root)
    {
        var resolved = Path.GetFullPath(root);
        var temporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        const string prefix = "PulseReviewFollowUpTests-";
        var name = Path.GetFileName(resolved);
        if (!string.Equals(Path.GetDirectoryName(resolved), temporaryDirectory, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Refusing to remove a path outside the isolated review follow-up fixture.");
        if (!Directory.Exists(resolved)) return;
        // Git marks loose object files read-only on Windows. Normalize only files
        // inside this verified, uniquely named temporary fixture before cleanup.
        foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(resolved, recursive: true);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (ProtocolException error) when (error.Code == code) { }
    }
}
