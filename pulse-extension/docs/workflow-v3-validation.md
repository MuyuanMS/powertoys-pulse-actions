# Workflow v3 local validation — 2026-09-15

The approved [PR/Issue proposal](pr-issue-workflow-proposal.md) is implemented. The validation recorded below did not start real model tasks, submit real GitHub writes, merge branches or push repositories. Plugin implementation and prompt maintenance were subsequently merged and pushed to plugin `main` as `b33319c`; website changes remain in their task worktree by user choice. CI is deferred, and the user will perform real-environment testing later. See [current acceptance](acceptance.md) for delivery status rather than inferring it from historical validation steps.

| Check | Evidence |
| --- | --- |
| Host Release build | Passed with zero warnings and errors; `.tmp/workflow-v3-host-build.log` |
| Full Host console scenarios | Passed, including v1/v2 compatibility, all v3 conclusions, P0-only manual approval across task and website entry points, mixed feedback, duplicate partial/unknown recovery, phase-only progress, per-version transport, task-source freezing and candidate snapshots; `.tmp/workflow-v3-host-tests.log` |
| Large full reports | 720 findings round-trip through bounded pages and a >2 MiB completion journal; last P0 preserved; large diagnostics retained and paged; original files unchanged |
| Candidate continuity | Real isolated Git fixtures preserve binary/Unicode/new/deleted/type-changed files, verify content hashes and source identity, retain snapshots across parent deletion, and reject path/reparse/tamper/base mismatches; saved-plan to candidate-application integration passed |
| Extension TypeScript, production/development builds and source tests | Passed: 268 source tests, including the trusted result-page bridge; `.tmp/workflow-v3-extension-tests.log` |
| Published Host Native Messaging | 10 tests passed, including real v3 large-result frames, protocol isolation, exact embedded prompt source, and explicit manual confirmation with historical reports; `.tmp/workflow-v3-native-tests.log` |
| Website | 171 tests and TypeScript noEmit passed; isolated current-source production export generated 15 pages. Original database hash and Git state unchanged; existing port 8080 remained available. Evidence in website worktree `build/research-v3-*` |
| Installed Host | Updated while idle; schema 3 and new workflow capabilities verified, eight bundled prompts loaded, review readiness passed and the historical PR #50027 result remained unchanged; `.tmp/workflow-v3-installed-verification.json` |
| Existing data | All 58 existing task/configuration/prompt files retained their SHA-256 values; `.tmp/workflow-v3-userdata-before.json` |

Browser verification used explicitly labelled preview samples on localhost:4186:

- Final report precedes E2E and manual actions. Completed analyses remain labelled Completed, even with findings or recommended verification.
- A full 225-finding PR retains its final P0 and displays P0 first in priority groups. Loaded GitHub controls disable Approve for P0.
- P1 plus Required E2E keeps Approve selectable; explicit user selection survives refresh with selected feedback intact.
- P2/P3 shows Approve as the report advice, selects valid code feedback after target verification, defaults submission to Comment, and restores Approve when selection is cleared.
- Feature research presents one combined conclusion and saved plan. Starting that plan navigates to a separate Feature implementation sample; the sample was cancelled after the check.
- Bug information-gap and verification-gap samples show distinct final conclusions, reproduction facts, and the corresponding comment or saved-plan recommendation.
- Duplicate confirmation identifies the canonical Issue and frozen association comment. The partial sample continued only its closing step and reached Completed without posting another comment.

Limits: CLI-worker tests use the controlled supervised launcher where this environment prohibits Windows Job breakaway; live Chrome/Edge detachment was not retested. The browser task/GitHub writes above were simulated, not real workflows. Actual Chrome/Edge extension reload is unavailable through the in-app browser surface. Website production validation uses an isolated labelled fixture database because the local source database is empty; existing triage-cache and SSR chart-size warnings remain.

The local package and existing unpacked extension directories are refreshed after the final checks. The website main remains `89447fbc5326e9dd6cbfa7fe8488c1c248a0ee65`; changes remain in its task branch.

Original package validation: local revision `pr-issue-workflows-v3`, 258 ZIP entries and 248 copied build files, including the exact installed Host DLL. Original SHA-256: `bdb0eaf7abd192adf19608f0d9159eae5565706720cde50edcc4dbcb20a4f01b`. The package at `artifacts/Pulse-Extension-0.2.0-local-win-x64.zip` has since been refreshed by the [GPT-6 prompt maintenance](prompt-optimization-gpt6.md), which records its current revision and checksum.
