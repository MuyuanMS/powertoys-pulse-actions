# PowerToys PR Review

Review microsoft/PowerToys PR <PRNumber>: <PRTitle>
Target: https://github.com/microsoft/PowerToys/pull/<PRNumber>

Deliver a complete review of the original PR revision within the Host's accepted execution scope. Choose the investigation order from the actual change and evidence. Review does not authorize editing production code or applying the proposed fixes.

## Review the whole change and converge

Cover the complete current diff and affected paths using the relevant PR context, implementation, callers, tests and existing evidence. Investigate consequential behavior and risks, rather than applying every possible checklist to every change.

Use an internal Loop: examine the change, collect candidate defects, recheck each against the original revision, and investigate related omissions revealed by those findings. Merge duplicates and remove disproven candidates. Do not stop at a Top N, an arbitrary finding count, or the first P0/P1. New candidates must be rechecked before completion.

Finish only when the selected coverage is complete, all retained findings have been rechecked, and the final coverage pass leaves no candidate awaiting review. This is a finite completion condition, not proof that no hidden defect exists. Running messages give phase progress; deliver every confirmed finding once in the final structured report. Missing source, interrupted work or reporting limits require an explicitly incomplete report with retained evidence, never a partial list described as complete.

## Explain confirmed findings

Each finding explains the trigger, user impact, causal evidence and a practical fix. Use stable IDs and priorities P0 through P3. State any remaining root-cause uncertainty without hiding a defect already established by other evidence. Unverified hypotheses belong in limitations or verification questions, not the confirmed list. Do not infer P0 from historical high/medium/low labels.

Findings describe the original PR: a remedy in a local candidate does not mark the original defect fixed. Give each finding independent editable feedback. Add a code suggestion only for a precise replacement at a valid RIGHT-side position in the current diff; otherwise provide an ordinary comment. The extension combines feedback after the user selects findings and a GitHub action.

## Assess the need for E2E

Always provide an explicit E2E necessity assessment:

- `not_needed`: static reasoning and appropriate unit-test coverage establish the changed behavior; explain the basis and whether the tests actually ran.
- `recommended`: runtime evidence would resolve a meaningful remaining uncertainty.
- `required`: essential correctness depends on an actual scenario that static reasoning and unit tests cannot establish.

Do not classify by filename alone. Give the behavioral question, concrete scenarios with paired expected results, relevant prerequisites, existing evidence and truthful readiness. Missing old data is not evidence that E2E is unnecessary.

Necessity and execution are separate. Unselected E2E does not make a completed static review fail. Preserve the necessity level even when matching original-source evidence satisfies it. Keep failed, unexecuted and conflicting observations visible, and follow the Host's scenario reporting rules. One passing check or a tested local candidate cannot satisfy the whole original-PR plan.

A useful follow-up tests the saved question and scenarios without repeating the full review. Missing prerequisites call for specific preparation, not an unchanged Retry.

## Recommend the next step

Prioritize confirmed unresolved P0, then P1 feedback and repair, then missing required E2E evidence. Otherwise a completed review without P0/P1 may recommend Approve; P2/P3 and recommended E2E remain visible. When P1 and required E2E coexist, retain both even though feedback is the primary recommendation.

Recommendations and per-finding feedback do not determine the user's fixed GitHub controls. The Host owns the separate manual-action rules and confirmation.

## Completion and report

The context check establishes the original revision, diff and essential context. The local-review check records completion of the internal Loop. Additional checks come from the selected scope: they establish that the requested work was performed and interpreted, not that every product observation passed. Preserve actual product failures as findings and evidence; missing selected work leaves that workflow check incomplete.

Return the final PR report under the Host contract. Include the full confirmed findings, code conclusion and E2E assessment. Leave Feature/Bug dispositions and Issue plans inapplicable; PR follow-up uses the saved E2E assessment.
