import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { validateExternal, validateTask, validateSavedTask, validateExecution, validateReviewOptions, validateWebActionDraft, senderOrigin, publicRun, publicEvent, publicCapabilities, publicAgentDefaults, publicReadiness, publicObservedExecution, publicResultMetadata, publicWebAction, safeHttpsUrl, sameTaskIdentity, requireReviewMode } from '../src/policy.ts';

const origin = 'https://cautious-memory-r38ze9j.pages.github.io';
const task = { requestId: 'test-request', actionId: 'issue:7:fix', actionKind: 'issue-fix', repository: 'microsoft/PowerToys', target: { type: 'issue', number: 7 }, prompt: '只读分析，包含中文、"quotes"、\n多行。' };
test('production manifest and runtime origin checks reject lookalikes and localhost', async () => {
  const manifest = JSON.parse(await readFile(new URL('../manifest.json', import.meta.url), 'utf8'));
  assert.deepEqual(manifest.externally_connectable.matches, [origin + '/*']);
  assert.deepEqual(manifest.permissions, ['nativeMessaging', 'storage', 'alarms']);
  assert.equal(senderOrigin(origin + '/pulse', [origin]), origin);
  for (const url of ['http://localhost:8080', origin + '.evil.test/', 'https://evil.test/' + origin, undefined]) assert.throws(() => senderOrigin(url, [origin]));
});
test('external channel only accepts the bounded versioned business contract', () => {
  assert.deepEqual(validateExternal({ protocolVersion: 1, type: 'tasks.submit', payload: { task } }).payload.task, task);
  for (const type of ['config.get', 'config.save', 'agents.list', 'agents.test.start', 'agents.test.get', 'agents.test.cancel', 'github.accounts', 'tasks.list', 'tasks.cancel', 'tasks.read', 'tasks.handle', 'tasks.rerun', 'operations.submit', 'operations.preview', 'resultActions.prepare', 'resultActions.list', 'webActions.prepare', 'webActions.get', 'webActions.preview', 'webActions.submit', 'webActions.cancel', 'webActions.reconcile']) assert.throws(() => validateExternal({ protocolVersion: 1, type, payload: {} }), { code: 'FORBIDDEN_METHOD' });
  assert.throws(() => validateExternal({ protocolVersion: 2, type: 'hello' }), { code: 'PROTOCOL_MISMATCH' });
  assert.throws(() => validateExternal({ protocolVersion: 1, type: 'tasks.submit', payload: { task, sourceOrigin: 'https://evil.test' } }));
  assert.throws(() => validateExternal({ protocolVersion: 1, type: 'tasks.events', payload: { runId: 'abc', afterSequence: -1 } }));
  assert.throws(() => validateExternal({ protocolVersion: 1, type: 'tasks.events', payload: { runId: 'abc', afterSequence: 0, limit: 101 } }));
});
test('new task admission requires the matching target while saved targetless identities remain readable', () => {
  for (const actionKind of ['issue-fix', 'reproduction-setup', 'feature-research', 'bug-investigation', 'pr-review', 'e2e']) {
    const type = ['pr-review', 'e2e'].includes(actionKind) ? 'pr' : 'issue';
    const linked = { ...task, actionKind, target: { type, number: 7 }, ...(type === 'pr' ? { expectedHeadSha: 'A'.repeat(40) } : {}) };
    assert.deepEqual(validateTask(linked), linked);
    const missing = { ...linked }; delete missing.target; delete missing.expectedHeadSha;
    assert.throws(() => validateTask(missing), { code: 'INVALID_REQUEST' });
    assert.throws(() => validateExternal({ protocolVersion: 1, type: 'tasks.submit', payload: { task: missing } }), { code: 'INVALID_REQUEST' });
    const mismatched = { ...linked, target: { type: type === 'pr' ? 'issue' : 'pr', number: 7 } };
    assert.throws(() => validateTask(mismatched), { code: 'INVALID_REQUEST' });
    assert.throws(() => validateSavedTask(mismatched), { code: 'INVALID_REQUEST' });
    if (['issue-fix', 'reproduction-setup', 'e2e'].includes(actionKind)) {
      const frozen = structuredClone(missing);
      assert.deepEqual(validateSavedTask(missing), frozen);
      assert.deepEqual(validateExternal({ protocolVersion: 1, type: 'tasks.lookup', payload: { task: missing } }).payload.task, frozen);
      assert.equal(sameTaskIdentity(missing, { ...missing, repository: 'MICROSOFT/POWERTOYS' }), true);
      assert.equal(sameTaskIdentity(missing, { ...missing, prompt: 'Changed request' }), false);
      assert.equal(sameTaskIdentity(missing, linked), false, 'Recovery cannot invent a target for a saved request.');
      assert.deepEqual(missing, frozen, 'Validation and identity checks preserve the frozen draft.');
    } else assert.throws(() => validateSavedTask(missing), { code: 'INVALID_REQUEST' });
  }
});
test('bridge routes have fixed destinations and only accept supported readiness and target fields', () => {
  for (const type of ['bridge.hello', 'agents.defaults', 'ui.openSettings', 'ui.openTasks']) {
    assert.deepEqual(validateExternal({ protocolVersion: 1, type }).payload, {});
    for (const payload of [{ url: 'https://evil.test' }, { path: 'secrets.json' }, { sourceOrigin: origin }, { token: 'secret' }]) assert.throws(() => validateExternal({ protocolVersion: 1, type, payload }));
  }
  assert.equal(validateExternal({ protocolVersion: 1, type: 'actions.check', payload: { actionKind: 'reproduction-setup' } }).type, 'actions.check');
  assert.throws(() => validateExternal({ protocolVersion: 1, type: 'actions.check', payload: { actionKind: 'shell' } }));
  assert.deepEqual(validateExternal({ protocolVersion: 1, type: 'targets.get', payload: { target: { type: 'pr', number: 7 } } }).payload.target, { type: 'pr', number: 7 });
  for (const target of [{ type: 'issue', number: 7 }, { type: 'pr', number: 0 }, { type: 'pr', number: 7, repository: 'evil/repo' }]) assert.throws(() => validateExternal({ protocolVersion: 1, type: 'targets.get', payload: { target } }));
  assert.throws(() => validateExternal({ protocolVersion: 1, type: 'ui.openTask', payload: { runId: 'run-1', url: 'https://evil.test' } }));
  assert.throws(() => validateExternal({ protocolVersion: 1, type: 'github.get', payload: { operationId: '../config.json' } }));
});
test('execution overrides preserve omission and explicit CLI default while excluding commands and permissions', () => {
  assert.deepEqual(validateExecution({}), {});
  const override = { agent: 'copilot', model: '', reasoningEffort: '' };
  assert.deepEqual(validateExecution(override), override);
  assert.deepEqual(validateTask({ ...task, execution: override }).execution, override);
  assert.equal(validateTask(task).execution, undefined);
  const check = validateExternal({ protocolVersion: 1, type: 'actions.check', payload: { actionKind: 'e2e', execution: override } });
  assert.deepEqual(check.payload.execution, override);
  assert.deepEqual(validateExecution({ model: 'custom-model.v2', reasoningEffort: 'none' }), { model: 'custom-model.v2', reasoningEffort: 'none' });
  for (const execution of [null, [], 'codex', { agent: 'shell' }, { model: 'a'.repeat(129) }, { model: 'model\n' }, { model: '\n' }, { model: '--dangerous' }, { model: 'C:\\private' }, { model: 'gpt; command' }, { reasoningEffort: 'arbitrary' }, { agent: 'codex', reasoningEffort: 'none' }, { agent: 'codex', reasoningEffort: 'max' }, { agent: 'copilot', reasoningEffort: 'ultra' }]) assert.throws(() => validateTask({ ...task, execution }));
  for (const field of ['permission', 'command', 'args', 'cliPath', 'cliSelections', 'path', 'resolvedPath', 'installation', 'worktreeRoot', 'sourceOrigin', 'githubAccount', 'agentDefaults']) assert.throws(() => validateExecution({ [field]: 'override' }));
  assert.deepEqual(validateExecution({ agent: 'codex', model: '', reasoningEffort: 'ultra' }), { agent: 'codex', model: '', reasoningEffort: 'ultra' });
  assert.deepEqual(validateExecution({ agent: 'copilot', reasoningEffort: 'max' }), { agent: 'copilot', reasoningEffort: 'max' });
});

test('review modes are explicit PR-only choices and remain part of accepted task identity', () => {
  const pr = { ...task, actionKind: 'pr-review', target: { type: 'pr', number: 7 }, expectedHeadSha: 'a'.repeat(40) };
  assert.equal(validateTask(pr).reviewOptions, undefined, 'Legacy requests retain their unrecorded mode.');
  for (const mode of ['static', 'build-tests', 'ui-e2e']) {
    const scoped = { ...pr, reviewOptions: { mode } };
    assert.deepEqual(validateTask(scoped).reviewOptions, { mode });
    assert.deepEqual(validateExternal({ protocolVersion: 1, type: 'actions.check', payload: { actionKind: 'pr-review', reviewOptions: { mode } } }).payload.reviewOptions, { mode });
    assert.equal(sameTaskIdentity(scoped, { ...scoped, expectedHeadSha: 'A'.repeat(40) }), true);
    assert.equal(sameTaskIdentity(scoped, pr), false, 'An accepted legacy review must not acquire a new mode.');
    assert.equal(sameTaskIdentity(scoped, { ...pr, reviewOptions: { mode: mode === 'static' ? 'ui-e2e' : 'static' } }), false);
    for (const actionKind of ['issue-fix', 'reproduction-setup', 'e2e', 'pr-verify']) {
      assert.throws(() => validateTask({ ...scoped, actionKind }), { code: 'INVALID_REQUEST' });
      assert.throws(() => validateExternal({ protocolVersion: 1, type: 'actions.check', payload: { actionKind, reviewOptions: { mode } } }), { code: 'INVALID_REQUEST' });
    }
  }
  for (const reviewOptions of [null, [], {}, 'static', { mode: 'all' }, { mode: ' static' }, { mode: 'static', source: 'host' }, { mode: 'static', allowBuild: true }]) {
    assert.throws(() => validateReviewOptions(reviewOptions), { code: 'INVALID_REQUEST' });
    assert.throws(() => validateTask({ ...pr, reviewOptions }), { code: 'INVALID_REQUEST' });
  }
  assert.equal(sameTaskIdentity({ ...pr, reviewOptions: { mode: 'unknown' } }, pr), false);
  for (const field of ['followUp', 'verificationOf', 'provenance', 'reviewSource']) for (const type of ['tasks.submit', 'tasks.lookup']) {
    assert.throws(() => validateExternal({ protocolVersion: 1, type, payload: { task: { ...pr, [field]: { parentRunId: 'spoofed' } } } }), { code: 'INVALID_REQUEST' });
  }
  for (const type of ['reviews.verify', 'reviews.related']) assert.throws(() => validateExternal({ protocolVersion: 1, type, payload: {} }), { code: 'FORBIDDEN_METHOD' });
});

test('public review capability and mode projections preserve Host support without leaking provenance', () => {
  const modes = ['static', 'build-tests', 'ui-e2e'];
  const caps = { protocolVersion: 1, hostVersion: 'test', agents: {}, reviewModes: modes, internalModes: ['pr-verify'] };
  assert.deepEqual(publicCapabilities(caps).reviewModes, modes);
  assert.notEqual(publicCapabilities(caps).reviewModes, modes);
  assert.equal(publicCapabilities(caps).internalModes, undefined);
  assert.deepEqual(publicReadiness({ actionKind: 'pr-review', ready: true, blockers: [], reviewModes: modes, reviewOptions: { mode: 'static' } }), { actionKind: 'pr-review', ready: true, blockers: [], reviewModes: modes, reviewOptions: { mode: 'static' } });
  requireReviewMode(modes, { mode: 'ui-e2e' });
  for (const supported of [undefined, [], ['static']]) assert.throws(() => requireReviewMode(supported, { mode: 'ui-e2e' }), { code: 'HOST_UPDATE_REQUIRED' });
  for (const invalid of [null, 'static', ['full'], ['static', 'static']]) assert.throws(() => publicCapabilities({ ...caps, reviewModes: invalid }), { code: 'INVALID_RESPONSE' });
  const run = { runId: 'run-1', task: { ...task, actionKind: 'pr-review', reviewOptions: { mode: 'static' } }, config: {}, status: { state: 'accepted' }, reviewSource: { privatePath: 'secret' } };
  assert.deepEqual(publicRun(run).reviewOptions, { mode: 'static' });
  assert.equal(publicRun(run).reviewSource, undefined);
  assert.throws(() => publicRun({ ...run, task: { ...run.task, reviewOptions: { mode: 'unknown' } } }), { code: 'INVALID_RESPONSE' });
  assert.throws(() => publicReadiness({ actionKind: 'e2e', ready: true, blockers: [], reviewOptions: { mode: 'static' } }), { code: 'INVALID_RESPONSE' });
});
test('agent defaults expose model and reasoning choices without private configuration', () => {
  const source = { defaultAgent: 'copilot', defaults: { codex: { model: 'custom-model.v2', reasoningEffort: 'high', cliPath: 'private' }, copilot: { model: '', reasoningEffort: '', account: 'private' } }, reasoningEfforts: { codex: ['minimal', 'high', 'ultra'], copilot: ['none', 'max'] }, githubAccount: 'private', repoFolder: 'private', cliSelections: { codex: 'C:\\private\\codex.exe' }, installations: ['private'] };
  const output = publicAgentDefaults(source);
  assert.deepEqual(output, { defaultAgent: 'copilot', defaults: { codex: { model: 'custom-model.v2', reasoningEffort: 'high' }, copilot: { model: '', reasoningEffort: '' } }, reasoningEfforts: { codex: ['minimal', 'high', 'ultra'], copilot: ['none', 'max'] } });
  assert.throws(() => publicAgentDefaults({ ...source, reasoningEfforts: { ...source.reasoningEfforts, codex: ['private-path'] } }));
});
test('installation discovery and choice remain internal and public readiness reveals only repair guidance', () => {
  const privateProbe = { available: false, path: 'C:\\private\\codex.cmd', resolvedPath: 'C:\\private\\codex.exe', aliases: ['C:\\private\\alias.cmd'], source: 'private source', sources: ['private source'], error: { code: 'CLI_SELECTION_REQUIRED', message: 'Choose a CLI installation in extension Settings.', raw: 'C:\\private' } };
  const capabilities = publicCapabilities({ protocolVersion: 1, hostVersion: 'test', agents: { codex: privateProbe, copilot: { available: false } }, github: { account: 'private' }, installations: { codex: [privateProbe] }, cliSelections: { codex: privateProbe.path } });
  assert.deepEqual(capabilities, { protocolVersion: 1, hostVersion: 'test', agents: { codex: { available: false }, copilot: { available: false } } });
  assert.deepEqual(publicReadiness({ actionKind: 'issue-fix', ready: false, blockers: [{ code: 'CLI_SELECTION_REQUIRED', message: 'Choose a CLI installation in extension Settings.', guidance: 'Open Settings.', cliPath: privateProbe.path }], installations: [privateProbe], cliSelections: { codex: privateProbe.path } }), { actionKind: 'issue-fix', ready: false, blockers: [{ code: 'CLI_SELECTION_REQUIRED', message: 'Choose a CLI installation in extension Settings.', guidance: 'Open Settings.' }] });
  for (const type of ['tasks.submit', 'tasks.lookup']) {
    for (const field of ['cliSelections', 'cliPath', 'resolvedPath', 'installation', 'installations']) assert.throws(() => validateExternal({ protocolVersion: 1, type, payload: { task: { ...task, [field]: privateProbe.path } } }), { code: 'INVALID_REQUEST' });
  }
  for (const payload of [{ actionKind: 'issue-fix', cliPath: privateProbe.path }, { actionKind: 'issue-fix', execution: { agent: 'codex', cliPath: privateProbe.path } }]) assert.throws(() => validateExternal({ protocolVersion: 1, type: 'actions.check', payload }), { code: 'INVALID_REQUEST' });
});
test('public task execution reports recorded values and keeps historical missing values distinct from CLI defaults', () => {
  const base = { runId: 'run-1', task, status: { state: 'accepted', sequence: 0, error: null }, result: null, config: { agent: 'copilot', model: 'custom-model.v2', reasoningEffort: '', executionSource: { agent: 'task', model: 'default', reasoningEffort: 'cli', secret: 'private' }, cliPath: 'C:\\private', repoFolder: 'C:\\private' } };
  const output = publicRun(base);
  assert.deepEqual(output.execution, { agent: 'copilot', model: 'custom-model.v2', reasoningEffort: '', modelSource: 'agent-default', reasoningEffortSource: 'cli-default' });
  assert.equal(output.config, undefined); assert.equal(output.result, undefined); assert.equal(output.status.error, undefined);
  assert.equal(publicRun({ ...base, config: { agent: 'codex', cliPath: 'C:\\private' } }).execution, undefined);
  assert.equal(publicRun({ ...base, config: { agent: 'codex', model: '', cliPath: 'C:\\private' } }).execution, undefined);
  assert.deepEqual(publicRun({ ...base, config: { agent: 'codex', model: '', reasoningEffort: '', executionSource: { agent: 'default', model: 'cli', reasoningEffort: 'cli' } } }).execution, { agent: 'codex', model: '', reasoningEffort: '', modelSource: 'cli-default', reasoningEffortSource: 'cli-default' });
});
test('public v2 result includes workflow outcome metadata without findings or private diagnostics', () => {
  const run = { runId: 'run-1', task, config: { agent: 'codex', repoFolder: 'C:\\private' }, status: { state: 'succeeded', sequence: 7, error: null }, result: {
    schemaVersion: 2, outcome: 'blocked', phase: 'validation', structured: true, summary: 'Required validation could not run.', needsReview: true,
    findings: [{ id: 'f-1', title: 'Private finding', path: 'C:\\private\\file.cpp', line: 1, evidence: ['private evidence'] }],
    diagnostics: [{ code: 'ENVIRONMENT_UNAVAILABLE', severity: 'error', message: 'Private diagnostic', recovery: 'configure', account: 'private', localPath: 'C:\\private' }],
    rawOutput: 'private raw output', cliExitCode: 0, artifacts: [{ path: 'C:\\private\\report.md', label: 'Report' }],
    validation: [{ id: 'verification', name: 'Verification', status: 'not_run', required: true, details: 'A required dependency is unavailable.', evidence: ['private evidence'] }],
    blockers: ['Validation unavailable'], nextSteps: [{ kind: 'configure', reason: 'Configure the task environment.', body: '' }], nextActions: [{ kind: 'configure', reason: 'private detail', body: '' }],
  } };
  const output = publicRun(run);
  assert.equal(output.status.state, 'succeeded');
  assert.deepEqual(publicResultMetadata(output.result), { schemaVersion: 2, outcome: 'blocked', phase: 'validation', structured: true });
  assert.equal(output.result.summary, 'Required validation could not run.');
  for (const field of ['findings', 'diagnostics', 'nextActions', 'rawOutput', 'cliExitCode']) assert.equal(output.result[field], undefined);
  assert.equal(output.result.artifacts[0].path, undefined);
  assert.equal(output.result.validation[0].evidence, undefined);
  assert.equal(output.result.validation[0].required, undefined);
});
test('legacy and nullable workflow metadata stay compatible while malformed v2 cannot look completed', () => {
  assert.deepEqual(publicResultMetadata({ summary: 'Legacy result' }), { schemaVersion: undefined, outcome: undefined, phase: undefined, structured: undefined });
  assert.deepEqual(publicResultMetadata({ schemaVersion: null, outcome: null, phase: null, structured: null }), { schemaVersion: undefined, outcome: undefined, phase: undefined, structured: undefined });
  assert.deepEqual(publicResultMetadata({ schemaVersion: 1, structured: true, outcome: 'completed', phase: 'reporting' }), { schemaVersion: 1, structured: true, outcome: 'completed', phase: 'reporting' });
  const valid = { schemaVersion: 2, outcome: 'completed', phase: 'reporting', structured: true };
  for (const field of ['outcome', 'phase', 'structured']) {
    assert.throws(() => publicResultMetadata({ ...valid, [field]: undefined }), { code: 'INVALID_RESPONSE' });
    assert.throws(() => publicResultMetadata({ ...valid, [field]: null }), { code: 'INVALID_RESPONSE' });
  }
  for (const invalid of [{ ...valid, schemaVersion: 4 }, { ...valid, schemaVersion: '2' }, { ...valid, outcome: 'success' }, { ...valid, outcome: ['completed'] }, { ...valid, outcome: 'C:\\private' }, { ...valid, phase: ['reporting'] }, { ...valid, phase: 'private diagnostics' }, { ...valid, structured: 'true' }]) assert.throws(() => publicResultMetadata(invalid), { code: 'INVALID_RESPONSE' });
  const base = { runId: 'run-1', task, config: {}, status: { state: 'succeeded', sequence: 0 }, result: { summary: 'Invalid v2', ...valid, phase: undefined } };
  assert.throws(() => publicRun(base), { code: 'INVALID_RESPONSE' });
  assert.equal(publicRun({ ...base, result: null }).result, undefined);
});
test('observed execution projects bounded actual metadata and never substitutes the configured values', () => {
  const observedAt = '2026-09-11T03:20:42.1234567+00:00';
  const actual = { source: 'codex-turn-context', observedAt, model: 'actual-model.v2', reasoningEffort: 'high', sessionPath: 'C:\\private\\session.jsonl', cwd: 'C:\\private', token: 'secret', raw: { apiKey: 'secret' } };
  assert.deepEqual(publicObservedExecution(actual), { source: 'codex-turn-context', observedAt, model: 'actual-model.v2', reasoningEffort: 'high' });
  const base = { runId: 'run-1', task, config: { agent: 'codex', model: 'configured-model', reasoningEffort: 'low', executionSource: { agent: 'default', model: 'default', reasoningEffort: 'default' } }, status: { state: 'running', sequence: 3, observedExecution: { source: 'cli-event', observedAt, model: 'actual-model.v2' } } };
  const output = publicRun(base);
  assert.equal(output.execution.model, 'configured-model');
  assert.equal(output.execution.reasoningEffort, 'low');
  assert.deepEqual(output.status.observedExecution, { source: 'cli-event', observedAt, model: 'actual-model.v2' });
  assert.equal(output.status.observedExecution.reasoningEffort, undefined);
  assert.deepEqual(publicObservedExecution({ source: 'cli-event', observedAt, reasoningEffort: 'none', model: null }), { source: 'cli-event', observedAt, reasoningEffort: 'none' });
  assert.equal(publicObservedExecution({ source: 'cli-event', observedAt }), undefined);
});
test('observed metadata ignores private or malformed values without losing a valid partial observation', () => {
  const base = { source: 'cli-event', observedAt: '2026-09-11T03:20:42Z', model: 'model-id', reasoningEffort: 'max' };
  for (const invalid of [null, [], 'event', { ...base, source: 'C:\\private\\session.jsonl' }, { ...base, observedAt: 'private secret' }, { ...base, observedAt: '2026-19-99T77:99:99Z' }]) assert.equal(publicObservedExecution(invalid), undefined);
  for (const model of ['', 'model\n', 'model with spaces', 'C:\\private\\model', '/private/model', 'https://example.test/model', 'a'.repeat(129), { token: 'secret' }]) assert.deepEqual(publicObservedExecution({ ...base, model }), { source: 'cli-event', observedAt: base.observedAt, reasoningEffort: 'max' });
  for (const reasoningEffort of ['', 'high\n', 'private secret', 'C:\\private', null]) assert.deepEqual(publicObservedExecution({ ...base, reasoningEffort }), { source: 'cli-event', observedAt: base.observedAt, model: 'model-id' });
});

const githubDraft = { requestId: '12345678-1234-1234-1234-123456789abc', actionId: 'issue:7:comment', kind: 'comment', target: { repository: 'microsoft/PowerToys', type: 'issue', number: 7 }, body: 'A proposed comment' };
test('GitHub drafts bind a supported target, review shape, and PR HEAD without granting execution control', () => {
  assert.deepEqual(validateWebActionDraft(githubDraft), githubDraft);
  assert.deepEqual(validateExternal({ protocolVersion: 1, type: 'github.prepare', payload: { draft: githubDraft } }).payload.draft, githubDraft);
  for (const field of ['sourceOrigin', 'account', 'token', 'command', 'url', 'operationId', 'confirmed', 'expectedAccount']) assert.throws(() => validateWebActionDraft({ ...githubDraft, [field]: 'override' }));
  assert.throws(() => validateWebActionDraft({ ...githubDraft, requestId: 'not-a-uuid' }));
  assert.throws(() => validateWebActionDraft({ ...githubDraft, body: '' }));
  assert.throws(() => validateWebActionDraft({ ...githubDraft, target: { ...githubDraft.target, repository: 'other/repo' } }));
  const pr = { ...githubDraft, kind: 'approve', target: { ...githubDraft.target, type: 'pr' } };
  assert.throws(() => validateWebActionDraft(pr), { code: 'MISSING_HEAD_SHA' });
  assert.equal(validateWebActionDraft({ ...pr, expectedHeadSha: 'a'.repeat(40) }).kind, 'approve');
  assert.equal(validateWebActionDraft({ ...pr, expectedHeadSha: 'a'.repeat(40), assignSelf: true }).assignSelf, true);
  assert.equal(validateWebActionDraft({ ...pr, kind: 'merge-pr', body: undefined, expectedHeadSha: 'a'.repeat(40), assignSelf: false }).kind, 'merge-pr');
  assert.throws(() => validateWebActionDraft({ ...pr, kind: 'merge-pr', body: undefined, expectedHeadSha: 'a'.repeat(40), assignSelf: true }));
  assert.throws(() => validateWebActionDraft({ ...pr, kind: 'trigger-ci', expectedHeadSha: 'a'.repeat(40), body: 'arbitrary bot command' }));
  assert.throws(() => validateWebActionDraft({ ...pr, expectedHeadSha: 'abc' }));
  assert.throws(() => validateWebActionDraft({ ...githubDraft, kind: 'approve' }));
  const review = { ...pr, kind: 'review', expectedHeadSha: 'a'.repeat(40), review: { event: 'REQUEST_CHANGES', comments: [{ path: 'src/file.ts', line: 10, side: 'RIGHT', body: 'Please fix this.' }], generalComments: [{ body: 'General finding' }] } };
  assert.equal(validateWebActionDraft(review).review.comments.length, 1);
  for (const comment of [{ ...review.review.comments[0], line: -1 }, { ...review.review.comments[0], side: 'MIDDLE' }, { ...review.review.comments[0], command: 'shell' }, { ...review.review.comments[0], startSide: 'LEFT' }]) assert.throws(() => validateWebActionDraft({ ...review, review: { ...review.review, comments: [comment] } }));
  assert.throws(() => validateWebActionDraft({ ...review, review: { ...review.review, comments: Array(51).fill(review.review.comments[0]) } }), { code: 'INPUT_TOO_LARGE' });
  assert.throws(() => validateWebActionDraft({ ...review, review: { ...review.review, generalComments: Array(5).fill({ body: '中'.repeat(59000) }) } }), { code: 'MESSAGE_TOO_LARGE' });
});
test('create PR and fork assignment drafts cannot broaden the repository or operation scope', () => {
  const create = { ...githubDraft, kind: 'create-pr', body: undefined, pullRequest: { head: 'contributor:fix/issue-7', base: 'main', title: 'Fix issue 7', body: 'Details', draft: true }, assignSelf: true };
  assert.equal(validateWebActionDraft(create).pullRequest.draft, true);
  assert.throws(() => validateWebActionDraft({ ...create, pullRequest: { ...create.pullRequest, command: 'git push' } }));
  assert.throws(() => validateWebActionDraft({ ...create, target: { ...create.target, type: 'pr' }, expectedHeadSha: 'a'.repeat(40) }));
  assert.throws(() => validateWebActionDraft({ ...githubDraft, pullRequest: create.pullRequest }));
  const fork = { ...githubDraft, target: { ...githubDraft.target, repository: 'MuyuanMS/PowerToys' }, assignSelf: true, assignmentTarget: githubDraft.target };
  assert.deepEqual(validateWebActionDraft(fork).assignmentTarget, githubDraft.target);
  assert.throws(() => validateWebActionDraft({ ...fork, assignmentTarget: undefined }));
  assert.throws(() => validateWebActionDraft({ ...fork, assignmentTarget: { ...githubDraft.target, repository: 'evil/repo' } }));
  assert.throws(() => validateWebActionDraft({ ...githubDraft, assignmentTarget: githubDraft.target, assignSelf: true }));
});
test('public GitHub status contains no draft, account, origin, confirmation token, or local paths', () => {
  const output = publicWebAction({ ...githubDraft, operationId: '87654321-1234-1234-1234-123456789abc', status: 'prepared', draft: githubDraft, account: 'private', sourceOrigin: origin, confirmed: true, completedSteps: [], remainingSteps: ['comment'], urls: ['file:///C:/private', 'https://user:token@github.com', 'https://github.com/microsoft/PowerToys/issues/7'], error: { code: 'CHECK', message: 'Check', guidance: 'Reload', localPath: 'C:\\private' }, createdAt: 'now', updatedAt: 'now' });
  for (const key of ['body', 'draft', 'account', 'sourceOrigin', 'confirmed']) assert.equal(output[key], undefined);
  assert.equal(output.error.localPath, undefined);
  assert.deepEqual(output.urls, ['https://github.com/microsoft/PowerToys/issues/7']);
});
test('web tasks cannot override local execution settings, target validation cannot be bypassed', () => {
  assert.throws(() => validateTask({ ...task, repository: 'owner/repo' }), { code: 'UNSUPPORTED_REPOSITORY' });
  for (const key of ['agent', 'cliPath', 'repoFolder', 'mainRepoFolder', 'worktreeRoot', 'worktreeBranch', 'permission', 'githubAccount', 'args', 'command', 'sourceOrigin']) assert.throws(() => validateTask({ ...task, [key]: 'override' }));
  for (const target of [{ type: 'pr', number: 0 }, { type: 'issue', number: 1.5 }, { type: 'issue', number: 1, repoFolder: 'C:\\secrets' }]) assert.throws(() => validateTask({ ...task, target }));
  for (const actionKind of ['pr-review', 'e2e']) {
    const pr = { ...task, actionKind, target: { type: 'pr', number: 7 } };
    assert.throws(() => validateTask(pr), { code: 'MISSING_HEAD_SHA' });
    assert.throws(() => validateTask({ ...pr, expectedHeadSha: 'abc' }));
    assert.equal(validateTask({ ...pr, expectedHeadSha: 'a'.repeat(40) }).expectedHeadSha, 'a'.repeat(40));
  }
  assert.throws(() => validateTask({ ...task, prompt: 'a'.repeat(128 * 1024 + 1) }));
  assert.throws(() => validateTask({ ...task, prompt: '中'.repeat(60000) }), { code: 'INPUT_TOO_LARGE' });
  assert.throws(() => validateTask({ ...task, context: '中'.repeat(20000) }), { code: 'INPUT_TOO_LARGE' });
  assert.throws(() => validateTask({ ...task, expectedHeadSha: 'a'.repeat(40) }));
});
test('external projections do not leak configuration, local artifact paths, prompts or arbitrary fields', () => {
  const run = {
    runId: 'run-1', task, config: { repoFolder: 'C:\\private', cliPath: 'C:\\secret.exe' },
    status: { state: 'succeeded', createdAt: 't', updatedAt: 't', sequence: 7, process: { token: 'secret' } },
    result: { summary: '完成', artifacts: [{ label: 'report', path: 'C:\\private\\report.md', url: 'file:///C:/private/report.md' }], validation: [{ name: 'test', status: 'passed', secret: 'private' }], nextSteps: [{ kind: 'comment', reason: 'review', body: 'draft', command: 'rm -rf', suggestions: [{ path: 'secret' }] }], blockers: [] },
    view: { read: false, handled: false }, credentials: 'secret',
  };
  const output = publicRun(run);
  assert.equal(output.runId, 'run-1');
  assert.equal(output.config, undefined); assert.equal(output.task, undefined); assert.equal(output.prompt, undefined);
  assert.equal(output.status.process, undefined); assert.equal(output.credentials, undefined);
  assert.equal(output.result.artifacts[0].path, undefined); assert.equal(output.result.artifacts[0].url, undefined);
  assert.equal(output.result.nextSteps[0].command, undefined); assert.equal(output.result.nextSteps[0].suggestions, undefined);
  assert.deepEqual(publicEvent({ sequence: 1, time: 't', type: 'text', text: 'hello', raw: { token: 'secret' } }), { sequence: 1, time: 't', type: 'text', text: 'hello' });
  assert.deepEqual(publicCapabilities({ protocolVersion: 1, hostVersion: 'test', agents: { codex: { available: true, path: 'secret' }, copilot: { available: false } }, github: { account: 'private' } }), { protocolVersion: 1, hostVersion: 'test', agents: { codex: { available: true }, copilot: { available: false } } });
});
test('artifact links only open ordinary credential-free HTTPS URLs', () => {
  assert.equal(safeHttpsUrl('https://github.com/owner/repo/issues/7'), 'https://github.com/owner/repo/issues/7');
  for (const value of ['javascript:alert(1)', 'data:text/html,hello', 'file:///C:/foo.cmd', 'http://example.com', 'https://user:token@example.com', 'chrome://settings', '/relative', undefined]) assert.equal(safeHttpsUrl(value), undefined);
});
