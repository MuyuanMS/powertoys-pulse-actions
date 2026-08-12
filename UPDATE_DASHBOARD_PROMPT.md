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
   https://github.com/MuyuanMS/powertoys-dashboard-skills and install it:
   `git clone https://github.com/MuyuanMS/powertoys-dashboard-skills.git`
   followed by
   `pwsh -NoProfile -File .\\powertoys-dashboard-skills\\Install-Skills.ps1`.
   Then reload skills or restart Copilot CLI before continuing.
4. Locate or clone `https://github.com/MuyuanMS/powertoys-triage-board`, enter
   that checkout, and preserve unrelated local changes.
5. Verify the authenticated operator has read access to
   `microsoft/PowerToys`, write access to their PowerToys fork and the dashboard
   repository, and Microsoft project 2445 access if project synchronization is
   expected.

Use the `powertoys-dashboard-update` skill as the orchestrator. Follow all its
dependency, freshness, artifact-schema, validation, publication, and approval
rules. In particular:
- examine every eligible open non-draft non-CmdPal PR and review any PR without
  a current clean result for its latest head;
- give every new or changed bug issue a lightweight explicit judgment and
  action;
- run only the bounded highest-confidence issue batch through the detailed
  design workflow;
- preserve and resume existing fork work instead of duplicating it;
- validate all newly processed artifacts and scan generated JSON for secrets;
- regenerate `data/index.json`, `data/index.js`, and per-number artifacts;
- synchronize project state when permissions are available;
- commit and push only dashboard/skill data to the dashboard repository.

Do not post reviews or comments, open pull requests against
`microsoft/PowerToys`, or modify upstream issue metadata without explicit human
approval. Fork-side work and dashboard publication are allowed under the
skills' existing gates.

At completion, report PR coverage, issue judgments/designs, resumed workflows,
closed/waiting items, generated counts, deployment URL, and explicitly confirm
whether any upstream public action occurred.
```
