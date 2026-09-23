using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pulse.Host;

/// <summary>Explicit user-selected operations, independent of the local CLI lifecycle.</summary>
public sealed class GitHubService(Store store, Func<JsonObject, Task<GitHubSession>>? sessionFactory = null)
{
    private readonly Func<JsonObject, Task<GitHubSession>> openSession = sessionFactory ?? GitHubSession.OpenAsync;
    private static readonly HashSet<string> Kinds = ["approve", "requestChanges", "suggestChanges", "comment", "close"];

    public async Task<JsonObject> GetIdentityAsync(JsonObject config)
    {
        try
        {
            using var session = await openSession(config);
            return new JsonObject { ["available"] = true, ["account"] = session.Account, ["host"] = "github.com" };
        }
        catch (Exception ex) when (ex is ProtocolException or GitHubRequestException)
        {
            return new JsonObject { ["available"] = false, ["account"] = null, ["host"] = "github.com", ["error"] = Describe(ex) };
        }
    }

    public async Task VerifyTaskContextAsync(JsonObject task, JsonObject config)
    {
        if (task["target"] is null) return;
        var target = ParseTarget(task);
        if (target.Type != "pr") return;
        var expected = Text(task, "expectedHeadSha");
        if (Text(task, "actionKind") is "pr-review" or "e2e" && expected.Length == 0)
            throw new ProtocolException("MISSING_HEAD_SHA", "PR review and E2E tasks require an expected PR HEAD SHA.", "Refresh the action in Pulse and send the current PR revision.");
        if (expected.Length == 0) return;
        ValidateSha(expected);
        JsonObject current;
        try
        {
            using var session = await openSession(config);
            current = (await session.RequestAsync(HttpMethod.Get, target.Endpoint)).AsObject();
        }
        catch (GitHubRequestException ex) { throw PublicReadError(ex); }
        VerifyTargetResponse(target, current);
        EnsureSha(expected, Text(current["head"] as JsonObject, "sha"));
    }

    public async Task<JsonObject> GetTargetAsync(JsonObject payload, JsonObject config)
    {
        Protocol.OnlyKeys(payload, "target");
        var raw = Protocol.RequireObject(payload["target"]);
        Protocol.OnlyKeys(raw, "type", "number");
        if (Protocol.RequiredString(raw, "type") != "pr" || raw["number"] is not JsonValue number || !number.TryGetValue<int>(out var n) || n <= 0)
            throw new ProtocolException("INVALID_REQUEST", "Target lookup requires a pull request and a positive integer number.");
        var target = new Target(Configuration.PowerToysRepository, "pr", n);
        JsonObject current;
        try
        {
            using var session = await openSession(config);
            current = (await session.RequestAsync(HttpMethod.Get, target.Endpoint)).AsObject();
        }
        catch (GitHubRequestException error) { throw PublicReadError(error); }
        VerifyTargetResponse(target, current);
        var sha = Text(current["head"] as JsonObject, "sha");
        if (!Regex.IsMatch(sha, @"\A[a-fA-F0-9]{40}\z"))
            throw new ProtocolException("INVALID_GITHUB_RESPONSE", "GitHub did not return a complete PR HEAD SHA.", "Refresh the pull request and retry the action.");
        var title = new string(Text(current, "title").Where(character => !char.IsControl(character)).Take(256).ToArray());
        return new JsonObject
        {
            ["target"] = new JsonObject { ["type"] = "pr", ["number"] = n },
            ["headSha"] = sha.ToLowerInvariant(), ["title"] = title
        };
    }

    public async Task<JsonObject> PreviewAsync(string runId)
    {
        runId = Protocol.RunId(runId);
        var saved = store.ReadTask(runId);
        var task = saved["task"]!.AsObject();
        var config = new Configuration(store).Read();
        try
        {
            using var session = await openSession(config);
            var context = await ReadContextAsync(runId, task, session, includeFiles: true);
            LimitPreviewFiles(context);
            return context;
        }
        catch (GitHubRequestException ex) { throw PublicReadError(ex); }
    }

    public JsonObject List(string runId)
    {
        runId = Protocol.RunId(runId);
        store.ReadTask(runId);
        using var operationLock = store.AcquireLock("operations:" + runId, TimeSpan.FromSeconds(90));
        var operations = ReadOperations(runId);
        foreach (var operation in operations)
        {
            // A live submit owns this file lock until its outcome is persisted.
            if (Text(operation, "status") == "submitting") MarkUnknown(runId, operation);
            else if (Text(operation, "status") == "prepared") MarkNotSubmitted(runId, operation);
        }
        var visible = new JsonArray();
        var bytes = 0;
        var ordered = operations.OrderByDescending(op => Text(op, "status") == "unknown").ThenByDescending(op => Text(op, "createdAt"));
        foreach (var operation in ordered)
        {
            var summary = PublicOperation(operation);
            var fullBody = Text(summary, "body");
            if (fullBody.Length > 4000)
            {
                summary["body"] = fullBody[..4000];
                summary["bodyTruncated"] = true;
            }
            summary["suggestionCount"] = Suggestions(summary).Count;
            summary.Remove("suggestions");
            bytes += Encoding.UTF8.GetByteCount(summary.ToJsonString());
            if (visible.Count >= 100 || bytes > 600000) break;
            visible.Add(summary);
        }
        return new JsonObject { ["operations"] = visible, ["truncated"] = visible.Count < operations.Count, ["totalCount"] = operations.Count };
    }

    public async Task<JsonObject> SubmitAsync(JsonObject payload)
    {
        RejectUnknownFields(payload, ["runId", "operationId", "kind", "body", "expectedHeadSha", "expectedAccount", "suggestions", "proposalId", "findingIds", "closeReason"]);
        var runId = Protocol.RunId(RequiredText(payload, "runId", 128));
        var operationId = RequiredText(payload, "operationId", 128).ToLowerInvariant();
        ValidateOperationId(operationId);
        var saved = store.ReadTask(runId);
        var task = saved["task"]!.AsObject();
        var draft = NormalizeDraft(payload, task);
        var proposalId = OptionalText(payload, "proposalId", 128);
        var sourceResult = proposalId.Length > 0 || payload.ContainsKey("findingIds") ? store.ReadResult(runId) : null;
        var findingIds = ValidateSelectedFindings(payload, draft, sourceResult, task);
        if (proposalId.Length > 0)
        {
            var savedResult = sourceResult;
            var proposal = savedResult is null ? null : WorkflowResult.FindProposal(savedResult, proposalId);
            if (proposal is null || Text(proposal, "kind") != Text(draft, "kind"))
                throw new ProtocolException("RESULT_PROPOSAL_NOT_FOUND", "This action does not match the selected proposal in the saved result.", "Refresh the task and select its exact proposal again.");
            ValidateProposalSuggestions(draft, savedResult!, proposal);
        }
        // Suggestion identities bind the selected local proposal, not the GitHub write.
        // Keep payload-equivalent writes deduplicated across compatible proposal records.
        foreach (var suggestion in Suggestions(draft).OfType<JsonObject>()) suggestion.Remove("id");
        // Association is metadata. Identical confirmed write content must still deduplicate
        // when it was reached from two equivalent proposals or an older extension client.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draft.ToJsonString()))).ToLowerInvariant();
        using var operationLock = store.AcquireLock("operations:" + runId, TimeSpan.FromSeconds(90));
        store.ReadTask(runId); // A deletion may have completed while waiting for the operation lock.
        var existing = ReadOperations(runId);
        foreach (var operation in existing)
            if (Text(operation, "status") == "submitting") MarkUnknown(runId, operation);

        var sameId = existing.SingleOrDefault(op => Text(op, "operationId").Equals(operationId, StringComparison.OrdinalIgnoreCase));
        if (sameId is not null && Text(sameId, "fingerprint") != fingerprint)
            throw new ProtocolException("OPERATION_ID_CONFLICT", "This operation ID already belongs to different content.", "Refresh the operation record before making a new selection.");
        var duplicate = sameId ?? existing.FirstOrDefault(op => Text(op, "fingerprint") == fingerprint && Text(op, "status") != "failed");
        if (duplicate is not null && Text(duplicate, "status") != "prepared")
        {
            if (Text(duplicate, "status") == "unknown") return await ReconcileLockedAsync(runId, duplicate, task);
            return PublicOperation(duplicate);
        }
        // An unresolved write may overlap this draft, even if its text was edited meanwhile.
        if (existing.Any(op => Text(op, "status") == "unknown" && !ReferenceEquals(op, duplicate)))
            throw new ProtocolException("OPERATION_UNKNOWN", "This task has an unconfirmed GitHub submission.", "Check that operation against GitHub before submitting another draft.");

        if (!ActionAllowed(task, Text(draft, "kind")))
            throw Text(task["target"] as JsonObject, "type") == "pr"
                ? new ProtocolException("CONFIRMED_P0", "This original PR revision has a confirmed unresolved P0 finding.", "Resolve the current P0 before approving this revision. Other GitHub actions remain available.")
                : new ProtocolException("INVALID_OPERATION", "This operation is not available for the selected target.");

        var record = duplicate ?? draft.DeepClone().AsObject();
        using (store.AcquireLock("accept"))
        {
            store.EnsureAvailable();
            if (duplicate is null)
            {
                record["runId"] = runId;
                record["operationId"] = operationId;
                if (proposalId.Length > 0) record["proposalId"] = proposalId;
                if (findingIds is not null) record["findingIds"] = findingIds.DeepClone();
                record["fingerprint"] = fingerprint;
                record["status"] = "prepared";
                record["createdAt"] = Now();
                Save(runId, record);
            }
        }
        var writeStarted = false;
        try
        {
            using var session = await openSession(new Configuration(store).Read());
            if (!session.Account.Equals(Text(record, "expectedAccount"), StringComparison.OrdinalIgnoreCase))
                throw new ProtocolException("GITHUB_IDENTITY_MISMATCH", "The active GitHub account differs from the account shown in the selected preview.", "Refresh the preview and review the actual account before submitting again.");
            var context = await ReadContextAsync(runId, task, session, includeFiles: Suggestions(record).Count > 0);
            record["account"] = session.Account;
            record["target"] = context["target"]!.DeepClone();
            record["url"] = context["url"]!.DeepClone();
            var kind = Text(record, "kind");
            if (kind == "close" && Text(context, "state") == "closed")
            {
                record["alreadyClosed"] = true;
                Succeed(runId, record, null, Text(context, "url"), "closed");
                return PublicOperation(record);
            }
            ValidatePermission(kind, context, Suggestions(record).Count > 0);
            if (Suggestions(record).Count > 0 && !Bool(context, "filesComplete"))
                throw new ProtocolException("INVALID_DIFF_LOCATION", "The complete PR diff could not be established.", "Refresh the PR before selecting line suggestions.");
            var target = ParseTarget(task);
            var files = context["files"]!.AsArray();
            var enriched = record.DeepClone().AsObject();
            foreach (var item in Suggestions(enriched).OfType<JsonObject>())
            {
                item["original"] = GitHubDiff.ValidateSuggestion(item, files);
                if (Encoding.UTF8.GetByteCount(PublicOperation(enriched).ToJsonString()) > 650000)
                    throw new ProtocolException("OPERATION_TOO_LARGE", "The selected source evidence exceeds the operation size limit.", "Select fewer or smaller line suggestions and submit a new review.");
            }
            record["suggestions"] = enriched["suggestions"]!.DeepClone();
            // Validate every complete public intent, including human decisions with large
            // disclosures, before the one remote write. Framing failure must not first
            // be discovered after GitHub has already accepted a review.
            if (Encoding.UTF8.GetByteCount(PublicOperation(record).ToJsonString(Protocol.JsonOptions)) > 650000)
                throw new ProtocolException("OPERATION_TOO_LARGE", "The confirmed operation exceeds the response size limit.", "Shorten the editable body or select fewer suggestions. Validation disclosures remain part of the saved evidence.");
            var baseline = kind == "close" ? new JsonArray() : await session.ReadPagesAsync(CollectionEndpoint(target, kind, Suggestions(record).Count > 0));
            record["baselineIds"] = new JsonArray(baseline.OfType<JsonObject>().Select(row => (JsonNode?)JsonValue.Create(RemoteId(row))).ToArray());
            // Re-read after pagination/diff work. Reviews are also explicitly bound to commit_id.
            var latest = (await session.RequestAsync(HttpMethod.Get, target.Endpoint)).AsObject();
            VerifyTargetResponse(target, latest);
            var expected = Text(record, "expectedHeadSha");
            if (target.Type == "pr" && expected.Length > 0) EnsureSha(expected, Text(latest["head"] as JsonObject, "sha"));
            if (Text(latest, "state") != Text(context, "state") || Bool(latest, "locked") != Bool(context, "locked"))
                throw new ProtocolException("TARGET_CHANGED", "The GitHub target state changed during preparation.", "Refresh the target and choose the operation again.");

            // A compatible verification run can finish while GitHub reads are in flight.
            // Re-evaluate its evidence immediately before committing a new remote write.
            if (!ActionAllowed(task, kind))
                throw new ProtocolException("CONFIRMED_P0", "This original PR revision now has a confirmed unresolved P0 finding.", "Refresh the current findings before confirming another review.");

            var (method, endpoint, body) = BuildRequest(target, record);
            record["status"] = "submitting";
            record["submittedAt"] = Now();
            Save(runId, record); // Durable intent MUST precede the single write request.
            writeStarted = true;
            var response = (await session.RequestAsync(method, endpoint, body)).AsObject();
            if (kind == "close")
            {
                VerifyTargetResponse(target, response);
                if (Text(response, "state") != "closed") throw new GitHubRequestException(0, true, "GitHub did not confirm the closed state.", "Check this operation against the target on GitHub.");
                Succeed(runId, record, RemoteId(response), target.Url, "closed");
            }
            else
            {
                var id = RemoteId(response);
                if (!Regex.IsMatch(id, "^[0-9]{1,30}$") ||
                    (UsesReview(record) && (Text(response, "state") != ReviewState(kind) || !Text(response, "commit_id").Equals(expected, StringComparison.OrdinalIgnoreCase))))
                    throw new GitHubRequestException(0, true, "GitHub did not confirm the expected submission ID, review event, and commit.", "Check this operation against the target on GitHub.");
                Succeed(runId, record, id, SafeResultUrl(Text(response, "html_url"), target.Url), Text(response, "state"));
            }
        }
        catch (Exception ex) when (ex is ProtocolException or GitHubRequestException)
        {
            // Once the request may have reached GitHub, only reconciliation may determine its result.
            record["status"] = ex is GitHubRequestException { Uncertain: true } || (writeStarted && ex is ProtocolException) ? "unknown" : "failed";
            record["error"] = Describe(ex);
            record["updatedAt"] = Now();
            Save(runId, record);
        }
        return PublicOperation(record);
    }

    public async Task<JsonObject> ReconcileAsync(JsonObject payload)
    {
        RejectUnknownFields(payload, ["runId", "operationId"]);
        var runId = Protocol.RunId(RequiredText(payload, "runId", 128));
        var operationId = RequiredText(payload, "operationId", 128).ToLowerInvariant();
        ValidateOperationId(operationId);
        var saved = store.ReadTask(runId);
        using var operationLock = store.AcquireLock("operations:" + runId, TimeSpan.FromSeconds(90));
        var record = store.ReadJson(OperationPath(runId, operationId)) ?? throw new ProtocolException("OPERATION_NOT_FOUND", "The saved GitHub operation does not exist.");
        if (Text(record, "status") == "submitting") MarkUnknown(runId, record);
        else if (Text(record, "status") == "prepared") MarkNotSubmitted(runId, record);
        return await ReconcileLockedAsync(runId, record, saved["task"]!.AsObject());
    }

    private async Task<JsonObject> ReconcileLockedAsync(string runId, JsonObject record, JsonObject task)
    {
        if (Text(record, "status") != "unknown") return PublicOperation(record);
        try
        {
            // Uncertain writes keep their confirmed account even after Settings changes.
            // Selecting this session does not switch gh's globally active account.
            var account = Text(record, "account");
            if (account.Length == 0) account = Text(record, "expectedAccount");
            var config = new Configuration(store).Read();
            config["githubAccount"] = account;
            using var session = await openSession(config);
            if (account.Length == 0 || !session.Account.Equals(account, StringComparison.OrdinalIgnoreCase))
                throw new ProtocolException("GITHUB_IDENTITY_MISMATCH", "The active GitHub account differs from the submission account.", "Select the original account and check this operation again.");
            record["account"] = account;
            var target = ParseTarget(task);
            var kind = Text(record, "kind");
            if (kind == "close")
            {
                var current = (await session.RequestAsync(HttpMethod.Get, target.Endpoint)).AsObject();
                VerifyTargetResponse(target, current);
                if (Text(current, "state") == "closed")
                {
                    record["reconciled"] = true;
                    record["confirmation"] = "Target is currently closed; the actor of the original close request is not asserted.";
                    Succeed(runId, record, RemoteId(current), target.Url, "closed");
                    return PublicOperation(record);
                }
            }
            else
            {
                var before = (record["baselineIds"] as JsonArray ?? []).Select(node => node?.GetValue<string>() ?? "").ToHashSet(StringComparer.Ordinal);
                var candidates = await session.ReadPagesAsync(CollectionEndpoint(target, kind, Suggestions(record).Count > 0));
                var matches = new List<JsonObject>();
                foreach (var candidate in candidates.OfType<JsonObject>())
                {
                    if (before.Contains(RemoteId(candidate)) ||
                        !Text(candidate["user"] as JsonObject, "login").Equals(Text(record, "account"), StringComparison.OrdinalIgnoreCase) ||
                        NormalizeNewlines(Text(candidate, "body")) != Text(record, "body")) continue;
                    if (UsesReview(record))
                    {
                        if (!Text(candidate, "commit_id").Equals(Text(record, "expectedHeadSha"), StringComparison.OrdinalIgnoreCase) ||
                            Text(candidate, "state") != ReviewState(kind)) continue;
                        var comments = await session.ReadPagesAsync($"{target.Endpoint}/reviews/{VerifiedRemoteId(candidate)}/comments");
                        if (!SameSuggestions(Suggestions(record), comments)) continue;
                    }
                    matches.Add(candidate);
                }
                if (matches.Count == 1)
                {
                    var match = matches[0];
                    record["reconciled"] = true;
                    Succeed(runId, record, RemoteId(match), SafeResultUrl(Text(match, "html_url"), target.Url), Text(match, "state"));
                    return PublicOperation(record);
                }
            }
            record["error"] = new JsonObject
            {
                ["code"] = "OPERATION_UNKNOWN",
                ["message"] = "GitHub did not provide a unique match for this submission. It has not been retried.",
                ["guidance"] = "Inspect the target on GitHub and check again. A missing, edited, dismissed, or deleted result does not prove that the original write failed."
            };
        }
        catch (Exception ex) when (ex is ProtocolException or GitHubRequestException)
        {
            record["error"] = Describe(ex);
        }
        record["checkedAt"] = Now();
        Save(runId, record);
        return PublicOperation(record);
    }

    private async Task<JsonObject> ReadContextAsync(string runId, JsonObject task, GitHubSession session, bool includeFiles)
    {
        var target = ParseTarget(task);
        var repository = (await session.RequestAsync(HttpMethod.Get, $"/repos/{target.Repository}")).AsObject();
        var current = (await session.RequestAsync(HttpMethod.Get, target.Endpoint)).AsObject();
        VerifyTargetResponse(target, current);
        var headSha = target.Type == "pr" ? Text(current["head"] as JsonObject, "sha") : "";
        var expected = Text(task, "expectedHeadSha").ToLowerInvariant();
        var stale = target.Type == "pr" && expected.Length > 0 && !expected.Equals(headSha, StringComparison.OrdinalIgnoreCase);
        var authenticated = session.Account.Length > 0;
        var permissions = repository["permissions"] as JsonObject;
        var read = authenticated && (Bool(permissions, "pull") || !Bool(repository, "private"));
        var write = Bool(permissions, "push") || Bool(permissions, "maintain") || Bool(permissions, "admin");
        var triage = write || Bool(permissions, "triage");
        var author = authenticated && session.Account.Equals(Text(current["user"] as JsonObject, "login"), StringComparison.OrdinalIgnoreCase);
        var open = Text(current, "state") == "open";
        var archived = Bool(repository, "archived") || Bool(repository, "disabled");
        var locked = Bool(current, "locked");
        var isPr = target.Type == "pr";
        var usable = read && !archived && (!locked || write) && !stale;
        var review = usable && target.Type == "pr" && open && expected.Length > 0;
        var confirmedP0 = isPr && PrActionPolicy.HasConfirmedP0(store, task);
        bool CanChoose(string kind) => isPr ? kind != "approve" || !confirmedP0 : kind is "comment" or "close";
        var reasons = new JsonArray();
        if (!authenticated) reasons.Add("Sign in with GitHub CLI to submit operations.");
        if (confirmedP0) reasons.Add("Approve is unavailable because this original PR revision has a confirmed unresolved P0 finding.");
        if (archived) reasons.Add("The repository is archived or disabled.");
        if (locked && !write) reasons.Add("The conversation is locked and this account cannot write to it.");
        if (stale) reasons.Add("The PR HEAD changed. Re-analyze the current revision before submitting this draft.");
        if (target.Type == "pr" && expected.Length == 0) reasons.Add("This task has no reviewed SHA; PR review operations require a new analysis tied to a SHA.");
        if (author && target.Type == "pr") reasons.Add("GitHub does not allow approving or requesting changes on your own pull request.");
        if (!open) reasons.Add("The target is already closed; review and close operations are unavailable.");
        var files = new JsonArray();
        var filesComplete = true;
        if (includeFiles && target.Type == "pr" && !stale)
        {
            var count = Integer(current, "changed_files", 0);
            if (count > 3000)
            {
                filesComplete = false;
                reasons.Add("This PR exceeds GitHub's 3,000-file limit. Line suggestions are unavailable.");
            }
            else
            {
                var received = 0;
                for (var page = 1; page <= Math.Max(1, (count + 99) / 100); page++)
                {
                    var rows = (await session.RequestAsync(HttpMethod.Get, $"{target.Endpoint}/files?per_page=100&page={page}")).AsArray();
                    foreach (var row in rows.OfType<JsonObject>())
                    {
                        received++;
                        files.Add(new JsonObject { ["path"] = Text(row, "filename"), ["status"] = Text(row, "status"), ["lines"] = GitHubDiff.ParseLines(Text(row, "patch")) });
                    }
                }
                filesComplete = count == received;
                if (!filesComplete) reasons.Add("The complete PR diff could not be established. Refresh before selecting line suggestions.");
            }
            var refreshed = (await session.RequestAsync(HttpMethod.Get, target.Endpoint)).AsObject();
            EnsureSha(headSha, Text(refreshed["head"] as JsonObject, "sha"));
        }
        return new JsonObject
        {
            ["account"] = authenticated ? session.Account : null,
            ["authenticated"] = authenticated,
            ["target"] = new JsonObject { ["type"] = target.Type, ["number"] = target.Number, ["repository"] = target.Repository, ["title"] = Text(current, "title"), ["url"] = target.Url, ["state"] = Text(current, "state") },
            ["url"] = target.Url,
            ["state"] = Text(current, "state"),
            ["headSha"] = headSha.Length > 0 ? headSha : null,
            ["expectedHeadSha"] = expected.Length > 0 ? expected : null,
            ["stale"] = stale,
            ["locked"] = locked,
            ["draft"] = Bool(current, "draft"),
            ["canApprove"] = review && !author && CanChoose("approve"),
            ["canRequestChanges"] = review && !author && CanChoose("requestChanges"),
            ["canSuggestChanges"] = review && filesComplete && (!includeFiles || files.OfType<JsonObject>().Any(file => file["lines"]!.AsArray().Count > 0)) && CanChoose("suggestChanges"),
            ["canComment"] = usable && CanChoose("comment"),
            ["canClose"] = usable && open && (triage || author) && CanChoose("close"),
            ["hasConfirmedP0"] = confirmedP0,
            ["filesComplete"] = filesComplete,
            ["files"] = files,
            ["reasons"] = reasons
        };
    }

    private bool ActionAllowed(JsonObject task, string kind)
    {
        if (Text(task["target"] as JsonObject, "type") == "pr")
            // The aggregate includes this run and applies Host provenance to verification-only
            // findings. Do not also apply an unqualified raw-result P0 check to the current run.
            return WorkflowResult.CanPublishManualPr(null, task, kind) && (kind != "approve" || !PrActionPolicy.HasConfirmedP0(store, task));
        return Text(task["target"] as JsonObject, "type") == "issue" && (kind is "comment" or "close");
    }

    private static JsonArray? ValidateSelectedFindings(JsonObject payload, JsonObject draft, JsonObject? result, JsonObject task)
    {
        if (!payload.ContainsKey("findingIds")) return null;
        if (payload["findingIds"] is not JsonArray selected || selected.Any(node => node is not JsonValue value || !value.TryGetValue<string>(out var id) ||
            !Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")))
            throw new ProtocolException("INVALID_REQUEST", "findingIds must contain saved finding IDs.");
        var ids = selected.Select(node => node!.GetValue<string>()).ToArray();
        if (Text(draft, "kind") == "close" && ids.Length > 0)
            throw new ProtocolException("INVALID_OPERATION", "Closing alone cannot submit selected finding feedback.", "Post the selected feedback first or explicitly clear the selection before closing.");
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ProtocolException("INVALID_REQUEST", "Each finding may be selected only once.");
        if (ids.Length > 0 && (result is null || !WorkflowResult.IsValidStoredV3(result)))
            throw new ProtocolException("RESULT_FINDING_NOT_FOUND", "The saved task does not contain these version 3 findings.", "Refresh the task and select its current saved findings again.");
        var findings = (result?["findings"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var permittedSuggestions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var matches = findings.Where(row => Text(row, "id") == id && Bool(row, "confirmed")).ToArray();
            if (matches.Length != 1) throw new ProtocolException("RESULT_FINDING_NOT_FOUND", "A selected finding is not a confirmed finding in the saved result.", "Refresh the task and select its current saved findings again.");
            var suggestionId = Text(matches[0]["feedback"] as JsonObject, "suggestionId");
            if (suggestionId.Length > 0) permittedSuggestions.Add(suggestionId);
        }
        var sourceReview = result?["review"] as JsonObject;
        var savedSuggestions = (sourceReview?["suggestions"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        foreach (var suggestion in Suggestions(draft).OfType<JsonObject>())
        {
            var id = Text(suggestion, "id");
            var matches = savedSuggestions.Where(row => Text(row, "id") == id).ToArray();
            if (!permittedSuggestions.Contains(id) || matches.Length != 1 || !SameSuggestionLocation(matches[0], suggestion) ||
                !Text(sourceReview, "headSha").Equals(Text(task, "expectedHeadSha"), StringComparison.OrdinalIgnoreCase))
                throw new ProtocolException("INVALID_SUGGESTION", "A selected code suggestion is not bound to the selected findings at this PR revision.", "Select only code suggestions linked to the selected saved findings; their explanations and replacement text remain editable.");
        }
        return selected.DeepClone().AsArray();
    }

    private static void ValidateProposalSuggestions(JsonObject draft, JsonObject result, JsonObject proposal)
    {
        if (Suggestions(draft).Count == 0) return;
        var saved = ((result["review"] as JsonObject)?["suggestions"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        HashSet<string>? allowed = null;
        if (proposal.ContainsKey("suggestionIds"))
        {
            if (proposal["suggestionIds"] is not JsonArray ids || ids.Any(node => node is not JsonValue value || !value.TryGetValue<string>(out _)))
                throw new ProtocolException("INVALID_SUGGESTION", "The selected proposal has invalid suggestion references.", "Refresh the task result before selecting its review draft.");
            allowed = ids.Select(node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        }
        foreach (var selected in Suggestions(draft).OfType<JsonObject>())
        {
            var id = Text(selected, "id");
            var candidates = saved.Where(suggestion => id.Length > 0
                ? Text(suggestion, "id") == id
                : SameSuggestionLocation(suggestion, selected)).ToList();
            if (candidates.Count != 1 || !SameSuggestionLocation(candidates[0], selected) ||
                allowed is not null && !allowed.Contains(Text(candidates[0], "id")))
                throw new ProtocolException("INVALID_SUGGESTION", "A selected suggestion does not belong to this proposal at its saved source location.", "Select the proposal's own suggestions again. You may edit their explanations and replacement text, but not their source ranges.");
        }
    }

    private static bool SameSuggestionLocation(JsonObject saved, JsonObject selected) =>
        Text(saved, "path") == Text(selected, "path") && Text(saved, "side") == Text(selected, "side") &&
        Integer(saved, "line", 0) == Integer(selected, "line", 0) &&
        Integer(saved, "startLine", Integer(saved, "line", 0)) == Integer(selected, "startLine", Integer(selected, "line", 0));

    private static JsonObject NormalizeDraft(JsonObject payload, JsonObject task)
    {
        var target = ParseTarget(task);
        var kind = RequiredText(payload, "kind", 30);
        var expectedAccount = RequiredText(payload, "expectedAccount", 100).ToLowerInvariant();
        if (!Regex.IsMatch(expectedAccount, @"\A[a-z0-9][a-z0-9_-]{0,99}\z"))
            throw new ProtocolException("INVALID_REQUEST", "expectedAccount must identify the GitHub account shown in the operation preview.");
        if (!Kinds.Contains(kind)) throw new ProtocolException("INVALID_OPERATION", "The selected GitHub operation is not supported.");
        var body = NormalizeNewlines(OptionalText(payload, "body", 60000));
        if (string.IsNullOrWhiteSpace(body) && (kind is "comment" or "requestChanges" or "suggestChanges") && payload["suggestions"] is not JsonArray { Count: > 0 })
            throw new ProtocolException("BODY_REQUIRED", "This operation requires a non-empty body.", "Write and review the full comment or review text before submitting.");
        if (kind == "close" && body.Length > 0)
            throw new ProtocolException("INVALID_OPERATION", "Close does not publish an additional comment.", "Clear the body to close the target, or explicitly select comment as a separate operation.");
        var closeReason = OptionalText(payload, "closeReason", 4000);
        if (kind == "close" && string.IsNullOrWhiteSpace(closeReason))
            throw new ProtocolException("CLOSE_REASON_REQUIRED", "An explicit reason is required to close this target.", "Review and record the closing reason. Closing alone does not publish a comment.");
        if (kind != "close" && payload.ContainsKey("closeReason"))
            throw new ProtocolException("INVALID_REQUEST", "closeReason belongs only to the close operation.");
        var expected = Text(task, "expectedHeadSha").ToLowerInvariant();
        var provided = OptionalText(payload, "expectedHeadSha", 64).ToLowerInvariant();
        if (provided.Length > 0 && provided != expected)
            throw new ProtocolException("STALE_CONTEXT", "The selected SHA differs from this task's recorded revision.", "Re-analyze the intended PR revision in a new task.");
        if (kind is "approve" or "requestChanges" or "suggestChanges" || kind == "comment" && payload["suggestions"] is JsonArray { Count: > 0 })
        {
            if (target.Type != "pr") throw new ProtocolException("INVALID_OPERATION", "PR reviews require a saved pull request target.");
            ValidateSha(expected);
        }
        var suggestions = new JsonArray();
        if (payload["suggestions"] is not null && payload["suggestions"] is not JsonArray)
            throw new ProtocolException("INVALID_SUGGESTION", "Suggestions must be an array.");
        var supplied = payload["suggestions"] as JsonArray ?? [];
        if (supplied.Count > 100) throw new ProtocolException("INVALID_SUGGESTION", "At most 100 selected suggestions can be submitted in one review.");
        if (supplied.Count > 0 && kind is not ("approve" or "suggestChanges" or "requestChanges" or "comment"))
            throw new ProtocolException("INVALID_OPERATION", "Only a PR review decision can contain line suggestions.");
        foreach (var node in supplied)
        {
            if (node is not JsonObject suggestion) throw new ProtocolException("INVALID_SUGGESTION", "Each suggestion must be an object.");
            RejectUnknownFields(suggestion, ["id", "path", "line", "startLine", "side", "body", "replacement"]);
            var suggestionId = OptionalText(suggestion, "id", 64);
            if (suggestion.ContainsKey("id") && !Regex.IsMatch(suggestionId, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z"))
                throw new ProtocolException("INVALID_SUGGESTION", "The suggestion ID must match its saved result identity.");
            var path = RequiredText(suggestion, "path", 1024);
            if (path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(segment => segment is "" or "." or "..") || path.Any(char.IsControl))
                throw new ProtocolException("INVALID_SUGGESTION", "The suggestion path must be a repository-relative GitHub diff path.");
            var last = Integer(suggestion, "line", 0);
            var first = Integer(suggestion, "startLine", last);
            if (first < 1 || last < first || (long)last - first > 999 || Text(suggestion, "side") != "RIGHT")
                throw new ProtocolException("INVALID_SUGGESTION", "Suggestions require a valid RIGHT-side range of at most 1,000 lines.");
            var replacement = NormalizeNewlines(OptionalText(suggestion, "replacement", 60000, required: true));
            if (replacement.Contains("```", StringComparison.Ordinal))
                throw new ProtocolException("INVALID_SUGGESTION", "A suggestion replacement containing triple backticks is not supported.", "Explicitly use a normal comment for this replacement.");
            var normalizedSuggestion = new JsonObject
            {
                ["path"] = path, ["line"] = last, ["startLine"] = first, ["side"] = "RIGHT",
                ["body"] = NormalizeNewlines(OptionalText(suggestion, "body", 10000)), ["replacement"] = replacement
            };
            if (suggestionId.Length > 0) normalizedSuggestion["id"] = suggestionId;
            suggestions.Add(normalizedSuggestion);
        }
        if (kind == "suggestChanges" && suggestions.Count == 0)
            throw new ProtocolException("SUGGESTION_REQUIRED", "Select at least one valid line suggestion.", "If no line suggestion applies, explicitly choose a normal comment.");
        var sorted = suggestions.OfType<JsonObject>().OrderBy(s => Text(s, "path"), StringComparer.Ordinal).ThenBy(s => Integer(s, "startLine", 0)).ThenBy(s => Integer(s, "line", 0)).ToList();
        for (var i = 1; i < sorted.Count; i++)
            if (Text(sorted[i], "path") == Text(sorted[i - 1], "path") && Integer(sorted[i], "startLine", 0) <= Integer(sorted[i - 1], "line", 0))
                throw new ProtocolException("INVALID_SUGGESTION", "Selected suggestion ranges overlap.", "Choose one replacement for each source range.");
        var normalized = new JsonObject
        {
            ["kind"] = kind, ["repository"] = target.Repository.ToLowerInvariant(), ["targetType"] = target.Type,
            ["targetNumber"] = target.Number, ["expectedHeadSha"] = expected, ["expectedAccount"] = expectedAccount, ["body"] = body,
            ["suggestions"] = new JsonArray(sorted.Select(s => s.DeepClone()).ToArray())
        };
        if (kind == "close") normalized["closeReason"] = closeReason;
        return normalized;
    }

    private static (HttpMethod Method, string Endpoint, JsonObject Body) BuildRequest(Target target, JsonObject record)
    {
        var kind = Text(record, "kind");
        if (kind == "close") return (HttpMethod.Patch, target.Endpoint, new JsonObject { ["state"] = "closed" });
        if (kind == "comment" && Suggestions(record).Count == 0) return (HttpMethod.Post, CollectionEndpoint(target, kind), new JsonObject { ["body"] = Text(record, "body") });
        var body = new JsonObject { ["commit_id"] = Text(record, "expectedHeadSha"), ["event"] = ReviewEvent(kind), ["body"] = Text(record, "body") };
        if (Suggestions(record).Count > 0)
        {
            body["comments"] = new JsonArray(Suggestions(record).OfType<JsonObject>().Select(s =>
            {
                var comment = new JsonObject { ["path"] = Text(s, "path"), ["line"] = Integer(s, "line", 0), ["side"] = "RIGHT", ["body"] = SuggestionBody(s) };
                if (Integer(s, "startLine", 0) < Integer(s, "line", 0))
                {
                    comment["start_line"] = Integer(s, "startLine", 0);
                    comment["start_side"] = "RIGHT";
                }
                return (JsonNode?)comment;
            }).ToArray());
        }
        return (HttpMethod.Post, CollectionEndpoint(target, kind, Suggestions(record).Count > 0), body);
    }

    private static bool SameSuggestions(JsonArray expected, JsonArray actual)
    {
        if (expected.Count != actual.Count) return false;
        var unmatched = actual.OfType<JsonObject>().ToList();
        foreach (var suggestion in expected.OfType<JsonObject>())
        {
            var index = unmatched.FindIndex(comment => Text(comment, "path") == Text(suggestion, "path") &&
                NormalizeNewlines(Text(comment, "body")) == SuggestionBody(suggestion) && Text(comment, "side") == "RIGHT" &&
                Integer(comment, "original_line", Integer(comment, "line", 0)) == Integer(suggestion, "line", 0) &&
                Integer(comment, "original_start_line", Integer(comment, "start_line", Integer(comment, "original_line", Integer(comment, "line", 0)))) == Integer(suggestion, "startLine", 0));
            if (index < 0) return false;
            unmatched.RemoveAt(index);
        }
        return unmatched.Count == 0;
    }

    private static string SuggestionBody(JsonObject suggestion)
    {
        var explanation = Text(suggestion, "body");
        return (explanation.Length > 0 ? explanation + "\n\n" : "") + "```suggestion\n" + Text(suggestion, "replacement") + "\n```";
    }
    private static string ReviewEvent(string kind) => kind switch { "approve" => "APPROVE", "requestChanges" => "REQUEST_CHANGES", _ => "COMMENT" };
    private static string ReviewState(string kind) => kind switch { "approve" => "APPROVED", "requestChanges" => "CHANGES_REQUESTED", _ => "COMMENTED" };
    private static bool UsesReview(JsonObject record) => Text(record, "kind") != "comment" || Suggestions(record).Count > 0;
    private static string CollectionEndpoint(Target target, string kind, bool inline = false) => kind == "comment" && !inline ? $"/repos/{target.Repository}/issues/{target.Number}/comments" : $"{target.Endpoint}/reviews";

    private static void ValidatePermission(string kind, JsonObject context, bool inline)
    {
        if (Bool(context, "stale")) throw new ProtocolException("STALE_CONTEXT", "The PR HEAD changed since this task was analyzed.", "Re-analyze the current PR revision before submitting.");
        var capability = kind switch { "approve" => "canApprove", "requestChanges" => "canRequestChanges", "suggestChanges" => "canSuggestChanges", "comment" => inline ? "canSuggestChanges" : "canComment", _ => "canClose" };
        if (!Bool(context, capability))
            throw new ProtocolException("GITHUB_OPERATION_UNAVAILABLE", "This operation is unavailable for the current account, target state, or task.", string.Join(" ", context["reasons"]!.AsArray().Select(n => n!.GetValue<string>())) + " Check the account and target on GitHub.");
    }

    private List<JsonObject> ReadOperations(string runId)
    {
        var directory = Path.Combine(store.RunDirectory(runId), "operations");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json").Select(path => store.ReadJson(path) ?? throw new ProtocolException("RECORD_UNREADABLE", "A GitHub operation record disappeared during reading.")).ToList();
    }
    private string OperationPath(string runId, string operationId)
    {
        ValidateOperationId(operationId);
        return Path.Combine(store.RunDirectory(runId), "operations", operationId.ToLowerInvariant() + ".json");
    }
    private void Save(string runId, JsonObject record) => store.WriteJson(OperationPath(runId, Text(record, "operationId")), record);
    private void MarkUnknown(string runId, JsonObject record)
    {
        record["status"] = "unknown";
        record["error"] = new JsonObject { ["code"] = "OPERATION_UNKNOWN", ["message"] = "The submitting process ended before confirming the remote result.", ["guidance"] = "Check this operation against GitHub; do not resend it." };
        record["updatedAt"] = Now();
        Save(runId, record);
    }
    private void MarkNotSubmitted(string runId, JsonObject record)
    {
        record["status"] = "failed";
        record["error"] = new JsonObject { ["code"] = "OPERATION_NOT_SUBMITTED", ["message"] = "Preparation stopped before a GitHub write was started.", ["guidance"] = "Refresh the preview and explicitly submit a new operation if it is still needed." };
        record["updatedAt"] = Now();
        Save(runId, record);
    }
    private void Succeed(string runId, JsonObject record, string? id, string url, string remoteState)
    {
        record["status"] = "succeeded";
        record["remoteId"] = id;
        record["url"] = url;
        record["remoteState"] = remoteState;
        record["error"] = null;
        record["updatedAt"] = Now();
        Save(runId, record);
    }
    private static JsonObject PublicOperation(JsonObject record)
    {
        var result = record.DeepClone().AsObject();
        result.Remove("baselineIds");
        result.Remove("fingerprint");
        return result;
    }
    private static void LimitPreviewFiles(JsonObject preview)
    {
        const int budget = 420000;
        var files = preview["files"]!.AsArray();
        var size = 0;
        var visible = new JsonArray();
        foreach (var file in files.OfType<JsonObject>())
        {
            var copy = new JsonObject { ["path"] = Text(file, "path"), ["status"] = Text(file, "status"), ["lines"] = new JsonArray() };
            size += Encoding.UTF8.GetByteCount(copy.ToJsonString());
            if (size > budget) { preview["previewTruncated"] = true; break; }
            foreach (var line in file["lines"]!.AsArray())
            {
                size += Encoding.UTF8.GetByteCount(line!.ToJsonString());
                if (size > budget) { preview["previewTruncated"] = true; break; }
                copy["lines"]!.AsArray().Add(line.DeepClone());
            }
            visible.Add(copy);
            if (size > budget) break;
        }
        preview["files"] = visible;
        if (Bool(preview, "previewTruncated")) preview["reasons"]!.AsArray().Add("The diff preview is truncated. Only displayed source ranges can be reviewed here; open GitHub for the rest.");
    }

    private sealed record Target(string Repository, string Type, int Number)
    {
        public string Endpoint => $"/repos/{Repository}/{(Type == "pr" ? "pulls" : "issues")}/{Number}";
        public string Url => $"https://github.com/{Repository}/{(Type == "pr" ? "pull" : "issues")}/{Number}";
    }
    private static Target ParseTarget(JsonObject task)
    {
        var repository = RequiredText(task, "repository", 200);
        if (!Regex.IsMatch(repository, "^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$") || repository.Split('/')[1] is "." or "..")
            throw new ProtocolException("INVALID_REPOSITORY", "The saved GitHub repository must be owner/name.");
        var raw = task["target"] as JsonObject ?? throw new ProtocolException("INVALID_TARGET", "The task has no saved GitHub target.");
        var type = Text(raw, "type");
        var number = Integer(raw, "number", 0);
        if (type is not ("issue" or "pr") || number <= 0) throw new ProtocolException("INVALID_TARGET", "The saved target must be an issue or PR with a positive number.");
        return new Target(repository, type, number);
    }
    private static void VerifyTargetResponse(Target target, JsonObject current)
    {
        if (Integer(current, "number", 0) != target.Number || (target.Type == "issue" && current["pull_request"] is not null) ||
            (target.Type == "pr" && !Text(current["base"]?["repo"] as JsonObject, "full_name").Equals(target.Repository, StringComparison.OrdinalIgnoreCase)))
            throw new ProtocolException("TARGET_MISMATCH", "GitHub returned a different target from the saved task.", "Refresh the action and create a task for the intended issue or pull request.");
    }
    private static void ValidateSha(string sha)
    {
        if (!Regex.IsMatch(sha, "^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$"))
            throw new ProtocolException("MISSING_HEAD_SHA", "A complete reviewed PR commit SHA is required.", "Re-analyze the current PR revision in a new task.");
    }
    private static void EnsureSha(string expected, string actual)
    {
        ValidateSha(expected);
        if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase)) throw new ProtocolException("STALE_CONTEXT", "The PR HEAD differs from the task's expected revision.", "Refresh the PR and re-analyze its current revision; Pulse does not rebind an old draft to a new HEAD.");
    }
    private static string SafeResultUrl(string value, string fallback) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com" && string.IsNullOrEmpty(uri.UserInfo) ? value : fallback;
    private static string RemoteId(JsonObject value) => value["id"]?.ToJsonString().Trim('"') ?? "";
    private static string VerifiedRemoteId(JsonObject value)
    {
        var id = RemoteId(value);
        if (!Regex.IsMatch(id, "^[0-9]{1,30}$")) throw new ProtocolException("INVALID_GITHUB_RESPONSE", "GitHub returned an invalid remote identifier.");
        return id;
    }
    private static JsonArray Suggestions(JsonObject record) => record["suggestions"] as JsonArray ?? [];
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
    private static bool Bool(JsonObject? value, string field) => value?[field] is JsonValue node && node.TryGetValue<bool>(out var result) && result;
    private static string Text(JsonObject? value, string field) => value?[field] is JsonValue node && node.TryGetValue<string>(out var result) ? result : "";
    private static int Integer(JsonObject value, string field, int fallback)
    {
        if (value[field] is null) return fallback;
        if (value[field] is JsonValue node && node.TryGetValue<int>(out var result)) return result;
        throw new ProtocolException("INVALID_REQUEST", $"{field} must be an integer.");
    }
    private static string RequiredText(JsonObject value, string field, int maximum) => OptionalText(value, field, maximum, required: true) is { Length: > 0 } text ? text : throw new ProtocolException("INVALID_REQUEST", $"{field} is required.");
    private static string OptionalText(JsonObject value, string field, int maximum, bool required = false)
    {
        if (value[field] is null && !required) return "";
        if (value[field] is not JsonValue node || !node.TryGetValue<string>(out var result) || result.Length > maximum || result.Contains('\0'))
            throw new ProtocolException("INVALID_REQUEST", $"{field} must be a string of at most {maximum} characters.");
        return result;
    }
    private static void ValidateOperationId(string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{8,128}$")) throw new ProtocolException("INVALID_REQUEST", "operationId must contain 8 to 128 letters, digits, hyphens, or underscores.");
    }
    private static void RejectUnknownFields(JsonObject value, HashSet<string> allowed)
    {
        foreach (var field in value.Select(pair => pair.Key))
            if (!allowed.Contains(field)) throw new ProtocolException("INVALID_REQUEST", $"Unsupported operation field: {field}.", "Use only the fixed operation schema; targets come from the saved task.");
    }
    private static JsonObject Describe(Exception exception)
    {
        if (exception is GitHubRequestException github)
            return new JsonObject { ["code"] = github.Uncertain ? "OPERATION_UNKNOWN" : "GITHUB_REQUEST_FAILED", ["message"] = github.Message, ["guidance"] = github.Guidance, ["httpStatus"] = github.StatusCode };
        if (exception is ProtocolException protocol)
            return new JsonObject { ["code"] = protocol.Code, ["message"] = protocol.Message, ["guidance"] = protocol.Guidance };
        return new JsonObject { ["code"] = "GITHUB_ERROR", ["message"] = "The GitHub operation could not be completed." };
    }
    private static ProtocolException PublicReadError(GitHubRequestException error) => new("GITHUB_REQUEST_FAILED", error.Message, error.Guidance);
}
