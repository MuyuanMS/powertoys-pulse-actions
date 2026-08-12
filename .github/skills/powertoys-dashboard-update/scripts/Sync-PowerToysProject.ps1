param(
  [switch]$DryRun,
  [string]$Upstream = 'microsoft/PowerToys',
  [string]$Owner = 'microsoft',
  [int]$ProjectNumber = 2445,
  [int]$Limit = 200,
  [string]$Dashboard = $(if ($env:POWERTOYS_DASHBOARD_PATH) {
    $env:POWERTOYS_DASHBOARD_PATH
  } else {
    Join-Path $PSScriptRoot '..\..\..\..'
  }),
  [string[]]$RecognizedReviewers = $(if ($env:POWERTOYS_RECOGNIZED_REVIEWERS) {
    @($env:POWERTOYS_RECOGNIZED_REVIEWERS -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  } else {
    @('MuyuanMS', 'LegendaryBlair', 'moooyu')
  })
)

$ErrorActionPreference = 'Stop'
$Dashboard = (Resolve-Path $Dashboard).Path

function Assert-ProjectAccess {
  try {
    & gh project field-list "$ProjectNumber" --owner $Owner --format json 1>$null 2>$null
    if ($LASTEXITCODE -ne 0) { throw "GitHub project access was denied." }
  } catch {
    throw "Cannot access project $Owner/$ProjectNumber. Grant the GitHub token project read/write permission (for classic tokens: project; for fine-grained tokens: organization Projects read/write), then retry. $($_.Exception.Message)"
  }
}

function Invoke-GhJson {
  param([string[]]$Arguments)
  $raw = & gh @Arguments
  if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments -join ' ') failed." }
  return ($raw -join "`n" | ConvertFrom-Json)
}

function Invoke-GhMutation {
  param([string[]]$Arguments)
  if ($DryRun) {
    Write-Host "[dry-run] gh $($Arguments -join ' ')"
    return $null
  }
  return Invoke-GhJson $Arguments
}

function Get-ProjectItems {
  $result = Invoke-GhJson @('project', 'item-list', "$ProjectNumber", '--owner', $Owner,
    '--format', 'json', '--limit', "$Limit")
  return @($result.items)
}

function Get-ProjectStatusField {
  $result = Invoke-GhJson @('project', 'field-list', "$ProjectNumber", '--owner', $Owner,
    '--format', 'json')
  $field = @($result.fields | Where-Object { $_.name -eq 'Status' }) | Select-Object -First 1
  if (-not $field) { throw "Project $Owner/$ProjectNumber has no Status field." }
  $field | Add-Member -NotePropertyName project_id -NotePropertyValue $field.projectId -Force
  return $field
}

function Get-StatusOption {
  param($Field, [string]$Name)
  $option = @($Field.options | Where-Object { $_.name -ieq $Name }) | Select-Object -First 1
  if (-not $option) {
    $option = @($Field.options | Where-Object { $_.name -ilike "$Name*" }) | Select-Object -First 1
  }
  return $option
}

function Get-ItemNumber {
  param($Item)
  if ($Item.content.number) { return [int]$Item.content.number }
  if ($Item.content.url -match '/(issues|pull)/(\d+)$') { return [int]$Matches[2] }
  return $null
}

function Get-IsCmdPal {
  param($Item)
  $labels = @($Item.labels | ForEach-Object { $_.name })
  $text = "$($Item.title) $($labels -join ' ')"
  return $text -match '(?i)(CmdPal|Command Palette|PowerToys\.CommandPalette|CommandPalette)'
}

function Get-LivePullRequest {
  param([int]$Number)
  return Invoke-GhJson @('pr', 'view', "$Number", '-R', $Upstream,
    '--json', 'number,state,isDraft,title,labels,reviews,comments,mergedAt,updatedAt')
}

function Get-LiveIssue {
  param([int]$Number)
  return Invoke-GhJson @('issue', 'view', "$Number", '-R', $Upstream,
    '--json', 'number,state,title,labels,comments,updatedAt')
}

function Get-ReviewerLogin {
  param($Live, [datetime]$Since)
  $known = @{}
  foreach ($reviewer in $RecognizedReviewers) {
    if (-not [string]::IsNullOrWhiteSpace($reviewer)) {
      $known[$reviewer.ToLowerInvariant()] = $reviewer
    }
  }
  $events = @()
  foreach ($review in @($Live.reviews)) {
    if ($review.author.login) {
      $events += [pscustomobject]@{ login = [string]$review.author.login; at = [datetime]$review.submittedAt }
    }
  }
  foreach ($comment in @($Live.comments)) {
    if ($comment.author.login) {
      $events += [pscustomobject]@{ login = [string]$comment.author.login; at = [datetime]$comment.createdAt }
    }
  }
  foreach ($event in ($events | Sort-Object at -Descending)) {
    if ($Since -and $event.at -lt $Since) { continue }
    $key = $event.login.ToLowerInvariant()
    if ($known.ContainsKey($key)) { return $known[$key] }
  }
  return $null
}

function Set-ProjectStatus {
  param($Item, $Field, [string]$Status)
  $option = Get-StatusOption $Field $Status
  if (-not $option) {
    Write-Warning "Status option '$Status' is not present; leaving item $($Item.id) unchanged."
    return
  }
  $current = if ($Item.status) { $Item.status.name } elseif ($Item.fieldValues.Status) { $Item.fieldValues.Status.name } else { $null }
  if ($current -and $current -ieq $option.name) { return }
  Invoke-GhMutation @('project', 'item-edit', '--id', "$($Item.id)", '--project-id',
    "$($Field.project_id)", '--field-id', "$($Field.id)", '--single-select-option-id',
    "$($option.id)") | Out-Null
  Write-Host "item $($Item.id) -> $($option.name)"
}

Assert-ProjectAccess
$field = Get-ProjectStatusField
$items = Get-ProjectItems

$artifactByNumber = @{}
$indexPath = Join-Path $Dashboard 'data\index.json'
if (Test-Path $indexPath) {
  $index = Get-Content $indexPath -Raw | ConvertFrom-Json
  foreach ($row in @($index.items)) {
    $artifactPath = Join-Path $Dashboard "data\items\$($row.number).json"
    if (Test-Path $artifactPath) {
      $artifactByNumber[[int]$row.number] = Get-Content $artifactPath -Raw | ConvertFrom-Json
    }
  }
}

$statusNames = @('To triage', 'To manually review', 'In Review', 'Done')
$statusOptions = @{}
foreach ($name in $statusNames) {
  $option = Get-StatusOption $field $name
  if ($option) { $statusOptions[$name] = $option.name }
}

# Add eligible open, non-draft, non-CmdPal PRs that are not already tracked.
$trackedNumbers = @($items | ForEach-Object { Get-ItemNumber $_ } | Where-Object { $_ })
$openPrs = @(Invoke-GhJson @('pr', 'list', '-R', $Upstream, '--state', 'open',
  '--json', 'number,title,isDraft,labels,url', '--limit', "$Limit"))
foreach ($pr in $openPrs) {
  if ($pr.isDraft -or ($trackedNumbers -contains [int]$pr.number)) { continue }
  if ((Get-IsCmdPal $pr)) { continue }
  $url = "https://github.com/$Upstream/pull/$($pr.number)"
  Invoke-GhMutation @('project', 'item-add', "$ProjectNumber", '--owner', $Owner, '--url', $url) | Out-Null
  Write-Host "added PR #$($pr.number)"
}

foreach ($item in $items) {
  $number = Get-ItemNumber $item
  if (-not $number) { continue }
  $isPull = [string]$item.content.type -eq 'PullRequest' -or [string]$item.content.url -match '/pull/'
  try {
    $live = if ($isPull) { Get-LivePullRequest $number } else { Get-LiveIssue $number }
  } catch {
    Write-Warning "Unable to read upstream item ${number}: $($_.Exception.Message)"
    continue
  }

  if ($live.state -eq 'CLOSED' -or $live.mergedAt) {
    Set-ProjectStatus $item $field 'Done'
    continue
  }

  $artifact = $artifactByNumber[$number]
  $artifactSince = $null
  if ($artifact.generated_at) {
    try { $artifactSince = [datetime]$artifact.generated_at } catch { $artifactSince = $null }
  }
  $reviewer = Get-ReviewerLogin $live $artifactSince
  if ($reviewer) {
    $named = "In Review: $reviewer"
    if (Get-StatusOption $field $named) {
      Set-ProjectStatus $item $field $named
    } else {
      Set-ProjectStatus $item $field 'In Review'
    }
    continue
  }

  if ($isPull -and $artifact -and $artifact.track -eq 'review' -and
      -not $artifact.pending_author -and
      ($artifact.proposed_comments -ne $null -or
       [string]$artifact.stage -match '(?i)review_ready|awaiting_review')) {
    Set-ProjectStatus $item $field 'To manually review'
    continue
  }
}

Write-Host "Project synchronization complete. dry_run=$DryRun tracked=$($items.Count)"
