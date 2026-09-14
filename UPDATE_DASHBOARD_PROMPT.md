# PowerToys dashboard update prompt

Copy the prompt below into GitHub Copilot CLI:

```text
Update the PowerToys maintenance dashboard end-to-end. Work autonomously until
the dashboard data is regenerated, validated, committed, pushed, and the
deployment is verified.

Before starting:
1. Confirm `gh`, `git`, and PowerShell 7 are available and `gh auth status`
   succeeds.
2. Check that these four Copilot skills exist either in the current
   repository's `.github/skills` directory or under `$HOME/.copilot/skills`:
   - powertoys-dashboard-update
   - powertoys-pr-review
   - powertoys-issue-to-design
   - powertoys-design-to-pr
3. If any skill is missing, obtain the complete suite from
   https://github.com/MuyuanMS/powertoys-pulse-actions and install it:
   `git clone https://github.com/MuyuanMS/powertoys-pulse-actions.git`
   followed by
   `pwsh -NoProfile -File .\\powertoys-pulse-actions\\Install-Skills.ps1`.
   Then reload skills or restart Copilot CLI before continuing.
4. Use `https://github.com/MuyuanMS/powertoys-pulse-actions` as both the
   skill suite and action-artifact source. Locate or clone the PowerToys Pulse
   repository/preview branch you are authorized to update. Preserve unrelated
   local changes in both checkouts.
5. Verify the authenticated operator has read access to
   `microsoft/PowerToys`, write access to their PowerToys fork and the dashboard
   repository, and Microsoft project 2445 access if project synchronization is
   expected.

Use the `powertoys-dashboard-update` skill as the orchestrator. Follow all its
dependency, freshness, artifact-schema, validation, publication, and approval
rules. In particular:
- examine every eligible open non-draft PR, but use the skill's run planner to
  select at most sixteen stale PRs for a normal invocation, or every stale PR
  when `POWERTOYS_DASHBOARD_DRAIN_QUEUE=1` is explicitly requested;
- process no more than three PR review workers concurrently in normal mode, or
  six in drain mode; prohibit nested agents, and require normal-mode workers to
  stop cleanly before the run deadline;
- when a worker reaches a cloud Copilot wait, checkpoint that stage and release
  the slot instead of polling; resume it in the next scheduler pass;
- before selecting new PRs, identify carryovers that were selected in the
  previous scheduled run but still have no maintainer action. Reserve the first
  review slots for those carryovers, oldest unchanged checkpoint first. In
  particular, keep PRs 50020, 50472, 49963, 50497, and 50495 in the carryover
  set until each becomes action-ready, is legitimately waiting on the author,
  or has a current terminal blocker with exact manual remediation;
- enforce run-over-run liveness for every selected carryover. A selected PR
  must move to a later concrete phase, produce a current allowed maintainer
  action, become legitimately author-waiting, or record a new external wait
  that was actually requested during this run. Updating `generated_at`,
  `source_updated_at`, status text, or republishing the same `queued`,
  `waiting_copilot`, or generic `review_in_progress` phase is not progress;
- resume from the recorded phase rather than restarting it. For `queued` or
  `mirroring`, perform the next fork setup/synchronization step. For
  `waiting_copilot`, inspect the existing fork PR's latest Copilot review and
  unresolved threads first; consume a completed review immediately, and only
  request another review when the prior request is absent or was invalidated
  by a new head. Never overwrite a more advanced checkpoint with a generic
  queue artifact;
- if a carryover cannot advance, preserve its full fork trace and exact phase,
  explain the concrete external or technical blocker, and report the run as
  blocked/partially complete rather than successful. The completion report
  must list every selected carryover's previous phase, final phase, and
  evidence of advancement;
- publish the fresh inventory before launching review workers and leave
  unselected PRs explicitly queued for later scheduled runs;
- checkpoint every durable PR stage locally and push refreshed JSON after two
  transitions, eight minutes in normal mode, five minutes in drain mode, or a
  completed review, whichever comes first;
- give every new or changed bug issue a lightweight explicit judgment and,
  when actionable, schema-version-5 display-only issue context summarizing the
  discussion, known facts, qualified inferences, Copilot analysis, initial
  investigation, and exact information gaps;
- make every request-info draft issue-specific: acknowledge useful evidence
  already supplied, explain why it is insufficient, ask only for the missing
  information that changes triage, and use established collection commands
  such as `/bugreport` when a fresh PowerToys diagnostic ZIP is needed;
- run only the bounded highest-confidence issue batch through the detailed
  design workflow in normal mode; in drain mode, process all actionable issue
  designs with the same checkpoint and publish guarantees;
- preserve and resume existing fork work instead of duplicating it;
- validate all newly processed artifacts and scan generated JSON for secrets;
- run targeted PR-state checks before publication: reject under-validated
  `review_ready`, mismatched validation heads, resumable stages that expose
  concluded review actions, terminal blockers without `terminal: true`, and
  stale-queue entries that lose `work_type` or machine-readable reasons;
- regenerate `data/index.json`, `data/index.js`, and per-number artifacts;
- publish completed review artifacts incrementally and finish the run with
  unfinished PRs queued/running; require a zero stale queue only when
  `POWERTOYS_DASHBOARD_DRAIN_QUEUE=1` was explicitly requested;
- send brief Outlook-only scheduled-run status notifications when M365/WorkIQ
  tools are available: default to mail to the signed-in user, disable only with
  `POWERTOYS_DASHBOARD_NOTIFY=none`, include the PR/issue sets selected for
  this run in the started email, and reply to that original email at completion
  or at the 30-minute mark if the run is still active;
- synchronize project state when permissions are available;
- commit and push only action-artifact data to the artifact repository;
- synchronize those artifacts into PowerToys Pulse with
  `scripts/sync-triage-artifacts.mjs`, then lint/build Pulse;
- verify Pulse copied the just-published feed rather than an older snapshot:
  compare the synchronized index's `generated_at`, artifact count, and source
  dashboard commit/version with the published source, and fail the run on any
  mismatch. The sync source must be `MuyuanMS/powertoys-pulse-actions`, never
  the retired `powertoys-triage-board`;
- publish or dispatch the approved Pulse preview/Pages workflow. Treat Pulse as
  the user-facing dashboard and this skills repository's `data/` directory as
  its artifact transport, not as the final preview.

Do not post reviews or comments, open pull requests against
`microsoft/PowerToys`, or modify upstream issue metadata without explicit human
approval. Fork-side work and dashboard publication are allowed under the
skills' existing gates.

At completion, report PR coverage, issue judgments/designs, resumed workflows,
closed/waiting items, generated counts, deployment URL, notification delivery
status, and explicitly confirm whether any upstream public action occurred.
```
