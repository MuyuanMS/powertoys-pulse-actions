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
        $actions = @($artifact.actions)
        if (@($actions | Where-Object { $_.type -eq 'hold' -or $_.label -match '(?i)^not now$' }).Count -gt 0) {
          $errors.Add("Manifest artifact exposes a non-actionable hold/Not now action: $artifactPath")
        }
        $allowedActionTypes = if ($artifact.kind -eq 'pr') {
          @('approve', 'post_review', 'request_changes')
        } else {
          @('request_info', 'approve_design', 'open_upstream_pr', 'post_comment')
        }
        foreach ($action in $actions) {
          if ($action.type -notin $allowedActionTypes) {
            $errors.Add("Manifest artifact exposes unsupported $($artifact.kind) action '$($action.type)': $artifactPath")
          }
        }
        if ($artifact.kind -eq 'issue' -and [int]$artifact.schemaVersion -ge 4) {
          $issueAction = @($actions | Where-Object {
            $_.type -in @('request_info', 'approve_design', 'open_upstream_pr', 'post_comment')
          }) | Select-Object -First 1
          $allowsNoAction = $artifact.pending_author -or
            $artifact.judgment.status -in @('duplicate_or_handled', 'not_actionable') -or
            $artifact.stage -eq 'design_in_progress'
          if (-not $issueAction -and -not $allowsNoAction) {
            $errors.Add("Issue manifest artifact has no meaningful maintainer action: $artifactPath")
          }
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

$runPlanScript = Join-Path $skillsRoot 'powertoys-dashboard-update\scripts\Get-PrReviewRunPlan.ps1'
$targetGuard = Join-Path $skillsRoot 'powertoys-dashboard-update\scripts\Assert-CanonicalDashboardTarget.ps1'
if (-not (Test-Path $targetGuard)) {
  $errors.Add("Missing canonical dashboard target guard: $targetGuard")
} else {
  try {
    & $targetGuard -Dashboard $PSScriptRoot | Out-Null
    $wrongTarget = Join-Path ([System.IO.Path]::GetTempPath()) "powertoys-wrong-target-$PID"
    New-Item -ItemType Directory -Force -Path $wrongTarget | Out-Null
    & git -C $wrongTarget init --quiet
    & git -C $wrongTarget remote add origin https://github.com/MuyuanMS/powertoys-triage-board.git
    try {
      & $targetGuard -Dashboard $wrongTarget | Out-Null
      $errors.Add('Canonical dashboard target guard accepted the retired repository.')
    } catch {
      if ($_.Exception.Message -notlike "Refusing dashboard update for*") {
        throw
      }
    }
  } catch {
    $errors.Add("Canonical dashboard target validation failed: $($_.Exception.Message)")
  } finally {
    if ($wrongTarget) {
      Remove-Item $wrongTarget -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
}

if (-not (Test-Path $runPlanScript)) {
  $errors.Add("Missing bounded PR run planner: $runPlanScript")
} else {
  $fixturePath = Join-Path ([System.IO.Path]::GetTempPath()) "powertoys-run-plan-$PID.json"
  try {
    @{
      stale_prs = @(
        @{ number = 1; artifact_stage = ''; updated_at = '2026-08-01T00:00:00Z'; reasons = @('missing_artifact') }
        @{ number = 2; artifact_stage = 'review_in_progress'; updated_at = '2026-08-02T00:00:00Z'; reasons = @('new_commits_since_artifact_head') }
        @{ number = 3; artifact_stage = ''; updated_at = '2026-08-03T00:00:00Z'; reasons = @('new_commits_since_proposed_review') }
        @{ number = 4; artifact_stage = ''; updated_at = '2026-08-04T00:00:00Z'; reasons = @('missing_artifact') }
        @{ number = 5; artifact_stage = ''; updated_at = '2026-08-05T00:00:00Z'; reasons = @('missing_current_review_action') }
      )
    } | ConvertTo-Json -Depth 5 | Set-Content $fixturePath
    $plan = & $runPlanScript -Dashboard $PSScriptRoot -QueueJsonPath $fixturePath `
      -BatchSize 3 -MaxConcurrency 2 -RunBudgetMinutes 45 -AsJson |
      ConvertFrom-Json
    if ($plan.selected_count -ne 3 -or $plan.deferred_count -ne 2) {
      $errors.Add('Bounded PR run planner did not enforce the requested batch size.')
    }
    if ($plan.policy.max_concurrency -ne 2 -or $plan.policy.run_budget_minutes -ne 45 -or
        $plan.policy.publish_interval_minutes -ne 8 -or $plan.policy.publish_transition_count -ne 2) {
      $errors.Add('Bounded PR run planner did not preserve concurrency, budget, or publish policy.')
    }
    if (@($plan.selected_prs)[0].number -ne 2) {
      $errors.Add('Bounded PR run planner did not prioritize resumable review work.')
    }

    $drainPlan = & $runPlanScript -Dashboard $PSScriptRoot -QueueJsonPath $fixturePath `
      -DrainQueue -AsJson |
      ConvertFrom-Json
    if ($drainPlan.selected_count -ne 5 -or $drainPlan.deferred_count -ne 0) {
      $errors.Add('Drain PR run planner did not select the full stale queue.')
    }
    if (-not $drainPlan.policy.drain_mode -or $null -ne $drainPlan.deadline_utc -or
        $drainPlan.policy.max_concurrency -ne 6 -or
        $drainPlan.policy.run_budget_minutes -ne 0 -or
        $null -ne $drainPlan.policy.worker_stop_minutes -or
        $drainPlan.policy.publish_interval_minutes -ne 5) {
      $errors.Add('Drain PR run planner did not remove the deadline or apply drain publish/concurrency policy.')
    }
  } catch {
    $errors.Add("Bounded PR run planner validation failed: $($_.Exception.Message)")
  } finally {
    Remove-Item $fixturePath -Force -ErrorAction SilentlyContinue
  }
}

$checkpointScript = Join-Path $skillsRoot 'powertoys-dashboard-update\scripts\Set-PrReviewCheckpoint.ps1'
if (-not (Test-Path $checkpointScript)) {
  $errors.Add("Missing PR review checkpoint writer: $checkpointScript")
} else {
  $checkpointRoot = Join-Path ([System.IO.Path]::GetTempPath()) "powertoys-checkpoint-$PID"
  try {
    New-Item -ItemType Directory -Force -Path (Join-Path $checkpointRoot 'data\items') | Out-Null
    & $checkpointScript -Dashboard $checkpointRoot -Number 12345 `
      -HeadSha ('a' * 40) -SourceUpdatedAt '2026-08-24T00:00:00Z' `
      -Phase waiting_copilot -Detail 'Waiting for a test review.'
    $checkpoint = Get-Content (Join-Path $checkpointRoot 'data\items\12345.json') -Raw |
      ConvertFrom-Json
    if ($checkpoint.stage -ne 'review_in_progress' -or
        $checkpoint.workflow.phase -ne 'waiting_copilot' -or
        @($checkpoint.actions).Count -ne 1) {
      $errors.Add('PR review checkpoint writer produced an invalid resumable artifact.')
    }

    @{
      number = 23456
      stage = 'review_ready'
      pending_author = $false
      head_sha = ('b' * 40)
    } | ConvertTo-Json | Set-Content (Join-Path $checkpointRoot 'data\items\23456.json')
    try {
      & $checkpointScript -Dashboard $checkpointRoot -Number 23456 `
        -HeadSha ('b' * 40) -SourceUpdatedAt '2026-08-24T00:00:00Z' `
        -Phase queued -Detail 'Must not overwrite.'
      $errors.Add('PR review checkpoint writer replaced a completed artifact.')
    } catch {
      if ($_.Exception.Message -notlike 'Refusing to replace protected PR*') {
        throw
      }
    }

    & $checkpointScript -Dashboard $checkpointRoot -Number 23456 `
      -HeadSha ('c' * 40) -SourceUpdatedAt '2026-08-25T00:00:00Z' `
      -Phase queued -Detail 'A newer live head must start a new review.'
    $requeued = Get-Content (Join-Path $checkpointRoot 'data\items\23456.json') -Raw |
      ConvertFrom-Json
    if ($requeued.stage -ne 'review_in_progress' -or $requeued.head_sha -ne ('c' * 40)) {
      $errors.Add('PR review checkpoint writer did not resume a completed artifact for a new head.')
    }
  } catch {
    $errors.Add("PR review checkpoint validation failed: $($_.Exception.Message)")
  } finally {
    Remove-Item $checkpointRoot -Recurse -Force -ErrorAction SilentlyContinue
  }
}

$artifactValidator = Join-Path $skillsRoot 'powertoys-dashboard-update\scripts\Test-DashboardArtifacts.ps1'
if (-not (Test-Path $artifactValidator)) {
  $errors.Add("Missing dashboard artifact validator: $artifactValidator")
} else {
  $artifactRoot = Join-Path ([System.IO.Path]::GetTempPath()) "powertoys-artifact-$PID"
  try {
    New-Item -ItemType Directory -Force -Path (Join-Path $artifactRoot 'data\items') | Out-Null
    @{
      number = 34567
      kind = 'pr'
      track = 'review'
      stage = 'awaiting_review_approval'
      generated_at = '2026-08-24T00:00:00Z'
      evaluated_at = '2026-08-24T00:00:00Z'
      source_updated_at = '2026-08-24T00:00:00Z'
      head_sha = ('d' * 40)
      proposed_comments = @(
        @{
          id = 'inline-fix'
          kind = 'inline'
          disposition = 'proposed'
          path = 'src/Test.cs'
          line = 2
          side = 'RIGHT'
          body = "### Fix value`n`n**Severity:** ``medium```n`nUse the corrected value.`n`n``````suggestion`nvalue = 2;`n``````"
        }
      )
      actions = @(
        @{ type = 'post_review'; label = 'Post inline suggestion' }
      )
    } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $artifactRoot 'data\items\34567.json')
    & $artifactValidator -Dashboard $artifactRoot -Numbers 34567 | Out-Null

    $invalidArtifact = Get-Content (Join-Path $artifactRoot 'data\items\34567.json') -Raw |
      ConvertFrom-Json
    $invalidArtifact.proposed_comments[0].body = 'Prose without a suggestion block.'
    $invalidArtifact | ConvertTo-Json -Depth 10 |
      Set-Content (Join-Path $artifactRoot 'data\items\34567.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 34567 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted inline prose without a suggestion block.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    @{
      schemaVersion = 4
      number = 45678
      kind = 'issue'
      track = 'triage'
      stage = 'triaged'
      generated_at = '2026-08-24T00:00:00Z'
      evaluated_at = '2026-08-24T00:00:00Z'
      source_updated_at = '2026-08-24T00:00:00Z'
      judgment = @{
        status = 'needs_information'
        rationale = 'The report identifies the failing action but not the component that rejects it.'
        evidence = @('The reporter supplied reproduction steps but no diagnostic archive.')
        recommended_action = 'Request a fresh diagnostic archive captured after reproduction.'
      }
      issue_context = @{
        summary = 'The issue consistently fails during app activation, but the discussion does not identify which activation stage fails.'
        known_information = @('The reporter can reproduce the failure when launching the target app.')
        inferences = @('The failure may occur after search result selection rather than during result discovery.')
        analysis = 'A diagnostic archive can distinguish search, extension, and activation failures.'
        initial_investigation = @('No linked duplicate or fix identifies the failing activation component.')
        information_gaps = @(
          @{
            information = 'A fresh PowerToys diagnostic ZIP captured immediately after reproduction'
            why_needed = 'The relevant logs identify which activation stage failed.'
            how_to_collect = 'Add a comment containing /bugreport immediately after reproducing.'
          }
        )
      }
      actions = @(
        @{
          type = 'request_info'
          label = 'Request activation diagnostics'
          comment = @{
            target = 'issue'
            number = 45678
            body = 'Thanks for confirming that the failure occurs when launching the selected app. The current steps show where you observe the problem, but they do not identify whether search, the extension, or app activation rejects the request. Please reproduce it once more and then add a comment containing `/bugreport` so the fresh PowerToys diagnostic ZIP includes the relevant activation logs.'
          }
        }
      )
    } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $artifactRoot 'data\items\45678.json')
    & $artifactValidator -Dashboard $artifactRoot -Numbers 45678 -RequireIssueContext | Out-Null

    $invalidIssue = Get-Content (Join-Path $artifactRoot 'data\items\45678.json') -Raw |
      ConvertFrom-Json
    $invalidIssue.actions[0].comment.body = 'Please send more logs and information.'
    $invalidIssue | ConvertTo-Json -Depth 10 |
      Set-Content (Join-Path $artifactRoot 'data\items\45678.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 45678 -RequireIssueContext 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted a generic request-info comment.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }
  } catch {
    $errors.Add("Dashboard artifact review validation failed: $($_.Exception.Message)")
  } finally {
    Remove-Item $artifactRoot -Recurse -Force -ErrorAction SilentlyContinue
  }
}

if ($errors.Count -gt 0) {
  $errors | ForEach-Object { Write-Error $_ -ErrorAction Continue }
  throw "Skill suite validation failed with $($errors.Count) error(s)."
}

Write-Host "Skill suite validated: $($requiredSkills.Count) skills, $($scripts.Count) PowerShell files."
