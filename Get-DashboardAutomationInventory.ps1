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

function Get-PublicSearchCount {
  param([string]$Query)

  $uri = "https://api.github.com/search/issues?q=$([uri]::EscapeDataString($Query))&per_page=1"
  $response = Invoke-RestMethod -Uri $uri -Headers @{
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'powertoys-pulse-actions'
  }
  if ($response.incomplete_results) {
    throw "GitHub returned an incomplete search result for: $Query"
  }
  return [int]$response.total_count
}

$since = (Get-Date).ToUniversalTime().AddDays(-1).ToString('yyyy-MM-dd')
$openPrCount = Get-PublicSearchCount "repo:$UpstreamRepository is:pr is:open"
$draftPrCount = Get-PublicSearchCount "repo:$UpstreamRepository is:pr is:open draft:true"
$openIssueCount = Get-PublicSearchCount "repo:$UpstreamRepository is:issue is:open"
$recentIssueCount = Get-PublicSearchCount "repo:$UpstreamRepository is:issue updated:>=$since"
$recentPrCount = Get-PublicSearchCount "repo:$UpstreamRepository is:pr updated:>=$since"

$index = Get-Content (Join-Path $dashboard 'data\index.json') -Raw |
  ConvertFrom-Json
$inventory = [ordered]@{
  generated_at = (Get-Date).ToUniversalTime().ToString('o')
  upstream = $UpstreamRepository
  live = [ordered]@{
    open_prs = $openPrCount
    draft_prs = $draftPrCount
    open_issues = $openIssueCount
    issues_updated_last_day = $recentIssueCount
    prs_updated_last_day = $recentPrCount
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
