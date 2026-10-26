# Start or resume a PR-only drain

Paste into the owning update session. This is a task request, not another copy
of the skills' policy.

```text
Start/resume one PowerToys PR-only drain using canonical
MuyuanMS/powertoys-pulse-actions main.

Recover the existing owner and useful fork/worker/request state first; do not
duplicate a live run. A NEW run includes all areas, INCLUDING CmdPal.
Preserve an existing explicitly non-CmdPal scope unless I request expansion.

Fast-forward a clean dedicated main checkout, run Install-Skills.ps1 -Update,
then Install-Skills.ps1 -VerifyOnly, and reload the rewritten skill suite.
Record its revision and update existing workers at a safe checkpoint.

Load powertoys-dashboard-update and follow its PR-only drain path. Execute
dispatch, validate and publish artifacts incrementally, and refill freed slots.
Use powertoys-pr-review for each worker, not inherited old review instructions.
No issue work, no nested workers, no local-review substitution.

Update this session's existing schedules without duplicating them:
the regular schedule keeps its cadence and covers all areas when no drain is
active; otherwise it services this same run. Replace a passive idle monitor
with at most one five-minute drain watchdog that executes dispatch. Stop it
after the final reconciled outcome; keep the regular update schedule.

Finish with verified per-PR delivered-action coverage and Pulse delivery,
not checkpoint counts. Use -FailOnStale only for a genuinely complete scoped
drain; report exact private exceptions separately as incomplete coverage.
Fork review work and authorized artifact publication are allowed.
No writes to microsoft/PowerToys without separate explicit approval.
```
