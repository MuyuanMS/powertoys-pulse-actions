<#
.SYNOPSIS
    Count unresolved Copilot review threads on a fork PR.
.DESCRIPTION
    Returns the number of review threads whose first comment is authored by
    'copilot-pull-request-reviewer' and that are not yet resolved. Used to decide
    whether the review loop is finished (expect 0) and to detect a stranded loop
    when resuming an interrupted session.
.PARAMETER ForkOwner
    The fork owner login (the repo is assumed to be <owner>/PowerToys).
.PARAMETER PRNumber
    The fork PR number.
.EXAMPLE
    ./Get-UnresolvedCopilotThreads.ps1 -ForkOwner octocat -PRNumber 12
    Returns an integer count; 0 alone does not prove a fresh clean review or build.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ForkOwner,
    [Parameter(Mandatory)] [int]    $PRNumber
)

$ErrorActionPreference = 'Stop'

$query = @'
query($owner:String!, $number:Int!, $cursor:String) {
  repository(owner:$owner, name:"PowerToys") {
    pullRequest(number:$number) {
      reviewThreads(first:100, after:$cursor) {
        nodes { isResolved comments(first:1) { nodes { author { login } } } }
        pageInfo { hasNextPage endCursor }
      }
    }
  }
}
'@
$count = 0
$cursor = ''
do {
    $arguments = @('api', 'graphql', '-f', "query=$query", '-f', "owner=$ForkOwner", '-F', "number=$PRNumber")
    if ($cursor) { $arguments += @('-f', "cursor=$cursor") }
    $output = & gh @arguments
    if ($LASTEXITCODE -ne 0) { throw "Cannot read review threads for $ForkOwner/PowerToys PR $PRNumber." }
    $response = ($output -join "`n") | ConvertFrom-Json
    $connection = $response.data.repository.pullRequest.reviewThreads
    if ($response.errors -or $null -eq $connection -or $null -eq $connection.pageInfo) {
        throw "Incomplete review thread response for $ForkOwner/PowerToys PR $PRNumber."
    }
    foreach ($thread in @($connection.nodes)) {
        if ($thread.isResolved -eq $false -and
            $thread.comments.nodes[0].author.login -in @('copilot-pull-request-reviewer', 'copilot-pull-request-reviewer[bot]')) {
            $count++
        }
    }
    $nextCursor = [string]$connection.pageInfo.endCursor
    if ($connection.pageInfo.hasNextPage -and (-not $nextCursor -or $nextCursor -eq $cursor)) {
        throw 'Review thread pagination did not advance.'
    }
    $cursor = $nextCursor
} while ($connection.pageInfo.hasNextPage)
$count
