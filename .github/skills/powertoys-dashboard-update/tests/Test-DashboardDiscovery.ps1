$ErrorActionPreference = 'Stop'
$candidateScript = Join-Path $PSScriptRoot '..\scripts\Get-DashboardUpdateCandidates.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) "pulse-discovery-$([guid]::NewGuid().ToString('N'))"
New-Item (Join-Path $fixture 'data\items') -ItemType Directory -Force | Out-Null
$global:PulseDiscoveryTestCalls = 0
$global:PulseDiscoveryTestResponse = ''
$global:PulseDiscoveryTestPrResponse = ''

function gh {
    if ($args[0] -eq 'pr') {
        $limitIndex = [array]::IndexOf($args, '--limit')
        if ($args[1] -ne 'list' -or $limitIndex -lt 0 -or
            $args[$limitIndex + 1] -ne [string][int]::MaxValue) {
            throw "PR discovery must default to all results: $args"
        }
        $global:PulseDiscoveryTestCalls++
        $global:LASTEXITCODE = 0
        return $global:PulseDiscoveryTestPrResponse
    }
    if ($args[0] -ne 'api' -or '--paginate' -notin $args -or '--slurp' -notin $args -or
        $args[-1] -ne 'repos/microsoft/PowerToys/issues?state=open&per_page=100') {
        throw "Unexpected gh invocation: $args"
    }
    $global:PulseDiscoveryTestCalls++
    $global:LASTEXITCODE = 0
    $global:PulseDiscoveryTestResponse
}

function Get-TestCandidates {
    $raw = & $candidateScript -Dashboard $fixture `
        -PullRequestsJsonPath (Join-Path $fixture 'prs.json') `
        -PrQueueJsonPath (Join-Path $fixture 'pr-queue.json') `
        -IssueQueueJsonPath (Join-Path $fixture 'issue-queue.json') -AsJson
    if (-not $?) { throw 'Candidate discovery failed' }
    ($raw -join "`n") | ConvertFrom-Json
}

try {
    '[]' | Set-Content (Join-Path $fixture 'prs.json')
    '{"stale_prs":[]}' | Set-Content (Join-Path $fixture 'pr-queue.json')
    '{"issues":[]}' | Set-Content (Join-Path $fixture 'issue-queue.json')
    $issues = @(foreach ($number in 1..1205) {
        @{
            number = $number; title = "Issue $number"
            updated_at = '2026-11-02T10:00:00Z'
            html_url = "https://github.com/microsoft/PowerToys/issues/$number"
            labels = @(@{ name = switch ($number) {
                1204 { 'bug' }
                1205 { 'Issue-Feature' }
                default { 'Issue-Bug' }
            } })
        }
    })
    $pr = @{
        number = 99999; title = 'Not an issue'; labels = @(@{ name = 'Issue-Bug' })
        pull_request = @{ url = 'https://api.github.com/repos/microsoft/PowerToys/pulls/99999' }
    }
    $pages = @(@($issues[0..499]), (@($issues[500..1204]) + @($pr)))
    $global:PulseDiscoveryTestResponse = ConvertTo-Json -InputObject $pages -Depth 10
    $result = Get-TestCandidates
    if ($result.issues.Count -ne 1204 -or 1204 -notin $result.issues.number -or
        1205 -in $result.issues.number -or 99999 -in $result.issues.number) {
        throw "Unexpected multi-page issue count $($result.issues.Count); alias=$([bool](1204 -in $result.issues.number)); feature=$([bool](1205 -in $result.issues.number)); PR=$([bool](99999 -in $result.issues.number))."
    }
    $global:PulseDiscoveryTestResponse = ConvertTo-Json -InputObject (, @($issues[0], $issues[1203], $pr)) -Depth 10
    $result = Get-TestCandidates
    if ($result.issues.Count -ne 2 -or 1204 -notin $result.issues.number -or $global:PulseDiscoveryTestCalls -ne 2) {
        throw 'Single-page discovery lost an issue or made an unexpected request.'
    }
    $global:PulseDiscoveryTestPrResponse = ConvertTo-Json -InputObject @(@{
        number = 1206; title = 'PR'; headRefOid = 'abc123'; isDraft = $false
        updatedAt = '2026-11-02T10:00:00Z'; labels = @()
    }) -Depth 10
    '[]' | Set-Content (Join-Path $fixture 'issues.json')
    $raw = & $candidateScript -Dashboard $fixture `
        -IssuesJsonPath (Join-Path $fixture 'issues.json') `
        -PrQueueJsonPath (Join-Path $fixture 'pr-queue.json') `
        -IssueQueueJsonPath (Join-Path $fixture 'issue-queue.json') -AsJson
    $result = ($raw -join "`n") | ConvertFrom-Json
    if ($result.prs.Count -ne 1 -or $global:PulseDiscoveryTestCalls -ne 3) {
        throw 'Default PR enumeration or supplied issue-input handling regressed.'
    }
    Write-Host 'Dashboard discovery tests passed (multi-page >1000 issues, single page, bug alias, PR/non-bug exclusion, uncapped PR default and supplied issue input).'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
    Remove-Variable PulseDiscoveryTestCalls, PulseDiscoveryTestResponse, PulseDiscoveryTestPrResponse -Scope Global
}
