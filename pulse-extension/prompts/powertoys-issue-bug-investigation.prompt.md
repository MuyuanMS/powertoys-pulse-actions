# PowerToys Bug Investigation

Investigate microsoft/PowerToys issue <IssueNumber>: <IssueTitle>
Target: https://github.com/microsoft/PowerToys/issues/<IssueNumber>

Investigate the reported bug and deliver one integrated conclusion with evidence and useful next steps. This research task does not implement a production fix. A focused disposable experiment or fixture may support the investigation within the accepted permissions.

## Establish facts and complete the Loop

Establish expected and reported behavior, versions, relevant environment and impact from the issue, available logs/attachments, source and tests. Investigate relevant existing fixes and duplicate reports. Distinguish the reported release, current upstream source and any local candidate; evidence for one is not proof about another.

Use an internal Loop within the issue's affected scope: form hypotheses from the facts, check them against source and feasible experiments, recheck every candidate finding, and investigate related omissions. Merge duplicates, remove disproven candidates and revisit conclusions affected by new evidence. Choose the order from the evidence rather than following a fixed sequence of commands.

Do not stop at a Top N, several findings or the first P0/P1, and do not turn this into an unlimited whole-repository audit. Finish when issue-relevant coverage is complete and no retained candidate awaits review. Running messages describe phase progress; deliver the full final finding list once. If interruption, resource limits or missing essential evidence/access prevent a supported research conclusion, report the investigation as incomplete and preserve established facts and remaining work.

## Keep different kinds of evidence distinct

Confirmation of a defect, knowledge of its root cause and actual reproduction are separate. Sufficient source evidence can confirm a defect without a full UI reproduction. Record actual reproduction as `reproduced`, `not_reproduced`, `not_run` or `blocked`, with the tested version, environment and observations. Not reproducing a symptom is not proof of `not_a_bug`.

Complete checks feasible in this task before proposing more verification. Ask for reporter-held facts only after examining what the available code, attachments and logs can establish. A required experiment belongs in a follow-up only when concrete environment, equipment or scope limits prevent it here. If information and verification are both missing, first obtain the facts that make the experiment meaningful and retain its plan.

## Give one Bug conclusion

- `confirmed`: explain the proven defect, impact, priority, causal evidence and practical repair. If this project cannot repair the cause, explain the constraint and workaround instead of automatically recommending a fix task.
- `needs_information`: identify essential reporter-held facts, why they matter and the precise questions that existing materials cannot answer.
- `needs_verification`: preserve established facts and unresolved hypotheses, with an executable reproduction/verification plan, expected observations and specific prerequisites.
- `already_fixed`: identify the existing upstream fix, commit or release and why it resolves this report. A local unpublished remedy is insufficient.
- `duplicate`: identify one verified original Issue and evidence that both reports concern the same problem.
- `not_a_bug`: explain expected versus observed behavior using design or configuration evidence and provide a supported resolution.

A fully investigated information or verification gap can be a complete research conclusion. Skipped available work cannot. Preserve every confirmed finding with its supported root cause and fix suggestion; state partial causal uncertainty honestly and keep unverified hypotheses outside the confirmed list.

## Complete the report and useful next steps

The context check establishes the issue and source facts; investigation assesses the relevant hypotheses; local-review records the final recheck and coverage. Completion requires those tasks to be performed with evidence, not necessarily a reproduced symptom or a repair.

Use the Bug assessment and its reproduction record; leave unrelated Feature/PR assessments inapplicable. Propose a saved repair or experiment plan only when it is useful and feasible. A follow-up task starts only after the user's selection.

Default advice follows the conclusion: a ready repair plan, specific inquiry, targeted experiment, upstream-fix explanation, duplicate association or expected-behavior guidance. For a duplicate, the assessment and close-as-duplicate proposal must name the same canonical Issue; the user may choose only a linking comment. Other closure recommendations need independent disposition evidence. Research completion alone is not closure evidence.
