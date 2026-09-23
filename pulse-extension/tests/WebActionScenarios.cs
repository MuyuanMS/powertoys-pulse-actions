using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Pulse.Host.Tests;

/// <summary>Web-origin GitHub drafts and extension confirmation, using only an offline HTTP handler.</summary>
public static class WebActionScenarios
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Upstream = "microsoft/powertoys";
    private const string Fork = "muyuanms/powertoys";

    public static async Task RunAllAsync()
    {
        PreparationAndIsolation();
        await PreviewAndFixedApproval();
        await WebsiteApprovalUsesConfirmedP0Policy();
        await ReviewCoordinates();
        await StaleAndIdentity();
        await PagedMarkersUseSelectedRepository();
        await PartialSubmissionIsDurable();
        await KnownRejectionAllowsNewConfirmation();
        await UnknownSubmissionIsNotResent();
        await ReconciliationUsesRecordedAccount();
        await DurableIntentAndMaintenance();
        await CommentReconciliationRequiresNewEvidence();
        await ReviewReconciliationVerifiesInlineComments();
        await ReconciliationPreservesRemainingSteps();
        await Cancellation();
        await MergeChecksAndMethod();
        await TriggerCiChecks();
        await CreatePrChecksAndAssignment();
        await DuplicateConfirmationAndEdits();
        await DuplicatePartialContinuation();
        await DuplicateUnknownReconciliation();
        await DuplicateCanonicalReadEvidence();
        await DuplicateTargetAndPermissions();
        InvalidDrafts();
        Console.WriteLine("PASS Web actions: origin-bound preparation, extension confirmation, fixed GitHub writes, SHA/CI gates, paged deduplication, durable intent, and read-only reconciliation (offline)");
    }

    private static void PreparationAndIsolation()
    {
        using var fixture = new Fixture();
        var draft = Draft("comment", "issue");
        var prepared = fixture.Prepare(draft);
        Check(Text(prepared, "status") == "prepared", "Preparing a web draft must await extension confirmation.");
        Check(fixture.SessionCount == 0 && fixture.Api.Reads.Count == 0 && fixture.Api.Writes.Count == 0, "Preparing a draft must not inspect credentials or access GitHub.");
        // A browser reconnect can serialize object fields in another order.
        var reordered = new JsonObject();
        foreach (var pair in draft.Reverse()) reordered[pair.Key] = pair.Value?.DeepClone();
        Check(Text(fixture.Prepare(reordered), "operationId") == Text(prepared, "operationId"), "An unchanged request must reuse the original operation regardless of JSON key order.");
        var changed = draft.DeepClone().AsObject();
        changed["body"] = "An edited comment requires another request ID.";
        Throws("REQUEST_CONFLICT", () => fixture.Prepare(changed));
        Throws("REQUEST_CONFLICT", () => fixture.Service.Prepare(new JsonObject { ["draft"] = draft.DeepClone() }, "http://localhost:8080"));
        Throws("OPERATION_NOT_FOUND", () => fixture.Service.Get(Id(prepared), "http://localhost:8080"));
        Check(Text(fixture.Service.Get(Id(prepared), Protocol.ProductionOrigin), "status") == "prepared", "The original origin must be able to read its prepared operation.");
        Throws("OPERATION_NOT_FOUND", () => fixture.Service.Get(new JsonObject { ["operationId"] = Guid.NewGuid().ToString("D") }, Protocol.ProductionOrigin));
        Throws("ORIGIN_DENIED", () => fixture.Service.Prepare(new JsonObject { ["draft"] = Draft("comment", "issue") }, ""));
        Check(fixture.SessionCount == 0, "Reading prepared status and rejecting conflicting requests must remain offline.");
    }

    private static async Task PreviewAndFixedApproval()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Prepare(Draft("approve"));
        var preview = await fixture.Service.PreviewAsync(Id(prepared));
        Check(Text(preview, "account") == "reviewer" && preview["canSubmit"]?.GetValue<bool>() == true, "Preview must show the account that will execute the operation and its current capability.");
        Check(Text(preview, "headSha") == Sha && fixture.Api.Writes.Count == 0, "Preview must read the live PR revision without publishing anything.");
        var result = await fixture.Submit(prepared);
        Check(Text(result, "status") == "succeeded", "A confirmed approval of the current PR revision must succeed.");
        var write = fixture.Api.Writes.Single();
        Check(write.Method == "POST" && write.Path == "/repos/microsoft/powertoys/pulls/7/reviews", "An approval must use the fixed target review endpoint.");
        Check(Text(write.Body, "event") == "APPROVE" && Text(write.Body, "commit_id") == Sha, "Approval must bind GitHub's review to the prepared 40-character SHA.");
        Check(result["urls"] is JsonArray { Count: 1 }, "A successful write must expose its GitHub result URL.");
        await fixture.Submit(prepared);
        Check(fixture.Api.Writes.Count == 1, "Repeated confirmation of a terminal operation must never write again.");
    }

    private static async Task WebsiteApprovalUsesConfirmedP0Policy()
    {
        using var known = new Fixture();
        SaveKnownFinding(known, "P0");
        var blocked = known.Prepare(Draft("approve"));
        Check(known.SessionCount == 0, "Website approval preparation remains an offline immutable draft even when current findings need confirmation.");
        var preview = await known.Service.PreviewAsync(Id(blocked));
        Check(preview["canSubmit"]?.GetValue<bool>() == false && preview["blockers"]!.AsArray().OfType<JsonObject>().Any(error => Text(error, "code") == "CONFIRMED_P0"),
            "Website-origin approval preview must use the same confirmed original-revision P0 rule as task approval.");
        var denied = await known.Submit(blocked);
        Check(Error(denied) == "CONFIRMED_P0" && known.Api.Writes.Count == 0, "An external-origin draft cannot bypass a P0 stored for its immutable PR and SHA.");

        using var afterPreview = new Fixture();
        var prepared = afterPreview.Prepare(Draft("approve"));
        Check((await afterPreview.Service.PreviewAsync(Id(prepared)))["canSubmit"]?.GetValue<bool>() == true, "Approval starts available when no confirmed P0 is known.");
        SaveKnownFinding(afterPreview, "P0");
        var changed = await afterPreview.Submit(prepared);
        Check(Error(changed) == "CONFIRMED_P0" && afterPreview.Api.Writes.Count == 0, "A P0 report arriving after preview must block the confirmation without posting a review.");

        using var duringRead = new Fixture();
        var raced = duringRead.Prepare(Draft("approve"));
        duringRead.Api.BeforeTargetRead = count => { if (count == 3) SaveKnownFinding(duringRead, "P0"); };
        var latest = await duringRead.Submit(raced);
        Check(duringRead.Api.TargetReadCount == 3 && Error(latest) == "CONFIRMED_P0" && duringRead.Api.Writes.Count == 0,
            "A P0 arriving inside the last GitHub target GET must be checked after that GET and before saving or sending approval intent.");

        foreach (var scenario in new[] { "P1", "other-sha", "other-pr" })
        {
            using var allowed = new Fixture();
            SaveKnownFinding(allowed, scenario == "P1" ? "P1" : "P0", scenario == "other-sha" ? OtherSha : Sha, scenario == "other-pr" ? 8 : 7);
            var action = allowed.Prepare(Draft("approve"));
            Check((await allowed.Service.PreviewAsync(Id(action)))["canSubmit"]?.GetValue<bool>() == true && Text(await allowed.Submit(action), "status") == "succeeded" && allowed.Api.Writes.Count == 1,
                "The website approval rule must not infer a current P0 from " + scenario + " evidence.");
        }

        using var unknown = new Fixture();
        unknown.Api.LoseResponseAt = 1;
        var uncertain = unknown.Prepare(Draft("approve"));
        Check(Text(await unknown.Submit(uncertain), "status") == "unknown", "The fixture must first create an unknown approval outcome.");
        SaveKnownFinding(unknown, "P0");
        Check(Text(await unknown.Service.ReconcileAsync(Id(uncertain)), "status") == "succeeded" && unknown.Api.Writes.Count == 1,
            "New P0 evidence must not block read-only reconciliation or rewrite a review that GitHub already accepted.");

        using var assigned = new Fixture();
        var withAssignment = Draft("approve"); withAssignment["assignSelf"] = true;
        assigned.Api.BeforeWrite = () => { if (assigned.Api.Writes.Count == 0) SaveKnownFinding(assigned, "P0"); };
        var completed = await assigned.Submit(assigned.Prepare(withAssignment));
        Check(Text(completed, "status") == "succeeded" && assigned.Api.Writes.Count == 2 && Strings(completed, "completedSteps").SequenceEqual(["review", "assign-self"]),
            "Once an approval write is confirmed, later P0 evidence must not impose approval policy on the independent remaining assignment step.");
    }

    private static void SaveKnownFinding(Fixture fixture, string priority, string revision = Sha, int number = 7)
    {
        var task = WorkflowV3Scenarios.TaskContext();
        task["target"]!["number"] = number; task["expectedHeadSha"] = revision;
        var model = WorkflowV3Scenarios.Model();
        model["assessment"]!["revisionSha"] = revision;
        model["reviewConclusion"]!["revisionSha"] = revision;
        model["reviewConclusion"]!["status"] = "changes-requested";
        model["review"]!["headSha"] = revision;
        model["findings"]!.AsArray().Add(WorkflowV3Scenarios.Finding("web-current-finding", priority));
        var result = WorkflowResult.FromModel(model.ToJsonString(), task, "succeeded", 0, null, null, expectedSchemaVersion: 3);
        Check(WorkflowResult.IsValidStoredV3(result) && (priority != "P0" || WorkflowResult.HasConfirmedP0(result, task)), "Website policy tests must save a valid version-bound v3 result, not a synthetic severity shortcut.");
        var runId = Guid.NewGuid().ToString("D");
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(runId), "task.json"), new JsonObject { ["runId"] = runId, ["task"] = task });
        fixture.Store.WriteJson(Path.Combine(fixture.Store.RunDirectory(runId), "result.json"), result);
    }

    private static async Task ReviewCoordinates()
    {
        using var fixture = new Fixture();
        var draft = ReviewDraft();
        var prepared = fixture.Prepare(draft);
        var result = await fixture.Submit(prepared);
        Check(Text(result, "status") == "succeeded", "A review with an in-diff multiline comment must submit.");
        var write = fixture.Api.Writes.Single();
        var comment = write.Body["comments"]![0]!.AsObject();
        Check(Text(write.Body, "event") == "REQUEST_CHANGES" && Text(write.Body, "commit_id") == Sha, "Request changes must preserve the review event and expected revision.");
        Check(Text(comment, "path") == "a.cs" && comment["line"]!.GetValue<int>() == 3 && comment["start_line"]!.GetValue<int>() == 2 && Text(comment, "side") == "RIGHT" && Text(comment, "start_side") == "RIGHT", "The host must translate the validated multiline location into GitHub REST coordinates.");
        using var invalid = new Fixture();
        var outside = ReviewDraft();
        outside["review"]!["comments"]![0]!["line"] = 30;
        var rejected = await invalid.Submit(invalid.Prepare(outside));
        Check(Text(rejected, "status") == "failed" && invalid.Api.Writes.Count == 0, "An out-of-diff selection must fail before any GitHub write.");
    }

    private static async Task StaleAndIdentity()
    {
        using var stale = new Fixture();
        var prepared = stale.Prepare(Draft("approve"));
        await stale.Service.PreviewAsync(Id(prepared));
        stale.Api.Head = OtherSha;
        var result = await stale.Submit(prepared);
        Check(Error(result) == "STALE_CONTEXT" && stale.Api.Writes.Count == 0, "A HEAD change after preview must prevent the confirmed write.");

        using var identity = new Fixture();
        var identityDraft = identity.Prepare(Draft("comment", "issue"));
        var mismatch = await identity.Service.SubmitAsync(new JsonObject { ["operationId"] = Text(identityDraft, "operationId"), ["expectedAccount"] = "another-account" });
        Check(Error(mismatch) == "GITHUB_IDENTITY_MISMATCH" && identity.Api.Writes.Count == 0, "A changed confirmation identity must prevent all GitHub writes.");

        using var racing = new Fixture();
        racing.Api.ChangeHeadAfterCollectionRead = true;
        var racingDraft = Draft("approve");
        racingDraft["body"] = "Reviewed.\n\n<!-- powertoys-pulse:pr:7:revision-1:approval -->";
        var raced = await racing.Submit(racing.Prepare(racingDraft));
        Check(Error(raced) == "STALE_CONTEXT" && racing.Api.Writes.Count == 0, "HEAD must be checked again after fetching discussion collections.");
    }

    private static async Task PagedMarkersUseSelectedRepository()
    {
        const string marker = "<!-- powertoys-pulse:issue:7:revision-1:comment-1 -->";
        using var duplicate = new Fixture();
        for (var index = 0; index < 100; index++) duplicate.Api.ForkComments.Add(Comment(index + 1, "Unrelated discussion " + index, Fork));
        duplicate.Api.ForkComments.Add(Comment(101, "Previously posted.\n\n" + marker, Fork));
        var draft = Draft("comment", "issue", Fork);
        draft["body"] = "Proposed discussion.\n\n" + marker;
        var result = await duplicate.Submit(duplicate.Prepare(draft));
        Check(Error(result) == "DUPLICATE_COMMENT" && duplicate.Api.Writes.Count == 0, "A marker on a later comments page must prevent duplicate publication.");
        Check(duplicate.Api.Reads.Contains("/repos/muyuanms/powertoys/issues/7/comments?per_page=100&page=2"), "Marker detection must read beyond the first full page in the selected fork.");
        Check(duplicate.Api.Reads.All(path => !path.StartsWith("/repos/microsoft/powertoys/", StringComparison.Ordinal)), "A fork comment must not accidentally check the upstream issue with the same number.");

        using var separate = new Fixture();
        separate.Api.UpstreamComments.Add(Comment(99, marker, Upstream));
        var fresh = Draft("comment", "issue", Fork);
        fresh["body"] = "This is the fork discussion.\n\n" + marker;
        var posted = await separate.Submit(separate.Prepare(fresh));
        Check(Text(posted, "status") == "succeeded" && separate.Api.Writes.Single().Path == "/repos/muyuanms/powertoys/issues/7/comments", "A marker in an unrelated upstream conversation must not suppress the selected fork comment.");
    }

    private static async Task PartialSubmissionIsDurable()
    {
        using var fixture = new Fixture();
        var draft = ReviewDraft();
        draft["review"]!["generalComments"] = new JsonArray(new JsonObject { ["body"] = "First general note." }, new JsonObject { ["body"] = "Second general note." });
        fixture.Api.FailWriteAt = 3;
        var prepared = fixture.Prepare(draft);
        var result = await fixture.Submit(prepared);
        Check(Text(result, "status") == "partial" && Error(result).Length > 0, "A later rejected comment must preserve a partially completed review operation.");
        Check(Strings(result, "completedSteps").SequenceEqual(["review", "general-comment-1"]) && Strings(result, "remainingSteps").SequenceEqual(["general-comment-2"]), "Partial results must identify exactly which independent writes completed and remain.");
        Check(fixture.Api.Writes.Count == 3 && Strings(result, "urls").Count == 2, "Partial results must preserve URLs for successful steps without claiming the rejected step succeeded.");
        fixture.Api.FailWriteAt = null;
        await fixture.Submit(prepared);
        var restarted = fixture.NewService();
        var persisted = restarted.Get(Id(prepared), Protocol.ProductionOrigin);
        var repeated = await restarted.SubmitAsync(Confirm(prepared));
        Check(Text(persisted, "status") == "partial" && Text(repeated, "status") == "partial" && fixture.Api.Writes.Count == 3, "Neither repeated clicks nor a new Host process may resend completed or failed steps automatically.");
    }

    private static async Task UnknownSubmissionIsNotResent()
    {
        using var fixture = new Fixture();
        fixture.Api.LoseResponseAt = 1;
        var prepared = fixture.Prepare(Draft("comment", "issue"));
        var result = await fixture.Submit(prepared);
        Check(Text(result, "status") == "unknown" && fixture.Api.Writes.Count == 1, "A lost response after sending a write must remain unknown.");
        fixture.Api.LoseResponseAt = null;
        var restarted = fixture.NewService();
        var again = await restarted.SubmitAsync(Confirm(prepared));
        Check(Text(again, "status") == "unknown" && fixture.Api.Writes.Count == 1, "An uncertain operation must remain durable and must not be retried as a fresh write.");
    }

    private static async Task KnownRejectionAllowsNewConfirmation()
    {
        using var fixture = new Fixture();
        fixture.Api.FailWriteAt = 1;
        var prepared = fixture.Prepare(Draft("comment", "issue"));
        var rejected = await fixture.Submit(prepared);
        var record = fixture.Store.ReadJson(Path.Combine(fixture.Store.Root, "web-actions", "operations", Text(prepared, "operationId") + ".json"))!;
        Check(Text(rejected, "status") == "failed" && Text(record, "activeStep").Length == 0 && record["completedSteps"] is JsonArray { Count: 0 },
            "An explicit GitHub rejection must clear uncertain-write intent so a deliberate new confirmation is possible.");
        fixture.Api.FailWriteAt = null;
        await fixture.Submit(prepared);
        Check(fixture.Api.Writes.Count == 1, "Retransmitting the rejected operation itself must not send it again.");
        var retry = await fixture.Submit(fixture.Prepare(Draft("comment", "issue")));
        Check(Text(retry, "status") == "succeeded" && fixture.Api.Writes.Count == 2, "A new confirmation may submit after the previous attempt was conclusively rejected.");
    }

    private static async Task Cancellation()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Prepare(Draft("comment", "issue"));
        Check(Text(fixture.Service.Cancel(Id(prepared)), "status") == "cancelled", "A pending extension confirmation must be cancellable.");
        Check(Text(await fixture.Submit(prepared), "status") == "cancelled" && fixture.Api.Writes.Count == 0, "A cancelled draft must not be revived by a delayed confirmation.");
        await ThrowsAsync("OPERATION_NOT_FOUND", () => fixture.Service.PreviewAsync(new JsonObject { ["operationId"] = Guid.NewGuid().ToString("D") }));
        await ThrowsAsync("INVALID_REQUEST", () => fixture.Service.SubmitAsync(new JsonObject { ["operationId"] = "../escape", ["expectedAccount"] = "reviewer" }));
    }

    private static async Task ReconciliationUsesRecordedAccount()
    {
        using var fixture = new Fixture();
        fixture.Api.LoseResponseAt = 1;
        var prepared = fixture.Prepare(Draft("comment", "issue"));
        var submitted = await fixture.Submit(prepared);
        Check(Text(submitted, "status") == "unknown" && Text(submitted, "account") == "reviewer", "An uncertain write must retain its actual submission account.");
        fixture.Store.WriteJson(Path.Combine(fixture.Store.Root, "config.json"), new JsonObject { ["githubAccount"] = "new-selected-account" });
        // The fixture session factory requires reviewer: recovery must override the changed default with the original account.
        var recovered = await fixture.NewService().ReconcileAsync(Id(prepared));
        Check(Text(recovered, "status") == "succeeded" && Text(recovered, "account") == "reviewer" && fixture.Api.Writes.Count == 1,
            "Changing Settings must not change the identity used to reconcile an already submitted write or cause another write.");
    }

    private static async Task DurableIntentAndMaintenance()
    {
        using var fixture = new Fixture();
        Check(!WebActions.HasActive(fixture.Store), "An empty Host must not block maintenance.");
        var prepared = fixture.Prepare(Draft("comment", "issue"));
        Check(!WebActions.HasActive(fixture.Store), "Waiting for user confirmation must not count as an active GitHub mutation.");
        var observed = false;
        fixture.Api.BeforeWrite = () =>
        {
            observed = true;
            var record = fixture.Store.ReadJson(Path.Combine(fixture.Store.Root, "web-actions", "operations", Text(prepared, "operationId") + ".json"));
            Check(record is not null && Text(record, "status") == "submitting", "The submission intent must be on disk before a GitHub write starts.");
            Check(WebActions.HasActive(fixture.Store), "Maintenance must see the durable in-flight GitHub submission.");
        };
        var result = await fixture.Submit(prepared);
        Check(observed && Text(result, "status") == "succeeded" && !WebActions.HasActive(fixture.Store), "A completed write must release the maintenance gate.");
        var sessions = fixture.SessionCount;
        var terminalPreview = await fixture.Service.PreviewAsync(Id(prepared));
        Check(Text(terminalPreview, "account") == "reviewer" && terminalPreview["canSubmit"]?.GetValue<bool>() == false, "Terminal confirmation details must preserve the executing account.");
        Check(fixture.SessionCount == sessions && fixture.Api.Writes.Count == 1, "Inspecting a terminal preview must not reopen a session or mutate GitHub.");
    }

    private static async Task CommentReconciliationRequiresNewEvidence()
    {
        using var fixture = new Fixture();
        var draft = Draft("comment", "issue");
        fixture.Api.UpstreamComments.Add(Comment(10, Text(draft, "body")));
        fixture.Api.LoseResponseAt = 1;
        var prepared = fixture.Prepare(draft);
        Check(Text(await fixture.Submit(prepared), "status") == "unknown", "The fixture must lose the response after applying the comment.");
        Check(!WebActions.HasActive(fixture.Store), "An unknown terminal outcome must not look like an active network write to maintenance.");
        var reconciled = await fixture.NewService().ReconcileAsync(Id(prepared));
        Check(Text(reconciled, "status") == "succeeded" && Strings(reconciled, "completedSteps").SequenceEqual(["comment"]), "A unique matching comment outside the saved baseline must confirm the unknown write.");
        Check(Strings(reconciled, "urls").Single().EndsWith("#issuecomment-101", StringComparison.Ordinal), "Reconciliation must select the newly posted comment rather than an identical pre-existing comment.");
        await fixture.Service.ReconcileAsync(Id(prepared));
        await fixture.Submit(prepared);
        Check(fixture.Api.Writes.Count == 1, "Repeated reconciliation and confirmation must remain read-only after success.");

        using var unapplied = new Fixture();
        var missingDraft = Draft("comment", "issue");
        unapplied.Api.UpstreamComments.Add(Comment(10, Text(missingDraft, "body")));
        unapplied.Api.LoseResponseAt = 1;
        unapplied.Api.ApplyWrites = false;
        var missing = unapplied.Prepare(missingDraft);
        await unapplied.Submit(missing);
        var otherAccount = Comment(11, Text(missingDraft, "body"));
        otherAccount["user"]!["login"] = "another-account";
        unapplied.Api.UpstreamComments.Add(otherAccount);
        unapplied.Api.UpstreamComments.Add(Comment(12, "Different text from the same account."));
        var unconfirmed = await unapplied.Service.ReconcileAsync(Id(missing));
        Check(Text(unconfirmed, "status") == "unknown" && Strings(unconfirmed, "completedSteps").Count == 0, "Baseline comments, another account, and different text must not be accepted as evidence of a lost write.");
        unapplied.Api.LoseResponseAt = null;
        unapplied.Api.ApplyWrites = true;
        await unapplied.Submit(missing);
        Check(unapplied.Api.Writes.Count == 1, "No evidence must leave the operation unknown without automatically resending it.");
    }

    private static async Task ReviewReconciliationVerifiesInlineComments()
    {
        using var fixture = new Fixture();
        fixture.Api.LoseResponseAt = 1;
        var prepared = fixture.Prepare(ReviewDraft());
        Check(Text(await fixture.Submit(prepared), "status") == "unknown", "The inline review fixture must lose its submission response.");
        var posted = fixture.Api.PostedInlineComments[101];
        var original = posted[0]!["body"]!.DeepClone();
        posted[0]!["body"] = "Different inline finding.";
        var mismatch = await fixture.Service.ReconcileAsync(Id(prepared));
        Check(Text(mismatch, "status") == "unknown", "A matching review body/SHA/event must not confirm different inline comments.");
        posted[0]!["body"] = original;
        var reconciled = await fixture.Service.ReconcileAsync(Id(prepared));
        Check(Text(reconciled, "status") == "succeeded" && fixture.Api.Writes.Count == 1, "Matching inline comment bodies and saved locations must confirm the original review without posting again.");
        Check(fixture.Api.Reads.Any(path => path.StartsWith("/repos/microsoft/powertoys/pulls/7/reviews/101/comments", StringComparison.Ordinal)), "Review reconciliation must inspect the specific candidate review's inline comments.");
    }

    private static async Task ReconciliationPreservesRemainingSteps()
    {
        using var fixture = new Fixture();
        var draft = ReviewDraft();
        draft["review"]!["generalComments"] = new JsonArray(
            new JsonObject { ["body"] = "First general note." },
            new JsonObject { ["body"] = "Second general note." },
            new JsonObject { ["body"] = "Third general note." });
        fixture.Api.LoseResponseAt = 3;
        var prepared = fixture.Prepare(draft);
        var unknown = await fixture.Submit(prepared);
        Check(Text(unknown, "status") == "unknown" && Strings(unknown, "completedSteps").SequenceEqual(["review", "general-comment-1"]), "Losing a later response must retain confirmed earlier steps.");
        var reconciled = await fixture.NewService().ReconcileAsync(Id(prepared));
        Check(Text(reconciled, "status") == "partial" && Strings(reconciled, "completedSteps").SequenceEqual(["review", "general-comment-1", "general-comment-2"]), "Reconciliation must add only the newly confirmed active step to the completed steps.");
        Check(Strings(reconciled, "remainingSteps").SequenceEqual(["general-comment-3"]) && fixture.Api.Writes.Count == 3, "Reconciliation must expose the unstarted remainder without executing it.");
        await fixture.Service.ReconcileAsync(Id(prepared));
        await fixture.Submit(prepared);
        Check(fixture.Api.Writes.Count == 3 && !WebActions.HasActive(fixture.Store), "A reconciled partial operation must remain terminal and safe for maintenance.");
    }

    private static async Task MergeChecksAndMethod()
    {
        using var ready = new Fixture();
        ready.Api.Reviews.Add(Review(10, "APPROVED", "maintainer", Sha));
        var merged = await ready.Submit(ready.Prepare(Draft("merge-pr")));
        Check(Text(merged, "status") == "succeeded", "An approved PR with passing checks and current clean merge state must merge.");
        var write = ready.Api.Writes.Single();
        Check(write.Method == "PUT" && write.Path == "/repos/microsoft/powertoys/pulls/7/merge" && Text(write.Body, "sha") == Sha && Text(write.Body, "merge_method") == "squash", "Merge must use GitHub's fixed PUT endpoint with the confirmed SHA and squash method.");

        using var unapproved = new Fixture();
        var missing = await unapproved.Submit(unapproved.Prepare(Draft("merge-pr")));
        Check(Text(missing, "status") == "succeeded" && unapproved.Api.Writes.Count == 1, "A human merge must use GitHub's actual mergeability instead of inventing a current-head approval requirement.");

        using var withdrawn = new Fixture();
        withdrawn.Api.Reviews.Add(Review(10, "APPROVED", "maintainer", Sha));
        withdrawn.Api.Reviews.Add(Review(11, "CHANGES_REQUESTED", "maintainer", Sha));
        var revoked = await withdrawn.Submit(withdrawn.Prepare(Draft("merge-pr")));
        Check(Text(revoked, "status") == "succeeded" && withdrawn.Api.Writes.Count == 1, "Human merge availability must not impose Pulse review policy when GitHub still reports clean mergeability.");

        using var pending = new Fixture();
        pending.Api.Reviews.Add(Review(10, "APPROVED", "maintainer", Sha));
        pending.Api.CheckState = "in_progress";
        var waiting = await pending.Submit(pending.Prepare(Draft("merge-pr")));
        Check(Text(waiting, "status") == "succeeded" && pending.Api.Writes.Count == 1 && pending.Api.Reads.All(path => !path.Contains("check-runs", StringComparison.Ordinal)), "Human merge must not add an all-checks-passed requirement or query unnecessary CI evidence.");
        using var blocked = new Fixture();
        blocked.Api.MergeableState = "blocked";
        var unavailable = await blocked.Submit(blocked.Prepare(Draft("merge-pr")));
        Check(Error(unavailable) == "MERGE_NOT_READY" && blocked.Api.Writes.Count == 0, "GitHub's actual blocked merge state must still prevent submission.");
        foreach (var state in new[] { "unstable", "has_hooks" })
        {
            using var allowed = new Fixture(); allowed.Api.MergeableState = state; allowed.Api.CheckConclusion = "failure";
            var result = await allowed.Submit(allowed.Prepare(Draft("merge-pr")));
            Check(Text(result, "status") == "succeeded" && allowed.Api.Writes.Count == 1, "GitHub mergeable " + state + " status may proceed to the user's fixed-SHA merge confirmation without an invented all-CI-passed requirement.");
        }
        foreach (var state in new[] { "dirty", "draft", "unknown" })
        {
            using var denied = new Fixture(); denied.Api.MergeableState = state;
            var result = await denied.Submit(denied.Prepare(Draft("merge-pr")));
            Check(Error(result) == "MERGE_NOT_READY" && denied.Api.Writes.Count == 0, "GitHub " + state + " state is not current merge readiness.");
        }
    }

    private static async Task TriggerCiChecks()
    {
        using var passed = new Fixture();
        var unnecessary = await passed.Submit(passed.Prepare(Draft("trigger-ci")));
        Check(Error(unnecessary) == "CI_ALREADY_PASSED" && passed.Api.Writes.Count == 0, "Passing CI must not receive another trigger comment.");
        using var running = new Fixture();
        running.Api.CheckState = "in_progress";
        var duplicate = await running.Submit(running.Prepare(Draft("trigger-ci")));
        Check(Error(duplicate) == "CI_ALREADY_RUNNING" && running.Api.Writes.Count == 0, "An active CI run must not be triggered again.");
        using var failed = new Fixture();
        failed.Api.CheckConclusion = "failure";
        var result = await failed.Submit(failed.Prepare(Draft("trigger-ci")));
        Check(Text(result, "status") == "succeeded" && Text(failed.Api.Writes.Single().Body, "body") == "/azp run", "The CI action must publish only the host's fixed /azp run command.");
        var sameFailure = failed.Prepare(Draft("trigger-ci"));
        var waitingPreview = await failed.Service.PreviewAsync(Id(sameFailure));
        Check(waitingPreview["canSubmit"]?.GetValue<bool>() == false && waitingPreview["blockers"]!.AsArray().OfType<JsonObject>().Any(error => Text(error, "code") == "CI_RETRY_NOT_READY"),
            "A preview must explain that the previous trigger has not produced a new failed run yet.");
        var unchanged = await failed.Submit(sameFailure);
        Check(Error(unchanged) == "CI_RETRY_NOT_READY" && failed.Api.Writes.Count == 1, "Repeated new requests against unchanged failed checks must not duplicate the trigger before CI updates.");
        failed.Api.CheckId = 2;
        var explicitRetry = failed.Prepare(Draft("trigger-ci"));
        var retryPreview = await failed.Service.PreviewAsync(Id(explicitRetry));
        Check(retryPreview["canSubmit"]?.GetValue<bool>() == true, "A later failed CI run on the same SHA must allow a fresh explicit confirmation.");
        var retried = await failed.Submit(explicitRetry);
        Check(Text(retried, "status") == "succeeded" && failed.Api.Writes.Count == 2, "Same-SHA CI retries must work after a newly failed run rather than being disabled permanently.");
        await failed.Submit(explicitRetry);
        Check(failed.Api.Writes.Count == 2, "Transport retries of the same CI confirmation must remain idempotent.");
        using var draftPr = new Fixture(); draftPr.Api.IsDraft = true; draftPr.Api.CheckConclusion = "failure";
        var draftResult = await draftPr.Submit(draftPr.Prepare(Draft("trigger-ci")));
        Check(Text(draftResult, "status") == "succeeded" && draftPr.Api.Writes.Count == 1, "A draft PR may request CI through the same fixed command when its actual CI state permits it.");
    }

    private static async Task CreatePrChecksAndAssignment()
    {
        using var fixture = new Fixture();
        var draft = CreatePrDraft();
        draft["assignSelf"] = true;
        var prepared = fixture.Prepare(draft);
        var preview = await fixture.Service.PreviewAsync(Id(prepared));
        Check(preview["canSubmit"]?.GetValue<bool>() == true && Text(preview, "sourceHeadSha") == Sha && fixture.Api.Writes.Count == 0,
            "Create-PR confirmation must display and pin the existing remote source commit without publishing local work.");
        var result = await fixture.Submit(prepared);
        Check(Text(result, "status") == "succeeded", "A validated fork branch must support opening its upstream PR and assigning the source issue.");
        Check(fixture.Api.Reads.Contains("/repos/muyuanms/powertoys/git/ref/heads/feature/work") && fixture.Api.Reads.Contains("/repos/microsoft/powertoys/git/ref/heads/main"), "The head and base refs must both be verified before PR creation.");
        var create = fixture.Api.Writes[0];
        Check(create.Method == "POST" && create.Path == "/repos/microsoft/powertoys/pulls" && Text(create.Body, "head") == "muyuanms:feature/work" && Text(create.Body, "base") == "main", "PR creation must use the validated structured head and base fields on the fixed upstream endpoint.");
        var assign = fixture.Api.Writes[1];
        Check(assign.Method == "POST" && assign.Path == "/repos/microsoft/powertoys/issues/7/assignees" && assign.Body["assignees"]![0]!.GetValue<string>() == "reviewer", "Self-assignment must target the source issue using the locally selected account.");
        Check(!create.Body.ContainsKey("sourceHeadSha") && !create.Body.ContainsKey("proposalId"), "Host source-commit and proposal metadata must never become unsupported GitHub PR-create fields.");

        using var preMoved = new Fixture();
        var assessedDraft = CreatePrDraft(); assessedDraft["pullRequest"]!["sourceHeadSha"] = Sha;
        var assessed = preMoved.Prepare(assessedDraft);
        preMoved.Api.SourceHeadSha = OtherSha;
        var firstPreview = await preMoved.Service.PreviewAsync(Id(assessed));
        Check(firstPreview["canSubmit"]?.GetValue<bool>() == false && Text(firstPreview, "sourceHeadSha") == Sha && firstPreview["blockers"]!.AsArray().OfType<JsonObject>().Any(error => Text(error, "code") == "STALE_SOURCE_BRANCH") && preMoved.Api.Writes.Count == 0,
            "A source branch moved before the first preview must not replace the commit the model actually verified.");

        using var pinned = new Fixture();
        var pinnedDraft = CreatePrDraft(); pinnedDraft["pullRequest"]!["sourceHeadSha"] = Sha;
        var pinnedPrepared = pinned.Prepare(pinnedDraft);
        await pinned.Service.PreviewAsync(Id(pinnedPrepared));
        var pinnedSubmitted = await pinned.Submit(pinnedPrepared);
        Check(Text(pinnedSubmitted, "status") == "succeeded" && !pinned.Api.Writes.Single().Body.ContainsKey("sourceHeadSha"),
            "The reviewed source SHA is a local confirmation constraint, never an unsupported GitHub create-PR field.");

        using var pinnedNoPreview = new Fixture();
        var pinnedUnreviewed = CreatePrDraft(); pinnedUnreviewed["pullRequest"]!["sourceHeadSha"] = Sha;
        var notConfirmed = await pinnedNoPreview.Submit(pinnedNoPreview.Prepare(pinnedUnreviewed));
        Check(Error(notConfirmed) == "PREVIEW_REQUIRED" && pinnedNoPreview.Api.Writes.Count == 0, "Recording a model source SHA must not bypass the user's first source-commit preview.");

        using var moved = new Fixture();
        var moving = moved.Prepare(CreatePrDraft());
        await moved.Service.PreviewAsync(Id(moving));
        moved.Api.SourceHeadSha = OtherSha;
        var stale = await moved.Submit(moving);
        Check(Error(stale) == "STALE_SOURCE_BRANCH" && moved.Api.Writes.Count == 0, "A moving remote source branch must not silently change the commit the user confirmed.");

        using var noPreview = new Fixture();
        var unreviewed = await noPreview.Submit(noPreview.Prepare(CreatePrDraft()));
        Check(Error(unreviewed) == "PREVIEW_REQUIRED" && noPreview.Api.Writes.Count == 0, "PR creation must first disclose its source commit in the confirmation preview.");

        using var raced = new Fixture();
        var racing = raced.Prepare(CreatePrDraft());
        await raced.Service.PreviewAsync(Id(racing));
        raced.Api.CreatedPrHeadSha = OtherSha;
        var changedDuringPost = await raced.Submit(racing);
        Check(Text(changedDuringPost, "status") == "unknown" && Strings(changedDuringPost, "urls").Single().EndsWith("/pull/71", StringComparison.Ordinal),
            "If the branch moves during GitHub's branch-based PR creation, preserve the created PR link and do not claim the confirmed source commit was submitted.");
        await raced.Submit(racing);
        Check(raced.Api.Writes.Count == 1, "A source-commit race after PR creation must never resend the POST.");

        using var absent = new Fixture();
        absent.Api.HeadBranchExists = false;
        var unavailable = await absent.Submit(absent.Prepare(CreatePrDraft()));
        Check(Text(unavailable, "status") == "failed" && Error(unavailable) == "SOURCE_BRANCH_UNPUBLISHED" && absent.Api.Writes.Count == 0,
            "An unpublished source branch must produce a clear blocker without committing or pushing local files.");
    }

    private static async Task DuplicateConfirmationAndEdits()
    {
        using var fixture = new Fixture();
        var draft = DuplicateDraft();
        var prepared = fixture.Prepare(draft);
        var preview = await fixture.Service.PreviewAsync(Id(prepared));
        Check(preview["canSubmit"]?.GetValue<bool>() == true && Text(preview["duplicateOf"]!.AsObject(), "url") == $"https://github.com/{Upstream}/issues/8" && fixture.Api.Writes.Count == 0,
            "Duplicate preview must verify and disclose the fixed original issue without publishing anything.");
        Check(Text(preview, "commentBody") == Text(draft, "body") + Text(preview, "duplicateSuffix") && Text(preview, "duplicateSuffix").Contains("Duplicate of https://github.com/" + Upstream + "/issues/8", StringComparison.Ordinal),
            "The preview must disclose the exact authoritative reference that will be appended to editable text.");
        var confirmation = Confirm(prepared);
        confirmation["body"] = "  Edited explanation.\n\n```text\nkeep this formatting\n```\n";
        var submitted = await fixture.Service.SubmitAsync(confirmation);
        Check(Text(submitted, "status") == "succeeded" && Strings(submitted, "completedSteps").SequenceEqual(["duplicate-comment", "close-issue"]), "A duplicate action must record both ordered effects.");
        Check(fixture.Api.Writes.Count == 2 && fixture.Api.Writes[0].Path == "/repos/microsoft/powertoys/issues/7/comments" && Text(fixture.Api.Writes[0].Body, "body") == Text(confirmation, "body") + Text(preview, "duplicateSuffix"),
            "The comment must preserve the user's Markdown and append the canonical reference independently.");
        var close = fixture.Api.Writes[1];
        Check(close.Method == "PATCH" && close.Path == "/repos/microsoft/powertoys/issues/7" && Text(close.Body, "state") == "closed" && Text(close.Body, "state_reason") == "duplicate" && close.Body["duplicate_issue_id"]?.GetValue<long>() == fixture.Api.OriginalIssueId,
            "Duplicate closure must use a fixed endpoint and the original issue database ID obtained from GitHub, never a client-provided ID.");
        await fixture.Service.SubmitAsync(confirmation);
        Check(fixture.Api.Writes.Count == 2, "Repeated confirmation cannot duplicate the comment or close.");
        var changed = confirmation.DeepClone().AsObject(); changed["body"] = "A later replacement";
        await ThrowsAsync("REQUEST_CONFLICT", () => fixture.Service.SubmitAsync(changed));
        var terminalPreview = await fixture.Service.PreviewAsync(Id(prepared));
        Check(Text(terminalPreview["draft"]!.AsObject(), "body") == Text(confirmation, "body") && terminalPreview["bodyEditable"]?.GetValue<bool>() == false,
            "A frozen confirmation must show the actual edited explanation after a reload.");
        fixture.Api.IssueState = "open";
        var fresh = await fixture.Submit(fixture.Prepare(DuplicateDraft()));
        Check(Error(fresh) == "DUPLICATE_COMMENT" && fixture.Api.Writes.Count == 2, "A separate request for an already-posted canonical association must not duplicate that comment.");
    }

    private static async Task DuplicatePartialContinuation()
    {
        using var fixture = new Fixture();
        fixture.Api.FailWriteAt = 2;
        var prepared = fixture.Prepare(DuplicateDraft());
        var partial = await fixture.Submit(prepared);
        Check(Text(partial, "status") == "partial" && Strings(partial, "completedSteps").SequenceEqual(["duplicate-comment"]) && Strings(partial, "remainingSteps").SequenceEqual(["close-issue"]),
            "A rejected close must retain the successful association and identify only the remaining close step.");
        var preview = await fixture.Service.PreviewAsync(Id(prepared));
        Check(preview["canSubmit"]?.GetValue<bool>() == true && preview["resumeRequired"]?.GetValue<bool>() == true && fixture.Api.Writes.Count == 2,
            "A partial duplicate may preview an explicit continuation without reposting or blocking on its own marker.");
        await fixture.Submit(prepared);
        Check(fixture.Api.Writes.Count == 2, "A replay of the original confirmation must not automatically continue a partial write.");
        var continuation = Confirm(prepared); continuation["resume"] = true;
        var finished = await fixture.NewService().SubmitAsync(continuation);
        Check(Text(finished, "status") == "succeeded" && fixture.Api.Writes.Count == 3 && fixture.Api.Writes.Count(write => write.Method == "POST") == 1 &&
            finished["stepResults"]?["duplicate-comment"]?["account"]?.GetValue<string>() == "reviewer",
            "Explicit continuation after Host restart must perform only the uncompleted close and preserve the first step's account/history.");
    }

    private static async Task DuplicateUnknownReconciliation()
    {
        using var fixture = new Fixture();
        fixture.Api.LoseResponseAt = 1;
        var prepared = fixture.Prepare(DuplicateDraft());
        Check(Text(await fixture.Submit(prepared), "status") == "unknown", "Lost association response must remain unknown.");
        var changed = Confirm(prepared); changed["body"] = "Different text after a lost response.";
        await ThrowsAsync("REQUEST_CONFLICT", () => fixture.Service.SubmitAsync(changed));
        var resolved = await fixture.Service.ReconcileAsync(Id(prepared));
        Check(Text(resolved, "status") == "partial" && fixture.Api.Writes.Count == 1 && Strings(resolved, "completedSteps").SequenceEqual(["duplicate-comment"]),
            "Read-only reconciliation must confirm the exact association without automatically closing or reposting.");
        var continuation = Confirm(prepared); continuation["resume"] = true;
        var finished = await fixture.Service.SubmitAsync(continuation);
        Check(Text(finished, "status") == "succeeded" && fixture.Api.Writes.Count == 2 && fixture.Api.Writes[1].Method == "PATCH", "Confirmed unknown association must continue with one close only.");

        using var absent = new Fixture();
        absent.Api.LoseResponseAt = 1; absent.Api.ApplyWrites = false;
        var missing = absent.Prepare(DuplicateDraft());
        await absent.Submit(missing);
        Check(Text(await absent.Service.ReconcileAsync(Id(missing)), "status") == "unknown", "No matching comment is not proof that the unknown write was rejected.");
        var unsafeResume = Confirm(missing); unsafeResume["resume"] = true;
        await absent.Service.SubmitAsync(unsafeResume);
        Check(absent.Api.Writes.Count == 1, "Unknown association must never be retried or skipped by requesting continuation.");
    }

    private static async Task DuplicateCanonicalReadEvidence()
    {
        using var fixture = new Fixture();
        fixture.Api.LoseResponseAt = 2;
        var prepared = fixture.Prepare(DuplicateDraft());
        Check(Text(await fixture.Submit(prepared), "status") == "unknown" && fixture.Api.IssueState == "closed", "Lost close response must not be declared successful solely because the preceding comment exists.");
        var confirmed = await fixture.Service.ReconcileAsync(Id(prepared));
        Check(Text(confirmed, "status") == "succeeded" && fixture.Api.Writes.Count == 2 && fixture.Api.GraphqlReads == 1,
            "Current GitHub duplicateOf evidence for the pinned original must reconcile an uncertain close without new writes.");

        foreach (var evidence in new[] { "missing", "different", "ordinary-close" })
        {
            using var unavailable = new Fixture();
            unavailable.Api.LoseResponseAt = 2;
            var action = unavailable.Prepare(DuplicateDraft());
            await unavailable.Submit(action);
            if (evidence == "ordinary-close") unavailable.Api.IssueStateReason = "completed";
            else unavailable.Api.CanonicalEvidence = evidence;
            var unresolved = await unavailable.Service.ReconcileAsync(Id(action));
            Check(Text(unresolved, "status") == "unknown" && Error(unresolved) == "DUPLICATE_NOT_CONFIRMED" && unavailable.Api.Writes.Count == 2,
                "A closed issue with " + evidence + " canonical evidence must remain unknown and explain why duplicate closure is unconfirmed.");
        }
    }

    private static async Task DuplicateTargetAndPermissions()
    {
        using var invalid = new Fixture();
        var self = DuplicateDraft(); self["duplicateOf"]!["number"] = 7; self["duplicateOf"]!["url"] = $"https://github.com/{Upstream}/issues/7";
        Throws("INVALID_DUPLICATE", () => invalid.Prepare(self));
        var wrongUrl = DuplicateDraft(); wrongUrl["duplicateOf"]!["url"] = $"https://github.com/{Upstream}/issues/9";
        Throws("INVALID_DUPLICATE", () => invalid.Prepare(wrongUrl));
        var injectedId = DuplicateDraft(); injectedId["duplicateOf"]!["id"] = 9999;
        Throws("INVALID_REQUEST", () => invalid.Prepare(injectedId));
        var pr = DuplicateDraft(); pr["target"]!["type"] = "pr"; pr["expectedHeadSha"] = Sha;
        Throws("INVALID_TARGET", () => invalid.Prepare(pr));
        Check(invalid.SessionCount == 0, "Malformed duplicate targets and injected database IDs must be rejected offline.");

        using var wrongKind = new Fixture(); wrongKind.Api.OriginalIsPullRequest = true;
        var mismatch = await wrongKind.Submit(wrongKind.Prepare(DuplicateDraft()));
        Check(Error(mismatch) == "TARGET_MISMATCH" && wrongKind.Api.Writes.Count == 0, "The canonical target must be a real issue, not an issue endpoint representing a PR.");

        using var drift = new Fixture();
        var pinned = drift.Prepare(DuplicateDraft()); await drift.Service.PreviewAsync(Id(pinned));
        drift.Api.OriginalIssueId++;
        var changed = await drift.Submit(pinned);
        Check(Error(changed) == "DUPLICATE_TARGET_CHANGED" && drift.Api.Writes.Count == 0, "A previewed canonical issue identity cannot silently change before submission.");

        using var denied = new Fixture(); denied.Api.CanPush = false;
        var rejected = await denied.Submit(denied.Prepare(DuplicateDraft()));
        Check(Error(rejected) == "GITHUB_PERMISSION_REQUIRED" && denied.Api.Writes.Count == 0, "Duplicate closure must keep actual GitHub repository permission checks.");
    }

    private static JsonObject DuplicateDraft()
    {
        var draft = Draft("close-as-duplicate", "issue");
        draft["body"] = "This report describes the same trigger and failure as the original issue.";
        draft["duplicateOf"] = new JsonObject { ["repository"] = Upstream, ["number"] = 8, ["url"] = $"https://github.com/{Upstream}/issues/8" };
        return draft;
    }

    private static void InvalidDrafts()
    {
        using var fixture = new Fixture();
        var injected = Draft("comment", "issue");
        injected["endpoint"] = "https://example.invalid/steal";
        Throws("INVALID_REQUEST", () => fixture.Prepare(injected));
        var malformed = Draft("approve");
        malformed["expectedHeadSha"] = "not-a-sha";
        Throws("MISSING_HEAD_SHA", () => fixture.Prepare(malformed));
        var wrongTarget = Draft("approve", "issue");
        Throws("INVALID_TARGET", () => fixture.Prepare(wrongTarget));
        var traversal = CreatePrDraft();
        traversal["pullRequest"]!["head"] = "muyuanms:../main";
        Throws("INVALID_BRANCH", () => fixture.Prepare(traversal));
        var destination = Draft("comment", "issue", "attacker/other");
        Throws("INVALID_TARGET", () => fixture.Prepare(destination));
        Check(fixture.SessionCount == 0 && fixture.Api.Writes.Count == 0, "Malformed browser input must be rejected before accessing the selected GitHub session.");
    }

    private static JsonObject Draft(string kind, string targetType = "pr", string repository = Upstream)
    {
        var draft = new JsonObject
        {
            ["requestId"] = Guid.NewGuid().ToString("D"), ["actionId"] = "fixture-action", ["kind"] = kind,
            ["target"] = new JsonObject { ["repository"] = repository, ["type"] = targetType, ["number"] = 7 }
        };
        if (targetType == "pr") draft["expectedHeadSha"] = Sha;
        if (kind == "comment") draft["body"] = "A reviewed discussion comment.";
        return draft;
    }

    private static JsonObject ReviewDraft()
    {
        var draft = Draft("review");
        draft["body"] = "Please address the selected finding.";
        draft["review"] = new JsonObject
        {
            ["event"] = "REQUEST_CHANGES", ["comments"] = new JsonArray(new JsonObject
            {
                ["path"] = "a.cs", ["line"] = 3, ["startLine"] = 2, ["side"] = "RIGHT", ["startSide"] = "RIGHT", ["body"] = "Use a clear value."
            })
        };
        return draft;
    }

    private static JsonObject CreatePrDraft()
    {
        var draft = Draft("create-pr", "issue");
        draft["pullRequest"] = new JsonObject { ["head"] = "muyuanms:feature/work", ["base"] = "main", ["title"] = "Fix the source issue", ["body"] = "Resolves #7", ["draft"] = false };
        return draft;
    }

    private static JsonObject Id(JsonObject operation) => new() { ["operationId"] = Text(operation, "operationId") };
    private static JsonObject Confirm(JsonObject operation) => new() { ["operationId"] = Text(operation, "operationId"), ["expectedAccount"] = "reviewer" };
    private static string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
    private static string Error(JsonObject value) => value["error"]?["code"]?.GetValue<string>() ?? "";
    private static List<string> Strings(JsonObject value, string key) => value[key]!.AsArray().Select(item => item!.GetValue<string>()).ToList();
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

    private static JsonObject Comment(int id, string body, string repository = Upstream) => new()
    {
        ["id"] = id, ["body"] = body, ["user"] = new JsonObject { ["login"] = "reviewer" }, ["created_at"] = Protocol.Now(),
        ["html_url"] = $"https://github.com/{repository}/issues/7#issuecomment-{id}"
    };

    private static JsonObject Review(int id, string state, string account, string sha) => new()
    {
        ["id"] = id, ["body"] = "Reviewed.", ["state"] = state, ["commit_id"] = sha,
        ["submitted_at"] = DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddMinutes(id).ToString("O"),
        ["user"] = new JsonObject { ["login"] = account }, ["html_url"] = $"https://github.com/{Upstream}/pull/7#pullrequestreview-{id}"
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pulse-web-actions-" + Guid.NewGuid().ToString("N"));
        public Store Store { get; }
        public FakeGitHub Api { get; } = new();
        public WebActions Service { get; }
        public int SessionCount { get; private set; }
        public Fixture()
        {
            Store = new Store(root);
            Store.WriteJson(Path.Combine(Store.Root, "config.json"), new JsonObject { ["githubAccount"] = "reviewer" });
            Service = NewService();
        }
        public WebActions NewService() => new(Store, config =>
        {
            SessionCount++;
            Check(Text(config, "githubAccount") == "reviewer", "Web actions must use the locally saved GitHub account configuration.");
            return Task.FromResult(new GitHubSession(new HttpClient(Api, disposeHandler: false), "reviewer"));
        });
        public JsonObject Prepare(JsonObject draft) => Service.Prepare(new JsonObject { ["draft"] = draft.DeepClone() }, Protocol.ProductionOrigin);
        public Task<JsonObject> Submit(JsonObject operation) => Service.SubmitAsync(Confirm(operation));
        public void Dispose()
        {
            Api.Dispose();
            var target = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var name = Path.GetFileName(target);
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !name.StartsWith("pulse-web-actions-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe web-action fixture cleanup path.");
            Directory.Delete(target, recursive: true);
        }
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public string Head { get; set; } = Sha;
        public string CheckState { get; set; } = "completed";
        public int CheckId { get; set; } = 1;
        public string CheckConclusion { get; set; } = "success";
        public string MergeableState { get; set; } = "clean";
        public bool IsDraft { get; set; }
        public bool CanPush { get; set; } = true;
        public long OriginalIssueId { get; set; } = 8008;
        public bool OriginalIsPullRequest { get; set; }
        public string IssueState { get; set; } = "open";
        public string IssueStateReason { get; set; } = "";
        public string CanonicalEvidence { get; set; } = "exact";
        public int GraphqlReads { get; private set; }
        public bool HeadBranchExists { get; set; } = true;
        public string SourceHeadSha { get; set; } = Sha;
        public string CreatedPrHeadSha { get; set; } = Sha;
        public bool ChangeHeadAfterCollectionRead { get; set; }
        public int? FailWriteAt { get; set; }
        public int? LoseResponseAt { get; set; }
        public bool ApplyWrites { get; set; } = true;
        public Action? BeforeWrite { get; set; }
        public Action<int>? BeforeTargetRead { get; set; }
        public int TargetReadCount { get; private set; }
        public JsonArray UpstreamComments { get; } = [];
        public JsonArray ForkComments { get; } = [];
        public JsonArray Reviews { get; } = [];
        public Dictionary<int, JsonArray> PostedInlineComments { get; } = [];
        public List<string> Reads { get; } = [];
        public List<(string Method, string Path, JsonObject Body)> Writes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "api.github.com", "The fixture must never receive arbitrary or non-GitHub URLs.");
            var path = request.RequestUri.AbsolutePath;
            if (path == "/graphql" && request.Method == HttpMethod.Post)
            {
                GraphqlReads++;
                var input = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                Check(Text(input, "query").StartsWith("query PulseDuplicateConfirmation(", StringComparison.Ordinal) && !Text(input, "query").Contains("mutation", StringComparison.Ordinal) &&
                    Text(input["variables"]!.AsObject(), "owner") == "microsoft" && Text(input["variables"]!.AsObject(), "name") == "powertoys" && input["variables"]!["number"]!.GetValue<int>() == 7,
                    "Canonical reconciliation must use the fixed read-only query and the original target variables.");
                return Json(new JsonObject { ["data"] = new JsonObject { ["repository"] = new JsonObject
                {
                    ["nameWithOwner"] = Upstream, ["issue"] = new JsonObject
                    {
                        ["number"] = 7, ["state"] = IssueState.ToUpperInvariant(), ["stateReason"] = IssueStateReason.ToUpperInvariant(),
                        ["duplicateOf"] = CanonicalEvidence == "missing" ? null : new JsonObject { ["id"] = CanonicalEvidence == "different" ? "I_other" : "I_fixture_8", ["number"] = 8,
                            ["url"] = $"https://github.com/{Upstream}/issues/8", ["repository"] = new JsonObject { ["nameWithOwner"] = Upstream } }
                    }
                } } });
            }
            if (request.Method == HttpMethod.Get)
            {
                Reads.Add(request.RequestUri.PathAndQuery);
                var segments = path.Split('/');
                var repository = segments.Length >= 4 ? segments[2] + "/" + segments[3] : "";
                Check(repository is Upstream or Fork, "Only the fixed upstream or supported fork may be read.");
                var suffix = path[("/repos/" + repository).Length..];
                if (suffix.Length == 0) return Json(Repository(repository));
                if (suffix == "/pulls") return Page(new JsonArray(), request);
                if (suffix is "/pulls/7" or "/issues/7")
                {
                    TargetReadCount++;
                    BeforeTargetRead?.Invoke(TargetReadCount);
                    return Json(Target(repository, suffix.StartsWith("/pulls/", StringComparison.Ordinal)));
                }
                if (suffix == "/issues/8")
                {
                    var original = Target(repository, false);
                    original["number"] = 8; original["id"] = OriginalIssueId; original["node_id"] = "I_fixture_8";
                    original["html_url"] = $"https://github.com/{repository}/issues/8";
                    if (OriginalIsPullRequest) original["pull_request"] = new JsonObject { ["url"] = $"https://api.github.com/repos/{repository}/pulls/8" };
                    return Json(original);
                }
                if (suffix == "/pulls/7/files") return Page(new JsonArray(new JsonObject { ["filename"] = "a.cs", ["status"] = "modified", ["patch"] = "@@ -1,3 +1,4 @@\n one\n-two\n+replacement\n+extra\n three\n" }), request);
                if (suffix.StartsWith("/pulls/7/reviews/", StringComparison.Ordinal) && suffix.EndsWith("/comments", StringComparison.Ordinal))
                {
                    var id = int.Parse(suffix.Split('/')[4], System.Globalization.CultureInfo.InvariantCulture);
                    return Page(PostedInlineComments.GetValueOrDefault(id) ?? [], request);
                }
                if (suffix is "/issues/7/comments" or "/pulls/7/reviews" or "/pulls/7/comments")
                {
                    if (ChangeHeadAfterCollectionRead) Head = OtherSha;
                    return Page(suffix == "/pulls/7/reviews" ? Reviews : suffix == "/pulls/7/comments" ? new JsonArray() : repository == Fork ? ForkComments : UpstreamComments, request);
                }
                if (suffix == $"/commits/{Sha}/check-runs")
                    return Json(new JsonObject { ["total_count"] = 1, ["check_runs"] = new JsonArray(new JsonObject { ["id"] = CheckId, ["name"] = "Required build", ["status"] = CheckState, ["conclusion"] = CheckState == "completed" ? CheckConclusion : null, ["head_sha"] = Sha }) });
                if (suffix.StartsWith("/git/ref/heads/", StringComparison.Ordinal))
                {
                    if (repository == Fork && !HeadBranchExists) return Json(new JsonObject { ["message"] = "Not Found" }, HttpStatusCode.NotFound);
                    return Json(new JsonObject { ["ref"] = "refs/heads/" + suffix["/git/ref/heads/".Length..], ["object"] = new JsonObject { ["sha"] = repository == Fork ? SourceHeadSha : OtherSha, ["type"] = "commit" } });
                }
                throw new InvalidOperationException("Unexpected offline web-action GET " + request.RequestUri.PathAndQuery);
            }

            BeforeWrite?.Invoke();
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Writes.Add((request.Method.Method, path, body.DeepClone().AsObject()));
            if (Writes.Count == FailWriteAt) return Json(new JsonObject { ["message"] = "Fixture rejected the selected step." }, HttpStatusCode.UnprocessableEntity);
            JsonObject response;
            if (path == "/repos/microsoft/powertoys/pulls/7/reviews" && request.Method == HttpMethod.Post)
            {
                response = Review(100 + Writes.Count, Text(body, "event") switch { "APPROVE" => "APPROVED", "REQUEST_CHANGES" => "CHANGES_REQUESTED", _ => "COMMENTED" }, "reviewer", Text(body, "commit_id"));
                response["body"] = body["body"]?.DeepClone();
                response["submitted_at"] = Protocol.Now();
                if (ApplyWrites)
                {
                    Reviews.Add(response.DeepClone());
                    var comments = body["comments"]?.DeepClone().AsArray() ?? [];
                    foreach (var item in comments.OfType<JsonObject>())
                    {
                        item["original_line"] = item["line"]?.DeepClone();
                        item["original_start_line"] = item["start_line"]?.DeepClone();
                        item["pull_request_review_id"] = 100 + Writes.Count;
                        item["user"] = new JsonObject { ["login"] = "reviewer" };
                    }
                    PostedInlineComments[100 + Writes.Count] = comments;
                }
            }
            else if ((path == "/repos/microsoft/powertoys/issues/7/comments" || path == "/repos/muyuanms/powertoys/issues/7/comments") && request.Method == HttpMethod.Post)
            {
                var fork = path.Contains("/muyuanms/", StringComparison.Ordinal);
                response = Comment(100 + Writes.Count, Text(body, "body"), fork ? Fork : Upstream);
                if (ApplyWrites) (fork ? ForkComments : UpstreamComments).Add(response.DeepClone());
            }
            else if (path == "/repos/microsoft/powertoys/pulls/7/merge" && request.Method == HttpMethod.Put)
                response = new JsonObject { ["merged"] = true, ["sha"] = OtherSha, ["message"] = "Pull Request successfully merged" };
            else if (path == "/repos/microsoft/powertoys/issues/7" && request.Method == HttpMethod.Patch)
            {
                Check(Text(body, "state") == "closed" && Text(body, "state_reason") == "duplicate" && body["duplicate_issue_id"]?.GetValue<long>() == OriginalIssueId,
                    "Only a fixed duplicate closure with the server-resolved canonical issue ID may reach the fixture.");
                response = Target(Upstream, false);
                response["state"] = "closed"; response["state_reason"] = "duplicate"; response["closed_by"] = new JsonObject { ["login"] = "reviewer" };
                if (ApplyWrites) { IssueState = "closed"; IssueStateReason = "duplicate"; }
            }
            else if (path == "/repos/microsoft/powertoys/pulls" && request.Method == HttpMethod.Post)
                response = new JsonObject { ["number"] = 71, ["html_url"] = $"https://github.com/{Upstream}/pull/71", ["state"] = "open", ["base"] = new JsonObject { ["repo"] = Repository(Upstream) }, ["head"] = new JsonObject { ["sha"] = CreatedPrHeadSha } };
            else if (path == "/repos/microsoft/powertoys/issues/7/assignees" && request.Method == HttpMethod.Post)
            {
                response = Target(Upstream, false);
                response["assignees"] = new JsonArray(new JsonObject { ["login"] = "reviewer" });
            }
            else throw new InvalidOperationException("Unexpected offline web-action write " + request.Method + " " + path);
            if (Writes.Count == LoseResponseAt) throw new HttpRequestException("Simulated lost response after GitHub accepted the request.");
            return Json(response);
        }

        private JsonObject Repository(string repository) => new()
        {
            ["full_name"] = repository, ["name"] = "powertoys", ["archived"] = false, ["disabled"] = false,
            ["fork"] = repository == Fork, ["parent"] = new JsonObject { ["full_name"] = Upstream },
            ["permissions"] = new JsonObject { ["pull"] = true, ["push"] = CanPush, ["admin"] = false }
        };
        private JsonObject Target(string repository, bool isPr) => new()
        {
            ["number"] = 7, ["id"] = 7, ["title"] = "Offline target", ["state"] = isPr ? "open" : IssueState, ["state_reason"] = isPr ? null : IssueStateReason, ["draft"] = IsDraft,
            ["locked"] = false, ["mergeable"] = true, ["mergeable_state"] = MergeableState, ["changed_files"] = 1,
            ["closed_by"] = IssueState == "closed" ? new JsonObject { ["login"] = "reviewer" } : null,
            ["head"] = new JsonObject { ["sha"] = Head, ["ref"] = "feature/work", ["repo"] = Repository(Fork) },
            ["base"] = new JsonObject { ["ref"] = "main", ["repo"] = Repository(repository) },
            ["user"] = new JsonObject { ["login"] = "author" }, ["html_url"] = $"https://github.com/{repository}/{(isPr ? "pull" : "issues")}/7"
        };
        private static HttpResponseMessage Page(JsonArray values, HttpRequestMessage request)
        {
            var query = request.RequestUri!.Query;
            var pageText = query.Split('&').FirstOrDefault(part => part.StartsWith("page=", StringComparison.Ordinal));
            var page = pageText is null ? 1 : int.Parse(pageText["page=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            return Json(new JsonArray(values.Skip((page - 1) * 100).Take(100).Select(value => value?.DeepClone()).ToArray()));
        }
        private static HttpResponseMessage Json(JsonNode value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
