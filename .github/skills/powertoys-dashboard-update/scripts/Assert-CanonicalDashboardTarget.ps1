[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Dashboard,
    [string]$ExpectedRepository = 'MuyuanMS/powertoys-pulse-actions'
)

$ErrorActionPreference = 'Stop'
$Dashboard = (Resolve-Path $Dashboard).Path
if (-not (Test-Path (Join-Path $Dashboard '.git'))) {
    throw "Dashboard path is not a Git repository: $Dashboard"
}

$originUrl = (& git -C $Dashboard remote get-url origin 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($originUrl)) {
    throw "Dashboard repository has no readable origin remote: $Dashboard"
}

$repository = $originUrl `
    -replace '^https://github\.com/', '' `
    -replace '^git@github\.com:', '' `
    -replace '^ssh://git@github\.com/', '' `
    -replace '\.git$', '' `
    -replace '/$', ''

if ($repository -ine $ExpectedRepository) {
    throw "Refusing dashboard update for '$repository'. The canonical target is '$ExpectedRepository'."
}

[pscustomobject]@{
    dashboard = $Dashboard
    repository = $repository
    origin_url = $originUrl
}
