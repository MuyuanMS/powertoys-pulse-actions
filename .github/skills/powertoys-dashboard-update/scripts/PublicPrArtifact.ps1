function Get-PrivatePrStatePath {
    param([string]$Dashboard, [string]$PrivateStatePath)

    if (-not $PrivateStatePath) {
        $PrivateStatePath = if ($env:POWERTOYS_PRIVATE_REVIEW_STATE) {
            $env:POWERTOYS_PRIVATE_REVIEW_STATE
        } else {
            Join-Path $env:LOCALAPPDATA 'PowerToysPulse\review-state'
        }
    }
    $root = [IO.Path]::GetFullPath($PrivateStatePath).TrimEnd('\')
    $repository = [IO.Path]::GetFullPath($Dashboard).TrimEnd('\')
    if ($root.Equals($repository, [StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith("$repository\", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Private review state must be outside the dashboard repository.'
    }
    return $root
}

function Save-PrivatePrArtifact {
    param($Artifact, [string]$Dashboard, [string]$PrivateStatePath)

    $root = Get-PrivatePrStatePath $Dashboard $PrivateStatePath
    $json = $Artifact | ConvertTo-Json -Depth 60
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $directory = Join-Path $root ([string]$Artifact.number)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $path = Join-Path $directory "$hash.json"
    if (-not (Test-Path -LiteralPath $path)) {
        [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
    }
}

function ConvertTo-PublicPrArtifact {
    param($Artifact)

    if ($Artifact.kind -ne 'pr') { return $Artifact }
    $result = [ordered]@{}
    foreach ($name in @(
        'schemaVersion', 'schema_version', 'number', 'kind', 'repository', 'title', 'url',
        'track', 'stage', 'owes', 'pending_author', 'waiting_since', 'generated_at',
        'evaluated_at', 'source_updated_at', 'head_sha', 'confidence', 'needs_revalidation',
        'proposed_comments', 'actions', 'author_wait_evidence'
    )) {
        if ($Artifact.PSObject.Properties[$name]) { $result[$name] = $Artifact.$name }
    }
    if ([string]$Artifact.stage -match '^(review_blocked|blocked.*|building|waiting_copilot|reviewing_findings|review_iteration_cap_reached)$') {
        $result['stage'] = 'review_in_progress'
    }
    if ($Artifact.freshness) {
        $freshness = [ordered]@{}
        foreach ($name in @('queued_at', 'checkpoint_at')) {
            if ($Artifact.freshness.$name) { $freshness[$name] = $Artifact.freshness.$name }
        }
        $result['freshness'] = [pscustomobject]$freshness
    }
    if ($Artifact.validation) {
        $validation = [ordered]@{}
        foreach ($name in @('upstream_head', 'suggestion_patch', 'line_ending_safety')) {
            $source = $Artifact.validation.$name
            if (-not $source) { continue }
            $evidence = [ordered]@{}
            foreach ($field in @('head_sha', 'result', 'minimal_ranges_reviewed', 'applied_comment_ids', 'checked_comment_ids')) {
                if (($source -is [Collections.IDictionary] -and $source.Contains($field)) -or
                    $source.PSObject.Properties[$field]) { $evidence[$field] = $source.$field }
            }
            if ($name -eq 'line_ending_safety') {
                $evidence['files'] = @($source.files | ForEach-Object {
                    [pscustomobject]@{ path = $_.path; result = $_.result; line_endings = $_.line_endings }
                })
            }
            $validation[$name] = [pscustomobject]$evidence
        }
        $result['validation'] = [pscustomobject]$validation
    }
    return [pscustomobject]$result
}
