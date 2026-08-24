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
- examine every eligible open non-draft PR, but use the skill's bounded run
  planner to select at most five stale PRs for a normal invocation;
- process no more than two PR review workers concurrently, prohibit nested
  agents, and require each worker to stop cleanly before the run deadline;
- when a worker reaches a cloud Copilot wait, checkpoint that stage and release
  the slot instead of polling; resume it in the next scheduler pass;
- publish the fresh inventory before launching review workers and leave
  unselected PRs explicitly queued for later scheduled runs;
- checkpoint every durable PR stage locally and push refreshed JSON after two
  transitions, ten minutes, or a completed review, whichever comes first;
- give every new or changed bug issue a lightweight explicit judgment and
  action;
- run only the bounded highest-confidence issue batch through the detailed
  design workflow;
- preserve and resume existing fork work instead of duplicating it;
- validate all newly processed artifacts and scan generated JSON for secrets;
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
