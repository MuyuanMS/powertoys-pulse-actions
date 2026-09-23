[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'Common.ps1')
$root = Get-PulseRoot
if (-not (Test-Path -LiteralPath $root)) { Write-Output 'Pulse Host is not installed.'; return }
$maintenance = Enter-PulseMaintenance $root 'uninstall'
try {
    Assert-PulseIdle $root ''
    $manifest = Join-Path $root 'native\com.powertoys.pulse.json'
    foreach ($registry in Get-PulseRegistryPaths) {
        if (Test-Path -LiteralPath $registry) {
            $registered = (Get-Item -LiteralPath $registry).GetValue('')
            if ($registered -and ([string]$registered).Equals($manifest, [StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $registry -Recurse -Force
            }
        }
    }
    foreach ($child in @('bin', 'native', 'installation.json')) {
        $path = Assert-PulseChildPath (Join-Path $root $child) $root
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    # Backups and staging folders are created only by this installer and never contain task data.
    Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -match '^bin\.(?:previous|staging)-[0-9a-f]{32}$' } | ForEach-Object {
        Remove-Item -LiteralPath (Assert-PulseChildPath $_.FullName $root) -Recurse -Force
    }
    Write-Output "Pulse Host unregistered. Configuration, task records, and logs remain at $root. User repositories and CLIs were not removed."
}
finally { Exit-PulseMaintenance $root $maintenance }
