$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$emitter = Join-Path (Split-Path -Parent $PSScriptRoot) 'emit.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
  $emitter, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Emitter contains parse errors.' }
$names = @('Get-PropertyValue', 'Test-MeaningfulAction', 'Test-CurrentOpenBugArtifact')
foreach ($function in $ast.FindAll({
  param($node)
  $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -in $names
}, $true)) {
  Invoke-Expression $function.Extent.Text
}

$json = @'
{
  "schemaVersion": 5,
  "kind": "issue",
  "fix_assessment": {
    "status": "not_applicable",
    "rationale": "The reported limitation is expected behavior."
  },
  "issue_context": {
    "summary": "A documented limitation was reported.",
    "analysis": "The supplied evidence matches the documented behavior.",
    "known_information": ["The report includes the affected version."],
    "inferences": [],
    "initial_investigation": ["Checked the documented behavior."]
  },
  "actions": [],
  "proposed_fixes": []
}
'@
foreach ($asHashtable in @($false, $true)) {
  $artifact = $json | ConvertFrom-Json -AsHashtable:$asHashtable
  if (-not (Test-CurrentOpenBugArtifact $artifact)) {
    throw "Valid empty inferences were rejected (hashtable=$asHashtable)."
  }
  $artifact.issue_context.inferences = @('A qualified inference.')
  if (-not (Test-CurrentOpenBugArtifact $artifact)) {
    throw "Nonempty inferences were rejected (hashtable=$asHashtable)."
  }
  $artifact.issue_context.inferences = $null
  if (Test-CurrentOpenBugArtifact $artifact) {
    throw "Null inferences were accepted (hashtable=$asHashtable)."
  }
  if ($asHashtable) {
    $artifact.issue_context.Remove('inferences')
  } else {
    $artifact.issue_context.PSObject.Properties.Remove('inferences')
  }
  if (Test-CurrentOpenBugArtifact $artifact) {
    throw "Missing inferences were accepted (hashtable=$asHashtable)."
  }
}
'PASS: empty, populated, null and missing issue inferences in both JSON object forms.'
