[CmdletBinding()]
param(
  [string]$UpstreamRepository = 'microsoft/PowerToys',
  [string]$OutputPath = (Join-Path $PSScriptRoot 'automation-output\dashboard-inventory.json')
)

$ErrorActionPreference = 'Stop'
$dashboard = $PSScriptRoot
$skillRoot = Join-Path $dashboard '.github\skills\powertoys-dashboard-update'

& (Join-Path $skillRoot 'scripts\Assert-CanonicalDashboardTarget.ps1') `
  -Dashboard $dashboard | Out-Null

function Invoke-GhJson {
  param([string[]]$Arguments)

  $raw = & gh @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "GitHub API command failed: gh $($Arguments -join ' ')"
  }
  return ($raw -join "`n") | ConvertFrom-Json
}

$openPrPages = Invoke-GhJson -Arguments @(
  'api',
  '--paginate',
  '--slurp',
  "repos/$UpstreamRepository/pulls?state=open&per_page=100"
)
$openPrs = @($openPrPages | ForEach-Object { @($_) })
$openIssues = Invoke-GhJson -Arguments @(
  'api',
  '--method', 'GET',
  'search/issues',
  '-f', "q=repo:$UpstreamRepository is:issue is:open",
  '-F', 'per_page=1'
)
$since = (Get-Date).ToUniversalTime().AddDays(-1).ToString('yyyy-MM-dd')
$recentIssues = Invoke-GhJson -Arguments @(
  'api',
  '--method', 'GET',
  'search/issues',
  '-f', "q=repo:$UpstreamRepository is:issue updated:>=$since",
  '-F', 'per_page=1'
)
$recentPrs = Invoke-GhJson -Arguments @(
  'api',
  '--method', 'GET',
  'search/issues',
  '-f', "q=repo:$UpstreamRepository is:pr updated:>=$since",
  '-F', 'per_page=1'
)

$index = Get-Content (Join-Path $dashboard 'data\index.json') -Raw |
  ConvertFrom-Json
$inventory = [ordered]@{
  generated_at = (Get-Date).ToUniversalTime().ToString('o')
  upstream = $UpstreamRepository
  live = [ordered]@{
    open_prs = $openPrs.Count
    draft_prs = @($openPrs | Where-Object { $_.draft }).Count
    open_issues = [int]$openIssues.total_count
    issues_updated_last_day = [int]$recentIssues.total_count
    prs_updated_last_day = [int]$recentPrs.total_count
  }
  published = [ordered]@{
    generated_at = $index.generated_at
    items = @($index.items).Count
    artifacts = @($index.artifact_numbers).Count
    open_prs = [int]$index.counts.open_prs
    indexed_issues = [int]$index.counts.open_issues
  }
  safety = [ordered]@{
    upstream_write_enabled = $false
    mirror_write_enabled = $false
    publication_attempted = $false
  }
}

$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$inventory | ConvertTo-Json -Depth 8 |
  Set-Content -Path $OutputPath -Encoding utf8NoBOM

if ($env:GITHUB_STEP_SUMMARY) {
  @"
## PowerToys dashboard inventory

| Signal | Count |
| --- | ---: |
| Live open PRs | $($inventory.live.open_prs) |
| Live draft PRs | $($inventory.live.draft_prs) |
| Live open issues | $($inventory.live.open_issues) |
| Issues updated in the last day | $($inventory.live.issues_updated_last_day) |
| PRs updated in the last day | $($inventory.live.prs_updated_last_day) |
| Published dashboard items | $($inventory.published.items) |
| Published action artifacts | $($inventory.published.artifacts) |

Read-only inventory completed. No upstream or mirror write was attempted.
"@ | Add-Content -Path $env:GITHUB_STEP_SUMMARY
}

[pscustomobject]$inventory
