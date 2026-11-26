[CmdletBinding()]
param(
    [Parameter(Mandatory)][int[]]$Numbers,
    [Parameter(Mandatory)][string]$CandidateDataPath,
    [Parameter(Mandatory)][string]$ValidationReceiptPath,
    [Parameter(Mandatory)][string]$LiveMetadataPath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '.github\skills\powertoys-dashboard-update\scripts\Assert-CanonicalDashboardTarget.ps1') `
    -Dashboard $PSScriptRoot | Out-Null
. (Join-Path $PSScriptRoot 'Emit.Scoped.ps1')
Invoke-ScopedActionDataGeneration -Dashboard $PSScriptRoot -Numbers $Numbers `
    -CandidateDataPath $CandidateDataPath -ValidationReceiptPath $ValidationReceiptPath `
    -LiveMetadataPath $LiveMetadataPath -ValidateOnly:$ValidateOnly
