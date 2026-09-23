[CmdletBinding()]
param([string]$PublishedHostDirectory)

# Standalone Windows PowerShell 5.1 scenarios. Uses already-published binaries only.
# Every process, installation path, registry override, and file mutation belongs to this fixture.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PublishedHostDirectory) {
    $PublishedHostDirectory = Join-Path $repository 'artifacts\Pulse-Extension-0.2.0-local-win-x64\host'
}
$published = (Resolve-Path -LiteralPath $PublishedHostDirectory).ProviderPath
$publishedHost = Join-Path $published 'Pulse.Host.exe'
if (-not (Test-Path -LiteralPath $publishedHost -PathType Leaf) -or -not (Test-Path -LiteralPath (Join-Path $published 'coreclr.dll') -PathType Leaf)) {
    throw 'These scenarios require the existing self-contained published Host; no build or installation of dependencies is performed.'
}
if ($env:OS -ne 'Windows_NT') { throw 'Installer process-lock scenarios require Windows.' }
. (Join-Path $repository 'installer\Common.ps1')
if (-not (Get-Command Move-PulseInstalledBin -ErrorAction SilentlyContinue)) { throw 'Move-PulseInstalledBin is required before running these scenarios.' }

$suiteParent = [IO.Path]::GetFullPath((Join-Path $repository '.tmp'))
$suiteRoot = Join-Path $suiteParent ('installer-scenarios-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($suiteRoot) | Out-Null
$script:fixtureProcesses = New-Object 'System.Collections.Generic.List[object]'
$script:passed = 0
$script:fixtureFailure = $null

function Assert-FixturePath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Path]::GetFullPath($suiteRoot).TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture path escaped its isolated suite directory.' }
    return $absolute
}

function Assert-Scenario([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:passed++
    Write-Output "PASS: $Message"
}

function Expect-FixtureFailure([scriptblock]$Action, [string]$Message, [string]$Pattern = '') {
    $failure = $null
    try { & $Action | Out-Null }
    catch { $failure = $_.Exception.Message }
    Assert-Scenario ($null -ne $failure) $Message
    if ($Pattern) { Assert-Scenario ($failure -match $Pattern) "$Message (expected failure reason)" }
}

function Copy-FixtureHost([string]$Root) {
    $bin = Assert-FixturePath (Join-Path $Root 'bin')
    [IO.Directory]::CreateDirectory($bin) | Out-Null
    Get-ChildItem -LiteralPath $published -Force | Copy-Item -Destination $bin -Recurse -Force
    [IO.File]::WriteAllText((Join-Path $bin 'previous-version-only.txt'), 'Previous fixture installation; must be restored on rollback.')
    return (Join-Path $bin 'Pulse.Host.exe')
}

function New-Fixture([string]$Name) {
    $root = Assert-FixturePath (Join-Path $suiteRoot $Name)
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $hostPath = Copy-FixtureHost $root
    $runId = [Guid]::NewGuid().ToString('D')
    $run = Join-Path $root "runs\$runId"
    [IO.Directory]::CreateDirectory($run) | Out-Null
    $created = [DateTime]::UtcNow.AddHours(-1).ToString('O')
    Write-PulseJson (Join-Path $root 'config.json') @{ agent = 'codex'; permission = 'read-only'; mainRepoFolder = ''; worktreeRoot = ''; githubAccount = ''; fixture = 'retained config' }
    Write-PulseJson (Join-Path $run 'task.json') @{ runId = $runId; task = @{ requestId = $runId; actionId = 'installer-fixture'; actionKind = 'issue-fix'; repository = 'microsoft/powertoys'; prompt = 'Preserve this saved fixture.' }; config = @{ agent = 'codex'; repositoryKey = 'installer-fixture' }; sourceOrigin = 'https://example.invalid'; createdAt = $created }
    Write-PulseJson (Join-Path $run 'status.json') @{ state = 'failed'; createdAt = $created; endedAt = $created; updatedAt = $created; exitCode = 7 }
    Write-PulseJson (Join-Path $run 'result.json') @{ summary = 'Retained fixture failure'; needsReview = $true }
    [IO.File]::WriteAllText((Join-Path $run 'stdout.jsonl'), "{`"type`":`"fixture`",`"text`":`"saved stdout`"}`n")
    [IO.File]::WriteAllText((Join-Path $run 'stderr.log'), "saved stderr`r`n")
    Write-PulseJson (Join-Path $root 'installation.json') @{ extensionIds = @('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'); fixture = 'old installation metadata' }
    Write-PulseJson (Join-Path $root 'native\com.powertoys.pulse.json') @{ name = 'com.powertoys.pulse'; path = $hostPath; fixture = 'old native metadata' }
    $preserved = @((Join-Path $root 'config.json')) + @(Get-ChildItem -LiteralPath $run -File | ForEach-Object { $_.FullName })
    return [pscustomobject]@{ Root = $root; HostPath = $hostPath; Run = $run; Preserved = $preserved }
}

function Get-FixtureHashes([string[]]$Paths) {
    $hashes = @{}
    foreach ($path in $Paths) { $hashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    return $hashes
}

function Assert-FixtureHashes([hashtable]$Before, [string]$Message) {
    $same = $true
    foreach ($path in $Before.Keys) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Before[$path]) { $same = $false }
    }
    Assert-Scenario $same $Message
}

function Quote-FixtureArgument([string]$Value) {
    # ProcessStartInfo.Arguments uses Windows command-line quoting, never a shell.
    # All generated arguments are known fixture paths/flags; embedded quotes are forbidden.
    if ($Value -match '["\r\n]') { throw 'Unexpected quote or line break in a fixture process argument.' }
    if ($Value.Length -gt 0 -and $Value -notmatch '\s') { return $Value }
    return '"' + ([regex]::Replace($Value, '(\\+)$', '$1$1')) + '"'
}

function Start-FixtureConnection([string]$HostPath, [string[]]$Arguments) {
    $hostPath = Assert-FixturePath $HostPath
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $hostPath
    $start.Arguments = (($Arguments | ForEach-Object { Quote-FixtureArgument $_ }) -join ' ')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $record = [pscustomobject]@{ Process = $process; Path = $hostPath; StartTime = $process.StartTime.ToUniversalTime() }
    $script:fixtureProcesses.Add($record)
    if ($process.WaitForExit(600)) { throw ('Fixture native connection exited before the lock scenario: ' + $process.StandardError.ReadToEnd()) }
    return $record
}

function Test-FixtureConnectionAlive([object]$Record) {
    $Record.Process.Refresh()
    return -not $Record.Process.HasExited
}

function Stop-FixtureConnection([object]$Record) {
    $process = $Record.Process
    if (-not (Test-FixtureConnectionAlive $Record)) { return }
    # The test owns this original process object, path, PID/start time, and stdin handle.
    Assert-FixturePath $Record.Path | Out-Null
    if ($process.StartTime.ToUniversalTime() -ne $Record.StartTime) { throw 'Fixture process identity changed; refusing to stop it.' }
    try { $process.StandardInput.Close() } catch [IO.IOException] { }
    if (-not $process.WaitForExit(3000)) { $process.Kill(); if (-not $process.WaitForExit(5000)) { throw 'An owned fixture process could not be stopped.' } }
}

function Assert-FixtureExecutableLocked([string]$Path) {
    $locked = $false
    try { $handle = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $handle.Dispose() }
    catch [IO.IOException] { $locked = $true }
    Assert-Scenario $locked 'The live native connection holds the installed executable open.'
}

function New-FixtureInstaller([string]$Root, [switch]$FailMetadata, [switch]$CorruptNewBinary) {
    $directory = Assert-FixturePath (Join-Path $Root 'test-installer')
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'installer\Install.ps1') -Destination (Join-Path $directory 'Install.ps1')
    $text = [IO.File]::ReadAllText((Join-Path $repository 'installer\Common.ps1'))
    $escapedRoot = $Root.Replace("'", "''")
    $text += "`r`nfunction Get-PulseRoot { return '$escapedRoot' }`r`nfunction Get-PulseRegistryPaths { return @() }`r`n"
    if ($FailMetadata) {
        $text += if ($CorruptNewBinary) { "`r`n`$script:FixtureCorruptNewBinary = `$true`r`n" } else { "`r`n`$script:FixtureCorruptNewBinary = `$false`r`n" }
        $text += @'
$script:FixtureOriginalWritePulseJson = ${function:Write-PulseJson}
function Write-PulseJson([string]$Path, [object]$Value) {
    if ([IO.Path]::GetFileName($Path) -eq 'installation.json') {
        if ($script:FixtureCorruptNewBinary) {
            [IO.File]::WriteAllBytes((Join-Path ([IO.Path]::GetDirectoryName($Path)) 'bin\Pulse.Host.exe'), [byte[]](0, 1, 2, 3))
        }
        throw 'Injected fixture installation metadata write failure.'
    }
    & $script:FixtureOriginalWritePulseJson $Path $Value
}
'@
    }
    [IO.File]::WriteAllText((Join-Path $directory 'Common.ps1'), $text, [Text.UTF8Encoding]::new($false))
    return (Join-Path $directory 'Install.ps1')
}

try {
    $commandRoot = Join-Path $suiteRoot 'command parser fixture'
    $commandHost = Join-Path $commandRoot 'bin\Pulse.Host.exe'
    $prefix = '"' + $commandHost + '" '
    foreach ($tail in @('chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/', 'chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/ --parent-window=123', ('--stdio --data-root "' + $commandRoot + '"'), ('--stdio --data-root "' + $commandRoot + '" --development'))) {
        Assert-Scenario (Test-PulseConnectionCommand ($prefix + $tail) $commandHost $commandRoot) 'The connection classifier accepts a known browser or exact-root stdio command.'
    }
    foreach ($tail in @('chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/ --worker', 'chrome-extension://invalid/ --parent-window=1', ('--stdio --data-root "' + $commandRoot + '-other"'), ('--stdio --data-root "' + $commandRoot + '" --unknown'), ('--worker fixture --data-root "' + $commandRoot + '"'), ('--agent-test fixture --data-root "' + $commandRoot + '"'))) {
        Assert-Scenario (-not (Test-PulseConnectionCommand ($prefix + $tail) $commandHost $commandRoot)) 'The connection classifier rejects worker, diagnostic, unknown, and other-root commands.'
    }
    $upgrade = New-Fixture 'upgrade with spaces'
    $foreign = New-Fixture 'other installation'
    $connection = Start-FixtureConnection $upgrade.HostPath @('--stdio', '--data-root', $upgrade.Root)
    $foreignConnection = Start-FixtureConnection $foreign.HostPath @('--stdio', '--data-root', $foreign.Root)
    Assert-FixtureExecutableLocked $upgrade.HostPath
    $hashes = Get-FixtureHashes $upgrade.Preserved
    $installer = New-FixtureInstaller $upgrade.Root
    & $installer -PublishedHostDirectory $published -ExtensionIds @('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa') | Out-Null
    Assert-Scenario (-not (Test-FixtureConnectionAlive $connection)) 'Upgrade stops the idle connection from the exact installed binary.'
    Assert-Scenario (Test-FixtureConnectionAlive $foreignConnection) 'Upgrade leaves a Host at a different binary path running.'
    Assert-Scenario ((Test-Path -LiteralPath $upgrade.HostPath -PathType Leaf) -and -not (Test-Path -LiteralPath (Join-Path $upgrade.Root 'bin\previous-version-only.txt'))) 'Upgrade installs the staged binaries successfully.'
    Assert-FixtureHashes $hashes 'Upgrade preserves saved config, task records, results, and both logs byte-for-byte.'
    Assert-Scenario (-not (Test-Path -LiteralPath (Join-Path $upgrade.Root 'maintenance.json'))) 'Upgrade releases its maintenance record.'
    Stop-FixtureConnection $foreignConnection

    $guard = New-Fixture 'active action guard'
    $guardConnection = Start-FixtureConnection $guard.HostPath @('--stdio', '--data-root', $guard.Root)
    $guardInstaller = New-FixtureInstaller $guard.Root
    $operationPath = Join-Path $guard.Root 'web-actions\operations\fixture.json'
    Write-PulseJson $operationPath @{ status = 'submitting' }
    Expect-FixtureFailure { & $guardInstaller -PublishedHostDirectory $published } 'An active GitHub action blocks upgrade before stopping the connection.' 'in progress|active'
    Assert-Scenario (Test-FixtureConnectionAlive $guardConnection) 'The active-action guard preserves the installed native connection.'
    [IO.File]::WriteAllText($operationPath, '{ unreadable fixture')
    Expect-FixtureFailure { & $guardInstaller -PublishedHostDirectory $published } 'Unreadable action state blocks upgrade.' 'unreadable'
    Assert-Scenario (Test-FixtureConnectionAlive $guardConnection) 'Unreadable action state preserves the installed native connection.'
    Remove-Item -LiteralPath (Assert-FixturePath $operationPath) -Force
    $destination = Join-Path $guard.Root 'bin.previous-test'
    Expect-FixtureFailure { Move-PulseInstalledBin $guard.Root $destination $publishedHost } 'Moving installed binaries requires owned maintenance.' 'maintenance'
    Assert-Scenario (Test-FixtureConnectionAlive $guardConnection) 'Missing maintenance cannot stop a native connection.'
    Write-PulseJson (Join-Path $guard.Root 'maintenance.json') @{ pid = $guardConnection.Process.Id; startTimeUtc = $guardConnection.StartTime.ToString('O'); operation = 'foreign-fixture-owner' }
    Expect-FixtureFailure { Move-PulseInstalledBin $guard.Root $destination $publishedHost } 'A maintenance record owned by another process cannot authorize connection shutdown.' 'maintenance'
    Assert-Scenario (Test-FixtureConnectionAlive $guardConnection) 'Foreign-owned maintenance preserves the native connection.'
    Remove-Item -LiteralPath (Assert-FixturePath (Join-Path $guard.Root 'maintenance.json')) -Force
    $busyLock = Open-PulseLock $guard.Root 'config'
    $busyMaintenance = Enter-PulseMaintenance $guard.Root 'install-fixture'
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        Expect-FixtureFailure { Move-PulseInstalledBin $guard.Root $destination $publishedHost } 'An in-progress configuration publication blocks connection shutdown.' 'busy'
        $timer.Stop()
        Assert-Scenario ($timer.Elapsed.TotalSeconds -ge 9 -and $timer.Elapsed.TotalSeconds -lt 25) 'The configuration lock wait is bounded.'
        Assert-Scenario (Test-FixtureConnectionAlive $guardConnection) 'A busy configuration lock does not kill the connection publishing it.'
    }
    finally { Exit-PulseMaintenance $guard.Root $busyMaintenance; $busyLock.Dispose() }
    Stop-FixtureConnection $guardConnection

    $unknown = New-Fixture 'unclassified same binary'
    foreach ($extra in @('--worker', '--agent-test', '--unknown-mode')) {
        $unknownConnection = Start-FixtureConnection $unknown.HostPath @('--stdio', '--data-root', $unknown.Root, $extra)
        $maintenance = Enter-PulseMaintenance $unknown.Root 'install-fixture'
        try {
            Assert-PulseIdle $unknown.Root $publishedHost
            Expect-FixtureFailure { Move-PulseInstalledBin $unknown.Root (Join-Path $unknown.Root 'bin.previous-test') $publishedHost } "Same-binary $extra arguments block replacement."
            Assert-Scenario (Test-FixtureConnectionAlive $unknownConnection) "Same-binary $extra process is never killed by the installer."
        }
        finally { Exit-PulseMaintenance $unknown.Root $maintenance; Stop-FixtureConnection $unknownConnection }
    }
    $customRoot = Assert-FixturePath (Join-Path $suiteRoot 'custom data root')
    $customConnection = Start-FixtureConnection $unknown.HostPath @('--stdio', '--data-root', $customRoot)
    $maintenance = Enter-PulseMaintenance $unknown.Root 'install-fixture'
    try {
        Assert-PulseIdle $unknown.Root $publishedHost
        Expect-FixtureFailure { Move-PulseInstalledBin $unknown.Root (Join-Path $unknown.Root 'bin.previous-test') $publishedHost } 'The same binary serving another data root blocks replacement.'
        Assert-Scenario (Test-FixtureConnectionAlive $customConnection) 'A custom-data-root connection is never killed by the installer.'
    }
    finally { Exit-PulseMaintenance $unknown.Root $maintenance; Stop-FixtureConnection $customConnection }

    $rollback = New-Fixture 'rollback fixture'
    $rollbackConnection = Start-FixtureConnection $rollback.HostPath @('--stdio', '--data-root', $rollback.Root, '--development')
    $rollbackPaths = $rollback.Preserved + @($rollback.HostPath, (Join-Path $rollback.Root 'installation.json'), (Join-Path $rollback.Root 'native\com.powertoys.pulse.json'), (Join-Path $rollback.Root 'bin\previous-version-only.txt'))
    $rollbackHashes = Get-FixtureHashes $rollbackPaths
    $rollbackInstaller = New-FixtureInstaller $rollback.Root -FailMetadata
    Expect-FixtureFailure { & $rollbackInstaller -PublishedHostDirectory $published } 'A metadata write failure triggers installation rollback.' 'Injected fixture'
    Assert-Scenario (-not (Test-FixtureConnectionAlive $rollbackConnection)) 'Rollback fixture stopped the owned idle connection before replacement.'
    Assert-FixtureHashes $rollbackHashes 'Rollback restores the old binary directory and registration metadata while preserving config, tasks, and logs.'
    Assert-Scenario ((Test-Path -LiteralPath $rollback.HostPath -PathType Leaf) -and -not (Test-Path -LiteralPath (Join-Path $rollback.Root 'maintenance.json'))) 'Rollback leaves the prior Host installed and releases maintenance.'

    $corrupted = New-Fixture 'corrupted new binary rollback'
    $corruptedConnection = Start-FixtureConnection $corrupted.HostPath @('--stdio', '--data-root', $corrupted.Root)
    $corruptedPaths = $corrupted.Preserved + @($corrupted.HostPath, (Join-Path $corrupted.Root 'installation.json'), (Join-Path $corrupted.Root 'native\com.powertoys.pulse.json'), (Join-Path $corrupted.Root 'bin\previous-version-only.txt'))
    $corruptedHashes = Get-FixtureHashes $corruptedPaths
    $corruptedInstaller = New-FixtureInstaller $corrupted.Root -FailMetadata -CorruptNewBinary
    Expect-FixtureFailure { & $corruptedInstaller -PublishedHostDirectory $published } 'Rollback remains possible when the replacement executable is corrupt.' 'Injected fixture'
    Assert-Scenario (-not (Test-FixtureConnectionAlive $corruptedConnection)) 'Corrupted-replacement rollback stops only the original owned connection.'
    Assert-FixtureHashes $corruptedHashes 'A corrupt replacement is rolled back using the preserved Host, restoring original binary and metadata hashes.'
    Assert-PulseIdle $corrupted.Root $publishedHost
    Assert-Scenario (-not (Test-Path -LiteralPath (Join-Path $corrupted.Root 'maintenance.json'))) 'The restored Host is executable and the corrupt-replacement rollback releases maintenance.'

    Write-Output "Installer scenarios passed: $script:passed checks. Only isolated fixture files and processes were used; registry access was disabled."
}
catch { $script:fixtureFailure = $_; throw }
finally {
    foreach ($record in $script:fixtureProcesses) {
        try { Stop-FixtureConnection $record } catch { Write-Warning ('Fixture cleanup could not stop its own process: ' + $_.Exception.Message) }
        $record.Process.Dispose()
    }
    $absoluteSuite = [IO.Path]::GetFullPath($suiteRoot)
    $expectedParent = [IO.Path]::GetFullPath($suiteParent).TrimEnd('\') + '\'
    if ($absoluteSuite.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($absoluteSuite) -match '^installer-scenarios-[0-9a-f]{32}$') {
        if (Test-Path -LiteralPath $absoluteSuite) { Remove-Item -LiteralPath $absoluteSuite -Recurse -Force }
    }
}
