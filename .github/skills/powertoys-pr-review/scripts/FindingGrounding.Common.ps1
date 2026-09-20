function Get-FindingBodyHash {
    param([AllowEmptyString()][string]$Body)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Body.Replace("`r`n", "`n"))
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-GroundingSource {
    param([string]$HeadSha, [string]$Path, [string]$SourceRepository)
    if ($SourceRepository) {
        $lines = @(& git -C $SourceRepository show "${HeadSha}:$Path" 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "Cannot read pinned blob ${HeadSha}:$Path" }
        return $lines -join "`n"
    }
    $encodedPath = (($Path -split '/') | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
    $json = & gh api "repos/microsoft/PowerToys/contents/${encodedPath}?ref=$HeadSha"
    if ($LASTEXITCODE -ne 0) { throw "Cannot read upstream blob ${HeadSha}:$Path" }
    $blob = ($json -join "`n") | ConvertFrom-Json
    if ($blob.encoding -ne 'base64' -or -not $blob.content) {
        throw "Upstream blob ${HeadSha}:$Path has no readable base64 content"
    }
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($blob.content))
}

function Test-FindingGrounding {
    param(
        [AllowEmptyCollection()][object[]]$Items,
        [AllowNull()]$Grounding,
        [string]$HeadSha,
        [switch]$CheckSources,
        [string]$SourceRepository,
        [scriptblock]$SourceReader
    )
    $errors = [Collections.Generic.List[string]]::new()
    $itemsToCheck = @($Items | Where-Object { $null -ne $_ })
    if (-not $itemsToCheck.Count) { return @() }
    if ($null -eq $Grounding -or $Grounding.version -ne 1 -or
        [string]$Grounding.head_sha -cne $HeadSha -or $HeadSha -notmatch '^[a-f0-9]{40}$') {
        return @('finding grounding requires version 1 and the exact 40-character upstream head_sha')
    }
    $findings = @($Grounding.findings | Where-Object { $null -ne $_ })
    $expected = @($itemsToCheck | ForEach-Object { [string]$_.id } | Sort-Object)
    $actual = @($findings | ForEach-Object { [string]$_.id } | Sort-Object)
    if (($expected -join "`n") -cne ($actual -join "`n") -or
        @($expected | Select-Object -Unique).Count -ne $expected.Count -or
        @($expected | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -match '[\r\n]' }).Count) {
        $errors.Add('finding grounding IDs must match the proposed items exactly, without duplicates')
    }
    $cache = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($item in $itemsToCheck) {
        $label = "finding '$($item.id)'"
        $finding = @($findings | Where-Object { [string]$_.id -ceq [string]$item.id })
        if ($finding.Count -ne 1) { continue }
        $finding = $finding[0]
        if ([string]$finding.origin -cne 'upstream') {
            $errors.Add("$label originated outside upstream; review-introduced, speculative and rejected findings cannot be published")
        }
        if ([string]$finding.body_sha256 -cne (Get-FindingBodyHash ([string]$item.body))) {
            $errors.Add("$label body changed after grounding; re-read upstream and revalidate the rewritten claims")
        }
        foreach ($field in @('claim', 'upstream_failure', 'counterevidence', 'why_not_already_fixed', 'verification')) {
            if ([string]::IsNullOrWhiteSpace([string]$finding.$field)) {
                $errors.Add("$label missing $field")
            }
        }
        $sources = @($finding.sources | Where-Object { $null -ne $_ })
        if (-not $sources.Count) { $errors.Add("$label needs at least one pinned upstream source range") }
        foreach ($source in $sources) {
            $path = [string]$source.path
            if (-not $path -or $path -match '[:\\]' -or $path.StartsWith('/') -or
                @($path -split '/' | Where-Object { $_ -in @('', '.', '..') }).Count) {
                $errors.Add("$label invalid repository-relative source path '$path'")
                continue
            }
            $start = 0
            $end = 0
            if (-not [int]::TryParse([string]$source.start_line, [ref]$start) -or
                -not [int]::TryParse([string]$source.end_line, [ref]$end) -or
                $start -lt 1 -or $end -lt $start) {
                $errors.Add("$label invalid source line range for '$path'")
                continue
            }
            $excerpt = ([string]$source.excerpt).Replace("`r`n", "`n")
            if ([string]::IsNullOrWhiteSpace($excerpt)) {
                $errors.Add("$label missing source excerpt for '$path'")
                continue
            }
            foreach ($symbol in @($source.symbols)) {
                if (-not [string]::IsNullOrWhiteSpace([string]$symbol) -and
                    -not $excerpt.Contains([string]$symbol, [StringComparison]::Ordinal)) {
                    $errors.Add("$label referenced symbol '$symbol' is absent from its source excerpt")
                }
            }
            if ($CheckSources) {
                try {
                    if (-not $cache.ContainsKey($path)) {
                        $text = if ($SourceReader) { & $SourceReader $HeadSha $path }
                            else { Get-GroundingSource $HeadSha $path $SourceRepository }
                        $cache[$path] = ([string]$text).Replace("`r`n", "`n") -split "`n"
                    }
                    $lines = $cache[$path]
                    if ($end -gt $lines.Count -or
                        ($lines[($start - 1)..($end - 1)] -join "`n") -cne $excerpt) {
                        $errors.Add("$label excerpt does not match pinned upstream blob '$path' lines $start-$end")
                    }
                } catch {
                    $errors.Add("$label source verification failed: $($_.Exception.Message)")
                }
            }
        }
    }
    return $errors.ToArray()
}
