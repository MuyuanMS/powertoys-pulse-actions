# Execution and storage design

The Host is a .NET 10 Windows x64 program with Native Messaging, internal `--worker <runId>`, agent-diagnostic `--agent-test <testId>`, and installer activity-check entry points. Tests and local preview can use `--stdio --data-root <absolute path>` without registry changes. Production needs no Windows service, scheduled startup task, database, or network listener.

## Worker lifecycle

Browser-connected Host processes handle communication. Each accepted action has a separate worker that owns CLI execution and output capture. Native process creation prevents inheritance of browser standard handles, requests parent-Job breakaway, and verifies that the worker has detached. If that cannot be proved, production reports `BACKGROUND_JOB_RESTRICTED`. The worker waits for its PID and start identity to be committed before starting the CLI; this admission guard is not a task timeout.

The explicitly selected CLI starts by its frozen absolute executable path with an argument array. Prompt text is input data. A suspended CLI is attached to the worker's kill-on-close Windows Job before execution resumes. Worker failure therefore terminates the CLI process tree. Tasks have no execution deadline; explicit cancellation stops execution while preserving existing changes.

The intended browser lifecycle is reconnecting to the same persisted run while its worker continues recording output. Actual Chrome/Edge Job behavior still requires the browser matrix in [acceptance](acceptance.md). The test-only launcher used in the current restricted environment validates streams, cancellation, and persistence within its existing Job; it does not establish production browser detachment.

Configuration and the selected prompt are frozen before acknowledgment. Changing Settings in another browser cannot switch a running task's agent, permissions, directories, or template. Cross-process file locks serialize admission and execution for a Git common directory. There is no queue: an active common repository returns `REPOSITORY_BUSY`.

## PowerToys worktrees

Settings contain the default agent, separate model/effort defaults and selected CLI installation for each agent, permission, PowerToys main folder, worktree root, selected GitHub account, and the existing PR/fix/E2E/reproduction template selections. Settings lists discovered executable paths for explicit selection; a missing or blank selection never falls back to another installation. Version probing supplies metadata only, with no version, helper-file or advertised-interface compatibility gate. Tasks accept only `microsoft/PowerToys`; there are no arbitrary repository mappings or web-supplied executable paths.

The main path must identify a main checkout, not an existing linked worktree. A personal fork may retain its origin, with another remote pointing to the PowerToys upstream. The normalized worktree root must lie outside the main checkout, including when directory links are resolved.

Admission captures the source revision and assigns `<worktreeRoot>/pulse-<runId>` with branch `codex/pulse-<runId>`. Expected PR SHAs are checked through the selected `gh` identity. The worker fetches the fixed PowerToys `refs/pull/<number>/head` into `refs/pulse/runs/<runId>`, verifies the expected SHA, and then creates the worktree. Ordinary Issue tasks use the captured main HEAD; saved-plan follow-ups bind the retained plan and its source revision. Main-checkout branches and dirty files are preserved.

Preparation Git processes are cancellable and Job-managed. `worktree.json` records the path, branch, base SHA, and preparation state. Existing destinations without matching run ownership are not overwritten. Success, failure, cancellation, and task-history deletion preserve worktrees, branches, and modifications.

An explicit saved-plan action creates a separate task through the Host. Implementation results can retain a content-addressed candidate delta, including changed, new and deleted files. Candidate verification restores that exact delta into a fresh child worktree and records its identity; a missing snapshot cannot silently become verification of the unmodified source. Capture does not commit or push. See [the v3 saved-plan and candidate contract](workflow-v3-wire.md).

## Local prompt catalog

Eight repository-owned business prompts and one shared PR verification fragment are embedded in the Host assembly. Catalog reads are offline and identify `bundled://Pulse.Host/prompts` with SHA-256 content hashes and a bundle revision. The legacy `prompts.sync` method returns this local catalog without downloading or writing a cache. Older downloaded generations remain untouched and are not selected for new tasks; accepted tasks retain their original snapshots.

Public workflows are PR review, Issue fix, reproduction setup, E2E, Feature research and Bug investigation. Feature implementation and Issue verification start only from saved plans; PR verification reuses the E2E template with a bounded follow-up scope. The existing four configurable template selections remain compatible, while the newer Issue workflows use fixed bundled defaults. Filename, target, action-kind and placeholder validation prevent mismatched selection. The supported substitutions are `<PRNumber>`, `<PRTitle>`, `<IssueNumber>`, and `<IssueTitle>`; optional titles are bounded and JSON-quoted as data. Missing titles receive an explicit instruction to read the linked target.

Admission stores `promptTemplate` with the rendered body, template and fragment hashes, schema version, required checks, accepted review scope, bundle revision, source and capture time. Static PR review omits the runtime verification fragment; relevant execution scopes and verification tasks include it. The common wrapper supplies one full result schema and the applicable task/scope protocol for either agent. The original webpage task remains unchanged for replay/fingerprint checks and is untrusted context. Template or settings updates affect new tasks only.

## Task views and logs

The English interface uses one **Tasks** workspace. **Target** filters All targets, Pull requests, or Issues; **View** selects active/recent work or all finished runs. PR/issue history includes all terminal outcomes, not only success. Task details and action confirmations retain the Tasks navigation selection; Settings has its own selection. Existing `view=prs` and `view=issues` links still open the corresponding filtered history. Host filtering precedes pagination; running, PR, issue, and unread counts describe the full readable record set. Read and handled flags are independent of execution state.

The worker drains stdout and stderr continuously, parses structured CLI activity/results, and saves redacted raw streams separately. Task details can request the structured activity view or either raw stream. Raw `tasks.logs` is internal-only: web callers cannot use it to read local output.

Raw readers accept a run ID and fixed `stdout/stderr` selector, never a filesystem path. Independent byte cursors advance only past complete UTF-8 lines. Partial trailing data is deferred; oversized complete lines return bounded prefixes with visible omission markers. Pages default to 64 KiB and accept 1–128 KiB. A reported file EOF is not a task terminal state, so active runs continue polling.

Events are capped at 16 MiB. Each raw stream is capped at 64 MiB for a saved schema-v3 task and 8 MiB for legacy tasks. Capture continues draining pipes after a raw-log cap, and final-result parsing has its own budget. Truncation or incomplete output is surfaced explicitly rather than claimed as a complete report. Known credentials are redacted during capture and raw reads. Model/user content otherwise retains its original language.

## Structured reports

New bundled tasks require schema v3; historical snapshots retain their recorded contract, including readable v1/v2 results. Both agents receive the complete schema. Only recognized assistant final results qualify as model output; raw logs and tool/progress events remain diagnostics. The Host derives workflow outcome separately from process state, checks required evidence and report completeness, and preserves structured failure/cancellation/interruption diagnostics.

V3 retains the complete rechecked findings, coverage, limitations, assessments and plans without Top-N slicing. Model JSON is bounded at 8 MiB, with a separate 16.5 MiB normalized-result budget and escaped CLI-event budget. Large detail responses use fingerprint-bound `tasks.resultPage` sections under the 900 KiB Native frame limit. The UI distinguishes a complete report from a partial read; Host action decisions use the full stored result. See [workflow v3 wire](workflow-v3-wire.md) for the current contract and [structured results v2](structured-results-v2.md) for the historical format.

## Files and recovery

```text
%LOCALAPPDATA%/PulseExtension/
  config.json
  installation.json
  bin/
  locks/                         Cross-process handle locks
  requests/                      Rebuildable request index and deletion tombstones
  prompts/                       Retained legacy download caches, if present
  candidate-snapshots/
    <snapshotHash>.json          Immutable candidate manifest
    <snapshotHash>.zip           Retained changed/new/deleted content
  agent-tests/<testId>/
    status.json
    cancel.json                  When cancellation is requested
  runs/<runId>/
    task.json                    Original task, origin, config, promptTemplate
    status.json                  State, timestamps, process identity, exit status
    completion-journal.json      Complete terminal result and recovery state
    completion.json              Legacy terminal journal, when present
    result.json
    view.json                    Read/handled flags
    events.jsonl                 Structured activity, at most 16 MiB
    stdout.jsonl                 Redacted CLI stdout, 64 MiB v3 / 8 MiB legacy
    stderr.log                   Redacted CLI stderr, 64 MiB v3 / 8 MiB legacy
    *.truncated                  Explicit raw-stream truncation markers
    result-schema.json
    worktree.json
    provenance.json              Host source observations at execution boundaries
    candidate-snapshot.json      Implementation candidate reference, when captured
    candidate-input.json         Restored candidate identity, when applicable
    cancel.json
    operations/<operationId>.json
```

JSON records are written to same-directory temporary files, flushed, and atomically replaced. New runs become visible only after their complete staging directory is renamed. Completion writes the result and terminal journal before final status, allowing reconnection to repair a crash between those writes.

Recovery verifies PID plus process start identity. It does not confuse normal completion with interruption merely because the process exited. Unverifiable active identities remain protected from blind termination or reruns. System restart does not automatically resume model execution.

Events use monotonically increasing per-run sequence numbers. Readers ignore incomplete JSONL tails; a subsequent append repairs an interrupted final line. Browser caches do not own results or unread state. Records with uncertain GitHub writes cannot be cleaned up; ordinary cleanup requires a read, handled terminal run and never removes its repository or worktree.

## Permissions and GitHub identity

Web payloads and model output are untrusted data. Exact origin/extension allowlists protect Native access. Only internal Settings chooses local folders, permissions, GitHub accounts, and prompts. UI output uses text rendering and restricted links; model-returned paths and commands are not automatically executed as Host operations.

Permissions are `read-only/workspace-write/yolo`. Codex uses its sandbox policies for the first two and its approval/sandbox bypass for YOLO. Copilot's restricted modes allow selected file tools and deny shell/URL tools; YOLO uses `--yolo`. Built-in Copilot MCPs are disabled. Restricted Copilot file tools do not provide OS isolation or enable shell-based testing; this capability limit is reported with the affected work, not used as a blanket workflow or version gate. A worktree itself is not a permission boundary. Task results must report validation actually performed. PowerToys and related builds are reserved for final validation or explicit current user requests.

GitHub detection returns only `login/active/state` from local `gh auth status`. A nonempty chosen identity must be a valid saved login; an empty choice does not silently use the global default. Agent authentication is separate from GitHub operation identity.

All product GitHub API calls, including identity, PR checks and submissions, use fixed `gh api` URLs and argument arrays. JSON bodies use stdin. The selected credential exists only in memory and the relevant child's environment, with no global account switch or token persistence. Prompt catalog reads do not access GitHub. Offline tests may inject an HTTP handler.

Fixed manual PR/Issue actions remain available independently of model success, report completeness or proposed next actions, subject to fresh target/account/permission/SHA checks. A confirmed unresolved P0 on the current original PR adds an Approve restriction; P1 or incomplete analysis does not add that restriction. Result recommendations remain advice, and selected finding feedback is composed only after user selection. User-confirmed writes use independent durable operations with intent logging, deduplication and read-only reconciliation of uncertain results. Completion, viewing, reconnecting and marking handled never initiate writes.

Maintenance prevents new tasks, submissions, and agent tests while installation changes are in progress; active work prevents maintenance. User data is preserved by default.

## Model diagnostics and preview

Settings tests use the saved installation or an explicit unsaved inventory choice, send only `what's your model`, and require a nonempty assistant response, exit code zero, and no CLI error. They use temporary directories and restricted permissions, need no PowerToys checkout, and are cancellable without a model deadline. These opt-in tests do not gate saving an existing executable choice. Version probing alone does not call a model.

Diagnostic workers may remain in the current Windows Job and make no browser-shutdown persistence promise. Production action detachment checks stay enabled.

The local UI preview serves static source assets and separately compiled JavaScript from `.tmp/extension-ui`. Its proxy launches `host/bin/Release/net10.0/Pulse.Host.exe` with data in `.tmp/ui-preview-host`. Only explicit model tests, account detection, and prompt list/get/sync use the real Host. Tasks, results, logs, and saved form state in that preview remain sample/session data. Production Native Messaging has no such loopback proxy. This design description is not evidence that the real Codex/Copilot and Chrome/Edge execution matrix has passed; that validation remains separately recorded in [acceptance](acceptance.md) and [v3 validation](workflow-v3-validation.md).
