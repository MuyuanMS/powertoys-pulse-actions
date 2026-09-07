[CmdletBinding()]
param(
  [switch]$SkipSkillSuite
)

$ErrorActionPreference = 'Stop'
$dashboard = $PSScriptRoot
$skillRoot = Join-Path $dashboard '.github\skills\powertoys-dashboard-update'

& (Join-Path $skillRoot 'scripts\Assert-CanonicalDashboardTarget.ps1') `
  -Dashboard $dashboard | Out-Null

& (Join-Path $dashboard 'emit.ps1') -AllowStaleReviewQueue
& (Join-Path $dashboard 'Sanitize-ActionData.ps1') `
  -DataPath (Join-Path $dashboard 'data')
$index = Get-Content (Join-Path $dashboard 'data\index.json') -Raw |
  ConvertFrom-Json
$publishedArtifactNumbers = [System.Collections.Generic.HashSet[int]]::new()
foreach ($number in @($index.artifact_numbers)) {
  [void]$publishedArtifactNumbers.Add([int]$number)
}
$changedArtifactNumbers = @(
  & git -C $dashboard status --short -- data/items |
    ForEach-Object {
      $match = [regex]::Match($_, 'data/items/(\d+)\.json$')
      if ($match.Success) {
        $number = [int]$match.Groups[1].Value
        if ($publishedArtifactNumbers.Contains($number)) {
          $number
        }
      }
    } |
    Sort-Object -Unique
)
if ($changedArtifactNumbers.Count -gt 0) {
  & (Join-Path $skillRoot 'scripts\Test-DashboardArtifacts.ps1') `
    -Dashboard $dashboard `
    -Numbers $changedArtifactNumbers `
    -RequireIssueContext
}

if (-not $SkipSkillSuite) {
  & (Join-Path $dashboard 'Test-SkillSuite.ps1')
}

[pscustomobject]@{
  dashboard = $dashboard
  generated_at = (Get-Date).ToUniversalTime().ToString('o')
  changed_files = @(
    & git -C $dashboard status --short -- data
  )
}
