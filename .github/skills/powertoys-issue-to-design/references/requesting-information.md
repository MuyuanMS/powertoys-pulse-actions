# Requesting Information from PowerToys Reporters

Use this guide when public evidence is insufficient to reproduce a bug, choose
between plausible causes, or prepare a safe fix. A request for information is a
diagnostic action, not a generic holding message.

## Required sequence

1. Read the complete issue body and discussion, including attachments and prior
   maintainer questions.
2. Perform the focused code, duplicate, history, and ownership investigation
   that is possible without the reporter.
3. Record what is already known and the exact decision that remains blocked.
4. Ask for the smallest evidence package that distinguishes the remaining
   possibilities. Separate **Needed** evidence from merely **Helpful**
   context.
5. Give the established collection method in the same comment.
6. When evidence arrives, inspect it and ask only the next question derived
   from that evidence. Do not repeat the original checklist.

`Needs-Author-Feedback` is a workflow label, not proof that the requested
evidence was received. An author reply can clear the label even when it does not
contain the requested ZIP, screenshot, file, or answer. Inspect the reply
content and attachments before marking the request satisfied.

## Evidence categories

Every `issue_context.information_gaps[]` entry must use one of these
`evidence_type` values:

| `evidence_type` | Ask when | Collection guidance |
| --- | --- | --- |
| `bugreport_zip` | PowerToys logs or diagnostic state are needed for a crash, startup, lifecycle, settings, module, or cross-process failure. | Ask the reporter to reproduce and submit a comment containing `/bugreport` immediately afterward. If Settings/tray UI cannot open, explain how to run `BugReportTool.exe` from the PowerToys installation directory. |
| `repro_steps` | The triggering sequence, expected result, or exact failing transition is unclear. | Ask for short numbered steps, prerequisites, expected result, actual result, and whether restart/reboot changes it. |
| `screenshot_image` | A static error, layout, state, or settings value is unclear. | Ask for the exact surface and tell the reporter to upload through the GitHub web UI because email replies may drop attachments. |
| `gif_video` | Timing, animation, focus, pointer, keyboard, or multi-step UI behavior cannot be understood from a still image. | Ask for a short recording focused on the failing transition, with sensitive content hidden. |
| `sample_file` | The failure depends on a document, image, archive, path shape, encoding, or other input asset. | Ask for the smallest non-sensitive reproducing file uploaded through the GitHub web UI. If it cannot be shared, ask for a synthetic equivalent and the properties that make it fail. |
| `event_viewer` | A crash or process exit is absent from the PowerToys report or needs OS-level correlation. | Ask for the relevant Application log event, timestamp, faulting module, exception code, and stack/details when present. |
| `crash_dump` | A reproducible crash requires native or managed fault-state inspection beyond packaged logs. | Ask for the smallest relevant dump and exact occurrence time only when a maintainer/debugger can use it; prefer `/bugreport` first. |
| `module_trace` | A known module-specific trace or focused profiling capture distinguishes code paths that ordinary logs cannot. | Use only an established repository script, logger, or maintainer procedure; name it exactly. |
| `installer_log` | Install, update, repair, or uninstall behavior is failing. | Ask for exact error text, installation source and scope, relevant installer/temp logs, and only the registry/cache facts needed to distinguish the suspected path. |
| `powertoys_version` | The report may predate a fix or contains conflicting versions. | Ask for the version shown in PowerToys About and whether it reproduces on the latest stable build. |
| `windows_version` | Behavior depends on an OS release/build. | Ask for edition and build from `winver`, not only “Windows 10/11.” |
| `install_scope` | Store/GitHub, per-user/per-machine, elevation, or user identity may change behavior. | Ask for install source, scope, elevation state, and whether PowerToys runs under the same Windows user. |
| `settings_permissions` | Settings fail to save/load or files appear inaccessible. | Ask whether settings files are hidden/read-only, whether controlled-folder/security software intervenes, and whether elevation or another user is involved. Do not request private setting values unless essential. |
| `configuration_export` | A specific setting combination or serialized configuration is required to reproduce. | Ask only for the relevant module export with secrets and personal paths removed; do not request an entire private configuration tree. |
| `keyboard_layout` | Keyboard Manager, Mouse Without Borders, AltGr, OEM, IME, or remapping behavior varies by input layout. | Ask for exact physical keys, displayed shortcut, keyboard language/layout on each involved machine, and other remappers. Use a focused capture script only when ordinary answers cannot distinguish the mapping path. |
| `monitor_topology` | DPI, taskbar, work area, orientation, docking, or monitor changes affect layout/window behavior. | Ask for monitor count, primary monitor, resolution/scaling/orientation for affected displays, taskbar edge/auto-hide, connection/dock type, and the exact Win+P/dock/sleep transition. |
| `other_software` | A shell extension, remapper, window manager, security product, or other application may intercept the path. | Name the suspected software class and ask whether disabling only that integration changes the result. |
| `behavior_confirmation` | Product intent or the exact observed result is ambiguous. | Ask one bounded question that distinguishes the possible behaviors; do not substitute a broad product survey. |

Do not invent a module-specific raw-log path when `/bugreport` already packages
the required PowerToys logs. Ask for a specialized module log only when the
repository or an established maintainer instruction names that collection path.

## Comment shape

Use this structure:

```markdown
Thanks for <specific useful evidence already supplied>.

**Needed**
1. <exact missing evidence and collection method>
2. <second item only when independently necessary>

**Helpful, if available**
- <nonessential context>

This will distinguish <possibility A> from <possibility B> and determine
<triage or implementation decision>.
```

For one short request, headings are optional. The comment must still:

- acknowledge evidence already present;
- name the unresolved ambiguity;
- directly ask for exact evidence;
- explain how to collect it;
- explain what decision it changes.

Good labels name the evidence: `Request startup bug report`,
`Confirm keyboard layout`, `Request installer transaction log`, or
`Request monitor topology`. Reject labels such as `Request information`,
`Request targeted evidence`, `Ask for focused repro details`,
`Ask for a narrower repro and fresh diagnostics`, and
`Reply with the missing-info request`.

## Established PowerToys patterns

- PowerToys diagnostic logs: ask the reporter to reproduce and comment
  `/bugreport`; do not say only “attach logs.”
- Inaccessible Settings or tray UI: include the `BugReportTool.exe` fallback.
- Installer/update failure: pair diagnostics with the exact visible error and
  install source/scope; after reading logs, ask the next log-derived question.
- File-dependent preview/thumbnail/locksmith failure: request the smallest
  shareable sample through the GitHub web UI.
- Keyboard/input failure: request exact key tuples and layouts, including both
  host and remote systems when applicable.
- Display/window failure: request the topology and transition that changes it,
  not a generic hardware inventory.
- Old report: first ask whether it reproduces on the latest stable release.

Representative precedent: PowerToys issues 9829, 9514, 17697, 23982, 27791,
28572, 32586, 35178, 47501, 49516, and 50037.

## Privacy and scope

Ask only for public-safe evidence that materially changes the decision. Warn
reporters to remove sensitive content from screenshots, sample files, logs, and
configuration exports. Never request credentials, private installation
directories, complete registry exports, or unrelated diagnostics.
