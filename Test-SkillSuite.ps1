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
          @('approve', 'post_review', 'request_changes', 'trigger_ci', 'merge_pr')
        } else {
          @('request_info', 'approve_design', 'open_upstream_pr', 'post_comment', 'reproduce')
        }
        foreach ($action in $actions) {
          if ($action.type -notin $allowedActionTypes) {
            $errors.Add("Manifest artifact exposes unsupported $($artifact.kind) action '$($action.type)': $artifactPath")
          }
          if ($action.type -eq 'trigger_ci' -and
              [string]$action.comment.body -ne '/azp run') {
            $errors.Add("Manifest artifact trigger_ci action must post exactly /azp run: $artifactPath")
          }
          if ($action.type -eq 'reproduce') {
            if (-not $action.reproduce) {
              $errors.Add("Manifest artifact reproduce action missing reproduce payload: $artifactPath")
            }
            if (@($action.reproduce.steps).Count -eq 0) {
              $errors.Add("Manifest artifact reproduce action missing reproduce.steps: $artifactPath")
            }
          }
        }
        if ($artifact.kind -eq 'issue' -and [int]$artifact.schemaVersion -ge 4) {
          $issueAction = @($actions | Where-Object {
            $_.type -in @('request_info', 'approve_design', 'open_upstream_pr', 'post_comment', 'reproduce')
          }) | Select-Object -First 1
          $allowsNoAction = $artifact.pending_author -or
            $artifact.judgment.status -in @('duplicate_or_handled', 'not_actionable') -or
            $artifact.stage -eq 'design_in_progress'
          if (-not $issueAction -and -not $allowsNoAction) {
            $errors.Add("Issue manifest artifact has no meaningful maintainer action: $artifactPath")
          }
        }
        if ($artifact.kind -eq 'issue' -and [int]$artifact.schemaVersion -ge 5) {
          $fixStatus = [string]$artifact.fix_assessment.status
          $proposedFixes = @($artifact.proposed_fixes | Where-Object { $null -ne $_ })
          if (-not $artifact.fix_assessment -or
              $fixStatus -notin @('proposed', 'existing_fix', 'not_applicable')) {
            $errors.Add("Issue manifest artifact has invalid fix_assessment: $artifactPath")
          }
          if ([string]::IsNullOrWhiteSpace([string]$artifact.fix_assessment.rationale)) {
            $errors.Add("Issue manifest artifact missing fix_assessment.rationale: $artifactPath")
          }
          if ($fixStatus -eq 'proposed' -and $proposedFixes.Count -eq 0) {
            $errors.Add("Issue manifest artifact missing proposed_fixes: $artifactPath")
          }
          if ($fixStatus -eq 'existing_fix' -and
              @($artifact.fix_assessment.existing_fix_urls | Where-Object {
                -not [string]::IsNullOrWhiteSpace([string]$_)
              }).Count -eq 0) {
            $errors.Add("Issue manifest artifact existing_fix missing URLs: $artifactPath")
          }
          foreach ($fix in $proposedFixes) {
            if (@($fix.plan | Where-Object {
              -not [string]::IsNullOrWhiteSpace([string]$_)
            }).Count -eq 0) {
              $errors.Add("Issue manifest artifact proposed fix missing plan: $artifactPath")
            }
            if (@($fix.verification | Where-Object {
              -not [string]::IsNullOrWhiteSpace([string]$_)
            }).Count -eq 0) {
              $errors.Add("Issue manifest artifact proposed fix missing verification: $artifactPath")
            }
            $score = 0
            if (-not [int]::TryParse([string]$fix.confidence.score, [ref]$score) -or
                $score -lt 0 -or $score -gt 100) {
              $errors.Add("Issue manifest artifact has invalid fix confidence score: $artifactPath")
              continue
            }
            $expectedLevel = if ($score -ge 85) {
              'green'
            } elseif ($score -ge 51) {
              'yellow'
            } else {
              'red'
            }
            if ([string]$fix.confidence.level -ne $expectedLevel) {
              $errors.Add("Issue manifest artifact has mismatched fix confidence level: $artifactPath")
            }
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
$staleQueueScript = Join-Path $skillsRoot 'powertoys-dashboard-update\scripts\Get-StalePrReviewQueue.ps1'
$candidateScript = Join-Path $skillsRoot 'powertoys-dashboard-update\scripts\Get-DashboardUpdateCandidates.ps1'
$updatePlanScript = Join-Path $skillsRoot 'powertoys-dashboard-update\scripts\Get-DashboardUpdateRunPlan.ps1'
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
        @{ number = 6; artifact_stage = 'review_ready'; updated_at = '2026-08-06T00:00:00Z'; reasons = @('new_discussion_on_reviewed_head'); work_type = 'context_revalidation' }
      )
    } | ConvertTo-Json -Depth 5 | Set-Content $fixturePath
    $plan = & $runPlanScript -Dashboard $PSScriptRoot -QueueJsonPath $fixturePath `
      -BatchSize 3 -MaxConcurrency 2 -RunBudgetMinutes 45 -AsJson |
      ConvertFrom-Json
    if ($plan.selected_count -ne 3 -or $plan.deferred_count -ne 3) {
      $errors.Add('Bounded PR run planner did not enforce the requested batch size.')
    }
    if ($plan.policy.max_concurrency -ne 2 -or $plan.policy.run_budget_minutes -ne 45 -or
        $plan.policy.publish_interval_minutes -ne 8 -or $plan.policy.publish_transition_count -ne 2) {
      $errors.Add('Bounded PR run planner did not preserve concurrency, budget, or publish policy.')
    }
    if (@($plan.selected_prs)[0].number -ne 3) {
      $errors.Add('Bounded PR run planner did not prioritize changed-head review work.')
    }
    if (@($plan.deferred_prs)[-1].number -ne 6) {
      $errors.Add('Bounded PR run planner did not defer same-head context revalidation behind full reviews.')
    }

    $drainPlan = & $runPlanScript -Dashboard $PSScriptRoot -QueueJsonPath $fixturePath `
      -DrainQueue -AsJson |
      ConvertFrom-Json
    if ($drainPlan.selected_count -ne 6 -or $drainPlan.deferred_count -ne 0) {
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

if (-not (Test-Path $candidateScript) -or -not (Test-Path $updatePlanScript)) {
  $errors.Add('Missing exhaustive dashboard candidate inventory or combined run planner.')
} else {
  $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "powertoys-update-candidates-$PID"
  try {
    New-Item -ItemType Directory -Force -Path (Join-Path $fixtureRoot 'data\items') | Out-Null
    @{
      number = 10; kind = 'pr'; stage = 'review_ready'; head_sha = ('a' * 40)
      evaluated_at = '2026-08-01T00:00:00Z'; source_updated_at = '2026-08-01T00:00:00Z'
      proposed_comments = @(); actions = @()
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $fixtureRoot 'data\items\10.json')
    @{
      number = 11; kind = 'pr'; stage = 'waiting_on_author'; head_sha = ('b' * 40)
      pending_author = $true; source_updated_at = '2026-08-02T00:00:00Z'
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $fixtureRoot 'data\items\11.json')
    @{
      number = 14; kind = 'pr'; stage = 'review_blocked'; head_sha = ('f' * 40)
      source_updated_at = '2026-08-05T00:00:00Z'
      blockers = @(@{
        id = 'fresh-copilot-review-pending'
        detail = 'The fresh Copilot review has not arrived yet.'
        remediation = 'Resume the existing fork review after it arrives.'
      })
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $fixtureRoot 'data\items\14.json')
    @{
      number = 15; kind = 'pr'; stage = 'review_blocked'; head_sha = ('g' * 40)
      source_updated_at = '2026-08-05T00:00:00Z'
      blockers = @(@{
        id = 'manual-access-required'
        terminal = $true
        detail = 'The configured fork is no longer writable.'
        remediation = 'Restore fork write access before resuming.'
      })
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $fixtureRoot 'data\items\15.json')
    @{
      number = 20; kind = 'issue'; schemaVersion = 5
      source_updated_at = '2026-08-01T00:00:00Z'; evaluated_at = '2026-08-01T00:00:00Z'
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $fixtureRoot 'data\items\20.json')
    @{
      number = 21; kind = 'issue'; schemaVersion = 5
      source_updated_at = '2026-08-03T00:00:00Z'; evaluated_at = '2026-08-03T00:00:00Z'
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $fixtureRoot 'data\items\21.json')

    $pullRequestsPath = Join-Path $fixtureRoot 'prs.json'
    @(
      @{ number = 10; title = 'Changed head'; url = 'https://example.test/10'; updatedAt = '2026-08-04T00:00:00Z'; headRefOid = ('c' * 40); isDraft = $false; labels = @() }
      @{ number = 11; title = 'Waiting author'; url = 'https://example.test/11'; updatedAt = '2026-08-02T00:00:00Z'; headRefOid = ('b' * 40); isDraft = $false; labels = @() }
      @{ number = 12; title = 'Same head discussion'; url = 'https://example.test/12'; updatedAt = '2026-08-05T00:00:00Z'; headRefOid = ('d' * 40); isDraft = $false; labels = @() }
      @{ number = 13; title = 'Draft'; url = 'https://example.test/13'; updatedAt = '2026-08-05T00:00:00Z'; headRefOid = ('e' * 40); isDraft = $true; labels = @() }
      @{ number = 14; title = 'Copilot still pending'; url = 'https://example.test/14'; updatedAt = '2026-08-05T00:00:00Z'; headRefOid = ('f' * 40); isDraft = $false; labels = @() }
      @{ number = 15; title = 'Manual access blocker'; url = 'https://example.test/15'; updatedAt = '2026-08-05T00:00:00Z'; headRefOid = ('g' * 40); isDraft = $false; labels = @() }
    ) | ConvertTo-Json -Depth 5 | Set-Content $pullRequestsPath
    $issuesPath = Join-Path $fixtureRoot 'issues.json'
    @(
      @{ number = 20; title = 'Changed bug'; url = 'https://example.test/20'; updatedAt = '2026-08-06T00:00:00Z'; labels = @(@{ name = 'Issue-Bug' }) }
      @{ number = 21; title = 'Current bug'; url = 'https://example.test/21'; updatedAt = '2026-08-03T00:00:00Z'; labels = @('Issue-Bug') }
      @{ number = 22; title = 'Feature'; url = 'https://example.test/22'; updatedAt = '2026-08-03T00:00:00Z'; labels = @('Idea-Enhancement') }
    ) | ConvertTo-Json -Depth 5 | Set-Content $issuesPath
    $prQueuePath = Join-Path $fixtureRoot 'pr-queue.json'
    $resumeQueue = & $staleQueueScript -Dashboard $fixtureRoot `
      -PullRequestsJsonPath $pullRequestsPath -AsJson | ConvertFrom-Json
    $resumeNumbers = @($resumeQueue.stale_prs | ForEach-Object { [int]$_.number })
    if (-not ($resumeNumbers -contains 14) -or ($resumeNumbers -contains 15)) {
      $errors.Add('Stale PR queue did not resume workflow blockers while preserving explicit terminal blockers.')
    }
    @{
      number = 14; kind = 'pr'; stage = 'review_ready'; head_sha = ('f' * 40)
      source_updated_at = '2026-08-05T00:00:00Z'; proposed_comments = @(); actions = @()
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $fixtureRoot 'data\items\14.json')
    $completedQueue = & $staleQueueScript -Dashboard $fixtureRoot `
      -PullRequestsJsonPath $pullRequestsPath -AsJson | ConvertFrom-Json
    if (@($completedQueue.stale_prs | Where-Object { [int]$_.number -eq 14 }).Count -ne 0) {
      $errors.Add('Stale PR queue did not clear a resumed Copilot wait after a clean review result.')
    }
    @{
      number = 14; kind = 'pr'; stage = 'review_blocked'; head_sha = ('f' * 40)
      source_updated_at = '2026-08-05T00:00:00Z'
      blockers = @(@{
        id = 'fresh-copilot-review-pending'
        detail = 'The fresh Copilot review has not arrived yet.'
        remediation = 'Resume the existing fork review after it arrives.'
      })
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $fixtureRoot 'data\items\14.json')
    @{
      stale_prs = @(
        @{ number = 10; work_type = 'full_review'; artifact_stage = 'review_ready'; reasons = @('new_commits_since_artifact_head') }
        @{ number = 12; work_type = 'context_revalidation'; artifact_stage = 'review_ready'; reasons = @('new_discussion_on_reviewed_head') }
        @{ number = 14; work_type = 'full_review'; artifact_stage = 'review_blocked'; reasons = @('missing_current_review_action') }
      )
    } | ConvertTo-Json -Depth 5 | Set-Content $prQueuePath
    $issueQueuePath = Join-Path $fixtureRoot 'issue-queue.json'
    @{
      issues = @(
        @{ number = 20; reasons = @('upstream activity is newer than the artifact') }
        @{ number = 21; reasons = @('schemaVersion below 5') }
      )
    } |
      ConvertTo-Json -Depth 5 | Set-Content $issueQueuePath

    $inventoryPath = Join-Path $fixtureRoot 'inventory.json'
    & $candidateScript -Dashboard $fixtureRoot `
      -PullRequestsJsonPath $pullRequestsPath -IssuesJsonPath $issuesPath `
      -PrQueueJsonPath $prQueuePath -IssueQueueJsonPath $issueQueuePath -AsJson |
      Set-Content $inventoryPath
    $inventory = Get-Content $inventoryPath -Raw | ConvertFrom-Json
    if ($inventory.summary.full_review -ne 2 -or
        $inventory.summary.context_revalidation -ne 1 -or
        $inventory.summary.issue_revalidation -ne 2 -or
        $inventory.summary.waiting_author -ne 1 -or
        $inventory.summary.blocked -ne 1 -or
        $inventory.summary.no_action -ne 0 -or
        $inventory.summary.excluded -ne 1) {
      $errors.Add('Dashboard candidate inventory did not classify the complete fixture set.')
    }

    $plan = & $updatePlanScript -Dashboard $fixtureRoot -CandidatesJsonPath $inventoryPath `
      -PrBatchSize 1 -IssueBatchSize 1 -OldIssueReserve 0 `
      -SpareIssuesPerUnusedPrSlot 0 -AsJson | ConvertFrom-Json
    if ($plan.selected_pr_count -ne 1 -or $plan.deferred_pr_count -ne 2 -or
        $plan.selected_issue_count -ne 1 -or $plan.deferred_issue_count -ne 1 -or
        @($plan.selected_prs)[0].classification -ne 'full_review') {
      $errors.Add('Combined dashboard run plan did not prioritize and bound candidate work.')
    }

    $adaptivePlan = & $updatePlanScript -Dashboard $fixtureRoot -CandidatesJsonPath $inventoryPath `
      -PrBatchSize 4 -IssueBatchSize 1 -OldIssueReserve 1 `
      -SpareIssuesPerUnusedPrSlot 1 -AsJson | ConvertFrom-Json
    if ($adaptivePlan.policy.effective_issue_batch_size -ne 2 -or
        $adaptivePlan.policy.selected_old_issue_count -ne 1 -or
        $adaptivePlan.selected_issue_count -ne 2 -or
        [int]$adaptivePlan.selected_issues[-1].number -ne 21) {
      $errors.Add('Combined dashboard run plan did not spend spare PR capacity while reserving old issue work.')
    }

    $drainPlan = & $updatePlanScript -Dashboard $fixtureRoot -CandidatesJsonPath $inventoryPath `
      -DrainQueue -AsJson | ConvertFrom-Json
    if ($drainPlan.selected_pr_count -ne 3 -or $drainPlan.deferred_pr_count -ne 0 -or
        $drainPlan.selected_issue_count -ne 2 -or $drainPlan.deferred_issue_count -ne 0) {
      $errors.Add('Combined dashboard drain plan did not select every candidate.')
    }
  } catch {
    $errors.Add("Dashboard candidate inventory validation failed: $($_.Exception.Message)")
  } finally {
    Remove-Item $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
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
        @($checkpoint.actions).Count -ne 0) {
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

    @{
      number = 34568
      stage = 'owned_elsewhere'
      pending_author = $false
      head_sha = ('e' * 40)
    } | ConvertTo-Json | Set-Content (Join-Path $checkpointRoot 'data\items\34568.json')
    & $checkpointScript -Dashboard $checkpointRoot -Number 34568 `
      -HeadSha ('e' * 40) -SourceUpdatedAt '2026-08-25T00:00:00Z' `
      -Phase queued -Detail 'Maintainer activity does not exclude a non-draft PR.'
    $formerlyOwned = Get-Content (Join-Path $checkpointRoot 'data\items\34568.json') -Raw |
      ConvertFrom-Json
    if ($formerlyOwned.stage -ne 'review_in_progress' -or $formerlyOwned.head_sha -ne ('e' * 40)) {
      $errors.Add('PR review checkpoint writer did not requeue a legacy owned_elsewhere artifact.')
    }
  } catch {
    $errors.Add("PR review checkpoint validation failed: $($_.Exception.Message)")
  } finally {
    Remove-Item $checkpointRoot -Recurse -Force -ErrorAction SilentlyContinue
  }
}

$artifactValidator = Join-Path $skillsRoot 'powertoys-dashboard-update\scripts\Test-DashboardArtifacts.ps1'
$staleIssueQueue = Join-Path $skillsRoot 'powertoys-dashboard-update\scripts\Get-StaleIssueTriageQueue.ps1'
$actionSanitizer = Join-Path $PSScriptRoot 'Sanitize-ActionData.ps1'
if (-not (Test-Path $artifactValidator)) {
  $errors.Add("Missing dashboard artifact validator: $artifactValidator")
} elseif (-not (Test-Path $staleIssueQueue)) {
  $errors.Add("Missing stale issue triage queue: $staleIssueQueue")
} elseif (-not (Test-Path $actionSanitizer)) {
  $errors.Add("Missing action-data sanitizer: $actionSanitizer")
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
          in_diff = $true
          disposition = 'proposed'
          path = 'src/Test.cs'
          line = 2
          side = 'RIGHT'
          body = "### Fix value`n`n**Severity:** ``medium```n`nUse the corrected value.`n`n``````suggestion`nvalue = 2;`n``````"
        }
      )
      actions = @(
        @{
          type = 'post_review'
          label = 'Post inline suggestion'
          review = @{ event = 'COMMENT'; pr = 34567 }
        }
      )
    } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $artifactRoot 'data\items\34567.json')
    & $artifactValidator -Dashboard $artifactRoot -Numbers 34567 | Out-Null
    $validPrArtifactText = Get-Content (Join-Path $artifactRoot 'data\items\34567.json') -Raw

    $invalidStage = $validPrArtifactText | ConvertFrom-Json
    $invalidStage.stage = 'review_ready'
    $invalidStage | ConvertTo-Json -Depth 10 |
      Set-Content (Join-Path $artifactRoot 'data\items\34567.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 34567 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted review_ready with drafted findings.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }
    @{
      items = @(
        @{
          id = 'pr-34567'
          kind = 'pr'
          number = 34567
          stage = 'review_ready'
          primary_action = @{
            type = 'post_review'
            label = 'Post inline suggestion'
          }
        }
      )
    } | ConvertTo-Json -Depth 6 |
      Set-Content (Join-Path $artifactRoot 'data\index.json')
    & $actionSanitizer -DataPath (Join-Path $artifactRoot 'data') 3>$null | Out-Null
    $sanitizedInvalidStage = Get-Content (Join-Path $artifactRoot 'data\items\34567.json') -Raw |
      ConvertFrom-Json
    $sanitizedIndexRow = (Get-Content (Join-Path $artifactRoot 'data\index.json') -Raw |
      ConvertFrom-Json).items[0]
    if ($sanitizedInvalidStage.stage -ne 'review_in_progress' -or
        -not $sanitizedInvalidStage.needs_revalidation -or
        @($sanitizedInvalidStage.actions | Where-Object {
          $_.type -in @('post_review', 'request_changes')
        }).Count -gt 0 -or
        $sanitizedIndexRow.stage -ne 'review_in_progress' -or
        -not $sanitizedIndexRow.needs_revalidation -or
        $null -ne $sanitizedIndexRow.primary_action) {
      $errors.Add('Sanitizer did not fail closed for invalid review stage/action data.')
    }
    Set-Content (Join-Path $artifactRoot 'data\items\34567.json') $validPrArtifactText

    $generalOnly = $validPrArtifactText | ConvertFrom-Json
    $generalOnly.proposed_comments[0].kind = 'companion'
    $generalOnly.proposed_comments[0].in_diff = $false
    $generalOnly.proposed_comments[0].PSObject.Properties.Remove('path')
    $generalOnly.proposed_comments[0].PSObject.Properties.Remove('line')
    $generalOnly.proposed_comments[0].PSObject.Properties.Remove('side')
    $generalOnly.proposed_comments[0].body = '### Coordinate the cross-file lifetime change`n`n**Severity:** `medium``n`nThis concern spans unchanged ownership and shutdown paths, so please align the lifetime contract before applying a localized edit.'
    $generalOnly.proposed_comments[0] |
      Add-Member -NotePropertyName out_of_diff_reason -NotePropertyValue 'The required ownership change spans unchanged files and has no current RIGHT-side anchor.'
    $generalOnly.actions[0].label = 'Post general review notes'
    $generalOnly.actions[0] |
      Add-Member -NotePropertyName note -NotePropertyValue 'General review notes — separate PR conversation comments'
    $generalOnly | ConvertTo-Json -Depth 10 |
      Set-Content (Join-Path $artifactRoot 'data\items\34567.json')
    & $artifactValidator -Dashboard $artifactRoot -Numbers 34567 | Out-Null

    $misleadingLabel = $generalOnly
    $misleadingLabel.actions[0].label = 'Post inline suggestion'
    $misleadingLabel | ConvertTo-Json -Depth 10 |
      Set-Content (Join-Path $artifactRoot 'data\items\34567.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 34567 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted an inline-suggestion label without inline suggestions.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }
    Set-Content (Join-Path $artifactRoot 'data\items\34567.json') $validPrArtifactText

    $missingKind = $generalOnly
    $missingKind.actions[0].label = 'Post general review notes'
    $missingKind.proposed_comments[0].PSObject.Properties.Remove('kind')
    $missingKind | ConvertTo-Json -Depth 10 |
      Set-Content (Join-Path $artifactRoot 'data\items\34567.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 34567 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted an out-of-diff comment without companion kind.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }
    Set-Content (Join-Path $artifactRoot 'data\items\34567.json') $validPrArtifactText

    @{
      number = 34569
      kind = 'issue'
      track = 'triage'
      stage = 'triaged'
      generated_at = '2026-08-24T00:00:00Z'
      evaluated_at = '2026-08-24T00:00:00Z'
      source_updated_at = '2026-08-24T00:00:00Z'
      judgment = @{
        status = 'not_actionable'
        rationale = 'Legacy fixture'
        evidence = @('Legacy evidence')
        recommended_action = 'Re-triage'
      }
      actions = @()
    } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $artifactRoot 'data\items\34569.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 34569 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted a legacy issue artifact without schema-v5 fix coverage.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    $invalidArtifact = Get-Content (Join-Path $artifactRoot 'data\items\34567.json') -Raw |
      ConvertFrom-Json
    $invalidArtifact.actions[0].review.event = 'REQUEST_CHANGES'
    $invalidArtifact | ConvertTo-Json -Depth 10 |
      Set-Content (Join-Path $artifactRoot 'data\items\34567.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 34567 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted REQUEST_CHANGES on a post_review action.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    $invalidArtifact.actions[0].review.event = 'COMMENT'
    $invalidArtifact.actions[0].review | Add-Member -NotePropertyName body_prefix -NotePropertyValue 'Unnecessary overall message.'
    $invalidArtifact | ConvertTo-Json -Depth 10 |
      Set-Content (Join-Path $artifactRoot 'data\items\34567.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 34567 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted body_prefix on an inline-only post_review action.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    $invalidArtifact.actions[0].review.PSObject.Properties.Remove('body_prefix')
    $invalidArtifact.proposed_comments[0].kind = 'companion'
    $invalidArtifact.proposed_comments[0].in_diff = $true
    $invalidArtifact.proposed_comments[0].body = 'Prose without a suggestion block.'
    $invalidArtifact | ConvertTo-Json -Depth 10 |
      Set-Content (Join-Path $artifactRoot 'data\items\34567.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 34567 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted inconsistent companion inline metadata.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    $invalidArtifact.proposed_comments[0].kind = 'inline'
    $invalidArtifact.proposed_comments[0].in_diff = $true
    $invalidArtifact.proposed_comments[0].body = "Malformed block.`n`n``````suggestion`nvalue = 2;"
    $invalidArtifact | ConvertTo-Json -Depth 10 |
      Set-Content (Join-Path $artifactRoot 'data\items\34567.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 34567 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted a malformed inline suggestion block.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    @{
      schemaVersion = 5
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
      fix_assessment = @{
        status = 'proposed'
        rationale = 'No existing fix attempt covers the likely activation failure.'
      }
      proposed_fixes = @(
        @{
          title = 'Correct the failing activation handoff'
          root_cause = 'The best current hypothesis is that the selected result reaches an invalid activation target.'
          plan = @('Trace the selected result through activation and validate the target before launch.')
          verification = @('Repeat the reported activation sequence and confirm the selected app launches.')
          confidence = @{
            score = 40
            level = 'red'
            rationale = 'The symptom fits an activation handoff failure, but diagnostics are needed to locate the rejecting component.'
          }
        }
      )
      issue_context = @{
        summary = 'The issue consistently fails during app activation, but the discussion does not identify which activation stage fails.'
        known_information = @('The reporter can reproduce the failure when launching the target app.')
        inferences = @('The failure may occur after search result selection rather than during result discovery.')
        analysis = 'A diagnostic archive can distinguish search, extension, and activation failures.'
        initial_investigation = @('No linked duplicate or fix identifies the failing activation component.')
        information_gaps = @(
          @{
            evidence_type = 'bugreport_zip'
            information = 'A fresh PowerToys diagnostic ZIP captured immediately after reproduction'
            why_needed = 'The relevant logs identify which activation stage failed.'
            how_to_collect = 'Add a comment containing /bugreport immediately after reproducing.'
          }
        )
      }
      actions = @(
        @{
          type = 'approve_design'
          label = 'Approve activation handoff design'
        },
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

    @{
      schemaVersion = 5
      number = 45679
      kind = 'issue'
      track = 'triage'
      stage = 'triaged'
      generated_at = '2026-08-24T00:00:00Z'
      evaluated_at = '2026-08-24T00:00:00Z'
      source_updated_at = '2026-08-24T00:00:00Z'
      judgment = @{
        status = 'reproducible'
        rationale = 'The public report contains enough environment and step detail to verify locally.'
        evidence = @('The reporter supplied the affected module, version, setup, and numbered steps.')
        recommended_action = 'Reproduce locally before deciding whether to design a fix or request more diagnostics.'
      }
      fix_assessment = @{
        status = 'proposed'
        rationale = 'No existing fix attempt covers the reported large-file slowdown.'
      }
      proposed_fixes = @(
        @{
          title = 'Avoid repeated full-file work during rename preview'
          root_cause = 'The large-file path may repeat expensive metadata or preview work for each update.'
          plan = @('Profile the supplied reproduction and cache or defer repeated per-file work.')
          verification = @('Repeat the large-file scenario and confirm preview latency remains bounded.')
          confidence = @{
            score = 45
            level = 'red'
            rationale = 'The public reproduction is sufficient to measure the failure, but profiling is needed to identify the exact hot path.'
          }
        }
      )
      issue_context = @{
        summary = 'The issue describes a clear PowerRename slowdown with a large file input.'
        known_information = @('PowerRename is enabled.', 'The repro needs a file larger than 2 MB.')
        inferences = @('The failure is likely in preview generation rather than shell integration.')
        analysis = 'The report is concrete enough for a maintainer to verify before asking for logs.'
        initial_investigation = @('No duplicate currently proves this specific large-file preview path.')
        information_gaps = @()
      }
      actions = @(
        @{
          type = 'approve_design'
          label = 'Approve large-file preview design'
        },
        @{
          type = 'reproduce'
          label = 'Reproduce locally'
          note = 'Prepare the large file, then follow the issue steps.'
          reproduce = @{
            title = 'Verify PowerRename large-file preview'
            module = 'PowerRename'
            version_requirement = 'PowerToys 0.94 or newer'
            prerequisites = @('PowerRename enabled')
            setup = @('Create a temporary folder containing one file larger than 2 MB.')
            steps = @('Open PowerRename for the temporary folder.', 'Apply the rename pattern from the issue.')
            expected_result = 'The preview should complete without hanging.'
            setup_prompt = 'Create a temporary folder containing one binary file larger than 2 MB for PowerRename testing.'
          }
        }
      )
    } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $artifactRoot 'data\items\45679.json')
    & $artifactValidator -Dashboard $artifactRoot -Numbers 45679 -RequireIssueContext | Out-Null

    @{
      schemaVersion = 5
      number = 45680
      kind = 'issue'
      track = 'triage'
      stage = 'triaged'
      generated_at = '2026-09-03T00:00:00Z'
      evaluated_at = '2026-09-03T00:00:00Z'
      source_updated_at = '2026-09-03T00:00:00Z'
      judgment = @{
        status = 'needs_information'
        rationale = 'The likely stale-cache path fits the symptom but still needs a targeted trace.'
        evidence = @('The failure occurs after the source result changes and before activation.')
        recommended_action = 'Review the proposed fix and request the trace that distinguishes cache reuse from activation failure.'
      }
      fix_assessment = @{
        status = 'proposed'
        rationale = 'No existing fix attempt covers the likely stale activation target.'
      }
      proposed_fixes = @(
        @{
          title = 'Invalidate the stale activation target'
          root_cause = 'The cached activation target can outlive the source result that produced it.'
          plan = @(
            'Locate the cache and source-result version boundary.',
            'Re-resolve the target when the source result changes.',
            'Add a regression test for stale-result activation.'
          )
          verification = @(
            'Repeat the issue activation sequence and confirm the latest target launches.'
          )
          confidence = @{
            score = 72
            level = 'yellow'
            rationale = 'The control flow matches the symptom, but a targeted trace would confirm the stale-cache branch.'
          }
        }
      )
      issue_context = @{
        summary = 'Activation can use an earlier result after the source updates.'
        known_information = @('The issue reproduces only after the result list changes.')
        inferences = @('A cached target may survive longer than its source result.')
        analysis = 'The cache lifetime is the strongest current explanation, but activation telemetry would distinguish it from a launcher failure.'
        initial_investigation = @('No linked PR or fork implementation currently covers this path.')
        information_gaps = @(
          @{
            evidence_type = 'bugreport_zip'
            information = 'A trace showing the selected result identifier and activation target'
            why_needed = 'It distinguishes stale target reuse from downstream launch failure.'
            how_to_collect = 'Capture a fresh diagnostic archive with /bugreport immediately after reproduction.'
          }
        )
      }
      actions = @(
        @{
          type = 'approve_design'
          label = 'Approve stale-target design'
        },
        @{
          type = 'request_info'
          label = 'Request activation trace'
          comment = @{
            target = 'issue'
            number = 45680
            body = 'Thanks for narrowing the failure to the sequence after the result list changes. The current evidence points to a stale activation target, but it does not yet distinguish cached-target reuse from a downstream launch failure. Please reproduce once more and add a comment containing `/bugreport` immediately afterward so the fresh diagnostic archive captures both the selected result identifier and activation target.'
          }
        }
      )
    } | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $artifactRoot 'data\items\45680.json')
    & $artifactValidator -Dashboard $artifactRoot -Numbers 45680 -RequireIssueContext | Out-Null

    $validFixArtifactText = Get-Content (Join-Path $artifactRoot 'data\items\45680.json') -Raw
    $missingPlan = $validFixArtifactText | ConvertFrom-Json
    $missingPlan.proposed_fixes[0].PSObject.Properties.Remove('plan')
    $missingPlan | ConvertTo-Json -Depth 12 |
      Set-Content (Join-Path $artifactRoot 'data\items\45680.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 45680 -RequireIssueContext 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted a proposed fix without plan steps.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    $missingApproveDesign = $validFixArtifactText | ConvertFrom-Json
    $missingApproveDesign.actions = @($missingApproveDesign.actions | Where-Object {
      $_.type -ne 'approve_design'
    })
    $missingApproveDesign | ConvertTo-Json -Depth 12 |
      Set-Content (Join-Path $artifactRoot 'data\items\45680.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 45680 -RequireIssueContext 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted a proposed fix without approve_design.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    $missingRequestInfo = $validFixArtifactText | ConvertFrom-Json
    $missingRequestInfo.actions = @($missingRequestInfo.actions | Where-Object {
      $_.type -ne 'request_info'
    })
    $missingRequestInfo | ConvertTo-Json -Depth 12 |
      Set-Content (Join-Path $artifactRoot 'data\items\45680.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 45680 -RequireIssueContext 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted a yellow fix without request_info.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    $genericRequestInfo = $validFixArtifactText | ConvertFrom-Json
    $genericRequestInfo.actions = @($genericRequestInfo.actions | ForEach-Object {
      if ($_.type -eq 'request_info') {
        $_.label = 'Request targeted evidence'
      }
      $_
    })
    $genericRequestInfo | ConvertTo-Json -Depth 12 |
      Set-Content (Join-Path $artifactRoot 'data\items\45680.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 45680 -RequireIssueContext 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted a request_info action with a generic label.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    $missingEvidenceType = $validFixArtifactText | ConvertFrom-Json
    $missingEvidenceType.issue_context.information_gaps[0].PSObject.Properties.Remove('evidence_type')
    $missingEvidenceType | ConvertTo-Json -Depth 12 |
      Set-Content (Join-Path $artifactRoot 'data\items\45680.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 45680 -RequireIssueContext 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted an information gap without evidence_type.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    $emitSource = Get-Content (Join-Path $PSScriptRoot 'emit.ps1') -Raw
    $tokens = $null
    $parseErrors = $null
    $emitAst = [System.Management.Automation.Language.Parser]::ParseInput(
      $emitSource,
      [ref]$tokens,
      [ref]$parseErrors
    )
    foreach ($functionName in @(
      'Get-PropertyValue',
      'Test-MeaningfulAction',
      'Test-CurrentOpenBugArtifact'
    )) {
      $functionAst = $emitAst.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq $functionName
      }, $true)
      if (-not $functionAst) {
        throw "Could not load $functionName from emit.ps1."
      }
      Invoke-Expression $functionAst.Extent.Text
    }
    if (-not (Test-CurrentOpenBugArtifact $missingEvidenceType)) {
      $errors.Add('Emitter hid a legacy request_info artifact before stale-queue revalidation.')
    }
    if (-not (Test-CurrentOpenBugArtifact $genericRequestInfo)) {
      $errors.Add('Emitter hid a legacy generic request_info action before stale-queue revalidation.')
    }
    Set-Content (Join-Path $artifactRoot 'data\items\45680.json') $validFixArtifactText

    $greenFix = $validFixArtifactText | ConvertFrom-Json
    $greenFix.proposed_fixes[0].confidence.score = 90
    $greenFix.proposed_fixes[0].confidence.level = 'green'
    $greenFix.proposed_fixes[0].confidence.rationale = 'The source and reproduction establish the stale-cache path and its invalidation boundary.'
    $greenFix.issue_context.information_gaps = @()
    $greenFix.actions = @($greenFix.actions | Where-Object {
      $_.type -ne 'request_info'
    })
    $greenFix | ConvertTo-Json -Depth 12 |
      Set-Content (Join-Path $artifactRoot 'data\items\45680.json')
    & $artifactValidator -Dashboard $artifactRoot -Numbers 45680 -RequireIssueContext | Out-Null
    Set-Content (Join-Path $artifactRoot 'data\items\45680.json') $validFixArtifactText

    @{
      items = @(
        @{
          number = 45680
          kind = 'issue'
          state = 'open'
          title = 'Valid current bug'
          labels = @('Issue-Bug')
          issue_type = 'bug'
          updated_at = '2026-09-03T00:00:00Z'
        },
        @{
          number = 45681
          kind = 'issue'
          state = 'open'
          title = 'Missing bug artifact'
          labels = @('Issue-Bug')
          issue_type = 'bug'
          updated_at = '2026-09-03T00:00:00Z'
        }
      )
    } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $artifactRoot 'data\index.json')
    $issueQueueResult = & $staleIssueQueue -Dashboard $artifactRoot -AsJson |
      ConvertFrom-Json
    if ($issueQueueResult.count -ne 1 -or
        [int]$issueQueueResult.issues[0].number -ne 45681) {
      $errors.Add('Stale issue triage queue did not isolate the missing artifact.')
    }

    $missingEvidenceType = $validFixArtifactText | ConvertFrom-Json
    $missingEvidenceType.issue_context.information_gaps[0].PSObject.Properties.Remove('evidence_type')
    $missingEvidenceType | ConvertTo-Json -Depth 12 |
      Set-Content (Join-Path $artifactRoot 'data\items\45680.json')
    $issueQueueResult = & $staleIssueQueue -Dashboard $artifactRoot -AsJson |
      ConvertFrom-Json
    $malformedIssue = @($issueQueueResult.issues | Where-Object { [int]$_.number -eq 45680 })
    if ($malformedIssue.Count -ne 1 -or
        @($malformedIssue[0].reasons) -notcontains 'information gap missing supported evidence_type') {
      $errors.Add('Stale issue triage queue did not select a malformed information request.')
    }
    Set-Content (Join-Path $artifactRoot 'data\items\45680.json') $validFixArtifactText

    $invalidConfidence = Get-Content (Join-Path $artifactRoot 'data\items\45680.json') -Raw |
      ConvertFrom-Json
    $invalidConfidence.proposed_fixes[0].confidence.level = 'green'
    $invalidConfidence | ConvertTo-Json -Depth 12 |
      Set-Content (Join-Path $artifactRoot 'data\items\45680.json')
    try {
      & $artifactValidator -Dashboard $artifactRoot -Numbers 45680 -RequireIssueContext 2>$null | Out-Null
      $errors.Add('Dashboard artifact validator accepted a mismatched fix confidence level.')
    } catch {
      if ($_.Exception.Message -notlike 'Dashboard artifact validation failed*') {
        throw
      }
    }

    $invalidIssue = Get-Content (Join-Path $artifactRoot 'data\items\45678.json') -Raw |
      ConvertFrom-Json
    $invalidRequestInfo = @($invalidIssue.actions | Where-Object {
      $_.type -eq 'request_info'
    }) | Select-Object -First 1
    $invalidRequestInfo.comment.body = 'Please provide the PowerToys bug-report ZIP after reproducing this activation failure so we can identify whether the request stopped before target resolution or during launch, and correlate the failing stage with the current root-cause hypothesis.'
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
