import test from 'node:test';
import assert from 'node:assert/strict';

const extensionId = 'a'.repeat(32);
const origin = 'https://cautious-memory-r38ze9j.pages.github.io';
const messages = [];
const storage = {};
const tabs = [];
let settingsOpened = 0;
let rejectTab = false;
let externalListener;
let internalListener;
const task = { requestId: 'request-1', actionId: 'action-1', actionKind: 'issue-fix', repository: 'microsoft/PowerToys', prompt: 'inspect', target: { type: 'issue', number: 1 }, execution: { agent: 'codex', model: 'custom-model.v2', reasoningEffort: 'high' } };
const run = { runId: 'run-1', task, config: { agent: 'codex', model: 'custom-model.v2', reasoningEffort: 'high', executionSource: { agent: 'task', model: 'task', reasoningEffort: 'task' }, cliPath: 'C:\\private\\codex.exe', repoFolder: 'C:\\private', permission: 'read-only' }, status: { state: 'running', sequence: 1 }, view: { read: false, handled: false } };
const caps = { hostVersion: 'test', protocolVersion: 1, agents: { codex: { available: true }, copilot: { available: false } }, github: { available: true, account: 'private' } };
const draft = { requestId: '12345678-1234-1234-1234-123456789abc', actionId: 'comment-1', kind: 'comment', target: { repository: 'microsoft/PowerToys', type: 'issue', number: 7 }, body: 'Draft comment' };
const operation = { operationId: '87654321-1234-1234-1234-123456789abc', ...draft, status: 'prepared', completedSteps: [], remainingSteps: ['comment'], urls: [], createdAt: 'now', updatedAt: 'now', account: 'private', sourceOrigin: origin };
const installations = { installations: { codex: [{ path: 'C:\\private\\codex.exe', available: true, version: '1.0', source: 'private source', resolvedPath: 'C:\\private\\resolved.exe', aliases: ['C:\\private\\alias.cmd'] }], copilot: [] }, selections: { codex: '', copilot: '' } };
const respond = request => request.type === 'hello' ? caps
  : request.type === 'agents.list' ? installations
  : request.type === 'agents.defaults' ? { defaultAgent: 'codex', defaults: { codex: { model: 'custom-model.v2', reasoningEffort: 'high', cliPath: 'private' }, copilot: { model: '', reasoningEffort: '' } }, reasoningEfforts: { codex: ['high', 'ultra'], copilot: ['none', 'max'] }, config: 'private' }
  : request.type === 'actions.check' ? { actionKind: request.payload.actionKind, ready: false, blockers: [{ code: 'PROMPT_MISSING', message: 'Sync prompts.', path: 'private' }], config: 'private' }
  : request.type === 'targets.get' ? { target: request.payload.target, headSha: 'a'.repeat(40), title: 'Target PR', token: 'private' }
  : request.type.startsWith('webActions.') ? operation
  : request.type === 'tasks.list' ? { runs: [run], runningCount: 1, unreadCount: 0 }
  : request.type === 'tasks.events' ? { events: [{ sequence: 1, time: 'now', type: 'text', text: 'hello', raw: 'private' }], nextSequence: 1, truncated: false } : run;
test('background enforces sender identity and persisted origin grants before talking to Host', async t => {
  t.mock.method(globalThis, 'setTimeout', () => 1);
  t.mock.method(globalThis, 'clearTimeout', () => {});
  globalThis.chrome = {
    runtime: {
      id: extensionId, getURL: path => `chrome-extension://${extensionId}/${path}`, getManifest: () => ({ version: '1.2.3' }),
      openOptionsPage: async () => { settingsOpened++; },
      onMessageExternal: { addListener: listener => { externalListener = listener; } },
      onMessage: { addListener: listener => { internalListener = listener; } },
      onStartup: { addListener() {} }, onInstalled: { addListener() {} },
      connectNative: name => {
        assert.equal(name, 'com.powertoys.pulse');
        let reply;
        return {
          onMessage: { addListener: listener => { reply = listener; } }, onDisconnect: { addListener() {} }, disconnect() {},
          postMessage: message => {
            messages.push(message);
            queueMicrotask(() => reply({ id: message.id, protocolVersion: 1, ok: true, data: respond(message) }));
          },
        };
      },
    },
    storage: { local: { get: async key => ({ [key]: storage[key] }), set: async values => Object.assign(storage, values) } },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {}, setTitle: async () => {} },
    tabs: { create: async value => { if (rejectTab) throw new Error('Tab unavailable'); tabs.push(value); return { id: tabs.length }; } },
    alarms: { create: async () => {}, onAlarm: { addListener() {} } },
  };
  await import('../src/background.ts');
  const external = (message, sender = { url: origin + '/page', origin }) => new Promise(resolve => { assert.equal(externalListener(message, sender, resolve), true); });
  const bridgeHello = await external({ protocolVersion: 1, type: 'bridge.hello' });
  assert.equal(bridgeHello.data.extensionVersion, '1.2.3');
  assert.ok(bridgeHello.data.methods.includes('github.prepare'));
  assert.ok(bridgeHello.data.methods.includes('agents.defaults'));
  assert.equal(bridgeHello.data.methods.includes('agents.list'), false);
  assert.equal(messages.some(message => message.type === 'bridge.hello'), false);
  assert.equal((await external({ protocolVersion: 1, type: 'ui.openSettings' })).data.opened, true);
  assert.equal(settingsOpened, 1);
  assert.equal((await external({ protocolVersion: 1, type: 'ui.openTasks' })).data.opened, true);
  assert.equal(tabs.at(-1).url, `chrome-extension://${extensionId}/popup.html?expanded=1`);
  const agentDefaults = await external({ protocolVersion: 1, type: 'agents.defaults' });
  assert.equal(agentDefaults.data.defaultAgent, 'codex'); assert.equal(agentDefaults.data.defaults.codex.model, 'custom-model.v2');
  assert.equal(agentDefaults.data.config, undefined); assert.equal(agentDefaults.data.defaults.codex.cliPath, undefined);
  assert.deepEqual(messages.findLast(message => message.type === 'agents.defaults').payload, {});
  const execution = { agent: 'copilot', model: '', reasoningEffort: 'none' };
  const readiness = await external({ protocolVersion: 1, type: 'actions.check', payload: { actionKind: 'e2e', execution } });
  assert.equal(readiness.data.actionKind, 'e2e'); assert.equal(readiness.data.config, undefined); assert.equal(readiness.data.blockers[0].path, undefined);
  assert.deepEqual(messages.findLast(message => message.type === 'actions.check').payload, { actionKind: 'e2e', execution });
  const target = await external({ protocolVersion: 1, type: 'targets.get', payload: { target: { type: 'pr', number: 7 } } });
  assert.equal(target.data.headSha, 'a'.repeat(40)); assert.equal(target.data.token, undefined);
  const denied = await external({ protocolVersion: 1, type: 'tasks.get', payload: { runId: 'run-1' } });
  assert.equal(denied.error.code, 'FORBIDDEN_RUN');
  assert.equal(messages.some(message => message.type === 'tasks.get'), false);
  assert.equal((await external({ protocolVersion: 1, type: 'ui.openTask', payload: { runId: 'run-1' } })).error.code, 'FORBIDDEN_RUN');
  assert.equal((await external({ protocolVersion: 1, type: 'hello' }, { url: 'https://evil.test/' })).error.code, 'FORBIDDEN_ORIGIN');
  assert.equal((await external({ protocolVersion: 1, type: 'hello' }, { url: origin + '/', id: 'other-extension' })).error.code, 'FORBIDDEN_ORIGIN');
  assert.equal((await external({ protocolVersion: 1, type: 'hello' }, { url: origin + '/', origin: 'https://evil.test' })).error.code, 'FORBIDDEN_ORIGIN');
  const submit = await external({ protocolVersion: 1, type: 'tasks.submit', payload: { task } });
  assert.equal(submit.ok, true); assert.equal(submit.data.runId, 'run-1'); assert.equal(submit.data.config, undefined);
  assert.deepEqual(submit.data.execution, { agent: 'codex', model: 'custom-model.v2', reasoningEffort: 'high', modelSource: 'task', reasoningEffortSource: 'task' });
  assert.equal(messages.find(message => message.type === 'tasks.submit').payload.sourceOrigin, origin);
  assert.deepEqual(messages.find(message => message.type === 'tasks.submit').payload.task.execution, task.execution);
  assert.deepEqual(storage['pulse.origin-grants.v1'], [{ origin, runId: 'run-1', requestId: 'request-1', actionId: 'action-1' }]);
  assert.equal((await external({ protocolVersion: 1, type: 'ui.openTask', payload: { runId: 'run-1' } })).data.opened, true);
  assert.equal(tabs.at(-1).url, `chrome-extension://${extensionId}/details.html?runId=run-1`);
  const prepared = await external({ protocolVersion: 1, type: 'github.prepare', payload: { draft } });
  assert.equal(prepared.ok, true); assert.equal(prepared.data.operationId, operation.operationId); assert.equal(prepared.data.opened, true);
  assert.equal(prepared.data.account, undefined); assert.equal(prepared.data.body, undefined); assert.equal(prepared.data.sourceOrigin, undefined);
  assert.deepEqual(messages.findLast(message => message.type === 'webActions.prepare').payload, { draft, sourceOrigin: origin });
  assert.equal(tabs.at(-1).url, `chrome-extension://${extensionId}/action.html?operationId=${operation.operationId}`);
  assert.equal(messages.some(message => message.type === 'webActions.submit'), false);
  rejectTab = true;
  const acceptedWithoutTab = await external({ protocolVersion: 1, type: 'github.prepare', payload: { draft } });
  rejectTab = false;
  assert.equal(acceptedWithoutTab.ok, true); assert.equal(acceptedWithoutTab.data.operationId, operation.operationId); assert.equal(acceptedWithoutTab.data.opened, false);
  const operationStatus = await external({ protocolVersion: 1, type: 'github.get', payload: { operationId: operation.operationId } });
  assert.equal(operationStatus.data.status, 'prepared');
  assert.deepEqual(messages.findLast(message => message.type === 'webActions.get').payload, { operationId: operation.operationId, sourceOrigin: origin });
  const get = await external({ protocolVersion: 1, type: 'tasks.get', payload: { runId: 'run-1' } });
  assert.equal(get.ok, true); assert.equal(get.data.config, undefined);
  run.result = { schemaVersion: 2, outcome: 'blocked', phase: 'validation', structured: true, summary: 'Required validation is incomplete.', findings: [{ path: 'C:\\private\\file.cpp' }], diagnostics: [{ account: 'private' }] };
  const blocked = await external({ protocolVersion: 1, type: 'tasks.get', payload: { runId: 'run-1' } });
  assert.equal(blocked.ok, true); assert.equal(blocked.data.result.outcome, 'blocked'); assert.equal(blocked.data.result.phase, 'validation');
  assert.equal(blocked.data.result.findings, undefined); assert.equal(blocked.data.result.diagnostics, undefined);
  run.result.outcome = 'invalid';
  const invalidOutcome = await external({ protocolVersion: 1, type: 'tasks.get', payload: { runId: 'run-1' } });
  assert.equal(invalidOutcome.ok, false); assert.equal(invalidOutcome.error.code, 'INVALID_RESPONSE'); assert.equal(invalidOutcome.data, undefined);
  delete run.result;
  const events = await external({ protocolVersion: 1, type: 'tasks.events', payload: { runId: 'run-1', afterSequence: 0 } });
  assert.equal(events.data.events[0].raw, undefined);
  const forbiddenInternal = await new Promise(resolve => internalListener({ channel: 'pulse-ui', type: 'config.save' }, { id: extensionId, url: origin + '/page' }, resolve));
  assert.equal(forbiddenInternal.error.code, 'FORBIDDEN_SOURCE');
  assert.equal(messages.some(message => message.type === 'config.save'), false);
  const internal = await new Promise(resolve => internalListener({ channel: 'pulse-ui', type: 'tasks.get', payload: { runId: 'run-1' } }, { id: extensionId, url: `chrome-extension://${extensionId}/details.html` }, resolve));
  assert.equal(internal.data.config.repoFolder, 'C:\\private');
  const pagePayload = { runId: 'run-1', path: 'findings', offset: 37, fingerprint: 'b'.repeat(64) };
  assert.equal((await external({ protocolVersion: 1, type: 'tasks.resultPage', payload: pagePayload })).error.code, 'FORBIDDEN_METHOD');
  const deniedPage = await new Promise(resolve => internalListener({ channel: 'pulse-ui', type: 'tasks.resultPage', payload: pagePayload }, { id: extensionId, url: origin + '/page' }, resolve));
  assert.equal(deniedPage.error.code, 'FORBIDDEN_SOURCE');
  assert.equal(messages.some(message => message.type === 'tasks.resultPage'), false);
  const page = await new Promise(resolve => internalListener({ channel: 'pulse-ui', type: 'tasks.resultPage', payload: pagePayload }, { id: extensionId, url: `chrome-extension://${extensionId}/details.html` }, resolve));
  assert.equal(page.ok, true);
  assert.deepEqual(messages.find(message => message.type === 'tasks.resultPage').payload, pagePayload);
  for (const type of ['agents.list', 'agents.test.start', 'agents.test.get', 'agents.test.cancel', 'github.accounts', 'prompts.list', 'prompts.sync', 'prompts.get', 'tasks.logs', 'webActions.preview', 'webActions.submit', 'webActions.cancel', 'webActions.reconcile']) {
    const payload = type === 'agents.test.start' ? { agent: 'codex', cliPath: 'C:\\private\\codex.exe' } : type === 'github.accounts' || type === 'agents.list' ? {} : { testId: 'test-1' };
    const before = messages.filter(message => message.type === type).length;
    assert.equal((await external({ protocolVersion: 1, type, payload })).error.code, 'FORBIDDEN_METHOD');
    const denied = await new Promise(resolve => internalListener({ channel: 'pulse-ui', type, payload }, { id: extensionId, url: origin + '/page' }, resolve));
    assert.equal(denied.error.code, 'FORBIDDEN_SOURCE');
    assert.equal(messages.filter(message => message.type === type).length, before);
    const allowed = await new Promise(resolve => internalListener({ channel: 'pulse-ui', type, payload }, { id: extensionId, url: `chrome-extension://${extensionId}/options.html` }, resolve));
    assert.equal(allowed.ok, true);
    assert.deepEqual(messages.findLast(message => message.type === type).payload, payload);
    if (type === 'agents.list') assert.deepEqual(allowed.data, installations);
  }
});
