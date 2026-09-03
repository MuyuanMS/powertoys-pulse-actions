param(
  [string]$DataPath = (Join-Path $PSScriptRoot 'data')
)

$ErrorActionPreference = 'Stop'
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
      ($null -eq $_.kind -and $_.in_diff -eq $true)
    })
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
  if ($artifact.PSObject.Properties['actions']) {
    $artifact.actions = @(Get-PublicActions $artifact)
  }
  $publicArtifact = Convert-PublicValue $artifact
  $json = $publicArtifact | ConvertTo-Json -Depth 30
  [System.IO.File]::WriteAllText($path.FullName, $json, $encoding)
  $count++
}

Write-Host "Sanitized $count public action artifact(s)."
