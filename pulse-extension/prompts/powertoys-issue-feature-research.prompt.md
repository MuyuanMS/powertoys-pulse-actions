# PowerToys Issue Feature Research

Research the feature request in microsoft/PowerToys issue <IssueNumber>: <IssueTitle>
Target: https://github.com/microsoft/PowerToys/issues/<IssueNumber>

Deliver one integrated investigation of the user's need, existing capabilities, technical feasibility and appropriate next step. Research does not authorize production changes or implementing the feature. A ready plan is neither maintainer acceptance nor permission to start development.

## Investigate the need and feasible options

Separate the user's underlying problem and explicit requirements from a suggested implementation or your assumptions. Examine the relevant issue evidence, existing documentation and responsible code. Determine what already exists, how users reach it, its prerequisites and the actual gap. Distinguish released capability, current source and proposed work.

Check relevant duplicate requests and ongoing implementation using available evidence. Compare behavior and underlying need, not titles alone. Incomplete search is a limitation, not proof that no related work exists.

Assess the proposed behavior against affected architecture, platform capabilities and material constraints. Investigate cross-cutting concerns when they affect the requirements or acceptance criteria, rather than applying every concern to every feature. Prefer existing mechanisms when sufficient.

Resolve questions that available source, documentation and issue history can answer. Reporter inquiries should concern material facts unavailable from those sources. For consequential product decisions, explain concrete options and tradeoffs instead of silently changing scope.

Develop a focused plan only when implementation is warranted: intended behavior, affected components, acceptance criteria, meaningful steps and necessary verification. A decisive unavailable experiment should leave a precise question and prerequisites. Missing tools, access or equipment do not establish technical impossibility. Research does not require a full implementation or runtime campaign.

## Give one integrated Feature conclusion

Feasibility is part of this conclusion, not a separate workflow state or card.

- `ready`: requirements are clear, an implementation plan is technically supported, and acceptance criteria and verification prerequisites are identified, including any unmet conditions.
- `needs_information`: identify the missing implementation-relevant facts, why they matter and an editable inquiry. Do not use this for an unresolved product decision.
- `needs_decision`: describe the consequential choice, alternatives, tradeoffs and effects on the plan or acceptance criteria.
- `already_supported`: identify the existing capability and version, conditions, usage instructions and any difference from the request.
- `duplicate`: identify one verified original Issue, the shared requirement and meaningful differences. Similar titles or ongoing implementation alone are insufficient.
- `not_feasible`: explain why the requested behavior cannot be implemented under the current requirements and platform constraints, with evidence and viable alternatives. Difficulty, preference or incomplete research is not technical impossibility.

If essential investigation is unfinished and no disposition is supportable, report incomplete analysis with the permitted null assessment. Do not force a positive or negative conclusion.

## Completion and next steps

The requirements check covers the need, scope and consequential gaps. Existing-capabilities covers relevant current behavior and related work. Feasibility records investigation of architecture and platform constraints, not another product judgment. Next-step records the evidence-backed plan, question, decision or explanation appropriate to the conclusion. There is no Bug reproduction gate.

A fully investigated information gap, decision, existing capability, duplicate or infeasibility can be a complete research result. Missing essential source or skipped available investigation cannot. Return the integrated Feature assessment and any confirmed findings; leave unrelated Bug/PR assessments inapplicable.

A ready assessment references its saved feature-implement plan. Other conclusions may recommend an inquiry, decision discussion, usage guidance or alternatives. Duplicate assessment and close-as-duplicate proposal must identify the same canonical Issue, with the option to send only an association comment. Ordinary closure needs independent disposition evidence. Keep implementation and GitHub actions as recommendations for the user's separate selection.
