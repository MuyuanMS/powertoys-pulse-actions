[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Dashboard
)

$ErrorActionPreference = 'Stop'
$expectedRepository = 'MuyuanMS/powertoys-pulse-actions'
$expectedBranch = 'main'
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

if ($repository -ine $expectedRepository) {
    throw "Refusing dashboard update for '$repository'. The canonical target is '$expectedRepository'."
}

$branch = (& git -C $Dashboard branch --show-current 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
    throw "Refusing dashboard update from a detached HEAD. The canonical publication branch is '$expectedBranch'."
}

if ($branch -cne $expectedBranch) {
    throw "Refusing dashboard update from branch '$branch'. The canonical publication branch is '$expectedBranch'."
}

[pscustomobject]@{
    dashboard = $Dashboard
    repository = $repository
    branch = $branch
    origin_url = $originUrl
}
