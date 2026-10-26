# Normal delta updates

This is the bounded scheduled/manual path. An explicitly requested drain uses
`drain-dispatch.md`; a routine tick never silently becomes an uncapped run.

## Recover ownership, then discover

Follow entry-point startup once. If a drain is active, service that same run
and return; do not start a parallel normal update. Normal scope includes CmdPal.
Otherwise recover prior private states and obtain exhaustive inventories:

```powershell
$skill = Join-Path $Dashboard '.github\skills\powertoys-dashboard-update'
& "$skill\scripts\Get-DashboardUpdateCandidates.ps1" -Dashboard $Dashboard -AsJson
& "$skill\scripts\Get-DashboardUpdateRunPlan.ps1" -Dashboard $Dashboard -AsJson
```

Save the combined run plan, then dispatch its selected PRs directly:

```powershell
& "$skill\scripts\Get-PrReviewDispatchPlan.ps1" `
  -RunPlanJsonPath <saved-update-plan.json> -StateJsonPath <private-ledger.json> -AsJson
```

This input excludes deferred PRs and preserves context/head/activity fields.
No hand-built queue conversion or second PR selection is needed. The dispatcher
inherits the run plan's concurrency. Give workers its `deadline_utc` and
`stop_new_work_after_utc`; regenerate those on a new normal tick, not resume
an expired deadline from the previous tick.

Enumerate all eligible open PRs and bugs, not only a recent-time window.
Recency controls execution priority, never discovery. Classify missing/changed
heads as full review; newer discussion on a reviewed unchanged head as context
revalidation; retain current author waits and exclude drafts/closed work.
The helpers, not `generated_at`, determine freshness.

Use the planner's selected/deferred sets. Defaults:

| Setting | Normal default / environment override |
| --- | --- |
| PR batch | 16 / `POWERTOYS_PR_REVIEW_BATCH_SIZE` |
| PR concurrency | 3 / `POWERTOYS_PR_REVIEW_CONCURRENCY` |
| Run budget | 50 minutes / `POWERTOYS_DASHBOARD_RUN_BUDGET_MINUTES` |
| Issue revalidation | 75 / `POWERTOYS_ISSUE_REVALIDATION_BATCH_SIZE` |
| Old issue reserve | 15 / `POWERTOYS_OLD_ISSUE_RESERVE` |
| Issues per unused PR slot | 4 / `POWERTOYS_SPARE_ISSUES_PER_UNUSED_PR_SLOT` |
| Detailed issue designs | 6 / `POWERTOYS_DESIGN_BATCH_SIZE` |

Pass explicit values to the run planner when overrides are set. Use
`-PrNumbers`/`-IssueNumbers` for targeted execution without narrowing discovery.
Every deferred item remains eligible on the next tick, including old backlog.

## Advance selected work

Publish initial inventory, then run selected PRs through the same dispatcher
and PR worker contract as a drain, with this run's concurrency and deadline.
Both delta and drain PRs use at most three total cloud requests followed by
assessment, focused validation and drafting, or grounded architectural exit.
There is no separate delta-review algorithm or additional convergence gate.
Give workers the current UTC deadline, not an expired prior-run cutoff.
Stop starting requests/builds ten minutes before the normal deadline; persist
unfinished work and publish completed results. A cloud wait releases capacity
to other selected PRs; do not select unlimited additional PRs.

For issues, follow `issue-triage.md`. Run the selected lightweight judgment pass
separately from the bounded full-design batch. Prioritize recent actionable
work but preserve the oldest stale-issue reserve. Full design requires the
execution skill; an approved design implementation is a separate workflow.

Re-read substantive new discussion, author rebuttals and invalid-finding
reports even on the same head. Withdraw unsupported findings and retain the
private rejection reason. Explicit targeted reruns override freshness skipping.
No full code loop or rebuild solely to advance a timestamp.

An author wait requires actual live evidence: needs-author-feedback label,
current changes-requested review, outstanding posted Pulse request, or a
specific maintainer request with recorded author/URL/time/requested evidence.
A last commenter or unposted draft is not an author wait. New author activity
requires substantive reassessment; changed heads cannot inherit an old wait.

## Publish and finish on time

Follow `publication.md`. Publish completed artifacts immediately and other
substantive pending changes after two transitions or eight minutes.
At the deadline, retain explicit selected/in-progress/deferred counts and
finish the bounded run. A nonempty stale queue is expected, not a failure:
never use `-FailOnStale` for a normal bounded update.

Report PR delivered/clean/context-revalidated/author-wait/pending/deferred
counts, issue judgments and designs, contract-invalid bugs corrected/remaining,
canonical commit, Pulse synchronization/deployment, and integration failures.
Always state whether any upstream write occurred.

## Optional integrations: failures do not stop review work

- Project 2445: use `Sync-PowerToysProject.ps1` with the configured owner/number
  (defaults `microsoft`/2445). Use `-DryRun` when permissions/options changed.
  Missing PRs are added; drafts/closed states and review dispositions follow
  the script's mapping. Report authorization failures explicitly; do not claim
  success or block unrelated artifact delivery.
- Scheduled notifications: when M365/WorkIQ is available, mail the signed-in
  user; `POWERTOYS_DASHBOARD_NOTIFY=none` disables it. Resolve mail with
  `/me?$select=mail,userPrincipalName`, send via `/me/sendMail`, then recover
  the Sent Items message ID for replies. Send selected/deferred item tables
  at start, a reply after 30 minutes if active, and a final result/failure
  reply. If the original ID is unavailable, use the same subject with `Re:`.
  Keep private/local evidence and credentials out of notifications. Missing
  tools or delivery failure is reported, never a reason to stop the update.

Regular schedules include all areas. A temporary drain watchdog services only
the active drain and is stopped at its final reconciled outcome.
