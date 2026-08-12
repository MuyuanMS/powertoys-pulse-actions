$ErrorActionPreference = 'Stop'
$skillsRoot = Join-Path $PSScriptRoot '.github\skills'
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

$forbiddenText = 'powertoys-daily-maintenance|\$HOME\\\.copilot\\skills'
foreach ($file in Get-ChildItem $skillsRoot -Recurse -File -Include *.md,*.ps1) {
  if ($file.FullName -eq $PSCommandPath) { continue }
  $matches = Select-String -Path $file.FullName -Pattern $forbiddenText
  foreach ($match in $matches) {
    $errors.Add("External user-profile dependency in $($file.FullName):$($match.LineNumber)")
  }
}

$scripts = Get-ChildItem $skillsRoot -Recurse -Filter *.ps1
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
