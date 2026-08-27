<#
.SYNOPSIS
    Build a resumable PR review plan for one dashboard update run.
.DESCRIPTION
    Reads the live stale PR queue and prioritizes resumable and stale-head work.
    Normal mode selects only the configured number of PRs for this invocation,
    leaving the rest in the durable stale queue. Drain mode selects the entire
    queue, removes the run deadline, and relies on checkpoints plus incremental
    publication to make progress durable if the session stops.
#>
[CmdletBinding()]
param(
    [string]$Dashboard = $(if ($env:POWERTOYS_DASHBOARD_PATH) {
        $env:POWERTOYS_DASHBOARD_PATH
    } else {
        Join-Path $PSScriptRoot '..\..\..\..'
    }),
    [string]$Upstream = 'microsoft/PowerToys',
    [ValidateRange(1, 2147483647)]
    [int]$BatchSize = $(if ($env:POWERTOYS_PR_REVIEW_BATCH_SIZE) {
        [int]$env:POWERTOYS_PR_REVIEW_BATCH_SIZE
    } elseif ($env:POWERTOYS_DASHBOARD_DRAIN_QUEUE -eq '1') {
        [int]::MaxValue
    } else {
        16
    }),
    [ValidateRange(1, 8)]
    [int]$MaxConcurrency = $(if ($env:POWERTOYS_PR_REVIEW_CONCURRENCY) {
        [int]$env:POWERTOYS_PR_REVIEW_CONCURRENCY
    } elseif ($env:POWERTOYS_DASHBOARD_DRAIN_QUEUE -eq '1') {
        6
    } else {
        3
    }),
    [ValidateRange(0, 90)]
    [int]$RunBudgetMinutes = $(if ($env:POWERTOYS_DASHBOARD_RUN_BUDGET_MINUTES) {
        [int]$env:POWERTOYS_DASHBOARD_RUN_BUDGET_MINUTES
    } elseif ($env:POWERTOYS_DASHBOARD_DRAIN_QUEUE -eq '1') {
        0
    } else {
        50
    }),
    [switch]$DrainQueue,
    [string]$QueueJsonPath,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$Dashboard = (Resolve-Path $Dashboard).Path
if (Test-Path (Join-Path $Dashboard '.git')) {
    & (Join-Path $PSScriptRoot 'Assert-CanonicalDashboardTarget.ps1') `
        -Dashboard $Dashboard | Out-Null
}
$plannedAt = (Get-Date).ToUniversalTime()
$isDrainMode = $DrainQueue -or $env:POWERTOYS_DASHBOARD_DRAIN_QUEUE -eq '1'
if ($isDrainMode) {
    if (-not $PSBoundParameters.ContainsKey('BatchSize') -and -not $env:POWERTOYS_PR_REVIEW_BATCH_SIZE) {
        $BatchSize = [int]::MaxValue
    }
    if (-not $PSBoundParameters.ContainsKey('MaxConcurrency') -and -not $env:POWERTOYS_PR_REVIEW_CONCURRENCY) {
        $MaxConcurrency = 6
    }
    if (-not $PSBoundParameters.ContainsKey('RunBudgetMinutes') -and -not $env:POWERTOYS_DASHBOARD_RUN_BUDGET_MINUTES) {
        $RunBudgetMinutes = 0
    }
}

if ($QueueJsonPath) {
    $queueResult = Get-Content (Resolve-Path $QueueJsonPath) -Raw | ConvertFrom-Json
} else {
    $queueScript = Join-Path $PSScriptRoot 'Get-StalePrReviewQueue.ps1'
    $raw = & pwsh -NoProfile -File $queueScript `
        -Dashboard $Dashboard -Upstream $Upstream -AsJson
    if ($LASTEXITCODE -ne 0) {
        throw "Stale PR review queue discovery failed with exit code $LASTEXITCODE."
    }
    $queueResult = ($raw -join "`n") | ConvertFrom-Json
}

$ranked = @(
    @($queueResult.stale_prs) |
        Sort-Object `
            @{ Expression = {
                if ([string]$_.artifact_stage -eq 'review_in_progress') { 0 } else { 1 }
            } }, `
            @{ Expression = {
                if (@($_.reasons) -contains 'new_commits_since_proposed_review') { 0 }
                elseif (@($_.reasons) -contains 'new_commits_since_artifact_head') { 1 }
                elseif (@($_.reasons) -contains 'missing_artifact') { 2 }
                else { 3 }
            } }, `
            @{ Expression = {
                if ($_.updated_at) { [datetime]$_.updated_at } else { [datetime]::MinValue }
            } }, `
            number
)

$selected = @($ranked | Select-Object -First $BatchSize)
$deferred = @($ranked | Select-Object -Skip $BatchSize)
$deadlineUtc = if ($RunBudgetMinutes -gt 0) { $plannedAt.AddMinutes($RunBudgetMinutes).ToString('o') } else { $null }
$workerStopMinutes = if ($RunBudgetMinutes -gt 0) { [Math]::Max(10, $RunBudgetMinutes - 10) } else { $null }
$publishIntervalMinutes = if ($isDrainMode) { 5 } else { 8 }
$plan = [pscustomobject]@{
    planned_at = $plannedAt.ToString('o')
    deadline_utc = $deadlineUtc
    upstream = $Upstream
    dashboard = $Dashboard
    policy = [pscustomobject]@{
        drain_mode = $isDrainMode
        batch_size = $BatchSize
        max_concurrency = [Math]::Min($MaxConcurrency, $BatchSize)
        run_budget_minutes = $RunBudgetMinutes
        worker_stop_minutes = $workerStopMinutes
        publish_interval_minutes = $publishIntervalMinutes
        publish_transition_count = 2
    }
    stale_count = $ranked.Count
    selected_count = $selected.Count
    deferred_count = $deferred.Count
    selected_prs = $selected
    deferred_prs = $deferred
}

if ($AsJson) {
    $plan | ConvertTo-Json -Depth 10
} else {
    Write-Host "PR review run plan: selected=$($selected.Count) deferred=$($deferred.Count)"
    $selected | Format-Table number, artifact_stage, reasons, title -AutoSize
    if ($plan.deadline_utc) {
        Write-Host "Deadline (UTC): $($plan.deadline_utc)"
    } else {
        Write-Host "Deadline (UTC): none (drain mode)"
    }
}
