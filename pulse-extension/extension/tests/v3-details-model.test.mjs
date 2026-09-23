import test from 'node:test';
import assert from 'node:assert/strict';
import {
  completedV3Findings, currentRecommendation, eligibleManualOperationKinds, hasConfirmedP0,
  isFinalReportComplete, manualOperationAvailability, manualOperationKinds, resultPresentation,
} from '../src/details-model.ts';

const sha = 'abcdef0123456789abcdef0123456789abcdef01';
const otherSha = '1234567890abcdef1234567890abcdef12345678';
const repository = 'microsoft/PowerToys';
const runId = '6c9f13d3-e925-4527-9e1b-4a7fcd2d2af2';
const duplicateOf = { repository, number: 27, url: `https://github.com/${repository}/issues/27` };
const originalProvenance = () => ({ version: 1, source: 'host', subject: 'original-pr', expectedHeadSha: sha,
  start: { headSha: sha, workingTree: 'clean' }, end: { headSha: sha, workingTree: 'clean' } });

function finding(id = 'finding-1', overrides = {}) {
  return {
    id, title: `Confirmed issue ${id}`, priority: 'P2', status: 'open', confirmed: true,
    path: 'src/Shortcuts.cs', line: 17, details: 'A later registration replaces the earlier callback.',
    impact: 'The original shortcut stops responding.', trigger: 'Register two actions for one shortcut.',
    rootCause: 'The replacement branch does not retain the previous callback.',
    fixSuggestion: 'Reject the duplicate registration and keep the original callback.',
    evidence: ['The registration branch replaces the callback at the reviewed revision.'],
    feedback: { body: 'Preserve the original shortcut callback when registration conflicts.', suggestionId: null },
    ...overrides,
  };
}

function e2e(level = 'not_needed', overrides = {}) {
  return {
    level, reason: level === 'not_needed' ? 'The changed pure function is covered by the recorded unit tests.' : 'Observe shortcut routing in the application.',
    question: level === 'not_needed' ? '' : 'Does the original shortcut still respond after a conflicting registration?',
    scenarios: level === 'not_needed' ? [] : ['Register conflicting shortcuts, then activate the original action.'],
    expectedResults: level === 'not_needed' ? [] : ['The original action runs once.'],
    prerequisites: [], evidence: ['The source branch and relevant unit tests were inspected.'], readiness: 'ready',
    ...overrides,
  };
}

function proposal(kind, overrides = {}) {
  return {
    proposalId: `proposal-${kind}`, kind, reason: `Review the saved ${kind} recommendation.`,
    body: ['start-task', 'merge-pr', 'trigger-ci', 'create-pr'].includes(kind) ? '' : 'The investigation and its evidence are recorded below.',
    recommended: true, ...overrides,
  };
}

function plan(kind, id = `plan-${kind}`) {
  return {
    id, kind, summary: 'Continue from the saved investigation and version.',
    steps: ['Inspect the affected registration path and implement the recorded change.'],
    acceptanceCriteria: ['The original shortcut remains functional after a conflict.'],
    prerequisites: [], evidence: ['The final investigation identifies the affected branch.'],
  };
}

function v3Run(overrides = {}) {
  return {
    runId,
    task: {
      requestId: runId, actionId: 'review-pr', actionKind: 'pr-review', repository,
      target: { type: 'pr', number: 42 }, expectedHeadSha: sha, reviewOptions: { mode: 'static' }, prompt: 'Review this PR.',
    },
    config: { agent: 'codex', repoFolder: 'C:\\Tasks\\review-pr', cliPath: 'codex.exe', permission: 'read-only' },
    status: { state: 'succeeded', exitCode: 0, createdAt: '2026-09-15T00:00:00Z', updatedAt: '2026-09-15T00:01:00Z', sequence: 2 },
    view: { read: false, handled: false },
    result: {
      schemaVersion: 3, structured: true, cliExitCode: 0, outcome: 'completed', phase: 'reporting', summary: 'The selected analysis is complete.',
      assessment: { subject: 'original-pr', status: 'passed', summary: 'The original PR revision was inspected.', revisionSha: sha },
      reviewConclusion: { status: 'no-blocking-findings', summary: 'The code review completed on this PR revision.', revisionSha: sha, blockingUncertainties: [] },
      verificationEvidence: [], report: { complete: true, rechecked: true, coverage: ['All changed files and affected registration paths were rechecked.'], limitations: [] },
      findings: [], artifacts: [], validation: [], diagnostics: [], nextActions: [], review: null, needsReview: false,
      e2eAssessment: e2e(), featureAssessment: null, bugAssessment: null, plans: [], blockers: [], nextSteps: [], ...overrides,
    },
  };
}

function preview(overrides = {}) {
  const url = `https://github.com/${repository}/pull/42`;
  return {
    account: 'reviewer', target: { type: 'pr', number: 42, repository, title: 'Keep shortcut registrations stable', url, state: 'open' },
    url, state: 'open', headSha: sha, expectedHeadSha: sha, stale: false,
    canApprove: true, canRequestChanges: true, canSuggestChanges: true, canComment: true, canClose: true,
    files: [], reasons: [], ...overrides,
  };
}

function issueRun(kind, assessment, overrides = {}) {
  const run = v3Run({ assessment: { subject: 'target', status: 'passed', summary: 'The issue investigation is complete.', revisionSha: null }, reviewConclusion: null, e2eAssessment: null, ...overrides });
  run.task.actionKind = kind === 'feature' ? 'feature-research' : 'bug-investigation';
  run.task.target = { type: 'issue', number: 42 };
  delete run.task.expectedHeadSha;
  delete run.task.reviewOptions;
  run.result[kind === 'feature' ? 'featureAssessment' : 'bugAssessment'] = assessment;
  return run;
}

function featureAssessment(status, overrides = {}) {
  return {
    status, summary: `Feature investigation: ${status}.`, reasons: ['The current architecture and reported scenario were examined.'],
    evidence: ['The relevant implementation, issue history, and platform capability were inspected.'],
    acceptanceCriteria: status === 'ready' ? ['The requested shortcut action can be configured and triggered.'] : [],
    questions: status === 'needs_information' ? ['Which shortcut combinations must be supported?'] : [],
    alternatives: status === 'needs_decision' ? ['Keep the existing binding or ask before replacing it.'] : [],
    relatedIssue: status === 'duplicate' ? { ...duplicateOf } : null, planId: status === 'ready' ? 'plan-feature-implement' : null,
    ...overrides,
  };
}

function bugAssessment(status, overrides = {}) {
  return {
    status, summary: `Bug investigation: ${status}.`, reasons: ['The report, current implementation, and available evidence were examined.'],
    evidence: ['The original registration is replaced by the conflicting registration.'],
    questions: status === 'needs_information' ? ['Which shortcut configuration produced the failure?'] : [],
    relatedIssue: status === 'duplicate' ? { ...duplicateOf } : null,
    planId: status === 'confirmed' ? 'plan-issue-fix' : status === 'needs_verification' ? 'plan-reproduction-setup' : null,
    reproduction: { status: 'not_run', revisionSha: sha, environment: '', steps: [], expected: '', observed: '', evidence: [] },
    ...overrides,
  };
}

const editorKinds = ['comment', 'approve', 'suggestChanges', 'requestChanges', 'close'];
const allPrKinds = [...editorKinds, 'merge-pr', 'trigger-ci'];

test('complete v3 reports retain every confirmed P0-P3 finding without a Top N limit', () => {
  const findings = Array.from({ length: 137 }, (_, index) => finding(`finding-${index + 1}`, { priority: `P${index % 4}` }));
  findings.push(finding('confirmed-fixed', { priority: 'P0', status: 'fixed' }));
  const candidates = [finding('candidate', { priority: 'P0', confirmed: false, status: 'unverified' }), finding('unconfirmed', { confirmed: false })];
  const run = v3Run({ findings: [...findings, ...candidates] });
  const saved = structuredClone(run);
  assert.equal(isFinalReportComplete(run), true);
  assert.deepEqual(completedV3Findings(run), findings);
  assert.deepEqual(new Set(completedV3Findings(run).map(row => row.priority)), new Set(['P0', 'P1', 'P2', 'P3']));
  assert.deepEqual(run, saved, 'reading the complete list preserves the saved report and its finding IDs');
});

test('partial reports and active runs never expose a completed final finding list', () => {
  const mutations = [
    ['missing report', run => { delete run.result.report; }],
    ['not complete', run => { run.result.report.complete = false; }],
    ['not rechecked', run => { run.result.report.rechecked = false; }],
    ['unread result pages', run => { run.resultPaging = { fingerprint: 'a'.repeat(64), sections: [{ path: 'findings', total: 138, nextOffset: 1 }] }; }],
    ...['blocked', 'failed', 'cancelled', 'interrupted'].map(outcome => [outcome, run => { run.result.outcome = outcome; }]),
    ...['accepted', 'running'].map(state => [state, run => { run.status.state = state; }]),
  ];
  for (const [label, mutate] of mutations) {
    const run = v3Run({ findings: [finding()] }); mutate(run);
    assert.equal(isFinalReportComplete(run), false, label);
    assert.deepEqual(completedV3Findings(run), [], label);
  }
  const missing = v3Run(); delete missing.result;
  assert.equal(isFinalReportComplete(missing), false);
  assert.deepEqual(completedV3Findings(missing), []);
  for (const schemaVersion of [undefined, 1, 2]) {
    const legacy = v3Run({ schemaVersion, findings: [finding()] });
    assert.equal(isFinalReportComplete(legacy), false);
    assert.deepEqual(completedV3Findings(legacy), []);
  }
});

test('confirmed P0 binding accepts the current original assessment or a current PR-review conclusion', () => {
  const run = v3Run({ findings: [finding('current-p0', { priority: 'P0' })] });
  assert.equal(hasConfirmedP0(run), true);
  run.result.reviewConclusion = null;
  run.task.actionKind = 'pr-verify';
  assert.equal(hasConfirmedP0(run), false, 'verification-only findings require Host provenance of the original source');
  run.provenance = originalProvenance();
  assert.equal(hasConfirmedP0(run), true, 'verification-only evidence applies when the Host records unchanged original source');
  run.result.assessment.revisionSha = sha.toUpperCase();
  assert.equal(hasConfirmedP0(run), true, 'full SHA comparison is case insensitive');
  run.result.assessment = { subject: 'local-candidate', status: 'failed', summary: 'A local candidate was inspected.', revisionSha: sha };
  run.result.reviewConclusion = { status: 'changes-requested', summary: 'The original PR contains a confirmed P0.', revisionSha: sha, blockingUncertainties: [] };
  assert.equal(hasConfirmedP0(run), false, 'a non-review task cannot use an unrelated reviewConclusion to bind its findings');
  run.task.actionKind = 'pr-review';
  assert.equal(hasConfirmedP0(run), true, 'PR review has its own immutable original-revision conclusion');
});

test('verification-only P0 findings require Host provenance of clean original code at the task SHA throughout the run', () => {
  const mutations = [
    ['missing provenance', run => { delete run.provenance; }],
    ['unsupported provenance version', run => { run.provenance.version = 2; }],
    ['model-authored provenance', run => { run.provenance.source = 'model'; }],
    ['local candidate', run => { run.provenance.subject = 'local-candidate'; }],
    ['wrong expected revision', run => { run.provenance.expectedHeadSha = otherSha; }],
    ['missing start observation', run => { delete run.provenance.start; }],
    ['different starting revision', run => { run.provenance.start.headSha = otherSha; }],
    ['dirty starting tree', run => { run.provenance.start.workingTree = 'dirty'; }],
    ['missing end observation', run => { delete run.provenance.end; }],
    ['different ending revision', run => { run.provenance.end.headSha = otherSha; }],
    ['dirty ending tree', run => { run.provenance.end.workingTree = 'dirty'; }],
  ];
  for (const actionKind of ['e2e', 'pr-verify']) {
    const original = v3Run({ findings: [finding('verified-p0', { priority: 'P0' })] });
    original.task.actionKind = actionKind; original.provenance = originalProvenance();
    assert.equal(hasConfirmedP0(original), true, actionKind);
    for (const [label, mutate] of mutations) {
      const run = structuredClone(original); mutate(run);
      assert.equal(hasConfirmedP0(run), false, `${actionKind}/${label}`);
    }
    original.provenance.expectedHeadSha = sha.toUpperCase();
    original.provenance.start.headSha = sha.toUpperCase(); original.provenance.end.headSha = sha.toUpperCase();
    assert.equal(hasConfirmedP0(original), true, `${actionKind}/case-insensitive full SHAs`);
  }
});

test('P0 detection excludes stale revisions, local candidates, resolved findings, hypotheses, and non-PR targets', () => {
  const mutations = [
    ['stale assessment and conclusion', run => { run.result.assessment.revisionSha = otherSha; run.result.reviewConclusion.revisionSha = otherSha; }],
    ['local candidate only', run => { run.result.assessment.subject = 'local-candidate'; run.result.reviewConclusion = null; }],
    ['generic target only', run => { run.result.assessment.subject = 'target'; run.result.reviewConclusion = null; }],
    ['no original metadata', run => { run.result.assessment = null; run.result.reviewConclusion = null; }],
    ['missing task SHA', run => { delete run.task.expectedHeadSha; }],
    ['abbreviated task SHA', run => { run.task.expectedHeadSha = sha.slice(0, 7); }],
    ['resolved P0', run => { run.result.findings[0].status = 'fixed'; }],
    ['unverified hypothesis', run => { run.result.findings[0].confirmed = false; run.result.findings[0].status = 'unverified'; }],
    ['unconfirmed open issue', run => { run.result.findings[0].confirmed = false; }],
    ['issue target', run => { run.task.target.type = 'issue'; }],
    ...['P1', 'P2', 'P3'].map(priority => [priority, run => { run.result.findings[0].priority = priority; }]),
  ];
  for (const [label, mutate] of mutations) {
    const run = v3Run({ findings: [finding('p0', { priority: 'P0' })] }); mutate(run);
    assert.equal(hasConfirmedP0(run), false, label);
  }
  for (const schemaVersion of [undefined, 1, 2]) {
    const run = v3Run({ schemaVersion, findings: [{ id: 'legacy-high', severity: 'high', status: 'open', priority: 'P0', confirmed: true }] });
    assert.equal(hasConfirmedP0(run), false, 'legacy high severity never becomes a v3 P0, even with incidental newer fields');
  }
});

test('P0 detection ignores Host execution metadata but requires a valid canonical confirmed finding', () => {
  for (const structured of [undefined, false, true]) {
    const run = v3Run({ structured, cliExitCode: 9, findings: [finding('confirmed-p0', { priority: 'P0' })] });
    run.status.exitCode = 9;
    assert.equal(hasConfirmedP0(run), true, String(structured));
  }
  const mutations = [
    ['missing evidence', run => { run.result.findings[0].evidence = []; }],
    ['missing root cause', run => { delete run.result.findings[0].rootCause; }],
    ['missing impact', run => { delete run.result.findings[0].impact; }],
    ['invalid confirmed flag', run => { run.result.findings[0].confirmed = 'true'; }],
    ['confirmed but unverified', run => { run.result.findings[0].status = 'unverified'; }],
    ['invalid priority', run => { run.result.findings[0].priority = 'p0'; }],
    ['duplicate finding ID', run => { run.result.findings.push({ ...run.result.findings[0] }); }],
  ];
  for (const [label, mutate] of mutations) {
    const run = v3Run({ findings: [finding('confirmed-p0', { priority: 'P0' })] }); mutate(run);
    assert.equal(hasConfirmedP0(run), false, label);
  }
});

test('confirmed current P0 still blocks manual approval when the saved report is incomplete', () => {
  for (const state of ['accepted', 'running', 'succeeded', 'failed', 'cancelled', 'interrupted']) {
    const run = v3Run({ outcome: 'blocked', findings: [finding('confirmed-p0', { priority: 'P0' })], report: { complete: false, rechecked: false, coverage: ['The critical path was checked.'], limitations: ['The remaining review is unfinished.'] } });
    run.status.state = state;
    assert.equal(isFinalReportComplete(run), false, state);
    assert.deepEqual(completedV3Findings(run), [], state);
    assert.equal(hasConfirmedP0(run), true, state);
    assert.deepEqual(manualOperationKinds(run), editorKinds, state);
    assert.equal(manualOperationAvailability(run, 'approve').enabled, false, state);
    assert.match(manualOperationAvailability(run, 'approve').reasons.join(' '), /P0/, state);
    for (const kind of allPrKinds.filter(kind => kind !== 'approve')) assert.equal(manualOperationAvailability(run, kind).enabled, true, `${state}/${kind}`);
  }
});

test('fixed manual PR choices survive lifecycle failures, missing AI proposals, P1-P3, and required E2E', () => {
  const cases = [
    ['no saved result', run => { delete run.result; }],
    ['no AI review or proposals', () => {}],
    ['incomplete analysis', run => { run.result.outcome = 'blocked'; run.result.report.complete = false; }],
    ['failed CLI', run => { run.status.state = 'failed'; run.status.exitCode = 1; run.result.outcome = 'failed'; }],
    ['active run', run => { run.status.state = 'running'; }],
    ['required E2E', run => { run.result.e2eAssessment = e2e('required', { readiness: 'missing-prerequisites', prerequisites: ['A Windows desktop session is required.'] }); }],
    ['failed product assessment', run => { run.result.assessment.status = 'failed'; }],
    ...['P1', 'P2', 'P3'].map(priority => [priority, run => { run.result.findings = [finding(`finding-${priority}`, { priority })]; }]),
  ];
  for (const [label, mutate] of cases) {
    const run = v3Run(); mutate(run);
    assert.deepEqual(manualOperationKinds(run), editorKinds, label);
    for (const kind of allPrKinds) assert.equal(manualOperationAvailability(run, kind).enabled, true, `${label}/${kind}`);
  }
});

test('manual operation sets follow the target and do not invent unsupported Issue actions', () => {
  const run = v3Run(); run.task.target = { type: 'issue', number: 42 };
  assert.deepEqual(manualOperationKinds(run), ['comment', 'close']);
  for (const kind of ['comment', 'close']) assert.equal(manualOperationAvailability(run, kind).enabled, true);
  for (const kind of ['approve', 'suggestChanges', 'requestChanges', 'merge-pr', 'trigger-ci', 'start-task']) assert.equal(manualOperationAvailability(run, kind).enabled, false, kind);
  delete run.task.target;
  assert.deepEqual(manualOperationKinds(run), []);
  assert.equal(manualOperationAvailability(run, 'comment').enabled, false);
});

test('fresh manual previews enforce account, identity, SHA, and individual permissions without requiring an AI draft', () => {
  const run = v3Run(); delete run.result;
  assert.deepEqual(eligibleManualOperationKinds(run, preview()), editorKinds);
  const mutations = [
    ['no account', value => { value.account = null; }],
    ['stale preview', value => { value.stale = true; }],
    ['different target type', value => { value.target.type = 'issue'; }],
    ['different target number', value => { value.target.number++; }],
    ['different repository', value => { value.target.repository = 'microsoft/other'; }],
    ['different current SHA', value => { value.headSha = otherSha; }],
    ['different expected SHA', value => { value.expectedHeadSha = otherSha; }],
    ['missing SHA', value => { delete value.headSha; }],
  ];
  for (const [label, mutate] of mutations) {
    const value = preview(); mutate(value);
    assert.deepEqual(eligibleManualOperationKinds(run, value), [], label);
  }
  assert.deepEqual(eligibleManualOperationKinds(run, undefined), []);
  assert.deepEqual(eligibleManualOperationKinds(run, preview({ canApprove: false })), editorKinds.filter(kind => kind !== 'approve'));
  const matchingCase = preview({ headSha: sha.toUpperCase(), expectedHeadSha: sha.toUpperCase() }); matchingCase.target.repository = repository.toUpperCase();
  assert.deepEqual(eligibleManualOperationKinds(run, matchingCase), editorKinds);
});

test('manual preview disables only approval for a confirmed current P0', () => {
  const run = v3Run({ findings: [finding('confirmed-p0', { priority: 'P0' })] });
  assert.deepEqual(eligibleManualOperationKinds(run, preview()), editorKinds.filter(kind => kind !== 'approve'));
  run.result.findings[0].priority = 'P1';
  assert.deepEqual(eligibleManualOperationKinds(run, preview()), editorKinds);
});

test('explicit Host preview P0 flags override local evidence without bypassing live approval prerequisites', () => {
  for (const localP0 of [false, true]) for (const flag of [undefined, true, false]) {
    const run = v3Run({ findings: localP0 ? [finding('local-p0', { priority: 'P0' })] : [] });
    const saved = structuredClone(run);
    const expectedP0 = typeof flag === 'boolean' ? flag : localP0;
    assert.deepEqual(eligibleManualOperationKinds(run, preview({ hasConfirmedP0: flag })), editorKinds.filter(kind => kind !== 'approve' || !expectedP0), `${localP0}/${flag}`);
    assert.deepEqual(run, saved, 'authoritative live availability does not rewrite the saved findings');
  }
  const run = v3Run({ findings: [finding('local-p0', { priority: 'P0' })] });
  for (const hasConfirmedP0 of [null, 'false', 0]) assert.equal(eligibleManualOperationKinds(run, preview({ hasConfirmedP0 })).includes('approve'), false, 'only explicit boolean flags override local evidence');
  assert.deepEqual(eligibleManualOperationKinds(run, preview({ hasConfirmedP0: false, canApprove: false })), editorKinds.filter(kind => kind !== 'approve'));
  for (const mutate of [value => { value.target.number++; }, value => { value.headSha = otherSha; }, value => { value.stale = true; }, value => { value.account = null; }]) {
    const value = preview({ hasConfirmedP0: false }); mutate(value);
    assert.deepEqual(eligibleManualOperationKinds(run, value), [], 'an explicit no-P0 flag does not override target, SHA, freshness, or account checks');
  }
});

test('PR advice prioritizes confirmed P0/P1 while keeping required E2E independent from manual approval', () => {
  for (const priority of ['P0', 'P1']) {
    const run = v3Run({ findings: [finding(`finding-${priority}`, { priority })], e2eAssessment: e2e('required'), nextActions: [proposal('approve')] });
    const saved = structuredClone(run);
    assert.equal(currentRecommendation(run)?.kind, 'address-findings', priority);
    assert.equal(manualOperationAvailability(run, 'approve').enabled, priority !== 'P0', priority);
    assert.equal(run.result.e2eAssessment.level, 'required');
    assert.deepEqual(run, saved, 'derived advice preserves both the saved proposal and the independent E2E assessment');
  }
});

test('P2/P3 and suggested E2E permit positive advice while missing E2E never means not needed', () => {
  for (const level of ['not_needed', 'recommended', 'required']) {
    const run = v3Run({ findings: [finding('p2', { priority: 'P2' }), finding('p3', { priority: 'P3' })], e2eAssessment: e2e(level) });
    assert.equal(currentRecommendation(run)?.kind, level === 'required' ? 'run-e2e' : 'approve', level);
    assert.equal(manualOperationAvailability(run, 'approve').enabled, true, level);
  }
  for (const e2eAssessment of [null, undefined]) {
    const run = v3Run({ e2eAssessment, nextActions: [proposal('approve')] });
    assert.equal(currentRecommendation(run)?.kind, 'incomplete');
    assert.equal(run.result.e2eAssessment, e2eAssessment, 'missing E2E data remains missing');
    assert.equal(manualOperationAvailability(run, 'approve').enabled, true);
  }
});

test('required E2E advice changes only for the authoritative evidence-complete projection', () => {
  for (const e2eEvidenceComplete of [undefined, false, null, 1, 'true']) {
    const run = v3Run({ e2eAssessment: e2e('required'), e2eEvidenceComplete });
    run.relatedVerification = { currentConclusion: { canSupplementAssessment: true, evidenceComplete: true, completedRunId: runId } };
    assert.equal(currentRecommendation(run)?.kind, 'run-e2e', String(e2eEvidenceComplete));
  }
  const run = v3Run({ e2eAssessment: e2e('required'), e2eEvidenceComplete: true });
  const saved = structuredClone(run);
  assert.equal(currentRecommendation(run)?.kind, 'approve');
  assert.deepEqual(run, saved, 'supplemental evidence does not rewrite the original E2E necessity or review report');
  for (const priority of ['P0', 'P1']) {
    run.result.findings = [finding(`finding-${priority}`, { priority })];
    assert.equal(currentRecommendation(run)?.kind, 'address-findings', priority);
  }
});

test('the Host recommendation after compatible verification is displayed without rewriting the original report', () => {
  const original = v3Run({ e2eAssessment: e2e('required'), nextActions: [proposal('inspectResult', { reason: 'Preserve the original proposed follow-up.' })] });
  const saved = structuredClone(original);
  assert.equal(currentRecommendation(original)?.kind, 'run-e2e');
  const projected = structuredClone(original);
  projected.result.e2eEvidenceComplete = true;
  projected.result.recommendation = { kind: 'approve', reason: 'The Host accepted complete compatible evidence for the recorded PR scenarios.' };
  const projectedSnapshot = structuredClone(projected);
  const advice = currentRecommendation(projected);
  assert.deepEqual(advice, projected.result.recommendation); assert.notEqual(advice, projected.result.recommendation);
  assert.deepEqual(resultPresentation(projected).recommendation, projected.result.recommendation);
  assert.equal(projected.result.e2eAssessment.level, 'required', 'supplementing evidence does not change the original necessity level');
  assert.deepEqual(projected, projectedSnapshot); assert.deepEqual(original, saved);
  advice.reason = 'Local display edit';
  assert.deepEqual(projected, projectedSnapshot, 'the returned recommendation is a detached display value');
  const incomplete = structuredClone(projected); incomplete.result.report.complete = false;
  assert.equal(currentRecommendation(incomplete)?.kind, 'incomplete', 'a projected recommendation cannot disguise an incomplete final report');
});

test('active or incomplete analyses never produce positive advice and legacy reports remain unrelabeled', () => {
  const incomplete = v3Run(); incomplete.result.report.complete = false;
  assert.equal(currentRecommendation(incomplete)?.kind, 'incomplete');
  for (const state of ['accepted', 'running']) {
    const active = v3Run(); active.status.state = state;
    assert.equal(currentRecommendation(active)?.kind, 'incomplete', state);
  }
  for (const schemaVersion of [undefined, 1, 2]) assert.equal(currentRecommendation(v3Run({ schemaVersion })), undefined);
});

for (const status of ['ready', 'needs_information', 'needs_decision', 'already_supported', 'duplicate', 'not_feasible']) {
  test(`Feature ${status} retains the combined assessment and uses its canonical recommended proposal`, () => {
    const assessment = featureAssessment(status);
    const plans = status === 'ready' ? [plan('feature-implement')] : [];
    const action = status === 'ready'
      ? proposal('start-task', { taskKind: 'feature-implement', planId: assessment.planId })
      : status === 'duplicate' ? proposal('close-as-duplicate', { duplicateOf: { ...duplicateOf } }) : proposal('comment');
    const run = issueRun('feature', assessment, { plans, nextActions: [proposal('inspectResult', { recommended: false }), action] });
    const saved = structuredClone(run);
    const presented = resultPresentation(run);
    assert.equal(isFinalReportComplete(run), true);
    assert.deepEqual(presented.featureAssessment, assessment);
    assert.equal(presented.bugAssessment ?? null, null);
    assert.equal(currentRecommendation(run)?.kind, action.kind);
    assert.equal(currentRecommendation(run)?.proposalId, action.proposalId);
    assert.equal(currentRecommendation(run)?.reason, action.reason);
    assert.equal(currentRecommendation(run)?.planId, action.planId);
    assert.deepEqual(run, saved, 'presentation preserves the conclusion, questions, alternatives, target, and saved plan');
  });
}

for (const status of ['confirmed', 'needs_information', 'needs_verification', 'already_fixed', 'duplicate', 'not_a_bug']) {
  test(`Bug ${status} retains investigation and reproduction facts with the selected next-action reference`, () => {
    const assessment = bugAssessment(status);
    const taskKind = status === 'confirmed' ? 'issue-fix' : status === 'needs_verification' ? 'reproduction-setup' : undefined;
    const plans = taskKind ? [plan(taskKind)] : [];
    const action = taskKind
      ? proposal('start-task', { taskKind, planId: assessment.planId })
      : status === 'duplicate' ? proposal('close-as-duplicate', { duplicateOf: { ...duplicateOf } }) : proposal('comment');
    const run = issueRun('bug', assessment, { plans, findings: status === 'confirmed' ? [finding()] : [], nextActions: [proposal('inspectResult', { recommended: false }), action] });
    const saved = structuredClone(run);
    const presented = resultPresentation(run);
    assert.equal(isFinalReportComplete(run), true);
    assert.deepEqual(presented.bugAssessment, assessment);
    assert.equal(presented.featureAssessment ?? null, null);
    assert.equal(currentRecommendation(run)?.kind, action.kind);
    assert.equal(currentRecommendation(run)?.proposalId, action.proposalId);
    assert.equal(currentRecommendation(run)?.reason, action.reason);
    assert.equal(currentRecommendation(run)?.planId, action.planId);
    assert.deepEqual(run, saved, 'presentation does not turn investigation or reproduction completion into Issue closure');
  });
}

test('a confirmed Bug may remain not reproduced and recommend a comment when local fixing is not actionable', () => {
  const assessment = bugAssessment('confirmed', {
    planId: null,
    reasons: ['The code proves the defect, but the affected platform dependency cannot be changed in this repository.'],
    reproduction: { status: 'not_reproduced', revisionSha: sha, environment: 'The available Windows test device.', steps: ['Trigger the conflicting shortcut.'], expected: 'The original action responds.', observed: 'The reported device condition was unavailable.', evidence: ['The attempted reproduction and its environment were recorded.'] },
  });
  const action = proposal('comment', { reason: 'Explain the confirmed defect, local limitation, and workaround.' });
  const run = issueRun('bug', assessment, { findings: [finding()], nextActions: [action] });
  assert.equal(resultPresentation(run).bugAssessment?.status, 'confirmed');
  assert.equal(resultPresentation(run).bugAssessment?.reproduction.status, 'not_reproduced');
  assert.equal(currentRecommendation(run)?.kind, 'comment');
  assert.equal(currentRecommendation(run)?.planId, undefined);
});

test('Issue advice never substitutes a status-derived or legacy nextSteps action for canonical nextActions', () => {
  const action = proposal('comment', { reason: 'Discuss the ready plan before choosing implementation.' });
  const run = issueRun('feature', featureAssessment('ready'), {
    plans: [plan('feature-implement')], nextActions: [action],
    nextSteps: [proposal('start-task', { taskKind: 'feature-implement', planId: 'plan-feature-implement' })],
  });
  assert.equal(currentRecommendation(run)?.kind, 'comment', 'ready does not authorize or invent implementation');
  run.result.nextActions[0].recommended = false;
  assert.equal(currentRecommendation(run)?.kind, 'incomplete', 'a missing canonical default is not repaired from legacy nextSteps');
});

test('partial Issue reports retain their saved assessments without presenting them as final conclusions', () => {
  for (const kind of ['feature', 'bug']) {
    const assessment = kind === 'feature' ? featureAssessment('needs_information') : bugAssessment('needs_information');
    const run = issueRun(kind, assessment, { nextActions: [proposal('comment')] });
    run.result.report.complete = false; run.result.outcome = 'blocked';
    const saved = structuredClone(run);
    const presented = resultPresentation(run);
    assert.equal(presented.featureAssessment ?? null, null);
    assert.equal(presented.bugAssessment ?? null, null);
    assert.equal(currentRecommendation(run)?.kind, 'incomplete');
    assert.deepEqual(run, saved);
  }
});
