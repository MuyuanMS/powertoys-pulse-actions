# PR drain dispatch and schedule contract

A drain is an execution loop, not a repeated inventory report. Use this
contract for a PR-only drain without starting issue/design work. The operator
may exclude Command Palette with `Get-StalePrReviewQueue.ps1 -ExcludeCmdPal`;
use the same switch for the final `-FailOnStale` gate. Without that switch,
normal scheduled updates continue to include Command Palette.

## Load the complete execution suite

At run startup, fast-forward a clean canonical `main` checkout, then run its
`Install-Skills.ps1 -Update` and `Install-Skills.ps1 -VerifyOnly`. The installer
backs up existing copies outside the skills directory before replacing them
and verifies every file in all four skills. Read the updated entry points and
references, especially `powertoys-pr-review`, and record the source commit in
the private run ledger and worker prompts. Reloading only the orchestrator is
not an upgrade of the execution skills. Preserve and adjudicate local fixes
from the backup; do not silently mix two suite revisions.

## Dispatch from work state, not agent existence

At each per-PR round boundary and resume, use the PR skill's read-only
`Get-UpstreamPrReviewGate.ps1` against the original upstream PR before advancing
the fork loop. Workers also check before another request, expensive validation
or finalization. Save the observation privately and act on it: stop closed,
merged or draft work, safely release owned resources and refresh the scoped
live queue; reconcile a new upstream head before further review work.
The offline planner cannot perform this live check. Do not let an old queue
snapshot or an open fork PR override a newer upstream observation. Re-ground
findings and architecture, invalidate old-head checks and preserve total
requests when new commits arrive. Do not poll a superseded request to justify
continuing the obsolete version; retain its identity privately.

Maintain one private `prs` record per upstream number, outside public `data/`.
At each worker completion or due cloud check, reconcile the real worker state,
save results, and call:

```powershell
.\scripts\Get-PrReviewDispatchPlan.ps1 `
  -QueueJsonPath <scoped-live-queue.json> `
  -StateJsonPath <private-ledger.json> -MaxConcurrency 8 -AsJson
```

This read-only planner neither launches workers nor verifies review evidence.
The coordinator must execute its returned `assignments` and
`coordinator_actions`, save the actual result, and immediately replan.
Never report a plan as work executed. A minimal private ledger is:

```json
{
  "prs": [{
    "number": 50000,
    "head_sha": "0123456789012345678901234567890123456789",
    "phase": "reviewing_findings",
    "worker_status": "idle",
    "worker_id": "known-worker-id",
    "next_action": "Assess saved review 123, implement valid findings, reply and resolve.",
    "round": 2
  }]
}
```

`round` is the number of actual requests already consumed, including the initial
request, not the next iteration number. Reconcile legacy next-round counters
against saved request history before migration; unknown history is not zero.
Pass that count as `Request-CopilotReview.ps1 -RequestsUsed <count>` and persist
each actual request immediately. No helper/API fallback bypasses this cap.

Use `worker_status: running|idle|none`. Resume a known idle worker only for
the same PR; a different PR gets a fresh worker. Historical idle workers are
neither active capacity nor a reason to skip the PR. If the queue head differs
from the ledger head, reconcile the new head before using old review, blocker,
or build evidence. Include running workers outside the queue until safely
finished or reconciled; do not duplicate their operations.

The planner uses phases `waiting_copilot`, `reviewing_findings`, `building`,
`finalizing`, and `blocked`; other legacy states schedule resume reconciliation.
For `waiting_copilot`, save:

```json
{
  "request": {
    "fork_repo": "example/PowerToys",
    "fork_pr": 100,
    "head_sha": "0123456789012345678901234567890123456789",
    "requested_at": "2026-10-18T15:00:00Z",
    "after_review_id": 123
  },
  "review": {
    "checked_at": "2026-10-18T15:01:00Z",
    "submitted": false,
    "request_pending": true
  }
}
```

These review fields come from the fully paginated read-only status helper,
not assignment emptiness or a guessed request time. Preserve the exact fork
head independently of the upstream head. Unknown status schedules a check;
submitted schedules finding processing; absent review AND absent assignment
schedules reconciliation, not an indefinite wait or blind duplicate request.
Pending checks default to every 120 seconds, outside heavy worker slots.
API failures are errors to diagnose with bounded backoff, not pending reviews.
After ten minutes from the saved request, the planner first obtains a current
read-only status, then assigns `diagnose_cloud_wait` if it is still pending.
Diagnose the actual request/run once; a slow response is not proof of a
service defect, clean review, or permission to duplicate the request.
Persist the diagnosis and a concrete next action. If externally blocked,
adjudicate the exception below rather than polling indefinitely. If a result
arrives, consume it; never discard an eventual review because time elapsed.

`building` reserves at most one build per dispatch. Pass `-BuildOwnerNumber`
while a separately tracked build process still owns the lock. Reconcile that
owner if all workers are idle; never steal a lock or kill unrelated processes.
Use actual exclusive lock ownership, not a historical permission token.
All product and candidate compilations remain serialized. Read-only GitHub,
source inspection, drafting, queue reconciliation and publication preparation
do not take the compilation lock. Do not repeatedly prepare source-only
packets while a free build slot and the actual validation command are ready.
Validation-ready work has priority over starting another ordinary review
stage; prepare receipts and publication while other PRs wait on cloud. Reuse
verified unchanged validation inputs as described in the PR build reference,
and minimize suggestion ranges before their first candidate build.

For `work_type: context_revalidation`, the planner assigns `revalidate_context`
instead of replaying the fork loop. Existing in-flight results, finding
processing, validation and finalization finish first. Read the new discussion substantively:
retain a supported current decision, withdraw invalid proposals, or escalate
to a full current-head review if the evidence changes the code judgment.
Do not request a new round or recompile solely because an unrelated comment
advanced `updatedAt`; never blindly advance covered-activity timestamps.

For `blocked`, record `blocker.detail`, `blocker.remediation`, and
`blocker.external`. Missing commands, idle-agent ownership, a stale coordinator
grant, or routine dependency restore are local work, not external blockers.
`publish_terminal_blocker` is a legacy planner action name: independently
adjudicate and record a current-head operator exception in the PRIVATE ledger.
Never publish its detail/remediation as dashboard content.
It does not turn the ledger flag itself into proof. At the three-round limit,
dispose the last review, preserve the request ledger, and finalize a specific
manual-review outcome after drafting every independently validated finding;
do not reset the round counter or request round four.
Three means the initial review plus at most two re-requests, across preserved
lanes and sessions; all actual requests, including timeout responses, count.
Existing histories at or above three get no new allowance. Consume requests
already in flight, complete validation and publish supported drafts instead.
Neither a scheduler tick, head change nor recovery starts a fresh allowance.
No action permits claiming convergence without the required evidence.

### Park verified exceptions without hiding incomplete coverage

Cloud review remains required. Do not substitute an independent/local review
or a manufactured author-facing defect when the cloud allowance is exhausted.
After consuming all results, grounding accepted/rejected findings and
publishing every supportable draft, a remaining no-clean-result case is a
private `review_policy` exception for the operator's later decision.

To prevent repeatedly assigning the same exception ahead of uncovered PRs,
save `operator_exception` only after independent coordinator adjudication:
`head_sha`, `source_updated_at` (the covered live activity), `category`
(`external` or `review_policy`), exact `detail`, concrete `remediation`,
non-empty `evidence` references and `verified_at`. Local planning/build-command
gaps, unused build slots and optional experiments are not external exceptions.
The planner checks shape/freshness, not the truth of this evidence.

Matching verified exceptions appear in `operator_exceptions`, not worker
assignments. Changed heads/activity and incomplete receipts schedule
revalidation. `exhausted_with_exceptions: true` is explicitly an INCOMPLETE
drain requiring the consolidated operator decision list, never success or a
zero stale queue. Finish all unaffected work first. Never copy these private
fields into public artifacts or exclude them from the public coverage count.

### Architectural early exit

Before first-round fixes or another request, follow the PR skill's
`references/architecture-review.md`. Record a private `architecture` object
with upstream `head_sha`, `status` (`pending`, `cleared`, `changes_required`,
`insufficient_evidence`) and its evidence/next action. All workers perform the
gate, including legacy rows with no architecture record.

The planner routes saved pending/unproven or stale-head assessments to
`investigate_architecture`, and current-head `changes_required` to
`draft_architecture_review` instead of more fixing/requesting. These actions
are work to execute, not evidence: investigate independently, then ground and
draft the supported overall concern. Do not turn a noisy first review alone
into a verdict. `cleared` permits the normal three-request loop; confirmed
design flaws end new requests even after round one. Preserve validation and
finalization phases, consume in-flight responses and never manufacture a
clean result. Only the drafted contributor-relevant concern goes to public
data; the architecture record stays private.

### Public data is not a drain log

`data/` contains user-facing review drafts and neutral availability metadata,
not operator instructions. `Set-PrReviewCheckpoint.ps1` saves full receipts
outside the repository under `POWERTOYS_PRIVATE_REVIEW_STATE` (default
`%LOCALAPPDATA%\PowerToysPulse\review-state`), then emits only the public
projection. `Sanitize-ActionData.ps1` archives legacy receipts there before
removing operational fields. Recover private receipts by PR number and pinned
head, alongside the active-run ledger and live fork traces.
Never put `blockers`, worker phases, fork bookkeeping, summary/status prose,
build logs, cap-override requests or `next_action` in public PR artifacts.
Run the sanitizer even for incremental publication; `emit.ps1` also enforces
this boundary. An unavailable automated review is not a defect in the PR.

At the cap, conserve all accepted findings across rounds and finish grounding
and validation for any publishable correction. A validated `post_review` does
not require an unrelated zero-comment pass; `approve` still requires the clean
review gates. If none survives, publish only neutral pending metadata and
report the precise exception privately. Neither a terminal flag nor a cap
outcome is completed review coverage. The public stale queue deliberately
continues to include those PRs; reconcile private exceptions separately rather
than manufacturing a zero stale count.

## Forward-progress invariant

An unchanged queue is not permission to do nothing. On every dispatch pass,
each unfinished PR must be running, dispatched, due for a cloud check, waiting
until a recorded next check, waiting on an actual build owner, or being
finalized with independently supported external-blocker evidence.

If workers are idle and any local work is runnable, dispatch it in the same
pass. After two consecutive passes without a substantive transition, audit
each PR's exact next operation and eliminate local planning/grant loops.
Do not rewrite timestamps, send more reminders, or invent product findings to
simulate progress. Reuse a proven build launcher and dependency setup across
PRs, with isolated outputs and exact-tree results; do not commission a new
process-custody/projection framework for every review.

Routine local/fork operations already authorized by the operator do not need
another approval per command or review round. Genuine credential, destructive,
upstream-posting, runtime-lab, or product-design decisions remain gated.
Finite technical retries must end in a verified alternative or an honest
specific blocker, not perpetual `review_in_progress`.

## Coexistence with scheduled updates

Keep one regular update schedule at its existing cadence. When no drain is
active, it runs the normal bounded update with all areas, including CmdPal.
When a drain is active, it services that same run through dispatch/status and
publication; it must not start another drain, issue-design batch, or competing
publisher. Do not mutate schedules from another session.

Use worker/process completion notifications for immediate re-dispatch. If a
scheduled watchdog is needed, keep only one lightweight five-minute drain
watchdog; it runs dispatch, not a full inventory rebuild or a no-op idle report.
If all remaining work is genuine cloud wait, keep a real wake mechanism for
the next checks. Do not end the loop with only "await next tick" unless the
schedule is confirmed active. Stop this temporary watchdog when the drain is
finished; leave the regular update schedule active.

Persist `run_id`, `owner_session_id`, scope, suite commit, running workers,
last substantive progress, next check, and completion state in the private
active-run marker. An expired timestamp alone neither proves ownership nor
authorizes a competing drain. Reconcile owner/process state on recovery;
resume orphaned work once, retaining review identities and results.

## Finish and report honestly

Refresh live scope before the final gate, handling new heads and newly eligible
PRs. Validate and publish every concluded artifact, verify it in canonical
`main` and synchronized Pulse, and run the scoped stale-queue gate.
Run a final artifact/head/coverage reconciliation, not merely a zero count.
Keep completed results while repairing another PR; one blocked PR must not
prevent every other PR from finishing.

Report ready reviews separately from author waits and private operator exceptions.
Pending artifacts are not completed reviews. If real external service,
credential or lab failures prevent full review coverage, publish all supported
results and deliver one consolidated PRIVATE exception list; never promise a clean full drain
or manufacture evidence. Clear the active marker and stop the watchdog only
when the recorded outcome is fully reconciled.
