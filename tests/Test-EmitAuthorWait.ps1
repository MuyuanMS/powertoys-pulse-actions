$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Emit.AuthorWait.ps1')

function New-AuthorWaitLiveFixture {
  param(
    [string]$LatestAuthorActivity,
    [string]$HeadSha = 'bbe5c91bb7a5d1abad5f89eaf8c192c9cdaf739c',
    [string[]]$Labels = @(),
    [switch]$BlockingReview
  )
  $reviews = @(
    [pscustomobject]@{
      submittedAt = '2026-09-22T21:04:22Z'
      author = [pscustomobject]@{ login = 'noraa-junker' }
      state = 'APPROVED'
      body = 'LGTM'
    }
  )
  if ($BlockingReview) {
    $reviews += [pscustomobject]@{
      submittedAt = '2026-09-23T01:00:00Z'
      author = [pscustomobject]@{ login = 'maintainer' }
      state = 'CHANGES_REQUESTED'
      body = 'Please address the remaining issue.'
    }
  }
  [pscustomobject]@{
    author = [pscustomobject]@{ login = 'daverayment' }
    labels = @($Labels | ForEach-Object { [pscustomobject]@{ name = $_ } })
    headRefOid = $HeadSha
    commits = @(
      [pscustomobject]@{
        authoredDate = '2026-09-10T17:55:02Z'
        authors = @([pscustomobject]@{ login = 'daverayment' })
      }
    )
    comments = @(
      [pscustomobject]@{
        createdAt = $LatestAuthorActivity
        author = [pscustomobject]@{ login = 'daverayment' }
        body = 'Author follow-up'
      }
    )
    reviews = $reviews
    updatedAt = $LatestAuthorActivity
  }
}

$artifact = Get-Content (Join-Path $PSScriptRoot 'fixtures\50497-author-wait.json') -Raw |
  ConvertFrom-Json

$defaultPending = Get-DefaultPrPendingAuthor $artifact 'pr' 2 'us' $true
if ($defaultPending) {
  throw 'Posted history overrode the artifact explicit pending_author:false value.'
}

$covered = Resolve-PrAuthorWaitStateFromLive `
  (New-AuthorWaitLiveFixture '2026-09-22T20:56:41Z') `
  $artifact $defaultPending $null 2
if ($covered.pending_author -or $covered.resolved_by_author_activity) {
  throw "Covered 50497 author activity incorrectly invalidated the fresh artifact: $($covered | ConvertTo-Json -Compress)"
}
if (@($artifact.proposed_comments | Where-Object disposition -eq 'posted').Count -ne 2 -or
    @($artifact.proposed_comments | Where-Object disposition -eq 'proposed').Count -ne 1 -or
    @($artifact.actions).Count -ne 1) {
  throw 'Author-wait evaluation modified the posted/proposed comment history.'
}

$missingSourceArtifact = $artifact | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$missingSourceArtifact.source_updated_at = $null
$missingSource = Resolve-PrAuthorWaitStateFromLive `
  (New-AuthorWaitLiveFixture '2026-09-22T20:56:41Z') `
  $missingSourceArtifact $false $null 2
if (-not $missingSource.resolved_by_author_activity) {
  throw "Missing source_updated_at incorrectly preserved a current review: $($missingSource | ConvertTo-Json -Compress)"
}

$invalidSourceArtifact = $artifact | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$invalidSourceArtifact.source_updated_at = 'not-a-timestamp'
$invalidSource = Resolve-PrAuthorWaitStateFromLive `
  (New-AuthorWaitLiveFixture '2026-09-22T20:56:41Z') `
  $invalidSourceArtifact $false $null 2
if (-not $invalidSource.resolved_by_author_activity) {
  throw "Invalid source_updated_at incorrectly preserved a current review: $($invalidSource | ConvertTo-Json -Compress)"
}

$changedHead = Resolve-PrAuthorWaitStateFromLive `
  (New-AuthorWaitLiveFixture '2026-09-22T20:00:00Z' -HeadSha 'new-live-head') `
  $artifact $false $null 2
if (-not $changedHead.resolved_by_author_activity) {
  throw "A changed live head with older authored activity incorrectly preserved a current review: $($changedHead | ConvertTo-Json -Compress)"
}

$newActivity = Resolve-PrAuthorWaitStateFromLive `
  (New-AuthorWaitLiveFixture '2026-09-23T00:00:00Z') `
  $artifact $false $null 2
if ($newActivity.pending_author -or -not $newActivity.resolved_by_author_activity) {
  throw "Author activity after source_updated_at did not invalidate the artifact: $($newActivity | ConvertTo-Json -Compress)"
}

$needsAuthorLabel = Resolve-PrAuthorWaitStateFromLive `
  (New-AuthorWaitLiveFixture '2026-09-22T20:56:41Z' -Labels 'Needs-Author-Feedback') `
  $artifact $false $null 2
if (-not $needsAuthorLabel.pending_author -or $needsAuthorLabel.resolved_by_author_activity) {
  throw "An active needs-author label did not override explicit pending_author:false: $($needsAuthorLabel | ConvertTo-Json -Compress)"
}

$blockingReview = Resolve-PrAuthorWaitStateFromLive `
  (New-AuthorWaitLiveFixture '2026-09-22T20:56:41Z' -BlockingReview) `
  $artifact $false $null 2
if (-not $blockingReview.pending_author -or $blockingReview.resolved_by_author_activity) {
  throw "A current blocking review did not override explicit pending_author:false: $($blockingReview | ConvertTo-Json -Compress)"
}

$alreadyRevalidatingArtifact = $invalidSourceArtifact | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$alreadyRevalidatingArtifact | Add-Member -NotePropertyName needs_revalidation -NotePropertyValue $true
$alreadyRevalidatingArtifact.actions = @()
$alreadyRevalidatingArtifact.proposed_comments = @(
  $alreadyRevalidatingArtifact.proposed_comments | Where-Object disposition -eq 'posted'
)
$alreadyRevalidating = Resolve-PrAuthorWaitStateFromLive `
  (New-AuthorWaitLiveFixture '2026-09-22T20:56:41Z' -HeadSha 'new-live-head') `
  $alreadyRevalidatingArtifact $false $null 2
if ($alreadyRevalidating.pending_author -or $alreadyRevalidating.resolved_by_author_activity) {
  throw "An actionless already-revalidated state regressed: $($alreadyRevalidating | ConvertTo-Json -Compress)"
}

$waitingArtifact = [pscustomobject]@{
  head_sha = $artifact.head_sha
  source_updated_at = '2026-09-10T18:20:01Z'
  proposed_comments = $artifact.proposed_comments
}
$stillWaiting = Resolve-PrAuthorWaitStateFromLive `
  (New-AuthorWaitLiveFixture '2026-09-10T18:00:00Z') `
  $waitingArtifact $true '2026-09-10T18:20:00Z' 2
if (-not $stillWaiting.pending_author -or $stillWaiting.resolved_by_author_activity) {
  throw "Unanswered posted review action did not remain pending: $($stillWaiting | ConvertTo-Json -Compress)"
}

Write-Host 'Emitter author-wait regression tests passed.'
