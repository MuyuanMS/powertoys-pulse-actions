param(
  [string]$Dashboard = $(if ($env:POWERTOYS_DASHBOARD_PATH) {
    $env:POWERTOYS_DASHBOARD_PATH
  } else {
    Join-Path $PSScriptRoot '..\..\..\..'
  }),
  [string]$Upstream = 'microsoft/PowerToys',
  [int[]]$Numbers,
  [string]$Login,
  [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$Dashboard = (Resolve-Path $Dashboard).Path
& (Join-Path $PSScriptRoot 'Assert-CanonicalDashboardTarget.ps1') `
  -Dashboard $Dashboard | Out-Null

if (-not $Numbers) {
  $index = Get-Content (Join-Path $Dashboard 'data\index.json') -Raw |
    ConvertFrom-Json
  $Numbers = @($index.artifact_numbers | ForEach-Object { [int]$_ })
}

$markerPattern = '<!--\s*powertoys-pulse:(?<kind>pr|issue):(?<number>\d+):(?<revision>[^:>\s]+):(?<proposal>[^>\s]+)\s*-->'
$records = [System.Collections.Generic.List[object]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new(
  [System.StringComparer]::Ordinal
)

function Add-MarkerRecords {
  param(
    [int]$Number,
    [string]$Surface,
    $Entries
  )

  foreach ($entry in @($Entries)) {
    $body = [string]$entry.body
    if ([string]::IsNullOrWhiteSpace($body)) {
      continue
    }
    foreach ($match in [regex]::Matches($body, $script:markerPattern)) {
      $author = [string]$entry.user.login
      if ($script:Login -and $author -ne $script:Login) {
        continue
      }
      $marker = $match.Value
      if (-not $script:seen.Add($marker)) {
        continue
      }
      $script:records.Add([pscustomobject][ordered]@{
        marker = $marker
        kind = $match.Groups['kind'].Value
        number = [int]$match.Groups['number'].Value
        revision = $match.Groups['revision'].Value
        proposal_id = $match.Groups['proposal'].Value
        author = $author
        created_at = [string]$entry.created_at
        updated_at = [string]$entry.updated_at
        surface = $Surface
        url = [string]$entry.html_url
      })
    }
  }
}

foreach ($number in $Numbers | Sort-Object -Unique) {
  try {
    Add-MarkerRecords -Number $number -Surface 'inline_review_comment' -Entries @(
      gh api "repos/$Upstream/pulls/$number/comments?per_page=100" |
        ConvertFrom-Json
    )
    Add-MarkerRecords -Number $number -Surface 'review_body' -Entries @(
      gh api "repos/$Upstream/pulls/$number/reviews?per_page=100" |
        ConvertFrom-Json
    )
    Add-MarkerRecords -Number $number -Surface 'conversation_comment' -Entries @(
      gh api "repos/$Upstream/issues/$number/comments?per_page=100" |
        ConvertFrom-Json
    )
  } catch {
    Write-Warning "Could not inspect $Upstream#$number`: $($_.Exception.Message)"
  }
}

$byAuthor = @(
  $records |
    Group-Object author |
    Sort-Object Name |
    ForEach-Object {
      [pscustomobject][ordered]@{
        author = $_.Name
        posted_actions = $_.Count
        prs = @($_.Group | Where-Object kind -eq 'pr' |
          Select-Object -ExpandProperty number -Unique).Count
        issues = @($_.Group | Where-Object kind -eq 'issue' |
          Select-Object -ExpandProperty number -Unique).Count
      }
    }
)

$result = [pscustomobject][ordered]@{
  generated_at = (Get-Date).ToUniversalTime().ToString('o')
  upstream = $Upstream
  inspected_numbers = @($Numbers | Sort-Object -Unique).Count
  traceable_posted_actions = $records.Count
  by_author = $byAuthor
  records = @($records)
  limitations = @(
    'Counts only GitHub comments and review bodies containing a powertoys-pulse marker.',
    'Approvals and opened pull requests do not currently carry a marker.',
    'GitHub does not expose a reliable accepted-suggestion signal for every inline suggestion.',
    'PR review-loop and issue-design counts still come from dashboard artifacts, not posted-comment markers.'
  )
}

if ($AsJson) {
  $result | ConvertTo-Json -Depth 8
} else {
  $byAuthor | Format-Table -AutoSize
  Write-Host "Traceable posted actions: $($records.Count)"
  Write-Host "Inspected artifact numbers: $(@($Numbers | Sort-Object -Unique).Count)"
}
