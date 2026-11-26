[CmdletBinding()]
param([Parameter(Mandatory)][string]$Dashboard)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AutoPost.Common.ps1')
& (Join-Path $PSScriptRoot '..\..\powertoys-dashboard-update\scripts\Assert-CanonicalDashboardTarget.ps1') -Dashboard $Dashboard | Out-Null
$dirty = & git -C $Dashboard status --porcelain
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Use a clean, dedicated canonical checkout.' }
$revision = (& git -C $Dashboard rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source revision.' }
$index = Read-JsonFile (Join-Path $Dashboard 'data\index.json')
$candidates = @(
    foreach ($row in @($index.items | Where-Object { $_.kind -eq 'pr' -and $_.has_artifact })) {
        $path = Join-Path $Dashboard "data\items\$($row.number).json"
        $artifact = Read-JsonFile $path
        $comments = @($artifact.proposed_comments | Where-Object disposition -eq 'proposed')
        if (-not $comments.Count -or $artifact.pending_author -eq $true -or $artifact.needs_revalidation -eq $true -or
            $row.pending_author -eq $true -or $row.needs_revalidation -eq $true -or
            -not @($artifact.actions | Where-Object { $_.type -eq 'post_review' -and $_.review.event -eq 'COMMENT' }).Count) { continue }
        [pscustomobject]@{ number = $row.number; title = $row.title; created_at = $row.created_at;
            head_sha = $artifact.head_sha; artifact_sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();
            evaluated_at = $artifact.evaluated_at; proposed = $comments.Count; comments = $comments }
    }
)
[pscustomobject]@{ source_revision = $revision; candidates = @($candidates | Sort-Object created_at, number) } | ConvertTo-Json -Depth 20
