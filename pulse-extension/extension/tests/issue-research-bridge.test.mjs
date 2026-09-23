import test from 'node:test';
import assert from 'node:assert/strict';
import { publicCapabilities, publicResultMetadata, publicRun, requireWorkflowCapabilities, validateExternal, validateTask } from '../src/policy.ts';

const researchTask = { requestId: 'request-1', actionId: 'feature-research-issue-7', actionKind: 'feature-research', repository: 'microsoft/PowerToys', target: { type: 'issue', number: 7 }, prompt: 'Research this request.', execution: { agent: 'codex', model: '', reasoningEffort: '' } };
test('public Issue kinds and v3 result metadata exclude internal plans and capabilities', () => {
  const caps = { protocolVersion: 1, hostVersion: 'test', agents: {}, workflowKinds: ['issue-fix', 'pr-review', 'reproduction-setup', 'e2e', 'feature-research', 'bug-investigation'], resultSchemaVersions: [2, 3], privatePath: 'private' };
  const output = publicCapabilities(caps);
  assert.deepEqual(output.workflowKinds, caps.workflowKinds); assert.deepEqual(output.resultSchemaVersions, [2, 3]); assert.equal(output.privatePath, undefined);
  for (const actionKind of ['feature-research', 'bug-investigation']) {
    assert.equal(validateTask({ ...researchTask, actionKind }).actionKind, actionKind);
    assert.throws(() => validateTask({ ...researchTask, actionKind, target: { type: 'pr', number: 7 } }), { code: 'INVALID_REQUEST' });
    assert.throws(() => validateTask({ ...researchTask, actionKind, target: undefined }), { code: 'INVALID_REQUEST' });
    requireWorkflowCapabilities(caps, actionKind);
    for (const data of [{}, { workflowKinds: [actionKind], resultSchemaVersions: [2] }, { workflowKinds: ['issue-fix'], resultSchemaVersions: [3] }]) assert.throws(() => requireWorkflowCapabilities(data, actionKind), { code: 'HOST_UPDATE_REQUIRED' });
    for (const type of ['tasks.submit', 'tasks.lookup']) assert.throws(() => validateExternal({ protocolVersion: 1, type, payload: { task: { ...researchTask, actionKind, planSource: { runId: 'private', planId: 'p1' } } } }), { code: 'INVALID_REQUEST' });
  }
  for (const actionKind of ['pr-verify', 'feature-implement', 'issue-verify']) {
    assert.throws(() => validateTask({ ...researchTask, actionKind }), { code: 'INVALID_REQUEST' });
    assert.throws(() => validateExternal({ protocolVersion: 1, type: 'actions.check', payload: { actionKind } }), { code: 'INVALID_REQUEST' });
  }
  for (const update of [{ workflowKinds: ['feature-implement'] }, { workflowKinds: ['feature-research', 'feature-research'] }, { resultSchemaVersions: ['3'] }, { resultSchemaVersions: [3, 3] }]) assert.throws(() => publicCapabilities({ ...caps, ...update }), { code: 'INVALID_RESPONSE' });
  requireWorkflowCapabilities({}, 'issue-fix');
  const result = { schemaVersion: 3, outcome: 'completed', phase: 'reporting', structured: true, summary: 'Research completed.', findings: [{ path: 'private' }], plans: [{ id: 'p1', steps: ['private'] }], featureAssessment: { status: 'ready', evidence: ['private'] }, bugAssessment: { status: 'confirmed' }, nextActions: [{ kind: 'start-task', planId: 'p1' }] };
  const projected = publicRun({ runId: 'run-1', task: researchTask, config: {}, status: { state: 'succeeded', sequence: 1 }, result });
  assert.deepEqual(publicResultMetadata(projected.result), { schemaVersion: 3, outcome: 'completed', phase: 'reporting', structured: true });
  for (const field of ['findings', 'plans', 'featureAssessment', 'bugAssessment', 'nextActions']) assert.equal(projected.result[field], undefined);
  assert.throws(() => publicResultMetadata({ ...result, phase: null }), { code: 'INVALID_RESPONSE' });
});

test('new Issue research requires live Host support before readiness or submission; saved lookup stays read-only', async t => {
  t.mock.method(globalThis, 'setTimeout', () => 1); t.mock.method(globalThis, 'clearTimeout', () => {});
  const origin = 'https://cautious-memory-r38ze9j.pages.github.io';
  const extensionId = 'a'.repeat(32);
  let capabilities = { protocolVersion: 1, hostVersion: 'old', agents: {} };
  let externalListener, internalListener;
  const calls = [];
  globalThis.chrome = {
    runtime: {
      id: extensionId, getURL: path => `chrome-extension://${extensionId}/${path}`, getManifest: () => ({ version: 'test' }),
      onMessageExternal: { addListener: value => { externalListener = value; } }, onMessage: { addListener: value => { internalListener = value; } },
      onStartup: { addListener() {} }, onInstalled: { addListener() {} },
      connectNative: () => {
        let respond;
        return { onMessage: { addListener: value => { respond = value; } }, onDisconnect: { addListener() {} }, disconnect() {}, postMessage: request => {
          calls.push(request);
          const data = request.type === 'hello' ? capabilities : request.type === 'actions.check' ? { actionKind: request.payload.actionKind, ready: true, blockers: [] }
            : request.type === 'tasks.submit' ? { runId: 'aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa', task: request.payload.task, config: { agent: 'codex' }, status: { state: 'accepted', sequence: 1 } }
            : request.type === 'tasks.lookup' ? { run: null } : { runs: [], runningCount: 0, unreadCount: 0 };
          queueMicrotask(() => respond({ id: request.id, protocolVersion: 1, ok: true, data }));
        } };
      },
    },
    storage: { local: { get: async () => ({}), set: async () => {} } },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {}, setTitle: async () => {} },
    alarms: { create: async () => {}, onAlarm: { addListener() {} } },
  };
  await import('../src/background.ts');
  const send = (type, payload) => new Promise(resolve => externalListener({ protocolVersion: 1, type, payload }, { origin, url: origin + '/' }, resolve));
  for (const capability of [{}, { workflowKinds: ['feature-research'], resultSchemaVersions: [2] }, { workflowKinds: ['bug-investigation'], resultSchemaVersions: [3] }]) {
    capabilities = { protocolVersion: 1, hostVersion: 'test', agents: {}, ...capability };
    assert.equal((await send('tasks.submit', { task: researchTask })).error.code, 'HOST_UPDATE_REQUIRED');
    assert.equal((await send('actions.check', { actionKind: researchTask.actionKind })).error.code, 'HOST_UPDATE_REQUIRED');
  }
  assert.equal(calls.some(call => call.type === 'tasks.submit' || call.type === 'actions.check'), false);
  assert.deepEqual((await send('tasks.lookup', { task: researchTask })).data, { run: null });
  capabilities = { protocolVersion: 1, hostVersion: 'test', agents: {}, workflowKinds: ['feature-research', 'bug-investigation'], resultSchemaVersions: [2, 3] };
  assert.equal((await send('actions.check', { actionKind: researchTask.actionKind })).data.ready, true);
  const submitted = await send('tasks.submit', { task: researchTask });
  assert.equal(submitted.ok, true); assert.equal(submitted.data.actionKind, 'feature-research');
  assert.deepEqual(calls.find(call => call.type === 'tasks.submit').payload, { task: researchTask, sourceOrigin: origin });
  assert.equal((await send('tasks.startFromResult', { runId: 'private' })).error.code, 'FORBIDDEN_METHOD');
  const forbidden = await new Promise(resolve => internalListener({ channel: 'pulse-ui', type: 'tasks.startFromResult', payload: {} }, { id: extensionId, url: origin + '/' }, resolve));
  assert.equal(forbidden.error.code, 'FORBIDDEN_SOURCE');
  const payload = { runId: 'aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa', proposalId: 'saved-plan', requestId: 'new-request' };
  const allowed = await new Promise(resolve => internalListener({ channel: 'pulse-ui', type: 'tasks.startFromResult', payload }, { id: extensionId, url: `chrome-extension://${extensionId}/details.html` }, resolve));
  assert.equal(allowed.ok, true); assert.deepEqual(calls.find(call => call.type === 'tasks.startFromResult').payload, payload);
});
