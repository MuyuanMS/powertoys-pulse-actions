function Convert-LiveItem {
  param($Raw, [string]$Kind, $Previous, [int]$CommentCount, $Readiness, [Parameter(Mandatory)][string]$Maintainer)
  $labels = @($Raw.labels | ForEach-Object { $_.name })
  $assignees = @($Raw.assignees | ForEach-Object { $_.login } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
  $author = if ($Raw.user.login) { $Raw.user.login } else { 'unknown' }
  $association = [string]$Raw.author_association
  $community = $association -notin @('MEMBER','OWNER','COLLABORATOR')
  [ordered]@{
    id = "$Kind-$($Raw.number)"; kind = $Kind; number = [int]$Raw.number
    url = $Raw.html_url; title = $Raw.title; author = $author
    state = $Raw.state
    is_draft = [bool]$Raw.draft
    is_community = [bool]$community; mine = ($author -ieq $Maintainer)
    is_cmdpal = (($labels -join '|') -match '(?i)Command Palette|CmdPal')
    labels = $labels; created_at = $Raw.created_at; updated_at = $Raw.updated_at
    assignees = $assignees
    comments = $CommentCount
    review_decision = if ($Kind -eq 'pr' -and $Readiness.reviewDecision) { [string]$Readiness.reviewDecision } else { $null }
    ci_state = if ($Kind -eq 'pr' -and $Readiness.ci_state) { [string]$Readiness.ci_state } else { $null }
    merge_state = if ($Kind -eq 'pr' -and $Readiness.mergeStateStatus) { [string]$Readiness.mergeStateStatus } else { $null }
    track = if ($Previous) { $Previous.track } else { $null }
    stage = if ($Previous) { $Previous.stage } else { $null }
    owes = if ($Previous) { $Previous.owes } else { 'us' }
    priority = if ($Previous) { $Previous.priority } else { $null }
  }
}

function New-BoardIndexEntry {
  param(
    $Item, $Track, $Stage, $Owes, [bool]$PendingAuthor, $WaitingSince,
    [bool]$HasArtifact, [string]$AgentStatus, $IssueType, [int]$ProposedOpen,
    $PrimaryAction, $Mirror
  )
  $entry = [ordered]@{
    id=$Item.id; kind=$Item.kind; number=[int]$Item.number; url=$Item.url; title=$Item.title; author=$Item.author
    state=$Item.state
    is_draft=[bool]$Item.is_draft
    is_community=[bool]$Item.is_community; mine=[bool]$Item.mine; is_cmdpal=[bool]$Item.is_cmdpal
    track=$Track; stage=$Stage; owes=$Owes; pending_author=$PendingAuthor
    waiting_since=$WaitingSince
    has_artifact=$HasArtifact; agent_status=$AgentStatus; issue_type=$IssueType
    proposed_open=$ProposedOpen
    primary_action=if ($PrimaryAction) { [pscustomobject]$PrimaryAction } else { $null }
    labels=@($Item.labels); assignees=@($Item.assignees | Where-Object { $_ -is [string] -and -not [string]::IsNullOrWhiteSpace($_) }); created_at=$Item.created_at; updated_at=$Item.updated_at
    comments=$Item.comments; priority=$Item.priority
  }
  if ($Item.kind -eq 'pr') {
    $entry['review_decision'] = $Item.review_decision
    $entry['ci_state'] = $Item.ci_state
    $entry['merge_state'] = $Item.merge_state
  }
  if ($Mirror) { $entry['mirror'] = [pscustomobject]$Mirror }
  return [pscustomobject]$entry
}

function Get-BoardIndexCounts {
  param($Items, [int]$ArtifactCount)
  $cPr=0; $cIss=0; $cCom=0
  foreach ($item in $Items) {
    if ($item.kind -eq 'pr') { $cPr++ } elseif ($item.kind -eq 'issue') { $cIss++ }
    if ($item.is_community) { $cCom++ }
  }
  return [ordered]@{ open_prs=$cPr; open_issues=$cIss; community=$cCom; artifacts=$ArtifactCount }
}

function ConvertTo-BoardIndexFiles {
  param($Index, [int]$Depth = 20)
  $json = $Index | ConvertTo-Json -Depth $Depth
  return [pscustomobject]@{ json=$json; javascript=("window.BOARD_INDEX = " + $json + ";") }
}
