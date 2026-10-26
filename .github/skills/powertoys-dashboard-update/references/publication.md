# Artifact publication and Pulse delivery

The coordinator alone publishes. Workers return drafts and private evidence.
Canonical source is `MuyuanMS/powertoys-pulse-actions` `main`, root `data/`.
Pulse is the user-facing dashboard, not the actions repository's Pages site.

## Persist privately, project publicly

Save full PR state/evidence in the active private ledger and retained receipts.
`Set-PrReviewCheckpoint.ps1` archives a receipt under
`POWERTOYS_PRIVATE_REVIEW_STATE` (default
`%LOCALAPPDATA%\PowerToysPulse\review-state`) and emits neutral pending data.
It is not the full worker request ledger. The coordinator invokes it as needed;
workers never write the shared dashboard checkout.

Public PR data must not contain operational `blockers`, `workflow`,
`next_action`, fork bookkeeping, local paths or private evidence.
Never substitute a request-limit/build/service diagnostic for an author-facing
review. Preserve supported comments/actions when checkpointing unrelated work.

## Generate, sanitize, validate

Publish initial inventory before heavy work, each completed result immediately,
and other substantive transitions at the path's bounded publish interval.
No regeneration/commit on unchanged watchdog ticks.

```powershell
& "$Dashboard\emit.ps1" -AllowStaleReviewQueue
& "$Dashboard\Sanitize-ActionData.ps1"
& "$SkillRoot\scripts\Test-DashboardArtifacts.ps1" `
  -Dashboard $Dashboard -Numbers $ProcessedNumbers `
  -RequireDetailedDesign -RequireIssueContext -RequireFindingGrounding `
  -GroundingPath $PrivateGroundingPath -SourceRepository $PowerToysClone
& "$SkillRoot\scripts\Get-StalePrReviewQueue.ps1" -Dashboard $Dashboard -AsJson
```

`$SkillRoot` is the canonical dashboard-update skill directory.
Validate explicitly processed numbers after sanitization. For issue-only work,
omit PR grounding/source arguments when no PR finding was processed.
Missing evidence is a failure to repair, not permission to fabricate a receipt
or downgrade an unfinished suggestion to prose. Failed unrelated proposals
must not block publication of independently validated results.

Verify `data/index.json`, `data/index.js` and per-number files; every completed
artifact is included in `artifact_numbers`. Preserve unrelated valid items.
Check generated content for secrets/private fields with existing validators
and sanitizer, not a claim based only on a file count.

Only a clean zero-finding PR uses `review_ready`. Findings use a pinned
`post_review`/deliberate `request_changes` and a draft approval stage.
`post_review` is event `COMMENT`; inline-only output has no overall review
body. Allow PR controls only from the existing schema:
`approve`, `post_review`, `request_changes`, `trigger_ci`, `merge_pr`.
Live CI/merge controls retain their immediate confirmation/current-head gates;
the updater never executes them automatically.

## Commit and verify canonical delivery

Run `Assert-CanonicalDashboardTarget.ps1` immediately before writing/committing.
Preserve concurrent remote updates; use non-destructive fast-forward/retry
instead of overwriting another publisher. Stage only this run's intended data
and index files; review staged changes, `git diff --cached --check`, commit and
push explicitly with `git push origin main:main` under existing authorization.
Do not stage skill/code changes as part of an ordinary data update.

Verify the remote commit and published item/head, then acknowledge delivery in
the private ledger. A worker finish, local artifact or push attempt is not
delivery. If publication fails, keep the validated output and retry publication,
not the cloud loop.

Normal runs/intermediate drains use `-AllowStaleReviewQueue`. Final complete
drains refresh the same scope and use `Get-StalePrReviewQueue.ps1 -FailOnStale`
(including `-ExcludeCmdPal` only for that explicit scope).
Exceptions never clear coverage. Do not force a zero queue with timestamps.

## Synchronize Pulse

After actions publication, synchronize the same canonical data into the
authorized Pulse checkout:

```powershell
$env:TRIAGE_DATA_SOURCE_DIR = Join-Path $Dashboard 'data'
node .\scripts\sync-triage-artifacts.mjs
npm run lint
npm run build
```

Use the approved preview/data-sync/deployment path. Official Pulse is
`gim-home/powertoys-pulse`: use the EMU account and a feature PR/merge under
existing approval, never direct-main writes. If an official deploy is already
authorized, dispatch `Sync + Build + Deploy to Pages` after artifacts publish.
Private validation may use the configured authorized preview repository.
Report missing authorization or failed sync separately from canonical artifact
delivery; never claim the user-facing dashboard is updated without verifying
the new item actions there.

Outlook/project synchronization is covered in `scheduled-updates.md` and does
not gate this pipeline. Upstream comments/reviews are a separate approval flow:
`Test-ReviewData.ps1` then `Publish-ApprovedReview.ps1`, never ad-hoc posting.
