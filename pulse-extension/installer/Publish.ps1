[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\host-win-x64'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\host\Pulse.Host.csproj'))
$destination = [IO.Path]::GetFullPath($OutputDirectory)
# Run this only for final acceptance or when explicitly requested; it performs a build.
$publishArguments = @('publish', $project, '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '--output', $destination)
if ($env:CODEX_HOST_NUGET_CONFIG) {
    $publishArguments += "-p:RestoreConfigFile=$env:CODEX_HOST_NUGET_CONFIG"
}
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw "Host publish failed (exit $LASTEXITCODE)." }
Write-Output "Self-contained Windows Host published to $destination"
