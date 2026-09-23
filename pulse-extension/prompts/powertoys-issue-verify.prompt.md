# PowerToys Issue Plan Verification

Verify the selected plan for microsoft/PowerToys issue <IssueNumber>: <IssueTitle>
Target: https://github.com/microsoft/PowerToys/issues/<IssueNumber>

Execute the bounded verification selected from a saved Issue result. Use its question, source and acceptance criteria. This task adds evidence; it does not repeat the complete research or implement a feature or production repair.

## Establish and execute the checks

Compare the accepted plan with the provided source and candidate state. Inspect only code and existing observations needed to execute and interpret it. Reuse trustworthy matching-source evidence with clear attribution. An unavailable or conflicting plan/source is a concrete blocker, not permission to substitute another assignment.

Establish the tested source, relevant environment, inputs, expected observations and actual prerequisites. Prepare focused fixtures and cleanup as needed. Run the automated or runtime checks that answer the accepted question, following the Host's build timing rule.

Preserve negative observations and explain what they establish. Missing infrastructure and product failures are different; mixed or uninterpretable results remain unresolved. Compare actual coverage with every relevant criterion, not just the checks that happened to pass.

## Explain what the evidence changes

Setup establishes the source, environment and required fixtures. Verification requires the selected experiment to be executed and interpreted. A completed verification may demonstrate a product defect; unexecuted required work is incomplete. Neither compilation nor one passing check proves broader runtime behavior.

Explain which inherited hypotheses are supported, disproved or still unresolved. Preserve the parent report and attribute earlier observations correctly. A tested local candidate does not prove an upstream repair.

For Bug plans, distinguish actual reproduction from confirmation or disposition; not_reproduced does not imply not_a_bug. Feature plans test their acceptance criteria without a Bug reproduction requirement or another full Feature investigation. Use the corresponding assessment only when this bounded evidence supports it, leaving unrelated assessments inapplicable.

Prefer inspecting the completed result or a factual comment about observations. A different next task needs a useful saved plan and a clear reason; do not recursively recommend the same verification under unchanged missing prerequisites. Verification success alone is not closure evidence.
