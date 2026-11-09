---
description: 'Verify scoped PR behavior on exact CI or locally built Debug bits.'
agent: 'agent'
---

# PowerToys PR Scenario Verification

Verify microsoft/PowerToys PR <PRNumber>: <PRTitle>
Target: https://github.com/microsoft/PowerToys/pull/<PRNumber>

Complete the selected PR verification and report what its evidence establishes. This is a bounded experiment, not another complete code review or an implementation task. Use the shared evidence guidance for preparing and interpreting checks.

For a linked pr-verify task, inherit the saved question, scenarios, expected observations and source identity. Inspect only code needed to execute and interpret them; do not repeat the parent investigation. A standalone e2e task derives focused scenarios from the requested behavior and relevant source.

Setup establishes the actual source and prerequisites. The execution check establishes whether the selected work was performed and interpreted. Report actual product observations separately: an experiment can complete and demonstrate a defect, while an unavailable essential scenario leaves verification incomplete.

## Missing CI: use a local Debug worktree

If a matching CI installer is missing, queued, expired or unusable, record that
condition and proceed to a local source build. Do not stop at "no CI build" or
wait for CI unless explicitly requested. Follow the detailed
[PR setup and testing workflow](../../.github/prompts/powertoys-pr-e2e-test.prompt.md#local-debug-source-build-fallback).

Pin the live upstream PR head, fetch its pull/<PRNumber>/head from
https://github.com/microsoft/PowerToys.git, and create a unique exact-head branch
and isolated worktree with tools\build\New-WorktreeFromBranch.ps1 -NoFetch (or
git worktree add if the helper cannot run). Preserve existing branches and dirty
work; verify the worktree HEAD before building.

Read the worktree's build guidance, initialize submodules, and run
tools\build\build-essentials.cmd -Platform x64 -Configuration Debug. Then build
the affected native/managed dependency chain with
tools\build\build.ps1 -Platform x64 -Configuration Debug -Path <ProjectDirectory>.
Use ARM64 when appropriate. Check each exit code and build.Debug.x64.errors.log;
do not launch failed or stale output. The essentials bootstrap is not an
affected-module build.

After successful builds and preservation of the original runner/settings,
launch the worktree's actual Debug runner/module from x64\Debug (or ARM64\Debug).
Verify the executable, process and loaded-module paths belong to the pinned
worktree. Run only the accepted scenarios; restore the original environment
and report the exact build/launch commands, source SHA and observed behavior.
Do not weaken signing/IPC checks or silently install packages. Explicitly
selected past CI builds can establish only earlier-revision results; test
later changes on the current-head local build.

Compare evidence with the entire accepted plan, including failed, conflicting and unexecuted scenarios. Retain the original E2E necessity level and parent report. Complete matching-source coverage can satisfy the evidence gap; generic success or a local candidate cannot.

Return the scoped findings, observations and coverage. Do not claim a complete code-review conclusion or manufacture an approval/Request changes proposal from verification alone. Leave unrelated Issue dispositions and plans inapplicable. A factual comment may report the observations; another verification is justified only by a meaningful remaining question or changed prerequisite.
