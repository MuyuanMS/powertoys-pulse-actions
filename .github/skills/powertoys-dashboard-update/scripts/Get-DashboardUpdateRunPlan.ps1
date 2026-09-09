<#
.SYNOPSIS
    Select bounded or drain-mode work from the exhaustive dashboard inventory.
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
    [int]$PrBatchSize = $(if ($env:POWERTOYS_PR_REVIEW_BATCH_SIZE) {
        [int]$env:POWERTOYS_PR_REVIEW_BATCH_SIZE
    } elseif ($env:POWERTOYS_DASHBOARD_DRAIN_QUEUE -eq '1') {
        [int]::MaxValue
    } else {
        16
    }),
    [ValidateRange(1, 2147483647)]
    [int]$IssueBatchSize = $(if ($env:POWERTOYS_ISSUE_REVALIDATION_BATCH_SIZE) {
        [int]$env:POWERTOYS_ISSUE_REVALIDATION_BATCH_SIZE
    } elseif ($env:POWERTOYS_DASHBOARD_DRAIN_QUEUE -eq '1') {
        [int]::MaxValue
    } else {
        50
    }),
    [int[]]$PrNumbers,
    [int[]]$IssueNumbers,
    [switch]$DrainQueue,
    [string]$CandidatesJsonPath,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$Dashboard = (Resolve-Path $Dashboard).Path
$isDrainMode = $DrainQueue -or $env:POWERTOYS_DASHBOARD_DRAIN_QUEUE -eq '1'
if ($isDrainMode) {
    if (-not $PSBoundParameters.ContainsKey('PrBatchSize') -and -not $env:POWERTOYS_PR_REVIEW_BATCH_SIZE) {
        $PrBatchSize = [int]::MaxValue
    }
    if (-not $PSBoundParameters.ContainsKey('IssueBatchSize') -and -not $env:POWERTOYS_ISSUE_REVALIDATION_BATCH_SIZE) {
        $IssueBatchSize = [int]::MaxValue
    }
}

if ($CandidatesJsonPath) {
    $inventory = Get-Content (Resolve-Path $CandidatesJsonPath) -Raw | ConvertFrom-Json
} else {
    $raw = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Get-DashboardUpdateCandidates.ps1') `
        -Dashboard $Dashboard -Upstream $Upstream -AsJson
    if ($LASTEXITCODE -ne 0) {
        throw "Dashboard update candidate discovery failed with exit code $LASTEXITCODE."
    }
    $inventory = ($raw -join "`n") | ConvertFrom-Json
}

$prCandidates = @(
    @($inventory.prs | Where-Object {
        $_.classification -in @('full_review', 'context_revalidation') -and
        ($PrNumbers.Count -eq 0 -or [int]$_.number -in $PrNumbers)
    }) |
        Sort-Object `
            @{ Expression = {
                if ($_.classification -eq 'full_review') { 0 } else { 1 }
            } }, `
            @{ Expression = {
                if ([string]$_.artifact_stage -eq 'review_in_progress') { 0 } else { 1 }
            } }, `
            @{ Expression = {
                if ($_.live_updated_at) { [datetime]$_.live_updated_at } else { [datetime]::MinValue }
            }; Descending = $true }, `
            number
)
$issueCandidates = @(
    @($inventory.issues | Where-Object {
        $_.classification -eq 'issue_revalidation' -and
        ($IssueNumbers.Count -eq 0 -or [int]$_.number -in $IssueNumbers)
    }) |
        Sort-Object `
            @{ Expression = {
                if ($_.live_updated_at) { [datetime]$_.live_updated_at } else { [datetime]::MinValue }
            }; Descending = $true }, `
            number
)

$selectedPrs = @($prCandidates | Select-Object -First $PrBatchSize)
$deferredPrs = @($prCandidates | Select-Object -Skip $PrBatchSize)
$selectedIssues = @($issueCandidates | Select-Object -First $IssueBatchSize)
$deferredIssues = @($issueCandidates | Select-Object -Skip $IssueBatchSize)

$plan = [pscustomobject]@{
    planned_at = (Get-Date).ToUniversalTime().ToString('o')
    upstream = $Upstream
    dashboard = $Dashboard
    policy = [pscustomobject]@{
        drain_mode = $isDrainMode
        pr_batch_size = $PrBatchSize
        issue_batch_size = $IssueBatchSize
        explicit_pr_numbers = @($PrNumbers)
        explicit_issue_numbers = @($IssueNumbers)
    }
    inventory_summary = $inventory.summary
    selected_pr_count = $selectedPrs.Count
    deferred_pr_count = $deferredPrs.Count
    selected_issue_count = $selectedIssues.Count
    deferred_issue_count = $deferredIssues.Count
    selected_prs = $selectedPrs
    deferred_prs = $deferredPrs
    selected_issues = $selectedIssues
    deferred_issues = $deferredIssues
}

if ($AsJson) {
    $plan | ConvertTo-Json -Depth 10
} else {
    Write-Host "Dashboard update plan: PRs selected=$($selectedPrs.Count) deferred=$($deferredPrs.Count); issues selected=$($selectedIssues.Count) deferred=$($deferredIssues.Count)"
    $selectedPrs | Format-Table number, classification, artifact_stage, title -AutoSize
    $selectedIssues | Format-Table number, classification, artifact_stage, title -AutoSize
}
