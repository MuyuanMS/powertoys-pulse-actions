<#
.SYNOPSIS
    Resolve the current teammate's PowerToys fork configuration.
.DESCRIPTION
    Detects the fork owner (from the authenticated gh account), the fork repo,
    the git remote that points at the fork, and a local clone path. Nothing is
    tied to a specific account -- 'gh api user' resolves whoever is logged in.
.EXAMPLE
    ./Get-ForkConfig.ps1
    Prints the resolved configuration and returns it as an object.
#>
[CmdletBinding()]
param(
    [string] $ClonePath,
    [string] $ForkRepo = $env:POWERTOYS_FORK_REPO
)

$ErrorActionPreference = 'Stop'

if (-not $ClonePath) {
    $ClonePath = @(
        'C:\PowerToys',
        "$env:USERPROFILE\source\repos\PowerToys",
        "$env:USERPROFILE\git\PowerToys"
    ) | Where-Object { Test-Path "$_\.git" } | Select-Object -First 1
}

$remoteRows = if ($ClonePath) {
    @(git -C $ClonePath remote -v 2>$null)
} else {
    @()
}
$forkRemoteRow = $remoteRows |
    Select-String 'github.com[:/](?<owner>[^/\s]+)/PowerToys(?:\.git)?' |
    Where-Object { $_.Matches[0].Groups['owner'].Value -ne 'microsoft' } |
    Select-Object -First 1

if (-not $ForkRepo -and $forkRemoteRow) {
    $remoteOwner = $forkRemoteRow.Matches[0].Groups['owner'].Value
    $ForkRepo = "$remoteOwner/PowerToys"
}
if (-not $ForkRepo) {
    $currentLogin = (gh api user --jq '.login').Trim()
    if (-not $currentLogin) {
        throw "Could not resolve the GitHub login. Run 'gh auth login' first."
    }
    $ForkRepo = "$currentLogin/PowerToys"
}

$repoInfo = gh repo view $ForkRepo --json nameWithOwner,isFork,viewerPermission 2>$null |
    ConvertFrom-Json
if (-not $repoInfo -or -not $repoInfo.isFork) {
    throw "Configured review repository '$ForkRepo' is not an accessible PowerToys fork."
}

$forkOwner = ([string]$repoInfo.nameWithOwner -split '/', 2)[0]
$writePermissions = @('ADMIN', 'MAINTAIN', 'WRITE')
if ([string]$repoInfo.viewerPermission -notin $writePermissions) {
    $activeLogin = (gh api user --jq '.login').Trim()
    if ($activeLogin -ne $forkOwner) {
        gh auth switch --user $forkOwner | Out-Null
        $repoInfo = gh repo view $ForkRepo --json nameWithOwner,isFork,viewerPermission 2>$null |
            ConvertFrom-Json
    }
}
if ([string]$repoInfo.viewerPermission -notin $writePermissions) {
    throw "The active GitHub account cannot write '$ForkRepo'. Set POWERTOYS_FORK_REPO to a writable fork or authenticate its owner."
}

$forkRemote = if ($forkRemoteRow) {
    ($forkRemoteRow.Line -split '\s+')[0]
} else {
    'fork'
}

$config = [pscustomobject]@{
    ForkOwner  = $forkOwner
    ForkRepo   = [string]$repoInfo.nameWithOwner
    ForkRemote = $forkRemote
    ClonePath  = $ClonePath
    AuthUser   = (gh api user --jq '.login').Trim()
}
Write-Host "Fork owner: $forkOwner | repo: $($config.ForkRepo) | remote: $forkRemote | clone: $ClonePath | auth: $($config.AuthUser)"
return $config
