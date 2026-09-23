# PowerToys Issue Local Fix

Implement a local fix for microsoft/PowerToys issue <IssueNumber>: <IssueTitle>
Target: https://github.com/microsoft/PowerToys/issues/<IssueNumber>

Investigate the accepted defect, implement a focused repair and verify the candidate in this task's worktree. This is a requested fix task, not the public Bug Investigation entry point. Reuse an inherited investigation and plan when its assumptions and source match.

## Establish and repair the defect

Understand expected versus observed behavior, triggers and relevant source/tests. Resolve facts available in the issue, code and logs before reporting missing information. Keep confirmation of the defect, its causal explanation and actual reproduction distinct.

Establish a minimal baseline reproduction or regression test demonstrating the defect. Source reasoning can justify a candidate but must not be labeled an executed reproduction. When the baseline needs a build reserved for final validation, preserve its source and prepare the scenario, then compare baseline and candidate during final validation. Do not test repaired code and call that the original reproduction.

Make the smallest justified repair against the accepted behavior and criteria. Reuse valid existing changes, add meaningful regression coverage and avoid unrelated refactoring. Review the full resulting diff and affected callers for actual regressions, boundary cases and relevant compatibility or runtime effects. Recheck findings and resolve supported in-scope regressions.

If edit permission or a consequential unresolved behavior choice prevents repair, complete useful independent investigation and preserve the plan and concrete blocker.

## Verify and report

Execute the smallest set of checks that establishes the repair and relevant regressions, including a meaningful before/after comparison. Prepare checks as work proceeds and follow the Host's final-build rule. Keep unavailable runtime/device prerequisites and actual failures explicit; compilation is not UI or hardware verification.

Reproduction requires demonstrated baseline defect evidence; implementation requires a reviewed repair; verification requires the applicable final repair and regression checks to be satisfied. Unfinished required work leaves the fix workflow incomplete even when a patch exists. Preserve completed work and all confirmed remaining findings.

The Bug disposition concerns the evidenced Issue; actual reproduction has its own record. Explain what changed, what was verified locally and whether any corresponding fix exists upstream. A local repair does not make the original Issue already_fixed or resolved. Leave Feature and unrelated PR assessments inapplicable.

Prefer inspection of local changes and necessary verification. A useful later task references a saved plan. Prepare Create PR only for an existing remote branch whose verified SHA and committed tree match the tested candidate; unpublished work remains local. Closing requires separate evidence and a concrete reason, not successful local implementation.
