import test from 'node:test';
import assert from 'node:assert/strict';
import { setImmediate as nextTurn } from 'node:timers/promises';

class Node {
  children = []; attributes = new Map(); listeners = new Map(); hidden = false; disabled = false; value = ''; checked = false; _text = ''; parent = undefined;
  constructor(tag) { this.tagName = tag.toUpperCase(); }
  set textContent(value) { this._text = value; this.children = []; }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  append(...children) { for (const child of children) { child.parent = this; this.children.push(child); } }
  replaceChildren(...children) { this.children = []; this.append(...children); }
  setAttribute(key, value) { this.attributes.set(key, value); }
  addEventListener(type, listener) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]); }
  async fire(type) { for (const listener of this.listeners.get(type) ?? []) listener({ preventDefault() {} }); await nextTurn(); }
  showModal() { this.open = true; }
  close() { this.open = false; for (const listener of this.listeners.get('close') ?? []) listener(); }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); }
}
const descendants = node => [node, ...node.children.flatMap(descendants)];
const nextId = 'd5ed57a0-dc12-445c-970a-81b63213a333';
const uuid = /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i;
let imports = 0;
const makeRun = () => ({
  runId: '2a229781-dc72-4300-8435-584460c7222a',
  task: { requestId: 'original-request', actionId: 'investigate', actionKind: 'bug-investigation', repository: 'microsoft/PowerToys', target: { type: 'issue', number: 50493 }, prompt: 'Preserve the original investigation.' },
  config: { agent: 'codex', model: 'previous-model', reasoningEffort: 'low' }, status: { state: 'succeeded', exitCode: 0 }, view: { read: true, handled: false },
  result: {
    schemaVersion: 3, outcome: 'completed', summary: 'The bug was investigated.', report: { complete: true, rechecked: true, coverage: ['Source and reproduction reviewed.'], limitations: [] },
    plans: [
      { id: 'other-plan', kind: 'feature-implement', summary: 'Unselected alternative plan.', steps: ['Do not start this alternative.'], acceptanceCriteria: [], prerequisites: [], evidence: [] },
      { id: 'fix-plan', kind: 'issue-fix', summary: 'Fix the confirmed null-handling bug.', steps: ['Add a focused guard.', 'Verify the original failure path.'], acceptanceCriteria: ['The original failure path is safe.'], prerequisites: ['Use the configured repository and available test tools.'], evidence: ['The saved investigation identified the unsafe call.'] },
    ],
    nextActions: [{ proposalId: 'saved-fix-proposal', kind: 'start-task', taskKind: 'issue-fix', planId: 'fix-plan', reason: 'Implement the saved fix plan.', body: '', recommended: true }],
  },
});
function receipt(payload, run, action) {
  return {
    runId: nextId,
    task: { requestId: payload.requestId, actionKind: action.taskKind, repository: run.task.repository, target: structuredClone(run.task.target),
      ...(payload.execution ? { execution: structuredClone(payload.execution) } : {}),
      planSource: { parentRunId: run.runId, parentResultFingerprint: 'saved-result-fingerprint', proposalId: action.proposalId, planId: action.planId, repository: run.task.repository, target: structuredClone(run.task.target), revisionSha: null } },
    status: { state: 'accepted' },
  };
}
async function withDialog(verify, { start, createTab, initialize } = {}) {
  const globals = new Map(['document', 'chrome', 'sessionStorage'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  const body = new Node('body'); const storage = new Map(); const calls = []; const tabs = []; const run = makeRun();
  initialize?.(run);
  const action = run.result.nextActions[0]; const originalRun = structuredClone(run); const originalAction = structuredClone(action);
  const defaults = { defaultAgent: 'copilot', defaults: { codex: { model: 'current-codex-model', reasoningEffort: 'ultra' }, copilot: { model: 'current-copilot-model', reasoningEffort: 'max' } }, reasoningEfforts: { codex: ['minimal', 'low', 'medium', 'high', 'xhigh', 'ultra'], copilot: ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'] } };
  let module;
  const page = {
    body, storage, calls, tabs, run, action, defaults,
    dialog: () => descendants(body).find(node => node.tagName === 'DIALOG'),
    node: id => descendants(body).find(node => node.id === id),
    async change(id, value) { const node = page.node(id); assert.ok(node, id); assert.equal(node.disabled, false); node.value = value; await node.fire('change'); },
    async submit() { assert.equal(page.node('start-planned-task').disabled, false); await descendants(page.dialog()).find(node => node.tagName === 'FORM').fire('submit'); },
    async load() { module = await import(new URL(`../src/result-task-dialog.ts?result-task-dialog-test=${++imports}`, import.meta.url)); },
    async open() { page.closed = module.openResultTaskDialog(run, action); await nextTurn(); assert.equal(page.dialog()?.open, true); },
    async close() { page.dialog()?.close(); await page.closed; },
  };
  try {
    globalThis.document = { body, createElement: tag => new Node(tag) };
    globalThis.sessionStorage = { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) };
    globalThis.chrome = {
      tabs: { create: async tab => { tabs.push(structuredClone(tab)); if (createTab) await createTab(tab); return {}; } },
      runtime: { getURL: path => `chrome-extension://test/${path}`, sendMessage: async message => {
        calls.push(structuredClone(message)); assert.equal(message.channel, 'pulse-ui');
        switch (message.type) {
          case 'agents.defaults': return { ok: true, data: structuredClone(defaults) };
          case 'tasks.startFromResult': return start ? await start(message.payload, { run, action }) : { ok: true, data: receipt(message.payload, run, action) };
          default: throw new Error(`Unexpected operation ${message.type}; this fixture never writes settings or GitHub or runs a real task.`);
        }
      } },
    };
    await page.load(); await page.open(); await verify(page);
    assert.deepEqual(run, originalRun, 'the saved investigation, selected plan and proposal remain unchanged');
    assert.deepEqual(action, originalAction);
  } finally {
    await page.close();
    for (const [key, descriptor] of globals) { if (descriptor) Object.defineProperty(globalThis, key, descriptor); else delete globalThis[key]; }
  }
}

test('the saved plan is shown intact and starts only on explicit confirmation without a blanket prerequisites checkbox', async () => {
  await withDialog(async page => {
    const plan = page.run.result.plans.find(item => item.id === page.action.planId);
    assert.ok(page.body.textContent.includes('microsoft/PowerToys · Issue #50493'));
    for (const value of [plan.summary, ...plan.steps, ...plan.acceptanceCriteria, ...plan.prerequisites, ...plan.evidence]) assert.ok(page.body.textContent.includes(value), value);
    assert.equal(page.body.textContent.includes('Unselected alternative plan.'), false);
    assert.equal(descendants(page.body).some(node => node.tagName === 'INPUT' && node.type === 'checkbox'), false);
    assert.equal(page.node('start-planned-task').disabled, false); assert.equal(page.node('verify-execution-mode').value, 'defaults');
    assert.deepEqual(page.calls.map(call => call.type), ['agents.defaults']); assert.equal(page.tabs.length, 0);
    await page.submit(); await page.closed;
    const payload = page.calls.find(call => call.type === 'tasks.startFromResult').payload;
    assert.deepEqual(Object.keys(payload).sort(), ['proposalId', 'requestId', 'runId']);
    assert.equal(payload.runId, page.run.runId); assert.equal(payload.proposalId, page.action.proposalId); assert.match(payload.requestId, uuid);
    assert.equal(page.tabs[0].url, `chrome-extension://test/details.html?runId=${nextId}`); assert.equal(page.storage.size, 0);
  });
});

test('CLI, model and reasoning choices apply only to the newly started saved-plan task', async () => {
  await withDialog(async page => {
    assert.equal(page.node('verify-agent').value, 'copilot'); assert.equal(page.node('verify-agent').disabled, true);
    await page.change('verify-execution-mode', 'custom'); await page.change('verify-agent', 'codex');
    assert.equal(page.node('verify-model').value, 'current-codex-model'); assert.equal(page.node('verify-effort').value, 'ultra');
    assert.equal(page.node('verify-effort').children.some(option => option.value === 'max'), false);
    page.node('verify-model').value = 'chosen-model'; await page.change('verify-effort', 'high');
    await page.submit(); await page.closed;
    const payload = page.calls.find(call => call.type === 'tasks.startFromResult').payload;
    assert.deepEqual(payload.execution, { agent: 'codex', model: 'chosen-model', reasoningEffort: 'high' });
    assert.deepEqual(Object.keys(payload).sort(), ['execution', 'proposalId', 'requestId', 'runId']);
    assert.deepEqual(page.calls.map(call => call.type), ['agents.defaults', 'tasks.startFromResult']);
  });
});

test('an unsent saved-plan draft retains its custom options when closed, reopened and switched through defaults', async () => {
  await withDialog(async page => {
    await page.change('verify-execution-mode', 'custom'); page.node('verify-model').value = 'retained-plan-model';
    await page.change('verify-execution-mode', 'defaults'); await page.change('verify-execution-mode', 'custom');
    assert.equal(page.node('verify-model').value, 'retained-plan-model');
    await page.close(); await page.load(); await page.open();
    assert.equal(page.node('verify-execution-mode').value, 'custom'); assert.equal(page.node('verify-model').value, 'retained-plan-model');
    assert.equal(page.calls.some(call => call.type === 'tasks.startFromResult'), false);
    await page.submit(); await page.closed;
    assert.equal(page.calls.find(call => call.type === 'tasks.startFromResult').payload.execution.model, 'retained-plan-model');
    assert.equal(page.storage.size, 0);
  });
});

test('Issue verification requires reviewing and acknowledging the saved requirements before its explicit start', async () => {
  await withDialog(async page => {
    assert.match(page.dialog().textContent, /Review verification requirements/); assert.equal(page.node('start-planned-task').textContent, 'Start verification');
    assert.equal(page.node('start-planned-task').disabled, true); assert.equal(page.calls.some(call => call.type === 'tasks.startFromResult'), false);
    page.node('plan-requirements-confirmed').checked = true; await page.node('plan-requirements-confirmed').fire('change');
    assert.equal(page.node('start-planned-task').disabled, false); await page.submit(); await page.closed;
    const payload = page.calls.find(call => call.type === 'tasks.startFromResult').payload;
    assert.equal(payload.proposalId, page.action.proposalId); assert.equal(payload.runId, page.run.runId);
  }, { initialize: run => { run.result.plans[1].kind = 'issue-verify'; run.result.nextActions[0].taskKind = 'issue-verify'; } });
});

test('a mismatched accepted receipt preserves the original request and opens no task until a matching receipt arrives', async () => {
  const invalid = [
    value => { value.runId = 'not-a-uuid'; }, value => { delete value.task; },
    value => { value.task.requestId = 'different-request'; }, value => { value.task.actionKind = 'feature-implement'; },
    value => { value.task.planSource.parentRunId = nextId; }, value => { value.task.planSource.proposalId = 'other-proposal'; },
    value => { value.task.planSource.planId = 'other-plan'; }, value => { value.task.repository = 'other/repository'; },
    value => { value.task.target.type = 'pr'; }, value => { value.task.target.number = 99; },
    value => { value.task.planSource.repository = 'other/repository'; }, value => { value.task.planSource.target.type = 'pr'; },
    value => { value.task.planSource.target.number = 99; }, value => { delete value.task.planSource; },
  ];
  for (const change of invalid) {
    let attempts = 0;
    await withDialog(async page => {
      await page.submit();
      assert.match(page.body.textContent, /did not match the confirmed plan/); assert.equal(page.tabs.length, 0);
      assert.equal(page.node('verify-execution-mode').disabled, true); assert.equal(page.node('start-planned-task').textContent, 'Recover task request');
      const stored = JSON.parse([...page.storage.values()][0]); assert.equal('acceptedRunId' in stored, false);
      const first = page.calls.find(call => call.type === 'tasks.startFromResult').payload;
      await page.close(); await page.open(); await page.submit(); await page.closed;
      assert.deepEqual(page.calls.filter(call => call.type === 'tasks.startFromResult').map(call => call.payload), [first, first], change.toString());
      assert.equal(page.tabs.length, 1); assert.equal(page.storage.size, 0);
    }, { start: async (payload, { run, action }) => { const accepted = receipt(payload, run, action); if (++attempts === 1) change(accepted); return { ok: true, data: accepted }; } });
  }
});

test('unknown request outcomes keep the exact request, proposal and execution frozen across close and module reload', async () => {
  for (const unknown of ['transport-error', 'unknown-host-status']) {
    let attempts = 0;
    await withDialog(async page => {
      await page.change('verify-execution-mode', 'custom'); await page.change('verify-agent', 'codex');
      page.node('verify-model').value = 'frozen-model'; await page.change('verify-effort', '');
      await page.submit();
      assert.equal(page.tabs.length, 0); assert.equal(page.node('verify-execution-mode').disabled, true);
      for (const id of ['verify-agent', 'verify-model', 'verify-effort']) assert.equal(page.node(id).disabled, true, id);
      const first = page.calls.find(call => call.type === 'tasks.startFromResult').payload;
      const stored = JSON.parse([...page.storage.values()][0]);
      assert.equal(stored.requestId, first.requestId); assert.equal(stored.proposalId, first.proposalId);
      assert.deepEqual(stored.plan, page.run.result.plans.find(plan => plan.id === page.action.planId));
      await page.close(); await page.load(); await page.open();
      assert.equal(page.node('verify-execution-mode').disabled, true); assert.equal(page.node('verify-agent').value, 'codex');
      assert.equal(page.node('verify-model').value, 'frozen-model'); assert.equal(page.node('verify-effort').value, '');
      assert.equal(page.node('start-planned-task').textContent, 'Recover task request'); assert.equal(page.tabs.length, 0);
      await page.submit(); await page.closed;
      assert.deepEqual(page.calls.filter(call => call.type === 'tasks.startFromResult').map(call => call.payload), [first, first]);
      assert.deepEqual(first.execution, { agent: 'codex', model: 'frozen-model', reasoningEffort: '' });
      assert.equal(page.tabs.length, 1); assert.equal(page.storage.size, 0);
    }, { start: async (payload, { run, action }) => {
      if (++attempts === 1) {
        if (unknown === 'transport-error') throw new Error('The Host acknowledgement was lost.');
        return { ok: false, error: { code: 'UNKNOWN_OUTCOME', message: 'The saved request has no confirmed outcome.' } };
      }
      return { ok: true, data: receipt(payload, run, action) };
    } });
  }
});

test('an accepted task with a failed tab open is recovered after reload without starting another task', async () => {
  let attempts = 0;
  await withDialog(async page => {
    await page.submit();
    assert.equal(page.node('start-planned-task').textContent, 'Open accepted task'); assert.match(page.body.textContent, /Simulated tab failure/);
    assert.equal(page.node('verify-execution-mode').disabled, true);
    await page.close(); await page.load(); await page.open();
    assert.equal(page.node('start-planned-task').textContent, 'Open accepted task');
    await page.submit(); await page.closed;
    assert.equal(page.calls.filter(call => call.type === 'tasks.startFromResult').length, 1);
    assert.deepEqual(page.tabs.map(tab => tab.url), [`chrome-extension://test/details.html?runId=${nextId}`, `chrome-extension://test/details.html?runId=${nextId}`]);
    assert.equal(page.storage.size, 0);
  }, { createTab: async () => { if (++attempts === 1) throw new Error('Simulated tab failure'); } });
});
