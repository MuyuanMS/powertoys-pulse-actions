import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';

const source = await readFile(new URL('../preview/bridge.js', import.meta.url), 'utf8');
async function completeResult(send, runId) {
  const run = (await send('tasks.get', { runId })).data;
  for (const section of run.resultPaging?.sections ?? []) {
    const names = section.path.split('.'), field = names.pop();
    const owner = names.reduce((value, name) => value[name], run.result);
    let offset = section.nextOffset;
    while (offset !== null) {
      const response = await send('tasks.resultPage', { runId, path: section.path, offset, limit: 37, fingerprint: run.resultPaging.fingerprint });
      assert.equal(response.ok, true); owner[field].push(...response.data.items); offset = response.data.nextOffset;
    }
    assert.equal(owner[field].length, section.total);
  }
  return run;
}
function preview(fetch, values = new Map(), diagnostics = false, { open } = {}) {
  const window = open ? { open } : {};
  const location = { hostname: '127.0.0.1', origin: 'http://127.0.0.1:4186', href: `http://127.0.0.1:4186/options.html${diagnostics ? '?diagnostics=1' : ''}` };
  runInNewContext(source, {
    window, fetch, URL, crypto, TextEncoder, TextDecoder,
    location,
    document: { readyState: 'loading', addEventListener() {} },
    sessionStorage: { getItem: key => values.get(key), setItem: (key, value) => values.set(key, value) },
  });
  const send = async (type, payload = {}) => JSON.parse(JSON.stringify(await window.chrome.runtime.sendMessage({ channel: 'pulse-ui', type, payload })));
  return Object.assign(send, { tabs: window.chrome.tabs, runtime: window.chrome.runtime, location });
}

test('preview tabs open local extension pages in a new window without replacing the parent or losing session fixtures', async () => {
  const storage = new Map(); const calls = []; let blocked = false; let opened;
  const send = preview(() => { throw new Error('Preview navigation must not contact a Host or remote service.'); }, storage, false, {
    open: (...args) => {
      calls.push(args);
      if (blocked) return null;
      // A normal same-origin window.open inherits the parent's sessionStorage before opener is detached.
      opened = { opener: 'parent-window', session: new Map(storage) };
      return opened;
    },
  });
  await send('tasks.read', { runId: 'demo-quick-failure', value: true });
  const before = new Map(storage); assert.ok(before.size > 0, 'there is actual session fixture state to preserve');
  const parentUrl = send.location.href;
  const target = send.runtime.getURL('popup.html?expanded=1&view=history');
  const result = await send.tabs.create({ url: target });
  assert.deepEqual(calls, [[target, '_blank']], 'the browser receives a new-tab request with opener-based session copying intact');
  assert.equal(result.url, target); assert.equal(send.location.href, parentUrl);
  assert.equal(opened.opener, null, 'the newly created local page is detached after opening');
  assert.deepEqual(opened.session, before); assert.deepEqual(storage, before);

  blocked = true;
  await assert.rejects(() => send.tabs.create({ url: target }), /blocked the preview tab/i);
  assert.equal(send.location.href, parentUrl, 'a blocked new tab must not silently fall back to replacing the popup');
  const openedCount = calls.length;
  for (const url of ['https://github.com/microsoft/PowerToys', 'http://127.0.0.1:9999/popup.html', 'https://example.invalid/popup.html', 'javascript:alert(1)']) {
    await assert.rejects(() => send.tabs.create({ url }), /only supports navigation to local pages/i);
  }
  assert.equal(calls.length, openedCount, 'cross-origin URLs are rejected before any window opens');
  assert.equal(send.location.href, parentUrl); assert.deepEqual(storage, before);
});
test('only explicitly enabled preview diagnostics forward to the local Host and preserve genuine responses', async () => {
  const calls = [];
  const real = { id: 'native', protocolVersion: 1, ok: true, data: { testId: 'test-1', agent: 'codex', state: 'succeeded', reply: 'Model reply from Host' } };
  const send = preview(async (url, init) => { calls.push({ url, init }); return { ok: true, json: async () => real }; }, new Map(), true);
  for (const type of ['agents.test.start', 'agents.test.get', 'agents.test.cancel', 'github.accounts', 'prompts.list', 'prompts.sync', 'prompts.get']) {
    const payload = type === 'agents.test.start' ? { agent: 'codex', cliPath: 'C:\\Actual\\codex.exe' } : type === 'prompts.sync' ? { githubAccount: 'selected-user' } : type === 'prompts.get' ? { name: 'review.prompt.md' } : type.startsWith('agents.') ? { testId: 'test-1' } : {};
    assert.deepEqual(await send(type, payload), real);
    assert.equal(calls.at(-1).url, '/__pulse/diagnostics');
    assert.equal(calls.at(-1).init.method, 'POST');
    assert.deepEqual(JSON.parse(calls.at(-1).init.body), { type, payload });
  }
});
test('preview cannot substitute fabricated model replies or accounts when diagnostics fail', async () => {
  const send = preview(async () => { throw new Error('Host offline'); }, new Map(), true);
  for (const type of ['agents.test.start', 'github.accounts', 'prompts.list', 'prompts.sync', 'prompts.get']) {
    const reply = await send(type, { agent: 'copilot' });
    assert.equal(reply.ok, false);
    assert.equal(reply.error.code, 'PREVIEW_DIAGNOSTICS_UNAVAILABLE');
    assert.equal(reply.data, undefined);
  }
});
test('task views partition active runs and completed targets before pagination', async () => {
  const send = preview(() => { throw new Error('Task samples must not call the Host'); });
  const tasks = (await send('tasks.list', { view: 'tasks' })).data;
  const prs = (await send('tasks.list', { view: 'prs' })).data;
  const issues = (await send('tasks.list', { view: 'issues', limit: 1 })).data;
  assert.deepEqual(Array.from(tasks.runs, run => run.runId), ['demo-running-review']);
  assert.deepEqual(Array.from(prs.runs, run => run.runId), ['demo-quick-failure', 'demo-v2-completed-review', 'demo-v2-proposed-pr-actions', 'demo-v2-negative-e2e', 'demo-actionable-review', 'demo-v2-ci-retry', 'demo-pending-review', 'demo-v2-invalid-result', 'demo-v2-stale-pr-actions', '10101010-1010-4010-8010-101010101010', '20202020-2020-4020-8020-202020202020', '30303030-3030-4030-8030-303030303030', '40404040-4040-4040-8040-404040404040', '50505050-5050-4050-8050-505050505050', 'demo-scope-static', 'demo-scope-build-tests', 'demo-scope-ui-e2e', 'demo-v2-cancelled-review', 'demo-v2-interrupted-review', 'demo-v2-review-limited-coverage', 'demo-v2-review-required-acceptance', 'demo-v2-review-legacy-coverage-blocked', 'demo-v3-pr-p0', 'demo-v3-pr-p1-required', 'demo-v3-pr-p2-p3', 'demo-v3-pr-not-needed', 'demo-v3-pr-required', 'demo-v3-pr-incomplete', 'demo-v3-pr-verification-ready', 'demo-v3-pr-page-error', 'demo-v3-pr-unknown-operation', 'demo-historical-failure']);
  assert.equal(issues.runs[0].runId, 'demo-v2-blocked-setup');
  assert.equal(issues.nextCursor, '1');
  const issuePage2 = (await send('tasks.list', { view: 'issues', cursor: issues.nextCursor, limit: 1 })).data;
  assert.equal(issuePage2.runs[0].runId, 'demo-v2-proposed-create-pr');
  assert.equal(tasks.prCount, 32); assert.equal(tasks.issueCount, 20); assert.equal(tasks.runningCount, 1);
  await send('tasks.cancel', { runId: 'demo-running-review' });
  assert.equal((await send('tasks.list', { view: 'tasks' })).data.runs.length, 0);
  assert.equal((await send('tasks.list', { view: 'prs' })).data.runs.length, 33);
});

test('sample CLI inventory exposes every detected version and retains manual selections without running a model', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Sample CLI inventory must not contact the Host'); };
  const send = preview(noHost, storage);
  const list = (await send('agents.list')).data;
  assert.equal(list.selections.codex, ''); assert.equal(list.selections.copilot, '');
  assert.equal(list.installations.codex.length, 3); assert.equal(list.installations.copilot.length, 2);
  assert.equal(list.installations.codex[0].available, true); assert.match(list.installations.codex[0].version, /0\.145/);
  assert.equal(list.installations.codex[0].error, undefined);
  assert.equal(list.installations.codex[1].available, true); assert.match(list.installations.codex[1].version, /0\.153/);
  assert.equal(list.installations.codex[2].version, '0.154.0'); assert.equal(list.installations.codex[2].available, true);
  assert.ok([...list.installations.codex, ...list.installations.copilot].every(item => item.path.endsWith('.exe') && item.aliases.every(path => path.endsWith('.exe'))));
  const testPath = list.installations.codex[1].resolvedPath;
  const tested = (await send('agents.test.start', { agent: 'codex', cliPath: testPath })).data;
  assert.equal(tested.path, testPath); assert.equal(tested.state, 'failed'); assert.equal(tested.reply, undefined);
  assert.equal(tested.error.code, 'PREVIEW_SAMPLE_INSTALLATION');
  assert.equal((await send('config.get')).data.cliSelections.codex, '', 'testing does not save the selection');
  const config = (await send('config.get')).data;
  config.cliSelections = { codex: testPath, copilot: 'C:\\Missing\\copilot.exe' };
  await send('config.save', config);
  const reloaded = (await preview(noHost, storage)('agents.list')).data;
  assert.equal(reloaded.selections.codex, testPath); assert.equal(reloaded.selections.copilot, 'C:\\Missing\\copilot.exe');
});

test('recent terminal fixtures retain a quick CLI failure after reload and show a newly finished older task first', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Recent task fixtures must not invoke a CLI'); };
  const send = preview(noHost, storage);
  const recent = (await send('tasks.list', { view: 'history', order: 'finished', limit: 5 })).data;
  assert.equal(recent.runs[0].runId, 'demo-quick-failure');
  assert.equal(recent.runs[0].status.state, 'failed'); assert.equal(recent.runs[0].status.error.code, 'CLI_EXECUTION_FAILED');
  assert.equal(Date.parse(recent.runs[0].status.endedAt) - Date.parse(recent.runs[0].status.startedAt), 22_000);
  const reloaded = preview(noHost, storage);
  assert.equal((await reloaded('tasks.list', { view: 'history', order: 'finished', limit: 5 })).data.runs[0].runId, 'demo-quick-failure');
  assert.equal((await reloaded('tasks.get', { runId: 'demo-quick-failure' })).data.status.error.code, 'CLI_EXECUTION_FAILED');
  await reloaded('tasks.cancel', { runId: 'demo-running-review' });
  const finished = (await reloaded('tasks.list', { view: 'history', order: 'finished', limit: 5 })).data;
  assert.equal(finished.runs[0].runId, 'demo-running-review'); assert.equal(finished.runs[0].status.state, 'cancelled');
  assert.equal(finished.runningCount, 0); assert.equal(finished.prCount, 33); assert.equal(finished.issueCount, 20);
});

test('historical failure fixture keeps null diagnostics and old end time available for safe detail rendering', async () => {
  const send = preview(() => { throw new Error('Historical task samples must not invoke a CLI'); });
  const run = (await send('tasks.get', { runId: 'demo-historical-failure' })).data;
  assert.equal(run.status.state, 'failed'); assert.ok(Date.parse(run.status.endedAt) < Date.now() - 60 * 60_000);
  assert.equal(run.status.exitCode, null); assert.equal(run.status.error.code, null);
  assert.equal(run.config.model, null); assert.equal(run.config.reasoningEffort, null);
  assert.equal(run.result.summary, run.status.error.message);
  assert.equal(run.result.structured, false);
});

test('scoped review samples distinguish selected work, conclusions, attributed evidence and recommendation readiness', async () => {
  const send = preview(() => { throw new Error('Scoped review fixtures must not build, run a CLI, or contact the Host'); });
  for (const [runId, mode, checks, conclusion, readiness] of [
    ['demo-scope-static', 'static', ['context', 'local-review'], 'no-blocking-findings', 'missing-prerequisites'],
    ['demo-scope-build-tests', 'build-tests', ['context', 'local-review', 'build-tests'], 'inconclusive', 'unknown'],
    ['demo-scope-ui-e2e', 'ui-e2e', ['context', 'local-review', 'setup', 'e2e'], 'changes-requested', 'ready'],
  ]) {
    const run = (await send('tasks.get', { runId })).data;
    assert.equal(run.task.reviewOptions.mode, mode); assert.equal(run.result.outcome, 'completed');
    assert.deepEqual(run.result.validation.map(check => check.id), checks);
    assert.equal(run.result.reviewConclusion.status, conclusion); assert.equal(run.result.reviewConclusion.revisionSha, run.task.expectedHeadSha);
    assert.equal(run.result.verificationRecommendation.readiness, readiness);
    assert.ok(run.result.verificationRecommendation.question); assert.ok(run.result.verificationRecommendation.scenarios.length);
    assert.ok(run.result.verificationRecommendation.prerequisites.length); assert.ok(run.result.verificationRecommendation.evidence.length);
    assert.equal(run.provenance.source, 'host'); assert.equal(run.provenance.subject, 'original-pr');
    assert.equal(run.provenance.start.capturedAt, run.status.startedAt); assert.equal(run.provenance.end.capturedAt, run.status.endedAt);
    assert.ok(run.result.verificationEvidence.every(row => row.revisionSha === run.task.expectedHeadSha && row.evidence.length));
  }
  const staticRun = (await send('tasks.get', { runId: 'demo-scope-static' })).data;
  assert.deepEqual(staticRun.result.verificationEvidence.map(row => row.source), ['ci', 'author']);
  assert.equal(staticRun.result.verificationEvidence.some(row => row.source === 'current-run'), false, 'static review does not fabricate a current build or runtime test');
  const runtime = (await send('tasks.get', { runId: 'demo-scope-ui-e2e' })).data;
  assert.equal(runtime.result.assessment.status, 'failed'); assert.equal(runtime.result.verificationEvidence[0].status, 'failed');
  const historical = (await send('tasks.get', { runId: 'demo-pending-review' })).data;
  assert.equal(historical.task.reviewOptions, undefined); assert.equal(historical.result.reviewConclusion, undefined);
});

test('related verification includes exact original-PR evidence and keeps local candidates and stale revisions excluded', async () => {
  const send = preview(() => { throw new Error('Related evidence fixtures must not contact the Host or GitHub'); });
  const parent = (await send('tasks.get', { runId: 'demo-scope-static' })).data;
  const related = (await send('reviews.related', { runId: parent.runId })).data;
  assert.equal(related.parentRunId, parent.runId); assert.match(related.recommendation.recommendationId, /^[a-f0-9]{64}$/);
  assert.equal(related.currentConclusion.status, 'evidence-added'); assert.equal(related.runs.length, 3);
  assert.equal(related.currentConclusion.canSupplementAssessment, false, 'historical generic evidence does not prove full scenario coverage'); assert.equal(related.currentConclusion.failedEvidence, null);
  assert.equal(related.totalCount, 3); assert.equal(related.truncated, false);
  assert.deepEqual(related.evidence.map(row => row.runId), ['10101010-1010-4010-8010-101010101010']);
  assert.equal(related.evidence[0].source, 'prior-run'); assert.equal(related.evidence[0].revisionSha, parent.task.expectedHeadSha);
  assert.equal(related.runs[0].verificationEvidence[0].source, 'current-run');
  const candidate = related.runs.find(run => run.runId === '20202020-2020-4020-8020-202020202020');
  assert.equal(candidate.compatibility.eligible, false); assert.equal(candidate.provenance.subject, 'local-candidate');
  assert.equal(candidate.assessment.subject, 'local-candidate'); assert.match(candidate.compatibility.reason, /Local candidate/);
  const stale = related.runs.find(run => run.runId === '30303030-3030-4030-8030-303030303030');
  assert.equal(stale.compatibility.eligible, false); assert.match(stale.compatibility.reason, /different PR revision/);
  assert.notEqual(stale.verificationEvidence[0].revisionSha, parent.task.expectedHeadSha);
  assert.deepEqual((await send('reviews.related', { runId: candidate.runId })).data, related);
  assert.deepEqual((await send('tasks.get', { runId: parent.runId })).data, parent, 'related evidence never rewrites the original review result');
  const blocked = (await send('reviews.related', { runId: 'demo-scope-build-tests' })).data;
  assert.equal(blocked.currentConclusion.status, 'verification-incomplete'); assert.equal(blocked.evidence.length, 0);
  assert.equal(blocked.runs[0].outcome, 'blocked'); assert.equal(blocked.runs[0].verificationEvidence[0].status, 'not_run');
  const negative = (await send('reviews.related', { runId: 'demo-scope-ui-e2e' })).data;
  assert.equal(negative.currentConclusion.status, 'changes-requested'); assert.equal(negative.evidence[0].status, 'failed');
  assert.equal(negative.currentConclusion.canSupplementAssessment, false); assert.equal(negative.currentConclusion.failedEvidence.status, 'failed');
  const old = (await send('reviews.related', { runId: 'demo-pending-review' })).data;
  assert.equal(old.recommendation, null); assert.equal(old.currentConclusion.status, 'unchanged'); assert.deepEqual(old.runs, []);
});

test('missing prerequisites need acknowledgement and supplemental verification stays isolated and idempotent', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Sample supplemental verification must not start a process or contact the Host'); };
  const send = preview(noHost, storage);
  const parent = (await send('tasks.get', { runId: 'demo-scope-static' })).data;
  const settings = (await send('config.get')).data;
  const related = (await send('reviews.related', { runId: parent.runId })).data;
  const payload = { parentRunId: parent.runId, requestId: 'sample-verification-new-request', recommendationId: related.recommendation.recommendationId };
  for (const prerequisitesConfirmed of [undefined, false]) {
    const reply = await send('reviews.verify', { ...payload, ...(prerequisitesConfirmed === undefined ? {} : { prerequisitesConfirmed }) });
    assert.equal(reply.ok, false); assert.equal(reply.error.code, 'VERIFICATION_PREREQUISITES_MISSING');
  }
  const confirmed = { ...payload, prerequisitesConfirmed: true, execution: { agent: 'copilot', model: 'claude-sonnet-4.5', reasoningEffort: 'high' } };
  const created = await send('reviews.verify', confirmed);
  assert.equal(created.ok, true);
  const run = created.data;
  assert.equal(run.task.actionKind, 'pr-verify'); assert.equal(run.status.state, 'running'); assert.equal(run.result, undefined); assert.equal(run.provenance, undefined);
  assert.notEqual(run.runId, parent.runId); assert.notEqual(run.task.requestId, parent.task.requestId);
  assert.deepEqual(run.task.target, parent.task.target); assert.equal(run.task.repository, parent.task.repository); assert.equal(run.task.expectedHeadSha, parent.task.expectedHeadSha);
  assert.equal(run.task.reviewOptions.mode, related.recommendation.mode); assert.equal(run.task.followUp.parentRunId, parent.runId);
  assert.match(run.task.followUp.parentResultFingerprint, /^[a-f0-9]{64}$/); assert.equal(run.task.followUp.recommendationId, payload.recommendationId);
  assert.deepEqual(run.task.context.reviewVerification.recommendation, parent.result.verificationRecommendation);
  assert.equal(run.task.context.reviewVerification.prerequisitesReportedReady, true);
  assert.match(run.task.context.reviewVerification.preflight, /does not prove/); assert.match(run.task.prompt, /do not repeat a complete code review/);
  assert.equal(run.config.agent, 'copilot'); assert.equal(run.config.model, 'claude-sonnet-4.5'); assert.equal(run.config.permission, settings.permission);
  assert.deepEqual((await send('tasks.get', { runId: parent.runId })).data, parent);
  assert.deepEqual((await send('config.get')).data, settings);
  const recovered = preview(noHost, storage);
  assert.deepEqual((await recovered('reviews.verify', confirmed)).data, (await recovered('tasks.get', { runId: run.runId })).data);
  assert.equal((await recovered('reviews.verify', { ...confirmed, execution: { agent: 'codex' } })).error.code, 'REQUEST_CONFLICT');
  assert.equal((await recovered('tasks.list')).data.runs.filter(item => item.task.requestId === payload.requestId).length, 1);
});

test('unknown readiness needs acknowledgement to attempt setup while ready recommendations need no readiness claim', async () => {
  const send = preview(() => { throw new Error('Readiness samples must not contact the Host or execute a real check'); });
  for (const runId of ['demo-scope-build-tests', 'demo-scope-ui-e2e']) {
    const parent = (await send('tasks.get', { runId })).data;
    const related = (await send('reviews.related', { runId })).data;
    const payload = { parentRunId: runId, requestId: `verify-${runId}`, recommendationId: related.recommendation.recommendationId };
    if (related.recommendation.readiness === 'unknown') {
      const unconfirmed = await send('reviews.verify', payload);
      assert.equal(unconfirmed.error.code, 'VERIFICATION_PREREQUISITES_MISSING');
      assert.match(unconfirmed.error.message, /does not establish/);
      payload.prerequisitesConfirmed = true;
    }
    const response = await send('reviews.verify', payload);
    assert.equal(response.ok, true); assert.equal(response.data.task.context.reviewVerification.prerequisitesReportedReady, related.recommendation.readiness === 'unknown');
    assert.match(response.data.task.context.reviewVerification.preflight, /Check the listed prerequisites first/);
    assert.equal(response.data.task.context.reviewVerification.recommendation.readiness, parent.result.verificationRecommendation.readiness);
    assert.equal(response.data.status.state, 'running'); assert.equal(response.data.result, undefined);
  }
});

test('review evidence with altered provenance or a changed parent result cannot silently replace the original assessment', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Evidence compatibility tests must not invoke the Host'); };
  const send = preview(noHost, storage);
  const parentRunId = 'demo-scope-static';
  const before = (await send('reviews.related', { runId: parentRunId })).data;
  const stored = JSON.parse(storage.get('pulse-ui-preview-v17'));
  for (const alter of [
    snapshot => { snapshot.runs.find(run => run.runId === '10101010-1010-4010-8010-101010101010').provenance.end.workingTree = 'modified'; },
    snapshot => { snapshot.runs.find(run => run.runId === '10101010-1010-4010-8010-101010101010').result.verificationEvidence[0].source = 'author'; },
    snapshot => { snapshot.runs.find(run => run.runId === '10101010-1010-4010-8010-101010101010').result.verificationEvidence[0].evidence = []; },
  ]) {
    const snapshot = structuredClone(stored); alter(snapshot);
    const modified = preview(noHost, new Map([['pulse-ui-preview-v17', JSON.stringify(snapshot)]]));
    const related = (await modified('reviews.related', { runId: parentRunId })).data;
    assert.equal(related.evidence.length, 0); assert.equal(related.currentConclusion.status, 'unchanged');
    assert.equal((await modified('tasks.get', { runId: parentRunId })).data.result.assessment.status, 'inconclusive');
  }
  const changed = structuredClone(stored);
  changed.runs.find(run => run.runId === parentRunId).result.verificationRecommendation.question = 'A newly scoped sample question.';
  const modified = preview(noHost, new Map([['pulse-ui-preview-v17', JSON.stringify(changed)]]));
  const related = (await modified('reviews.related', { runId: parentRunId })).data;
  assert.notEqual(related.recommendation.recommendationId, before.recommendation.recommendationId);
  assert.equal(related.evidence.length, 0); assert.match(related.runs[0].compatibility.reason, /exact saved review result/);
  const response = await modified('reviews.verify', { parentRunId, requestId: 'stale-recommendation-request', recommendationId: before.recommendation.recommendationId, prerequisitesConfirmed: true });
  assert.equal(response.error.code, 'RECOMMENDATION_CHANGED');
});

test('rerun scope overrides are explicit while omission preserves both selected and unrecorded historical scope', async () => {
  const send = preview(() => { throw new Error('Scoped rerun fixtures must not run any CLI'); });
  const config = (await send('config.get')).data;
  config.cliSelections.codex = (await send('agents.list')).data.installations.codex[2].resolvedPath;
  await send('config.save', config);
  const parent = (await send('tasks.get', { runId: 'demo-scope-static' })).data;
  const unchanged = (await send('tasks.rerun', { runId: parent.runId, requestId: 'scope-preserved' })).data;
  assert.equal(unchanged.task.reviewOptions.mode, 'static'); assert.equal(unchanged.provenance, undefined);
  const changed = (await send('tasks.rerun', { runId: parent.runId, requestId: 'scope-changed', reviewOptions: { mode: 'ui-e2e' } })).data;
  assert.equal(changed.task.reviewOptions.mode, 'ui-e2e'); assert.equal(changed.result, undefined);
  const legacy = (await send('tasks.rerun', { runId: 'demo-pending-review', requestId: 'legacy-mode-preserved' })).data;
  assert.equal(legacy.task.reviewOptions, undefined);
  const explicitDefault = (await send('tasks.rerun', { runId: 'demo-pending-review', requestId: 'legacy-ui-default', reviewOptions: { mode: 'build-tests' } })).data;
  assert.deepEqual(explicitDefault.task.reviewOptions, { mode: 'build-tests' });
  for (const reviewOptions of [null, { mode: 'all' }, { mode: 'static', runBuild: true }]) assert.equal((await send('tasks.rerun', { runId: parent.runId, requestId: crypto.randomUUID(), reviewOptions })).ok, false);
  assert.equal((await send('tasks.rerun', { runId: 'demo-completed-fix', requestId: 'issue-scope-invalid', reviewOptions: { mode: 'static' } })).ok, false);
  assert.deepEqual((await send('tasks.get', { runId: parent.runId })).data, parent);
});

test('mixed supplemental checks retain failure ahead of passing evidence and a passing model assessment', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Mixed verification fixtures must not contact the Host'); };
  const send = preview(noHost, storage);
  await send('reviews.related', { runId: 'demo-scope-static' });
  const saved = JSON.parse(storage.get('pulse-ui-preview-v17'));
  const result = saved.runs.find(run => run.runId === '10101010-1010-4010-8010-101010101010').result;
  result.verificationEvidence.push({ ...result.verificationEvidence[0], id: 'failed-runtime-check', status: 'failed', summary: 'Sample runtime check failed after another scenario passed.', evidence: ['The sample runtime driver failed during the second scenario.'] });
  const modified = preview(noHost, new Map([['pulse-ui-preview-v17', JSON.stringify(saved)]]));
  const related = (await modified('reviews.related', { runId: 'demo-scope-static' })).data;
  assert.deepEqual(related.evidence.map(row => row.status), ['passed', 'failed']);
  assert.equal(related.runs[0].assessment.status, 'passed');
  assert.equal(related.currentConclusion.status, 'verification-incomplete'); assert.equal(related.currentConclusion.canSupplementAssessment, false);
  assert.equal(related.currentConclusion.failedEvidence.id, 'failed-runtime-check');
  assert.match(related.currentConclusion.summary, /failed check/);
  assert.equal((await modified('tasks.get', { runId: 'demo-scope-static' })).data.result.assessment.status, 'inconclusive');
});

test('v2 preview fixtures cover workflow outcomes and preserve v1 history', async () => {
  const send = preview(() => { throw new Error('Structured result fixtures must not contact the Host'); });
  const runs = (await send('tasks.list', { limit: 100 })).data.runs;
  assert.equal(runs.length, 53);
  assert.equal(new Set(runs.map(run => run.runId)).size, runs.length);
  assert.equal(new Set(runs.map(run => run.task.requestId)).size, runs.length);
  const structured = runs.filter(run => run.result?.schemaVersion === 2);
  assert.equal(structured.length, 24);
  assert.deepEqual(Array.from(new Set(structured.map(run => run.result.outcome))).sort(), ['blocked', 'cancelled', 'completed', 'failed', 'interrupted']);
  const kinds = new Set(['viewChanges', 'inspectResult', 'openTarget', 'configure', 'rerun', 'approve', 'suggestChanges', 'requestChanges', 'comment', 'close', 'create-pr', 'merge-pr', 'trigger-ci', 'none']);
  const writes = new Set(['approve', 'suggestChanges', 'requestChanges', 'comment', 'close', 'create-pr', 'merge-pr', 'trigger-ci']);
  for (const run of structured) {
    const result = run.result;
    assert.ok(['setup', 'analysis', 'implementation', 'validation', 'reporting'].includes(result.phase));
    assert.match(result.summary, /^Sample:/);
    assert.equal(typeof result.needsReview, 'boolean');
    assert.equal(typeof result.structured, 'boolean');
    assert.equal(result.cliExitCode, run.status.exitCode);
    assert.ok(run.status.endedAt);
    assert.equal(new Set(result.findings.map(finding => finding.id)).size, result.findings.length);
    assert.equal(new Set(result.validation.map(check => check.id)).size, result.validation.length);
    for (const finding of result.findings) {
      assert.ok(finding.id && finding.title && finding.details);
      assert.ok(['high', 'medium', 'low'].includes(finding.severity));
      assert.ok(['open', 'fixed', 'unverified'].includes(finding.status));
      assert.equal(typeof finding.path, 'string');
      assert.ok(finding.line === null || Number.isInteger(finding.line) && finding.line > 0);
      assert.ok(Array.isArray(finding.evidence)); assert.ok(finding.evidence.every(item => typeof item === 'string'));
    }
    for (const check of result.validation) {
      assert.ok(check.id && check.name && check.details);
      assert.ok(['passed', 'failed', 'not_run'].includes(check.status)); assert.equal(typeof check.required, 'boolean');
      assert.ok(Array.isArray(check.evidence)); assert.ok(check.evidence.every(item => typeof item === 'string'));
    }
    for (const diagnostic of result.diagnostics) {
      assert.match(diagnostic.code, /^[A-Z][A-Z0-9_]*$/);
      assert.ok(['warning', 'error'].includes(diagnostic.severity)); assert.ok(diagnostic.message);
      assert.ok(['configure', 'rerun', 'inspectResult', 'openTarget', 'none'].includes(diagnostic.recovery));
    }
    for (const action of result.nextActions) {
      assert.deepEqual(Object.keys(action).sort(), ['availability', 'body', 'kind', 'proposalId', ...(action.kind === 'create-pr' ? ['pullRequest'] : []), 'reason', ...(action.suggestionIds ? ['suggestionIds'] : [])]);
      assert.equal(typeof action.availability.enabled, 'boolean'); assert.ok(Array.isArray(action.availability.reasons));
      assert.match(action.proposalId, /^preview-demo-/);
      assert.ok(kinds.has(action.kind)); assert.ok(action.reason); assert.equal(typeof action.body, 'string');
      if (result.outcome !== 'completed') assert.equal(writes.has(action.kind), false);
    }
    assert.deepEqual(Array.from(result.blockers), Array.from(result.diagnostics.filter(item => item.severity === 'error'), item => item.message));
    assert.deepEqual(result.nextSteps, result.nextActions.filter(item => result.outcome === 'completed' || !writes.has(item.kind)));
  }
  for (const runId of ['demo-pending-review', 'demo-actionable-review', 'demo-completed-fix', 'demo-failed-e2e', 'demo-historical-failure']) {
    assert.equal((await send('tasks.get', { runId })).data.result.schemaVersion, undefined, `${runId} remains a v1 historical sample`);
  }
});

test('fixture mode blocks every Host diagnostic without making a network or model request', async () => {
  let requests = 0;
  const send = preview(() => { requests++; throw new Error('Fixture mode must not contact any service'); });
  for (const type of ['agents.test.start', 'agents.test.get', 'agents.test.cancel', 'github.accounts', 'prompts.list', 'prompts.sync', 'prompts.get']) {
    const reply = await send(type, { agent: 'codex', cliPath: 'C:\\Actual\\codex.exe', testId: 'unused' });
    assert.equal(reply.ok, false, type); assert.equal(reply.error.code, 'PREVIEW_FIXTURE_ONLY', type); assert.equal(reply.data, undefined);
  }
  assert.equal(requests, 0);
});

test('final browser fixtures cover editable feedback, ready and missing prerequisites, candidate publication and report recovery in session data', async () => {
  const storage = new Map(); const noHost = () => { throw new Error('Browser fixtures must never contact the Host or GitHub'); };
  const send = preview(noHost, storage);
  const feedback = await completeResult(send, 'demo-v3-pr-p1-required'); const target = (await send('operations.preview', { runId: feedback.runId })).data;
  assert.equal(feedback.result.findings.filter(row => row.confirmed).length, 2);
  const code = feedback.result.review.suggestions[0]; const file = target.files.find(row => row.path === code.path);
  assert.equal(target.headSha, feedback.task.expectedHeadSha); assert.ok(file.lines.some(row => row.line === code.line));
  for (const [runId, readiness] of [['demo-v3-pr-required', 'missing-prerequisites'], ['demo-v3-pr-verification-ready', 'ready']]) {
    const run = await completeResult(send, runId);
    assert.equal(run.result.findings.length, 0); assert.equal(run.result.e2eAssessment.level, 'required'); assert.equal(run.result.e2eAssessment.readiness, readiness);
    assert.equal(run.result.assessment.status, 'inconclusive'); assert.equal(run.result.recommendation.kind, 'run-e2e');
  }
  const candidate = await completeResult(send, 'demo-v3-issue-candidate'); const draft = candidate.result.nextActions[0];
  assert.equal(candidate.task.target.type, 'issue'); assert.equal(candidate.result.assessment.subject, 'local-candidate');
  assert.equal(candidate.result.assessment.revisionSha, draft.pullRequest.sourceHeadSha); assert.equal(draft.kind, 'create-pr'); assert.equal(draft.pullRequest.draft, true);
  assert.match(draft.pullRequest.body, /Fixes #450/); assert.equal(candidate.result.recommendation.kind, 'create-pr');
  const incomplete = await completeResult(send, 'demo-v3-pr-incomplete'); assert.equal(incomplete.result.report.complete, false);
  const paged = (await send('tasks.get', { runId: 'demo-v3-pr-page-error' })).data;
  assert.equal(paged.result.findings.length, 25); assert.equal(paged.resultPaging.sections[0].total, 26);
  const page = { runId: paged.runId, path: 'findings', offset: 25, limit: 25, fingerprint: paged.resultPaging.fingerprint };
  const failed = await send('tasks.resultPage', page); assert.equal(failed.ok, false); assert.equal(failed.error.code, 'RESULT_PAGE_UNAVAILABLE');
  const restored = await completeResult(preview(noHost, storage), paged.runId);
  assert.equal(restored.result.findings.length, 26); assert.equal(restored.result.findings[25].priority, 'P1');
});

test('unknown result action fixture retains the same operation and identity after a read-only result check', async () => {
  const send = preview(() => { throw new Error('Unknown fixture actions cannot access GitHub'); });
  const runId = 'demo-v3-pr-unknown-operation'; const run = (await send('tasks.get', { runId })).data;
  const before = (await send('resultActions.list', { runId })).data.operations;
  assert.equal(before.length, 1); assert.equal(before[0].status, 'unknown');
  const action = (await send('webActions.preview', { operationId: before[0].operationId })).data;
  assert.deepEqual(action.draft.target, { ...run.task.target, repository: run.task.repository }); assert.equal(action.canSubmit, false);
  const checked = (await send('webActions.reconcile', { operationId: action.operationId })).data;
  assert.equal(checked.operationId, action.operationId); assert.equal(checked.status, 'unknown');
  const after = (await send('resultActions.list', { runId })).data.operations;
  assert.equal(after.length, 1); assert.equal(after[0].operationId, action.operationId);
});

test('edited result Draft preparation freezes source fields and Draft status, preserves Markdown, and enforces attempt-content identity', async () => {
  const storage = new Map(); const noHost = () => { throw new Error('Draft fixtures cannot access Host or GitHub'); }; const send = preview(noHost, storage);
  const runId = 'demo-v3-issue-candidate'; const run = (await send('tasks.get', { runId })).data; const proposal = run.result.nextActions[0];
  const content = { title: '  Edited Draft title  ', body: '## Prepared changes\r\n\r\n- Keep **Markdown**.  \r\n' };
  const payload = { runId, proposalId: proposal.proposalId, attemptId: 'b0b0b0b0-b0b0-40b0-80b0-b0b0b0b0b0b0', content };
  const prepared = await send('resultActions.prepare', payload); assert.equal(prepared.ok, true);
  const confirmation = (await send('webActions.preview', { operationId: prepared.data.operationId })).data;
  assert.equal(confirmation.draft.pullRequest.title, content.title); assert.equal(confirmation.draft.pullRequest.body, content.body.replaceAll('\r\n', '\n'));
  assert.equal(confirmation.draft.pullRequest.draft, true);
  for (const key of ['head', 'base', 'sourceHeadSha']) assert.equal(confirmation.draft.pullRequest[key], proposal.pullRequest[key]);
  assert.deepEqual((await preview(noHost, storage)('resultActions.prepare', payload)).data, prepared.data);
  const changed = await send('resultActions.prepare', { ...payload, content: { ...content, body: 'Different body' } });
  assert.equal(changed.ok, false); assert.equal(changed.error.code, 'REQUEST_CONFLICT');
  const otherAttempt = (await send('resultActions.prepare', { ...payload, attemptId: 'b1b1b1b1-b1b1-41b1-81b1-b1b1b1b1b1b1', content: { title: 'Another edit', body: 'Changed' } })).data;
  assert.equal(otherAttempt.operationId, prepared.data.operationId, 'an unresolved operation is reopened, not overwritten');
  assert.deepEqual((await send('webActions.preview', { operationId: prepared.data.operationId })).data.draft, confirmation.draft);
  for (const invalid of [{ ...payload, attemptId: undefined }, { ...payload, content: { ...content, draft: false } }, { ...payload, content: { title: ' ', body: '' } }, { ...payload, content: { title: 'Title', body: 'bad\0value' } }, { ...payload, content: { title: 'Title' } }]) assert.equal((await send('resultActions.prepare', invalid)).ok, false);
  const stored = JSON.parse(storage.get('pulse-ui-preview-v17')); stored.resultActionRequests = {};
  const recovered = preview(noHost, new Map([['pulse-ui-preview-v17', JSON.stringify(stored)]]));
  assert.equal((await recovered('resultActions.prepare', { ...payload, content: { title: 'Changed again', body: '' } })).error.code, 'REQUEST_CONFLICT');
  assert.deepEqual((await send('tasks.get', { runId })).data.result.nextActions[0].pullRequest, proposal.pullRequest, 'editor changes never rewrite the saved report');
});

test('completed local review keeps an open finding separate from blockers and has evidence for each required check', async () => {
  const send = preview(() => { throw new Error('Completed review sample must not contact the Host'); });
  const run = (await send('tasks.get', { runId: 'demo-v2-completed-review' })).data;
  assert.equal(run.status.state, 'succeeded'); assert.equal(run.result.outcome, 'completed');
  assert.equal(run.result.needsReview, true); assert.equal(run.result.blockers.length, 0);
  assert.equal(run.result.findings[0].status, 'open'); assert.ok(run.result.findings[0].evidence.length);
  assert.deepEqual(Array.from(run.result.validation, check => check.id), ['context', 'local-review', 'verification']);
  assert.ok(run.result.validation.every(check => check.required && check.status === 'passed' && check.evidence.length));
  assert.match(run.result.validation[2].details, /No build, desktop session, or hardware behavior is claimed/);
  assert.equal(run.result.review.headSha, run.task.expectedHeadSha);
  assert.ok(run.result.review.suggestions.length);
  const available = (await send('operations.preview', { runId: run.runId })).data;
  assert.equal(available.stale, false); assert.equal(available.canSuggestChanges, true); assert.equal(available.canRequestChanges, true); assert.equal(available.canComment, true);
  assert.equal(available.canApprove, true); assert.equal(available.canClose, true);
});

test('ordinary review sample preserves unverified CJK coverage independently of fixed manual approval', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Review scope samples must not contact the Host or GitHub'); };
  const send = preview(noHost, storage);
  const runId = 'demo-v2-review-limited-coverage';
  const run = (await send('tasks.get', { runId })).data;
  assert.equal(run.task.repository, 'example/pulse-review-samples'); assert.equal(run.task.target.number, 101);
  assert.match(run.task.prompt, /Sample task/); assert.match(run.task.prompt, /Full CJK acceptance was not requested/);
  assert.equal(run.status.state, 'succeeded'); assert.equal(run.status.exitCode, 0); assert.equal(run.result.outcome, 'completed');
  assert.equal(run.result.assessment.subject, 'original-pr'); assert.equal(run.result.assessment.status, 'inconclusive');
  assert.equal(run.result.assessment.revisionSha, run.task.expectedHeadSha); assert.equal(run.result.review.headSha, run.task.expectedHeadSha);
  assert.equal(run.result.findings.length, 0); assert.equal(run.result.blockers.length, 0);
  const required = run.result.validation.filter(item => item.required);
  assert.deepEqual(required.map(item => item.id), ['context', 'local-review', 'verification']);
  assert.ok(required.every(item => item.status === 'passed' && item.evidence.length));
  const coverage = run.result.validation.find(item => item.id === 'cjk-acceptance');
  assert.equal(coverage.status, 'not_run'); assert.equal(coverage.required, false); assert.equal(coverage.evidence.length, 0);
  assert.deepEqual(run.result.diagnostics.map(item => [item.code, item.severity]), [['REVIEW_COVERAGE_LIMITED', 'warning']]);
  const comment = run.result.nextActions.find(item => item.kind === 'comment');
  assert.equal(comment.availability.enabled, true); assert.match(comment.body, /does not confirm that the reported crash is fixed/);
  for (const kind of ['approve', 'merge-pr']) {
    const proposal = run.result.nextActions.find(item => item.kind === kind);
    assert.equal(proposal.availability.enabled, kind === 'merge-pr');
    if (kind === 'approve') assert.match(proposal.availability.reasons.join(' '), /passing assessment/);
  }
  const available = (await send('operations.preview', { runId })).data;
  assert.equal(available.canComment, true); assert.equal(available.canApprove, true);
  assert.equal((await send('operations.list', { runId })).data.operations.length, 0, 'previewing never submits a comment');
  const merge = run.result.nextActions.find(item => item.kind === 'merge-pr');
  assert.equal((await send('resultActions.prepare', { runId, proposalId: merge.proposalId })).ok, true);
  assert.equal((await send('resultActions.list', { runId })).data.operations.length, 1);
  const reloaded = (await preview(noHost, storage)('tasks.get', { runId })).data;
  assert.deepEqual(reloaded.result, run.result, 'the sample retains its inconclusive assessment and optional coverage after reload');
});

test('explicit CJK acceptance sample stays blocked and retains both specific and generic diagnostics for UI grouping', async () => {
  const send = preview(() => { throw new Error('Required acceptance sample must not contact the Host or GitHub'); });
  const runId = 'demo-v2-review-required-acceptance';
  const run = (await send('tasks.get', { runId })).data;
  assert.equal(run.task.repository, 'example/pulse-review-samples'); assert.equal(run.task.target.number, 102);
  assert.match(run.task.prompt, /explicitly required/); assert.match(run.task.prompt, /Sample task/);
  assert.equal(run.status.state, 'succeeded'); assert.equal(run.status.exitCode, 0); assert.equal(run.result.outcome, 'blocked');
  assert.equal(run.result.phase, 'validation'); assert.equal(run.result.assessment.status, 'inconclusive');
  const requiredAcceptance = run.result.validation.find(item => item.id === 'verification');
  assert.equal(requiredAcceptance.status, 'not_run'); assert.equal(requiredAcceptance.required, true);
  assert.equal(requiredAcceptance.evidence.length, 0, 'missing acceptance is not replaced with invented passing evidence');
  assert.deepEqual(run.result.diagnostics.map(item => [item.code, item.severity]), [['CJK_ACCEPTANCE_UNAVAILABLE', 'error'], ['WORKFLOW_CHECKS_INCOMPLETE', 'error']]);
  assert.equal(run.result.blockers.length, 2, 'the raw result preserves both diagnoses even when presentation groups them');
  assert.deepEqual(run.result.nextActions.map(item => item.kind), ['configure', 'inspectResult', 'rerun']);
  const available = (await send('operations.preview', { runId })).data;
  for (const capability of ['canComment', 'canApprove', 'canRequestChanges', 'canSuggestChanges', 'canClose']) assert.equal(available[capability], true);
  assert.equal((await send('operations.list', { runId })).data.operations.length, 0);
  assert.equal((await send('resultActions.list', { runId })).data.operations.length, 0);
});

test('manual approval preserves historical report limitations without adding an approval permission state', async () => {
  const send = preview(() => { throw new Error('Manual approval samples must not contact the Host or GitHub'); });
  for (const runId of ['demo-v2-review-limited-coverage', 'demo-v2-review-legacy-coverage-blocked']) {
    const before = (await send('tasks.get', { runId })).data;
    const available = (await send('operations.preview', { runId })).data;
    assert.equal(available.canApprove, true); assert.deepEqual(available.reviewDecisions, []);
    assert.equal(before.reviewSummary.verification, 'limited');
    const body = 'I reviewed the available sample report and its recorded limitations.';
    const approved = (await send('operations.submit', { runId, kind: 'approve', body, expectedAccount: available.account, expectedHeadSha: before.task.expectedHeadSha })).data;
    assert.equal(approved.body, body); assert.equal(approved.reviewDecision, undefined);
    assert.deepEqual((await send('tasks.get', { runId })).data.result, before.result);
    assert.equal((await send('operations.submit', { runId, kind: 'approve', reviewDecisionId: 'legacy-special-approval', acknowledgedLimitations: true, expectedAccount: available.account, expectedHeadSha: before.task.expectedHeadSha })).ok, false);
  }
});

test('active failed and absent-result PR tasks retain fixed manual actions with live account and SHA checks', async () => {
  const send = preview(() => { throw new Error('Manual action samples must not contact the Host or GitHub'); });
  for (const runId of ['demo-running-review', 'demo-quick-failure', 'demo-historical-failure', 'demo-v2-invalid-result', 'demo-v2-cancelled-review', 'demo-v2-interrupted-review']) {
    const run = (await send('tasks.get', { runId })).data;
    const available = (await send('operations.preview', { runId })).data;
    for (const key of ['canApprove', 'canComment', 'canRequestChanges', 'canSuggestChanges', 'canClose']) assert.equal(available[key], true, `${runId}: ${key}`);
    const payload = { runId, kind: 'approve', expectedAccount: available.account, expectedHeadSha: run.task.expectedHeadSha, body: '' };
    assert.equal((await send('operations.submit', { ...payload, expectedAccount: 'another-account' })).ok, false);
    assert.equal((await send('operations.submit', { ...payload, expectedHeadSha: 'f'.repeat(40) })).ok, false);
    assert.equal((await send('operations.submit', payload)).ok, true);
    for (const kind of ['merge-pr', 'trigger-ci']) {
      const prepared = (await send('resultActions.prepare', { runId, kind })).data;
      const action = (await send('webActions.preview', { operationId: prepared.operationId })).data;
      assert.equal(action.kind, kind); assert.equal(action.canSubmit, true); assert.equal(action.draft.expectedHeadSha, run.task.expectedHeadSha);
    }
  }
});
test('blocked setup, invalid output, and process failure retain distinct diagnostics and actual CLI exits', async () => {
  const send = preview(() => { throw new Error('Failure fixtures must not invoke the Host'); });
  const setup = (await send('tasks.get', { runId: 'demo-v2-blocked-setup' })).data;
  assert.equal(setup.result.outcome, 'blocked'); assert.equal(setup.result.phase, 'setup');
  assert.equal(setup.status.state, 'succeeded'); assert.equal(setup.status.exitCode, 0);
  assert.equal(setup.result.diagnostics[0].code, 'REPRODUCTION_DRIVER_MISSING');
  assert.equal(setup.result.diagnostics[0].recovery, 'configure'); assert.equal(setup.result.nextActions[0].kind, 'configure');
  assert.ok(setup.result.validation.every(check => check.required && check.status === 'not_run'));
  const invalid = (await send('tasks.get', { runId: 'demo-v2-invalid-result' })).data;
  assert.equal(invalid.status.state, 'succeeded'); assert.equal(invalid.status.exitCode, 0);
  assert.equal(invalid.result.outcome, 'blocked'); assert.equal(invalid.result.structured, false);
  assert.equal(invalid.result.diagnostics[0].code, 'INVALID_RESULT');
  assert.equal(invalid.result.validation.length, 0, 'invalid output does not invent completed checks');
  assert.equal(JSON.parse(invalid.result.rawOutput).outcome, 'completed', 'original claimed success remains inspectable');
  const failed = (await send('tasks.get', { runId: 'demo-v2-process-failed' })).data;
  assert.equal(failed.status.state, 'failed'); assert.equal(failed.status.exitCode, 17);
  assert.equal(failed.result.outcome, 'failed'); assert.equal(failed.result.phase, 'implementation');
  assert.equal(failed.result.diagnostics[0].code, 'CLI_EXECUTION_FAILED'); assert.match(failed.result.rawOutput, /code 17/);
  assert.deepEqual(Array.from(failed.result.validation, check => check.status), ['passed', 'failed', 'not_run']);
  assert.ok(failed.result.artifacts.length); assert.equal(failed.view.handled, false);
});

test('cancelled and interrupted review samples retain evidence while distinguishing missing and not-run checks', async () => {
  const send = preview(() => { throw new Error('Interrupted task fixtures must not contact the Host'); });
  const cancelled = (await send('tasks.get', { runId: 'demo-v2-cancelled-review' })).data;
  assert.equal(cancelled.status.state, 'cancelled'); assert.equal(cancelled.result.outcome, 'cancelled');
  assert.equal(cancelled.result.phase, 'analysis'); assert.equal(cancelled.result.cliExitCode, null);
  assert.equal(cancelled.result.validation.find(check => check.id === 'local-review').status, 'not_run');
  assert.equal(cancelled.result.validation.some(check => check.id === 'verification'), false);
  assert.equal(cancelled.result.diagnostics[0].severity, 'warning'); assert.equal(cancelled.result.blockers.length, 0);
  const interrupted = (await send('tasks.get', { runId: 'demo-v2-interrupted-review' })).data;
  assert.equal(interrupted.status.state, 'interrupted'); assert.equal(interrupted.result.outcome, 'interrupted');
  assert.equal(interrupted.result.phase, 'validation'); assert.equal(interrupted.result.cliExitCode, null);
  assert.equal(interrupted.result.validation.find(check => check.id === 'verification').status, 'not_run');
  assert.equal(interrupted.result.findings[0].status, 'unverified');
  for (const run of [cancelled, interrupted]) {
    assert.ok(run.result.artifacts.length); assert.ok(run.result.validation.find(check => check.id === 'context').evidence.length);
    assert.equal(run.result.review, null);
    assert.ok(run.result.nextSteps.some(step => step.kind === 'rerun'));
  }
});

test('diagnostic v2 samples retain Issue gates while PR manual controls remain separate from execution results', async () => {
  const send = preview(() => { throw new Error('Diagnostic fixture operations must never contact the Host or GitHub'); });
  for (const runId of ['demo-v2-blocked-setup', 'demo-v2-invalid-result', 'demo-v2-process-failed', 'demo-v2-cancelled-review', 'demo-v2-interrupted-review']) {
    const run = (await send('tasks.get', { runId })).data;
    const available = (await send('operations.preview', { runId })).data;
    const isPr = run.task.target.type === 'pr';
    for (const capability of ['canApprove', 'canRequestChanges', 'canSuggestChanges', 'canComment', 'canClose']) assert.equal(available[capability], isPr, `${runId}: ${capability}`);
    if (isPr) continue;
    assert.match(available.reasons.join(' '), /recorded evidence/);
    for (const kind of ['approve', 'requestChanges', 'suggestChanges', 'comment', 'close']) {
      const response = await send('operations.submit', { runId, kind, expectedAccount: 'pulse-demo', expectedHeadSha: run.task.expectedHeadSha, body: 'Sample diagnostic output must not be posted.', suggestions: [{ path: 'sample.cs', line: 1, body: 'Sample', replacement: 'Sample' }] });
      assert.equal(response.ok, false, `${runId}: ${kind}`);
    }
    assert.equal((await send('operations.list', { runId })).data.totalCount, 0);
  }
});

test('v2 manual review simulation checks account and immutable target SHA without inventing report-completion gates', async () => {
  const noHost = () => { throw new Error('Review simulation must not contact the Host or GitHub'); };
  const storage = new Map();
  const send = preview(noHost, storage);
  const runId = 'demo-v2-completed-review';
  const run = (await send('tasks.get', { runId })).data;
  const payload = { runId, operationId: 'v2-sample-review', kind: 'suggestChanges', expectedAccount: 'pulse-demo', expectedHeadSha: run.task.expectedHeadSha, body: run.result.review.body, suggestions: run.result.review.suggestions };
  assert.equal((await send('operations.submit', { ...payload, expectedAccount: 'other-account' })).ok, false);
  assert.equal((await send('operations.submit', { ...payload, expectedHeadSha: 'f'.repeat(40) })).ok, false);
  assert.equal((await send('operations.submit', { ...payload, kind: 'approve', operationId: 'manual-legacy-approval' })).ok, true, 'manual approval does not require an AI proposal');
  const submitted = (await send('operations.submit', payload)).data;
  assert.equal(submitted.status, 'succeeded'); assert.equal(submitted.remoteId, 'preview-only'); assert.equal(submitted.body, payload.body);
  assert.deepEqual((await send('operations.submit', payload)).data, submitted);
  assert.equal((await send('operations.list', { runId })).data.totalCount, 2);
  const key = Array.from(storage.keys())[0];
  for (const alter of [
    target => { target.result.validation = target.result.validation.filter(check => check.id !== 'verification'); },
    target => { target.result.validation.find(check => check.id === 'verification').status = 'not_run'; },
    target => { target.result.validation.find(check => check.id === 'verification').required = false; },
    target => { target.result.review.headSha = 'e'.repeat(40); },
    target => { target.task.expectedHeadSha = 'e'.repeat(40); target.result.review.headSha = target.task.expectedHeadSha; },
    target => { target.result.diagnostics.push({ code: 'VERIFICATION_BLOCKED', severity: 'error', message: 'Sample required verification blocked.', recovery: 'rerun' }); },
  ]) {
    const changed = JSON.parse(storage.get(key));
    alter(changed.runs.find(item => item.runId === runId));
    const check = preview(noHost, new Map([[key, JSON.stringify(changed)]]));
    const available = (await check('operations.preview', { runId })).data;
    const allowed = changed.runs.find(item => item.runId === runId).task.expectedHeadSha === run.task.expectedHeadSha;
    assert.equal(available.canSuggestChanges, allowed); assert.equal(available.canComment, allowed);
    assert.equal((await check('operations.submit', { ...payload, operationId: crypto.randomUUID() })).ok, allowed);
  }
});

test('retrying a v2 diagnostic sample creates a distinct local run and preserves its original result', async () => {
  const send = preview(() => { throw new Error('Fixture retries must not start a process or contact the Host'); });
  const runId = 'demo-v2-process-failed';
  const original = (await send('tasks.get', { runId })).data;
  const config = (await send('config.get')).data;
  config.cliSelections.codex = (await send('agents.list')).data.installations.codex[2].resolvedPath;
  await send('config.save', config);
  const retried = (await send('tasks.rerun', { runId, requestId: 'v2-new-run-request' })).data;
  assert.notEqual(retried.runId, runId); assert.notEqual(retried.task.requestId, original.task.requestId);
  assert.equal(retried.status.state, 'running'); assert.equal(retried.result, undefined);
  assert.equal(retried.config.permission, 'yolo');
  assert.deepEqual((await send('tasks.get', { runId })).data, original);
  assert.deepEqual((await send('tasks.rerun', { runId, requestId: 'v2-new-run-request' })).data, retried);
});

test('duplicate comment proposals retain distinct stable IDs and exact Markdown through editable operation history', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Proposal samples must never contact the Host or GitHub'); };
  const send = preview(noHost, storage);
  const runId = 'demo-v2-completed-review';
  const run = (await send('tasks.get', { runId })).data;
  const comments = run.result.nextActions.filter(proposal => proposal.kind === 'comment');
  assert.equal(comments.length, 2); assert.notEqual(comments[0].proposalId, comments[1].proposalId);
  assert.equal(comments[0].proposalId, 'preview-demo-v2-completed-review-proposal-4');
  assert.equal(comments[1].proposalId, 'preview-demo-v2-completed-review-proposal-5');
  assert.equal(comments[0].body, '    var comparer = StringComparer.OrdinalIgnoreCase;\n\nThe local sample review found a case-sensitive path deduplication issue.  \n\n');
  assert.equal(comments[1].body, '  ### Verification note\n\n- Inspect the retained sample evidence.  \n- No desktop or hardware verification is claimed.\n\n  ');
  assert.deepEqual(run.result.nextSteps.filter(proposal => proposal.kind === 'comment'), comments);
  assert.deepEqual((await preview(noHost, storage)('tasks.get', { runId })).data.result.nextActions, run.result.nextActions);
  const historic = (await send('tasks.get', { runId: 'demo-actionable-review' })).data;
  assert.ok(historic.result.nextSteps.every(proposal => proposal.proposalId)); assert.equal(historic.result.schemaVersion, undefined);
  for (const proposal of comments) {
    assert.equal((await send('resultActions.prepare', { runId, proposalId: proposal.proposalId })).ok, false, 'comments continue through the editable operation flow');
    const response = await send('operations.submit', { runId, proposalId: proposal.proposalId, operationId: `operation-${proposal.proposalId}`, kind: proposal.kind, body: proposal.body, expectedAccount: 'pulse-demo', expectedHeadSha: run.task.expectedHeadSha });
    assert.equal(response.ok, true); assert.equal(response.data.proposalId, proposal.proposalId); assert.equal(response.data.body, proposal.body);
  }
  const operations = (await send('operations.list', { runId })).data.operations;
  assert.equal(operations.length, 2);
  for (const proposal of comments) assert.equal(operations.find(operation => operation.proposalId === proposal.proposalId).body, proposal.body);
  const editedBody = '    Edited sample Markdown.  \n\n  ';
  const edited = (await send('operations.submit', { runId, proposalId: comments[1].proposalId, kind: 'comment', body: editedBody, expectedAccount: 'pulse-demo', expectedHeadSha: run.task.expectedHeadSha })).data;
  assert.equal(edited.body, editedBody); assert.equal(edited.proposalId, comments[1].proposalId);
  assert.equal((await send('operations.submit', { runId, proposalId: comments[0].proposalId, kind: 'suggestChanges', body: 'Sample', expectedAccount: 'pulse-demo', expectedHeadSha: run.task.expectedHeadSha, suggestions: run.result.review.suggestions })).ok, false);
});

test('new result proposals prepare durable confirmation records with immutable targets and preserved pull request Markdown', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Preparing sample proposals must not contact the Host or GitHub'); };
  const send = preview(noHost, storage);
  for (const [runId, kind] of [['demo-v2-proposed-pr-actions', 'merge-pr'], ['demo-v2-proposed-pr-actions', 'trigger-ci'], ['demo-v2-proposed-create-pr', 'create-pr']]) {
    const run = (await send('tasks.get', { runId })).data;
    const proposal = run.result.nextActions.find(item => item.kind === kind);
    const payload = { runId, proposalId: proposal.proposalId };
    const prepared = await send('resultActions.prepare', payload);
    assert.equal(prepared.ok, true); assert.equal(prepared.data.kind, kind); assert.equal(prepared.data.status, 'prepared');
    assert.match(prepared.data.operationId, /^[a-f0-9-]{36}$/);
    assert.deepEqual((await preview(noHost, storage)('resultActions.prepare', payload)).data, prepared.data);
    const action = (await send('webActions.preview', { operationId: prepared.data.operationId })).data;
    assert.equal(action.canSubmit, true); assert.equal(action.account, 'pulse-demo'); assert.equal(action.sourceOrigin, `pulse-task://${runId}`);
    assert.deepEqual(action.draft.target, { ...run.task.target, repository: run.task.repository });
    assert.equal(action.draft.expectedHeadSha, run.task.expectedHeadSha);
    if (kind === 'create-pr') {
      assert.deepEqual(action.draft.pullRequest, proposal.pullRequest);
      assert.equal(action.draft.pullRequest.body, '    PreserveKeyboardFocus();\n\nRestore focus to the original setting after the dialog closes.  \n\nValidation: sample focused checks only.\n\n  ');
      assert.equal(action.sourceHeadSha, 'd'.repeat(40)); assert.equal(action.draft.pullRequest.draft, true);
      assert.match(proposal.reason, /existing remote branches/); assert.match(proposal.reason, /does not commit or push/);
    }
    if (kind === 'trigger-ci') assert.equal(action.draft.body, '/azp run');
    assert.equal((await send('resultActions.prepare', { ...payload, target: { repository: 'another/repo', type: 'pr', number: 99 }, expectedHeadSha: 'f'.repeat(40), body: 'Injected content' })).ok, false);
    assert.equal((await send('webActions.submit', { operationId: prepared.data.operationId, expectedAccount: 'different-account' })).ok, false);
    assert.equal((await send('webActions.preview', { operationId: prepared.data.operationId })).data.status, 'prepared');
    const submitted = (await send('webActions.submit', { operationId: prepared.data.operationId, expectedAccount: 'pulse-demo' })).data;
    assert.equal(submitted.status, 'succeeded'); assert.match(submitted.completedSteps[0], /Simulated GitHub action/);
    assert.deepEqual((await send('resultActions.prepare', payload)).data, submitted);
    assert.deepEqual((await send('webActions.submit', { operationId: prepared.data.operationId, expectedAccount: 'pulse-demo' })).data, submitted);
    assert.equal((await send('webActions.preview', { operationId: prepared.data.operationId })).data.canSubmit, false);
  }
});

test('stale result proposals prepare locally but confirmation and submit reject the changed current SHA', async () => {
  const send = preview(() => { throw new Error('Stale proposal fixtures must not contact the Host or GitHub'); });
  const runId = 'demo-v2-stale-pr-actions';
  const run = (await send('tasks.get', { runId })).data;
  const current = (await send('operations.preview', { runId })).data;
  assert.equal(current.stale, true); assert.equal(current.headSha, 'e'.repeat(40)); assert.equal(current.expectedHeadSha, run.task.expectedHeadSha);
  assert.match(current.reasons.join(' '), /immutable task revision/);
  for (const proposal of run.result.nextActions) {
    const payload = { runId, proposalId: proposal.proposalId };
    const prepared = (await send('resultActions.prepare', payload)).data;
    assert.equal(prepared.status, 'prepared', 'preparation freezes local task data without consulting current GitHub state');
    const action = (await send('webActions.preview', { operationId: prepared.operationId })).data;
    assert.equal(action.draft.expectedHeadSha, run.task.expectedHeadSha); assert.notEqual(action.headSha, action.draft.expectedHeadSha);
    assert.equal(action.canSubmit, false); assert.ok(action.blockers.some(blocker => blocker.code === 'STALE_HEAD'));
    assert.equal((await send('webActions.submit', { operationId: prepared.operationId, expectedAccount: 'pulse-demo' })).ok, false);
    assert.deepEqual((await send('resultActions.prepare', payload)).data, prepared);
  }
});

test('prepared merge and CI samples recheck the current SHA after reload without creating a replacement operation', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Prepared result fixtures must not contact the Host or GitHub'); };
  const send = preview(noHost, storage);
  const runId = 'demo-v2-proposed-pr-actions';
  const run = (await send('tasks.get', { runId })).data;
  const prepared = [];
  for (const proposal of run.result.nextActions) prepared.push({ payload: { runId, proposalId: proposal.proposalId }, action: (await send('resultActions.prepare', { runId, proposalId: proposal.proposalId })).data });
  const key = Array.from(storage.keys())[0];
  const changed = JSON.parse(storage.get(key));
  changed.remoteTargets['microsoft/PowerToys:pr:41288'] = { headSha: 'c'.repeat(40), ciState: 'success', state: 'OPEN', draftPr: false };
  const reloaded = preview(noHost, new Map([[key, JSON.stringify(changed)]]));
  for (const item of prepared) {
    assert.deepEqual((await reloaded('resultActions.prepare', item.payload)).data, item.action);
    const action = (await reloaded('webActions.preview', { operationId: item.action.operationId })).data;
    assert.equal(action.canSubmit, false); assert.equal(action.headSha, 'c'.repeat(40)); assert.equal(action.blockers[0].code, 'STALE_HEAD');
    assert.equal((await reloaded('webActions.submit', { operationId: item.action.operationId, expectedAccount: 'pulse-demo' })).ok, false);
  }
});

test('merge preparation ignores local execution judgments while preserving the typed draft contract', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Proposal validation must not contact the Host or GitHub'); };
  const send = preview(noHost, storage);
  const runId = 'demo-v2-proposed-pr-actions';
  const run = (await send('tasks.get', { runId })).data;
  const key = Array.from(storage.keys())[0];
  for (const alter of [
    target => { target.status.state = 'failed'; },
    target => { target.result.outcome = 'blocked'; },
    target => { target.result.validation.find(check => check.id === 'verification').status = 'not_run'; },
    target => { target.result.review.headSha = 'f'.repeat(40); },
    target => { target.result.nextActions[0].body = ' '; },
  ]) {
    const changed = JSON.parse(storage.get(key));
    alter(changed.runs.find(item => item.runId === runId));
    const modified = preview(noHost, new Map([[key, JSON.stringify(changed)]]));
    assert.equal((await modified('resultActions.prepare', { runId, proposalId: run.result.nextActions[0].proposalId })).ok, changed.runs.find(item => item.runId === runId).result.nextActions[0].body === '');
  }
  const emptyCi = JSON.parse(storage.get(key));
  emptyCi.runs.find(item => item.runId === runId).result.nextActions[1].body = '';
  const ci = preview(noHost, new Map([[key, JSON.stringify(emptyCi)]]));
  const prepared = (await ci('resultActions.prepare', { runId, proposalId: run.result.nextActions[1].proposalId })).data;
  assert.equal((await ci('webActions.preview', { operationId: prepared.operationId })).data.draft.body, '/azp run');
  assert.equal((await send('resultActions.prepare', { runId, proposalId: 'not-a-proposal' })).ok, false);
  const recovery = (await send('tasks.get', { runId: 'demo-v2-blocked-setup' })).data;
  assert.equal((await send('resultActions.prepare', { runId: recovery.runId, proposalId: recovery.result.nextActions[0].proposalId })).ok, false);
});

test('observed execution fixtures remain separate from configured selections and allow partial observations', async () => {
  const send = preview(() => { throw new Error('Observed execution samples must not invoke a CLI'); });
  const running = (await send('tasks.get', { runId: 'demo-running-review' })).data;
  assert.equal(running.config.model, 'gpt-5.3-codex'); assert.equal(running.config.reasoningEffort, 'high');
  assert.equal(running.status.observedExecution.model, 'gpt-6-astra'); assert.equal(running.status.observedExecution.reasoningEffort, 'ultra');
  const partial = (await send('tasks.get', { runId: 'demo-completed-fix' })).data;
  assert.equal(partial.status.observedExecution.model, 'gpt-6-astra'); assert.equal(partial.status.observedExecution.reasoningEffort, undefined);
  const historical = (await send('tasks.get', { runId: 'demo-historical-failure' })).data;
  assert.equal(historical.status.observedExecution, undefined);
});
test('sample CLI logs use independent byte cursors and explicitly identify fixture output', async () => {
  const send = preview(() => { throw new Error('Sample logs must not call the Host'); });
  let cursor = 0, text = '';
  for (let page = 0; page < 100; page++) {
    const result = await send('tasks.logs', { runId: 'demo-running-review', stream: 'stdout', cursor, limitBytes: 17 });
    assert.equal(result.ok, true); assert.equal(result.data.sample, true);
    assert.ok(result.data.nextCursor >= cursor);
    cursor = result.data.nextCursor; text += result.data.text;
    if (result.data.eof) break;
  }
  assert.ok(text.includes('SAMPLE OUTPUT')); assert.ok(!text.includes('\uFFFD'));
  assert.equal(cursor, new TextEncoder().encode(text).length);
  const stderr = (await send('tasks.logs', { runId: 'demo-failed-e2e', stream: 'stderr' })).data;
  assert.ok(stderr.text.includes('SAMPLE STDERR'));
  assert.ok(stderr.text.includes('test driver is missing'));
  assert.equal((await send('tasks.logs', { runId: 'demo-failed-e2e', stream: '../prompt.txt' })).ok, false);
});
test('preview settings and worktree task examples stay local and use the current config schema', async () => {
  const send = preview(() => { throw new Error('Business preview must not contact Host'); });
  const config = (await send('config.get')).data;
  assert.ok(config.mainRepoFolder && config.worktreeRoot);
  assert.equal(config.permission, 'yolo');
  assert.equal(config.cliSelections.codex, ''); assert.equal(config.cliSelections.copilot, '');
  assert.equal(config.cliPaths, undefined);
  assert.equal(config.repositories, undefined);
  assert.equal(config.timeoutSeconds, undefined);
  const updated = { ...config, agent: 'copilot', permission: 'yolo', githubAccount: 'chosen-account' };
  updated.cliSelections.copilot = (await send('agents.list')).data.installations.copilot[0].resolvedPath;
  assert.deepEqual(JSON.parse(JSON.stringify((await send('config.save', updated)).data)), JSON.parse(JSON.stringify(updated)));
  const rerun = (await send('tasks.rerun', { runId: 'demo-failed-e2e', requestId: 'request-new' })).data;
  assert.equal(rerun.config.agent, 'copilot');
  assert.equal(rerun.config.permission, 'yolo');
  assert.equal(rerun.config.mainRepoFolder, config.mainRepoFolder);
  assert.ok(rerun.config.repoFolder.startsWith(config.worktreeRoot + '\\'));
  assert.ok(rerun.config.worktreeBranch.startsWith('codex/pulse-'));
  assert.equal((await send('operations.preview', { runId: 'demo-pending-review' })).data.account, 'chosen-account');
  assert.equal(config.e2ePrompt, 'powertoys-pr-e2e-test.prompt.md');
  assert.equal(config.reproductionPrompt, '');
});

test('sample task execution preserves per-task overrides and resolves omitted values from the selected CLI defaults', async () => {
  const send = preview(() => { throw new Error('Execution fixtures must not invoke a CLI'); });
  const config = (await send('config.get')).data;
  config.agent = 'copilot';
  const installed = (await send('agents.list')).data.installations;
  config.cliSelections = { codex: installed.codex[1].resolvedPath, copilot: installed.copilot[0].resolvedPath };
  config.agentDefaults = { codex: { model: 'gpt-5.3-codex', reasoningEffort: 'medium' }, copilot: { model: 'claude-sonnet-4.5', reasoningEffort: 'high' } };
  await send('config.save', config);
  const defaults = (await send('agents.defaults')).data;
  assert.equal(defaults.defaultAgent, 'copilot'); assert.equal(defaults.defaults.copilot.reasoningEffort, 'high');
  const inherited = (await send('tasks.rerun', { runId: 'demo-failed-e2e', requestId: 'inherited-execution' })).data;
  assert.equal(inherited.config.agent, 'copilot'); assert.equal(inherited.config.model, 'claude-sonnet-4.5'); assert.equal(inherited.config.reasoningEffort, 'high');
  assert.equal(inherited.config.executionSource.model, 'default');
  const overridden = (await send('tasks.rerun', { runId: 'demo-running-review', requestId: 'overridden-execution' })).data;
  assert.equal(overridden.config.agent, 'codex'); assert.equal(overridden.config.model, 'gpt-5.3-codex'); assert.equal(overridden.config.reasoningEffort, 'high');
  assert.equal(overridden.config.executionSource.reasoningEffort, 'task');
  const reset = (await send('tasks.rerun', { runId: 'demo-quick-failure', requestId: 'explicit-cli-default' })).data;
  assert.equal(reset.config.model, ''); assert.equal(reset.config.reasoningEffort, ''); assert.equal(reset.config.executionSource.model, 'cli');
});

test('website action samples remain local, disclose all selected comments, and deduplicate confirmed submission', async () => {
  const send = preview(() => { throw new Error('GitHub action samples must not contact Host or GitHub'); });
  const operationId = '11111111-1111-4111-8111-111111111111';
  const draft = (await send('webActions.preview', { operationId })).data;
  assert.equal(draft.status, 'prepared'); assert.equal(draft.canSubmit, true);
  assert.equal(draft.draft.review.event, 'REQUEST_CHANGES');
  assert.equal(draft.draft.review.comments.length, 1);
  assert.equal(draft.draft.review.generalComments.length, 1);
  assert.equal((await send('webActions.submit', { operationId, expectedAccount: 'different-account' })).ok, false);
  assert.equal((await send('webActions.preview', { operationId })).data.status, 'prepared');
  const submitted = (await send('webActions.submit', { operationId, expectedAccount: 'pulse-demo' })).data;
  assert.equal(submitted.status, 'succeeded'); assert.match(submitted.completedSteps[0], /Simulated/);
  assert.deepEqual((await send('webActions.submit', { operationId, expectedAccount: 'pulse-demo' })).data, submitted);
  assert.equal((await send('webActions.preview', { operationId })).data.canSubmit, false);
});

test('stale action fixtures block submit and unknown outcomes never turn into a new submission', async () => {
  const send = preview(() => { throw new Error('GitHub action samples must not contact Host or GitHub'); });
  const blockedId = '66666666-6666-4666-8666-666666666666';
  const blocked = (await send('webActions.preview', { operationId: blockedId })).data;
  assert.equal(blocked.canSubmit, false); assert.equal(blocked.blockers.length, 2);
  assert.equal((await send('webActions.submit', { operationId: blockedId, expectedAccount: 'pulse-demo' })).ok, false);
  const unknownId = '55555555-5555-4555-8555-555555555555';
  const result = (await send('webActions.submit', { operationId: unknownId, expectedAccount: 'pulse-demo' })).data;
  assert.equal(result.status, 'unknown'); assert.equal(result.remainingSteps.length, 1);
});

test('fork comment samples disclose the separate assignment target and cancellation prevents submit', async () => {
  const send = preview(() => { throw new Error('GitHub action samples must not contact Host or GitHub'); });
  const operationId = '33333333-3333-4333-8333-333333333333';
  const draft = (await send('webActions.preview', { operationId })).data.draft;
  assert.equal(draft.target.repository, 'muyuanms/powertoys');
  assert.equal(draft.assignmentTarget.repository, 'microsoft/PowerToys');
  assert.equal(draft.assignmentTarget.number, 41192);
  assert.equal((await send('webActions.cancel', { operationId })).data.status, 'cancelled');
  assert.equal((await send('webActions.submit', { operationId, expectedAccount: 'pulse-demo' })).data.status, 'cancelled');
});

test('unknown action result checks simulate unique completion, partial completion, and missing evidence without writes', async () => {
  const send = preview(() => { throw new Error('Result-check fixtures must not contact Host or GitHub'); });
  for (const [operationId, expectedStatus, remainingCount] of [
    ['99999999-9999-4999-8999-999999999999', 'succeeded', 0],
    ['aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'partial', 1],
    ['55555555-5555-4555-8555-555555555555', 'unknown', 1],
  ]) {
    const before = (await send('webActions.preview', { operationId })).data;
    assert.equal(before.status, 'unknown');
    const result = (await send('webActions.reconcile', { operationId })).data;
    assert.equal(result.status, expectedStatus); assert.equal(result.remainingSteps.length, remainingCount);
    const after = (await send('webActions.preview', { operationId })).data;
    assert.deepEqual(after.draft, before.draft); assert.equal(after.canSubmit, false);
    if (expectedStatus === 'succeeded') { assert.equal(after.error, undefined); assert.deepEqual(Array.from(after.completedSteps), ['comment']); }
    if (expectedStatus === 'partial') { assert.equal(after.error.code, 'PARTIAL_ACTION'); assert.deepEqual(Array.from(after.completedSteps), ['review']); }
    if (expectedStatus === 'unknown') { assert.equal(after.error.code, 'OPERATION_UNKNOWN'); assert.match(after.error.guidance, /does not prove the write failed/); assert.equal(after.completedSteps.length, 0); }
    assert.deepEqual((await send('webActions.submit', { operationId, expectedAccount: 'pulse-demo' })).data, result);
  }
});

test('the new sample revision isolates old session fixtures and exposes assessment separately from task completion', async () => {
  const storage = new Map([['pulse-ui-preview-v13', JSON.stringify({ config: {}, runs: [], operations: {} })]]);
  const send = preview(() => { throw new Error('Assessment samples must not contact the Host'); }, storage);
  assert.equal((await send('tasks.list', { limit: 100 })).data.runs.length, 53);
  assert.ok(storage.has('pulse-ui-preview-v17'));
  const runId = 'demo-v2-negative-e2e';
  const run = (await send('tasks.get', { runId })).data;
  assert.equal(run.status.state, 'succeeded'); assert.equal(run.result.outcome, 'completed');
  assert.equal(run.result.assessment.status, 'failed'); assert.equal(run.result.assessment.revisionSha, run.task.expectedHeadSha);
  assert.ok(run.result.validation.some(item => item.status === 'failed' && item.required === false));
  const comment = run.result.nextActions.find(item => item.kind === 'comment');
  const merge = run.result.nextActions.find(item => item.kind === 'merge-pr');
  assert.equal(comment.availability.enabled, true); assert.match(comment.body, /regression found/);
  assert.equal(merge.availability.enabled, true);
  const available = (await send('operations.preview', { runId })).data;
  assert.equal(available.canComment, true); assert.equal(available.canApprove, true);
  assert.equal((await send('operations.submit', { runId, proposalId: comment.proposalId, kind: 'comment', body: comment.body, expectedAccount: available.account, expectedHeadSha: run.task.expectedHeadSha })).data.status, 'succeeded');
  assert.equal((await send('resultActions.prepare', { runId, proposalId: merge.proposalId })).ok, true);
});

test('legacy finding severity stays visible without being promoted to a confirmed P0 manual restriction', async () => {
  const send = preview(() => { throw new Error('Availability samples must not contact the Host'); });
  const runId = 'demo-v2-completed-review';
  const run = (await send('tasks.get', { runId })).data;
  const remote = (await send('operations.preview', { runId })).data;
  for (const kind of ['approve', 'merge-pr']) {
    const proposal = run.result.nextActions.find(item => item.kind === kind);
    assert.ok(proposal); assert.equal(proposal.availability.enabled, kind === 'merge-pr');
  }
  assert.equal(run.result.assessment.subject, 'original-pr');
  assert.equal(remote.canApprove, true); assert.equal(remote.canRequestChanges, true); assert.equal(remote.hasConfirmedP0, false);
});

test('review suggestions are associated with the selected proposal and cannot leak from another same-kind draft', async () => {
  const send = preview(() => { throw new Error('Suggestion samples must not contact the Host'); });
  const runId = 'demo-v2-completed-review';
  const run = (await send('tasks.get', { runId })).data;
  const proposals = run.result.nextActions.filter(item => item.kind === 'suggestChanges');
  assert.equal(proposals.length, 2); assert.notDeepEqual(proposals[0].suggestionIds, proposals[1].suggestionIds);
  const first = run.result.review.suggestions.find(item => item.id === proposals[0].suggestionIds[0]);
  const second = run.result.review.suggestions.find(item => item.id === proposals[1].suggestionIds[0]);
  const payload = { runId, proposalId: proposals[1].proposalId, kind: 'suggestChanges', body: proposals[1].body, expectedAccount: 'pulse-demo', expectedHeadSha: run.task.expectedHeadSha };
  assert.equal((await send('operations.submit', { ...payload, suggestions: [first] })).ok, false);
  const submitted = (await send('operations.submit', { ...payload, suggestions: [second] })).data;
  assert.deepEqual(submitted.suggestions, [second]); assert.equal(submitted.proposalId, proposals[1].proposalId);
});

test('create PR compares the first remote preview to the tested source SHA and explains unpublished local fixes', async () => {
  const send = preview(() => { throw new Error('Source-commit samples must not contact the Host'); });
  for (const [runId, code] of [['demo-v2-create-pr-source-changed', 'SOURCE_HEAD_CHANGED'], ['demo-v2-local-unpublished-fix', 'SOURCE_BRANCH_UNPUBLISHED']]) {
    const run = (await send('tasks.get', { runId })).data;
    assert.equal(run.result.assessment.subject, 'local-candidate'); assert.equal(run.result.assessment.status, 'passed');
    const proposal = run.result.nextActions.find(item => item.kind === 'create-pr');
    assert.equal(proposal.pullRequest.sourceHeadSha, run.result.assessment.revisionSha);
    const prepared = (await send('resultActions.prepare', { runId, proposalId: proposal.proposalId })).data;
    const action = (await send('webActions.preview', { operationId: prepared.operationId })).data;
    assert.equal(action.canSubmit, false); assert.ok(action.blockers.some(item => item.code === code));
    assert.equal(action.draft.pullRequest.sourceHeadSha, proposal.pullRequest.sourceHeadSha);
    if (code === 'SOURCE_HEAD_CHANGED') assert.equal(action.sourceHeadSha, proposal.pullRequest.sourceHeadSha, 'the confirmation retains the verified source SHA even when the remote branch drifts');
    else assert.equal(action.sourceHeadSha, undefined);
    assert.equal((await send('webActions.submit', { operationId: prepared.operationId, expectedAccount: 'pulse-demo' })).ok, false);
    assert.equal((await send('resultActions.list', { runId })).data.operations[0].status, 'prepared');
  }
});

test('cancelled proposals permit a distinct attempt while preserving attempt idempotency and task action history', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Attempt samples must not contact the Host or GitHub'); };
  const send = preview(noHost, storage);
  const runId = 'demo-v2-proposed-create-pr';
  const proposal = (await send('tasks.get', { runId })).data.result.nextActions[0];
  const firstPayload = { runId, proposalId: proposal.proposalId, attemptId: crypto.randomUUID() };
  const first = (await send('resultActions.prepare', firstPayload)).data;
  assert.equal(first.attemptId, firstPayload.attemptId); assert.equal(first.retryAllowed, false);
  const joinedPayload = { ...firstPayload, attemptId: crypto.randomUUID() };
  assert.equal((await send('resultActions.prepare', joinedPayload)).data.operationId, first.operationId, 'an unresolved confirmation is shared instead of duplicated');
  await send('webActions.cancel', { operationId: first.operationId });
  const same = (await send('resultActions.prepare', firstPayload)).data;
  assert.equal(same.operationId, first.operationId); assert.equal(same.status, 'cancelled'); assert.equal(same.retryAllowed, true);
  assert.equal((await send('resultActions.prepare', joinedPayload)).data.operationId, first.operationId, 'the joined attempt keeps its durable association after cancellation');
  const secondPayload = { ...firstPayload, attemptId: crypto.randomUUID(), retry: true };
  const second = (await send('resultActions.prepare', secondPayload)).data;
  assert.notEqual(second.operationId, first.operationId); assert.equal(second.status, 'prepared');
  const reloaded = preview(noHost, storage);
  assert.equal((await reloaded('resultActions.prepare', secondPayload)).data.operationId, second.operationId);
  const history = (await reloaded('resultActions.list', { runId })).data;
  assert.equal(history.totalCount, 2); assert.equal(history.truncated, false);
  assert.deepEqual(history.operations.map(item => item.status), ['prepared', 'cancelled']);
  assert.ok(history.operations.every(item => item.runId === runId && item.proposalId === proposal.proposalId));
});

test('only an unsubmitted failure can get a new attempt; unknown outcomes retain the original operation', async () => {
  const noHost = () => { throw new Error('Attempt recovery samples must not contact the Host'); };
  const storage = new Map();
  const send = preview(noHost, storage);
  const runId = 'demo-v2-proposed-pr-actions';
  const proposal = (await send('tasks.get', { runId })).data.result.nextActions[0];
  const initial = (await send('resultActions.prepare', { runId, proposalId: proposal.proposalId, attemptId: crypto.randomUUID() })).data;
  const key = Array.from(storage.keys())[0];
  for (const [status, writeStarted, retryAllowed] of [['failed', false, true], ['failed', true, false], ['unknown', false, false], ['partial', false, false]]) {
    const fixture = JSON.parse(storage.get(key));
    Object.assign(fixture.webActions[initial.operationId], { status, writeStarted, canSubmit: false });
    const modified = preview(noHost, new Map([[key, JSON.stringify(fixture)]]));
    const current = (await modified('webActions.get', { operationId: initial.operationId })).data;
    assert.equal(current.retryAllowed, retryAllowed);
    const next = (await modified('resultActions.prepare', { runId, proposalId: proposal.proposalId, attemptId: crypto.randomUUID() })).data;
    assert.equal(next.operationId !== initial.operationId, retryAllowed, status);
    assert.equal((await modified('resultActions.list', { runId })).data.totalCount, retryAllowed ? 2 : 1);
  }
});

test('CI needs an explicit new attempt and evidence of a later failed run before it can be triggered again', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('CI retry samples must not contact the Host or GitHub'); };
  const send = preview(noHost, storage);
  const runId = 'demo-v2-ci-retry';
  const proposal = (await send('tasks.get', { runId })).data.result.nextActions[0];
  const original = (await send('resultActions.list', { runId })).data.operations[0];
  assert.equal(original.status, 'succeeded'); assert.equal(original.retryAllowed, true);
  assert.equal((await send('resultActions.prepare', { runId, proposalId: proposal.proposalId, attemptId: crypto.randomUUID() })).data.operationId, original.operationId);
  const payload = { runId, proposalId: proposal.proposalId, attemptId: crypto.randomUUID(), retry: true };
  const retry = (await send('resultActions.prepare', payload)).data;
  assert.notEqual(retry.operationId, original.operationId); assert.equal(retry.retryRequested, true);
  assert.equal((await send('webActions.preview', { operationId: retry.operationId })).data.canSubmit, true);
  assert.equal((await send('webActions.submit', { operationId: retry.operationId, expectedAccount: 'pulse-demo' })).data.status, 'succeeded');
  assert.equal((await send('resultActions.prepare', payload)).data.operationId, retry.operationId);
  assert.equal((await send('resultActions.prepare', { ...payload, retry: false })).ok, false, 'an attempt identity cannot change its retry intent');
  const repeated = (await send('resultActions.prepare', { ...payload, attemptId: crypto.randomUUID() })).data;
  const staleCi = (await send('webActions.preview', { operationId: repeated.operationId })).data;
  assert.equal(staleCi.canSubmit, false); assert.ok(staleCi.blockers.some(item => item.code === 'CI_RETRY_NOT_REQUIRED'));
  const key = Array.from(storage.keys())[0];
  const changed = JSON.parse(storage.get(key));
  changed.remoteTargets['microsoft/PowerToys:pr:41294'].ciEvidence = 'sample-ci-failed-after-second-trigger';
  const later = preview(noHost, new Map([[key, JSON.stringify(changed)]]));
  assert.equal((await later('webActions.preview', { operationId: repeated.operationId })).data.canSubmit, true);
});

test('new GitHub confirmations and editable operations use the current account and keep confirmed history stable', async () => {
  const send = preview(() => { throw new Error('Account samples must not contact the Host'); });
  const runId = 'demo-v2-proposed-pr-actions';
  const proposal = (await send('tasks.get', { runId })).data.result.nextActions[0];
  const action = (await send('resultActions.prepare', { runId, proposalId: proposal.proposalId })).data;
  const config = (await send('config.get')).data;
  await send('config.save', { ...config, githubAccount: 'chosen-account' });
  assert.equal((await send('operations.preview', { runId: 'demo-v2-completed-review' })).data.account, 'chosen-account');
  assert.equal((await send('webActions.preview', { operationId: action.operationId })).data.account, 'chosen-account');
  assert.equal((await send('webActions.submit', { operationId: action.operationId, expectedAccount: 'pulse-demo' })).ok, false);
  const submitted = (await send('webActions.submit', { operationId: action.operationId, expectedAccount: 'chosen-account' })).data;
  assert.equal(submitted.account, 'chosen-account');
  await send('config.save', { ...config, githubAccount: 'third-account' });
  assert.equal((await send('resultActions.list', { runId })).data.operations[0].account, 'chosen-account');
});

test('sample rerun explicitly chooses profile defaults or task overrides and retains the acknowledged PR SHA', async () => {
  const send = preview(() => { throw new Error('Rerun samples must not invoke a model or the Host'); });
  const config = (await send('config.get')).data;
  const installations = (await send('agents.list')).data.installations;
  config.agent = 'copilot'; config.agentDefaults.copilot = { model: 'claude-sonnet-4.5', reasoningEffort: 'medium' };
  config.cliSelections = { codex: installations.codex[2].resolvedPath, copilot: installations.copilot[0].resolvedPath };
  await send('config.save', config);
  const runId = 'demo-running-review';
  const original = (await send('tasks.get', { runId })).data;
  const defaults = (await send('tasks.rerun', { runId, requestId: 'use-current-defaults', execution: null })).data;
  assert.equal(defaults.task.execution, undefined); assert.equal(defaults.config.agent, 'copilot'); assert.equal(defaults.config.reasoningEffort, 'medium');
  assert.equal(defaults.task.expectedHeadSha, original.task.expectedHeadSha);
  const revisedSha = 'f'.repeat(40);
  const payload = { runId, requestId: 'explicit-retry-config', execution: { agent: 'codex', model: '', reasoningEffort: 'high' }, expectedHeadSha: revisedSha };
  const overridden = (await send('tasks.rerun', payload)).data;
  assert.equal(overridden.config.agent, 'codex'); assert.equal(overridden.config.model, ''); assert.equal(overridden.config.reasoningEffort, 'high');
  assert.equal(overridden.task.expectedHeadSha, revisedSha);
  assert.deepEqual((await send('tasks.rerun', payload)).data, overridden, 'a lost acknowledgment uses the same frozen retry payload');
  assert.deepEqual((await send('tasks.get', { runId })).data, original);
  assert.equal((await send('tasks.rerun', { runId: 'demo-v2-process-failed', expectedHeadSha: revisedSha })).ok, false);
  assert.equal((await send('targets.get', { target: { type: 'pr', number: 41290 } })).data.headSha, 'e'.repeat(40));
});

test('v3 PR fixtures separate complete reports, default advice, explicit E2E levels and manual P0 permission', async () => {
  const send = preview(() => { throw new Error('V3 fixture views must not contact the Host or GitHub'); });
  for (const [runId, level, recommendation, approve] of [
    ['demo-v3-pr-p0', 'required', 'address-findings', false],
    ['demo-v3-pr-p1-required', 'required', 'address-findings', true],
    ['demo-v3-pr-p2-p3', 'recommended', 'approve', true],
    ['demo-v3-pr-not-needed', 'not_needed', 'none', true],
    ['demo-v3-pr-required', 'required', 'run-e2e', true],
  ]) {
    const run = await completeResult(send, runId);
    assert.equal(run.result.schemaVersion, 3); assert.equal(run.result.report.complete, true); assert.equal(run.result.report.rechecked, true);
    assert.equal(run.result.e2eAssessment.level, level); assert.equal(run.result.recommendation.kind, recommendation);
    assert.equal(run.result.e2eAssessment.scenarios.length, run.result.e2eAssessment.expectedResults.length);
    assert.ok(run.result.e2eAssessment.evidence.length); assert.equal('verificationRecommendation' in run.result, false);
    for (const row of run.result.findings) {
      assert.ok(['P0', 'P1', 'P2', 'P3'].includes(row.priority)); assert.equal(row.severity, undefined); assert.equal(row.confirmed, true);
      for (const field of ['details', 'impact', 'trigger', 'rootCause', 'fixSuggestion']) assert.ok(row[field]);
      assert.ok(row.evidence.length); assert.equal(typeof row.feedback.body, 'string');
    }
    const available = (await send('operations.preview', { runId })).data;
    assert.equal(available.canApprove, approve); assert.equal(available.hasConfirmedP0, !approve);
    assert.equal(available.canComment, true); assert.equal(available.canRequestChanges, true); assert.equal(available.canClose, true);
  }
  const incomplete = (await send('tasks.get', { runId: 'demo-v3-pr-incomplete' })).data;
  assert.equal(incomplete.result.report.complete, false); assert.equal(incomplete.result.report.rechecked, false);
  assert.equal(incomplete.result.recommendation.kind, 'incomplete'); assert.equal(incomplete.result.e2eAssessment, null);
  assert.equal((await send('operations.preview', { runId: incomplete.runId })).data.canApprove, true);
  const legacy = (await send('tasks.get', { runId: 'demo-pending-review' })).data;
  assert.equal(legacy.result.e2eAssessment, undefined); assert.equal(legacy.result.report, undefined);
  const capabilities = (await send('hello')).data;
  assert.deepEqual(capabilities.resultSchemaVersions, [2, 3]); assert.ok(capabilities.workflowKinds.includes('feature-research')); assert.ok(capabilities.workflowKinds.includes('bug-investigation'));
});

test('v3 paging preserves every final finding and nested coverage item while hidden-page P0 remains authoritative', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Paging must read session fixtures only'); };
  const send = preview(noHost, storage);
  const runId = 'demo-v3-pr-p0';
  const first = (await send('tasks.get', { runId })).data;
  assert.equal(first.result.findings.length, 25); assert.equal(first.result.findings.some(row => row.priority === 'P0'), false);
  assert.deepEqual(first.resultPaging.sections.map(row => [row.path, row.total]), [['findings', 225], ['report.coverage', 45]]);
  assert.equal((await send('operations.preview', { runId })).data.canApprove, false, 'P0 is evaluated from the full saved result before display paging finishes');
  const complete = await completeResult(send, runId);
  assert.equal(complete.result.findings.length, 225); assert.equal(new Set(complete.result.findings.map(row => row.id)).size, 225);
  assert.equal(complete.result.findings.at(-1).priority, 'P0'); assert.equal(complete.result.report.coverage.length, 45);
  for (const payload of [{ path: '__proto__.findings', offset: 0 }, { path: 'findings', offset: -1 }, { path: 'findings', offset: 0, limit: 201 }]) assert.equal((await send('tasks.resultPage', { runId, fingerprint: first.resultPaging.fingerprint, ...payload })).ok, false);
  assert.equal((await send('tasks.resultPage', { runId, path: 'findings', offset: 25, fingerprint: '0'.repeat(64) })).error.code, 'RESULT_CHANGED');
  const changed = JSON.parse(storage.get('pulse-ui-preview-v17'));
  changed.runs.find(run => run.runId === runId).result.findings.at(-1).evidence.push('Additional sample evidence changes the saved result fingerprint.');
  const reloaded = preview(noHost, new Map([['pulse-ui-preview-v17', JSON.stringify(changed)]]));
  assert.equal((await reloaded('tasks.resultPage', { runId, path: 'findings', offset: 25, fingerprint: first.resultPaging.fingerprint })).error.code, 'RESULT_CHANGED');
});

test('only confirmed open P0 on the same original PR revision blocks manual approval across related run history', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('P0 policy fixtures must not contact the Host'); };
  const send = preview(noHost, storage);
  await send('tasks.get', { runId: 'demo-v3-pr-p0' });
  const baseline = JSON.parse(storage.get('pulse-ui-preview-v17'));
  const target = baseline.runs.find(run => run.runId === 'demo-v3-pr-p0');
  for (const alter of [
    run => { run.result.findings.at(-1).priority = 'P1'; },
    run => { run.result.findings.at(-1).confirmed = false; },
    run => { run.result.findings.at(-1).status = 'fixed'; },
    run => { run.result.assessment.subject = 'local-candidate'; run.result.assessment.revisionSha = 'd'.repeat(40); run.result.reviewConclusion = null; },
  ]) {
    const changed = structuredClone(baseline); alter(changed.runs.find(run => run.runId === target.runId));
    const view = (await preview(noHost, new Map([['pulse-ui-preview-v17', JSON.stringify(changed)]]))('operations.preview', { runId: target.runId })).data;
    assert.equal(view.hasConfirmedP0, false); assert.equal(view.canApprove, true);
  }
  const matched = structuredClone(baseline);
  const active = matched.runs.find(run => run.runId === 'demo-running-review');
  active.task.repository = target.task.repository; active.task.target = structuredClone(target.task.target); active.task.expectedHeadSha = target.task.expectedHeadSha;
  const same = preview(noHost, new Map([['pulse-ui-preview-v17', JSON.stringify(matched)]]));
  assert.equal((await same('operations.preview', { runId: active.runId })).data.canApprove, false);
  matched.runs.find(run => run.runId === target.runId).task.expectedHeadSha = 'e'.repeat(40);
  const oldRevision = preview(noHost, new Map([['pulse-ui-preview-v17', JSON.stringify(matched)]]));
  assert.equal((await oldRevision('operations.preview', { runId: active.runId })).data.canApprove, true);
});

test('all Feature and Bug outcomes retain unified evidence, valid plan references and independent reproduction results', async () => {
  const send = preview(() => { throw new Error('Issue investigations are sample records only'); });
  const features = ['ready', 'needs_information', 'needs_decision', 'already_supported', 'duplicate', 'not_feasible'];
  const bugs = ['confirmed', 'needs_information', 'needs_verification', 'already_fixed', 'duplicate', 'not_a_bug'];
  const reproductionStates = new Set();
  for (const [kind, statuses] of [['feature', features], ['bug', bugs]]) for (const status of statuses) {
    const run = (await send('tasks.get', { runId: `demo-v3-${kind}-${status}` })).data;
    const assessment = run.result[`${kind}Assessment`];
    assert.equal(assessment.status, status); assert.ok(assessment.reasons.length); assert.ok(assessment.evidence.length);
    assert.equal(run.result.report.complete, true); assert.equal(run.result.report.rechecked, true);
    assert.equal(run.task.actionKind, kind === 'feature' ? 'feature-research' : 'bug-investigation');
    assert.equal('feasibility' in run.result, false); assert.equal(run.result.e2eAssessment, null);
    if (status === 'needs_information') assert.ok(assessment.questions.length);
    if (kind === 'feature' && status === 'needs_decision') assert.ok(assessment.alternatives.length >= 2);
    if (kind === 'feature' && status === 'ready') assert.ok(assessment.acceptanceCriteria.length);
    if (assessment.planId) assert.ok(run.result.plans.some(plan => plan.id === assessment.planId));
    for (const proposal of run.result.nextActions) {
      assert.equal(typeof proposal.recommended, 'boolean'); assert.ok(proposal.proposalId);
      if (proposal.kind === 'start-task') assert.ok(run.result.plans.some(plan => plan.id === proposal.planId && plan.kind === proposal.taskKind));
      if (proposal.kind === 'close-as-duplicate') assert.deepEqual(proposal.duplicateOf, assessment.relatedIssue);
    }
    if (kind === 'bug') {
      reproductionStates.add(assessment.reproduction.status);
      assert.ok(assessment.reproduction.environment); assert.ok(assessment.reproduction.steps.length); assert.ok(assessment.reproduction.expected); assert.ok(assessment.reproduction.observed);
      if (['reproduced', 'not_reproduced'].includes(assessment.reproduction.status)) assert.ok(assessment.reproduction.evidence.length);
    }
  }
  assert.deepEqual([...reproductionStates].sort(), ['blocked', 'not_reproduced', 'not_run', 'reproduced']);
  assert.equal((await send('tasks.get', { runId: 'demo-v3-bug-confirmed' })).data.result.bugAssessment.reproduction.status, 'not_run');
  assert.equal((await send('tasks.get', { runId: 'demo-v3-bug-needs_information' })).data.result.bugAssessment.reproduction.status, 'not_reproduced');
  assert.equal((await send('tasks.get', { runId: 'demo-v3-bug-not_a_bug' })).data.result.bugAssessment.reproduction.status, 'reproduced');
});

test('saved-plan tasks bind exact plan context and execution settings without a blanket prerequisite checkbox', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('Starting a fixture plan must not start any real process'); };
  const send = preview(noHost, storage);
  for (const runId of ['demo-v3-feature-ready', 'demo-v3-bug-confirmed', 'demo-v3-bug-needs_verification', 'demo-v3-bug-needs_information']) {
    const parent = (await send('tasks.get', { runId })).data;
    const proposal = parent.result.nextActions.find(row => row.kind === 'start-task');
    const plan = parent.result.plans.find(row => row.id === proposal.planId);
    const payload = { runId, proposalId: proposal.proposalId, requestId: `sample-plan-${runId}`, execution: { agent: 'copilot', model: 'claude-sonnet-4.5', reasoningEffort: 'high' } };
    const response = await send('tasks.startFromResult', payload);
    assert.equal(response.ok, true); const child = response.data;
    assert.equal(child.task.actionKind, plan.kind); assert.deepEqual(child.task.context.resultPlan.plan, plan);
    assert.equal(child.task.planSource.parentRunId, runId); assert.equal(child.task.planSource.planId, plan.id); assert.equal(child.task.planSource.proposalId, proposal.proposalId);
    assert.equal(child.task.planSource.revisionSha, parent.config.worktreeBase); assert.deepEqual(child.task.target, parent.task.target);
    assert.equal(child.task.context.resultPlan.prerequisitesReportedReady, false, 'requirements are shown to the next agent for preflight, not presumed fulfilled');
    assert.match(child.task.prompt, /verify prerequisites first/); assert.match(child.task.prompt, /Do not publish GitHub changes/);
    assert.equal(child.task.execution.agent, 'copilot'); assert.equal(child.config.model, 'claude-sonnet-4.5'); assert.equal(child.result, undefined);
    assert.deepEqual((await send('tasks.get', { runId })).data, parent);
    assert.equal((await preview(noHost, storage)('tasks.startFromResult', payload)).data.runId, child.runId);
    assert.equal((await send('tasks.startFromResult', { ...payload, execution: { agent: 'codex' } })).error.code, 'REQUEST_CONFLICT');
    assert.equal((await send('tasks.startFromResult', { ...payload, requestId: crypto.randomUUID(), target: { type: 'issue', number: 999 } })).ok, false);
  }
});

test('selected v3 findings produce only their edited comment and inline content with a separate review decision', async () => {
  const send = preview(() => { throw new Error('Selected finding submissions are simulated only'); });
  const runId = 'demo-v3-pr-p2-p3';
  const run = (await send('tasks.get', { runId })).data;
  const [inline, ordinary] = run.result.findings;
  const code = run.result.review.suggestions.find(row => row.id === inline.feedback.suggestionId);
  const edited = { ...code, body: 'Edited selected inline explanation.  \n', replacement: '    // Edited sample replacement\n' };
  const body = ordinary.feedback.body;
  const payload = { runId, kind: 'comment', findingIds: [inline.id, ordinary.id], body, suggestions: [edited], expectedAccount: 'pulse-demo', expectedHeadSha: run.task.expectedHeadSha };
  const mixed = (await send('operations.submit', payload)).data;
  assert.equal(mixed.kind, 'comment'); assert.equal(mixed.body, body); assert.deepEqual(mixed.suggestions, [edited]); assert.deepEqual(mixed.findingIds, [inline.id, ordinary.id]);
  assert.equal(mixed.body.includes(inline.feedback.body), false, 'unrequested boilerplate is not appended to the editable general comment');
  const onlyOrdinary = (await send('operations.submit', { ...payload, findingIds: [ordinary.id], suggestions: [] })).data;
  assert.deepEqual(onlyOrdinary.suggestions, []); assert.equal(onlyOrdinary.body, ordinary.feedback.body);
  const onlyInline = (await send('operations.submit', { ...payload, findingIds: [inline.id], body: '' })).data;
  assert.equal(onlyInline.kind, 'comment'); assert.equal(onlyInline.body, ''); assert.equal(onlyInline.suggestions.length, 1);
  const explicitDecision = (await send('operations.submit', { ...payload, kind: 'requestChanges', findingIds: [inline.id], body: '' })).data;
  assert.equal(explicitDecision.kind, 'requestChanges');
  assert.equal((await send('operations.submit', { ...payload, findingIds: [ordinary.id] })).error.code, 'INVALID_SUGGESTION');
  assert.equal((await send('operations.submit', { ...payload, findingIds: [inline.id, inline.id] })).ok, false);
  assert.equal((await send('operations.submit', { ...payload, suggestions: [{ ...edited, line: 999 }] })).error.code, 'INVALID_SUGGESTION');
  assert.equal((await send('operations.submit', { runId, kind: 'close', expectedAccount: 'pulse-demo', body: '' })).error.code, 'CLOSE_REASON_REQUIRED');
  const closed = (await send('operations.submit', { runId, kind: 'close', expectedAccount: 'pulse-demo', body: '', closeReason: 'Sample reviewer chose to close this superseded PR.' })).data;
  assert.equal(closed.body, ''); assert.ok(closed.closeReason);
});

test('duplicate closure freezes its original issue and edited association and resumes only the remaining step', async () => {
  const send = preview(() => { throw new Error('Duplicate workflow samples must not contact GitHub'); });
  const parent = (await send('tasks.get', { runId: 'demo-v3-bug-duplicate' })).data;
  const proposal = parent.result.nextActions.find(row => row.kind === 'close-as-duplicate');
  const prepared = (await send('resultActions.prepare', { runId: parent.runId, proposalId: proposal.proposalId })).data;
  const before = (await send('webActions.preview', { operationId: prepared.operationId })).data;
  assert.equal(before.bodyEditable, true); assert.deepEqual(before.draft.duplicateOf, proposal.duplicateOf);
  const body = '    Edited sample duplicate explanation.  \n\n';
  const payload = { operationId: prepared.operationId, expectedAccount: before.account, body };
  assert.equal((await send('webActions.submit', { ...payload, duplicateOf: { ...proposal.duplicateOf, number: 999 } })).ok, false);
  const submitted = (await send('webActions.submit', payload)).data;
  assert.deepEqual(submitted.completedSteps, ['duplicate-comment', 'close-issue']);
  const after = (await send('webActions.preview', { operationId: prepared.operationId })).data;
  assert.equal(after.draft.body, body); assert.equal(after.bodyEditable, false); assert.deepEqual(after.draft.duplicateOf, proposal.duplicateOf);
  assert.equal(after.commentBody, body + after.duplicateSuffix); assert.match(after.duplicateSuffix, /Duplicate of https:\/\/github.com\/example\/pulse-workflow-samples\/issues\/123/);
  assert.deepEqual((await send('webActions.submit', payload)).data, submitted);
  assert.equal((await send('webActions.submit', { ...payload, body: 'Changed after confirmation.' })).error.code, 'REQUEST_CONFLICT');
  const partialId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
  const partial = (await send('webActions.preview', { operationId: partialId })).data;
  assert.equal(partial.resumeRequired, true); assert.equal(partial.bodyEditable, false); assert.deepEqual(partial.completedSteps, ['duplicate-comment']);
  assert.equal((await send('webActions.submit', { operationId: partialId, expectedAccount: partial.account })).data.status, 'partial');
  const resumed = (await send('webActions.submit', { operationId: partialId, expectedAccount: partial.account, resume: true })).data;
  assert.equal(resumed.status, 'succeeded'); assert.deepEqual(resumed.completedSteps, ['duplicate-comment', 'close-issue']);
  const unknownId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
  assert.equal((await send('webActions.submit', { operationId: unknownId, expectedAccount: 'pulse-demo' })).data.status, 'unknown');
  assert.equal((await send('webActions.reconcile', { operationId: unknownId })).data.status, 'partial');
  assert.equal((await send('webActions.submit', { operationId: unknownId, expectedAccount: 'pulse-demo', resume: true })).data.status, 'succeeded');
});

test('v3 required E2E needs every recorded scenario and preserves its original necessity level after evidence is complete', async () => {
  const storage = new Map();
  const noHost = () => { throw new Error('E2E completion fixtures must not execute real scenarios'); };
  const send = preview(noHost, storage);
  const runId = 'demo-v3-pr-required';
  const parent = (await send('tasks.get', { runId })).data;
  const related = (await send('reviews.related', { runId })).data;
  assert.equal(related.recommendation.level, 'required'); assert.deepEqual(related.recommendation.expectedResults, parent.result.e2eAssessment.expectedResults);
  const child = (await send('reviews.verify', { parentRunId: runId, recommendationId: related.recommendation.recommendationId, requestId: 'v3-supplemental', prerequisitesConfirmed: true })).data;
  const base = JSON.parse(storage.get('pulse-ui-preview-v17'));
  const recorded = base.runs.find(run => run.runId === child.runId);
  recorded.status.state = 'succeeded'; recorded.status.exitCode = 0; recorded.status.endedAt = recorded.status.updatedAt;
  recorded.provenance = structuredClone(parent.provenance);
  recorded.result = { ...structuredClone(parent.result), outcome: 'completed', summary: 'Sample: the supplemental scenario observations are retained.', assessment: { subject: 'original-pr', status: 'passed', summary: 'Sample specific runtime observations passed.', revisionSha: parent.task.expectedHeadSha }, review: null, reviewConclusion: null, e2eAssessment: null, findings: [], validation: [{ id: 'setup', name: 'Setup', required: true, status: 'passed', details: 'Sample setup completed.', evidence: ['Sample driver available.'] }, { id: 'e2e', name: 'E2E procedure', required: true, status: 'passed', details: 'Sample procedure executed.', evidence: ['Sample generic execution completed.'] }], verificationEvidence: [{ id: 'runtime-evidence', source: 'current-run', kind: 'runtime', status: 'passed', subject: 'original-pr', revisionSha: parent.task.expectedHeadSha, summary: 'Sample runtime evidence.', evidence: ['Sample actual observation.'], runId: null }], cliExitCode: 0 };
  const prefix = child.task.context.reviewVerification.scenarioIdPrefix;
  const scenarios = parent.result.e2eAssessment.scenarios.map((name, index) => ({ id: `${prefix}${index + 1}`, name, required: false, status: 'passed', details: parent.result.e2eAssessment.expectedResults[index], evidence: ['Sample expected behavior observed at the saved original SHA.'] }));
  for (const count of [0, 1, scenarios.length]) {
    const snapshot = structuredClone(base); snapshot.runs.find(run => run.runId === child.runId).result.validation.push(...scenarios.slice(0, count));
    const current = preview(noHost, new Map([['pulse-ui-preview-v17', JSON.stringify(snapshot)]]));
    const evidence = (await current('reviews.related', { runId })).data;
    assert.equal(evidence.currentConclusion.evidenceComplete, count === scenarios.length);
    const view = (await current('tasks.get', { runId })).data;
    assert.equal(view.result.e2eAssessment.level, 'required'); assert.equal(view.result.recommendation.kind, count === scenarios.length ? 'approve' : 'run-e2e');
    assert.equal(view.result.summary, parent.result.summary); assert.deepEqual(view.result.report, parent.result.report);
  }
  const failed = structuredClone(base); failed.runs.find(run => run.runId === child.runId).result.validation.push(...scenarios.map((row, index) => index ? { ...row, status: 'failed' } : row));
  const mixed = (await preview(noHost, new Map([['pulse-ui-preview-v17', JSON.stringify(failed)]]))('reviews.related', { runId })).data;
  assert.equal(mixed.currentConclusion.evidenceComplete, false); assert.equal(mixed.currentConclusion.status, 'verification-incomplete');
  const noNeed = (await send('reviews.related', { runId: 'demo-v3-pr-not-needed' })).data;
  assert.equal((await send('reviews.verify', { parentRunId: noNeed.parentRunId, recommendationId: noNeed.recommendation.recommendationId, requestId: 'not-needed-scenarios' })).error.code, 'VERIFICATION_NOT_RECOMMENDED');
});
