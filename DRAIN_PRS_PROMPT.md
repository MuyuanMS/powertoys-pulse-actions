# Start or resume a PR-only drain

Paste the following into the owning update session. A new run covers all PR
areas by default. To resume an explicitly scoped non-CmdPal run, retain that
scope and use `-ExcludeCmdPal` consistently. Do not silently expand an active
run or start overlapping workers.

```text
Start or resume exactly one PowerToys PR-only drain, recovering useful prior
fork work rather than duplicating it. For a NEW all-area run, scope is every
live open, non-draft PR needing current artifacts, INCLUDING CmdPal. For an
existing explicitly non-CmdPal run, preserve its scope unless I authorize
expansion. Do not expand PR-only execution into issue designs.
My goal is all in-scope artifacts updated and published,
not more timestamp-only progress reports.

BOOTSTRAP AND OWNERSHIP
1. Locate any existing drain's private ledger, workers, saved cloud request
   identities, worktrees, validation results and canonical dashboard checkout.
   Reconcile actual running workers/processes before any recovery. Preserve
   useful work and evidence; do not launch a second drain. If no active owner
   remains, establish one owner and recover useful per-PR state for the new run.
2. Fast-forward a clean dedicated main checkout of
   https://github.com/MuyuanMS/powertoys-pulse-actions. If the old checkout is
   dirty or on a feature branch, preserve it and use a separate clean canonical
   checkout instead of overwriting it. Run Assert-CanonicalDashboardTarget.
3. From that main checkout run Install-Skills.ps1 -Update, then
   Install-Skills.ps1 -VerifyOnly. Read all four newly installed skill entry
   points and relevant references, including the PR-review loop/build guidance
   and powertoys-dashboard-update/references/drain-dispatch.md. Record the
   source commit. Existing installed copies are backed up by the installer;
   preserve local fixes in those backups, but do not run a mixed-version suite.
   Pass the source revision and current instructions to every resumed worker.
4. Record one active run_id and owner_session_id. Reconcile old/expired markers
   and orphaned work; age alone neither authorizes duplication nor means a
   worker is alive. Recover this one drain after context rollover or restart.

EXECUTION AUTHORIZATION
Fork feature-branch commits/pushes, mirror/review PRs, Copilot requests within
the skill's iteration limit, replies and resolution on fork threads, isolated
worktrees, normal repository dependency restore, and focused build/test
execution are authorized. Use the repository's established launchers and
configured package feeds. Do not request new coordinator permission for each
ordinary step, review round, build-slot assignment or harmless local probe.
Earlier coordinator-generated per-step grants and exhausted planning packets
are not additional user approval gates.

Preserve dirty/shared history and do not reset or force-push remote main.
If fork main is divergent, use the PR skill's isolated upstream-based review
feature-branch recovery instead of waiting for permission to rewrite main.
Recover existing work first; record any necessary replacement review lane.
Do not relax package signature/audit requirements or pretend focused checks
prove an untested full application. Real credential, destructive-operation,
product-design and external-lab decisions remain genuine gates.

DISPATCH UNTIL FINISHED
- Build the live queue with Get-StalePrReviewQueue.ps1. Use -ExcludeCmdPal
  ONLY for an explicitly scoped non-CmdPal run; never filter an all-area run.
  Inspect labels/title and changed paths when area classification is ambiguous;
  preserve explicit scope decisions. Re-enumerate live PRs before finalization
  so new eligible PRs and changed heads are not silently missed.
- Use at most eight active distinct-PR workers, with no nested agents. Historical
  idle agents do not occupy slots or make a PR covered/completed. Resume known
  idle workers only for the same PR; new PRs get new workers.
- Maintain the private per-PR ledger from drain-dispatch.md and EXECUTE the
  assignments/coordinator_actions from Get-PrReviewDispatchPlan.ps1 after each
  completion, returned wait, or due status check. Save results and replan
  immediately. An unchanged queue is not a reason to refrain from dispatch.
- On resume, after each cloud round and its processing, and before another
  request, expensive validation or finalization, run the PR skill's
  Get-UpstreamPrReviewGate.ps1 against the ORIGINAL PR and pinned upstream head.
  Stop closed/merged/draft work, safely release slots and refresh inventory.
  New commits require fetching/pinning and reviewing the new version,
  re-grounding findings/architecture and invalidating old checks. Preserve
  consumed requests; never copy old validation to a new SHA or wait on an
  obsolete fork request before stopping/reconciling. Read errors pause work.
- For saved cloud requests, use Get-CopilotReviewStatus.ps1 with the original
  fork head/request timestamp/baseline and exhaust pagination. Check pending
  requests at the documented interval without occupying heavy worker slots.
  Consume arrived reviews, fix valid findings, reply/resolve, push and requeue
  when another round is necessary. Do not send a new request as a status check.
  Reconcile absent review plus absent assignment; do not call it an active wait.
- Do not demand an unavailable "full" review tier. Apply the skill's actual
  substantive-review criteria to returned Lite reviews as well. A no-analysis
  timeout is not a clean review. Preserve request history and the three-round
  cap; do not endlessly retry or reset counters.
  Three is the total, including the initial request, across all preserved lanes
  and sessions. If three or more were already used, request no more; consume
  saved/in-flight results and complete validated drafts. Never request round four
  or reset the allowance on schedule ticks, head changes or recovery.
- Before fixing the first review or requesting round two, follow the PR skill's
  references/architecture-review.md. Investigate intent and overall design,
  especially when the first response has many distinct related findings.
  Count alone is not proof. A grounded design flaw stops new requests for this
  head and yields consolidated supported comments, not a speculative rewrite.
  Preserve private architecture state across workers/ticks; no false clean claim.
- Prepare exact validation commands early, request Copilot before ordinary
  compilation, and build after convergence or for supported drafts at the cap
  or architectural early exit. Keep actual compilations serialized
  by exclusive lock ownership, release only after the owned work is finished,
  and reconcile stale ownership safely. Do not put read-only status/source
  checks behind the compilation lock or invent per-PR process frameworks.
  Reuse established build/environment repairs; validate exact upstream and
  literal suggestion trees as required. No false build or test claims.
- After two passes with no substantive transition, diagnose each unfinished
  next action and repair local prerequisites. Do not respond with another
  "all agents idle, no queue changes" report. Missing commands, old grants,
  optional experiments and a free build slot are not external blockers.
- Keep consuming other PRs when one hits a real external dependency. Only an
  independently evidenced operator exception may stop that PR. Keep exact
  detail/remediation in the private ledger, NEVER public data. At the review
  cap, reconcile all accepted findings across rounds, complete exact-head and
  literal-patch validation, and draft every supported correction as post_review.
  The cap stops new requests, not publication of validated findings. Do not
  substitute a review-cap blocker for valid comments or claim clean approval.
  Publish supported results before collecting remaining exceptions privately.
- Cloud review remains required. Do NOT switch exhausted/failed cloud cases
  to local review or manufacture defects to create an action. After grounding
  all saved results and publishing supported drafts, acknowledge unavoidable
  no-clean-result cases as private review_policy exceptions for my later
  decision. Use the verified operator_exception contract in drain-dispatch.md
  so they stop consuming worker slots but remain counted as incomplete.
- For new discussion on a reviewed unchanged head, execute revalidate_context,
  not a full fork loop by default. For pending cloud requests, obtain a fresh
  status before diagnosing a ten-minute wait. Diagnose once, save the outcome
  and continue other PRs; do not blindly duplicate requests or poll forever.
- Prioritize publication-ready results and available validation slots over new
  ordinary loop work. Use smallest relevant checks, minimize suggestions BEFORE
  candidate builds and reuse only independently verified unchanged receipts.
  Context rollover alone is not a reason to rebuild completed inputs.

FIX THE SCHEDULES IN THIS SESSION
Inspect the existing schedules and update them without creating duplicates.
Keep the regular update at its existing cadence (currently every three hours):
when no drain is active, run the normal bounded dashboard update covering ALL
areas, INCLUDING CmdPal. When this drain is active, service this same drain's
dispatcher and publication instead; do not start an overlapping refresh,
issue-design batch or new drain. Scheduled normal-mode budgets must not leak
into this uncapped drain.

Replace the old passive ten-minute idle-report monitor with one lightweight
five-minute drain watchdog. Its job is to execute the current dispatch plan,
reconcile actual owner/build/cloud states and recover lost handoffs, not merely
compare queue snapshots or repeatedly reload context. Use worker/process
completion notifications for immediate refill between watchdog ticks. If all
remaining work is genuine cloud wait, confirm a real next wake is active.
Stop the temporary watchdog when this drain is reconciled; keep the regular
all-area schedule enabled. Do not end with only a promise to resume later.

PUBLISH AND FINISH
Coordinator alone emits/sanitizes/validates and publishes canonical artifacts.
Publish incrementally after substantive transitions/completions; do not run an
expensive full generator on every unchanged watchdog tick. Preserve the existing
dashboard publication authorization and source-main guard; this is not
permission to push code changes directly to any remote main.

Every ready review must pass exact-head, grounding, literal-patch/EOL and build
gates. Public checkpoints contain only neutral pending metadata, never worker
diagnostics, build instructions or cap-override requests. Run the sanitizer
before every publication, including incremental checkpoints. Sync and validate the authorized Pulse data path;
do not deploy official Pulse without its existing approval.

Before returning for my attention, reconcile every in-scope PR against its live
head and published artifact, run the live stale queue with the same scope,
and verify canonical publication and synchronized Pulse coverage. Use
-FailOnStale only when no pending reviews or private exceptions remain.
Do not claim completion from a zero stale count alone: list ready reviews,
justified author waits, and genuine private operator exceptions separately.
I want completed review artifacts, not blockers substituted to make the counter
zero. If an unavoidable external service/credential/lab dependency prevents
that goal, finish every unaffected PR, publish only neutral pending metadata,
and give me ONE consolidated list of exact remaining human actions, not
repeated requests or another indefinitely idle drain.

Report a final coverage table for EVERY in-scope PR: upstream number/live SHA,
published action and canonical artifact link, justified author wait, or exact
remaining private exception. Report new/missing artifacts separately from
exhausted historical reviews. A checkpoint rewrite, fork review or successful
plan is not a delivered action. Verify the final published files and actual
Pulse data synchronization before claiming delivery. If only verified exceptions
remain, say INCOMPLETE and give one decision list; never call that a full drain.

Never post comments/reviews, change metadata, open PRs, or merge in
microsoft/PowerToys without separate explicit approval. This prompt authorizes
the local/fork drain and schedule repairs, not upstream public actions.
```
