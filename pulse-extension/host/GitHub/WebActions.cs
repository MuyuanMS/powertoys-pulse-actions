using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>Durable website drafts. External callers can prepare/read; only extension UI can confirm writes.</summary>
public sealed class WebActions(Store store, Func<JsonObject, Task<GitHubSession>>? sessionFactory = null)
{
    private readonly Func<JsonObject, Task<GitHubSession>> openSession = sessionFactory ?? GitHubSession.OpenAsync;
    private const string Upstream = "microsoft/powertoys";
    private const string Fork = "muyuanms/powertoys";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(90);

    public JsonObject Prepare(JsonObject payload, string sourceOrigin)
        => PrepareCore(payload, sourceOrigin, "", false);

    internal JsonObject PrepareResult(JsonObject payload, string sourceOrigin, string attemptId, bool retry, string resultContentFingerprint = "")
        => PrepareCore(payload, sourceOrigin, attemptId, retry, resultContentFingerprint);

    private JsonObject PrepareCore(JsonObject payload, string sourceOrigin, string attemptId, bool retry, string resultContentFingerprint = "")
    {
        Protocol.OnlyKeys(payload, "draft");
        var draft = NormalizeDraft(Protocol.RequireObject(payload["draft"]));
        if (string.IsNullOrWhiteSpace(sourceOrigin)) throw new ProtocolException("ORIGIN_DENIED", "A verified source origin is required.");
        using var held = store.AcquireLock("web-actions-admission", LockTimeout);
        var requestId = Text(draft, "requestId");
        var indexPath = Path.Combine(store.Root, "web-actions", "requests", Protocol.Hash(requestId) + ".json");
        var existing = store.ReadJson(indexPath);
        // Records are authoritative if a process exited after saving a draft but before indexing it.
        if (existing is null)
        {
            var record = Records().FirstOrDefault(item => Text(item["draft"] as JsonObject, "requestId") == requestId);
            if (record is not null) existing = new JsonObject { ["operationId"] = Text(record, "operationId") };
        }
        if (existing is not null)
        {
            var record = Read(Text(existing, "operationId"));
            if (Text(record, "fingerprint") != Protocol.Fingerprint(draft) || Text(record, "sourceOrigin") != sourceOrigin)
                throw new ProtocolException("REQUEST_CONFLICT", "This requestId already belongs to different action content or origin.", "Retransmit the original draft unchanged, or create a new request for a deliberate new action.");
            if (Text(record, "attemptId") != attemptId || Bool(record, "retryRequested") != retry)
                throw new ProtocolException("REQUEST_CONFLICT", "This requestId already belongs to another confirmation attempt or retry intent.");
            return Summary(record);
        }
        using var admission = store.AcquireLock("accept");
        store.EnsureAvailable();
        var now = Protocol.Now();
        var saved = new JsonObject
        {
            ["operationId"] = Guid.NewGuid().ToString("D"), ["draft"] = draft,
            ["sourceOrigin"] = sourceOrigin, ["fingerprint"] = Protocol.Fingerprint(draft),
            ["status"] = "prepared", ["createdAt"] = now, ["updatedAt"] = now,
            ["completedSteps"] = new JsonArray(), ["urls"] = new JsonArray(), ["steps"] = Steps(draft)
        };
        if (attemptId.Length > 0) saved["attemptId"] = attemptId;
        if (resultContentFingerprint.Length > 0) saved["resultContentFingerprint"] = resultContentFingerprint;
        if (retry) saved["retryRequested"] = true;
        var proposedSource = Text(draft["pullRequest"] as JsonObject, "sourceHeadSha");
        if (proposedSource.Length > 0) { saved["sourceHeadSha"] = proposedSource; saved["sourcePreviewed"] = false; }
        Save(saved);
        store.WriteJson(indexPath, new JsonObject { ["operationId"] = Text(saved, "operationId") });
        return Summary(saved);
    }

    public JsonObject Get(JsonObject payload, string sourceOrigin)
    {
        var id = OperationId(payload);
        using var held = store.AcquireLock("web-action:" + id, LockTimeout);
        var record = Read(id);
        if (Text(record, "sourceOrigin") != sourceOrigin) throw NotFound();
        Recover(record);
        return Summary(record);
    }

    public async Task<JsonObject> PreviewAsync(JsonObject payload)
    {
        var id = OperationId(payload);
        using var held = store.AcquireLock("web-action:" + id, LockTimeout);
        var record = Read(id);
        Recover(record);
        var draft = record["draft"]!.AsObject();
        var result = Summary(record);
        result["draft"] = ExecutionDraft(record);
        result["sourceOrigin"] = Text(record, "sourceOrigin");
        result["targetUrl"] = TargetUrl(draft["target"]!.AsObject());
        if (Text(draft, "kind") == "close-as-duplicate")
        {
            result["duplicateSuffix"] = DuplicateSuffix(draft);
            result["commentBody"] = DuplicateCommentBody(record);
            result["bodyEditable"] = record["confirmedBody"] is null;
            result["resumeRequired"] = CanResumeDuplicate(record);
        }
        result["account"] = record["account"]?.DeepClone();
        result["canSubmit"] = false;
        var blockers = new JsonArray();
        result["blockers"] = blockers;
        if (Text(record, "status") != "prepared" && !CanResumeDuplicate(record))
        {
            blockers.Add(new ProtocolException("OPERATION_FINAL", "This action has already been submitted or cancelled.", "Inspect its recorded outcome and GitHub links. A partial or unknown action must never be resent automatically.").ToJson());
            return result;
        }
        try
        {
            ValidateApprovalPolicy(draft);
            ResultActions.ValidatePreparedSource(store, record);
            ValidateUnsettled(record);
            using var session = await openSession(new Configuration(store).Read());
            result["account"] = session.Account;
            var facts = await ReadContextAsync(draft, session, CanResumeDuplicate(record));
            foreach (var field in facts) result[field.Key] = field.Value?.DeepClone();
            ValidateDuplicatePin(record, facts);
            ValidateCiRetry(record, facts);
            if (!Completed(record, "duplicate-comment")) await CheckDuplicatesAsync(ExecutionDraft(record), session);
            if (Text(draft, "kind") == "create-pr")
            {
                var sourceSha = Text(facts, "sourceHeadSha");
                if (Text(record, "sourceHeadSha").Length == 0)
                {
                    record["sourceHeadSha"] = sourceSha;
                    Save(record);
                }
                result["sourceHeadSha"] = Text(record, "sourceHeadSha");
                if (Text(record, "sourceHeadSha") != sourceSha) throw SourceChanged();
                record["sourcePreviewed"] = true;
                Save(record);
            }
            ValidateApprovalPolicy(draft);
            result["canSubmit"] = true;
        }
        catch (Exception error) when (error is ProtocolException or GitHubRequestException)
        {
            blockers.Add(Describe(error));
        }
        return result;
    }

    public JsonObject Cancel(JsonObject payload)
    {
        var id = OperationId(payload);
        using var held = store.AcquireLock("web-action:" + id, LockTimeout);
        var record = Read(id);
        Recover(record);
        if (Text(record, "status") == "prepared")
        {
            record["status"] = "cancelled";
            Save(record);
        }
        return Summary(record);
    }

    /// <summary>Resolve a lost response using remote evidence only. Never resumes remaining writes.</summary>
    public async Task<JsonObject> ReconcileAsync(JsonObject payload)
    {
        var id = OperationId(payload);
        using var held = store.AcquireLock("web-action:" + id, LockTimeout);
        var record = Read(id);
        Recover(record);
        if (Text(record, "status") != "unknown") return Summary(record);
        var draft = record["draft"]!.AsObject();
        var target = draft["target"]!.AsObject();
        using var targetLock = store.AcquireLock("web-action-target:" + Text(target, "repository") + ":" + Integer(target, "number"), LockTimeout);
        try
        {
            // Reconciliation observes the account that made the uncertain write, even after Settings changes.
            var reconcileConfig = new Configuration(store).Read();
            var recordedAccount = Text(record, "account");
            if (recordedAccount.Length == 0) throw new ProtocolException("GITHUB_IDENTITY_UNAVAILABLE", "The original submission account is not recorded.", "Inspect the saved action and GitHub; this uncertain action will not be resent.");
            reconcileConfig["githubAccount"] = recordedAccount;
            using var session = await openSession(reconcileConfig);
            if (!session.Account.Equals(recordedAccount, StringComparison.OrdinalIgnoreCase)) throw new ProtocolException("GITHUB_IDENTITY_MISMATCH", "The original submission account could not be selected for reconciliation.");
            var step = Text(record, "activeStep");
            JsonObject? confirmed = null;
            if (step == "merge-pr")
            {
                var current = (await session.RequestAsync(HttpMethod.Get, TargetEndpoint(target))).AsObject();
                VerifyTarget(target, current);
                if (Bool(current, "merged") && Text(current["head"] as JsonObject, "sha").Equals(Text(draft, "expectedHeadSha"), StringComparison.OrdinalIgnoreCase) &&
                    Text(current["merged_by"] as JsonObject, "login").Equals(Text(record, "account"), StringComparison.OrdinalIgnoreCase)) confirmed = current;
            }
            else if (step == "assign-self")
            {
                var assignment = (draft["assignmentTarget"] ?? target).AsObject();
                var current = (await session.RequestAsync(HttpMethod.Get, TargetEndpoint(assignment))).AsObject();
                VerifyTarget(assignment, current);
                if ((current["assignees"] as JsonArray ?? []).OfType<JsonObject>().Any(item => Text(item, "login").Equals(Text(record, "account"), StringComparison.OrdinalIgnoreCase))) confirmed = current;
            }
            else if (step == "close-issue")
            {
                var current = (await session.RequestAsync(HttpMethod.Get, TargetEndpoint(target))).AsObject();
                VerifyTarget(target, current);
                if (Text(current, "state") == "closed" && Text(current, "state_reason") == "duplicate" && await ConfirmDuplicateAsync(record, session)) confirmed = current;
                else if (Text(current, "state") == "closed")
                    throw new ProtocolException("DUPLICATE_NOT_CONFIRMED", "The issue is closed, but its duplicate relationship to the selected original issue could not be confirmed.", "Inspect the GitHub duplicate reference. This uncertain close will not be resent or reported as successful without matching evidence.");
            }
            else if (step.Length > 0 && record["baselineIds"] is JsonArray baseline)
            {
                var rows = await session.ReadPagesAsync(StepCollection(draft, step), includeClosed: step == "create-pr");
                var baselineIds = baseline.Select(node => node!.GetValue<long>()).ToHashSet();
                var request = BuildWrite(ExecutionDraft(record), step, Text(record, "account"));
                var candidates = rows.OfType<JsonObject>().Where(row => Long(row, "id") > 0 && !baselineIds.Contains(Long(row, "id")) &&
                    Text(row["user"] as JsonObject, "login").Equals(Text(record, "account"), StringComparison.OrdinalIgnoreCase)).ToList();
                if (step == "create-pr")
                {
                    var pr = draft["pullRequest"]!.AsObject();
                    candidates = candidates.Where(row => Text(row["head"] as JsonObject, "label").Equals(Text(pr, "head"), StringComparison.OrdinalIgnoreCase) &&
                        Text(row["base"] as JsonObject, "ref") == Text(pr, "base") && Text(row["base"]?["repo"] as JsonObject, "full_name").Equals(Upstream, StringComparison.OrdinalIgnoreCase) &&
                        Text(row, "title") == Text(pr, "title") && NormalizeText(Text(row, "body")) == Text(pr, "body") && Bool(row, "draft") == Bool(pr, "draft") &&
                        (Text(record, "sourceHeadSha").Length == 0 || Text(row["head"] as JsonObject, "sha").Equals(Text(record, "sourceHeadSha"), StringComparison.OrdinalIgnoreCase))).ToList();
                }
                else
                {
                    candidates = candidates.Where(row => NormalizeText(Text(row, "body")) == Text(request.Body, "body")).ToList();
                    if (step == "review")
                    {
                        var expectedState = Text(request.Body, "event") switch { "APPROVE" => "APPROVED", "REQUEST_CHANGES" => "CHANGES_REQUESTED", _ => "COMMENTED" };
                        candidates = candidates.Where(row => Text(row, "state") == expectedState && Text(row, "commit_id").Equals(Text(draft, "expectedHeadSha"), StringComparison.OrdinalIgnoreCase)).ToList();
                        var exact = new List<JsonObject>();
                        foreach (var candidate in candidates)
                        {
                            var comments = await session.ReadPagesAsync(TargetEndpoint(target) + "/reviews/" + Long(candidate, "id") + "/comments");
                            if (SameInline(request.Body["comments"] as JsonArray ?? [], comments)) exact.Add(candidate);
                        }
                        candidates = exact;
                    }
                }
                if (candidates.Count == 1) confirmed = candidates[0];
            }
            if (confirmed is null)
            {
                record["error"] = new ProtocolException("OPERATION_UNKNOWN", "GitHub does not yet provide unique evidence confirming the pending write.", "Inspect the saved draft and GitHub. A missing or ambiguous match is not proof that the write failed; Pulse will not resend it.").ToJson();
            }
            else
            {
                if (!record["completedSteps"]!.AsArray().Any(node => node?.GetValue<string>() == step)) record["completedSteps"]!.AsArray().Add(step);
                RecordStepResult(record, step, confirmed, observed: step == "close-issue" && !Text(confirmed["closed_by"] as JsonObject, "login").Equals(recordedAccount, StringComparison.OrdinalIgnoreCase));
                var url = ResultUrl(draft, step, confirmed);
                if (!record["urls"]!.AsArray().Any(node => node?.GetValue<string>() == url)) record["urls"]!.AsArray().Add(url);
                record["activeStep"] = null;
                record.Remove("baselineIds");
                record["reconciled"] = true;
                var complete = record["completedSteps"]!.AsArray().Count == record["steps"]!.AsArray().Count;
                record["status"] = complete ? "succeeded" : "partial";
                record["error"] = complete ? null : new ProtocolException("PARTIAL_ACTION", "The pending write is confirmed; other selected steps were not submitted.", Text(draft, "kind") == "close-as-duplicate" ? "Review the confirmed association, then explicitly continue closing this issue. The association comment will not be posted again." : "Review the completed results before preparing a new draft containing only the remaining work.").ToJson();
            }
            Save(record);
        }
        catch (Exception error) when (error is ProtocolException or GitHubRequestException)
        {
            record["error"] = Describe(error);
            Save(record);
        }
        return Summary(record);
    }

    public async Task<JsonObject> SubmitAsync(JsonObject payload)
    {
        Protocol.OnlyKeys(payload, "operationId", "expectedAccount", "body", "resume");
        var id = Protocol.RunId(Protocol.RequiredString(payload, "operationId", 36));
        var expectedAccount = Protocol.RequiredString(payload, "expectedAccount", 100);
        if (!Regex.IsMatch(expectedAccount, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,99}\z"))
            throw new ProtocolException("INVALID_REQUEST", "expectedAccount must identify the account shown in the extension preview.");
        using var held = store.AcquireLock("web-action:" + id, LockTimeout);
        var record = Read(id);
        Recover(record);
        var savedDraft = record["draft"]!.AsObject();
        var duplicate = Text(savedDraft, "kind") == "close-as-duplicate";
        if (!duplicate && (payload.ContainsKey("body") || payload.ContainsKey("resume"))) throw new ProtocolException("INVALID_REQUEST", "Only close-as-duplicate accepts an edited explanation or an explicit continuation.");
        var resume = OptionalBool(payload, "resume");
        if (payload.ContainsKey("body") && record["confirmedBody"] is not null && OptionalText(payload, "body", 60000) != Text(record, "confirmedBody"))
            throw new ProtocolException("REQUEST_CONFLICT", "The explanation was already confirmed and cannot be replaced for this action.", "Inspect the original confirmation and completed steps. A continuation only closes the issue and does not send another comment.");
        if (Text(record, "status") != "prepared" && !(CanResumeDuplicate(record) && resume)) return Summary(record);
        if (resume && !CanResumeDuplicate(record)) throw new ProtocolException("INVALID_REQUEST", "Only an association comment that is already confirmed can continue with the remaining close step.");
        if (duplicate && record["confirmedBody"] is null)
        {
            record["confirmedBody"] = payload.ContainsKey("body") ? NonemptyBody(payload) : Text(savedDraft, "body");
            Save(record); // Freeze the reviewed text before any read or write can fail.
        }
        var draft = ExecutionDraft(record);
        var target = draft["target"]!.AsObject();
        // Coordinate all website actions for this target across browsers/native-host processes.
        using var targetLock = store.AcquireLock("web-action-target:" + Text(target, "repository") + ":" + Integer(target, "number"), LockTimeout);
        var activeWrite = false;
        try
        {
            ValidateApprovalPolicy(savedDraft);
            ResultActions.ValidatePreparedSource(store, record);
            ValidateUnsettled(record);
            using var session = await openSession(new Configuration(store).Read());
            if (!session.Account.Equals(expectedAccount, StringComparison.OrdinalIgnoreCase))
                throw new ProtocolException("GITHUB_IDENTITY_MISMATCH", "The selected GitHub account differs from the account shown in the confirmation.", "Refresh the preview and review the actual account before preparing a new submission.");
            record["account"] = session.Account;
            var context = await ReadContextAsync(draft, session, CanResumeDuplicate(record));
            ValidateDuplicatePin(record, context);
            ValidateSourcePin(record, context);
            ValidateCiRetry(record, context);
            if (!Completed(record, "duplicate-comment")) await CheckDuplicatesAsync(draft, session);
            // Maintenance takes this admission lock before checking active work. Publish activity
            // under the same lock so uninstall/upgrade can never miss an about-to-start mutation.
            using (store.AcquireLock("accept"))
            {
                store.EnsureAvailable();
                record["status"] = "submitting";
                Save(record);
            }
            foreach (var stepNode in record["steps"]!.AsArray())
            {
                var step = stepNode!.GetValue<string>();
                if (Completed(record, step)) continue;
                var baseline = step is "merge-pr" or "assign-self" or "close-issue" ? new JsonArray() : await session.ReadPagesAsync(StepCollection(draft, step), includeClosed: step == "create-pr");
                if (step is "merge-pr" or "trigger-ci" or "create-pr" or "close-issue")
                {
                    var latestContext = await ReadContextAsync(draft, session, step == "close-issue");
                    ValidateDuplicatePin(record, latestContext);
                    ValidateSourcePin(record, latestContext);
                    ValidateCiRetry(record, latestContext);
                    if (step == "trigger-ci") record["ciBaseline"] = latestContext["ciEvidence"]?.DeepClone();
                }
                var request = BuildWrite(draft, step, session.Account, Long(record, "duplicateIssueId"));
                if (step is not ("merge-pr" or "assign-self" or "create-pr" or "close-issue"))
                {
                    var bodies = new List<string> { Text(request.Body, "body") };
                    bodies.AddRange((request.Body["comments"] as JsonArray ?? []).OfType<JsonObject>().Select(item => Text(item, "body")));
                    await CheckDuplicateBodiesAsync(target, bodies, session);
                }
                // Re-read state/SHA immediately before each write, after any pagination and earlier steps.
                ResultActions.ValidatePreparedSource(store, record);
                var latest = await VerifyLatestAsync(draft, session, step == "close-issue");
                if (step == "close-issue" && Text(latest, "state") == "closed")
                {
                    if (Text(latest, "state_reason") != "duplicate" || !await ConfirmDuplicateAsync(record, session))
                        throw new ProtocolException("DUPLICATE_NOT_CONFIRMED", "The issue is closed, but GitHub has not confirmed it as a duplicate of the selected original issue.", "Inspect the current GitHub issue and its original reference. Pulse will not replace another closing decision automatically.");
                    record["completedSteps"]!.AsArray().Add(step);
                    RecordStepResult(record, step, latest, observed: true);
                    var closedUrl = ResultUrl(draft, step, latest);
                    if (!record["urls"]!.AsArray().Any(node => node?.GetValue<string>() == closedUrl)) record["urls"]!.AsArray().Add(closedUrl);
                    Save(record);
                    continue;
                }
                if (step == "assign-self") await ValidateAssignmentAsync(draft, session);
                if (step == "review") ValidateApprovalPolicy(savedDraft);
                record["status"] = "submitting";
                record["activeStep"] = step;
                record["baselineIds"] = new JsonArray(baseline.OfType<JsonObject>().Select(row => (JsonNode?)JsonValue.Create(Long(row, "id"))).ToArray());
                Save(record); // Flush intent before starting a mutation; a crash leaves an unknown outcome.
                activeWrite = true;
                var response = (await session.RequestAsync(request.Method, request.Endpoint, request.Body)).AsObject();
                if (step == "create-pr")
                {
                    // GitHub creates PRs from branch names, not an atomic source-SHA parameter.
                    // Keep the resulting link even if the branch moved during the POST.
                    var createdUrl = ResultUrl(draft, step, response);
                    if (!record["urls"]!.AsArray().Any(node => node?.GetValue<string>() == createdUrl)) record["urls"]!.AsArray().Add(createdUrl);
                }
                ValidateWriteResponse(draft, step, response, session.Account, Text(record, "sourceHeadSha"));
                var url = ResultUrl(draft, step, response);
                record["completedSteps"]!.AsArray().Add(step);
                RecordStepResult(record, step, response);
                if (url.Length > 0 && !record["urls"]!.AsArray().Any(node => node?.GetValue<string>() == url)) record["urls"]!.AsArray().Add(url);
                activeWrite = false; // The response now proves this step completed, even if snapshot persistence fails.
                record["activeStep"] = null;
                record.Remove("baselineIds");
                Save(record);
            }
            record["status"] = "succeeded";
            record["error"] = null;
            Save(record);
        }
        catch (Exception error) when (error is ProtocolException or GitHubRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var unknown = error is GitHubRequestException { Uncertain: true } || activeWrite && error is not GitHubRequestException { Uncertain: false };
            var completed = record["completedSteps"]!.AsArray().Count;
            var allCompleted = completed == record["steps"]!.AsArray().Count;
            record["status"] = unknown ? "unknown" : allCompleted ? "succeeded" : completed > 0 ? "partial" : "failed";
            record["error"] = allCompleted && !unknown ? null : unknown
                ? new ProtocolException("OPERATION_UNKNOWN", "A GitHub write started but its result could not be confirmed.", "Inspect the completed steps and GitHub links. Do not resend this action; remaining steps may include an unconfirmed write.").ToJson()
                : Describe(error);
            if (!unknown) { record["activeStep"] = null; record.Remove("baselineIds"); }
            Save(record);
        }
        return Summary(record);
    }

    private async Task<JsonObject> ReadContextAsync(JsonObject draft, GitHubSession session, bool allowClosedIssue = false)
    {
        var target = draft["target"]!.AsObject();
        var repository = Text(target, "repository");
        var repo = (await session.RequestAsync(HttpMethod.Get, "/repos/" + repository)).AsObject();
        if (!Text(repo, "full_name").Equals(repository, StringComparison.OrdinalIgnoreCase)) throw TargetMismatch();
        var permissions = repo["permissions"] as JsonObject;
        var writer = Bool(permissions, "push") || Bool(permissions, "maintain") || Bool(permissions, "admin");
        if (session.Account.Length == 0 || Bool(repo, "private") && !Bool(permissions, "pull") && !writer)
            throw new ProtocolException("GITHUB_PERMISSION_REQUIRED", "The selected account cannot read this repository.");
        if (Bool(repo, "archived") || Bool(repo, "disabled")) throw new ProtocolException("GITHUB_OPERATION_UNAVAILABLE", "The target repository is archived or disabled.");
        var current = await VerifyLatestAsync(draft, session, allowClosedIssue);
        if (Bool(current, "locked") && !writer) throw new ProtocolException("GITHUB_OPERATION_UNAVAILABLE", "This conversation is locked and the selected account cannot write to it.");
        var kind = Text(draft, "kind");
        var author = session.Account.Equals(Text(current["user"] as JsonObject, "login"), StringComparison.OrdinalIgnoreCase);
        if (author && (kind == "approve" || kind == "review" && Text(draft["review"] as JsonObject, "event") == "REQUEST_CHANGES"))
            throw new ProtocolException("GITHUB_OPERATION_UNAVAILABLE", "GitHub does not permit approving or requesting changes on your own pull request.");
        if (kind is "merge-pr" or "trigger-ci" && !writer)
            throw new ProtocolException("GITHUB_PERMISSION_REQUIRED", "This action requires repository write permission for the selected account.");
        if (kind == "close-as-duplicate" && !writer && !Bool(permissions, "triage"))
            throw new ProtocolException("GITHUB_PERMISSION_REQUIRED", "Marking this issue as a duplicate requires repository triage or write permission.");
        var facts = new JsonObject
        {
            ["state"] = Text(current, "state"), ["headSha"] = Text(current["head"] as JsonObject, "sha"),
            ["draftPr"] = Bool(current, "draft"), ["title"] = Text(current, "title"),
            ["canWriteRepository"] = writer, ["mergeable"] = Bool(current, "mergeable"), ["mergeableState"] = Text(current, "mergeable_state")
        };
        if (kind == "review" && (draft["review"]?["comments"] as JsonArray ?? []).Count > 0)
        {
            if (Integer(current, "changed_files") > 3000) throw InvalidDiff();
            // GitHub caps the collection at 3,000 files; page 31 proves termination when page 30 is full.
            var files = await session.ReadPagesAsync(TargetEndpoint(target) + "/files", 31);
            if (files.Count != Integer(current, "changed_files")) throw InvalidDiff();
            foreach (var comment in draft["review"]!["comments"]!.AsArray().OfType<JsonObject>()) ValidateInline(comment, files);
        }
        if (kind == "trigger-ci")
        {
            var ciEvidence = await ReadCiAsync(draft, session);
            var ci = Text(ciEvidence, "state");
            facts["ciState"] = ci;
            facts["ciEvidence"] = ciEvidence;
            if (kind == "trigger-ci" && ci is "passed" or "pending")
                throw new ProtocolException(ci == "passed" ? "CI_ALREADY_PASSED" : "CI_ALREADY_RUNNING", ci == "passed" ? "CI already passes for this PR revision." : "CI is already running for this PR revision.", "Refresh the current checks; no /azp run comment was posted.");
        }
        if (kind == "close-as-duplicate")
        {
            var original = DuplicateTarget(draft);
            var originalIssue = (await session.RequestAsync(HttpMethod.Get, TargetEndpoint(original))).AsObject();
            VerifyTarget(original, originalIssue);
            if (Long(originalIssue, "id") <= 0 || Text(originalIssue, "node_id").Length == 0)
                throw new ProtocolException("INVALID_GITHUB_RESPONSE", "GitHub did not identify the original issue.");
            facts["duplicateOf"] = draft["duplicateOf"]!.DeepClone();
            facts["duplicateTitle"] = Text(originalIssue, "title");
            facts["duplicateState"] = Text(originalIssue, "state");
            facts["duplicateIssueId"] = originalIssue["id"]!.DeepClone();
            facts["duplicateNodeId"] = originalIssue["node_id"]!.DeepClone();
        }
        if (kind == "create-pr")
        {
            var pr = draft["pullRequest"]!.AsObject();
            var parts = Text(pr, "head").Split(':', 2);
            facts["sourceHeadSha"] = await VerifyBranchAsync(parts[0] + "/powertoys", parts[1], session, isSource: true);
            facts["baseHeadSha"] = await VerifyBranchAsync(Upstream, Text(pr, "base"), session, isSource: false);
            // The full list is paginated so an existing open PR cannot be missed beyond page one.
            var pulls = await session.ReadPagesAsync("/repos/" + Upstream + "/pulls");
            if (pulls.OfType<JsonObject>().Any(pull => Text(pull, "state") == "open" &&
                Text(pull["head"] as JsonObject, "label").Equals(Text(pr, "head"), StringComparison.OrdinalIgnoreCase) && Text(pull["base"] as JsonObject, "ref") == Text(pr, "base")))
                throw new ProtocolException("DUPLICATE_PULL_REQUEST", "An open pull request already exists for this source and base branch.", "Open the existing pull request on GitHub instead of creating another.");
        }
        if (Bool(draft, "assignSelf")) await ValidateAssignmentAsync(draft, session);
        await VerifyLatestAsync(draft, session, allowClosedIssue);
        return facts;
    }

    private void ValidateApprovalPolicy(JsonObject draft)
    {
        if (Text(draft, "kind") != "approve" && !(Text(draft, "kind") == "review" && Text(draft["review"] as JsonObject, "event") == "APPROVE")) return;
        var target = draft["target"]!.AsObject();
        var task = new JsonObject
        {
            ["repository"] = target["repository"]!.DeepClone(),
            ["target"] = new JsonObject { ["type"] = target["type"]!.DeepClone(), ["number"] = target["number"]!.DeepClone() },
            ["expectedHeadSha"] = draft["expectedHeadSha"]?.DeepClone()
        };
        if (PrActionPolicy.HasConfirmedP0(store, task))
            throw new ProtocolException("CONFIRMED_P0", "This PR revision has a confirmed unresolved P0 finding.", "Resolve the confirmed P0 on the original PR revision before approving it. Other manual GitHub actions remain available.");
    }

    private async Task<JsonObject> VerifyLatestAsync(JsonObject draft, GitHubSession session, bool allowClosedIssue = false)
    {
        var target = draft["target"]!.AsObject();
        var current = (await session.RequestAsync(HttpMethod.Get, TargetEndpoint(target))).AsObject();
        VerifyTarget(target, current);
        if (Text(current, "state") != "open" && !(allowClosedIssue && Text(target, "type") == "issue" && Text(current, "state") == "closed")) throw new ProtocolException("GITHUB_OPERATION_UNAVAILABLE", "The target is no longer open.");
        if (Text(target, "type") == "pr")
        {
            if (!Text(current["head"] as JsonObject, "sha").Equals(Text(draft, "expectedHeadSha"), StringComparison.OrdinalIgnoreCase))
                throw new ProtocolException("STALE_CONTEXT", "The PR HEAD changed since this action was prepared.", "Refresh and re-analyze the current PR revision before creating a new draft.");
            if (Bool(current, "draft") && Text(draft, "kind") != "trigger-ci") throw new ProtocolException("GITHUB_OPERATION_UNAVAILABLE", "The pull request is a draft and is not ready for this action.");
            if (Text(draft, "kind") == "merge-pr" && (!Bool(current, "mergeable") || Text(current, "mergeable_state") is not ("clean" or "unstable" or "has_hooks")))
                throw new ProtocolException("MERGE_NOT_READY", "GitHub does not currently report this pull request as mergeable.", "Inspect the current GitHub merge requirements. The final merge request remains subject to GitHub's branch rules.");
        }
        return current;
    }

    private static async Task ValidateAssignmentAsync(JsonObject draft, GitHubSession session)
    {
        var target = (draft["assignmentTarget"] ?? draft["target"])!.AsObject();
        var item = (await session.RequestAsync(HttpMethod.Get, TargetEndpoint(target))).AsObject();
        VerifyTarget(target, item);
        if (Text(item, "state") != "open") throw new ProtocolException("ASSIGNMENT_UNAVAILABLE", "The self-assignment target is no longer open.");
        var repo = (await session.RequestAsync(HttpMethod.Get, "/repos/" + Text(target, "repository"))).AsObject();
        var permission = repo["permissions"] as JsonObject;
        if (!Text(repo, "full_name").Equals(Text(target, "repository"), StringComparison.OrdinalIgnoreCase)) throw TargetMismatch();
        if (Bool(repo, "archived") || Bool(repo, "disabled") || Bool(item, "locked") && !(Bool(permission, "push") || Bool(permission, "maintain") || Bool(permission, "admin")))
            throw new ProtocolException("ASSIGNMENT_UNAVAILABLE", "The repository or conversation does not currently allow this self-assignment.");
    }

    private static async Task<string> VerifyBranchAsync(string repository, string branch, GitHubSession session, bool isSource)
    {
        JsonObject reference;
        try { reference = (await session.RequestAsync(HttpMethod.Get, "/repos/" + repository + "/git/ref/heads/" + branch)).AsObject(); }
        catch (GitHubRequestException error) when (error.StatusCode == 404)
        {
            throw new ProtocolException(isSource ? "SOURCE_BRANCH_UNPUBLISHED" : "BASE_BRANCH_UNAVAILABLE",
                isSource ? "The proposed source branch does not exist on GitHub or is unavailable to this account." : "The proposed base branch does not exist on GitHub or is unavailable to this account.",
                "Choose an existing remote branch and review its commit. Creating a PR does not commit, push, or publish local worktree changes.");
        }
        if (Text(reference, "ref") != "refs/heads/" + branch || !Sha(Text(reference["object"] as JsonObject, "sha")))
            throw new ProtocolException("BRANCH_UNAVAILABLE", "GitHub could not confirm the proposed source or base branch.", "Verify both branches exist and refresh the proposed pull request.");
        return Text(reference["object"] as JsonObject, "sha").ToLowerInvariant();
    }

    private static void ValidateSourcePin(JsonObject record, JsonObject context)
    {
        if (Text(record["draft"] as JsonObject, "kind") != "create-pr") return;
        var pinned = Text(record, "sourceHeadSha");
        if (!Sha(pinned) || record["sourcePreviewed"] is not null && !Bool(record, "sourcePreviewed")) throw new ProtocolException("PREVIEW_REQUIRED", "Review the source branch commit in the extension before creating this PR.", "Reload this proposal's confirmation page. No local files or commits will be published automatically.");
        if (pinned != Text(context, "sourceHeadSha")) throw SourceChanged();
    }

    private static ProtocolException SourceChanged() => new("STALE_SOURCE_BRANCH", "The source branch no longer matches the commit recorded for this PR proposal.", "Inspect and validate the new remote commit before preparing a new proposal. The saved proposal is not silently rebound to another commit.");

    private void ValidateDuplicatePin(JsonObject record, JsonObject context)
    {
        if (Text(record["draft"] as JsonObject, "kind") != "close-as-duplicate") return;
        if (record["duplicateIssueId"] is null)
        {
            record["duplicateIssueId"] = context["duplicateIssueId"]!.DeepClone();
            record["duplicateNodeId"] = context["duplicateNodeId"]!.DeepClone();
            Save(record);
        }
        else if (Long(record, "duplicateIssueId") != Long(context, "duplicateIssueId") || Text(record, "duplicateNodeId") != Text(context, "duplicateNodeId"))
            throw new ProtocolException("DUPLICATE_TARGET_CHANGED", "The original issue no longer matches the issue shown in this confirmation.", "Inspect the original issue before preparing a new confirmation. The saved reference cannot silently select another issue.");
    }

    private static async Task<bool> ConfirmDuplicateAsync(JsonObject record, GitHubSession session)
    {
        var draft = record["draft"]!.AsObject();
        var target = draft["target"]!.AsObject();
        var parts = Text(target, "repository").Split('/');
        // This fixed read-only query observes the current canonical relationship, including later undo/change,
        // without inferring a duplicate from a generic closed state or an old timeline event.
        var query = new JsonObject
        {
            ["query"] = "query PulseDuplicateConfirmation($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){nameWithOwner issue(number:$number){number state stateReason duplicateOf{id number url repository{nameWithOwner}}}}}",
            ["variables"] = new JsonObject { ["owner"] = parts[0], ["name"] = parts[1], ["number"] = Integer(target, "number") }
        };
        var response = (await session.RequestAsync(HttpMethod.Post, "/graphql", query)).AsObject();
        if (response["errors"] is JsonArray errors && errors.Count > 0) return false;
        var repository = response["data"]?["repository"] as JsonObject;
        var issue = repository?["issue"] as JsonObject;
        var canonical = issue?["duplicateOf"] as JsonObject;
        var expected = draft["duplicateOf"]!.AsObject();
        return Text(repository, "nameWithOwner").Equals(Text(target, "repository"), StringComparison.OrdinalIgnoreCase) && issue is not null &&
            Integer(issue, "number") == Integer(target, "number") && Text(issue, "state") == "CLOSED" && Text(issue, "stateReason") == "DUPLICATE" &&
            canonical is not null && Text(canonical, "id") == Text(record, "duplicateNodeId") && Integer(canonical, "number") == Integer(expected, "number") &&
            Text(canonical["repository"] as JsonObject, "nameWithOwner").Equals(Text(expected, "repository"), StringComparison.OrdinalIgnoreCase) &&
            Text(canonical, "url").Equals(Text(expected, "url"), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonObject> ReadCiAsync(JsonObject draft, GitHubSession session)
    {
        var checks = new List<JsonObject>();
        var complete = false;
        for (var page = 1; page <= 100; page++)
        {
            var result = (await session.RequestAsync(HttpMethod.Get, "/repos/" + Text(draft["target"] as JsonObject, "repository") + "/commits/" + Text(draft, "expectedHeadSha") + "/check-runs?per_page=100&page=" + page)).AsObject();
            var rows = result["check_runs"] as JsonArray ?? throw new ProtocolException("INVALID_GITHUB_RESPONSE", "GitHub returned no check-run collection.");
            checks.AddRange(rows.OfType<JsonObject>());
            if (rows.Count < 100)
            {
                if (result["total_count"] is not null && checks.Count != Integer(result, "total_count")) throw new ProtocolException("GITHUB_READ_LIMIT", "The complete CI check record could not be established.");
                complete = true;
                break;
            }
        }
        if (!complete) throw new ProtocolException("GITHUB_READ_LIMIT", "The complete CI check record exceeds the supported read limit.");
        var latest = checks.GroupBy(check => Text(check["app"] as JsonObject, "slug") + ":" + Text(check, "name"))
            .Select(group => group.OrderBy(check => Long(check, "id")).ThenBy(check => Text(check, "started_at"), StringComparer.Ordinal).Last()).ToList();
        var state = latest.Count == 0 ? "missing" : latest.Any(check => Text(check, "status") != "completed") ? "pending" :
            latest.All(check => Text(check, "conclusion") is "success" or "neutral" or "skipped") ? "passed" : "failed";
        var identities = new JsonArray(latest.OrderBy(check => Long(check, "id")).Select(check => (JsonNode)new JsonObject
        {
            ["id"] = Long(check, "id"), ["status"] = Text(check, "status"), ["conclusion"] = Text(check, "conclusion"),
            ["startedAt"] = Text(check, "started_at"), ["completedAt"] = Text(check, "completed_at")
        }).ToArray());
        return new JsonObject { ["state"] = state, ["checks"] = identities };
    }

    private void ValidateCiRetry(JsonObject record, JsonObject context)
    {
        var draft = record["draft"]!.AsObject();
        if (Text(draft, "kind") != "trigger-ci") return;
        var previous = Records().Where(other => Text(other, "operationId") != Text(record, "operationId") &&
            SameTarget(draft["target"]!.AsObject(), other["draft"]?["target"] as JsonObject) && Text(other["draft"] as JsonObject, "kind") == "trigger-ci" &&
            Text(other["draft"] as JsonObject, "expectedHeadSha") == Text(draft, "expectedHeadSha") && Text(other, "status") == "succeeded")
            .OrderByDescending(other => Text(other, "updatedAt"), StringComparer.Ordinal).FirstOrDefault();
        if (previous is null) return;
        if (Text(record, "sourceOrigin").StartsWith("pulse-task://", StringComparison.Ordinal) && !Bool(record, "retryRequested"))
            throw new ProtocolException("CI_RETRY_CONFIRMATION_REQUIRED", "CI has already been requested for this revision.", "Use the explicit retry action after that CI run has failed.");
        var current = context["ciEvidence"] as JsonObject;
        var baseline = previous["ciBaseline"] as JsonObject;
        var newerFailure = baseline is not null ? !JsonNode.DeepEquals(baseline, current) :
            (current?["checks"] as JsonArray ?? []).OfType<JsonObject>().Any(check =>
                DateTimeOffset.TryParse(Text(check, "completedAt"), out var completed) && DateTimeOffset.TryParse(Text(previous, "updatedAt"), out var submitted) && completed > submitted);
        if (Text(context, "ciState") != "failed" || !newerFailure)
            throw new ProtocolException("CI_RETRY_NOT_READY", "No newly failed CI run has been observed since the previous trigger.", "Wait for the requested run to finish. Retry becomes available when a later run fails; repeated clicks will not post another command.");
    }

    private void ValidateUnsettled(JsonObject record)
    {
        var target = record["draft"]!["target"]!.AsObject();
        if (Records().Any(other => Text(other, "operationId") != Text(record, "operationId") &&
            SameTarget(target, other["draft"]?["target"] as JsonObject) && Text(other, "status") is "unknown" or "submitting" or "partial"))
            throw new ProtocolException("OPERATION_UNKNOWN", "Another action for this target has an unresolved or partial GitHub result.", "Inspect the earlier action and GitHub before creating a further submission for this target.");
    }

    private static async Task CheckDuplicatesAsync(JsonObject draft, GitHubSession session)
    {
        await CheckDuplicateBodiesAsync(draft["target"]!.AsObject(), DraftBodies(draft), session);
    }

    private static async Task CheckDuplicateBodiesAsync(JsonObject target, IEnumerable<string> bodies, GitHubSession session)
    {
        var wanted = bodies.SelectMany(Markers).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return;
        var endpoints = new List<string> { IssueEndpoint(target) + "/comments" };
        if (Text(target, "type") == "pr")
        {
            endpoints.Add(TargetEndpoint(target) + "/reviews");
            endpoints.Add(TargetEndpoint(target) + "/comments");
        }
        foreach (var endpoint in endpoints)
        {
            var rows = await session.ReadPagesAsync(endpoint);
            if (rows.OfType<JsonObject>().Any(row => Markers(Text(row, "body")).Any(wanted.Contains)))
                throw new ProtocolException("DUPLICATE_COMMENT", "A selected Pulse comment was already published on this target.", "Refresh the analysis and remove the already-posted selection before preparing a new action.");
        }
    }

    private sealed record WriteRequest(HttpMethod Method, string Endpoint, JsonObject Body);
    private static string StepCollection(JsonObject draft, string step) => step == "create-pr" ? "/repos/" + Upstream + "/pulls" :
        step == "review" ? TargetEndpoint(draft["target"]!.AsObject()) + "/reviews" : IssueEndpoint(draft["target"]!.AsObject()) + "/comments";
    private static WriteRequest BuildWrite(JsonObject draft, string step, string account, long duplicateIssueId = 0)
    {
        var target = draft["target"]!.AsObject();
        if (step == "assign-self") return new(HttpMethod.Post, IssueEndpoint((draft["assignmentTarget"] ?? target).AsObject()) + "/assignees", new JsonObject { ["assignees"] = new JsonArray(account) });
        if (step is "comment" or "trigger-ci") return new(HttpMethod.Post, IssueEndpoint(target) + "/comments", new JsonObject { ["body"] = Text(draft, "body") });
        if (step == "duplicate-comment") return new(HttpMethod.Post, IssueEndpoint(target) + "/comments", new JsonObject { ["body"] = Text(draft, "body") + DuplicateSuffix(draft) });
        if (step == "close-issue")
        {
            if (duplicateIssueId <= 0) throw new ProtocolException("PREVIEW_REQUIRED", "The original issue must be verified before closing this duplicate.");
            return new(HttpMethod.Patch, IssueEndpoint(target), new JsonObject { ["state"] = "closed", ["state_reason"] = "duplicate", ["duplicate_issue_id"] = duplicateIssueId });
        }
        if (step.StartsWith("general-comment-", StringComparison.Ordinal))
        {
            var index = int.Parse(step["general-comment-".Length..], System.Globalization.CultureInfo.InvariantCulture) - 1;
            return new(HttpMethod.Post, IssueEndpoint(target) + "/comments", new JsonObject { ["body"] = Text(draft["review"]!["generalComments"]![index] as JsonObject, "body") });
        }
        if (step == "merge-pr") return new(HttpMethod.Put, TargetEndpoint(target) + "/merge", new JsonObject { ["sha"] = Text(draft, "expectedHeadSha"), ["merge_method"] = "squash" });
        if (step == "create-pr")
        {
            var pullRequest = draft["pullRequest"]!.DeepClone().AsObject();
            pullRequest.Remove("sourceHeadSha");
            return new(HttpMethod.Post, "/repos/" + Upstream + "/pulls", pullRequest);
        }
        var review = draft["review"] as JsonObject;
        var body = new JsonObject { ["event"] = Text(draft, "kind") == "approve" ? "APPROVE" : Text(review, "event"), ["commit_id"] = Text(draft, "expectedHeadSha") };
        if (Text(draft, "body").Length > 0) body["body"] = Text(draft, "body");
        var inline = new JsonArray();
        foreach (var item in (review?["comments"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var comment = new JsonObject { ["path"] = Text(item, "path"), ["line"] = Integer(item, "line"), ["side"] = Text(item, "side"), ["body"] = Text(item, "body") };
            if (item["startLine"] is not null && Integer(item, "startLine") != Integer(item, "line"))
            {
                comment["start_line"] = Integer(item, "startLine");
                comment["start_side"] = Text(item, "startSide");
            }
            inline.Add(comment);
        }
        if (inline.Count > 0) body["comments"] = inline;
        return new(HttpMethod.Post, TargetEndpoint(target) + "/reviews", body);
    }

    private static void ValidateWriteResponse(JsonObject draft, string step, JsonObject response, string account, string sourceHeadSha = "")
    {
        var verified = step switch
        {
            "merge-pr" => Bool(response, "merged"),
            "create-pr" => Integer(response, "number") > 0 && Text(response, "state") == "open" &&
                Text(response["base"]?["repo"] as JsonObject, "full_name").Equals(Upstream, StringComparison.OrdinalIgnoreCase) &&
                Sha(sourceHeadSha) && Text(response["head"] as JsonObject, "sha").Equals(sourceHeadSha, StringComparison.OrdinalIgnoreCase),
            "assign-self" => (response["assignees"] as JsonArray ?? []).OfType<JsonObject>().Any(assignee => Text(assignee, "login").Equals(account, StringComparison.OrdinalIgnoreCase)),
            "close-issue" => Integer(response, "number") == Integer(draft["target"]!.AsObject(), "number") && response["pull_request"] is null && Text(response, "state") == "closed" && Text(response, "state_reason") == "duplicate",
            _ => Long(response, "id") > 0
        };
        if (!verified) throw new GitHubRequestException(0, true, "GitHub returned a write result that could not be confirmed.", "Check the saved action on GitHub; do not resend it.");
        if (step == "review" && response["commit_id"] is not null && !Text(response, "commit_id").Equals(Text(draft, "expectedHeadSha"), StringComparison.OrdinalIgnoreCase))
            throw new GitHubRequestException(0, true, "The returned review does not match the confirmed commit.", "Inspect the GitHub review before taking another action.");
    }

    private static bool SameInline(JsonArray expected, JsonArray actual)
    {
        if (expected.Count != actual.Count) return false;
        var unmatched = actual.OfType<JsonObject>().ToList();
        foreach (var item in expected.OfType<JsonObject>())
        {
            var line = Integer(item, "line");
            var start = item["start_line"] is null ? line : Integer(item, "start_line");
            var index = unmatched.FindIndex(row => Text(row, "path") == Text(item, "path") && Text(row, "side") == Text(item, "side") &&
                NormalizeText(Text(row, "body")) == Text(item, "body") &&
                (row["original_line"] is null ? Integer(row, "line") : Integer(row, "original_line")) == line &&
                (row["original_start_line"] is not null ? Integer(row, "original_start_line") : row["start_line"] is not null ? Integer(row, "start_line") : row["original_line"] is not null ? Integer(row, "original_line") : Integer(row, "line")) == start &&
                (start == line || Text(row, "start_side") == Text(item, "start_side")));
            if (index < 0) return false;
            unmatched.RemoveAt(index);
        }
        return unmatched.Count == 0;
    }

    private static JsonObject NormalizeDraft(JsonObject input)
    {
        Protocol.OnlyKeys(input, "requestId", "actionId", "kind", "target", "expectedHeadSha", "body", "review", "pullRequest", "assignSelf", "assignmentTarget", "duplicateOf");
        var kind = Protocol.RequiredString(input, "kind", 30);
        if (kind is not ("comment" or "review" or "approve" or "trigger-ci" or "merge-pr" or "create-pr" or "close-as-duplicate")) throw new ProtocolException("INVALID_OPERATION", "This GitHub action kind is unsupported.");
        var target = NormalizeTarget(Protocol.RequireObject(input["target"]));
        if (Text(target, "repository") == Fork && kind != "comment") throw new ProtocolException("INVALID_TARGET", "Only comments may target the supported PowerToys fork.");
        var isPr = Text(target, "type") == "pr";
        if (kind is "review" or "approve" or "trigger-ci" or "merge-pr" && !isPr || kind is "create-pr" or "close-as-duplicate" && isPr)
            throw new ProtocolException("INVALID_TARGET", "This action kind does not match the target type.");
        var draft = new JsonObject
        {
            ["requestId"] = Protocol.RunId(Protocol.RequiredString(input, "requestId", 36)),
            ["actionId"] = Protocol.RequiredString(input, "actionId", 256), ["kind"] = kind, ["target"] = target,
            ["assignSelf"] = OptionalBool(input, "assignSelf")
        };
        var expected = OptionalText(input, "expectedHeadSha", 40).ToLowerInvariant();
        if (isPr && !Sha(expected)) throw new ProtocolException("MISSING_HEAD_SHA", "Every PR action requires its complete 40-character expected HEAD SHA.", "Refresh the action and pin its current full commit SHA.");
        if (!isPr && expected.Length > 0) throw new ProtocolException("INVALID_REQUEST", "Only PR targets accept expectedHeadSha.");
        if (isPr) draft["expectedHeadSha"] = expected;
        var body = OptionalText(input, "body", 60000);
        if (kind == "trigger-ci")
        {
            if (body.Length > 0 && body != "/azp run") throw new ProtocolException("INVALID_REQUEST", "Trigger CI posts the fixed /azp run command only.");
            body = "/azp run";
        }
        if (kind is "merge-pr" or "create-pr" && body.Length > 0) throw new ProtocolException("INVALID_REQUEST", "This action does not accept a top-level comment body.");
        if (kind == "comment" && string.IsNullOrWhiteSpace(body)) throw new ProtocolException("BODY_REQUIRED", "A comment requires nonempty text.");
        if (kind == "close-as-duplicate")
        {
            if (string.IsNullOrWhiteSpace(body)) throw new ProtocolException("BODY_REQUIRED", "Explain why this issue duplicates the original issue.");
            if (Bool(draft, "assignSelf")) throw new ProtocolException("INVALID_REQUEST", "Closing as a duplicate does not include self-assignment.");
            draft["duplicateOf"] = NormalizeDuplicate(Protocol.RequireObject(input["duplicateOf"]), target);
        }
        else if (input["duplicateOf"] is not null) throw new ProtocolException("INVALID_REQUEST", "Only close-as-duplicate accepts an original issue reference.");
        if (body.Length > 0) draft["body"] = body;
        if (kind == "review")
        {
            var review = Protocol.RequireObject(input["review"]);
            Protocol.OnlyKeys(review, "event", "comments", "generalComments");
            var reviewEvent = Protocol.RequiredString(review, "event", 30);
            if (reviewEvent is not ("COMMENT" or "REQUEST_CHANGES")) throw new ProtocolException("INVALID_REQUEST", "Review event must be COMMENT or REQUEST_CHANGES.");
            var comments = Array(review, "comments");
            var general = Array(review, "generalComments");
            if (comments.Count + general.Count > 50) throw new ProtocolException("INPUT_TOO_LARGE", "Select at most 50 review comments per action.");
            var normalized = new JsonArray();
            foreach (var node in comments)
            {
                var item = Protocol.RequireObject(node);
                Protocol.OnlyKeys(item, "path", "line", "startLine", "side", "startSide", "body");
                var path = Protocol.RequiredString(item, "path", 1024);
                if (path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(segment => segment is "." or ".." or "") || path.Any(char.IsControl)) throw InvalidDiff();
                var side = Protocol.RequiredString(item, "side", 5);
                var line = Integer(item, "line");
                var start = item["startLine"] is null ? line : Integer(item, "startLine");
                var startSide = item["startSide"] is null ? side : Protocol.RequiredString(item, "startSide", 5);
                if (side is not ("LEFT" or "RIGHT") || startSide != side || start <= 0 || start > line || line - start > 1000) throw InvalidDiff();
                normalized.Add(new JsonObject { ["path"] = path, ["line"] = line, ["startLine"] = start, ["side"] = side, ["startSide"] = startSide, ["body"] = NonemptyBody(item) });
            }
            var notes = new JsonArray();
            foreach (var node in general)
            {
                var note = Protocol.RequireObject(node); Protocol.OnlyKeys(note, "body");
                notes.Add(new JsonObject { ["body"] = NonemptyBody(note) });
            }
            if (normalized.Count == 0 && notes.Count == 0 && string.IsNullOrWhiteSpace(body)) throw new ProtocolException("BODY_REQUIRED", "Select review findings or write a review body.");
            draft["review"] = new JsonObject { ["event"] = reviewEvent, ["comments"] = normalized, ["generalComments"] = notes };
        }
        else if (input["review"] is not null) throw new ProtocolException("INVALID_REQUEST", "Only review actions accept review fields.");
        if (kind == "create-pr")
        {
            var pr = Protocol.RequireObject(input["pullRequest"]);
            Protocol.OnlyKeys(pr, "head", "base", "title", "body", "draft", "sourceHeadSha");
            var head = Protocol.RequiredString(pr, "head", 240);
            var parts = head.Split(':');
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}\z") || !ValidBranch(parts[1])) throw new ProtocolException("INVALID_BRANCH", "The proposed PR head must be owner:branch using a supported Git branch name.");
            var baseBranch = Protocol.RequiredString(pr, "base", 200);
            if (!ValidBranch(baseBranch)) throw new ProtocolException("INVALID_BRANCH", "The proposed base branch is invalid.");
            draft["pullRequest"] = new JsonObject { ["head"] = parts[0].ToLowerInvariant() + ":" + parts[1], ["base"] = baseBranch,
                ["title"] = Protocol.RequiredString(pr, "title", 256), ["body"] = OptionalText(pr, "body", 60000), ["draft"] = OptionalBool(pr, "draft") };
            if (pr["sourceHeadSha"] is not null)
            {
                var sourceSha = Protocol.RequiredString(pr, "sourceHeadSha", 40).ToLowerInvariant();
                if (!Sha(sourceSha)) throw new ProtocolException("INVALID_REQUEST", "sourceHeadSha must be the full commit SHA validated for the proposed source branch.");
                draft["pullRequest"]!["sourceHeadSha"] = sourceSha;
            }
        }
        else if (input["pullRequest"] is not null) throw new ProtocolException("INVALID_REQUEST", "Only create-pr accepts pullRequest fields.");
        if (Bool(draft, "assignSelf") && kind is "merge-pr" or "trigger-ci") throw new ProtocolException("INVALID_REQUEST", "Merge and CI actions do not include self-assignment.");
        if (Text(target, "repository") == Fork && Bool(draft, "assignSelf"))
        {
            var assignment = NormalizeTarget(Protocol.RequireObject(input["assignmentTarget"]));
            if (Text(assignment, "repository") != Upstream) throw new ProtocolException("INVALID_TARGET", "Fork comments may self-assign only the original upstream target.");
            draft["assignmentTarget"] = assignment;
        }
        else if (input["assignmentTarget"] is not null) throw new ProtocolException("INVALID_REQUEST", "assignmentTarget is only valid for fork comments that request self-assignment.");
        var markers = DraftBodies(draft).SelectMany(Markers).ToList();
        if (markers.Distinct(StringComparer.Ordinal).Count() != markers.Count) throw new ProtocolException("DUPLICATE_COMMENT", "The selected draft repeats the same Pulse comment marker.");
        if (Encoding.UTF8.GetByteCount(draft.ToJsonString()) > 200 * 1024) throw new ProtocolException("INPUT_TOO_LARGE", "The GitHub action draft exceeds 200 KiB.");
        return draft;
    }

    private static JsonObject NormalizeTarget(JsonObject target)
    {
        Protocol.OnlyKeys(target, "repository", "type", "number");
        var repository = Protocol.RequiredString(target, "repository", 100).ToLowerInvariant();
        var type = Protocol.RequiredString(target, "type", 5);
        var number = Integer(target, "number");
        if (repository is not (Upstream or Fork) || type is not ("issue" or "pr") || number <= 0)
            throw new ProtocolException("INVALID_TARGET", "Choose an issue or PR in the supported PowerToys repositories.");
        return new JsonObject { ["repository"] = repository, ["type"] = type, ["number"] = number };
    }

    private static JsonObject NormalizeDuplicate(JsonObject input, JsonObject target)
    {
        Protocol.OnlyKeys(input, "repository", "number", "url");
        var repository = Protocol.RequiredString(input, "repository", 140).ToLowerInvariant();
        var number = Integer(input, "number");
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z") || repository.Split('/')[1] is "." or ".." || number <= 0)
            throw new ProtocolException("INVALID_DUPLICATE", "The original issue requires a valid GitHub repository and positive issue number.");
        if (repository == Text(target, "repository") && number == Integer(target, "number"))
            throw new ProtocolException("INVALID_DUPLICATE", "An issue cannot be a duplicate of itself.");
        var canonical = "https://github.com/" + repository + "/issues/" + number;
        var supplied = Protocol.RequiredString(input, "url", 300);
        if (!Uri.TryCreate(supplied, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || uri.Port != 443 ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !uri.AbsolutePath.Equals("/" + repository + "/issues/" + number, StringComparison.OrdinalIgnoreCase))
            throw new ProtocolException("INVALID_DUPLICATE", "The original issue URL must match its repository and issue number.");
        return new JsonObject { ["repository"] = repository, ["number"] = number, ["url"] = canonical };
    }

    private static JsonObject DuplicateTarget(JsonObject draft) => new()
    {
        ["repository"] = draft["duplicateOf"]!["repository"]!.DeepClone(), ["number"] = draft["duplicateOf"]!["number"]!.DeepClone(), ["type"] = "issue"
    };
    private static string DuplicateSuffix(JsonObject draft) => "\n\nDuplicate of " + Text(draft["duplicateOf"] as JsonObject, "url") +
        "\n\n<!-- powertoys-pulse:duplicate:" + Protocol.Hash(TargetUrl(draft["target"]!.AsObject()) + "\n" + Text(draft["duplicateOf"] as JsonObject, "url")) + " -->";
    private static string DuplicateCommentBody(JsonObject record) => Text(ExecutionDraft(record), "body") + DuplicateSuffix(record["draft"]!.AsObject());
    private static JsonObject ExecutionDraft(JsonObject record)
    {
        var draft = record["draft"]!.DeepClone().AsObject();
        if (record["confirmedBody"] is not null) draft["body"] = record["confirmedBody"]!.DeepClone();
        return draft;
    }
    private static bool Completed(JsonObject record, string step) => record["completedSteps"]!.AsArray().Any(node => node?.GetValue<string>() == step);
    private static bool CanResumeDuplicate(JsonObject record) => Text(record["draft"] as JsonObject, "kind") == "close-as-duplicate" &&
        Text(record, "status") == "partial" && Completed(record, "duplicate-comment") && !Completed(record, "close-issue") && Text(record, "activeStep").Length == 0;
    private static void RecordStepResult(JsonObject record, string step, JsonObject response, bool observed = false)
    {
        var results = record["stepResults"] as JsonObject;
        if (results is null) record["stepResults"] = results = new JsonObject();
        results[step] = new JsonObject { ["account"] = Text(record, "account"), ["url"] = ResultUrl(record["draft"]!.AsObject(), step, response), ["completedAt"] = Protocol.Now(), ["observed"] = observed };
    }

    private static void ValidateInline(JsonObject comment, JsonArray files)
    {
        var file = files.OfType<JsonObject>().SingleOrDefault(item => Text(item, "filename") == Text(comment, "path"));
        if (file is null) throw InvalidDiff();
        var locations = ParseDiffLocations(Text(file, "patch"));
        var side = Text(comment, "side");
        var first = Integer(comment, "startLine");
        var last = Integer(comment, "line");
        if (!locations.TryGetValue((side, first), out var hunk)) throw InvalidDiff();
        for (var line = first; line <= last; line++)
            if (!locations.TryGetValue((side, line), out var actual) || actual != hunk) throw InvalidDiff();
    }

    // Both LEFT deletions and RIGHT additions are supported, but only complete, contiguous hunks.
    private static Dictionary<(string Side, int Line), int> ParseDiffLocations(string patch)
    {
        var all = new Dictionary<(string, int), int>();
        var current = new List<(string, int)>();
        var oldRemaining = 0; var newRemaining = 0; var left = 0; var right = 0; var hunk = 0; var valid = false;
        void Finish()
        {
            if (valid && oldRemaining == 0 && newRemaining == 0) foreach (var location in current) all[location] = hunk;
            current.Clear();
        }
        foreach (var line in patch.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Finish(); hunk++;
                var match = Regex.Match(line, @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(?:.*)$");
                valid = match.Success && int.TryParse(match.Groups[1].Value, out left) && int.TryParse(match.Groups[3].Value, out right) &&
                    int.TryParse(match.Groups[2].Success ? match.Groups[2].Value : "1", out oldRemaining) && int.TryParse(match.Groups[4].Success ? match.Groups[4].Value : "1", out newRemaining);
                continue;
            }
            if (!valid || line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal)) continue;
            if (line.Length == 0) { if (oldRemaining != 0 || newRemaining != 0) valid = false; continue; }
            if (left == int.MaxValue || right == int.MaxValue) { valid = false; continue; }
            switch (line[0])
            {
                case ' ': current.Add(("LEFT", left++)); current.Add(("RIGHT", right++)); oldRemaining--; newRemaining--; break;
                case '-': current.Add(("LEFT", left++)); oldRemaining--; break;
                case '+': current.Add(("RIGHT", right++)); newRemaining--; break;
                default: valid = false; break;
            }
            if (oldRemaining < 0 || newRemaining < 0) valid = false;
        }
        Finish();
        return all;
    }

    private static JsonArray Steps(JsonObject draft)
    {
        var steps = new JsonArray();
        var kind = Text(draft, "kind");
        if (kind == "review")
        {
            var review = draft["review"]!.AsObject();
            if (Text(review, "event") == "REQUEST_CHANGES" || Text(draft, "body").Trim().Length > 0 || review["comments"]!.AsArray().Count > 0) steps.Add("review");
            for (var index = 1; index <= review["generalComments"]!.AsArray().Count; index++) steps.Add("general-comment-" + index);
        }
        else if (kind == "close-as-duplicate") { steps.Add("duplicate-comment"); steps.Add("close-issue"); }
        else steps.Add(kind == "approve" ? "review" : kind);
        if (Bool(draft, "assignSelf")) steps.Add("assign-self");
        return steps;
    }

    private static IEnumerable<string> DraftBodies(JsonObject draft)
    {
        yield return Text(draft, "body") + (Text(draft, "kind") == "close-as-duplicate" ? DuplicateSuffix(draft) : "");
        foreach (var item in (draft["review"]?["comments"] as JsonArray ?? []).OfType<JsonObject>()) yield return Text(item, "body");
        foreach (var item in (draft["review"]?["generalComments"] as JsonArray ?? []).OfType<JsonObject>()) yield return Text(item, "body");
    }
    private static IEnumerable<string> Markers(string body) => Regex.Matches(body, @"<!--\s*powertoys-pulse:[^\r\n]*?-->").Select(match => match.Value);
    private static bool ValidBranch(string branch) => branch.Length <= 200 && Regex.IsMatch(branch, @"\A[A-Za-z0-9_][A-Za-z0-9_./-]*\z") &&
        !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("//", StringComparison.Ordinal) && !branch.EndsWith('/') &&
        branch.Split('/').All(segment => !segment.StartsWith('.') && !segment.EndsWith('.') && !segment.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    private static string TargetEndpoint(JsonObject target) => "/repos/" + Text(target, "repository") + "/" + (Text(target, "type") == "pr" ? "pulls" : "issues") + "/" + Integer(target, "number");
    private static string IssueEndpoint(JsonObject target) => "/repos/" + Text(target, "repository") + "/issues/" + Integer(target, "number");
    private static string TargetUrl(JsonObject target) => "https://github.com/" + Text(target, "repository") + "/" + (Text(target, "type") == "pr" ? "pull" : "issues") + "/" + Integer(target, "number");
    private static void VerifyTarget(JsonObject target, JsonObject response)
    {
        if (Integer(response, "number") != Integer(target, "number") || Text(target, "type") == "issue" && response["pull_request"] is not null ||
            Text(target, "type") == "pr" && !Text(response["base"]?["repo"] as JsonObject, "full_name").Equals(Text(target, "repository"), StringComparison.OrdinalIgnoreCase)) throw TargetMismatch();
    }
    private static string ResultUrl(JsonObject draft, string step, JsonObject response)
    {
        var target = step == "assign-self" ? (draft["assignmentTarget"] ?? draft["target"])!.AsObject() : draft["target"]!.AsObject();
        var value = Text(response, "html_url");
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com" && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) &&
            uri.AbsolutePath.StartsWith("/" + Text(target, "repository") + "/", StringComparison.OrdinalIgnoreCase)) return value;
        return step == "create-pr" ? "https://github.com/" + Upstream + "/pull/" + Integer(response, "number") : TargetUrl(target);
    }
    private static bool SameTarget(JsonObject first, JsonObject? second) => second is not null && Text(first, "repository") == Text(second, "repository") && Integer(first, "number") == Integer(second, "number");
    private string PathFor(string id) => Path.Combine(store.Root, "web-actions", "operations", Protocol.RunId(id) + ".json");
    private JsonObject Read(string id) => store.ReadJson(PathFor(id)) ?? throw NotFound();
    private void Save(JsonObject record) { record["updatedAt"] = Protocol.Now(); store.WriteJson(PathFor(Text(record, "operationId")), record); }
    public static bool HasActive(Store store)
    {
        var directory = Path.Combine(store.Root, "web-actions", "operations");
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any(path => Text(store.ReadJson(path), "status") == "submitting");
    }
    private IEnumerable<JsonObject> Records()
    {
        var directory = Path.Combine(store.Root, "web-actions", "operations");
        return !Directory.Exists(directory) ? [] : Directory.EnumerateFiles(directory, "*.json").Select(path => store.ReadJson(path) ?? throw new ProtocolException("RECORD_UNREADABLE", "A saved GitHub action disappeared."));
    }
    internal JsonArray ListResult(string runId)
    {
        var summaries = new List<JsonObject>();
        foreach (var saved in ResultRecords(runId))
        {
            var id = Text(saved, "operationId");
            using var held = store.AcquireLock("web-action:" + id, LockTimeout);
            var record = Read(id);
            Recover(record);
            summaries.Add(Summary(record));
        }
        return new JsonArray(summaries.OrderByDescending(item => Text(item, "createdAt"), StringComparer.Ordinal).Select(item => (JsonNode)item).ToArray());
    }

    // Store.DeleteRun calls this while holding its own task/admission locks. Never acquire more locks here.
    internal bool HasUnresolvedResult(string runId) => ResultRecords(runId).Any(record => Text(record, "status") is "prepared" or "submitting" or "unknown" or "partial");
    private IEnumerable<JsonObject> ResultRecords(string runId) => Records().Where(record => Text(record, "sourceOrigin") == "pulse-task://" + runId &&
        Text(record["draft"] as JsonObject, "actionId").StartsWith(runId + ":", StringComparison.Ordinal));
    private void Recover(JsonObject record)
    {
        if (Text(record, "status") != "submitting") return;
        if (record["completedSteps"]!.AsArray().Count == record["steps"]!.AsArray().Count)
        {
            record["status"] = "succeeded";
            record["error"] = null;
            Save(record);
            return;
        }
        if (Text(record, "activeStep").Length == 0)
        {
            record["status"] = record["completedSteps"]!.AsArray().Count > 0 ? "partial" : "failed";
            record["error"] = new ProtocolException("OPERATION_INTERRUPTED", "The previous Host stopped between GitHub writes.", "Review the completed steps before preparing a new draft for the remaining work.").ToJson();
            Save(record);
            return;
        }
        record["status"] = "unknown";
        record["error"] = new ProtocolException("OPERATION_UNKNOWN", "The previous Host stopped before recording the final GitHub result.", "Check the recorded steps against GitHub. Do not resend this action.").ToJson();
        Save(record);
    }
    private static JsonObject Summary(JsonObject record)
    {
        var draft = record["draft"]!.AsObject();
        var completed = record["completedSteps"]!.AsArray();
        var result = new JsonObject
        {
            ["operationId"] = Text(record, "operationId"), ["requestId"] = Text(draft, "requestId"), ["actionId"] = Text(draft, "actionId"),
            ["kind"] = Text(draft, "kind"), ["target"] = draft["target"]!.DeepClone(), ["status"] = Text(record, "status"),
            ["completedSteps"] = completed.DeepClone(),
            ["remainingSteps"] = new JsonArray(record["steps"]!.AsArray().Where(step => !completed.Any(done => JsonNode.DeepEquals(done, step))).Select(step => step!.DeepClone()).ToArray()),
            ["urls"] = record["urls"]!.DeepClone(), ["createdAt"] = Text(record, "createdAt"), ["updatedAt"] = Text(record, "updatedAt")
        };
        if (record["error"] is not null) result["error"] = record["error"]!.DeepClone();
        if (record["account"] is not null) result["account"] = record["account"]!.DeepClone();
        if (record["stepResults"] is not null) result["stepResults"] = record["stepResults"]!.DeepClone();
        if (Text(draft, "kind") == "close-as-duplicate")
        {
            result["duplicateOf"] = draft["duplicateOf"]!.DeepClone();
            result["resumeRequired"] = CanResumeDuplicate(record);
        }
        const string prefix = "pulse-task://";
        var source = Text(record, "sourceOrigin");
        if (source.StartsWith(prefix, StringComparison.Ordinal))
        {
            var runId = source[prefix.Length..];
            var actionId = Text(draft, "actionId");
            if (actionId.StartsWith(runId + ":", StringComparison.Ordinal))
            {
                result["runId"] = runId;
                result["proposalId"] = actionId[(runId.Length + 1)..];
                if (record["attemptId"] is not null) result["attemptId"] = record["attemptId"]!.DeepClone();
                result["retryRequested"] = Bool(record, "retryRequested");
                if (record["resultContentFingerprint"] is not null) result["resultContentFingerprint"] = record["resultContentFingerprint"]!.DeepClone();
                var state = Text(record, "status");
                result["retryAllowed"] = state == "cancelled" || state == "failed" && completed.Count == 0 && Text(record, "activeStep").Length == 0 ||
                    state == "succeeded" && Text(draft, "kind") == "trigger-ci";
            }
        }
        return result;
    }
    private static string OperationId(JsonObject payload) { Protocol.OnlyKeys(payload, "operationId"); return Protocol.RunId(Protocol.RequiredString(payload, "operationId", 36)); }
    private static JsonArray Array(JsonObject value, string field) => value[field] is null ? [] : value[field] as JsonArray ?? throw new ProtocolException("INVALID_REQUEST", field + " must be an array.");
    private static string OptionalText(JsonObject value, string field, int limit)
    {
        if (value[field] is null) return "";
        if (value[field] is not JsonValue node || !node.TryGetValue<string>(out var text) || text.Length > limit || text.Contains('\0')) throw new ProtocolException("INVALID_REQUEST", field + " must be valid text within the action size limit.");
        return NormalizeText(text);
    }
    private static string NonemptyBody(JsonObject value) => OptionalText(value, "body", 60000) is { } body && !string.IsNullOrWhiteSpace(body) ? body : throw new ProtocolException("BODY_REQUIRED", "Each selected comment requires nonempty text.");
    private static bool OptionalBool(JsonObject value, string field) => value[field] is null ? false : value[field] is JsonValue node && node.TryGetValue<bool>(out var result) ? result : throw new ProtocolException("INVALID_REQUEST", field + " must be a boolean.");
    private static bool Bool(JsonObject? value, string field) => value?[field] is JsonValue node && node.TryGetValue<bool>(out var result) && result;
    private static string Text(JsonObject? value, string field) => value?[field] is JsonValue node && node.TryGetValue<string>(out var result) ? result : "";
    private static int Integer(JsonObject value, string field) => value[field] is JsonValue node && node.TryGetValue<int>(out var result) ? result : 0;
    private static long Long(JsonObject value, string field) => value[field] is JsonValue node && node.TryGetValue<long>(out var result) ? result : 0;
    private static bool Sha(string value) => Regex.IsMatch(value, @"\A[0-9a-fA-F]{40}\z");
    private static string NormalizeText(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
    private static ProtocolException NotFound() => new("OPERATION_NOT_FOUND", "This GitHub action was not found for the requesting origin.");
    private static ProtocolException TargetMismatch() => new("TARGET_MISMATCH", "GitHub returned a target different from the confirmed action.");
    private static ProtocolException InvalidDiff() => new("INVALID_DIFF_LOCATION", "A selected review range is outside a complete, contiguous diff hunk on its selected side.", "Refresh and re-analyze the PR diff, or deliberately move the finding to a general comment.");
    private static JsonObject Describe(Exception error) => error is ProtocolException protocol ? protocol.ToJson() : error is GitHubRequestException github
        ? new JsonObject { ["code"] = github.Uncertain ? "OPERATION_UNKNOWN" : "GITHUB_REQUEST_FAILED", ["message"] = github.Message, ["guidance"] = github.Guidance, ["httpStatus"] = github.StatusCode }
        : new ProtocolException("GITHUB_ERROR", "The local GitHub action could not be completed.", "Inspect the saved action and GitHub before preparing a new draft.").ToJson();
}
