# PowerToys dashboard skills

Public, reusable Copilot skill suite for maintaining the
[PowerToys triage dashboard](https://muyuanms.github.io/powertoys-triage-board/).

The updater is an orchestrator and requires all four checked-in skills:

| Skill | Responsibility |
| --- | --- |
| `powertoys-dashboard-update` | Freshness queue, orchestration, artifact generation, project sync, publication |
| `powertoys-pr-review` | Context review, fork review loop, local build, proposed upstream review |
| `powertoys-issue-to-design` | Bug judgment, investigation, adversary-reviewed implementation design |
| `powertoys-design-to-pr` | Approved design to reviewed and build-verified fork PR |

## Install

```powershell
git clone https://github.com/MuyuanMS/powertoys-dashboard-skills.git
pwsh -NoProfile -File .\powertoys-dashboard-skills\Install-Skills.ps1
```

Use `-Update` to replace previously installed copies:

```powershell
pwsh -NoProfile -File .\powertoys-dashboard-skills\Install-Skills.ps1 -Update
```

Repository-level use is also supported: copy the four directories from
`.github/skills` into another repository's `.github/skills`.

## Prerequisites

- GitHub Copilot CLI with skill support
- Git, GitHub CLI, and PowerShell 7
- Read access to `microsoft/PowerToys`
- Write access to the operator's PowerToys fork and dashboard repository
- Copilot code review/coding-agent access on the fork
- Microsoft project permissions when project synchronization is required
- PowerToys local build prerequisites for full PR validation

No token or account-specific local configuration is stored in this repository.

## Dashboard update prompt

The same prompt is available as
[`UPDATE_DASHBOARD_PROMPT.md`](./UPDATE_DASHBOARD_PROMPT.md) and from the
dashboard's **Copy update prompt** button.

```text
Update the PowerToys maintenance dashboard end-to-end. Work autonomously until
the dashboard data is regenerated, validated, committed, pushed, and the
deployment is verified.

First confirm gh/git/PowerShell and GitHub authentication. Check that
powertoys-dashboard-update, powertoys-pr-review, powertoys-issue-to-design, and
powertoys-design-to-pr exist in `.github/skills` or `$HOME/.copilot/skills`.
If any are missing, clone
https://github.com/MuyuanMS/powertoys-dashboard-skills and run:
pwsh -NoProfile -File .\\powertoys-dashboard-skills\\Install-Skills.ps1
Then reload skills or restart Copilot CLI.

Locate or clone https://github.com/MuyuanMS/powertoys-triage-board and run the
powertoys-dashboard-update skill as the orchestrator. Follow all freshness,
dependency, validation, publication, and approval rules. Review every eligible
PR lacking a current clean result for its latest head; judge every new or
changed bug issue; run the bounded highest-confidence issue batch through
detailed design; preserve existing fork work; validate and publish the board.

Never post or modify anything in microsoft/PowerToys without explicit human
approval. At completion report coverage, issue judgments/designs, workflow
state, board counts, deployment URL, and whether any upstream action occurred.
```

For the complete copyable version, use
[`UPDATE_DASHBOARD_PROMPT.md`](./UPDATE_DASHBOARD_PROMPT.md).

## Feedback

Dashboard action feedback is stored as GitHub Issues in this repository.
Use the dashboard's thumbs-up/down controls or
[open feedback manually](https://github.com/MuyuanMS/powertoys-dashboard-skills/issues/new?template=action-feedback.yml).

## Validation

```powershell
pwsh -NoProfile -File .\Test-SkillSuite.ps1
pwsh -NoProfile -File .\.github\skills\powertoys-pr-review\tests\Test-ReviewPayloads.ps1
```

The suite must not contain generated dashboard artifacts, credentials, approval
decisions, or machine-specific run state.
