$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $repository 'Emit.PrInventory.ps1')
$root = Join-Path $repository ('.inventory-tests-' + [guid]::NewGuid().ToString('N'))
$dashboard = Join-Path $root 'dashboard'
$metadataPath = Join-Path $root 'live.json'
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
  $script:assertions++
  if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Write-Json($Value, [string]$Path) {
  [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 60), [Text.UTF8Encoding]::new($false))
}
function New-Snapshot([int]$Number=100) {
  return [pscustomobject]@{
    number=$Number; url="https://github.com/microsoft/PowerToys/pull/$Number"; title='Pinned inventory title'
    state='OPEN'; isDraft=$false; headRefOid=('a' * 40)
    author=[pscustomobject]@{login='contributor'}; authorAssociation='FIRST_TIME_CONTRIBUTOR'
    createdAt='2026-11-23T02:23:35Z'; updatedAt='2026-11-24T06:42:50+00:00'
    labels=[pscustomobject]@{nodes=@([pscustomobject]@{name='Product-FancyZones'});pageInfo=[pscustomobject]@{hasNextPage=$false}}
    assignees=[pscustomobject]@{nodes=@([pscustomobject]@{login='reviewer'});pageInfo=[pscustomobject]@{hasNextPage=$false}}
    comments=[pscustomobject]@{totalCount=3}; reviews=[pscustomobject]@{totalCount=14}
    reviewDecision='REVIEW_REQUIRED'
  }
}
function Invoke-Inventory([int[]]$Selected=@(100,101), [switch]$ValidateOnly) {
  Invoke-PrInventoryGeneration -Dashboard $dashboard -Numbers $Selected -LiveMetadataPath $metadataPath -ValidateOnly:$ValidateOnly
}
function Assert-UnchangedFailure($Records, [int[]]$Selected, [string]$Pattern) {
  Write-Json $Records $metadataPath
  $message = ''
  try { Invoke-Inventory $Selected | Out-Null } catch { $message=$_.Exception.Message }
  Assert-True ($message -match $Pattern) "Expected '$Pattern', got '$message'"
  foreach ($path in $script:baseline.Keys) {
    Assert-True ((Get-FileHash -LiteralPath $path).Hash -ceq $script:baseline[$path]) 'failed insertion leaves every prior file unchanged'
  }
}
try {
  New-Item -ItemType Directory -Path (Join-Path $dashboard 'data\items') -Force | Out-Null
  $oldRows = @(
    [pscustomobject]@{number=900;kind='pr';stage='awaiting_author';has_artifact=$true;pending_author=$true;labels=@('existing');custom=[pscustomobject]@{timestamp='2026-01-02T01:02:03+00:00';empty=[pscustomobject]@{}}},
    [pscustomobject]@{number=901;kind='issue';stage='design_ready';has_artifact=$true;is_community=$true}
  )
  $index = [pscustomobject]@{
    upstream='microsoft/PowerToys';maintainer='maintainer';generated_at='2026-01-02T01:02:03+00:00'
    window_since='2025-12-01T00:00:00Z';impact=[pscustomobject]@{as_of='2026-01-02T01:02:03+00:00';comments_drafted=7}
    artifact_numbers=@(900,901);items=$oldRows
    counts=[pscustomobject]@{open_prs=1;open_issues=1;community=1;artifacts=2}
  }
  $indexPath = Join-Path $dashboard 'data\index.json'
  $jsPath = Join-Path $dashboard 'data\index.js'
  $files = ConvertTo-BoardIndexFiles $index -Depth 60
  [IO.File]::WriteAllText($indexPath, $files.json)
  [IO.File]::WriteAllText($jsPath, $files.javascript)
  $opaque = [byte[]]@(0,255,13,10,5)
  foreach ($number in @(900,901)) { [IO.File]::WriteAllBytes((Join-Path $dashboard "data\items\$number.json"), $opaque) }
  $script:baseline = @{}
  foreach ($path in @(Get-ChildItem (Join-Path $dashboard 'data') -Recurse -File).FullName) { $script:baseline[$path]=(Get-FileHash -LiteralPath $path).Hash }
  $first = New-Snapshot
  $second = New-Snapshot 101
  $second.author.login='maintainer';$second.authorAssociation='MEMBER'
  $second | Add-Member -NotePropertyName ci_state -NotePropertyValue 'passed'
  $second | Add-Member -NotePropertyName mergeStateStatus -NotePropertyValue 'CLEAN'
  Write-Json @($first,$second) $metadataPath
  $dry = Invoke-Inventory -ValidateOnly
  Assert-True ($dry.validated_only -and ($dry.inserted_numbers -join ',') -eq '100,101' -and $dry.changed_files.Count -eq 0) 'read-only inventory preflight identifies only new selected rows'
  foreach ($path in $script:baseline.Keys) { Assert-True ((Get-FileHash -LiteralPath $path).Hash -ceq $script:baseline[$path]) 'preflight writes no prior data' }
  $result = Invoke-Inventory
  Assert-True ($result.changed_files.Count -eq 2) 'inventory writes only the two index files'
  $after = Get-Content $indexPath -Raw | ConvertFrom-Json -DateKind String
  $row = $after.items[2]
  Assert-True ($row.id -eq 'pr-100' -and $row.kind -eq 'pr' -and $row.author -eq 'contributor' -and $row.url -eq $first.url -and $row.is_community -and -not $row.mine) 'row uses the shared live source identity/classification'
  Assert-True ($row.comments -eq 3 -and $row.labels[0] -eq 'Product-FancyZones' -and $row.assignees[0] -eq 'reviewer' -and $row.created_at -ceq $first.createdAt -and $row.updated_at -ceq $first.updatedAt) 'complete source fields are preserved; reviews are not discussion comments'
  Assert-True (-not $row.has_artifact -and $row.agent_status -eq 'none' -and $null -eq $row.track -and $null -eq $row.stage -and $null -eq $row.primary_action -and $row.proposed_open -eq 0 -and -not $row.pending_author -and $row.owes -eq 'us') 'inventory alone never fabricates review completion or actions'
  Assert-True ($after.items[3].mine -and -not $after.items[3].is_community -and $after.items[3].ci_state -eq 'passed' -and $after.items[3].merge_state -eq 'CLEAN') 'maintainer and supplied readiness map through the full generator factory'
  Assert-True ($after.counts.open_prs -eq 3 -and $after.counts.community -eq 2 -and $after.counts.open_issues -eq 1 -and $after.counts.artifacts -eq 2 -and ($after.artifact_numbers -join ',') -eq '900,901') 'only new PR/community counts change; artifact manifest stays untouched'
  Assert-True ((Get-ArtifactContentDigest @($after.items[0],$after.items[1])) -ceq (Get-ArtifactContentDigest $oldRows)) 'every unrelated PR/issue row is content-identical'
  Assert-True ($after.generated_at -ceq $index.generated_at -and $after.window_since -ceq $index.window_since -and (Get-ArtifactContentDigest $after.impact) -ceq (Get-ArtifactContentDigest $index.impact)) 'historical inventory timestamps and impact are untouched'
  $jsJson = ((Get-Content $jsPath -Raw) -replace '^window\.BOARD_INDEX = ', '' -replace ';$', '') | ConvertFrom-Json -DateKind String
  Assert-True ((Get-ArtifactContentDigest $jsJson) -ceq (Get-ArtifactContentDigest $after)) 'both generated indexes agree'
  foreach ($number in @(900,901)) { Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $dashboard "data\items\$number.json"))) -ceq [Convert]::ToBase64String($opaque)) 'unrelated opaque PR/issue artifact bytes were never rewritten or parsed' }
  Assert-True (-not (Test-Path (Join-Path $dashboard 'data\items\100.json'))) 'metadata insertion creates no per-number artifact'
  $repeat = Invoke-Inventory
  Assert-True ($repeat.changed_files.Count -eq 0 -and $repeat.inserted_numbers.Count -eq 0) 'existing rows remain byte-identical on repeated insertion'
  foreach ($path in @($script:baseline.Keys)) { $script:baseline[$path]=(Get-FileHash -LiteralPath $path).Hash }
  foreach ($case in @('draft','closed','cmdpal','bad-head','missing-author','missing-created','partial-labels','missing-assignees','missing-comments','wrong-url')) {
    $bad = New-Snapshot 102
    switch ($case) {
      'draft' {$bad.isDraft=$true}
      'closed' {$bad.state='CLOSED'}
      'cmdpal' {$bad.labels.nodes[0].name='Product-CmdPal'}
      'bad-head' {$bad.headRefOid='g' * 40}
      'missing-author' {$bad.author=$null}
      'missing-created' {$bad.createdAt=$null}
      'partial-labels' {$bad.labels.pageInfo.hasNextPage=$true}
      'missing-assignees' {$bad.assignees=$null}
      'missing-comments' {$bad.comments=$null}
      'wrong-url' {$bad.url='https://github.com/other/repository/pull/102'}
    }
    Assert-UnchangedFailure $bad @(102) 'Pinned inventory|pinned inventory|complete|CmdPal|totalCount'
  }
  Assert-UnchangedFailure @((New-Snapshot 102),(New-Snapshot 102)) @(102,103) 'duplicate or unselected'
  Assert-UnchangedFailure @((New-Snapshot 102),(New-Snapshot 103)) @(102,102) 'distinct positive'
  Assert-UnchangedFailure (New-Snapshot 103) @(102) 'duplicate or unselected'
  Assert-UnchangedFailure @((New-Snapshot 102),(New-Snapshot 103)) @(102) 'exactly the explicit'
  $bad = New-Snapshot 103; $bad.isDraft=$true
  Assert-UnchangedFailure @((New-Snapshot 102),$bad) @(102,103) 'Pinned inventory'
  Write-Json (New-Snapshot 102) $metadataPath
  $singleDry = Invoke-Inventory @(102) -ValidateOnly
  Assert-True (($singleDry.inserted_numbers -join ',') -eq '102') 'a complete singleton GraphQL snapshot is supported'
  Assert-True (-not (Test-Path (Join-Path $dashboard '.scoped-generation.lock'))) 'shared publication lock is cleaned'
  Write-Host "Bounded PR inventory tests passed ($script:assertions assertions)."
} finally {
  Remove-Item -LiteralPath $root -Recurse -Force
}
