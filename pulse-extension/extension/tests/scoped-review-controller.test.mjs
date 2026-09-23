import test from 'node:test';
import assert from 'node:assert/strict';
import { setImmediate as nextTurn } from 'node:timers/promises';

class Node {
  children = []; listeners = new Map(); attributes = new Map(); parent = undefined; hidden = false; disabled = false; checked = false; value = ''; className = ''; _text = '';
  constructor(tag) { this.tagName = tag.toUpperCase(); }
  set textContent(value) { this._text = String(value); this.children = []; }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  append(...children) { for (const child of children) { child.parent = this; this.children.push(child); } }
  replaceChildren(...children) { this._text = ''; this.children = []; this.append(...children); }
  setAttribute(key, value) { this.attributes.set(key, value); }
  addEventListener(type, listener) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]); }
  async fire(type) { for (const listener of this.listeners.get(type) ?? []) listener({ preventDefault() {} }); await nextTurn(); }
  showModal() { this.open = true; }
  focus() {}
  close() { this.open = false; for (const listener of this.listeners.get('close') ?? []) listener(); }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); }
}
const descendants = node => [node, ...node.children.flatMap(descendants)];
const sha = 'a'.repeat(40), recommendationId = 'b'.repeat(64);
const parentId = '10000000-0000-4000-8000-000000000001', childId = '20000000-0000-4000-8000-000000000002', acceptedId = '30000000-0000-4000-8000-000000000003';
let imports = 0;
const evidence = overrides => ({ id: 'ci-build', source: 'ci', kind: 'build', status: 'passed', subject: 'original-pr', revisionSha: sha, summary: 'CI verified this exact PR revision.', evidence: ['CI completed on the saved commit.'], runId: null, ...overrides });
function reviewRun() {
  return {
    runId: parentId,
    task: { actionKind: 'pr-review', actionId: 'review', repository: 'microsoft/PowerToys', target: { type: 'pr', number: 50493 }, expectedHeadSha: sha, reviewOptions: { mode: 'static' }, prompt: 'Preserve the original review.' },
    config: { agent: 'codex', model: 'saved-model', reasoningEffort: 'low', cliPath: 'codex.exe', repoFolder: 'C:\\Review\\saved', permission: 'read-only' },
    status: { state: 'succeeded', exitCode: 0, createdAt: '2026-09-14T01:00:00Z', updatedAt: '2026-09-14T01:00:22Z', sequence: 0 }, view: { read: true, handled: false },
    result: { schemaVersion: 2, structured: true, outcome: 'completed', phase: 'reporting', summary: 'Static review finished.', findings: [], diagnostics: [], validation: [], artifacts: [], blockers: [], nextSteps: [], nextActions: [],
      reviewConclusion: { status: 'no-blocking-findings', summary: 'Recorded code conclusion stays unchanged.', revisionSha: sha, blockingUncertainties: [] },
      verificationEvidence: [], verificationRecommendation: { mode: 'build-tests', reason: 'Old model recommendation', question: 'Old question', scenarios: ['Old scenario'], prerequisites: [], evidence: [], readiness: 'unknown' },
    },
  };
}
function verificationReceipt(parent, payload, mode = 'ui-e2e') {
  return {
    runId: acceptedId,
    task: { ...structuredClone(parent.task), requestId: payload.requestId, actionKind: 'pr-verify', reviewOptions: { mode },
      followUp: { parentRunId: payload.parentRunId, recommendationId: payload.recommendationId, subject: 'original-pr', revisionSha: parent.task.expectedHeadSha },
      ...(payload.execution ? { execution: structuredClone(payload.execution) } : {}),
    },
    config: structuredClone(parent.config),
    status: { state: 'accepted', createdAt: '2026-09-14T02:00:00Z', updatedAt: '2026-09-14T02:00:00Z', sequence: 0 },
    view: { read: false, handled: false },
  };
}
const related = (readiness = 'ready') => ({
  parentRunId: parentId, runs: [], evidence: [],
  currentConclusion: { status: 'unchanged', summary: 'Related evidence has not changed the recorded code conclusion.' },
  recommendation: { recommendationId, mode: 'ui-e2e', reason: 'Check the affected runtime behavior.', question: 'Does the shortcut preserve focus?', scenarios: ['Open and close the shortcut dialog.'], prerequisites: ['A desktop session is available.'], evidence: ['The diff changes focus restoration.'], readiness },
});

async function withController(run, value, verify, { start, defaultsFailure = false } = {}) {
  const originals = new Map(['document', 'chrome', 'sessionStorage'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  const body = new Node('body'); const error = new Node('p'); error.id = 'error'; error.hidden = true; body.append(error);
  const original = structuredClone(run); const storage = new Map(); let controller;
  const page = {
    body, calls: [], tabs: [], storage, related: structuredClone(value), container: undefined,
    defaults: { defaultAgent: 'copilot', defaults: { codex: { model: 'codex-default', reasoningEffort: 'high' }, copilot: { model: 'copilot-default', reasoningEffort: 'max' } }, reasoningEfforts: { codex: ['low', 'medium', 'high', 'xhigh', 'ultra'], copilot: ['low', 'medium', 'high', 'max'] } },
    node: id => descendants(body).find(node => node.id === id),
    button: text => descendants(body).find(node => node.tagName === 'BUTTON' && node.textContent === text),
    async click(id) { const node = page.node(id); assert.ok(node, id); assert.equal(node.disabled, false, id); if (node.type === 'submit') { let parent = node.parent; while (parent.tagName !== 'FORM') parent = parent.parent; await parent.fire('submit'); } else await node.fire('click'); },
    async change(id, value) { const node = page.node(id); assert.ok(node, id); assert.equal(node.disabled, false, id); node.value = value; await node.fire('change'); },
    async confirmPrerequisites() { const node = page.node('verification-prerequisites-confirmed'); assert.ok(node); assert.equal(node.disabled, false); node.checked = true; await node.fire('change'); },
    async refresh() { await controller.refresh(); await nextTurn(); },
    async prepare() { await controller.openPreparation(); await nextTurn(); assert.equal(page.node('verification-preparation-dialog')?.open, true); },
    async closePreparation() { page.node('verification-preparation-dialog')?.close(); await nextTurn(); },
    async reload() {
      await page.closePreparation();
      page.container?.remove(); page.container = new Node('section'); body.append(page.container);
      const source = await import(new URL(`../src/review-decision.ts?scoped-controller-test=${++imports}`, import.meta.url));
      controller = source.scopedReviewController(page.container); controller.render(run); await nextTurn();
    },
  };
  try {
    globalThis.document = { body, createElement: tag => new Node(tag), getElementById: page.node };
    globalThis.sessionStorage = { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) };
    globalThis.chrome = {
      tabs: { create: async tab => { page.tabs.push(structuredClone(tab)); return {}; } },
      runtime: { getURL: path => `chrome-extension://test/${path}`, sendMessage: async message => {
        page.calls.push(structuredClone(message)); assert.equal(message.channel, 'pulse-ui');
        if (message.type === 'reviews.related') { assert.deepEqual(message.payload, { runId: run.runId }); return { ok: true, data: structuredClone(page.related) }; }
        if (message.type === 'agents.defaults') {
          if (defaultsFailure) throw new Error('Simulated execution defaults unavailable.');
          return { ok: true, data: structuredClone(page.defaults) };
        }
        if (message.type === 'reviews.verify') { assert.ok(start, 'verification requires an explicit enabled fixture'); return await start(message.payload); }
        throw new Error(`Unexpected operation ${message.type}; this fixture cannot publish or retarget a review.`);
      } },
    };
    await page.reload(); await verify(page);
    assert.deepEqual(run, original, 'related verification never rewrites the original result, target or scope');
    assert.equal(page.calls.some(call => /operations\.|webActions\.|reviewDecision|targets\.get|tasks\.rerun/.test(call.type)), false);
  } finally {
    for (const [key, descriptor] of originals) { if (descriptor) Object.defineProperty(globalThis, key, descriptor); else delete globalThis[key]; }
  }
}

test('scoped review separates the immutable code conclusion from compatible related evidence and verification-only children', async () => {
  const run = reviewRun();
  run.result.verificationEvidence = [evidence(), evidence({ id: 'candidate', subject: 'local-candidate', summary: 'Candidate-only evidence.' }), evidence({ id: 'wrong-sha', revisionSha: 'c'.repeat(40), summary: 'Different-revision evidence.' })];
  const value = related(); value.recommendation = null;
  value.runs = [
    { runId: childId, status: { state: 'succeeded' }, outcome: 'completed', summary: 'Related runtime completed.', verificationEvidence: [], recommendationId, compatibility: { eligible: true, reason: null } },
    { runId: acceptedId, status: { state: 'succeeded' }, outcome: 'completed', summary: 'Unrelated candidate run.', verificationEvidence: [], recommendationId, compatibility: { eligible: false, reason: 'Candidate evidence does not verify this PR.' } },
  ];
  value.evidence = [evidence({ id: 'related-runtime', source: 'prior-run', kind: 'runtime', runId: childId, summary: 'Accepted related runtime evidence.' }), evidence({ id: 'incompatible-child', source: 'prior-run', runId: acceptedId, summary: 'Excluded incompatible child.' }), evidence({ id: 'related-wrong-sha', source: 'prior-run', runId: childId, revisionSha: 'c'.repeat(40), summary: 'Excluded wrong revision.' })];
  await withController(run, value, async page => {
    assert.match(page.container.textContent, /Selected scopeStatic code review/);
    assert.match(page.container.textContent, /Recorded code conclusionNo blocking findingsRecorded code conclusion stays unchanged/);
    assert.match(page.container.textContent, /Current conclusion with related verificationRelated evidence has not changed/);
    const rows = descendants(page.container).filter(node => node.className === 'scoped-review-evidence');
    assert.equal(rows.length, 2); assert.match(rows[0].textContent, /CI · Build.*CI verified this exact PR revision/); assert.match(rows[1].textContent, /Related run · Runtime.*Accepted related runtime evidence/);
    assert.match(page.container.textContent, /Other recorded evidence.*Candidate-only evidence.*Different-revision evidence/);
    assert.equal(descendants(rows[1]).find(node => node.tagName === 'A').href, `chrome-extension://test/details.html?runId=${childId}`);
    assert.equal(page.calls.some(call => call.type === 'reviews.verify'), false);
  });
  const child = reviewRun(); child.runId = childId; child.task.actionKind = 'pr-verify'; child.task.reviewOptions.mode = 'ui-e2e';
  child.task.followUp = { parentRunId: parentId, recommendationId, subject: 'original-pr', revisionSha: sha }; child.result.reviewConclusion = null;
  await withController(child, value, async page => {
    assert.match(page.container.textContent, /Verification scope.*does not replace the code conclusion/);
    assert.doesNotMatch(page.container.textContent, /Recorded code conclusion/);
    assert.equal(descendants(page.container).find(node => node.tagName === 'A' && node.textContent === 'Open parent review').href, `chrome-extension://test/details.html?runId=${parentId}`);
  });
});

test('missing and unknown verification prerequisites require explicit confirmation and carry selected CLI, model and effort', async () => {
  for (const readiness of ['missing-prerequisites', 'unknown']) {
    const run = reviewRun(); const value = related(readiness); const requests = [];
    await withController(run, value, async page => {
      assert.equal(page.node('start-review-verification'), undefined);
      await page.click('review-verification-requirements');
      assert.equal(page.calls.some(call => call.type === 'reviews.verify'), false);
      assert.equal(page.node('start-review-verification').disabled, true); assert.equal(page.node('verification-prerequisites-confirmed').checked, false);
      assert.match(page.container.textContent, /Does the shortcut preserve focus/); assert.doesNotMatch(page.container.textContent, /Old model recommendation|Old question/);
      assert.equal(page.node('verify-execution-mode').value, 'defaults'); assert.equal(page.node('verify-agent').disabled, true);
      await page.change('verify-execution-mode', 'custom'); await page.change('verify-agent', 'codex');
      assert.equal(page.node('verify-model').value, 'codex-default'); assert.equal(page.node('verify-effort').children.some(option => option.value === 'max'), false);
      page.node('verify-model').value = 'focused-model'; await page.change('verify-effort', 'high');
      assert.equal(requests.length, 0); assert.equal(page.node('start-review-verification').disabled, true);
      await page.confirmPrerequisites(); assert.equal(page.node('start-review-verification').disabled, false); assert.equal(requests.length, 0);
      await page.click('start-review-verification');
      assert.equal(requests.length, 1); const request = requests[0];
      assert.deepEqual(Object.keys(request).sort(), ['execution', 'parentRunId', 'prerequisitesConfirmed', 'recommendationId', 'requestId']);
      assert.equal(request.parentRunId, parentId); assert.equal(request.recommendationId, recommendationId); assert.equal(request.prerequisitesConfirmed, true);
      assert.deepEqual(request.execution, { agent: 'codex', model: 'focused-model', reasoningEffort: 'high' });
      assert.match(request.requestId, /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i);
      assert.deepEqual(page.tabs, [{ url: `chrome-extension://test/details.html?runId=${acceptedId}` }]); assert.equal(page.storage.size, 0);
    }, { start: async payload => { requests.push(structuredClone(payload)); return { ok: true, data: verificationReceipt(run, payload) }; } });
  }
});

test('ready verification still opens preparation first and keeps an unsent draft across close and reload', async () => {
  const run = reviewRun(); const requests = [];
  await withController(run, related('ready'), async page => {
    assert.equal(page.node('start-review-verification'), undefined);
    await page.prepare();
    assert.ok(page.body.textContent.includes(sha)); assert.ok(page.body.textContent.includes('PR #50493'));
    assert.equal(page.node('start-review-verification').disabled, true);
    await page.change('verify-execution-mode', 'custom'); page.node('verify-model').value = 'retained-review-model';
    await page.confirmPrerequisites(); await page.closePreparation(); await page.reload(); await page.prepare();
    assert.equal(page.node('verify-model').value, 'retained-review-model'); assert.equal(page.node('verification-prerequisites-confirmed').checked, true);
    assert.equal(requests.length, 0, 'opening and reviewing requirements never starts a task');
    await page.click('start-review-verification');
    assert.equal(requests.length, 1); assert.equal(requests[0].execution.model, 'retained-review-model');
    assert.equal('prerequisitesConfirmed' in requests[0], false, 'ready recommendations retain the existing Host payload contract');
    assert.equal(page.storage.size, 0);
  }, { start: async payload => { requests.push(structuredClone(payload)); return { ok: true, data: verificationReceipt(run, payload) }; } });
});

test('pending verification displays its saved scenarios and recovers when the current recommendation disappears', async () => {
  const run = reviewRun(); const requests = [];
  await withController(run, related('unknown'), async page => {
    await page.prepare(); await page.confirmPrerequisites(); await page.click('start-review-verification');
    const first = structuredClone(requests[0]);
    page.related.recommendation = null; await page.reload(); await page.prepare();
    assert.match(page.node('verification-preparation-dialog').textContent, /Open and close the shortcut dialog/);
    assert.equal(page.node('start-review-verification').textContent, 'Recover verification request');
    assert.equal(page.node('verification-prerequisites-confirmed').disabled, true);
    await page.click('start-review-verification');
    assert.deepEqual(requests, [first, first]); assert.equal(page.tabs.length, 1); assert.equal(page.storage.size, 0);
  }, { start: async payload => { requests.push(structuredClone(payload)); if (requests.length === 1) throw new Error('Lost acknowledgement.'); return { ok: true, data: verificationReceipt(run, payload) }; } });
});

test('an unknown verification response freezes recommendation, prerequisites and execution across reload until the same UUID is recovered', async () => {
  const run = reviewRun(); const requests = [];
  await withController(run, related('unknown'), async page => {
    await page.prepare();
    await page.change('verify-execution-mode', 'custom'); await page.change('verify-agent', 'codex'); page.node('verify-model').value = 'frozen-model'; await page.change('verify-effort', 'high');
    await page.confirmPrerequisites(); await page.click('start-review-verification');
    assert.match(page.container.textContent, /Simulated verification response lost/); assert.equal(page.tabs.length, 0);
    const first = structuredClone(requests[0]);
    for (const id of ['verify-execution-mode', 'verify-agent', 'verify-model', 'verify-effort', 'verification-prerequisites-confirmed']) assert.equal(page.node(id).disabled, true);
    assert.equal(page.node('start-review-verification').textContent, 'Recover verification request');
    page.defaults.defaultAgent = 'copilot'; page.defaults.defaults.codex.model = 'new-default'; page.related.recommendation.recommendationId = 'd'.repeat(64);
    await page.reload();
    await page.prepare();
    assert.equal(page.node('verify-model').value, 'frozen-model'); assert.equal(page.node('verify-agent').value, 'codex'); assert.equal(page.node('verify-effort').value, 'high');
    assert.equal(page.node('verify-execution-mode').disabled, true); assert.equal(page.node('verification-prerequisites-confirmed').checked, true);
    await page.click('start-review-verification');
    assert.deepEqual(requests, [first, first]); assert.equal(page.tabs.length, 1); assert.equal(page.storage.size, 0);
    await page.prepare(); await page.confirmPrerequisites(); await page.click('start-review-verification');
    assert.notEqual(requests[2].requestId, first.requestId); assert.equal(requests[2].recommendationId, 'd'.repeat(64));
    assert.deepEqual(page.tabs, [{ url: `chrome-extension://test/details.html?runId=${acceptedId}` }, { url: `chrome-extension://test/details.html?runId=${acceptedId}` }]);
  }, { start: async payload => { requests.push(structuredClone(payload)); if (requests.length === 1) throw new Error('Simulated verification response lost'); return { ok: true, data: verificationReceipt(run, payload) }; } });
});

test('mismatched verification receipts and request conflicts keep the pending UUID until a matching task is recovered', async () => {
  const run = reviewRun(); const requests = []; let rejection;
  const mismatches = [
    ['missing task', receipt => { delete receipt.task; }],
    ['request identity', receipt => { receipt.task.requestId = childId; }],
    ['action kind', receipt => { receipt.task.actionKind = 'pr-review'; }],
    ['parent review', receipt => { receipt.task.followUp.parentRunId = childId; }],
    ['recommendation', receipt => { receipt.task.followUp.recommendationId = 'c'.repeat(64); }],
    ['verification subject', receipt => { receipt.task.followUp.subject = 'local-candidate'; }],
    ['verification revision', receipt => { receipt.task.followUp.revisionSha = 'c'.repeat(40); }],
    ['scope', receipt => { receipt.task.reviewOptions.mode = 'build-tests'; }],
    ['revision', receipt => { receipt.task.expectedHeadSha = 'c'.repeat(40); }],
    ['repository', receipt => { receipt.task.repository = 'other/repository'; }],
    ['target', receipt => { receipt.task.target.number++; }],
    ['request conflict', 'REQUEST_CONFLICT'],
  ];
  await withController(run, related('ready'), async page => {
    for (const [name, change] of mismatches) {
      rejection = change; const before = page.tabs.length;
      await page.prepare(); await page.confirmPrerequisites();
      await page.click('start-review-verification');
      const first = structuredClone(requests.at(-1));
      assert.equal(page.tabs.length, before, `${name}: an unverified receipt cannot open a task tab`);
      assert.equal(page.node('start-review-verification').textContent, 'Recover verification request', name);
      assert.equal(page.node('verify-execution-mode').disabled, true, name);
      assert.equal(page.storage.size, 1, `${name}: the original request must remain recoverable`);
      const saved = JSON.parse([...page.storage.values()][0]);
      assert.equal(saved.requestId, first.requestId, name); assert.equal(saved.acceptedRunId, undefined, name);
      rejection = undefined; await page.click('start-review-verification');
      assert.deepEqual(requests.at(-1), first, `${name}: recovery must reuse every request field`);
      assert.equal(page.tabs.length, before + 1); assert.equal(page.storage.size, 0);
      assert.equal(page.tabs.at(-1).url, `chrome-extension://test/details.html?runId=${acceptedId}`);
    }
  }, { start: async payload => {
    requests.push(structuredClone(payload));
    if (rejection === 'REQUEST_CONFLICT') return { ok: false, error: { code: 'REQUEST_CONFLICT', message: 'Simulated request conflict.' } };
    const receipt = verificationReceipt(run, payload); if (rejection) rejection(receipt);
    return { ok: true, data: receipt };
  } });
});

test('independently paged compatible evidence resolves the recommendation and repeating verification remains an explicit secondary action', async () => {
  const run = reviewRun(); const value = related('ready'); const requests = [];
  value.totalCount = 76; value.truncated = true;
  value.currentConclusion = { status: 'evidence-added', canSupplementAssessment: true, failedEvidence: null, summary: 'Compatible verification has resolved the requested evidence gap.' };
  value.runs = [{ runId: acceptedId, status: { state: 'succeeded' }, outcome: 'completed', summary: 'Explicitly incompatible run.', verificationEvidence: [], recommendationId, compatibility: { eligible: false, reason: 'This child is not compatible with the parent review.' } }];
  value.evidence = [
    evidence({ id: 'projected-runtime', source: 'prior-run', kind: 'runtime', runId: childId, summary: 'Compatible evidence whose metadata is on another page.' }),
    evidence({ id: 'explicitly-ineligible', source: 'prior-run', runId: acceptedId, summary: 'This evidence has explicitly ineligible metadata.' }),
  ];
  await withController(run, value, async page => {
    const rows = descendants(page.container).filter(node => node.className === 'scoped-review-evidence');
    assert.equal(rows.length, 1); assert.match(rows[0].textContent, /Compatible evidence whose metadata is on another page/);
    assert.doesNotMatch(rows[0].textContent, /explicitly ineligible metadata/);
    assert.match(page.container.textContent, /76/); assert.match(page.container.textContent, /truncat|partial|showing|limited/i);
    assert.match(page.container.textContent, /Recorded code conclusion stays unchanged/);
    const open = descendants(page.container).find(node => ['A', 'BUTTON'].includes(node.tagName) && node.textContent === 'Open verification result');
    assert.ok(open, 'resolved verification defaults to opening its existing result');
    if (open.tagName === 'A') assert.equal(open.href, `chrome-extension://test/details.html?runId=${childId}`);
    else { await open.fire('click'); assert.equal(page.tabs.at(-1).url, `chrome-extension://test/details.html?runId=${childId}`); }
    const initialStart = page.node('start-review-verification');
    assert.ok(!initialStart || initialStart.hidden || initialStart.disabled, 'a resolved recommendation does not immediately offer another run');
    assert.equal(requests.length, 0);
    const repeat = page.button('Review a repeat verification'); assert.ok(repeat); assert.equal(repeat.disabled, false);
    await repeat.fire('click'); assert.equal(requests.length, 0, 'reviewing repeat options is not execution');
    assert.equal(page.node('start-review-verification').disabled, true); await page.confirmPrerequisites();
    await page.click('start-review-verification'); assert.equal(requests.length, 1);
    assert.equal(requests[0].parentRunId, parentId); assert.equal(requests[0].recommendationId, recommendationId);
  }, { start: async payload => { requests.push(structuredClone(payload)); return { ok: true, data: verificationReceipt(run, payload) }; } });
});

test('execution defaults failure disables new verification while preserving related evidence and the current conclusion', async () => {
  const run = reviewRun(); const value = related('ready');
  value.currentConclusion = { status: 'evidence-added', canSupplementAssessment: false, summary: 'Related evidence remains visible without local execution defaults.' };
  value.runs = [{ runId: childId, status: { state: 'succeeded' }, outcome: 'completed', summary: 'Recorded runtime evidence.', verificationEvidence: [], recommendationId, compatibility: { eligible: true, reason: null } }];
  value.evidence = [evidence({ id: 'available-evidence', source: 'prior-run', kind: 'runtime', runId: childId, summary: 'Saved compatible runtime evidence remains available.' })];
  await withController(run, value, async page => {
    assert.match(page.container.textContent, /Recorded code conclusion stays unchanged/);
    assert.match(page.container.textContent, /Related evidence remains visible without local execution defaults/);
    const rows = descendants(page.container).filter(node => node.className === 'scoped-review-evidence');
    assert.equal(rows.length, 1); assert.match(rows[0].textContent, /Saved compatible runtime evidence remains available/);
    assert.match(page.container.textContent, /Simulated execution defaults unavailable/);
    await page.prepare();
    assert.equal(page.node('start-review-verification').disabled, true);
    assert.equal(page.button('Refresh related evidence').disabled, false);
    assert.equal(page.calls.filter(call => call.type === 'reviews.related').length, 1);
    assert.equal(page.calls.some(call => call.type === 'reviews.verify'), false);
  }, { defaultsFailure: true });
});

function v3Review(level = 'required') {
  const run = reviewRun(); run.result.schemaVersion = 3; delete run.result.verificationRecommendation;
  run.result.report = { complete: true, rechecked: true, coverage: ['All changed paths and their callers were reviewed.'], limitations: [] };
  run.result.e2eAssessment = { level, reason: level === 'not_needed' ? 'Pure function behavior is covered by the recorded unit-test evidence.' : 'Focus restoration needs runtime evidence.', question: level === 'not_needed' ? '' : 'Does focus return to the original window?', scenarios: level === 'not_needed' ? [] : ['Open and close the shortcut dialog.'], expectedResults: level === 'not_needed' ? [] : ['The original window regains focus.'], prerequisites: [], evidence: ['Exact revision source and unit tests reviewed.'], readiness: 'ready' };
  return run;
}

test('v3 explicitly distinguishes all E2E necessity levels without inventing a legacy assessment', async () => {
  for (const level of ['not_needed', 'recommended', 'required']) {
    const run = v3Review(level); const value = related(); value.recommendation = { ...value.recommendation, ...run.result.e2eAssessment };
    await withController(run, value, async page => {
      assert.match(page.container.textContent, /E2E verification assessment/);
      assert.match(page.container.textContent, new RegExp(level === 'not_needed' ? 'Not needed' : level === 'recommended' ? 'Recommended' : 'Required'));
      if (level === 'not_needed') { assert.equal(page.node('start-review-verification'), undefined); assert.match(page.container.textContent, /Pure function behavior/); }
      else { assert.equal(page.node('start-review-verification'), undefined); await page.prepare(); assert.equal(page.node('start-review-verification').disabled, true); assert.match(page.container.textContent, /Expected: The original window regains focus/); }
      assert.equal(page.calls.some(call => call.type === 'reviews.verify'), false);
    });
  }
  const old = reviewRun(); delete old.task.reviewOptions;
  await withController(old, { ...related(), recommendation: null }, async page => {
    assert.match(page.container.textContent, /historical run did not record its scope/); assert.doesNotMatch(page.container.textContent, /Not needed|E2E verification assessment/);
  });
});

test('v3 coverage uses the Host all-scenario conclusion and labels current execution separately from cited evidence', async () => {
  for (const origin of ['currentRunEvidenceComplete', 'attributedEvidenceComplete', 'child']) {
    const run = v3Review(); const value = related(); value.recommendation = { ...value.recommendation, ...run.result.e2eAssessment };
    value.currentConclusion = { status: 'evidence-added', summary: 'Every required original-revision scenario is covered.', evidenceComplete: true, completedRunId: origin === 'child' ? childId : parentId, ...(origin === 'child' ? {} : { [origin]: true }) };
    await withController(run, value, async page => {
      assert.match(page.container.textContent, /Required · Evidence supplied/); assert.equal(page.node('start-review-verification'), undefined);
      if (origin !== 'child') {
        assert.match(page.container.textContent, new RegExp(origin === 'currentRunEvidenceComplete' ? 'Verified in this run' : 'Covered by cited evidence'));
        const checks = descendants(page.container).find(node => node.tagName === 'A' && node.href === '#recorded-checks'); assert.ok(checks);
      } else assert.ok(descendants(page.container).some(node => node.tagName === 'A' && node.href === `chrome-extension://test/details.html?runId=${childId}`));
      assert.ok(page.button('Review a repeat verification')); assert.equal(page.calls.some(call => call.type === 'reviews.verify'), false);
    });
  }
  const run = v3Review(); const value = related(); value.recommendation = { ...value.recommendation, ...run.result.e2eAssessment };
  value.currentConclusion = { status: 'evidence-added', summary: 'One scenario passed, but another is still missing.', canSupplementAssessment: true, evidenceComplete: false, completedRunId: null };
  await withController(run, value, async page => { assert.equal(page.node('review-verification-requirements').disabled, false); assert.doesNotMatch(page.container.textContent, /Evidence supplied|Verification evidence available/); });
});

test('incomplete v3 reports and absent E2E metadata never render a not-needed conclusion', async () => {
  const partial = v3Review(); partial.result.report.rechecked = false;
  await withController(partial, { ...related(), recommendation: null }, async page => {
    assert.match(page.container.textContent, /final report and E2E assessment are incomplete/); assert.equal(page.node('start-review-verification'), undefined);
  });
  const missing = v3Review(); missing.result.e2eAssessment = null;
  await withController(missing, { ...related(), recommendation: null }, async page => { assert.match(page.container.textContent, /E2E necessity was not recorded/); assert.doesNotMatch(page.container.textContent, /explicitly records.*not needed/); });
});
