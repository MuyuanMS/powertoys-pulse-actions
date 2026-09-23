## PR verification evidence guidance

Use this guidance for the verification work selected by the Host. It is not an additional assignment or a requirement to run every repository check.

### Make the experiment informative

For each selected behavioral question, establish the starting state, inputs, observable expectation and failure signal. Check only prerequisites that matter: for example language resources, elevation, hardware, desktop access, configuration or the executable being tested. Distinguish a known-ready environment from an assumption.

Choose focused experiments and useful existing fixtures. Plan cleanup of temporary runtime state and retain the outputs needed to substantiate observations. Use the relevant build/test entry points and supporting dependency chain; avoid unrelated full-solution validation or repetition without changed inputs or an unresolved concern.

### Use evidence for the exact question

Reuse CI, author or prior-run observations only when the source, original/candidate subject, tested conditions and scope actually answer the question. Attribute inherited evidence. A green status, URL or assertion alone is insufficient; an installed binary of unknown provenance cannot verify the requested source.

Capture actual versions, conditions, observations and useful output or artifact locations. Report whether behavior matched expectations and whether the experiment itself executed correctly. Never infer UI or hardware behavior from source inspection, compilation or a written test plan.

### Interpret the whole result

Keep failed and contradictory observations alongside successful ones. Check coverage against all selected scenarios, not only passing cases. An understood product failure is evidence; missing infrastructure or uninterpretable output leaves the corresponding work unresolved.

Complete checks that can run within the accepted scope before suggesting a follow-up. For an external blocker, identify the missing condition and the preparation that would change the next attempt. Repeating the same unavailable environment is not a remedy.

Supplementary evidence does not rewrite the parent report or E2E necessity level. Apply evidence only to the source and behavior actually tested; local-candidate success does not repair an original-PR finding.
