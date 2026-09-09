<#
.SYNOPSIS
    Classify every live open PowerToys PR and bug for dashboard update work.
.DESCRIPTION
    Produces an exhaustive, read-only freshness inventory. Candidate discovery
    is deliberately separate from bounded execution so deferred work remains
    visible on every scheduled run and cannot age out of a rolling time window.

    PR classifications:
      full_review          Missing review, changed head, or incomplete review.
      context_revalidation Same reviewed head with newer discussion/activity.
      waiting_author       Current author-wait state with no newer activity.
      blocked              Current terminal blocker with no newer activity.
      no_action            Current review and covered activity.
      excluded             Draft PR.

    Issue classifications:
      issue_revalidation   Missing/invalid artifact or newer issue activity.
      no_action            Current valid artifact with no newer activity.
#>
[CmdletBinding()]
param(
    [string]$Dashboard = $(if ($env:POWERTOYS_DASHBOARD_PATH) {
        $env:POWERTOYS_DASHBOARD_PATH
    } else {
        Join-Path $PSScriptRoot '..\..\..\..'
    }),
    [string]$Upstream = 'microsoft/PowerToys',
    [ValidateRange(1, 1000)]
    [int]$Limit = 500,
    [string]$PullRequestsJsonPath,
    [string]$IssuesJsonPath,
    [string]$PrQueueJsonPath,
    [string]$IssueQueueJsonPath,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$Dashboard = (Resolve-Path $Dashboard).Path
if (Test-Path (Join-Path $Dashboard '.git')) {
    & (Join-Path $PSScriptRoot 'Assert-CanonicalDashboardTarget.ps1') `
        -Dashboard $Dashboard | Out-Null
}
$itemsPath = Join-Path $Dashboard 'data\items'

function Read-JsonFile {
    param([Parameter(Mandatory)][string]$Path)
    return Get-Content (Resolve-Path $Path) -Raw | ConvertFrom-Json
}

function Invoke-GhJson {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $raw = & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed."
    }
    return ($raw -join "`n") | ConvertFrom-Json
}

function Get-Artifact {
    param([int]$Number)
    $path = Join-Path $itemsPath "$Number.json"
    if (-not (Test-Path $path)) {
        return $null
    }
    try {
        return Get-Content $path -Raw | ConvertFrom-Json
    } catch {
        return $null
    }
}

function Test-IsWaitingAuthor {
    param($Artifact)
    if (-not $Artifact) {
        return $false
    }
    return $Artifact.pending_author -eq $true -or
        [string]$Artifact.stage -in @('awaiting_author', 'waiting_on_author')
}

function Test-IsTerminalBlocker {
    param($Artifact, [string]$LiveHead)
    if (-not $Artifact -or [string]$Artifact.head_sha -ne $LiveHead -or
        [string]$Artifact.stage -ne 'review_blocked') {
        return $false
    }
    $blockers = @($Artifact.blockers | Where-Object { $null -ne $_ })
    return $blockers.Count -gt 0 -and @($blockers | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.detail) -or
        [string]::IsNullOrWhiteSpace([string]$_.remediation)
    }).Count -eq 0
}

function Get-LabelNames {
    param($Labels)
    return @($Labels | ForEach-Object {
        if ($_ -is [string]) { [string]$_ } else { [string]$_.name }
    })
}

if ($PullRequestsJsonPath) {
    $pullRequests = @(Read-JsonFile $PullRequestsJsonPath)
} else {
    $pullRequests = @(
        Invoke-GhJson @(
            'pr', 'list', '-R', $Upstream, '--state', 'open',
            '--json', 'number,title,author,labels,updatedAt,isDraft,headRefOid,url',
            '--limit', "$Limit"
        )
    )
}

if ($IssuesJsonPath) {
    $issues = @(Read-JsonFile $IssuesJsonPath)
} else {
    $issues = @(
        Invoke-GhJson @(
            'issue', 'list', '-R', $Upstream, '--state', 'open',
            '--json', 'number,title,labels,updatedAt,url', '--limit', "$Limit"
        )
    )
}

if ($PrQueueJsonPath) {
    $prQueue = Read-JsonFile $PrQueueJsonPath
} else {
    $raw = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Get-StalePrReviewQueue.ps1') `
        -Dashboard $Dashboard -Upstream $Upstream -Limit $Limit -AsJson
    if ($LASTEXITCODE -ne 0) {
        throw "PR freshness queue discovery failed with exit code $LASTEXITCODE."
    }
    $prQueue = ($raw -join "`n") | ConvertFrom-Json
}

if ($IssueQueueJsonPath) {
    $issueQueue = Read-JsonFile $IssueQueueJsonPath
} else {
    $raw = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Get-StaleIssueTriageQueue.ps1') `
        -Dashboard $Dashboard -AsJson
    if ($LASTEXITCODE -ne 0) {
        throw "Issue freshness queue discovery failed with exit code $LASTEXITCODE."
    }
    $issueQueue = ($raw -join "`n") | ConvertFrom-Json
}

$prQueueByNumber = @{}
foreach ($candidate in @($prQueue.stale_prs)) {
    $prQueueByNumber[[int]$candidate.number] = $candidate
}
$issueQueueByNumber = @{}
foreach ($candidate in @($issueQueue.issues)) {
    $issueQueueByNumber[[int]$candidate.number] = $candidate
}

$prInventory = foreach ($pr in $pullRequests) {
    $number = [int]$pr.number
    $artifact = Get-Artifact $number
    $candidate = $prQueueByNumber[$number]
    $classification = if ($pr.isDraft) {
        'excluded'
    } elseif ($candidate) {
        [string]$candidate.work_type
    } elseif (Test-IsWaitingAuthor $artifact) {
        'waiting_author'
    } elseif (Test-IsTerminalBlocker $artifact ([string]$pr.headRefOid)) {
        'blocked'
    } else {
        'no_action'
    }
    $reasons = if ($candidate) {
        @($candidate.reasons)
    } elseif ($classification -eq 'excluded') {
        @('draft_pr')
    } elseif ($classification -eq 'waiting_author') {
        @('current_author_wait')
    } elseif ($classification -eq 'blocked') {
        @('current_terminal_blocker')
    } else {
        @('review_head_and_activity_are_current')
    }

    [pscustomobject]@{
        kind = 'pr'
        number = $number
        title = [string]$pr.title
        url = [string]$pr.url
        classification = $classification
        recommended_skill = if ($classification -in @('full_review', 'context_revalidation')) {
            'powertoys-pr-review'
        } else {
            $null
        }
        live_updated_at = [string]$pr.updatedAt
        live_head_sha = [string]$pr.headRefOid
        artifact_stage = if ($artifact) { [string]$artifact.stage } else { '' }
        artifact_head_sha = if ($artifact) { [string]$artifact.head_sha } else { '' }
        evaluated_at = if ($artifact) { [string]$artifact.evaluated_at } else { '' }
        source_updated_at = if ($artifact) { [string]$artifact.source_updated_at } else { '' }
        reasons = $reasons
    }
}

$issueInventory = foreach ($issue in $issues) {
    $labels = Get-LabelNames $issue.labels
    if (@($labels | Where-Object { $_ -like 'Issue-Bug*' }).Count -eq 0) {
        continue
    }
    $number = [int]$issue.number
    $artifact = Get-Artifact $number
    $candidate = $issueQueueByNumber[$number]
    $reasons = [System.Collections.Generic.List[string]]::new()
    if ($candidate) {
        foreach ($reason in @($candidate.reasons)) {
            $reasons.Add([string]$reason)
        }
    }
    if (-not $artifact -and -not ($reasons -contains 'missing artifact')) {
        $reasons.Add('missing_artifact')
    }
    if ($artifact -and $artifact.source_updated_at -and $issue.updatedAt) {
        try {
            if ([datetimeoffset]::Parse([string]$issue.updatedAt).ToUniversalTime() -gt
                [datetimeoffset]::Parse([string]$artifact.source_updated_at).ToUniversalTime() -and
                -not ($reasons -contains 'upstream activity is newer than the artifact')) {
                $reasons.Add('new_issue_activity')
            }
        } catch {
            if (-not ($reasons -contains 'invalid freshness timestamp')) {
                $reasons.Add('invalid_freshness_timestamp')
            }
        }
    }
    $classification = if ($reasons.Count -gt 0) {
        'issue_revalidation'
    } else {
        'no_action'
    }

    [pscustomobject]@{
        kind = 'issue'
        number = $number
        title = [string]$issue.title
        url = [string]$issue.url
        classification = $classification
        recommended_skill = if ($classification -eq 'issue_revalidation') {
            'powertoys-issue-to-design'
        } else {
            $null
        }
        live_updated_at = [string]$issue.updatedAt
        evaluated_at = if ($artifact) { [string]$artifact.evaluated_at } else { '' }
        source_updated_at = if ($artifact) { [string]$artifact.source_updated_at } else { '' }
        artifact_stage = if ($artifact) { [string]$artifact.stage } else { '' }
        reasons = if ($reasons.Count -gt 0) {
            $reasons.ToArray()
        } else {
            @('issue_activity_and_artifact_are_current')
        }
    }
}

$all = @($prInventory) + @($issueInventory)
$summary = [ordered]@{}
foreach ($classification in @(
    'full_review', 'context_revalidation', 'issue_revalidation',
    'waiting_author', 'blocked', 'no_action', 'excluded'
)) {
    $summary[$classification] = @($all | Where-Object classification -eq $classification).Count
}

$result = [pscustomobject]@{
    generated_at = (Get-Date).ToUniversalTime().ToString('o')
    upstream = $Upstream
    dashboard = $Dashboard
    summary = [pscustomobject]$summary
    prs = @($prInventory | Sort-Object number)
    issues = @($issueInventory | Sort-Object number)
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 10
} else {
    Write-Host "Dashboard update candidates"
    $result.summary | Format-List
    @($all | Where-Object classification -in @(
        'full_review', 'context_revalidation', 'issue_revalidation'
    )) | Sort-Object classification, live_updated_at -Descending |
        Format-Table kind, number, classification, live_updated_at, title -AutoSize
}
