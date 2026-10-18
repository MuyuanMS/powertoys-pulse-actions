[CmdletBinding()]
param(
  [string]$Destination = (Join-Path $HOME '.copilot\skills'),
  [switch]$Update,
  [switch]$VerifyOnly,
  [string]$BackupDirectory
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '.github\skills'
$destinationPath = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
$sourcePath = [IO.Path]::GetFullPath($source).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($destinationPath.Equals($sourcePath, [StringComparison]::OrdinalIgnoreCase) -or
    $destinationPath.StartsWith("$sourcePath\", [StringComparison]::OrdinalIgnoreCase)) {
  throw 'The installation destination must be separate from the skill source.'
}
if ($BackupDirectory) {
  $backupPath = [IO.Path]::GetFullPath($BackupDirectory)
  if ($backupPath.TrimEnd('\').Equals($destinationPath, [StringComparison]::OrdinalIgnoreCase) -or
      $backupPath.StartsWith("$destinationPath\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Skill backups must be outside the installation destination.'
  }
}
$skills = @(
  'powertoys-dashboard-update',
  'powertoys-pr-review',
  'powertoys-issue-to-design',
  'powertoys-design-to-pr'
)

foreach ($skill in $skills) {
  $from = Join-Path $source $skill
  if (-not (Test-Path (Join-Path $from 'SKILL.md'))) {
    throw "Skill source is incomplete: $from"
  }
}

if (-not $VerifyOnly) {
  New-Item -ItemType Directory -Force -Path $Destination | Out-Null
  foreach ($skill in $skills) {
    $from = Join-Path $source $skill
    $to = Join-Path $Destination $skill
    if ((Test-Path $to) -and -not $Update) {
      Write-Host "Keeping existing skill: $skill (use -Update to replace it)"
      continue
    }
    if (Test-Path $to) {
      if (-not $BackupDirectory) {
        $BackupDirectory = Join-Path (Split-Path ([IO.Path]::GetFullPath($Destination)) -Parent) `
          "skill-backups\$([guid]::NewGuid().ToString('N'))"
      }
      New-Item -ItemType Directory -Force -Path $BackupDirectory | Out-Null
      $backup = Join-Path $BackupDirectory $skill
      if (Test-Path $backup) { throw "Refusing to overwrite skill backup: $backup" }
      Move-Item -LiteralPath $to -Destination $backup
      Write-Host "Backed up existing skill: $backup"
    }
    Copy-Item -Path $from -Destination $to -Recurse
    Write-Host "Installed skill: $skill"
  }
}

if ($VerifyOnly -or $Update) {
  foreach ($skill in $skills) {
    $from = Join-Path $source $skill
    $to = Join-Path $Destination $skill
    foreach ($file in Get-ChildItem -LiteralPath $from -Recurse -File) {
      $relative = [IO.Path]::GetRelativePath($from, $file.FullName)
      $installed = Join-Path $to $relative
      if (-not (Test-Path -LiteralPath $installed) -or
          (Get-FileHash -LiteralPath $file.FullName).Hash -ne
          (Get-FileHash -LiteralPath $installed).Hash) {
        throw "Installed skill differs from source: $skill\$relative. Update the complete suite before running."
      }
    }
    if (Test-Path -LiteralPath $to) {
      foreach ($file in Get-ChildItem -LiteralPath $to -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($to, $file.FullName)
        if (-not (Test-Path -LiteralPath (Join-Path $from $relative))) {
          throw "Installed skill has an untracked override: $skill\$relative."
        }
      }
    }
  }
  Write-Host "Verified all $($skills.Count) installed skills against $source."
} else {
  Write-Host "PowerToys dashboard skills are available under $Destination."
}
