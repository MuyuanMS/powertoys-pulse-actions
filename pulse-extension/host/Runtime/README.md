# Runtime adapter notes

Settings inventories PATH and known Codex/Copilot installation folders and resolves aliases where possible. Every detected native executable is selectable. `--version` is optional display metadata; discovery does not invoke help, inspect sandbox helpers, maintain a version allowlist or decide whether a CLI can run tasks. A failed version query leaves the choice selectable with an unknown version. `available` means the file was found, not that execution was verified. Tasks and optional diagnostics use the user-selected path without falling back to another installation, and report actual launch/output errors.

## Process ownership and recovery

`StartWorker` starts the same Host executable with `--worker <runId> --data-root <root>` using `CreateProcessW`, `DETACHED_PROCESS`, no inherited handles, and `CREATE_BREAKAWAY_FROM_JOB`. If explicit breakaway is rejected with access denied or invalid parameter, it retries without that flag. In either case it checks that the new worker belongs to no job before publishing the worker PID and process creation time. If the worker remains in a browser job, startup fails with `BACKGROUND_JOB_RESTRICTED`; the implementation does not claim browser-independent execution in that environment.

Workers start without `CREATE_SUSPENDED`. Before accepting work they wait up to 30 seconds for the launching Host to persist their exact PID and creation time. An unregistered orphan exits without starting a CLI. This prevents a connection Host crash between process creation and status persistence from leaking a permanently suspended worker. Once registered, the worker takes the repository file lock for its entire execution, prepares the run's worktree, and independently owns all CLI input, output, logs, cancellation, and terminal writes. Tasks have no execution deadline; legacy saved `timeoutSeconds` values are ignored.

CLI processes are created with `CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT`. A `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` permits inheritance of only their three dedicated pipe ends. Before resuming the CLI, the worker assigns it to a job with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and persists its process identity. A worker crash therefore closes the job and terminates CLI descendants. The worker kills lingering job descendants after the primary CLI exits, so an inherited output pipe cannot hold the task open forever.

The process wrapper retains the handle returned by `CreateProcessW` until disposal. Creation time comes from `GetProcessTimes`, exit detection uses `WaitForSingleObject`, and the recorded exit code comes from `GetExitCodeProcess` after termination. It does not depend on the exit-code support of a newly opened .NET `Process` object for an already-running PID.

Cancellation is a persisted `cancel.json` marker observed within the worker's 200 ms polling loop. Explicit cancellation first offers stdin EOF and `WM_CLOSE` where supported, allows a 1.5-second grace period, then terminates the job, drains output, retains existing repository changes, and saves terminal state. The selected headless CLI interfaces have no guaranteed graceful cancellation protocol, so EOF/window close may have no effect and job termination is the enforced fallback. Reconciliation checks exact process identity and the repository lock, recovers the full `completion-journal.json` before considering interruption (with compatibility for older `completion.json` plus `result.json`), and never starts another CLI. Unknown/inaccessible process identity remains active for investigation. An overdue accepted worker with no repository lock is stopped by verified identity and marked interrupted. Startup handoff, read-only executable probes, and draining pipes after the CLI has exited have bounded waits; none limits model execution time.

These mechanisms require acceptance testing with actual Chrome and Edge policy/job configurations. The real browser/CLI matrix remains pending separate user testing; synthetic checks and this documentation update do not establish those passes. See [scope](../../docs/scope.md) and [v3 validation](../../docs/workflow-v3-validation.md).

## CLI input and permissions

Business workflow instructions are agent-neutral. Both adapters receive the same complete schema for the accepted snapshot version; Codex's native schema argument adds a constraint without changing the workflow. The common wrapper injects the result protocol relevant to the task and accepted scope, with one shared permissions/publication/build boundary. Generic capability data describes the adapter's current file-tool scope and built-in tool-server configuration without claiming that dependencies, network access or hardware are available. Agent-specific launch flags and event parsing stay in the adapter.

Codex receives an argument vector equivalent to:

```text
codex.exe exec --json --color never --sandbox <read-only|workspace-write> -c approval_policy="never" --output-schema <run>/result-schema.json -
```

The entire trusted instruction wrapper and task JSON are written to stdin. Existing project instructions and CLI authentication remain enabled. With the saved `yolo` policy, Codex receives `--dangerously-bypass-approvals-and-sandbox` without `--sandbox`. YOLO is the default; explicit saved permission choices remain in force.

New runs execute the embedded prompt saved as the top-level `task.json`
`promptTemplate.body`. The worker passes it to `CliAdapter.Prompt` inside the fixed
instruction wrapper. Eight business templates cover the six public workflows and
saved-plan Feature implementation/Issue verification; PR verification reuses E2E
with its saved follow-up scope. Static PR review omits the shared runtime guidance.
The original web `task.prompt` and `task.context` remain
untrusted context and retain their original values for canonical request deduplication.
Selected local instructions remain subject to the configured permission policy,
applicable repository rules, PowerToys and related build policy, and separate
user-selected GitHub operations. The wrapper asks the agent to complete independently
available work and justified alternatives before reporting a genuine blocker; an
instruction that blocks required work must be identified by file and rule in the
existing diagnostics/evidence fields. A skill reference does not itself create a
new approval requirement or override applicable user instructions.
Generated summaries and next steps are requested in English; actual CLI output and
quoted source material retain their original language. Legacy runs without a saved
template still use the original task description within the same wrapper.

Copilot receives stdin **without** `-p`, with JSONL streaming and no interactive question tool. Read-only mode exposes `view,glob,grep`, grants `read`, and denies `write`, shell, and URL permissions. Workspace-write additionally exposes `create,edit` and grants `write`. Both modes disable built-in MCPs, remote export, automatic updates, and automatic temp-directory file access. An explicit tool allowlist prevents configured custom MCP tools or broad saved tool grants from broadening this invocation's exposed tool set. `COPILOT_ALLOW_ALL` is removed from the inherited environment and set to `false` for execution.

Copilot shell/test execution and URL/MCP access are unavailable in the read-only and workspace-write policies. The task reports the concrete affected checks after completing independent work or sufficient authorized alternatives; this is not a blanket gate on a workflow or CLI version. Copilot permission filters and best-effort file path checks are **not OS isolation for arbitrary prompts**. The selected `yolo` policy uses `--yolo` without the restrictive tool allowlist; built-in MCPs remain disabled by this adapter, and only actually exposed tools may be used. GitHub publishing remains a separate fixed Host operation.

## Agent discovery and explicit model tests

Settings requires an explicit installation choice from the discovered inventory, without accepting an arbitrary web-supplied path. Discovery checks native `.exe` files on PATH and known installation folders, including standalone/current and retained releases, desktop installations, WinGet links/packages and native npm package binaries. It never launches `.cmd`, PowerShell, or other shell wrappers. An existing task keeps its resolved executable path in its snapshot; a missing choice/file does not trigger automatic fallback.

`agents.test.start/get/cancel` use a persisted `agent-tests/<UUID>/status.json` and a hidden diagnostic worker. This worker may remain in its launcher's Windows Job; closing that launcher may interrupt the test, which the next poll reports without rerunning it. Accepted tasks retain their stricter detached lifetime guarantee. Each diagnostic worker owns its CLI Job and can stop the CLI and descendants on cancellation.

Tests send exactly `what's your model` in a newly created temporary directory, independently of PowerToys folder settings. Codex uses read-only, ephemeral execution with the Git repository check disabled. Copilot disables its tools, custom instructions, built-in MCPs, and remote export. CLI authentication and model configuration are reused. No model request is sent during discovery or configuration saving. Success requires a nonempty structured assistant response, zero process exit code, and no failure event; help/version output cannot pass. Test results are redacted and the temporary workspace is removed. Model tests have no automatic execution deadline and expose explicit cancellation.

## Events and completion

Codex JSONL parsing handles `thread.started.thread_id`, `item.completed.item` with `type: "agent_message"` and `text`, `turn.completed`, and `turn.failed` / `error`. Copilot uses its own adapter for `session.start`, `assistant.message.data.content`, `assistant.message_delta.data.deltaContent`, `session.idle`, `session.error`, and a programmatic `result` envelope when present. Only recognized complete assistant/result messages supply business-result JSON. Raw text, progress, tool output and a final non-JSON response remain diagnostic evidence; they cannot manufacture a completed result. A later malformed, empty or oversized final invalidates an earlier candidate rather than silently reusing it.

New bundled workflows require [schema v3](../../docs/workflow-v3-wire.md), frozen in the accepted prompt snapshot. It retains complete rechecked findings, coverage/limitations, product assessments, required checks/evidence, typed diagnostics and saved plans. A new v3 task cannot downgrade to v1/v2. The Host derives outcome separately from process state and validates report completeness and required evidence; model success cannot override a failed process. Invalid or incomplete output is normalized to structured diagnostics, without claiming a complete report. Completion journals retain the complete result before updating result, event and status projections.

V3 model JSON is bounded at 8 MiB, with a separate 16.5 MiB normalized-result allowance. The escaped CLI-event allowance is six times the model byte budget plus 16 Ki characters of event metadata; final text and diagnostic fallbacks have their own bounds. Full stored findings are not cut to Top N. `tasks.resultPage` transports large reports under the 900 KiB Native frame budget, with fingerprints preventing mixed-result pages; Host decisions read the complete stored result. Original diagnostic fallback text is capped at 32 Ki characters.

Historical snapshots retain their contracts. [V2 results](../../docs/structured-results-v2.md) use the prior 320 KiB model / 704 KiB normalized budgets, reject v1 downgrade when the accepted snapshot requires v2, and remain readable alongside legacy v1 records. These compatibility paths do not turn old records into v3 reports.

Fixed manual PR/Issue operations do not require an eligible model proposal or completed CLI result. A confirmed unresolved P0 on the current original PR adds the Approve restriction; fresh account/target/permission/SHA checks and explicit user confirmation still apply. AI recommendations, saved-plan execution and proposal-specific publication requirements remain distinct. Nothing in result parsing starts a task or GitHub write automatically.

Raw stdout/stderr files are limited to 64 MiB each for v3 snapshots and 8 MiB each for legacy snapshots; structured events remain capped at 16 MiB. Output events exceeding their versioned transport allowance produce an explicit diagnostic. Both pipe readers continue consuming after raw-log limits, and final-result extraction uses its independent budget. Known credential patterns, authorization headers, and secret-valued environment variables are redacted before persistence. This is pattern-based filtering, not proof that arbitrary transformed or unknown secrets can never appear in model output.

`RunLogs.Read` serves the fixed `stdout.jsonl` and `stderr.log` files independently
of normalized activity events. Callers select `stdout` or `stderr` and resume with
the returned byte `nextCursor`. Only complete LF-terminated UTF-8 lines are consumed;
an incomplete final line remains for a later poll. The usual page size is 64 KiB,
with a 128 KiB maximum and an additional encoded JSON response budget below 256 KiB.
An oversized complete line returns a marked prefix; `truncated` reports that omission
or a persisted stream-specific cap marker. Ordinary pagination uses `eof:false`
without reporting data loss. `eof:true` means the current file snapshot is exhausted;
running tasks can append more output later. Re-reading a log also applies redaction
to legacy files. Invalid byte cursors, non-allowlisted streams and log reparse points
are rejected. Logs remain readable after disconnection and CLI completion.

## Synthetic acceptance fixture

The BCL test apphost can impersonate Codex/Copilot without invoking a model, including fragmented JSONL writes and version/error fixtures. Its fixed version/help strings are fixture metadata, not compatibility gates. This historical minimal v1 record illustrates Codex transport only; it is not a valid final payload for a new schema-v3 task:

```json
{"type":"thread.started","thread_id":"fixture-session"}
{"type":"item.completed","item":{"type":"agent_message","text":"{\"summary\":\"Fixture completed\",\"artifacts\":[],\"validation\":[],\"blockers\":[],\"nextSteps\":[{\"kind\":\"none\",\"reason\":\"Synthetic fixture\",\"body\":\"\"}],\"review\":null,\"needsReview\":false}"}}
{"type":"turn.completed"}
```

Have the fixture's `--worker` branch call `RuntimeService.RunWorkerAsync`. Keep the same `runId` while killing the original connection process and assert that stdout continues growing and the same worker/CLI IDs reach a persisted terminal result. Use a second long-running fixture for cancellation and child-process cleanup. A missing final newline is completed only when the actual process pipe closes. Fixtures do not count as real browser/model acceptance.

For an explicitly requested real model/log acceptance check, the test apphost accepts
`--live-log-test codex` or `--live-log-test copilot`. This creates a unique empty Git
repository without remotes under `.tmp/cli-log-check/<agent>`, discovers the real CLI,
and runs the normal read-only worker with only the model identity question. It uses
the test-only Job fallback when necessary; production detachment checks are unchanged.
The entrypoint polls `RunLogs` during execution, supports Ctrl+C cancellation without
a model deadline, and prints only the actual task status, exit code, byte counts,
run ID and absolute stdout/stderr paths as JSON. It retains the original logs, result,
and `log-verification.json`, including capture-before-completion evidence and parsed
stdout event counts. An unknown result or missing evidence is not reported as a
successful verification. The ordinary automated test harness does not call this entrypoint.

`--live-command-test codex` additionally checks actual Windows sandbox command execution.
It creates an isolated repository with a random nonce file whose contents are absent from
the prompt. Success requires exactly one successful read-only command, the exact nonce
in saved command output before completion and in the structured final summary, no other
tool calls, and CLI exit code zero. Evidence remains in `.tmp/cli-command-check/codex`
with `command-verification.json`. This is separate from a model-response-only check and
does not establish that a complete PowerToys workflow can perform write-dependent steps.

Official references: [Codex non-interactive mode](https://developers.openai.com/codex/non-interactive-mode), [Copilot programmatic usage](https://docs.github.com/en/copilot/how-tos/copilot-cli/automate-copilot-cli/run-cli-programmatically), [Copilot programmatic reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-programmatic-reference), and [Copilot command/tool reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference).
