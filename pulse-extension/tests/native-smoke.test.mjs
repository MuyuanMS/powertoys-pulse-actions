import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, mkdir, rm, writeFile, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const host = process.env.PULSE_TEST_HOST ?? fileURLToPath(new URL('../host/bin/Release/net10.0/Pulse.Host.exe', import.meta.url));
function frame(message) {
  const data = Buffer.from(JSON.stringify(message));
  const header = Buffer.alloc(4); header.writeUInt32LE(data.length);
  return Buffer.concat([header, data]);
}

test('published Host exposes explicit review modes and rejects public supplemental-task injection', () => fixture(async root => {
  const modes = ['static', 'build-tests', 'ui-e2e'];
  const task = { requestId: crypto.randomUUID(), actionId: 'scope-frame', actionKind: 'pr-review', repository: 'microsoft/PowerToys', target: { type: 'pr', number: 42 }, expectedHeadSha: 'a'.repeat(40), prompt: 'Offline protocol validation fixture.', reviewOptions: { mode: 'static' } };
  const messages = modes.map(mode => ({ id: mode, type: 'actions.check', payload: { actionKind: 'pr-review', reviewOptions: { mode } } }));
  messages.push({ id: 'injection', type: 'tasks.submit', payload: { task: { ...task, actionKind: 'pr-verify', followUp: { parentRunId: crypto.randomUUID(), recommendationId: 'a'.repeat(64), parentResultFingerprint: 'b'.repeat(64), subject: 'original-pr', revisionSha: 'a'.repeat(40) } }, sourceOrigin: 'https://cautious-memory-r38ze9j.pages.github.io' } });
  messages.push({ id: 'lookup', type: 'tasks.lookup', payload: { task, sourceOrigin: 'https://cautious-memory-r38ze9j.pages.github.io' } });
  const response = await run(['--stdio', '--data-root', root], Buffer.concat(messages.map(message => frame({ protocolVersion: 1, ...message }))));
  assert.equal(response.code, 0); assert.equal(response.frames.length, messages.length);
  for (const [index, mode] of modes.entries()) {
    const reply = response.frames[index];
    assert.equal(reply.ok, true); assert.deepEqual(reply.data.reviewModes, modes);
    assert.deepEqual(reply.data.reviewOptions, { mode });
    assert.equal(reply.data.ready, false, 'An unconfigured fixture cannot start a CLI.');
  }
  assert.equal(response.frames[3].ok, false); assert.equal(response.frames[3].error.code, 'INVALID_REQUEST');
  assert.equal(response.frames[4].ok, true); assert.equal(response.frames[4].data.run, null);
}));

test('published Host pages every v3 finding through real native frames without rewriting the saved report', () => fixture(async root => {
  const runId = crypto.randomUUID(), sha = 'a'.repeat(40), directory = path.join(root, 'runs', runId);
  await mkdir(directory, { recursive: true });
  const task = { requestId: crypto.randomUUID(), actionId: 'large-v3', actionKind: 'pr-review', repository: 'microsoft/powertoys', target: { type: 'pr', number: 42 }, expectedHeadSha: sha, reviewOptions: { mode: 'static' }, prompt: 'Offline full report transport.' };
  const findings = Array.from({ length: 240 }, (_, index) => ({ id: 'finding-' + index, title: 'Confirmed finding ' + index, priority: index === 239 ? 'P0' : 'P3', status: 'open', confirmed: true, path: 'src/Example.cs', line: 1, details: '完整报告证据'.repeat(200), impact: 'An observed wrong result.', trigger: 'The recorded boundary input.', rootCause: 'The inspected source misses this input.', fixSuggestion: 'Handle the input and retain its regression test.', evidence: ['Source and test observation.'], feedback: { body: 'This finding was rechecked.', suggestionId: null } }));
  const result = { schemaVersion: 3, outcome: 'completed', phase: 'reporting', summary: 'One complete rechecked report.', report: { complete: true, rechecked: true, coverage: ['All fixture paths.'], limitations: [] }, findings,
    assessment: { subject: 'original-pr', status: 'failed', summary: 'Confirmed source defects remain.', revisionSha: sha },
    reviewConclusion: { status: 'changes-requested', summary: 'Address the confirmed findings.', revisionSha: sha, blockingUncertainties: [] },
    e2eAssessment: { level: 'not_needed', reason: 'Static evidence and unit tests cover the fixture.', question: '', scenarios: [], expectedResults: [], prerequisites: [], evidence: ['Fixture unit test evidence.'], readiness: 'ready' },
    featureAssessment: null, bugAssessment: null, plans: [], verificationEvidence: [], artifacts: [], diagnostics: [],
    validation: ['context', 'local-review'].map(id => ({ id, name: id, status: 'passed', required: true, details: 'The full check completed.', evidence: ['Fixture proof.'] })),
    review: { headSha: sha, body: 'Rechecked findings.', suggestions: [] }, nextActions: [], nextSteps: [], blockers: [], needsReview: true, structured: true, cliExitCode: 0 };
  await writeFile(path.join(directory, 'task.json'), JSON.stringify({ task, config: {}, sourceOrigin: 'https://cautious-memory-r38ze9j.pages.github.io' }));
  await writeFile(path.join(directory, 'status.json'), JSON.stringify({ state: 'succeeded', exitCode: 0, createdAt: new Date().toISOString(), endedAt: new Date().toISOString(), sequence: 0 }));
  const original = JSON.stringify(result); await writeFile(path.join(directory, 'result.json'), original);
  const call = async (type, payload) => {
    const response = await run(['--stdio', '--data-root', root], frame({ id: crypto.randomUUID(), protocolVersion: 1, type, payload }));
    assert.equal(response.code, 0); assert.equal(response.frames.length, 1); assert.equal(response.frames[0].ok, true, response.frames[0].error?.code); return response.frames[0].data;
  };
  const detail = await call('tasks.get', { runId });
  assert.ok(detail.resultPaging); assert.equal(detail.result.findingSummary.P0, 1);
  const section = detail.resultPaging.sections.find(item => item.path === 'findings');
  const rows = [...detail.result.findings]; let offset = section.nextOffset;
  while (offset !== null) {
    const page = await call('tasks.resultPage', { runId, path: 'findings', offset, fingerprint: detail.resultPaging.fingerprint });
    assert.equal(page.total, 240); assert.ok(page.items.length); rows.push(...page.items); offset = page.nextOffset;
  }
  assert.deepEqual(rows, findings); assert.equal(rows.at(-1).priority, 'P0');
  assert.equal(await readFile(path.join(directory, 'result.json'), 'utf8'), original);
}));

test('development origins permit only the two explicit local ports while production rejects them', () => fixture(async root => {
  const task = { requestId: crypto.randomUUID(), actionId: 'origin-check', actionKind: 'reproduction-setup', repository: 'microsoft/PowerToys', target: { type: 'issue', number: 42 }, prompt: 'Read-only origin validation fixture.' };
  const allowed = ['http://localhost:8080', 'http://127.0.0.1:8080', 'http://localhost:8081', 'http://127.0.0.1:8081'];
  const denied = ['http://localhost:8082', 'https://localhost:8081', 'http://localhost.evil.invalid:8081', 'http://localhost:8081/'];
  const request = sourceOrigin => frame({ id: sourceOrigin, protocolVersion: 1, type: 'tasks.lookup', payload: { task, sourceOrigin } });
  const development = await run(['--stdio', '--development', '--data-root', root], Buffer.concat([...allowed, ...denied].map(request)));
  assert.equal(development.code, 0);
  assert.equal(development.frames.length, allowed.length + denied.length);
  for (const reply of development.frames.slice(0, allowed.length)) { assert.equal(reply.ok, true); assert.equal(reply.data.run, null); }
  for (const reply of development.frames.slice(allowed.length)) { assert.equal(reply.ok, false); assert.equal(reply.error.code, 'ORIGIN_NOT_ALLOWED'); }
  const production = await run(['--stdio', '--data-root', root], Buffer.concat(allowed.map(request)));
  assert.equal(production.code, 0);
  for (const reply of production.frames) { assert.equal(reply.ok, false); assert.equal(reply.error.code, 'ORIGIN_NOT_ALLOWED'); }
}));
function parse(buffer) {
  const result = [];
  while (buffer.length) {
    assert.ok(buffer.length >= 4);
    const size = buffer.readUInt32LE(); assert.ok(size <= 900 * 1024 && size > 0);
    assert.ok(buffer.length >= size + 4);
    result.push(JSON.parse(buffer.subarray(4, size + 4).toString('utf8')));
    buffer = buffer.subarray(size + 4);
  }
  return result;
}
async function run(args, input) {
  const child = spawn(host, args, { windowsHide: true, stdio: 'pipe' });
  const output = [], errors = [];
  child.stdout.on('data', data => output.push(data));
  child.stderr.on('data', data => errors.push(data));
  const timer = setTimeout(() => child.kill(), 10_000);
  try {
    const result = new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', (code, signal) => resolve({ code, signal })); });
    child.stdin.on('error', () => {});
    child.stdin.end(input);
    return { ...await result, frames: parse(Buffer.concat(output)), error: Buffer.concat(errors).toString('utf8') };
  } finally { clearTimeout(timer); }
}
async function fixture(fn) {
  const root = await mkdtemp(path.join(tmpdir(), 'pulse-native-'));
  try { await fn(root); } finally { await rm(root, { recursive: true, force: true }); }
}
test('locally compiled Host handles several framed messages and EOF without stdout diagnostics', () => fixture(async root => {
  const messages = [
    { id: '配置 中文', protocolVersion: 1, type: 'config.get' },
    { id: 'list', protocolVersion: 1, type: 'tasks.list', payload: { view: 'running' } },
    { id: 'version', protocolVersion: 99, type: 'config.get' },
  ];
  const response = await run(['--stdio', '--data-root', root], Buffer.concat(messages.map(frame)));
  assert.equal(response.code, 0); assert.equal(response.error, ''); assert.equal(response.frames.length, 3);
  assert.equal(response.frames[0].id, '配置 中文'); assert.equal(response.frames[0].data.permission, 'yolo');
  assert.deepEqual(response.frames[1].data.runs, []);
  assert.equal(response.frames[2].error.code, 'PROTOCOL_MISMATCH');
}));
test('locally compiled Host admits only the extension ID in its local installation record', () => fixture(async root => {
  await writeFile(path.join(root, 'installation.json'), JSON.stringify({ extensionIds: ['a'.repeat(32)], developmentOrigins: false }));
  const input = frame({ id: 'config', protocolVersion: 1, type: 'config.get' });
  const valid = await run([`chrome-extension://${'a'.repeat(32)}/`, '--data-root', root], input);
  assert.equal(valid.code, 0); assert.equal(valid.frames[0].ok, true);
  const invalid = await run([`chrome-extension://${'b'.repeat(32)}/`, '--data-root', root], input);
  assert.equal(invalid.code, 3); assert.equal(invalid.frames.length, 0); assert.match(invalid.error, /EXTENSION_NOT_ALLOWED/);
}));
test('locally compiled Host rejects oversized framing before reading its body', () => fixture(async root => {
  const header = Buffer.alloc(4); header.writeUInt32LE(0xffffffff);
  const result = await run(['--stdio', '--data-root', root], header);
  assert.equal(result.code, 3); assert.equal(result.frames.length, 0); assert.match(result.error, /INPUT_TOO_LARGE/);
}));

test('published Host serves the exact repository prompt bundle without authentication or synchronization', () => fixture(async root => {
  const saved = JSON.stringify({ githubAccount: 'not-a-signed-in-account', permission: 'read-only' });
  await writeFile(path.join(root, 'config.json'), saved);
  const messages = [
    { id: 'list', protocolVersion: 1, type: 'prompts.list' },
    { id: 'old-sync', protocolVersion: 1, type: 'prompts.sync', payload: { githubAccount: 'not-a-signed-in-account' } },
    { id: 'get', protocolVersion: 1, type: 'prompts.get', payload: { name: 'powertoys-pr-loop-review.prompt.md' } },
  ];
  const response = await run(['--stdio', '--data-root', root], Buffer.concat(messages.map(frame)));
  assert.equal(response.code, 0); assert.equal(response.error, '');
  assert.equal(response.frames.length, 3);
  for (const reply of response.frames) assert.equal(reply.ok, true, reply.error?.code);
  const [catalog, reloaded, prompt] = response.frames.map(reply => reply.data);
  assert.equal(catalog.prompts.length, 8);
  assert.equal(catalog.sourceUrl, 'bundled://Pulse.Host/prompts');
  assert.deepEqual(reloaded, catalog);
  assert.equal(prompt.actionKind, 'pr-review');
  assert.equal(prompt.schemaVersion, 3);
  const source = await readFile(new URL('../prompts/powertoys-pr-loop-review.prompt.md', import.meta.url));
  assert.equal(prompt.content, source.toString('utf8'));
  assert.equal(prompt.sha, createHash('sha256').update(source).digest('hex'));
  assert.equal(await readFile(path.join(root, 'config.json'), 'utf8'), saved);
}));

test('compiled Host persists website drafts across connections without executing GitHub writes', () => fixture(async root => {
  const sourceOrigin = 'https://cautious-memory-r38ze9j.pages.github.io';
  const draft = { requestId: crypto.randomUUID(), actionId: 'issue:42:comment', kind: 'comment', target: { repository: 'microsoft/PowerToys', type: 'issue', number: 42 }, body: 'Offline transport fixture; never submitted.' };
  const request = (type, payload) => frame({ id: crypto.randomUUID(), protocolVersion: 1, type, payload });
  const args = ['--stdio', '--data-root', root];
  const prepare = await run(args, request('webActions.prepare', { draft, sourceOrigin }));
  assert.equal(prepare.code, 0); assert.equal(prepare.frames[0].ok, true);
  const operation = prepare.frames[0].data;
  assert.equal(operation.status, 'prepared'); assert.equal(operation.target.repository, 'microsoft/powertoys');
  assert.equal(operation.body, undefined); assert.equal(operation.account, undefined);
  const reconnect = await run(args, Buffer.concat([
    request('webActions.prepare', { draft, sourceOrigin }),
    request('webActions.get', { operationId: operation.operationId, sourceOrigin }),
    request('webActions.get', { operationId: operation.operationId, sourceOrigin: 'https://evil.invalid' }),
    request('webActions.reconcile', { operationId: operation.operationId }),
    request('webActions.cancel', { operationId: operation.operationId }),
  ]));
  assert.equal(reconnect.frames[0].data.operationId, operation.operationId);
  assert.equal(reconnect.frames[1].data.status, 'prepared');
  assert.equal(reconnect.frames[2].ok, false);
  assert.equal(reconnect.frames[3].data.status, 'prepared');
  assert.equal(reconnect.frames[4].data.status, 'cancelled');
  const disk = JSON.parse(await readFile(path.join(root, 'web-actions', 'operations', operation.operationId + '.json'), 'utf8'));
  assert.deepEqual(disk.completedSteps, []);
  assert.equal(disk.status, 'cancelled');
}));

test('compiled Host maintenance checker sees standalone GitHub submissions', () => fixture(async root => {
  const draft = { requestId: crypto.randomUUID(), actionId: 'maintenance:comment', kind: 'comment', target: { repository: 'microsoft/PowerToys', type: 'issue', number: 42 }, body: 'Offline fixture.' };
  const accepted = await run(['--stdio', '--data-root', root], frame({ id: 'prepare', protocolVersion: 1, type: 'webActions.prepare', payload: { draft, sourceOrigin: 'https://cautious-memory-r38ze9j.pages.github.io' } }));
  const operationId = accepted.frames[0].data.operationId;
  const recordPath = path.join(root, 'web-actions', 'operations', operationId + '.json');
  const record = JSON.parse(await readFile(recordPath, 'utf8'));
  record.status = 'submitting';
  await writeFile(recordPath, JSON.stringify(record));
  const busy = await run(['--has-active', '--data-root', root], Buffer.alloc(0));
  assert.equal(busy.code, 2);
  assert.deepEqual(busy.frames, []);
}));

test('published Host preserves legacy product failure while allowing explicit manual merge preparation and recoverable CI confirmations', () => fixture(async root => {
  const runId = crypto.randomUUID(), sha = 'a'.repeat(40);
  const directory = path.join(root, 'runs', runId);
  await mkdir(directory, { recursive: true });
  const task = { requestId: crypto.randomUUID(), actionId: 'negative-e2e', actionKind: 'e2e', repository: 'microsoft/PowerToys', target: { type: 'pr', number: 42 }, expectedHeadSha: sha, prompt: 'Offline recorded negative E2E report.' };
  const actions = [
    { kind: 'comment', reason: 'Report the observed failure.', body: 'The isolated scenario failed; inspect its recorded evidence.' },
    { kind: 'merge-pr', reason: 'A model proposal that must be disabled.', body: '' },
    { kind: 'trigger-ci', reason: 'Request independent CI verification.', body: '' },
  ];
  const result = {
    schemaVersion: 2, outcome: 'completed', phase: 'reporting', summary: 'The E2E report is complete and records a product defect.',
    assessment: { subject: 'original-pr', status: 'failed', summary: 'The feature scenario failed.', revisionSha: sha },
    findings: [{ id: 'failure', title: 'Feature regression', severity: 'high', status: 'open', path: 'src/example.cs', line: 12, details: 'Controlled negative fixture.', evidence: ['fixture-failure.log'] }],
    validation: ['setup', 'e2e'].map(id => ({ id, name: id, status: 'passed', required: true, details: 'The scenario was executed and its result recorded.', evidence: ['fixture-failure.log'] })),
    artifacts: [], diagnostics: [], nextActions: actions, nextSteps: actions, review: null, needsReview: true, structured: true, cliExitCode: 0, blockers: [],
  };
  const saved = JSON.stringify({ task, sourceOrigin: 'https://cautious-memory-r38ze9j.pages.github.io', config: { agent: 'codex', repoFolder: root, githubAccount: '' } });
  await writeFile(path.join(directory, 'task.json'), saved);
  await writeFile(path.join(directory, 'status.json'), JSON.stringify({ state: 'succeeded', exitCode: 0, createdAt: new Date().toISOString(), endedAt: new Date().toISOString(), sequence: 0 }));
  const resultBytes = JSON.stringify(result);
  await writeFile(path.join(directory, 'result.json'), resultBytes);
  const call = async (type, payload) => {
    const response = await run(['--stdio', '--data-root', root], frame({ id: crypto.randomUUID(), protocolVersion: 1, type, payload }));
    assert.equal(response.code, 0); assert.equal(response.error, ''); assert.equal(response.frames.length, 1);
    return response.frames[0];
  };
  const detail = await call('tasks.get', { runId });
  assert.equal(detail.ok, true);
  const projected = detail.data.result;
  assert.equal(projected.outcome, 'completed'); assert.equal(projected.assessment.status, 'failed');
  assert.deepEqual(projected.findingSummary, { unresolved: 1, high: 1, medium: 0, low: 0 });
  const comment = projected.nextActions.find(item => item.kind === 'comment');
  const merge = projected.nextActions.find(item => item.kind === 'merge-pr');
  const ci = projected.nextActions.find(item => item.kind === 'trigger-ci');
  assert.equal(comment.availability.enabled, true);
  assert.equal(merge.availability.enabled, false); assert.ok(merge.availability.reasons.length);
  assert.equal(ci.availability.enabled, true);
  const manualMerge = await call('resultActions.prepare', { runId, kind: 'merge-pr', attemptId: crypto.randomUUID() });
  assert.equal(manualMerge.ok, true); assert.equal(manualMerge.data.status, 'prepared');
  assert.equal((await call('webActions.cancel', { operationId: manualMerge.data.operationId })).data.status, 'cancelled');
  const attempt = { runId, proposalId: ci.proposalId, attemptId: crypto.randomUUID() };
  const first = await call('resultActions.prepare', attempt);
  assert.equal(first.ok, true); assert.equal(first.data.status, 'prepared');
  assert.equal((await call('resultActions.prepare', attempt)).data.operationId, first.data.operationId);
  assert.equal((await call('webActions.cancel', { operationId: first.data.operationId })).data.status, 'cancelled');
  const second = await call('resultActions.prepare', { ...attempt, attemptId: crypto.randomUUID() });
  assert.equal(second.ok, true); assert.notEqual(second.data.operationId, first.data.operationId);
  const history = await call('resultActions.list', { runId });
  assert.equal(history.ok, true); assert.equal(history.data.operations.length, 3);
  assert.deepEqual(new Set(history.data.operations.map(item => item.status)), new Set(['prepared', 'cancelled']));
  assert.ok(history.data.operations.every(item => item.runId === runId && item.urls.length === 0));
  assert.equal(history.data.operations.filter(item => item.proposalId === ci.proposalId).length, 2);
  assert.equal(await readFile(path.join(directory, 'task.json'), 'utf8'), saved);
  assert.equal(await readFile(path.join(directory, 'result.json'), 'utf8'), resultBytes);
}));
