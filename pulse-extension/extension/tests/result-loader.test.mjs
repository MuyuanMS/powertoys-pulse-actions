import test from 'node:test';
import assert from 'node:assert/strict';
import { loadFullResult } from '../src/result-loader.ts';

const sha = 'a'.repeat(40);
// ResultPages.cs emits these Host-contract sections independently of the client allowlist.
const sectionPaths = [
  'findings', 'nextActions', 'validation', 'artifacts', 'verificationEvidence', 'diagnostics', 'review.suggestions', 'plans',
  'report.coverage', 'report.limitations', 'reviewConclusion.blockingUncertainties', 'e2eAssessment.scenarios', 'e2eAssessment.expectedResults', 'e2eAssessment.prerequisites', 'e2eAssessment.evidence',
  'featureAssessment.reasons', 'featureAssessment.evidence', 'featureAssessment.acceptanceCriteria', 'featureAssessment.questions', 'featureAssessment.alternatives',
  'bugAssessment.reasons', 'bugAssessment.evidence', 'bugAssessment.questions', 'bugAssessment.reproduction.steps', 'bugAssessment.reproduction.evidence',
];
let nextRun = 0;
const atPath = (result, path) => path.split('.').reduce((value, part) => value[part], result);
const setPath = (result, path, value) => {
  const parts = path.split('.'); const key = parts.pop();
  parts.reduce((parent, part) => parent[part], result)[key] = value;
};
function item(path, index) {
  const text = `${path} item ${index}: preserve the full evidence and exact order.`;
  switch (path) {
    case 'findings': return { id: `finding-${index}`, title: text, priority: index === 240 ? 'P0' : 'P2', confirmed: true, status: 'open', path: 'src/task.cs', line: index + 1, details: text, impact: text, trigger: text, rootCause: text, fixSuggestion: text, evidence: [text], feedback: { body: text, suggestionId: `suggestion-${index}` } };
    case 'nextActions': return { proposalId: `proposal-${index}`, kind: 'comment', recommended: index === 240, reason: text, body: `  ${text}  \n\n` };
    case 'validation': return { id: `check-${index}`, name: text, status: 'passed', required: true, details: text, evidence: [text] };
    case 'artifacts': return { label: text, path: `C:\\Tasks\\report-${index}.txt` };
    case 'verificationEvidence': return { id: `evidence-${index}`, source: 'ci', kind: 'build', status: 'passed', subject: 'original-pr', revisionSha: sha, summary: text, evidence: [text], runId: null };
    case 'diagnostics': return { code: `REVIEW_NOTE_${index}`, severity: 'warning', message: text, recovery: 'none' };
    case 'review.suggestions': return { id: `suggestion-${index}`, path: 'src/task.cs', line: index + 1, startLine: index + 1, side: 'RIGHT', body: text, replacement: `guard${index}();` };
    case 'plans': return { id: `plan-${index}`, kind: 'issue-fix', summary: text, steps: [text], acceptanceCriteria: [text], prerequisites: [text], evidence: [text] };
    default: return `${text}\nSecond line is retained.`;
  }
}
function fixture({ paths = ['findings'], total = 6, first = 2, fingerprint = `result-fingerprint-${nextRun + 1}` } = {}) {
  const result = {
    schemaVersion: 3, outcome: 'completed', structured: true, cliExitCode: 0, phase: 'reporting', summary: 'Complete saved report.',
    findings: [], nextActions: [], nextSteps: [], validation: [], artifacts: [], verificationEvidence: [], diagnostics: [], plans: [], blockers: [],
    report: { complete: true, rechecked: true, coverage: [], limitations: [] },
    review: { headSha: sha, body: 'The full review body is retained.', suggestions: [] },
    reviewConclusion: { status: 'inconclusive', summary: 'Recorded review uncertainties.', revisionSha: sha, blockingUncertainties: [] },
    e2eAssessment: { level: 'recommended', reason: 'Runtime merits verification.', question: 'Does the scenario work?', scenarios: [], expectedResults: [], prerequisites: [], evidence: [], readiness: 'ready' },
    featureAssessment: { status: 'ready', summary: 'Feature investigated.', reasons: [], evidence: [], acceptanceCriteria: [], questions: [], alternatives: [], relatedIssue: null, planId: null },
    bugAssessment: { status: 'confirmed', summary: 'Bug investigated.', reasons: [], evidence: [], questions: [], relatedIssue: null, planId: null, reproduction: { status: 'reproduced', revisionSha: sha, environment: 'Saved test environment', steps: [], expected: 'Expected behavior', observed: 'Observed behavior', evidence: [] } },
  };
  for (const path of paths) setPath(result, path, Array.from({ length: total }, (_, index) => item(path, index)));
  result.nextSteps = structuredClone(result.nextActions);
  const expected = structuredClone(result);
  const run = {
    runId: `00000000-0000-4000-8000-${String(++nextRun).padStart(12, '0')}`,
    task: { requestId: 'saved-request', actionId: 'review', actionKind: 'pr-review', repository: 'microsoft/PowerToys', target: { type: 'pr', number: 42 }, expectedHeadSha: sha, reviewOptions: { mode: 'static' }, prompt: 'Review the original revision.' },
    status: { state: 'succeeded', exitCode: 0 }, view: { read: false, handled: false }, result: structuredClone(result),
    resultPaging: { fingerprint, sections: paths.map(path => ({ path, total, nextOffset: first < total ? first : null })) },
  };
  for (const path of paths) setPath(run.result, path, atPath(result, path).slice(0, first));
  run.result.nextSteps = structuredClone(run.result.nextActions);
  const calls = [];
  const reader = async payload => {
    calls.push(structuredClone(payload));
    assert.equal(payload.runId, run.runId); assert.equal(payload.fingerprint, run.resultPaging.fingerprint);
    const rows = atPath(expected, payload.path); const end = Math.min(payload.offset + 61, rows.length);
    return { items: structuredClone(rows.slice(payload.offset, end)), nextOffset: end < rows.length ? end : null, total: rows.length, fingerprint: run.resultPaging.fingerprint };
  };
  return { run, expected, calls, reader };
}

test('all result sections and nested arrays retain every row beyond 225, including late findings and proposals', async () => {
  const { run, expected, calls, reader } = fixture({ paths: sectionPaths, total: 241, first: 2 });
  const original = structuredClone(run);
  const full = await loadFullResult(run, reader);
  assert.notEqual(full, run); assert.equal('resultPaging' in full, false);
  assert.deepEqual(full.result, expected); assert.deepEqual(run, original, 'loading never mutates the bounded source snapshot');
  assert.equal(full.result.findings[240].priority, 'P0'); assert.equal(full.result.nextActions[240].proposalId, 'proposal-240');
  assert.deepEqual(full.result.nextSteps, full.result.nextActions);
  assert.notEqual(full.result.nextSteps, full.result.nextActions);
  for (const path of sectionPaths) {
    assert.equal(atPath(full.result, path).length, 241, path);
    assert.deepEqual(calls.filter(call => call.path === path).map(call => call.offset), [2, 63, 124, 185], path);
  }
});

test('unpaged reports and complete initial sections do not issue page requests', async () => {
  const { run } = fixture(); delete run.resultPaging;
  assert.equal(await loadFullResult(run, async () => assert.fail('An unpaged report must not be read again.')), run);
  for (const total of [0, 3]) {
    const complete = fixture({ paths: ['findings', 'report.coverage', 'bugAssessment.reproduction.steps'], total, first: total });
    const full = await loadFullResult(complete.run, async () => assert.fail('The initial section already contains every row.'));
    assert.deepEqual(full.result, complete.expected); assert.equal('resultPaging' in full, false);
  }
});

test('mismatched fingerprints, totals and nonmonotonic or incomplete page cursors reject the whole reconstruction', async () => {
  const invalidPages = [
    ['changed fingerprint', page => ({ ...page, fingerprint: 'different-result' })],
    ['changed total', page => ({ ...page, total: page.total + 1 })],
    ['string total', page => ({ ...page, total: String(page.total) })],
    ['empty page', page => ({ ...page, items: [], nextOffset: 2 })],
    ['nonarray items', page => ({ ...page, items: {} })],
    ['backward cursor', page => ({ ...page, nextOffset: 1 })],
    ['repeated cursor', page => ({ ...page, nextOffset: 2 })],
    ['skipped cursor', page => ({ ...page, nextOffset: 5 })],
    ['fractional cursor', page => ({ ...page, nextOffset: 4.5 })],
    ['early end', page => ({ ...page, nextOffset: null })],
    ['overfull page', page => ({ ...page, items: Array.from({ length: 5 }, (_, index) => item('findings', index)), nextOffset: null })],
    ['missing response', () => undefined],
  ];
  for (const [name, change] of invalidPages) {
    const { run } = fixture(); const original = structuredClone(run); let reads = 0;
    await assert.rejects(loadFullResult(run, async () => {
      reads++;
      assert.equal(reads, 1, 'invalid pagination must stop before another read');
      return change({ fingerprint: run.resultPaging.fingerprint, total: 6, items: [item('findings', 2), item('findings', 3)], nextOffset: 4 });
    }), /report|result page|saved result/i, name);
    assert.deepEqual(run, original, name);
  }
});

test('invalid initial sections and unsupported or missing nested paths fail without exposing a complete report', async () => {
  const invalidSections = [
    run => { run.resultPaging.sections[0].total = -1; }, run => { run.resultPaging.sections[0].total = 2.5; },
    run => { run.resultPaging.sections[0].total = 1; }, run => { run.resultPaging.sections[0].nextOffset = null; },
    run => { run.resultPaging.sections[0].nextOffset = 1; }, run => { run.resultPaging.sections[0].nextOffset = 3; },
    run => { run.resultPaging.sections[0].nextOffset = undefined; }, run => { run.resultPaging.sections[0].path = 'unknown-section'; },
    run => { run.resultPaging.sections[0].path = '__proto__.findings'; }, run => { run.resultPaging.sections[0].path = 'constructor.prototype'; },
    run => { run.resultPaging.sections[0].path = 'review.suggestions'; run.result.review = null; },
    run => { run.resultPaging.sections[0].path = 'bugAssessment.reproduction.steps'; delete run.result.bugAssessment.reproduction; },
    run => { run.resultPaging.sections[0].path = 'report.coverage'; run.result.report.coverage = 'not an array'; },
    run => { run.resultPaging.fingerprint = ''; }, run => { run.resultPaging.sections = null; }, run => { delete run.result; },
  ];
  for (const change of invalidSections) {
    const { run } = fixture(); change(run); const original = structuredClone(run);
    await assert.rejects(loadFullResult(run, async () => assert.fail('Invalid initial metadata must be rejected before a read.')), /report|result|pagination/i);
    assert.deepEqual(run, original, change.toString());
  }
  const duplicate = fixture({ total: 2, first: 2 });
  duplicate.run.resultPaging.sections.push(structuredClone(duplicate.run.resultPaging.sections[0]));
  await assert.rejects(loadFullResult(duplicate.run, async () => assert.fail('Complete duplicated sections need no reads.')), /pagination/i);
});

test('an interrupted or unknown read after another section completed cannot return or cache a mixed report', async () => {
  for (const failure of [new Error('Unknown result section on this Host.'), Object.assign(new Error('The page could not be read.'), { code: 'RESULT_READ_FAILED' })]) {
    const { run, expected, reader } = fixture({ paths: ['findings', 'nextActions'], total: 6, first: 2 });
    const original = structuredClone(run); const failedCalls = []; let completed;
    await assert.rejects(async () => {
      completed = await loadFullResult(run, async payload => {
        failedCalls.push(structuredClone(payload));
        if (payload.path === 'nextActions') throw failure;
        return reader(payload);
      });
    }, error => error === failure);
    assert.equal(completed, undefined); assert.deepEqual(run, original);
    assert.deepEqual(failedCalls.map(call => call.path), ['findings', 'nextActions']);
    const retryCalls = [];
    const full = await loadFullResult(run, async payload => { retryCalls.push(structuredClone(payload)); return reader(payload); });
    assert.deepEqual(retryCalls.map(call => ({ path: call.path, offset: call.offset })), [{ path: 'findings', offset: 2 }, { path: 'nextActions', offset: 2 }], 'the failed attempt contributes no cached partial sections');
    assert.deepEqual(full.result, expected); assert.equal('resultPaging' in full, false);
  }
});

test('completed caches are isolated by fingerprint and cannot be poisoned through a returned report', async () => {
  const { run, expected, reader } = fixture({ paths: ['findings', 'nextActions'], total: 6, first: 2 });
  const first = await loadFullResult(run, reader);
  first.result.findings[5].title = 'Caller mutation must not enter the cache.';
  const refreshed = structuredClone(run);
  refreshed.result.nextActions[0].availability = { enabled: false, reasons: ['Current account cannot publish.'] };
  const cached = await loadFullResult(refreshed, async () => assert.fail('A completed same-fingerprint section should already be available.'));
  assert.equal(cached.result.findings[5].title, expected.findings[5].title);
  assert.deepEqual(cached.result.nextActions[0].availability, refreshed.result.nextActions[0].availability, 'current Host projection replaces its cached prefix');
  const changed = structuredClone(run); changed.resultPaging.fingerprint = 'new-saved-result';
  const calls = [];
  const full = await loadFullResult(changed, async payload => {
    calls.push(structuredClone(payload));
    const page = await reader({ ...payload, fingerprint: run.resultPaging.fingerprint });
    return { ...page, fingerprint: payload.fingerprint };
  });
  assert.deepEqual(calls.map(call => call.fingerprint), ['new-saved-result', 'new-saved-result']);
  assert.deepEqual(full.result, expected);
});
