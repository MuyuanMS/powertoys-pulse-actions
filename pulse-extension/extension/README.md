# Pulse Chrome / Edge extension

Manifest V3 and TypeScript, with no page content script. The interface follows the PowerToys Pulse Primer light style and uses English. Original task data, prompt files, and CLI output are displayed without translation.

## Task views

- **Tasks** shows accepted and running tasks plus the five most recently finished runs, including failures, and remains selected when empty.
- **Pull requests** shows finished PR runs, with review drafts and PR actions.
- **Issues** shows finished issue runs, with artifacts, rerun, comments, and closing.

Finished runs include succeeded, failed, cancelled, and interrupted states. **All finished tasks** also exposes legacy records without a target. Host filtering happens before pagination. Read and handled flags remain separate.

Details show Activity events and the captured Agent stdout / Agent stderr streams. Raw logs are read incrementally using independent UTF-8 byte cursors; they are available only to extension pages. The UI keeps the latest 2,000 activity events or 1,048,576 characters of raw text while the Host retains its bounded log files. Sample preview logs are explicitly labeled.

## Settings

Choose a default Codex or Copilot agent. The Host detects installed CLIs automatically. Each Test button sends `what's your model` and displays the actual response or error. Discovery and version output alone do not count as a successful model test. Tests run in the Host, can be cancelled, and resume polling after the settings page reloads in the same session.

Configure a local PowerToys main repository and a worktree root folder. Each new task creates a separate worktree and branch. Local file permissions default to YOLO (full access without approvals); Read only and Allow workspace changes are also available. Tasks have no execution time limit and can be cancelled.

Choose prompts bundled with Pulse Host, inspect their text, and save. The source files are maintained in this repository's [`prompts/`](../prompts/README.md) directory and updated with the Host. PR review is entirely local; it does not require a fork PR or Copilot cloud review. Existing saved filenames and task snapshots remain compatible. Reload prompts reads the local bundle without synchronization or GitHub authentication.

New tasks use schema v3; stored v1/v2 results remain readable. Workflow completion, CLI execution, and product findings are reported separately. Details provide findings, evidence, diagnostics and recovery actions as available. Fixed manual GitHub controls do not require a successful task or an AI proposal; current account, target, SHA and permission checks still apply. See the [workflow guide](../README.md#workflows-and-results) for reports, follow-up work and approval restrictions.

GitHub configuration reads local `gh auth` status and saves one available account. Extension GitHub actions use that account per command without changing the global active account or the Pulse website. Sign in with `gh auth login --hostname github.com` if needed.

## Development

Requires Node.js 22.18+. Use the npm sources and configuration already configured on the host; do not override the registry or fall back to another source. From `extension`:

```powershell
npm ci --ignore-scripts
npm run check
npm test
```

The check uses `tsc --noEmit`; tests load TypeScript source directly. Neither produces extension packages. Do not build during PowerToys investigation or implementation. A local build is allowed for final acceptance or an explicit user request:

```powershell
npm run build
# Only when testing the localhost integration example:
npm run build:dev
```

Enable developer mode in Chrome or Edge and load `extension/dist` (production origins) or `extension/dist-dev` (development origins). The Host installer registers both browsers together and defaults to the checked-in stable extension ID; see the [installation guide](../installer/README.md).

The production origin is `https://cautious-memory-r38ze9j.pages.github.io`. With development origins enabled in both the extension and Host, local integration also permits HTTP on `localhost` or `127.0.0.1` at port `8080` or `8081`. Full origins, including ports, are validated. Do not distribute the development variant as a production package.

## Message boundaries

Pages call `chrome.runtime.sendMessage(extensionId, {protocolVersion: 1, type, payload}, callback)`. External calls support extension detection and readiness, bounded execution choices, task submission and recovery, origin-bound reads, fixed extension navigation, and typed GitHub draft preparation. Draft preparation does not write to GitHub. See the [protocol](../docs/protocol.md) and [integration example](../examples/README.md) for the current methods and payloads.

The extension uses `connectNative('com.powertoys.pulse')`. Requests are `{id,protocolVersion:1,type,payload}`; responses contain `ok` and `data` or `error`. The browser sender determines the origin. Web pages may select bounded agent, model and reasoning options, but cannot change saved settings or supply commands, paths, permissions or account identity. Local prompt catalogs, raw logs, paths and account configuration remain private.

Only packaged extension pages can use agent tests, `github.accounts`, `prompts.list/sync/get`, and `tasks.logs`. Account responses never include tokens. Prompt content and agent output use plain text rendering. Artifact links accept credential-free HTTPS only; local artifact paths are text.

A disconnected browser does not change task state or resubmit prompts. GitHub writes are never automatically retried; unconfirmed operations can be reconciled from their saved operation IDs. GitHub operations are selected and confirmed in the extension, subject to current account, target, permission, revision and applicable content checks. See the [current result and action contract](../docs/workflow-v3-wire.md).

## Validation scope

Source tests cover sender isolation, origin grants, forbidden external methods, configuration overrides, SHA requirements, size limits, sensitive-field projections, links, suggestion locations, PR/issue action separation, filtered preview pagination, and sample log byte cursors. Real browser registration, connection recovery, CLI execution, and GitHub writes require Windows acceptance testing; source tests do not substitute for those checks.
