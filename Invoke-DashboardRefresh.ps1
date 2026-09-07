[CmdletBinding()]
param(
  [switch]$SkipSkillSuite
)

$ErrorActionPreference = 'Stop'
$dashboard = $PSScriptRoot
$skillRoot = Join-Path $dashboard '.github\skills\powertoys-dashboard-update'
$baseline = Get-Content (Join-Path $dashboard 'data\index.json') -Raw |
  ConvertFrom-Json

& (Join-Path $skillRoot 'scripts\Assert-CanonicalDashboardTarget.ps1') `
  -Dashboard $dashboard | Out-Null

& (Join-Path $dashboard 'emit.ps1') -AllowStaleReviewQueue
& (Join-Path $dashboard 'Sanitize-ActionData.ps1') `
  -DataPath (Join-Path $dashboard 'data')
$generated = Get-Content (Join-Path $dashboard 'data\index.json') -Raw |
  ConvertFrom-Json

$minimumItems = [Math]::Floor(@($baseline.items).Count * 0.8)
$minimumPrs = [Math]::Floor([int]$baseline.counts.open_prs * 0.8)
if (@($generated.items).Count -lt $minimumItems -or
    [int]$generated.counts.open_prs -lt $minimumPrs) {
  throw "Refusing to publish an incomplete inventory. Baseline items/PRs: $(@($baseline.items).Count)/$($baseline.counts.open_prs); generated: $(@($generated.items).Count)/$($generated.counts.open_prs)."
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
