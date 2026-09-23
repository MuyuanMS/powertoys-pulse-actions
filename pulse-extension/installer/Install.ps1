[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedHostDirectory,
    [Alias('ExtensionId')][string[]]$ExtensionIds = @('nlpkbkhlnocknpgkjnmhpahapffdhblo'),
    [switch]$DevelopmentOrigins
)

. (Join-Path $PSScriptRoot 'Common.ps1')
if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') { throw 'Pulse Host requires Windows 11 x64.' }
$ids = @($ExtensionIds | Select-Object -Unique)
if ($ids.Count -eq 0 -or @($ids | Where-Object { $_ -cnotmatch '^[a-p]{32}$' }).Count -gt 0) {
    throw 'Provide each actual Chrome/Edge extension ID: exactly 32 lowercase letters a through p.'
}
$source = (Resolve-Path -LiteralPath $PublishedHostDirectory).ProviderPath
$sourceHost = Join-Path $source 'Pulse.Host.exe'
if (-not (Test-Path -LiteralPath $sourceHost -PathType Leaf)) { throw 'PublishedHostDirectory must contain the published Pulse.Host.exe.' }
if (-not (Test-Path -LiteralPath (Join-Path $source 'coreclr.dll') -PathType Leaf)) {
    throw 'Use the self-contained win-x64 publish output from Publish.ps1; an installed .NET runtime must not be required.'
}
$root = Get-PulseRoot
$bin = Assert-PulseChildPath (Join-Path $root 'bin') $root
$staging = Assert-PulseChildPath (Join-Path $root ('bin.staging-' + [Guid]::NewGuid().ToString('N'))) $root
$backup = Assert-PulseChildPath (Join-Path $root ('bin.previous-' + [Guid]::NewGuid().ToString('N'))) $root
if ($source.Equals($bin, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish to a separate directory before installing.' }
[IO.Directory]::CreateDirectory($staging) | Out-Null
$maintenance = $null
$movedOld = $false
$movedNew = $false
$metadataSnapshot = @{}
$registrySnapshot = @{}
$manifest = Join-Path $root 'native\com.powertoys.pulse.json'
$installation = Join-Path $root 'installation.json'
try {
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $staging -Recurse -Force
    $maintenance = Enter-PulseMaintenance $root 'install'
    Assert-PulseIdle $root (Join-Path $staging 'Pulse.Host.exe')
    foreach ($path in @($manifest, $installation)) {
        $metadataSnapshot[$path] = if (Test-Path -LiteralPath $path -PathType Leaf) { [IO.File]::ReadAllBytes($path) } else { $null }
    }
    foreach ($registry in Get-PulseRegistryPaths) {
        $registrySnapshot[$registry] = if (Test-Path -LiteralPath $registry) { (Get-Item -LiteralPath $registry).GetValue('') } else { $null }
    }
    if (Test-Path -LiteralPath $bin) {
        Move-PulseInstalledBin $root $backup (Join-Path $staging 'Pulse.Host.exe')
        $movedOld = $true
    }
    Move-Item -LiteralPath $staging -Destination $bin
    $movedNew = $true
    Write-PulseJson $manifest @{
        name = 'com.powertoys.pulse'; description = 'PowerToys Pulse local task Host';
        path = (Join-Path $bin 'Pulse.Host.exe'); type = 'stdio';
        allowed_origins = @($ids | ForEach-Object { "chrome-extension://$_/" })
    }
    Write-PulseJson $installation @{
        extensionIds = $ids; developmentOrigins = [bool]$DevelopmentOrigins;
        installedAt = [DateTime]::UtcNow.ToString('O'); hostPath = (Join-Path $bin 'Pulse.Host.exe');
        manifestPath = $manifest; browsers = @('chrome', 'edge')
    }
    foreach ($registry in Get-PulseRegistryPaths) {
        New-Item -Path $registry -Force | Out-Null
        Set-Item -LiteralPath $registry -Value $manifest
    }
    if ($movedOld) {
        try { Remove-Item -LiteralPath (Assert-PulseChildPath $backup $root) -Recurse -Force }
        catch { Write-Warning "The new Host is installed. A previous binary is still in use; close browsers and delete only this backup directory: $backup" }
    }
    Write-Output "Pulse Host registered for Chrome and Edge: $manifest"
    Write-Output 'CLI authentication, repositories, task history, and configuration are retained. Reload the extension after installation.'
}
catch {
    foreach ($path in $metadataSnapshot.Keys) {
        if ($null -ne $metadataSnapshot[$path]) { [IO.File]::WriteAllBytes($path, $metadataSnapshot[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath (Assert-PulseChildPath $path $root) -Force }
    }
    foreach ($registry in $registrySnapshot.Keys) {
        if ($null -ne $registrySnapshot[$registry]) {
            New-Item -Path $registry -Force | Out-Null
            Set-Item -LiteralPath $registry -Value $registrySnapshot[$registry]
        }
        elseif (Test-Path -LiteralPath $registry) { Remove-Item -LiteralPath $registry -Recurse -Force }
    }
    if ($movedOld -and (Test-Path -LiteralPath $backup)) {
        $failed = $null
        if ($movedNew -and (Test-Path -LiteralPath $bin)) {
            # A browser may have connected to the new binary before metadata or
            # registration failed. Drain that connection before restoring the old bin.
            $failed = Assert-PulseChildPath (Join-Path $root ('bin.staging-' + [Guid]::NewGuid().ToString('N'))) $root
            Move-PulseInstalledBin $root $failed '' -ActivityChecker (Join-Path $backup 'Pulse.Host.exe')
        }
        Move-Item -LiteralPath (Assert-PulseChildPath $backup $root) -Destination $bin
        if ($null -ne $failed) {
            try { Remove-Item -LiteralPath (Assert-PulseChildPath $failed $root) -Recurse -Force }
            catch { Write-Warning "The previous Host was restored. Failed update files remain at: $failed" }
        }
    }
    elseif ($movedNew -and (Test-Path -LiteralPath $bin)) {
        $failed = Assert-PulseChildPath (Join-Path $root ('bin.staging-' + [Guid]::NewGuid().ToString('N'))) $root
        Move-PulseInstalledBin $root $failed '' -ActivityChecker $sourceHost
        Remove-Item -LiteralPath (Assert-PulseChildPath $failed $root) -Recurse -Force
    }
    throw
}
finally {
    if ($null -ne $maintenance) { Exit-PulseMaintenance $root $maintenance }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath (Assert-PulseChildPath $staging $root) -Recurse -Force }
}
