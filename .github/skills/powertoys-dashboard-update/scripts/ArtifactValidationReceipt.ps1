function Get-ArtifactContentDigest {
    param([Parameter(Mandatory)]$Artifact)
    function ConvertTo-CanonicalValue($Value) {
        if ($Value -is [Collections.IDictionary] -or $Value -is [pscustomobject]) {
            $names = [string[]]@(if ($Value -is [Collections.IDictionary]) {
                $Value.Keys
            } else {
                $Value.PSObject.Properties | ForEach-Object { $_.Name }
            })
            [Array]::Sort($names, [StringComparer]::Ordinal)
            $result = [ordered]@{}
            foreach ($name in $names) {
                $result[$name] = ConvertTo-CanonicalValue $Value.$name
            }
            return [pscustomobject]$result
        }
        if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
            $items = [Collections.Generic.List[object]]::new()
            foreach ($item in $Value) { $items.Add((ConvertTo-CanonicalValue $item)) }
            return ,$items.ToArray()
        }
        return $Value
    }
    $json = ConvertTo-Json -InputObject (ConvertTo-CanonicalValue $Artifact) -Depth 60 -Compress
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($json))).ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}
