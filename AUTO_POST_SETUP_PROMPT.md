# Second-machine setup prompt

Paste this into Copilot CLI on the dedicated **Windows** posting machine.
Authenticate GitHub CLI and Copilot first. The active GitHub account must be
the account on whose behalf you intend to comment on PowerToys PRs. No token
needs to be pasted into the prompt. This is a standing authorization for this
separate opt-in skill, not permission for the updater to post upstream.

```text
Set up and run the independent powertoys-pr-auto-post skill on this machine.
I authorize this skill to automatically post only high-confidence, verified
COMMENT reviews on microsoft/PowerToys, under the GitHub CLI account currently
authenticated at setup. Record that exact login as the configured posting
identity; never switch accounts. Do not post approvals, request changes, merge,
close, label, trigger CI, edit/delete published comments, or act on issues.
This standing approval applies only to this evaluator's gated publisher, not
to powertoys-dashboard-update or its PR workers.

Verify Git, PowerShell 7, GitHub CLI authentication, and Copilot CLI skill
support. Locate or clone https://github.com/MuyuanMS/powertoys-pulse-actions
into a dedicated clean C:\PowerToysPulseAutoPost\source checkout on main.
Do not reuse an updater's checkout or modify dirty/divergent work. Fetch and
fast-forward main. Confirm .github\skills\powertoys-pr-auto-post\SKILL.md
exists; if it does not, stop and report that the skill has not reached main.
Run Install-Skills.ps1 -Update and -VerifyOnly, then load powertoys-pr-auto-post
and read its current SKILL.md and references\evaluation.md. Keep artifacts
sourced exclusively from actions main. Never run the dashboard updater,
regenerate/push its data, deploy Pulse, or start a PR-review drain.

Keep private assessments, evidence receipts and stable posting state under
%LOCALAPPDATA%\PowerToysPulse\auto-post, outside repositories. Configure one
designated posting machine and no concurrent evaluators. Preserve its ledger
between runs. Recover and verify producer evidence if accessible; otherwise
reconstruct exact upstream grounding and perform the necessary focused checks
on this machine. Missing tools/evidence mean hold, never fabricated proof.

Run the skill once now: inventory and score every current candidate in the five
dimensions, apply all hard gates, run the dry-run publisher before actual
posting, then post up to five eligible PR COMMENT reviews. Keep complete
atomic groups. Prioritize unresponded PRs approaching seven days, then score
and age. Deduplicate against all live participants and Pulse markers. Scores
must be independently justified, not copied from artifact confidence metadata.
Do not manufacture comments just to improve the response metric.

After that run, schedule this same skill hourly using Copilot CLI's /every
session scheduling. Each run must refresh clean canonical main, reload the
installed current skill, verify the saved exact posting account, evaluate all
current valid artifacts afresh, preserve the private ledger and post at most
five gated PR reviews. Reuse this standing COMMENT-only authorization. Save a
private report of dimensions, holds, duplicates, deferred eligible proposals,
confirmed posted URLs and failures. Keep the Copilot session running and use
its documented keep-alive feature to avoid sleep. Report setup, first-run
results, and the hourly schedule. If scheduling is not available in this CLI
build, report that explicitly and provide the documented command for a manual
run rather than pretending recurring execution was installed.
```

For subsequent runs in the configured session:

```text
Run powertoys-pr-auto-post using the saved standing COMMENT-only authorization,
configured posting login, dedicated clean canonical main checkout and stable
private ledger. Refresh and reload the current skill. Score all current valid
PR artifacts; dry-run and post at most five gated reviews; report confirmed
URLs, holds, duplicates, deferred items and errors. Do not run the updater.
```

Copilot CLI's documented recurring-session command is `/every 1h <prompt>`;
it requires that session to remain running. `/keep-alive on` prevents sleep
for that run. This setup does not install a Windows service or a reboot-persistent
Task Scheduler job. If the session exits, restart it with the setup prompt or
the saved configuration and re-arm the hourly schedule. Never launch a second
posting session while the first is active.
