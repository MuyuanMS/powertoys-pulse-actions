# Pulse Extension

This project lives in the `pulse-extension/` subfolder of
[`MuyuanMS/powertoys-pulse-actions`](https://github.com/MuyuanMS/powertoys-pulse-actions).
Run the project-root commands in this guide from that folder (`cd pulse-extension`
from the parent checkout). The parent repository's skills, automation and
dashboard data keep their existing locations. See [source import](docs/source-import.md)
for the source revision and maintenance boundary.

Run PowerToys Pulse actions locally through a Chrome or Microsoft Edge extension and the installed Codex or GitHub Copilot CLI. Local tasks target `microsoft/PowerToys`. Website GitHub drafts are reviewed and confirmed in the extension; fork-issue comments may also target `MuyuanMS/PowerToys`. The interface and generated result prose are English; quoted source and raw CLI output retain their original language.

Pulse now sends local tasks and typed GitHub drafts through the extension. Missing-extension, local-Host, and configuration problems have separate recovery paths. See [Actions integration](docs/actions-integration.md) for responsibilities, installation identity, confirmation, and retry behavior.

**Tasks** is the single workspace entry for local work. Its **Target** filter selects All targets, Pull requests, or Issues; **View** switches between active/recent work and all finished runs, including failed, cancelled, and interrupted runs. PR reviews deliver one final, rechecked P0–P3 finding list and a separate E2E assessment. Issue research gives one integrated Feature or Bug conclusion and saved plans for useful follow-up work. Reading a result never submits anything to GitHub.

The toolbar popup is a compact preview: brand and connection status at the top, active tasks and two recent results, and **Open extension**, **Refresh**, and **Open Pulse** in the bottom bar. It omits the update timestamp and Settings shortcut; full configuration remains in the extension page. History opens in a new tab. A disconnected popup retains its last records with a **Last known** label rather than treating old counts as current.

The Windows Host saves task state, results, and agent CLI output locally. Details provide structured activity plus separate stdout and stderr streams. V3 raw streams have a 64 MiB limit each; legacy streams retain 8 MiB limits, with explicit truncation. Full v3 reports are paged without silently dropping findings. The installed extension uses Native Messaging without a Windows service, database, or localhost listener.

Distribution currently uses a local Windows package and an unpacked extension with a stable public-key-derived Chrome/Edge ID. The Host has been built, packaged and installed locally; see [current acceptance](docs/acceptance.md). CI is deferred by the user. Complete real-browser, GPT-6 workflow and GitHub-write acceptance will be tested by the user later and is not claimed by the automated fixtures or preview. No extension-store release is implied.

## Workflows and results

- **PR Review:** choose `static` (code and existing evidence), `build-tests` (also relevant compilation and automated tests; the initial default), or `ui-e2e` (selected running scenarios with necessary supporting checks). All modes use an internal complete review Loop and recheck every candidate before one final report. Review does not apply production fixes automatically.
- **E2E necessity:** every PR review records `not_needed`, `recommended`, or `required`, with reasons and scenarios. Missing unselected E2E does not make a finished static review fail. A linked verification task adds evidence for the saved plan and revision without repeating the review.
- **Issue research:** select Feature Research or Bug Investigation. Feature feasibility is part of one final research conclusion. Bug investigation uses a complete Loop and distinguishes defect confirmation, missing information, remaining experiments and actual reproduction.
- **Follow-up work:** the extension can start a saved Feature implementation, Bug fix, reproduction or verification plan. Source-bound plans and local candidate snapshots preserve what is being implemented or tested. A research recommendation does not automatically start development.

New tasks use [structured results v3](docs/workflow-v3-wire.md); stored v1/v2 records remain compatible. Actual CLI failures and missing required evidence cannot be converted into success by model claims. Workflow completion, product observations and review advice remain separate.

Fixed manual GitHub controls are independent of AI proposals and task success. Only a confirmed unresolved P0 on the current original PR adds an Approve business restriction; live account, target, state and SHA checks still apply. Findings supply independent editable comments or code suggestions, and the extension combines only the user's selected feedback. Issue duplicates offer a verified canonical target and an explicit close-as-duplicate action or a linking comment. All writes require the user's concrete selection and confirmation.

Results now use one conclusion/next-step workspace with a shared primary-action and More actions footer. Confirmed findings start selected as ordinary feedback; edits and independent manual drafts are saved separately. Verification first opens its saved requirements. Verified Issue candidates enter an editable, fixed-Draft PR preparation workspace. See [UI/UX implementation and local acceptance](docs/uiux-implementation.md) for coverage and evidence boundaries.

## Settings

- **Default agent and installation:** choose Codex or Copilot, then select an installation for each CLI you use. Settings lists detected executables, versions, sources and paths; retained standalone releases are labelled as history. No version, helper or interface checks disable a detected choice. Test is optional and does not gate saving. No installation is chosen automatically; Save settings applies your selection to new tasks.
- **Models and reasoning:** save separate defaults for Codex and Copilot. New tasks can override the agent, model and effort under **Run options**. Omitted values inherit saved defaults; an explicit blank model/effort uses the CLI's default. Cards and details show actual runtime metadata when the CLI reports it; missing fields stay unknown. Requested values remain in Technical details. Model availability and supported effort levels depend on the selected CLI/provider.
- **Local file permissions:** `yolo` by default, with `read-only` and `workspace-write` also available. Tasks have no execution timeout and can be cancelled. YOLO uses the agent's unrestricted approval/sandbox mode.
- **PowerToys main folder:** the main checkout, not a linked worktree. A fork may retain its own `origin`; at least one remote must identify `microsoft/PowerToys`.
- **Worktree folder:** an external root. Each task creates `pulse-<runId>` and branch `codex/pulse-<runId>`. Completion, cancellation, and history cleanup preserve worktrees and changes.
- **GitHub configuration:** detect local `gh auth` accounts, select a valid account, and save it. Extension GitHub operations use `gh api` with that account per command without switching the global account. Tokens are never saved in settings or logs.
- **GitHub CLI version:** account enumeration requires `gh` 2.81.0 or newer for `auth status --json`.
- **Task prompts:** maintain eight workflows and a shared verification fragment in [`prompts/`](prompts/README.md). They ship inside the Host and load offline. The four existing PR-review, issue-fix, E2E and reproduction selections remain compatible; Feature/Bug research and saved-plan implementation/verification use their corresponding bundled entries. An empty reproduction selection uses the bundled reproduction prompt.

Prompts are embedded at build time; update the Host to deploy prompt changes. They are maintained in this repository and do not synchronize from another repository or require Copilot cloud review or an external skill. The Host injects the selected workflow, accepted scope and applicable result instructions; new static PR reviews omit runtime verification guidance. Nonempty selections must match a bundled template and target type. Each accepted run freezes the rendered content, content hash, schema version and bundle revision. Existing snapshots and old sync caches are retained; new tasks use the bundle. The original Pulse request remains part of request identity.

PR tasks with an expected SHA verify the remote PR and compare the fetched PowerToys PR ref before worktree creation. Ordinary Issue tasks use the main checkout's HEAD captured at acceptance. Saved-plan follow-ups use their frozen source identity; local-candidate verification restores the retained snapshot rather than silently testing the pre-fix source. Existing main-checkout changes stay in place.

## Local validation and UI preview

Development requires the .NET 10 SDK, Node.js, and installed extension npm dependencies. User machines need their chosen Codex/Copilot CLI, Git for worktrees, and `gh` for target verification and extension GitHub operations. Prompts need no network or GitHub sign-in. Discovery may query `--version` for display, but does not judge usability or enforce a release allowlist. An unreadable version is shown as unknown. Tasks attempt the chosen executable and report its actual output or launch error.

Build only for final acceptance validation or when explicitly requested. These commands validate locally and start the preview without creating a release package:

```powershell
dotnet build tests/Pulse.Host.Tests.csproj --configuration Release
./tests/bin/Release/net10.0/Pulse.Host.Tests.exe
node --test tests/native-smoke.test.mjs
npm --prefix extension test
node extension/node_modules/typescript/bin/tsc -p extension --outDir .tmp/extension-ui
node extension/preview.mjs
```

Open [the UI preview](http://127.0.0.1:4186/popup.html?expanded=1). Static assets come from `extension/public`; preview JavaScript comes from `.tmp/extension-ui`. The diagnostic proxy launches `host/bin/Release/net10.0/Pulse.Host.exe` and stores local data in `.tmp/ui-preview-host`.

Preview tasks, results, and task logs are sample data. The default UI fixture mode disables Host, model and GitHub writes. Only the separate `diagnostics=1` preview mode enables explicit agent tests, `gh` account detection, and prompt list/get/sync calls against the local Host. Form settings remain preview-session data. The installed extension uses shared Native Messaging settings and records.

## Documentation

- [UI/UX implementation and local acceptance](docs/uiux-implementation.md)
- [Installation, repair, and uninstall](installer/README.md)
- [Protocol and Pulse integration](docs/protocol.md)
- [Approved PR/Issue workflows](docs/pr-issue-workflow-proposal.md)
- [Current structured result contract](docs/workflow-v3-wire.md)
- [Prompt maintenance and validation](docs/prompt-optimization-gpt6.md)
- [Local runtime skill activation and evidence](docs/runtime-skill-activation.md)
- [Execution, storage, and permissions](docs/architecture.md)
- [Acceptance evidence and remaining browser checks](docs/acceptance.md)
- [Current requirements and historical scope](docs/scope.md)

Production data defaults to `%LOCALAPPDATA%\PulseExtension`. Uninstall preserves configuration and tasks by default. GitHub writes require a specific extension action after reviewing the target, account, SHA, and content.
