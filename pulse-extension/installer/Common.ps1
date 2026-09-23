Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PulseRoot {
    $path = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PulseExtension'
    return [IO.Path]::GetFullPath($path)
}

function Assert-PulseChildPath([string]$Path, [string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a file operation outside the Pulse installation: $resolved"
    }
    return $resolved
}

function Open-PulseLock([string]$Root, [string]$Key) {
    $directory = Join-Path $Root 'locks'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Key)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $path = Join-Path $directory "$hash.lock"
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        try { return [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Pulse is busy. Wait for the current settings/install operation to finish and retry.' }
            Start-Sleep -Milliseconds 50
        }
    } while ($true)
}

function Write-PulseJson([string]$Path, [object]$Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
        # Windows PowerShell 5.1 otherwise converts $null to an empty backup path.
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, [Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $Path) }
    }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Enter-PulseMaintenance([string]$Root, [string]$Operation) {
    $maintenanceLock = Open-PulseLock $Root 'maintenance'
    try {
        $admission = Open-PulseLock $Root 'accept'
        try {
            $owner = Get-Process -Id $PID
            Write-PulseJson (Join-Path $Root 'maintenance.json') @{
                pid = $PID; startTimeUtc = $owner.StartTime.ToUniversalTime().ToString('O'); operation = $Operation
            }
        }
        finally { $admission.Dispose() }
        return $maintenanceLock
    }
    catch { $maintenanceLock.Dispose(); throw }
}

function Exit-PulseMaintenance([string]$Root, [IDisposable]$MaintenanceLock) {
    try {
        $admission = Open-PulseLock $Root 'accept'
        try { [IO.File]::Delete((Join-Path $Root 'maintenance.json')) }
        finally { $admission.Dispose() }
    }
    finally { $MaintenanceLock.Dispose() }
}

function Assert-PulseIdle([string]$Root, [string]$FallbackHost, [string]$ActivityChecker = '') {
    # Older installed Hosts predate standalone website action records. Check these
    # before invoking either version so an upgrade cannot overlook a live write.
    $webActions = Join-Path $Root 'web-actions\operations'
    if (Test-Path -LiteralPath $webActions) {
        foreach ($recordFile in Get-ChildItem -LiteralPath $webActions -Filter '*.json' -File) {
            try { $record = Get-Content -LiteralPath $recordFile.FullName -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
            catch { throw 'A website action record is unreadable. Repair its local storage before upgrading or uninstalling.' }
            if ($record.status -eq 'submitting') {
                throw 'Pulse has a GitHub action in progress or awaiting recovery. Open its confirmation page to check the result before upgrading or uninstalling.'
            }
        }
    }
    $existingHost = Join-Path $Root 'bin\Pulse.Host.exe'
    if ($ActivityChecker -and -not (Test-Path -LiteralPath $ActivityChecker -PathType Leaf)) {
        throw 'The rollback activity checker is missing. Preserve the installation and backup files.'
    }
    $checker = if ($ActivityChecker) { $ActivityChecker }
        elseif (Test-Path -LiteralPath $existingHost -PathType Leaf) { $existingHost } else { $FallbackHost }
    if (-not $checker -or -not (Test-Path -LiteralPath $checker -PathType Leaf)) {
        $runs = Join-Path $Root 'runs'
        if ((Test-Path -LiteralPath $runs) -and @(Get-ChildItem -LiteralPath $runs -Directory).Count -gt 0) {
            throw 'The existing Host is missing but task records remain. Repair with a published Host before uninstalling.'
        }
        return
    }
    & $checker '--has-active' '--data-root' $Root
    $result = $LASTEXITCODE
    if ($result -eq 2) { throw 'Pulse has an active task or GitHub submission. Finish or cancel it before upgrading or uninstalling.' }
    if ($result -ne 0) { throw "Pulse could not confirm that task execution is idle (exit $result). Preserve the task records and repair the Host first." }
}

function Get-PulseRegistryPaths {
    return @(
        'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.powertoys.pulse',
        'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.powertoys.pulse'
    )
}

function Assert-PulseMaintenanceOwner([string]$Root) {
    try {
        $record = Get-Content -LiteralPath (Join-Path $Root 'maintenance.json') -Raw | ConvertFrom-Json
        $owner = Get-Process -Id $PID
        # PowerShell 7 can materialize JSON dates as DateTime; converting those
        # back to strings loses fractional seconds and breaks process identity.
        $startedTicks = if ($record.startTimeUtc -is [DateTime]) { $record.startTimeUtc.ToUniversalTime().Ticks }
            elseif ($record.startTimeUtc -is [DateTimeOffset]) { $record.startTimeUtc.UtcDateTime.Ticks }
            else { ([DateTimeOffset]::Parse($record.startTimeUtc)).UtcDateTime.Ticks }
        if ($record.pid -eq $PID -and $startedTicks -eq $owner.StartTime.ToUniversalTime().Ticks) { return }
    }
    catch { }
    throw 'The installer must own the maintenance guard before closing Host connections or replacing binaries.'
}

function Test-PulseConnectionCommand([string]$CommandLine, [string]$Executable, [string]$Root) {
    # Accept connection modes only. Workers, agent tests, and other data roots must
    # never be terminated merely because their executable has the same filename.
    $exePattern = [Regex]::Escape($Executable)
    $rootPattern = [Regex]::Escape([IO.Path]::GetFullPath($Root).TrimEnd('\'))
    $browser = 'chrome-extension://[a-p]{32}/(?:\s+--parent-window=\d+)?'
    $stdio = '--stdio\s+--data-root\s+(?:"' + $rootPattern + '"|' + $rootPattern + ')(?:\s+--development)?'
    $pattern = '^\s*(?:"' + $exePattern + '"|' + $exePattern + ')\s+(?:' + $browser + '|' + $stdio + ')\s*$'
    return [Regex]::IsMatch($CommandLine, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

function Stop-PulseIdleConnections([string]$Root, [string]$FallbackHost, [string]$ActivityChecker = '') {
    Assert-PulseMaintenanceOwner $Root
    Assert-PulseIdle $Root $FallbackHost $ActivityChecker
    $executable = Assert-PulseChildPath (Join-Path $Root 'bin\Pulse.Host.exe') $Root
    $connections = [Collections.Generic.List[Diagnostics.Process]]::new()
    $configLock = $null
    $promptLock = $null
    try {
        # Older Hosts can save settings and finish an already-started prompt sync
        # during maintenance. Let these atomic publications finish before exit.
        $configLock = Open-PulseLock $Root 'config'
        $promptLock = Open-PulseLock $Root 'prompt-catalog-sync'
        foreach ($candidate in Get-CimInstance Win32_Process -Filter "Name = 'Pulse.Host.exe'") {
            if (-not $candidate.ExecutablePath -or
                -not $candidate.ExecutablePath.Equals($executable, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $process = $null
            try {
                try { $process = [Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId) }
                catch [ArgumentException] { continue } # Already disconnected.
                # Retain the process handle, then re-read its command line. If the
                # PID is recycled, HasExited refers to the retained original handle.
                $null = $process.Handle
                if ($process.HasExited) { continue }
                $identity = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)"
                if ($process.HasExited) { continue }
                if ($null -eq $identity -or -not $identity.ExecutablePath -or
                    -not $identity.ExecutablePath.Equals($executable, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'A Host process changed while checking its identity. Retry the installation.'
                }
                if (-not (Test-PulseConnectionCommand $identity.CommandLine $executable $Root)) {
                    throw "Pulse Host process $($process.Id) is a worker, diagnostic, or unrecognized connection. It was not stopped. Wait for it to exit before updating."
                }
                $connections.Add($process)
                $process = $null
            }
            finally { if ($null -ne $process) { $process.Dispose() } }
        }
        # Validate every candidate before stopping any of them. The maintenance
        # guard prevents a new task or GitHub submission from starting meanwhile.
        foreach ($connection in $connections) {
            if ($connection.HasExited) { continue }
            Write-Verbose "Closing idle Pulse Host connection $($connection.Id)."
            try { $connection.Kill() }
            catch { if (-not $connection.HasExited) { throw } }
            if (-not $connection.WaitForExit(5000)) { throw "Pulse Host connection $($connection.Id) did not exit. Its files were not replaced." }
        }
    }
    finally {
        foreach ($connection in $connections) { $connection.Dispose() }
        if ($null -ne $promptLock) { $promptLock.Dispose() }
        if ($null -ne $configLock) { $configLock.Dispose() }
    }
}

function Move-PulseInstalledBin([string]$Root, [string]$Destination, [string]$FallbackHost, [string]$ActivityChecker = '') {
    $bin = Assert-PulseChildPath (Join-Path $Root 'bin') $Root
    $target = Assert-PulseChildPath $Destination $Root
    if (Test-Path -LiteralPath $target) { throw 'The Host backup destination must not already exist.' }
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        Stop-PulseIdleConnections $Root $FallbackHost $ActivityChecker
        try {
            Move-Item -LiteralPath $bin -Destination $target -ErrorAction Stop
            return
        }
        catch {
            if ($_.Exception -isnot [IO.IOException] -and $_.Exception -isnot [UnauthorizedAccessException]) { throw }
            if ($attempt -eq 9) {
                throw "The installed Host files are still in use or inaccessible. Preserve the installation and any rollback folders; task data is retained. Details: $($_.Exception.Message)"
            }
            # A browser may reconnect while Windows releases the old image. Only
            # recheck known idle connections; never terminate a browser or CLI.
            Start-Sleep -Milliseconds 200
        }
    }
}
