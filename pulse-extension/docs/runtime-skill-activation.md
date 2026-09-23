# Local runtime skill activation — 2026-09-16

The PowerToys verification skill has been corrected, integrated into local PowerToys `main`, and installed for the current Windows user. Pulse now refers to the optional installation only when runtime work is in scope. This is a local delivery; no remote repository or website deployment was updated.

## Source and installation

- Source: PowerToys commits `4d04aa808746ff20cb8e50e426c24eae8dd27ef2` and `fa3ae84ab84422f6369c6b2df91e6308b100813d` on `codex/gpt6-verification-skill`.
- Local integration: only those skill commits were cherry-picked onto the existing local main, now `7b36c93526ccb10bbd06ee79af3bf75e0b78106e`. The primary checkout remains on its existing development branch. The integrated skill tree is `cc119b67e708ae8587d6e1965aa18ee05059bba1`.
- Installation: `~/.agents/skills/pulse-powertoys-verification`, containing all 39 source files. Only the `SKILL.md` frontmatter name is changed; an additional `source.json` records the source commit/tree and every source/installed file hash. The previous installation was verified before replacement and retained outside the discovery directory.
- The unique name avoids the older repository skill's name. The selected Codex CLI discovered it, and the selected Copilot CLI reported that exact directory as enabled with source `personal-agents`, while retaining the older project skill separately.

Skill sources remain maintained in PowerToys. The personal installation is a versioned copy, not a second editable source or part of the Pulse package. A later update should use a committed source, preserve any local edits, and verify the replacement files before activating it.

The corrections retain evidence for the exact tested artifact, real shortcut/input paths and prior user state. They fix stale helper names and parameters, default-state restoration, unconditional environment changes and profile maintenance. Foreground and CmdPal recovery guidance now states the helpers' actual limitations, including ambiguous same-AppId instances and recovery that launches an installed package. Helper executable logic was not changed.

## Pulse integration

The new `runtime-skill.shared.prompt.md` fragment uses the existing bundled prompt assembly and snapshot mechanism. It is absent from static and build-tests PR tasks and Feature Research. UI/E2E tasks receive it; historical reviews and Issue investigation, implementation and verification use it only when the accepted work actually runs PowerToys. Writing a future experiment or executing a code-only plan does not activate it.

The agent reads the named skill and relevant resources on demand, recording its actual path and source commit from `source.json` in existing evidence fields. A missing installation does not prevent work supported by existing tools, trigger an installation, or select the older repository skill as a substitute. Host scope, permissions, source identity and the v3 JSON result remain authoritative.

Both shared fragments contribute SHA-256 values to the catalog revision and accepted prompt snapshot. The installed skill itself remains external; reports identify the version actually read. Existing accepted task snapshots are not rewritten.

## Verification

- Final Host Release build: zero warnings and errors; complete Windows-apphost automated scenario suite passed.
- Self-contained win-x64 publish and all 10 Native Messaging tests passed.
- 26 assembled prompts across 13 profiles and both adapters: 18 include the conditional runtime fragment, 8 omit it. Schema and task contexts match the previous audit. Omitted instruction text is unchanged after normalizing checkout CRLF/LF; it is not claimed to be byte-identical to the earlier build.
- Catalog fragment hashes and rendered prompt hashes verified. The installed Host serves the exact eight business prompts and two shared-fragment hashes; catalog revision `d76d1cb2209530b120276316ed243a20e09fde521067f87bcd177121d4a187af`.
- All 192 published Host files match the installation; all 58 existing configuration/task/prompt files retain their hashes. Existing extension IDs and development-origin configuration were reused.
- Skill YAML was parsed with an existing host `js-yaml`; relative links/anchors, PowerShell syntax and unchanged executable tokens were checked. The bundled `quick_validate.py` was attempted but could not run because its Python lacked PyYAML; no package was installed for that check.
- Independent read-only scenarios covered static review and CmdPal shortcut verification with another installed instance running. These are instruction-level checks, not actual UI observations.

Local evidence is retained in the task worktree's `.tmp/skill-activation/`: `installation.json`, `cli-discovery.json`, `assembly-verification.json`, `installed-verification.json`, build/test/publish logs and the assembled prompts.

No real model workflow, desktop interaction or GitHub write was performed. Controlled worker tests and CLI discovery do not establish full Chrome/Edge lifecycle acceptance or real-model output quality; those checks remain in [acceptance](acceptance.md).
