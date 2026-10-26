# PR-only drain: coordinator execution contract

Read this with the dashboard entry point. Review decisions belong to the PR
skill; this file defines dispatch, persistence and delivery only.
Do not load issue workflows, Outlook or project synchronization for this path.

## One owner, one queue, one publisher

Recover existing `run_id`, `owner_session_id`, scope, suite revision, workers,
requests and build owner before starting. Check actual worker/process state;
an old timestamp alone does not authorize a second owner. Preserve useful
branches, commits, receipts and request counts.

Default new PR-only drains include all areas. Use `-ExcludeCmdPal` only for an
explicit non-CmdPal scope, consistently on discovery and final verification.
Drain mode has no selection limit/deadline; it does not enable issue work.
Default six active distinct-PR workers, explicit maximum eight; no nested
workers. Resume idle workers for the same PR only. Idle agents are not capacity.

Run `Get-StalePrReviewQueue.ps1 -Dashboard <canonical-main> -AsJson`.
Persist a live snapshot and one PRIVATE ledger outside public `data/`:

```json
{
  "prs": [{
    "number": 50000,
    "head_sha": "0123456789012345678901234567890123456789",
    "phase": "reviewing_findings",
    "worker_status": "idle",
    "worker_id": "known-worker",
    "round": 1,
    "next_action": "Assess saved review 123, then fix valid localized issues."
  }]
}
```

`round` means actual requests used across preserved lanes/sessions. Unknown
history must be reconciled, never initialized to zero. A live head mismatch
requires the PR skill's upstream gate and repinning, not an old-state resume.
Keep running workers outside the queue tracked until safely reconciled.

## Execute until no runnable work remains

```powershell
.\scripts\Get-PrReviewDispatchPlan.ps1 `
  -QueueJsonPath <live-queue.json> -StateJsonPath <private-ledger.json> `
  -MaxConcurrency 6 -AsJson
```

On each completion or due check: save results, execute coordinator actions,
publish newly ready results, execute assignments into free slots, then replan.
An unchanged queue never justifies idle runnable work.

| Planner action | Execute and save |
| --- | --- |
| `reconcile_resume`, `recover_request_identity`, `reconcile_fork_head`, `reconcile_missing_review`, `reconcile_updated_head` | Worker recovers the exact existing lane/request, then resumes the PR skill. No blind re-request. |
| `check_saved_review` | Read-only paginated `Get-CopilotReviewStatus.ps1`; save actual status/IDs/time. Arrived results become `reviewing_findings`. |
| `process_review`, `investigate_architecture`, `draft_architecture_review`, `finalize_review_limit` | Worker executes the PR skill's assess/exit rules. Move supported output to `building` or `finalizing`, not another generic resume. |
| `validate` | Run saved focused commands under the single build slot; retain exact-input receipts; then `finalizing` on success. |
| `finalize_artifact` | Complete grounding/schema checks, then coordinator publishes and verifies delivery. Do not leave it in finalization after successful publication. |
| `revalidate_context` | Read new same-head discussion; retain/withdraw the decision or escalate when substantive evidence warrants it. No full loop merely to renew timestamps. |
| `diagnose_cloud_wait` | Diagnose the saved request once; save `review.diagnosed_request_at` equal to its `request.requested_at`, plus evidence and next action. Continue checks or adjudicate an actual external failure. |
| `repair_blocker_evidence`, `repair_local_prerequisite`, `revalidate_operator_exception`, `publish_terminal_blocker` | Repair local work or verify a PRIVATE exception. The last name is legacy; never publish an operational blocker. |
| `reconcile_build_owner` | Check the specific owned process/lock; release only when actually finished. Never kill unrelated processes or steal a live lock. |

Coordinator actions do not occupy heavy worker slots. One exclusive compilation
at a time; do not hold it for cloud waits, inspection or drafting. Finalization
and ready validation get priority. Verify unchanged receipts rather than
rebuilding solely because a worker/context changed.

After two passes without a substantive transition, execute the missing command
or diagnose the exact failure. Do not create more planning/grant packets.
Ordinary authorized fork/local operations require no per-step permission.

## Waiting is a saved request, not a stopped drain

Save `request.fork_repo`, `fork_pr`, fork `head_sha`, `requested_at` and
`after_review_id`. Save reader results under `review`: `checked_at`,
`head_matches`, `submitted`, `request_pending`, and submitted review identity.
Upstream and fork heads are distinct.

Use the read-only reader, not the requester, for status. Checks default to
120 seconds, outside worker/build slots. After ten minutes and a fresh status,
the planner schedules one diagnosis per request. Time alone does not authorize
another request or prove a service failure. Inspect actual review content:
a startup warning can coexist with substantive findings.

On cloud wait or build-slot wait, persist state and release the worker.
Immediately use the slot for another PR. If every remaining PR is waiting,
confirm a real next wake, not just a promise to resume.

## Worker handoff

Pass upstream number/head, configured fork and existing lane, private saved
state/receipt locations, suite revision, assigned action and normal-run deadline
if any. Tell workers to load `powertoys-pr-review`, execute directly without
nested agents, and return its outcome contract.
Fork pushes/replies/resolution are allowed; dashboard writes and all upstream
writes are not. Do not paste this entire coordinator guide into every worker.

The coordinator saves worker results before reuse/replacement. Never mark
`worker_status: running` based only on an old agent's existence.

## Delivery and exceptions

Follow [publication.md](./publication.md). Checkpoint privately immediately;
publish completed artifacts immediately, other substantive pending changes
after two transitions or five minutes. No full generator on unchanged ticks.
Publication errors retain validated output for retry; do not restart reviews.
Record the published commit/artifact head after verifying canonical delivery.

Local planning gaps, interrupted runnable builds and unused slots are not
external exceptions. If all supportable findings are delivered but a real
external/review-policy decision remains, independently verify and save:

```text
operator_exception:
  head_sha, source_updated_at, category (external|review_policy),
  detail, remediation, evidence[] and verified_at
```

The planner parks matching verified exceptions, and revalidates them after
head/activity changes. It checks shape/freshness, not truth. Exceptions remain
incomplete coverage; no automatic local-review fallback or fabricated finding.

Refresh live scope and reconcile every PR with its actual published artifact.
Only use `-FailOnStale` when no unfinished PRs/exceptions remain.
`exhausted_with_exceptions` means INCOMPLETE, never drain success.
Return one coverage table: PR/live head, delivered artifact/action, justified
author wait, excluded state, or private exact remaining exception.

## Schedule handoff

Keep the regular schedule's cadence. With no active drain it runs a bounded
all-area update INCLUDING CmdPal; with a drain it services the same owner/run.
Do not start another drain, issue batch or competing publisher.

Use completion notifications for immediate refill. If needed, keep one
five-minute drain watchdog that executes dispatch and recovers handoffs, not a
passive idle report. Persist the next check and active owner across rollover.
Stop the watchdog after the final reconciled outcome; retain the normal schedule.
Only the owning session changes its schedules.
