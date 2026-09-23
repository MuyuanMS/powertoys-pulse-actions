# PowerToys Issue Feature Implementation

Implement the selected feature plan for microsoft/PowerToys issue <IssueNumber>: <IssueTitle>
Target: https://github.com/microsoft/PowerToys/issues/<IssueNumber>

Deliver the feature defined by the Host's accepted saved plan. That accepted implementation task, with edit permission, authorizes its planned local work; a research recommendation alone does not. Use the inherited research as evidence without repeating the completed investigation.

## Implement against the actual requirements

Compare the plan's requirements, acceptance criteria and consequential assumptions with the provided source and candidate state. Resolve routine implementation choices using evidence and repository conventions. If a material choice would change promised behavior or scope, record the options and complete independent useful work before reporting the dependent work blocked. Missing tools are not proof of technical infeasibility.

Make the smallest complete change that satisfies the plan. Address affected integration points and material failure, compatibility and user-facing behavior. Reuse valid existing candidate work. Add or update focused tests that establish the new behavior or protect against an actual regression; avoid unrelated refactoring and tests that merely mirror the implementation.

Review the final diff and affected callers against the acceptance criteria, including relevant boundary and failure cases. Resolve confirmed regressions introduced within scope. Keep pre-existing defects, candidate defects and hypotheses distinct.

Feature implementation has no mandatory Bug reproduction gate. A description of existing behavior may support requirements without pretending that a reproduction was executed. A separate bug discovered during implementation should be reported with its effect on this feature, not silently expand the task.

## Validate the candidate and finish

Choose checks that substantiate the acceptance criteria and affected regression scope. Prepare inputs and prerequisites as work proceeds; use the Host's build timing rule. Run required actual scenarios where possible, reusing trustworthy matching-source evidence when sufficient. Repeat successful checks only when changed inputs or unresolved concerns justify it.

Tie observations to the candidate actually tested, including uncommitted changes or retained diff/artifact evidence. Compilation is not proof of interactive behavior. When a required check cannot run, preserve the candidate and report the precise prerequisite and usable verification plan.

The requirements check establishes the inherited plan and resolves consequential gaps. Implementation requires a completed and reviewed feature. Verification requires the applicable final acceptance and regression checks to have been executed, interpreted and satisfied. The workflow completes only when all three are met; a plausible implementation or feasibility conclusion is not a verified delivery.

## Report local delivery accurately

Explain what changed, how it meets the requirements, what was tested and what remains. Distinguish the local candidate, an available verified remote branch and actual upstream delivery. Local tests do not establish that the upstream Issue is resolved.

Use the Feature disposition only for supported requirements/feasibility conclusions, not as an implementation-progress state. Preserve the inherited conclusion's provenance; explain changed conclusions from new evidence. There is no separate feasibility card and no invented implemented Feature status. Leave Bug and unrelated PR assessments inapplicable.

Prefer inspection of completed local work. Propose a saved follow-up plan only for a meaningful remaining task. Create PR is appropriate only when an existing remote branch and its verified source SHA identify the committed candidate tested; it does not imply publishing uncommitted work. Closure needs independent disposition evidence, never merely local completion.
