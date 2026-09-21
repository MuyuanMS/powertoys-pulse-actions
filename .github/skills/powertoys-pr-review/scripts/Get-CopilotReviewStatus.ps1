<#
.SYNOPSIS
    Read a fork PR's fully paginated Copilot review state without requesting a review.
.DESCRIPTION
    Matches submitted reviews to the pinned fork head and optional saved request
    timestamp. Submitted means a review arrived, not that it is clean or built.
    API errors fail explicitly; they are never converted to waiting_copilot.
.PARAMETER HeadSha
    Exact fork head saved with the request (not the upstream head).
.PARAMETER RequestedAt
    Saved UTC request timestamp. Omit only when discovering prior reviews.
.PARAMETER AfterReviewId
    Optional baseline review ID returned by Request-CopilotReview.ps1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ForkRepo,
    [Parameter(Mandatory)][int]$PRNumber,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$HeadSha,
    [datetimeoffset]$RequestedAt = [datetimeoffset]::MinValue,
    [long]$AfterReviewId = 0,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReviewPayload.Common.ps1')
$endpoint = "repos/$ForkRepo/pulls/$PRNumber"
$pullRequest = Invoke-GhGet -Endpoint $endpoint
if ([string]::IsNullOrWhiteSpace([string]$pullRequest.head.sha)) {
    throw "Missing fork head in GitHub response: $endpoint"
}
$reviews = @(Get-GhPagedItems -Endpoint "$endpoint/reviews")
$latest = Get-LatestCopilotReview -Reviews $reviews -HeadSha $HeadSha `
    -RequestedAt $RequestedAt -AfterReviewId $AfterReviewId
$headMatches = [string]$pullRequest.head.sha -ceq $HeadSha
$result = [pscustomobject]@{
    Available = if ($latest) { $true } else { $null }
    Submitted = $null -ne $latest -and $headMatches
    HeadMatches = $headMatches
    HeadSha = $HeadSha
    CurrentHeadSha = [string]$pullRequest.head.sha
    RequestedAt = if ($RequestedAt -ne [datetimeoffset]::MinValue) { $RequestedAt.ToUniversalTime() } else { $null }
    AfterReviewId = $AfterReviewId
    ReviewId = if ($latest) { [long]$latest.id } else { $null }
    SubmittedAt = if ($latest) { ([datetimeoffset]$latest.submitted_at).ToUniversalTime() } else { $null }
    ReviewCommitSha = if ($latest) { [string]$latest.commit_id } else { $null }
    ReviewUrl = if ($latest) { [string]$latest.html_url } else { $null }
    ReviewBody = if ($latest) { [string]$latest.body } else { $null }
    RequestPending = @($pullRequest.requested_reviewers | Where-Object {
        $_.login -in @('copilot-pull-request-reviewer[bot]', 'copilot-pull-request-reviewer')
    }).Count -gt 0
    ReviewsScanned = $reviews.Count
}
if ($AsJson) { $result | ConvertTo-Json -Depth 5 } else { $result }
