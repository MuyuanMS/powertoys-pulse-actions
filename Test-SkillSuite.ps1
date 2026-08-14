$ErrorActionPreference = 'Stop'
$skillsRoot = Join-Path $PSScriptRoot '.github\skills'
$dataRoot = Join-Path $PSScriptRoot 'data'
$requiredSkills = @(
  'powertoys-dashboard-update',
  'powertoys-pr-review',
  'powertoys-issue-to-design',
  'powertoys-design-to-pr'
)

$errors = [System.Collections.Generic.List[string]]::new()
foreach ($skill in $requiredSkills) {
  $skillFile = Join-Path $skillsRoot "$skill\SKILL.md"
  if (-not (Test-Path $skillFile)) {
    $errors.Add("Missing required skill entry point: $skillFile")
  }
}

$forbiddenFiles = Get-ChildItem $skillsRoot -Recurse -File | Where-Object {
  $_.Name -match '^review-data-\d+\.json$|^syncfork-\d+\.json$' -or
  $_.FullName -match '\\assets\\dashboard-v3\\data\\items\\'
}
foreach ($file in $forbiddenFiles) {
  $errors.Add("Generated run artifact must not be packaged: $($file.FullName)")
}

$indexPath = Join-Path $dataRoot 'index.json'
$itemsPath = Join-Path $dataRoot 'items'
if (-not (Test-Path $indexPath)) {
  $errors.Add("Missing canonical action-data index: $indexPath")
}
if (-not (Test-Path $itemsPath)) {
  $errors.Add("Missing canonical action-data directory: $itemsPath")
}
if (Test-Path $indexPath) {
  try {
    $index = Get-Content $indexPath -Raw | ConvertFrom-Json
    foreach ($number in @($index.artifact_numbers)) {
      $artifactPath = Join-Path $itemsPath "$number.json"
      if (-not (Test-Path $artifactPath)) {
        $errors.Add("Manifest artifact is missing: $artifactPath")
        continue
      }
      try {
        $artifactText = Get-Content $artifactPath -Raw
        $artifact = $artifactText | ConvertFrom-Json
        if ($artifact.number -ne $number -or
            $artifact.kind -notin @('issue', 'pr') -or
            $artifactText -notmatch '"actions"\s*:\s*\[') {
          $errors.Add("Manifest artifact has an invalid Pulse action schema: $artifactPath")
        }
      } catch {
        $errors.Add("Invalid manifest artifact $artifactPath`: $($_.Exception.Message)")
      }
    }
  } catch {
    $errors.Add("Invalid canonical action-data index: $($_.Exception.Message)")
  }
}

$privateArtifactPattern =
  'internal_evidence|internalEvidence|evidenceDirectory|"worktree"\s*:|' +
  'C:\\PowerToys(?:-|\\)|C:\\powertoys-triage-board-source|' +
  'ghp_|github_pat_|Bearer\s+[A-Za-z0-9._-]+'
foreach ($file in Get-ChildItem $itemsPath -Filter '*.json' -ErrorAction SilentlyContinue) {
  try {
    Get-Content $file.FullName -Raw | ConvertFrom-Json | Out-Null
  } catch {
    $errors.Add("Invalid action artifact $($file.FullName): $($_.Exception.Message)")
    continue
  }
  foreach ($match in Select-String -Path $file.FullName -Pattern $privateArtifactPattern) {
    $errors.Add("Private or machine-specific content in $($file.FullName):$($match.LineNumber)")
  }
}

$forbiddenText = 'powertoys-daily-maintenance|\$HOME\\\.copilot\\skills'
foreach ($file in Get-ChildItem $skillsRoot -Recurse -File -Include *.md,*.ps1) {
  if ($file.FullName -eq $PSCommandPath) { continue }
  $matches = Select-String -Path $file.FullName -Pattern $forbiddenText
  foreach ($match in $matches) {
    $errors.Add("External user-profile dependency in $($file.FullName):$($match.LineNumber)")
  }
}

$scripts = @(
  Get-ChildItem $skillsRoot -Recurse -Filter *.ps1
  Get-ChildItem $PSScriptRoot -File -Filter *.ps1
)
foreach ($script in $scripts) {
  $tokens = $null
  $parseErrors = $null
  [System.Management.Automation.Language.Parser]::ParseFile(
    $script.FullName,
    [ref]$tokens,
    [ref]$parseErrors
  ) | Out-Null
  foreach ($parseError in @($parseErrors)) {
    $errors.Add("$($script.FullName): $($parseError.Message)")
  }
}

if ($errors.Count -gt 0) {
  $errors | ForEach-Object { Write-Error $_ -ErrorAction Continue }
  throw "Skill suite validation failed with $($errors.Count) error(s)."
}

Write-Host "Skill suite validated: $($requiredSkills.Count) skills, $($scripts.Count) PowerShell files."
