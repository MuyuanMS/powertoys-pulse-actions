param(
  [string]$Dashboard = $(if ($env:POWERTOYS_DASHBOARD_PATH) {
    $env:POWERTOYS_DASHBOARD_PATH
  } else {
    Join-Path $PSScriptRoot '..\..\..\..'
  }),
  [int[]]$Numbers,
  [switch]$RequireDetailedDesign,
  [switch]$RequireIssueContext
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
  foreach ($action in @($artifact.actions)) {
    if ($action.type -eq 'hold' -or $action.label -match '(?i)^not now$') {
      $errors.Add("$prefix exposes a non-actionable hold/Not now action")
    }
    $allowedActionTypes = if ($artifact.kind -eq 'pr') {
      @('approve', 'post_review', 'request_changes')
    } else {
      @('request_info', 'approve_design', 'open_upstream_pr', 'post_comment')
    }
    if ($action.type -notin $allowedActionTypes) {
      $errors.Add("$prefix exposes unsupported $($artifact.kind) action '$($action.type)'")
    }
  }

  if ($artifact.kind -eq 'pr' -and $artifact.track -eq 'review') {
    Require-Text $artifact.head_sha 'head_sha' $prefix

    $proposedComments = @(
      $artifact.proposed_comments |
        Where-Object { $_.disposition -eq 'proposed' }
    )
    $inlineComments = @(
      $proposedComments |
        Where-Object {
          $_.kind -eq 'inline' -or
          ($null -eq $_.kind -and $_.in_diff -eq $true)
        }
    )
    foreach ($comment in $inlineComments) {
      Require-Text $comment.path 'proposed_comments[].path' $prefix
      Require-Text $comment.body 'proposed_comments[].body' $prefix
      if ([int]$comment.line -lt 1) {
        $errors.Add("$prefix inline comment '$($comment.id)' has an invalid line")
      }
      if ($comment.side -and $comment.side -ne 'RIGHT') {
        $errors.Add("$prefix inline comment '$($comment.id)' must target side RIGHT")
      }
      if (([regex]::Matches([string]$comment.body, '(?s)```suggestion\s*\r?\n.+?\r?\n```')).Count -ne 1) {
        $errors.Add("$prefix inline comment '$($comment.id)' must contain exactly one non-empty suggestion block")
      }
    }
    foreach ($comment in $proposedComments | Where-Object { $null -ne $_.confidence }) {
      $confidenceScore = 0
      if (-not [int]::TryParse([string]$comment.confidence.score, [ref]$confidenceScore) -or
          $confidenceScore -lt 50 -or $confidenceScore -gt 100) {
        $errors.Add("$prefix comment '$($comment.id)' confidence.score must be an integer from 50 to 100")
      }
      Require-Text $comment.confidence.rationale 'proposed_comments[].confidence.rationale' $prefix
    }

    $reviewAction = @(
      $artifact.actions |
        Where-Object { $_.type -eq 'post_review' }
    ) | Select-Object -First 1
    if ($reviewAction -and $proposedComments.Count -gt 0 -and $inlineComments.Count -eq 0) {
      $presentationText = "$($reviewAction.label) $($reviewAction.note)"
      if ($presentationText -notmatch '(?i)general|no inline|text block') {
        $errors.Add("$prefix companion-only review action must disclose that it posts general notes with no inline suggestions")
      }
    }
  }

  if ($artifact.kind -ne 'issue') {
    continue
  }

  $issueAction = @($artifact.actions | Where-Object {
    $_.type -in @('request_info', 'approve_design', 'open_upstream_pr', 'post_comment')
  }) | Select-Object -First 1
  $allowsNoAction = $artifact.judgment.status -in @(
    'duplicate_or_handled',
    'not_actionable'
  ) -or $artifact.stage -eq 'design_in_progress'
  if (-not $issueAction -and -not $artifact.pending_author -and -not $allowsNoAction) {
    $errors.Add("$prefix issue artifact has no meaningful maintainer action")
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

  $requestInfoAction = @($artifact.actions | Where-Object {
    $_.type -eq 'request_info'
  }) | Select-Object -First 1
  $requiresIssueContext = $RequireIssueContext -or [int]$artifact.schemaVersion -ge 4
  if ($requiresIssueContext -and $issueAction) {
    if (-not $artifact.issue_context) {
      $errors.Add("$prefix missing issue_context for an actionable issue")
    } else {
      Require-Text $artifact.issue_context.summary 'issue_context.summary' $prefix
      Require-Text $artifact.issue_context.analysis 'issue_context.analysis' $prefix
      if (@($artifact.issue_context.known_information).Count -eq 0) {
        $errors.Add("$prefix missing issue_context.known_information")
      }
      if (-not $artifact.issue_context.PSObject.Properties['inferences']) {
        $errors.Add("$prefix missing issue_context.inferences")
      }
      if (@($artifact.issue_context.initial_investigation).Count -eq 0) {
        $errors.Add("$prefix missing issue_context.initial_investigation")
      }
    }
  }

  if ($requestInfoAction -and $requiresIssueContext) {
    $commentBody = [string]$requestInfoAction.comment.body
    Require-Text $commentBody 'request_info comment.body' $prefix
    if ($commentBody.Trim().Length -lt 160) {
      $errors.Add("$prefix request_info comment is too generic; acknowledge current evidence, explain the gap, and ask for exact information")
    }

    $informationGaps = @($artifact.issue_context.information_gaps)
    if ($informationGaps.Count -eq 0) {
      $errors.Add("$prefix request_info action requires issue_context.information_gaps")
    }
    foreach ($gap in $informationGaps) {
      Require-Text $gap.information 'issue_context.information_gaps[].information' $prefix
      Require-Text $gap.why_needed 'issue_context.information_gaps[].why_needed' $prefix
      if ([string]$gap.how_to_collect -match '(?i)/bugreport' -and
          $commentBody -notmatch '(?i)/bugreport') {
        $errors.Add("$prefix request_info comment must use /bugreport because its collection guidance requires it")
      }
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
