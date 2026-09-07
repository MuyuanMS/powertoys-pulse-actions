[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)]
  [ValidateRange(1, [int]::MaxValue)]
  [int]$Number,

  [string]$UpstreamRepository = 'microsoft/PowerToys',

  [Parameter(Mandatory)]
  [ValidatePattern('^[^/]+/[^/]+$')]
  [string]$TargetRepository
)

$ErrorActionPreference = 'Stop'

if ($TargetRepository -ieq $UpstreamRepository) {
  throw 'The mirror target must not be the upstream repository.'
}

if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
  throw 'GH_TOKEN is required. Use a GitHub App installation token or a fine-grained token with Issues: write on the target repository.'
}

$source = gh api "repos/$UpstreamRepository/issues/$Number" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) {
  throw "Unable to read $UpstreamRepository issue #$Number."
}
if ($source.pull_request) {
  throw "$UpstreamRepository#$Number is a pull request, not an issue."
}

$marker = "<!-- powertoys-pulse-mirror source=$UpstreamRepository#$Number -->"
$mirrorTitle = "[Issue $Number] $($source.title)"
$mirrorBody = @"
$marker

Fork-side mirror of [$UpstreamRepository#$Number]($($source.html_url)) for investigation and implementation work.

| Source field | Value |
| --- | --- |
| Upstream state | $($source.state) |
| Upstream updated | $($source.updated_at) |
| Reporter | @$($source.user.login) |

This issue is synchronized from public upstream metadata. Continue investigation here, but do not post or modify anything upstream without explicit human approval.
"@

$query = [uri]::EscapeDataString("repo:$TargetRepository in:title `"[Issue $Number]`"")
$matches = @(
  (gh api "search/issues?q=$query&per_page=20" | ConvertFrom-Json).items |
    Where-Object {
      -not $_.pull_request -and
      ([string]$_.title).StartsWith("[Issue $Number]")
    }
)
if ($LASTEXITCODE -ne 0) {
  throw "Unable to search mirror issues in $TargetRepository."
}

if ($matches.Count -gt 1) {
  throw "Multiple mirrors exist for $UpstreamRepository#$Number in $TargetRepository."
}

if ($matches.Count -eq 0) {
  if ($PSCmdlet.ShouldProcess(
      "$TargetRepository issue",
      "Create mirror for $UpstreamRepository#$Number")) {
    $url = gh issue create -R $TargetRepository `
      --title $mirrorTitle `
      --body $mirrorBody
    if ($LASTEXITCODE -ne 0) {
      throw "Unable to create mirror issue in $TargetRepository."
    }
    [pscustomobject]@{
      operation = 'created'
      source = $source.html_url
      mirror = ($url | Select-Object -Last 1)
    }
  }
  return
}

$mirror = $matches[0]
if ($PSCmdlet.ShouldProcess(
    "$TargetRepository#$($mirror.number)",
    "Synchronize mirror for $UpstreamRepository#$Number")) {
  gh issue edit $mirror.number -R $TargetRepository `
    --title $mirrorTitle `
    --body $mirrorBody | Out-Null
  if ($LASTEXITCODE -ne 0) {
    throw "Unable to update mirror issue $TargetRepository#$($mirror.number)."
  }

  if ($source.state -eq 'closed' -and $mirror.state -ne 'closed') {
    gh issue close $mirror.number -R $TargetRepository | Out-Null
  } elseif ($source.state -eq 'open' -and $mirror.state -ne 'open') {
    gh issue reopen $mirror.number -R $TargetRepository | Out-Null
  }
  if ($LASTEXITCODE -ne 0) {
    throw "Unable to synchronize mirror state for $TargetRepository#$($mirror.number)."
  }

  [pscustomobject]@{
    operation = 'updated'
    source = $source.html_url
    mirror = $mirror.html_url
  }
}
