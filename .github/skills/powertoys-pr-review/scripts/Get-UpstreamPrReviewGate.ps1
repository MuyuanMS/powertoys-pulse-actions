<#
.SYNOPSIS
    Check the original PR's lifecycle and head before continuing review work.
.DESCRIPTION
    Read-only. Stop closed, merged or draft PRs; reconcile a changed upstream
    head before using old findings or validation. API/response errors throw.
#>
[CmdletBinding()]
param(
    [string]$Upstream = 'microsoft/PowerToys',
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$PRNumber,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$HeadSha,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReviewPayload.Common.ps1')
$endpoint = "repos/$Upstream/pulls/$PRNumber"
$pr = Invoke-GhGet -Endpoint $endpoint
if ($null -eq $pr -or [string]$pr.state -notin @('open', 'closed') -or
    $pr.draft -isnot [bool] -or $pr.merged -isnot [bool] -or
    [string]$pr.head.sha -notmatch '^[a-fA-F0-9]{40}$') {
    throw "Incomplete or invalid upstream PR response: $endpoint"
}
$headMatches = [string]$pr.head.sha -ieq $HeadSha
$reason = if ($pr.merged) { 'merged' }
    elseif ($pr.state -eq 'closed') { 'closed' }
    elseif ($pr.draft) { 'draft' }
    elseif (-not $headMatches) { 'head_changed' }
    else { 'current' }
$result = [pscustomobject]@{
    number = $PRNumber
    checked_at = [datetimeoffset]::UtcNow.ToString('o')
    state = [string]$pr.state
    is_draft = [bool]$pr.draft
    merged = [bool]$pr.merged
    reviewed_head_sha = $HeadSha
    live_head_sha = [string]$pr.head.sha
    head_matches = $headMatches
    reason = $reason
    action = if ($reason -in @('merged', 'closed', 'draft')) { 'stop_review' }
        elseif ($reason -eq 'head_changed') { 'reconcile_updated_head' }
        else { 'continue_review' }
}
if ($AsJson) { $result | ConvertTo-Json -Depth 5 } else { $result }
