#requires -Version 7.5
. (Join-Path $PSScriptRoot '.github\skills\powertoys-dashboard-update\scripts\ArtifactValidationReceipt.ps1')
. (Join-Path $PSScriptRoot '.github\skills\powertoys-dashboard-update\scripts\PublicPrArtifact.ps1')

function Invoke-ScopedActionDataGeneration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Dashboard,
        [Parameter(Mandatory)][int[]]$Numbers,
        [Parameter(Mandatory)][string]$CandidateDataPath,
        [Parameter(Mandatory)][string]$ValidationReceiptPath,
        [Parameter(Mandatory)][string]$LiveMetadataPath,
        [switch]$ValidateOnly
    )

    $ErrorActionPreference = 'Stop'
    if ($Numbers.Count -eq 0 -or @($Numbers | Where-Object { $_ -le 0 }).Count -gt 0) {
        throw 'Scoped generation requires positive explicit PR numbers.'
    }
    $Numbers = @($Numbers | Sort-Object -Unique)
    $Dashboard = (Resolve-Path -LiteralPath $Dashboard).Path
    $CandidateDataPath = (Resolve-Path -LiteralPath $CandidateDataPath).Path
    $data = Join-Path $Dashboard 'data'
    if ($CandidateDataPath -ieq $data) { throw 'Candidates must be separate from the publication target.' }
    $receipt = Get-Content -LiteralPath $ValidationReceiptPath -Raw | ConvertFrom-Json -DateKind String -NoEnumerate
    $liveMetadata = @(Get-Content -LiteralPath $LiveMetadataPath -Raw | ConvertFrom-Json -DateKind String)
    $retainedReadiness = $receipt -is [Array]
    if (-not $retainedReadiness -and ($receipt.schemaVersion -ne 1 -or $receipt.validator -cne 'Test-DashboardArtifacts.ps1' -or
        $receipt.finding_grounding_required -ne $true)) {
        throw 'A successful strict processed-artifact validation receipt is required.'
    }
    $indexPath = Join-Path $data 'index.json'
    $jsPath = Join-Path $data 'index.js'
    $originals = [ordered]@{}
    $originals[$indexPath] = [IO.File]::ReadAllBytes($indexPath)
    $originals[$jsPath] = [IO.File]::ReadAllBytes($jsPath)
    $index = [Text.Encoding]::UTF8.GetString($originals[$indexPath]).TrimStart([char]0xfeff) | ConvertFrom-Json -DateKind String
    $js = [Text.Encoding]::UTF8.GetString($originals[$jsPath]).TrimStart([char]0xfeff)
    if ($js -notmatch '(?s)^\s*window\.BOARD_INDEX\s*=\s*(.*?);\s*$' -or
        (Get-ArtifactContentDigest ($Matches[1] | ConvertFrom-Json -DateKind String)) -cne (Get-ArtifactContentDigest $index)) {
        throw 'Existing index.json and index.js disagree; repair the baseline before scoped generation.'
    }
    if (-not $index.items -or -not $index.PSObject.Properties['artifact_numbers'] -or -not $index.counts) {
        throw 'Existing index is missing the Pulse manifest schema.'
    }
    $work = Join-Path $Dashboard ('.scoped-generation-' + [guid]::NewGuid().ToString('N'))
    $lockPath = Join-Path $Dashboard '.scoped-generation.lock'
    $lock = $null
    $written = [Collections.Generic.List[string]]::new()
    $validatedDigests = @{}
    try {
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $items = Join-Path $work 'data\items'
        New-Item -ItemType Directory -Path $items -Force | Out-Null
        foreach ($number in $Numbers) {
            $rows = @($index.items | Where-Object { [int]$_.number -eq $number })
            if ($rows.Count -ne 1 -or $rows[0].kind -cne 'pr' -or $rows[0].is_cmdpal -eq $true) {
                throw "PR $number requires one existing non-CmdPal index row."
            }
            $live = @($liveMetadata | Where-Object { [int]$_.number -eq $number })
            if ($live.Count -ne 1 -or $live[0].isDraft -ne $false -or
                [string]$live[0].headRefOid -notmatch '^[0-9a-fA-F]{40}$' -or
                [string]::IsNullOrWhiteSpace([string]$live[0].title) -or
                @($live[0].labels | Where-Object { $_.name -match '(?i)Command Palette|CmdPal' }).Count -gt 0 -or
                ($live[0].state -and $live[0].state -ine 'open')) {
                throw "PR $number requires pinned eligible live metadata."
            }
            $live = $live[0]
            $updatedAt = [datetime]::MinValue
            if (-not [datetime]::TryParse([string]$live.updatedAt, [ref]$updatedAt)) {
                throw "PR $number live metadata is missing updatedAt."
            }
            $candidatePath = Join-Path $CandidateDataPath "items\$number.json"
            $candidate = Get-Content -LiteralPath $candidatePath -Raw | ConvertFrom-Json -DateKind String
            $records = @($(if ($retainedReadiness) { $receipt } else { $receipt.artifacts }) |
                Where-Object { [int]$_.number -eq $number })
            if ($candidate.number -ne $number -or $candidate.kind -cne 'pr' -or $candidate.track -cne 'review' -or
                [string]$candidate.head_sha -cne [string]$live.headRefOid -or
                $records.Count -ne 1 -or [string]$records[0].head_sha -cne [string]$candidate.head_sha) {
                throw "PR $number does not match its strict validation receipt."
            }
            $digest = Get-ArtifactContentDigest $candidate
            if ($retainedReadiness) {
                if ($records[0].status -cne 'ready_for_publication' -or
                    [string]::IsNullOrWhiteSpace([string]$records[0].validation) -or
                    [string]$records[0].source_updated_at -cne [string]$candidate.source_updated_at -or
                    [string]$records[0].candidate_sha256 -ine (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash) {
                    throw "PR $number does not match its verified retained-candidate readiness receipt."
                }
            } elseif ($records[0].kind -cne 'pr' -or [string]$records[0].content_sha256 -cne $digest) {
                throw "PR $number does not match its strict validation receipt."
            }
            $validatedDigests[$number] = $digest
            if ((Get-ArtifactContentDigest (ConvertTo-PublicPrArtifact $candidate)) -cne (Get-ArtifactContentDigest $candidate)) {
                throw "PR $number is not already public; sanitize it and verify its evidence before supplying a receipt."
            }
            Copy-Item -LiteralPath $candidatePath -Destination (Join-Path $items "$number.json")
            $target = Join-Path $data "items\$number.json"
            $originals[$target] = if (Test-Path -LiteralPath $target) { [IO.File]::ReadAllBytes($target) } else { $null }
        }
        # The isolated input contains selected artifacts only: neither gate can enumerate legacy items.
        $validator = Join-Path $PSScriptRoot '.github\skills\powertoys-dashboard-update\scripts\Test-DashboardArtifacts.ps1'
        & $validator -Dashboard $work -Numbers $Numbers
        & (Join-Path $PSScriptRoot 'Sanitize-ActionData.ps1') -DataPath (Join-Path $work 'data') -PreserveJsonDateStrings
        & $validator -Dashboard $work -Numbers $Numbers
        $outputs = [ordered]@{}
        foreach ($number in $Numbers) {
            $artifactPath = Join-Path $items "$number.json"
            $artifact = Get-Content -LiteralPath $artifactPath -Raw | ConvertFrom-Json -DateKind String
            if ((Get-ArtifactContentDigest $artifact) -cne $validatedDigests[$number]) {
                throw "Sanitization changed PR $number; sanitize and strictly validate the candidate before publication."
            }
            $row = @($index.items | Where-Object { [int]$_.number -eq $number })[0]
            $live = @($liveMetadata | Where-Object { [int]$_.number -eq $number })[0]
            foreach ($name in @('blockers', 'workflow', 'summary', 'status', 'next_action', 'mirror', 'fork_pr', 'fork_branch')) {
                $row.PSObject.Properties.Remove($name)
            }
            $primary = @($artifact.actions | Where-Object { $null -ne $_ }) | Select-Object -First 1
            $values = [ordered]@{
                track = $artifact.track
                stage = $artifact.stage
                owes = if ($artifact.owes) { $artifact.owes } else { 'us' }
                pending_author = [bool]$artifact.pending_author
                waiting_since = if ($artifact.pending_author) { $artifact.waiting_since } else { $null }
                has_artifact = $true
                agent_status = $artifact.track
                title = [string]$live.title
                updated_at = [string]$live.updatedAt
                is_draft = [bool]$live.isDraft
                labels = @($live.labels | ForEach-Object { [string]$_.name })
                proposed_open = @($artifact.proposed_comments | Where-Object {
                    $null -ne $_ -and [string]$_.disposition -notin @('posted', 'withdrawn')
                }).Count
                primary_action = if ($primary -and -not $artifact.needs_revalidation -and $artifact.stage -ne 'ci_blocked') {
                    [pscustomobject]@{ type = $primary.type; label = $primary.label }
                } else { $null }
            }
            foreach ($name in $values.Keys) {
                $row | Add-Member -NotePropertyName $name -NotePropertyValue $values[$name] -Force
            }
            if ($artifact.PSObject.Properties['needs_revalidation']) {
                $row | Add-Member -NotePropertyName needs_revalidation -NotePropertyValue ([bool]$artifact.needs_revalidation) -Force
            } else {
                $row.PSObject.Properties.Remove('needs_revalidation')
            }
            $outputs[(Join-Path $data "items\$number.json")] = [IO.File]::ReadAllBytes($artifactPath)
        }
        $manifest = [Collections.Generic.List[int]]::new()
        foreach ($number in @($index.artifact_numbers)) { $manifest.Add([int]$number) }
        foreach ($number in $Numbers) {
            if (-not $manifest.Contains($number)) { $manifest.Add($number) }
        }
        $index.artifact_numbers = $manifest.ToArray()
        $index.counts.artifacts = $manifest.Count
        # Inventory timestamps and impact are deliberately retained: this is not a live backlog refresh.
        $json = $index | ConvertTo-Json -Depth 60
        $outputs[$indexPath] = [Text.Encoding]::UTF8.GetBytes($json)
        $outputs[$jsPath] = [Text.Encoding]::UTF8.GetBytes("window.BOARD_INDEX = $json;")
        foreach ($path in $originals.Keys) {
            $current = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
            if ([Convert]::ToBase64String(@($current)) -cne [Convert]::ToBase64String(@($originals[$path]))) {
                throw 'Publication target changed during scoped generation; retry on the new baseline.'
            }
        }
        if ($ValidateOnly) {
            return [pscustomobject]@{ numbers = $Numbers; validated_only = $true; changed_files = @() }
        }
        foreach ($path in $outputs.Keys) {
            if ([Convert]::ToBase64String(@($outputs[$path])) -ceq [Convert]::ToBase64String(@($originals[$path]))) { continue }
            $written.Add($path)
            [IO.File]::WriteAllBytes($path, $outputs[$path])
        }
        [pscustomobject]@{ numbers = $Numbers; changed_files = $written.ToArray() }
    } catch {
        foreach ($path in $written) {
            if ($null -eq $originals[$path]) { Remove-Item -LiteralPath $path -Force }
            else { [IO.File]::WriteAllBytes($path, $originals[$path]) }
        }
        throw
    } finally {
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
        if ($lock) { $lock.Dispose(); Remove-Item -LiteralPath $lockPath -Force }
    }
}
