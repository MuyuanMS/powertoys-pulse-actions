param(
  [string]$DataPath = (Join-Path $PSScriptRoot 'data')
)

$ErrorActionPreference = 'Stop'
$downgradedPrs = [System.Collections.Generic.HashSet[int]]::new()
$dashboardRoot = Split-Path -Parent (Resolve-Path $DataPath).Path
if (Test-Path (Join-Path $dashboardRoot '.git')) {
  & (Join-Path $dashboardRoot '.github\skills\powertoys-dashboard-update\scripts\Assert-CanonicalDashboardTarget.ps1') `
    -Dashboard $dashboardRoot | Out-Null
}
$blockedProperties = @(
  'internal_evidence',
  'internalEvidence',
  'worktree',
  'local_worktree_branch',
  'worktree_head',
  'worktree_clean',
  'evidenceDirectory'
)

function Convert-PublicValue {
  param($Value)

  if ($null -eq $Value) {
    return $null
  }

  if ($Value -is [string]) {
    return $Value `
      -replace 'C:\\PowerToys-[^\\]+\\', '<PowerToysCheckout>\' `
      -replace 'C:\\PowerToys-[^\s"''\\]+', '<PowerToysCheckout>' `
      -replace 'C:\\\\PowerToys-[^\\]+\\\\', '<PowerToysCheckout>\\' `
      -replace 'C:\\PowerToys\\', '<PowerToysCheckout>\' `
      -replace 'C:\\Users\\muyuanli\\\.copilot\\[^"''\r\n]*', '<local-artifact>' `
      -replace 'C:\\powertoys-triage-board-source\\[^"''\r\n]*', '<local-artifact>' `
      -replace '(?i)\bworktree\b', 'local checkout'
  }

  if ($Value -is [System.Collections.IDictionary]) {
    $result = [ordered]@{}
    foreach ($key in $Value.Keys) {
      if ($blockedProperties -contains [string]$key) {
        continue
      }
      $result[$key] = Convert-PublicValue $Value[$key]
    }
    return [pscustomobject]$result
  }

  if ($Value -is [pscustomobject]) {
    $result = [ordered]@{}
    foreach ($property in $Value.PSObject.Properties) {
      if ($blockedProperties -contains $property.Name) {
        continue
      }
      $result[$property.Name] = Convert-PublicValue $property.Value
    }
    return [pscustomobject]$result
  }

  if ($Value -is [System.Collections.IEnumerable]) {
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $Value) {
      $items.Add((Convert-PublicValue $item))
    }
    return ,$items.ToArray()
  }

  return $Value
}

function Get-PublicActions {
  param($Artifact)

  $kind = [string]$Artifact.kind
  $allowedTypes = if ($kind -eq 'pr') {
    @('approve', 'post_review', 'request_changes', 'trigger_ci', 'merge_pr')
  } elseif ($kind -eq 'issue') {
    @('request_info', 'approve_design', 'open_upstream_pr', 'post_comment', 'reproduce')
  } else {
    @()
  }

  $actions = @($Artifact.actions | Where-Object {
    $_ -and
    $_.type -in $allowedTypes -and
    $_.type -ne 'hold' -and
    $_.label -notmatch '(?i)^not now$'
  })

  if ($kind -eq 'pr') {
    foreach ($action in $actions | Where-Object { $_.type -eq 'trigger_ci' }) {
      if (-not $action.comment -or [string]$action.comment.body -ne '/azp run') {
        throw "PR $($Artifact.number) trigger_ci action must post exactly /azp run"
      }
    }
    $proposedComments = @($Artifact.proposed_comments | Where-Object {
      $_.disposition -eq 'proposed'
    })
    $inlineComments = @($proposedComments | Where-Object {
      $_.kind -eq 'inline' -or
      $_.in_diff -eq $true
    })
    $reviewActions = @($actions | Where-Object {
      $_.type -in @('post_review', 'request_changes')
    })
    $invalidReviewReasons = [System.Collections.Generic.List[string]]::new()
    if ($Artifact.stage -eq 'review_ready' -and
        ($proposedComments.Count -gt 0 -or $reviewActions.Count -gt 0)) {
      $invalidReviewReasons.Add("PR $($Artifact.number) stage review_ready cannot contain proposed comments or review actions")
    }
    foreach ($comment in $proposedComments) {
      $id = if ($comment.id) { [string]$comment.id } else { '<missing-id>' }
      if ($comment.kind -notin @('inline', 'companion')) {
        $invalidReviewReasons.Add("PR $($Artifact.number) comment $id must declare kind inline or companion")
        continue
      }
      if (($comment.kind -eq 'inline') -ne ($comment.in_diff -eq $true)) {
        $invalidReviewReasons.Add("PR $($Artifact.number) comment $id has inconsistent kind/in_diff metadata")
      }
      if ($comment.kind -eq 'companion') {
        if ([string]::IsNullOrWhiteSpace([string]$comment.out_of_diff_reason)) {
          $invalidReviewReasons.Add("PR $($Artifact.number) companion comment $id is missing out_of_diff_reason")
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$comment.path) -or
            -not [string]::IsNullOrWhiteSpace([string]$comment.line) -or
            -not [string]::IsNullOrWhiteSpace([string]$comment.start_line) -or
            [string]$comment.body -match '(?i)```suggestion') {
          $invalidReviewReasons.Add("PR $($Artifact.number) companion comment $id contains inline location or suggestion data")
        }
      }
    }
    $validInlineSuggestions = @($inlineComments | Where-Object {
      ([regex]::Matches([string]$_.body, '(?s)```suggestion\s*\r?\n.+?\r?\n```')).Count -eq 1
    })
    foreach ($action in $reviewActions) {
      if ("$($action.label) $($action.note)" -match '(?i)inline suggestion' -and
          $validInlineSuggestions.Count -eq 0) {
        $invalidReviewReasons.Add("PR $($Artifact.number) action '$($action.type)' claims inline suggestions but none are valid")
      }
    }
    if ($invalidReviewReasons.Count -gt 0) {
      foreach ($reason in $invalidReviewReasons) {
        Write-Warning $reason
      }
      $actions = @($actions | Where-Object {
        $_.type -notin @('post_review', 'request_changes')
      })
      $Artifact.stage = 'review_in_progress'
      if ($Artifact.PSObject.Properties['needs_revalidation']) {
        $Artifact.needs_revalidation = $true
      } else {
        $Artifact | Add-Member -NotePropertyName needs_revalidation -NotePropertyValue $true
      }
      [void]$downgradedPrs.Add([int]$Artifact.number)
    }
    foreach ($action in $actions | Where-Object { $_.type -eq 'post_review' }) {
      if ($action.review) {
        $action.review.event = 'COMMENT'
        if ($proposedComments.Count -gt 0 -and
            $inlineComments.Count -eq $proposedComments.Count -and
            $action.review.PSObject.Properties['body_prefix']) {
          $action.review.PSObject.Properties.Remove('body_prefix')
        }
      }
    }
  }

  $actions
}

$itemsPath = Join-Path $DataPath 'items'
if (-not (Test-Path $itemsPath)) {
  throw "Action-data items directory not found: $itemsPath"
}

$encoding = [System.Text.UTF8Encoding]::new($false)
$count = 0
foreach ($path in Get-ChildItem $itemsPath -Filter '*.json') {
  $artifact = Get-Content $path.FullName -Raw | ConvertFrom-Json
  if ($artifact.PSObject.Properties.Name -contains 'actions') {
    $artifact.actions = @(Get-PublicActions $artifact)
  }
  $publicArtifact = Convert-PublicValue $artifact
  $json = $publicArtifact | ConvertTo-Json -Depth 30
  [System.IO.File]::WriteAllText($path.FullName, $json, $encoding)
  $count++
}

if ($downgradedPrs.Count -gt 0) {
  $indexPath = Join-Path $DataPath 'index.json'
  if (Test-Path $indexPath) {
    $index = Get-Content $indexPath -Raw | ConvertFrom-Json
    foreach ($row in @($index.items | Where-Object {
      $_.kind -eq 'pr' -and $downgradedPrs.Contains([int]$_.number)
    })) {
      $row.stage = 'review_in_progress'
      if ($row.PSObject.Properties['needs_revalidation']) {
        $row.needs_revalidation = $true
      } else {
        $row | Add-Member -NotePropertyName needs_revalidation -NotePropertyValue $true
      }
      if ($row.PSObject.Properties['primary_action']) {
        $row.primary_action = $null
      }
    }
    $indexJson = $index | ConvertTo-Json -Depth 30
    [System.IO.File]::WriteAllText($indexPath, $indexJson, $encoding)
    $indexJsPath = Join-Path $DataPath 'index.js'
    if (Test-Path $indexJsPath) {
      [System.IO.File]::WriteAllText(
        $indexJsPath,
        "window.BOARD_INDEX = $indexJson;`n",
        $encoding
      )
    }
  }
}

Write-Host "Sanitized $count public action artifact(s)."
