import test from 'node:test';
import assert from 'node:assert/strict';

test('read-only task lookup restores only the exact origin-associated task and never submits work', async t => {
  t.mock.method(globalThis, 'setTimeout', () => 1);
  t.mock.method(globalThis, 'clearTimeout', () => {});
  const origin = 'https://cautious-memory-r38ze9j.pages.github.io';
  const extensionId = 'a'.repeat(32);
  const task = { requestId: '12345678-1234-1234-1234-123456789abc', actionId: 'pr:7:review', actionKind: 'pr-review', repository: 'microsoft/PowerToys', expectedHeadSha: 'A'.repeat(40), target: { type: 'pr', number: 7 }, prompt: 'Private submitted prompt', context: { b: 2, a: 1 }, execution: { agent: 'codex', model: '', reasoningEffort: '' } };
  const run = { runId: '87654321-1234-1234-1234-123456789abc', task: { ...task, repository: 'microsoft/powertoys', expectedHeadSha: 'a'.repeat(40), context: { a: 1, b: 2 } }, config: { agent: 'codex', cliPath: 'C:\\private\\codex.exe', repoFolder: 'C:\\private' }, status: { state: 'failed', sequence: 1, error: null }, view: { read: false, handled: false }, result: null };
  const messages = [];
  const storage = {};
  let lookupResult = { run: null, privatePrompt: task.prompt };
  let externalListener;
  let internalListener;
  globalThis.chrome = {
    runtime: {
      id: extensionId, getURL: path => `chrome-extension://${extensionId}/${path}`, getManifest: () => ({ version: 'test' }),
      onMessageExternal: { addListener: listener => { externalListener = listener; } },
      onMessage: { addListener: listener => { internalListener = listener; } },
      onStartup: { addListener() {} }, onInstalled: { addListener() {} },
      connectNative: () => {
        let reply;
        return {
          onMessage: { addListener: listener => { reply = listener; } }, onDisconnect: { addListener() {} }, disconnect() {},
          postMessage: message => {
            messages.push(message);
            assert.notEqual(message.type, 'tasks.submit', 'Lookup must never submit a task.');
            const data = message.type === 'tasks.lookup' ? lookupResult : message.type === 'tasks.get' ? run : { runs: [], runningCount: 0, unreadCount: 0 };
            queueMicrotask(() => reply({ id: message.id, protocolVersion: 1, ok: true, data }));
          },
        };
      },
    },
    storage: { local: { get: async key => ({ [key]: storage[key] }), set: async values => Object.assign(storage, values) } },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {}, setTitle: async () => {} },
    alarms: { create: async () => {}, onAlarm: { addListener() {} } },
  };
  await import('../src/background.ts');
  const sender = { url: origin + '/page', origin };
  const external = (type, payload, from = sender) => new Promise(resolve => externalListener({ protocolVersion: 1, type, payload }, from, resolve));
  assert.ok((await external('bridge.hello', {})).data.methods.includes('tasks.lookup'));
  for (const from of [{ url: 'https://evil.test/' }, { ...sender, id: 'other-extension' }, { ...sender, origin: 'https://evil.test' }]) assert.equal((await external('tasks.lookup', { task }, from)).error.code, 'FORBIDDEN_ORIGIN');
  assert.equal(messages.filter(message => message.type === 'tasks.lookup').length, 0);
  for (const payload of [{ task, sourceOrigin: 'https://evil.test' }, { task, command: 'submit' }, { task: { ...task, permission: 'yolo' } }]) assert.equal((await external('tasks.lookup', payload)).error.code, 'INVALID_REQUEST');
  assert.equal(messages.filter(message => message.type === 'tasks.lookup').length, 0);
  const absent = await external('tasks.lookup', { task });
  assert.deepEqual(absent.data, { run: null });
  assert.equal(storage['pulse.origin-grants.v1'], undefined);
  assert.equal((await external('tasks.get', { runId: run.runId })).error.code, 'FORBIDDEN_RUN');
  assert.deepEqual(messages.findLast(message => message.type === 'tasks.lookup').payload, { task, sourceOrigin: origin });
  for (const wrongTask of [{ ...run.task, prompt: 'Another prompt' }, { ...run.task, target: { type: 'pr', number: 8 } }, { ...run.task, context: { a: 9, b: 2 } }, { ...run.task, expectedHeadSha: 'b'.repeat(40) }, { ...run.task, execution: { agent: 'codex' } }]) {
    lookupResult = { run: { ...run, task: wrongTask } };
    assert.equal((await external('tasks.lookup', { task })).error.code, 'INVALID_RESPONSE');
    assert.equal(storage['pulse.origin-grants.v1'], undefined);
  }
  lookupResult = {};
  assert.equal((await external('tasks.lookup', { task })).error.code, 'INVALID_RESPONSE');
  lookupResult = { run };
  const found = await external('tasks.lookup', { task });
  assert.equal(found.ok, true); assert.equal(found.data.run.runId, run.runId);
  assert.equal(found.data.run.config, undefined); assert.equal(found.data.run.task, undefined); assert.equal(found.data.run.prompt, undefined); assert.equal(found.data.run.execution, undefined);
  assert.equal(found.data.run.result, undefined); assert.equal(found.data.run.status.error, undefined);
  assert.deepEqual(storage['pulse.origin-grants.v1'], [{ origin, runId: run.runId, requestId: task.requestId, actionId: task.actionId }]);
  assert.equal((await external('tasks.get', { runId: run.runId })).data.runId, run.runId);
  const forbiddenInternal = await new Promise(resolve => internalListener({ channel: 'pulse-ui', type: 'tasks.lookup', payload: { task, sourceOrigin: origin } }, { id: extensionId, url: origin + '/page' }, resolve));
  assert.equal(forbiddenInternal.error.code, 'FORBIDDEN_SOURCE');
  const allowedInternal = await new Promise(resolve => internalListener({ channel: 'pulse-ui', type: 'tasks.lookup', payload: { task, sourceOrigin: origin } }, { id: extensionId, url: `chrome-extension://${extensionId}/popup.html` }, resolve));
  assert.equal(allowedInternal.data.run.config.repoFolder, 'C:\\private');
  for (const actionKind of ['issue-fix', 'reproduction-setup', 'e2e']) {
    const savedTask = { ...task, requestId: `saved-${actionKind}`, actionKind };
    delete savedTask.target; delete savedTask.expectedHeadSha;
    const savedRun = { ...run, runId: `historical-${actionKind}`, task: { ...savedTask, repository: 'microsoft/powertoys' } };
    const before = messages.length;
    assert.equal((await external('tasks.submit', { task: savedTask })).error.code, 'INVALID_REQUEST');
    assert.equal(messages.length, before, 'An unlinked new submission must never reach the Host.');
    lookupResult = { run: null };
    assert.deepEqual((await external('tasks.lookup', { task: savedTask })).data, { run: null });
    lookupResult = { run: savedRun };
    const recovered = await external('tasks.lookup', { task: savedTask });
    assert.equal(recovered.ok, true); assert.equal(recovered.data.run.runId, savedRun.runId);
    assert.equal(recovered.data.run.target, undefined, 'Lookup must not invent a linked target.');
    assert.deepEqual(messages.findLast(message => message.type === 'tasks.lookup').payload.task, savedTask);
    lookupResult = { run: { ...savedRun, task: { ...savedTask, prompt: 'Changed original request' } } };
    assert.equal((await external('tasks.lookup', { task: savedTask })).error.code, 'INVALID_RESPONSE');
  }
  assert.equal(messages.filter(message => message.type === 'tasks.submit').length, 0);
});
