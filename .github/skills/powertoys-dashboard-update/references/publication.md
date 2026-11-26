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

Review actions must request an actionable author change. Do not publish standalone
"qualified assessment", "validation scope" or "no additional change requested"
reviews as suggested actions. A standalone general finding must use the existing
implementation-ready sections (Affected code, Problem, Why it matters, Suggested
change, Verification). An incomplete review has neutral pending metadata and no
posting/approval/merge suggestion; retain its assessment and recovery notes privately.
The sanitizer and direct publication validator share this content gate, including
instructions disguised inside proposed comments or review bodies. Never bypass it
by putting operator prose into `actions.review.body_prefix`.

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

### Scoped offline PR publication

Use `Publish-ScopedActionData.ps1` instead of full `emit.ps1` when independently
validated PR results must publish without enumerating unrelated item files or
fetching upstream metadata. This is a supported generator, not permission to
hand-edit the generated index. It requires PowerShell 7.5+ (JSON timestamps must
remain strings to preserve unrelated row content) and the same canonical `main`
guard. It supports existing non-CmdPal PR index rows only; new inventory rows and
issue generation remain the full generator's responsibility.

Inputs:

- `-Numbers`: the explicit authorized, processed selection. No implicit discovery.
- `-CandidateDataPath`: a separate directory with `items/<number>.json`. Supply
  already-sanitized public PR review artifacts, including neutral corrections.
  The publisher validates, sanitizes, and validates an isolated selected-only
  copy; any sanitizer content change invalidates its receipt rather than silently
  stripping findings or manufacturing review completion.
- `-LiveMetadataPath`: a coordinator-supplied JSON array containing selected
  `number`, `headRefOid`, `title`, `updatedAt`, `isDraft`, and `labels` (objects
  with `name`). Export it from the eligible open-PR discovery. If supplied,
  `state` must be open. Drafts, CmdPal labels and non-matching heads fail closed.
  Selected display metadata is pinned to this snapshot; no upstream calls occur.
- `-ValidationReceiptPath`: a private integrity envelope for previously successful
  strict validation. For new validations, generate it with
  `Test-DashboardArtifacts.ps1 -Numbers ... -RequireFindingGrounding
  -GroundingPath ... -SourceRepository ... -OutputReceiptPath ...` **after**
  sanitizing the separate candidates. The existing exact-head, literal-suggestion,
  public-content and grounding gates remain unchanged. Do not reread restricted
  source to obtain a receipt; retain an unavailable result privately instead.

For unchanged retained candidates, the coordinator may supply the same envelope
only after verifying the saved successful strict validation, exact-tree/literal
patch and finding-grounding evidence for the pinned head and final bodies. The
envelope binds that verified result to the public artifact, not new source
evidence. Never infer a pass from a candidate filename, hash alone, or worker
completion. Private envelope schema (do not put it in `data/`):

```json
{
  "schemaVersion": 1,
  "validator": "Test-DashboardArtifacts.ps1",
  "finding_grounding_required": true,
  "artifacts": [
    { "number": 49276, "kind": "pr", "head_sha": "<full pinned head>",
      "content_sha256": "<verified public artifact digest>" }
  ]
}
```

The coordinator's retained-result readiness array is also supported directly:
each selected entry must have `number`, `head_sha`, `source_updated_at`,
`status: "ready_for_publication"`, a nonempty `validation` attestation covering
the preserved evidence verification, and `candidate_sha256` (SHA256 of the exact
supplied candidate file bytes). `candidate_path` is ignored; only the explicitly
supplied candidate directory is read. This form accommodates already verified
historical receipts without rerunning source checks, rebuilding, or rewriting
their evidence. As with the envelope, the coordinator must verify the underlying
receipts; a readiness label or byte hash alone is not substantive validation.

Compute `content_sha256` using `Get-ArtifactContentDigest` from
`scripts/ArtifactValidationReceipt.ps1`, parsing candidates with
`ConvertFrom-Json -DateKind String`. Object-key ordering is canonicalized; array
order and every JSON value remain significant. This integrity envelope does not
replace the coordinator's substantive evidence verification or posting approval.

```powershell
& "$Dashboard\Publish-ScopedActionData.ps1" -Numbers $ProcessedNumbers `
  -CandidateDataPath $ValidatedCandidateData `
  -LiveMetadataPath $PinnedEligibleMetadata `
  -ValidationReceiptPath $VerifiedPublicationReceipt
```

Only selected per-number artifacts and their index rows are projected. Unselected
item bytes are never read or written; unselected row content and manifest order
are preserved. Both indexes are generated together, selected numbers enter
`artifact_numbers`, and its count is updated. Inventory timestamps, other counts
and historical impact remain unchanged: this is not a live inventory/impact
refresh or a complete-drain claim. Baseline JSON/JS disagreement, missing rows,
receipt mismatch or any failed gate leaves the target untouched. Repeated
generation is deterministic. Hold exclusive publication ownership; the generator
also detects changed index/selected-file baselines and serializes scoped writers.
Use `-ValidateOnly` for a read-only preflight through all selected gates.

Do not run the full sanitizer or a whole-feed validator afterward in a restricted
scope: those enumerate unrelated files. The scoped command already runs both
public gates for the selected artifacts. Continue the canonical commit and Pulse
delivery steps below, staging only the returned `changed_files`.

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
