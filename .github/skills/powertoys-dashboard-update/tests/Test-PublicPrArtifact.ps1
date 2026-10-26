$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\PublicPrArtifact.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) "pulse-public-$([guid]::NewGuid().ToString('N'))"
$dashboard = Join-Path $root 'dashboard'
$private = Join-Path $root 'private'
$items = Join-Path $dashboard 'data\items'
New-Item -ItemType Directory -Path $items -Force | Out-Null
$head = 'a' * 40
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
try {
    $blocked = [pscustomobject]@{
        number = 123; kind = 'pr'; stage = 'review_blocked'; head_sha = $head
        generated_at = '2026-10-22T00:00:00Z'; source_updated_at = '2026-10-22T00:00:00Z'
        evaluated_at = '2026-10-22T00:00:00Z'
        summary = 'Install the coordinator toolchain'
        status = @{ detail = 'Permission packet exhausted' }
        workflow = @{ phase = 'building' }; next_action = 'Resume worker'
        blockers = @(@{ terminal = $true; detail = 'Review cap'; remediation = 'Authorize round nine' })
        fork_pr = @{ number = 900 }; fork_branch = 'pr-iterate/123'
        actions = @(); proposed_comments = @()
        validation = @{ upstream_head = @{ head_sha = $head; result = 'failed'; scope = 'Private compiler log' } }
    }
    $blocked | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $items '123.json')
    @{ items = @(@{ number = 123; kind = 'pr'; stage = 'review_blocked'; mirror = @{number=900} }) } |
        ConvertTo-Json -Depth 20 | Set-Content (Join-Path $dashboard 'data\index.json')
    $sanitizer = Join-Path $PSScriptRoot '..\..\..\..\Sanitize-ActionData.ps1'
    & $sanitizer -DataPath (Join-Path $dashboard 'data') -PrivateStatePath $private
    $public = Get-Content (Join-Path $items '123.json') -Raw | ConvertFrom-Json
    Assert-True ($public.stage -eq 'review_in_progress') 'internal blocker is pending work, not a PR defect'
    Assert-True (($public | ConvertTo-Json -Depth 20) -notmatch 'coordinator|Permission packet|Review cap|round nine|compiler log|pr-iterate') 'operational prose is absent from public data'
    Assert-True (@(Get-ChildItem (Join-Path $private '123') -File).Count -eq 1) 'original private receipt is preserved before projection'
    $saved = Get-Content (Get-ChildItem (Join-Path $private '123') -File)[0].FullName -Raw | ConvertFrom-Json
    Assert-True ($saved.blockers[0].terminal -eq $true -and $saved.fork_pr.number -eq 900) 'private recovery evidence is intact'
    $index = Get-Content (Join-Path $dashboard 'data\index.json') -Raw | ConvertFrom-Json
    Assert-True ($index.items[0].stage -eq 'review_in_progress' -and -not $index.items[0].mirror) 'index matches public projection'
    $live = Join-Path $root 'live.json'
    @(@{ number=123; headRefOid=$head; title='Review pending'; isDraft=$false; updatedAt='2026-10-22T00:00:00Z' }) |
        ConvertTo-Json -AsArray | Set-Content $live
    $stale = & (Join-Path $PSScriptRoot '..\scripts\Get-StalePrReviewQueue.ps1') `
        -Dashboard $dashboard -PullRequestsJsonPath $live -AsJson | ConvertFrom-Json
    Assert-True ($stale.stale_count -eq 1) 'private exception does not manufacture completed review coverage'
    & $sanitizer -DataPath (Join-Path $dashboard 'data') -PrivateStatePath $private
    Assert-True (@(Get-ChildItem (Join-Path $private '123') -File).Count -eq 1) 'projection is idempotent'
    $ready = [pscustomobject]@{
        number=124; kind='pr'; stage='awaiting_review_approval'; head_sha=$head
        actions=@(@{ type='post_review'; review=@{head_sha=$head} })
        proposed_comments=@(@{ id='fix'; kind='inline'; body='Concrete validated author-facing fix' })
        validation=@{ suggestion_patch=@{ head_sha=$head; result='passed'; minimal_ranges_reviewed=$true; applied_comment_ids=@('fix') } }
        summary='Private request ledger'
    }
    $projected = ConvertTo-PublicPrArtifact $ready
    Assert-True ($projected.actions[0].type -eq 'post_review' -and $projected.proposed_comments[0].id -eq 'fix') 'validated review payload is preserved'
    Assert-True ($projected.validation.suggestion_patch.applied_comment_ids[0] -eq 'fix') 'literal-patch evidence is preserved'
    $failure = ''
    try { Get-PrivatePrStatePath $dashboard (Join-Path $dashboard 'private') | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-True ($failure -match 'outside') 'private receipts cannot be stored inside the published repository'
    $checkpoint = Join-Path $PSScriptRoot '..\scripts\Set-PrReviewCheckpoint.ps1'
    & $checkpoint -Dashboard $dashboard -PrivateStatePath $private -Number 125 -HeadSha $head `
        -SourceUpdatedAt '2026-10-22T00:00:00Z' -Phase building -Detail 'Install a missing compiler' -ForkPr 901
    $publicCheckpoint = Get-Content (Join-Path $items '125.json') -Raw
    Assert-True ($publicCheckpoint -notmatch 'missing compiler|fork_pr|workflow|next_action') 'checkpoint writer never emits operator instructions'
    Assert-True (@(Get-ChildItem (Join-Path $private '125') -File).Count -eq 1) 'checkpoint has private recovery receipt'
    $candidate = $publicCheckpoint | ConvertFrom-Json
    $candidate.proposed_comments = @(@{ id='saved-finding'; body='Accepted correction awaiting literal-patch validation' })
    $candidate | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $items '125.json')
    & $checkpoint -Dashboard $dashboard -PrivateStatePath $private -Number 125 -HeadSha $head `
        -SourceUpdatedAt '2026-10-22T00:00:00Z' -Phase reviewing_findings -Detail 'Continue validation'
    $receipts = @(Get-ChildItem (Join-Path $private '125') -File | ForEach-Object {
        Get-Content $_.FullName -Raw | ConvertFrom-Json
    })
    Assert-True (@($receipts | Where-Object { $_.proposed_comments.id -contains 'saved-finding' }).Count -eq 1) 'checkpoint replacement cannot lose accepted findings awaiting validation'
    $validator = Join-Path $PSScriptRoot '..\scripts\Test-DashboardArtifacts.ps1'
    $blocked.evaluated_at = '2026-10-22T00:00:00Z'
    $blocked | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $items '123.json')
    $failure = ''
    try { & $validator -Dashboard $dashboard -Numbers 123 2>$null } catch { $failure = $_.Exception.Message }
    Assert-True ($failure -match 'validation failed') 'publication gate rejects operational data even if sanitizer was skipped'
    $loop = Get-Content (Join-Path $PSScriptRoot '..\..\powertoys-pr-review\references\copilot-review-loop.md') -Raw
    $worker = Get-Content (Join-Path $PSScriptRoot '..\..\powertoys-pr-review\SKILL.md') -Raw
    Assert-True ($worker -match '`post_review`' -and $loop -match 'Assess ALL saved findings' -and
        $loop -match 'cap ends new requests, not the review.s finalization work') 'cap contract conserves validated findings instead of publishing blocker substitutes'
    Write-Host "Public PR boundary tests passed ($script:assertions assertions)."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
