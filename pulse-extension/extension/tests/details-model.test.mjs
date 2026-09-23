import test from 'node:test';
import assert from 'node:assert/strict';
import { eligibleOperationKinds, executionSelection, limitedReviewSummary, observedExecutionValue, outcomeLabel, phaseLabel, recoveryActions, resultActionAvailability, resultActionEligible, resultNeedsAttention, resultOperationKinds, resultPresentation, runFailure, textValue, workflowOutcome } from '../src/details-model.ts';

const run = (state = 'failed', result) => ({
  runId: 'details-task',
  task: { actionKind: 'issue-fix', actionId: 'Investigate task failure', repository: 'microsoft/PowerToys', prompt: 'Inspect the failed task.' },
  config: { agent: 'codex', repoFolder: 'C:\\Tasks\\details-task', cliPath: 'codex.exe', permission: 'read-only' },
  status: { state, createdAt: '2026-09-10T00:00:00Z', updatedAt: '2026-09-10T00:00:22Z', sequence: 0 },
  view: { read: false, handled: false },
  ...(result ? { result } : {}),
});
const result = overrides => ({ summary: '', artifacts: [], validation: [], blockers: [], nextSteps: [], ...overrides });
const fallbackStep = { kind: 'inspectResult', reason: 'Check existing artifacts and incomplete validation before deciding whether to start a new run.', body: '' };

test('execution facts distinguish missing historical selections from explicit CLI defaults', () => {
  for (const value of [undefined, null]) {
    assert.equal(textValue(value), undefined);
    assert.equal(executionSelection(value), 'Not recorded');
  }
  for (const value of ['', '  \n ']) {
    assert.equal(textValue(value), undefined);
    assert.equal(executionSelection(value), 'CLI default');
  }
  assert.equal(executionSelection(' gpt-5.3-codex '), 'gpt-5.3-codex');
  assert.equal(executionSelection(' high '), 'high');
  assert.equal(textValue(' CLI_EXECUTION_FAILED '), 'CLI_EXECUTION_FAILED');
});

test('failure diagnostics omit null and blank fields without hiding an exit code of zero', () => {
  const task = run();
  task.status.error = { message: '  ', code: null, guidance: null };
  task.status.exitCode = null;
  const missing = runFailure(task);
  assert.ok(missing?.message);
  assert.doesNotMatch(missing.message, /\b(?:null|undefined)\b/);
  assert.equal(missing.code, undefined); assert.equal(missing.guidance, undefined); assert.equal(missing.exitCode, undefined);
  task.status.error = { message: ' CLI stopped. ', code: ' CLI_EXECUTION_FAILED ', guidance: ' Inspect stderr. ' };
  task.status.exitCode = 0;
  assert.deepEqual(runFailure(task), { message: 'CLI stopped.', code: 'CLI_EXECUTION_FAILED', guidance: 'Inspect stderr.', exitCode: 0 });
  for (const value of [null, undefined, NaN, Infinity, 1.5, '1']) {
    task.status.exitCode = value;
    assert.equal(runFailure(task).exitCode, undefined);
  }
  assert.equal(runFailure(run('running')), undefined);
  assert.equal(runFailure(run('succeeded')), undefined);
  assert.ok(runFailure(run('interrupted'))?.message);
});

test('legacy Host failures retain response and recovery without repeating the primary error', () => {
  const message = 'The handle is invalid.';
  const guidance = 'Inspect the captured stderr.';
  const task = run('failed', result({
    summary: ` ${message} `,
    rawOutput: 'Agent response before the process stopped.\nExisting analysis is preserved.',
    structured: false, needsReview: true,
    blockers: ['CLI_EXECUTION_FAILED', message, guidance],
    nextSteps: [fallbackStep, { kind: 'inspectResult', reason: message, body: '' }],
  }));
  task.status.error = { code: 'CLI_EXECUTION_FAILED', message, guidance };
  const presentation = resultPresentation(task);
  assert.equal(presentation.mode, 'diagnostic');
  assert.equal(presentation.summary, undefined);
  assert.deepEqual(presentation.blockers, []); assert.deepEqual(presentation.nextSteps, [fallbackStep, { kind: 'inspectResult', reason: '', body: '' }]);
  assert.deepEqual(presentation.artifacts, []); assert.deepEqual(presentation.validation, []);
  assert.equal(presentation.showReviewNotice, false);
  assert.equal(presentation.rawOutput, task.result.rawOutput);
  assert.equal(presentation.hasContent, true);
});

test('failed legacy records expose retained evidence as diagnostics without publication', () => {
  const findings = result({
    summary: 'Shortcut analysis completed before desktop validation stopped.',
    artifacts: [{ label: 'Failure capture', url: 'https://github.com/microsoft/PowerToys/issues/123#issuecomment-456', path: 'C:\\Tasks\\details-task\\capture.txt' }],
    validation: [{ name: 'Shortcut configuration', status: 'passed' }, { name: 'Desktop automation', status: 'not_run', details: 'Test driver unavailable.' }],
    blockers: ['Install the desktop test driver.'],
    nextSteps: [{ kind: 'configure', reason: 'Install the desktop test driver before retrying validation.', body: 'Keep the captured analysis for the next run.' }],
    needsReview: true, structured: true,
  });
  const task = run('failed', findings);
  task.status.error = { code: 'TEST_DRIVER_MISSING', message: 'Desktop validation could not start.' };
  const presentation = resultPresentation(task);
  assert.equal(presentation.mode, 'diagnostic'); assert.equal(presentation.summary, findings.summary);
  for (const field of ['artifacts', 'validation', 'blockers', 'nextSteps']) assert.deepEqual(presentation[field], findings[field]);
  assert.equal(presentation.showReviewNotice, false); assert.equal(presentation.hasContent, true);
  assert.deepEqual(resultOperationKinds(task), []);
  assert.equal(task.result, findings, 'presentation never rewrites historical records');
});

test('incomplete unstructured output keeps its response without generic warnings or empty result sections', () => {
  const task = run('succeeded', result({
    summary: 'The CLI exited. Inspect the result to confirm completion.',
    rawOutput: 'I inspected the reproduction path; the remaining condition needs a desktop session.',
    needsReview: true, structured: false, nextSteps: [fallbackStep],
  }));
  const presentation = resultPresentation(task);
  assert.equal(presentation.mode, 'diagnostic');
  assert.equal(presentation.showReviewNotice, false);
  assert.equal(presentation.rawOutput, task.result.rawOutput);
  for (const field of ['artifacts', 'validation', 'blockers']) assert.deepEqual(presentation[field], []);
  assert.deepEqual(presentation.nextSteps, [fallbackStep]);
  assert.equal(presentation.hasContent, true);
  const empty = resultPresentation(run('failed'));
  assert.equal(empty.hasContent, false); assert.equal(empty.showReviewNotice, false);
  assert.equal(empty.rawOutput, undefined);
  for (const field of ['artifacts', 'validation', 'blockers', 'nextSteps']) assert.deepEqual(empty[field], []);
});

test('successful structured results retain genuine review guidance and useful content', () => {
  const task = run('succeeded', result({ summary: 'The patch is ready for inspection.', structured: true, needsReview: true, blockers: ['Keyboard validation still needs review.'] }));
  const presentation = resultPresentation(task);
  assert.equal(presentation.summary, task.result.summary);
  assert.equal(presentation.showReviewNotice, true); assert.equal(presentation.hasContent, true);
  assert.deepEqual(presentation.blockers, task.result.blockers);
  task.result.needsReview = false;
  assert.equal(resultPresentation(task).showReviewNotice, false);
  assert.equal(resultPresentation(run('succeeded', result({ structured: true, needsReview: true }))).hasContent, false, 'a generic needs-review flag is not a meaningful result');
});

const sha = 'a'.repeat(40);
const prRun = overrides => {
  const task = run('succeeded', result({ summary: 'Verified review', structured: true, needsReview: true,
    review: { headSha: sha, body: 'Verified findings', suggestions: [{ path: 'src/a.cs', line: 4, side: 'RIGHT', body: 'Guard null', replacement: 'guard();' }] },
    nextSteps: [{ kind: 'requestChanges', reason: 'Guard required', body: 'Please add the guard.' }, { kind: 'suggestChanges', reason: 'Suggested guard', body: 'Please add the guard.' }, { kind: 'comment', reason: 'Discuss guard', body: 'Please consider the guard.' }],
    ...overrides,
  }));
  task.task.target = { type: 'pr', number: 42 }; task.task.expectedHeadSha = sha;
  return task;
};
const preview = overrides => ({ account: 'reviewer', target: { type: 'pr', number: 42, repository: 'microsoft/PowerToys', state: 'OPEN', url: 'https://github.com/microsoft/PowerToys/pull/42' },
  headSha: sha, expectedHeadSha: sha, stale: false, canApprove: true, canRequestChanges: true, canSuggestChanges: true, canComment: true, canClose: true,
  files: [{ path: 'src/a.cs', lines: [{ line: 4, kind: 'add', original: 'unsafe();', hunk: 1 }] }], ...overrides });

test('actual execution metadata never substitutes requested selections', () => {
  const task = run('running'); task.config.model = 'requested-model'; task.config.reasoningEffort = 'high';
  assert.equal(observedExecutionValue(task, 'model'), 'Reading from CLI…');
  assert.equal(observedExecutionValue(task, 'reasoningEffort'), 'Reading from CLI…');
  task.status.observedExecution = { model: 'actual-model', source: 'codex-turn-context', observedAt: '2026-09-11T01:00:00Z' };
  assert.equal(observedExecutionValue(task, 'model'), 'actual-model');
  assert.equal(observedExecutionValue(task, 'reasoningEffort'), 'Reading from CLI…');
  task.status.state = 'succeeded';
  assert.equal(observedExecutionValue(task, 'reasoningEffort'), 'Not recorded');
  task.status.observedExecution.reasoningEffort = 'ultra';
  assert.equal(observedExecutionValue(task, 'reasoningEffort'), 'ultra');
});

test('active results stay hidden and terminal failure evidence offers no publishing actions', () => {
  for (const state of ['accepted', 'running', 'failed', 'cancelled', 'interrupted']) {
    const task = prRun(); task.status.state = state;
    const active = state === 'accepted' || state === 'running';
    assert.equal(resultPresentation(task).hasContent, !active, state);
    assert.equal(resultPresentation(task).mode, active ? 'active' : 'diagnostic', state);
    assert.deepEqual(recoveryActions(task), [], state);
    assert.deepEqual(resultOperationKinds(task), [], state);
    assert.deepEqual(eligibleOperationKinds(task, preview()), [], state);
  }
});

test('blocked, failed validation, unstructured and unsupported legacy outcomes expose no PR actions', () => {
  for (const overrides of [
    { blockers: ['CLI could not read repository'], review: null, nextSteps: [{ kind: 'configure' }, { kind: 'rerun' }] },
    { validation: [{ name: 'Build', status: 'failed' }] },
    { structured: false },
    { review: null, nextSteps: [{ kind: 'approve' }] },
    { nextSteps: [{ kind: 'inspectResult' }] },
  ]) {
    const task = prRun(overrides);
    assert.deepEqual(resultOperationKinds(task), []);
    assert.deepEqual(eligibleOperationKinds(task, preview()), []);
  }
  assert.equal(resultNeedsAttention(prRun({ blockers: ['Review blocked'] })), true);
  assert.equal(resultNeedsAttention(prRun()), true, 'useful findings still need attention without blocking the eligible review actions');
  assert.equal(resultNeedsAttention({ ...prRun(), result: { summary: 'Review blocked', needsReview: true } }), true, 'task-list summaries retain only summary and needsReview');
});

test('valid review suggestions are intersected with fresh target permissions without blanket actions', () => {
  const task = prRun();
  assert.deepEqual(resultOperationKinds(task), ['comment', 'suggestChanges', 'requestChanges']);
  assert.deepEqual(eligibleOperationKinds(task, preview({ canComment: false, canSuggestChanges: false })), ['requestChanges']);
  assert.deepEqual(eligibleOperationKinds(task, undefined), []);
  for (const changed of [preview({ stale: true }), preview({ headSha: 'b'.repeat(40) }), preview({ account: null }), preview({ target: { type: 'pr', number: 43, repository: 'microsoft/PowerToys' } })]) assert.deepEqual(eligibleOperationKinds(task, changed), []);
  assert.deepEqual(eligibleOperationKinds(task, preview({ files: [] })), ['comment', 'requestChanges']);
  assert.ok(!eligibleOperationKinds(task, preview()).includes('approve'));
  assert.ok(!eligibleOperationKinds(task, preview()).includes('close'));
});

test('successful E2E findings can propose a SHA-pinned comment without a review draft', () => {
  const task = prRun({ review: null, nextSteps: [{ kind: 'comment', reason: 'Report verified checks', body: 'All recorded checks passed.' }], validation: [{ name: 'Desktop scenario', status: 'passed' }] });
  task.task.actionKind = 'e2e';
  assert.deepEqual(eligibleOperationKinds(task, preview()), ['comment']);
  assert.deepEqual(eligibleOperationKinds(task, preview({ headSha: 'b'.repeat(40) })), []);
});

const workflowChecks = {
  'pr-review': ['context', 'local-review', 'verification'],
  'issue-fix': ['reproduction', 'implementation', 'verification'],
  e2e: ['setup', 'e2e'],
  'reproduction-setup': ['reproduction', 'instructions'],
};
const checksFor = actionKind => workflowChecks[actionKind].map(id => ({ id, name: id, status: 'passed', required: true, details: `Completed ${id}.`, evidence: [`Evidence for ${id}.`] }));
const nextAction = (kind, reason = 'Use the verified findings.', body = 'Verified local review.') => ({ kind, reason, body });
const diagnostic = (overrides = {}) => ({ code: 'VALIDATION_BLOCKED', severity: 'error', message: 'Desktop verification could not start.', recovery: 'configure', ...overrides });
const v2Run = (overrides = {}, actionKind = 'pr-review') => {
  const task = prRun({ schemaVersion: 2, outcome: 'completed', phase: 'reporting', diagnostics: [], findings: [], validation: checksFor(actionKind), nextActions: [nextAction('comment')], ...overrides });
  task.task.actionKind = actionKind;
  task.status.exitCode = 0;
  if (actionKind === 'issue-fix' || actionKind === 'reproduction-setup') { task.task.target = { type: 'issue', number: 42 }; delete task.task.expectedHeadSha; task.result.review = null; }
  return task;
};

test('workflow outcome uses versioned metadata and Host terminal lifecycle overrides every model outcome', () => {
  for (const state of ['accepted', 'running']) {
    const task = v2Run(); task.status.state = state;
    assert.equal(workflowOutcome(task), undefined); assert.equal(outcomeLabel(task), state === 'accepted' ? 'Queued' : 'Running');
  }
  for (const state of ['failed', 'cancelled', 'interrupted']) for (const outcome of ['completed', 'blocked', 'failed', 'cancelled', 'interrupted']) {
    const task = v2Run({ outcome }); task.status.state = state;
    assert.equal(workflowOutcome(task), state, `${state}/${outcome}`);
    assert.equal(resultPresentation(task).mode, 'diagnostic'); assert.deepEqual(resultOperationKinds(task), []);
  }
  for (const outcome of ['completed', 'blocked', 'failed', 'cancelled', 'interrupted']) {
    assert.equal(workflowOutcome(v2Run({ outcome })), outcome);
    assert.equal(workflowOutcome(prRun({ schemaVersion: 1, outcome })), outcome);
  }
  assert.equal(workflowOutcome(run('succeeded')), 'completed');
  assert.equal(workflowOutcome(prRun({ outcome: 'blocked' })), 'completed', 'unversioned history keeps its original interpretation');
  assert.equal(workflowOutcome(v2Run({ outcome: undefined })), 'blocked');
  assert.equal(workflowOutcome(v2Run({ outcome: 'toString' })), 'blocked');
  assert.equal(outcomeLabel(v2Run({ outcome: 'blocked' })), 'Blocked');
  for (const phase of ['setup', 'analysis', 'implementation', 'validation', 'reporting']) assert.equal(phaseLabel(phase), phase[0].toUpperCase() + phase.slice(1));
  assert.equal(phaseLabel(undefined), 'Not recorded'); assert.equal(phaseLabel('toString'), 'Not recorded');
});

test('completed v2 reviews retain open findings and evidence while allowing verified publication', () => {
  const findings = [{ id: 'null-guard', title: 'Guard null', severity: 'medium', status: 'open', path: 'src/a.cs', line: 4, details: 'Null reaches this call.', evidence: ['src/a.cs:4 receives a nullable value.'] }];
  const task = v2Run({ findings, nextActions: [nextAction('comment'), nextAction('requestChanges'), nextAction('suggestChanges')], needsReview: true });
  const presentation = resultPresentation(task);
  assert.equal(workflowOutcome(task), 'completed'); assert.equal(presentation.mode, 'completed'); assert.equal(presentation.showReviewNotice, true);
  assert.deepEqual(presentation.findings, findings); assert.deepEqual(presentation.validation, task.result.validation);
  assert.equal(resultNeedsAttention(task), true);
  assert.deepEqual(resultOperationKinds(task), ['comment', 'suggestChanges', 'requestChanges']);
  assert.deepEqual(eligibleOperationKinds(task, preview({ canSuggestChanges: false })), ['comment', 'requestChanges']);
});

test('each v2 action requires every workflow check explicitly marked required and passed', () => {
  for (const actionKind of Object.keys(workflowChecks)) {
    const task = v2Run({}, actionKind);
    assert.deepEqual(resultOperationKinds(task), ['comment'], actionKind);
    for (const id of workflowChecks[actionKind]) for (const change of ['missing', 'optional', 'failed', 'not_run']) {
      const changed = structuredClone(task);
      if (change === 'missing') changed.result.validation = changed.result.validation.filter(item => item.id !== id);
      else Object.assign(changed.result.validation.find(item => item.id === id), change === 'optional' ? { required: false } : { status: change });
      assert.deepEqual(resultOperationKinds(changed), [], `${actionKind}/${id}/${change}`);
      assert.deepEqual(recoveryActions(changed).filter(action => resultActionEligible(changed, action)), [], 'unavailable proposals stay visible but cannot execute');
    }
  }
});

test('optional v2 checks do not block unrelated operations but extra required or duplicate checks do', () => {
  for (const status of ['not_run', 'failed']) {
    const task = v2Run({ nextActions: [nextAction('approve'), nextAction('close')], validation: [...checksFor('pr-review'), { id: 'optional-desktop', name: 'Optional desktop scenario', required: false, status, details: 'Outside the requested review.', evidence: [] }] });
    assert.deepEqual(resultOperationKinds(task), ['approve', 'close']);
    task.result.validation.at(-1).required = true;
    assert.deepEqual(resultOperationKinds(task), []);
  }
  for (const validation of [undefined, [], [...checksFor('pr-review'), checksFor('pr-review')[0]], checksFor('pr-review').map(item => ({ ...item, required: undefined }))]) assert.deepEqual(resultOperationKinds(v2Run({ validation })), []);
  for (const structured of [undefined, false]) assert.deepEqual(resultOperationKinds(v2Run({ structured })), []);
  for (const diagnostics of [undefined, [diagnostic()], [{ severity: 'unexpected', message: 'Malformed diagnostic' }]]) assert.deepEqual(resultOperationKinds(v2Run({ diagnostics })), []);
  for (const code of [1, -1, 130]) {
    const task = v2Run(); task.status.exitCode = code;
    assert.deepEqual(resultOperationKinds(task), []);
    delete task.status.exitCode; task.result.cliExitCode = code;
    assert.deepEqual(resultOperationKinds(task), []);
  }
});

test('canonical v2 actions and diagnostics take precedence over legacy compatibility projections', () => {
  const task = v2Run({ blockers: ['Outdated projected blocker'], nextSteps: [nextAction('approve')], rawOutput: 'Preserved original response.', nextActions: [nextAction('comment')], diagnostics: [diagnostic({ severity: 'warning', recovery: 'none' })] });
  assert.deepEqual(resultOperationKinds(task), ['comment']);
  assert.deepEqual(recoveryActions(task), [nextAction('comment')]);
  assert.deepEqual(resultPresentation(task).blockers, []);
  task.result.nextActions = undefined;
  assert.deepEqual(resultOperationKinds(task), [], 'missing canonical actions cannot fall back to stale nextSteps');
  assert.deepEqual(recoveryActions(task), []);
});

test('v2 PR comments require pinned task SHA and reject a mismatched supplied review', () => {
  const task = v2Run({ review: null }, 'e2e');
  assert.deepEqual(eligibleOperationKinds(task, preview()), ['comment']);
  assert.deepEqual(eligibleOperationKinds(task, preview({ headSha: 'b'.repeat(40) })), []);
  assert.deepEqual(eligibleOperationKinds(task, preview({ canComment: false })), []);
  task.result.review = { headSha: 'b'.repeat(40), body: 'Old findings', suggestions: [] };
  assert.deepEqual(resultOperationKinds(task), []);
  task.result.review = null; task.task.expectedHeadSha = undefined;
  assert.deepEqual(resultOperationKinds(task), []);
});

test('blocked v2 presentation retains diagnostics, check evidence, artifacts and raw output without publication', () => {
  const primary = diagnostic();
  const task = v2Run({ outcome: 'blocked', phase: 'validation', summary: primary.message, diagnostics: [primary, { ...primary, message: '  Desktop verification could not start.  ' }], blockers: [primary.message, primary.code],
    artifacts: [{ label: 'Failure capture', path: 'C:\\Tasks\\capture.txt' }], rawOutput: 'Retained CLI response.', nextActions: [nextAction('approve'), nextAction('configure', 'Install the desktop test driver.', '')],
    validation: checksFor('pr-review').map(item => item.id === 'verification' ? { ...item, status: 'not_run', evidence: ['The driver was unavailable.'] } : item) });
  const presentation = resultPresentation(task);
  assert.equal(presentation.mode, 'diagnostic'); assert.equal(presentation.hasContent, true); assert.equal(presentation.summary, undefined);
  assert.deepEqual(presentation.diagnostics, [primary]); assert.deepEqual(presentation.blockers, []);
  assert.deepEqual(presentation.artifacts, task.result.artifacts); assert.deepEqual(presentation.validation, task.result.validation); assert.equal(presentation.rawOutput, task.result.rawOutput);
  assert.equal(presentation.showReviewNotice, false); assert.deepEqual(resultOperationKinds(task), []);
  assert.deepEqual(presentation.nextSteps.filter(action => resultActionEligible(task, action)), [nextAction('configure', 'Install the desktop test driver.', '')]);
  assert.ok(presentation.nextSteps.some(action => action.kind === 'approve' && !resultActionEligible(task, action)));
});

test('v2 diagnostics preserve the actual Host failure once even if the model claimed success', () => {
  const message = 'The process exited with code 9.';
  const task = v2Run({ summary: message, diagnostics: [diagnostic({ code: 'CLI_EXECUTION_FAILED', message, recovery: 'inspectResult' })], blockers: [message, 'CLI_EXECUTION_FAILED'] });
  task.status.state = 'failed'; task.status.exitCode = 9; task.status.error = { code: 'CLI_EXECUTION_FAILED', message };
  const presentation = resultPresentation(task);
  assert.equal(presentation.mode, 'diagnostic'); assert.equal(presentation.summary, undefined); assert.equal(presentation.diagnostics.length, 1);
  assert.equal(presentation.diagnostics[0].code, 'CLI_EXECUTION_FAILED'); assert.equal(presentation.diagnostics[0].message, message);
  assert.deepEqual(presentation.blockers, []); assert.deepEqual(presentation.nextSteps.filter(action => resultActionEligible(task, action)).map(item => item.kind), ['inspectResult']);
  assert.equal(presentation.nextSteps.find(item => item.kind === 'inspectResult').reason, '', 'the recovery button does not repeat the primary diagnosis');
  task.status.error.guidance = 'Inspect the retained stderr before retrying.';
  assert.ok(resultPresentation(task).diagnostics.some(item => item.message === task.status.error.guidance));
  delete task.status.error.guidance;
  task.result.diagnostics = [diagnostic({ code: 'OPTIONAL_CHECK', severity: 'warning', message: 'Optional desktop check skipped.', recovery: 'none' })];
  const changed = resultPresentation(task);
  assert.equal(changed.diagnostics.length, 2); assert.ok(changed.diagnostics.some(item => item.message === message && item.severity === 'error'));
  task.status.state = 'cancelled'; delete task.status.error; task.result.diagnostics = [];
  assert.ok(resultPresentation(task).diagnostics.some(item => item.code === 'RUN_CANCELLED'));
});

test('a redundant workflow check summary is folded only in presentation and retains its recovery and original evidence', () => {
  const specific = diagnostic({ code: 'CJK_RUNTIME_VALIDATION_INCOMPLETE', message: 'Required startup acceptance could not run in this environment.', recovery: 'configure' });
  const generic = diagnostic({ code: 'WORKFLOW_CHECKS_INCOMPLETE', message: 'One or more required workflow checks were not completed.', recovery: 'inspectResult' });
  for (const diagnostics of [[specific, generic], [generic, specific]]) {
    const task = v2Run({ outcome: 'blocked', diagnostics, nextActions: [], validation: checksFor('pr-review').map(item => item.id === 'verification' ? { ...item, status: 'not_run', evidence: ['Required acceptance environment unavailable.'] } : item) });
    const original = structuredClone(task);
    const presentation = resultPresentation(task);
    assert.equal(presentation.mode, 'diagnostic'); assert.equal(workflowOutcome(task), 'blocked');
    assert.deepEqual(presentation.diagnostics, diagnostics, 'grouping does not change the diagnostic collection');
    assert.deepEqual(presentation.diagnosticGroups, [{ primary: specific, related: [generic] }]);
    assert.deepEqual(presentation.validation, original.result.validation);
    assert.deepEqual(new Set(recoveryActions(task).map(item => item.kind)), new Set(['configure', 'inspectResult']));
    assert.deepEqual(task, original, 'historical result and outcome are never rewritten');
  }
});

test('workflow summaries stay visible without a specific error or when a separate execution failure exists', () => {
  const generic = diagnostic({ code: 'WORKFLOW_CHECKS_INCOMPLETE', message: 'Required checks remain incomplete.', recovery: 'inspectResult' });
  const specific = diagnostic({ code: 'ACCEPTANCE_UNAVAILABLE' });
  const warning = diagnostic({ code: 'REVIEW_COVERAGE_LIMITED', severity: 'warning' });
  for (const diagnostics of [[generic], [warning, generic], [generic, diagnostic({ code: 'WORKFLOW_BLOCKED' })], [specific, generic, diagnostic({ code: 'OUTPUT_INCOMPLETE', message: 'Some execution output was lost.' })], [generic, diagnostic({ code: 'CLI_EXECUTION_FAILED', message: 'The process failed before reporting.' })]]) {
    const task = v2Run({ outcome: 'blocked', diagnostics });
    assert.deepEqual(resultPresentation(task).diagnosticGroups, diagnostics.map(primary => ({ primary, related: [] })));
  }
  for (const state of ['failed', 'cancelled', 'interrupted']) {
    const task = v2Run({ outcome: 'blocked', diagnostics: [specific, generic] }); task.status.state = state; task.status.exitCode = 9;
    task.status.error = { code: 'PROCESS_TERMINATED', message: 'The Host recorded a separate process termination.' };
    const presentation = resultPresentation(task);
    assert.equal(presentation.diagnosticGroups.length, 3);
    assert.ok(presentation.diagnosticGroups.every(group => !group.related.length));
    assert.ok(presentation.diagnosticGroups.some(group => group.primary.code === 'PROCESS_TERMINATED'));
  }
});

test('completed review with limited coverage remains completed and keeps Host-disabled approval and merge proposals', () => {
  const actions = [
    { proposalId: 'report-limits', ...nextAction('comment'), availability: { enabled: true, reasons: [] } },
    ...['approve', 'merge-pr'].map(kind => ({ proposalId: kind, ...nextAction(kind), availability: { enabled: false, reasons: ['The original PR assessment remains inconclusive.'] } })),
  ];
  const task = v2Run({ assessment: { subject: 'original-pr', status: 'inconclusive', summary: 'Runtime acceptance remains unverified.', revisionSha: sha }, needsReview: true, nextActions: actions,
    diagnostics: [diagnostic({ code: 'REVIEW_COVERAGE_LIMITED', severity: 'warning', message: 'Runtime acceptance was outside the completed code review.', recovery: 'none' })],
    validation: [...checksFor('pr-review'), { id: 'cjk-acceptance', name: 'CJK startup acceptance', required: false, status: 'not_run', details: 'Not performed.', evidence: [] }],
  });
  const presentation = resultPresentation(task);
  assert.equal(presentation.mode, 'completed'); assert.equal(outcomeLabel(task), 'Completed · Needs attention');
  assert.equal(presentation.diagnosticGroups[0].primary.severity, 'warning');
  assert.equal(resultActionEligible(task, actions[0]), true);
  for (const action of actions.slice(1)) { assert.equal(resultActionEligible(task, action), false); assert.match(resultActionAvailability(task, action).reasons.join(' '), /inconclusive/); }
  assert.deepEqual(recoveryActions(task), actions, 'unavailable proposals remain visible with their reasons');
});

test('a valid historical Host review summary remains readable without overriding the saved workflow outcome', () => {
  const task = v2Run({ outcome: 'blocked', summary: 'Saved blocked workflow.', assessment: { status: 'inconclusive', subject: 'original-pr', revisionSha: sha }, diagnostics: [diagnostic()] });
  task.reviewSummary = { codeReview: 'completed', verification: 'limited', headSha: sha, assessmentStatus: 'inconclusive', limitationCount: 1, canRequestEvidence: true, canApproveWithLimitations: true };
  const original = structuredClone(task);
  assert.ok(limitedReviewSummary(task), 'the task-list summary does not need the full limitation array');
  assert.equal(outcomeLabel(task), 'Blocked');
  assert.equal(resultPresentation(task).mode, 'diagnostic'); assert.equal(workflowOutcome(task), 'blocked'); assert.deepEqual(task, original);
  for (const patch of [
    changed => { changed.status.state = 'failed'; }, changed => { changed.status.exitCode = 1; }, changed => { changed.status.error = { code: 'CLI_FAILED', message: 'A real CLI error.' }; },
    changed => { changed.result.structured = false; }, changed => { changed.result.schemaVersion = 1; }, changed => { changed.task.actionKind = 'e2e'; },
    changed => { changed.reviewSummary.headSha = 'b'.repeat(40); }, changed => { changed.reviewSummary.codeReview = 'incomplete'; },
    changed => { changed.reviewSummary.assessmentStatus = 'failed'; }, changed => { changed.result.assessment.status = 'failed'; },
    changed => { changed.reviewSummary.assessmentStatus = changed.result.assessment.status = 'failed'; },
    changed => { changed.result.assessment.revisionSha = 'b'.repeat(40); }, changed => { changed.result.assessment.subject = 'local-candidate'; },
    changed => { delete changed.result.assessment; },
  ]) {
    const changed = structuredClone(task); patch(changed); assert.equal(limitedReviewSummary(changed), undefined);
    assert.notEqual(outcomeLabel(changed), 'Code review complete · Validation limited');
  }
  delete task.reviewSummary; task.result.reviewSummary = original.reviewSummary;
  assert.equal(limitedReviewSummary(task), undefined, 'a model field cannot impersonate the Host projection');
  assert.equal(workflowOutcome(task), 'blocked');
});

test('recovery handlers accept only fixed kinds and immutable task targets', () => {
  const task = v2Run({ outcome: 'blocked', nextActions: [
    nextAction('runShell', 'Execute powershell.', 'Remove-Item C:\\Tasks'), nextAction('none'), nextAction('inspectResult', 'Inspect captured output.', ''),
    { ...nextAction('openTarget', 'Open the task target.', ''), url: 'https://attacker.example/', target: { repository: 'other/repo', number: 99 } }, nextAction('comment'), nextAction('rerun', 'Retry after correcting setup.', ''),
  ], diagnostics: [diagnostic(), diagnostic({ code: 'SETTINGS_AGAIN', message: 'Choose a repository folder.', recovery: 'configure' }), diagnostic({ code: 'ARBITRARY_COMMAND', message: 'Malformed recovery.', recovery: 'runShell' })] });
  assert.deepEqual(recoveryActions(task).filter(action => resultActionEligible(task, action)), [nextAction('inspectResult', 'Inspect captured output.', ''), nextAction('openTarget', 'Open the task target.', ''), nextAction('rerun', 'Retry after correcting setup.', ''), nextAction('configure', 'Desktop verification could not start.', '')]);
  delete task.task.target;
  assert.ok(!recoveryActions(task).some(item => item.kind === 'openTarget'));
  task.result.nextActions = []; task.result.diagnostics = [diagnostic({ recovery: 'none' })];
  assert.deepEqual(recoveryActions(task), [], 'a terminal result does not invent a retry request');
});

test('active v2 records show no stale findings, diagnostics or recovery and malformed terminal records remain inspectable', () => {
  const task = v2Run({ outcome: 'blocked', diagnostics: [diagnostic()], nextActions: [nextAction('rerun')] }); task.status.state = 'running';
  const active = resultPresentation(task);
  assert.equal(active.mode, 'active'); assert.equal(active.hasContent, false);
  for (const field of ['findings', 'diagnostics', 'artifacts', 'validation', 'blockers', 'nextSteps']) assert.deepEqual(active[field], []);
  assert.deepEqual(recoveryActions(task), []);
  const malformed = resultPresentation(v2Run({ outcome: undefined, summary: '', findings: [], diagnostics: [], validation: [], nextActions: [] }));
  assert.equal(malformed.mode, 'diagnostic'); assert.equal(malformed.hasContent, true); assert.match(malformed.summary, /blocked/);
});

test('canonical proposals preserve every same-kind draft and exact Markdown body', () => {
  const body = '    indented code  \n\nParagraph with a Markdown break.  \nNext line.\n\n';
  const actions = [
    { proposalId: 'opaque-server-id-a', ...nextAction('comment', 'Report scenario A.', body) },
    { proposalId: 'opaque-server-id-b', ...nextAction('comment', 'Report scenario B.', '  A different result.  \n') },
    { proposalId: 'opaque-server-id-c', ...nextAction('comment', 'Offer another explanation.', body) },
    { proposalId: 'opaque-server-id-d', ...nextAction('comment', 'Report scenario A.', body) },
  ];
  const task = v2Run({ review: null, nextActions: actions }, 'e2e');
  assert.deepEqual(recoveryActions(task), actions);
  assert.deepEqual(resultPresentation(task).nextSteps, actions);
  assert.deepEqual(resultOperationKinds(task), ['comment']);
  for (const action of actions) assert.equal(resultActionEligible(task, action), true);
  assert.equal(task.result.nextActions[0].body, body, 'the saved record is never rewritten');
});

test('legacy proposed bodies remain exact and do not require new proposal identifiers', () => {
  const body = '    code();  \n\n';
  const actions = [nextAction('comment', 'Share the legacy result.', body), nextAction('comment', 'Share another report.', 'Other findings.  \n')];
  const task = prRun({ review: null, nextSteps: actions });
  assert.deepEqual(recoveryActions(task), actions);
  assert.deepEqual(eligibleOperationKinds(task, preview()), ['comment']);
  assert.equal(resultActionEligible(task, recoveryActions(task)[0]), true);
});

test('specific proposal eligibility does not borrow another same-kind body or identity', () => {
  const blank = { proposalId: 'proposal-blank', ...nextAction('comment', 'Missing draft.', ' \n  ') };
  const valid = { proposalId: 'proposal-valid', ...nextAction('comment', 'Publish the actual report.', 'Actual report.  \n') };
  const task = v2Run({ review: null, nextActions: [blank, valid] }, 'e2e');
  assert.equal(resultActionEligible(task, blank), false); assert.equal(resultActionEligible(task, valid), true);
  assert.deepEqual(recoveryActions(task), [blank, valid]);
  assert.equal(resultActionEligible(task, { ...valid, proposalId: 'unrecognized-id' }), false);
  assert.equal(resultActionEligible(task, { ...valid, body: valid.body.trim() }), false, 'a changed body is not the saved proposal');
  assert.equal(resultActionEligible(task, { ...valid, kind: 'close' }), false, 'an ID cannot be reused for another operation kind');
});

test('approval and request changes accept proposal bodies with an empty SHA-bound review body', () => {
  const body = '    Verified draft content.  \n';
  for (const kind of ['approve', 'requestChanges']) {
    const action = { proposalId: `proposal-${kind}`, ...nextAction(kind, 'Review this proposal.', body) };
    const task = v2Run({ review: { headSha: sha, body: '', suggestions: [] }, nextActions: [action] });
    assert.equal(resultActionEligible(task, action), true, kind);
    assert.deepEqual(resultOperationKinds(task), [kind]); assert.deepEqual(eligibleOperationKinds(task, preview()), [kind]);
    assert.equal(recoveryActions(task)[0].body, body);
    task.result.review.headSha = 'b'.repeat(40); assert.equal(resultActionEligible(task, action), false);
    task.result.review = null; assert.equal(resultActionEligible(task, action), false);
  }
  const blank = nextAction('approve', 'Empty approval.', ' \n ');
  assert.equal(resultActionEligible(v2Run({ review: { headSha: sha, body: '', suggestions: [] }, nextActions: [blank] }), blank), false);
});

test('invalid inline suggestions cannot support review publication', () => {
  const suggestion = { path: 'src/a.cs', line: 4, startLine: 4, side: 'RIGHT', body: 'Add a guard.', replacement: 'guard();' };
  const invalid = [
    { path: 'a'.repeat(1025) }, { path: '../a.cs' }, { path: '/a.cs' }, { path: 'src\\a.cs' }, { path: 'src/./a.cs' }, { path: 'src//a.cs' }, { path: 'src/a\u0000.cs' },
    { side: 'LEFT' }, { line: 0 }, { line: 1.5 }, { startLine: 5 }, { line: 1001, startLine: 1 }, { line: 2147483648, startLine: 2147483648 },
    { body: undefined }, { body: 'a'.repeat(10001) }, { body: 'A\0B' }, { replacement: undefined }, { replacement: 'a'.repeat(60001) }, { replacement: 'A\0B' }, { replacement: '```suggestion\nunsafe();\n```' },
  ];
  const actions = [nextAction('approve'), nextAction('requestChanges'), nextAction('suggestChanges'), nextAction('comment')];
  for (const overrides of invalid) {
    const task = v2Run({ review: { headSha: sha, body: 'Verified review.', suggestions: [{ ...suggestion, ...overrides }] }, nextActions: actions });
    assert.deepEqual(resultOperationKinds(task), ['comment'], JSON.stringify(overrides).slice(0, 120));
    for (const action of actions.slice(0, 3)) assert.equal(resultActionEligible(task, action), false);
  }
});

test('inline proposal limits match the existing Host boundaries without smaller client caps', () => {
  const action = nextAction('suggestChanges');
  const suggestion = { path: 'a'.repeat(1024), line: 1000, startLine: 1, side: 'RIGHT', body: 'a'.repeat(10000), replacement: 'a'.repeat(60000) };
  const task = v2Run({ review: { headSha: sha, body: '', suggestions: [suggestion] }, nextActions: [action] });
  assert.equal(resultActionEligible(task, action), true);
  assert.deepEqual(resultOperationKinds(task), ['suggestChanges']);
  task.result.review.suggestions = [];
  assert.equal(resultActionEligible(task, action), false);
  task.result.review.suggestions = Array.from({ length: 100 }, (_, index) => ({ ...suggestion, path: `src/${index}.cs` }));
  assert.equal(resultActionEligible(task, action), true);
  task.result.review.suggestions.push({ ...suggestion, path: 'src/overflow.cs' });
  assert.equal(resultActionEligible(task, action), false);
});

const extendedProposal = (kind, overrides = {}) => ({ proposalId: `opaque-${kind}-server-id`, ...nextAction(kind, `Prepare ${kind}.`, ''), ...overrides });
const pullRequest = { head: 'contributor:verified-fix', base: 'main', title: 'Fix the verified issue', body: '    Patch details.  \n', draft: true };

test('extended GitHub proposals require a verified run, backend identity and the correct immutable target type', () => {
  for (const kind of ['create-pr', 'merge-pr', 'trigger-ci']) {
    const action = extendedProposal(kind, kind === 'create-pr' ? { pullRequest } : {});
    const task = v2Run({ review: null, nextActions: [action] }, kind === 'create-pr' ? 'issue-fix' : 'e2e');
    assert.equal(resultActionEligible(task, action), true, kind); assert.deepEqual(recoveryActions(task), [action]);
    assert.deepEqual(resultOperationKinds(task), [], 'new proposals use their dedicated confirmation flow');
    for (const state of ['accepted', 'running', 'failed', 'cancelled', 'interrupted']) {
      const stopped = structuredClone(task); stopped.status.state = state;
      assert.equal(resultActionEligible(stopped, stopped.result.nextActions[0]), false, `${kind}/${state}`);
    }
    for (const outcome of ['blocked', 'failed', 'cancelled', 'interrupted']) {
      const incomplete = structuredClone(task); incomplete.result.outcome = outcome;
      assert.equal(resultActionEligible(incomplete, incomplete.result.nextActions[0]), false, `${kind}/${outcome}`);
    }
    const missingCheck = structuredClone(task); missingCheck.result.validation.pop();
    assert.equal(resultActionEligible(missingCheck, missingCheck.result.nextActions[0]), false);
    const missingId = structuredClone(task); delete missingId.result.nextActions[0].proposalId;
    assert.equal(resultActionEligible(missingId, missingId.result.nextActions[0]), false);
    const otherTarget = structuredClone(task); otherTarget.task.target.type = kind === 'create-pr' ? 'pr' : 'issue'; otherTarget.task.expectedHeadSha = sha;
    assert.equal(resultActionEligible(otherTarget, otherTarget.result.nextActions[0]), false);
    if (kind !== 'create-pr') { delete task.task.expectedHeadSha; assert.equal(resultActionEligible(task, action), false); }
  }
});

test('create PR proposals preserve typed fields exactly and reject incomplete or changed proposal parameters', () => {
  const action = extendedProposal('create-pr', { pullRequest });
  const task = v2Run({ nextActions: [action] }, 'issue-fix');
  assert.deepEqual(recoveryActions(task)[0].pullRequest, pullRequest);
  assert.equal(resultPresentation(task).nextSteps[0].pullRequest.body, pullRequest.body);
  for (const patch of [{ head: '' }, { base: ' ' }, { title: '' }, { body: undefined }, { draft: undefined }]) {
    const changed = structuredClone(task); Object.assign(changed.result.nextActions[0].pullRequest, patch);
    assert.equal(resultActionEligible(changed, changed.result.nextActions[0]), false);
  }
  assert.equal(resultActionEligible(task, { ...action, pullRequest: { ...pullRequest, head: 'other:branch' } }), false);
  const missing = structuredClone(task); delete missing.result.nextActions[0].pullRequest;
  assert.equal(resultActionEligible(missing, missing.result.nextActions[0]), false);
});

test('new proposal bodies follow the fixed typed GitHub operation contract exactly', () => {
  for (const kind of ['create-pr', 'merge-pr', 'trigger-ci']) for (const body of ['', '/azp run', ' /azp run ', ' ', '\n', 'Arbitrary command', undefined]) {
    const action = extendedProposal(kind, { body, ...(kind === 'create-pr' ? { pullRequest } : {}) });
    const task = v2Run({ review: null, nextActions: [action] }, kind === 'create-pr' ? 'issue-fix' : 'e2e');
    const allowed = body === '' || kind === 'trigger-ci' && body === '/azp run';
    assert.equal(resultActionEligible(task, action), allowed, `${kind}/${JSON.stringify(body)}`);
    if (allowed) assert.equal(recoveryActions(task)[0].body, body);
    else assert.deepEqual(recoveryActions(task).filter(item => resultActionEligible(task, item)), []);
  }
});

test('Host proposal availability permits truthful failed reports while lifecycle and saved identity stay authoritative', () => {
  const comment = { proposalId: 'report-negative-e2e', ...nextAction('comment', 'Report the captured regression.', 'The tested scenario failed.'), availability: { enabled: true, reasons: [] } };
  const task = v2Run({ outcome: 'failed', review: null, assessment: { subject: 'original-pr', status: 'failed', summary: 'Regression reproduced.', revisionSha: sha }, validation: [{ id: 'e2e', required: true, status: 'failed' }], diagnostics: [diagnostic()], nextActions: [comment] }, 'e2e');
  assert.equal(resultActionEligible(task, comment), true, 'the Host may authorize a truthful report even when the assessment is negative');
  assert.deepEqual(eligibleOperationKinds(task, preview()), ['comment']);
  assert.equal(resultPresentation(task).mode, 'diagnostic');
  for (const code of [undefined, null, 1]) {
    task.status.exitCode = code; assert.equal(resultActionEligible(task, comment), false); assert.match(resultActionAvailability(task, comment).reasons.join(' '), /successful CLI exit/);
  }
  task.status.exitCode = 0;
  for (const state of ['failed', 'cancelled', 'interrupted', 'running']) { task.status.state = state; assert.equal(resultActionEligible(task, comment), false); }
  task.status.state = 'succeeded';
  task.result.nextActions[0].availability = { enabled: false, reasons: ['This report no longer matches the analyzed revision.'] };
  assert.deepEqual(resultActionAvailability(task, { ...comment, availability: { enabled: true, reasons: [] } }), task.result.nextActions[0].availability, 'a captured or modified action cannot override current Host availability');
  assert.equal(recoveryActions(task).length, 2, 'the refused proposal and diagnostic recovery both remain visible');
});

test('completion labels distinguish workflow completion from unresolved findings and product assessment', () => {
  const task = v2Run({ needsReview: false, findings: [], assessment: null });
  assert.equal(outcomeLabel(task), 'Completed');
  task.result.findingSummary = { unresolved: 2, high: 1, medium: 1, low: 0 };
  assert.equal(outcomeLabel(task), 'Completed · 2 issues'); assert.equal(resultNeedsAttention(task), true);
  delete task.result.findingSummary;
  task.result.assessment = { subject: 'local-candidate', status: 'inconclusive', summary: 'The candidate still needs validation.', revisionSha: sha };
  assert.equal(outcomeLabel(task), 'Completed · Needs attention'); assert.equal(resultNeedsAttention(task), true);
  task.result.findings = [{ id: 'open', severity: 'high', status: 'open' }, { id: 'fixed', severity: 'medium', status: 'fixed' }];
  assert.equal(outcomeLabel(task), 'Completed · 1 issue');
  task.status.state = 'running'; assert.equal(outcomeLabel(task), 'Running');
});

test('legacy Host fallback blocks both approval and merge for original-revision unresolved serious findings', () => {
  for (const severity of ['high', 'medium']) {
    const approve = nextAction('approve'); const merge = extendedProposal('merge-pr');
    const task = v2Run({ nextActions: [approve, merge], findings: [{ id: 'unresolved', severity, status: 'open' }] });
    assert.equal(resultActionEligible(task, approve), false); assert.equal(resultActionEligible(task, merge), false);
    assert.match(resultActionAvailability(task, merge).reasons.join(' '), /Resolve the open/);
  }
});

test('legacy Host fallback keeps inconclusive or mismatched assessments from enabling approval and merge', () => {
  const approve = nextAction('approve'); const merge = extendedProposal('merge-pr');
  const task = v2Run({ nextActions: [approve, merge], assessment: { subject: 'original-pr', status: 'passed', summary: 'Original revision verified.', revisionSha: sha } });
  for (const action of [approve, merge]) assert.equal(resultActionEligible(task, action), true);
  const passing = structuredClone(task.result.assessment);
  for (const change of [{ status: 'inconclusive' }, { status: 'failed' }, { subject: 'local-candidate' }, { revisionSha: 'b'.repeat(40) }, { revisionSha: undefined }]) {
    task.result.assessment = { ...passing, ...change };
    for (const action of [approve, merge]) { assert.equal(resultActionEligible(task, action), false); assert.match(resultActionAvailability(task, action).reasons.join(' '), /passing assessment of the original pull request/); }
  }
  delete task.result.assessment;
  for (const action of [approve, merge]) assert.equal(resultActionEligible(task, action), true, 'records predating assessment keep the existing compatibility policy');
});
