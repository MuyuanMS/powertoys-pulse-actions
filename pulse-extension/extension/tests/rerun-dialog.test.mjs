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
const oldSha = 'a'.repeat(40), newSha = 'b'.repeat(40);
const nextId = 'd5ed57a0-dc12-445c-970a-81b63213a333';
let imports = 0;
const makeRun = () => ({
  runId: '2a229781-dc72-4300-8435-584460c7222a', task: { requestId: 'old-request', actionId: 'review', actionKind: 'pr-review', repository: 'microsoft/PowerToys', target: { type: 'pr', number: 50493 }, expectedHeadSha: oldSha, prompt: 'Retain original prompt', execution: { agent: 'codex', model: 'old-model', reasoningEffort: 'low' } },
  config: { agent: 'codex', model: 'old-model', reasoningEffort: 'low', cliPath: 'C:\\old-cli.exe', repoFolder: 'C:\\old-run', permission: 'yolo' },
  status: { state: 'failed' }, view: { read: true, handled: false },
});

async function withDialog(verify, { rerun, target, createTab, initialize } = {}) {
  const originalGlobals = new Map(['document', 'chrome', 'sessionStorage'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  const body = new Node('body'); const storage = new Map(); const calls = []; const tabs = []; const run = makeRun();
  initialize?.({ run, storage });
  const originalRun = structuredClone(run);
  const config = { agent: 'copilot', agentDefaults: { codex: { model: 'current-codex-model', reasoningEffort: 'ultra' }, copilot: { model: 'current-copilot-model', reasoningEffort: 'max' } }, cliSelections: { codex: 'C:\\current-codex.exe', copilot: 'C:\\current-copilot.exe' }, permission: 'yolo' };
  let open;
  const page = {
    body, calls, tabs, run, storage, config,
    dialog: () => descendants(body).find(node => node.tagName === 'DIALOG'),
    node: id => descendants(body).find(node => node.id === id),
    button: text => descendants(body).find(node => node.tagName === 'BUTTON' && node.textContent === text),
    async change(id, value) { const node = page.node(id); assert.equal(node.disabled, false); node.value = value; await node.fire('change'); },
    async click(text) { const button = page.button(text); assert.ok(button, text); assert.equal(button.disabled, false); await button.fire('click'); },
    async submit() { const submit = descendants(page.dialog()).find(node => node.tagName === 'BUTTON' && node.type === 'submit'); assert.equal(submit.disabled, false); await descendants(page.dialog()).find(node => node.tagName === 'FORM').fire('submit'); },
    async load() { open = await import(new URL(`../src/rerun-dialog.ts?rerun-dialog-test=${++imports}`, import.meta.url)); },
    async open() { page.closed = open.openRerunDialog(run); await nextTurn(); assert.equal(page.dialog()?.open, true); },
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
          case 'config.get': return { ok: true, data: structuredClone(config) };
          case 'agents.defaults': return { ok: true, data: { defaultAgent: config.agent, defaults: structuredClone(config.agentDefaults), reasoningEfforts: { codex: ['minimal', 'low', 'medium', 'high', 'xhigh', 'ultra'], copilot: ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'] } } };
          case 'targets.get': return { ok: true, data: target ? await target(message.payload) : { target: { type: 'pr', number: 50493 }, headSha: newSha.toUpperCase(), title: 'Current title' } };
          case 'tasks.rerun': return rerun ? await rerun(message.payload) : { ok: true, data: { runId: nextId } };
          default: throw new Error(`Unexpected operation ${message.type}; this fixture never runs CLI tasks or writes GitHub.`);
        }
      } },
    };
    await page.load(); await page.open(); await verify(page);
    assert.deepEqual(run, originalRun, 'The dialog cannot rewrite the old task, results, target or execution record');
    assert.equal(calls.some(call => /operations\.|webActions\.|tasks.submit/.test(call.type)), false);
  } finally {
    await page.close();
    for (const [key, descriptor] of originalGlobals) { if (descriptor) Object.defineProperty(globalThis, key, descriptor); else delete globalThis[key]; }
  }
}

test('rerun shows current defaults and previous execution, and starts only after explicit confirmation', async () => {
  await withDialog(async page => {
    assert.equal(page.node('rerun-mode').value, 'defaults'); assert.equal(page.node('rerun-agent').value, 'copilot');
    assert.equal(page.node('rerun-model').value, 'current-copilot-model'); assert.equal(page.node('rerun-effort').value, 'max');
    assert.equal(page.node('rerun-agent').disabled, true); assert.match(page.body.textContent, /Previous requested modelold-model/);
    assert.ok(page.body.textContent.includes('C:\\current-copilot.exe')); assert.ok(page.body.textContent.includes(oldSha));
    assert.deepEqual(page.calls.map(call => call.type), ['config.get', 'agents.defaults']);
    await page.submit(); await page.closed;
    const payload = page.calls.find(call => call.type === 'tasks.rerun').payload;
    assert.deepEqual(Object.keys(payload).sort(), ['execution', 'requestId', 'reviewOptions', 'runId']); assert.equal(payload.execution, null);
    assert.deepEqual(payload.reviewOptions, { mode: 'build-tests' });
    assert.notEqual(payload.requestId, page.run.task.requestId); assert.equal(page.tabs[0].url, `chrome-extension://test/details.html?runId=${nextId}`); assert.equal(page.storage.size, 0);
  });
});

test('previous overrides stay explicit in the preview and custom CLI/model/effort are sent as one replacement', async () => {
  await withDialog(async page => {
    await page.change('rerun-mode', 'previous');
    assert.equal(page.node('rerun-agent').value, 'codex'); assert.equal(page.node('rerun-model').value, 'old-model'); assert.equal(page.node('rerun-effort').value, 'low');
    await page.submit(); await page.closed;
    assert.equal('execution' in page.calls.find(call => call.type === 'tasks.rerun').payload, false);
    await page.open(); await page.change('rerun-mode', 'custom'); await page.change('rerun-agent', 'codex');
    assert.equal(page.node('rerun-model').value, 'current-codex-model'); assert.equal(page.node('rerun-effort').value, 'ultra');
    assert.equal(page.node('rerun-effort').children.some(option => option.value === 'max'), false);
    page.node('rerun-model').value = ''; await page.change('rerun-effort', ''); await page.submit(); await page.closed;
    const attempts = page.calls.filter(call => call.type === 'tasks.rerun');
    assert.deepEqual(attempts[1].payload.execution, { agent: 'codex', model: '', reasoningEffort: '' }); assert.notEqual(attempts[0].payload.requestId, attempts[1].payload.requestId);
  });
});

test('an explicitly scoped PR seeds its previous scope and submits the newly selected scope', async () => {
  await withDialog(async page => {
    assert.equal(page.node('rerun-review-scope').value, 'static');
    assert.equal(page.node('rerun-review-scope').disabled, false);
    await page.change('rerun-review-scope', 'ui-e2e');
    await page.submit(); await page.closed;
    const payload = page.calls.find(call => call.type === 'tasks.rerun').payload;
    assert.deepEqual(payload.reviewOptions, { mode: 'ui-e2e' });
    assert.equal('expectedHeadSha' in payload, false);
    assert.equal(page.calls.some(call => call.type === 'targets.get'), false);
  }, { initialize: ({ run }) => { run.task.reviewOptions = { mode: 'static' }; } });
});

test('verification follow-ups keep their exact scope and original revision without offering or reading another PR HEAD', async () => {
  for (const sourceMode of ['build-tests', 'ui-e2e']) await withDialog(async page => {
    const scope = page.node('rerun-review-scope'); const revision = page.node('rerun-revision');
    assert.equal(scope.value, sourceMode); assert.equal(scope.disabled, true);
    assert.equal(scope.children.some(option => option.value === 'static'), false);
    assert.equal(revision.value, 'original'); assert.equal(revision.disabled, true);
    const readHead = page.button('Read current PR HEAD');
    assert.ok(!readHead || readHead.hidden || readHead.disabled, 'current-HEAD lookup is unavailable for a verification follow-up');
    if (readHead) await readHead.fire('click');
    await page.submit(); await page.closed;
    const payload = page.calls.find(call => call.type === 'tasks.rerun').payload;
    assert.deepEqual(payload.reviewOptions, { mode: sourceMode }); assert.equal('expectedHeadSha' in payload, false);
    assert.equal(page.calls.some(call => call.type === 'targets.get'), false);
  }, { initialize: ({ run }) => {
    run.task.actionKind = 'pr-verify'; run.task.reviewOptions = { mode: sourceMode };
    run.task.followUp = { parentRunId: '25429772-5c6a-4b62-9dc1-8e8dd94fd410', recommendationId: 'runtime-check', subject: 'original-pr', revisionSha: oldSha };
  } });
});

test('current PR HEAD must be explicitly read, displayed and checked before changing the requested revision', async () => {
  await withDialog(async page => {
    await page.change('rerun-revision', 'current');
    assert.equal(page.button('Start new run').disabled, true); assert.equal(page.calls.some(call => call.type === 'targets.get'), false);
    await page.click('Read current PR HEAD');
    assert.deepEqual(page.calls.find(call => call.type === 'targets.get').payload, { target: { type: 'pr', number: 50493 } });
    assert.ok(page.body.textContent.includes(newSha)); assert.equal(page.button('Start new run').disabled, true);
    page.node('rerun-confirm-head').checked = true; await page.node('rerun-confirm-head').fire('change'); await page.submit(); await page.closed;
    assert.equal(page.calls.find(call => call.type === 'tasks.rerun').payload.expectedHeadSha, newSha);
  });
});

test('lost acknowledgement freezes request options and survives reopening and module reload with the same identity', async () => {
  let count = 0;
  await withDialog(async page => {
    await page.change('rerun-mode', 'custom'); page.node('rerun-model').value = 'chosen-model'; await page.submit();
    assert.match(page.body.textContent, /Simulated lost acknowledgement/); assert.equal(page.node('rerun-mode').disabled, true); assert.equal(page.node('rerun-model').disabled, true);
    const first = page.calls.find(call => call.type === 'tasks.rerun').payload; await page.close(); await page.load(); await page.open();
    assert.equal(page.node('rerun-model').value, 'chosen-model'); assert.equal(page.node('rerun-mode').disabled, true); assert.ok(page.button('Retry same request'));
    await page.submit(); await page.closed;
    assert.deepEqual(page.calls.filter(call => call.type === 'tasks.rerun').map(call => call.payload), [first, first]); assert.equal(page.tabs.length, 1);
  }, { rerun: async () => { if (++count === 1) throw new Error('Simulated lost acknowledgement'); return { ok: true, data: { runId: nextId } }; } });
});

test('a persisted unknown request without a recorded scope recovers unchanged instead of gaining the new default', async () => {
  let originalPayload; let attempts = 0;
  await withDialog(async page => {
    assert.equal(page.node('rerun-review-scope').value, ''); assert.equal(page.node('rerun-review-scope').disabled, true);
    assert.equal(page.node('rerun-mode').disabled, true); assert.equal(page.node('rerun-model').value, 'persisted-model');
    await page.submit();
    assert.match(page.body.textContent, /Simulated persisted request uncertainty/);
    assert.deepEqual(JSON.parse([...page.storage.values()][0]).payload, originalPayload);
    await page.close(); await page.load(); await page.open();
    assert.equal(page.node('rerun-review-scope').value, ''); assert.equal(page.node('rerun-review-scope').disabled, true);
    await page.submit(); await page.closed;
    const payloads = page.calls.filter(call => call.type === 'tasks.rerun').map(call => call.payload);
    assert.deepEqual(payloads, [originalPayload, originalPayload]);
    assert.equal(payloads.some(payload => 'reviewOptions' in payload), false);
    assert.equal(page.tabs.length, 1); assert.equal(page.storage.size, 0);
  }, {
    initialize: ({ run, storage }) => {
      originalPayload = { runId: run.runId, requestId: '718bb53e-8c59-4c39-ad47-883f759f2957', execution: { agent: 'codex', model: 'persisted-model', reasoningEffort: 'high' } };
      storage.set(`pulse-rerun-pending:${run.runId}`, JSON.stringify({ payload: originalPayload }));
    },
    rerun: async () => { if (++attempts === 1) throw new Error('Simulated persisted request uncertainty'); return { ok: true, data: { runId: nextId } }; },
  });
});

test('an accepted task whose tab failed to open is reopened without creating another task', async () => {
  let tabs = 0;
  await withDialog(async page => {
    await page.submit(); assert.ok(page.button('Open accepted task')); assert.match(page.body.textContent, /Simulated tab error/);
    await page.submit(); await page.closed;
    assert.equal(page.calls.filter(call => call.type === 'tasks.rerun').length, 1); assert.equal(page.tabs.length, 2);
  }, { createTab: async () => { if (++tabs === 1) throw new Error('Simulated tab error'); } });
});

test('a definite stale-context rejection allows an explicit newer revision with a fresh request identity', async () => {
  let attempts = 0;
  await withDialog(async page => {
    await page.submit(); assert.equal(page.node('rerun-revision').disabled, false); assert.match(page.body.textContent, /STALE_CONTEXT/);
    await page.change('rerun-revision', 'current'); await page.click('Read current PR HEAD'); page.node('rerun-confirm-head').checked = true; await page.node('rerun-confirm-head').fire('change');
    await page.submit(); await page.closed;
    const requests = page.calls.filter(call => call.type === 'tasks.rerun').map(call => call.payload);
    assert.notEqual(requests[0].requestId, requests[1].requestId); assert.equal(requests[1].expectedHeadSha, newSha);
  }, { rerun: async () => ++attempts === 1 ? { ok: false, error: { code: 'STALE_CONTEXT', message: 'PR moved before admission.' } } : { ok: true, data: { runId: nextId } } });
});

test('a target lookup returning a different PR cannot enable submission', async () => {
  await withDialog(async page => {
    await page.change('rerun-revision', 'current'); await page.click('Read current PR HEAD');
    assert.match(page.body.textContent, /different PR or an invalid revision/); assert.equal(page.button('Start new run').disabled, true);
    assert.equal(page.calls.some(call => call.type === 'tasks.rerun'), false);
  }, { target: async () => ({ target: { type: 'pr', number: 99999 }, headSha: newSha }) });
});

test('unsent rerun choices survive close and reload without submitting or changing the original task', async () => {
  await withDialog(async page => {
    await page.change('rerun-mode', 'custom'); page.node('rerun-model').value = 'retained-rerun-model';
    await page.change('rerun-mode', 'defaults'); await page.change('rerun-mode', 'custom');
    assert.equal(page.node('rerun-model').value, 'retained-rerun-model');
    await page.change('rerun-review-scope', 'ui-e2e'); await page.change('rerun-revision', 'current'); await page.click('Read current PR HEAD');
    page.node('rerun-confirm-head').checked = true; await page.node('rerun-confirm-head').fire('change');
    await page.close(); await page.load(); await page.open();
    assert.equal(page.node('rerun-model').value, 'retained-rerun-model'); assert.equal(page.node('rerun-review-scope').value, 'ui-e2e');
    assert.equal(page.node('rerun-revision').value, 'current'); assert.equal(page.node('rerun-confirm-head').checked, true);
    assert.equal(page.calls.some(call => call.type === 'tasks.rerun'), false);
    await page.submit(); await page.closed;
    const payload = page.calls.find(call => call.type === 'tasks.rerun').payload;
    assert.equal(payload.execution.model, 'retained-rerun-model'); assert.equal(payload.expectedHeadSha, newSha); assert.equal(page.storage.size, 0);
  });
});

test('unlinked historical records stay readable but cannot start a new targetless rerun', async () => {
  await withDialog(async page => {
    assert.match(page.body.textContent, /Unlinked historical record/); assert.match(page.body.textContent, /Open its PR or Issue to start a new task/);
    assert.equal(page.button('Start new run').disabled, true); assert.equal(page.calls.some(call => call.type === 'tasks.rerun'), false);
  }, { initialize: ({ run }) => { delete run.task.target; } });
});
