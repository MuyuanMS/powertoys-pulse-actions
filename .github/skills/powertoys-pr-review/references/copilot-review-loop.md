# Copilot Review Loop (Steps 4–8)

The fork Copilot loop is required unless the grounded [architectural early-exit gate](./architecture-review.md) applies, and is independent of the posting decision (Critical Rule 11). Drive it toward convergence within **three total review iterations** (initial request plus at most two re-requests), then draft independently validated findings even if clean convergence is absent.

Count actual requests across the existing PR's preserved review lanes and
sessions, including timed-out/no-analysis requests. Do not reset the count on
a scheduled tick, restart, replacement fork PR, rebase, or head change. Before
any request, reconcile saved request history and refuse a fourth iteration.
For an existing review already at or above three, issue no new requests:
consume saved/in-flight results and finish grounding, validation and drafting.
A request already in flight when this policy changes may finish; it grants
no additional round. Unknown counts require history reconciliation, not zero.

> **Reviewing 2+ PRs?** Run the loop for each PR **concurrently**, not one-by-one — see [batch-parallel.md](./batch-parallel.md) (one background sub-agent per PR, builds serialized, orchestrator is the single writer of `review-data.json`). The per-PR steps below are identical; only the orchestration differs.

## Step 4: Request Copilot review

**Resume before requesting.** For an existing checkpoint, use the read-only
reader; do not invoke the request script to check whether a review finished:

```powershell
.\scripts\Get-CopilotReviewStatus.ps1 -ForkRepo <FORK_REPO> `
  -PRNumber <fork_pr_number> -HeadSha <saved-fork-head> `
  -RequestedAt <saved-UTC-request-time> -AsJson
```

Pass `-AfterReviewId` too when the checkpoint contains the baseline returned by
the request script. Discovery without a saved timestamp may omit `RequestedAt`,
but never invent a new request time for old work. The reader exhausts REST pages
and matches the exact **fork** head, bot identity and submitted timestamp.
An upstream head and a fork head with review fixes are different identities.
`Submitted: true` means the result arrived, not that it is clean: consume its
review body/comments and inspect unresolved threads before proceeding.
If `HeadMatches: false`, reconcile the changed branch; do not transfer the result.
An API/pagination error is not evidence that the review is still pending.

Only when a genuinely new round is needed:

```powershell
$request = .\scripts\Request-CopilotReview.ps1 -ForkRepo <FORK_REPO> `
  -PRNumber <fork_pr_number> -RequestsUsed <reconciled-count> -TimeoutMinutes 0
```

Save `RequestedAt`, `HeadSha`, `AfterReviewId`, and, when available, `ReviewId`
and `SubmittedAt`. The script uses the correct bot account, rejects an already
pending Copilot request, checks immediately, and exhausts all review pages.
It refuses missing/negative `RequestsUsed` or a count of three or more before
any API access. This count is required, not a new per-session allowance.
Use the default 10-minute wait only outside bounded dashboard workers.
An empty `requested_reviewers` array proves neither unavailability nor
completion: the review may have completed asynchronously. Require the actual
matching submitted review. Do not infer a clean pass from an empty first page
or from the absence of an assignment.

Review effort is not an extra completion gate: a submitted Lite review is not
automatically a failure. Inspect the actual body and findings against the same
convergence criteria. A timeout/no-analysis service response is not substantive
review, but do not invent a requirement for an unavailable "full" tier.
Repeated service failure needs a bounded diagnostic and an exact manual
remediation, not unlimited identical requests.

For bounded workers, if the immediate check finds no review, checkpoint
`waiting_copilot` and return the slot. Before the coordinator republishes a
waiting checkpoint, recheck once with the read-only reader; if a result arrived,
record it and queue/continue `reviewing_findings` or `building`, not another wait.
Do not request again merely because a timeout elapsed. Across all manual API
fallbacks, reviews and comments must use `--paginate` (not just `per_page=100`);
GraphQL threads must traverse `pageInfo`. This avoids the PR 50663 failure where
review 39 was invisible to a 30-row default request.

### Fallback: local review

If Copilot review cannot be enabled, first guide the user to turn it on (see [prerequisites.md](./prerequisites.md#3-copilot-code-review-on-the-fork)). If they choose local review instead:

- Launch a local code-review sub-agent against the changed files in the worktree.
- Focus on correctness, PowerToys style conventions, potential bugs, performance, and edge cases.
- Treat the local agent's findings the same as Copilot comments for the rest of the loop.

## Step 5: Process review comments — fix, push, reply, resolve

Before implementing first-round fixes or requesting round two, run
[architecture-review.md](./architecture-review.md). Large comment volume is a
signal to investigate independently, not proof. If the design cannot meet the
stated goal or violates a proven invariant, stop new requests for this head and
draft consolidated grounded concerns; do not rewrite the entire design just to
force convergence. This exception overrides the fix/re-request rules below.
For already-running reviews, perform this gate at the next safe checkpoint
before another request; do not discard or duplicate any in-flight review.

For each review comment from Copilot:

1. **Fetch new comments**: `gh api --paginate "repos/<FORK_REPO>/pulls/<fork_pr_number>/comments?per_page=100" --jq '.[]'`, filtered to the Copilot bot and the submitted `pull_request_review_id`. Inspect the matching review body too; never count only the first comments page or substitute another round's comments.
2. **Assess validity and origin** — follow [finding-grounding.md](./finding-grounding.md).
   Inspect the pinned upstream source and first affected commit. Is this an
   upstream defect, an agent-introduced regression, already fixed, optional,
   or unsupported? Check the existing guards and the author's stated intent.
   Fixing a review-introduced regression never creates an upstream finding.
3. **If valid — actually fix the code.** Open the referenced file and make the change. Do not just acknowledge — fix it.
4. **If invalid / not applicable** — note a clear reason for the reply (e.g., "intentional because...", "out of scope for this fix because...").
5. **After processing all comments in the round:**
   ```powershell
   cd <worktree-path>
   git add -A
   git commit -m "Address review round N: <brief summary of fixes>"
   git push <FORK_REMOTE> pr-iterate/N
   ```
   Then reply to each comment **and** resolve it:
   ```powershell
   # Reply
   gh api repos/<FORK_REPO>/pulls/<fork_pr_number>/comments/<comment_id>/replies -f body="Fixed in <commit-sha>: <brief description>"
   # Resolve the thread
   gh api graphql -f query='mutation { resolveReviewThread(input: {threadId: "<thread_node_id>"}) { thread { isResolved } } }'
   ```
   Every comment must be **both** replied to and resolved. If `resolveReviewThread` is unavailable, try `minimizeComment` with `classifier: RESOLVED` using the comment's `node_id`.

### Suppressed-comment policy

Copilot may retain suppressed/internal candidate comments even when the published review has zero inline comments. Suppressed comments do **not** block convergence.

- During rounds 1–3, inspect a suppressed comment only when available and act only if it identifies an important correctness, security, data-loss, crash, or significant user-impact issue.
- Ignore suppressed style, cleanup, speculative, low-severity, and preference comments. Do not create another review round for them.
- Suppressed comments never authorize a fourth iteration.
- A review with zero published inline comments is a zero-comment review even if suppressed candidates exist.

## Step 6: Iterate the loop

**Loop only after the architecture gate permits localized fixes.** A proven
design-level flaw may stop automation after round one even with comments.

```
requests_used = <reconciled actual request count across preserved lanes>
last_review_timestamp = <timestamp before first request>

WHILE requests_used < 3:
  1. Request Copilot review (Step 4); persist requests_used += 1 immediately.
  2. Wait for a new review with submitted_at > last_review_timestamp (poll up to 10 min).
  3. Fetch comments created after last_review_timestamp from copilot-pull-request-reviewer[bot].
     Before first-round fixes, investigate intent/architecture; if changes_required,
     EXIT new requests and draft independently grounded design-level findings.
  4. If ZERO new published inline comments, and no important round-1-to-3 suppressed issue was accepted -> EXIT LOOP -> Step 7.
  5. For each new comment: assess; if valid+in-scope, fix in the worktree.
  6. If any fixes were made: git add -A; commit; push to the fork branch.
  7. For each comment: reply with the fix (or the reason) AND resolve the thread.
  8. last_review_timestamp = now.
  9. Termination:
       - After request 3, stop new requests; finish validation and draft supported findings.
       - If clean convergence is absent, keep any operator exception private. Do not claim approval.
       - Otherwise -> GOTO 1.
END LOOP
```

**Rules for the loop:**
- **Push before re-requesting** — Copilot reviews the latest pushed code.
- **Re-request after a push only while fewer than three requests have been used** — Copilot will not auto-review; the cap still applies.
- **Track timestamps** — only process comments newer than the last request; do not re-process old ones.
- **If Copilot repeats a fixed concern**, reply "Already addressed in commit `<sha>`" and resolve; do not re-fix.
- **Out-of-scope comments** (files this PR did not modify): reply "Not applicable: this file is not modified by this PR" and resolve; do not fix.
- **Suppressed candidates are advisory only** — apply the policy above and never count them as published inline comments.

**Step 6 convergence conditions:**
1. The most recent **freshly-requested** Copilot review returned **zero published inline comments**. Suppressed candidates do not count.
2. **Zero** unresolved Copilot review threads remain (every thread replied-to and resolved).

After both conditions hold, run the serialized Step 7 build before marking the
review complete or proceeding to Step 9. If the final build changes any code,
request another Copilot review only within the three-request cap and rebuild.
If the allowance is exhausted, validate the changed tree for supported drafts
without claiming a fresh clean review.

At the three-round limit, finish assessing and replying to the final review
and reconcile ALL accepted findings against the exact upstream head, not only
the last response. The cap limits new automated requests; it does not prohibit
drafting validated findings. Preserve each finding's disposition privately:
publishable, already posted, unsupported, superseded, or validation pending.
For independently supported author-facing corrections, complete the exact-head
grounding and required literal-patch/EOL/build checks and emit `post_review`
at `awaiting_review_approval`, even when the final automated response was not
zero-comment. Do not emit an approval or claim clean convergence.
Only when no defensible publishable finding remains should the result be
pending with an operator exception in the private ledger. Keep request counts,
build failures, toolchain instructions and manual remediation out of public
artifacts. Continue unaffected PRs; never erase valid findings because another
finding or the automation service is blocked. Do not reset the counter or
silently authorize round four.

For architectural early exit, preserve unresolved findings truthfully; never
resolve a real unfixed flaw merely to satisfy a clean-thread count.

Before moving on (to the next PR, to Step 9, or to ending the turn), run the stranded-loop self-audit with [scripts/Get-UnresolvedCopilotThreads.ps1](../scripts/Get-UnresolvedCopilotThreads.ps1). Expect 0 for convergence. For architectural early exit, draft the grounded concerns and retain unresolved-thread evidence privately without requesting another round. Otherwise resume within the round cap or preserve the remaining operator exception privately at the cap; never present unresolved concerns as clean.

## Step 8: Summarize the converged net diff

1. `git log --oneline main..pr-iterate/N` — review all commits made during the loop.
2. Produce a clean diff of the net changes relative to the original PR's base: `git diff main...pr-iterate/N`.
3. Distinguish meaningful changes from noise (back-and-forth later reverted). If some iterations added code that does not contribute, consider squashing or reverting those specific changes.
4. Keep only changes that fix genuine issues. The Step 9 suggestions are drawn from this converged net diff — never from raw round-1 Copilot output.

### Fork-to-upstream delta accounting

Before calling the result clean, compare the pinned upstream head with the
converged fork tree:

```powershell
git diff --stat <upstream-head>..<converged-fork-head>
git diff <upstream-head>..<converged-fork-head>
```

Classify every fork-only hunk:

- **Already upstream** — no action.
- **Apply-ready localized upstream fix** — draft an inline suggestion only
  after independent upstream defect and remedy validation.
- **Required but not safely suggestible** — draft an exact inline prose or
  companion request and keep the PR pending review approval.
- **Review-introduced regression / rejected alternative / optional experiment**
  — keep the reason in the private ledger and remove it with an ordinary commit
  if retaining that branch. Do not reset shared history or export it as a
  required upstream change.

The accounting must reach zero unexplained fork-only hunks. A clean
zero-finding result is allowed only when the validated tree is the exact
upstream head. Do not claim that upstream has tests, documentation, syntax
repairs, or behavior that exists only in the fork.
