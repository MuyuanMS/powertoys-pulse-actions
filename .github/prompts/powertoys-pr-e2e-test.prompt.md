Build and test microsoft/PowerToys PR <PRNumber> locally: <PRTitle>
https://github.com/microsoft/PowerToys/pull/<PRNumber>

Prepare this PR for local end-to-end testing from source. Do not review the code, modify the upstream PR, push branches, or post comments. Use the powertoys-verification skill's PR-validation workflow when available.

CI installer downloads are not a ready-to-use E2E path: unsigned Release binaries can fail Settings IPC authentication, and unsigned MSIX packages can fail registration. Do not substitute a CI installer for this source-build workflow or treat a successful CI build/install as E2E validation.

Required workflow:
1. Read the PR description and diff, record the current upstream head SHA, and derive concrete behavioral checks. Locate the existing PowerToys clone and verify the repository's documented build prerequisites. Preserve all existing branches, changes, and worktrees.
2. Reuse an isolated exact-head PR worktree when safe, or fetch the PR head without changing the main checkout:
   git fetch origin pull/<PRNumber>/head:pr-test/<PRNumber>
   .\tools\build\New-WorktreeFromBranch.ps1 -Branch pr-test/<PRNumber>
3. Enter the worktree and initialize dependencies:
   git submodule update --init --recursive
   Confirm HEAD matches the recorded upstream PR head. Do not rebase or change the PR code merely to make the build pass.
4. Inspect changed files to identify the complete affected dependency/native/managed module chain. Build using the repository's documented scripts, starting with tools\build\build-essentials.cmd when required, then the affected projects in dependency order. Read the current repository instructions rather than inventing MSBuild commands.
5. Verify actual successful build exit codes, the built PowerToys.exe path, and the complete affected module binaries/resources. Establish module-specific signing and registration requirements explicitly; do not bypass authentication or silently trust certificates.
6. Preserve current PowerToys version, scope, processes and settings before sideloading. If package replacement is needed, obtain confirmation and back up the affected AppX LocalState as well as the ordinary PowerToys settings. Use a disposable test environment when possible and establish a verified restoration path first.
7. Launch the exact built binaries and verify their identity. Exercise the changed feature with concrete scenarios covering the intended behavior, original failure and relevant edge cases. Keep unavailable signing, hardware or manual requirements explicit rather than claiming success.
8. Restore the original runner, packages and settings. Return the PR SHA/worktree, exact launch path, commands and exit codes, each scenario's expected/observed result and PASS/FAIL/BLOCKED verdict, evidence/log paths and restoration status. Recheck the upstream head and label older-revision results accurately.

Do not claim E2E readiness until the affected chain builds and the feature is exercised successfully.
