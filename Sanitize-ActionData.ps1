param(
  [string]$DataPath = (Join-Path $PSScriptRoot 'data')
)

$ErrorActionPreference = 'Stop'
$blockedProperties = @(
  'internal_evidence',
  'internalEvidence',
  'worktree',
  'evidenceDirectory'
)

function Convert-PublicValue {
  param($Value)

  if ($null -eq $Value) {
    return $null
  }

  if ($Value -is [string]) {
    return $Value `
      -replace 'C:\\PowerToys-[^\\]+\\', '<PowerToysCheckout>\' `
      -replace 'C:\\PowerToys\\', '<PowerToysCheckout>\' `
      -replace 'C:\\powertoys-triage-board-source\\[^"''\r\n]*', '<local-artifact>'
  }

  if ($Value -is [System.Collections.IDictionary]) {
    $result = [ordered]@{}
    foreach ($key in $Value.Keys) {
      if ($blockedProperties -contains [string]$key) {
        continue
      }
      $result[$key] = Convert-PublicValue $Value[$key]
    }
    return [pscustomobject]$result
  }

  if ($Value -is [pscustomobject]) {
    $result = [ordered]@{}
    foreach ($property in $Value.PSObject.Properties) {
      if ($blockedProperties -contains $property.Name) {
        continue
      }
      $result[$property.Name] = Convert-PublicValue $property.Value
    }
    return [pscustomobject]$result
  }

  if ($Value -is [System.Collections.IEnumerable]) {
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $Value) {
      $items.Add((Convert-PublicValue $item))
    }
    return ,$items.ToArray()
  }

  return $Value
}

$itemsPath = Join-Path $DataPath 'items'
if (-not (Test-Path $itemsPath)) {
  throw "Action-data items directory not found: $itemsPath"
}

$encoding = [System.Text.UTF8Encoding]::new($false)
$count = 0
foreach ($path in Get-ChildItem $itemsPath -Filter '*.json') {
  $artifact = Get-Content $path.FullName -Raw | ConvertFrom-Json
  $publicArtifact = Convert-PublicValue $artifact
  $json = $publicArtifact | ConvertTo-Json -Depth 30
  [System.IO.File]::WriteAllText($path.FullName, $json, $encoding)
  $count++
}

Write-Host "Sanitized $count public action artifact(s)."
