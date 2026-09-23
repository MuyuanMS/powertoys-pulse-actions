# Bundled local workflow prompts

These repository-owned files are bundled with the Host for new local tasks. The approved behavior is described in [the PR and Issue workflow proposal](../docs/pr-issue-workflow-proposal.md). New tasks use the Host's complete v3 result schema; saved v1/v2 tasks retain their historical snapshots and meaning.

## Instruction ownership

The Host supplies the task's immutable target, source/plan provenance, selected scope, actual execution capabilities and authorization. Its common instruction block owns permissions, publication boundaries, PowerToys build timing and the general result protocol. Task-specific Host instructions own the authoritative required-check IDs, PR manual-action policy and direct/linked scenario reporting IDs.

Business prompts explain the task's objective, evidence standards, disposition semantics and completion conditions. They do not duplicate the full JSON schema, transport limits, capability matrix or permission policy. The shared PR fragment adds practical evidence guidance only when verification is selected; static-only review does not need those execution instructions.

Keep these sources agent-neutral. Codex and Copilot adapters own CLI arguments, structured transport and capability differences. Do not introduce a named-model prerequisite or another runtime skill-discovery/router layer. The Host selects and snapshots applicable prompt fragments; the optional local runtime skill is read by the agent only when its selected work needs it.

## Sources and action mapping

| Filename | Action | Purpose | Required checks |
| --- | --- | --- | --- |
| `powertoys-pr-loop-review.prompt.md` | `pr-review` | Complete PR review within the selected scope | `context`, `local-review`, plus selected verification |
| `powertoys-issue-feature-research.prompt.md` | `feature-research` | Integrated need, existing-capability and feasibility research | `requirements`, `existing-capabilities`, `feasibility`, `next-step` |
| `powertoys-issue-bug-investigation.prompt.md` | `bug-investigation` | Complete Issue investigation and internal recheck | `context`, `investigation`, `local-review` |
| `powertoys-issue-feature-implement.prompt.md` | `feature-implement` | Implement the selected saved Feature plan | `requirements`, `implementation`, `verification` |
| `powertoys-issue-local-fix.prompt.md` | `issue-fix` | Repair and verify an accepted Bug | `reproduction`, `implementation`, `verification` |
| `powertoys-issue-reproduction-setup.prompt.md` | `reproduction-setup` | Run an experiment and preserve accurate instructions | `reproduction`, `instructions` |
| `powertoys-issue-verify.prompt.md` | `issue-verify` | Execute a saved Issue verification plan | `setup`, `verification` |
| `powertoys-pr-e2e-test.prompt.md` | `e2e` / internal `pr-verify` | PR verification without repeating code review | `setup`, plus `e2e` or `build-tests` |
| `pr-verification.shared.prompt.md` | Shared fragment | Focused experiment and evidence guidance | Follows the accepted task |
| `runtime-skill.shared.prompt.md` | Shared fragment | Optional `pulse-powertoys-verification` runtime techniques | Follows the accepted task; adds no checks |

PR scopes remain distinct: static reads code and existing evidence without executing builds/tests/UI; build-tests adds relevant builds and automated tests without actual UI operation; ui-e2e covers selected runtime scenarios with only necessary supporting builds/tests. The Host preserves historical unrecorded scope instead of silently relabeling it.

The runtime skill fragment is omitted from static/build-tests PR tasks and Feature Research. It accompanies UI/E2E work, historical reviews, Bug Investigation, Issue Local Fix, Reproduction Setup, Feature Implementation and Issue Verification. For historical reviews and Issue work, the agent activates it only if the accepted work actually runs PowerToys; a code-only plan or a proposed future experiment does not activate it. It refers to an optional local installation named `pulse-powertoys-verification`, leaves installation outside task execution, and does not substitute the older `powertoys-verification` skill when absent. The Host does not scan for, load or embed the installed skill. Agents read relevant resources on demand and record the resolved path and source commit from `source.json` (or content hash) in existing report evidence. Host scope, permissions, source identity and JSON reporting remain authoritative.

## Business rules that must survive maintenance

- PR Review covers the complete current diff and affected paths. Bug Investigation covers the complete issue-relevant scope. Both use an internal Loop, recheck every candidate, investigate related omissions and stop only after their declared coverage converges. Neither stops at a Top N or the first P0/P1.
- Running output gives phase progress. One final structured report contains all confirmed findings; incomplete coverage or output is disclosed rather than presented as a complete review.
- Findings use stable IDs, P0-P3, impact, trigger, supported root cause, evidence, a practical fix and independent editable feedback. Causal uncertainty does not erase a proved defect. Hypotheses are not confirmed findings, and historical severities do not imply P0.
- Workflow completion and product judgment remain separate. Completed review/verification can reveal a defect. An unselected check is not missing selected work; an unexecuted required check is not passed.
- PR E2E necessity is explicitly not_needed, recommended or required. Necessity is separate from fulfillment. Preserve all relevant scenario observations and original-source identity; a local candidate, generic success or partial coverage cannot satisfy the full original-PR plan.
- Advice prioritizes unresolved P0, P1 feedback, then missing required E2E. Other completed reviews may recommend Approve while retaining P2/P3 and recommended E2E. The Host's fixed manual-action policy remains independent: only confirmed unresolved P0 on the current original PR adds a business restriction to Approve; technical target/account/SHA and submission-state checks still apply.
- Feature Research returns one integrated ready / needs_information / needs_decision / already_supported / duplicate / not_feasible disposition. Feasibility is part of that research, not a separate status/card. Ready does not authorize implementation or imply maintainer acceptance.
- Bug Investigation returns confirmed / needs_information / needs_verification / already_fixed / duplicate / not_a_bug. Actual reproduction is recorded separately. Available investigation stays in the current task; reporter-held information and externally blocked experiments are different gaps.
- Feature implementation has no Bug reproduction gate. A bounded reproduction or verification task does not repeat broad investigation or repair production code.
- A duplicate identifies one canonical Issue consistently across assessment and proposal. The user may send only an association comment. Ordinary closure needs independent disposition evidence; local repair, reproduction or verification success never implies upstream resolution.
- Follow-up tasks use saved plans and Host-bound source identity. Create PR uses an existing verified remote branch and does not imply commit/push. GitHub writes and proposed next tasks require their separate extension confirmation.

## Editing and verification

Keep objectives and completion criteria clear without forcing one command sequence or unnecessary rereading. Remove duplicate instructions from the layer that does not own them; do not replace complete review with sampling or weaken evidence requirements to reduce text.

Only `<PRNumber>`, `<PRTitle>`, `<IssueNumber>` and `<IssueTitle>` are supported substitutions. The Host quotes titles as data and preserves rendered prompt provenance. Do not add unsupported placeholders, competing schema fields, model-specific requirements or arbitrary executable action content.

Verify the rendered instruction assembly for every workflow and PR scope, including the absence of runtime skill guidance from static/build-tests PR tasks and conditional use for Issue plans. Exercise structured-result and follow-up fixtures for both existing adapters. Every appended fragment contributes its source SHA-256 to the bundle revision and immutable task snapshot; the rendered body has its own hash. Installed skill content is external to that prompt snapshot, so the execution report records the skill version actually read. Compare source and rendered sizes separately; this README is maintenance documentation, so shortening it is not a runtime-context saving.

## Historical provenance

The original PR Review, Issue Local Fix and PR E2E sources were adapted from the locally stored snapshot of `MuyuanMS/powertoys-pulse-actions`, revision `05d4dcebff3f73a3340d48cc65e4a160836761a8`, synchronized on `2026-09-10`. Their seed blob hashes were `f4157843e2e7fbda36a8a9b842aef3697cf74ba0`, `52121d85ecf62f5a6ee4a5ad4435c1691dbdd9c6`, and `ac50859eae6fd6453616d088b1ba55e2355b4add`. Reproduction Setup originated in the former bundled Host instructions.

These are seed identifiers, not current content hashes. Current sources and shared fragments are maintained here, bundled and snapshotted by the Host. Editing them does not rewrite historical tasks, reports or user selections.
