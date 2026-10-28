<#
.SYNOPSIS
Download one exact-head PowerToys PR CI installer and its SHA256 manifest, not the full artifact.
.EXAMPLE
.\Get-PrCiInstaller.ps1 -PrNumber 51599 -Architecture x64 -Scope User -Destination C:\Temp\pr51599
.EXAMPLE
.\Get-PrCiInstaller.ps1 -PrNumber 51599 -BuildId 391858 -ExpectedHead abef3393d77d25cdbe133d37f1ef0b8ea5e74f1c -Destination C:\Temp\historical-pr51599
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int]$PrNumber,
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [ValidateSet('User', 'Machine')][string]$Scope = 'User',
    [ValidateRange(1, [int]::MaxValue)][int]$BuildId,
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedHead,
    [Parameter(Mandatory)][string]$Destination
)

$ErrorActionPreference = 'Stop'
$Architecture = if ($Architecture -ieq 'ARM64') { 'ARM64' } else { 'x64' }
$base = 'https://dev.azure.com/shine-oss/62769a65-d989-45e3-803c-9ad21fcf2ea2/_apis/build'
if (-not $ExpectedHead) {
    $pr = Invoke-RestMethod "https://api.github.com/repos/microsoft/PowerToys/pulls/$PrNumber"
    $ExpectedHead = $pr.head.sha
}
if ($ExpectedHead -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'The PR source head could not be established.'
}

$branch = "refs/pull/$PrNumber/merge"
$build = $null
if ($BuildId) {
    $build = Invoke-RestMethod "$base/builds/${BuildId}?api-version=7.1"
} else {
    $continuation = $null
    do {
        $query = "$base/builds?branchName=$([uri]::EscapeDataString($branch))&queryOrder=queueTimeDescending&`$top=100&api-version=7.1"
        if ($continuation) { $query += "&continuationToken=$([uri]::EscapeDataString($continuation))" }
        $page = Invoke-WebRequest $query
        $matches = @(($page.Content | ConvertFrom-Json).value | Where-Object {
            $_.sourceBranch -ceq $branch -and
            $_.triggerInfo.'pr.number' -eq "$PrNumber" -and
            $_.triggerInfo.'pr.sourceSha' -ieq $ExpectedHead -and
            $_.status -eq 'completed' -and $_.result -eq 'succeeded'
        })
        if ($matches.Count) { $build = $matches[0] }
        $continuation = $page.Headers['x-ms-continuationtoken'] -join ''
    } while (-not $build -and $continuation)
}
if (-not $build -or $build.sourceBranch -cne $branch -or
    $build.triggerInfo.'pr.number' -ne "$PrNumber" -or
    $build.triggerInfo.'pr.sourceSha' -ine $ExpectedHead -or
    $build.status -ne 'completed' -or $build.result -ne 'succeeded' -or
    $build.sourceVersion -notmatch '^[0-9a-fA-F]{40}$') {
    throw "No successful matching build for PR #$PrNumber at $ExpectedHead. No older-head fallback is allowed."
}

$artifactName = "build-$($Architecture.ToLowerInvariant())-Release"
$artifact = Invoke-RestMethod "$base/builds/$($build.id)/artifacts?artifactName=$artifactName&api-version=7.1"
$download = [uri]$artifact.resource.downloadUrl
if ($artifact.name -cne $artifactName -or $download.Scheme -ne 'https' -or
    -not $download.IsDefaultPort -or $download.UserInfo -or
    $download.Host -notmatch '(^|\.)artifacts\.visualstudio\.com$' -or
    $download.Query -notmatch '(^|[?&])format=zip(&|$)') {
    throw 'Unexpected artifact identity or download host/format.'
}
$timeline = Invoke-RestMethod "$base/builds/$($build.id)/timeline?api-version=7.1"
$prefix = if ($Scope -eq 'User') { 'PowerToysUserSetup' } else { 'PowerToysSetup' }
$pattern = '(?<![A-Za-z0-9])' + $prefix + '-[0-9]+(?:\.[0-9]+){2,3}-' +
    [regex]::Escape($Architecture) + '\.exe'
$names = @(foreach ($stage in @($timeline.records | Where-Object { $_.name -eq 'Stage Installers' -and $_.log.id })) {
    $log = Invoke-RestMethod "$base/builds/$($build.id)/logs/$($stage.log.id)?api-version=7.1"
    foreach ($match in [regex]::Matches([string]$log, $pattern, 'IgnoreCase')) { $match.Value }
}) | Sort-Object -Unique
if (@($names).Count -ne 1) {
    throw 'Expected exactly one installer filename in the build staging logs; refusing to guess.'
}
$filename = [string]$names
$hashName = "hash_$($Scope.ToLowerInvariant())_$($Architecture.ToLowerInvariant()).txt"
$destinationPath = [IO.Path]::GetFullPath($Destination)
New-Item $destinationPath -ItemType Directory -Force | Out-Null
$installerPath = Join-Path $destinationPath $filename
$hashPath = Join-Path $destinationPath $hashName
$receiptPath = Join-Path $destinationPath 'download-receipt.json'
foreach ($path in @($installerPath, $hashPath, $receiptPath)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite existing file: $path" }
}
foreach ($entry in @(
    @{ Name = $hashName; Path = $hashPath },
    @{ Name = $filename; Path = $installerPath }
)) {
    $url = $download.AbsoluteUri -replace '([?&])format=zip(?=&|$)',
        ('$1format=file&subPath=' + [uri]::EscapeDataString('/' + $entry.Name))
    Invoke-WebRequest $url -OutFile $entry.Path
}
$expectedHash = (Get-Content $hashPath -Raw).Trim()
$actualHash = (Get-FileHash $installerPath -Algorithm SHA256).Hash
if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or $actualHash -ine $expectedHash) {
    throw "Installer SHA256 mismatch. Do not execute $installerPath."
}
$signature = Get-AuthenticodeSignature $installerPath
$receipt = [ordered]@{
    PrNumber = $PrNumber
    SourceHead = $ExpectedHead
    BuildId = $build.id
    MergeSha = $build.sourceVersion
    BuildUrl = "https://dev.azure.com/shine-oss/62769a65-d989-45e3-803c-9ad21fcf2ea2/_build/results?buildId=$($build.id)"
    Architecture = $Architecture
    Scope = $Scope
    Artifact = $artifactName
    Installer = $installerPath
    Bytes = (Get-Item $installerPath).Length
    Sha256 = $actualHash
    SignatureStatus = [string]$signature.Status
    DownloadedAt = [datetime]::UtcNow.ToString('o')
    Readiness = 'Download and integrity only. No installation or E2E readiness asserted. Unsigned CI packages may fail Settings IPC and MSIX registration.'
}
$receipt | ConvertTo-Json | Set-Content $receiptPath
[pscustomobject]$receipt
