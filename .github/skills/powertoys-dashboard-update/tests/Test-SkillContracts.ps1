$ErrorActionPreference = 'Stop'
$skills = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$repository = [IO.Path]::GetFullPath((Join-Path $skills '..\..'))
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Read-Contract([string]$RelativePath) {
    Get-Content (Join-Path $skills $RelativePath) -Raw
}

$dashboard = Read-Contract 'powertoys-dashboard-update\SKILL.md'
$worker = Read-Contract 'powertoys-pr-review\SKILL.md'
$drain = Read-Contract 'powertoys-dashboard-update\references\drain-dispatch.md'
$normal = Read-Contract 'powertoys-dashboard-update\references\scheduled-updates.md'
$issue = Read-Contract 'powertoys-dashboard-update\references\issue-triage.md'
$publication = Read-Contract 'powertoys-dashboard-update\references\publication.md'
$loop = Read-Contract 'powertoys-pr-review\references\copilot-review-loop.md'
$batch = Read-Contract 'powertoys-pr-review\references\batch-parallel.md'
$build = Read-Contract 'powertoys-pr-review\references\build-and-test.md'
$migration = Read-Contract 'powertoys-pr-review\references\migration-and-resume.md'

foreach ($entry in @($dashboard, $worker)) {
    Assert-True (($entry -split '\r?\n').Count -le 120) 'entry points stay compact instead of accumulating execution recipes'
}
foreach ($reference in @('drain-dispatch.md', 'scheduled-updates.md', 'issue-triage.md', 'publication.md')) {
    Assert-True ($dashboard.Contains("./references/$reference")) "dashboard routes explicitly to $reference"
}
foreach ($pattern in @('same dispatcher', 'three total cloud requests', 'no separate delta-review algorithm', '50 minutes', '75 /', '15 /', '4 /', '6 /')) {
    Assert-True ($normal.Contains($pattern)) "normal update preserves selection policy and shared worker: $pattern"
}
Assert-True ($normal -match 'never use `-FailOnStale`') 'normal delta update permits explicit unfinished/deferred coverage'
Assert-True ($normal -match 'RunPlanJsonPath' -and $normal -match 'deadline_utc') 'normal path uses executable shared dispatch and fresh deadlines'
Assert-True ($drain -match 'No issue work|does not enable issue work') 'PR-only drain does not expand into issue designs'
Assert-True ($dashboard -notmatch 'fix_assessment|bugreport_zip|/me/sendMail') 'entry point does not preload issue schemas and integrations'
Assert-True ($worker -notmatch 'Sync-PowerToysProject|/me/sendMail|emit.ps1') 'worker does not inherit coordinator side effects'
foreach ($pattern in @('Three total requests', 'Cloud remains required', 'Never wait for a fourth', 'fork-only hunk', 'no-analysis timeout is not a clean pass')) {
    Assert-True ($loop.Contains($pattern)) "worker loop preserves review evidence/cap contract: $pattern"
}
Assert-True ($worker -match 'prepare focused validation commands; do not compile before the first request') 'validation planning precedes cloud requests without pre-review compilation'
Assert-True ($build -match 'literal|verbatim') 'literal suggestion validation remains required'
Assert-True ($batch -match 'Validation after\s+cap/architecture exits is allowed') 'batch validation does not require impossible post-cap convergence'
Assert-True ($batch -match 'Do not hold the\s+lock while waiting') 'cloud waits do not reserve compilation'
Assert-True ($migration -notmatch 'Rebuild after every cross-session resume') 'resume verifies receipts instead of unconditionally rebuilding'
Assert-True ($worker -notmatch 'Emit `stage: review_blocked`|blockers\[\]') 'worker does not instruct publication of operational blockers'
Assert-True ($drain -match 'diagnosed_request_at') 'cloud diagnosis has a durable per-request acknowledgement'
foreach ($field in @('judgment.status', 'fix_assessment', 'proposed_fixes', 'issue_context', 'approve_design',
    'initial_investigation', 'information_gaps', 'existing_fix', 'how_to_collect', 'BugReportTool.exe',
    'implementation_steps', 'code_block', 'edge_cases', 'tests')) {
    Assert-True ($issue.Contains($field)) "issue workflow preserves schema and evidence coverage: $field"
}
foreach ($pattern in @('RequireFindingGrounding', 'ProcessedNumbers', 'Sanitize-ActionData.ps1',
    'main:main', 'artifact_numbers', 'sync-triage-artifacts.mjs', 'EMU account', 'Publish-ApprovedReview.ps1')) {
    Assert-True ($publication.Contains($pattern)) "publication retains enforced boundary: $pattern"
}
foreach ($prompt in @('DRAIN_PRS_PROMPT.md', 'UPDATE_DASHBOARD_PROMPT.md')) {
    $text = Get-Content (Join-Path $repository $prompt) -Raw
    Assert-True (($text -split '\r?\n').Count -le 50) "$prompt delegates policy instead of duplicating it"
    Assert-True ($text.Contains('Install-Skills.ps1 -VerifyOnly')) "$prompt verifies the complete installed suite"
}
$launchPrompt = Get-Content (Join-Path $repository '.github\prompts\powertoys-pr-loop-review.prompt.md') -Raw
Assert-True ($launchPrompt -match 'three total' -and $launchPrompt -match 'architectural') 'direct-review launcher does not resurrect the old unbounded convergence loop'

$contracts = @(
    Get-Item (Join-Path $skills 'powertoys-dashboard-update\SKILL.md')
    Get-Item (Join-Path $skills 'powertoys-pr-review\SKILL.md')
    Get-ChildItem (Join-Path $skills 'powertoys-dashboard-update\references') -Filter *.md
    Get-ChildItem (Join-Path $skills 'powertoys-pr-review\references') -Filter *.md
)
foreach ($contract in $contracts) {
    $text = Get-Content $contract.FullName -Raw
    foreach ($link in [regex]::Matches($text, '\[[^\]]+\]\(([^)]+)\)')) {
        $target = $link.Groups[1].Value.Split('#')[0]
        if (-not $target -or $target -match '^\w+://') { continue }
        $path = Join-Path $contract.DirectoryName ($target -replace '/', '\')
        Assert-True (Test-Path -LiteralPath $path) "local reference exists: $($contract.Name) -> $target"
    }
}
Write-Host "Skill execution contract tests passed ($script:assertions assertions)."
