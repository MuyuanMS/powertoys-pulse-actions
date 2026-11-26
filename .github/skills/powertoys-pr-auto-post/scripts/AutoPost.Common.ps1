. (Join-Path $PSScriptRoot '..\..\powertoys-pr-review\scripts\ReviewPayload.Common.ps1')

$script:AutoPostWeights = [ordered]@{ correctness = 35; validation = 25; actionability = 20; context = 10; risk = 10 }

function Get-AutoPostSelection {
    param([Parameter(Mandatory)]$Artifact, [Parameter(Mandatory)]$Assessment)
    $comments = @($Artifact.proposed_comments | Where-Object disposition -eq 'proposed')
    $ratings = @($Assessment.items)
    if ((($comments.id | Sort-Object) -join "`n") -cne (($ratings.id | Sort-Object) -join "`n") -or
        @($ratings | Group-Object id | Where-Object Count -gt 1).Count) {
        throw 'Assessment must score every proposed comment exactly once.'
    }
    $results = @(
        foreach ($comment in $comments) {
            $rating = @($ratings | Where-Object id -CEQ $comment.id)[0]
            $reasons = [Collections.Generic.List[string]]::new()
            $weighted = 0.0
            foreach ($dimension in $script:AutoPostWeights.Keys) {
                $value = $rating.dimensions.$dimension
                $score = 0
                if (-not [int]::TryParse([string]$value.score, [ref]$score) -or $score -lt 0 -or $score -gt 100 -or
                    [string]::IsNullOrWhiteSpace([string]$value.rationale)) {
                    throw "Proposal '$($comment.id)' needs a 0-100 integer and rationale for $dimension."
                }
                $weighted += $score * $script:AutoPostWeights[$dimension] / 100.0
                $floor = if ($dimension -eq 'risk') { 90 } else { 85 }
                if ($score -lt $floor) { $reasons.Add("$dimension below $floor") }
            }
            if ($weighted -lt 90) { $reasons.Add('weighted score below 90') }
            if ($rating.verdict -cne 'supported') { $reasons.Add('finding not independently supported') }
            if ($comment.severity -notin @('medium', 'high', 'critical')) { $reasons.Add('optional/low-severity guidance is manual') }
            if ($rating.hard_gates.discussion_checked -ne $true -or
                $rating.hard_gates.no_unanswered_rebuttal -ne $true -or
                $rating.hard_gates.not_already_addressed -ne $true -or
                $rating.hard_gates.validation_verified -ne $true) { $reasons.Add('hard evidence gate not satisfied') }
            [pscustomobject]@{ id = [string]$comment.id; score = [math]::Round($weighted, 2); dimensions = $rating.dimensions;
                verdict = $rating.verdict; selected = $reasons.Count -eq 0; reasons = $reasons.ToArray() }
        }
    )
    foreach ($group in @($comments | Where-Object selection_group | Group-Object { $_.selection_group.id })) {
        $members = @($group.Group)
        $total = [int]$members[0].selection_group.total
        if ($members.Count -lt 2 -or $total -ne $members.Count -or
            (@($members.selection_group.position | Sort-Object -Unique) -join ',') -ne (@(1..$total) -join ',')) {
            throw "Incomplete atomic group '$($group.Name)'."
        }
        foreach ($member in $members) {
            if ($member.selection_group.atomic -ne $true -or [int]$member.selection_group.total -ne $total -or
                [string]$member.selection_group.title -cne [string]$members[0].selection_group.title -or
                $member.in_diff -ne $true -or [string]$member.body -notmatch '```suggestion') {
                throw "Invalid atomic group '$($group.Name)'."
            }
        }
        $groupResults = @($results | Where-Object id -in $members.id)
        if (@($groupResults | Where-Object { -not $_.selected }).Count) {
            foreach ($result in $groupResults) { $result.selected = $false; $result.reasons += 'atomic group has a held member' }
        }
    }
    return $results
}

function Assert-AutoPostArtifact {
    param([Parameter(Mandatory)]$Artifact, [Parameter(Mandatory)]$Assessment, [Parameter(Mandatory)][string]$ArtifactHash)
    if ($Assessment.schemaVersion -ne 1 -or [string]$Assessment.artifact_sha256 -cne $ArtifactHash -or
        [int]$Assessment.number -ne [int]$Artifact.number -or [string]$Assessment.head_sha -cne [string]$Artifact.head_sha) {
        throw 'Assessment does not match the exact artifact bytes, PR number and head.'
    }
    if ($Artifact.kind -ne 'pr' -or $Artifact.track -ne 'review' -or $Artifact.pending_author -eq $true -or
        $Artifact.needs_revalidation -eq $true -or [string]$Artifact.head_sha -notmatch '^[a-f0-9]{40}$') {
        throw 'Artifact is not an eligible pinned PR review.'
    }
    ConvertTo-ReviewTimestamp -Value $Artifact.evaluated_at | Out-Null
    $evaluated = [DateTimeOffset](ConvertTo-ReviewTimestamp -Value $Assessment.evaluated_at)
    if ($evaluated -gt [DateTimeOffset]::UtcNow.AddMinutes(5) -or $evaluated -lt [DateTimeOffset]::UtcNow.AddHours(-2)) {
        throw 'Assessment must be from this run (within two hours).'
    }
    $actions = @($Artifact.actions | Where-Object { $_.type -eq 'post_review' -and $_.review.event -eq 'COMMENT' })
    if ($actions.Count -ne 1 -or -not [string]::IsNullOrWhiteSpace([string]$actions[0].review.body_prefix)) {
        throw 'Auto-post requires exactly one COMMENT post_review action with no body prefix.'
    }
    foreach ($field in @('head_sha', 'repository', 'pr', 'pullRequest')) {
        $value = $actions[0].review.$field
        if ($null -eq $value) { continue }
        $expected = switch ($field) { 'head_sha' { $Artifact.head_sha }; 'repository' { 'microsoft/PowerToys' }; default { $Artifact.number } }
        if ([string]$value -cne [string]$expected) { throw "Review action has a mismatched $field." }
    }
}

function Assert-AutoPostLive {
    param([Parameter(Mandatory)]$Artifact, [Parameter(Mandatory)]$Assessment, [Parameter(Mandatory)]$PullRequest,
          [Parameter(Mandatory)][string]$Login, [Parameter(Mandatory)]$Snapshot)
    if ($PullRequest.state -ne 'open' -or $PullRequest.draft -eq $true -or $PullRequest.merged -eq $true) {
        throw 'Live PR is closed, merged or draft.'
    }
    if ($PullRequest.user.login -ieq $Login) { throw 'Self-authored PRs require manual handling.' }
    if ([string]$PullRequest.head.sha -cne [string]$Artifact.head_sha -or
        [string]$Snapshot.headSha -cne [string]$Artifact.head_sha) { throw 'Live PR head changed.' }
    $observed = $Assessment.snapshot
    foreach ($field in @('updatedAt', 'issueCommentCount', 'reviewCommentCount', 'reviewCount')) {
        $expected = if ($field -eq 'updatedAt') { ConvertTo-ReviewTimestamp $observed.$field } else { [string]$observed.$field }
        if ($expected -cne [string]$Snapshot.$field) { throw "Discussion changed since evaluation ($field); reassess, do not invalidate the artifact." }
    }
}

function Get-AutoPostMarker {
    param([int]$Number, [string]$Head, [string]$Id)
    if ($Number -lt 1 -or $Head -notmatch '^[a-f0-9]{40}$' -or $Id -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$') { throw 'Invalid posting identity.' }
    return "<!-- powertoys-pulse:pr:${Number}:${Head}:${Id} -->"
}

function Test-AutoPostDuplicate {
    param([int]$Number, [string]$Head, [Parameter(Mandatory)]$Comment, [AllowEmptyCollection()][object[]]$Bodies)
    $normalize = { param([string]$Text) ([regex]::Replace($Text.Replace("`r`n", "`n"), '<!--\s*powertoys-pulse:[^>]+-->', '')).Trim() }
    $expected = & $normalize ([string]$Comment.body)
    foreach ($body in $Bodies) {
        if ([string]$body -match "<!--\s*powertoys-pulse:pr:${Number}:${Head}:$([regex]::Escape([string]$Comment.id))\s*-->" -or
            (& $normalize ([string]$body)) -ceq $expected -or
            @(([string]$body -split '\n\s*---\s*\n') | Where-Object { (& $normalize $_) -ceq $expected }).Count) { return $true }
    }
    return $false
}

function New-AutoPostReviewData {
    param([Parameter(Mandatory)]$Artifact, [Parameter(Mandatory)]$Assessment, [Parameter(Mandatory)]$Snapshot,
          [Parameter(Mandatory)][string[]]$SelectedIds)
    $data = $Assessment.reviewData | ConvertTo-Json -Depth 40 | ConvertFrom-Json
    if ($data.repository -cne 'microsoft/PowerToys' -or $data.schemaVersion -ne 2 -or @($data.prs).Count -ne 1 -or
        $data.prs[0].number -ne $Artifact.number -or [string]$data.prs[0].headSha -cne [string]$Artifact.head_sha -or
        $data.prs[0].phase -cne 'ready' -or -not [string]::IsNullOrWhiteSpace([string]$data.prs[0].publicPayload.contextBody)) {
        throw 'Assessment needs one exact-head ready review-data v2 entry with empty contextBody.'
    }
    $pr = $data.prs[0]
    $pr.snapshot = $Snapshot
    $items = @($pr.publicPayload.items | Where-Object id -in $SelectedIds)
    if ((($items.id | Sort-Object) -join "`n") -cne (($SelectedIds | Sort-Object) -join "`n") -or
        @($items | Group-Object id | Where-Object Count -gt 1).Count) { throw 'Review data must contain each selected proposal exactly once.' }
    foreach ($item in $items) {
        $comment = @($Artifact.proposed_comments | Where-Object id -CEQ $item.id)[0]
        foreach ($field in @('body', 'title', 'severity')) {
            if ([string]$item.$field -cne [string]$comment.$field) { throw "Review data rewrites '$($item.id)' $field; manual review required." }
        }
        if ($comment.in_diff -eq $true) {
            if ($item.kind -cne 'inline') { throw 'Inline proposal changed kind.' }
            foreach ($field in @('path', 'line', 'side')) {
                if ([string]$item.$field -cne [string]$comment.$field) { throw "Review data changed $field." }
            }
            $start = if ($null -ne $comment.start_line) { [int]$comment.start_line } else { [int]$comment.line }
            $itemStart = if ($null -ne $item.startLine) { [int]$item.startLine } else { [int]$item.line }
            if ($start -ne $itemStart) { throw 'Review data changed start line.' }
        } elseif ($item.kind -cne 'companion') { throw 'General proposal changed kind.' }
        $expectedGroup = $comment.selection_group | ConvertTo-Json -Depth 10 -Compress
        $actualGroup = $item.selectionGroup | ConvertTo-Json -Depth 10 -Compress
        if ($expectedGroup -cne $actualGroup) { throw 'Review data changed atomic group membership.' }
    }
    $pr.publicPayload.items = $items
    $validation = $pr.internalEvidence.validation
    if ($null -eq $validation.findingGrounding) { throw 'Missing independently verified private grounding.' }
    $validation.findingGrounding.findings = @($validation.findingGrounding.findings | Where-Object id -in $SelectedIds)
    $suggestions = @($items | Where-Object { $_.body -match '```suggestion' })
    if ($suggestions.Count -gt 0 -and $null -ne $validation.suggestionPatch -and $null -ne $validation.lineEndingSafety) {
        $expectedPatchIds = @($suggestions.id | Sort-Object -Unique)
        $actualPatchIds = @($validation.suggestionPatch.appliedItemIds | Sort-Object -Unique)
        if (($expectedPatchIds -join "`n") -cne ($actualPatchIds -join "`n")) {
            throw 'Exact-patch receipt must validate precisely the selected suggestions, not a larger candidate patch.'
        }
        foreach ($id in $suggestions.id) {
            if ($id -notin @($validation.suggestionPatch.appliedItemIds) -or $id -notin @($validation.lineEndingSafety.checkedItemIds)) {
                throw "Suggestion '$id' has no exact-patch/EOL receipt."
            }
        }
        $validation.lineEndingSafety.checkedItemIds = @($suggestions.id)
        $validation.lineEndingSafety.files = @($validation.lineEndingSafety.files | Where-Object path -in $suggestions.path)
    }
    return $data
}
