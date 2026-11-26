#requires -Version 7.5
. (Join-Path $PSScriptRoot 'Emit.Index.ps1')
. (Join-Path $PSScriptRoot '.github\skills\powertoys-dashboard-update\scripts\ArtifactValidationReceipt.ps1')

function Convert-PinnedPrInventoryItem {
  param($Snapshot, [string]$Maintainer)
  $number = 0
  if (-not [int]::TryParse([string]$Snapshot.number, [ref]$number) -or $number -le 0 -or
      $Snapshot.state -ine 'OPEN' -or $Snapshot.isDraft -isnot [bool] -or $Snapshot.isDraft -or
      [string]$Snapshot.headRefOid -notmatch '^[0-9a-fA-F]{40}$' -or
      $Snapshot.url -cne "https://github.com/microsoft/PowerToys/pull/$number" -or
      $Snapshot.title -isnot [string] -or [string]::IsNullOrWhiteSpace($Snapshot.title) -or
      $Snapshot.author.login -isnot [string] -or [string]::IsNullOrWhiteSpace($Snapshot.author.login) -or
      $Snapshot.authorAssociation -notin @('MEMBER','OWNER','COLLABORATOR','CONTRIBUTOR','FIRST_TIMER','FIRST_TIME_CONTRIBUTOR','NONE','MANNEQUIN')) {
    throw 'Pinned inventory metadata requires an identified open, non-draft PR with a valid head and source identity.'
  }
  $dates = @{}
  foreach ($name in @('createdAt', 'updatedAt')) {
    $date = [datetimeoffset]::MinValue
    if ($Snapshot.$name -isnot [string] -or $Snapshot.$name -notmatch '^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$' -or
        -not [datetimeoffset]::TryParse($Snapshot.$name, [ref]$date)) {
      throw "PR $number pinned inventory metadata requires an exact $name timestamp."
    }
    $dates[$name] = $date
  }
  if ($dates.updatedAt -lt $dates.createdAt) { throw "PR $number updatedAt precedes createdAt." }
  foreach ($name in @('labels', 'assignees')) {
    $connection = $Snapshot.$name
    if ($connection.nodes -isnot [Array] -or $connection.pageInfo.hasNextPage -isnot [bool] -or $connection.pageInfo.hasNextPage) {
      throw "PR $number requires a complete $name connection with pageInfo.hasNextPage:false."
    }
    $field = if ($name -eq 'labels') { 'name' } else { 'login' }
    foreach ($node in $connection.nodes) {
      if ($node.$field -isnot [string] -or [string]::IsNullOrWhiteSpace($node.$field)) {
        throw "PR $number has invalid $name metadata."
      }
    }
  }
  if (@($Snapshot.labels.nodes | Where-Object { $_.name -match '(?i)Command Palette|CmdPal' }).Count -gt 0) {
    throw "PR $number is CmdPal and is outside this inventory scope."
  }
  $commentCount = 0
  if (-not [int]::TryParse([string]$Snapshot.comments.totalCount, [ref]$commentCount) -or $commentCount -lt 0) {
    throw "PR $number requires comments.totalCount; do not substitute review counts."
  }
  if ($Snapshot.reviewDecision -and $Snapshot.reviewDecision -notin @('APPROVED','CHANGES_REQUESTED','REVIEW_REQUIRED')) {
    throw "PR $number has invalid reviewDecision metadata."
  }
  if ($Snapshot.ci_state -and $Snapshot.ci_state -notin @('missing','pending','failed','passed')) {
    throw "PR $number has invalid ci_state metadata."
  }
  if ($Snapshot.mergeStateStatus -and $Snapshot.mergeStateStatus -notin @('BEHIND','BLOCKED','CLEAN','DIRTY','DRAFT','HAS_HOOKS','UNKNOWN','UNSTABLE')) {
    throw "PR $number has invalid mergeStateStatus metadata."
  }
  $raw = [pscustomobject]@{
    number=$number; html_url=$Snapshot.url; title=$Snapshot.title; state='open'; draft=$Snapshot.isDraft
    user=$Snapshot.author; author_association=$Snapshot.authorAssociation
    labels=$Snapshot.labels.nodes; assignees=$Snapshot.assignees.nodes
    created_at=$Snapshot.createdAt; updated_at=$Snapshot.updatedAt
  }
  return [pscustomobject](Convert-LiveItem $raw 'pr' $null $commentCount $Snapshot -Maintainer $Maintainer)
}

function Invoke-PrInventoryGeneration {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$Dashboard,
    [Parameter(Mandatory)][int[]]$Numbers,
    [Parameter(Mandatory)][string]$LiveMetadataPath,
    [switch]$ValidateOnly
  )
  $ErrorActionPreference = 'Stop'
  if ($Numbers.Count -eq 0 -or @($Numbers | Where-Object { $_ -le 0 }).Count -gt 0 -or
      @($Numbers | Sort-Object -Unique).Count -ne $Numbers.Count) {
    throw 'Inventory insertion requires distinct positive explicit PR numbers.'
  }
  $snapshot = Get-Content -LiteralPath $LiveMetadataPath -Raw | ConvertFrom-Json -DateKind String -NoEnumerate
  $records = @($snapshot)
  if ($records.Count -ne $Numbers.Count) { throw 'Pinned inventory metadata must contain exactly the explicit PR selection.' }
  $Dashboard = (Resolve-Path -LiteralPath $Dashboard).Path
  $paths = @((Join-Path $Dashboard 'data\index.json'), (Join-Path $Dashboard 'data\index.js'))
  $originals = @{}
  foreach ($path in $paths) { $originals[$path] = [IO.File]::ReadAllBytes($path) }
  $index = [Text.Encoding]::UTF8.GetString($originals[$paths[0]]).TrimStart([char]0xfeff) | ConvertFrom-Json -DateKind String
  $js = [Text.Encoding]::UTF8.GetString($originals[$paths[1]]).TrimStart([char]0xfeff)
  if ($js -notmatch '(?s)^\s*window\.BOARD_INDEX\s*=\s*(.*?);\s*$' -or
      (Get-ArtifactContentDigest ($Matches[1] | ConvertFrom-Json -DateKind String)) -cne (Get-ArtifactContentDigest $index)) {
    throw 'Existing index.json and index.js disagree; repair the baseline before inventory insertion.'
  }
  if ($index.items -isnot [Array] -or $index.artifact_numbers -isnot [Array] -or
      $index.upstream -cne 'microsoft/PowerToys' -or [string]::IsNullOrWhiteSpace([string]$index.maintainer)) {
    throw 'Inventory insertion requires the existing Pulse manifest and maintainer identity.'
  }
  foreach ($name in @('open_prs','open_issues','community','artifacts')) {
    $count = 0
    if (-not [int]::TryParse([string]$index.counts.$name, [ref]$count) -or $count -lt 0) {
      throw "Existing index requires a valid counts.$name."
    }
  }
  $seen = @{}
  $newRows = [Collections.Generic.List[object]]::new()
  $added = [Collections.Generic.List[int]]::new()
  foreach ($record in $records) {
    $item = Convert-PinnedPrInventoryItem $record $index.maintainer
    if ($item.number -notin $Numbers -or $seen.ContainsKey($item.number)) {
      throw 'Pinned inventory metadata has duplicate or unselected PR numbers.'
    }
    $seen[$item.number] = $true
    $existing = @($index.items | Where-Object { $_.number -eq $item.number })
    if ($existing.Count -gt 1 -or ($existing.Count -eq 1 -and $existing[0].kind -ne 'pr')) {
      throw "PR $($item.number) conflicts with the existing inventory."
    }
    if ($existing.Count -eq 1) { continue }
    if ($item.number -in $index.artifact_numbers) { throw "PR $($item.number) has an inconsistent existing artifact manifest entry." }
    $newRows.Add((New-BoardIndexEntry -Item $item -Track $item.track -Stage $item.stage -Owes $item.owes `
      -PendingAuthor $false -HasArtifact $false -AgentStatus 'none' -ProposedOpen 0))
    $added.Add($item.number)
  }
  $lock = $null
  $lockPath = Join-Path $Dashboard '.scoped-generation.lock'
  $written = [Collections.Generic.List[string]]::new()
  try {
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $index.items = @($index.items) + $newRows.ToArray()
    $delta = Get-BoardIndexCounts -Items $newRows -ArtifactCount 0
    $index.counts.open_prs = [int]$index.counts.open_prs + $delta.open_prs
    $index.counts.community = [int]$index.counts.community + $delta.community
    $files = ConvertTo-BoardIndexFiles $index -Depth 60
    foreach ($path in $paths) {
      if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($originals[$path])) {
        throw 'Publication indexes changed during inventory generation; retry on the new baseline.'
      }
    }
    if (-not $ValidateOnly -and $added.Count -gt 0) {
      $outputs = @($files.json, $files.javascript)
      for ($i=0; $i -lt $paths.Count; $i++) {
        $written.Add($paths[$i])
        [IO.File]::WriteAllText($paths[$i], $outputs[$i], [Text.UTF8Encoding]::new($false))
      }
    }
    return [pscustomobject]@{ inserted_numbers=$added.ToArray(); validated_only=[bool]$ValidateOnly; changed_files=$written.ToArray() }
  } catch {
    foreach ($path in $written) { [IO.File]::WriteAllBytes($path, $originals[$path]) }
    throw
  } finally {
    if ($lock) { $lock.Dispose(); Remove-Item -LiteralPath $lockPath -Force }
  }
}
