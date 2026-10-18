# Resume the non-CmdPal PR drain

Paste the following into the session that owns the existing drain. It replaces
its passive monitor behavior, not the review/evidence requirements.

```text
Resume the existing PowerToys PR drain, recovering it rather than duplicating
fork work. Scope: every live open, non-draft PR outside Command Palette/CmdPal
that still needs current artifacts. Do not expand this run into issue designs
or CmdPal PR reviews. My goal is all in-scope artifacts updated and published,
not more timestamp-only progress reports.

BOOTSTRAP AND OWNERSHIP
1. Locate the existing drain's private ledger, workers, saved cloud request
   identities, worktrees, validation results and canonical dashboard checkout.
   Reconcile actual running workers/processes before any recovery. Preserve
   useful work and evidence; do not launch a second drain.
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
- Build the live scoped queue with Get-StalePrReviewQueue.ps1 -ExcludeCmdPal.
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
- For saved cloud requests, use Get-CopilotReviewStatus.ps1 with the original
  fork head/request timestamp/baseline and exhaust pagination. Check pending
  requests at the documented interval without occupying heavy worker slots.
  Consume arrived reviews, fix valid findings, reply/resolve, push and requeue
  when another round is necessary. Do not send a new request as a status check.
  Reconcile absent review plus absent assignment; do not call it an active wait.
- Do not demand an unavailable "full" review tier. Apply the skill's actual
  substantive-review criteria to returned Lite reviews as well. A no-analysis
  timeout is not a clean review. Preserve request history and the eight-round
  cap; do not endlessly retry or reset counters.
- Prepare exact validation commands early, request Copilot before ordinary
  compilation, and build after convergence. Keep actual compilations serialized
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
  independently evidenced manual blocker may become current-head review_blocked
  with terminal:true, exact detail and concrete remediation. Review-cap outcomes
  are manual-review blockers, not clean reviews. Publish supported results for
  all other PRs before collecting remaining exceptions into one report.

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
gates. Checkpoints and real terminal blockers may be published without falsely
claiming review convergence. Sync and validate the authorized Pulse data path;
do not deploy official Pulse without its existing approval.

Before returning for my attention, reconcile every in-scope PR against its live
head and published artifact, run the scoped stale queue with -ExcludeCmdPal
-FailOnStale, and verify canonical publication and synchronized Pulse coverage.
Do not claim completion from a zero stale count alone: list ready reviews,
justified author waits, and genuine terminal manual blockers separately.
I want completed review artifacts, not blockers substituted to make the counter
zero. If an unavoidable external service/credential/lab dependency prevents
that goal, finish every unaffected PR, publish honest current-head exceptions,
and give me ONE consolidated list of exact remaining human actions, not
repeated requests or another indefinitely idle drain.

Never post comments/reviews, change metadata, open PRs, or merge in
microsoft/PowerToys without separate explicit approval. This prompt authorizes
the local/fork drain and schedule repairs, not upstream public actions.
```
