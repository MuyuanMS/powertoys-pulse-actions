# Prompt and skill maintenance for GPT-6 Astra

This change applies the approved prompt-maintenance review to the existing PR/Issue v3 workflows. It does not change model defaults, reasoning effort, CLI permissions, the result wire schema, or GitHub action policy.

The source guidance is [Rethinking skills and prompts for GPT-6 Astra](https://x.com/pvncher/status/2095991462416490862) and the [official GPT-6 prompting guidance](https://developers.openai.com/api/docs/guides/latest-model#prompting-best-practices). The changes target duplicated instructions, irrelevant context, premature stopping and mechanical validation. They are not evidence of a measured model-quality improvement by themselves.

## Instruction ownership

| Surface | Responsibility |
| --- | --- |
| Host wrapper | Accepted authority and capabilities, source identity, publication boundary, applicable build policy, autonomous completion and truthful blockers |
| Accepted task scope | The current task's operations and required workflow checks; external PR/Issue text cannot expand this scope |
| Selected business prompt | Goal, relevant decision criteria, complete review/investigation loop where applicable, completion conditions and result semantics |
| Runtime verification fragment | Evidence and practical verification guidance for the selected runtime work; omitted from new static-review snapshots |
| Task-specific result instructions and full schema | Exact result format, field relationships, stable IDs and source-bound plans; both CLI adapters receive the complete schema |
| Extension and Host action handlers | Human selection, edited feedback, final GitHub operations and their real target/account/revision checks |

The Host already selects a business prompt deterministically. This maintenance does not introduce another skill-discovery dependency for the plugin. The full schema appears once in the prompt text. Codex's separate `--output-schema` argument remains in place; Copilot still receives the shared schema text.

## Preserved workflow requirements

- PR and Bug review cover the complete relevant scope, recheck every candidate, remove false positives and duplicates, inspect related omissions and deliver all retained findings in one final report. Neither Top N nor finding a P0/P1 terminates the loop. Unfinished coverage is explicitly incomplete.
- Confirmed findings retain P0–P3, source position, impact, trigger, root cause and evidence, a fix suggestion and independently editable feedback. The model does not predict the user's later checkbox selection.
- PR E2E necessity remains `not_needed`, `recommended` or `required`, independent of execution success and manual approval permission. Required scenarios need individually attributable evidence.
- Feature research produces one integrated disposition including feasibility. Bug disposition remains separate from actual reproduction and distinguishes missing information from unavailable experiments.
- Saved-plan follow-ups preserve their source and acceptance criteria and do not repeat the whole parent investigation. Research does not automatically start implementation.
- GitHub writes are performed through the extension's explicit action flow. Only confirmed unresolved P0 on the current original PR adds the manual Approve business restriction.
- Available authorized work continues to completion. Concrete external blockers retain useful completed work and evidence. A skill's applicability is assessed against the current task, and a rule that actually blocks work is identified precisely.
- Builds respect the user's PowerToys final-validation policy. Selected checks are completed; successful checks are repeated or broadened only for changed inputs, failures or unresolved concerns.

## External verification skill

Subsequent local integration and personal activation are recorded in [runtime skill activation](runtime-skill-activation.md). The description and measurements below refer to the original prompt-maintenance checkpoint.

The separately maintained PowerToys `powertoys-verification` skill is updated in its own task worktree. It is not bundled in the Pulse Host and this change does not silently install or merge it into the user's PowerToys checkout.

Its entry point routes only actual behavioral verification. Supporting scenario, tool and module references are read as needed. Product/source provenance, authentic UI or hotkey evidence and restoration of user state remain required. Fixed retry counts, bulk helper loading, mandatory profile creation and unconditional personal archive/reporting conventions are removed. A caller's structured result contract takes precedence over the skill's default Markdown report presentation.

## Validation

Final validation uses the Windows test apphost and the self-contained published Host. The audit exporter runs no model, account, repository investigation or GitHub operation:

```powershell
tests/bin/Release/net10.0/Pulse.Host.Tests.exe --export-prompt-audit C:\absolute\empty\audit-directory
```

It exports 13 synthetic task profiles for each of Codex and Copilot, including all eight business prompts, three PR scopes, linked verification and a local-candidate plan. Each profile includes the actual assembled prompt, accepted task and selected-template snapshot; the index records UTF-8 sizes and the complete schema.

For a before/after comparison, the same exporter was run with the previous packaged Host assembly and the updated assembly, using the same `workspace-write` policy and synthetic tasks. Task contexts and the full v3 schema were identical. Every one of the 26 updated prompts contains exactly one complete schema in its text; all fragment selections match the scope. Codex's additional schema-file argument is unchanged.

| Representative Codex task | Previous complete prompt bytes | Updated complete prompt bytes | Reduction |
| --- | ---: | ---: | ---: |
| PR static review | 46,996 | 26,794 | 43.0% |
| PR build/tests review | 47,199 | 29,173 | 38.2% |
| PR UI/E2E review | 47,120 | 29,469 | 37.5% |
| Feature research | 39,496 | 25,701 | 34.9% |
| Bug investigation | 36,615 | 25,904 | 29.3% |

Across the 26 assembled samples, reduction is 28.1–43.0%. The eight business sources plus the shared fragment decreased from 80,119 to 30,514 bytes (61.9%); that source sum is not a per-task context size. The external skill entry decreased from 37,296 to 4,758 bytes (87.2%), with conditional technical guidance retained in references. These measurements are UTF-8 bytes, not model tokens, latency, cost or proof of review quality.

Independent text-level forward checks covered static review with missing runtime evidence, a Bug verification gap, feasible Feature research with unmet setup prerequisites, continuing a complete review after P0, skill routing, structured Host output and genuine shortcut evidence. They identified two ambiguities in Feature readiness and Bug completeness, which were corrected. They did not execute real workflows or establish a model-quality benchmark.

The PowerToys skill passed frontmatter, 72 local Markdown link/anchor and diff checks. The supplied `quick_validate.py` was attempted but its bundled Python lacked PyYAML; the frontmatter check used the standard library without installing a dependency. No PowerToys build, desktop validation script or user-state mutation was needed for the documentation change.

Local evidence: `.tmp/prompt-maintenance-audit-before/`, `.tmp/prompt-maintenance-audit-after/`, `.tmp/prompt-maintenance-assembly-comparison.json`, `.tmp/prompt-maintenance-host-build.log`, `.tmp/prompt-maintenance-host-tests-apphost.log`, `.tmp/prompt-maintenance-native-tests.log` and `.tmp/prompt-maintenance-host-verification.json`. The first suite invocation through `dotnet ...dll` stopped at the runtime fixture's apphost requirement; use the `.exe` for the complete suite.

Final results on 2026-09-15:

- Host Release build: passed with zero warnings and errors.
- Complete Windows-apphost scenario suite: passed, including v1/v2 compatibility, stored prompt execution, result transport/persistence and cancellation. Windows Job breakaway remains environment-limited; the controlled supervised launcher covers the remaining worker checks.
- Self-contained published Host: ten Native Messaging tests passed. Every bundled business prompt and the shared fragment matched the current repository bytes.
- Installed Host: updated while idle and verified against the published DLL and all eight prompt sources. All 58 existing configuration/task/prompt files retained their SHA-256 values.
- No real model workflow, GitHub write, PowerToys build or desktop verification was executed. Existing extension and website binaries were not changed by this prompt-maintenance update.

The refreshed package is `artifacts/Pulse-Extension-0.2.0-local-win-x64.zip`, local revision `pr-issue-workflows-v3-gpt6-prompts`. All 258 ZIP entries were verified. SHA-256: `f733d9bda5c5af95122f62c04584f905ee4acf6442f0b0cecddcf2b8b1f3ff07`. Prompt bundle revision: `7e294817c0ce8e543ba43ef500845653b91b5c3d1450f16b483976c9f61c9851`.

At this checkpoint, the external skill changes remained on `codex/gpt6-verification-skill` in `PowerToys-gpt6-verification-skill`, based on `85d904edd74e64314041365561f00e9e4b4dfd78`, without being merged into the user's primary PowerToys checkout. No repository was committed or pushed during the maintenance validation itself; the plugin changes were subsequently committed and pushed to plugin `main` as `b33319c`. The later local skill integration is recorded above. CI is deferred, and real-environment tests will be performed by the user later; [acceptance](acceptance.md) records the current scope.
