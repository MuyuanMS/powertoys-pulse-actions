$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $repository 'Emit.Scoped.ps1')
$validator = Join-Path $repository '.github\skills\powertoys-dashboard-update\scripts\Test-DashboardArtifacts.ps1'
$root = Join-Path $repository ('.scoped-tests-' + [guid]::NewGuid().ToString('N'))
$dashboard = Join-Path $root 'dashboard'
$candidateData = Join-Path $root 'candidate\data'
$receiptPath = Join-Path $root 'receipt.json'
$metadataPath = Join-Path $root 'live.json'
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Write-Json($Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 60), [Text.UTF8Encoding]::new($false))
}
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Assert-True ($message -match $Pattern) "Expected failure '$Pattern', got '$message'"
}
function Invoke-Generation([int[]]$Selected = @(100)) {
    Invoke-ScopedActionDataGeneration -Dashboard $dashboard -Numbers $Selected `
        -CandidateDataPath $candidateData -ValidationReceiptPath $receiptPath -LiveMetadataPath $metadataPath
}
try {
    New-Item -ItemType Directory -Path (Join-Path $dashboard 'data\items'), (Join-Path $candidateData 'items') -Force | Out-Null
    $head = 'a' * 40
    $date = '2026-11-26T00:00:00+00:00'
    $candidate = [pscustomobject][ordered]@{
        number = 100; kind = 'pr'; track = 'review'; stage = 'review_ready'; owes = 'us'
        pending_author = $false; generated_at = $date; evaluated_at = $date; source_updated_at = $date
        head_sha = $head; actions = @(); proposed_comments = @()
        validation = [pscustomobject]@{ upstream_head = [pscustomobject]@{ head_sha = $head; result = 'passed' } }
    }
    $candidatePath = Join-Path $candidateData 'items\100.json'
    Write-Json $candidate $candidatePath
    & $validator -Dashboard (Join-Path $root 'candidate') -Numbers 100 -RequireFindingGrounding -OutputReceiptPath $receiptPath
    $live = @([pscustomobject]@{number=100;headRefOid=$head;updatedAt=$date;title='Pinned title';isDraft=$false;labels=@()})
    Write-Json $live $metadataPath
    $unselectedRow = [pscustomobject][ordered]@{
        number = 200; kind = 'pr'; title = 'Untouched'; stage = 'original'; has_artifact = $true
        custom = @{ values = @(3, 1, 2); evidence = 'Retained index metadata'; empty = [pscustomobject]@{} }
    }
    $index = [pscustomobject][ordered]@{
        generated_at = $date; counts = [pscustomobject]@{open_prs=2;open_issues=0;community=2;artifacts=1}
        impact = @{as_of=$date;comments_drafted=4}; artifact_numbers = @(200)
        items = @([pscustomobject]@{number=100;kind='pr';is_cmdpal=$false;stage='review_in_progress';mirror=@{number=900}}, $unselectedRow)
    }
    $indexPath = Join-Path $dashboard 'data\index.json'
    $jsPath = Join-Path $dashboard 'data\index.js'
    Write-Json $index $indexPath
    [IO.File]::WriteAllText($jsPath, ('window.BOARD_INDEX = ' + (Get-Content $indexPath -Raw) + ';'))
    $opaquePath = Join-Path $dashboard 'data\items\200.json'
    $opaque = [byte[]]@(0, 255, 12, 13, 10, 1)
    [IO.File]::WriteAllBytes($opaquePath, $opaque)
    [IO.File]::WriteAllText((Join-Path $candidateData 'items\200.json'), 'MUST NOT BE READ: not JSON')
    $beforeRow = Get-ArtifactContentDigest $unselectedRow
    $beforeImpact = Get-ArtifactContentDigest $index.impact
    $result = Invoke-Generation
    Assert-True ($result.changed_files.Count -eq 3) 'selected artifact and both indexes were generated'
    $after = Get-Content $indexPath -Raw | ConvertFrom-Json -DateKind String
    Assert-True ((Get-ArtifactContentDigest $after.items[1]) -ceq $beforeRow) 'unselected index row content is unchanged'
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($opaquePath)) -ceq [Convert]::ToBase64String($opaque)) 'unselected invalid artifact bytes are unchanged, never parsed'
    Assert-True ($after.items[0].stage -eq 'review_ready' -and $after.items[0].has_artifact -and $after.items[0].title -eq 'Pinned title') 'selected row uses validated artifact and pinned metadata'
    Assert-True (-not $after.items[0].PSObject.Properties['mirror'] -and $after.items[0].proposed_open -eq 0) 'selected row follows public sanitizer conventions'
    Assert-True (($after.artifact_numbers -join ',') -eq '200,100' -and $after.counts.artifacts -eq 2) 'manifest preserves order and adds selected artifact'
    Assert-True ($after.generated_at -eq $date -and (Get-ArtifactContentDigest $after.impact) -ceq $beforeImpact) 'scoped mode does not claim a live inventory or impact refresh'
    $jsJson = (Get-Content $jsPath -Raw) -replace '^window\.BOARD_INDEX = ', '' -replace ';$', '' | ConvertFrom-Json -DateKind String
    Assert-True ((Get-ArtifactContentDigest $jsJson) -ceq (Get-ArtifactContentDigest $after)) 'JSON and JS indexes agree'
    $again = Invoke-Generation @(100,100)
    Assert-True ($again.changed_files.Count -eq 0) 'duplicate numbers and repeated generation are idempotent'
    $beforeIndex = [IO.File]::ReadAllBytes($indexPath)
    $candidate.stage = 'review_in_progress'
    Write-Json $candidate $candidatePath
    Assert-Fails { Invoke-Generation } 'validation receipt'
    Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($indexPath)) -ceq [Convert]::ToBase64String($beforeIndex)) 'receipt mismatch does not mutate target'
    $candidate.stage = 'review_ready'
    Write-Json $candidate $candidatePath
    $live[0].headRefOid = 'b' * 40
    Write-Json $live $metadataPath
    Assert-Fails { Invoke-Generation } 'validation receipt'
    $live[0].headRefOid = $head
    $live[0].isDraft = $true
    Write-Json $live $metadataPath
    Assert-Fails { Invoke-Generation } 'pinned eligible'
    $live[0].isDraft = $false
    $live[0].labels = @(@{name='Product-CmdPal'})
    Write-Json $live $metadataPath
    Assert-Fails { Invoke-Generation } 'pinned eligible'
    $live[0].labels = @()
    Write-Json $live $metadataPath
    Assert-Fails { Invoke-Generation @(999) } 'existing non-CmdPal'
    Assert-Fails { Invoke-Generation @(0) } 'positive explicit'
    Assert-Fails { & $validator -Dashboard (Join-Path $root 'candidate') -Numbers 100 -OutputReceiptPath $receiptPath } 'RequireFindingGrounding'
    $candidate.stage = 'awaiting_review_approval'
    $candidate.actions = @(@{type='post_review';review=@{event='COMMENT';head_sha=$head;body_prefix='No additional change requested.'}})
    Write-Json $candidate $candidatePath
    Assert-Fails { & $validator -Dashboard (Join-Path $root 'candidate') -Numbers 100 -RequireFindingGrounding -OutputReceiptPath (Join-Path $root 'invalid-receipt.json') 2>$null } 'validation failed'
    Assert-True (-not (Test-Path (Join-Path $root 'invalid-receipt.json'))) 'failed public-content/grounding gates never produce a receipt'
    $candidate.actions = @()
    $candidate.stage = 'review_in_progress'
    Write-Json $candidate $candidatePath
    & $validator -Dashboard (Join-Path $root 'candidate') -Numbers 100 -RequireFindingGrounding -OutputReceiptPath $receiptPath
    $pending = Invoke-Generation
    $pendingIndex = Get-Content $indexPath -Raw | ConvertFrom-Json -DateKind String
    Assert-True ($pendingIndex.items[0].stage -eq 'review_in_progress' -and -not $pendingIndex.items[0].primary_action) 'neutral correction remains neutral, not a fabricated completed review'
    $readiness = @([pscustomobject]@{
        number=100;head_sha=$head;source_updated_at=$date;status='ready_for_publication'
        validation='Fixture strict validation passed; saved evidence verified.'
        candidate_sha256=(Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash
        candidate_path='Ignored; only supplied candidate directory may be read'
    })
    Write-Json $readiness $receiptPath
    $retained = Invoke-Generation
    Assert-True ($retained.changed_files.Count -eq 0) 'verified retained readiness uses pinned bytes without re-reading source or candidate_path'
    $readiness[0].status = 'pending'
    Write-Json $readiness $receiptPath
    Assert-Fails { Invoke-Generation } 'readiness receipt'
    $readiness[0].status = 'ready_for_publication'
    $readiness[0].candidate_sha256 = '0' * 64
    Write-Json $readiness $receiptPath
    Assert-Fails { Invoke-Generation } 'readiness receipt'
    $readiness[0].candidate_sha256 = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash
    Write-Json $readiness $receiptPath
    $candidate.stage = 'review_ready'
    Write-Json $candidate $candidatePath
    $readiness[0].candidate_sha256 = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash
    Write-Json $readiness $receiptPath
    $dry = Invoke-ScopedActionDataGeneration -Dashboard $dashboard -Numbers 100 -CandidateDataPath $candidateData `
        -ValidationReceiptPath $receiptPath -LiveMetadataPath $metadataPath -ValidateOnly
    Assert-True ($dry.validated_only -and $dry.changed_files.Count -eq 0 -and
        (Get-ArtifactContentDigest (Get-Content $indexPath -Raw | ConvertFrom-Json -DateKind String)) -ceq (Get-ArtifactContentDigest $pendingIndex)) 'validation-only checks do not mutate selected data or indexes'
    Assert-Fails { Invoke-Generation @(100,999) } 'existing non-CmdPal'
    $pendingHash = Get-ArtifactContentDigest $pendingIndex
    Assert-True ((Get-ArtifactContentDigest (Get-Content $indexPath -Raw | ConvertFrom-Json -DateKind String)) -ceq $pendingHash) 'partial invalid selection does not write a partial batch'
    Assert-True (@(Get-ChildItem $dashboard -Force | Where-Object Name -like '.scoped-generation*').Count -eq 0) 'scratch and lock files are cleaned'
    Write-Host "Scoped publication tests passed ($script:assertions assertions)."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
