using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host.Tests;

/// <summary>Result proposal identity and local confirmation drafts; no CLI or GitHub session is opened.</summary>
internal static class ResultActionScenarios
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    internal static Task RunAllAsync()
    {
        ProposalIdentityPreservesContent();
        LegacyProposalIdentityRemainsStable();
        CreatesOnlyPreparedTypedActions();
        RetryIdentityIncludesRunAndProposal();
        AttemptIdentityAndSafeRetries();
        AttemptHistoryIsScoped();
        CallerCannotOverrideSavedDraft();
        RejectsUnavailableResultsAndActions();
        BlockedReportsCanProposeCiRecovery();
        PreparedProposalKeepsHumanChoiceAfterRecovery();
        SupplementalEvidenceDoesNotGateHumanMerge();
        FixedHumanActionsNeedNoModelProposal();
        BothIssueKindsPrepareDuplicateClosure();
        DraftContentEditsKeepSourceIdentity();
        Console.WriteLine("PASS result actions: stable proposal identities, preserved proposal text, immutable typed drafts, local-only preparation and eligibility checks (offline)");
        return Task.CompletedTask;
    }

    private static void ProposalIdentityPreservesContent()
    {
        const string body = "    first();\r\n    second();\n\n";
        var first = FlatProposal("comment", body, "Discuss the first finding.");
        var second = FlatProposal("comment", "A different comment.", "Discuss the second finding.");
        var model = new JsonObject { ["schemaVersion"] = 2, ["nextActions"] = new JsonArray(first, second, first.DeepClone()) };
        var before = model.ToJsonString();
        var projected = WorkflowResult.WithProposalIds(model);
        var actions = projected["nextActions"]!.AsArray();
        var ids = actions.OfType<JsonObject>().Select(ProposalId).ToArray();
        Check(model.ToJsonString() == before && !ReferenceEquals(projected, model), "Adding proposal IDs must clone rather than mutate the stored model result.");
        Check(ids.Length == 3 && ids.Distinct(StringComparer.Ordinal).Count() == 3 && ids.All(id => Regex.IsMatch(id, "^proposal-[a-f0-9]{24}-[0-9]+$")),
            "Different and repeated same-kind proposals must each receive a stable, bounded proposal ID.");
        Check(ids[0][..ids[0].LastIndexOf('-')] == ids[2][..ids[2].LastIndexOf('-')] && ids[0] != ids[2],
            "Identical proposal content must retain a common content identity while its occurrences remain independently selectable.");
        Check(Text(actions[0]!.AsObject(), "body") == body && Text(actions[2]!.AsObject(), "body") == body,
            "Proposal projection must preserve leading indentation, CRLF and trailing newlines byte for byte.");
        Check(JsonNode.DeepEquals(projected, WorkflowResult.WithProposalIds(model)) && JsonNode.DeepEquals(projected, WorkflowResult.WithProposalIds(projected)),
            "Proposal IDs must be deterministic across reads and re-projection of an already projected result.");

        var reorderedFields = new JsonObject();
        foreach (var pair in second.Reverse()) reorderedFields[pair.Key] = pair.Value?.DeepClone();
        var reordered = WorkflowResult.WithProposalIds(new JsonObject { ["schemaVersion"] = 2, ["nextActions"] = new JsonArray(reorderedFields, first.DeepClone()) });
        Check(ProposalId(reordered["nextActions"]![0]!.AsObject()) == ids[1] && ProposalId(reordered["nextActions"]![1]!.AsObject()) == ids[0],
            "Distinct proposal reordering and JSON field order must not change their content-based identities.");
        var changed = first.DeepClone().AsObject(); changed["body"] = body.Trim();
        var changedProjection = WorkflowResult.WithProposalIds(new JsonObject { ["schemaVersion"] = 2, ["nextActions"] = new JsonArray(changed) });
        Check(ProposalId(changedProjection["nextActions"]![0]!.AsObject()) != ids[0], "Meaningful body whitespace changes must change the proposal identity.");
    }

    private static void LegacyProposalIdentityRemainsStable()
    {
        const string body = "    legacy sample\n";
        var legacy = new JsonObject { ["summary"] = "Retained legacy result.", ["nextSteps"] = new JsonArray(FlatProposal("comment", body), FlatProposal("comment", "Another legacy draft.")) };
        var before = legacy.ToJsonString();
        var projected = WorkflowResult.WithProposalIds(legacy);
        var rows = projected["nextSteps"]!.AsArray().OfType<JsonObject>().ToArray();
        Check(legacy.ToJsonString() == before && !projected.ContainsKey("nextActions") && !projected.ContainsKey("schemaVersion"),
            "Adding UI proposal identities to history must preserve its legacy envelope and original stored content.");
        Check(rows.Length == 2 && ProposalId(rows[0]) != ProposalId(rows[1]) && Text(rows[0], "body") == body,
            "Legacy same-kind proposals must remain separate without trimming their draft bodies.");
        Check(JsonNode.DeepEquals(projected, WorkflowResult.WithProposalIds(legacy)), "Legacy proposal identities must be stable on subsequent reads.");
    }

    private static void CreatesOnlyPreparedTypedActions()
    {
        using var fixture = new Fixture();
        foreach (var kind in new[] { "create-pr", "merge-pr", "trigger-ci" })
        {
            var run = fixture.Create(kind);
            var savedBefore = fixture.Store.ReadResult(run.Id)!.ToJsonString();
            var summary = fixture.Prepare(run);
            Check(Text(summary, "status") == "prepared" && Text(summary, "kind") == kind,
                "A selected " + kind + " proposal must only create a pending extension confirmation.");
            Check(summary["completedSteps"] is JsonArray { Count: 0 } && summary["urls"] is JsonArray { Count: 0 },
                "Preparing a task proposal must not report remote effects or a submitted GitHub result.");
            var record = fixture.Operation(summary);
            var draft = record["draft"]!.AsObject();
            Check(Text(record, "sourceOrigin") == "pulse-task://" + run.Id && Text(draft, "actionId") == run.Id + ":" + run.ProposalId,
                "A prepared action must retain its local task and exact proposal provenance.");
            Check(Guid.TryParseExact(Text(draft, "requestId"), "D", out _), "Result preparation must assign a valid durable request UUID.");
            Check(Text(draft["target"]!.AsObject(), "repository") == "microsoft/powertoys" &&
                Text(draft["target"]!.AsObject(), "type") == Text(run.Task["target"]!.AsObject(), "type") &&
                draft["target"]!["number"]!.GetValue<int>() == 7,
                "A proposal must use the immutable saved task's repository, target type and number.");
            if (kind == "create-pr")
            {
                var expected = run.Model["nextActions"]![0]!["pullRequest"]!;
                Check(JsonNode.DeepEquals(draft["pullRequest"], expected) && draft["expectedHeadSha"] is null,
                    "PR creation must preserve its structured head/base/title/body/draft fields without inventing a PR SHA for the source issue.");
            }
            else
            {
                Check(Text(draft, "expectedHeadSha") == Sha && draft["pullRequest"] is null,
                    "Merge and CI proposals must bind to the saved PR SHA rather than carry a new pull-request target.");
                if (kind == "trigger-ci") Check(Text(draft, "body") == "/azp run", "CI preparation must choose the host's fixed /azp run command.");
                else Check(Text(draft, "body").Length == 0, "Merge preparation must not append a comment body.");
            }
            Check(!record.ContainsKey("account") && fixture.Store.ReadResult(run.Id)!.ToJsonString() == savedBefore && Text(fixture.Store.ReadStatus(run.Id), "state") == "succeeded",
                "Local preparation must neither resolve an execution account nor rewrite the completed task or its result.");
            if (kind != "create-pr")
            {
                var withoutReview = fixture.Create(kind, mutateModel: model => model["review"] = null);
                var fromTask = fixture.Operation(fixture.Prepare(withoutReview))["draft"]!.AsObject();
                Check(Text(fromTask, "expectedHeadSha") == Sha, "A typed merge or CI proposal can bind directly to the verified task SHA without a separate review draft.");
            }
        }
    }

    private static void RetryIdentityIncludesRunAndProposal()
    {
        using var fixture = new Fixture();
        var run = fixture.Create("create-pr");
        var first = fixture.Prepare(run);
        var original = fixture.Operation(first);
        var again = new ResultActions(fixture.Store).Prepare(Request(run));
        Check(Text(again, "operationId") == Text(first, "operationId") && Text(fixture.Operation(again)["draft"]!.AsObject(), "requestId") == Text(original["draft"]!.AsObject(), "requestId"),
            "Repeated clicks and a new Host object must reuse the exact pending operation and deterministic request UUID.");

        var otherRun = fixture.Create("create-pr");
        var other = fixture.Prepare(otherRun);
        Check(otherRun.ProposalId == run.ProposalId && Text(fixture.Operation(other)["draft"]!.AsObject(), "requestId") != Text(original["draft"]!.AsObject(), "requestId") && Text(other, "operationId") != Text(first, "operationId"),
            "Identical proposal content in another run must have a distinct request identity bound to that run.");

        var multi = fixture.Create("create-pr", mutateModel: model => model["nextActions"]!.AsArray().Add(model["nextActions"]![0]!.DeepClone()));
        var projected = WorkflowResult.WithProposalIds(fixture.Store.ReadResult(multi.Id)!)["nextActions"]!.AsArray().OfType<JsonObject>().Where(action => Text(action, "kind") == "create-pr").ToArray();
        var a = new ResultActions(fixture.Store).Prepare(new JsonObject { ["runId"] = multi.Id, ["proposalId"] = ProposalId(projected[0]) });
        var b = new ResultActions(fixture.Store).Prepare(new JsonObject { ["runId"] = multi.Id, ["proposalId"] = ProposalId(projected[1]) });
        Check(Text(a, "operationId") != Text(b, "operationId") && Text(fixture.Operation(a)["draft"]!.AsObject(), "requestId") != Text(fixture.Operation(b)["draft"]!.AsObject(), "requestId"),
            "Two independently selected occurrences must not become the same prepared operation merely because their kind and body match.");
    }

    private static void CallerCannotOverrideSavedDraft()
    {
        using var fixture = new Fixture();
        var run = fixture.Create("merge-pr");
        foreach (var field in new[] { "endpoint", "sourceOrigin", "target", "body", "expectedHeadSha", "kind" })
        {
            var request = Request(run);
            request[field] = field == "target" ? new JsonObject { ["repository"] = "other/repo", ["type"] = "pr", ["number"] = 99 } : JsonValue.Create("caller-controlled");
            Throws("INVALID_REQUEST", () => new ResultActions(fixture.Store).Prepare(request));
        }
        Throws("RESULT_PROPOSAL_NOT_FOUND", () => new ResultActions(fixture.Store).Prepare(new JsonObject { ["runId"] = run.Id, ["proposalId"] = "proposal-ffffffffffffffffffffffff-99" }));
        Throws("INVALID_REQUEST", () => new ResultActions(fixture.Store).Prepare(new JsonObject { ["runId"] = "../outside", ["proposalId"] = run.ProposalId }));
        Check(fixture.OperationCount() == 0, "Rejected overrides and unknown proposals must not leave a prepared operation behind.");
    }

    private static void DraftContentEditsKeepSourceIdentity()
    {
        using var fixture = new Fixture();
        var run = fixture.Create("create-pr", mutateModel: model => model["nextActions"]![0]!["pullRequest"]!["draft"] = false);
        var service = new ResultActions(fixture.Store);
        var request = Request(run); request["attemptId"] = Guid.NewGuid().ToString("D");
        request["content"] = new JsonObject { ["title"] = "Review the focus fix", ["body"] = "  Human-edited description.  \n\nRefs #7\n" };
        var prepared = service.Prepare(request);
        var record = fixture.Operation(prepared);
        var pr = record["draft"]!["pullRequest"]!.AsObject();
        Check(pr["draft"]?.GetValue<bool>() == true && Text(pr, "title") == "Review the focus fix" && Text(pr, "body") == "  Human-edited description.  \n\nRefs #7\n",
            "Result preparation forces Draft while retaining the explicitly edited title and complete description.");
        Check(Text(pr, "head") == "muyuanms:fix-issue-7" && Text(pr, "base") == "main" && Text(pr, "sourceHeadSha") == Sha,
            "Content edits cannot change the saved task branch, base or verified source SHA.");
        Check(fixture.Store.ReadResult(run.Id)!["nextActions"]![0]!["pullRequest"]!["draft"]?.GetValue<bool>() == false,
            "Fixed Draft preparation does not rewrite the saved report or its original proposal.");
        ResultActions.ValidatePreparedSource(fixture.Store, record);
        Check(Text(service.Prepare(request), "operationId") == Text(prepared, "operationId"), "The same content and preparation attempt recover the same immutable operation.");
        var changed = request.DeepClone().AsObject(); changed["content"]!["body"] = "Different description";
        Throws("REQUEST_CONFLICT", () => service.Prepare(changed));
        // An operation saved before its attempt index is authoritative during crash recovery.
        var attemptPath = Path.Combine(fixture.Store.RunDirectory(run.Id), "result-action-attempts", Text(prepared, "requestId") + ".json");
        File.Delete(attemptPath);
        Throws("REQUEST_CONFLICT", () => service.Prepare(changed));
        Check(Text(service.Prepare(request), "operationId") == Text(prepared, "operationId"), "Crash recovery preserves the original edited payload identity.");
        foreach (var field in new[] { "head", "base", "sourceHeadSha", "draft" })
        {
            var tampered = record.DeepClone().AsObject();
            tampered["draft"]!["pullRequest"]![field] = field == "draft" ? JsonValue.Create(false) : JsonValue.Create("changed");
            Throws("TARGET_MISMATCH", () => ResultActions.ValidatePreparedSource(fixture.Store, tampered));
            var overrideRequest = request.DeepClone().AsObject(); overrideRequest["content"]![field] = "override";
            Throws("INVALID_REQUEST", () => service.Prepare(overrideRequest));
        }
        var alias = changed.DeepClone().AsObject(); alias["attemptId"] = Guid.NewGuid().ToString("D");
        Check(Text(service.Prepare(alias), "operationId") == Text(prepared, "operationId"), "A different draft cannot replace an unresolved prepared action.");
        new WebActions(fixture.Store).Cancel(new JsonObject { ["operationId"] = prepared["operationId"]!.DeepClone() });
        changed["attemptId"] = Guid.NewGuid().ToString("D"); changed["retry"] = true;
        var edited = service.Prepare(changed);
        Check(Text(edited, "operationId") != Text(prepared, "operationId") && Text(fixture.Operation(edited)["draft"]!["pullRequest"]!.AsObject(), "body") == "Different description",
            "Cancelling an unsubmitted confirmation allows a deliberate new preparation with edited content.");
        var merge = Request(fixture.Create("merge-pr")); merge["attemptId"] = Guid.NewGuid().ToString("D"); merge["content"] = request["content"]!.DeepClone();
        Throws("INVALID_REQUEST", () => service.Prepare(merge));
        var missingAttempt = Request(run); missingAttempt["content"] = request["content"]!.DeepClone();
        Throws("INVALID_REQUEST", () => service.Prepare(missingAttempt));
    }

    private static void AttemptIdentityAndSafeRetries()
    {
        using var fixture = new Fixture();
        var service = new ResultActions(fixture.Store);
        var run = fixture.Create("create-pr");
        var firstRequest = Request(run); firstRequest["attemptId"] = Guid.NewGuid().ToString("D");
        var first = service.Prepare(firstRequest);
        var aliasRequest = Request(run); aliasRequest["attemptId"] = Guid.NewGuid().ToString("D");
        Check(Text(service.Prepare(aliasRequest), "operationId") == Text(first, "operationId"), "Another click while confirmation is open must reuse the pending draft.");
        var cancelled = new WebActions(fixture.Store).Cancel(new JsonObject { ["operationId"] = first["operationId"]!.DeepClone() });
        Check(cancelled["retryAllowed"]?.GetValue<bool>() == true, "A cancelled confirmation must offer a new confirmation attempt without rerunning the agent.");
        Check(Text(service.Prepare(firstRequest), "status") == "cancelled" && Text(service.Prepare(aliasRequest), "status") == "cancelled",
            "Both original and coalesced attempts remain bound to their original cancelled record after a lost response.");
        var secondRequest = Request(run); secondRequest["attemptId"] = Guid.NewGuid().ToString("D"); secondRequest["retry"] = true;
        var second = service.Prepare(secondRequest);
        Check(Text(second, "operationId") != Text(first, "operationId") && Text(second, "status") == "prepared" &&
            Text(second, "attemptId") == Text(secondRequest, "attemptId"), "A deliberate new attempt must reopen the original proposal as a fresh confirmation.");
        Check(Text(service.Prepare(secondRequest), "operationId") == Text(second, "operationId"), "Transport retries must preserve the new confirmation identity.");
        var conflict = secondRequest.DeepClone().AsObject(); conflict["retry"] = false;
        Throws("REQUEST_CONFLICT", () => service.Prepare(conflict));
        fixture.SetOperationState(second, "failed");
        var thirdRequest = Request(run); thirdRequest["attemptId"] = Guid.NewGuid().ToString("D");
        var third = service.Prepare(thirdRequest);
        Check(Text(third, "status") == "prepared" && Text(third, "operationId") != Text(second, "operationId"), "A known failure before any successful write must support a new confirmation attempt.");
        fixture.SetOperationState(third, "unknown");
        var unknownRequest = Request(run); unknownRequest["attemptId"] = Guid.NewGuid().ToString("D"); unknownRequest["retry"] = true;
        var unknown = service.Prepare(unknownRequest);
        Check(Text(unknown, "operationId") == Text(third, "operationId") && unknown["retryAllowed"]?.GetValue<bool>() == false,
            "An unknown remote outcome must only reopen its existing reconciliation view, never prepare another write.");
        fixture.SetOperationState(third, "partial");
        var partialRequest = Request(run); partialRequest["attemptId"] = Guid.NewGuid().ToString("D");
        Check(Text(service.Prepare(partialRequest), "operationId") == Text(third, "operationId"), "Partial operations must remain bound to their retained evidence.");
        fixture.SetOperationState(third, "succeeded");
        var successRequest = Request(run); successRequest["attemptId"] = Guid.NewGuid().ToString("D"); successRequest["retry"] = true;
        Check(Text(service.Prepare(successRequest), "operationId") == Text(third, "operationId"), "A successful create-PR proposal must open its existing result even when retry is requested.");

        var ci = fixture.Create("trigger-ci");
        var ciFirst = fixture.Prepare(ci);
        fixture.SetOperationState(ciFirst, "succeeded");
        var normalCi = Request(ci); normalCi["attemptId"] = Guid.NewGuid().ToString("D");
        Check(Text(service.Prepare(normalCi), "operationId") == Text(ciFirst, "operationId"), "Clicking a successful CI proposal normally must open its recorded result.");
        var retryCi = Request(ci); retryCi["attemptId"] = Guid.NewGuid().ToString("D"); retryCi["retry"] = true;
        var ciAgain = service.Prepare(retryCi);
        Check(Text(ciAgain, "operationId") != Text(ciFirst, "operationId") && Text(ciAgain, "status") == "prepared" && ciAgain["retryRequested"]?.GetValue<bool>() == true,
            "Only an explicit CI retry may prepare another confirmation; current CI evidence is checked before submission.");
        Check(Text(service.Prepare(retryCi), "operationId") == Text(ciAgain, "operationId"), "The CI retry is itself idempotent.");
        var invalid = Request(ci); invalid["retry"] = true;
        Throws("INVALID_REQUEST", () => service.Prepare(invalid));
        invalid["attemptId"] = "../escape";
        Throws("INVALID_REQUEST", () => service.Prepare(invalid));
    }

    private static void AttemptHistoryIsScoped()
    {
        using var fixture = new Fixture();
        var firstRun = fixture.Create("create-pr");
        var secondRun = fixture.Create("create-pr");
        var first = fixture.Prepare(firstRun);
        fixture.Prepare(secondRun);
        var service = new ResultActions(fixture.Store);
        var list = service.List(new JsonObject { ["runId"] = firstRun.Id });
        var history = list["operations"]!.AsArray();
        Check(history.Count == 1 && list["totalCount"]?.GetValue<int>() == 1 && list["truncated"]?.GetValue<bool>() == false &&
            Text(history[0]!.AsObject(), "runId") == firstRun.Id && Text(history[0]!.AsObject(), "proposalId") == firstRun.ProposalId && Text(history[0]!.AsObject(), "operationId") == Text(first, "operationId"),
            "Task history must expose the exact task/proposal/action association and exclude other task records.");
        Check(service.HasUnresolved(firstRun.Id), "A prepared GitHub confirmation must prevent removal of its source task.");
        fixture.Store.SetView(firstRun.Id, "read", true);
        fixture.Store.SetView(firstRun.Id, "handled", true);
        Throws("OPERATION_UNRESOLVED", () => fixture.Store.DeleteRun(firstRun.Id));
        new WebActions(fixture.Store).Cancel(new JsonObject { ["operationId"] = first["operationId"]!.DeepClone() });
        Check(!service.HasUnresolved(firstRun.Id) && service.HasUnresolved(secondRun.Id), "A cancelled action must not pin an unrelated task or prevent safe removal of its own source.");
        foreach (var state in new[] { "submitting", "unknown", "partial" })
        {
            fixture.SetOperationState(first, state);
            Check(service.HasUnresolved(firstRun.Id), "The " + state + " action must keep its source result available for reconciliation.");
            Throws("OPERATION_UNRESOLVED", () => fixture.Store.DeleteRun(firstRun.Id));
        }
        fixture.SetOperationState(first, "cancelled");
        fixture.Store.DeleteRun(firstRun.Id);
        Check(!Directory.Exists(fixture.Store.RunDirectory(firstRun.Id)) && Text(new WebActions(fixture.Store).Get(new JsonObject { ["operationId"] = first["operationId"]!.DeepClone() }, "pulse-task://" + firstRun.Id), "status") == "cancelled",
            "Cancellation must permit deletion of the handled task while the durable action remains terminal and cannot be resent.");
    }

    private static void RejectsUnavailableResultsAndActions()
    {
        using var fixture = new Fixture();
        var oldKind = fixture.Create("comment");
        Throws("RESULT_ACTION_UNSUPPORTED", () => fixture.Prepare(oldKind));

        var blocked = fixture.Create("create-pr", mutateModel: model => model["outcome"] = "blocked");
        Throws("RESULT_NOT_ACTIONABLE", () => fixture.Prepare(blocked));
        var missingEvidence = fixture.Create("create-pr", mutateModel: model => model["validation"]![0]!["evidence"] = new JsonArray());
        Throws("RESULT_NOT_ACTIONABLE", () => fixture.Prepare(missingEvidence));
        var unpinnedSource = fixture.Create("create-pr", mutateModel: model => model["nextActions"]![0]!["pullRequest"]!.AsObject().Remove("sourceHeadSha"), normalize: false);
        Throws("RESULT_NOT_ACTIONABLE", () => fixture.Prepare(unpinnedSource));
        var failedExecution = fixture.Create("merge-pr", executionState: "failed", exitCode: 1);
        Check(Text(fixture.Prepare(failedExecution), "status") == "prepared", "A failed analysis does not prohibit the user's separately confirmed PR merge choice.");

        // Keep structurally valid but target-ineligible proposals in these isolated persisted
        // fixtures so the bridge must independently enforce target/SHA eligibility.
        var issueMerge = fixture.Create("merge-pr", targetType: "issue", normalize: false);
        Throws("INVALID_TARGET", () => fixture.Prepare(issueMerge));
        var prCreation = fixture.Create("create-pr", targetType: "pr", normalize: false);
        Throws("RESULT_NOT_ACTIONABLE", () => fixture.Prepare(prCreation));
        var missingSha = fixture.Create("trigger-ci", mutateTask: task => task.Remove("expectedHeadSha"), normalize: false);
        Throws("MISSING_HEAD_SHA", () => fixture.Prepare(missingSha));
        var changedReview = fixture.Create("merge-pr", mutateModel: model => model["review"]!["headSha"] = new string('b', 40), normalize: false);
        Check(Text(fixture.Prepare(changedReview), "status") == "prepared", "Human merge is fixed to the task SHA, independent of an AI review draft.");
        foreach (var kind in new[] { "create-pr", "merge-pr", "trigger-ci" })
        {
            var legacy = fixture.CreateLegacy(kind);
            Throws("RESULT_NOT_ACTIONABLE", () => fixture.Prepare(legacy));
        }
        Check(fixture.OperationCount() == 2, "Invalid issue publication drafts and actual target/SHA mismatches are rejected while explicit PR choices remain available.");
    }

    private static void PreparedProposalKeepsHumanChoiceAfterRecovery()
    {
        using var fixture = new Fixture();
        var run = fixture.Create("merge-pr");
        var prepared = fixture.Prepare(run);
        var blocked = WorkflowResult.Failure("interrupted", "WORKER_INTERRUPTED", "Recovered incomplete persistence.", run.Task);
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(run.Id), "result.json"), blocked);
        ResultActions.ValidatePreparedSource(fixture.Store, fixture.Operation(prepared));
        var mismatched = fixture.Operation(prepared); mismatched["draft"]!["target"]!["number"] = 999;
        Throws("TARGET_MISMATCH", () => ResultActions.ValidatePreparedSource(fixture.Store, mismatched));
        Check(Text(fixture.Operation(prepared), "status") == "prepared",
            "Recovered analysis failure does not change a pending human merge choice; its immutable PR target still must match.");
    }

    private static void FixedHumanActionsNeedNoModelProposal()
    {
        using var fixture = new Fixture();
        foreach (var state in new[] { "running", "failed", "cancelled", "interrupted", "succeeded" })
        {
            var id = Guid.NewGuid().ToString("D");
            var task = new JsonObject { ["requestId"] = id, ["actionId"] = "fixed-human-fixture", ["actionKind"] = "pr-review", ["repository"] = "microsoft/PowerToys",
                ["target"] = new JsonObject { ["type"] = "pr", ["number"] = 7 }, ["expectedHeadSha"] = Sha, ["prompt"] = "Retained analysis context." };
            fixture.Store.CreateRun(id, task, new JsonObject(), Protocol.ProductionOrigin);
            var status = fixture.Store.ReadStatus(id); status["state"] = state; status["exitCode"] = null;
            fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(id), "status.json"), status);
            foreach (var kind in new[] { "merge-pr", "trigger-ci" })
            {
                var request = new JsonObject { ["runId"] = id, ["kind"] = kind, ["attemptId"] = Guid.NewGuid().ToString("D") };
                var prepared = new ResultActions(fixture.Store).Prepare(request);
                Check(Text(prepared, "status") == "prepared" && Text(prepared, "kind") == kind && fixture.Store.ReadResult(id) is null,
                    "Fixed " + kind + " is available without any model output during " + state);
                var record = fixture.Operation(prepared); ResultActions.ValidatePreparedSource(fixture.Store, record);
                Check(Text(record["draft"]!.AsObject(), "expectedHeadSha") == Sha &&
                    record["draft"]!["target"]!["number"]?.GetValue<int>() == 7, "Manual preparation pins the saved PR target and SHA.");
                Check(Text(new ResultActions(fixture.Store).Prepare(request), "operationId") == Text(prepared, "operationId"),
                    "Lost responses cannot create another manual confirmation.");
                var mixed = request.DeepClone().AsObject(); mixed["proposalId"] = "proposal-other";
                Throws("INVALID_REQUEST", () => new ResultActions(fixture.Store).Prepare(mixed));
            }
        }
    }

    private static void SupplementalEvidenceDoesNotGateHumanMerge()
    {
        using var fixture = new Fixture();
        SavedRun Parent() => fixture.Create("merge-pr", mutateTask: task => task["reviewOptions"] = new JsonObject { ["mode"] = "static" }, mutateModel: model =>
        {
            model["assessment"] = new JsonObject { ["subject"] = "original-pr", ["status"] = "passed", ["summary"] = "Original review assessment.", ["revisionSha"] = Sha };
            model["reviewConclusion"] = new JsonObject { ["status"] = "no-blocking-findings", ["summary"] = "No blocking source finding.", ["revisionSha"] = Sha, ["blockingUncertainties"] = new JsonArray() };
            model["verificationRecommendation"] = new JsonObject
            {
                ["mode"] = "ui-e2e", ["reason"] = "Additional runtime verification is useful.", ["question"] = "Does startup work under the affected configuration?",
                ["scenarios"] = new JsonArray("Start the affected configuration."), ["prerequisites"] = new JsonArray(), ["evidence"] = new JsonArray("The changed code affects startup."), ["readiness"] = "ready"
            };
        });
        void AddFailingSupplement(SavedRun parent, string assessmentStatus)
        {
            var parentResult = fixture.Store.ReadResult(parent.Id)!;
            var childId = Guid.NewGuid().ToString("D");
            var task = new JsonObject
            {
                ["requestId"] = childId, ["actionId"] = "typed-action-verification", ["actionKind"] = "pr-verify", ["repository"] = parent.Task["repository"]!.DeepClone(),
                ["target"] = parent.Task["target"]!.DeepClone(), ["expectedHeadSha"] = Sha, ["reviewOptions"] = new JsonObject { ["mode"] = "ui-e2e" }, ["prompt"] = "Controlled supplemental verification fixture.",
                ["followUp"] = new JsonObject { ["parentRunId"] = parent.Id, ["recommendationId"] = ReviewFollowUps.RecommendationId(parentResult["verificationRecommendation"]!.AsObject()),
                    ["parentResultFingerprint"] = Protocol.Fingerprint(parentResult), ["subject"] = "original-pr", ["revisionSha"] = Sha }
            };
            var model = new JsonObject
            {
                ["schemaVersion"] = 2, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "Runtime verification produced a failing same-revision observation.",
                ["assessment"] = new JsonObject { ["subject"] = "original-pr", ["status"] = assessmentStatus, ["summary"] = "Recorded product assessment.", ["revisionSha"] = Sha },
                ["reviewConclusion"] = null, ["review"] = null, ["verificationRecommendation"] = null, ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(),
                ["diagnostics"] = new JsonArray(), ["needsReview"] = true,
                ["nextActions"] = new JsonArray(FlatProposal("inspectResult")),
                ["verificationEvidence"] = new JsonArray(new JsonObject
                {
                    ["id"] = "startup-failure", ["source"] = "current-run", ["kind"] = "runtime", ["status"] = "failed", ["subject"] = "original-pr", ["revisionSha"] = Sha,
                    ["summary"] = "The tested startup scenario failed.", ["evidence"] = new JsonArray("The controlled scenario log preserves the failure."), ["runId"] = null
                }),
                ["validation"] = new JsonArray(ReviewModes.RequiredChecks(task).Select(id => (JsonNode?)new JsonObject
                {
                    ["id"] = id, ["name"] = id, ["status"] = "passed", ["required"] = true, ["details"] = "The verification check was executed and interpreted.", ["evidence"] = new JsonArray("Controlled fixture evidence.")
                }).ToArray())
            };
            var result = WorkflowResult.FromModel(model.ToJsonString(), task, "succeeded", 0, null, null, expectedSchemaVersion: 2);
            Check(WorkflowResult.IsValidStoredV2(result) && Text(result, "outcome") == "completed", "A successful supplemental workflow can report failed product evidence.");
            fixture.Store.CreateRun(childId, task, new JsonObject(), Protocol.ProductionOrigin);
            fixture.Store.Complete(childId, "succeeded", 0, result);
            foreach (var id in new[] { parent.Id, childId })
            {
                var snapshot = new JsonObject { ["headSha"] = Sha, ["workingTree"] = "clean", ["diffHash"] = "clean", ["capturedAt"] = Protocol.Now() };
                fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(id), "provenance.json"), ReviewProvenance.Compose(Sha, snapshot, snapshot.DeepClone().AsObject()));
            }
        }
        foreach (var assessment in new[] { "failed", "passed" })
        {
            // A passing top-level assessment must not hide an explicitly failed observation.
            var beforePreparation = Parent(); var original = fixture.Store.ReadResult(beforePreparation.Id)!;
            AddFailingSupplement(beforePreparation, assessment);
            Check(Text(fixture.Prepare(beforePreparation), "status") == "prepared", "Supplemental failure changes advice but does not gate a user's explicit merge choice.");
            Check(JsonNode.DeepEquals(fixture.Store.ReadResult(beforePreparation.Id), original), "Preparing a human action must preserve the immutable source report and its failures.");

            var afterPreparation = Parent();
            var parentPath = Path.Combine(fixture.Store.RunDirectory(afterPreparation.Id), "result.json");
            var parentBytes = File.ReadAllBytes(parentPath);
            var prepared = fixture.Prepare(afterPreparation);
            var record = fixture.Operation(prepared); var draft = record["draft"]!.DeepClone();
            ResultActions.ValidatePreparedSource(fixture.Store, record);
            AddFailingSupplement(afterPreparation, assessment);
            ResultActions.ValidatePreparedSource(fixture.Store, record);
            Check(File.ReadAllBytes(parentPath).SequenceEqual(parentBytes) && JsonNode.DeepEquals(fixture.Operation(prepared)["draft"], draft),
                "Effective action eligibility must not replace source result bytes, proposal identity, or the prepared target and draft.");
        }
    }

    private static void BothIssueKindsPrepareDuplicateClosure()
    {
        using var fixture = new Fixture();
        foreach (var kind in new[] { "feature-investigate", "bug-investigate" })
        {
            var id = Guid.NewGuid().ToString("D");
            var task = new JsonObject { ["requestId"] = id, ["actionId"] = "duplicate-fixture", ["actionKind"] = kind, ["repository"] = "microsoft/powertoys",
                ["target"] = new JsonObject { ["type"] = "issue", ["number"] = 7 }, ["prompt"] = "Controlled duplicate research." };
            var duplicateOf = new JsonObject { ["repository"] = "Microsoft/PowerToys", ["number"] = 23, ["url"] = "https://github.com/Microsoft/PowerToys/issues/23" };
            var result = WorkflowResult.Failure("succeeded", null, "The investigation identified the same requested behavior.", task, 0, "reporting", expectedSchemaVersion: 3);
            result["outcome"] = "completed"; result["report"] = new JsonObject { ["complete"] = true, ["rechecked"] = true, ["coverage"] = new JsonArray("Compared both issue descriptions and current code."), ["limitations"] = new JsonArray() };
            result["diagnostics"] = new JsonArray(); result["blockers"] = new JsonArray();
            result["nextActions"] = new JsonArray(new JsonObject { ["kind"] = "close-as-duplicate", ["reason"] = "The same behavior is tracked in the original issue.", ["recommended"] = true,
                ["body"] = "    Both reports describe the same behavior.\n", ["duplicateOf"] = duplicateOf.DeepClone() });
            if (kind == "feature-investigate") result["featureAssessment"] = new JsonObject
            {
                ["status"] = "duplicate", ["summary"] = "The feature is already tracked.", ["reasons"] = new JsonArray("Same request."), ["evidence"] = new JsonArray("Both requests name the same workflow."),
                ["acceptanceCriteria"] = new JsonArray(), ["questions"] = new JsonArray(), ["alternatives"] = new JsonArray(), ["relatedIssue"] = duplicateOf.DeepClone(), ["planId"] = null
            };
            else result["bugAssessment"] = new JsonObject
            {
                ["status"] = "duplicate", ["summary"] = "The same bug is already tracked.", ["reasons"] = new JsonArray("Same reported behavior."), ["evidence"] = new JsonArray("Both reports identify the same failing path."),
                ["questions"] = new JsonArray(), ["relatedIssue"] = duplicateOf.DeepClone(), ["planId"] = null,
                ["reproduction"] = new JsonObject { ["status"] = "not_run", ["revisionSha"] = null, ["environment"] = "Not needed to establish the documented duplicate.", ["steps"] = new JsonArray(), ["expected"] = "", ["observed"] = "", ["evidence"] = new JsonArray() }
            };
            Check(WorkflowResult.IsValidStoredV3(result), "Both issue kinds use a valid v3 duplicate contract.");
            fixture.Store.CreateRun(id, task, new JsonObject(), Protocol.ProductionOrigin);
            fixture.Store.Complete(id, "succeeded", 0, result);
            var proposalId = ProposalId(WorkflowResult.WithProposalIds(result)["nextActions"]![0]!.AsObject());
            var payload = new JsonObject { ["runId"] = id, ["proposalId"] = proposalId, ["attemptId"] = Guid.NewGuid().ToString("D") };
            var prepared = new ResultActions(fixture.Store).Prepare(payload);
            var record = fixture.Operation(prepared); var draft = record["draft"]!.AsObject();
            Check(Text(prepared, "kind") == "close-as-duplicate" && Text(prepared, "status") == "prepared" && draft["duplicateOf"]?["repository"]?.GetValue<string>() == "microsoft/powertoys" &&
                draft["duplicateOf"]?["number"]?.GetValue<int>() == 23 &&
                Text(draft, "body") == "    Both reports describe the same behavior.\n", "Duplicate preparation preserves the exact original issue and editable explanation for " + kind);
            ResultActions.ValidatePreparedSource(fixture.Store, record);
            var tampered = record.DeepClone().AsObject(); tampered["draft"]!["duplicateOf"]!["number"] = 24;
            Throws("TARGET_MISMATCH", () => ResultActions.ValidatePreparedSource(fixture.Store, tampered));
            fixture.SetOperationState(prepared, "partial");
            var retry = payload.DeepClone().AsObject(); retry["attemptId"] = Guid.NewGuid().ToString("D"); retry["retry"] = true;
            Check(Text(new ResultActions(fixture.Store).Prepare(retry), "operationId") == Text(prepared, "operationId"), "A partially completed duplicate must retain the original operation for its remaining close step.");
        }
    }

    private static void BlockedReportsCanProposeCiRecovery()
    {
        using var fixture = new Fixture();
        void Blocked(JsonObject model)
        {
            model["outcome"] = "blocked";
            model["validation"]![2]!["status"] = "failed";
            model["validation"]![2]!["details"] = "The complete report records failing product verification.";
        }
        var report = fixture.Create("trigger-ci", mutateModel: Blocked);
        Check(Text(fixture.Prepare(report), "status") == "prepared", "A valid blocked workflow report from a successful CLI may offer CI as an extension-confirmed recovery action.");
        var merge = fixture.Create("merge-pr", mutateModel: Blocked);
        Check(Text(fixture.Prepare(merge), "status") == "prepared", "Human merge can be confirmed separately from a blocked workflow report.");
        var invalid = fixture.Create("trigger-ci", mutateModel: model => model["diagnostics"]!.AsArray().Add(new JsonObject
        {
            ["code"] = "OUTPUT_INCOMPLETE", ["severity"] = "error", ["message"] = "Output could not be fully retained.", ["recovery"] = "inspectResult"
        }));
        Check(Text(fixture.Prepare(invalid), "status") == "prepared", "Incomplete model output does not prohibit a user's fixed CI action.");
    }

    private static JsonObject FlatProposal(string kind, string body = "", string reason = "Follow up on the verified result.") => new() { ["kind"] = kind, ["reason"] = reason, ["body"] = body };
    private static JsonObject TypedProposal(string kind)
    {
        var proposal = FlatProposal(kind, kind == "comment" ? "A reviewed comment." : "");
        if (kind == "create-pr") proposal["pullRequest"] = new JsonObject
        {
            ["head"] = "muyuanms:fix-issue-7", ["base"] = "main", ["title"] = "Fix the verified issue",
            ["body"] = "    preserve_indentation();\n\nResolves #7\n", ["draft"] = true, ["sourceHeadSha"] = Sha
        };
        return proposal;
    }
    private static JsonObject Request(SavedRun run) => new() { ["runId"] = run.Id, ["proposalId"] = run.ProposalId };
    private static string Text(JsonObject row, string field) => row[field]?.GetValue<string>() ?? "";
    private static string ProposalId(JsonObject row) => Text(row, "proposalId");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(string code, Action action)
    {
        try { action(); } catch (ProtocolException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected ProtocolException " + code);
    }
    private sealed record SavedRun(string Id, JsonObject Task, JsonObject Model, string ProposalId);

    private sealed class Fixture : IDisposable
    {
        private readonly string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string root;
        public Store Store { get; }
        public Fixture()
        {
            root = Path.GetFullPath(Path.Combine(temporary, "PulseResultActions-" + Guid.NewGuid().ToString("N")));
            Store = new Store(root);
        }
        public SavedRun Create(string kind, string? targetType = null, Action<JsonObject>? mutateModel = null, Action<JsonObject>? mutateTask = null, string executionState = "succeeded", int exitCode = 0, bool normalize = true)
        {
            var id = Guid.NewGuid().ToString("D");
            targetType ??= kind is "create-pr" or "comment" ? "issue" : "pr";
            var actionKind = targetType == "issue" ? "issue-fix" : "pr-review";
            var task = new JsonObject
            {
                ["requestId"] = id, ["actionId"] = "task-fixture", ["actionKind"] = actionKind, ["repository"] = "microsoft/PowerToys",
                ["target"] = new JsonObject { ["type"] = targetType, ["number"] = 7 }, ["prompt"] = "Offline result-action fixture."
            };
            if (targetType == "pr") task["expectedHeadSha"] = Sha;
            mutateTask?.Invoke(task);
            var checks = new JsonArray();
            foreach (var checkId in targetType == "issue" ? new[] { "reproduction", "implementation", "verification" } : new[] { "context", "local-review", "verification" })
                checks.Add(new JsonObject { ["id"] = checkId, ["name"] = checkId, ["status"] = "passed", ["required"] = true, ["details"] = "Controlled fixture check completed.", ["evidence"] = new JsonArray("Offline fixture evidence.") });
            var model = new JsonObject
            {
                ["schemaVersion"] = 2, ["outcome"] = "completed", ["phase"] = "reporting", ["summary"] = "The local fixture workflow completed.",
                ["findings"] = new JsonArray(), ["artifacts"] = new JsonArray(), ["validation"] = checks, ["diagnostics"] = new JsonArray(),
                ["nextActions"] = new JsonArray(TypedProposal(kind)), ["needsReview"] = true,
                ["review"] = targetType == "pr" ? new JsonObject { ["headSha"] = Sha, ["body"] = "Verified local review.", ["suggestions"] = new JsonArray() } : null
            };
            mutateModel?.Invoke(model);
            var originalProposal = WorkflowResult.WithProposalIds(model)["nextActions"]!.AsArray().OfType<JsonObject>().First(row => Text(row, "kind") == kind);
            var normalized = normalize ? WorkflowResult.FromModel(model.ToJsonString(), task, executionState, exitCode, executionState == "failed" ? "CLI_EXECUTION_FAILED" : null,
                executionState == "failed" ? "Controlled fixture process failure." : null, expectedSchemaVersion: 2) : model.DeepClone().AsObject();
            if (!normalize) { normalized["structured"] = true; normalized["cliExitCode"] = exitCode; }
            var proposal = WorkflowResult.WithProposalIds(normalized)["nextActions"]!.AsArray().OfType<JsonObject>().FirstOrDefault(row => Text(row, "kind") == kind) ?? originalProposal;
            Store.CreateRun(id, task, new JsonObject { ["agent"] = "codex", ["permission"] = "read-only", ["githubAccount"] = "" }, Protocol.ProductionOrigin, new JsonObject { ["schemaVersion"] = 2 });
            Store.Complete(id, executionState, exitCode, normalized);
            return new SavedRun(id, task, model, ProposalId(proposal));
        }
        public JsonObject Prepare(SavedRun run) => new ResultActions(Store).Prepare(Request(run));
        public SavedRun CreateLegacy(string kind)
        {
            var id = Guid.NewGuid().ToString("D");
            var isIssue = kind == "create-pr";
            var task = new JsonObject
            {
                ["requestId"] = id, ["actionId"] = "legacy-fixture", ["actionKind"] = isIssue ? "issue-fix" : "pr-review", ["repository"] = "microsoft/PowerToys",
                ["target"] = new JsonObject { ["type"] = isIssue ? "issue" : "pr", ["number"] = 7 }, ["prompt"] = "Historical fixture."
            };
            if (!isIssue) task["expectedHeadSha"] = Sha;
            var legacy = new JsonObject
            {
                ["schemaVersion"] = 1, ["summary"] = "Legacy execution succeeded without the v2 workflow contract.", ["structured"] = true, ["cliExitCode"] = 0,
                ["needsReview"] = false, ["artifacts"] = new JsonArray(), ["blockers"] = new JsonArray(), ["review"] = null,
                ["validation"] = new JsonArray(new JsonObject { ["name"] = "Historical check", ["status"] = "passed", ["details"] = "Legacy evidence." }),
                ["nextSteps"] = new JsonArray(TypedProposal(kind))
            };
            Store.CreateRun(id, task, new JsonObject { ["agent"] = "codex", ["permission"] = "read-only", ["githubAccount"] = "" }, Protocol.ProductionOrigin);
            Store.Complete(id, "succeeded", 0, legacy);
            var proposal = WorkflowResult.WithProposalIds(legacy)["nextSteps"]![0]!.AsObject();
            return new SavedRun(id, task, legacy, ProposalId(proposal));
        }
        public JsonObject Operation(JsonObject summary) => Store.ReadJson(Path.Combine(Store.Root, "web-actions", "operations", Text(summary, "operationId") + ".json"))
            ?? throw new InvalidOperationException("The prepared operation must be persisted locally.");
        public void SetOperationState(JsonObject summary, string state)
        {
            var record = Operation(summary);
            record["status"] = state;
            record["activeStep"] = state is "unknown" or "submitting" ? Text(record["draft"]!.AsObject(), "kind") : null;
            record["completedSteps"] = state is "succeeded" or "partial" ? record["steps"]!.DeepClone() : new JsonArray();
            if (state == "failed") record["error"] = new ProtocolException("GITHUB_REQUEST_FAILED", "Controlled write rejection.").ToJson();
            Store.WriteJson(Path.Combine(Store.Root, "web-actions", "operations", Text(summary, "operationId") + ".json"), record);
        }
        public int OperationCount()
        {
            var directory = Path.Combine(Store.Root, "web-actions", "operations");
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Count() : 0;
        }
        public void Dispose()
        {
            Check(string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("PulseResultActions-", StringComparison.Ordinal),
                "Result-action fixture cleanup must stay within its newly created direct child of temp.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
