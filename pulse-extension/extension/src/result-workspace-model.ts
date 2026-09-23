import { completedV3Findings, currentRecommendation, isFinalReportComplete, recoveryActions, resultActionEligible, runFailure, textValue, workflowOutcome } from './details-model.js';
import { isActive } from './policy.js';
import type { ResultNextAction, ResultRecommendation, Run } from './types.js';

export interface WorkspaceSummary {
  title: string; detail: string; tone: 'neutral' | 'attention' | 'success';
  facts: { label: string; value: string }[];
  next: { kind: string; title: string; reason: string; proposal?: ResultNextAction };
}
const labels: Record<string, string> = {
  approve: 'Review approval', 'address-findings': 'Review and send feedback', comment: 'Review and send comment',
  requestChanges: 'Review and request changes', suggestChanges: 'Review code suggestions', 'run-e2e': 'Review verification requirements',
  'create-pr': 'Create Draft PR', 'start-task': 'Review and start saved plan', close: 'Review closing this target',
  'close-as-duplicate': 'Review duplicate and close', 'merge-pr': 'Review merge', 'trigger-ci': 'Review CI trigger',
  configure: 'Review missing prerequisites', rerun: 'Review a new run', inspectResult: 'Inspect recorded diagnostics',
  viewChanges: 'Inspect retained changes', openTarget: 'Open target on GitHub', none: 'No recommended action',
};

/** Presentation only. Permissions, remote state and publication checks remain authoritative in the Host. */
export function resultWorkspaceSummary(run: Run, state: { loading?: boolean; error?: string } = {}): WorkspaceSummary {
  const result = run.result;
  const facts: WorkspaceSummary['facts'] = [];
  const output: WorkspaceSummary = { title: 'No result was recorded', detail: 'Execution details and available logs are retained below.', tone: 'neutral', facts,
    next: { kind: 'none', title: labels.none!, reason: 'No supported next step was recorded. You can choose a manual action.' } };
  const target = run.task.target;
  facts.push({ label: 'Target', value: target ? `${target.type === 'pr' ? 'PR' : 'Issue'} #${target.number} · ${run.task.repository}` : 'Unlinked historical record' });
  if (run.task.expectedHeadSha) facts.push({ label: 'Analyzed revision', value: run.task.expectedHeadSha });
  if (isActive(run.status.state)) {
    output.title = run.status.state === 'accepted' ? 'Waiting to start' : 'Work in progress';
    output.detail = textValue(run.status.latestProgress) ?? 'The conclusion and next step will appear when this task has a recorded result.';
    output.next.reason = 'This task is still running. Its result is not ready for review.';
    return output;
  }
  if (state.loading || state.error) {
    output.title = state.error ? 'The complete report could not be loaded' : 'Loading the complete report';
    output.detail = state.error ?? 'Reading all findings and evidence. A partial response cannot establish the result.';
    output.tone = state.error ? 'attention' : 'neutral';
    output.next = { kind: state.error ? 'reload-report' : 'none', title: state.error ? 'Reload complete report' : labels.none!, reason: 'The saved execution and manual actions remain available while the report is unavailable.' };
    return output;
  }
  const complete = result?.schemaVersion === 3 && isFinalReportComplete(run);
  const failure = runFailure(run);
  const invalidDiagnostic = result?.diagnostics?.find(item => item?.code === 'INVALID_RESULT');
  const invalidReport = failure?.code === 'INVALID_RESULT' || Boolean(invalidDiagnostic);
  facts.push({ label: 'Report', value: invalidReport ? 'Invalid final report format' : result?.schemaVersion === 3 ? complete ? 'Complete · Rechecked' : 'Incomplete analysis' : result ? `Historical report · ${result.schemaVersion === 2 ? 'V2' : 'Original output'}` : 'Not recorded' });
  const outcome = workflowOutcome(run);
  if (invalidReport) {
    output.title = 'Final report format is invalid';
    output.detail = `${textValue(invalidDiagnostic?.message) ?? textValue(failure?.message) ?? 'The final response does not match the result contract.'} The original response and execution logs remain available below.`;
    output.tone = 'attention';
  } else if (failure || ['failed', 'cancelled', 'interrupted'].includes(run.status.state) || outcome && outcome !== 'completed') {
    output.title = run.status.state === 'cancelled' ? 'Task cancelled' : run.status.state === 'interrupted' ? 'Task interrupted' : outcome === 'blocked' ? 'Task blocked by missing requirements' : 'Task did not complete';
    output.detail = textValue(failure?.message) ?? textValue(result?.summary) ?? output.detail;
    output.tone = 'attention';
  } else if (result?.schemaVersion === 3 && !complete) {
    output.title = 'Analysis is incomplete'; output.detail = textValue(result.summary) ?? 'The complete, rechecked result was not recorded.'; output.tone = 'attention';
  } else if (result) {
    output.detail = textValue(result.summary) ?? 'Read the recorded evidence and limitations before choosing an action.';
    if (run.task.actionKind === 'pr-review' && result.reviewConclusion) {
      const conclusion = result.reviewConclusion;
      const findings = complete ? completedV3Findings(run).filter(row => row.status === 'open') : [];
      output.title = findings.length ? `${findings.length} confirmed ${findings.length === 1 ? 'finding needs' : 'findings need'} review` : ({ 'no-blocking-findings': 'No blocking code findings', 'changes-requested': 'Changes are needed', inconclusive: 'The code conclusion is inconclusive' }[conclusion.status]);
      output.tone = findings.length || conclusion.status !== 'no-blocking-findings' ? 'attention' : 'neutral';
      facts.push({ label: 'Code conclusion', value: conclusion.status.replaceAll('-', ' ') });
    } else if (result.assessment && ['issue-fix', 'feature-implement', 'issue-verify', 'pr-verify', 'e2e'].includes(run.task.actionKind)) {
      const assessment = result.assessment;
      const subject = { 'local-candidate': 'Local candidate', 'original-pr': 'Original PR', target: 'Task target' }[assessment.subject];
      output.title = `${subject} · ${assessment.status === 'passed' ? 'Verification passed' : assessment.status === 'failed' ? 'Verification failed' : 'Verification is inconclusive'}`;
      output.detail = assessment.summary || output.detail;
      output.tone = assessment.status === 'passed' ? 'neutral' : 'attention';
    } else if (result.featureAssessment) {
      output.title = ({ ready: 'The feature plan is ready for review', needs_information: 'More information is needed', needs_decision: 'A product decision is needed', already_supported: 'The requested capability is already supported', duplicate: 'A related feature request was identified', not_feasible: 'The request has feasibility constraints' })[result.featureAssessment.status];
      output.detail = result.featureAssessment.summary || output.detail;
    } else if (result.bugAssessment) {
      output.title = ({ confirmed: 'The defect is confirmed', needs_information: 'More information is needed', needs_verification: 'Additional verification is needed', already_fixed: 'The defect is fixed in the assessed revision', duplicate: 'A related issue was identified', not_a_bug: 'The investigation found expected behavior' })[result.bugAssessment.status];
      output.detail = result.bugAssessment.summary || output.detail;
    } else output.title = textValue(result.summary) ? 'Recorded result' : 'No conclusion was recorded';
  }
  if (result?.assessment) {
    const item = result.assessment;
    facts.push({ label: 'Assessment', value: `${{ 'original-pr': 'Original PR', 'local-candidate': 'Local candidate', target: 'Task target' }[item.subject]} · ${item.status}` });
    if (item.revisionSha !== run.task.expectedHeadSha || !item.revisionSha) facts.push({ label: 'Assessed revision', value: item.revisionSha ?? 'Not recorded' });
  }
  if (result?.bugAssessment) facts.push({ label: 'Actual reproduction', value: ({ reproduced: 'Reproduced', not_reproduced: 'Not reproduced in this environment', not_run: 'Not run', blocked: 'Blocked by prerequisites' })[result.bugAssessment.reproduction.status] });
  if (target?.type === 'pr') {
    const e2e = result?.e2eAssessment;
    facts.push({ label: 'E2E requirement', value: e2e ? ({ required: 'Required', recommended: 'Recommended', not_needed: 'Not needed' })[e2e.level] : 'Not recorded' });
    if (e2e && e2e.level !== 'not_needed') facts.push({ label: 'E2E evidence', value: result?.e2eEvidenceComplete ? 'Supplied for this revision' : 'Not yet complete' });
  }
  const actions = recoveryActions(run);
  const derived = currentRecommendation(run);
  let advice: ResultRecommendation | undefined;
  if (result?.recommendation?.kind === 'none') advice = result.recommendation;
  else if (complete && run.task.actionKind === 'pr-review' && completedV3Findings(run).some(row => row.status === 'open'))
    advice = { kind: 'address-findings', reason: 'Review the confirmed findings and prepare feedback for the author.' };
  else if (result?.recommendation || actions.some(action => action.recommended) ||
    run.task.actionKind === 'pr-review' && ['address-findings', 'run-e2e'].includes(derived?.kind ?? '')) advice = derived;
  // A publishable candidate leads straight to the Draft preparation workspace.
  const draft = target?.type === 'issue' && result?.assessment?.subject === 'local-candidate' && result.assessment.status === 'passed'
    ? actions.find(action => action.kind === 'create-pr' && resultActionEligible(run, action)) : undefined;
  let proposal = draft ?? (advice?.kind === 'none' ? undefined : actions.find(action => advice?.proposalId && action.proposalId === advice.proposalId) ?? actions.find(action => action.recommended));
  let kind = draft ? 'create-pr' : advice?.kind === 'incomplete' ? proposal?.kind ?? 'none' : advice?.kind ?? proposal?.kind ?? 'none';
  if (kind === 'approve' && complete && completedV3Findings(run).some(row => row.status === 'open')) kind = 'address-findings';
  if (result?.schemaVersion === 3 && !complete && ['approve', 'comment', 'requestChanges', 'suggestChanges', 'close', 'create-pr', 'merge-pr', 'close-as-duplicate', 'address-findings'].includes(kind)) { kind = 'none'; proposal = undefined; }
  output.next = { kind, title: labels[kind] ?? 'Review the recommended next step', reason: proposal?.reason || advice?.reason || output.next.reason, ...(proposal ? { proposal } : {}) };
  if (!complete && result?.schemaVersion === 3 && !proposal) output.next.reason = 'A complete recommendation is unavailable. Review the recorded limitations or choose a manual action.';
  if (invalidReport) output.next = { kind: 'inspectResult', title: 'Inspect original response and diagnostics', reason: 'Review the reported format error and retained execution evidence before deciding whether to start a new run.' };
  return output;
}
