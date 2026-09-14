<#
.SYNOPSIS
    Find PowerToys PRs that must go through the looped review before publishing.
.DESCRIPTION
    Enumerates all live open upstream PRs and compares them with dashboard
    artifacts. A PR is queued when it is open, non-draft, not genuinely waiting
    on its author, and either has no publishable proposed review action for the
    live head or has new commits since the artifact head.

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
if (Test-Path (Join-Path $Dashboard '.git')) {
    & (Join-Path $PSScriptRoot 'Assert-CanonicalDashboardTarget.ps1') `
        -Dashboard $Dashboard | Out-Null
}
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

function Get-Artifact {
    param([int]$Number)
    $path = Join-Path $itemsPath "$Number.json"
    if (-not (Test-Path $path)) {
        return $null
    }

    return Get-Content $path -Raw | ConvertFrom-Json
}

function Test-HasNewerActivity {
    param($Artifact, [string]$LiveUpdatedAt)
    if (-not $Artifact -or
        [string]::IsNullOrWhiteSpace([string]$Artifact.source_updated_at) -or
        [string]::IsNullOrWhiteSpace($LiveUpdatedAt)) {
        return $false
    }

    return [datetimeoffset]::Parse($LiveUpdatedAt).ToUniversalTime() -gt
        [datetimeoffset]::Parse([string]$Artifact.source_updated_at).ToUniversalTime()
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

    # A stale checkpoint must not masquerade as a concluded clean review.
    # review_ready is valid only after the workflow has left all resumable
    # phases and its upstream-head validation is pinned to the live head.
    if ($Artifact.needs_revalidation -eq $true -or
        [string]$Artifact.workflow.phase -in @(
            'queued',
            'mirroring',
            'review_requested',
            'waiting_copilot',
            'reviewing_findings',
            'building',
            'review_in_progress'
        )) {
        return $false
    }

    if ([string]$Artifact.stage -eq 'review_ready' -and @($Artifact.proposed_comments).Count -eq 0) {
        $validation = $Artifact.validation.upstream_head
        return $null -ne $validation -and
            [string]$validation.head_sha -eq $LiveHead -and
            [string]$validation.result -eq 'passed'
    }

    foreach ($action in @($Artifact.actions)) {
        if ($action.type -in @('post_review', 'request_changes') -and $action.review) {
            $reviewHead = [string]$action.review.head_sha
            if ([string]::IsNullOrWhiteSpace($reviewHead)) {
                $reviewHead = $artifactHead
            }
            if ($reviewHead -eq $LiveHead) {
                return $true
            }
        }
    }

    return $false
}

function Test-HasLegacyReviewActionStage {
    param($Artifact)
    if (-not $Artifact) {
        return $false
    }

    $reviewActions = @($Artifact.actions | Where-Object {
        $_.type -in @('post_review', 'request_changes') -and $_.review
    })
    if ($reviewActions.Count -eq 0) {
        return $false
    }

    return [string]$Artifact.stage -in @(
        'request_changes',
        'review_drafted',
        'converged_draft_ready'
    )
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
        'waiting_on_author'
    )
}

function Test-IsTerminalBlocker {
    param($Artifact, [string]$LiveHead)
    if (-not $Artifact -or [string]$Artifact.head_sha -ne $LiveHead) {
        return $false
    }

    if ([string]$Artifact.stage -ne 'review_blocked') {
        return $false
    }

    $terminalBlockers = @($Artifact.blockers | Where-Object {
        $null -ne $_ -and $_.terminal -eq $true
    })
    if ($terminalBlockers.Count -eq 0) {
        return $false
    }

    foreach ($blocker in $terminalBlockers) {
        if ([string]::IsNullOrWhiteSpace([string]$blocker.detail) -or
            [string]::IsNullOrWhiteSpace([string]$blocker.remediation)) {
            return $false
        }
    }

    return $true
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
    $artifactHead = if ($artifact) { [string]$artifact.head_sha } else { '' }
    $liveHead = [string]$pr.headRefOid
    $hasNewerActivity = Test-HasNewerActivity $artifact ([string]$pr.updatedAt)
    if ((Test-IsHoldState $artifact) -and -not $hasNewerActivity) {
        continue
    }
    if ((Test-IsTerminalBlocker $artifact $liveHead) -and -not $hasNewerActivity) {
        continue
    }

    $hasApplicableReview = Test-HasApplicableReviewAction $artifact $liveHead
    $hasLegacyReviewActionStage = Test-HasLegacyReviewActionStage $artifact
    $reviewHead = if ($artifact) {
        $reviewAction = @($artifact.actions | Where-Object {
            $_.type -in @('post_review', 'request_changes') -and $_.review
        }) | Select-Object -First 1
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
    if ($hasLegacyReviewActionStage) {
        $reasons.Add('legacy_review_action_stage')
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
    if ($hasNewerActivity -and $artifactHead -eq $liveHead) {
        if (Test-IsHoldState $artifact) {
            $reasons.Add('new_activity_after_author_wait')
        } elseif (Test-IsTerminalBlocker $artifact $liveHead) {
            $reasons.Add('new_activity_after_blocker')
        } elseif ($hasApplicableReview) {
            $reasons.Add('new_discussion_on_reviewed_head')
        }
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
        work_type = if ($reasons -contains 'new_discussion_on_reviewed_head') {
            'context_revalidation'
        } else {
            'full_review'
        }
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
