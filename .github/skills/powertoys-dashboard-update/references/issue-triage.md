# Issue delta triage and design routing

Use for selected issues in a normal update, targeted issue work, or an
explicit all-work drain. A PR-only drain does not load or execute this path.
Keep existing judgments/current fork work; do not start a competing fix.

## Discover and read

`Get-StaleIssueTriageQueue.ps1 -Dashboard <canonical-main> -AsJson` enumerates
every open bug missing a valid current schema-v5 artifact or having newer
upstream activity. A recent generation timestamp does not excuse a malformed
artifact. `Get-DashboardUpdateRunPlan.ps1` bounds normal execution and leaves
unselected issues explicitly deferred.

Read the issue body and full relevant discussion, labels, attachments,
assignees, linked PRs/issues and existing fork traces. Detect closed, handled,
superseded, author-response and ownership changes. Perform a focused initial
investigation of likely code, history, duplicates and diagnostic evidence
before defaulting to a request for logs.

Freshness uses covered `source_updated_at` and latest upstream comments,
not generation time. New discussion invalidates old actionable proposals until
reassessed. Unchanged valid schema-v5 bugs need not be reread every run.
The default 30-day window prioritizes detailed design; it never excludes older
bugs from judgment/fix coverage.

## Make one explicit judgment

| `judgment.status` | Outcome |
| --- | --- |
| `actionable_design` | Confidence-scored fix proposal and `approve_design`; eligible for detailed design. |
| `reproducible` | Maintainer-ready `reproduce` steps when reproduction is useful but design is not yet justified. |
| `needs_information` | Specific reporter-actionable `request_info`, not a generic log checklist. |
| `duplicate_or_handled` | Linked covering issue/fix/owner; no competing agent work. |
| `waiting_on_author` | Actual outstanding request, requested evidence and waiting-since time. |
| `not_actionable` | Evidence-based expected/external/unsupported/out-of-scope explanation. |

Every judgment includes `rationale`, concrete `evidence`, `recommended_action`,
`evaluated_at` and covered `source_updated_at`. Separate facts from inference.
Lightweight triage is not an implementation-grade design.

## Schema-v5 public result

Use root `SCHEMA.md` and `Test-DashboardArtifacts.ps1` for exact artifact syntax.
Every newly assessed open bug includes:

| Field | Required content |
| --- | --- |
| `fix_assessment` | `status: proposed|existing_fix|not_applicable` and rationale. `existing_fix` includes public covering URLs. |
| `proposed_fixes[]` | For a proposed applicable fix: title, root-cause hypothesis, ordered plan, verification and confidence. |
| `issue_context` | `summary`, `known_information`, qualified `inferences`, `analysis`, `initial_investigation`, `information_gaps`. |
| `actions[]` | Concrete supported actions, never hold/monitor/wait placeholders. Every proposed fix includes `approve_design`. |

Confidence is canonical in `proposed_fixes[].confidence`: score 85-100 green,
51-84 yellow, 0-50 red, plus evidence-based rationale. It measures whether the
root cause/fix is supported, not optimism or severity.

Yellow/red proposals need an uncertainty-reducing action: `request_info` when
the reporter can supply decisive evidence, otherwise a concrete maintainer
`reproduce` action. Do not ask reporters to perform maintainer profiling.
Green may omit both only when no material uncertainty remains.
For red hypotheses, an optional one/two-iteration investigator/adversary pass
may cheaply resolve uncertainty; stop after two, never fabricate confidence.

`reproduce` includes module, version/build when relevant, prerequisites,
numbered steps, expected result and setup. Include `setup_prompt` for local
asset preparation or public `attachments` links when needed.

No competing proposal when an existing upstream/fork fix covers the issue.
`not_applicable` requires a real reason; insufficient evidence alone is not
permission to hide an otherwise applicable investigation.

## Ask for exactly the missing evidence

Use the sibling issue-design skill's `references/requesting-information.md`
for established collection methods. The proposed comment must acknowledge
what the reporter already supplied, explain the unresolved decision, and
directly request only the next discriminating evidence.

Each context gap records `information`, `why_needed`, `how_to_collect` when
known, and an `evidence_type` from:

```text
bugreport_zip, repro_steps, screenshot_image, gif_video, sample_file,
event_viewer, crash_dump, module_trace, installer_log, powertoys_version,
windows_version, install_scope, settings_permissions, configuration_export,
keyboard_layout, monitor_topology, other_software, behavior_confirmation
```

Use an evidence-specific action label such as `Request activation trace`, not
`Request information`/`Ask for more details`. Several asks should be a short
scannable list. The comment and `issue_context.information_gaps` must agree.

- For fresh PowerToys diagnostics, ask for a `/bugreport` comment and capture
  immediately after reproduction. If Settings/tray UI cannot open, provide
  the established `BugReportTool.exe` fallback.
- Do not ask for raw logs when `/bugreport` packages them. Specialized traces
  need an established collection method.
- Do not request evidence already supplied in text/attachments. After an
  answer, explain what it established and ask only the next unresolved item.
- Removal of Needs-Author-Feedback does not prove the evidence arrived; read
  the response. A reproducible report needing maintainer investigation gets
  `reproduce`, not another reporter log request.

Public context is read-only decision support. Keep it separate from
`actions[].comment.body`; never append it automatically to upstream comments.
No private paths, sensitive diagnostics, credentials or unsupported assertions.

## Detailed design and implementation

Normal updates select at most six full designs by default after the cheap
judgment pass, favoring confidence, reproducibility, small scope and lack of
existing ownership. Other proposals retain their concrete `approve_design`
action. Only an explicitly requested all-work drain removes this cap.

Use `powertoys-issue-to-design` for selected full designs, resume its existing
fork mirror, run its adversary loop and stop at the design approval gate.
Required design fields:

- Root cause and evidence: failing mechanism, call/data flow, facts/inferences.
- Affected files: exact repository paths, roles and symbols.
- Ordered implementation steps: file/symbol, current behavior, proposed change,
  focused code sketch, state/threading/error/edge cases and tests.
- Verification: focused commands and reproduction with expected results.
- Risk and alternatives: blast radius, compatibility and rejected approaches.

Map to `design.root_cause`, `evidence`, `affected_files`,
`implementation_steps`, `verify`, `risks`, `alternatives`. Each implementation
step records `order`, `file`, `symbols`, `current_behavior`, `change`,
`code_block`, `edge_cases`, `tests`. Unidentified code means investigate more,
not declare a design ready.

Only an approved design enters `powertoys-design-to-pr`; implementation stops
before opening upstream without separate approval. Allowed issue controls are
`request_info`, `reproduce`, `approve_design`, `post_comment` and
`open_upstream_pr` with their respective evidence/approval gates.

Publish through `publication.md`. Invalid/partial results have actions hidden
and remain queued; do not count them as delivered just because a file exists.
