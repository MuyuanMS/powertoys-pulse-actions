# Cloud review: bounded worker transitions

This is the authoritative review-loop policy. The coordinator schedules work;
it does not impose a second definition of convergence or request allowance.

## Original PR lifecycle/head gate (every round)

```powershell
$gate = .\scripts\Get-UpstreamPrReviewGate.ps1 `
  -PRNumber <upstream-number> -HeadSha <pinned-upstream-head>
```

Check on resume, after a returned cloud result and its processing, and before
another request, expensive validation or finalization.

| Gate action | Worker action |
| --- | --- |
| `stop_review` | Stop closed/merged/draft work, including obsolete polling/fixes/builds. Preserve history, safely release owned resources, notify coordinator to suppress old proposals. |
| `reconcile_updated_head` | Fetch/pin new upstream head and inspect its full diff/discussion; reassess findings, architecture and validation. Preserve request count and old evidence under its actual identity. |
| `continue_review` | Continue the assigned operation for this pinned version. |

A still-open fork never overrides upstream state. Do not wait for an obsolete
request before stopping/repinning. Never rewrite SHA fields on old evidence.
API/auth/malformed-response failures are explicit private errors; retry the
failed read with bounded backoff, not assumed-open progress.

## Resume or request

Run the saved request reader first when a request identity exists:

```powershell
.\scripts\Get-CopilotReviewStatus.ps1 -ForkRepo <fork-repo> `
  -PRNumber <fork-pr> -HeadSha <saved-fork-head> `
  -RequestedAt <saved-request-time> -AfterReviewId <saved-baseline-id> -AsJson
```

The reader exhausts REST review pages and matches the fork head/request/bot.
Use discovered history when an old timestamp is missing; do not invent one.
Submitted means arrived, not clean. An empty assignment is neither failure
nor success. A changed fork head requires identity reconciliation.

Only request a genuinely new round after reconciling actual requests used:

```powershell
$request = .\scripts\Request-CopilotReview.ps1 -ForkRepo <fork-repo> `
  -PRNumber <fork-pr> -RequestsUsed <reconciled-count> -TimeoutMinutes 0
```

Persist the returned fork `HeadSha`, `RequestedAt`, `AfterReviewId`, any
`ReviewId`/`SubmittedAt`, and increment consumed requests immediately.
If the command fails after submission may have occurred, recover GitHub/history
before any retry. A failed caller is not proof that no request was sent.

**Three total requests**, including initial request and timeout/no-analysis
attempts, across preserved lanes, sessions and upstream head changes.
The helper rejects missing/negative counts and counts >=3 before API access.
Unknown history must be reconciled, not assumed zero.

If no review has arrived, checkpoint `waiting_copilot`, return the exact
identity and release the worker/build slot. The coordinator checks at the
saved interval and resumes arrived results; no status-check re-requests.
Cloud remains required; a local reviewer is not an automatic substitute.

## Assess the returned round

Read the matching review body, all its published comments and all thread
cursors. Match by review ID rather than a guessed "newer than now" timestamp.
A startup warning may accompany real comments; inspect the actual result.
A substantive Lite review is not invalid merely because it is not full-tier.
A no-analysis timeout is not a clean pass.

Before first-round fixes, follow `architecture-review.md`: trace intent and
the pinned upstream mechanism. High comment count is a hint, never a verdict.
A proven goal/design mismatch ends new requests for that head and moves to
grounded overall drafting, not a speculative reviewer-authored redesign.

For each published concern, use `finding-grounding.md` to classify:

- Upstream defect: prove the failing path and remedy; fix localized valid code.
- Review-introduced regression: repair it; never export it as an upstream flaw.
- Already addressed, unsupported or optional: preserve specific counterevidence.

After local fixes, commit only intended changes and push the review branch.
Reply to fork comments with the fix commit or evidence-based disposition and
resolve addressed/rejected threads. Never resolve an unfixed real design flaw
solely to manufacture a clean count. If thread resolution fails, keep the
actual unresolved state; minimizing a comment is not resolving its thread.

Suppressed/internal candidates are advisory: inspect available important
correctness/crash/security/data-loss concerns, ignore speculative style noise.
They do not count as published comments or independently force another round.

## Choose exactly one next transition

| Condition after assessment | Next state |
| --- | --- |
| Grounded architectural flaw | Validate the supported concern, then draft; no more cloud requests for this head. |
| Fresh substantive zero published inline comments, zero unresolved threads, no later unreviewed fixes | `building`, then final drafting. |
| Fewer than three requests used, no clean result and localized iteration remains appropriate | Push fixes, recheck upstream, request the next round. |
| Three or more used | Assess ALL saved findings, then validate/draft supported output. Never wait for a fourth/zero-comment pass. |
| Actual external/API/build failure | Preserve work and exact error/remediation; coordinator repairs or independently adjudicates an exception. |

A review with supported findings is useful output even without clean
convergence. The cap ends new requests, not the review's finalization work.
Do not drop accepted earlier-round findings because the last round is empty.

For a clean zero-finding result, require the fresh zero-comment/thread gates,
context completion and passing exact-upstream validation. A divergent fixed
fork is not a clean original PR. Changes after a clean result invalidate that
clean claim; re-request only within allowance, otherwise finalize supported
drafts without claiming fresh convergence.

When no supported finding survives and clean evidence is absent, return the
honest private exception. Never invent a defect or silently use local-only
completion. Finish unaffected PRs.

## Finalize the net result

Account for every fork-only hunk relative to pinned upstream:
supported upstream correction, agent-introduced regression, rejected/optional
experiment or already upstream. Retain reasons and remove rejected experiments
with ordinary commits if needed; no shared-history reset.

Use `build-and-test.md` for focused validation and exact suggestion candidates,
then `drafting-and-posting.md` for the current diff/body format and validators.
For architectural prose, prove the concern; do not build an invented redesign.
Return the worker outcome and private receipts; the coordinator publishes.
