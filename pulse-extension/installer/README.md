# User-level Windows installation

These scripts register `com.powertoys.pulse` for the current Windows user in **both** Chrome and Edge. They do not install browsers, CLIs, CLI credentials, or the extension itself. No administrator access should be required for the user-level registration.

1. For final acceptance or an explicitly requested build, publish the self-contained Windows x64 Host:

   ```powershell
   .\installer\Publish.ps1
   ```

2. Load the extension in Chrome or Edge. The checked-in manifest public key keeps unpacked installations at `nlpkbkhlnocknpgkjnmhpahapffdhblo`, which is the installer's default. Register the Host:

   ```powershell
   .\installer\Install.ps1 -PublishedHostDirectory .\artifacts\host-win-x64
   ```

   `-ExtensionId` is an alias when registering one ID. A manifest with explicit `chrome-extension://<id>/` origins is generated at `%LOCALAPPDATA%\PulseExtension\native\com.powertoys.pulse.json`. The executable is fixed at `%LOCALAPPDATA%\PulseExtension\bin\Pulse.Host.exe`.

3. Install Git for worktree preparation, GitHub CLI (`gh`) for GitHub operations, and Codex CLI and/or GitHub Copilot CLI for agent tasks. Log into the CLIs separately. Reload the extension and open settings. Refresh installations, explicitly select a detected installation for each CLI you use, and save settings. Versions and full paths are listed without compatibility checks; nothing is selected automatically, including after upgrading from settings without a selection. The Test button uses the currently selected installation, sends `what's your model`, requires a real response and exit code `0`, and can be cancelled. It needs no repository. Detection alone does not establish successful model access.

4. Choose local file permissions (`read-only`, `workspace-write`, or `yolo`; default: `yolo`). `yolo` enables the agent's full-permission mode. Tasks have no execution deadline and can be cancelled manually.

5. Configure `mainRepoFolder` as the local PowerToys main repository, not a linked worktree. A fork origin is allowed, but at least one remote, such as upstream, must point to `microsoft/PowerToys`. Configure `worktreeRoot` as a folder outside that repository. Only PowerToys is supported; there is no repository mapping configuration. Each task creates `worktreeRoot/pulse-<UUID>` with a `codex/pulse-<UUID>` branch. Ordinary tasks use the main repository's HEAD captured when accepted. PR tasks with an expected SHA verify the target through `gh`, fetch the fixed PowerToys PR ref, and verify its SHA before creating the worktree. Completion, cancellation, and clearing task history retain the worktree, branch, and local changes.

6. Under GitHub configuration, detect local `gh auth status`, select an account with successful authentication, and save it. Usernames containing underscores are supported. Agent login and `gh` authentication are independent. All extension GitHub operations use `gh api`; the selected account's token stays in memory and the relevant child process environment, without changing the global active account or storing the token in settings. Website GitHub drafts also use the extension's explicit confirmation flow.

7. Save separate model and reasoning defaults for each CLI and review the bundled prompt selections. The Host loads eight repository-owned workflows offline; there is no remote prompt-sync step. Existing PR-review, issue-fix, E2E and reproduction selections remain supported; Feature/Bug research and saved-plan follow-ups use their corresponding bundled entries. Update the Host to receive new prompt content. Per-task Run options can override agent, model and effort without changing saved defaults. See the [workflow guide](../README.md#workflows-and-results) and [prompt catalog](../prompts/README.md).

For a store build or another extension identity, pass its actual Chrome/Edge IDs explicitly with `-ExtensionIds`. Existing preview installations created before the stable public key must reload the extension and repair Host registration for the new ID. The public key is an unpacked-development identity, not a published store listing or signing credential.

Use `-DevelopmentOrigins` with local integration; it sets the matching explicit Host development-origin policy. Run the Pulse website with `npm run dev -- --port 8080` or `npm run dev -- --port 8081` and use the development extension (`npm run build:dev`, only during final validation or when explicitly requested). Both layers allow only `http://localhost:8080`, `http://127.0.0.1:8080`, `http://localhost:8081`, and `http://127.0.0.1:8081`. Normal installation leaves development origins off. Host startup additionally checks the extension origin supplied by Chrome/Edge against `installation.json`.

Run the same install command to repair registration or upgrade. Installation stages the published files, enters a shared maintenance guard, and invokes the installed Host with `--has-active --data-root <root>` before replacing components. Exit `0` permits the operation, `2` blocks for an active task, submission, or agent test, and all other codes block because idleness is unknown. Finish or cancel active work first. New work is denied during maintenance.

Upgrades wait for settings/prompt publication, then close only idle connection processes launched from this installation's exact Host path. Browsers and CLIs remain open. Workers, diagnostic processes, unknown arguments, and connections using another data directory block replacement rather than being terminated. A bounded retry handles reconnects and Windows releasing image handles; rollback uses the same checks. Configuration, task records, and logs remain in place. Reload the extension after upgrading so its UI and Host use the matching versions. Any backup directory reported after cleanup fails is retained for inspection.

To uninstall:

```powershell
.\installer\Uninstall.ps1
```

Uninstall performs the same activity check, removes only this product's matching Chrome/Edge registrations and installed binaries/manifests, and retains configuration, task records, and logs in `%LOCALAPPDATA%\PulseExtension`. It never deletes user repositories, task-produced repository files, CLIs, or login state. If task records remain but the installed binary is missing, repair first so activity can be verified. Task history is managed from the extension; no destructive data purge is bundled into uninstall.

If registration appears unavailable, confirm that the two HKCU `NativeMessagingHosts\com.powertoys.pulse` default values point at the generated manifest, the manifest executable path exists, and the manifest contains the actual extension ID. Managed browser Native Messaging policies can still disallow launching a registered Host. A `BACKGROUND_JOB_RESTRICTED` error means the environment prevented the worker from escaping browser lifetime control; do not treat that as a successful detached launch.

The localhost UI preview is separate from installed Native Messaging operation. Its `/__pulse/diagnostics` bridge can forward `agents.test.start`, `agents.test.get`, `agents.test.cancel`, `github.accounts`, `prompts.list`, `prompts.get`, and the compatibility `prompts.sync` method to the local Host. Prompt sync only reloads the embedded catalog. Agent tests forwarded to the Host invoke real CLIs; preview workflow tasks and GitHub submissions remain simulated, and preview settings are session-only. Earlier successful CLI probes do not establish complete current-workflow or installed-browser acceptance. The user has deferred the full Chrome/Edge × Codex/Copilot and real-workflow checks to later testing; see [acceptance](../docs/acceptance.md).
