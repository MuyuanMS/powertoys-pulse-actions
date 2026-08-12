[CmdletBinding()]
param(
  [string]$Destination = (Join-Path $HOME '.copilot\skills'),
  [switch]$Update
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '.github\skills'
$skills = @(
  'powertoys-dashboard-update',
  'powertoys-pr-review',
  'powertoys-issue-to-design',
  'powertoys-design-to-pr'
)

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
foreach ($skill in $skills) {
  $from = Join-Path $source $skill
  $to = Join-Path $Destination $skill
  if (-not (Test-Path (Join-Path $from 'SKILL.md'))) {
    throw "Skill source is incomplete: $from"
  }
  if ((Test-Path $to) -and -not $Update) {
    Write-Host "Keeping existing skill: $skill (use -Update to replace it)"
    continue
  }
  if (Test-Path $to) {
    Remove-Item -Path $to -Recurse -Force
  }
  Copy-Item -Path $from -Destination $to -Recurse
  Write-Host "Installed skill: $skill"
}

Write-Host "PowerToys dashboard skills are available under $Destination."
