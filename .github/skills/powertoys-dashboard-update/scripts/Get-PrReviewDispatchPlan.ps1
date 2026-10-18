<#
.SYNOPSIS
    Plan the next drain actions from a scoped stale queue and private worker ledger.
.DESCRIPTION
    Read-only and offline: does not start agents, access GitHub, or acquire locks.
    Idle workers never occupy slots. The coordinator executes returned actions,
    persists their results, and calls this planner after each completion.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$QueueJsonPath,
    [Parameter(Mandatory)][string]$StateJsonPath,
    [ValidateRange(1, 8)][int]$MaxConcurrency = 6,
    [ValidateRange(0, 2147483647)][int]$BuildOwnerNumber = 0,
    [ValidateRange(30, 600)][int]$PollSeconds = 120,
    [datetimeoffset]$Now = [datetimeoffset]::UtcNow,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$queue = Get-Content -LiteralPath $QueueJsonPath -Raw | ConvertFrom-Json
$ledger = Get-Content -LiteralPath $StateJsonPath -Raw | ConvertFrom-Json
if ($null -eq $queue.stale_prs -or $null -eq $ledger.prs) {
    throw 'Expected stale_prs in queue and prs in private ledger, including empty arrays.'
}
$states = @{}
$runningIds = @{}
foreach ($row in @($ledger.prs)) {
    $number = [int]$row.number
    if ($number -le 0 -or $states.ContainsKey($number)) {
        throw "Invalid or duplicate PR in private ledger: $number"
    }
    if ([string]$row.worker_status -notin @('running', 'idle', 'none')) {
        throw "Invalid worker_status for PR $number; expected running, idle or none."
    }
    $states[$number] = $row
    if ($row.worker_status -eq 'running') {
        if (-not $row.worker_id -or $runningIds.ContainsKey([string]$row.worker_id)) {
            throw "Missing or multiply assigned running worker_id for PR $number."
        }
        $runningIds[[string]$row.worker_id] = $number
    }
}
if ($runningIds.Count -gt $MaxConcurrency) {
    throw "Running workers exceed concurrency cap: $($runningIds.Count) > $MaxConcurrency."
}

$runnable = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
$waits = [Collections.Generic.List[object]]::new()
$seen = @{}
:prLoop foreach ($pr in @($queue.stale_prs)) {
    $number = [int]$pr.number
    if ($number -le 0 -or $seen.ContainsKey($number) -or
        [string]$pr.live_head_sha -notmatch '^[a-fA-F0-9]{40}$') {
        throw "Invalid, duplicate or unpinned queue PR: $number"
    }
    $seen[$number] = $true
    $row = $states[$number]
    if ($row -and $row.worker_status -eq 'running') { continue }
    $action = 'reconcile_resume'
    $rank = 3
    $needsBuild = $false
    if ($row -and [string]$row.head_sha -ne [string]$pr.live_head_sha) {
        $action = 'reconcile_updated_head'
        $rank = 1
    } elseif ($row) {
        switch ([string]$row.phase) {
            'waiting_copilot' {
                if (-not $row.request -or -not $row.request.head_sha -or
                    -not $row.request.requested_at -or -not $row.request.fork_pr -or
                    -not $row.request.fork_repo) {
                    $action = 'recover_request_identity'
                } elseif ($row.review.head_matches -eq $false) {
                    $action = 'reconcile_fork_head'
                    $rank = 1
                } elseif ($row.review.submitted -eq $true) {
                    $action = 'process_review'
                    $rank = 1
                } elseif ($row.review.request_pending -eq $false) {
                    $action = 'reconcile_missing_review'
                    $rank = 1
                } else {
                    $due = if ($row.review.checked_at) {
                        ([datetimeoffset]$row.review.checked_at).AddSeconds($PollSeconds)
                    } else { $Now }
                    if ($due -le $Now) {
                        $checks.Add([pscustomobject]@{
                            number = $number
                            action = 'check_saved_review'
                            request = $row.request
                        })
                    } else {
                        $waits.Add([pscustomobject]@{
                            number = $number
                            reason = 'cloud_poll_not_due'
                            next_check_at = $due.ToUniversalTime().ToString('o')
                        })
                    }
                    continue prLoop
                }
            }
            'reviewing_findings' { $action = 'process_review'; $rank = 1 }
            'building' { $action = 'validate'; $rank = 2; $needsBuild = $true }
            'finalizing' { $action = 'finalize_artifact'; $rank = 0 }
            'blocked' {
                if (-not $row.blocker.detail -or -not $row.blocker.remediation) {
                    $action = 'repair_blocker_evidence'
                } elseif ($row.blocker.external -eq $true) {
                    $action = 'publish_terminal_blocker'
                    $rank = 0
                } else {
                    $action = 'repair_local_prerequisite'
                    $rank = 1
                }
            }
        }
        if ([int]$row.round -ge 8 -and $action -in @(
            'reconcile_resume', 'reconcile_missing_review'
        )) {
            $action = 'finalize_review_limit'
            $rank = 0
            $needsBuild = $false
        }
    }
    $runnable.Add([pscustomobject]@{
        number = $number
        head_sha = [string]$pr.live_head_sha
        action = $action
        next_action = if ($row) { [string]$row.next_action } else { '' }
        worker_id = if ($row) { [string]$row.worker_id } else { '' }
        rank = $rank
        needs_build_slot = $needsBuild
    })
}

$slots = $MaxConcurrency - $runningIds.Count
$assignments = [Collections.Generic.List[object]]::new()
$buildReserved = $BuildOwnerNumber -gt 0 -or @($ledger.prs | Where-Object {
    $_.worker_status -eq 'running' -and $_.phase -eq 'building'
}).Count -gt 0
foreach ($item in @($runnable | Sort-Object rank, number)) {
    if ($item.needs_build_slot -and $buildReserved) {
        $waits.Add([pscustomobject]@{ number = $item.number; reason = 'build_slot_owned' })
        continue
    }
    if ($assignments.Count -ge $slots) { continue }
    $assignments.Add($item)
    if ($item.needs_build_slot) { $buildReserved = $true }
}
$coordinatorActions = @($checks.ToArray())
if ($BuildOwnerNumber -gt 0 -and $runningIds.Count -eq 0) {
    $coordinatorActions += [pscustomobject]@{
        number = $BuildOwnerNumber
        action = 'reconcile_build_owner'
    }
}
$nextCheck = @($waits | Where-Object { $_.next_check_at } |
    Sort-Object next_check_at | Select-Object -First 1)
$plan = [pscustomobject]@{
    planned_at = $Now.ToUniversalTime().ToString('o')
    stale_count = $seen.Count
    running_count = $runningIds.Count
    available_slots = $slots
    runnable_count = $runnable.Count
    assignments = $assignments.ToArray()
    coordinator_actions = $coordinatorActions
    waits = $waits.ToArray()
    next_check_at = if ($nextCheck.Count) { $nextCheck[0].next_check_at } else { $null }
    complete = $seen.Count -eq 0 -and $runningIds.Count -eq 0 -and $BuildOwnerNumber -eq 0
}
if ($AsJson) { $plan | ConvertTo-Json -Depth 12 } else { $plan }
