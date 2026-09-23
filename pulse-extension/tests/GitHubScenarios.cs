using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Offline acceptance scenarios. No GitHub credential or real network is used.</summary>
public static class GitHubScenarios
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static async Task RunAllAsync()
    {
        await GhCliScenarios.RunAllAsync();
        DiffLocations();
        await PreviewAndFixedReview();
        await SuggestionReview();
        await StaleAndInvalidInputs();
        await UnknownAndReconciliation();
        await CloseAndComment();
        await PermissionChecks();
        await AbandonedPreparation();
        await MaintenanceAndBoundedHistory();
        await ContextUsesSelectedSession();
        await PrActionsWithFailedReports();
        await ProposalAssociationPreservesWriteDeduplication();
        await CurrentAccountAndOriginalSubmissionIdentity();
        await PrActionsIndependentOfExecution();
        await ProposalSuggestionIsolation();
        await StructuredFailureReportsCanBeCommented();
        await ScopedReviewActions();
        await ScopedReviewBoundaries();
        await ScopedReviewLiveGuards();
        await ScopedReviewFraming();
        await ScopedSupplementalEvidenceGuards();
        await ConfirmedP0IsTheOnlyPrBusinessRestriction();
        await VerificationP0RequiresOriginalHostProvenance();
        await IssueManualActionsIndependentOfAnalysis();
        await SelectedFindingFeedbackIsExact();
        Console.WriteLine("PASS GitHub: fixed endpoints, diff bounds, permissions, SHA, deduplication, durable intent, and unknown recovery (offline)");
    }

    private static JsonObject ScopedReview(Fixture fixture, Action<JsonObject>? change = null, string mode = "static")
    {
        var saved = fixture.Store.ReadTask(fixture.RunId);
        var task = saved["task"]!.AsObject();
        task["reviewOptions"] = new JsonObject { ["mode"] = mode };
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "task.json"), saved);
        JsonObject CheckRow(string id) => new()
        {
            ["id"] = id, ["name"] = id, ["status"] = "passed", ["required"] = true,
            ["details"] = "Completed the selected check.", ["evidence"] = new JsonArray("Retained evidence for " + id)
        };
        var model = new JsonObject
        {
            ["schemaVersion"] = 2, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "Completed the selected review scope.",
            ["assessment"] = new JsonObject { ["subject"] = "original-pr", ["status"] = "inconclusive", ["summary"] = "Runtime acceptance was outside this static review.", ["revisionSha"] = Sha },
            ["reviewConclusion"] = new JsonObject { ["status"] = "no-blocking-findings", ["summary"] = "No blocking issue was found in the reviewed source.",
                ["revisionSha"] = Sha, ["blockingUncertainties"] = new JsonArray() },
            ["verificationEvidence"] = new JsonArray(VerificationObservation("not_run")),
            ["verificationRecommendation"] = null,
            ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(),
            ["validation"] = new JsonArray(ReviewModes.RequiredChecks(task).Select(id => (JsonNode?)CheckRow(id)).ToArray()),
            ["diagnostics"] = new JsonArray(),
            ["nextActions"] = new JsonArray(
                new JsonObject { ["kind"] = "approve", ["reason"] = "The completed source review found no blocking issue.", ["body"] = "Source review completed; runtime verification was not selected.", ["suggestionIds"] = new JsonArray() },
                new JsonObject { ["kind"] = "comment", ["reason"] = "Share the review's recorded scope.", ["body"] = "Static review completed. Runtime coverage is not claimed." }),
            ["review"] = new JsonObject { ["headSha"] = Sha, ["body"] = "Static review completed; runtime verification was not selected.", ["suggestions"] = new JsonArray() },
            ["needsReview"] = false
        };
        change?.Invoke(model);
        var result = WorkflowResult.FromModel(model.ToJsonString(), task, "succeeded", 0, null, null, expectedSchemaVersion: 2);
        Check(WorkflowResult.IsValidStoredV2(result) && result["diagnostics"]!.AsArray().OfType<JsonObject>().All(row => row["code"]?.GetValue<string>() != "INVALID_RESULT"),
            "The controlled scoped review must retain valid structured evidence.");
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "result.json"), result);
        return result;
    }

    private static JsonObject VerificationObservation(string status, string revision = Sha) => new()
    {
        ["id"] = "runtime-observation", ["source"] = "ci", ["kind"] = "runtime", ["status"] = status,
        ["subject"] = "original-pr", ["revisionSha"] = revision, ["summary"] = status == "not_run" ? "Runtime verification was not selected." : "Same-revision runtime observation.",
        ["evidence"] = status == "not_run" ? new JsonArray() : new JsonArray("Retained runtime evidence."), ["runId"] = null
    };

    private static JsonObject ScopedDraft(Fixture fixture, JsonObject result, string kind, string operationId, string body)
    {
        var draft = fixture.Draft(kind, operationId, body);
        var proposal = WorkflowResult.WithProposalIds(result)["nextActions"]!.AsArray().OfType<JsonObject>().Single(row => row["kind"]!.GetValue<string>() == kind);
        draft["proposalId"] = proposal["proposalId"]!.DeepClone();
        draft["expectedHeadSha"] = Sha;
        return draft;
    }

    private static async Task ScopedReviewActions()
    {
        using var fixture = new Fixture();
        var result = ScopedReview(fixture);
        var resultPath = Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "result.json");
        var before = File.ReadAllBytes(resultPath);
        var preview = await fixture.Service.PreviewAsync(fixture.RunId);
        Check(preview["canApprove"]!.GetValue<bool>() && preview["canComment"]!.GetValue<bool>() && !preview.ContainsKey("reviewDecisions"),
            "A completed selected scope exposes ordinary result proposals without a separate manual decision channel.");
        var comment = await fixture.Service.SubmitAsync(ScopedDraft(fixture, result, "comment", "scoped-comment", "    Static review completed; runtime testing was not selected.\n"));
        Check(comment["status"]?.GetValue<string>() == "succeeded" && fixture.Api.Writes.Single().Body["body"]?.GetValue<string>() == "    Static review completed; runtime testing was not selected.\n",
            "An ordinary scoped comment preserves the exact edited report through the fixed conversation endpoint.");
        var draft = ScopedDraft(fixture, result, "approve", "scoped-approval", "Reviewed the selected source scope.");
        var approved = await fixture.Service.SubmitAsync(draft);
        var wire = fixture.Api.Writes.Last().Body;
        Check(approved["status"]?.GetValue<string>() == "succeeded" && wire["event"]?.GetValue<string>() == "APPROVE" &&
            wire["commit_id"]?.GetValue<string>() == Sha && wire["body"]?.GetValue<string>() == "Reviewed the selected source scope." && !approved.ContainsKey("reviewDecision"),
            "Normal approval is bound to the scoped result and source SHA without adding a manual acknowledgement or fabricated disclosure.");
        await fixture.Service.SubmitAsync(ScopedDraft(fixture, result, "approve", "scoped-duplicate", "Reviewed the selected source scope."));
        Check(fixture.Api.Writes.Count == 2 && File.ReadAllBytes(resultPath).SequenceEqual(before),
            "Identical approvals still deduplicate and publishing never rewrites the original result.");

        foreach (var field in new[] { "reviewDecisionId", "acknowledgedLimitations" })
        {
            var bypass = ScopedDraft(fixture, result, "approve", "manual-bypass-" + field, "No bypass allowed.");
            bypass[field] = field == "reviewDecisionId" ? JsonValue.Create("old-manual-decision") : JsonValue.Create(true);
            await ThrowsAsync("INVALID_REQUEST", () => fixture.Service.SubmitAsync(bypass));
        }
        Check(fixture.Api.Writes.Count == 2, "Removed manual decision arguments must be rejected even when ordinary approval would otherwise be valid.");

        var historical = approved.DeepClone().AsObject();
        historical["operationId"] = "historical-manual";
        historical["reviewDecision"] = new JsonObject { ["kind"] = "approve-with-limitations", ["acknowledgedLimitations"] = true };
        fixture.Store.WriteJson(fixture.OperationPath("historical-manual"), historical);
        Check(fixture.Service.List(fixture.RunId)["operations"]!.AsArray().OfType<JsonObject>().Any(row =>
            row["operationId"]?.GetValue<string>() == "historical-manual" && row["reviewDecision"]?["acknowledgedLimitations"]?.GetValue<bool>() == true),
            "Removing new manual submissions must not erase the metadata displayed for historical operations.");
    }

    private static async Task ScopedReviewBoundaries()
    {
        var cases = new (string Name, Action<JsonObject> Edit)[]
        {
            ("context missing", model => model["validation"]![0]!["status"] = "not_run"),
            ("local review missing", model => model["validation"]![1]!["status"] = "not_run"),
            ("local review lacks evidence", model => model["validation"]![1]!["evidence"] = new JsonArray()),
            ("source SHA mismatch", model => model["reviewConclusion"]!["revisionSha"] = OtherSha),
            ("local candidate assessment", model => model["assessment"]!["subject"] = "local-candidate"),
            ("incomplete transport", model => model["diagnostics"]!.AsArray().Add(new JsonObject { ["code"] = "OUTPUT_INCOMPLETE", ["severity"] = "error", ["message"] = "Output truncated.", ["recovery"] = "inspectResult" })),
            ("reported workflow failure", model => model["outcome"] = "failed"),
            ("inconclusive code review", model => model["reviewConclusion"]!["status"] = "inconclusive"),
            ("material code uncertainty", model => model["reviewConclusion"]!["blockingUncertainties"] = new JsonArray("The changed lifetime cannot be established.")),
            ("failed original product", model => model["assessment"]!["status"] = "failed"),
            ("verified same-revision failure", model => model["verificationEvidence"] = new JsonArray(VerificationObservation("failed"))),
            ("known source issue", model => model["findings"]!.AsArray().Add(new JsonObject { ["id"] = "source-defect", ["title"] = "Known defect", ["severity"] = "high", ["status"] = "open", ["path"] = "a.cs", ["line"] = 2, ["details"] = "Confirmed issue.", ["evidence"] = new JsonArray("The affected path fails.") })),
            ("unverified medium finding", model => model["findings"]!.AsArray().Add(new JsonObject { ["id"] = "source-defect", ["title"] = "Unverified concern", ["severity"] = "medium", ["status"] = "unverified", ["path"] = "a.cs", ["line"] = 2, ["details"] = "Needs investigation.", ["evidence"] = new JsonArray("The affected path remains unresolved.") }))
        };
        foreach (var (name, edit) in cases)
        {
            using var fixture = new Fixture();
            var result = ScopedReview(fixture, edit);
            var preview = await fixture.Service.PreviewAsync(fixture.RunId);
            Check(preview["canApprove"]?.GetValue<bool>() == true && !preview.ContainsKey("reviewDecisions"), "Historical analysis does not supply the new confirmed-P0 restriction: " + name);
            var submitted = await fixture.Service.SubmitAsync(fixture.Draft("approve", "manual-scope-choice", "My explicit review after reading the limitations."));
            Check(submitted["status"]?.GetValue<string>() == "succeeded" && fixture.Api.Writes.Count == 1, "The user may independently approve despite AI scope or outcome limitations: " + name);
        }
        foreach (var state in new[] { "running", "failed", "cancelled", "interrupted" })
        {
            using var fixture = new Fixture(); var result = ScopedReview(fixture);
            fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "status.json"), new JsonObject { ["state"] = state, ["exitCode"] = 0 });
            Check((await fixture.Service.PreviewAsync(fixture.RunId))["canApprove"]?.GetValue<bool>() == true, "Manual PR actions remain present during every CLI state: " + state);
            Check((await fixture.Service.SubmitAsync(fixture.Draft("approve", "manual-process-independent", "My explicit review.")))["status"]?.GetValue<string>() == "succeeded", "CLI status does not authorize or prohibit the user's manual review.");
        }
        using (var invalid = new Fixture())
        {
            var result = ScopedReview(invalid);
            result["diagnostics"]!.AsArray().Add(new JsonObject { ["code"] = "INVALID_RESULT", ["severity"] = "error", ["message"] = "The final response was not valid.", ["recovery"] = "inspectResult" });
            invalid.Store.WriteJson(Path.Combine(invalid.Store.RunDirectory(invalid.RunId), "result.json"), result);
            Check((await invalid.Service.PreviewAsync(invalid.RunId))["canApprove"]?.GetValue<bool>() == true, "Invalid AI output must not hide fixed manual controls or invent a confirmed P0.");
            Check((await invalid.Service.SubmitAsync(invalid.Draft("approve", "manual-with-invalid-ai-report", "My own review.")))["status"]?.GetValue<string>() == "succeeded", "Manual approval does not claim the invalid AI report was complete.");
        }
        foreach (var mode in new[] { "build-tests", "ui-e2e" })
        {
            using var fixture = new Fixture();
            var result = ScopedReview(fixture, model => model["validation"]!.AsArray().Last()!["status"] = "not_run", mode);
            Check((await fixture.Service.PreviewAsync(fixture.RunId))["canApprove"]?.GetValue<bool>() == true,
                "Incomplete selected verification affects advice, not the user's fixed approval control: " + mode);
            Check((await fixture.Service.SubmitAsync(fixture.Draft("approve", "manual-with-coverage-limit", "Reviewed with awareness of recorded coverage.")))["status"]?.GetValue<string>() == "succeeded", "The submitted manual body remains independent of the AI verification verdict.");
        }
        using var unrelated = new Fixture();
        ScopedReview(unrelated, model => model["verificationEvidence"] = new JsonArray(VerificationObservation("failed", OtherSha)));
        Check((await unrelated.Service.PreviewAsync(unrelated.RunId))["canApprove"]?.GetValue<bool>() == true,
            "A failure explicitly attributed to another revision cannot be silently treated as a failure of this reviewed SHA.");
    }

    private static async Task ScopedReviewLiveGuards()
    {
        using (var fixture = new Fixture())
        {
            var result = ScopedReview(fixture); fixture.Api.Head = OtherSha;
            Check((await fixture.Service.PreviewAsync(fixture.RunId))["canApprove"]?.GetValue<bool>() == false, "Live HEAD drift disables scoped approval.");
            var denied = await fixture.Service.SubmitAsync(ScopedDraft(fixture, result, "approve", "stale-scoped-approval", "Old revision."));
            Check(denied["error"]?["code"]?.GetValue<string>() == "STALE_CONTEXT" && fixture.Api.Writes.Count == 0, "Scoped approval rechecks current HEAD.");
        }
        using (var fixture = new Fixture())
        {
            var result = ScopedReview(fixture); fixture.Api.Author = "reviewer";
            var preview = await fixture.Service.PreviewAsync(fixture.RunId);
            Check(preview["canComment"]?.GetValue<bool>() == true && preview["canApprove"]?.GetValue<bool>() == false, "A PR author may comment but cannot approve their own PR.");
            var denied = await fixture.Service.SubmitAsync(ScopedDraft(fixture, result, "approve", "scoped-self-approval", "Not allowed."));
            Check(denied["error"]?["code"]?.GetValue<string>() == "GITHUB_OPERATION_UNAVAILABLE" && fixture.Api.Writes.Count == 0, "Server-side author permission remains required.");
        }
        using (var fixture = new Fixture())
        {
            var result = ScopedReview(fixture); fixture.Api.Locked = true; fixture.Api.WritePermission = false;
            var denied = await fixture.Service.SubmitAsync(ScopedDraft(fixture, result, "comment", "locked-scoped-comment", "Review report."));
            Check(denied["error"]?["code"]?.GetValue<string>() == "GITHUB_OPERATION_UNAVAILABLE" && fixture.Api.Writes.Count == 0, "Scoped report comments respect locked conversations.");
        }
        using (var fixture = new Fixture())
        {
            var result = ScopedReview(fixture);
            var wrongAccount = ScopedDraft(fixture, result, "approve", "wrong-scoped-account", "Review."); wrongAccount["expectedAccount"] = "other-reviewer";
            var denied = await fixture.Service.SubmitAsync(wrongAccount);
            Check(denied["error"]?["code"]?.GetValue<string>() == "GITHUB_IDENTITY_MISMATCH" && fixture.Api.Writes.Count == 0, "The current account must match the scoped approval confirmation.");
        }
        using (var fixture = new Fixture())
        {
            var result = ScopedReview(fixture); fixture.Api.ChangeHeadAfterCollectionRead = true;
            var changed = await fixture.Service.SubmitAsync(ScopedDraft(fixture, result, "approve", "scoped-late-head-drift", "Review."));
            Check(changed["error"]?["code"]?.GetValue<string>() == "STALE_CONTEXT" && fixture.Api.Writes.Count == 0, "Final HEAD recheck remains active after baseline pagination.");
        }
        using (var fixture = new Fixture())
        {
            var result = ScopedReview(fixture); fixture.Api.LoseWriteResponse = true;
            var draft = ScopedDraft(fixture, result, "approve", "unknown-scoped-approval", "Explicitly reviewed.");
            var uncertain = await fixture.Service.SubmitAsync(draft);
            Check(uncertain["status"]?.GetValue<string>() == "unknown", "A lost scoped approval response preserves durable unknown intent.");
            var recovered = await fixture.Service.SubmitAsync(draft);
            Check(recovered["status"]?.GetValue<string>() == "succeeded" && fixture.Api.Writes.Count == 1,
                "Unknown scoped approval reconciles the original body and SHA without another write.");
        }
    }

    private static async Task ScopedReviewFraming()
    {
        using var fixture = new Fixture();
        var result = ScopedReview(fixture, model => model["reviewConclusion"]!["summary"] = new string('验', 3000) + "\n\"Completed the selected source scope.\"");
        var preview = await fixture.Service.PreviewAsync(fixture.RunId);
        Check(preview["canApprove"]?.GetValue<bool>() == true, "A valid Unicode scoped conclusion remains confirmable.");
        var body = new string('评', 25000) + "\n\"Source review completed\"";
        var sent = await fixture.Service.SubmitAsync(ScopedDraft(fixture, result, "approve", "unicode-scoped-approval", body));
        Check(sent["status"]?.GetValue<string>() == "succeeded" && !sent.ContainsKey("reviewDecision"), "Scoped approval retains its actual body without duplicate manual decision metadata.");
        var history = fixture.Service.List(fixture.RunId);
        foreach (var value in new[] { preview, sent, history })
        {
            await using var framed = new MemoryStream();
            await NativeFraming.WriteAsync(framed, new JsonObject { ["id"] = "review-framing", ["protocolVersion"] = 1, ["ok"] = true, ["data"] = value.DeepClone() });
            var wire = framed.ToArray();
            var actual = JsonNode.Parse(wire.AsSpan(4), documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 })!.AsObject();
            Check(actual["ok"]!.GetValue<bool>() && JsonNode.DeepEquals(actual["data"], value) && wire.Length - 4 <= Protocol.MaxOutputBytes,
                "Native Messaging preserves scoped preview, Unicode submit response and operation history without OUTPUT_TOO_LARGE.");
        }
        Check(history["operations"]![0]!["bodyTruncated"]?.GetValue<bool>() == true &&
            fixture.Store.ReadJson(fixture.OperationPath("unicode-scoped-approval"))!["body"]!.GetValue<string>() == body &&
            fixture.Store.ReadResult(fixture.RunId)!["reviewConclusion"]!["summary"]!.GetValue<string>() == result["reviewConclusion"]!["summary"]!.GetValue<string>(),
            "History may shorten display text while the complete publication and original conclusion remain on disk.");
    }

    private static async Task ScopedSupplementalEvidenceGuards()
    {
        using var fixture = new Fixture();
        var parent = ScopedReview(fixture, model => model["verificationRecommendation"] = new JsonObject
        {
            ["mode"] = "ui-e2e", ["reason"] = "Runtime verification can supplement the static review.", ["question"] = "Does the changed startup path work?",
            ["scenarios"] = new JsonArray("Start the affected configuration."), ["prerequisites"] = new JsonArray(),
            ["evidence"] = new JsonArray("The reviewed change affects startup."), ["readiness"] = "ready"
        });
        var childId = Guid.NewGuid().ToString("D");
        var childTask = new JsonObject
        {
            ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "supplemental-fixture", ["actionKind"] = "pr-verify", ["repository"] = "example/project",
            ["target"] = new JsonObject { ["type"] = "pr", ["number"] = 7 }, ["expectedHeadSha"] = Sha, ["reviewOptions"] = new JsonObject { ["mode"] = "ui-e2e" },
            ["followUp"] = new JsonObject { ["parentRunId"] = fixture.RunId, ["recommendationId"] = ReviewFollowUps.RecommendationId(parent["verificationRecommendation"]!.AsObject()),
                ["parentResultFingerprint"] = Protocol.Fingerprint(parent), ["subject"] = "original-pr", ["revisionSha"] = Sha }
        };
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(childId), "task.json"), new JsonObject { ["task"] = childTask.DeepClone(), ["config"] = new JsonObject() });
        void SaveChildStatus(string state) => fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(childId), "status.json"), new JsonObject { ["state"] = state, ["exitCode"] = 0 });
        void SaveProvenance(string id)
        {
            var snapshot = new JsonObject { ["headSha"] = Sha, ["workingTree"] = "clean", ["diffHash"] = "clean", ["capturedAt"] = Protocol.Now() };
            fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(id), "provenance.json"), ReviewProvenance.Compose(Sha, snapshot, snapshot.DeepClone().AsObject()));
        }
        SaveProvenance(fixture.RunId); SaveProvenance(childId);
        var observation = VerificationObservation("failed"); observation["source"] = "current-run";
        var model = new JsonObject
        {
            ["schemaVersion"] = 2, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "The executed scenario found a product failure.",
            ["assessment"] = new JsonObject { ["subject"] = "original-pr", ["status"] = "failed", ["summary"] = "The original PR fails the tested startup scenario.", ["revisionSha"] = Sha },
            ["reviewConclusion"] = null, ["review"] = null, ["verificationEvidence"] = new JsonArray(observation), ["verificationRecommendation"] = null,
            ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(), ["diagnostics"] = new JsonArray(), ["needsReview"] = true,
            ["nextActions"] = new JsonArray(new JsonObject { ["kind"] = "inspectResult", ["reason"] = "Inspect the observed failure.", ["body"] = "" }),
            ["validation"] = new JsonArray(ReviewModes.RequiredChecks(childTask).Select(id => (JsonNode?)new JsonObject
            {
                ["id"] = id, ["name"] = id, ["status"] = "passed", ["required"] = true, ["details"] = "The selected workflow check was executed and interpreted.",
                ["evidence"] = new JsonArray("The controlled scenario log records the failing behavior.")
            }).ToArray())
        };
        var child = WorkflowResult.FromModel(model.ToJsonString(), childTask, "succeeded", 0, null, null, expectedSchemaVersion: 2);
        Check(WorkflowResult.IsValidStoredV2(child) && child["outcome"]?.GetValue<string>() == "completed", "The supplemental fixture must represent a successful verification workflow that found a product failure.");
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(childId), "result.json"), child);
        SaveChildStatus("running");
        var before = File.ReadAllBytes(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "result.json"));
        Check((await fixture.Service.PreviewAsync(fixture.RunId))["canApprove"]?.GetValue<bool>() == true, "Incomplete verification does not masquerade as accepted failure evidence.");
        fixture.Api.BeforeCollectionRead = () => SaveChildStatus("succeeded");
        var denied = await fixture.Service.SubmitAsync(ScopedDraft(fixture, parent, "approve", "late-supplement-failure", "Reviewed source scope."));
        Check(denied["status"]?.GetValue<string>() == "succeeded" && fixture.Api.Writes.Count == 1,
            "Supplemental product failure cannot be promoted into a confirmed P0 or prohibit the user's manual approval.");
        Check((await fixture.Service.PreviewAsync(fixture.RunId))["canApprove"]?.GetValue<bool>() == true,
            "Supplemental failure updates evidence without silently adding an approval restriction.");
        fixture.Api.BeforeCollectionRead = null;
        child["outcome"] = "blocked"; child["assessment"]!["status"] = "inconclusive";
        child["verificationEvidence"]![0]!["status"] = "not_run"; child["verificationEvidence"]![0]!["evidence"] = new JsonArray();
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(childId), "result.json"), child);
        var incomplete = ReviewFollowUps.EffectiveResult(fixture.Store, fixture.RunId, parent);
        Check(incomplete["assessment"]?["status"]?.GetValue<string>() == "inconclusive" && File.ReadAllBytes(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "result.json")).SequenceEqual(before),
            "A child process exiting zero without completed passing verification cannot upgrade product assessment or rewrite the source report.");
    }

    private static JsonObject V3Review(Fixture fixture, string priority = "P1", Action<JsonObject>? edit = null)
    {
        var source = ScopedReview(fixture);
        var model = new JsonObject();
        foreach (var field in new[] { "outcome", "phase", "summary", "assessment", "reviewConclusion", "verificationEvidence", "artifacts", "validation", "diagnostics", "review", "needsReview" })
            model[field] = source[field]?.DeepClone();
        model["schemaVersion"] = 3;
        model["report"] = new JsonObject { ["complete"] = true, ["rechecked"] = true, ["coverage"] = new JsonArray("The complete selected code review."), ["limitations"] = new JsonArray("Runtime verification remains recommended.") };
        model["e2eAssessment"] = new JsonObject { ["level"] = "required", ["reason"] = "Runtime evidence would answer a remaining product question.", ["question"] = "Does the affected scenario pass?",
            ["scenarios"] = new JsonArray("Start the affected configuration."), ["expectedResults"] = new JsonArray("Startup succeeds."), ["prerequisites"] = new JsonArray("Affected runtime environment."),
            ["evidence"] = new JsonArray("Reviewed source evidence."), ["readiness"] = "unknown" };
        model["featureAssessment"] = null; model["bugAssessment"] = null; model["plans"] = new JsonArray(); model["nextActions"] = new JsonArray();
        model["findings"] = new JsonArray(V3Finding("first", priority));
        edit?.Invoke(model);
        var task = fixture.Store.ReadTask(fixture.RunId)["task"]!.AsObject();
        var result = WorkflowResult.FromModel(model.ToJsonString(), task, "succeeded", 0, null, null, expectedSchemaVersion: 3);
        Check(WorkflowResult.IsValidStoredV3(result) && !result["diagnostics"]!.AsArray().OfType<JsonObject>().Any(row => row["code"]?.GetValue<string>() == "INVALID_RESULT"), "The manual action fixture must use valid v3 findings.");
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "result.json"), result);
        return result;
    }

    private static JsonObject V3Finding(string id, string priority, string? suggestionId = null) => new()
    {
        ["id"] = id, ["title"] = "Confirmed finding " + id, ["priority"] = priority, ["status"] = "open", ["confirmed"] = true,
        ["path"] = "a.cs", ["line"] = 2, ["details"] = "The changed source exhibits the recorded problem.", ["impact"] = "The affected behavior can fail.",
        ["trigger"] = "Execute the affected configuration.", ["rootCause"] = "The reviewed source contains the relevant condition.", ["fixSuggestion"] = "Correct the reviewed condition.",
        ["evidence"] = new JsonArray("Saved source and behavior evidence."), ["feedback"] = new JsonObject { ["body"] = "Feedback for " + id, ["suggestionId"] = suggestionId }
    };

    private static async Task ConfirmedP0IsTheOnlyPrBusinessRestriction()
    {
        foreach (var priority in new[] { "P0", "P1", "P2", "P3" })
        {
            using var fixture = new Fixture(); V3Review(fixture, priority);
            fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "status.json"), new JsonObject { ["state"] = "running", ["exitCode"] = null });
            var preview = await fixture.Service.PreviewAsync(fixture.RunId);
            Check(preview["canApprove"]?.GetValue<bool>() == (priority != "P0") && preview["hasConfirmedP0"]?.GetValue<bool>() == (priority == "P0") && preview["canComment"]?.GetValue<bool>() == true,
                "Priority affects fixed approval only for confirmed open P0, independent of active execution and required E2E: " + priority);
            var draft = fixture.Draft("approve", "manual-priority", "My explicit review.");
            if (priority == "P0") await ThrowsAsync("CONFIRMED_P0", () => fixture.Service.SubmitAsync(draft));
            else Check((await fixture.Service.SubmitAsync(draft))["status"]?.GetValue<string>() == "succeeded", "P1/P2/P3 do not prohibit manual approval.");
        }
        foreach (var variant in new[] { "unconfirmed", "fixed", "local-candidate" })
        {
            using var fixture = new Fixture();
            V3Review(fixture, "P0", model =>
            {
                if (variant == "unconfirmed") model["findings"]![0]!["confirmed"] = false;
                if (variant == "fixed") model["findings"]![0]!["status"] = "fixed";
                if (variant == "local-candidate") { model["assessment"]!["subject"] = "local-candidate"; model["reviewConclusion"] = null; }
            });
            Check((await fixture.Service.PreviewAsync(fixture.RunId))["canApprove"]?.GetValue<bool>() == true, "Only a confirmed unresolved original-PR P0 qualifies: " + variant);
        }
        using var aggregate = new Fixture();
        var own = V3Review(aggregate, "P1");
        var relatedId = Guid.NewGuid().ToString("D");
        var relatedTask = aggregate.Store.ReadTask(aggregate.RunId).DeepClone().AsObject(); relatedTask["runId"] = relatedId;
        aggregate.Store.WriteJson(Path.Combine(aggregate.Store.RunDirectory(relatedId), "task.json"), relatedTask);
        var p0 = own.DeepClone().AsObject(); p0["findings"]![0]!["priority"] = "P0"; p0["report"]!["complete"] = false; p0["outcome"] = "failed";
        aggregate.Store.WriteJson(Path.Combine(aggregate.Store.RunDirectory(relatedId), "result.json"), p0);
        Check((await aggregate.Service.PreviewAsync(aggregate.RunId))["canApprove"]?.GetValue<bool>() == false,
            "A confirmed open P0 in another same-PR/SHA report remains known even if that report is incomplete.");
        relatedTask["task"]!["expectedHeadSha"] = OtherSha;
        aggregate.Store.WriteJson(Path.Combine(aggregate.Store.RunDirectory(relatedId), "task.json"), relatedTask);
        Check((await aggregate.Service.PreviewAsync(aggregate.RunId))["canApprove"]?.GetValue<bool>() == true, "Other-revision P0 evidence cannot block approval of this saved SHA.");
    }

    private static async Task VerificationP0RequiresOriginalHostProvenance()
    {
        foreach (var kind in new[] { "e2e", "pr-verify" })
        foreach (var source in new[] { "missing", "unknown", "candidate", "different-sha", "original" })
        {
            using var fixture = new Fixture();
            var result = V3Review(fixture, "P0", model => { model["reviewConclusion"] = null; model["review"] = null; });
            var saved = fixture.Store.ReadTask(fixture.RunId); saved["task"]!["actionKind"] = kind;
            if (kind == "e2e") saved["task"]!.AsObject().Remove("reviewOptions");
            else saved["task"]!["reviewOptions"] = new JsonObject { ["mode"] = "ui-e2e" };
            fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "task.json"), saved);
            if (source != "missing")
            {
                var snapshot = new JsonObject { ["headSha"] = source == "different-sha" ? OtherSha : Sha, ["workingTree"] = source == "unknown" ? "unknown" : "clean", ["diffHash"] = "fixture", ["capturedAt"] = Protocol.Now() };
                var end = snapshot.DeepClone().AsObject(); if (source == "candidate") end["workingTree"] = "modified";
                fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "provenance.json"), ReviewProvenance.Compose(Sha, snapshot, end));
            }
            var expected = source == "original";
            var preview = await fixture.Service.PreviewAsync(fixture.RunId);
            Check(preview["hasConfirmedP0"]?.GetValue<bool>() == expected && preview["canApprove"]?.GetValue<bool>() == !expected,
                "Verification-only findings must use Host source evidence instead of trusting a model's original-pr label: " + kind + "/" + source);
            var draft = fixture.Draft("approve", "verification-source-approval", "My explicit source review.");
            if (expected) await ThrowsAsync("CONFIRMED_P0", () => fixture.Service.SubmitAsync(draft));
            else Check((await fixture.Service.SubmitAsync(draft))["status"]?.GetValue<string>() == "succeeded", "Submit must agree with preview after excluding candidate/unknown verification P0.");
            Check(JsonNode.DeepEquals(fixture.Store.ReadResult(fixture.RunId), result), "Source classification must not rewrite the saved verification report.");
        }
        using var reviewed = new Fixture();
        V3Review(reviewed, "P0", model => model["assessment"]!["subject"] = "local-candidate");
        var original = new JsonObject { ["headSha"] = Sha, ["workingTree"] = "clean", ["diffHash"] = "fixture", ["capturedAt"] = Protocol.Now() };
        var candidate = original.DeepClone().AsObject(); candidate["workingTree"] = "modified";
        reviewed.Store.WriteJson(Path.Combine(reviewed.Store.RunDirectory(reviewed.RunId), "provenance.json"), ReviewProvenance.Compose(Sha, original, candidate));
        Check((await reviewed.Service.PreviewAsync(reviewed.RunId))["hasConfirmedP0"]?.GetValue<bool>() == true,
            "An explicit original-revision code-review P0 remains valid when a later candidate test or dirty worktree is recorded separately.");
    }

    private static async Task IssueManualActionsIndependentOfAnalysis()
    {
        foreach (var kind in new[] { "feature-research", "bug-investigation" })
        foreach (var report in new[] { "missing", "failed", "no-proposals" })
        {
            using var fixture = new Fixture(targetType: "issue");
            var saved = fixture.Store.ReadTask(fixture.RunId); saved["task"]!["actionKind"] = kind;
            fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "task.json"), saved);
            var task = saved["task"]!.AsObject();
            var status = new JsonObject { ["state"] = report == "missing" ? "running" : report == "failed" ? "failed" : "succeeded", ["exitCode"] = report == "missing" ? null : JsonValue.Create(report == "failed" ? 1 : 0) };
            fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "status.json"), status);
            JsonObject? result = null;
            if (report != "missing")
            {
                result = WorkflowResult.Failure(report == "failed" ? "failed" : "succeeded", report == "failed" ? "CLI_EXECUTION_FAILED" : null,
                    "The investigation did not supply an action draft.", task, report == "failed" ? 1 : 0, "reporting", expectedSchemaVersion: 3);
                fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "result.json"), result);
            }
            var preview = await fixture.Service.PreviewAsync(fixture.RunId);
            Check(preview["canComment"]?.GetValue<bool>() == true && preview["canClose"]?.GetValue<bool>() == true && preview["canApprove"]?.GetValue<bool>() == false,
                "Fixed issue comment/close controls use actual GitHub prerequisites, not the investigation state: " + kind + "/" + report);
            var invalidProposal = fixture.Draft("comment", "missing-issue-proposal", "Explicit comment."); invalidProposal["proposalId"] = "proposal-missing";
            await ThrowsAsync("RESULT_PROPOSAL_NOT_FOUND", () => fixture.Service.SubmitAsync(invalidProposal));
            var invalidFinding = fixture.Draft("comment", "missing-issue-finding", "Explicit comment."); invalidFinding["findingIds"] = new JsonArray("missing");
            await ThrowsAsync("RESULT_FINDING_NOT_FOUND", () => fixture.Service.SubmitAsync(invalidFinding));
            var noReason = fixture.Draft("close", "missing-issue-close-reason", ""); noReason.Remove("closeReason");
            await ThrowsAsync("CLOSE_REASON_REQUIRED", () => fixture.Service.SubmitAsync(noReason));
            var comment = await fixture.Service.SubmitAsync(fixture.Draft("comment", "manual-issue-comment", "A separately confirmed human explanation."));
            var close = await fixture.Service.SubmitAsync(fixture.Draft("close", "manual-issue-close", ""));
            Check(comment["status"]?.GetValue<string>() == "succeeded" && close["status"]?.GetValue<string>() == "succeeded" && fixture.Api.Writes.Count == 2 &&
                fixture.Api.Writes[0].Path == "/repos/example/project/issues/7/comments" && fixture.Api.Writes[1].Method == "PATCH" &&
                close["closeReason"]?.GetValue<string>() == "The target has been resolved.",
                "Manual issue comment and reasoned close must execute only their explicitly selected fixed effects.");
            Check(JsonNode.DeepEquals(fixture.Store.ReadStatus(fixture.RunId), status) && JsonNode.DeepEquals(fixture.Store.ReadResult(fixture.RunId), result),
                "Human GitHub actions must leave investigation state and AI result unchanged.");
        }
    }

    private static async Task SelectedFindingFeedbackIsExact()
    {
        using var fixture = new Fixture();
        V3Review(fixture, edit: model =>
        {
            var first = Suggestion(2, 2); first["id"] = "first-inline";
            var omitted = Suggestion(3, 3); omitted["id"] = "omitted-inline";
            model["review"]!["suggestions"] = new JsonArray(first, omitted);
            model["findings"] = new JsonArray(V3Finding("first", "P1", "first-inline"), V3Finding("general", "P2"), V3Finding("omitted", "P3", "omitted-inline"));
        });
        JsonObject Draft(string kind, string id)
        {
            var draft = fixture.Draft(kind, id, "Edited general feedback for the selected finding only.");
            draft["findingIds"] = new JsonArray("first", "general");
            var suggestion = Suggestion(2, 2); suggestion["id"] = "first-inline"; suggestion["body"] = "Edited inline explanation."; suggestion["replacement"] = "reviewed replacement";
            draft["suggestions"] = new JsonArray(suggestion);
            return draft;
        }
        var spoof = Draft("approve", "unselected-inline"); spoof["suggestions"]![0]!["id"] = "omitted-inline";
        await ThrowsAsync("INVALID_SUGGESTION", () => fixture.Service.SubmitAsync(spoof));
        var range = Draft("approve", "changed-inline-location"); range["suggestions"]![0]!["line"] = 3;
        await ThrowsAsync("INVALID_SUGGESTION", () => fixture.Service.SubmitAsync(range));
        var missing = Draft("approve", "missing-finding"); missing["findingIds"] = new JsonArray("unknown");
        await ThrowsAsync("RESULT_FINDING_NOT_FOUND", () => fixture.Service.SubmitAsync(missing));
        foreach (var kind in new[] { "approve", "requestChanges", "suggestChanges", "comment" })
        {
            var sent = await fixture.Service.SubmitAsync(Draft(kind, "selected-" + kind));
            var wire = fixture.Api.Writes.Last().Body;
            Check(sent["status"]?.GetValue<string>() == "succeeded" && wire["body"]?.GetValue<string>() == "Edited general feedback for the selected finding only." &&
                wire["comments"]!.AsArray().Count == 1 && wire["comments"]![0]!["body"]?.GetValue<string>() == "Edited inline explanation.\n\n```suggestion\nreviewed replacement\n```",
                "Every explicitly selected review decision submits exactly the chosen inline and general comments: " + kind);
            Check(!wire["comments"]![0]!.AsObject().ContainsKey("id") && !wire.ContainsKey("findingIds"), "Host finding identities never become arbitrary GitHub payload fields.");
        }
        var inlineOnly = Draft("requestChanges", "selected-inline-only"); inlineOnly["body"] = ""; inlineOnly["findingIds"] = new JsonArray("first");
        Check((await fixture.Service.SubmitAsync(inlineOnly))["status"]?.GetValue<string>() == "succeeded" && fixture.Api.Writes.Last().Body["body"]?.GetValue<string>() == "",
            "An explicitly chosen review decision may contain only selected inline comments without adding an unselected general comment.");
        var comment = Draft("comment", "unknown-selected-comment"); comment["body"] = "A distinct selected comment with inline feedback.";
        fixture.Api.LoseWriteResponse = true;
        var unknown = await fixture.Service.SubmitAsync(comment);
        var writeCount = fixture.Api.Writes.Count;
        Check(unknown["status"]?.GetValue<string>() == "unknown" && unknown["kind"]?.GetValue<string>() == "comment" && fixture.Api.Writes.Last().Body["event"]?.GetValue<string>() == "COMMENT",
            "Selected comment intent retains its public kind while using the GitHub COMMENT review endpoint for inline feedback.");
        var reconciled = await fixture.Service.SubmitAsync(comment);
        Check(reconciled["status"]?.GetValue<string>() == "succeeded" && fixture.Api.Writes.Count == writeCount, "Unknown mixed comments reconcile the review and its inline evidence without sending another review.");
    }

    private static async Task PrActionsWithFailedReports()
    {
        foreach (var state in new[] { "failed", "cancelled", "interrupted" })
        {
            using var fixture = new Fixture();
            var task = fixture.Store.ReadTask(fixture.RunId)["task"]!.AsObject();
            var result = WorkflowResult.Failure(state, "PERMISSION_DENIED", "Synthetic process failure", task);
            fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "result.json"), result);
            var preview = await fixture.Service.PreviewAsync(fixture.RunId);
            Check(new[] { "canApprove", "canRequestChanges", "canSuggestChanges", "canComment", "canClose" }.All(key => preview[key]!.GetValue<bool>()),
                "A failed historical report does not prohibit an explicit human PR action or invent a confirmed P0.");
            var submitted = await fixture.Service.SubmitAsync(fixture.Draft("approve", "manual-failed-report", "My own explicit review."));
            Check(submitted["status"]?.GetValue<string>() == "succeeded" && fixture.Api.Writes.Count == 1, "A human review needs neither successful CLI output nor an AI review proposal.");
        }
    }

    private static async Task ProposalAssociationPreservesWriteDeduplication()
    {
        using var fixture = new Fixture();
        var result = new JsonObject
        {
            ["summary"] = "Stored editable proposals.", ["needsReview"] = true, ["artifacts"] = new JsonArray(), ["validation"] = new JsonArray(),
            ["blockers"] = new JsonArray(), ["review"] = null,
            ["nextSteps"] = new JsonArray(new JsonObject { ["kind"] = "comment", ["reason"] = "First selection", ["body"] = "Original body." },
                new JsonObject { ["kind"] = "comment", ["reason"] = "Second selection", ["body"] = "Original body." })
        };
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "result.json"), result);
        var proposals = WorkflowResult.WithProposalIds(result)["nextSteps"]!.AsArray();
        var first = fixture.Draft("comment", "proposal-first", "    Reviewed edited body.\n");
        first["proposalId"] = proposals[0]!["proposalId"]!.DeepClone();
        var submitted = await fixture.Service.SubmitAsync(first);
        Check(submitted["proposalId"]?.GetValue<string>() == first["proposalId"]?.GetValue<string>() && fixture.Api.Writes.Single().Body["body"]?.GetValue<string>() == "    Reviewed edited body.\n",
            "The selected proposal identity must be recorded while preserving the user's exact edited comment text.");
        var duplicate = fixture.Draft("comment", "proposal-second", "    Reviewed edited body.\n");
        duplicate["proposalId"] = proposals[1]!["proposalId"]!.DeepClone();
        var reused = await fixture.Service.SubmitAsync(duplicate);
        Check(reused["operationId"]?.GetValue<string>() == submitted["operationId"]?.GetValue<string>() && fixture.Api.Writes.Count == 1,
            "Proposal metadata must not make otherwise identical confirmed writes publish twice.");
        var wrongKind = fixture.Draft("approve", "proposal-wrong", "Approve text."); wrongKind["proposalId"] = first["proposalId"]!.DeepClone();
        await ThrowsAsync("RESULT_PROPOSAL_NOT_FOUND", () => fixture.Service.SubmitAsync(wrongKind));
        var unknown = fixture.Draft("comment", "proposal-unknown", "Different body."); unknown["proposalId"] = "proposal-missing";
        await ThrowsAsync("RESULT_PROPOSAL_NOT_FOUND", () => fixture.Service.SubmitAsync(unknown));
        Check(fixture.Api.Writes.Count == 1, "Unknown or mismatched proposal identities must not send a GitHub request.");
    }

    private static async Task CurrentAccountAndOriginalSubmissionIdentity()
    {
        using var fixture = new Fixture();
        var saved = fixture.Store.ReadTask(fixture.RunId);
        saved["config"]!["githubAccount"] = "old-account";
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "task.json"), saved);
        void SelectAccount(string account) => fixture.Store.WriteJson(Path.Combine(fixture.Store.Root, "config.json"), new JsonObject { ["githubAccount"] = account });
        var openedAccounts = new List<string>();
        var service = new GitHubService(fixture.Store, config =>
        {
            var account = config["githubAccount"]!.GetValue<string>();
            openedAccounts.Add(account);
            return Task.FromResult(new GitHubSession(new HttpClient(fixture.Api, disposeHandler: false), account));
        });
        SelectAccount("reviewer");
        var preview = await service.PreviewAsync(fixture.RunId);
        Check(preview["account"]?.GetValue<string>() == "reviewer" && openedAccounts.Last() == "reviewer", "A new preview must use Settings even when the task snapshot saved a different account.");
        fixture.Api.LoseWriteResponse = true;
        var draft = fixture.Draft("comment", "account-unknown", "The confirmed account owns this comment.");
        var unknown = await service.SubmitAsync(draft);
        Check(unknown["status"]?.GetValue<string>() == "unknown" && openedAccounts.Last() == "reviewer", "A new write must use the current selected account and persist its identity before uncertainty.");
        SelectAccount("new-account");
        var confirmed = await service.ReconcileAsync(new JsonObject { ["runId"] = fixture.RunId, ["operationId"] = "account-unknown" });
        Check(confirmed["status"]?.GetValue<string>() == "succeeded" && openedAccounts.Last() == "reviewer" && fixture.Api.Writes.Count == 1,
            "After Settings changes, uncertain writes must be checked with the original submission account without another write.");
        var changed = await service.PreviewAsync(fixture.RunId);
        Check(changed["account"]?.GetValue<string>() == "new-account", "Later previews must immediately reflect the selected account.");
        var mismatch = await service.SubmitAsync(fixture.Draft("comment", "account-mismatch", "A separately confirmed comment."));
        Check(mismatch["error"]?["code"]?.GetValue<string>() == "GITHUB_IDENTITY_MISMATCH" && fixture.Api.Writes.Count == 1,
            "An old expectedAccount cannot authorize a new write after the selected account changes.");

        SelectAccount("reviewer");
        fixture.Api.ApplyWrite = false;
        var repeatedDraft = fixture.Draft("comment", "account-repeat", "An uncertain write with no visible result.");
        await service.SubmitAsync(repeatedDraft);
        SelectAccount("new-account");
        var repeated = await service.SubmitAsync(repeatedDraft);
        Check(repeated["status"]?.GetValue<string>() == "unknown" && openedAccounts.Last() == "reviewer" && fixture.Api.Writes.Count == 2,
            "Repeating a pending draft must also reconcile with its original account, without resending.");
        var historical = fixture.Store.ReadJson(fixture.OperationPath("account-repeat"))!;
        historical.Remove("account");
        fixture.Store.WriteJson(fixture.OperationPath("account-repeat"), historical);
        await service.ReconcileAsync(new JsonObject { ["runId"] = fixture.RunId, ["operationId"] = "account-repeat" });
        Check(openedAccounts.Last() == "reviewer" && fixture.Api.Writes.Count == 2,
            "Historical uncertain intent without account must retain its confirmed expectedAccount for read-only checks.");
        Check(fixture.Store.ReadTask(fixture.RunId)["config"]?["githubAccount"]?.GetValue<string>() == "old-account" &&
            new Configuration(fixture.Store).Read()["githubAccount"]?.GetValue<string>() == "new-account", "Submission and recovery must not rewrite the immutable task account or current Settings.");
    }

    private static async Task PrActionsIndependentOfExecution()
    {
        var states = new JsonObject[]
        {
            new() { ["state"] = "succeeded" }, new() { ["state"] = "succeeded", ["exitCode"] = null },
            new() { ["state"] = "succeeded", ["exitCode"] = 7 }, new() { ["state"] = "succeeded", ["exitCode"] = "0" },
            new() { ["state"] = "running", ["exitCode"] = 0 }, new() { ["state"] = "failed", ["exitCode"] = 0 },
            new() { ["state"] = "cancelled", ["exitCode"] = 0 }, new() { ["state"] = "interrupted", ["exitCode"] = 0 }
        };
        foreach (var status in states)
        {
            using var fixture = new Fixture();
            fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "status.json"), status);
            var preview = await fixture.Service.PreviewAsync(fixture.RunId);
            Check(new[] { "canApprove", "canRequestChanges", "canSuggestChanges", "canComment", "canClose" }.All(key => preview[key]?.GetValue<bool>() == true),
                "Fixed human PR controls do not depend on the model lifecycle or a result file: " + status.ToJsonString());
            var submitted = await fixture.Service.SubmitAsync(fixture.Draft("comment", "actual-status", "An explicit manual comment."));
            Check(submitted["status"]?.GetValue<string>() == "succeeded" && fixture.Api.Writes.Count == 1 && JsonNode.DeepEquals(fixture.Store.ReadStatus(fixture.RunId), status),
                "An explicit manual comment can publish while leaving the CLI task state unchanged.");
        }
    }

    private static async Task ProposalSuggestionIsolation()
    {
        using var fixture = new Fixture();
        var first = Suggestion(2, 2); first["id"] = "first";
        var second = Suggestion(3, 3); second["id"] = "second";
        var result = new JsonObject
        {
            ["summary"] = "Two independent review proposals.", ["review"] = new JsonObject { ["headSha"] = Sha, ["body"] = "Review", ["suggestions"] = new JsonArray(first, second) },
            ["nextSteps"] = new JsonArray(
                new JsonObject { ["kind"] = "requestChanges", ["reason"] = "First", ["body"] = "First review.", ["suggestionIds"] = new JsonArray("first") },
                new JsonObject { ["kind"] = "requestChanges", ["reason"] = "Second", ["body"] = "Second review.", ["suggestionIds"] = new JsonArray("second") },
                new JsonObject { ["kind"] = "requestChanges", ["reason"] = "No inline changes", ["body"] = "General review.", ["suggestionIds"] = new JsonArray() },
                new JsonObject { ["kind"] = "requestChanges", ["reason"] = "Equivalent first", ["body"] = "First review.", ["suggestionIds"] = new JsonArray("first") })
        };
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "result.json"), result);
        var proposals = WorkflowResult.WithProposalIds(result)["nextSteps"]!.AsArray();
        JsonObject Draft(string operation, int proposalIndex, JsonObject selection)
        {
            var draft = fixture.Draft("requestChanges", operation, "The reviewed body.");
            draft["proposalId"] = proposals[proposalIndex]!["proposalId"]!.DeepClone();
            draft["suggestions"] = new JsonArray(selection.DeepClone());
            return draft;
        }
        await ThrowsAsync("INVALID_SUGGESTION", () => fixture.Service.SubmitAsync(Draft("cross-proposal", 1, first)));
        await ThrowsAsync("INVALID_SUGGESTION", () => fixture.Service.SubmitAsync(Draft("empty-selection", 2, first)));
        var wrongRange = first.DeepClone().AsObject(); wrongRange["startLine"] = 3; wrongRange["line"] = 3;
        await ThrowsAsync("INVALID_SUGGESTION", () => fixture.Service.SubmitAsync(Draft("range-spoof", 0, wrongRange)));
        var wrongId = first.DeepClone().AsObject(); wrongId["id"] = "missing";
        await ThrowsAsync("INVALID_SUGGESTION", () => fixture.Service.SubmitAsync(Draft("identity-spoof", 0, wrongId)));
        var malformedId = first.DeepClone().AsObject(); malformedId["id"] = "bad/id";
        await ThrowsAsync("INVALID_SUGGESTION", () => fixture.Service.SubmitAsync(Draft("invalid-identity", 0, malformedId)));
        Check(fixture.Api.Writes.Count == 0, "Cross-proposal identities or altered source locations must never publish.");
        var edited = first.DeepClone().AsObject(); edited["body"] = "Reviewed explanation."; edited["replacement"] = "user-edited value";
        var published = await fixture.Service.SubmitAsync(Draft("selected-first", 0, edited));
        var wire = fixture.Api.Writes.Single().Body["comments"]![0]!.AsObject();
        Check(published["status"]?.GetValue<string>() == "succeeded" && wire["body"]?.GetValue<string>() == "Reviewed explanation.\n\n```suggestion\nuser-edited value\n```" && !wire.ContainsKey("id"),
            "The user may edit an associated suggestion while the GitHub payload retains only its approved range and text.");
        var repeated = await fixture.Service.SubmitAsync(Draft("selected-equivalent", 3, edited));
        Check(repeated["operationId"]?.GetValue<string>() == "selected-first" && fixture.Api.Writes.Count == 1, "Equivalent suggestion writes must still deduplicate across different proposal identities.");

        using var legacy = new Fixture();
        var legacyResult = new JsonObject
        {
            ["summary"] = "Historical result without suggestion IDs.",
            ["review"] = new JsonObject { ["headSha"] = Sha, ["body"] = "Old review", ["suggestions"] = new JsonArray(Suggestion(2, 2), Suggestion(3, 3)) },
            ["nextSteps"] = new JsonArray(new JsonObject { ["kind"] = "requestChanges", ["reason"] = "Historical review", ["body"] = "Review." })
        };
        legacy.Store.WriteJson(Path.Combine(legacy.Store.RunDirectory(legacy.RunId), "result.json"), legacyResult);
        var legacyDraft = legacy.Draft("requestChanges", "legacy-selection", "Reviewed historical draft.");
        legacyDraft["proposalId"] = WorkflowResult.WithProposalIds(legacyResult)["nextSteps"]![0]!["proposalId"]!.DeepClone();
        legacyDraft["suggestions"] = new JsonArray(Suggestion(3, 3));
        Check((await legacy.Service.SubmitAsync(legacyDraft))["status"]?.GetValue<string>() == "succeeded" && legacy.Api.Writes.Count == 1,
            "Historical proposals without references may still select uniquely matching saved source ranges.");
    }

    private static async Task StructuredFailureReportsCanBeCommented()
    {
        foreach (var outcome in new[] { "completed", "blocked", "failed" })
        {
            using var fixture = new Fixture();
            var task = fixture.Store.ReadTask(fixture.RunId)["task"]!.AsObject();
            var model = new JsonObject
            {
                ["schemaVersion"] = 2, ["outcome"] = outcome, ["phase"] = "reporting", ["summary"] = "The local report describes a failing product check.",
                ["assessment"] = new JsonObject { ["subject"] = "original-pr", ["status"] = "failed", ["summary"] = "Observed product regression.", ["revisionSha"] = Sha },
                ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(),
                ["validation"] = new JsonArray(WorkflowResult.RequiredChecks("pr-review").Select(id => (JsonNode?)new JsonObject
                {
                    ["id"] = id, ["name"] = id, ["status"] = "passed", ["required"] = true, ["details"] = "Completed the evidence collection.", ["evidence"] = new JsonArray("A controlled offline observation.")
                }).ToArray()),
                ["diagnostics"] = new JsonArray(), ["review"] = null, ["needsReview"] = true,
                ["nextActions"] = new JsonArray(new JsonObject { ["kind"] = "comment", ["reason"] = "Share this explicit failure report.", ["body"] = "The product check failed; inspect the attached evidence." })
            };
            var result = WorkflowResult.FromModel(model.ToJsonString(), task, "succeeded", 0, null, null);
            fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "result.json"), result);
            var preview = await fixture.Service.PreviewAsync(fixture.RunId);
            Check(preview["canComment"]?.GetValue<bool>() == true && preview["canApprove"]?.GetValue<bool>() == true,
                "A structured negative report can be shared while manual approval remains the user's separate choice: " + outcome);
            var draft = fixture.Draft("comment", "report-comment", "The product check failed; inspect the attached evidence.");
            draft["proposalId"] = WorkflowResult.WithProposalIds(result)["nextActions"]![0]!["proposalId"]!.DeepClone();
            Check((await fixture.Service.SubmitAsync(draft))["status"]?.GetValue<string>() == "succeeded" && fixture.Api.Writes.Count == 1,
                "The exact confirmed negative-report comment must reach the fixed conversation endpoint.");
        }
    }

    private static async Task ContextUsesSelectedSession()
    {
        using var fixture = new Fixture();
        var task = fixture.Store.ReadTask(fixture.RunId)["task"]!.AsObject();
        await fixture.Service.VerifyTaskContextAsync(task, new JsonObject { ["githubAccount"] = "reviewer" });
        Check(fixture.Api.Writes.Count == 0, "PR context verification must only read through the selected session.");
        var unavailable = new GitHubService(fixture.Store, _ => throw new ProtocolException("GITHUB_AUTH_REQUIRED", "Select a local gh account."));
        await ThrowsAsync("GITHUB_AUTH_REQUIRED", () => unavailable.PreviewAsync(fixture.RunId));
        try { await unavailable.VerifyTaskContextAsync(task, new JsonObject()); }
        catch (ProtocolException ex) when (ex.Code == "GITHUB_AUTH_REQUIRED")
        {
            await unavailable.VerifyTaskContextAsync(new JsonObject { ["repository"] = "example/project" }, new JsonObject());
            await unavailable.VerifyTaskContextAsync(new JsonObject { ["repository"] = "example/project", ["target"] = new JsonObject { ["type"] = "pr", ["number"] = 7 }, ["actionKind"] = "issue-fix" }, new JsonObject());
            return;
        }
        throw new InvalidOperationException("PR revision verification must require the saved gh account; no anonymous HTTP fallback is permitted.");
    }

    private static void DiffLocations()
    {
        var lines = GitHubDiff.ParseLines("@@ -1,3 +1,4 @@\n one\n-two\n+replacement\n+extra\n three\n");
        Check(lines.Count == 4, "Complete RIGHT context and added lines must be available.");
        var files = new JsonArray(new JsonObject { ["path"] = "a.cs", ["status"] = "modified", ["lines"] = lines });
        var selection = Suggestion(2, 3);
        Check(GitHubDiff.ValidateSuggestion(selection, files) == "replacement\nextra", "The original source must correspond to the selected right-side range.");
        Check(GitHubDiff.ParseLines("@@ -1,2 +1,2 @@\n one\n").Count == 0, "Truncated hunks must not be accepted.");
        Check(GitHubDiff.ParseLines("@@ -0,0 +1 @@\n+new\n\\ No newline at end of file\n").Count == 1, "A complete added-file hunk is valid.");
        var separated = GitHubDiff.ParseLines("@@ -1 +1 @@\n one\n@@ -2 +2 @@\n two\n");
        var separatedFiles = new JsonArray(new JsonObject { ["path"] = "a.cs", ["status"] = "modified", ["lines"] = separated });
        Throws("INVALID_DIFF_LOCATION", () => GitHubDiff.ValidateSuggestion(Suggestion(1, 2), separatedFiles));
        Throws("INVALID_DIFF_LOCATION", () => GitHubDiff.ValidateSuggestion(Suggestion(10, 11), files));
    }

    private static async Task PreviewAndFixedReview()
    {
        using var fixture = new Fixture();
        var preview = await fixture.Service.PreviewAsync(fixture.RunId);
        Check(preview["account"]!.GetValue<string>() == "reviewer" && preview["canApprove"]!.GetValue<bool>(), "The preview must use the actual authenticated account.");
        Check(fixture.Api.Writes.Count == 0, "Preview must never write.");
        fixture.Api.BeforeWrite = () =>
        {
            var persisted = fixture.Store.ReadJson(fixture.OperationPath("approve-0001"));
            Check(persisted?["status"]?.GetValue<string>() == "submitting", "Intent must be flushed before HTTP POST.");
        };
        var draft = fixture.Draft("approve", "approve-0001", "Reviewed.\r\nLooks good.");
        var result = await fixture.Service.SubmitAsync(draft);
        Check(result["status"]!.GetValue<string>() == "succeeded", "Approve must succeed against the fake API.");
        var write = fixture.Api.Writes.Single();
        Check(write.Method == "POST" && write.Path == "/repos/example/project/pulls/7/reviews", "Approve must use only the fixed review endpoint.");
        Check(write.Body["event"]!.GetValue<string>() == "APPROVE" && write.Body["commit_id"]!.GetValue<string>() == Sha, "Approve must use APPROVE and the saved SHA.");
        Check(write.Body["body"]!.GetValue<string>() == "Reviewed.\nLooks good.", "Newlines must have deterministic normalization.");
        var duplicate = fixture.Draft("approve", "approve-0002", "Reviewed.\nLooks good.");
        var repeated = await fixture.Service.SubmitAsync(duplicate);
        Check(repeated["operationId"]!.GetValue<string>() == "approve-0001" && fixture.Api.Writes.Count == 1, "Equivalent drafts from another browser must not publish again.");
        await fixture.Service.SubmitAsync(draft);
        Check(fixture.Api.Writes.Count == 1, "A repeated operationId must not publish again.");
        await fixture.Service.SubmitAsync(fixture.Draft("approve", "APPROVE-0001", "Reviewed.\nLooks good."));
        Check(fixture.Api.Writes.Count == 1, "Windows filename casing must not bypass operation ID deduplication.");
        await ThrowsAsync("OPERATION_ID_CONFLICT", () => fixture.Service.SubmitAsync(fixture.Draft("approve", "approve-0001", "different")));
        Check(fixture.Store.ReadStatus(fixture.RunId)["state"]!.GetValue<string>() == "succeeded", "GitHub operations must not change CLI state.");
    }

    private static async Task SuggestionReview()
    {
        using var fixture = new Fixture();
        var draft = fixture.Draft("requestChanges", "review-00001", "Please address these findings.");
        var suggestion = Suggestion(2, 3);
        suggestion["body"] = "Use a clear value.";
        suggestion["replacement"] = "fixed\nvalue";
        draft["suggestions"] = new JsonArray(suggestion);
        var result = await fixture.Service.SubmitAsync(draft);
        Check(result["status"]!.GetValue<string>() == "succeeded", "Valid suggestions must publish as one review.");
        var write = fixture.Api.Writes.Single();
        Check(write.Body["event"]!.GetValue<string>() == "REQUEST_CHANGES", "Request changes must not become a normal comment.");
        var comment = write.Body["comments"]![0]!.AsObject();
        Check(comment["line"]!.GetValue<int>() == 3 && comment["start_line"]!.GetValue<int>() == 2 && comment["start_side"]!.GetValue<string>() == "RIGHT", "Multi-line suggestions need both RIGHT range endpoints.");
        Check(comment["body"]!.GetValue<string>() == "Use a clear value.\n\n```suggestion\nfixed\nvalue\n```", "Only the selected text should form the GitHub suggestion.");
        Check(result["suggestions"]![0]!["original"]!.GetValue<string>() == "replacement\nextra", "The persisted operation must retain the original code evidence.");
        using var second = new Fixture();
        var suggestionDraft = second.Draft("suggestChanges", "suggest-0001", "Suggested edits.");
        suggestionDraft["suggestions"] = new JsonArray(Suggestion(2, 2));
        await second.Service.SubmitAsync(suggestionDraft);
        Check(second.Api.Writes.Single().Body["event"]!.GetValue<string>() == "COMMENT", "Suggested changes use COMMENT review, never APPROVE.");
    }

    private static async Task StaleAndInvalidInputs()
    {
        using var fixture = new Fixture();
        fixture.Api.Head = OtherSha;
        var stale = await fixture.Service.SubmitAsync(fixture.Draft("approve", "stale-00001", ""));
        Check(stale["error"]!["code"]!.GetValue<string>() == "STALE_CONTEXT" && fixture.Api.Writes.Count == 0, "A changed HEAD must prevent a write.");
        fixture.Api.Head = Sha;
        var overrideTarget = fixture.Draft("close", "override-001", "");
        overrideTarget["repository"] = "other/target";
        await ThrowsAsync("INVALID_REQUEST", () => fixture.Service.SubmitAsync(overrideTarget));
        var mismatched = fixture.Draft("approve", "mismatch-001", "");
        mismatched["expectedHeadSha"] = OtherSha;
        await ThrowsAsync("STALE_CONTEXT", () => fixture.Service.SubmitAsync(mismatched));
        var invalid = fixture.Draft("suggestChanges", "bad-line-001", "A suggestion.");
        invalid["suggestions"] = new JsonArray(Suggestion(30, 31));
        var invalidResult = await fixture.Service.SubmitAsync(invalid);
        Check(invalidResult["error"]!["code"]!.GetValue<string>() == "INVALID_DIFF_LOCATION", "Lines outside the diff must fail.");
        var wrongSide = fixture.Draft("suggestChanges", "bad-side-001", "A suggestion.");
        var left = Suggestion(2, 2); left["side"] = "LEFT";
        wrongSide["suggestions"] = new JsonArray(left);
        await ThrowsAsync("INVALID_SUGGESTION", () => fixture.Service.SubmitAsync(wrongSide));
        var overlaps = fixture.Draft("suggestChanges", "overlap-0001", "A suggestion.");
        overlaps["suggestions"] = new JsonArray(Suggestion(1, 2), Suggestion(2, 3));
        await ThrowsAsync("INVALID_SUGGESTION", () => fixture.Service.SubmitAsync(overlaps));
        await ThrowsAsync("INVALID_REQUEST", () => fixture.Service.SubmitAsync(fixture.Draft("comment", "../bad-path", "text")));
        await ThrowsAsync("INVALID_OPERATION", () => fixture.Service.SubmitAsync(fixture.Draft("merge", "merge-00001", "")));
        Check(fixture.Api.Writes.Count == 0, "Validation failures must not send any HTTP writes.");

        using var racing = new Fixture();
        racing.Api.ChangeHeadAfterCollectionRead = true;
        var race = await racing.Service.SubmitAsync(racing.Draft("approve", "race-000001", ""));
        Check(race["status"]!.GetValue<string>() == "failed" && racing.Api.Writes.Count == 0, "HEAD must be rechecked after fetching the baseline.");
    }

    private static async Task UnknownAndReconciliation()
    {
        using var fixture = new Fixture();
        fixture.Api.LoseWriteResponse = true;
        var draft = fixture.Draft("approve", "unknown-0001", "Approved after analysis.");
        var uncertain = await fixture.Service.SubmitAsync(draft);
        Check(uncertain["status"]!.GetValue<string>() == "unknown" && fixture.Api.Writes.Count == 1, "A lost response must record unknown even if the API applied the write.");
        var reconciled = await fixture.Service.ReconcileAsync(new JsonObject { ["runId"] = fixture.RunId, ["operationId"] = "unknown-0001" });
        Check(reconciled["status"]!.GetValue<string>() == "succeeded" && reconciled["reconciled"]!.GetValue<bool>(), "A unique account/body/SHA/state match outside the baseline confirms a review.");
        Check(fixture.Api.Writes.Count == 1, "Reconciliation must only read.");

        using var inline = new Fixture();
        inline.Api.LoseWriteResponse = true;
        var inlineDraft = inline.Draft("requestChanges", "unknown-line", "Please apply the selected suggestion.");
        inlineDraft["suggestions"] = new JsonArray(Suggestion(2, 3));
        await inline.Service.SubmitAsync(inlineDraft);
        var inlineReconciled = await inline.Service.ReconcileAsync(new JsonObject { ["runId"] = inline.RunId, ["operationId"] = "unknown-line" });
        Check(inlineReconciled["status"]!.GetValue<string>() == "succeeded" && inline.Api.Writes.Count == 1, "Reconciliation must confirm the selected multiline comments as well as the review body and SHA.");

        using var noResult = new Fixture();
        noResult.Api.ExistingReviews.Add(FakeGitHub.Review("11", "Already reviewed.", "APPROVED", Sha));
        noResult.Api.LoseWriteResponse = true;
        noResult.Api.ApplyWrite = false;
        var unconfirmedDraft = noResult.Draft("approve", "unknown-0002", "Already reviewed.");
        var unknown = await noResult.Service.SubmitAsync(unconfirmedDraft);
        Check(unknown["status"]!.GetValue<string>() == "unknown", "A timed-out write with no visible result is still unknown.");
        var retriedClick = await noResult.Service.SubmitAsync(unconfirmedDraft);
        Check(retriedClick["status"]!.GetValue<string>() == "unknown" && noResult.Api.Writes.Count == 1, "Old identical reviews in the baseline cannot prove success, and repeated clicks must not resend.");
        await ThrowsAsync("OPERATION_UNKNOWN", () => noResult.Service.SubmitAsync(noResult.Draft("comment", "unknown-0003", "A different body")));
        Check(noResult.Api.Writes.Count == 1, "Unresolved writes must also prevent edited drafts from bypassing deduplication.");

        using var crashed = new Fixture();
        var success = await crashed.Service.SubmitAsync(crashed.Draft("comment", "crashed-0001", "A persisted comment."));
        var record = crashed.Store.ReadJson(crashed.OperationPath("crashed-0001"))!;
        record["status"] = "submitting"; record["remoteId"] = null;
        crashed.Store.WriteJson(crashed.OperationPath("crashed-0001"), record);
        Check(crashed.Service.List(crashed.RunId)["operations"]![0]!["status"]!.GetValue<string>() == "unknown", "A leftover submitting record without a lock owner becomes unknown.");
        var recovered = await crashed.Service.ReconcileAsync(new JsonObject { ["runId"] = crashed.RunId, ["operationId"] = "crashed-0001" });
        Check(recovered["status"]!.GetValue<string>() == "succeeded" && crashed.Api.Writes.Count == 1, "A recovered native host verifies the existing comment without posting again.");
    }

    private static async Task CloseAndComment()
    {
        using var fixture = new Fixture(targetType: "issue");
        var comment = await fixture.Service.SubmitAsync(fixture.Draft("comment", "comment-0001", "The selected full text."));
        Check(comment["status"]!.GetValue<string>() == "succeeded", "An ordinary issue comment must work.");
        var commentWrite = fixture.Api.Writes.Single();
        Check(commentWrite.Path == "/repos/example/project/issues/7/comments" && commentWrite.Body.Count == 1, "An ordinary comment uses the fixed issue conversation endpoint and only body.");
        var closed = await fixture.Service.SubmitAsync(fixture.Draft("close", "close-000001", ""));
        var closeWrite = fixture.Api.Writes.Last();
        Check(closed["status"]!.GetValue<string>() == "succeeded" && closeWrite.Method == "PATCH" && closeWrite.Path == "/repos/example/project/issues/7", "Closing an issue must use the issue endpoint.");
        Check(closeWrite.Body.Count == 1 && closeWrite.Body["state"]!.GetValue<string>() == "closed", "Close must not append a comment or merge.");
        var again = await fixture.Service.SubmitAsync(fixture.Draft("close", "close-000002", ""));
        Check(again["status"]!.GetValue<string>() == "succeeded" && fixture.Api.Writes.Count == 2, "The same closed effect must not be sent again.");
        await ThrowsAsync("INVALID_OPERATION", () => fixture.Service.SubmitAsync(fixture.Draft("close", "close-000003", "Unexpected comment")));
        var missingReason = fixture.Draft("close", "close-no-reason", ""); missingReason.Remove("closeReason");
        await ThrowsAsync("CLOSE_REASON_REQUIRED", () => fixture.Service.SubmitAsync(missingReason));
        Check(closed["closeReason"]?.GetValue<string>() == "The target has been resolved." && !closeWrite.Body.ContainsKey("closeReason"), "A closing reason is retained as operation metadata without an implicit comment or unsupported GitHub field.");
        using var pr = new Fixture();
        await pr.Service.SubmitAsync(pr.Draft("close", "close-pr-001", ""));
        Check(pr.Api.Writes.Single().Path == "/repos/example/project/pulls/7", "Closing a PR must preserve the saved PR target type.");
    }

    private static async Task PermissionChecks()
    {
        using var fixture = new Fixture();
        fixture.Api.Author = "reviewer";
        var own = await fixture.Service.SubmitAsync(fixture.Draft("approve", "own-pr-0001", ""));
        Check(own["status"]!.GetValue<string>() == "failed" && fixture.Api.Writes.Count == 0, "Self-approval is unavailable.");
        fixture.Api.Author = "author"; fixture.Api.WritePermission = false; fixture.Api.Locked = true;
        var locked = await fixture.Service.SubmitAsync(fixture.Draft("comment", "locked-0001", "Text"));
        Check(locked["status"]!.GetValue<string>() == "failed", "A locked conversation without write permission is unavailable.");
        fixture.Api.Locked = false;
        var deniedClose = await fixture.Service.SubmitAsync(fixture.Draft("close", "denied-0001", ""));
        Check(deniedClose["status"]!.GetValue<string>() == "failed", "A non-author reader cannot close the target.");
        fixture.Api.Archived = true;
        var archived = await fixture.Service.SubmitAsync(fixture.Draft("approve", "archive-001", ""));
        Check(archived["status"]!.GetValue<string>() == "failed" && fixture.Api.Writes.Count == 0, "Archived repositories must reject writes before POST.");
        fixture.Api.Archived = false;
        var changedAccount = fixture.Draft("comment", "account-0001", "Text");
        changedAccount["expectedAccount"] = "someone-else";
        var identityMismatch = await fixture.Service.SubmitAsync(changedAccount);
        Check(identityMismatch["error"]!["code"]!.GetValue<string>() == "GITHUB_IDENTITY_MISMATCH" && fixture.Api.Writes.Count == 0, "An account change after preview must stop the write.");
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(fixture.RunId), "status.json"), new JsonObject { ["state"] = "running" });
        Check((await fixture.Service.SubmitAsync(fixture.Draft("comment", "active-0001", "Text")))["status"]?.GetValue<string>() == "succeeded" && fixture.Api.Writes.Count == 1,
            "The user may explicitly comment while a local analysis is active; no model-driven write is implied.");

        using var managed = new Fixture();
        var managedService = new GitHubService(managed.Store, _ => Task.FromResult(new GitHubSession(new HttpClient(managed.Api, disposeHandler: false), "enterprise_user")));
        var managedDraft = managed.Draft("comment", "managed-0001", "Comment from the selected managed account.");
        managedDraft["expectedAccount"] = "enterprise_user";
        var managedResult = await managedService.SubmitAsync(managedDraft);
        Check(managedResult["status"]!.GetValue<string>() == "succeeded" && managed.Api.Writes.Count == 1, "Managed accounts with underscores must be accepted by the preview identity check.");
    }

    private static async Task AbandonedPreparation()
    {
        using var fixture = new Fixture();
        fixture.Store.WriteJson(fixture.OperationPath("prepare-001"), new JsonObject { ["operationId"] = "prepare-001", ["runId"] = fixture.RunId, ["status"] = "prepared", ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["kind"] = "comment", ["body"] = "Never sent." });
        var listed = fixture.Service.List(fixture.RunId)["operations"]![0]!.AsObject();
        Check(listed["status"]!.GetValue<string>() == "failed" && listed["error"]!["code"]!.GetValue<string>() == "OPERATION_NOT_SUBMITTED", "Interrupted preparation is safely distinguishable from an uncertain write.");
        Check(fixture.Api.Writes.Count == 0, "Listing a recovered draft must not publish.");
        await fixture.Service.SubmitAsync(fixture.Draft("comment", "prepare-002", "Never sent."));
        Check(fixture.Api.Writes.Count == 1, "After confirmed preparation-only failure, a new explicit operation may publish.");
    }

    private static async Task MaintenanceAndBoundedHistory()
    {
        using var fixture = new Fixture();
        await fixture.Service.VerifyTaskContextAsync(new JsonObject { ["repository"] = "example/project" }, new JsonObject());
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            fixture.Store.WriteJson(Path.Combine(fixture.Store.Root, "maintenance.json"), new JsonObject { ["pid"] = process.Id, ["startTimeUtc"] = process.StartTime.ToUniversalTime().ToString("O") });
            await ThrowsAsync("HOST_MAINTENANCE", () => fixture.Service.SubmitAsync(fixture.Draft("comment", "maintain-001", "Do not send during installation.")));
            Check(!File.Exists(fixture.OperationPath("maintain-001")) && fixture.Api.Writes.Count == 0, "The maintenance admission gate must precede both durable preparation and network write.");
        }
        for (var index = 0; index < 120; index++)
        {
            var id = "history-" + index.ToString("D4");
            fixture.Store.WriteJson(fixture.OperationPath(id), new JsonObject { ["runId"] = fixture.RunId, ["operationId"] = id, ["status"] = "succeeded", ["kind"] = "comment", ["body"] = new string('x', 5000), ["createdAt"] = DateTimeOffset.UtcNow.AddSeconds(index).ToString("O") });
        }
        var history = fixture.Service.List(fixture.RunId);
        Check(history["truncated"]!.GetValue<bool>() && history["totalCount"]!.GetValue<int>() == 120 && history["operations"]!.AsArray().Count == 100, "Large histories must return bounded records with explicit truncation.");
        Check(history["operations"]![0]!["bodyTruncated"]!.GetValue<bool>() && Encoding.UTF8.GetByteCount(history.ToJsonString()) < Protocol.MaxOutputBytes, "Long saved bodies must not break the native framing limit.");
    }

    private static JsonObject Suggestion(int start, int end) => new() { ["path"] = "a.cs", ["startLine"] = start, ["line"] = end, ["side"] = "RIGHT", ["body"] = "", ["replacement"] = "new value" };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(string code, Action action)
    {
        try { action(); } catch (ProtocolException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected ProtocolException " + code);
    }
    private static async Task ThrowsAsync(string code, Func<Task<JsonObject>> action)
    {
        try { await action(); } catch (ProtocolException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected ProtocolException " + code);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pulse-github-tests-" + Guid.NewGuid().ToString("N"));
        public Store Store { get; }
        public string RunId { get; } = Guid.NewGuid().ToString("D");
        public FakeGitHub Api { get; }
        public GitHubService Service { get; }
        public Fixture(string targetType = "pr")
        {
            Store = new Store(root);
            Api = new FakeGitHub { TargetType = targetType };
            Service = new GitHubService(Store, _ => Task.FromResult(new GitHubSession(new HttpClient(Api, disposeHandler: false), "reviewer")));
            Store.WriteJson(Path.Combine(Store.RunDirectory(RunId), "task.json"), new JsonObject
            {
                ["runId"] = RunId,
                ["task"] = new JsonObject { ["repository"] = "example/project", ["target"] = new JsonObject { ["type"] = targetType, ["number"] = 7 }, ["expectedHeadSha"] = targetType == "pr" ? Sha : null, ["actionKind"] = targetType == "pr" ? "pr-review" : "issue-fix" },
                ["config"] = new JsonObject { ["githubAccount"] = "reviewer" }
            });
            Store.WriteJson(Path.Combine(Store.RunDirectory(RunId), "status.json"), new JsonObject { ["state"] = "succeeded", ["exitCode"] = 0 });
        }
        public JsonObject Draft(string kind, string operationId, string body)
        {
            var draft = new JsonObject { ["runId"] = RunId, ["operationId"] = operationId, ["kind"] = kind, ["body"] = body, ["expectedAccount"] = "reviewer" };
            if (kind == "close") draft["closeReason"] = "The target has been resolved.";
            return draft;
        }
        public string OperationPath(string operationId) => Path.Combine(Store.RunDirectory(RunId), "operations", operationId + ".json");
        public void Dispose()
        {
            Api.Dispose();
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public string TargetType { get; init; } = "pr";
        public string Head { get; set; } = Sha;
        public string State { get; set; } = "open";
        public string Author { get; set; } = "author";
        public bool WritePermission { get; set; } = true;
        public bool Locked { get; set; }
        public bool Archived { get; set; }
        public bool LoseWriteResponse { get; set; }
        public bool ApplyWrite { get; set; } = true;
        public bool ChangeHeadAfterCollectionRead { get; set; }
        public Action? BeforeWrite { get; set; }
        public Action? BeforeCollectionRead { get; set; }
        public JsonArray ExistingReviews { get; } = [];
        private JsonArray ExistingComments { get; } = [];
        private readonly Dictionary<string, JsonArray> commentsByReview = [];
        public List<(string Method, string Path, JsonObject Body)> Writes { get; } = [];
        private int nextId = 100;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri!.Host == "api.github.com" && request.RequestUri.Scheme == "https", "All requests must use fixed GitHub HTTPS API URLs.");
            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (path == "/repos/example/project") return Json(new JsonObject { ["full_name"] = "example/project", ["private"] = false, ["archived"] = Archived, ["permissions"] = new JsonObject { ["pull"] = true, ["push"] = WritePermission } });
                if (path is "/repos/example/project/pulls/7" or "/repos/example/project/issues/7") return Json(Target());
                if (path == "/repos/example/project/pulls/7/files") return Json(new JsonArray(new JsonObject { ["filename"] = "a.cs", ["status"] = "modified", ["patch"] = "@@ -1,3 +1,4 @@\n one\n-two\n+replacement\n+extra\n three\n" }));
                if (path == "/repos/example/project/pulls/7/reviews")
                {
                    BeforeCollectionRead?.Invoke();
                    if (ChangeHeadAfterCollectionRead) Head = OtherSha;
                    return Json(ExistingReviews);
                }
                if (path == "/repos/example/project/issues/7/comments") return Json(ExistingComments);
                if (path.StartsWith("/repos/example/project/pulls/7/reviews/", StringComparison.Ordinal) && path.EndsWith("/comments", StringComparison.Ordinal))
                {
                    var id = path.Split('/')[7];
                    return Json(commentsByReview.GetValueOrDefault(id) ?? []);
                }
                throw new InvalidOperationException("Unexpected GET " + path);
            }
            BeforeWrite?.Invoke();
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Writes.Add((request.Method.Method, path, body.DeepClone().AsObject()));
            var idText = (++nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            JsonObject response;
            if (path == "/repos/example/project/pulls/7/reviews" && request.Method == HttpMethod.Post)
            {
                var reviewEvent = body["event"]!.GetValue<string>();
                response = Review(idText, body["body"]!.GetValue<string>(), reviewEvent switch { "APPROVE" => "APPROVED", "REQUEST_CHANGES" => "CHANGES_REQUESTED", _ => "COMMENTED" }, body["commit_id"]!.GetValue<string>());
                if (ApplyWrite)
                {
                    ExistingReviews.Add(response.DeepClone());
                    commentsByReview[idText] = body["comments"]?.DeepClone().AsArray() ?? [];
                }
            }
            else if (path == "/repos/example/project/issues/7/comments" && request.Method == HttpMethod.Post)
            {
                response = new JsonObject { ["id"] = int.Parse(idText), ["body"] = body["body"]!.DeepClone(), ["user"] = new JsonObject { ["login"] = "reviewer" }, ["html_url"] = "https://github.com/example/project/issues/7#issuecomment-" + idText };
                if (ApplyWrite) ExistingComments.Add(response.DeepClone());
            }
            else if ((path is "/repos/example/project/pulls/7" or "/repos/example/project/issues/7") && request.Method == HttpMethod.Patch)
            {
                if (ApplyWrite) State = "closed";
                response = Target();
            }
            else throw new InvalidOperationException("Unexpected write " + path);
            if (LoseWriteResponse) throw new HttpRequestException("Simulated lost response; this text must not reach persisted records.");
            return Json(response);
        }
        private JsonObject Target() => new()
        {
            ["id"] = 7, ["number"] = 7, ["title"] = "Test target", ["state"] = State, ["head"] = new JsonObject { ["sha"] = Head },
            ["base"] = new JsonObject { ["repo"] = new JsonObject { ["full_name"] = "example/project" } },
            ["user"] = new JsonObject { ["login"] = Author }, ["locked"] = Locked, ["changed_files"] = 1,
            ["html_url"] = "https://github.com/example/project/" + (TargetType == "pr" ? "pull" : "issues") + "/7"
        };
        public static JsonObject Review(string id, string body, string state, string commit) => new()
        {
            ["id"] = int.Parse(id), ["body"] = body, ["state"] = state, ["commit_id"] = commit,
            ["user"] = new JsonObject { ["login"] = "reviewer" }, ["html_url"] = "https://github.com/example/project/pull/7#pullrequestreview-" + id
        };
        private static HttpResponseMessage Json(JsonNode node) => new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
