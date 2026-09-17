[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Checkout,
    [Parameter(Mandatory)][string]$HeadSha,
    [Parameter(Mandatory)][string[]]$Path,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$Checkout = (Resolve-Path -LiteralPath $Checkout).Path
if ([string]$HeadSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'HeadSha must be a 40-character commit SHA.'
}

function Read-GitBlobBytes {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Object
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.WorkingDirectory = $Repository
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    [void]$startInfo.ArgumentList.Add('cat-file')
    [void]$startInfo.ArgumentList.Add('blob')
    [void]$startInfo.ArgumentList.Add($Object)

    $process = [Diagnostics.Process]::Start($startInfo)
    $memory = [IO.MemoryStream]::new()
    try {
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "git cat-file failed for ${Object}: $errorText"
        }
        return $memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $process.Dispose()
    }
}

$fileResults = foreach ($filePath in $Path | Sort-Object -Unique) {
    $normalizedPath = $filePath.Replace('\', '/')
    $bytes = Read-GitBlobBytes -Repository $Checkout -Object "${HeadSha}:$normalizedPath"
    $crlf = 0
    $lfOnly = 0
    $crOnly = 0
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        if ($bytes[$index] -eq 13) {
            if ($index + 1 -lt $bytes.Length -and $bytes[$index + 1] -eq 10) {
                $crlf++
                $index++
            }
            else {
                $crOnly++
            }
        }
        elseif ($bytes[$index] -eq 10) {
            $lfOnly++
        }
    }

    $styles = @(
        if ($crlf -gt 0) { 'crlf' }
        if ($lfOnly -gt 0) { 'lf' }
        if ($crOnly -gt 0) { 'cr' }
    )
    $lineEndings = if ($styles.Count -eq 0) {
        'none'
    }
    elseif ($styles.Count -eq 1) {
        $styles[0]
    }
    else {
        'mixed'
    }

    [pscustomobject]@{
        path = $normalizedPath
        result = if ($lineEndings -eq 'mixed') { 'failed' } else { 'passed' }
        line_endings = $lineEndings
        utf8_bom = (
            $bytes.Length -ge 3 -and
            $bytes[0] -eq 0xEF -and
            $bytes[1] -eq 0xBB -and
            $bytes[2] -eq 0xBF
        )
        crlf_count = $crlf
        lf_only_count = $lfOnly
        cr_only_count = $crOnly
    }
}

$result = [pscustomobject]@{
    head_sha = $HeadSha.ToLowerInvariant()
    result = if (@($fileResults | Where-Object result -eq 'failed').Count -gt 0) {
        'failed'
    }
    else {
        'passed'
    }
    files = @($fileResults)
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 5
}
else {
    $result
}

if ($result.result -ne 'passed') {
    throw 'Apply-ready suggestions are unsafe for mixed-line-ending blobs; use inline prose or a companion comment instead.'
}
