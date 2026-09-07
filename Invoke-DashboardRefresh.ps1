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
