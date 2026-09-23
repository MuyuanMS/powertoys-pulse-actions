# Acceptance status and historical validation records

## Local maintenance — 2026-09-16

The external PowerToys verification skill is now integrated into local PowerToys `main` and installed under the distinct personal name `pulse-powertoys-verification`. Pulse selects its optional runtime guidance by task scope; the updated Host is installed locally. The three current README guides were corrected in separate documentation commits. These changes have not been pushed or deployed to the website. See [runtime skill activation](runtime-skill-activation.md) for exact source revisions, loading evidence, validation and retained user data.

## Delivery baseline — 2026-09-15

Current behavior is defined by the [PR/Issue workflow agreement](pr-issue-workflow-proposal.md), [v3 wire contract](workflow-v3-wire.md) and [README](../README.md). The dated records below retain evidence from earlier versions; their old prompt synchronization, compatibility gates, result-only GitHub controls and test counts do not describe current behavior.

| Area | Verified status |
| --- | --- |
| Plugin implementation | PR/Issue v3 workflows and prompt maintenance merged and pushed to plugin `main` as `b33319c`; full reports, P0-only manual Approve restriction, scoped E2E and saved-plan tasks implemented |
| Current Host | Final Release build with zero warnings/errors and the complete Windows-apphost automated scenario suite passed; the ten published Native Messaging tests passed |
| Prompt contract | Eight embedded business prompts and one shared fragment; 26 actual assembled samples cover 13 profiles and both CLIs with unchanged full v3 schema and task context; new static reviews omit runtime guidance |
| Extension | 268 source tests and the recorded TypeScript/production/development builds passed for v3; later prompt maintenance did not change the extension source |
| Website integration | 171 source tests, TypeScript and a 15-page export using an isolated labelled fixture database passed; integration remains in its worktree and was not deployed |
| Local installation and package | Installed Host matched published files and all prompt sources; 58 existing configuration/task/prompt files retained their hashes; the refreshed ZIP's 258 entries were verified |
| External verification skill | Initially optimized in a separate PowerToys worktree; the local integration and activation above supersede that pending state. It remains separate from the Pulse Host package |

The exact package revision/checksum, source-size measurements and limitations are recorded in [workflow v3 validation](workflow-v3-validation.md) and [prompt maintenance validation](prompt-optimization-gpt6.md). The original scope's unchecked checklist is historical, not evidence that the implemented features remain missing.

## User testing and deferred scope

Plugin CI is not required at this stage, per the user's current decision. No plugin CI result or automatically generated release artifact is claimed; this does not change the website's existing deployment workflow.

The user will perform real-environment testing later. Keep these checks unverified until their actual evidence is recorded:

- Installed Chrome and Edge with the selected Codex/Copilot installations, including execution across full browser exit, reconnection, cancellation and completion while disconnected.
- Current GPT-6 PR/Issue workflows producing useful complete v3 reports and the intended next-step suggestions.
- Explicit GitHub submissions and uncertain-outcome recovery on an authorized test target; no experimental writes to PowerToys upstream are implied.

Native protocol tests, fake CLI workers and preview sample actions do not establish these results. Windows Job breakaway is restricted in the current command environment; the supervised test launcher does not relax the production guard. Older real CLI probes below establish their specific model-response/output checks, not complete acceptance of the new workflows.

Website publication remains separate and pending. The external PowerToys skill has been integrated locally as recorded above; publication of those PowerToys commits upstream is separate.

## Historical records

The sections below describe what was tested at each earlier checkpoint, including superseded behavior and diagnoses. Current requirements and deferred acceptance are stated above; historical counts and packages must not be used as the latest release status.

## CLI listing without compatibility decisions — 2026-09-11

The prior adjacent-helper diagnosis was wrong. Both standalone 0.145.0 and 0.154.0 contain their Windows helpers under the package's `codex-resources` directory, as declared by `codex-package.json`; they are not required to sit beside `bin/codex.exe`. The observed earlier helper-launch failure did not establish that the files were absent. The missing-component interpretations in the historical notes below are superseded by this correction.

Per the user's explicit request, discovery now only lists existing CLI executables and optional version/source/path metadata. There is no release allowlist, helper-location check, help/interface probe, or capability gate. A version-query failure does not disable a choice. Standalone current and retained previous releases are labelled separately, every detected version remains selectable, and Test remains optional without gating Save. Real execution errors are reported when the user runs the chosen CLI.

Final validation: Host Release build passed with zero warnings/errors, the complete offline suite passed, all 69 extension source tests and production/development builds passed, and 6 native smoke tests passed. A native-protocol check against an isolated data root listed the actual three Codex/two Copilot installations, kept every Codex selectable, and saved/reloaded current standalone 0.154.0 without executing a task. Browser fixture verification showed selectable 0.154.0/current, 0.145.0/history and 0.153.4/desktop rows with neutral Detected/Selected labels. Local Host and extension files were updated; all 30 existing configuration/task/log files retained their hashes. No real model task or GitHub write was performed for this change.

## Manual CLI selection and observed execution — 2026-09-11

Settings now lists every detected native Codex/Copilot installation, including incompatible entries with reasons, versions, sources, physical paths and aliases. Junction aliases are grouped by physical path. Users explicitly select installations; missing selections or unavailable saved paths block new tasks without fallback. Old-client saves preserve selections. Diagnostic tests may use the current unsaved listed choice and freeze it without saving settings. Inventory, selections and paths remain internal to extension pages.

The worker separately records actual model/effort from the exact Codex session's `turn_context`, or root Copilot session/usage events. Reading is bounded and correlated to the CLI session, without assistant self-identification or requested-setting fallback. Actual values are published while active and retained on completion; missing fields stay unknown. Task cards and details use observed values and retain requested settings in Technical details.

Active, failed and cancelled runs hide Results and PR actions. A completed result only offers its proposed operations after result validation and fresh GitHub target/SHA/permission intersection. Review writes require a matching review draft; verified E2E comments may use the task's pinned SHA without an inline review. Blocked or incomplete results show Needs attention, including compact task-list summaries. Prior submission history remains separate.

| Check | Result |
| --- | --- |
| Website | 116 tests, TypeScript, lint and static export passed; temporary cache fixture removed |
| Extension | 69 source tests passed, TypeScript and production/development/preview compilation passed |
| Host | Final Release build 0 warnings/errors; full offline scenarios passed for inventory, aliases, selection, frozen diagnostic path, metadata parsing, persistence and existing runtime behavior |
| Packaged Host | Self-contained publish and 6 native smoke tests passed |
| Actual local inventory | 3 Codex paths and 2 Copilot paths detected; isolated native-protocol checks verified blank initial selection, canonical save/reload, older-client save preservation and private defaults |
| Real active execution metadata | Read-only command run `6067127a-0d5b-49af-b870-ad11d09bd592` succeeded with exit 0; `gpt-6-astra / ultra` was observed from `codex-turn-context` and persisted before completion |
| Browser | Sample Settings retained a manually chosen installation through refresh/save/reload. Active details showed observed model/effort with Results and PR actions hidden. Blocked results had no PR controls; a valid review exposed only Post comment and Suggest changes after preview |
| Installed update | Host and loaded extension files refreshed; all 30 existing config/task/log files retained their hashes. User selections remain blank pending their explicit choice |

Evidence is retained in `.tmp/cli-selection-check-5a3eba55-df72-49b2-9aee-856fe506691c/verification.json` and `.tmp/cli-command-check/codex/runs/6067127a-0d5b-49af-b870-ad11d09bd592/command-verification.json`. Browser scenarios use sample data. The live command uses the test-only supervised launcher; it does not establish full browser-detachment or complete PowerToys review acceptance. No real GitHub submission was performed.

## Codex sandbox startup and frontend acceptance handoff — 2026-09-11

The reported PR #50100 run `26b290e9-8eb3-4c6a-806c-558a78739869` started its CLI and received model output, but every shell command failed because the selected 0.145.0 installation lacked both adjacent Windows sandbox executables. Its zero CLI exit and structured blocked result do not establish a completed review. The earlier model-response check never executed a shell command, so it did not cover this failure.

The Host now skips incomplete installations during automatic discovery and selects the first complete supported candidate; an existing task's saved executable path remains pinned. This machine has a complete, OpenAI-signed 0.153.4 installation, now accepted alongside 0.145.0 after interface and execution validation. No helpers were copied between releases, no CLI package was downloaded, and local permissions stayed read-only. The [official Windows sandbox documentation](https://developers.openai.com/codex/windows) was read; sandbox mode and account configuration were not changed.

The run also contained four `web_search` events with duplicate `item.id` keys. Event parsing now reads known fields through `JsonDocument`, while final task results retain strict validation. All 30 recorded stdout lines replayed without a parser exception; synthetic duplicate-event and invalid-final-result scenarios passed. The model-cache and Slack MCP messages were separate startup diagnostics; they did not stop the CLI from producing its final response.

| Check | Result |
| --- | --- |
| Website | 115 tests, TypeScript, scoped lint, and static export passed; existing empty-cache validation fixture removed afterward |
| Frontend behavior | Actual component browser fixture confirmed Run acknowledgment closes the dialog and leaves a global workflow/target notice with View task. No frontend task polling remains; unknown, failed and historical replies do not announce a new run |
| Host | Final Release build 0 warnings/errors, complete offline scenarios passed, including installation candidates, missing helpers, strict versions, frozen paths, parser and process lifecycle |
| Published native Host | Self-contained publish and 6 native smoke tests passed |
| Standalone real CLI | Complete Codex 0.153.4 executed a read-only file command successfully without the missing-helper error |
| Real Host runtime | Run `639ff454-f5cb-4471-8296-476aa390c085` selected 0.153.4 and executed exactly one read-only nonce command, exit 0. Output was saved before completion; final structured summary matched the file; nonce was absent from the prompt; no unexpected tools |
| Installed update | Updated Host discovery selects the complete 0.153.4 path. All 30 existing configuration/task/log files retained their hashes; the two existing user runs were preserved |

The runtime test retains `.tmp/cli-command-check/codex/runs/639ff454-f5cb-4471-8296-476aa390c085/command-verification.json`. It uses the test-only supervised worker launcher; the full browser-detachment matrix and a full PR review remain separate checks. The frontend stays available at `http://localhost:8080/prs`.

## Workflow presentation and historical recovery — 2026-09-10

The reported failure screenshots refer to run `aa8601f5-e844-4a1b-a408-2f130de427bb`, which ended at 14:29:16 local time before the Host upgrade. The installed data still contained only that one run. The webpage now presents separate workflow cards, one connection footer, collapsed run options, and an explicit previous-run heading/time. The extension details page prioritizes the outcome and recorded execution selections, folds paths/IDs/diagnostics, and omits null exit codes and repeated fallback messages.

Saved requests recover through read-only `tasks.get` / `tasks.lookup`; missing or unsupported lookup never automatically submits. Only an explicit send or Run again executes work. Late responses cannot replace a newer saved request, and a deleted-run tombstone permits an explicit fresh request without replaying the removed identity.

| Check | Result |
| --- | --- |
| Website validation | 106 tests, TypeScript and scoped lint passed; production static export passed with its pre-existing empty-cache fixture, then fixture rows were removed |
| Extension validation | 51 source tests and TypeScript passed; production/development packages and preview JavaScript compiled |
| Host validation | Release build passed with 0 warnings/errors; complete console suite passed, including native-framed missing/active/failed lookup, source isolation, immutable identity, tombstones and maintenance reads |
| Published package | Self-contained win-x64 publish used the configured host NuGet mirror; all 6 packaged native smoke tests passed with external runtime resolution disabled |
| Real Codex output check | One isolated read-only diagnostic succeeded with exit 0, a structured result, and 4 stdout events captured before completion; no user repository, full PR review, or GitHub write was executed |
| Visual verification | Actual localhost page showed the installation guide without an extension and returned focus to Actions after Escape. Real components with a simulated extension showed desktop two-column / phone single-column cards, previous-failure time, and editable model/effort options with no horizontal overflow. Extension preview showed folded technical details and omitted null/duplicate failures |
| Installed recovery | Updated local Host returned the original failed run through `tasks.lookup`; it did not create another task. Configuration/task/log hashes matched the earlier snapshot; only the existing view marker had changed after the user opened the run |

The diagnostic evidence is retained under `.tmp/cli-log-check/codex/runs/0848165a-73e4-445c-932d-69d3b28ba040/`. It verifies output capture through the runtime using the test-only supervised launcher; the full Chrome/Edge detached-worker matrix remains outside this check. The exact old failure trigger remains unconfirmed because 0.1.0 discarded its inner exception.

## Installed Host upgrade repair — 2026-09-10

The installed 0.1.0 browser connection remained alive after its task finished. The activity checker correctly returned idle, but the installer then failed moving the in-use `bin` directory. The installer now owns maintenance, checks activity, waits for configuration/prompt publication, verifies each connection's exact executable and arguments, stops only those idle connections, and retries replacement within a fixed bound. Rollback drains reconnects too and uses the preserved Host to check activity if the replacement binary is broken. Windows PowerShell 5.1 receives a true null backup filename for atomic JSON replacement; PowerShell 7 date values retain their full precision when checking the maintenance owner's identity.

| Check | Result |
| --- | --- |
| Standalone installer scenarios | 50 checks passed in Windows PowerShell 5.1 and 50 in PowerShell 7; real executable locks, scoped process shutdown, active/unreadable activity guards, publication locks, preserved data, metadata failure rollback, and corrupt replacement rollback |
| Test isolation | Temporary installation roots only; registry access disabled in fixtures; all fixture processes and directories removed |
| Actual installed upgrade | Repaired package installer completed successfully with development origins; only the verified idle installed connection was stopped |
| Installed native protocol | `hello` returned Host 0.2.0; `agents.defaults` succeeded; development origins enabled |
| Installed files and retained data | All 192 Host payload files matched the package; all 18 existing configuration, task, request, prompt and log files retained their SHA-256 hashes; both Chrome/Edge registrations verified; no staging/backup directories left |

The local 0.2.0 ZIP and expanded package were refreshed with the installer fix and per-file checksums. Host/extension binaries were reused from the preceding final build; this installer-only repair required no build, package download, model task, or GitHub write. Installed browser UI reloading remains separate from the native protocol verification.

## Workflow UX and execution settings — 0.2.0

The webpage now shows only extension workflows, automatically detects the extension, and offers installation guidance when absent. Codex/Copilot defaults and single-task overrides are frozen in accepted task snapshots; Tasks retains five recently finished runs with errors and detail/log links.

| Check | Result |
| --- | --- |
| Website tests / TypeScript / scoped ESLint | 97 tests passed; TypeScript and lint passed |
| Extension source tests / builds | 42 tests passed; production and localhost builds passed |
| Host Release build and full console scenarios | Passed, final build 0 warnings / 0 errors; includes immutable execution overrides and both fixture CLI argument contracts |
| Self-contained package checks | 6 native framing/startup/default-configuration tests passed with empty DOTNET_ROOT and multilevel lookup disabled |
| Output handling regression | Confirmed a held status-file handle no longer aborts raw output capture; fatal pipe/log failures preserve redacted stream, stage, exception type and HResult |
| Copilot interface compatibility | Explicit 1.0.79 and 1.0.83 allowlist plus all required interface flags; other versions and missing flags remain rejected. No real Copilot task was executed |
| Static website export | Passed with the same isolated one-row fixture for the pre-existing empty-cache dynamic-route requirement; fixture rows removed |
| Browser verification | PR #50027 dialog showed only extension detection/install guidance in a browser without the extension; sample Settings saved/reloaded model and effort; sample failed task remained visible directly on Tasks |

The user's installed 0.1.0 task was accepted and ran for about 22 seconds before failing with `CLI_EXECUTION_FAILED` / `CLI output could not be persisted`. The website separately rejected valid `null` error/result fields; that compatibility bug is fixed without resubmitting the task. All nine saved stdout lines and two stderr lines replayed successfully through the old parser. The old Host discarded the failing inner exception, so the historical trigger is still unconfirmed; the status-file contention regression proves a fixed fragility, not the exact cause of that historical run. Its files and installed configuration were not modified or rerun.

Task details report the model/effort passed to the CLI and their origin. An empty value is labelled CLI default, not inferred from a model's self-description. Codex's model/reasoning configuration mechanism is documented in the [official configuration reference](https://developers.openai.com/codex/config-reference/).

## Actions integration — 2026-09-10

Both repositories use dedicated `codex/actions-extension-integration` worktrees. Builds were performed only after implementation, during final validation. Dependencies were installed with lifecycle scripts disabled from the effective host npm mirror; .NET restore explicitly used `CODEX_HOST_NUGET_CONFIG`.

The user subsequently approved the official `WiseLibs/better-sqlite3` release as a scoped source exception for the missing Windows native binding. The existing `better-sqlite3` 12.10.0 package was completed with `better-sqlite3-v12.10.0-node-v147-win32-x64.tar.gz` for Node 26.4.0. Its SHA-256 matched the GitHub release metadata: `edfd78cba97618d25b0af9f7d5593f86de2fdf8342985f5ffa5f61274afac901`. The binding loaded and answered an in-memory SQLite query. No package or lockfile version changed.

| Check | Result |
| --- | --- |
| Host + console test project Release build | Passed, 0 warnings / 0 errors |
| Complete Host console scenarios | Passed, including new action readiness, template selection, typed website writes, durable intent, partial outcomes and read-only reconciliation; all GitHub writes and model executions were fixtures |
| Native Messaging smoke | 5 passed against compiled Host, including draft persistence across processes and standalone GitHub activity detection |
| Extension source tests and TypeScript | 33 passed; no-emit TypeScript passed |
| Extension development build + preview compilation | Passed; fixed unpacked extension ID verified against default installer registration |
| Website complete test suite | 80 passed, including 44 client/draft/UI-source checks; whole-site no-emit TypeScript and changed-file ESLint passed |
| Website complete static export | Passed after supplying one labelled local issue/utility fixture for the existing dynamic routes; all 15 static pages generated, including `/extension/install`. Fixture rows were removed afterward, restoring the empty local cache |
| Website existing database tests | All 9 previously blocked database tests passed with the approved, integrity-verified native binding |
| Installer maintenance guard | Confirmed active standalone action records block maintenance before invoking an older Host; completed records allow the idle check |
| Browser UI checks | Development and production-export installation pages rendered and missing-extension checks offered install/recheck; sample review confirmation reached completed; stale-HEAD merge stayed disabled; unknown sample result reconciled to completed without a resubmit control |

Browser UI checks used the local development site and extension sample preview. They did not install the extension into a real Chrome/Edge profile or exercise real GitHub writes. The earlier browser-detachment matrix below remains pending. The following sections are historical evidence from the earlier UI implementation.

The initial empty-cache static export failed because the existing `generateStaticParams` queries had no utility slugs. Validation used only temporary local rows, not a production data sync, and made no changes to database schema or route implementation. The successful export still logged missing local triage artifacts and server-rendered chart dimension notices with this minimal dataset. The generated `out/` is a validation artifact and was not deployed.

Date: 2026-09-08. Host/extension: `0.1.0`. Environment: Windows `10.0.26200.0` x64, .NET SDK `10.0.303`, Node.js `26.4.0`. Builds were reserved for final acceptance after implementation.

This update separates active Tasks from completed PR/issue results, adds local prompt synchronization and per-target selections, exposes persisted agent stdout/stderr, and makes extension-owned UI text English. The earlier automatic CLI detection, model tests, YOLO, no-timeout execution, PowerToys worktrees, and selected `gh` identity remain supported. PowerToys Pulse's own operations were not changed.

No publishing, release packaging, or GitHub writes were performed for this update. No model implementation/review work was run against real issues or PRs.

## Automated validation

| Check | Result and scope |
| --- | --- |
| .NET Release final-acceptance build | Passed with 0 warnings and 0 errors; test project and Host compiled locally |
| Host BCL console scenarios | Passed, including framing, Unicode, request limits, configuration, replay, recovery, cancellation, maintenance, and operation contracts |
| Local Native Messaging smoke tests | 3 passed against the locally compiled Host: multiple framed requests and EOF, registered extension identity, and oversized framing rejection |
| Split task views | Passed; active Tasks, terminal PRs, terminal issues, global counts, filtering before pagination, and numeric cursor/page handling |
| Raw CLI log contracts | Passed; fixed stream names, byte cursors, complete UTF-8 lines, partial tails, page bounds, oversized-line truncation, credential redaction, and persisted log limits |
| Prompt synchronization | Passed offline; pinned commit/tree/blob reads, exact UTF-8 and blob identity, atomic generations, failure preservation, unsafe paths/modes, case collisions, missing files, and size limits |
| Prompt selection and rendering | Passed; PR/issue defaults, target validation, unsupported variables, quoted title data, unchanged original task fingerprints, and immutable accepted snapshots after resync |
| Configuration and worktrees | Passed; PowerToys-only main checkout, fork upstream matching, linked-worktree/nested-root rejection, isolated run branches, captured HEAD, cancellation, dirty main preservation, and preserved worktrees |
| Agent tests and worker fixtures | Passed; fixed model question, actual assistant/exit-zero success criteria, log-only output rejection, both CLI adapters, persistence, child cancellation, and no execution deadline |
| `gh` and GitHub operations | Passed offline; account selection, per-command identity, credential isolation, stdin JSON, HTTP status, SHA/diff checks, write intent, deduplication, and uncertain-result reconciliation |
| Extension source tests | 16 passed, including internal prompt/log authorization, separate task views, English UI contracts, preview boundaries, and existing sender/reconnect/rendering checks |
| Local preview compilation | Passed; JavaScript emitted to `.tmp/extension-ui` without creating or refreshing a release archive |

Routine console tests use temporary data, fixture CLIs, and fake GitHub transport. The separate live checks below explicitly call authenticated local CLIs. Historical packaging/native smoke evidence is not treated as validation of a newly published package.

## Real CLI output evidence

Read-only model probes were run through the worker/output pipeline using the installed real CLIs. Both returned a model response and exit code zero. Persisted raw output was observed before process completion, not merely synthesized from final summaries.

| Agent | Version | Run ID | stdout bytes | stderr bytes | Structured stdout events |
| --- | --- | --- | ---: | ---: | ---: |
| Codex | `0.145.0` | `4b89e3bd-9f6d-4827-8f4d-2e285782b7d3` | 596 | 2,999 | 4 |
| Copilot | `1.0.79` | `7c508ada-c53a-49c0-a32e-1547db9c5355` | 37,059 | 0 | 83 |

The original checkpoint recorded `stdout.jsonl`, `stderr.log`, status/result records and `log-verification.json` under `.tmp/cli-log-check/<agent>/runs/<runId>/`, with `verified:true` and `capturedBeforeCompletion:true`. These are historical, Git-ignored local artifacts, not files shipped in this repository. Their original verification-record locations were:

- Codex: `.tmp/cli-log-check/codex/runs/4b89e3bd-9f6d-4827-8f4d-2e285782b7d3/log-verification.json`.
- Copilot: `.tmp/cli-log-check/copilot/runs/7c508ada-c53a-49c0-a32e-1547db9c5355/log-verification.json`.

These probes use the test-only launcher that can remain within the current Windows Job. They verify real CLI streams, responses, persistence, and exit status; they do not prove browser-independent production detachment. A model's reported name is a self-description, not independent proof of the configured provider model ID.

Settings also provide separate real `what's your model` tests without repository setup. Tests expose running/cancel states and pass only on assistant text plus exit zero. Versions, startup logs, and usage events alone do not pass.

## Real prompt synchronization and UI checks

The Settings synchronization flow used the selected `moooyo` local `gh` account and successfully saved all three source prompts:

- `powertoys-issue-local-fix.prompt.md`
- `powertoys-pr-e2e-test.prompt.md`
- `powertoys-pr-loop-review.prompt.md`

The synchronized revision was `397da449513f7ea8d9eacbd5dd76a635ef2d1667`. Local generation `5273b7f9b15749dd8132ab456505c736` is recorded in `.tmp/ui-preview-host/prompts/current.json`; its catalog and Markdown files are under the matching `generations/` directory. This was read-only against the public source repository and made no GitHub writes.

The preview was checked for active-only Tasks, completed-PR-only Pull requests, and completed-issue-only Issues. PR and issue next steps remain distinct. Extension-owned UI is English and retains the PowerToys Pulse visual style; user-supplied titles, prompt text, and CLI output are not translated.

The preview serves `extension/public` assets and `.tmp/extension-ui` JavaScript. Its diagnostic proxy runs `host/bin/Release/net10.0/Pulse.Host.exe`, with local data under `.tmp/ui-preview-host`. Only agent tests, account detection, and prompt list/get/sync invoke the real Host. Task lists, results, task logs, and saved form state in preview are sample/session data. Production uses Native Messaging and shared local records.

## Remaining browser and installation acceptance

This is the earlier acceptance matrix, with versions recorded at that time. The current user-testing policy above applies; use the actually selected browser/CLI versions for future evidence. None of the pending entries below is a claimed pass.

The current command environment restricts full Windows Job breakaway. Production retains `BACKGROUND_JOB_RESTRICTED` when worker detachment cannot be verified. The test fallback and diagnostic entry points do not relax that production guard.

| Browser | Agent | Browser version | Installed Native Host / PowerToys action | Continued execution after full browser exit |
| --- | --- | --- | --- | --- |
| Chrome | Codex 0.145.0 | Pending | Pending | Pending |
| Chrome | Copilot 1.0.79 | Pending | Pending | Pending |
| Edge | Codex 0.145.0 | Pending | Pending | Pending |
| Edge | Copilot 1.0.79 | Pending | Pending | Pending |

Use a dedicated PowerToys main checkout and external worktree root for these checks. Record extension IDs and browser/CLI/Windows/Host versions. Verify shared configuration, exact accepted snapshots, cross-browser replay and common-repository exclusion, isolated worktrees, and preservation of existing main-checkout changes.

Record run IDs, worker/CLI PID plus start identities, event sequences, and stdout/stderr cursors. Fully close the browser including background processes, then cover both reopening during execution and reopening after completion. The same run must restore output, result, exit status, and unread state without restarting the CLI. Also verify worker failure cleanup, cancellation during worktree preparation, and restart recovery without automatic reruns.

Installed-browser HKCU registration, maintenance blocking during active work, repair/upgrade rollback, uninstall preservation, managed-browser policy, and stable extension distribution IDs remain environment acceptance work. Existing installer scripts were not changed by this update. Any real GitHub write acceptance requires an explicitly chosen test target and user-authorized action.

## Local reproduction

Build only during final acceptance or when explicitly requested. These commands validate and preview locally; they do not publish or package:

```powershell
dotnet build tests/Pulse.Host.Tests.csproj --configuration Release
./tests/bin/Release/net10.0/Pulse.Host.Tests.exe
node --test tests/native-smoke.test.mjs
npm --prefix extension test
node extension/node_modules/typescript/bin/tsc -p extension --outDir .tmp/extension-ui
node extension/preview.mjs
```

Optional explicit model connectivity checks:

```powershell
./tests/bin/Release/net10.0/Pulse.Host.Tests.exe --live-agent-test codex
./tests/bin/Release/net10.0/Pulse.Host.Tests.exe --live-agent-test copilot
```

The optional commands call the user's signed-in model services. Ordinary automated scenarios use fixtures. Existing `examples/` and [protocol documentation](protocol.md) remain available for integration, while changes to PowerToys Pulse's own operations are outside this update.
