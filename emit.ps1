<#
  emit.ps1  ---  v3 member-board generator.

  Produces the static action-data layer consumed by PowerToys Pulse. The canonical
  repository is public, so generated artifacts must contain only publishable review
  evidence, drafts, judgments, and actions. Local paths, private notes, credentials,
  and internal-only provenance must be removed before publication.

  The board is member-oriented: every item carries a human `status`, a suggested
  `next_action`, `pending_author` (ball-in-author's-court), and a list of concrete
  `actions[]` that a signed-in member executes AS THEMSELVES from the page
  (post a PR review with the comments they select, open the upstream PR, request
  missing info, approve a design, comment).

  Source of truth for the open backlog is the live upstream GitHub API. The older
  v2 latest.json is used only for display metadata and tracked-item hints. Tracked
  items get a hand-authored overlay ($OV) with drafted agent content.
#>

$ErrorActionPreference = 'Stop'
$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$v2json = Join-Path $here '..\dashboard\data\latest.json'
$currentIndex = Join-Path $here 'data\index.json'
$standaloneMode = -not (Test-Path $v2json) -and (Test-Path $currentIndex)
if ($standaloneMode) {
  $v2json = $currentIndex
}
$outDir = Join-Path $here 'data'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$UP  = 'microsoft/PowerToys'
# The upstream repository is the source of truth for the open backlog. Keep
# the older snapshot only for display metadata and tracked-item hints.
$src = Get-Content $v2json -Raw | ConvertFrom-Json
$FORK= if ($src.fork) { $src.fork } else { 'MuyuanMS/PowerToys' }
$ME  = if ($src.me)   { $src.me }   else { 'MuyuanMS' }
function Get-LiveCollection {
  param([string]$Endpoint)
  $pages = gh api --paginate --slurp $Endpoint 2>$null | ConvertFrom-Json
  @($pages | ForEach-Object { @($_) })
}
function Convert-LiveItem {
  param($Raw, [string]$Kind, $Previous)
  $labels = @($Raw.labels | ForEach-Object { $_.name })
  $author = if ($Raw.user.login) { $Raw.user.login } else { 'unknown' }
  $association = [string]$Raw.author_association
  $community = $association -notin @('MEMBER','OWNER','COLLABORATOR')
  [ordered]@{
    id = "$Kind-$($Raw.number)"; kind = $Kind; number = [int]$Raw.number
    url = $Raw.html_url; title = $Raw.title; author = $author
    state = $Raw.state
    is_community = [bool]$community; mine = ($author -ieq $ME)
    is_cmdpal = (($labels -join '|') -match '(?i)Command Palette|CmdPal')
    labels = $labels; created_at = $Raw.created_at; updated_at = $Raw.updated_at
    comments = [int]$Raw.comments
    track = if ($Previous) { $Previous.track } else { $null }
    stage = if ($Previous) { $Previous.stage } else { $null }
    owes = if ($Previous) { $Previous.owes } else { 'us' }
    priority = if ($Previous) { $Previous.priority } else { $null }
  }
}
$previousByNumber = @{}
foreach ($old in @($src.items)) { $previousByNumber[[int]$old.number] = $old }
try {
  $livePrs = Get-LiveCollection "repos/$UP/pulls?state=open&per_page=100"
  $liveOpenIssues = Get-LiveCollection "repos/$UP/issues?state=open&per_page=100" |
    Where-Object { -not $_.pull_request }
  $issueSince = (Get-Date).ToUniversalTime().AddDays(-180).ToString('o')
  $liveRecentIssues = Get-LiveCollection "repos/$UP/issues?state=all&since=$([uri]::EscapeDataString($issueSince))&per_page=100" |
    Where-Object { -not $_.pull_request }
  $issueByNumber = @{}
  foreach ($issue in @($liveOpenIssues) + @($liveRecentIssues)) {
    $issueByNumber[[int]$issue.number] = $issue
  }
  $liveIssues = @($issueByNumber.Values)
  $liveItems = New-Object System.Collections.Generic.List[object]
  foreach ($raw in $livePrs) {
    $liveItems.Add([pscustomobject](Convert-LiveItem $raw 'pr' $previousByNumber[[int]$raw.number]))
  }
  foreach ($raw in $liveIssues) {
    $liveItems.Add([pscustomobject](Convert-LiveItem $raw 'issue' $previousByNumber[[int]$raw.number]))
  }
  $src.items = $liveItems.ToArray()
  "live upstream backlog loaded: prs=$($livePrs.Count) openIssues=$($liveOpenIssues.Count) recentIssues=$($liveIssues.Count)"
} catch {
  Write-Warning "live upstream backlog load failed; using previous snapshot: $($_.Exception.Message)"
}

# ---- helpers -------------------------------------------------------------
function Obj { param($h) [pscustomobject]$h }   # hashtable -> object
function Test-PublishableArtifact {
  param($Artifact)

  if ($null -eq $Artifact) {
    return $false
  }

  $hasActions = if ($Artifact -is [System.Collections.IDictionary]) {
    $Artifact.Contains('actions')
  } else {
    $null -ne $Artifact.PSObject.Properties['actions']
  }
  $hasFreshness = $Artifact.generated_at -and
    $Artifact.evaluated_at -and
    $Artifact.source_updated_at
  return $hasActions -and $hasFreshness
}

# ---- tracked-item overlay ------------------------------------------------
# Keyed by upstream number. Only fields that augment the v2 base item.
$OV = @{}

$OV[49604] = @{
  track='fix'; stage='awaiting_pr_approval'; confidence='High'; owes='maintainer'
  status = @{ glyph='🚀'; label='Ready to open upstream PR'; detail='Fork PR #177 review-clean (8 rounds); DockMultiMonitorTests 42/42 green.' }
  design = @{
    root_cause='MonitorConfigReconciler creates secondary-monitor configs with IsCustomized=true but empty band lists; the DockSettings resolvers then return the empty custom bands instead of the global defaults -> blank bar.'
    fix_plan ='Leave secondary configs uncustomized and migrate legacy empty-band configs; extend DockMultiMonitorTests to cover secondary + mixed-DPI + all edge arrangements.'
    confidence='High'; adversary=@{ status='signed_off'; rounds=8 }
    repro   =@('Enable CmdPal Dock (Preview)','Enable dock for a secondary monitor','Secondary shows an empty bar at the top; content indents down')
    verify  =@('With fix: secondary dock renders default bands/stats','Multi-monitor + mixed-DPI + top/bottom/left/right arrangements')
    mirror_issue=@{ number=176; url="https://github.com/$FORK/issues/176" }
  }
  fork_pr=@{ number=177; url="https://github.com/$FORK/pull/177" }; fork_branch='copilot/fix-dock-secondary-blank'
  actions=@(
    @{ id='open-pr-49604'; type='open_upstream_pr'; label='Open upstream PR as me'; primary=$true; confirm=$true
       pr=@{ base='main'; head="$($FORK.Split('/')[0]):copilot/fix-dock-secondary-blank"
             title='[CmdPal][Dock] Fix blank dock on secondary monitor'
             body="Fixes #49604.`n`nSecondary-monitor Dock configs were created as customized with empty band lists, so the resolvers returned empty bands instead of the global defaults. This leaves the secondary configs uncustomized and migrates legacy empty-band configs; DockMultiMonitorTests extended to cover the secondary + mixed-DPI arrangements (42/42 green)."
             draft=$false } }
  )
}

$OV[49599] = @{
  track='fix'; stage='awaiting_pr_approval'; confidence='High'; owes='maintainer'
  status = @{ glyph='🚀'; label='Ready to open upstream PR'; detail='Fork PR #175 review-clean (2 rounds); targeted AoT/SettingsAPI x64 Debug builds pass.' }
  design = @{
    root_cause='AlwaysOnTopModuleInterface\dllmain.cpp set_config() saves settings but never notifies the running daemon; the FileWatcher suppresses the first write, so nothing reloads until restart.'
    fix_plan ='PostMessage(HWND_BROADCAST) the existing settings-changed GUID after save_to_settings_file(); the existing LoadSettings -> SettingsUpdate path then re-applies live. Optional: harden FileWatcher first-write.'
    confidence='High'; adversary=@{ status='signed_off'; rounds=2 }
    repro   =@('Enable Always On Top','Toggle a setting (sound / system-menu / frame / excluded apps)','Pin a window or open its system menu','Observe stale behavior until the module is restarted')
    verify  =@('With fix: setting changes take effect immediately, no restart','Hotkey changes still re-register','Border color/thickness/opacity/corners, excluded apps, sound, system menu all live-update')
    mirror_issue=@{ number=174; url="https://github.com/$FORK/issues/174" }
  }
  fork_pr=@{ number=175; url="https://github.com/$FORK/pull/175" }; fork_branch='copilot/issue-49599-hot-reload-settings'
  actions=@(
    @{ id='open-pr-49599'; type='open_upstream_pr'; label='Open upstream PR as me'; primary=$true; confirm=$true
       pr=@{ base='main'; head="$($FORK.Split('/')[0]):copilot/issue-49599-hot-reload-settings"
             title='[Always On Top] Apply settings changes without a restart'
             body="Fixes #49599.`n`nset_config() saved settings but never notified the running daemon (FileWatcher suppresses the first write), so changes needed a restart. This broadcasts the existing settings-changed GUID after save; the existing LoadSettings -> SettingsUpdate path re-applies live."
             draft=$false } }
  )
}

$OV[49572] = @{
  track='fix'; stage='awaiting_pr_approval'; confidence='High'; owes='maintainer'
  status = @{ glyph='🚀'; label='Ready to open upstream PR'; detail='Fork PR #173 review-clean (2 Copilot rounds).' }
  design = @{
    root_cause='ALT+F4 on the CmdPal Dock forwarded SC_CLOSE and closed it like an app, leaving a stale _docks entry.'
    fix_plan ='Dock WndProc swallows Dock-targeted SC_CLOSE and self-heals the _docks map on unexpected close (snapshot enumeration + guarded removal). Deliberate: ALT+F4 = no-op.'
    confidence='High'; adversary=@{ status='signed_off'; rounds=2 }
    repro   =@('Enable CmdPal Dock','Focus the Dock','Press ALT+F4','Observe the Dock closes like an app (stale _docks entry)')
    verify  =@('With fix: ALT+F4 on the Dock is a no-op; the Dock survives','Intentional closes (disable / monitor-removal / quit) still work')
    mirror_issue=@{ number=172; url="https://github.com/$FORK/issues/172" }
  }
  fork_pr=@{ number=173; url="https://github.com/$FORK/pull/173" }; fork_branch='copilot/fix-dock-closing-with-alt-f4'
  actions=@(
    @{ id='open-pr-49572'; type='open_upstream_pr'; label='Open upstream PR as me'; primary=$true; confirm=$true
       pr=@{ base='main'; head="$($FORK.Split('/')[0]):copilot/fix-dock-closing-with-alt-f4"
             title='[CmdPal][Dock] Do not close the Dock on ALT+F4'
             body="Fixes #49572.`n`nThe Dock WndProc now swallows Dock-targeted SC_CLOSE and self-heals the _docks map on unexpected close. ALT+F4 on the Dock is a no-op; intentional closes still work. Copilot-review-clean in the fork mirror PR."
             draft=$false } }
  )
}

$OV[49617] = @{
  track='fix'; stage='awaiting_design_approval'; confidence='Medium'; owes='maintainer'
  status = @{ glyph='🎯'; label='Design ready — needs approval'; detail='Adversary signed off (Medium). A crash dump would raise confidence to High.' }
  design = @{
    root_cause='App.xaml.cs key-up reads OverlayWindow.AppWindow.IsVisible off the native keyboard-hook thread while the overlay is closing -> access violation across the C++/WinRT boundary.'
    fix_plan ='Track visibility with a UI-independent Volatile flag; keep CloseAnimated on the DispatcherQueue.'
    confidence='Medium'; adversary=@{ status='signed_off'; rounds=3 }
    repro   =@('Open Shortcut Guide','Wait until the Win+number taskbar labels show','Press Win+1 (or any Win+number)','ShortcutGuide.exe crashes + dump')
    verify  =@('With fix: app activates, guide hides, no crash/dump','Normal Win long-press/release still works; Esc / click-outside close')
    mirror_issue=@{ number=180; url="https://github.com/$FORK/issues/180" }
  }
  actions=@(
    @{ id='approve-design-49617'; type='approve_design'; label='Approve design → build fix'; primary=$true; confirm=$true
       comment=@{ target='fork_issue'; number=180; body='Design approved — proceed to build the fix PR in the fork.' }
       note='Posts an approval note on the fork mirror issue; the scheduled job then builds the fix PR in the fork.' }
    @{ id='reqinfo-49617'; type='request_info'; label='Ask author for a crash dump'
       comment=@{ target='issue'; number=49617; body='Thanks for the report! To confirm the root cause, could you attach the crash dump (`%LOCALAPPDATA%\Microsoft\PowerToys\...` or the Windows Error Reporting `.dmp`) from when ShortcutGuide.exe crashed on Win+number? That will let us verify the fix against the exact fault.' }
       note='Medium confidence — a dump confirms the AV path before we open a PR.' }
    @{ id='hold-49617'; type='hold'; label='Not now' }
  )
}

# 49114 -- real bounded issue-to-design demo run. The design converged in the
# fork mirror issue #93 and is intentionally waiting for design approval.
$OV[49114] = @{
  track='fix'; stage='awaiting_design_approval'; confidence='High'; owes='maintainer'
  status = @{ glyph='🎯'; label='Design ready — needs approval'; detail='Adversary signed off after 2 rounds. Root cause and fix plan are captured in fork mirror issue 93.' }
  design = @{
    root_cause='Clipboard history WinRT APIs are invoked from Task.Run on an MTA thread instead of the required STA/UI thread, causing the application hang.'
    fix_plan ='Dispatch history loading through the page UI dispatcher while preserving the existing UI-update handling.'
    confidence='High'; adversary=@{ status='signed_off'; rounds=2 }
    repro   =@('Open Advanced Paste / clipboard history','Trigger history loading','Observe the application hang')
    verify  =@('With fix: history loads without hanging','Existing UI updates remain correct','Exercise repeated history loads and empty history')
    mirror_issue=@{ number=93; url="https://github.com/$FORK/issues/93" }
  }
  actions=@(
    @{ id='approve-design-49114'; type='approve_design'; label='Start fixing'; primary=$true; confirm=$true
       comment=@{ target='fork_issue'; number=93; body='Design approved — proceed to build the fix PR in the fork.' }
       note='Posts an approval note on the fork mirror issue; the scheduled job then builds the fix PR in the fork.' }
    @{ id='hold-49114'; type='hold'; label='Not now' }
  )
}

# Fork review-loop results. These mirrors reached a fresh Copilot review with
# zero new comments; the board exposes them as approval-ready examples.
$OV[49245] = @{
  track='review'; stage='awaiting_review_approval'; confidence='High'; owes='maintainer'
  status = @{ glyph='✅'; label='Review clean — needs approval'; detail='Fork PR 221: Copilot reviewed all changed files and generated no new comments.' }
  fork_pr=@{ number=221; url="https://github.com/$FORK/pull/221" }; fork_branch='pr-iterate/49245'
  proposed_comments=@()
}

$OV[49647] = @{
  track='review'; stage='awaiting_review_approval'; confidence='High'; owes='maintainer'
  status = @{ glyph='✅'; label='Review clean — needs approval'; detail='Fork PR 211: Copilot reviewed all 22 changed files and generated no new comments.' }
  fork_pr=@{ number=211; url="https://github.com/$FORK/pull/211" }; fork_branch='pr-iterate/49647'
  proposed_comments=@()
}

$OV[49639] = @{
  track='review'; stage='awaiting_review_approval'; confidence='High'; owes='maintainer'
  status = @{ glyph='✅'; label='Review clean — needs approval'; detail='Fork PR 205: Copilot reviewed all 10 changed files and generated no new comments.' }
  fork_pr=@{ number=205; url="https://github.com/$FORK/pull/205" }; fork_branch='pr-iterate/49639'
  proposed_comments=@()
}

$OV[49427] = @{
  track='review'; stage='awaiting_review_approval'; confidence='Medium'; owes='maintainer'
  status = @{ glyph='📝'; label='Review drafted — needs approval'; detail='Fork PR 218 converged with zero new Copilot comments and zero unresolved threads; local build remains blocked by unrelated charset/PCH failures.' }
  fork_pr=@{ number=218; url="https://github.com/$FORK/pull/218" }; fork_branch='pr-iterate/49427'
  proposed_comments=@(
    @{ id='c-49427-1'; severity='medium'; disposition='proposed'; in_diff=$false
       title='Document the non-elevated apply requirement'
       body='The implementation rejects profile set operations from an elevated process before touching the user-controlled settings tree, but the reference examples do not mention that requirement. Add a note that apply operations must run non-elevated, while read-only operations remain available as appropriate.'
       path='doc/dsc/profile-resource.md'; line=46 },
    @{ id='c-49427-2'; severity='medium'; disposition='proposed'; in_diff=$false
       title='Do not overstate localization coverage'
       body='The resource localizes the stable error prefix, but validation details and warning text remain invariant English diagnostics. Either move the complete user-facing messages into Resources.resx, or narrow the checklist wording to say that stable prefixes are localized while technical diagnostic details remain invariant.'
       path='src/dsc/v3/PowerToys.DSC/Properties/Resources.resx'; line=185 }
  )
}

$OV[49682] = @{
  track='fix'; stage='awaiting_design_approval'; confidence='High'; owes='maintainer'
  status = @{ glyph='🎯'; label='Design ready — needs approval'; detail='Fork mirror issue 224 converged after adversary review. Root cause is a mixed Windows App SDK deployment mode that can fail during editor startup; runtime confirmation remains desirable.' }
  design = @{
    root_cause='KeyboardManagerEditorUI omitted WindowsAppSDKSelfContained=true, allowing machine-wide and app-local Windows App SDK payloads to mix and causing startup fail-fast during title-bar/input initialization.'
    fix_plan ='Enable Windows App SDK self-contained deployment, initialize logging synchronously before window construction, add pre/post-main-window startup logs, and avoid catch-and-continue handling for native fail-fast.'
    confidence='High'; adversary=@{ status='signed_off'; rounds=2 }
    repro=@('Install the affected Store build','Open Keyboard Manager Editor from Settings','Observe that the editor silently fails to launch')
    verify=@('Compare app-local WinRT manifests and bootstrap references','Test Store installs on Windows 10 and Windows 11','Confirm startup logs before and after main-window construction')
    mirror_issue=@{ number=224; url="https://github.com/$FORK/issues/224" }
  }
  actions=@(
    @{ id='approve-design-49682'; type='approve_design'; label='Start fixing'; primary=$true; confirm=$true
       comment=@{ target='fork_issue'; number=224; body='Design approved — proceed to build the fix PR in the fork.' }
       note='Posts an approval note on the fork mirror issue; the fix workflow remains fork-only until upstream approval.' }
    @{ id='hold-49682'; type='hold'; label='Not now' }
  )
}

$OV[49692] = @{
  track='review'; stage='awaiting_review_approval'; confidence='High'; owes='maintainer'
  status = @{ glyph='✅'; label='Review clean — needs approval'; detail='Fork PR 226 converged with zero Copilot comments and zero unresolved threads; Essentials, Runner, and Settings UI Debug builds passed.' }
  fork_pr=@{ number=226; url="https://github.com/$FORK/pull/226" }; fork_branch='pr-iterate/49692-v2'
  proposed_comments=@()
}

# Additional current fork mirrors whose latest Copilot review reported zero new
# comments. They remain approval-gated; the board does not infer upstream
# approval from the clean review alone.
foreach ($reviewMirror in @(
  @{ upstream=48816; fork=213; branch='pr-iterate/48816'; detail='Fork PR 213 Copilot review clean; approval remains gated.' },
  @{ upstream=48904; fork=214; branch='pr-iterate/48904'; detail='Fork PR 214 Copilot review clean; approval remains gated.' },
  @{ upstream=48700; fork=223; branch='pr-iterate/48700'; detail='Fork PR 223 Copilot review clean; approval remains gated.' },
  @{ upstream=49034; fork=186; branch='pr-iterate/49034'; detail='Fork PR 186 Copilot review clean; approval remains gated.' },
  @{ upstream=49615; fork=216; branch='pr-iterate/49615'; detail='Fork PR 216 Copilot review clean; approval remains gated.' },
  @{ upstream=49597; fork=209; branch='pr-iterate/49597'; detail='Fork PR 209 Copilot review clean; approval remains gated.' }
)) {
  $OV[$reviewMirror.upstream] = @{
    track='review'; stage='awaiting_review_approval'; confidence='Medium'; owes='maintainer'
    status = @{ glyph='✅'; label='Fork review clean — needs approval'; detail=$reviewMirror.detail }
    fork_pr=@{ number=$reviewMirror.fork; url="https://github.com/$FORK/pull/$($reviewMirror.fork)" }
    fork_branch=$reviewMirror.branch; proposed_comments=@()
  }
}

$OV[49197] = @{
  track='review'; stage='review_drafted'; confidence='Medium'; owes='maintainer'
  status = @{ glyph='📝'; label='Review drafted — needs approval'; detail='Fork PR 227 converged after 3 Copilot rounds with zero new comments and zero unresolved threads. Spelling remains pending; local builds are blocked by an unrelated encoding warning.' }
  fork_pr=@{ number=227; url="https://github.com/$FORK/pull/227" }; fork_branch='pr-iterate/49197'
  proposed_comments=@(
    @{ id='c-49197-1'; severity='high'; disposition='proposed'; in_diff=$false
       title='Hold merge until dependency PR 49246 lands'
       body='This telemetry path depends on the API and behavior introduced by PR 49246. Please either wait for that dependency to merge or document the compatibility contract and validation plan for the current base.' },
    @{ id='c-49197-2'; severity='medium'; disposition='proposed'; in_diff=$false
       title='Add a concrete telemetry analysis plan'
       body='Please describe the target graph or analysis plan for the new telemetry snapshots, including which decisions the data will support and how sampling/privacy constraints will be evaluated.' },
    @{ id='c-49197-3'; severity='low'; disposition='proposed'; in_diff=$false
       title='Clarify the LatencyMs metric name'
       body='The recorded value appears to measure filtering duration rather than end-to-end latency. Consider renaming it to FilterDurationMs or documenting the exact start and end events.' },
    @{ id='c-49197-4'; severity='low'; disposition='proposed'; in_diff=$false
       title='Correlate abandoned searches'
       body='Consider recording SearchAbandoned together with a stable event correlation ID so abandoned and completed searches can be analyzed as one interaction.' }
  )
}

$OV[49194] = @{
  track='review'; stage='review_drafted'; confidence='Medium'; owes='maintainer'
  status = @{ glyph='🛑'; label='Review drafted — blockers found'; detail='Fork PR 233 resolved review threads but the final review found two architectural blockers; build is blocked by an unrelated C4819 encoding failure.' }
  fork_pr=@{ number=233; url="https://github.com/$FORK/pull/233" }; fork_branch='pr-iterate/49194'
  proposed_comments=@(
    @{ id='c-49194-1'; severity='high'; disposition='proposed'; in_diff=$false
       title='Tag fallback label snapshots by query generation'
       body='Use immutable fallback label snapshots tagged with the query generation so a slower earlier response cannot overwrite the first-paint result for a newer query.' },
    @{ id='c-49194-2'; severity='high'; disposition='proposed'; in_diff=$false
       title='Do not serialize RPCs behind a blocking gate'
       body='Avoid making newer queries wait behind a blocking RPC gate. A hung extension can otherwise prevent all subsequent queries from completing; prefer cancellation or generation-aware result suppression.' }
  )
}

$OV[49474] = @{
  track='review'; stage='awaiting_author'; confidence='High'; owes='author'
  status = @{ glyph='⏳'; label='Waiting for author'; detail='Fork PR 232 review converged with zero Copilot comments and the build passed. Upstream PR has a merge conflict and Needs-Author-Feedback; request a short demo and validation confirmation.' }
  fork_pr=@{ number=232; url="https://github.com/$FORK/pull/232" }; fork_branch='pr-iterate/49474'
  proposed_comments=@()
}

$OV[49669] = @{
  track='fix'; stage='needs_reply'; confidence='Low'; owes='author'
  status = @{ glyph='❓'; label='Needs diagnostic information'; detail='Fork mirror issue 234 found the report non-diagnostic: it contains a separate PowerToys Run crash but no CmdPal activation, package, AppX, or crash evidence.' }
  design = @{
    root_cause='Unconfirmed — the supplied evidence does not identify a CmdPal startup failure.'
    fix_plan ='Collect CmdPal ModuleInterface logs, package registration/version, AppX events, Microsoft.CmdPal.UI.exe exit/crash data, and a CmdPal-specific reproduction.'
    confidence='Low'; adversary=@{ status='diagnostic_only'; rounds=1 }
    repro=@('Attempt to start CmdPal','Capture the exact failure and timestamp','Collect the requested CmdPal and AppX diagnostics')
    verify=@('Confirm the logs correspond to the affected package/version','Reproduce the startup failure with CmdPal-specific evidence')
    mirror_issue=@{ number=234; url="https://github.com/$FORK/issues/234" }
  }
  actions=@(
    @{ id='request-info-49669'; type='request_info'; label='Reply with diagnostic request'; primary=$true
       comment=@{ target='issue'; number=49669; body='Could you provide CmdPal ModuleInterface logs, the installed package registration/version, relevant AppX event entries, Microsoft.CmdPal.UI.exe exit or crash data, and a CmdPal-specific reproduction? The current report appears to contain a separate PowerToys Run crash, so these details are needed to identify the CmdPal failure.' } }
    @{ id='hold-49669'; type='hold'; label='Wait for diagnostics' }
  )
}

$OV[49711] = @{
  track='fix'; stage='awaiting_design_approval'; confidence='Medium'; owes='maintainer'
  status = @{ glyph='🎯'; label='Design ready — narrow fix'; detail='Fork mirror issue 235 converged on a defensive tray-registration retry. Cold-boot logs are still missing, so confidence is medium.' }
  design = @{
    root_cause='Shell_NotifyIcon(NIM_ADD) can fail while Explorer is unavailable during cold start; the existing recovery path does not guarantee a retry.'
    fix_plan ='Add a bounded retry for failed tray registration, handle Explorer restart, and verify settings toggles, shutdown, and duplicate prevention.'
    confidence='Medium'; adversary=@{ status='signed_off'; rounds=2 }
    repro=@('Enable Power Display tray icon','Cold-boot Windows while Explorer is starting','Observe the icon is missing until PowerToys restarts')
    verify=@('Retry succeeds without duplicate icons','Explorer restart recovers the icon','Settings toggles and shutdown remain clean')
    mirror_issue=@{ number=235; url="https://github.com/$FORK/issues/235" }
  }
  actions=@(
    @{ id='approve-design-49711'; type='approve_design'; label='Start fixing'; primary=$true
       comment=@{ target='fork_issue'; number=235; body='Design approved — proceed to build the narrow defensive fix in the fork.' } }
    @{ id='request-info-49711'; type='request_info'; label='Request cold-boot logs'
       comment=@{ target='issue'; number=49711; body='Could you provide Power Display logs from a cold boot where the tray icon is missing? We need to confirm the failed Shell_NotifyIcon registration and retry path.' } }
  )
}

$OV[49704] = @{
  track='fix'; stage='needs_reply'; confidence='Medium'; owes='maintainer'
  status = @{ glyph='❓'; label='Needs reproduction confirmation'; detail='Fork mirror issue 236 converged on a Win-key recovery race, but a Windows 11 / PowerToys 0.100.2 trace is still needed before implementation.' }
  design = @{
    root_cause='Win-key consumption relies on transient drag flags after MouseProc ends the drag; foreground/input recovery can clear the state and forward Win-key up to Explorer.'
    fix_plan ='Preserve explicit drag-completion consumption state through key release, then validate both modifier release orders and foreground transitions.'
    confidence='Medium-high'; adversary=@{ status='signed_off'; rounds=2 }
    repro=@('Hold Win and start Grab and Move','Release the mouse while holding Win','Release Win and observe whether Start opens')
    verify=@('Repeat 20 times with LWIN and RWIN','Test reverse release order and resize','Verify click-only passthrough and foreground transitions')
    mirror_issue=@{ number=236; url="https://github.com/$FORK/issues/236" }
  }
  actions=@(
    @{ id='request-info-49704'; type='request_info'; label='Request trace / repro'
       comment=@{ target='issue'; number=49704; body='Could you provide a Windows 11 / PowerToys 0.100.2 trace or a short recording confirming the Win-key recovery race after Grab and Move? Please include which modifier and release order reproduces it.' } }
  )
}

$OV[49677] = @{
  track='fix'; stage='needs_reply'; confidence='Medium-high'; owes='maintainer'
  status = @{ glyph='❓'; label='Needs reproduction details'; detail='Fork mirror issue 237 converged on a modifier-swap state race, but the clean two-row repro and environment details are still missing.' }
  design = @{
    root_cause='Modifier-only remaps are not atomic: the physical source modifier remains visible while the replacement modifier is injected, creating transient or stale Alt+Win state.'
    fix_plan ='Add an atomic modifier-swap path with explicit per-source state, paired transitions, injection-failure handling, and engine regression tests.'
    confidence='Medium-high'; adversary=@{ status='signed_off'; rounds=2 }
    repro=@('Configure Left Alt to Left Win and Left Win to Left Alt','Tap and hold both keys','Exercise Start, Alt+Tab, Alt+F4, Win+R/E/M, and repeated keyups')
    verify=@('Generated events do not recurse','Paired transitions remain balanced','Injection failure leaves no stale modifier state')
    mirror_issue=@{ number=237; url="https://github.com/$FORK/issues/237" }
  }
  actions=@(
    @{ id='request-info-49677'; type='request_info'; label='Request clean repro details'
       comment=@{ target='issue'; number=49677; body='Could you provide the Windows build, elevation state, a clean two-row repro recording or trace, and whether another remapper is installed? Please include which key combinations fail.' } }
  )
}

$OV[49671] = @{
  track='review'; stage='review_in_progress'; confidence='Medium'; owes='us'; needs_revalidation=$true
  status = @{ glyph='🔄'; label='Review incomplete — fresh Copilot pass needed'; detail='Fork PR 231 built the changed UI-test projects and has zero unresolved threads, but the fresh Copilot re-request timed out. Draft concerns include documentation, exception cleanup, test coverage, AMD support, and UIA accessibility.' }
  fork_pr=@{ number=231; url="https://github.com/$FORK/pull/231" }; fork_branch='pr-iterate/49671-v2'
  proposed_comments=@(
    @{ id='c-49671-1'; severity='medium'; disposition='proposed'; in_diff=$false; title='Fix broken Sandbox documentation'; body='The Sandbox documentation path and instructions do not match the implementation. Please update the documented setup and verification steps.' },
    @{ id='c-49671-2'; severity='medium'; disposition='proposed'; in_diff=$false; title='Clean up certificates on exception paths'; body='Ensure temporary certificates are removed when setup fails partway through so retries do not inherit stale credentials.' },
    @{ id='c-49671-3'; severity='medium'; disposition='proposed'; in_diff=$false; title='Cover AltWindowCycle in the test scope'; body='The test filters do not cover the AltWindowCycle path introduced by this change. Add the missing project or narrow the stated coverage.' },
    @{ id='c-49671-4'; severity='low'; disposition='proposed'; in_diff=$false; title='Correct the test filters'; body='The current filters imply broader coverage than the selected test projects actually provide. Align the filters and documentation with the executed tests.' },
    @{ id='c-49671-5'; severity='low'; disposition='proposed'; in_diff=$false; title='Handle AMD svm capability'; body='Please confirm the AMD svm capability is handled consistently with the supported virtualization configurations.' },
    @{ id='c-49671-6'; severity='medium'; disposition='proposed'; in_diff=$false; title='Avoid UIA accessibility pollution'; body='Ensure the new UI elements do not add unintended UI Automation nodes or duplicate accessible content.' }
  )
}

# 49136 -- review track, already partially posted. Demonstrates the per-comment
# "choose which to post" workflow with real dispositions from the last review.
$OV[49136] = @{
  track='review'; stage='review_in_progress'; confidence='High'; owes='us'; needs_revalidation=$true
  status = @{ glyph='🔄'; label='Needs re-review'; detail='Upstream head changed from 23987e4b to a413b177. The prior review remains historical; fork review must resume on the new head.' }
  upstream_pr=@{ number=49136; url="https://github.com/$UP/pull/49136" }
  fork_pr=@{ number=178; url="https://github.com/$FORK/pull/178" }; fork_branch='pr-iterate/49136-v3'
  head_sha='23987e4b'
  proposed_comments=@(
    @{ id='c-m5'; severity='medium'; disposition='posted'; in_diff=$true
       path='src/modules/keyboardmanager/KeyboardManagerEditorUI/Helpers/ValidationHelper.cs'; line=334; side='RIGHT'
       title='Edit-mode conflict check can miss a real duplicate'
       body='`HasConflictingModifierMapping`/`IsDuplicateMapping` gate edit mode with `upperLimit = isEditMode ? 1 : 0` and test `conflictCount > upperLimit`. That count tolerance can''t distinguish "matches the edited row" from "collides with one other mapping" — both give `conflictCount == 1`, so editing a row into a value that duplicates one different existing mapping is accepted. The public `Validate*` methods already thread an `editingRemapping`; could these use it to exclude the edited row by identity instead of by count?' }
    @{ id='c-m10'; severity='low'; disposition='posted'; in_diff=$false
       title='Add load/save round-trip tests for the condition contract'
       body='This revision adds the `condition` field so alone remaps round-trip through `aloneSingleKeyReMap`. A unit test that saves an "alone" single-key remap and asserts it reloads into the alone table (and an "always" one into the regular table) would lock the contract down.' }
    @{ id='c-m9'; severity='low'; disposition='posted'; in_diff=$false
       title='Alone badge missing on DisabledList rows'
       body='The "alone" badge added to the RemappingList rows is not mirrored on the DisabledList rows. If a disabled entry can carry the alone condition, show the badge there too for consistency.' }
    @{ id='c-m1'; severity='medium'; disposition='withdrawn'; reason='Undercut on head re-verification: the validator already blocks generic-vs-sided as ambiguous, so a generic alone source cannot be persisted unnoticed.'
       title='Generic-modifier alone sources never fire' }
    @{ id='c-m2'; severity='medium'; disposition='withdrawn'; reason='Key-up paths clear alone state unconditionally (no state corruption) and a failed release has no clean recovery — no useful one-click fix.'
       title='Stuck key when combination key-up injection fails' }
    @{ id='c-m3'; severity='medium'; disposition='withdrawn'; reason='Author documented the pending-on-failure continue as deliberate.'
       title='Promotion failure still lets release fire alone action' }
    @{ id='c-m4'; severity='medium'; disposition='withdrawn'; reason='Anchor was wrong: those lines are the alone->single dispatch, not the editor-suspension guard.'
       title='Editor-suspension leaves stale alone state' }
    @{ id='c-m6'; severity='medium'; disposition='withdrawn'; reason='Premise wrong: dedup compares the full mapping value (condition+target+op), so duplicates are exact-equal and which copy is kept is irrelevant.'
       title='Duplicate collapse ignores IsActive' }
    @{ id='c-m7'; severity='medium'; disposition='withdrawn'; reason='Already an open comment of mine on this PR (HasOtherHeldAloneKey).'
       title='HasOtherHeldAloneKey ignores a normal key held first' }
    @{ id='c-m8'; severity='low'; disposition='withdrawn'; reason='Author documented the auto-repeat suppression while in a combination as deliberate.'
       title='Promoted non-modifier alone key loses auto-repeat' }
  )
  actions=@(
    @{ id='rereview-49136'; type='post_review'; label='Post selected comments as a review'; primary=$true; confirm=$true
       review=@{ pr=49136; event='COMMENT'; body_prefix='' } }
    @{ id='rerun-49136'; type='rerun'; label='Re-run review loop (head changed?)'
       note='If the author pushed new commits since head 23987e4b, re-mirror and re-run the fork review loop before posting again.' }
  )
}

# ---- mirror-trace index (fork MuyuanMS/PowerToys) ------------------------
# Even with no local agent artifact, a fork issue/PR is itself a status signal:
# "an agent has already started work on this upstream item in the mirror."
# Map upstream number -> the fork trace by parsing fork titles and branch names.
#   titles:  "[Issue 49604] ..."  /  "[PR 49625] ..."  /  "... (Issue 49572)"
#   branches: "copilot/issue-49599-..."  /  "pr-iterate/49136-v4"
$mirror = @{}
function Add-MirrorTrace {
  param([int]$Num, $Kind, $ForkNum, $Title, $State, $Branch)
  if ($Num -le 0) { return }
  if (-not $mirror.ContainsKey($Num)) {
    $mirror[$Num] = [ordered]@{
      kind=$Kind; fork_number=$ForkNum; fork_title=$Title; fork_state=$State
      fork_branch=$Branch; url="https://github.com/$FORK/$(if($Kind -eq 'pr'){'pull'}else{'issues'})/$ForkNum"
    }
  }
}
function Get-UpstreamNums {
  param([string]$Title, [string]$Branch)
  $nums = New-Object System.Collections.Generic.List[int]
  foreach ($m in [regex]::Matches($Title, '\[(?:Issue|PR)\s+(\d+)')) { $nums.Add([int]$m.Groups[1].Value) }
  foreach ($m in [regex]::Matches($Title, '(?i)\(Issue\s+(\d+)\)'))   { $nums.Add([int]$m.Groups[1].Value) }
  if ($Branch) {
    $bm = [regex]::Match($Branch, '(?i)(?:issue-|pr-iterate/|/)(\d{4,6})')
    if ($bm.Success) { $nums.Add([int]$bm.Groups[1].Value) }
  }
  $nums | Select-Object -Unique
}
try {
  $forkPrs = gh pr list -R $FORK --state open --json number,title,headRefName,state --limit 200 2>$null | ConvertFrom-Json
  foreach ($p in $forkPrs) {
    foreach ($un in (Get-UpstreamNums $p.title $p.headRefName)) {
      Add-MirrorTrace -Num $un -Kind 'pr' -ForkNum $p.number -Title $p.title -State $p.state -Branch $p.headRefName
    }
  }
  $forkIss = gh issue list -R $FORK --state open --json number,title,state --limit 200 2>$null | ConvertFrom-Json
  foreach ($i in $forkIss) {
    foreach ($un in (Get-UpstreamNums $i.title $null)) {
      Add-MirrorTrace -Num $un -Kind 'issue' -ForkNum $i.number -Title $i.title -State $i.state -Branch $null
    }
  }
  "mirror traces found: $($mirror.Count)"
} catch {
  Write-Warning "fork mirror scan skipped (gh unavailable?): $($_.Exception.Message)"
}

# Record current fork review mirrors whose latest Copilot review is clean.
# A clean Copilot pass alone is not approval-ready: build, context, spelling,
# and fresh-review requirements still belong to the review skill.
try {
  $openUpstream = @{}
  foreach ($live in @($src.items)) { $openUpstream[[int]$live.number] = $live }
  foreach ($fp in @($forkPrs)) {
    if (-not ($fp.headRefName -match '(?i)pr-iterate' -or $fp.title -match '\[PR\s+\d+\]')) { continue }
    $upNums = @(Get-UpstreamNums $fp.title $fp.headRefName)
    foreach ($upNum in $upNums) {
      if (-not $openUpstream.ContainsKey($upNum) -or $OV.ContainsKey($upNum)) { continue }
      $forkReview = gh pr view $fp.number -R $FORK --json reviews 2>$null | ConvertFrom-Json
      $latest = @($forkReview.reviews | Sort-Object submittedAt -Descending | Select-Object -First 1)
      if ($latest -and ([string]$latest[0].body -match '(?i)generated no new comments')) {
        $OV[$upNum] = @{
          track='review'; stage='review_in_progress'; confidence='Medium'; owes='us'; needs_revalidation=$true
          status = @{ glyph='🔄'; label='Copilot clean — validation pending'; detail="Fork PR $($fp.number) latest Copilot review generated no new comments; local build/context validation is still required before approval." }
          fork_pr=@{ number=[int]$fp.number; url="https://github.com/$FORK/pull/$($fp.number)" }
          fork_branch=$fp.headRefName; proposed_comments=@()
        }
      }
    }
  }
} catch {
  Write-Warning "clean fork review promotion skipped: $($_.Exception.Message)"
}

# ---- build: slim index + per-number artifacts ----------------------------
# Two outputs, matching the "folder per number" model:
#   data/index.json          slim list of every open item (basic facts + hint + has_artifact)
#   data/items/<number>.json the latest agent artifact (drafted, approvable content) for tracked items
# Basic facts + live status are refreshed from GitHub client-side; the index is just the
# cheap regenerated backlog so the board renders instantly without 180 live calls.
$enc      = New-Object System.Text.UTF8Encoding($false)
$itemsDir = Join-Path $outDir 'items'
New-Item -ItemType Directory -Force -Path $itemsDir | Out-Null
$existingArtifacts = @{}
$workerArtifacts = @{}
foreach ($path in Get-ChildItem $itemsDir -Filter '*.json' -ErrorAction SilentlyContinue) {
  try {
    $artifact = Get-Content $path.FullName -Raw | ConvertFrom-Json
    if ([int]$artifact.number -eq [int]$path.BaseName) {
      $existingArtifacts[[int]$artifact.number] = $artifact
      if ($artifact.evaluated_at -and $artifact.source_updated_at) {
        $workerArtifacts[[int]$artifact.number] = $artifact
      }
    }
  } catch {
    Write-Warning "Ignoring invalid existing artifact $($path.Name): $($_.Exception.Message)"
  }
}

$now       = (Get-Date).ToString('o')
$indexList = New-Object System.Collections.Generic.List[object]
$artifactNumbers = New-Object System.Collections.Generic.List[int]

foreach ($it in $src.items) {
  $n = [int]$it.number
  $o = if ($existingArtifacts.ContainsKey($n)) {
    $existingArtifacts[$n]
  } elseif (-not $standaloneMode) {
    $OV[$n]
  } else {
    $null
  }
  # Worker artifacts are the durable source of truth. Rebuild the index around
  # them, but never project them through the smaller overlay schema and drop
  # freshness, judgment, review, or design evidence.
  $writeArtifact =
    (-not $standaloneMode -and -not $existingArtifacts.ContainsKey($n) -and $OV.ContainsKey($n))
  # Pulse publishes only artifacts with an explicit actions array. Historical
  # traces without an executable/monitor action remain useful for stage hints,
  # but must not enter the downloadable artifact manifest.
  $hasArtifact = Test-PublishableArtifact $o
  $track = if ($o -and $o.track) { $o.track } elseif ($it.track) { $it.track } else { $null }
  $stage = if ($o -and $o.stage) { $o.stage } else { $it.stage }

  # ---- slim index entry (basic, public-ish facts only) ----
  $iowes = if ($o -and $o.owes) { $o.owes } elseif ($it.owes) { $it.owes } else { 'us' }
  $mt = $mirror[$n]
  # agent_status: an explicit status for EVERY item (never blank) ---
  #   artifact  -> a local agent produced drafted content (use its track)
  #   mirror    -> work started in the fork but no drafted artifact yet
  #   none      -> no agent has touched this yet ("no agent working on it" is a status)
  $agentStatus = if ($hasArtifact) { if ($track) { $track } else { 'tracked' } }
                 elseif ($mt) { 'mirror' }
                 else { 'none' }
  # ---- classify issue type (bug|feature|other) from labels ----
  $issueType = $null
  if ($it.kind -eq 'issue') {
    $lbls = @($it.labels) -join '|'
    if     ($lbls -match '(?i)Issue-Feature|Enhancement|Feature-Request|\bfeature\b') { $issueType = 'feature' }
    elseif ($lbls -match '(?i)Issue-Bug|\bbug\b')                                     { $issueType = 'bug' }
    else                                                                              { $issueType = 'other' }
  }
  # ---- outstanding proposed comments (drives the "good to go -> more comments" sort) ----
  $proposedOpen = 0
  if ($o -and $o.proposed_comments) {
    $proposedOpen = @($o.proposed_comments | Where-Object { $_.disposition -eq 'proposed' }).Count
  }
  $primary = $null
  if ($hasArtifact -and $stage -eq 'owned_elsewhere') {
    $primary = [ordered]@{ type='monitor'; label='Owned elsewhere' }
  } elseif ($hasArtifact -and $stage -eq 'ci_blocked') {
    $primary = [ordered]@{ type='monitor'; label='Wait for CI' }
  } elseif ($hasArtifact -and $o.needs_revalidation) {
    $primary = [ordered]@{ type='rerun'; label=if ($it.kind -eq 'pr') { 'Re-run review' } else { 'Re-run triage' } }
  } elseif ($hasArtifact -and $it.kind -eq 'pr') {
    if ($proposedOpen -gt 0) {
      $primary = [ordered]@{ type='review'; label='Post comments' }
    } elseif ($iowes -ne 'author') {
      $primary = [ordered]@{ type='approve'; label='Approve' }
    }
  } elseif ($hasArtifact -and $o.actions) {
    $action = @($o.actions | Where-Object { $_.type -eq 'request_info' }) | Select-Object -First 1
    if (-not $action) { $action = @($o.actions | Where-Object { $_.type -eq 'open_upstream_pr' }) | Select-Object -First 1 }
    if (-not $action) { $action = @($o.actions | Where-Object { $_.type -eq 'approve_design' }) | Select-Object -First 1 }
    if ($action) {
      $label = switch ($action.type) {
        'request_info' { 'Reply with suggested comments' }
        'open_upstream_pr' { 'Create PR' }
        'approve_design' { 'Start fixing' }
        default { $action.label }
      }
      $primary = [ordered]@{ type=$action.type; label=$label }
    }
  } elseif ($it.state -ne 'closed' -and -not ($iowes -eq 'author')) {
    $primary = if ($it.kind -eq 'pr') {
      [ordered]@{ type=if ($mt) { 'rerun' } else { 'start_review' }; label=if ($mt) { 'Resume review' } else { 'Start review' } }
    } else {
      $recentBug = $false
      try { $recentBug = ($issueType -eq 'bug' -and ([datetime]$it.updated_at -ge (Get-Date).ToUniversalTime().AddDays(-30))) } catch {}
      [ordered]@{
        type=if ($mt) { 'rerun' } else { 'start_triage' }
        label=if ($mt) { if ($recentBug) { 'Resume bug triage' } else { 'Resume triage' } }
              else { if ($recentBug) { 'Check bug' } else { 'Start triage' } }
      }
    }
  }
  $entry = [ordered]@{
    id=$it.id; kind=$it.kind; number=$n; url=$it.url; title=$it.title; author=$it.author
    state=$it.state
    is_community=[bool]$it.is_community; mine=[bool]$it.mine; is_cmdpal=[bool]$it.is_cmdpal
    track=$track; stage=$stage; owes=$iowes; pending_author=($iowes -eq 'author')
    waiting_since=if ($o -and $o.waiting_since) { $o.waiting_since } elseif ($iowes -eq 'author') { $it.updated_at } else { $null }
    has_artifact=$hasArtifact; agent_status=$agentStatus; issue_type=$issueType
    proposed_open=$proposedOpen
    primary_action=if ($primary) { [pscustomobject]$primary } else { $null }
    labels=@($it.labels); created_at=$it.created_at; updated_at=$it.updated_at
    comments=$it.comments; priority=$it.priority
  }
  if ($mt) { $entry['mirror'] = [pscustomobject]$mt }
  $indexList.Add([pscustomobject]$entry)

  if (-not $hasArtifact) { continue }

  # ---- per-number artifact (the drafted, approvable payload) ----
  $owes = if ($o.owes) { $o.owes } elseif ($it.owes) { $it.owes } else { 'us' }
  $art = [ordered]@{
    number=$n; kind=$it.kind; track=$track; stage=$stage; owes=$owes
    pending_author=($owes -eq 'author'); generated_at=$now
    evaluated_at=if ($o.evaluated_at) { $o.evaluated_at } else { $now }
    source_updated_at=if ($o.source_updated_at) { $o.source_updated_at } else { $it.updated_at }
    status     = Obj $o.status
    next_action= if ($o.next_action) { Obj $o.next_action } elseif ($it.next_action) { Obj @{ glyph=$it.next_action.glyph; label=$it.next_action.label; reason=$it.next_action.reason } } else { $null }
    actions    = @($o.actions | ForEach-Object { Obj $_ })
  }
  if ($o.needs_revalidation) { $art.needs_revalidation = $true }
  if ($o.confidence)  { $art.confidence  = $o.confidence }
  if ($o.head_sha)    { $art.head_sha    = $o.head_sha }        # staleness anchor vs live PR head
  if ($o.design)      { $art.design      = Obj $o.design }
  if ($o.proposed_comments) { $art.proposed_comments = @($o.proposed_comments | ForEach-Object { Obj $_ }) }
  if ($o.upstream_pr) { $art.upstream_pr = Obj $o.upstream_pr } elseif ($it.upstream_pr) { $art.upstream_pr = $it.upstream_pr }
  if ($it.upstream_issue) { $art.upstream_issue = $it.upstream_issue }
  if ($o.fork_pr)     { $art.fork_pr     = Obj $o.fork_pr }
  if ($o.fork_branch) { $art.fork_branch = $o.fork_branch }
  if ($o.fork_issue)  { $art.fork_issue  = Obj $o.fork_issue } elseif ($it.fork_issue) { $art.fork_issue = $it.fork_issue }

  if ($writeArtifact) {
    $aj = ([pscustomobject]$art) | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText((Join-Path $itemsDir "$n.json"), $aj, $enc)
  }
  $artifactNumbers.Add($n)
}

# ---- AI impact (leadership view) -----------------------------------------
# Simple, honest snapshot derived from what the local agents have produced.
# NOTE: this is the current-board snapshot; a cumulative ledger (surviving closed
# items) can be layered on later. Kept intentionally small for now.
$impPrReviewed = 0; $impIssueDesigned = 0; $impPosted = 0; $impDrafted = 0
foreach ($ovKey in $OV.Keys) {
  $ovItem = $OV[$ovKey]
  if ($ovItem.track -eq 'review') { $impPrReviewed++ }
  if ($ovItem.track -eq 'fix')    { $impIssueDesigned++ }
  if ($ovItem.proposed_comments) {
    $impDrafted += @($ovItem.proposed_comments).Count
    $impPosted  += @($ovItem.proposed_comments | Where-Object { $_.disposition -eq 'posted' }).Count
  }
}
$impact = [ordered]@{
  as_of                       = $now
  issues_helped               = $impIssueDesigned   # issues we triaged + produced a fix design/PR for
  prs_iterated                = $impPrReviewed       # community PRs we ran through the review loop
  constructive_comments_posted= $impPosted           # review comments actually posted upstream
  comments_drafted            = $impDrafted          # total drafted (posted + withdrawn + pending)
}

# ---- index.json (+ .js fallback) -----------------------------------------
$cPr=0; $cIss=0; $cCom=0
foreach ($x in $indexList) {
  if ($x.kind -eq 'pr') { $cPr++ } elseif ($x.kind -eq 'issue') { $cIss++ }
  if ($x.is_community) { $cCom++ }
}
$idxItems = $indexList.ToArray()
$artNums  = $artifactNumbers.ToArray()
$index = [ordered]@{}
$index['generated_at']     = $now
$index['window_since']     = [string]$src.window_since
$index['upstream']         = $UP
$index['fork']             = $FORK
$index['maintainer']       = $ME
$index['stage_labels']     = $src.stage_labels
$index['tracks']           = $src.tracks
$index['counts']           = [ordered]@{ open_prs=$cPr; open_issues=$cIss; community=$cCom; artifacts=$artNums.Count }
$index['impact']           = $impact
$index['artifact_numbers'] = $artNums
$index['items']            = $idxItems
$ij = $index | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText((Join-Path $outDir 'index.json'), $ij, $enc)
[System.IO.File]::WriteAllText((Join-Path $outDir 'index.js'), ("window.BOARD_INDEX = " + $ij + ";"), $enc)

"index.json written: items=$($indexList.Count)  artifacts=$($artifactNumbers.Count) [$(( $artifactNumbers | Sort-Object ) -join ', ')]"
