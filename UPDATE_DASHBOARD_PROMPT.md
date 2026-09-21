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
   skill suite and action-artifact source. Use a dedicated checkout whose
   checked-out branch is exactly `main`, set `POWERTOYS_DASHBOARD_PATH` to that
   checkout, fetch `origin`, and fast-forward it with
   `git pull --ff-only origin main`. Abort if that checkout is dirty, detached,
   divergent, or on any other branch; never switch or reuse a Pulse preview,
   feature, or development checkout for action-data publication. Locate or
   clone the separate PowerToys Pulse repository/preview branch you are
   authorized to update. Preserve unrelated local changes in both checkouts.
5. Verify the authenticated operator has read access to
   `microsoft/PowerToys`, write access to their PowerToys fork and the dashboard
   repository, and Microsoft project 2445 access if project synchronization is
   expected.
6. On every run, synchronize the skill source from canonical `main`, not merely
   when a skill is missing. Run `Install-Skills.ps1 -Update` from that clean
   checkout and read its current skill entry points/references. Record the
   loaded source commit; do not continue with an older in-memory prompt.

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
- use the PR skill's paginated Get-CopilotReviewStatus.ps1 to resume a saved
  request by fork head and original timestamp (and baseline review ID when
  saved). Do not call Request-CopilotReview.ps1 merely to check status. For a
  genuinely new bounded round use -TimeoutMinutes 0 and persist its returned
  identity. Recheck once before publishing waiting_copilot; consume an arrived
  review instead of waiting/requesting again. Exhaust review/comment pages and
  thread cursors; API failures must be reported, not labeled as pending reviews;
- prioritize repeatedly deferred finalization: account for prior findings,
  ground them against current upstream, validate the exact candidate, then
  draft publishable comments. Recompute cutoffs from this run's deadline and
  do not inherit expired deadlines or count timestamp-only rewrites as progress;
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
- follow `powertoys-pr-review/references/finding-grounding.md` for every PR
  finding, including rewritten general comments: prove the defect on the pinned
  upstream tree, check existing guards and author counterevidence, distinguish
  upstream defects from agent-introduced regressions/optional experiments, and
  keep a private source-and-body-hash dossier; no clean fork review or build can
  substitute for that proof;
- after emission/sanitization, run `Test-DashboardArtifacts.ps1` with explicit
  processed `-Numbers`, `-RequireFindingGrounding`, and `-GroundingPath` pointing
  to the private dossier (optionally `-SourceRepository` for pinned git blobs).
  Never publish that dossier. Missing/contradictory evidence means withdraw the
  proposal and checkpoint unfinished work, not invent a passing result;
- author rebuttals require reassessing a finding even without a new commit:
  revalidate substantively and preserve rejected-finding reasons against revival.
  Explicit targeted reruns take priority over normal freshness skipping;
- before emitting any apply-ready PR suggestion, run the PR review skill's
  raw-blob line-ending check against the exact pinned upstream head; reject
  suggestion blocks for files mixing LF, CRLF, or lone CR endings and retain
  the finding as exact inline prose or an implementation-ready general comment
  instead, because GitHub Apply suggestion can otherwise rewrite the full file;
- when one correction maps to multiple safe current-diff locations, emit
  separate inline suggestions with one atomic selection group so Pulse shows a
  shared checkbox and posts every member together; use numbered `(1/N)` titles
  and reject partial, optional, out-of-diff, or incompletely validated groups;
- for PR findings that cannot be anchored inline, publish only consolidated,
  implementation-ready general comments that name affected paths/symbols,
  explain the problem and impact, provide ordered change guidance or
  illustrative pseudo-code, and state verification; reject terse or duplicate
  conversions of suppressed review findings;
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
- immediately before committing, rerun
  `Assert-CanonicalDashboardTarget.ps1`; commit action-artifact data only on
  the canonical checkout's `main` branch and push explicitly with
  `git push origin main:main`, never `git push origin HEAD`;
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
