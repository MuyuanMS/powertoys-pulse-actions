<#
.SYNOPSIS
    Persist private review receipts and neutral public availability metadata.
.DESCRIPTION
    Saves full execution state outside the repository, then atomically writes
    a minimal review_in_progress artifact without operator instructions.
    Completed or author-waiting artifacts are protected unless -Force is supplied.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$Number,
    [Parameter(Mandatory)]
    [string]$HeadSha,
    [Parameter(Mandatory)]
    [string]$SourceUpdatedAt,
    [Parameter(Mandatory)]
    [ValidateSet(
        'queued',
        'mirroring',
        'review_requested',
        'waiting_copilot',
        'reviewing_findings',
        'building',
        'review_in_progress'
    )]
    [string]$Phase,
    [Parameter(Mandatory)]
    [string]$Detail,
    [string]$Title,
    [string]$Url,
    [int]$ForkPr,
    [string]$ForkBranch,
    [string]$Fork = $env:POWERTOYS_FORK_REPO,
    [string]$PreserveEvaluatedAt,
    [string]$PreserveQueuedAt,
    [string]$PrivateStatePath,
    [string]$Dashboard = $(if ($env:POWERTOYS_DASHBOARD_PATH) {
        $env:POWERTOYS_DASHBOARD_PATH
    } else {
        Join-Path $PSScriptRoot '..\..\..\..'
    }),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PublicPrArtifact.ps1')

function ConvertTo-UtcIsoTimestamp {
    param($Value, [string]$Field)
    if ($Value -is [datetimeoffset]) {
        return $Value.UtcDateTime.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [datetime]) {
        return $Value.ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    }
    $parsed = [datetimeoffset]::MinValue
    $styles = [Globalization.DateTimeStyles]::AssumeUniversal -bor
        [Globalization.DateTimeStyles]::AdjustToUniversal
    if (-not [datetimeoffset]::TryParse(
        [string]$Value,
        [Globalization.CultureInfo]::GetCultureInfo('en-US'),
        $styles,
        [ref]$parsed
    )) {
        throw "Cannot preserve $Field as a UTC timestamp: '$Value'."
    }
    return $parsed.UtcDateTime.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
}

$Dashboard = (Resolve-Path $Dashboard).Path
if (Test-Path (Join-Path $Dashboard '.git')) {
    & (Join-Path $PSScriptRoot 'Assert-CanonicalDashboardTarget.ps1') `
        -Dashboard $Dashboard | Out-Null
}
$itemsPath = Join-Path $Dashboard 'data\items'
New-Item -ItemType Directory -Force -Path $itemsPath | Out-Null
$artifactPath = Join-Path $itemsPath "$Number.json"
$existing = if (Test-Path $artifactPath) {
    Get-Content $artifactPath -Raw | ConvertFrom-Json
} else {
    $null
}
$sameHead = $existing -and ([string]$existing.head_sha -eq $HeadSha)

if ($sameHead -and -not $Force) {
    $protectedStage = [string]$existing.stage
    if ($existing.pending_author -eq $true -or $protectedStage -in @(
        'ready',
        'review_drafted',
        'review_ready',
        'review_posted',
        'awaiting_review_approval',
        'awaiting_author',
        'waiting_on_author'
    )) {
        throw "Refusing to replace protected PR $Number artifact at stage '$protectedStage'."
    }
}
$now = (Get-Date).ToUniversalTime().ToString('o')
$artifact = [ordered]@{
    number = $Number
    kind = 'pr'
    track = 'review'
    stage = 'review_in_progress'
    owes = 'us'
    pending_author = $false
    generated_at = $now
    evaluated_at = if ($PreserveEvaluatedAt) {
        ConvertTo-UtcIsoTimestamp $PreserveEvaluatedAt 'evaluated_at'
    } elseif ($sameHead -and $existing.evaluated_at) {
        ConvertTo-UtcIsoTimestamp $existing.evaluated_at 'evaluated_at'
    } else {
        $now
    }
    source_updated_at = $SourceUpdatedAt
    head_sha = $HeadSha
    title = if ($Title) { $Title } elseif ($existing) { [string]$existing.title } else { '' }
    url = if ($Url) { $Url } elseif ($existing) { [string]$existing.url } else { "https://github.com/microsoft/PowerToys/pull/$Number" }
    status = [ordered]@{
        glyph = '...'
        label = switch ($Phase) {
            'queued' { 'Review queued' }
            'mirroring' { 'Preparing fork review' }
            'review_requested' { 'Copilot review requested' }
            'waiting_copilot' { 'Waiting for Copilot review' }
            'reviewing_findings' { 'Reviewing findings' }
            'building' { 'Local validation running' }
            default { 'Review in progress' }
        }
        detail = $Detail
    }
    next_action = 'Resume the saved review stage in the next dashboard update.'
    proposed_comments = @()
    actions = @()
    workflow = [ordered]@{
        phase = $Phase
        checkpoint_at = $now
    }
    freshness = [ordered]@{
        queued_at = if ($PreserveQueuedAt) {
            ConvertTo-UtcIsoTimestamp $PreserveQueuedAt 'freshness.queued_at'
        } elseif ($sameHead -and $existing.freshness.queued_at) {
            ConvertTo-UtcIsoTimestamp $existing.freshness.queued_at 'freshness.queued_at'
        } else {
            $now
        }
        checkpoint_at = $now
    }
}

if ($ForkPr -gt 0) {
    $forkPrData = [ordered]@{
        number = $ForkPr
    }
    if ($Fork) {
        $forkPrData['url'] = "https://github.com/$Fork/pull/$ForkPr"
    }
    $artifact['fork_pr'] = $forkPrData
}
if ($ForkBranch) {
    $artifact['fork_branch'] = $ForkBranch
}

$encoding = [System.Text.UTF8Encoding]::new($false)
if ($existing) { Save-PrivatePrArtifact $existing $Dashboard $PrivateStatePath }
Save-PrivatePrArtifact ([pscustomobject]$artifact) $Dashboard $PrivateStatePath
$artifact = ConvertTo-PublicPrArtifact ([pscustomobject]$artifact)
$tempPath = "$artifactPath.tmp"
[System.IO.File]::WriteAllText(
    $tempPath,
    (([pscustomobject]$artifact) | ConvertTo-Json -Depth 12),
    $encoding
)
Move-Item -Force $tempPath $artifactPath
Write-Host "Checkpointed PR $Number at phase '$Phase'."
