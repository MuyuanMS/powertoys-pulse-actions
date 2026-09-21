$ErrorActionPreference = 'Stop'
$scripts = Join-Path $PSScriptRoot '..\scripts'
. (Join-Path $scripts 'ReviewPayload.Common.ps1')
$script:assertions = 0
$head = '0d66394e940394eaa60a41c22412e19848af3e18'
$requestedAt = [datetimeoffset]'2026-09-21T01:10:00Z'
$fixture = @{}

function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function New-Review([long]$Id, [string]$HeadSha = $head, [datetimeoffset]$At = $requestedAt.AddMinutes(1)) {
    [pscustomobject]@{
        id = $Id; commit_id = $HeadSha; submitted_at = $At.ToString('o')
        state = 'COMMENTED'; user = @{ login = 'copilot-pull-request-reviewer[bot]' }
        body = 'Findings: None'; html_url = "https://github.com/example/PowerToys/pull/860#pullrequestreview-$Id"
    }
}
function Reset-Fixture {
    $fixture.calls = [Collections.Generic.List[string]]::new()
    $fixture.reviews = @(1..100 | ForEach-Object { New-Review $_ -At $requestedAt.AddDays(-1) })
    $fixture.reviews += New-Review 101
    $fixture.liveHead = $head
    $fixture.pending = @()
    $fixture.failEndpoint = ''
    $fixture.malformed = $false
    $fixture.posted = $false
    $fixture.completeOnPost = $false
    $fixture.completeOnSleep = $false
    $fixture.moveOnPost = $false
    $fixture.failAfterPost = $false
    $fixture.postCount = 0
    $fixture.sleepCount = 0
    $fixture.threadFailure = $false
    $fixture.threadStalledCursor = $false
}

# No GitHub writes or waits: exercise the real scripts through a strict gh fake.
function gh {
    $arguments = @($args)
    if ($arguments[0] -ne 'api') { throw "Unexpected gh command: $arguments" }
    $endpoint = [string]$arguments[1]
    $fixture.calls.Add($endpoint)
    $global:LASTEXITCODE = 0
    if ($endpoint -eq 'graphql') {
        $secondPage = $arguments -contains 'cursor=page2'
        if ($fixture.threadFailure -and $secondPage) {
            $global:LASTEXITCODE = 1
            return '{"errors":[{"message":"unavailable"}]}'
        }
        $nodes = if ($secondPage) {
            @(
                @{ isResolved = $false; comments = @{ nodes = @(@{ author = @{ login = 'copilot-pull-request-reviewer' } }) } },
                @{ isResolved = $false; comments = @{ nodes = @(@{ author = @{ login = 'github-advanced-security' } }) } }
            )
        } else {
            @(1..100 | ForEach-Object { @{ isResolved = $true; comments = @{ nodes = @(@{ author = @{ login = 'copilot-pull-request-reviewer' } }) } } })
        }
        return (@{ data = @{ repository = @{ pullRequest = @{ reviewThreads = @{
            nodes = $nodes
            pageInfo = @{ hasNextPage = (-not $secondPage -or $fixture.threadStalledCursor); endCursor = 'page2' }
        } } } } } | ConvertTo-Json -Depth 15 -Compress)
    }
    if ($endpoint -eq $fixture.failEndpoint -or
        ($fixture.failAfterPost -and $fixture.posted -and $endpoint -match 'reviews\?')) {
        $global:LASTEXITCODE = 1
        return '{"message":"API unavailable"}'
    }
    if ($endpoint -eq 'repos/example/PowerToys/pulls/860/requested_reviewers') {
        if ($arguments -notcontains '--method' -or $arguments -notcontains 'POST') {
            throw 'Request must explicitly use POST.'
        }
        $inputPath = $arguments[[array]::IndexOf($arguments, '--input') + 1]
        $payload = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json
        if (@($payload.reviewers).Count -ne 1 -or $payload.reviewers[0] -ne 'copilot-pull-request-reviewer[bot]') {
            throw 'Wrong requested reviewer.'
        }
        $fixture.postCount++
        $fixture.posted = $true
        if ($fixture.completeOnPost) {
            $fixture.reviews += New-Review 102 -At ([datetimeoffset]::UtcNow)
        }
        if ($fixture.moveOnPost) { $fixture.liveHead = 'f' * 40 }
        return '{"requested_reviewers":[]}'
    }
    if ($endpoint -eq 'repos/example/PowerToys/pulls/860') {
        return (@{ head = @{ sha = $fixture.liveHead }; requested_reviewers = $fixture.pending } | ConvertTo-Json -Depth 5 -Compress)
    }
    if ($endpoint -match '^repos/example/PowerToys/pulls/860/reviews\?per_page=100&page=(\d+)$') {
        if ($fixture.malformed) { return 'not-json' }
        $page = [int]$Matches[1]
        $batch = @($fixture.reviews | Select-Object -Skip (($page - 1) * 100) -First 100)
        return (ConvertTo-Json -InputObject $batch -Depth 5 -Compress)
    }
    throw "Unexpected/unpaginated API call: $endpoint"
}
function Start-Sleep {
    param([double]$Seconds)
    $fixture.sleepCount++
    if (-not $fixture.completeOnSleep -or $fixture.sleepCount -gt 1) { throw 'Unexpected polling wait' }
    $fixture.reviews += New-Review 102 -At ([datetimeoffset]::UtcNow)
}
function Read-Status {
    & (Join-Path $scripts 'Get-CopilotReviewStatus.ps1') -ForkRepo example/PowerToys `
        -PRNumber 860 -HeadSha $head -RequestedAt $requestedAt
}
function Send-Request([int]$Timeout = 0) {
    & (Join-Path $scripts 'Request-CopilotReview.ps1') -ForkRepo example/PowerToys `
        -PRNumber 860 -TimeoutMinutes $Timeout 6>$null 3>$null
}
function Assert-Fails([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    $failure = ''
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-True ($failure -match $Pattern) "$Message (got '$failure')"
}

$savedExitCode = $global:LASTEXITCODE
try {
    Reset-Fixture
    $status = Read-Status
    Assert-True ($status.Submitted -and $status.ReviewId -eq 101) 'Review beyond page 1 is discovered.'
    Assert-True ($status.ReviewsScanned -eq 101 -and $fixture.calls -contains 'repos/example/PowerToys/pulls/860/reviews?per_page=100&page=2') 'All pages are consumed.'
    Assert-True ($fixture.postCount -eq 0 -and $fixture.sleepCount -eq 0) 'Resume is strictly read-only and immediate.'
    Assert-True ($status.ReviewCommitSha -ceq $head -and $status.ReviewBody -eq 'Findings: None') 'Result retains head and review evidence, not just a boolean.'

    Reset-Fixture
    $fixture.reviews = @($fixture.reviews | Select-Object -First 38) + @(New-Review 39)
    $status = Read-Status
    Assert-True ($status.Submitted -and $status.ReviewId -eq 39) '50663 regression: review 39 is not hidden behind default page of 30.'

    Reset-Fixture
    $fixture.reviews += New-Review 102 -HeadSha ('f' * 40) -At $requestedAt.AddHours(1)
    $status = Read-Status
    Assert-True ($status.ReviewId -eq 101) 'Newer review on a different head cannot replace matching-head evidence.'
    $fixture.liveHead = 'f' * 40
    $status = Read-Status
    Assert-True (-not $status.Submitted -and -not $status.HeadMatches) 'A moved live head cannot be marked reviewed.'

    Reset-Fixture
    $fixture.reviews = @(New-Review 101 -At $requestedAt)
    Assert-True ((Read-Status).Submitted) 'GitHub same-second timestamp is accepted.'
    $status = & (Join-Path $scripts 'Get-CopilotReviewStatus.ps1') -ForkRepo example/PowerToys -PRNumber 860 -HeadSha $head -RequestedAt $requestedAt -AfterReviewId 101
    Assert-True (-not $status.Submitted) 'Baseline review ID excludes a pre-existing same-second review.'
    $fixture.reviews[0].submitted_at = $requestedAt.AddSeconds(-1).ToString('o')
    Assert-True (-not (Read-Status).Submitted) 'Reviews preceding request are excluded.'
    $fixture.reviews[0].submitted_at = $requestedAt.AddMinutes(1).ToString('o')
    foreach ($state in @('PENDING', 'DISMISSED')) {
        $fixture.reviews[0].state = $state
        Assert-True (-not (Read-Status).Submitted) "$state is not a completed review."
    }
    $fixture.reviews[0].state = 'COMMENTED'
    $fixture.reviews[0].user.login = 'some-human'
    Assert-True (-not (Read-Status).Submitted) 'Human review cannot satisfy Copilot wait.'
    $fixture.reviews[0].user.login = 'copilot-pull-request-reviewer'
    Assert-True ((Read-Status).Submitted) 'Recognized non-bot-suffixed Copilot login is accepted.'

    Reset-Fixture
    $fixture.reviews = @()
    $fixture.pending = @(@{ login = 'copilot-pull-request-reviewer[bot]' })
    $status = Read-Status
    Assert-True (-not $status.Submitted -and $status.RequestPending -and $status.ReviewsScanned -eq 0) 'Empty review list preserves genuine pending state.'
    Assert-Fails { Send-Request } 'already pending' 'Duplicate requests are rejected.'
    Assert-True ($fixture.postCount -eq 0) 'Pending request did not trigger a POST.'

    Reset-Fixture
    $fixture.reviews = @(1..100 | ForEach-Object { New-Review $_ })
    $status = Read-Status
    Assert-True ($status.ReviewId -eq 100 -and $status.ReviewsScanned -eq 100) 'Exactly full page reads the empty next page and resolves timestamp ties by ID.'
    foreach ($endpoint in @('repos/example/PowerToys/pulls/860', 'repos/example/PowerToys/pulls/860/reviews?per_page=100&page=2')) {
        Reset-Fixture
        $fixture.failEndpoint = $endpoint
        Assert-Fails { Read-Status } 'GitHub API request failed' 'API failure is not a successful empty/pending result.'
    }
    Reset-Fixture
    $fixture.malformed = $true
    Assert-Fails { Read-Status } 'JSON' 'Malformed JSON fails explicitly.'

    Reset-Fixture
    $fixture.completeOnPost = $true
    $result = Send-Request
    Assert-True ($result.Submitted -and $result.ReviewId -eq 102) 'Fast completion with empty requested_reviewers is discovered immediately.'
    Assert-True ($result.AfterReviewId -eq 101 -and $null -ne $result.RequestedAt -and $result.HeadSha -eq $head) 'Request returns a resumable identity.'
    Assert-True ($fixture.postCount -eq 1 -and $fixture.sleepCount -eq 0) 'Timeout 0 performs one immediate check without waiting.'

    Reset-Fixture
    $result = Send-Request
    Assert-True (-not $result.Submitted -and $fixture.postCount -eq 1 -and $fixture.sleepCount -eq 0) 'Bounded no-result check returns resume state without polling.'
    Reset-Fixture
    $fixture.completeOnSleep = $true
    $result = Send-Request 1
    Assert-True ($result.Submitted -and $fixture.sleepCount -eq 1 -and $fixture.postCount -eq 1) 'Subsequent poll also uses complete pagination.'
    Reset-Fixture
    $fixture.failEndpoint = 'repos/example/PowerToys/pulls/860/requested_reviewers'
    Assert-Fails { Send-Request } 'GitHub API request failed: POST' 'POST failure is not reported as a review timeout.'
    Reset-Fixture
    $fixture.failAfterPost = $true
    Assert-Fails { Send-Request } 'GitHub API request failed' 'Polling failure is not reported as a review timeout.'
    Reset-Fixture
    $fixture.moveOnPost = $true
    Assert-Fails { Send-Request } 'Fork head moved' 'Head movement during a request requires reconciliation.'

    Reset-Fixture
    $count = & (Join-Path $scripts 'Get-UnresolvedCopilotThreads.ps1') -ForkOwner example -PRNumber 860
    Assert-True ($count -eq 1) 'Unresolved Copilot thread beyond 100 is counted; other bots are not Copilot.'
    $fixture.threadFailure = $true
    Assert-Fails { & (Join-Path $scripts 'Get-UnresolvedCopilotThreads.ps1') -ForkOwner example -PRNumber 860 } 'Cannot read review threads' 'Thread page failure must not look like zero unresolved threads.'
    $fixture.threadFailure = $false
    $fixture.threadStalledCursor = $true
    Assert-Fails { & (Join-Path $scripts 'Get-UnresolvedCopilotThreads.ps1') -ForkOwner example -PRNumber 860 } 'pagination did not advance' 'Repeated thread cursor fails instead of looping.'
} finally {
    $global:LASTEXITCODE = $savedExitCode
}
Write-Host "Copilot review discovery tests passed ($script:assertions assertions)."
