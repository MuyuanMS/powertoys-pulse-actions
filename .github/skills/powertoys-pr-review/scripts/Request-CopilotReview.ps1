<#
.SYNOPSIS
    Request a GitHub Copilot code review on a fork PR and poll until it lands.
.DESCRIPTION
    Requests 'copilot-pull-request-reviewer[bot]' as a reviewer (the plain
    'copilot' name silently no-ops), then polls the PR reviews until a Copilot
    review is submitted for the pinned fork head after the request time or the timeout elapses. GitHub
    may complete the review before returning the request response, so an empty
    requested_reviewers collection is not treated as evidence that the feature
    is disabled.
.PARAMETER ForkRepo
    owner/PowerToys for the fork that holds the mirror PR.
.PARAMETER PRNumber
    The fork PR number to review.
.PARAMETER TimeoutMinutes
    Maximum minutes to wait (default 10). Use 0 for one immediate check in
    bounded dashboard workers. Resume via Get-CopilotReviewStatus.ps1, not here.
.EXAMPLE
    ./Request-CopilotReview.ps1 -ForkRepo octocat/PowerToys -PRNumber 12
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ForkRepo,
    [Parameter(Mandatory)] [int]    $PRNumber,
    [ValidateRange(0, 60)][int] $TimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReviewPayload.Common.ps1')
$endpoint = "repos/$ForkRepo/pulls/$PRNumber"
$pullRequest = Invoke-GhGet -Endpoint $endpoint
$headSha = [string]$pullRequest.head.sha
if ($headSha -notmatch '^[a-f0-9]{40}$') { throw "Missing or invalid fork head: $endpoint" }
if (@($pullRequest.requested_reviewers | Where-Object {
    $_.login -in @('copilot-pull-request-reviewer[bot]', 'copilot-pull-request-reviewer')
}).Count -gt 0) {
    throw 'A Copilot request is already pending. Resume with Get-CopilotReviewStatus.ps1 instead of submitting another request.'
}
$priorReviews = @(Get-GhPagedItems -Endpoint "$endpoint/reviews")
$afterReviewId = 0L
foreach ($review in $priorReviews) {
    if ([long]$review.id -gt $afterReviewId) { $afterReviewId = [long]$review.id }
}
# GitHub timestamps have second precision; the baseline ID excludes older same-second reviews.
$requestedAt = [datetimeoffset]::FromUnixTimeSeconds([datetimeoffset]::UtcNow.ToUnixTimeSeconds())

$resp = Invoke-GhJsonInput -Endpoint "$endpoint/requested_reviewers" `
    -Payload @{ reviewers = @('copilot-pull-request-reviewer[bot]') }
if ($null -eq $resp) { throw "Empty review-request response: $endpoint" }

$requested = @($resp.requested_reviewers | Where-Object {
    $_.login -in @('copilot-pull-request-reviewer[bot]', 'copilot-pull-request-reviewer')
})
$requestPending = $requested.Count -gt 0
if (-not $requestPending) {
    Write-Host "Copilot is not listed as pending; checking for an asynchronously completed review."
}

Write-Host "Requested Copilot review on $ForkRepo#$PRNumber. Polling (timeout ${TimeoutMinutes}m)..."
$deadline = $requestedAt.AddMinutes($TimeoutMinutes)
while ($true) {
    $status = & (Join-Path $PSScriptRoot 'Get-CopilotReviewStatus.ps1') `
        -ForkRepo $ForkRepo -PRNumber $PRNumber -HeadSha $headSha `
        -RequestedAt $requestedAt -AfterReviewId $afterReviewId
    if (-not $status.HeadMatches) {
        throw "Fork head moved from $headSha to $($status.CurrentHeadSha). Reconcile the checkpoint before requesting another review."
    }
    if ($status.Submitted) {
        Write-Host "Copilot review submitted."
        return $status
    }
    $remainingSeconds = ($deadline - [datetimeoffset]::UtcNow).TotalSeconds
    if ($remainingSeconds -le 0) { break }
    Start-Sleep -Seconds ([Math]::Min(30, $remainingSeconds))
}
Write-Warning 'No matching review has arrived. Save RequestedAt, HeadSha and AfterReviewId; resume with Get-CopilotReviewStatus.ps1 without making another request.'
return $status
