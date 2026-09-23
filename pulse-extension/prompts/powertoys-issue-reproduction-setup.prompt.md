# PowerToys Issue Reproduction Setup

Reproduce microsoft/PowerToys issue <IssueNumber>: <IssueTitle>
Target: https://github.com/microsoft/PowerToys/issues/<IssueNumber>

Prepare and execute a minimal reproduction, then retain accurate instructions and observations. This assignment does not repair production code. If a saved plan is supplied, execute its question and source scope rather than repeating the parent investigation.

## Run the useful experiment

Use the relevant Issue evidence, logs, code and tests to identify reported versus expected behavior and the conditions that matter. Design the smallest informative experiment: input and starting state, expected observation, failure signal and prerequisites. Check relevant runtime, language/resource, device or desktop access before relying on it.

Reuse suitable fixtures or prepare a disposable fixture, script or instructions file when permitted. Run the experiment and narrow or repeat it only when needed to interpret the result. Distinguish a product defect, missing prerequisite and broken setup; retain negative observations rather than hiding unsuccessful attempts.

Complete verification available in this assignment. An external blocker needs a precise prerequisite and preserved partial evidence, not a generic unchanged Retry. Follow the Host's build timing rule; a build, source inspection or written plan does not constitute runtime reproduction.

## Finish with reproducible evidence

Record actual reproduction as reproduced, not_reproduced, not_run or blocked, with the tested source, environment, steps and observed versus expected behavior. Not reproducing the symptom is limited evidence, not proof that the Issue is not_a_bug or resolved. Sufficient independent source evidence may still establish a defect.

Write rerun instructions from the actual setup and verify their accuracy, including inputs, evidence locations and safe cleanup. When file creation is not permitted, keep the instructions in the result instead of inventing an artifact.

The reproduction check measures execution and interpretation of the experiment, not whether it demonstrated the defect. The instructions check establishes an accurate retained procedure. A well-executed negative reproduction can complete this workflow; missing essential steps or uninterpretable results cannot.

Report confirmed findings and supported Bug conclusions without claiming a complete broad investigation. A factual comment may explain observations and limitations. Recommend another saved experiment only for a concrete unresolved question or changed prerequisite. Closure still needs independent disposition evidence.
