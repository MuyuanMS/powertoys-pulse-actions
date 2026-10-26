# First-round intent and architecture review

Do not turn a fundamentally wrong approach into a larger reviewer-authored
rewrite merely to obtain a clean cloud review. Before fixing the first
returned review's comments or requesting round two, check whether the upstream
design can actually deliver the PR's stated outcome. Start this investigation
earlier if the description, linked issue or source already reveals a concern.
Apply it to every author type, not only community process reviews.

## Signals are not findings

Read all pages of the matching first review's comments and its body. Group
distinct findings by failure mechanism before judging their volume. An unusually
large first-round response, related failures across components, repeated
workarounds or conflicting state/ownership assumptions are investigation hints.
Duplicates, style nits, suppressed candidates and PR size do not establish
an architectural defect. There is no comment-count-only rejection threshold.
Conversely, one demonstrated goal mismatch is enough to investigate; a
zero-comment cloud response does not prove the design works.

## Investigate independently

1. Extract the promised behavior and acceptance criteria from the PR, linked
   issue, discussion and author validation. Separate explicit requirements
   from reviewer preferences; do not invent product requirements.
2. Trace the pinned upstream implementation end to end: entry points,
   callers/consumers, data/state ownership, lifecycle, persistence and error
   paths relevant to the promise. Check existing abstractions and invariants.
   Do not limit investigation to the lines the cloud reviewer mentioned.
3. Test a concrete counterexample: which supported scenario still fails, why
   the changed path cannot fix it, or which existing invariant is broken.
   Reproduce on the exact upstream head when feasible. Static proof can be
   sufficient for a deterministic unreachable path or violated contract; record
   what it proves and what remains untested. Compile-only is not behavior proof.
4. Read counterevidence, existing guards, intended scope and the author's
   rationale. Distinguish a broken approach from a localized bug, reasonable
   tradeoff, optional refactor or product decision needing clarification.
5. Identify the root cause and smallest coherent remedy. Determine whether
   localized fixes preserve the intended approach or whether it needs a
   different integration boundary, ownership model, algorithm or design.

Follow `finding-grounding.md` for every public claim. Cloud comments are leads,
not authority. Do not demand a replacement architecture based on volume alone.

## Decision and early exit

Keep a private `architecture` record beside the per-PR request ledger:
`head_sha` (upstream), `status` (`pending`, `cleared`,
`changes_required`, or `insufficient_evidence`), triggers, review IDs,
deduplicated findings, intended outcome, traced paths, counterexample,
counterevidence, verification results/limitations and next action.

- `pending`: pause new requests and speculative local fixes while investigating.
- `cleared`: no supported design-level defect remains; continue localized fixes
  within the existing three-total-request allowance. Record why noisy feedback
  did not establish a broader flaw.
- `changes_required`: the design-level defect is grounded. Stop further cloud
  requests for this head, ideally after round one. Assess/dispose all received
  findings, preserve request history and draft the consolidated concern now;
  do not implement a speculative wholesale redesign to unlock drafting.
- `insufficient_evidence`: do not publish an architectural accusation. Record
  the unresolved question privately, continue other independent grounded work,
  and investigate or seek narrowly targeted clarification. Do not spend another
  cloud round simply to settle an unverified design hypothesis.

Do not repeat the same architecture investigation on every dispatch. Finish
the current focused trace with a disposition: clear an unsupported hypothesis,
record a grounded flaw, or identify the specific missing external evidence for
coordinator adjudication. A preference for another design is not a reason to
keep `pending` forever or block localized review output.

Consume any request already in flight without duplicating it. On resume, these
decisions survive worker replacement and scheduler ticks. A changed upstream
head requires rechecking the evidence, not carrying an obsolete accusation
forward; the consumed request count still never resets.

## Author-facing output

Draft one comment per independent root cause, consolidating cloud symptoms
under the larger flaw rather than dumping all first-round comments. Include
`Affected code`, `Problem`, `Why it matters`, `Suggested change` and
`Verification`: name paths/symbols, connect the promised result to the actual
failure, give a specific supported scenario and a coherent corrective direction
with acceptance checks. Distinguish a proven defect from an open design choice;
do not present an unimplemented alternative as a tested fix.

Prefer a meaningful current RIGHT-side inline anchor when it exists; otherwise
use a normal PR conversation comment with a concrete out-of-diff reason.
Never force a multi-component redesign into an unsafe apply-ready suggestion.
Retain independent localized findings only if still relevant after the proposed
design correction; avoid redundant symptom comments.

This is an explicit exception to mandatory fix/push/re-request convergence and
to requiring a reviewer-implemented replacement before drafting. Validate the
defect with the smallest relevant tests/trace; a needless full fork rewrite or
build is not a prerequisite for a source-proven architectural comment. Existing
exact-patch, EOL and build gates still apply to any literal suggestions.
Never claim clean approval or auto-close/reject the PR. Dashboard drafts use
the existing pinned `post_review`/`COMMENT` action at
`awaiting_review_approval`; a deliberate blocking submission still requires
explicit approval. Keep counts, early-exit reasons and workflow diagnostics
private. A draft is not posted author feedback and does not create author-wait.
