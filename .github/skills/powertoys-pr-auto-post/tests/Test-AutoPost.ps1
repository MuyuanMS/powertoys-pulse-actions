$ErrorActionPreference = 'Stop'
$skill = Split-Path -Parent $PSScriptRoot
. (Join-Path $skill 'scripts\AutoPost.Common.ps1')
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Assert-True ($message -match $Pattern) "Expected '$Pattern', got '$message'"
}
function Copy-Object($Value) { $Value | ConvertTo-Json -Depth 40 | ConvertFrom-Json }
$head = '0123456789abcdef0123456789abcdef01234567'
$hash = 'a' * 64
$body = @'
### Preserve the existing value

**Severity:** `medium`

**Affected code:** `src/Test.cs` - `Existing`.

**Problem:** Changing the persisted value renumbers saved data.

**Why it matters:** Previously saved values change meaning on upgrade.

**Suggested change:**
1. Keep the existing numeric assignment.
2. Add a regression test for persisted values.

**Verification:** Deserialize the old value and assert that its meaning is preserved.
'@
$comment = [pscustomobject]@{ id = 'preserve-value'; kind = 'companion'; in_diff = $false; disposition = 'proposed';
    severity = 'medium'; title = 'Preserve the existing value'; body = $body;
    out_of_diff_reason = 'The value declaration is outside the changed diff.' }
$artifact = [pscustomobject]@{ number = 42; kind = 'pr'; track = 'review'; head_sha = $head;
    evaluated_at = [DateTimeOffset]::UtcNow.AddDays(-1).ToString('o'); pending_author = $false; needs_revalidation = $false;
    proposed_comments = @($comment); actions = @([pscustomobject]@{ type = 'post_review'; review = [pscustomobject]@{ event = 'COMMENT' } }) }
$dimensions = [ordered]@{}
foreach ($dimension in $script:AutoPostWeights.Keys) { $dimensions[$dimension] = [pscustomobject]@{ score = 95; rationale = "Observed $dimension evidence." } }
$rating = [pscustomobject]@{ id = $comment.id; verdict = 'supported'; dimensions = [pscustomobject]$dimensions;
    hard_gates = [pscustomobject]@{ discussion_checked = $true; no_unanswered_rebuttal = $true; not_already_addressed = $true; validation_verified = $true } }
$snapshot = [pscustomobject]@{ headSha = $head; updatedAt = '2026-11-26T00:00:00.0000000+00:00'; issueCommentCount = 0; reviewCommentCount = 0; reviewCount = 0 }
$item = [pscustomobject]@{ id = $comment.id; kind = 'companion'; title = $comment.title; severity = $comment.severity;
    body = $body; confidence = [pscustomobject]@{ score = 95; rationale = 'Saved numeric assignments prove the failure.' };
    outOfDiffReason = $comment.out_of_diff_reason }
$grounding = [pscustomobject]@{ version = 1; head_sha = $head; findings = @([pscustomobject]@{
    id = $comment.id; origin = 'upstream'; body_sha256 = Get-FindingBodyHash $body;
    claim = 'Saved values change meaning.'; upstream_failure = 'Existing was renumbered from 1 to 2.';
    counterevidence = 'No migration remaps old data.'; why_not_already_fixed = 'The consumer still reads persisted numeric values.';
    verification = 'A source-level comparison confirms the changed assignment.';
    sources = @([pscustomobject]@{ path = 'src/Test.cs'; start_line = 1; end_line = 1; excerpt = 'Existing = 2,'; symbols = @('Existing') })
}) }
$assessment = [pscustomobject]@{ schemaVersion = 1; number = 42; head_sha = $head; artifact_sha256 = $hash;
    evaluated_at = [DateTimeOffset]::UtcNow.ToString('o'); snapshot = $snapshot; items = @($rating);
    reviewData = [pscustomobject]@{ schemaVersion = 2; repository = 'microsoft/PowerToys'; prs = @([pscustomobject]@{
        number = 42; headSha = $head; phase = 'ready'; snapshot = $snapshot;
        publicPayload = [pscustomobject]@{ contextBody = ''; items = @($item) };
        internalEvidence = [pscustomobject]@{ validation = [pscustomobject]@{ findingGrounding = $grounding } }
    }) } }
Assert-AutoPostArtifact $artifact $assessment $hash
$results = @(Get-AutoPostSelection $artifact $assessment)
Assert-True ($results.Count -eq 1 -and $results[0].selected -and $results[0].score -eq 95) 'Supported evidence selects high-score proposal.'
$bad = Copy-Object $assessment
$bad.items[0].dimensions.correctness.score = 84
Assert-True (-not @(Get-AutoPostSelection $artifact $bad)[0].selected) 'Strong average cannot hide weak correctness.'
$bad = Copy-Object $assessment
$bad.items[0].dimensions.risk.score = 89
Assert-True (-not @(Get-AutoPostSelection $artifact $bad)[0].selected) 'Risk has a stricter floor.'
$bad = Copy-Object $assessment
$bad.items[0].hard_gates.validation_verified = $false
Assert-True (-not @(Get-AutoPostSelection $artifact $bad)[0].selected) 'Missing verified evidence defeats high scores.'
$bad = Copy-Object $assessment
$bad.items += Copy-Object $rating
Assert-Fails { Get-AutoPostSelection $artifact $bad } 'exactly once'
$bad = Copy-Object $assessment
$bad.items[0].dimensions.context.rationale = ''
Assert-Fails { Get-AutoPostSelection $artifact $bad } 'rationale'
$bad = Copy-Object $assessment
$bad.artifact_sha256 = 'b' * 64
Assert-Fails { Assert-AutoPostArtifact $artifact $bad $hash } 'exact artifact'
$bad = Copy-Object $assessment
$bad.evaluated_at = [DateTimeOffset]::UtcNow.AddHours(-3).ToString('o')
Assert-Fails { Assert-AutoPostArtifact $artifact $bad $hash } 'this run'
$badArtifact = Copy-Object $artifact
$badArtifact.actions[0].review.event = 'REQUEST_CHANGES'
Assert-Fails { Assert-AutoPostArtifact $badArtifact $assessment $hash } 'COMMENT'
$badArtifact = Copy-Object $artifact
$badArtifact.pending_author = $true
Assert-Fails { Assert-AutoPostArtifact $badArtifact $assessment $hash } 'eligible'
$live = [pscustomobject]@{ state = 'open'; draft = $false; merged = $false; user = [pscustomobject]@{ login = 'author' }; head = [pscustomobject]@{ sha = $head } }
Assert-AutoPostLive $artifact $assessment $live 'poster' $snapshot
$badLive = Copy-Object $live
$badLive.head.sha = 'b' * 40
Assert-Fails { Assert-AutoPostLive $artifact $assessment $badLive 'poster' $snapshot } 'head changed'
$badLive = Copy-Object $live
$badLive.draft = $true
Assert-Fails { Assert-AutoPostLive $artifact $assessment $badLive 'poster' $snapshot } 'draft'
Assert-Fails { Assert-AutoPostLive $artifact $assessment $live 'author' $snapshot } 'Self-authored'
$newSnapshot = Copy-Object $snapshot
$newSnapshot.issueCommentCount = 1
Assert-Fails { Assert-AutoPostLive $artifact $assessment $live 'poster' $newSnapshot } 'reassess, do not invalidate'
$marker = Get-AutoPostMarker 42 $head $comment.id
Assert-True (Test-AutoPostDuplicate 42 $head $comment @("$body`n`n$marker")) 'Existing Pulse marker prevents repost.'
Assert-True (Test-AutoPostDuplicate 42 $head $comment @($body.Replace("`r`n", "`n").Replace("`n", "`r`n"))) 'Unmarked equivalent body also prevents repost.'
Assert-True (Test-AutoPostDuplicate 42 $head $comment @("Other finding`n`n---`n`n$body")) 'General finding in combined review is detected.'
Assert-True (-not (Test-AutoPostDuplicate 42 $head $comment @('Different finding'))) 'Distinct finding is not suppressed.'
$data = New-AutoPostReviewData $artifact $assessment $snapshot @($comment.id)
$errors = @(Test-ReviewDataDocument $data -RequireFindingGrounding)
Assert-True ($errors.Count -eq 0) "Grounded selected payload validates: $($errors -join '; ')"
$bad = Copy-Object $assessment
$bad.reviewData.prs[0].publicPayload.items[0].body += ' Rewritten claim.'
Assert-Fails { New-AutoPostReviewData $artifact $bad $snapshot @($comment.id) } 'rewrites'
$bad = Copy-Object $assessment
$bad.reviewData.prs[0].internalEvidence.validation.findingGrounding = $null
Assert-Fails { New-AutoPostReviewData $artifact $bad $snapshot @($comment.id) } 'grounding'

$groupArtifact = Copy-Object $artifact
$groupAssessment = Copy-Object $assessment
$groupArtifact.proposed_comments = @()
$groupAssessment.items = @()
foreach ($position in 1..2) {
    $member = Copy-Object $comment
    $member.id = "group-$position"; $member.in_diff = $true; $member.body = "Fix member $position`n``````suggestion`nvalue`n``````"
    $member | Add-Member selection_group ([pscustomobject]@{ id = 'group'; title = 'Both fixes'; total = 2; position = $position; atomic = $true })
    $groupArtifact.proposed_comments += $member
    $memberRating = Copy-Object $rating
    $memberRating.id = $member.id
    $groupAssessment.items += $memberRating
}
$groupAssessment.items[1].dimensions.validation.score = 70
Assert-True (@(Get-AutoPostSelection $groupArtifact $groupAssessment | Where-Object selected).Count -eq 0) 'A held member holds complete atomic group.'
$groupArtifact.proposed_comments = @($groupArtifact.proposed_comments[0])
$groupAssessment.items = @($groupAssessment.items[0])
Assert-Fails { Get-AutoPostSelection $groupArtifact $groupAssessment } 'Incomplete atomic'

$inlineArtifact = Copy-Object $artifact
$inlineAssessment = Copy-Object $assessment
$inlineComment = $inlineArtifact.proposed_comments[0]
$inlineComment.in_diff = $true; $inlineComment.kind = 'inline'
$inlineComment.body = "### Preserve the existing value`n`n**Severity:** ``medium```n`nPreserve the persisted numeric assignment.`n`n``````suggestion`nExisting = 1,`n``````"
$inlineComment | Add-Member path 'src/Test.cs'
$inlineComment | Add-Member line 1
$inlineComment | Add-Member side 'RIGHT'
$inlineItem = $inlineAssessment.reviewData.prs[0].publicPayload.items[0]
$inlineItem.kind = 'inline'; $inlineItem.body = $inlineComment.body
$inlineItem.PSObject.Properties.Remove('outOfDiffReason')
$inlineItem | Add-Member path 'src/Test.cs'
$inlineItem | Add-Member line 1
$inlineItem | Add-Member side 'RIGHT'
$inlineValidation = $inlineAssessment.reviewData.prs[0].internalEvidence.validation
$inlineValidation.findingGrounding.findings[0].body_sha256 = Get-FindingBodyHash $inlineItem.body
$inlineValidation | Add-Member suggestionPatch ([pscustomobject]@{ headSha = $head; result = 'passed';
    commands = @('trusted focused check'); minimalRangesReviewed = $true; appliedItemIds = @($comment.id, 'unselected-fix') })
$inlineValidation | Add-Member lineEndingSafety ([pscustomobject]@{ headSha = $head; result = 'passed'; checkedItemIds = @($comment.id);
    files = @([pscustomobject]@{ path = 'src/Test.cs'; result = 'passed'; lineEndings = 'lf' }) })
Assert-Fails { New-AutoPostReviewData $inlineArtifact $inlineAssessment $snapshot @($comment.id) } 'larger candidate patch'
$inlineValidation.suggestionPatch.appliedItemIds = @($comment.id)
$inlineData = New-AutoPostReviewData $inlineArtifact $inlineAssessment $snapshot @($comment.id)
$errors = @(Test-ReviewDataDocument $inlineData -RequireFindingGrounding)
Assert-True ($errors.Count -eq 0) "Exact selected suggestion receipt validates: $($errors -join '; ')"
$inlineValidation.suggestionPatch.result = 'failed'
$inlineData = New-AutoPostReviewData $inlineArtifact $inlineAssessment $snapshot @($comment.id)
Assert-True ((@(Test-ReviewDataDocument $inlineData -RequireFindingGrounding) -join ';') -match "result must be 'passed'") 'Failed literal patch cannot pass existing publisher gate.'

# Integration uses a private temporary canonical fixture and a mocked GitHub API.
$root = Join-Path ([IO.Path]::GetTempPath()) "pulse-auto-post-$([guid]::NewGuid().ToString('N'))"
$dashboard = Join-Path $root 'source'
$stateRoot = Join-Path $root 'state'
$assessmentPath = Join-Path $root 'assessment.json'
$global:AutoPostTestState = @{ writes = 0; remoteReview = $null; apiFailure = $false; closedBeforeSubmit = $false }
function gh {
    $arguments = @($args)
    if ($arguments[0] -ne 'api') { throw 'Unexpected mock gh call.' }
    $endpoint = ([string]$arguments[1] -split '\?')[0]
    $global:LASTEXITCODE = 0
    if ($global:AutoPostTestState.apiFailure) { $global:LASTEXITCODE = 1; return }
    $methodIndex = [Array]::IndexOf($arguments, '--method')
    $method = if ($methodIndex -ge 0) { $arguments[$methodIndex + 1] } else { 'GET' }
    if ($method -ne 'GET') {
        $global:AutoPostTestState.writes++
        $inputIndex = [Array]::IndexOf($arguments, '--input')
        $payload = Read-JsonFile $arguments[$inputIndex + 1]
        if ($endpoint -eq 'repos/microsoft/PowerToys/pulls/42/reviews' -and $method -eq 'POST') {
            $global:AutoPostTestState.remoteReview = [pscustomobject]@{ id = 99; state = 'PENDING'; commit_id = $payload.commit_id; body = $payload.body;
                html_url = 'https://github.com/microsoft/PowerToys/pull/42#pullrequestreview-99'; user = [pscustomobject]@{ login = 'poster' } }
            $global:AutoPostTestState.remoteReview | ConvertTo-Json -Depth 10; return
        }
        if ($endpoint -eq 'repos/microsoft/PowerToys/pulls/42/reviews/99/events') {
            if ($payload.event -cne 'COMMENT') { throw 'Wrong event.' }
            $global:AutoPostTestState.remoteReview.state = 'COMMENTED'
            $global:AutoPostTestState.remoteReview | ConvertTo-Json -Depth 10; return
        }
        throw "Unexpected mock write: $method $endpoint"
    }
    switch -Regex ($endpoint) {
        '^user$' { '{"login":"poster"}'; return }
        '^repos/microsoft/PowerToys/pulls/42$' {
            $result = Copy-Object $live
            $result | Add-Member updated_at $snapshot.updatedAt
            if ($global:AutoPostTestState.closedBeforeSubmit -and $null -ne $global:AutoPostTestState.remoteReview) { $result.state = 'closed' }
            $result | ConvertTo-Json -Depth 10; return
        }
        '^repos/microsoft/PowerToys/pulls/42/reviews/99$' { $global:AutoPostTestState.remoteReview | ConvertTo-Json -Depth 10; return }
        '^repos/microsoft/PowerToys/pulls/42/reviews$' {
            if ($null -eq $global:AutoPostTestState.remoteReview) { '[]' } else { ConvertTo-Json -InputObject @($global:AutoPostTestState.remoteReview) -Depth 10 }; return
        }
        '^repos/microsoft/PowerToys/(issues/42/comments|pulls/42/comments|pulls/42/files|pulls/42/reviews/99/comments)$' { '[]'; return }
        '^repos/microsoft/PowerToys/contents/src/Test.cs$' {
            [pscustomobject]@{ encoding = 'base64'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('Existing = 2,')) } | ConvertTo-Json; return
        }
        default { throw "Unexpected mock GET $endpoint" }
    }
}
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $dashboard 'data\items') | Out-Null
    & git -C $dashboard init --quiet --initial-branch=main
    if ($LASTEXITCODE -ne 0) { throw 'Fixture git init failed.' }
    & git -C $dashboard remote add origin https://github.com/MuyuanMS/powertoys-pulse-actions.git
    $artifactPath = Join-Path $dashboard 'data\items\42.json'
    Write-JsonFileAtomically $artifactPath $artifact
    Write-JsonFileAtomically (Join-Path $dashboard 'data\index.json') ([pscustomobject]@{
        artifact_numbers = @(42); items = @([pscustomobject]@{ number = 42; kind = 'pr'; has_artifact = $true; title = 'Preserve values'; created_at = '2026-11-20T00:00:00Z' })
    })
    & git -C $dashboard add data
    & git -C $dashboard -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) { throw 'Fixture commit failed.' }
    $assessment.artifact_sha256 = (Get-FileHash $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-JsonFileAtomically $assessmentPath $assessment
    $inventory = (& (Join-Path $skill 'scripts\Get-AutoPostCandidates.ps1') -Dashboard $dashboard) | ConvertFrom-Json
    Assert-True (@($inventory.candidates).Count -eq 1) 'Canonical inventory discovers supported PR artifact.'
    $invoke = Join-Path $skill 'scripts\Invoke-AutoPost.ps1'
    $parameters = @{ Dashboard = $dashboard; AssessmentPath = $assessmentPath; ExpectedLogin = 'poster'; StateDirectory = $stateRoot }
    $output = (& $invoke @parameters) | ConvertFrom-Json
    Assert-True ($global:AutoPostTestState.writes -eq 0 -and -not $output.posted -and $output.items[0].selected) 'Live dry-run validates evidence without any GitHub write.'
    $badParameters = $parameters.Clone(); $badParameters.ExpectedLogin = 'another-account'
    Assert-Fails { & $invoke @badParameters -Post } 'differs'
    Assert-True ($global:AutoPostTestState.writes -eq 0) 'Account mismatch writes nothing.'
    $assessment.snapshot.issueCommentCount = 1
    Write-JsonFileAtomically $assessmentPath $assessment
    Assert-Fails { & $invoke @parameters -Post } 'Discussion changed'
    $assessment.snapshot.issueCommentCount = 0
    Write-JsonFileAtomically $assessmentPath $assessment
    Assert-True ($global:AutoPostTestState.writes -eq 0) 'Stale discussion assessment cannot post.'
    $global:AutoPostTestState.apiFailure = $true
    Assert-Fails { & $invoke @parameters -Post } 'GitHub API request failed'
    $global:AutoPostTestState.apiFailure = $false
    Assert-True ($global:AutoPostTestState.writes -eq 0) 'API failure is fail-closed.'
    $heldLock = [IO.File]::Open((Join-Path $stateRoot 'posting.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Assert-Fails { & $invoke @parameters -Post } 'being used|another process|process cannot access' } finally { $heldLock.Dispose() }
    Assert-True ($global:AutoPostTestState.writes -eq 0) 'Overlapping process cannot post.'
    $output = (& $invoke @parameters -Post) | ConvertFrom-Json
    Assert-True ($output.posted -and $global:AutoPostTestState.writes -eq 2 -and $global:AutoPostTestState.remoteReview.state -eq 'COMMENTED') 'One pending review and one COMMENT submission are verified.'
    Assert-True ($global:AutoPostTestState.remoteReview.body.Contains($marker)) 'Posted body contains the standard Pulse attribution marker.'
    $assessment.snapshot.reviewCount = 1
    Write-JsonFileAtomically $assessmentPath $assessment
    $output = (& $invoke @parameters -Post) | ConvertFrom-Json
    Assert-True ($global:AutoPostTestState.writes -eq 2 -and -not $output.items[0].selected -and -not $output.posted) 'New run recognizes remote post without reposting.'
    Assert-True (($output.items[0].reasons -join ';') -match 'already posted') 'Duplicate is explained in private report.'

    $global:AutoPostTestState.remoteReview = $null
    $assessment.snapshot.reviewCount = 0
    Write-JsonFileAtomically $assessmentPath $assessment
    $parameters.StateDirectory = Join-Path $root 'closed-state'
    $global:AutoPostTestState.closedBeforeSubmit = $true
    Assert-Fails { & $invoke @parameters -Post } 'no longer open'
    Assert-True ($global:AutoPostTestState.remoteReview.state -eq 'PENDING' -and $global:AutoPostTestState.writes -eq 3) 'Closing between prepare and submit prevents public posting.'
    Assert-True (Test-Path (Join-Path $parameters.StateDirectory "42-$($assessment.artifact_sha256)\review-publish-state.json")) 'Interrupted attempt retains its publish state.'
    Write-Host "Auto-post tests passed ($script:assertions assertions)."
} finally {
    Remove-Item Function:\gh
    Remove-Variable AutoPostTestState -Scope Global
    if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
