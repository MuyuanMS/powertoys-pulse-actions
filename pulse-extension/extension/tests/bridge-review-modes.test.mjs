import test from 'node:test';
import assert from 'node:assert/strict';

test('scoped review bridge checks real Host support, freezes accepted scope, and keeps follow-ups private', async t => {
  t.mock.method(globalThis, 'setTimeout', () => 1);
  t.mock.method(globalThis, 'clearTimeout', () => {});
  const extensionId = 'a'.repeat(32);
  const origin = 'https://cautious-memory-r38ze9j.pages.github.io';
  const modes = ['static', 'build-tests', 'ui-e2e'];
  const task = { requestId: '12345678-1234-1234-1234-123456789abc', actionId: 'pr:7:review', actionKind: 'pr-review', repository: 'microsoft/PowerToys', target: { type: 'pr', number: 7 }, expectedHeadSha: 'a'.repeat(40), prompt: 'Review this PR.', reviewOptions: { mode: 'static' } };
  const run = { runId: '87654321-1234-1234-1234-123456789abc', task, config: { privatePath: 'secret' }, status: { state: 'accepted', sequence: 1 }, view: { read: false, handled: false } };
  const messages = [];
  const storage = {};
  let capabilities = { protocolVersion: 1, hostVersion: 'test', agents: {}, reviewModes: modes };
  let readiness = { actionKind: 'pr-review', ready: true, blockers: [], reviewModes: modes, reviewOptions: { mode: 'static' }, sourcePath: 'secret' };
  let readinessError;
  let returnedRun = run;
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
            const data = message.type === 'hello' ? capabilities : message.type === 'actions.check' ? readiness
              : message.type === 'tasks.list' ? { runs: [], runningCount: 0, unreadCount: 0 }
              : message.type === 'tasks.lookup' ? { run: returnedRun } : returnedRun;
            queueMicrotask(() => reply(message.type === 'actions.check' && readinessError
              ? { id: message.id, protocolVersion: 1, ok: false, error: readinessError }
              : { id: message.id, protocolVersion: 1, ok: true, data }));
          },
        };
      },
    },
    storage: { local: { get: async key => ({ [key]: storage[key] }), set: async values => Object.assign(storage, values) } },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {}, setTitle: async () => {} },
    alarms: { create: async () => {}, onAlarm: { addListener() {} } },
  };
  await import('../src/background.ts');
  const external = (type, payload = {}) => new Promise(resolve => externalListener({ protocolVersion: 1, type, payload }, { url: origin + '/page', origin }, resolve));
  const internal = (type, payload, url = `chrome-extension://${extensionId}/details.html`) => new Promise(resolve => internalListener({ channel: 'pulse-ui', type, payload }, { id: extensionId, url }, resolve));
  const check = () => external('actions.check', { actionKind: 'pr-review', reviewOptions: { mode: 'static' } });
  const submit = () => external('tasks.submit', { task });

  const hello = await external('bridge.hello');
  assert.equal(hello.ok, true);
  assert.equal(hello.data.reviewModes, undefined, 'Extension-only discovery must not advertise Host support.');
  assert.deepEqual((await external('hello')).data.reviewModes, modes);
  const ready = await check();
  assert.equal(ready.ok, true);
  assert.deepEqual(ready.data.reviewOptions, { mode: 'static' });
  assert.deepEqual(ready.data.reviewModes, modes);
  assert.equal(ready.data.sourcePath, undefined);
  assert.deepEqual(messages.findLast(message => message.type === 'actions.check').payload, { actionKind: 'pr-review', reviewOptions: { mode: 'static' } });

  readiness = { ...readiness, reviewOptions: { mode: 'ui-e2e' } };
  assert.equal((await check()).error.code, 'INVALID_RESPONSE');
  readiness = { ...readiness, reviewOptions: undefined };
  assert.equal((await check()).error.code, 'INVALID_RESPONSE');
  readiness = { ...readiness, reviewModes: undefined };
  assert.equal((await check()).error.code, 'HOST_UPDATE_REQUIRED');
  readinessError = { code: 'INVALID_REQUEST', message: 'Unsupported field: reviewOptions' };
  assert.equal((await check()).error.code, 'HOST_UPDATE_REQUIRED');
  readinessError = undefined;

  capabilities = { ...capabilities, reviewModes: undefined };
  assert.equal((await submit()).error.code, 'HOST_UPDATE_REQUIRED');
  assert.equal(messages.some(message => message.type === 'tasks.submit'), false, 'An old Host must never start a wider legacy review.');
  capabilities = { ...capabilities, reviewModes: ['ui-e2e'] };
  assert.equal((await submit()).error.code, 'HOST_UPDATE_REQUIRED');
  assert.equal(messages.some(message => message.type === 'tasks.submit'), false);
  capabilities = { ...capabilities, reviewModes: modes };
  for (const reviewOptions of [undefined, { mode: 'ui-e2e' }, { mode: 'unknown' }]) {
    returnedRun = { ...run, task: { ...task, reviewOptions } };
    assert.equal((await submit()).error.code, 'INVALID_RESPONSE');
    assert.equal(storage['pulse.origin-grants.v1'], undefined);
  }
  returnedRun = run;
  const accepted = await submit();
  assert.equal(accepted.ok, true);
  assert.deepEqual(accepted.data.reviewOptions, task.reviewOptions);
  assert.equal(accepted.data.config, undefined);
  assert.deepEqual(messages.findLast(message => message.type === 'tasks.submit').payload, { task, sourceOrigin: origin });
  assert.deepEqual((await external('tasks.get', { runId: run.runId })).data.reviewOptions, task.reviewOptions);

  capabilities = { ...capabilities, reviewModes: undefined };
  const submissions = messages.filter(message => message.type === 'tasks.submit').length;
  const lookup = await external('tasks.lookup', { task });
  assert.equal(lookup.ok, true, 'Recovery of an accepted exact task remains read-only and does not depend on new-run capabilities.');
  assert.deepEqual(lookup.data.run.reviewOptions, task.reviewOptions);
  assert.equal(messages.filter(message => message.type === 'tasks.submit').length, submissions);
  returnedRun = { ...run, task: { ...task, reviewOptions: { mode: 'ui-e2e' } } };
  assert.equal((await external('tasks.lookup', { task })).error.code, 'INVALID_RESPONSE');

  for (const type of ['reviews.verify', 'reviews.related']) {
    const payload = { runId: run.runId, requestId: 'verification-request' };
    const before = messages.filter(message => message.type === type).length;
    assert.equal((await external(type, payload)).error.code, 'FORBIDDEN_METHOD');
    assert.equal((await internal(type, payload, origin + '/page')).error.code, 'FORBIDDEN_SOURCE');
    assert.equal(messages.filter(message => message.type === type).length, before);
    assert.equal((await internal(type, payload)).ok, true);
    assert.deepEqual(messages.findLast(message => message.type === type).payload, payload);
  }
  for (const field of ['followUp', 'verificationOf']) {
    assert.equal((await external('tasks.submit', { task: { ...task, [field]: { parentRunId: run.runId } } })).error.code, 'INVALID_REQUEST');
  }
  assert.equal((await external('tasks.submit', { task: { ...task, actionKind: 'pr-verify' } })).error.code, 'INVALID_REQUEST');
});
