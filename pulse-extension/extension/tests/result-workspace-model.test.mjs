import test from 'node:test';
import assert from 'node:assert/strict';
import { resultWorkspaceSummary } from '../src/result-workspace-model.ts';

const sha = 'a'.repeat(40);
const candidateSha = 'd'.repeat(40);
function finding(id = 'finding-1', overrides = {}) {
  return { id, title: 'Preserve queued work', priority: 'P1', confirmed: true, status: 'open', path: 'src/Queue.cs', line: 17,
    details: 'The clear-history predicate deletes queued records.', impact: 'Queued work disappears.', trigger: 'Clear history while a task is queued.',
    rootCause: 'Only running tasks are excluded.', fixSuggestion: 'Select terminal records explicitly.', evidence: ['The predicate was rechecked at the saved SHA.'],
    feedback: { body: 'Preserve queued records when clearing history.', suggestionId: null }, ...overrides };
}
function run(overrides = {}) {
  return { runId: 'result-workspace-run', task: { requestId: 'request-1', actionId: 'review-1', actionKind: 'pr-review', repository: 'example/pulse', target: { type: 'pr', number: 42 }, expectedHeadSha: sha, reviewOptions: { mode: 'static' }, prompt: 'Inspect the saved target.' },
    config: { agent: 'codex', cliPath: 'codex.exe', repoFolder: 'C:\\Sample', permission: 'read-only' }, status: { state: 'succeeded', exitCode: 0, createdAt: '2026-09-17T00:00:00Z', updatedAt: '2026-09-17T00:01:00Z', sequence: 2 }, view: { read: false, handled: false },
    result: { schemaVersion: 3, structured: true, outcome: 'completed', phase: 'reporting', cliExitCode: 0, summary: 'The selected source review is complete.',
      report: { complete: true, rechecked: true, coverage: ['All changed paths and their callers were rechecked.'], limitations: [] }, findings: [],
      assessment: { subject: 'original-pr', status: 'inconclusive', summary: 'The runtime behavior has not been verified.', revisionSha: sha },
      reviewConclusion: { status: 'no-blocking-findings', summary: 'The selected code review has no blocking finding.', revisionSha: sha, blockingUncertainties: [] },
      e2eAssessment: { level: 'not_needed', reason: 'Recorded unit coverage covers the changed pure function.', question: '', scenarios: [], expectedResults: [], prerequisites: [], evidence: ['The relevant tests were inspected.'], readiness: 'ready' },
      review: null, artifacts: [], validation: [], verificationEvidence: [], diagnostics: [], nextActions: [], blockers: [], nextSteps: [], plans: [], featureAssessment: null, bugAssessment: null, needsReview: false, ...overrides } };
}
const fact = (summary, label) => summary.facts.find(row => row.label === label)?.value;

test('a completed PR with zero findings and no recorded follow-up has an explicit no-recommendation state', () => {
  const value = run(); const saved = structuredClone(value); const summary = resultWorkspaceSummary(value);
  assert.equal(summary.title, 'No blocking code findings'); assert.equal(summary.next.kind, 'none'); assert.equal(summary.next.title, 'No recommended action'); assert.ok(summary.next.reason);
  assert.equal(fact(summary, 'Assessment'), 'Original PR · inconclusive'); assert.notEqual(summary.tone, 'success');
  assert.deepEqual(value, saved, 'presentation never rewrites the saved result');
});

test('explicit no-action advice remains visible without selecting an unrelated available manual proposal', () => {
  const value = run({ recommendation: { kind: 'none', reason: 'No follow-up work was identified in the saved scope.' }, nextActions: [{ proposalId: 'optional-comment', kind: 'comment', body: 'Optional explanation', reason: 'Available if the reviewer wants to comment.', recommended: false }] });
  const summary = resultWorkspaceSummary(value);
  assert.equal(summary.next.kind, 'none'); assert.equal(summary.next.reason, value.result.recommendation.reason);
  value.result.nextActions[0].recommended = true;
  assert.equal(resultWorkspaceSummary(value).next.kind, 'none', 'explicit saved no-action advice does not select a different proposal');
});

test('confirmed findings are actionable while execution completion remains separate from failed product assessment', () => {
  const value = run({ findings: [finding(), finding('unconfirmed', { confirmed: false, status: 'unverified', priority: 'P0' })],
    assessment: { subject: 'original-pr', status: 'failed', summary: 'The saved revision loses queued tasks.', revisionSha: sha },
    reviewConclusion: { status: 'changes-requested', summary: 'The P1 defect was rechecked.', revisionSha: sha, blockingUncertainties: [] } });
  const summary = resultWorkspaceSummary(value);
  assert.equal(summary.title, '1 confirmed finding needs review'); assert.equal(summary.next.kind, 'address-findings'); assert.equal(summary.tone, 'attention');
  assert.equal(fact(summary, 'Report'), 'Complete · Rechecked'); assert.equal(fact(summary, 'Assessment'), 'Original PR · failed');
});

test('all confirmed priorities can produce feedback without a separate positive review recommendation', () => {
  const summary = resultWorkspaceSummary(run({ findings: [finding('minor', { priority: 'P3' })] }));
  assert.equal(summary.next.kind, 'address-findings'); assert.equal(summary.title, '1 confirmed finding needs review');
});

test('required E2E remains visible with zero findings and an inconclusive original PR', () => {
  const value = run(); Object.assign(value.result.e2eAssessment, { level: 'required', readiness: 'missing-prerequisites', reason: 'The UI behavior requires a desktop observation.', question: 'Does the first dialog restore focus?', scenarios: ['Open and close the first dialog.'], expectedResults: ['The original control regains focus.'], prerequisites: ['An isolated Windows desktop.'] });
  const summary = resultWorkspaceSummary(value);
  assert.equal(summary.next.kind, 'run-e2e'); assert.equal(summary.next.title, 'Review verification requirements');
  assert.equal(fact(summary, 'E2E requirement'), 'Required'); assert.equal(fact(summary, 'E2E evidence'), 'Not yet complete');
  assert.equal(fact(summary, 'Assessment'), 'Original PR · inconclusive'); assert.notEqual(summary.tone, 'success');
});

test('verification-only tasks retain their assessed outcome and never infer approval from code-review defaults', () => {
  for (const actionKind of ['pr-verify', 'e2e']) {
    const value = run({ assessment: { subject: 'original-pr', status: 'failed', summary: 'The recorded runtime scenario failed.', revisionSha: sha }, reviewConclusion: null });
    value.task.actionKind = actionKind; value.task.reviewOptions = { mode: 'ui-e2e' };
    const summary = resultWorkspaceSummary(value);
    assert.equal(summary.title, 'Original PR · Verification failed', actionKind); assert.equal(summary.next.kind, 'none', actionKind); assert.equal(summary.tone, 'attention');
  }
});

test('a verified Issue candidate recommends its saved Draft PR workspace and preserves exact proposal content', () => {
  const proposal = { proposalId: 'create-candidate', kind: 'create-pr', body: '', reason: 'Review the saved candidate and prepare the Draft PR.', recommended: true,
    pullRequest: { head: 'reviewer:fix/queue', base: 'main', sourceHeadSha: candidateSha, title: 'Preserve queued tasks', body: '## Fix\n\nKeep queued work.  \n\nFixes #42\n', draft: true } };
  const value = run({ reviewConclusion: null, e2eAssessment: null, assessment: { subject: 'local-candidate', status: 'passed', summary: 'The saved local candidate passed its focused checks.', revisionSha: candidateSha }, nextActions: [proposal] });
  value.task.target.type = 'issue'; value.task.actionKind = 'issue-fix'; delete value.task.expectedHeadSha; delete value.task.reviewOptions;
  const summary = resultWorkspaceSummary(value);
  assert.equal(summary.next.kind, 'create-pr'); assert.equal(summary.next.title, 'Create Draft PR'); assert.deepEqual(summary.next.proposal.pullRequest, proposal.pullRequest);
  assert.equal(summary.title, 'Local candidate · Verification passed'); assert.equal(fact(summary, 'Assessed revision'), candidateSha);
});

test('failed, cancelled, interrupted, partial and unavailable reports never turn missing findings into a clean conclusion', () => {
  const cases = [
    ['failed', value => { value.status.state = 'failed'; value.status.exitCode = 17; value.result.outcome = 'failed'; value.result.cliExitCode = 17; }],
    ['cancelled', value => { value.status.state = 'cancelled'; value.result.outcome = 'cancelled'; }],
    ['interrupted', value => { value.status.state = 'interrupted'; value.result.outcome = 'interrupted'; }],
    ['partial', value => { value.result.report.complete = false; value.result.report.rechecked = false; }],
    ['unread pages', value => { value.resultPaging = { fingerprint: 'f'.repeat(64), sections: [{ path: 'findings', total: 5, nextOffset: 0 }] }; }],
    ['no result', value => { delete value.result; value.status.state = 'failed'; }],
  ];
  for (const [label, mutate] of cases) {
    const value = run(); mutate(value); const summary = resultWorkspaceSummary(value);
    assert.doesNotMatch(summary.title, /No blocking|0 confirmed|Verification passed/, label); assert.notEqual(summary.tone, 'success', label);
    assert.equal(summary.next.kind, 'none', label); assert.ok(summary.next.reason, label);
  }
  for (const state of [{ loading: true }, { error: 'The next report page could not be loaded.' }]) {
    const summary = resultWorkspaceSummary(run(), state); assert.doesNotMatch(summary.title, /No blocking|0 confirmed/);
    assert.equal(summary.next.kind, state.error ? 'reload-report' : 'none'); assert.ok(summary.next.reason);
  }
});

test('failed analysis cannot recommend a retained positive publishing proposal as a reliable next step', () => {
  const value = run({ outcome: 'failed', nextActions: [{ proposalId: 'stale-approval', kind: 'approve', body: '', reason: 'An intermediate recommendation before failure.', recommended: true }] });
  value.status.state = 'failed'; value.status.exitCode = 1; value.result.cliExitCode = 1;
  const summary = resultWorkspaceSummary(value); assert.notEqual(summary.next.kind, 'approve'); assert.notEqual(summary.tone, 'success');
});

test('unlinked historical output retains its identity without inventing a target or modern review conclusion', () => {
  const value = run(); delete value.task.target; delete value.task.expectedHeadSha;
  value.result = { summary: 'Retained original response.', structured: false, artifacts: [], validation: [], blockers: [], nextSteps: [], rawOutput: 'Original output preserved.' };
  const summary = resultWorkspaceSummary(value);
  assert.equal(fact(summary, 'Target'), 'Unlinked historical record'); assert.match(fact(summary, 'Report'), /Historical report/); assert.equal(summary.next.kind, 'none');
});

test('INVALID_RESULT identifies a final-report format error and keeps recorded execution evidence without inferring missing prerequisites or clean findings', () => {
  for (const version of [2, 3])
  for (const executionState of ['succeeded', 'failed']) {
    const raw = '{"schemaVersion":3,"verificationEvidence":[{"source":"current-run","runId":"trx-run-123"}],"summary":"Build and focused tests passed; report shape needs correction."}';
    const value = run({ schemaVersion: version, outcome: 'blocked', summary: 'The final result did not match the workflow contract.',
      report: { complete: false, rechecked: false, coverage: [], limitations: [] }, findings: [], rawOutput: raw,
      diagnostics: [{ code: 'INVALID_RESULT', severity: 'error', message: 'verificationEvidence[0].runId must be null for current-run evidence.', recovery: 'inspectResult' }],
      validation: [{ id: 'build-tests', name: 'Build and focused tests', status: 'passed', required: true, details: 'The retained execution log records successful build and test commands.', evidence: ['Saved TRX and compiler log.'] }],
      nextActions: [{ proposalId: 'inspect-invalid-report', kind: 'inspectResult', reason: 'Inspect retained output.', body: '', recommended: true }] });
    value.status.state = executionState; value.status.exitCode = executionState === 'succeeded' ? 0 : 1;
    const saved = structuredClone(value); const summary = resultWorkspaceSummary(value);
    assert.equal(summary.title, 'Final report format is invalid'); assert.equal(fact(summary, 'Report'), 'Invalid final report format');
    assert.match(summary.detail, /verificationEvidence\[0\]\.runId/); assert.match(summary.detail, /original response and execution logs remain available/);
    assert.equal(summary.next.kind, 'inspectResult'); assert.equal(summary.next.title, 'Inspect original response and diagnostics'); assert.equal(summary.tone, 'attention');
    assert.doesNotMatch(summary.title + ' ' + summary.detail + ' ' + summary.next.reason, /missing prerequisites|missing requirements|builds? (?:were |was )?not run|tests? (?:were |was )?not run|No blocking|0 confirmed/i);
    assert.deepEqual(value, saved, 'displaying an invalid format never rewrites retained raw output, verification evidence or run metadata');
  }
});

test('an INVALID_RESULT execution error remains a format error before a structured result is available', () => {
  const value = run(); delete value.result;
  value.status.state = 'failed'; value.status.error = { code: 'INVALID_RESULT', message: 'The final response could not be parsed as a result object.' };
  const summary = resultWorkspaceSummary(value);
  assert.equal(summary.title, 'Final report format is invalid'); assert.equal(summary.next.kind, 'inspectResult'); assert.match(summary.detail, /could not be parsed/);
});
