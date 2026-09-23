using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Scope, result semantics and publication boundaries; no CLI, build or network dependencies.</summary>
internal static class ScopedReviewScenarios
{
    private static readonly string Sha = new('a', 40);

    internal static Task RunAllAsync()
    {
        ScopeIsAcceptedAndImmutable();
        InternalVerificationCannotBeSpoofed();
        ScopeControlsChecksAndPrompt();
        ConclusionAndEvidenceAreIndependent();
        InvalidEvidenceCannotMasqueradeAsVerified();
        StandaloneVerificationDoesNotInventReview();
        RecommendationFitsFollowUpContext();
        SnapshotIncludesSharedSourceProvenance();
        Console.WriteLine("PASS scoped review: accepted presets, internal follow-up boundary, fixed checks, attributed evidence, grounded recommendations and ordinary review eligibility (offline)");
        return Task.CompletedTask;
    }

    private static void ScopeIsAcceptedAndImmutable()
    {
        foreach (var mode in ReviewModes.Values)
        {
            var task = ReviewTask(mode); var before = task.ToJsonString();
            var accepted = Protocol.ValidateTask(task);
            Check(ReviewModes.Mode(accepted) == mode && task.ToJsonString() == before, "Accepting review scope must preserve the caller and selected value.");
            accepted["reviewOptions"]!["mode"] = "different";
            Check(task.ToJsonString() == before, "Accepted scope is a detached immutable request snapshot.");
        }
        var first = ReviewTask("static"); var second = first.DeepClone().AsObject(); second["reviewOptions"]!["mode"] = "build-tests";
        Check(Protocol.Fingerprint(first) != Protocol.Fingerprint(second), "Different review scopes cannot reuse one request fingerprint.");
        foreach (var bad in new JsonNode[] { JsonValue.Create("static")!, new JsonArray(), new JsonObject { ["mode"] = "static", ["command"] = "run" }, new JsonObject { ["mode"] = "everything" } })
        {
            var task = ReviewTask("static"); task["reviewOptions"] = bad; RejectRequest(task);
        }
        var legacy = ReviewTask(null);
        Check(ReviewModes.Mode(Protocol.ValidateTask(legacy)) is null && WorkflowResult.RequiredChecksForTask(legacy).SequenceEqual(["context", "local-review", "verification"]),
            "Historical reviews remain unrecorded and retain their original workflow requirements.");
        var e2e = ReviewTask(null, "e2e");
        Check(ReviewModes.Mode(Protocol.ValidateTask(e2e)) == "ui-e2e" && WorkflowResult.RequiredChecksForTask(e2e).SequenceEqual(["setup", "e2e"]),
            "Legacy E2E remains a scenario-execution request.");
        e2e["reviewOptions"] = new JsonObject { ["mode"] = "static" }; RejectRequest(e2e);
    }

    private static void InternalVerificationCannotBeSpoofed()
    {
        var task = VerificationTask("ui-e2e");
        RejectRequest(task);
        Check(Protocol.ValidateTask(task, allowFollowUp: true)["actionKind"]?.GetValue<string>() == "pr-verify", "Only the internal acceptance path can validate a saved verification link.");
        foreach (var field in new[] { "parentRunId", "recommendationId", "parentResultFingerprint", "subject", "revisionSha" })
        {
            var corrupt = task.DeepClone().AsObject(); corrupt["followUp"]![field] = "invalid"; RejectRequest(corrupt, true);
        }
        var changedSha = task.DeepClone().AsObject(); changedSha["expectedHeadSha"] = new string('b', 40); RejectRequest(changedSha, true);
        var staticFollowUp = task.DeepClone().AsObject(); staticFollowUp["reviewOptions"]!["mode"] = "static"; RejectRequest(staticFollowUp, true);
        var noLink = task.DeepClone().AsObject(); noLink.Remove("followUp"); RejectRequest(noLink, true);
        var regular = ReviewTask("static"); regular["followUp"] = task["followUp"]!.DeepClone(); RejectRequest(regular, true);
    }

    private static void ScopeControlsChecksAndPrompt()
    {
        foreach (var (mode, ids) in new[]
        {
            ("static", new[] { "context", "local-review" }),
            ("build-tests", new[] { "context", "local-review", "build-tests" }),
            ("ui-e2e", new[] { "context", "local-review", "setup", "e2e" })
        })
        {
            var task = ReviewTask(mode); var input = Result(task);
            input["validation"]!.AsArray().Add(CheckRow("external-pr-matrix", "not_run"));
            var normalized = Normalize(task, input);
            Check(Text(normalized, "outcome") == "completed" && normalized["validation"]!.AsArray().OfType<JsonObject>().Where(row => row["required"]!.GetValue<bool>())
                .Select(row => Text(row, "id")).SequenceEqual(ids), "A PR description cannot add mandatory execution beyond the accepted scope.");
            input["validation"]![0]!["status"] = "not_run"; input["validation"]![0]!["required"] = false;
            Check(Text(Normalize(task, input), "outcome") == "blocked", "The chosen core checks cannot be made optional by the model.");
            foreach (var agent in new[] { "codex", "copilot" })
            {
                var prompt = CliAdapter.Prompt(task, "yolo", agent);
                Check(prompt.Contains(ReviewModes.Instructions(task), StringComparison.Ordinal) && prompt.Contains(string.Join(", ", ids), StringComparison.Ordinal),
                    "Every supported agent receives the exact accepted scope and check IDs.");
            }
        }
        Check(WorkflowResult.RequiredChecksForTask(VerificationTask("build-tests")).SequenceEqual(["setup", "build-tests"]) &&
            WorkflowResult.RequiredChecksForTask(VerificationTask("ui-e2e")).SequenceEqual(["setup", "e2e"]), "Verification follow-ups omit complete code review.");
        var staticScope = ReviewModes.Instructions(ReviewTask("static"));
        Check(new[] { "Do not build", "execute tests", "operate a UI", "modify production code" }.All(boundary => staticScope.Contains(boundary, StringComparison.Ordinal)), "Static explicitly excludes execution and product edits.");
        Check(ReviewModes.Instructions(VerificationTask("ui-e2e")).Contains("not another code review", StringComparison.Ordinal), "Follow-up instructions do not repeat the parent review.");
        var staticTask = ReviewTask("static"); var overrun = Result(staticTask); overrun["verificationEvidence"]!.AsArray().Add(Evidence("performed-build", "current-run", "build"));
        Check(HasDiagnostic(Normalize(staticTask, overrun), "REVIEW_SCOPE_EXCEEDED"), "A reported build outside static scope cannot be silently presented as compliant.");
        var buildTask = ReviewTask("build-tests"); overrun = Result(buildTask); overrun["verificationEvidence"]!.AsArray().Add(Evidence("performed-ui", "current-run", "runtime"));
        Check(HasDiagnostic(Normalize(buildTask, overrun), "REVIEW_SCOPE_EXCEEDED"), "Build/tests scope does not silently include actual UI testing.");
    }

    private static void ConclusionAndEvidenceAreIndependent()
    {
        var task = ReviewTask("static"); var input = Result(task);
        input["assessment"]!["status"] = "inconclusive";
        input["verificationEvidence"]!.AsArray().Add(Evidence("author-runtime", "author", "runtime", "not_run"));
        input["verificationRecommendation"] = Recommendation();
        var normalized = Normalize(task, input); var stored = normalized.ToJsonString();
        Check(Text(normalized, "outcome") == "completed" && WorkflowResult.CanPublish(normalized, task, "approve"),
            "A completed static review with supported code conclusion is not automatically denied ordinary approval for unselected runtime coverage.");
        Check(!WorkflowResult.CanPublish(normalized, task, "merge-pr"), "An inconclusive product assessment is not automatic merge readiness.");
        Check(normalized["verificationRecommendation"]?["question"]?.GetValue<string>() == "Does the changed startup behavior work in the affected language?",
            "A meaningful follow-up question and prerequisites survive normalization.");
        Check(!normalized["nextActions"]!.AsArray().OfType<JsonObject>().Any(row => Text(row, "kind") is "configure" or "rerun"),
            "Coverage gaps do not fabricate settings/retry proposals.");
        var projected = WorkflowResult.WithProposalAvailability(normalized, task, new JsonObject { ["state"] = "succeeded", ["exitCode"] = 0 });
        Check(normalized.ToJsonString() == stored && projected["verificationRecommendation"] is JsonObject, "Eligibility display does not overwrite the original conclusion or evidence.");

        var uncertain = input.DeepClone().AsObject(); uncertain["reviewConclusion"]!["blockingUncertainties"]!.AsArray().Add("The changed encoding path may corrupt titles.");
        Check(!WorkflowResult.CanPublish(Normalize(task, uncertain), task, "approve"), "A material unresolved code question still prevents approval.");
        uncertain = input.DeepClone().AsObject(); uncertain["reviewConclusion"]!["status"] = "changes-requested";
        Check(!WorkflowResult.CanPublish(Normalize(task, uncertain), task, "approve"), "A changes-requested conclusion cannot be bypassed by static scope.");
        uncertain = input.DeepClone().AsObject(); uncertain["reviewConclusion"]!["revisionSha"] = new string('b', 40);
        Check(HasDiagnostic(Normalize(task, uncertain), "REVIEW_REVISION_MISMATCH"), "A conclusion for another SHA cannot support the original review.");
        var failed = Result(ReviewTask("ui-e2e")); failed["assessment"]!["status"] = "failed";
        failed["verificationEvidence"]!.AsArray().Add(Evidence("scenario-failure", "current-run", "runtime", "failed"));
        var report = Normalize(ReviewTask("ui-e2e"), failed);
        Check(Text(report, "outcome") == "completed" && !WorkflowResult.CanPublish(report, ReviewTask("ui-e2e"), "approve") && WorkflowResult.CanPublish(report, ReviewTask("ui-e2e"), "comment"),
            "A fully executed failing scenario is a complete report that can be commented on, not approved.");
        var legacy = ReviewTask(null); var historical = Result(legacy); historical["assessment"]!["status"] = "inconclusive";
        Check(!WorkflowResult.CanPublish(Normalize(legacy, historical), legacy, "approve"), "New scope semantics do not silently relax historical publication gates.");
    }

    private static void InvalidEvidenceCannotMasqueradeAsVerified()
    {
        var task = ReviewTask("static"); var fixture = Result(task);
        fixture["verificationEvidence"]!.AsArray().Add(Evidence("ci-build", "ci", "build"));
        var valid = Normalize(task, fixture);
        Check(WorkflowResult.IsValidStoredV2(valid), "Scoped fields remain valid when restored with Host proposal metadata.");
        foreach (var mutation in new Action<JsonObject>[]
        {
            row => row["verificationEvidence"]![0]!["revisionSha"] = "not-a-sha",
            row => row["verificationEvidence"]![0]!["evidence"] = new JsonArray(),
            row => row["verificationEvidence"]![0]!["source"] = "trusted-host",
            row => row["verificationEvidence"]![0]!["runId"] = Guid.NewGuid().ToString("D"),
            row => row["verificationEvidence"]!.AsArray().Add(row["verificationEvidence"]![0]!.DeepClone()),
            row => { row["verificationRecommendation"] = Recommendation(); row["verificationRecommendation"]!["scenarios"] = new JsonArray(); },
            row => { row["verificationRecommendation"] = Recommendation(); row["verificationRecommendation"]!["command"] = "arbitrary-command"; },
            row => { row["verificationRecommendation"] = Recommendation(); row["verificationRecommendation"]!["prerequisites"] = new JsonArray(); }
        })
        {
            var malformed = fixture.DeepClone().AsObject(); mutation(malformed);
            Check(HasDiagnostic(Normalize(task, malformed), "INVALID_RESULT"), "Malformed verification provenance or recommendation must not be accepted.");
        }
        var verifyTask = VerificationTask("ui-e2e"); var replacement = Result(verifyTask); replacement["reviewConclusion"] = Conclusion();
        Check(HasDiagnostic(Normalize(verifyTask, replacement), "VERIFICATION_SCOPE_MISMATCH"), "A verification-only task cannot manufacture a replacement code review.");
    }

    private static void SnapshotIncludesSharedSourceProvenance()
    {
        // Use an isolated fixture; no installed settings or run records are read or changed.
        var root = Path.Combine(Path.GetTempPath(), "PulseScopedPromptReadOnly-" + Guid.NewGuid().ToString("N"));
        var store = new Store(root); var catalog = new PromptCatalog(store); var config = new Configuration(store).Read();
        try
        {
            foreach (var task in new[] { ReviewTask("static"), ReviewTask("build-tests"), ReviewTask("ui-e2e"), ReviewTask(null), VerificationTask("build-tests"), VerificationTask("ui-e2e"), ReviewTask(null, "e2e") })
            {
                var snapshot = TaskPrompt.Snapshot(catalog, task, config)!;
                var needsShared = ReviewModes.Mode(task) != "static";
                var needsRuntime = ReviewModes.Mode(task) is null or "ui-e2e";
                Check(snapshot["fragments"] is JsonArray fragments && fragments.Count == (needsShared ? needsRuntime ? 2 : 1 : 0) &&
                    (!needsShared || fragments[0]!["name"]?.GetValue<string>() == PromptCatalog.VerificationFragmentName &&
                        fragments[0]!["sha"]?.GetValue<string>() == catalog.VerificationInstructions()["sha"]?.GetValue<string>()) &&
                    (!needsRuntime || fragments[1]!["name"]?.GetValue<string>() == PromptCatalog.RuntimeSkillFragmentName &&
                        fragments[1]!["sha"]?.GetValue<string>() == catalog.RuntimeSkillInstructions()["sha"]?.GetValue<string>()) &&
                    snapshot["renderedSha"]?.GetValue<string>() == Protocol.Hash(snapshot["body"]!.GetValue<string>()), "Snapshots identify every applicable fragment and exact rendered text; static/build-only work excludes runtime skill guidance.");
                Check(snapshot["requiredChecks"]!.AsArray().Select(node => node!.GetValue<string>()).SequenceEqual(WorkflowResult.RequiredChecksForTask(task)), "Snapshot check IDs follow accepted scope.");
            }
            Check(catalog.List()["prompts"]!.AsArray().Count == 8, "Sharing scenario instructions preserves the legacy templates and adds the four Issue workflow templates.");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Check(Path.GetDirectoryName(full)!.Equals(temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("PulseScopedPromptReadOnly-", StringComparison.Ordinal),
                "Scope fixture cleanup stays in its dedicated temporary directory.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static void StandaloneVerificationDoesNotInventReview()
    {
        var task = ReviewTask(null, "e2e");
        foreach (var includeConclusion in new[] { false, true })
        {
            var input = Result(task);
            input["review"] = new JsonObject { ["headSha"] = Sha, ["body"] = "Invented approval from an E2E run.", ["suggestions"] = new JsonArray() };
            if (includeConclusion) input["reviewConclusion"] = Conclusion();
            var normalized = Normalize(task, input);
            Check(HasDiagnostic(normalized, "VERIFICATION_SCOPE_MISMATCH") && Text(normalized, "outcome") == "blocked",
                "Standalone E2E cannot claim a complete code-review verdict or draft from runtime observations.");
            foreach (var kind in new[] { "approve", "requestChanges", "suggestChanges" })
                Check(!WorkflowResult.CanPublish(normalized, task, kind), "E2E must not submit a code-review decision.");

            // Historical evidence remains readable; detached publication gating also denies the wrong workflow.
            input["structured"] = true; input["cliExitCode"] = 0;
            var before = input.ToJsonString();
            Check(WorkflowResult.IsValidStoredV2(input) && !WorkflowResult.CanPublish(input, task, "approve") && input.ToJsonString() == before,
                "Existing E2E records stay readable and unchanged while inappropriate new publication is denied.");
        }
        var honest = Result(task);
        honest["nextActions"] = new JsonArray(new JsonObject { ["kind"] = "comment", ["reason"] = "Report observed runtime evidence.", ["body"] = "Runtime scope was executed." });
        var report = Normalize(task, honest);
        Check(Text(report, "outcome") == "completed" && WorkflowResult.CanPublish(report, task, "comment"), "Ordinary completed E2E reports retain factual comments.");
    }

    private static void RecommendationFitsFollowUpContext()
    {
        var task = ReviewTask("static"); var input = Result(task); var recommendation = Recommendation();
        var evidence = recommendation["evidence"]!.AsArray();
        while (System.Text.Encoding.UTF8.GetByteCount(recommendation.ToJsonString(Protocol.JsonOptions)) < WorkflowResult.MaximumVerificationRecommendationBytes - 16)
        {
            var remaining = WorkflowResult.MaximumVerificationRecommendationBytes - 16 - System.Text.Encoding.UTF8.GetByteCount(recommendation.ToJsonString(Protocol.JsonOptions));
            if (remaining <= 3) break;
            evidence.Add(new string('e', Math.Min(4096, remaining - 3)));
        }
        var size = System.Text.Encoding.UTF8.GetByteCount(recommendation.ToJsonString(Protocol.JsonOptions));
        Check(size <= WorkflowResult.MaximumVerificationRecommendationBytes && size > WorkflowResult.MaximumVerificationRecommendationBytes - 32,
            "The fixture exercises a near-limit actionable recommendation.");
        input["verificationRecommendation"] = recommendation;
        Check(!HasDiagnostic(Normalize(task, input), "INVALID_RESULT"), "A complete near-24-KiB recommendation remains accepted without truncating its scenarios.");
        evidence.Add(new string('x', 32));
        Check(HasDiagnostic(Normalize(task, input), "INVALID_RESULT"), "A recommendation too large for bounded follow-up context is rejected at result ingestion.");
        var escaped = Result(task); escaped["verificationRecommendation"] = Recommendation();
        escaped["verificationRecommendation"]!["evidence"] = new JsonArray(new string('界', 4096));
        Check(HasDiagnostic(Normalize(task, escaped), "INVALID_RESULT"), "The aggregate recommendation budget includes JSON Unicode escaping, matching immutable context serialization.");
        var schema = WorkflowResult.Schema()["properties"]!["verificationRecommendation"]!["anyOf"]!.AsArray().OfType<JsonObject>().Single(row => row["type"]?.GetValue<string>() != "null");
        Check(schema["description"]?.GetValue<string>().Contains("24576", StringComparison.Ordinal) == true, "Both CLI schema consumers receive the aggregate recommendation budget.");
    }

    private static JsonObject ReviewTask(string? mode, string kind = "pr-review")
    {
        var task = new JsonObject { ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "scoped-review-fixture", ["actionKind"] = kind,
            ["repository"] = "microsoft/powertoys", ["target"] = new JsonObject { ["type"] = "pr", ["number"] = 50027 }, ["expectedHeadSha"] = Sha,
            ["prompt"] = "External PR context recommends more scenarios; it is not accepted scope." };
        if (mode is not null) task["reviewOptions"] = new JsonObject { ["mode"] = mode };
        return task;
    }
    private static JsonObject VerificationTask(string mode)
    {
        var task = ReviewTask(mode, "pr-verify"); task["followUp"] = new JsonObject { ["parentRunId"] = Guid.NewGuid().ToString("D"),
            ["recommendationId"] = new string('b', 64), ["parentResultFingerprint"] = new string('c', 64), ["subject"] = "original-pr", ["revisionSha"] = Sha };
        return task;
    }
    private static JsonObject Result(JsonObject task) => new()
    {
        ["schemaVersion"] = 2, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "The selected workflow completed.",
        ["assessment"] = new JsonObject { ["subject"] = "original-pr", ["status"] = "passed", ["summary"] = "Original revision was assessed.", ["revisionSha"] = Sha },
        ["reviewConclusion"] = Text(task, "actionKind") == "pr-review" ? Conclusion() : null,
        ["verificationEvidence"] = new JsonArray(), ["verificationRecommendation"] = null, ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(),
        ["validation"] = new JsonArray(WorkflowResult.RequiredChecksForTask(task).Select(id => (JsonNode?)CheckRow(id)).ToArray()), ["diagnostics"] = new JsonArray(),
        ["nextActions"] = new JsonArray(new JsonObject { ["kind"] = "approve", ["reason"] = "The inspected code has no blocking findings.", ["body"] = "Reviewed the selected scope.", ["suggestionIds"] = new JsonArray() },
            new JsonObject { ["kind"] = "comment", ["reason"] = "Publish observed evidence.", ["body"] = "Observed scoped result." },
            new JsonObject { ["kind"] = "merge-pr", ["reason"] = "Inspect merge eligibility.", ["body"] = "" }),
        ["review"] = Text(task, "actionKind") == "pr-review" ? new JsonObject { ["headSha"] = Sha, ["body"] = "Review of the original revision.", ["suggestions"] = new JsonArray() } : null,
        ["needsReview"] = true
    };
    private static JsonObject Conclusion() => new() { ["status"] = "no-blocking-findings", ["summary"] = "No blocking code findings.", ["revisionSha"] = Sha, ["blockingUncertainties"] = new JsonArray() };
    private static JsonObject CheckRow(string id, string status = "passed") => new() { ["id"] = id, ["name"] = id, ["status"] = status, ["required"] = true,
        ["details"] = "The selected check was interpreted with concrete evidence.", ["evidence"] = new JsonArray("Fixture observation for " + id) };
    private static JsonObject Evidence(string id, string source, string kind, string status = "passed") => new()
    {
        ["id"] = id, ["source"] = source, ["kind"] = kind, ["status"] = status, ["subject"] = "original-pr", ["revisionSha"] = Sha,
        ["summary"] = "Observed or explicitly unavailable verification.", ["evidence"] = new JsonArray("Identified source and observed fixture evidence."), ["runId"] = null
    };
    private static JsonObject Recommendation() => new() { ["mode"] = "ui-e2e", ["reason"] = "The changed language-dependent startup behavior lacks runtime evidence.",
        ["question"] = "Does the changed startup behavior work in the affected language?", ["scenarios"] = new JsonArray("Start the affected component using its CJK title in the affected elevation state."),
        ["prerequisites"] = new JsonArray("Affected language resources and a runnable desktop session."), ["evidence"] = new JsonArray("Source review covered the new title initialization path; no matching runtime result is available."), ["readiness"] = "missing-prerequisites" };
    private static JsonObject Normalize(JsonObject task, JsonObject result) => WorkflowResult.FromModel(result.ToJsonString(), task, "succeeded", 0, null, null, expectedSchemaVersion: 2);
    private static string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
    private static bool HasDiagnostic(JsonObject value, string code) => value["diagnostics"]!.AsArray().OfType<JsonObject>().Any(row => Text(row, "code") == code);
    private static void RejectRequest(JsonObject task, bool allowFollowUp = false)
    {
        try { Protocol.ValidateTask(task, allowFollowUp); }
        catch (ProtocolException) { return; }
        throw new InvalidOperationException("Expected invalid task to be rejected.");
    }
    private static void Check(bool condition, string message) => CoreScenarios.Check(condition, message);
}
