function ConvertTo-DateTimeOrNull {
  param($Value)
  if (-not $Value) { return $null }
  if ($Value -is [datetimeoffset]) {
    return $Value.UtcDateTime
  }
  if ($Value -is [datetime]) {
    return $Value.ToUniversalTime()
  }
  $parsed = [datetime]::MinValue
  if ([datetime]::TryParse(
      [string]$Value,
      [System.Globalization.CultureInfo]::InvariantCulture,
      [System.Globalization.DateTimeStyles]::AdjustToUniversal,
      [ref]$parsed)) {
    return $parsed
  }
  return $null
}

function Get-MaxDateTime {
  param([object[]]$Values)
  $dates = @($Values | ForEach-Object { ConvertTo-DateTimeOrNull $_ } | Where-Object { $null -ne $_ })
  if ($dates.Count -eq 0) { return $null }
  return @($dates | Sort-Object -Descending | Select-Object -First 1)[0]
}

function Test-AfterDate {
  param($Candidate, $Anchor)
  $candidateDate = ConvertTo-DateTimeOrNull $Candidate
  $anchorDate = ConvertTo-DateTimeOrNull $Anchor
  return $candidateDate -and $anchorDate -and $candidateDate -gt $anchorDate
}

function Get-FirstDateValue {
  param([object[]]$Values)
  $dates = @($Values | ForEach-Object { ConvertTo-DateTimeOrNull $_ } | Where-Object { $null -ne $_ })
  if ($dates.Count -eq 0) { return $null }
  return @($dates | Sort-Object | Select-Object -First 1)[0].ToString('o')
}

function Get-DefaultPrPendingAuthor {
  param(
    [object]$Artifact,
    [string]$ItemKind,
    [int]$PostedComments,
    [string]$Owes,
    [bool]$HasDraftReviewAction
  )

  if ($Artifact -and $Artifact.psobject.Properties['pending_author']) {
    return [bool]$Artifact.pending_author
  }

  return (
    ($ItemKind -eq 'pr' -and $PostedComments -gt 0) -or
    (($Owes -eq 'author') -and -not $HasDraftReviewAction)
  )
}

function Test-CurrentPrReviewCoverage {
  param(
    [object]$Live,
    [object]$Artifact
  )

  if (-not $Artifact -or [bool]$Artifact.needs_revalidation) {
    return $false
  }

  $coveredSourceAt = ConvertTo-DateTimeOrNull $Artifact.source_updated_at
  if (-not $coveredSourceAt) {
    return $false
  }

  $artifactHead = [string]$Artifact.head_sha
  $liveHead = [string]$Live.headRefOid
  return (
    -not [string]::IsNullOrWhiteSpace($artifactHead) -and
    -not [string]::IsNullOrWhiteSpace($liveHead) -and
    [string]::Equals($artifactHead, $liveHead, [System.StringComparison]::OrdinalIgnoreCase)
  )
}

function Resolve-PrAuthorWaitStateFromLive {
  param(
    [object]$Live,
    [object]$Artifact,
    [bool]$DefaultPendingAuthor,
    [string]$DefaultWaitingSince,
    [int]$PostedComments
  )

  $decision = [pscustomobject]@{
    pending_author = $DefaultPendingAuthor
    waiting_since = if ($DefaultPendingAuthor) { $DefaultWaitingSince } else { $null }
    resolved_by_author_activity = $false
    reason = 'preserved from existing artifact'
  }

  $author = [string]$Live.author.login
  $labels = @($Live.labels | ForEach-Object { [string]$_.name })
  $hasNeedsAuthorLabel = @($labels | Where-Object { $_ -match '(?i)needs[- ]author[- ]feedback|author[- ]feedback|waiting[- ]for[- ]author' }).Count -gt 0
  $authorActivity = @()
  $authorActivity += @($Live.commits | Where-Object { @($_.authors | ForEach-Object { $_.login }) -contains $author } | ForEach-Object { $_.authoredDate })
  $authorActivity += @($Live.comments | Where-Object { $_.author.login -eq $author } | ForEach-Object { $_.createdAt })
  $authorActivity += @($Live.reviews | Where-Object { $_.author.login -eq $author } | ForEach-Object { $_.submittedAt })
  $latestAuthorActivity = Get-MaxDateTime $authorActivity

  $latestBlockingReview = Get-MaxDateTime @(
    $Live.reviews |
      Where-Object { $_.author.login -ne $author -and $_.state -eq 'CHANGES_REQUESTED' } |
      ForEach-Object { $_.submittedAt }
  )
  $latestPulseReview = Get-MaxDateTime @(
    $Live.reviews |
      Where-Object { $_.author.login -ne $author -and [string]$_.body -match 'powertoys-pulse:' } |
      ForEach-Object { $_.submittedAt }
  )
  $latestPulseComment = Get-MaxDateTime @(
    $Live.comments |
      Where-Object { $_.author.login -ne $author -and [string]$_.body -match 'powertoys-pulse:' } |
      ForEach-Object { $_.createdAt }
  )
  $latestPulseAction = Get-MaxDateTime @($latestPulseReview, $latestPulseComment)
  $recordedWaitAt = if ($Artifact.author_wait_evidence) {
    ConvertTo-DateTimeOrNull $Artifact.author_wait_evidence.created_at
  } else {
    $null
  }

  if ($hasNeedsAuthorLabel) {
    $decision.pending_author = $true
    $decision.waiting_since = if ($DefaultWaitingSince) { $DefaultWaitingSince } else { [string]$Live.updatedAt }
    $decision.reason = 'needs-author-feedback label is present'
  } elseif ($latestBlockingReview -and (-not $latestAuthorActivity -or $latestBlockingReview -gt $latestAuthorActivity)) {
    $decision.pending_author = $true
    $decision.waiting_since = $latestBlockingReview.ToString('o')
    $decision.reason = 'latest current changes-requested review is after author activity'
  } elseif ($latestPulseAction -and (-not $latestAuthorActivity -or $latestPulseAction -gt $latestAuthorActivity)) {
    $decision.pending_author = $true
    $decision.waiting_since = $latestPulseAction.ToString('o')
    $decision.reason = 'posted Pulse review action is after author activity'
  } elseif ($recordedWaitAt -and (-not $latestAuthorActivity -or $recordedWaitAt -gt $latestAuthorActivity)) {
    $decision.pending_author = $true
    $decision.waiting_since = $recordedWaitAt.ToString('o')
    $decision.reason = 'recorded upstream author request is after author activity'
  } elseif ($PostedComments -gt 0) {
    $postedAt = Get-FirstDateValue @(
      $Artifact.proposed_comments |
        Where-Object { $_.disposition -eq 'posted' -and $_.posted_at } |
        ForEach-Object { $_.posted_at }
    )
    $anchor = if ($postedAt) { $postedAt } elseif ($DefaultWaitingSince) { $DefaultWaitingSince } else { [string]$Artifact.source_updated_at }
    $coveredSourceAt = ConvertTo-DateTimeOrNull $Artifact.source_updated_at
    $coverageIsCurrent = Test-CurrentPrReviewCoverage $Live $Artifact
    if ([bool]$Artifact.needs_revalidation -and -not $DefaultPendingAuthor) {
      $decision.pending_author = $false
      $decision.waiting_since = $null
      $decision.reason = 'artifact is already marked for revalidation'
    } elseif (-not $DefaultPendingAuthor -and -not $coverageIsCurrent) {
      $decision.pending_author = $false
      $decision.waiting_since = $null
      $decision.resolved_by_author_activity = $true
      $decision.reason = 'artifact review coverage is missing, stale, or not pinned to the live head'
    } elseif ($latestAuthorActivity -and $coveredSourceAt -and $latestAuthorActivity -gt $coveredSourceAt) {
      $decision.pending_author = $false
      $decision.waiting_since = $null
      $decision.resolved_by_author_activity = $true
      $decision.reason = 'author activity occurred after the artifact source activity'
    } elseif (-not $DefaultPendingAuthor) {
      $decision.pending_author = $false
      $decision.waiting_since = $null
      $decision.reason = 'posted history and author activity are covered by the current artifact'
    } elseif ($latestAuthorActivity -and (Test-AfterDate $latestAuthorActivity $anchor)) {
      $decision.pending_author = $false
      $decision.waiting_since = $null
      $decision.resolved_by_author_activity = $true
      $decision.reason = 'author activity occurred after the posted Pulse review action'
    } else {
      $decision.pending_author = $true
      $decision.waiting_since = $anchor
      $decision.reason = 'posted Pulse review action has no newer author activity'
    }
  } else {
    $decision.pending_author = $false
    $decision.waiting_since = $null
    $decision.resolved_by_author_activity = $true
    $decision.reason = 'no current needs-author label, changes-requested review, or posted Pulse action remains after author activity'
  }

  return $decision
}
