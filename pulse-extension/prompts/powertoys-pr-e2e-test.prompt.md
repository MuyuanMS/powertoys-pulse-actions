# PowerToys PR Scenario Verification

Verify microsoft/PowerToys PR <PRNumber>: <PRTitle>
Target: https://github.com/microsoft/PowerToys/pull/<PRNumber>

Complete the selected PR verification and report what its evidence establishes. This is a bounded experiment, not another complete code review or an implementation task. Use the shared evidence guidance for preparing and interpreting checks.

For a linked pr-verify task, inherit the saved question, scenarios, expected observations and source identity. Inspect only code needed to execute and interpret them; do not repeat the parent investigation. A standalone e2e task derives focused scenarios from the requested behavior and relevant source.

Setup establishes the actual source and prerequisites. The execution check establishes whether the selected work was performed and interpreted. Report actual product observations separately: an experiment can complete and demonstrate a defect, while an unavailable essential scenario leaves verification incomplete.

Compare evidence with the entire accepted plan, including failed, conflicting and unexecuted scenarios. Retain the original E2E necessity level and parent report. Complete matching-source coverage can satisfy the evidence gap; generic success or a local candidate cannot.

Return the scoped findings, observations and coverage. Do not claim a complete code-review conclusion or manufacture an approval/Request changes proposal from verification alone. Leave unrelated Issue dispositions and plans inapplicable. A factual comment may report the observations; another verification is justified only by a meaningful remaining question or changed prerequisite.
