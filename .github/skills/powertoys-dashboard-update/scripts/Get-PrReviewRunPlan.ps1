<#
.SYNOPSIS
    Build a bounded, resumable PR review plan for one dashboard update run.
.DESCRIPTION
    Reads the live stale PR queue, prioritizes resumable and stale-head work,
    and selects only the configured number of PRs for this invocation. The
    remaining PRs stay in the durable stale queue for later scheduled runs.
#>
[CmdletBinding()]
param(
    [string]$Dashboard = $(if ($env:POWERTOYS_DASHBOARD_PATH) {
        $env:POWERTOYS_DASHBOARD_PATH
    } else {
        Join-Path $PSScriptRoot '..\..\..\..'
    }),
    [string]$Upstream = 'microsoft/PowerToys',
    [ValidateRange(1, 5)]
    [int]$BatchSize = $(if ($env:POWERTOYS_PR_REVIEW_BATCH_SIZE) {
        [int]$env:POWERTOYS_PR_REVIEW_BATCH_SIZE
    } else {
        5
    }),
    [ValidateRange(1, 3)]
    [int]$MaxConcurrency = $(if ($env:POWERTOYS_PR_REVIEW_CONCURRENCY) {
        [int]$env:POWERTOYS_PR_REVIEW_CONCURRENCY
    } else {
        2
    }),
    [ValidateRange(20, 55)]
    [int]$RunBudgetMinutes = $(if ($env:POWERTOYS_DASHBOARD_RUN_BUDGET_MINUTES) {
        [int]$env:POWERTOYS_DASHBOARD_RUN_BUDGET_MINUTES
    } else {
        45
    }),
    [string]$QueueJsonPath,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$Dashboard = (Resolve-Path $Dashboard).Path
$plannedAt = (Get-Date).ToUniversalTime()

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
$plan = [pscustomobject]@{
    planned_at = $plannedAt.ToString('o')
    deadline_utc = $plannedAt.AddMinutes($RunBudgetMinutes).ToString('o')
    upstream = $Upstream
    dashboard = $Dashboard
    policy = [pscustomobject]@{
        batch_size = $BatchSize
        max_concurrency = [Math]::Min($MaxConcurrency, $BatchSize)
        run_budget_minutes = $RunBudgetMinutes
        worker_stop_minutes = [Math]::Max(10, $RunBudgetMinutes - 15)
        publish_interval_minutes = 10
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
    Write-Host "Deadline (UTC): $($plan.deadline_utc)"
}
