$ErrorActionPreference = 'Stop'
$planner = Join-Path $PSScriptRoot '..\scripts\Get-PrReviewDispatchPlan.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) "powertoys-dispatch-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$script:assertions = 0
$head = 'a' * 40
$now = [datetimeoffset]'2026-10-18T15:00:00Z'
function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Plan($Queue, $Rows, [int]$Cap = 6, [int]$Owner = 0) {
    @{ stale_prs = @($Queue) } | ConvertTo-Json -Depth 12 |
        Set-Content (Join-Path $root 'queue.json')
    @{ prs = @($Rows) } | ConvertTo-Json -Depth 12 |
        Set-Content (Join-Path $root 'state.json')
    & $planner -QueueJsonPath (Join-Path $root 'queue.json') `
        -StateJsonPath (Join-Path $root 'state.json') -MaxConcurrency $Cap `
        -BuildOwnerNumber $Owner -Now $now
}
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    $failure = ''
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-True ($failure -match $Pattern) "Expected $Pattern, got '$failure'"
}
try {
    $queue = @(1..37 | ForEach-Object { @{ number = $_; live_head_sha = $head } })
    $rows = @(1..37 | ForEach-Object {
        @{ number = $_; head_sha = $head; worker_id = "old-$_"; worker_status = 'idle'; phase = 'review_in_progress' }
    })
    $plan = Plan $queue $rows
    Assert-True ($plan.running_count -eq 0 -and $plan.assignments.Count -eq 6) '37 idle PRs dispatch six, not zero'
    Assert-True ($plan.runnable_count -eq 37 -and -not $plan.complete) 'idle ownership is not completion'
    $again = Plan $queue $rows
    Assert-True (($again.assignments.number -join ',') -eq ($plan.assignments.number -join ',')) 'unchanged queue remains runnable'
    Assert-True ((Plan $queue $rows 8).assignments.Count -eq 8) 'explicit eight-worker cap is respected'
    $rows[0].worker_status = 'running'
    $plan = Plan $queue $rows
    Assert-True ($plan.assignments.Count -eq 5 -and $plan.assignments.number -notcontains 1) 'running PR is not duplicated'
    $rows[0].worker_status = 'idle'
    $rows[0].phase = 'finalizing'
    Assert-True ((Plan $queue $rows).assignments[0].action -eq 'finalize_artifact') 'finalization has priority'
    $rows[0].head_sha = 'b' * 40
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'reconcile_updated_head') 'new head invalidates old finalization'
    $rows[0].head_sha = $head
    $rows[0].phase = 'waiting_copilot'
    $plan = Plan @($queue[0]) @($rows[0])
    Assert-True ($plan.assignments[0].action -eq 'recover_request_identity') 'missing request identity is runnable recovery'
    $rows[0].request = @{ fork_repo = 'example/PowerToys'; fork_pr = 100; head_sha = $head; requested_at = $now.AddMinutes(-5).ToString('o') }
    $plan = Plan @($queue[0]) @($rows[0])
    Assert-True ($plan.assignments.Count -eq 0 -and $plan.coordinator_actions[0].action -eq 'check_saved_review') 'unknown cloud state gets a read-only check'
    $rows[0].review = @{ submitted = $false; request_pending = $true; checked_at = $now.ToString('o') }
    $plan = Plan @($queue[0]) @($rows[0])
    Assert-True ($plan.coordinator_actions.Count -eq 0 -and $plan.next_check_at -eq $now.AddSeconds(120).ToString('o')) 'pending cloud state has a bounded next check'
    Assert-True (-not $plan.complete) 'pending cloud request is not completion'
    $rows[0].review.checked_at = $now.AddMinutes(-3).ToString('o')
    Assert-True ((Plan @($queue[0]) @($rows[0])).coordinator_actions.Count -eq 1) 'due cloud check is scheduled without a worker slot'
    $rows[0].review.submitted = $true
    $rows[0].review.request_pending = $false
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'process_review') 'arrived review is consumed before new requests'
    $rows[0].review.head_matches = $false
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'reconcile_fork_head') 'changed fork head cannot consume old review evidence'
    $rows[0].review.head_matches = $true
    $rows[0].review.submitted = $false
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'reconcile_missing_review') 'absent request is not an indefinite wait'
    $rows[0].round = 2
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'reconcile_missing_review') 'two used iterations leave room for the third after reconciliation'
    $rows[0].round = 3
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'finalize_review_limit') 'three used iterations prohibit a fourth request'
    $rows[0].review.submitted = $true
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'process_review') 'third review result is consumed rather than discarded at the cap'
    $rows[0].review.submitted = $false
    $rows[0].review.request_pending = $true
    Assert-True ((Plan @($queue[0]) @($rows[0])).coordinator_actions[0].action -eq 'check_saved_review') 'in-flight third request is checked without requesting again'
    $rows[0].review.request_pending = $false
    $rows[0].phase = 'reviewing_findings'
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'process_review') 'last-round findings must still be disposed'
    $rows[0].phase = 'building'
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'validate') 'converged third round still receives required validation'
    $rows[0].phase = 'waiting_copilot'
    $savedRequest = $rows[0].request
    $rows[0].request = $null
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'recover_request_identity') 'round cap cannot discard an unidentified final review'
    $rows[0].request = $savedRequest
    $rows[0].phase = 'queued'
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'finalize_review_limit') 'exhausted queued review cannot start round four'
    $rows[0].round = 8
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'finalize_review_limit') 'legacy history exceeding three is preserved and receives no new allowance'
    $rows[0].round = 0
    $rows[0].phase = 'blocked'
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'repair_blocker_evidence') 'bare blocked label is not a terminal blocker'
    $rows[0].blocker = @{ detail = 'Build command not derived'; remediation = 'Read changed project and derive command'; external = $false }
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'repair_local_prerequisite') 'local planning gap remains runnable'
    $rows[0].blocker.external = $true
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'publish_terminal_blocker') 'real external gate is published, not hidden in idle state'
    $rows[0].head_sha = 'b' * 40
    Assert-True ((Plan @($queue[0]) @($rows[0])).assignments[0].action -eq 'reconcile_updated_head') 'old-head blocker cannot terminate current work'
    $buildRows = @(1..3 | ForEach-Object {
        @{ number = $_; head_sha = $head; worker_status = 'idle'; phase = 'building' }
    })
    $plan = Plan @($queue[0..2]) $buildRows
    Assert-True ($plan.assignments.Count -eq 1 -and $plan.waits.Count -eq 2) 'only one validation build is assigned'
    $plan = Plan @($queue[0..2]) $buildRows 6 99
    Assert-True ($plan.assignments.Count -eq 0 -and $plan.coordinator_actions[0].action -eq 'reconcile_build_owner') 'build wait with no workers requires owner reconciliation'
    Assert-True ((Plan @() @()).complete) 'empty queue with no running workers is complete'
    Assert-True (-not (Plan @() @() 6 99).complete) 'outstanding build ownership must be reconciled before completion'
    Assert-True (Plan @() @($rows[0])).complete 'historical idle row does not prevent completion'
    Assert-Fails { Plan $queue @($rows[1], $rows[1]) } 'duplicate PR'
    $running = @(1..2 | ForEach-Object {
        @{ number = $_; head_sha = $head; worker_status = 'running'; worker_id = 'same'; phase = 'building' }
    })
    Assert-Fails { Plan $queue $running } 'multiply assigned'
    $running[1].worker_id = 'other'
    Assert-Fails { Plan $queue $running 1 } 'exceed concurrency'
    Assert-Fails { Plan @(@{number=1;live_head_sha='not-a-sha'}) @() } 'unpinned queue'
    Assert-Fails { Plan @($queue[0],$queue[0]) @() } 'duplicate or unpinned'
    $simulationRows = @(1..37 | ForEach-Object {
        @{ number = $_; head_sha = $head; worker_status = 'idle'; phase = 'review_in_progress' }
    })
    $remaining = @($queue)
    $completed = [Collections.Generic.HashSet[int]]::new()
    for ($pass = 0; $pass -lt 100 -and $remaining.Count; $pass++) {
        $step = Plan $remaining $simulationRows 8
        Assert-True ($step.assignments.Count -gt 0) 'simulation cannot stall with local work and idle capacity'
        foreach ($assignment in $step.assignments) {
            $row = $simulationRows[$assignment.number - 1]
            switch ($assignment.action) {
                'reconcile_resume' { $row.phase = 'building' }
                'validate' { $row.phase = 'finalizing' }
                'finalize_artifact' { Assert-True ($completed.Add($assignment.number)) 'each PR finalizes only once' }
                default { throw "Unexpected simulation action: $($assignment.action)" }
            }
        }
        $remaining = @($remaining | Where-Object { -not $completed.Contains([int]$_.number) })
    }
    Assert-True ($remaining.Count -eq 0 -and $completed.Count -eq 37) 'all 37 idle PRs reach finalization in bounded simulation'
    $live = @(1..205 | ForEach-Object {
        @{ number = $_; headRefOid = $head; title = "PR $_"; isDraft = $false; updatedAt = '2026-10-18T15:00:00Z'; labels = @() }
    })
    $live[0].labels = @(@{ name = 'Product-Command Palette' })
    $live[1].title = '[CmdPal] Fix focus'
    $live[2].is_cmdpal = $true
    $live[3].isDraft = $true
    $liveJson = ConvertTo-Json -InputObject $live -Depth 6
    function gh {
        if ($args -notcontains '2147483647') { throw 'Live discovery must not truncate at 200 PRs.' }
        $global:LASTEXITCODE = 0
        $liveJson
    }
    $queueScript = Join-Path $PSScriptRoot '..\scripts\Get-StalePrReviewQueue.ps1'
    $full = & $queueScript -Dashboard $root -AsJson | ConvertFrom-Json
    Assert-True ($full.stale_count -eq 204) 'normal discovery retains CmdPal and includes more than 200 PRs'
    $scoped = & $queueScript -Dashboard $root -ExcludeCmdPal -AsJson | ConvertFrom-Json
    Assert-True ($scoped.stale_count -eq 201 -and $scoped.excluded_cmdpal.Count -eq 3) 'PR-only drain excludes CmdPal labels, titles and explicit classification'
    Assert-True ($scoped.stale_prs.number -notcontains 4) 'drafts remain excluded'
    Write-Host "PR review dispatch tests passed ($script:assertions assertions)."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
