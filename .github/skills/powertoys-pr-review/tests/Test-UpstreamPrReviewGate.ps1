$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot '..\scripts\Get-UpstreamPrReviewGate.ps1'
$head = 'a' * 40
$script:assertions = 0
$fixture = @{}
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Reset-Fixture {
    $fixture.pr = @{ state = 'open'; draft = $false; merged = $false; head = @{ sha = $head } }
    $fixture.failure = $false
    $fixture.raw = $null
    $fixture.calls = 0
}
function gh {
    $fixture.calls++
    $arguments = @($args)
    if ($arguments.Count -ne 2 -or $arguments[0] -ne 'api' -or
        $arguments[1] -ne 'repos/microsoft/PowerToys/pulls/123') {
        throw "Unexpected API access: $arguments"
    }
    $global:LASTEXITCODE = if ($fixture.failure) { 1 } else { 0 }
    if ($fixture.failure) { return 'API unavailable' }
    if ($null -ne $fixture.raw) { return $fixture.raw }
    return $fixture.pr | ConvertTo-Json -Depth 5
}
function Read-Gate {
    & $gate -PRNumber 123 -HeadSha $head
}
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    $failure = ''
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-True ($failure -match $Pattern) "Expected $Pattern, got '$failure'"
}
$savedExitCode = $global:LASTEXITCODE
try {
    Reset-Fixture
    $result = Read-Gate
    Assert-True ($result.action -eq 'continue_review' -and $result.head_matches) 'Open unchanged original PR continues.'
    Assert-True ($fixture.calls -eq 1 -and $result.checked_at) 'Quick check performs one read and records its observation time.'
    $fixture.pr.head.sha = 'b' * 40
    $result = Read-Gate
    Assert-True ($result.action -eq 'reconcile_updated_head' -and -not $result.head_matches) 'New upstream commits invalidate the pinned version.'
    Assert-True ($result.live_head_sha -eq ('b' * 40) -and $result.reviewed_head_sha -eq $head) 'Changed head result preserves both identities.'
    $fixture.pr.draft = $true
    Assert-True ((Read-Gate).action -eq 'stop_review') 'Conversion to draft stops even when new commits exist.'
    $fixture.pr.draft = $false
    $fixture.pr.state = 'closed'
    Assert-True ((Read-Gate).reason -eq 'closed') 'Closed original PR stops.'
    $fixture.pr.merged = $true
    Assert-True ((Read-Gate).reason -eq 'merged') 'Merged original PR stops.'
    Reset-Fixture
    $json = & $gate -PRNumber 123 -HeadSha $head -AsJson | ConvertFrom-Json
    Assert-True ($json.action -eq 'continue_review' -and $json.number -eq 123) 'JSON output preserves the gate decision.'
    $fixture.failure = $true
    Assert-Fails { Read-Gate } 'GitHub API request failed'
    Reset-Fixture
    $fixture.raw = 'not-json'
    Assert-Fails { Read-Gate } 'JSON'
    foreach ($raw in @('', '{}', '{"state":"open","draft":false,"merged":false,"head":{"sha":"invalid"}}')) {
        Reset-Fixture
        $fixture.raw = $raw
        Assert-Fails { Read-Gate } 'Incomplete or invalid'
    }
    foreach ($field in @('draft', 'merged', 'state')) {
        Reset-Fixture
        $fixture.pr.Remove($field)
        Assert-Fails { Read-Gate } 'Incomplete or invalid'
    }
    Reset-Fixture
    $fixture.pr.draft = 'false'
    Assert-Fails { Read-Gate } 'Incomplete or invalid'
    Reset-Fixture
    Read-Gate | Out-Null
    $fixture.pr.draft = $true
    Assert-True ((Read-Gate).action -eq 'stop_review') 'A later round rechecks upstream rather than reusing the first observation.'
} finally {
    $global:LASTEXITCODE = $savedExitCode
}
Write-Host "Upstream PR review gate tests passed ($script:assertions assertions)."
