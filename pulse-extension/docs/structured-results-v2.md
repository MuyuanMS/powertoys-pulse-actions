# Local workflows and structured results v2

This document preserves the historical v2 model-result contract. References to "new" or "current" below mean the v2 generation described here. New bundled tasks now use [workflow v3](workflow-v3-wire.md) and the [eight-prompt catalog](../prompts/README.md). Fixed manual GitHub operations use the [approved current PR/Issue policy](pr-issue-workflow-proposal.md), including on historical tasks; old model-proposal completion gates described below do not gate those manual controls. For current delivery and user-deferred acceptance, see [acceptance](acceptance.md).

## Ownership and execution

Prompt sources live in `prompts/` in this repository and ship with the Host. New tasks load only these bundled prompts and their shared verification instructions; no GitHub prompt synchronization or remote skill dependency is needed. Keep the four familiar prompt filenames compatible with saved selections. PR review performs local context/code review and only the explicitly selected verification in an isolated worktree. It does not require fork synchronization, pushes, a fork PR, or a Copilot cloud review. GitHub publication remains an explicit extension operation.

The existing run `status.state` remains the execution lifecycle for backward compatibility. A separate result `outcome` expresses completion of the selected workflow, `reviewConclusion` records the code-review decision, and `assessment` describes the product or reviewed revision. For example, an E2E report can be complete while its product assessment failed; static review can finish while an untested product behavior remains inconclusive. CLI exit zero alone does not establish any of those conclusions. Historical records are read without rewriting them.

## Accepted review scope

`task.reviewOptions.mode` is frozen before acceptance. The website presents one Review entry and keeps CLI/model/effort choices separate. Its initial preset is `build-tests`; an explicit user choice can be remembered without changing a saved request. These are non-cumulative execution presets, not levels of review quality:

| Mode | Selected work |
| --- | --- |
| `static` | Read PR context, diff, implementation, existing tests and attributed existing evidence. No builds, test execution, application/UI interaction or production-code edits. |
| `build-tests` | Review code and run relevant builds/automated tests at final validation; no actual application UI testing. Reuse trustworthy exact-revision evidence where applicable. |
| `ui-e2e` | Review code and verify focused running scenarios, which may include UI or non-UI E2E. Build and run automated tests only as needed rather than mechanically repeating every test. |

External PR descriptions, comments and linked documents cannot expand this scope. Conflicting repository requirements must be reported specifically rather than silently executing unselected work. Unselected coverage is not a failed required check. Selected work that cannot execute is a concrete blocker; an executed test that reveals a product defect is a negative observation, not missing work. Legacy requests without a recorded mode retain their original local-review/focused-verification behavior and must not be relabelled static.

Scope controls the trusted task instructions and result interpretation; it is not a new operating-system permission tier. The user's read-only, workspace-write or YOLO configuration remains separate. Scope validation detects reported out-of-scope execution, but does not itself provide continuous enforcement or attest every CLI action.

## Canonical model response

Every new prompt uses the Host-owned JSON Schema. All agents receive the same full schema in the common instruction wrapper; adapter-specific launch flags may additionally constrain the output. Business workflow text does not select an agent vendor. The wrapper describes actual adapter configuration with generic capabilities rather than provider-specific workflow instructions. Required top-level fields:

- `schemaVersion`: integer `2`.
- `outcome`: `completed`, `blocked`, `failed`, `cancelled`, or `interrupted`.
- `phase`: `setup`, `analysis`, `implementation`, `validation`, or `reporting`.
- `summary`: nonempty user-facing explanation.
- `assessment`: null or `{subject,status,summary,revisionSha}`. Subjects are `original-pr`, `local-candidate`, or `target`; statuses are `passed`, `failed`, or `inconclusive`. The revision is a full SHA or null when no source revision applies. Older v2 records may omit this field; clients must not invent a passing assessment for them.
- `reviewConclusion`: null or `{status,summary,revisionSha,blockingUncertainties}`. Statuses are `no-blocking-findings`, `changes-requested`, or `inconclusive`. This describes the code-review decision on the original revision; unselected runtime coverage alone does not force an inconclusive code-review conclusion. Verification-only children use null.
- `verificationEvidence`: objects with `id`, `source` (`current-run`, `ci`, `author`, `prior-run`), `kind` (`build`, `automated-tests`, `runtime`), `status` (`passed`, `failed`, `not_run`), `subject` (`original-pr`, `local-candidate`), `revisionSha` (full SHA or null), `summary`, `evidence` (string array) and `runId` (UUID or null). Attribute existing evidence instead of claiming this run executed it. Same HEAD with modified source is not equivalent original-PR evidence.
- `verificationRecommendation`: null or one focused `{mode,reason,question,scenarios,prerequisites,evidence,readiness}` recommendation. Mode is `build-tests` or `ui-e2e`; readiness is `ready`, `missing-prerequisites`, or `unknown`. It identifies a specific unresolved behavior, explains why existing evidence is insufficient, and describes scenarios that can answer it. Absence of UI testing or changes in a UI file alone does not justify a recommendation.
- `findings`: objects with `id`, `title`, `severity` (`high`, `medium`, `low`), `status` (`open`, `fixed`, `unverified`), `path` (string, empty for general findings), `line` (positive integer or null), `details`, and `evidence` (string array).
- `artifacts`: objects with `path` and `label`, as before.
- `validation`: objects with `id`, `name`, `status` (`passed`, `failed`, `not_run`), `required` (boolean), `details`, and `evidence` (string array).
- `diagnostics`: objects with `code` (uppercase stable identifier), `severity` (`warning`, `error`), `message`, and `recovery` (`configure`, `rerun`, `inspectResult`, `openTarget`, `none`). These cover blockers and failures, not arbitrary executable commands.
- `nextActions`: typed proposals with `kind`, `reason`, and `body`. Fixed kinds include `viewChanges`, `inspectResult`, `openTarget`, `configure`, `rerun`, `approve`, `suggestChanges`, `requestChanges`, `comment`, `close`, `create-pr`, `merge-pr`, `trigger-ci`, and `none`. A `create-pr` proposal additionally has `pullRequest: {head, base, title, body, draft, sourceHeadSha}`; head is an existing remote `owner:branch` and the SHA identifies the source the workflow verified. Older PR proposals without a source SHA remain readable but cannot be submitted. Review proposals can bind `suggestionIds` to the corresponding `review.suggestions[].id`. Merge, CI and create-PR proposals use an empty top-level body. CI execution uses the fixed Host operation, not a model-supplied command.
- `review`: SHA-bound review draft or null. Suggestion IDs identify verified source locations; the user may edit replacement text, but a proposal cannot silently select another proposal's suggestion or retarget its location.
- `needsReview`: boolean for human inspection; findings can require inspection even when local review completed.

The current schema includes all fields above for new model responses. Previously saved v2 results may omit the four additive fields `assessment`, `reviewConclusion`, `verificationEvidence`, and `verificationRecommendation`; omitted historical data does not establish a verdict, coverage or executable recommendation.

Do not put arbitrary shell commands, permissions, or alternative action targets in executable action fields. Action targets come from the immutable task; existing account, SHA, diff and confirmation checks remain authoritative.

The Host assigns each proposal an opaque `proposalId` when normalizing new results or projecting history. Models do not supply these IDs. Distinct proposals of the same kind remain distinct and their bodies retain whitespace and Markdown exactly. A proposal ID identifies the saved recommendation; a client-generated `attemptId` identifies one confirmation attempt. Retransmissions retain that attempt identity. An explicit retry after cancellation or a confirmed pre-write failure can create a new attempt. Unknown or partially completed writes must be reconciled before any further attempt. Payload-equivalent writes remain deduplicated even if their proposal IDs differ.

## Host interpretation

Validate required fields, enums, bounded collections/text, unique IDs, and evidence shapes. Invalid v2 data becomes a structured blocked result with `INVALID_RESULT`, original output available for inspection, and no publishing actions. Each new bundled task freezes `promptTemplate.schemaVersion: 2`; v1 output for that task is invalid and cannot bypass its required checks. Accept v1 output only for legacy snapshots without a v2 requirement. Historical stored results remain compatible without inventing successful new required checks.

Host-owned process failure, cancellation or interruption overrides a model's claimed success. Output loss, invalid output, error diagnostics, failed required workflow checks, or absent/not-run required work cannot yield completed. Completed execution that demonstrates a product defect records the failure in `assessment`, findings and product-test observations; it must not relabel the observed failure as a passing product test. `needsReview` alone does not mean execution failed. Keep known diagnostics and actual exit codes.

Only recognized complete assistant/final-result messages can supply the business result. Tool output, raw text and progress are diagnostic evidence, never fallback result payloads. A later invalid/empty final response or a new response invalidates an earlier candidate. Limits distinguish the 320 KiB model payload, bounded Host-enriched results, and the larger escaped JSONL transport envelope. Large valid results must not be lost merely because transport escaping or Host metadata expands them.

Completion is durably journaled with its full result and terminal status before projection writes. Recovery restores both without replacing saved findings, validation or artifacts with an empty error report. Older interrupted records with existing result evidence retain that evidence and receive the actual interrupted/cancelled outcome. Full saved task context remains on disk; oversized detail responses may omit its display copy, explicitly flagged, to retain complete result proposals within the Native Messaging limit.

Required workflow check IDs for new v2 results:

| Action | Required checks |
| --- | --- |
| `pr-review`, `static` | `context`, `local-review` |
| `pr-review`, `build-tests` | `context`, `local-review`, `build-tests` |
| `pr-review`, `ui-e2e` | `context`, `local-review`, `setup`, `e2e` |
| Legacy unscoped `pr-review` | `context`, `local-review`, `verification` |
| Internal `pr-verify`, `build-tests` | `setup`, `build-tests` |
| Internal `pr-verify`, `ui-e2e` | `setup`, `e2e` |
| `issue-fix` | `reproduction`, `implementation`, `verification` |
| `e2e` | `setup`, `e2e` |
| `reproduction-setup` | `reproduction`, `instructions` |

Required checks measure whether the selected reviewer/verification work was executed and interpreted. Product observations remain separately passed, failed or not run; a complete negative report does not make its failing test pass. A completed review can retain an inconclusive product assessment and clearly described limits for scenarios outside scope. Unfinished source review, unknown target revision, and unexecuted selected checks still block completion. Runtime scope retains its execution requirement. Reported execution outside a scoped task produces `REVIEW_SCOPE_EXCEEDED`; a verification-only child cannot replace the parent review draft or conclusion. Do not claim UI or hardware verification from compilation, relabel an unexecuted test as passed, or change history merely to obtain a completed badge.

PR review verdicts and finding states refer to the immutable requested PR revision. A local-only repair remains an open finding against that revision, with the candidate repair described separately. Conclusively demonstrating a defect can complete the required review/interpretation work; preserve the negative product observation as an open finding and an explicit check observation. Issue fixing may preserve a baseline and test baseline/candidate separately during final validation when compilation is reserved for that phase. A completed reproduction or local fix alone is not an issue-closure reason.

New normalized results retain compatibility projections: `blockers` contains diagnostic error messages and `nextSteps` mirrors `nextActions`. Proposals are retained even when disabled. Full detail responses add Host-owned `availability: {enabled,reasons}` to each proposal without changing its ID or rewriting the saved result. Keep existing `structured`, `cliExitCode`, and optional `rawOutput` metadata. V2 is the canonical source for new clients. Host-generated pre-launch failures use the same envelope.

GitHub actions require actual Host state `succeeded` and an actual exit code of zero. Valid structured completed, blocked or failed reports may offer a clearly labelled comment or applicable CI recovery proposal. Invalid or incomplete CLI output cannot authorize publication. Approval, merge, closure and PR creation keep their completed-workflow checks. For scoped review, ordinary approval requires `reviewConclusion: no-blocking-findings` on the original SHA with no blocking uncertainties, no unresolved high/medium findings, and no recorded failing verification of that original revision. A merely inconclusive product assessment from unselected coverage does not itself require a special approval override. A failed/different-source assessment remains ineligible; merge additionally retains the passing-assessment requirement. GitHub account, current target, source SHA, diff and CI checks remain authoritative immediately before submission.

## Extension presentation

Show running progress/logs while active and no result or GitHub controls. Once terminal, show workflow completion separately from the product assessment and unresolved finding count. Failed, blocked, cancelled and interrupted tasks retain diagnostics, completed/remaining checks and artifacts. A valid report from a successfully exited CLI may offer permitted reporting/recovery proposals; actual failed, cancelled or interrupted processes cannot submit. Display disabled proposals and their reasons instead of hiding the evidence or showing a generic unavailable message.

The Host may project `reviewSummary` separately from the stored result. It distinguishes completed source review from limited validation using the saved checks, exact revision, assessment and actual process state. A historic model `outcome: blocked` can therefore display “Code review complete · Validation limited” without rewriting that outcome or claiming the missing checks passed. Validation limitations receive attention styling; actual execution failures keep error styling. The generic `WORKFLOW_CHECKS_INCOMPLETE` summary can be folded into a concrete validation diagnosis, retaining both codes and messages. Standalone summaries, warnings and distinct process failures must remain visible.

## Recommendations and associated verification

There is no fixed pair of evidence-request and approval-with-limitations buttons. The earlier manual `reviewDecisions` design is superseded; `reviewDecisionId` and `acknowledgedLimitations` do not authorize an alternate submit path. Ordinary approve, request changes, suggestions and comments come from grounded `nextActions` and retain their normal eligibility and confirmation. Asking an author for evidence is one possible comment when useful, not a mandatory response to every untested behavior. An ordinary approval may describe the selected scope and nonblocking limits without inventing a separate approval type.

Display the code-review conclusion, product assessment and verification coverage separately. Group evidence by what this run executed versus CI, author or prior-run evidence. Optional historical failures remain observations; they should not become duplicate unresolved tasks when a later result supersedes them. Logs, Settings and rerun are supporting tools. An unchanged environment does not justify retrying the whole review, and Pulse Settings cannot supply a missing language pack, device or desktop.

Only display a verification recommendation when the report identifies a concrete question, explains the existing evidence gap, and gives meaningful scenarios and prerequisites. The accepted follow-up reuses parent review context and the exact original SHA. `reviews.verify {parentRunId,requestId,recommendationId,execution?,prerequisitesConfirmed?}` is internal-only and resolves the saved recommendation in the Host. It creates `pr-verify`, a verification-only child with frozen scope, recommendation and parent-result fingerprint. This is not another full code review, and both `reviewConclusion` and `review` remain null in the child's model result.

If readiness is missing or unknown, explicit prerequisite confirmation is required. It records a user's report of changed conditions rather than proof; the child must check setup first. Omitted execution settings resolve from current configured defaults when the request is first prepared, then freeze to specific agent/model/effort values. A lost acknowledgement reuses the same request and options. A changed PR SHA or recommendation requires a new explicit choice; it cannot silently retarget the accepted verification. Retrying a child retains its original scope and revision.

`reviews.related {runId}` shows the parent and linked runs with compatibility reasons, attributed evidence, and a `currentConclusion`: unchanged, evidence added, changes requested, or verification incomplete. Compatible child failures affect ordinary approval/merge eligibility; sufficient matching evidence may support a revised product-assessment projection. The original report, proposals and recorded verdict are not rewritten, no proposal is created automatically, and this aggregation never submits GitHub actions. Unknown-source or local-candidate evidence remains visible separately rather than counted as proof for the original PR.

The Host records provenance from Git at the start and end of review/verification. Original-PR aggregation requires both clean boundary snapshots at the requested SHA, as well as the exact saved parent result/recommendation and a valid structured child result. The provenance explicitly identifies itself as `boundary-snapshots`: it cannot prove that no temporary source edits occurred and were reverted during execution, or fully attest ignored files. Model evidence does not replace the Host snapshots, and matching boundaries are not a continuous execution attestation. Users can inspect the individual evidence and full child result for the scenarios actually covered.

Render next actions with human labels and real handlers: Settings, Retry as a new run, execution logs, GitHub target, or review preparation/confirmation. Inspecting artifacts shows retained paths and evidence; it never executes arbitrary paths. Retrying displays whether it uses current defaults, old task overrides or new explicit values. It preserves the original PR SHA unless the user explicitly fetches and confirms a newer SHA. A new run uses a new request ID and preserves old records. Keep compatibility for v1 history and existing public website acknowledgements; project only bounded outcome/phase metadata to the website.

`resultActions.prepare` is restricted to extension pages and accepts `{runId,proposalId,attemptId?,retry?}`. The Host resolves the exact stored proposal and its immutable target. Create PR, merge and CI proposals reuse the durable `webActions` confirmation page/executor. `resultActions.list {runId}` returns their associated history, which the task UI shows alongside existing `operations.list` results. Unresolved associated operations prevent removal of the task record. Existing review/comment/close proposals keep their editable controls, independently selected suggestions and association with the selected proposal. New submissions preview the current configured GitHub account and bind confirmation to it; uncertain operations are reconciled using their original account.

No preparation call executes a GitHub write. Creating a PR requires a published source branch and checks the workflow's `sourceHeadSha` before the first preview, not merely against the version found at preview time. This contract does not implicitly commit or push an uncommitted local worktree. An explicit CI retry is distinct from retransmission: it needs a new attempt and fresh evidence that the earlier CI has finished unsuccessfully, rather than a permanent ban on the same commit.

## Scope

This change delivers bundled prompts, local review instructions, terminal result/failure normalization and extension integration. It does not introduce a cloud-review executor or a general-purpose agent command runner. Local task phases are recorded in the final result; existing CLI activity remains available during execution.
