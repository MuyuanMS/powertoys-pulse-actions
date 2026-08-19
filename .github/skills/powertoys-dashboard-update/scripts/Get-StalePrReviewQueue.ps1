<#
.SYNOPSIS
    Find PowerToys PRs that must go through the looped review before publishing.
.DESCRIPTION
    Enumerates all live open upstream PRs and compares them with dashboard
    artifacts. A PR is queued when it is open, non-draft, non-CmdPal, not in an
    author/owner/excluded hold state, and either has no publishable proposed
    review action for the live head or has new commits since the artifact head.

    This script is intentionally read-only. Use -FailOnStale in dashboard runs
    as a publication gate after workers finish; a non-empty queue means the
    updater must resume/run powertoys-pr-review rather than publish a
    metadata-only refresh.
#>
[CmdletBinding()]
param(
    [string]$Dashboard = $(if ($env:POWERTOYS_DASHBOARD_PATH) {
        $env:POWERTOYS_DASHBOARD_PATH
    } else {
        Join-Path $PSScriptRoot '..\..\..\..'
    }),
    [string]$Upstream = 'microsoft/PowerToys',
    [int]$Limit = 200,
    [switch]$AsJson,
    [switch]$FailOnStale
)

$ErrorActionPreference = 'Stop'
$Dashboard = (Resolve-Path $Dashboard).Path
$itemsPath = Join-Path $Dashboard 'data\items'

function Invoke-GhJson {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $raw = & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed."
    }

    if ([string]::IsNullOrWhiteSpace(($raw -join "`n"))) {
        return $null
    }

    return ($raw -join "`n" | ConvertFrom-Json)
}

function Test-IsCmdPal {
    param($PullRequest)
    $labels = @($PullRequest.labels | ForEach-Object { $_.name })
    $text = "$($PullRequest.title) $($labels -join ' ')"
    return $text -match '(?i)(CmdPal|Command Palette|PowerToys\.CommandPalette|CommandPalette)'
}

function Get-Artifact {
    param([int]$Number)
    $path = Join-Path $itemsPath "$Number.json"
    if (-not (Test-Path $path)) {
        return $null
    }

    return Get-Content $path -Raw | ConvertFrom-Json
}

function Test-HasApplicableReviewAction {
    param($Artifact, [string]$LiveHead)
    if (-not $Artifact) {
        return $false
    }

    $artifactHead = [string]$Artifact.head_sha
    if ([string]::IsNullOrWhiteSpace($artifactHead) -or $artifactHead -ne $LiveHead) {
        return $false
    }

    if ([string]$Artifact.stage -eq 'review_ready' -and @($Artifact.proposed_comments).Count -eq 0) {
        return $true
    }

    foreach ($action in @($Artifact.actions)) {
        if ($action.type -eq 'post_review' -and $action.review) {
            $reviewHead = [string]$action.review.head_sha
            if ([string]::IsNullOrWhiteSpace($reviewHead)) {
                $reviewHead = $artifactHead
            }
            if ($reviewHead -eq $LiveHead) {
                return $true
            }
        }
        if ($action.type -eq 'hold' -and [string]$Artifact.stage -eq 'review_ready') {
            return $true
        }
    }

    return $false
}

function Test-IsHoldState {
    param($Artifact)
    if (-not $Artifact) {
        return $false
    }

    if ($Artifact.pending_author -eq $true) {
        return $true
    }

    $stage = [string]$Artifact.stage
    return $stage -in @(
        'awaiting_author',
        'waiting_on_author',
        'awaiting_close_decision',
        'awaiting_maintainer_alignment',
        'awaiting_maintainer_direction',
        'awaiting_maintainer_direction_and_review_approval',
        'awaiting_maintainer_takeover_approval',
        'dropped',
        'owned_elsewhere',
        'excluded',
        'ineligible'
    )
}

$pullRequests = @(
    Invoke-GhJson @('pr', 'list', '-R', $Upstream, '--state', 'open',
        '--json', 'number,title,author,labels,updatedAt,isDraft,headRefOid,url',
        '--limit', "$Limit")
)

$queue = [System.Collections.Generic.List[object]]::new()
foreach ($pr in $pullRequests) {
    $number = [int]$pr.number
    $artifact = Get-Artifact $number

    if ($pr.isDraft) {
        continue
    }
    if (Test-IsCmdPal $pr) {
        continue
    }
    if (Test-IsHoldState $artifact) {
        continue
    }

    $artifactHead = if ($artifact) { [string]$artifact.head_sha } else { '' }
    $liveHead = [string]$pr.headRefOid
    $hasApplicableReview = Test-HasApplicableReviewAction $artifact $liveHead
    $reviewHead = if ($artifact) {
        $reviewAction = @($artifact.actions | Where-Object { $_.type -eq 'post_review' -and $_.review }) | Select-Object -First 1
        if ($reviewAction -and $reviewAction.review.head_sha) {
            [string]$reviewAction.review.head_sha
        } else {
            ''
        }
    } else {
        ''
    }

    $reasons = [System.Collections.Generic.List[string]]::new()
    if (-not $artifact) {
        $reasons.Add('missing_artifact')
    }
    if (-not $hasApplicableReview) {
        $reasons.Add('missing_current_review_action')
    }
    if ([string]::IsNullOrWhiteSpace($artifactHead)) {
        $reasons.Add('missing_artifact_head_sha')
    }
    elseif ($artifactHead -ne $liveHead) {
        $reasons.Add('new_commits_since_artifact_head')
    }
    if (-not [string]::IsNullOrWhiteSpace($reviewHead) -and $reviewHead -ne $liveHead) {
        $reasons.Add('new_commits_since_proposed_review')
    }

    if ($reasons.Count -eq 0) {
        continue
    }

    $queue.Add([pscustomobject]@{
        number = $number
        title = [string]$pr.title
        url = [string]$pr.url
        updated_at = [string]$pr.updatedAt
        live_head_sha = $liveHead
        artifact_stage = if ($artifact) { [string]$artifact.stage } else { '' }
        artifact_head_sha = $artifactHead
        proposed_review_head_sha = $reviewHead
        reasons = $reasons.ToArray()
    })
}

$result = [pscustomobject]@{
    generated_at = (Get-Date).ToUniversalTime().ToString('o')
    upstream = $Upstream
    dashboard = $Dashboard
    stale_count = $queue.Count
    stale_prs = $queue.ToArray()
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 10
} else {
    $queue.ToArray() | Sort-Object number | Format-Table number, artifact_stage, reasons, title -AutoSize
    Write-Host "Stale PR review queue: $($queue.Count)"
}

if ($FailOnStale -and $queue.Count -gt 0) {
    throw "Dashboard has $($queue.Count) applicable PR(s) that still require looped powertoys-pr-review."
}
