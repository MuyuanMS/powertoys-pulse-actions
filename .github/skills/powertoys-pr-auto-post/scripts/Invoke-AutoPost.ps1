[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Dashboard,
    [Parameter(Mandatory)][string]$AssessmentPath,
    [Parameter(Mandatory)][string]$ExpectedLogin,
    [Parameter(Mandatory)][string]$StateDirectory,
    [switch]$Post
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AutoPost.Common.ps1')
& (Join-Path $PSScriptRoot '..\..\powertoys-dashboard-update\scripts\Assert-CanonicalDashboardTarget.ps1') -Dashboard $Dashboard | Out-Null
$dirty = & git -C $Dashboard status --porcelain
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Use a clean, dedicated canonical checkout.' }
$dashboardRoot = [IO.Path]::GetFullPath($Dashboard).TrimEnd('\')
$stateRoot = [IO.Path]::GetFullPath($StateDirectory).TrimEnd('\')
if ($stateRoot.Equals($dashboardRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $stateRoot.StartsWith("$dashboardRoot\", [StringComparison]::OrdinalIgnoreCase)) { throw 'Private state must be outside the dashboard checkout.' }
New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
$lock = $null
try {
    $lock = [IO.File]::Open((Join-Path $stateRoot 'posting.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $assessment = Read-JsonFile $AssessmentPath
    $number = 0
    if (-not [int]::TryParse([string]$assessment.number, [ref]$number) -or $number -lt 1) { throw 'Invalid assessment PR number.' }
    $path = Join-Path $Dashboard "data\items\$number.json"
    $artifactHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $artifact = Read-JsonFile $path
    $index = Read-JsonFile (Join-Path $Dashboard 'data\index.json')
    $rows = @($index.items | Where-Object { $_.kind -eq 'pr' -and $_.number -eq $number -and $_.has_artifact })
    if ($number -notin @($index.artifact_numbers) -or $rows.Count -ne 1 -or
        $rows[0].pending_author -eq $true -or $rows[0].needs_revalidation -eq $true) { throw 'PR is not actionable in the canonical manifest.' }
    Assert-AutoPostArtifact -Artifact $artifact -Assessment $assessment -ArtifactHash $artifactHash
    $results = @(Get-AutoPostSelection -Artifact $artifact -Assessment $assessment)
    $login = [string](Invoke-GhGet -Endpoint 'user').login
    if ($login -ine $ExpectedLogin) { throw "Authenticated login '$login' differs from '$ExpectedLogin'." }
    $repository = 'microsoft/PowerToys'
    $pr = Invoke-GhGet -Endpoint "repos/$repository/pulls/$number"
    $issueComments = @(Get-GhPagedItems -Endpoint "repos/$repository/issues/$number/comments")
    $reviewComments = @(Get-GhPagedItems -Endpoint "repos/$repository/pulls/$number/comments")
    $reviews = @(Get-GhPagedItems -Endpoint "repos/$repository/pulls/$number/reviews")
    $snapshot = [pscustomobject]@{ headSha = [string]$pr.head.sha; updatedAt = ConvertTo-ReviewTimestamp $pr.updated_at;
        issueCommentCount = $issueComments.Count; reviewCommentCount = $reviewComments.Count; reviewCount = $reviews.Count }
    Assert-AutoPostLive -Artifact $artifact -Assessment $assessment -PullRequest $pr -Login $login -Snapshot $snapshot
    $bodies = @($issueComments.body) + @($reviewComments.body) + @($reviews | Where-Object state -ne 'PENDING' | ForEach-Object body)
    foreach ($result in $results) {
        $comment = @($artifact.proposed_comments | Where-Object id -CEQ $result.id)[0]
        if (Test-AutoPostDuplicate -Number $number -Head $artifact.head_sha -Comment $comment -Bodies $bodies) {
            $result.selected = $false; $result.reasons += 'already posted by a dashboard user or agent'
        }
    }
    foreach ($group in @($artifact.proposed_comments | Where-Object selection_group | Group-Object { $_.selection_group.id })) {
        $members = @($results | Where-Object id -in $group.Group.id)
        if (@($members | Where-Object { -not $_.selected }).Count) {
            foreach ($member in $members) { $member.selected = $false; $member.reasons += 'atomic group held after duplicate check' }
        }
    }
    $selected = @($results | Where-Object selected | ForEach-Object id)
    $report = [pscustomobject]@{ number = $number; artifact_sha256 = $artifactHash; account = $login;
        evaluated_at = [DateTimeOffset]::UtcNow.ToString('o'); mode = if ($Post) { 'post' } else { 'dry-run' };
        items = $results; posted = $false; publish_state = $null }
    if ($selected.Count) {
        $data = New-AutoPostReviewData -Artifact $artifact -Assessment $assessment -Snapshot $snapshot -SelectedIds $selected
        $errors = @(Test-ReviewDataDocument -Document $data -CheckGitHub -RequireFindingGrounding)
        if ($errors.Count) { throw ($errors -join "`n") }
        foreach ($item in $data.prs[0].publicPayload.items) {
            $item.body += "`n`n$(Get-AutoPostMarker -Number $number -Head $artifact.head_sha -Id $item.id)"
            $finding = @($data.prs[0].internalEvidence.validation.findingGrounding.findings | Where-Object id -CEQ $item.id)[0]
            # The verified author's text is unchanged; only the deterministic attribution marker is added.
            $finding.body_sha256 = Get-FindingBodyHash $item.body
        }
        $runRoot = Join-Path $stateRoot "$number-$artifactHash"
        New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
        $dataPath = Join-Path $runRoot 'review-data.json'
        $decisionPath = Join-Path $runRoot 'review-decisions.json'
        $statePath = Join-Path $runRoot 'review-publish-state.json'
        if (Test-Path $statePath) {
            $existing = Read-JsonFile $dataPath
            if (($existing.prs[0].publicPayload | ConvertTo-Json -Depth 20 -Compress) -cne
                ($data.prs[0].publicPayload | ConvertTo-Json -Depth 20 -Compress)) { throw 'An interrupted publish has a different selection; resolve it before changing the payload.' }
        } else {
            Write-JsonFileAtomically -Path $dataPath -Value $data
            $selections = [ordered]@{}
            foreach ($id in $selected) { $selections[$id] = 'post' }
            $decisions = [pscustomobject]@{ schemaVersion = 2; reviewDataHash = Get-ReviewDataHash $dataPath;
                prs = @([pscustomobject]@{ number = $number; headSha = $artifact.head_sha; action = 'comment';
                    postContext = $false; contextBody = ''; items = [pscustomobject]$selections }) }
            Write-JsonFileAtomically -Path $decisionPath -Value $decisions
        }
        if (@($reviews | Where-Object { $_.state -eq 'PENDING' -and $_.user.login -ieq $login }).Count -and
            -not (Test-Path $statePath)) { throw 'Existing pending review requires reconciliation before a new publish.' }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $artifactHash) { throw 'Artifact changed during evaluation.' }
        $publisher = Join-Path $PSScriptRoot '..\..\powertoys-pr-review\scripts\Publish-ApprovedReview.ps1'
        if ($Post) {
            & $publisher -DataPath $dataPath -DecisionsPath $decisionPath -StatePath $statePath -ExpectedLogin $ExpectedLogin | Out-Null
            $state = Read-JsonFile $statePath
            if (@($state.prs | Where-Object status -eq 'submitted').Count -ne 1) { throw 'Submission not confirmed in publish ledger.' }
            $posted = Invoke-GhGet -Endpoint "repos/$repository/pulls/$number/reviews/$($state.prs[0].submittedReviewId)"
            if ($posted.state -ne 'COMMENTED' -or $posted.commit_id -cne $artifact.head_sha) { throw 'Remote submission is not a COMMENT review on the pinned head.' }
            $report.posted = $true
            $report.publish_state = $state
        } else {
            & $publisher -DataPath $dataPath -DecisionsPath $decisionPath -StatePath $statePath -ExpectedLogin $ExpectedLogin -DryRun | Out-Null
        }
    }
    Write-JsonFileAtomically -Path (Join-Path $stateRoot "$number-latest-report.json") -Value $report
    $report | ConvertTo-Json -Depth 20
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
}
