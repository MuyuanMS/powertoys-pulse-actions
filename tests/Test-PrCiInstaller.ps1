$ErrorActionPreference = 'Stop'
$script:tool = Join-Path (Split-Path $PSScriptRoot -Parent) 'Get-PrCiInstaller.ps1'
$script:temp = Join-Path ([IO.Path]::GetTempPath()) ("ci-installer-test-" + [guid]::NewGuid().ToString('N'))
New-Item $script:temp -ItemType Directory | Out-Null
$script:head = 'a' * 40
$script:merge = 'b' * 40
$script:hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('installer fixture')))
$script:requests = [Collections.Generic.List[string]]::new()
$global:PrCiInstallerTest = @{
    Head=$script:head; Merge=$script:merge; Hash=$script:hash
    Requests=$script:requests; Mode=''; Arch='x64'
}

function Invoke-RestMethod {
    param([string]$Uri)
    $state = $global:PrCiInstallerTest
    $state.Requests.Add($Uri)
    if ($Uri -like 'https://api.github.com/*') { return @{head=@{sha=$state.Head}} }
    if ($Uri -match '/logs/') {
        return "Copying PowerToysUserSetup-0.0.1-$($state.Arch).exe and PowerToysSetup-0.0.1-$($state.Arch).exe"
    }
    if ($Uri -match '/timeline') { return @{records=@(@{name='Stage Installers';log=@{id=1}})} }
    if ($Uri -match '/artifacts') {
        $hostName = if ($state.Mode -eq 'host') { 'example.com' } else { 'artprodcus3.artifacts.visualstudio.com' }
        return @{name="build-$($state.Arch.ToLowerInvariant())-Release";resource=@{downloadUrl="https://$hostName/content?format=zip"}}
    }
    $sourceHead = if ($state.Mode -eq 'head') { 'c' * 40 } else { $state.Head }
    return @{id=10;sourceBranch='refs/pull/42/merge';sourceVersion=$state.Merge;status='completed';result='succeeded';triggerInfo=@{'pr.number'='42';'pr.sourceSha'=$sourceHead}}
}
function Invoke-WebRequest {
    param([string]$Uri,[string]$OutFile)
    $state = $global:PrCiInstallerTest
    $state.Requests.Add($Uri)
    if (-not $OutFile) {
        if ($state.Mode -eq 'paged' -and $Uri -notmatch 'continuationToken') {
            return @{Content='{"value":[]}';Headers=@{'x-ms-continuationtoken'='page2'}}
        }
        return @{Content=(@{value=@(Invoke-RestMethod 'build')}|ConvertTo-Json -Depth 6);Headers=@{}}
    }
    if ($Uri -notmatch 'format=file&subPath=') { throw 'Full artifact download attempted.' }
    if ($Uri -match 'hash_') {
        $text = if ($state.Mode -eq 'hash') { '0' * 64 } else { $state.Hash }
        [IO.File]::WriteAllText($OutFile,$text)
    } else { [IO.File]::WriteAllText($OutFile,'installer fixture') }
}
function Get-AuthenticodeSignature { param($FilePath) [pscustomobject]@{Status='NotSigned'} }
function Assert($Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
try {
    $result = & $script:tool -PrNumber 42 -Destination "$script:temp\success"
    Assert ($result.Sha256 -eq $script:hash -and $result.MergeSha -eq $script:merge) 'Provenance/hash receipt incorrect.'
    Assert (@($script:requests | Where-Object { $_ -match 'format=file' }).Count -eq 2) 'Expected exactly two single-file transfers.'
    Assert ($result.SignatureStatus -eq 'NotSigned') 'Unsigned status was hidden.'
    foreach ($failure in @('head','host','hash')) {
        $global:PrCiInstallerTest.Mode = $failure
        $failed = $false
        try { & $script:tool -PrNumber 42 -BuildId 10 -ExpectedHead $script:head -Destination "$script:temp\$failure" | Out-Null }
        catch { $failed = $true }
        Assert $failed "Expected rejection for $failure."
    }
    $global:PrCiInstallerTest.Mode = 'paged'
    $global:PrCiInstallerTest.Arch = 'ARM64'
    $script:requests.Clear()
    $result = & $script:tool -PrNumber 42 -Architecture arm64 -Scope Machine -Destination "$script:temp\arm64"
    Assert ($result.Installer -clike '*PowerToysSetup-0.0.1-ARM64.exe') 'Wrong ARM64/scope installer.'
    Assert (@($script:requests | Where-Object { $_ -cmatch 'hash_machine_arm64.txt' }).Count -eq 1) 'ARM64 hash filename casing changed.'
    Assert (@($script:requests | Where-Object { $_ -match 'continuationToken=page2' }).Count -eq 1) 'Build pagination ignored.'
    $global:PrCiInstallerTest.Mode = ''
    $global:PrCiInstallerTest.Arch = 'x64'
    $failed = $false
    try { & $script:tool -PrNumber 42 -Destination "$script:temp\success" | Out-Null }
    catch { $failed = $true }
    Assert $failed 'Existing installer was overwritten.'
    'PASS: exact head, historical build, pagination, scope/architecture/case, selective files, SHA256, signature reporting, unsafe-host/stale-head/hash rejection and no-overwrite.'
} finally {
    Remove-Item -LiteralPath $script:temp -Recurse -Force
    Remove-Variable PrCiInstallerTest -Scope Global
}
