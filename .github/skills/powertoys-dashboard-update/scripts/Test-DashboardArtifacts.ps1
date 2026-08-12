param(
  [string]$Dashboard = $(if ($env:POWERTOYS_DASHBOARD_PATH) {
    $env:POWERTOYS_DASHBOARD_PATH
  } else {
    Join-Path $PSScriptRoot '..\..\..\..'
  }),
  [int[]]$Numbers,
  [switch]$RequireDetailedDesign
)

$ErrorActionPreference = 'Stop'
$Dashboard = (Resolve-Path $Dashboard).Path
$itemsPath = Join-Path $Dashboard 'data\items'
if (-not (Test-Path $itemsPath)) {
  throw "Dashboard artifact directory not found: $itemsPath"
}

$allowedJudgments = @(
  'actionable_design',
  'needs_information',
  'duplicate_or_handled',
  'waiting_on_author',
  'not_actionable'
)

$errors = [System.Collections.Generic.List[string]]::new()
$paths = if ($Numbers) {
  foreach ($number in $Numbers | Sort-Object -Unique) {
    $path = Join-Path $itemsPath "$number.json"
    if (-not (Test-Path $path)) {
      $errors.Add("Issue/PR $number has no artifact at $path")
    } else {
      Get-Item $path
    }
  }
} else {
  Get-ChildItem $itemsPath -Filter '*.json'
}

function Require-Text {
  param($Value, [string]$Field, [string]$Prefix)
  if ([string]::IsNullOrWhiteSpace([string]$Value)) {
    $script:errors.Add("$Prefix missing $Field")
  }
}

function Require-Date {
  param($Value, [string]$Field, [string]$Prefix)
  $parsed = [datetime]::MinValue
  if (-not [datetime]::TryParse([string]$Value, [ref]$parsed)) {
    $script:errors.Add("$Prefix has invalid or missing $Field")
  }
}

foreach ($path in @($paths)) {
  try {
    $artifact = Get-Content $path.FullName -Raw | ConvertFrom-Json
  } catch {
    $errors.Add("$($path.Name) is not valid JSON: $($_.Exception.Message)")
    continue
  }

  $prefix = $path.Name
  $fileNumber = 0
  [void][int]::TryParse($path.BaseName, [ref]$fileNumber)
  if ([int]$artifact.number -ne $fileNumber) {
    $errors.Add("$prefix number does not match its filename")
  }
  if ($artifact.kind -notin @('issue', 'pr')) {
    $errors.Add("$prefix kind must be issue or pr")
  }
  Require-Date $artifact.generated_at 'generated_at' $prefix
  Require-Date $artifact.evaluated_at 'evaluated_at' $prefix
  Require-Date $artifact.source_updated_at 'source_updated_at' $prefix

  if ($artifact.kind -eq 'pr' -and $artifact.track -eq 'review') {
    Require-Text $artifact.head_sha 'head_sha' $prefix
  }

  if ($artifact.kind -ne 'issue') {
    continue
  }

  if (-not $artifact.judgment) {
    $errors.Add("$prefix missing judgment")
  } else {
    if ($artifact.judgment.status -notin $allowedJudgments) {
      $errors.Add("$prefix has invalid judgment.status '$($artifact.judgment.status)'")
    }
    Require-Text $artifact.judgment.rationale 'judgment.rationale' $prefix
    Require-Text $artifact.judgment.recommended_action 'judgment.recommended_action' $prefix
    if (@($artifact.judgment.evidence).Count -eq 0) {
      $errors.Add("$prefix missing judgment.evidence")
    }
  }

  $requiresDesign = $RequireDetailedDesign -and (
    $artifact.design -or
    $artifact.stage -in @('awaiting_design_approval', 'design_approved', 'pr_open_fork', 'awaiting_pr_approval')
  )
  if (-not $requiresDesign) {
    continue
  }

  if (-not $artifact.design) {
    $errors.Add("$prefix is at a design stage but has no design")
    continue
  }

  Require-Text $artifact.design.root_cause 'design.root_cause' $prefix
  if (@($artifact.design.evidence).Count -eq 0) {
    $errors.Add("$prefix missing design.evidence")
  }
  if (@($artifact.design.affected_files).Count -eq 0) {
    $errors.Add("$prefix missing design.affected_files")
  }
  foreach ($file in @($artifact.design.affected_files)) {
    Require-Text $file.path 'design.affected_files[].path' $prefix
    Require-Text $file.purpose 'design.affected_files[].purpose' $prefix
    if (@($file.symbols).Count -eq 0) {
      $errors.Add("$prefix affected file '$($file.path)' has no symbols")
    }
  }

  $steps = @($artifact.design.implementation_steps)
  if ($steps.Count -eq 0) {
    $errors.Add("$prefix missing design.implementation_steps")
  }
  foreach ($step in $steps) {
    Require-Text $step.file 'design.implementation_steps[].file' $prefix
    if (@($step.symbols).Count -eq 0) {
      $errors.Add("$prefix implementation step $($step.order) has no symbols")
    }
    Require-Text $step.current_behavior 'design.implementation_steps[].current_behavior' $prefix
    Require-Text $step.change 'design.implementation_steps[].change' $prefix
    Require-Text $step.code_block 'design.implementation_steps[].code_block' $prefix
    if (@($step.tests).Count -eq 0) {
      $errors.Add("$prefix implementation step $($step.order) has no tests")
    }
  }

  if (@($artifact.design.verify).Count -eq 0) {
    $errors.Add("$prefix missing design.verify")
  }
  if (@($artifact.design.risks).Count -eq 0) {
    $errors.Add("$prefix missing design.risks")
  }
  if (@($artifact.design.alternatives).Count -eq 0) {
    $errors.Add("$prefix missing design.alternatives")
  }
}

if ($errors.Count -gt 0) {
  $errors | ForEach-Object { Write-Error $_ -ErrorAction Continue }
  throw "Dashboard artifact validation failed with $($errors.Count) error(s)."
}

Write-Host "Validated $(@($paths).Count) dashboard artifact(s)."
