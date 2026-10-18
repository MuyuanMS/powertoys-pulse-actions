$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot '..\..\..\..\Install-Skills.ps1'
$source = Join-Path $PSScriptRoot '..\..'
$root = Join-Path ([IO.Path]::GetTempPath()) "powertoys-install-$([guid]::NewGuid().ToString('N'))"
$destination = Join-Path $root 'skills'
$script:assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    $failure = ''
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-True ($failure -match $Pattern) "Expected $Pattern, got '$failure'"
}
try {
    & $installer -Destination $destination
    & $installer -Destination $destination -VerifyOnly
    Assert-True (@(Get-ChildItem $destination -Directory).Count -eq 4) 'all four execution skills installed'
    $entry = Join-Path $destination 'powertoys-pr-review\SKILL.md'
    Add-Content -LiteralPath $entry 'local override that must not be lost'
    $override = Get-Content $entry -Raw
    Assert-Fails { & $installer -Destination $destination -VerifyOnly } 'differs from source'
    $backup = Join-Path $root 'backup'
    & $installer -Destination $destination -Update -BackupDirectory $backup
    Assert-True ((Get-Content (Join-Path $backup 'powertoys-pr-review\SKILL.md') -Raw) -ceq $override) 'update preserves old local changes in backup'
    Assert-True ((Get-FileHash $entry).Hash -eq (Get-FileHash (Join-Path $source 'powertoys-pr-review\SKILL.md')).Hash) 'update installs canonical PR execution instructions'
    & $installer -Destination $destination -VerifyOnly
    Set-Content (Join-Path $destination 'powertoys-pr-review\untracked.txt') 'local extra'
    Assert-Fails { & $installer -Destination $destination -VerifyOnly } 'untracked override'
    Remove-Item -LiteralPath (Join-Path $destination 'powertoys-pr-review\untracked.txt')
    Remove-Item -LiteralPath (Join-Path $destination 'powertoys-design-to-pr\SKILL.md')
    Assert-Fails { & $installer -Destination $destination -VerifyOnly } 'differs from source'
    $missing = Join-Path $root 'absent'
    Assert-Fails { & $installer -Destination $missing -VerifyOnly } 'differs from source'
    Assert-True (-not (Test-Path $missing)) 'verification does not create or change installation'
    Assert-Fails { & $installer -Destination $source -Update } 'separate from the skill source'
    Assert-Fails { & $installer -Destination $destination -Update -BackupDirectory (Join-Path $destination 'backups') } 'outside the installation'
    Write-Host "Skill installation tests passed ($script:assertions assertions)."
} finally {
    if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
