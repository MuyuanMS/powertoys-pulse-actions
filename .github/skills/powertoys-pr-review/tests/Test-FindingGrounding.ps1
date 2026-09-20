$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\FindingGrounding.Common.ps1')
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Copy-Object($Value) { $Value | ConvertTo-Json -Depth 30 | ConvertFrom-Json }

$head = '0123456789abcdef0123456789abcdef01234567'
$source = "void Hook() {`n    if (altPressed) Navigate();`n    CallNextHookEx();`n}"
$item = [pscustomobject]@{ id = 'hook'; body = 'An upstream-grounded finding.' }
$grounding = [pscustomobject]@{
    version = 1
    head_sha = $head
    findings = @([pscustomobject]@{
        id = $item.id
        origin = 'upstream'
        body_sha256 = Get-FindingBodyHash $item.body
        claim = 'A specific upstream defect.'
        upstream_failure = 'A traced failure in the current upstream path.'
        counterevidence = 'The hook already requires altPressed and forwards unhandled keys.'
        why_not_already_fixed = 'The asserted defect is independent of that existing guard.'
        verification = 'Source trace only, not an interactive test.'
        sources = @([pscustomobject]@{
            path = 'src/Hook.cs'; start_line = 1; end_line = 4
            excerpt = $source; symbols = @('altPressed', 'CallNextHookEx')
        })
    })
}
$reader = { param($sha, $path) $source }.GetNewClosure()
function Check($Evidence, $Items = @($item)) {
    @(Test-FindingGrounding -Items $Items -Grounding $Evidence -HeadSha $head -CheckSources -SourceReader $reader)
}
Assert-True (@(Check $grounding).Count -eq 0) 'Pinned excerpts and body hash pass.'
Assert-True ((Get-FindingBodyHash "a`r`nb") -ceq (Get-FindingBodyHash "a`nb")) 'Body hashes normalize CRLF.'
Assert-True (@(Test-FindingGrounding -Items @() -Grounding $null -HeadSha $head).Count -eq 0) 'Clean review requires no invented finding.'
Assert-True ((@(Check $null) -join "`n") -match 'requires version 1') 'Missing evidence fails closed.'
foreach ($field in @('claim', 'upstream_failure', 'counterevidence', 'why_not_already_fixed', 'verification')) {
    $bad = Copy-Object $grounding
    $bad.findings[0].$field = ''
    Assert-True ((@(Check $bad) -join "`n") -match "missing $field") "Missing $field fails."
}
foreach ($origin in @('review_introduced', 'already_fixed', 'optional', 'unsupported')) {
    $bad = Copy-Object $grounding
    $bad.findings[0].origin = $origin
    Assert-True ((@(Check $bad) -join "`n") -match 'originated outside upstream') "$origin cannot be published."
}
$bad = Copy-Object $grounding
$bad.head_sha = 'f' * 40
Assert-True ((@(Check $bad) -join "`n") -match 'exact 40-character') 'Evidence cannot transfer across heads.'
$bad = Copy-Object $grounding
$bad.findings[0].sources[0].excerpt = "void ClearInitialFocus() {`n    FocusManager.GetFocusedElement(XamlRoot);`n}"
$bad.findings[0].sources[0].symbols = @('ClearInitialFocus', 'FocusManager')
Assert-True ((@(Check $bad) -join "`n") -match 'does not match pinned upstream') '50472 fork-only excerpt cannot be attached to upstream SHA.'
$bad = Copy-Object $grounding
$bad.findings[0].sources[0].symbols = @('ClearInitialFocus', 'FocusManager')
Assert-True ((@(Check $bad) -join "`n") -match 'is absent') '50472 absent API claims fail the symbol check.'
$rewritten = Copy-Object $item
$rewritten.body += ' Expanded lifecycle claim.'
Assert-True ((@(Check $grounding @($rewritten)) -join "`n") -match 'body changed') 'Prose expansion requires revalidation even with unchanged head.'
foreach ($mode in @('missing', 'extra', 'duplicate', 'renamed')) {
    $bad = Copy-Object $grounding
    switch ($mode) {
        missing { $bad.findings = @() }
        extra { $extra = Copy-Object $bad.findings[0]; $extra.id = 'extra'; $bad.findings += $extra }
        duplicate { $bad.findings += $bad.findings[0] }
        renamed { $bad.findings[0].id = 'other' }
    }
    Assert-True ((@(Check $bad) -join "`n") -match 'IDs must match') "$mode evidence IDs fail."
}
Assert-True ((@(Check $grounding @($item, $item)) -join "`n") -match 'IDs must match') 'Duplicate item IDs fail.'
$bad = Copy-Object $grounding
$bad.findings[0].sources[0].end_line = 99
Assert-True ((@(Check $bad) -join "`n") -match 'does not match') 'Out-of-bounds excerpt fails.'
$bad.findings[0].sources[0].start_line = 0
Assert-True ((@(Check $bad) -join "`n") -match 'invalid source line range') 'Invalid ranges fail.'
foreach ($path in @('../Hook.cs', '/Hook.cs', 'C:\Hook.cs', 'src\Hook.cs', 'src//Hook.cs')) {
    $bad = Copy-Object $grounding
    $bad.findings[0].sources[0].path = $path
    Assert-True ((@(Check $bad) -join "`n") -match 'invalid repository-relative') "Unsafe path $path fails."
}
$errors = @(Test-FindingGrounding -Items @($item) -Grounding $grounding -HeadSha $head -CheckSources -SourceReader { throw 'unavailable' })
Assert-True (($errors -join "`n") -match 'source verification failed: unavailable') 'Read/API failures fail closed.'
$bad = Copy-Object $grounding
$bad.findings[0].sources = @()
Assert-True ((@(Check $bad) -join "`n") -match 'at least one') 'Empty sources fail.'

# Exercise the processed-artifact gate against real git objects, not the worktree.
$temp = Join-Path ([IO.Path]::GetTempPath()) ("grounding-tests-" + [guid]::NewGuid())
$validator = Join-Path $PSScriptRoot '..\..\powertoys-dashboard-update\scripts\Test-DashboardArtifacts.ps1'
try {
    New-Item -ItemType Directory -Path (Join-Path $temp 'src'), (Join-Path $temp 'data\items') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $temp 'src\Hook.cs'), $source)
    & git -C $temp init --quiet
    if ($LASTEXITCODE) { throw 'Test git init failed' }
    & git -C $temp -c core.autocrlf=false add src/Hook.cs
    if ($LASTEXITCODE) { throw 'Test git add failed' }
    & git -C $temp -c user.name=Test -c user.email=test@example.invalid -c commit.gpgsign=false commit --quiet -m fixture
    if ($LASTEXITCODE) { throw 'Test git commit failed' }
    $realHead = (& git -C $temp rev-parse HEAD).Trim()
    [IO.File]::WriteAllText((Join-Path $temp 'src\Hook.cs'), 'Divergent working tree with ClearInitialFocus')
    $evidence = Copy-Object $grounding
    $evidence.head_sha = $realHead
    $artifact = @{
        number = 50472; kind = 'pr'; track = 'review'; head_sha = $realHead
        generated_at = '2026-09-19T00:00:00Z'; evaluated_at = '2026-09-19T00:00:00Z'; source_updated_at = '2026-09-19T00:00:00Z'
        stage = 'awaiting_review_approval'
        proposed_comments = @(@{ id = $item.id; kind = 'inline'; in_diff = $true; path = 'src/Hook.cs'; line = 2; side = 'RIGHT'; body = $item.body })
        actions = @(@{ id = 'post'; type = 'post_review'; label = 'Post review comments'; review = @{ head_sha = $realHead; event = 'COMMENT' } })
    }
    $artifactPath = Join-Path $temp 'data\items\50472.json'
    $dossierPath = Join-Path $temp 'private-grounding.json'
    $artifact | ConvertTo-Json -Depth 30 | Set-Content $artifactPath
    @{ prs = @(@{ number = 50472; findingGrounding = $evidence }) } | ConvertTo-Json -Depth 30 | Set-Content $dossierPath
    $output = & pwsh -NoProfile -File $validator -Dashboard $temp -Numbers 50472 -RequireFindingGrounding -GroundingPath $dossierPath -SourceRepository $temp 2>&1
    Assert-True ($LASTEXITCODE -eq 0) "Real git source passes despite divergent worktree: $output"
    $output = & pwsh -NoProfile -File $validator -Dashboard $temp -Numbers 50472 -RequireFindingGrounding -SourceRepository $temp 2>&1
    Assert-True ($LASTEXITCODE -ne 0 -and "$output" -match 'exactly one private grounding record') 'Processed proposals require dossier; missing disposition is still proposed.'
    $artifact.actions[0].review.body_prefix = 'An additional overall claim.'
    $artifact | ConvertTo-Json -Depth 30 | Set-Content $artifactPath
    $output = & pwsh -NoProfile -File $validator -Dashboard $temp -Numbers 50472 -RequireFindingGrounding -GroundingPath $dossierPath -SourceRepository $temp 2>&1
    Assert-True ($LASTEXITCODE -ne 0 -and "$output" -match 'IDs must match') 'Review bodies cannot bypass grounding.'
    $bodyEvidence = Copy-Object $evidence.findings[0]
    $bodyEvidence.id = 'review-body:post'
    $bodyEvidence.body_sha256 = Get-FindingBodyHash $artifact.actions[0].review.body_prefix
    $evidence.findings += $bodyEvidence
    @{ prs = @(@{ number = 50472; findingGrounding = $evidence }) } | ConvertTo-Json -Depth 30 | Set-Content $dossierPath
    # Inline-only reviews cannot have body prefixes; use a body-only review for this gate.
    $artifact.proposed_comments = @()
    $evidence.findings = @($bodyEvidence)
    $artifact | ConvertTo-Json -Depth 30 | Set-Content $artifactPath
    @{ prs = @(@{ number = 50472; findingGrounding = $evidence }) } | ConvertTo-Json -Depth 30 | Set-Content $dossierPath
    $output = & pwsh -NoProfile -File $validator -Dashboard $temp -Numbers 50472 -RequireFindingGrounding -GroundingPath $dossierPath -SourceRepository $temp 2>&1
    Assert-True ($LASTEXITCODE -eq 0) "Body-only review passes with its own grounding: $output"
    $artifact.proposed_comments = @(); $artifact.actions = @(); $artifact.stage = 'review_ready'
    $artifact.validation = @{ upstream_head = @{ head_sha = $realHead; result = 'passed' } }
    $artifact | ConvertTo-Json -Depth 30 | Set-Content $artifactPath
    $output = & pwsh -NoProfile -File $validator -Dashboard $temp -Numbers 50472 -RequireFindingGrounding -SourceRepository $temp 2>&1
    Assert-True ($LASTEXITCODE -eq 0) "Clean exact-head review requires no findings dossier: $output"
    $output = & pwsh -NoProfile -File $validator -Dashboard $temp -RequireFindingGrounding 2>&1
    Assert-True ($LASTEXITCODE -ne 0 -and "$output" -match 'explicit processed') 'Strict gate cannot accidentally invalidate entire legacy feed.'
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
Write-Host "Finding grounding tests passed ($script:assertions assertions)."
