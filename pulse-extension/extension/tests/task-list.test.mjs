import test from 'node:test';
import assert from 'node:assert/strict';
import { executionChips, filterLoadedTasks, recentTaskLimit, taskExecutionLabel, taskFailureText, taskPrimaryAction, taskResultContext, taskRowTitle, taskSections } from '../src/task-list.ts';

const run = (runId, state, createdAt, endedAt) => ({ runId, task: {}, config: { agent: 'codex', model: '', reasoningEffort: '' }, status: { state, createdAt, updatedAt: endedAt ?? createdAt, ...(endedAt ? { endedAt } : {}) }, view: {} });

test('Tasks keeps active runs first and newly finished records visible without duplicating a completion race', () => {
  const active = run('one', 'running', '2026-09-10T00:00:00Z');
  const finished = { ...active, status: { ...active.status, state: 'failed', endedAt: '2026-09-10T00:00:22Z', updatedAt: '2026-09-10T00:00:22Z', error: { code: 'CLI_EXECUTION_FAILED', message: 'CLI exited after 22 seconds.', guidance: 'Inspect execution logs.' } } };
  const otherActive = run('two', 'running', '2026-09-10T00:00:10Z');
  const sections = taskSections([active, otherActive], [finished]);
  assert.deepEqual(sections.active.map(run => run.runId), ['two']);
  assert.deepEqual(sections.recent.map(run => run.runId), ['one']);
  assert.equal(taskFailureText(sections.recent[0]), 'CLI exited after 22 seconds. Inspect execution logs. [CLI_EXECUTION_FAILED]');
  assert.equal(taskFailureText(active), undefined);
  assert.match(taskFailureText(run('legacy-failed', 'failed', '2026-09-10T00:00:00Z')), /Task failed.*execution logs/);
  assert.equal(taskSections([], [finished]).recent[0].runId, 'one', 'recent records remain visible on an initial reload without a remembered running list');
});

test('recent task section is bounded and sorts by finish time regardless of creation time or target type', () => {
  const oldTask = run('old-long-task', 'failed', '2026-09-01T00:00:00Z', '2026-09-10T01:00:00Z');
  const finished = Array.from({ length: 8 }, (_, i) => run(`task-${i}`, i % 2 ? 'succeeded' : 'cancelled', `2026-09-10T00:0${i}:00Z`, `2026-09-10T00:1${i}:00Z`));
  const sections = taskSections([], [...finished, oldTask, oldTask]);
  assert.equal(sections.recent.length, recentTaskLimit);
  assert.equal(sections.recent[0].runId, oldTask.runId);
  assert.equal(new Set(sections.recent.map(run => run.runId)).size, recentTaskLimit);
});

test('execution chips report observed model and effort, never requested defaults', () => {
  const task = run('test', 'running', '2026-09-10T00:00:00Z');
  task.config.model = 'requested'; task.config.reasoningEffort = 'high';
  assert.deepEqual(executionChips(task).map(chip => chip.label), ['Agent: Codex CLI', 'Model: Reading from CLI…', 'Reasoning: Reading from CLI…']);
  task.status.observedExecution = { model: 'actual', source: 'codex-turn-context', observedAt: '2026-09-11T00:00:00Z' };
  assert.equal(executionChips(task)[1].label, 'Model: actual');
  assert.match(executionChips(task)[1].title, /Reported by the CLI/);
  assert.equal(executionChips(task)[2].label, 'Reasoning: Reading from CLI…');
  task.status.state = 'succeeded';
  assert.equal(executionChips(task)[2].label, 'Reasoning: Not recorded');
  task.status.observedExecution.reasoningEffort = 'ultra';
  assert.equal(executionChips(task)[2].label, 'Reasoning: ultra');
});

test('blocked workflow summaries remain visible even when the CLI exits successfully', () => {
  const task = run('blocked', 'succeeded', '2026-09-14T00:00:00Z', '2026-09-14T00:00:22Z');
  task.result = { schemaVersion: 2, outcome: 'blocked', phase: 'setup', summary: 'A test device is required.', diagnostics: [{ code: 'DEVICE_REQUIRED', severity: 'error', message: 'Connect a test device.', recovery: 'configure' }] };
  assert.equal(taskFailureText(task), 'A test device is required.');
  task.result.outcome = 'completed';
  assert.equal(taskFailureText(task), undefined);
  task.status.state = 'failed'; task.status.error = { message: 'CLI stopped.', code: null, guidance: null };
  assert.equal(taskFailureText(task), 'CLI stopped.');
});

test('targetless history is preserved without becoming a normal target filter or a guessed PR', () => {
  const legacy = run('stored-original', 'failed', '2026-09-10T00:00:00Z');
  legacy.task = { actionKind: 'issue-fix', actionId: 'Saved request', repository: 'microsoft/PowerToys' };
  const pr = { ...legacy, runId: 'pr-run', task: { ...legacy.task, actionKind: 'pr-review', target: { type: 'pr', number: 123 } } };
  assert.equal(taskRowTitle(legacy), 'Issue fix · Unlinked historical record');
  assert.deepEqual(filterLoadedTasks([legacy, pr], { target: 'all', taskKind: '', search: 'stored-original' }), [legacy]);
  assert.deepEqual(filterLoadedTasks([legacy, pr], { target: 'pr', taskKind: '', search: '' }), [pr]);
  assert.deepEqual(filterLoadedTasks([legacy, pr], { target: 'all', taskKind: 'pr-review', search: '#123 PowerToys' }), [pr]);
  assert.equal(legacy.task.target, undefined);
});

test('rows distinguish execution from outcome and keep assessment revisions attached to their subject', () => {
  const task = run('candidate', 'succeeded', '2026-09-10T00:00:00Z');
  task.task = { actionKind: 'pr-review', target: { type: 'pr', number: 123 }, expectedHeadSha: 'a'.repeat(40) };
  task.result = { schemaVersion: 2, assessment: { subject: 'local-candidate', status: 'passed', revisionSha: 'b'.repeat(40) } };
  assert.equal(taskExecutionLabel(task), 'Finished');
  assert.deepEqual(taskResultContext(task), ['Local candidate: passed · bbbbbbbb']);
  assert.equal(taskPrimaryAction(task).label, 'Review result');
  delete task.result;
  assert.equal(taskPrimaryAction(task).label, 'Review task');
  task.status.state = 'accepted';
  assert.equal(taskExecutionLabel(task), 'Accepted · Preparing worker');
  assert.equal(taskPrimaryAction(task).anchor, 'execution-logs');
});
