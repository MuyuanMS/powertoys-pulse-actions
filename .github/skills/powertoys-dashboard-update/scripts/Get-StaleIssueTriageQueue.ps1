param(
  [string]$Dashboard = $(if ($env:POWERTOYS_DASHBOARD_PATH) {
    $env:POWERTOYS_DASHBOARD_PATH
  } else {
    Join-Path $PSScriptRoot '..\..\..\..'
  }),
  [switch]$AsJson,
  [switch]$FailOnStale
)

$ErrorActionPreference = 'Stop'
$Dashboard = (Resolve-Path $Dashboard).Path
$indexPath = Join-Path $Dashboard 'data\index.json'
$itemsPath = Join-Path $Dashboard 'data\items'
if (-not (Test-Path $indexPath) -or -not (Test-Path $itemsPath)) {
  throw "Dashboard data not found under $Dashboard"
}

$index = Get-Content $indexPath -Raw | ConvertFrom-Json
$queue = [System.Collections.Generic.List[object]]::new()
$allowedEvidenceTypes = @(
  'bugreport_zip',
  'repro_steps',
  'screenshot_image',
  'gif_video',
  'sample_file',
  'event_viewer',
  'crash_dump',
  'module_trace',
  'installer_log',
  'powertoys_version',
  'windows_version',
  'install_scope',
  'settings_permissions',
  'configuration_export',
  'keyboard_layout',
  'monitor_topology',
  'other_software',
  'behavior_confirmation'
)

foreach ($row in @($index.items | Where-Object {
  $_.kind -eq 'issue' -and
  $_.state -eq 'open' -and
  (@($_.labels) -contains 'Issue-Bug' -or [string]$_.issue_type -eq 'bug')
})) {
  $reasons = [System.Collections.Generic.List[string]]::new()
  $path = Join-Path $itemsPath "$($row.number).json"
  $artifact = $null
  if (-not (Test-Path $path)) {
    $reasons.Add('missing artifact')
  } else {
    try {
      $artifact = Get-Content $path -Raw | ConvertFrom-Json
    } catch {
      $reasons.Add('invalid JSON')
    }
  }

  if ($artifact) {
    if ([int]$artifact.schemaVersion -lt 5) {
      $reasons.Add('schemaVersion below 5')
    }
    $fixStatus = [string]$artifact.fix_assessment.status
    if ($fixStatus -notin @('proposed', 'existing_fix', 'not_applicable')) {
      $reasons.Add('invalid or missing fix_assessment')
    }
    if (-not $artifact.issue_context -or
        [string]::IsNullOrWhiteSpace([string]$artifact.issue_context.summary) -or
        [string]::IsNullOrWhiteSpace([string]$artifact.issue_context.analysis) -or
        @($artifact.issue_context.known_information).Count -eq 0 -or
        -not $artifact.issue_context.PSObject.Properties['inferences'] -or
        @($artifact.issue_context.initial_investigation).Count -eq 0) {
      $reasons.Add('incomplete issue_context')
    }

    $actions = @($artifact.actions | Where-Object { $null -ne $_ })
    $fixes = @($artifact.proposed_fixes | Where-Object { $null -ne $_ })
    $requestInfo = @($actions | Where-Object { $_.type -eq 'request_info' }) |
      Select-Object -First 1
    if ($requestInfo) {
      $commentBody = [string]$requestInfo.comment.body
      $informationGaps = @($artifact.issue_context.information_gaps)
      $genericLabels = @(
        'request information',
        'request more information',
        'request targeted evidence',
        'ask for focused repro details',
        'ask for a narrower repro and fresh diagnostics',
        'reply with the missing-info request',
        'draft request for details'
      )
      if ([string]::IsNullOrWhiteSpace([string]$requestInfo.label) -or
          $genericLabels -contains ([string]$requestInfo.label).Trim().ToLowerInvariant()) {
        $reasons.Add('request_info label does not name the requested evidence')
      }
      if ($commentBody -notmatch '(?i)\b(please|could you|can you|would you|share|provide|confirm|capture|attach|run|reproduce)\b') {
        $reasons.Add('request_info comment has no direct request')
      }
      if ($commentBody -match '(?i)\b(?:PowerToys\s+)?(?:diagnostic|bug[- ]report)\s+(?:archive|report|zip)|\bPowerToys\s+logs?\b' -and
          $commentBody -notmatch '(?i)/bugreport') {
        $reasons.Add('PowerToys diagnostic request does not use /bugreport')
      }
      if ($informationGaps.Count -gt 1 -and
          $commentBody -notmatch '(?m)^\s*(?:[-*]|\d+[.)])\s+\S') {
        $reasons.Add('request_info comment does not list multiple requested items clearly')
      }
      if ($informationGaps.Count -eq 0) {
        $reasons.Add('request_info missing information gaps')
      }
      foreach ($gap in $informationGaps) {
        if ([string]$gap.evidence_type -notin $allowedEvidenceTypes) {
          $reasons.Add('information gap missing supported evidence_type')
        }
        if ([string]::IsNullOrWhiteSpace([string]$gap.information) -or
            [string]::IsNullOrWhiteSpace([string]$gap.why_needed) -or
            [string]::IsNullOrWhiteSpace([string]$gap.how_to_collect)) {
          $reasons.Add('incomplete information gap')
        }
        if ([string]$gap.evidence_type -eq 'bugreport_zip' -and
            ([string]$gap.how_to_collect -notmatch '(?i)/bugreport' -or
             $commentBody -notmatch '(?i)/bugreport')) {
          $reasons.Add('request_info comment omits required /bugreport command')
        }
      }
    }
    if ($fixStatus -eq 'proposed') {
      if ($fixes.Count -eq 0) {
        $reasons.Add('missing proposed_fixes')
      }
      if (@($actions | Where-Object { $_.type -eq 'approve_design' }).Count -eq 0) {
        $reasons.Add('missing approve_design')
      }
      $nonGreen = @($fixes | Where-Object {
        [string]$_.confidence.level -in @('yellow', 'red')
      })
      foreach ($fix in $fixes) {
        $score = 0
        if ([string]::IsNullOrWhiteSpace([string]$fix.title) -or
            [string]::IsNullOrWhiteSpace([string]$fix.root_cause) -or
            @($fix.plan | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -eq 0 -or
            @($fix.verification | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -eq 0) {
          $reasons.Add('incomplete proposed fix')
        }
        if (-not [int]::TryParse([string]$fix.confidence.score, [ref]$score) -or
            $score -lt 0 -or $score -gt 100) {
          $reasons.Add('invalid proposed-fix confidence score')
          continue
        }
        $expectedLevel = if ($score -ge 85) { 'green' } elseif ($score -ge 51) { 'yellow' } else { 'red' }
        if ([string]$fix.confidence.level -ne $expectedLevel -or
            [string]::IsNullOrWhiteSpace([string]$fix.confidence.rationale)) {
          $reasons.Add('invalid proposed-fix confidence')
        }
      }
      if ($nonGreen.Count -gt 0 -and
          @($actions | Where-Object { $_.type -in @('request_info', 'reproduce') }).Count -eq 0) {
        $reasons.Add('yellow/red fix missing uncertainty-reducing action')
      }
      if ($nonGreen.Count -gt 0 -and $requestInfo -and
          @($artifact.issue_context.information_gaps).Count -eq 0) {
        $reasons.Add('request_info missing information gaps')
      }
      if ($nonGreen.Count -gt 0 -and $requestInfo) {
        $commentBody = [string]$requestInfo.comment.body
        if ($commentBody.Trim().Length -lt 160) {
          $reasons.Add('request_info comment is too generic')
        }
      }
    }
    if ($fixStatus -eq 'existing_fix' -and
        @($artifact.fix_assessment.existing_fix_urls | Where-Object {
          -not [string]::IsNullOrWhiteSpace([string]$_)
        }).Count -eq 0) {
      $reasons.Add('existing_fix missing URLs')
    }
    if ($artifact.source_updated_at -and $row.updated_at) {
      try {
        if ([datetime]$row.updated_at -gt [datetime]$artifact.source_updated_at) {
          $reasons.Add('upstream activity is newer than the artifact')
        }
      } catch {
        $reasons.Add('invalid freshness timestamp')
      }
    }
  }

  if ($reasons.Count -gt 0) {
    $queue.Add([pscustomobject]@{
      number = [int]$row.number
      title = [string]$row.title
      updated_at = $row.updated_at
      reasons = $reasons.ToArray()
    })
  }
}

$result = [pscustomobject]@{
  generated_at = (Get-Date).ToUniversalTime().ToString('o')
  count = $queue.Count
  issues = @($queue | Sort-Object updated_at, number -Descending)
}

if ($AsJson) {
  $result | ConvertTo-Json -Depth 6
} else {
  $result.issues | Format-Table number, updated_at, title, @{ Name='reasons'; Expression={ $_.reasons -join '; ' } } -AutoSize
  "Stale issue triage queue: $($result.count)"
}

if ($FailOnStale -and $queue.Count -gt 0) {
  throw "Stale issue triage queue contains $($queue.Count) open bug(s)."
}
