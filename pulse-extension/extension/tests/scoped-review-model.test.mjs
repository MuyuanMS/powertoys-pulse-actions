import test from 'node:test';
import assert from 'node:assert/strict';
import { compatibleVerificationEvidence, resultActionAvailability, resultActionEligible, reviewModeLabel, scopedReviewConclusion } from '../src/details-model.ts';

const sha = 'a'.repeat(40);
const otherSha = 'b'.repeat(40);
const runId = '6c9f13d3-e925-4527-9e1b-4a7fcd2d2af2';
const priorRunId = '5ce3fe36-d6b0-430b-8246-668b41e4b3ef';
const requiredChecks = {
  static: ['context', 'local-review'],
  'build-tests': ['context', 'local-review', 'build-tests'],
  'ui-e2e': ['context', 'local-review', 'setup', 'e2e'],
};
const check = id => ({ id, name: id, required: true, status: 'passed', details: `Completed ${id}.`, evidence: [`Observed ${id} on ${sha}.`] });
const action = kind => ({ proposalId: `proposal-${kind}`, kind, reason: 'Use the recorded review.', body: kind === 'merge-pr' ? '' : 'The recorded review has no blocking findings.' });
const conclusion = overrides => ({ status: 'no-blocking-findings', summary: 'Reviewed the original PR revision.', revisionSha: sha, blockingUncertainties: [], ...overrides });
const evidence = overrides => ({ id: 'verified-build', source: 'ci', kind: 'build', status: 'passed', subject: 'original-pr', revisionSha: sha, summary: 'The PR build passed.', evidence: ['CI completed successfully on the reviewed SHA.'], runId: null, ...overrides });
const scopedRun = (mode = 'static', resultOverrides = {}) => ({
  runId,
  task: { requestId: runId, actionId: 'review-pr', actionKind: 'pr-review', repository: 'microsoft/PowerToys', target: { type: 'pr', number: 42 }, expectedHeadSha: sha, reviewOptions: { mode }, prompt: 'Review this PR.' },
  config: { agent: 'codex', repoFolder: 'C:\\Tasks\\review-pr', cliPath: 'codex.exe', permission: 'read-only' },
  status: { state: 'succeeded', exitCode: 0, createdAt: '2026-09-14T00:00:00Z', updatedAt: '2026-09-14T00:01:00Z', sequence: 2 },
  view: { read: false, handled: false },
  result: {
    schemaVersion: 2, structured: true, outcome: 'completed', cliExitCode: 0, phase: 'reporting', summary: 'The selected review is complete.',
    artifacts: [], blockers: [], diagnostics: [], findings: [], nextSteps: [], validation: requiredChecks[mode].map(check),
    review: { headSha: sha, body: 'No blocking findings on this revision.', suggestions: [] }, reviewConclusion: conclusion(),
    verificationEvidence: [], verificationRecommendation: null, nextActions: [action('approve'), action('merge-pr'), action('comment')],
    ...resultOverrides,
  },
});
const proposal = (run, kind) => run.result.nextActions.find(item => item.kind === kind);
const eligible = (run, kind = 'approve') => resultActionEligible(run, proposal(run, kind));

test('Host supplemental assessment can enable merge while the original inconclusive report stays immutable', () => {
  const run = scopedRun('static', { assessment: { subject: 'original-pr', status: 'inconclusive', summary: 'Original runtime coverage was limited.', revisionSha: sha } });
  const merge = proposal(run, 'merge-pr');
  assert.equal(eligible(run, 'merge-pr'), false, 'older Hosts without authoritative availability remain conservative');
  merge.availability = { enabled: true, reasons: [] };
  const saved = structuredClone(run);
  assert.equal(eligible(run, 'merge-pr'), true);
  assert.deepEqual(run, saved, 'the Host projection does not rewrite the saved assessment');
  merge.availability = { enabled: false, reasons: ['Supplemental verification is incomplete.'] };
  assert.equal(eligible(run, 'merge-pr'), false);
  merge.availability = { enabled: true, reasons: [] }; run.result.assessment.status = 'failed';
  assert.equal(eligible(run, 'merge-pr'), false, 'an actual failing original assessment still blocks');
});

test('review mode labels distinguish explicit modes and keep missing or malformed history unknown', () => {
  const labels = Object.keys(requiredChecks).map(mode => reviewModeLabel(mode));
  assert.equal(new Set(labels).size, 3);
  for (const label of labels) { assert.equal(typeof label, 'string'); assert.ok(label.trim()); assert.notEqual(label, 'Not recorded'); }
  for (const mode of [undefined, null, '', 'review', 'Static', 'toString', 'constructor']) assert.equal(reviewModeLabel(mode), 'Not recorded');
});

test('scoped conclusions retain every recorded verdict without substituting workflow or verification outcomes', () => {
  for (const mode of Object.keys(requiredChecks)) for (const status of ['no-blocking-findings', 'changes-requested', 'inconclusive']) {
    const run = scopedRun(mode, { outcome: 'blocked', reviewConclusion: conclusion({ status }), assessment: { status: 'inconclusive', subject: 'original-pr', revisionSha: sha, summary: 'Runtime was outside scope.' } });
    assert.equal(scopedReviewConclusion(run), run.result.reviewConclusion, `${mode}/${status}`);
    assert.equal(run.result.outcome, 'blocked', 'a separate review conclusion never rewrites saved workflow state');
  }
  const run = scopedRun(); run.result.reviewConclusion.revisionSha = sha.toUpperCase();
  assert.equal(scopedReviewConclusion(run), run.result.reviewConclusion, 'SHA comparison is case insensitive');
  delete run.result.reviewConclusion;
  assert.equal(scopedReviewConclusion(run), undefined, 'passed checks and a review draft do not invent a conclusion');
});

test('scoped conclusions require an explicit PR review scope, valid original SHA and successful recorded execution', () => {
  const invalid = [
    run => { delete run.task.reviewOptions; }, run => { run.task.reviewOptions.mode = 'unknown'; },
    run => { run.task.actionKind = 'pr-verify'; }, run => { run.task.actionKind = 'e2e'; },
    run => { run.task.target.type = 'issue'; }, run => { delete run.task.target; },
    run => { delete run.task.expectedHeadSha; }, run => { run.task.expectedHeadSha = 'short-sha'; },
    run => { run.result.reviewConclusion.revisionSha = otherSha; }, run => { run.result.reviewConclusion.revisionSha = null; },
    run => { run.result.schemaVersion = 1; }, run => { run.result.structured = false; },
    run => { delete run.status.exitCode; }, run => { run.status.exitCode = 1; },
    run => { run.status.error = { code: 'CLI_EXECUTION_FAILED', message: 'The CLI stopped.' }; },
    ...['accepted', 'running', 'failed', 'cancelled', 'interrupted'].map(state => run => { run.status.state = state; }),
  ];
  for (const change of invalid) { const run = scopedRun(); change(run); assert.equal(scopedReviewConclusion(run), undefined, change.toString()); }
});

test('malformed conclusions cannot become a trusted review verdict', () => {
  for (const overrides of [
    { status: 'passed' }, { status: 'toString' }, { summary: null }, { summary: ' ' }, { summary: 'x'.repeat(4097) }, { summary: 'review\0text' },
    { blockingUncertainties: null }, { blockingUncertainties: [''] }, { blockingUncertainties: [7] }, { blockingUncertainties: ['x'.repeat(4097)] },
    { blockingUncertainties: Array.from({ length: 51 }, () => 'Unresolved question') }, { extra: true },
  ]) assert.equal(Boolean(scopedReviewConclusion(scopedRun('static', { reviewConclusion: conclusion(overrides) }))), false, JSON.stringify(overrides).slice(0, 100));
});

test('verification evidence keeps attribution and only matches the exact original PR revision', () => {
  const rows = [
    evidence({ id: 'current-tests', source: 'current-run', kind: 'automated-tests' }),
    evidence({ id: 'ci-build' }),
    evidence({ id: 'author-runtime', source: 'author', kind: 'runtime' }),
    evidence({ id: 'prior-runtime', source: 'prior-run', kind: 'runtime', runId: priorRunId }),
    evidence({ id: 'prior-unlinked', source: 'prior-run' }),
    evidence({ id: 'upper-sha', revisionSha: sha.toUpperCase() }),
    evidence({ id: 'not-selected', status: 'not_run', evidence: [] }),
    evidence({ id: 'original-failure', status: 'failed' }),
  ];
  const run = scopedRun('ui-e2e', { verificationEvidence: [...rows,
    evidence({ id: 'different-revision', revisionSha: otherSha }),
    evidence({ id: 'candidate', subject: 'local-candidate' }),
    evidence({ id: 'unbound', revisionSha: null }),
  ] });
  assert.deepEqual(compatibleVerificationEvidence(run), rows);
  for (const expectedHeadSha of [undefined, null, 'short-sha']) { run.task.expectedHeadSha = expectedHeadSha; assert.deepEqual(compatibleVerificationEvidence(run), []); }
  run.task.expectedHeadSha = sha; run.task.target.type = 'issue';
  assert.deepEqual(compatibleVerificationEvidence(run), [], 'issue evidence is not original-PR evidence');
});

test('malformed evidence is excluded instead of being repaired or relabelled', () => {
  const invalid = [null, false, evidence({ id: '' }), evidence({ id: 'white space' }), evidence({ id: 'x'.repeat(65) }),
    evidence({ source: 'unattributed' }), evidence({ kind: 'verification' }), evidence({ status: 'inconclusive' }), evidence({ subject: 'target' }),
    evidence({ summary: null }), evidence({ summary: '' }), evidence({ summary: 'x'.repeat(4097) }), evidence({ summary: 'bad\0summary' }),
    evidence({ evidence: [] }), evidence({ evidence: null }), evidence({ evidence: [false] }), evidence({ evidence: [''] }), evidence({ evidence: ['bad\0evidence'] }),
    evidence({ evidence: ['x'.repeat(4097)] }), evidence({ evidence: Array.from({ length: 51 }, () => 'Observation') }),
    evidence({ runId: priorRunId }), evidence({ source: 'prior-run', runId: 'not-a-uuid' }), evidence({ source: 'prior-run', runId: 1 }),
    evidence({ runId: undefined }), evidence({ extra: 'unrecognized field' }),
  ];
  const run = scopedRun();
  for (const [index, row] of invalid.entries()) assert.equal(compatibleVerificationEvidence(run, [row]).length, 0, `malformed evidence case ${index}`);
  for (const rows of [undefined, null, {}, 'evidence']) {
    run.result.verificationEvidence = rows;
    assert.deepEqual(compatibleVerificationEvidence(run), []);
  }
});

test('related evidence input is filtered independently without mutating saved or supplied rows', () => {
  const own = evidence({ id: 'own' }); const related = evidence({ id: 'follow-up', source: 'prior-run', runId: priorRunId });
  const run = scopedRun('static', { verificationEvidence: [own] });
  const rows = [related, evidence({ id: 'different-parent', revisionSha: otherSha }), evidence({ id: 'local-patch', subject: 'local-candidate' })];
  const originalRun = structuredClone(run); const originalRows = structuredClone(rows);
  assert.deepEqual(compatibleVerificationEvidence(run, rows), [related]);
  assert.deepEqual(compatibleVerificationEvidence(run), [own]);
  assert.deepEqual(run, originalRun); assert.deepEqual(rows, originalRows);
  for (const state of ['accepted', 'running']) { run.status.state = state; assert.deepEqual(compatibleVerificationEvidence(run), [], state); }
  run.status.state = 'failed';
  assert.deepEqual(compatibleVerificationEvidence(run), [own], 'terminal execution failure does not erase retained attributable evidence');
});

test('a clean scoped code review allows approval with explicitly limited product verification while merge retains its assessment gate', () => {
  const run = scopedRun('static', { assessment: { status: 'inconclusive', subject: 'original-pr', revisionSha: sha, summary: 'No runtime claim was made.' } });
  assert.deepEqual(resultActionAvailability(run, proposal(run, 'approve')), { enabled: true, reasons: [] });
  assert.equal(eligible(run, 'merge-pr'), false);
  run.result.assessment.status = 'passed';
  assert.equal(eligible(run), true); assert.equal(eligible(run, 'merge-pr'), true);
  for (const assessment of [null, undefined]) {
    run.result.assessment = assessment;
    assert.equal(eligible(run), true); assert.equal(eligible(run, 'merge-pr'), true, 'absent optional assessments preserve existing compatibility behavior');
  }
});

test('selected review checks are complete and evidenced without accumulating unselected verification modes', () => {
  for (const mode of Object.keys(requiredChecks)) {
    const run = scopedRun(mode);
    assert.equal(eligible(run), true, mode);
    for (const id of requiredChecks[mode]) for (const field of ['missing', 'required', 'status', 'details', 'evidence']) {
      const changed = structuredClone(run);
      if (field === 'missing') changed.result.validation = changed.result.validation.filter(row => row.id !== id);
      else Object.assign(changed.result.validation.find(row => row.id === id), {
        required: { required: false }, status: { status: 'not_run' }, details: { details: '' }, evidence: { evidence: [] },
      }[field]);
      assert.equal(eligible(changed), false, `${mode}/${id}/${field}`);
    }
    for (const unselected of ['verification', 'build-tests', 'setup', 'e2e'].filter(id => !requiredChecks[mode].includes(id))) {
      const changed = structuredClone(run);
      changed.result.validation.push({ ...check(unselected), required: false, status: 'not_run', details: 'Outside the accepted scope.', evidence: [] });
      assert.equal(eligible(changed), true, `${mode} does not require ${unselected}`);
    }
  }
});

test('verification follow-ups use setup and only their selected checks without producing a code-review verdict', () => {
  for (const mode of ['build-tests', 'ui-e2e']) {
    const ids = mode === 'build-tests' ? ['setup', 'build-tests'] : ['setup', 'e2e'];
    const run = scopedRun(mode, { review: null, reviewConclusion: null, validation: ids.map(check), nextActions: [action('comment')] });
    run.task.actionKind = 'pr-verify';
    assert.equal(eligible(run, 'comment'), true, mode);
    assert.equal(scopedReviewConclusion(run), undefined);
    run.result.reviewConclusion = conclusion();
    assert.equal(scopedReviewConclusion(run), undefined, 'follow-up evidence cannot impersonate a new code-review verdict');
    for (const id of ids) {
      const changed = structuredClone(run); changed.result.validation = changed.result.validation.filter(row => row.id !== id);
      assert.equal(eligible(changed, 'comment'), false, `${mode}/${id}`);
    }
  }
});

test('approval cannot ignore unresolved conclusions, material findings or original-SHA verification failures', () => {
  const mutations = [
    run => { run.result.reviewConclusion = null; }, run => { run.result.reviewConclusion.status = 'changes-requested'; },
    run => { run.result.reviewConclusion.status = 'inconclusive'; }, run => { run.result.reviewConclusion.revisionSha = otherSha; },
    run => { run.result.reviewConclusion.blockingUncertainties = ['The null path may violate the API contract.']; },
    ...['high', 'medium'].flatMap(severity => ['open', 'unverified'].map(status => run => { run.result.findings = [{ id: 'remaining', title: 'Guard needed', severity, status, path: 'src/a.cs', line: 4, details: 'The original code is unsafe.', evidence: ['The reviewed SHA dereferences null.'] }]; })),
    run => { run.result.verificationEvidence = [evidence({ status: 'failed' })]; },
    run => { run.result.verificationEvidence = [evidence({ status: 'failed', extra: true })]; },
    run => { run.result.verificationEvidence = [{ subject: 'original-pr', revisionSha: sha, status: 'failed' }]; },
  ];
  for (const change of mutations) {
    const run = scopedRun(); change(run);
    for (const kind of ['approve', 'merge-pr']) {
      const availability = resultActionAvailability(run, proposal(run, kind));
      assert.equal(availability.enabled, false, `${kind}/${change.toString()}`);
      assert.ok(availability.reasons.length, 'a disabled saved proposal retains an actionable explanation');
    }
  }
  for (const status of ['fixed', 'open']) {
    const run = scopedRun('static', { findings: [{ id: 'nonblocking', title: 'Minor cleanup', severity: status === 'fixed' ? 'high' : 'low', status, path: 'src/a.cs', line: 4, details: 'No remaining material issue.', evidence: ['Reviewed.'] }] });
    assert.equal(eligible(run), true, `${status} does not automatically block scoped approval`);
  }
});

test('assessment and evidence from a local candidate or another revision cannot justify original-PR approval', () => {
  for (const assessment of [
    { status: 'failed', subject: 'original-pr', revisionSha: sha },
    { status: 'inconclusive', subject: 'local-candidate', revisionSha: sha },
    { status: 'passed', subject: 'local-candidate', revisionSha: sha },
    { status: 'passed', subject: 'original-pr', revisionSha: otherSha },
    { status: 'inconclusive', subject: 'original-pr', revisionSha: null },
  ]) assert.equal(eligible(scopedRun('static', { assessment: { summary: 'Recorded assessment.', ...assessment } })), false, JSON.stringify(assessment));
  const run = scopedRun('static', { reviewConclusion: conclusion({ status: 'inconclusive' }), verificationEvidence: [evidence({ subject: 'local-candidate' }), evidence({ revisionSha: otherSha })] });
  assert.equal(eligible(run), false, 'passing unrelated evidence never supplies the missing code-review conclusion');
  run.result.reviewConclusion = conclusion();
  run.result.verificationEvidence = [evidence({ subject: 'local-candidate', status: 'failed' }), evidence({ revisionSha: otherSha, status: 'failed' })];
  assert.equal(eligible(run), true, 'unrelated failures do not become failures of this exact original revision');
});

test('Host-enabled scoped proposals still require the saved revision, verdict, workflow checks and original evidence to agree', () => {
  const run = scopedRun();
  for (const row of run.result.nextActions) row.availability = { enabled: true, reasons: [] };
  assert.equal(eligible(run), true); assert.equal(eligible(run, 'merge-pr'), true);
  for (const change of [
    changed => { changed.result.reviewConclusion = null; },
    changed => { changed.result.reviewConclusion.status = 'inconclusive'; },
    changed => { changed.result.reviewConclusion.revisionSha = otherSha; },
    changed => { changed.result.review = null; },
    changed => { changed.result.review.headSha = otherSha; },
    changed => { changed.result.validation = []; },
    changed => { changed.result.outcome = 'blocked'; },
    changed => { changed.result.verificationEvidence = [evidence({ status: 'failed', extra: true })]; },
  ]) {
    const changed = structuredClone(run); change(changed);
    for (const kind of ['approve', 'merge-pr']) assert.equal(eligible(changed, kind), false, `${kind}/${change.toString()}`);
  }
  const blocked = proposal(run, 'approve'); blocked.availability = { enabled: false, reasons: ['The Host requires the current reviewer account.'] };
  assert.deepEqual(resultActionAvailability(run, blocked), blocked.availability, 'valid local evidence never overrides a Host refusal');
});

test('verification recommendations do not grant or revoke a saved scoped code-review verdict', () => {
  const recommendation = { mode: 'ui-e2e', reason: 'Runtime behavior merits a separate check.', question: 'Does startup succeed?', scenarios: ['Start the application.'], prerequisites: ['Interactive desktop'], evidence: [], readiness: 'missing-prerequisites' };
  const run = scopedRun('static', { verificationRecommendation: recommendation });
  assert.equal(eligible(run), true);
  run.result.reviewConclusion.status = 'inconclusive';
  run.result.verificationRecommendation.readiness = 'ready';
  assert.equal(eligible(run), false);
});

test('unrecorded review scope retains historical verification and assessment gates', () => {
  const run = scopedRun(); delete run.task.reviewOptions;
  assert.equal(scopedReviewConclusion(run), undefined);
  assert.equal(eligible(run), false, 'historical review still requires its verification check');
  run.result.validation.push(check('verification'));
  assert.equal(eligible(run), true);
  run.result.assessment = { status: 'inconclusive', subject: 'original-pr', revisionSha: sha, summary: 'Historical verification is inconclusive.' };
  assert.equal(eligible(run), false, 'historical approval does not adopt the new scoped assessment exception');
  run.task.reviewOptions = { mode: 'unknown' };
  assert.equal(scopedReviewConclusion(run), undefined); assert.equal(eligible(run), false);
});
