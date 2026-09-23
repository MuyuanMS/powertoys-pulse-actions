# Source import

The complete Pulse Extension project was imported on 2026-09-23 from the local
Git history of `moooyo/Pulse-Extension` at commit
`a0bfa923eda634aaae3ca1c1b01153fb707fec50` (`codex/uiux-local-package-20260918`).
This is the source revision used for the current 0.2.0 popup-sizing package.
The source commit identifier records provenance; the original branch need not
be available from the source remote.

All 193 tracked source files were imported. Documentation was adjusted for the
new project location. Generated packages, `node_modules`, `.tmp`, `bin`, `obj`,
browser profiles, installed Host state and task data are not part of the import.

## Project boundary

The project is self-contained under `pulse-extension/`:

- `extension/`: Chrome/Edge extension source, static UI and source tests.
- `host/`: .NET Native Messaging Host, task execution and GitHub integration.
- `prompts/`: workflow templates embedded in the Host.
- `installer/`: publishing, installation and removal scripts.
- `tests/`: Host and Native Messaging tests.
- `docs/` and `examples/`: project documentation and integration examples.

The parent repository's `.github/skills`, `.github/workflows`, root PowerShell
scripts and `data/` remain separate and unchanged. This project does not replace
the existing Copilot skill suite or dashboard artifact format.

## Working in this repository

From the parent repository root, first run `cd pulse-extension`. Paths described
as the "repository root" in the imported project documentation refer to this
project directory. This also lets .NET discover the project's `global.json`.
The extension directory has its own `package.json` and lockfile; it is not a
parent-repository npm workspace.

For a source-only check with Node.js 22.18 or later, run:

```powershell
npm --prefix extension test
```

Use the host's configured package sources if dependencies need installation.
Build and installation steps remain in the [project guide](../README.md) and
[installer guide](../installer/README.md). Preserve the existing stable extension
identity and keep development origins restricted to development packages.

Historical acceptance documents describe the source project's local packages
and tests. Importing the source does not publish an extension-store release,
install software, or execute a GitHub action.
