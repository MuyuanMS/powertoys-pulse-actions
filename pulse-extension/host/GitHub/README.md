# Fixed GitHub operations

`GitHubService` only accepts saved task targets on `github.com`. `PreviewAsync` and
`ReconcileAsync` make read requests. A separate explicit `SubmitAsync` request is
required for each user-selected write. CLI completion never calls this service.

Settings enumerate local accounts using `gh auth status --hostname github.com
--json hosts`. Each account's login, active flag and state are returned independently;
an expired login does not hide other valid accounts. Tokens, scopes, raw error output
and credential source paths are never exposed to the extension.
Authentication requires the saved `githubAccount` selection to appear as a successful
account in that status. `gh auth token --hostname github.com --user <selected>` reads
its credential into Host memory. Ambient tokens and HTTP debug logging are removed
from child processes. The credential is provided only as the `GH_TOKEN` environment
variable of each `gh api` process; the service never changes the global active account.
The API `/user` result is checked against the selected account before use.
Every submit carries the preview's `expectedAccount`; an account change between
preview and click stops the operation instead of publishing under a different user.

All production API requests, including PR context verification and previews, use
`gh api` with an explicit method, fixed `https://api.github.com` URL and argument
arrays. JSON request bodies are sent on stdin with `--input -`. `--include` supplies
the HTTP status line for structured error handling; stdout is bounded and raw stderr
is discarded. The Host does not retry failed or uncertain writes. GitHub CLI owns
its HTTP behavior, including redirect handling. There is no anonymous HttpClient
fallback: PR tasks requiring remote HEAD verification need a selected valid gh login.
Tasks without remote revision verification can still run locally without GitHub.

Writes are fixed to `POST /repos/{owner}/{repo}/pulls/{number}/reviews`,
`POST /repos/{owner}/{repo}/issues/{number}/comments`, and
`PATCH /repos/{owner}/{repo}/{issues|pulls}/{number}` with `state: closed`.
Review events are explicitly `APPROVE`, `REQUEST_CHANGES`, or `COMMENT`, and all
reviews have the task's recorded `commit_id`. Close never adds a comment or merges.
Read access, known repository permissions, ownership, archive/lock/state and HEAD
are checked first. GitHub remains authoritative for token scopes, organization
rules, Copilot assignment restrictions, and changes racing the final read.

Suggestion source comes from complete RIGHT-side patch hunks at the recorded HEAD.
Each selected range must be continuous in one hunk; omitted/binary/truncated patch
regions and overlapping suggestions are rejected. The original source is saved
with the operation. A replacement containing triple backticks is rejected to avoid
ambiguously closed Markdown suggestion fences. It is never silently downgraded to
a normal comment. The browser preview is capped; omitted ranges are unavailable in
that preview. GitHub's maximum 3,000-file response is handled explicitly.

Operations live in `runs/{runId}/operations/{operationId}.json`. A shared file lock
covers preparation through result persistence. The normalized draft fingerprint
coalesces repeat clicks with different IDs; reusing an ID with different content is
rejected. Before a write, the service stores existing remote IDs and persists
`submitting`. A network interruption or 5xx response becomes `unknown`; it never
causes automatic retry. Recovering an abandoned `submitting` record also produces
`unknown`. Reconciliation requires a unique new remote result with matching account,
body, review SHA/event and all selected inline comments. Missing or edited results
remain unknown. A recovered `prepared` record is safe to mark failed because no HTTP
write was started. CLI status is never modified by GitHub operations.
Operation history prioritizes unconfirmed operations and returns at most 100 recent
records within 600 KB, with explicit `truncated`, `totalCount`, and `bodyTruncated`
metadata. Full selected text and source evidence remain in the local operation file.

The injected session factory and HttpClient constructor exist for offline contract tests.
`tests/GitHubScenarios.cs` supplies a fake HTTP handler and checks durable intent,
fixed payloads, source ranges, stale HEAD, permissions, deduplication, and recovery.
It does not log in or contact GitHub. Real writes require a separately authorized
acceptance repository and have not been used while implementing this adapter.
`tests/GhCliScenarios.cs` checks command arguments, account selection, credential
isolation, stdin bodies, status parsing and uncertain writes through an injected
process runner, without running credential commands or contacting GitHub.

Official references checked read-only on 2026-09-08:

- [GitHub CLI auth token](https://cli.github.com/manual/gh_auth_token)
- [GitHub CLI auth status](https://cli.github.com/manual/gh_auth_status)
- [GitHub CLI API](https://cli.github.com/manual/gh_api)
- [Create and list PR reviews](https://docs.github.com/en/rest/pulls/reviews#create-a-review-for-a-pull-request)
- [PR review comments and line ranges](https://docs.github.com/en/rest/pulls/comments#create-a-review-comment-for-a-pull-request)
- [List PR files and update PR state](https://docs.github.com/en/rest/pulls/pulls)
- [Issue comments](https://docs.github.com/en/rest/issues/comments#create-an-issue-comment)
- [Update issue state](https://docs.github.com/en/rest/issues/issues#update-an-issue)
- [Repository permissions](https://docs.github.com/en/rest/repos/repos#get-a-repository)
- [Review access and actions](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/reviewing-changes-in-pull-requests/about-pull-request-reviews)
