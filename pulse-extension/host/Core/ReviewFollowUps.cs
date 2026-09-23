using System.Text.Json.Nodes;

namespace Pulse.Host;

/// <summary>Creates explicit, revision-bound supplemental verification and projects related evidence without rewriting review history.</summary>
public static class ReviewFollowUps
{
    public static string RecommendationId(JsonObject recommendation) => Protocol.Fingerprint(recommendation);

    public static JsonObject? VerificationRecommendation(JsonObject? result)
    {
        if (result?["schemaVersion"]?.GetValue<int>() == 3 && result["e2eAssessment"] is JsonObject assessment)
        {
            var recommendation = assessment.DeepClone().AsObject();
            recommendation["mode"] = "ui-e2e";
            return recommendation;
        }
        return (result?["verificationRecommendation"] as JsonObject)?.DeepClone().AsObject();
    }

    private static JsonArray ScenarioChecks(JsonObject recommendation)
    {
        var hash = RecommendationId(recommendation)[..24];
        return new JsonArray((recommendation["scenarios"] as JsonArray)?.Select((_, index) => (JsonNode?)new JsonObject
        {
            ["id"] = "e2e-" + hash + "-" + (index + 1), ["index"] = index
        }).ToArray() ?? []);
    }

    public static JsonObject Prepare(Store store, JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "parentRunId", "requestId", "recommendationId", "execution", "prerequisitesConfirmed");
        var parentId = Protocol.RunId(Protocol.RequiredString(payload, "parentRunId"));
        var requestId = Protocol.RequiredString(payload, "requestId", 128);
        var recommendationId = Protocol.RequiredString(payload, "recommendationId", 64);
        if (payload.ContainsKey("prerequisitesConfirmed") && (payload["prerequisitesConfirmed"] is not JsonValue confirmed || !confirmed.TryGetValue<bool>(out _)))
            throw new ProtocolException("INVALID_REQUEST", "prerequisitesConfirmed must be a boolean.");
        if (payload.ContainsKey("execution") && payload["execution"] is not null) ExecutionOptions.ValidateOverride(payload["execution"]);
        var fingerprint = Protocol.Fingerprint(payload);
        // Freeze before admission. Defaults, recommendation edits, and lost acknowledgements
        // cannot silently replace a user's already confirmed intent on a retransmission.
        using var held = store.AcquireLock("review-follow-up-" + requestId);
        var path = Path.Combine(store.Root, "review-follow-ups", Protocol.Hash(requestId) + ".json");
        var frozen = store.ReadJson(path);
        if (frozen is not null)
        {
            if (Text(frozen, "fingerprint") != fingerprint)
                throw new ProtocolException("REQUEST_CONFLICT", "This verification request was already used with different options.", "Recover the original request unchanged, or explicitly start a new verification request.");
            var original = Protocol.RequireObject(frozen["submission"]);
            var frozenTask = Protocol.RequireObject(original["task"]);
            using var admission = store.AcquireLock("accept");
            if (store.FindRequest(frozenTask, Protocol.RequiredString(original, "sourceOrigin")) is null)
            {
                // An unaccepted frozen request is not permission to recreate work after
                // its source record was removed or changed. Accepted lost-ack recovery
                // remains possible even if the user later removed the parent report.
                var currentParent = store.ReadTask(parentId);
                var currentResult = store.ReadResult(parentId);
                if (Protocol.IsActive(Text(store.ReadStatus(parentId), "state")) || currentResult is null ||
                    Text(frozenTask["followUp"] as JsonObject, "parentResultFingerprint") != Protocol.Fingerprint(currentResult) ||
                    !JsonNode.DeepEquals(currentParent["task"]?["target"], frozenTask["target"]) ||
                    Text(currentParent["task"] as JsonObject, "expectedHeadSha") != Text(frozenTask, "expectedHeadSha"))
                    throw new ProtocolException("RECOMMENDATION_CHANGED", "The original review changed before this verification request was accepted.", "Refresh the review before confirming another request.");
            }
            return original.DeepClone().AsObject();
        }
        var saved = store.ReadTask(parentId);
        var parent = Protocol.RequireObject(saved["task"]);
        var status = store.ReadStatus(parentId);
        var result = store.ReadResult(parentId);
        var recommendation = VerificationRecommendation(result);
        if (Text(parent, "actionKind") != "pr-review" || Text(parent["target"] as JsonObject, "type") != "pr")
            throw new ProtocolException("INVALID_REQUEST", "Supplemental verification must start from a PR code review.");
        if (Protocol.IsActive(Text(status, "state")))
            throw new ProtocolException("TASK_STILL_RUNNING", "Wait for this code review to finish before adding verification.");
        if (requestId == Text(parent, "requestId")) throw new ProtocolException("INVALID_REQUEST", "Supplemental verification requires a new requestId.");
        if (result is null || !ValidStoredResult(result) || result["structured"]?.GetValue<bool>() != true ||
            recommendation is null || RecommendationId(recommendation) != recommendationId)
            throw new ProtocolException("RECOMMENDATION_CHANGED", "The saved review does not contain this verification recommendation.", "Refresh the review and select its current recommendation.");
        if (Text(recommendation, "level") == "not_needed")
            throw new ProtocolException("VERIFICATION_NOT_RECOMMENDED", "This review explicitly reports that additional E2E verification is not needed.", "Start a separate explicitly scoped verification task if you want to investigate a new question.");
        var mode = Text(recommendation, "mode");
        if (mode is not ("build-tests" or "ui-e2e")) throw new ProtocolException("INVALID_REQUEST", "The recommended verification range is invalid.");
        if ((Text(recommendation, "readiness") is "missing-prerequisites" or "unknown") && payload["prerequisitesConfirmed"]?.GetValue<bool>() != true)
            throw new ProtocolException("VERIFICATION_PREREQUISITES_MISSING", "The required verification environment has not been established.", "Prepare the listed prerequisites, then explicitly confirm readiness. The verification will check those prerequisites before running the scenarios.");
        var sha = Protocol.RequiredString(parent, "expectedHeadSha", 40);
        var subject = Text(result["assessment"] as JsonObject, "subject");
        if (subject == "local-candidate")
            throw new ProtocolException("VERIFICATION_SUBJECT_MISMATCH", "This result describes a local candidate, not the original PR revision.", "Keep candidate evidence separate. Start verification from a review of the intended PR revision.");
        var task = new JsonObject
        {
            ["requestId"] = requestId, ["actionId"] = "pr-verify:" + parentId + ":" + recommendationId[..16],
            ["actionKind"] = "pr-verify", ["repository"] = parent["repository"]!.DeepClone(),
            ["target"] = parent["target"]!.DeepClone(), ["expectedHeadSha"] = sha,
            ["reviewOptions"] = new JsonObject { ["mode"] = mode },
            ["followUp"] = new JsonObject
            {
                ["parentRunId"] = parentId, ["recommendationId"] = recommendationId,
                ["parentResultFingerprint"] = Protocol.Fingerprint(result), ["subject"] = "original-pr", ["revisionSha"] = sha
            },
            ["prompt"] = "Perform only the explicitly recommended supplemental verification for the saved original PR revision. Reuse the parent review context; do not repeat a complete code review. Report what the selected scenarios establish and any unmet prerequisites. Do not silently change the revision or count tests of modified local code as evidence for the original PR.",
            ["context"] = new JsonObject
            {
                ["reviewVerification"] = new JsonObject
                {
                    ["parentRunId"] = parentId, ["recommendation"] = recommendation.DeepClone(),
                    ["scenarioChecks"] = result["schemaVersion"]?.GetValue<int>() == 3 ? null : ScenarioChecks(recommendation),
                    ["scenarioIdPrefix"] = "e2e-" + RecommendationId(recommendation)[..24] + "-",
                    ["scenarioReporting"] = "For every saved recommendation.scenarios entry, report one validation row with id = scenarioIdPrefix plus its 1-based index, required:false, actual passed/failed/not_run status and evidence. Match the same index in expectedResults when present. Existing explicit scenarioChecks, when present, use these same IDs. A generic passing E2E check does not establish that all scenarios passed.",
                    ["parentSummary"] = Limit(Text(result, "summary"), 512),
                    ["parentReviewStatus"] = Text(result["reviewConclusion"] as JsonObject, "status"),
                    ["parentAssessmentStatus"] = Text(result["assessment"] as JsonObject, "status"),
                    ["prerequisitesReportedReady"] = payload["prerequisitesConfirmed"]?.GetValue<bool>() == true,
                    ["preflight"] = "Check the listed prerequisites first. User confirmation reports changed conditions; it does not prove the required environment is available. Report concrete missing prerequisites without repeating a full code review."
                }
            }
        };
        var resolved = ExecutionOptions.Resolve(new Configuration(store).Read(), payload["execution"]);
        task["execution"] = new JsonObject
        {
            ["agent"] = resolved["agent"]!.DeepClone(), ["model"] = resolved["model"]!.DeepClone(),
            ["reasoningEffort"] = resolved["reasoningEffort"]!.DeepClone()
        };
        var submission = new JsonObject
        {
            ["task"] = Protocol.ValidateTask(task, allowFollowUp: true),
            ["sourceOrigin"] = saved["sourceOrigin"]!.DeepClone()
        };
        store.WriteJson(path, new JsonObject { ["fingerprint"] = fingerprint, ["submission"] = submission.DeepClone() });
        return submission;
    }

    public static JsonObject Related(Store store, JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "runId");
        var requestedId = Protocol.RunId(Protocol.RequiredString(payload, "runId"));
        var requestedTask = Protocol.RequireObject(store.ReadTask(requestedId)["task"]);
        var parentId = Text(requestedTask["followUp"] as JsonObject, "parentRunId") ?? requestedId;
        var parent = Protocol.RequireObject(store.ReadTask(parentId)["task"]);
        if (Text(parent, "actionKind") != "pr-review") throw new ProtocolException("INVALID_REQUEST", "Related verification belongs to a PR review.");
        var result = store.ReadResult(parentId);
        var parentProvenance = ReadProvenance(store, parentId);
        var recommendation = VerificationRecommendation(result);
        JsonObject? recommendationDisplay = recommendation?.DeepClone().AsObject();
        if (recommendationDisplay is not null) recommendationDisplay["recommendationId"] = RecommendationId(recommendation!);
        var runs = new JsonArray(); var evidence = new JsonArray(); var errors = new JsonArray();
        var acceptedFailure = false; var acceptedSuccess = false; var incomplete = false; var passingAssessment = false;
        var relatedConfirmedP0 = false; var relatedConfirmedP1 = false;
        string? completedRunId = null;
        JsonObject? failedEvidence = null;
        var relatedCount = 0; var evidenceCount = 0;
        var currentRunEvidenceComplete = false;
        var attributedEvidenceComplete = false;
        var parentStatus = store.ReadStatus(parentId);
        if (result is not null && recommendation is not null &&
            Text(parentStatus, "state") == "succeeded" && parentStatus["exitCode"] is JsonValue parentExit && parentExit.TryGetValue<int>(out var parentCode) && parentCode == 0 &&
            WorkflowResult.IsFinalReportComplete(result) && ReviewProvenance.MatchesOriginal(parentProvenance, Text(parent, "expectedHeadSha")) &&
            Text(result["assessment"] as JsonObject, "subject") == "original-pr" && Text(result["assessment"] as JsonObject, "revisionSha") == Text(parent, "expectedHeadSha"))
        {
            // Direct UI/E2E review records the final assessment scenarios by stable
            // ordinal IDs. This is separate from the Host-frozen linked-task prefix.
            var proofTask = parent.DeepClone().AsObject();
            proofTask["context"] = new JsonObject { ["reviewVerification"] = new JsonObject { ["scenarioChecks"] = new JsonArray(
                (recommendation["scenarios"] as JsonArray)?.Select((_, index) => (JsonNode?)new JsonObject { ["id"] = "e2e-scenario-" + (index + 1) }).ToArray() ?? []) } };
            var selectedRuntime = ReviewModes.Mode(parent) == "ui-e2e";
            var ownFailure = selectedRuntime ? ScenarioFailure(proofTask, result) : null;
            var scenarioIds = ScenarioIds(proofTask).ToHashSet(StringComparer.Ordinal);
            var attributed = (result["verificationEvidence"] as JsonArray)?.OfType<JsonObject>().Where(row =>
                scenarioIds.Contains(Text(row, "id") ?? "") && (Text(row, "source") is "ci" or "author") && Text(row, "kind") == "runtime" &&
                Text(row, "subject") == "original-pr" && Text(row, "revisionSha") == Text(parent, "expectedHeadSha") && (row["evidence"] as JsonArray)?.Count > 0).ToArray() ?? [];
            var failedObservation = attributed.FirstOrDefault(row => Text(row, "status") == "failed") ??
                (selectedRuntime ? (result["verificationEvidence"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(row => EvidenceMatches(parent, row) && Text(row, "status") == "failed") : null);
            failedEvidence = ownFailure ?? (failedObservation is null ? null : EvidenceSummary(failedObservation));
            if (failedEvidence is not null && ownFailure is not null) { failedEvidence["source"] = "current-run"; failedEvidence["runId"] = null; }
            var passedAssessment = Text(result["assessment"] as JsonObject, "status") == "passed";
            currentRunEvidenceComplete = selectedRuntime && failedEvidence is null && passedAssessment && AllScenariosPassed(proofTask, result);
            var currentChecks = (result["validation"] as JsonArray)?.OfType<JsonObject>().ToLookup(row => Text(row, "id") ?? "", StringComparer.Ordinal);
            var attributedById = attributed.ToLookup(row => Text(row, "id") ?? "", StringComparer.Ordinal);
            attributedEvidenceComplete = failedEvidence is null && passedAssessment && attributed.Length > 0 && scenarioIds.Count > 0 && scenarioIds.All(id =>
            {
                var cited = attributedById[id].ToArray();
                if (cited.Length == 1 && Text(cited[0], "status") == "passed") return true;
                var observed = selectedRuntime ? currentChecks?[id].ToArray() : null;
                return observed is { Length: 1 } && Text(observed[0], "status") == "passed" && (observed[0]["evidence"] as JsonArray)?.Count > 0;
            });
            acceptedFailure = (selectedRuntime || attributed.Length > 0) && Text(result["assessment"] as JsonObject, "status") == "failed";
            if (currentRunEvidenceComplete || attributedEvidenceComplete) { acceptedSuccess = true; passingAssessment = true; completedRunId = parentId; }
        }
        foreach (var id in store.RunIds().Order(StringComparer.Ordinal))
        {
            JsonObject saved;
            try { saved = store.ReadTask(id); }
            catch (ProtocolException) { continue; }
            var task = saved["task"] as JsonObject;
            if (Text(task?["followUp"] as JsonObject, "parentRunId") != parentId) continue;
            relatedCount++;
            try
            {
                var status = store.ReadStatus(id); var childResult = store.ReadResult(id); var provenance = ReadProvenance(store, id);
                var reason = CompatibilityReason(parent, result, parentProvenance, task!, childResult, provenance);
                var compatible = reason is null;
                if (compatible && childResult is not null && WorkflowResult.IsValidStoredV3(childResult) &&
                    Text(childResult["assessment"] as JsonObject, "subject") == "original-pr" &&
                    Text(childResult["assessment"] as JsonObject, "revisionSha") == Text(parent, "expectedHeadSha"))
                {
                    relatedConfirmedP0 |= WorkflowResult.HasConfirmedP0(childResult, task!);
                    relatedConfirmedP1 |= (childResult["findings"] as JsonArray)?.OfType<JsonObject>().Any(finding =>
                        Text(finding, "priority") == "P1" && Text(finding, "status") == "open" && finding["confirmed"]?.GetValue<bool>() == true) == true;
                }
                var finished = Text(status, "state") == "succeeded" && status["exitCode"] is JsonValue exit && exit.TryGetValue<int>(out var code) && code == 0;
                var complete = finished && Text(childResult, "outcome") == "completed" &&
                    (childResult?["schemaVersion"]?.GetValue<int>() != 3 || WorkflowResult.IsFinalReportComplete(childResult));
                var allScenariosPassed = compatible && complete && AllScenariosPassed(task!, childResult!);
                if (!complete) incomplete = true;
                if (compatible && complete && !allScenariosPassed && ScenarioIds(task!).Count > 0) incomplete = true;
                var childEvidence = (childResult?["verificationEvidence"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
                var verifiedEvidence = compatible && complete ? childEvidence.Where(row => EvidenceMatches(task!, row)).ToArray() : [];
                foreach (var row in verifiedEvidence)
                {
                    var projected = EvidenceSummary(row);
                    projected["source"] = "prior-run"; projected["runId"] = id;
                    evidenceCount++;
                    if (evidence.Count < 32) evidence.Add(projected);
                    if (Text(row, "status") == "failed") failedEvidence ??= projected.DeepClone().AsObject();
                    if (complete && Text(row, "status") == "passed") acceptedSuccess = true;
                }
                if (compatible && complete && ScenarioFailure(task!, childResult!) is JsonObject scenarioFailure)
                {
                    scenarioFailure["runId"] = id; failedEvidence ??= scenarioFailure;
                }
                if (allScenariosPassed) acceptedSuccess = true;
                if (allScenariosPassed &&
                    Text(childResult?["assessment"] as JsonObject, "subject") == "original-pr" &&
                    Text(childResult?["assessment"] as JsonObject, "revisionSha") == Text(parent, "expectedHeadSha") &&
                    Text(childResult?["assessment"] as JsonObject, "status") == "passed")
                { passingAssessment = true; completedRunId ??= id; }
                if (compatible && complete && Text(childResult?["assessment"] as JsonObject, "subject") == "original-pr" &&
                    Text(childResult?["assessment"] as JsonObject, "revisionSha") == Text(parent, "expectedHeadSha") &&
                    Text(childResult?["assessment"] as JsonObject, "status") == "failed") acceptedFailure = true;
                if (runs.Count < 32) runs.Add(new JsonObject
                {
                    ["runId"] = id, ["status"] = StatusSummary(status), ["summary"] = Limit(Text(childResult, "summary"), 2048),
                    ["outcome"] = childResult?["outcome"]?.DeepClone(),
                    ["scenarioCoverageComplete"] = allScenariosPassed,
                    ["assessment"] = childResult?["assessment"]?.DeepClone(), ["verificationEvidence"] = new JsonArray(childEvidence.Take(12).Select(row => (JsonNode?)EvidenceSummary(row)).ToArray()),
                    ["provenance"] = provenance?.DeepClone(), ["compatibility"] = new JsonObject { ["eligible"] = compatible, ["reason"] = reason },
                    ["recommendationId"] = task!["followUp"]!["recommendationId"]!.DeepClone()
                });
            }
            catch (ProtocolException error) { if (errors.Count < 32) errors.Add(new JsonObject { ["runId"] = id, ["error"] = error.ToJson() }); }
        }
        var response = new JsonObject
        {
            ["parentRunId"] = parentId, ["recommendation"] = recommendationDisplay,
            ["runs"] = runs, ["evidence"] = evidence, ["provenance"] = parentProvenance?.DeepClone(), ["errors"] = errors,
            ["totalCount"] = relatedCount, ["truncated"] = relatedCount > runs.Count || evidenceCount > evidence.Count,
            ["currentConclusion"] = new JsonObject
            {
                ["status"] = acceptedFailure ? "changes-requested" : failedEvidence is not null ? "verification-incomplete" : acceptedSuccess ? "evidence-added" : incomplete ? "verification-incomplete" : "unchanged",
                ["canSupplementAssessment"] = !acceptedFailure && failedEvidence is null && passingAssessment,
                ["evidenceComplete"] = !acceptedFailure && failedEvidence is null && passingAssessment,
                ["currentRunEvidenceComplete"] = !acceptedFailure && failedEvidence is null && currentRunEvidenceComplete,
                ["attributedEvidenceComplete"] = !acceptedFailure && failedEvidence is null && attributedEvidenceComplete,
                ["completedRunId"] = !acceptedFailure && failedEvidence is null && passingAssessment ? completedRunId : null,
                ["relatedConfirmedP0"] = relatedConfirmedP0, ["relatedConfirmedP1"] = relatedConfirmedP1,
                ["failedEvidence"] = failedEvidence,
                ["summary"] = acceptedFailure ? "Supplemental verification found a failing behavior in the original PR revision. Inspect that evidence before approving."
                    : failedEvidence is not null ? "Supplemental verification includes a failed check. Determine whether it reflects the product or unavailable infrastructure before treating this evidence gap as resolved."
                    : acceptedSuccess ? "Supplemental verification adds evidence for the same original PR revision. Review the covered scenarios and any remaining questions; the original report is preserved."
                    : incomplete ? "Supplemental verification has not completed. The existing code review and its evidence remain available."
                    : "No compatible supplemental verification has changed the evidence for this original PR revision."
            }
        };
        // Related runs are a compact projection, not a substitute for each immutable
        // full result. Keep escaped Native Messaging bytes within its actual budget.
        while (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response, Protocol.JsonOptions).Length > 800 * 1024 && (runs.Count > 0 || evidence.Count > 0))
        {
            if (runs.Count > 0) runs.RemoveAt(runs.Count - 1); else evidence.RemoveAt(evidence.Count - 1);
            response["truncated"] = true;
        }
        return response;
    }

    /// <summary>Apply compatible verification to ordinary action eligibility without changing saved proposals or history.</summary>
    public static JsonObject EffectiveResult(Store store, string runId, JsonObject originalResult)
    {
        var task = Protocol.RequireObject(store.ReadTask(runId)["task"]);
        if (Text(task, "actionKind") != "pr-review" || VerificationRecommendation(originalResult) is not JsonObject) return originalResult.DeepClone().AsObject();
        var projected = originalResult.DeepClone().AsObject();
        var related = Related(store, new JsonObject { ["runId"] = runId });
        projected["relatedVerification"] = related["currentConclusion"]?.DeepClone();
        if (originalResult["schemaVersion"]?.GetValue<int>() == 3)
        {
            // V3 verification affects the current recommendation, not the user's
            // fixed PR action permissions or the immutable E2E necessity level.
            projected["e2eEvidenceComplete"] = related["currentConclusion"]?["evidenceComplete"]?.GetValue<bool>() == true;
            projected["relatedConfirmedP0"] = related["currentConclusion"]?["relatedConfirmedP0"]?.GetValue<bool>() == true;
            projected["relatedConfirmedP1"] = related["currentConclusion"]?["relatedConfirmedP1"]?.GetValue<bool>() == true;
            return projected;
        }
        if (related["currentConclusion"]?["failedEvidence"] is JsonObject failed)
        {
            // Preserve the distinction between infrastructure failure and a confirmed
            // defect, while retaining the existing failed-evidence approval guard.
            var check = failed.DeepClone().AsObject();
            check["id"] = "related-" + Protocol.Fingerprint(check)[..24];
            var checks = projected["verificationEvidence"] as JsonArray ?? new JsonArray();
            if (projected["verificationEvidence"] is not JsonArray) projected["verificationEvidence"] = checks;
            checks.Add(check);
        }
        var status = Text(related["currentConclusion"] as JsonObject, "status");
        if (status == "changes-requested")
        {
            projected["assessment"] = new JsonObject
            {
                ["subject"] = "original-pr", ["status"] = "failed", ["revisionSha"] = task["expectedHeadSha"]?.DeepClone(),
                ["summary"] = "Compatible supplemental verification found a failing behavior. Inspect the linked evidence before approving."
            };
            return projected;
        }
        if (status != "evidence-added" || Text(projected["reviewConclusion"] as JsonObject, "status") != "no-blocking-findings" ||
            projected["reviewConclusion"]?["blockingUncertainties"] is not JsonArray uncertainties || uncertainties.Count != 0 ||
            Text(projected["assessment"] as JsonObject, "status") != "inconclusive") return projected;
        var completedEvidence = related["currentConclusion"]?["canSupplementAssessment"]?.GetValue<bool>() == true;
        if (completedEvidence)
            projected["assessment"] = new JsonObject
            {
                ["subject"] = "original-pr", ["status"] = "passed", ["revisionSha"] = task["expectedHeadSha"]?.DeepClone(),
                ["summary"] = "Code review found no blocking issue, and compatible supplemental verification supplied the recommended evidence. See the linked run for its precise scope."
            };
        return projected;
    }

    private static string? CompatibilityReason(JsonObject parent, JsonObject? result, JsonObject? parentProvenance,
        JsonObject task, JsonObject? childResult, JsonObject? provenance)
    {
        var link = task["followUp"] as JsonObject;
        if (Text(task, "actionKind") != "pr-verify" || Text(task, "repository") != Text(parent, "repository") ||
            !JsonNode.DeepEquals(task["target"], parent["target"]) || Text(task, "expectedHeadSha") != Text(parent, "expectedHeadSha") ||
            Text(link, "revisionSha") != Text(parent, "expectedHeadSha") || Text(link, "subject") != "original-pr")
            return "This verification targets a different PR revision or subject.";
        if (result is null || Text(link, "parentResultFingerprint") != Protocol.Fingerprint(result))
            return "The verification was not linked to this exact saved review result.";
        var recommendation = VerificationRecommendation(result);
        if (recommendation is null || Text(link, "recommendationId") != RecommendationId(recommendation))
            return "The verification belongs to a different recommendation.";
        if (!ReviewProvenance.MatchesOriginal(parentProvenance, Text(parent, "expectedHeadSha")) ||
            !ReviewProvenance.MatchesOriginal(provenance, Text(parent, "expectedHeadSha")))
            return "The Host could not establish matching clean original-PR snapshots. Local candidate or unknown-source evidence stays separate.";
        if (childResult is null || childResult["structured"]?.GetValue<bool>() != true || !ValidStoredResult(childResult))
            return "A valid structured supplemental result is not available yet.";
        if (Text(childResult["assessment"] as JsonObject, "subject") == "local-candidate")
            return "This verification assessed a local candidate, not the original PR.";
        return null;
    }

    private static bool ValidStoredResult(JsonObject result) => WorkflowResult.IsValidStoredV2(result) || WorkflowResult.IsValidStoredV3(result);

    private static bool AllScenariosPassed(JsonObject task, JsonObject result)
    {
        var ids = ScenarioIds(task);
        if (ids.Count == 0 || result["validation"] is not JsonArray validation)
            return false; // Historical generic success is evidence, not proof of complete scenario coverage.
        var byId = validation.OfType<JsonObject>().ToLookup(row => Text(row, "id") ?? "", StringComparer.Ordinal);
        return ids.All(id =>
        {
            var rows = byId[id].ToArray();
            return rows.Length == 1 && Text(rows[0], "status") == "passed" && (rows[0]["evidence"] as JsonArray)?.Count > 0;
        });
    }

    internal static IReadOnlyList<string> ScenarioIds(JsonObject task)
    {
        var context = task["context"]?["reviewVerification"] as JsonObject;
        if (context?["recommendation"] is JsonObject recommendation && recommendation["scenarios"] is JsonArray scenarios &&
            Text(context, "scenarioIdPrefix") == "e2e-" + RecommendationId(recommendation)[..24] + "-")
            return Enumerable.Range(1, scenarios.Count).Select(index => Text(context, "scenarioIdPrefix") + index).ToArray();
        if (context?["scenarioChecks"] is JsonArray checks && checks.OfType<JsonObject>().Count() == checks.Count)
            return checks.OfType<JsonObject>().Select(check => Text(check, "id")).OfType<string>().ToArray();
        return [];
    }

    private static JsonObject? ScenarioFailure(JsonObject task, JsonObject result)
    {
        var ids = ScenarioIds(task).ToHashSet(StringComparer.Ordinal);
        var failed = (result["validation"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(row => ids.Contains(Text(row, "id") ?? "") && Text(row, "status") == "failed");
        if (failed is null) return null;
        return EvidenceSummary(new JsonObject
        {
            ["id"] = failed["id"]!.DeepClone(), ["source"] = "prior-run", ["kind"] = Text(task["reviewOptions"] as JsonObject, "mode") == "ui-e2e" ? "runtime" : "automated-tests",
            ["status"] = "failed", ["subject"] = "original-pr", ["revisionSha"] = task["expectedHeadSha"]?.DeepClone(),
            ["summary"] = "Requested scenario failed: " + Limit(Text(failed, "name"), 256), ["evidence"] = failed["evidence"]?.DeepClone() ?? new JsonArray(), ["runId"] = null
        });
    }

    private static bool EvidenceMatches(JsonObject task, JsonObject row) => Text(row, "source") == "current-run" &&
        Text(row, "subject") == "original-pr" && Text(row, "revisionSha") == Text(task, "expectedHeadSha") &&
        (Text(row, "status") is "passed" or "failed") && (row["evidence"] as JsonArray)?.Count > 0 &&
        (Text(task["reviewOptions"] as JsonObject, "mode") == "ui-e2e" ? Text(row, "kind") == "runtime" : Text(row, "kind") is "build" or "automated-tests");

    private static JsonObject EvidenceSummary(JsonObject row)
    {
        var result = new JsonObject();
        foreach (var key in new[] { "id", "source", "kind", "status", "subject", "revisionSha", "runId" }) result[key] = row[key]?.DeepClone();
        result["summary"] = Limit(Text(row, "summary"), 512);
        result["evidence"] = new JsonArray((row["evidence"] as JsonArray)?.Take(3).Select(value =>
            (JsonNode?)JsonValue.Create(Limit(value?.GetValue<string>(), 512))).ToArray() ?? []);
        return result;
    }
    private static JsonObject StatusSummary(JsonObject status)
    {
        var result = new JsonObject();
        foreach (var key in new[] { "state", "exitCode", "createdAt", "startedAt", "endedAt" }) result[key] = status[key]?.DeepClone();
        return result;
    }
    private static string? Limit(string? text, int length) => text is not null && text.Length > length ? text[..length] + "…" : text;

    private static JsonObject? ReadProvenance(Store store, string id)
    {
        try { return store.ReadJson(Path.Combine(store.RunDirectory(id), "provenance.json")); }
        catch (ProtocolException) { return null; } // Unknown provenance disables reuse, not access to the review itself.
    }
    private static string? Text(JsonObject? value, string key) => value?[key] is JsonValue field && field.TryGetValue<string>(out var text) ? text : null;
}
