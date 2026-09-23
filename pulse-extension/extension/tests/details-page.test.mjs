import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { setImmediate as nextTurn } from 'node:timers/promises';
import { resultDraftScope, writeDraft } from '../src/draft-store.ts';

class Node {
  children = []; attributes = new Map(); listeners = new Map(); dataset = {}; hidden = false; className = ''; _text = ''; value = ''; checked = false;
  constructor(tag = 'div') { this.tagName = tag.toUpperCase(); }
  set textContent(value) { this._text = String(value); this.children = []; }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  append(...children) { for (const child of children) { child.remove(); child.parentNode = this; this.children.push(child); } }
  prepend(...children) { for (const child of [...children].reverse()) { child.remove(); child.parentNode = this; this.children.unshift(child); } }
  replaceChildren(...children) { for (const child of this.children) child.parentNode = undefined; this._text = ''; this.children = []; this.append(...children); }
  setAttribute(key, value) { this.attributes.set(key, value); }
  removeAttribute(key) { this.attributes.delete(key); }
  addEventListener(type, callback) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), callback]); }
  get childElementCount() { return this.children.length; }
  get parentElement() { return this.parentNode; }
  focus() { this.focused = true; }
  scrollIntoView() { this.scrolled = true; }
  showModal() { this.open = true; }
  close() { this.open = false; for (const listener of this.listeners.get('close') ?? []) listener(); }
  remove() { if (this.parentNode) this.parentNode.children = this.parentNode.children.filter(node => node !== this); this.parentNode = undefined; }
}
const descendants = node => [node, ...node.children.flatMap(descendants)];
const control = (root, text) => descendants(root).find(node => ['BUTTON', 'A'].includes(node.tagName) && node.textContent === text);
const proposal = (root, id) => descendants(root).find(node => node.className === 'details-next-action' && node.dataset.proposalId === id);
const targetUrl = task => `https://github.com/${task.task.repository}/${task.task.target.type === 'pr' ? 'pull' : 'issues'}/${task.task.target.number}`;
const noPublish = page => assert.equal(page.calls.some(call => call.type === 'operations.submit' || call.type.startsWith('webActions.')), false);
const clean = page => assert.equal(page.nodes.get('error').hidden, true, page.nodes.get('error').textContent);
const sha = 'a'.repeat(40);
const fixedPrKinds = ['comment', 'approve', 'suggestChanges', 'requestChanges', 'close'];
let imports = 0;
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
const settle = async () => { await nextTurn(); await nextTurn(); };

function run(runId, state = 'succeeded') {
  return {
    runId,
    task: { actionKind: 'issue-fix', actionId: 'Investigate the shortcut conflict', repository: 'microsoft/PowerToys', target: { type: 'issue', number: 123 }, prompt: 'Inspect the shortcut conflict.' },
    config: { agent: 'codex', model: null, cliPath: 'codex.exe', repoFolder: `C:\\Tasks\\${runId}`, permission: 'read-only' },
    status: { state, createdAt: '2026-09-10T01:00:00Z', startedAt: '2026-09-10T01:00:05Z', endedAt: '2026-09-10T01:00:27Z', updatedAt: '2026-09-10T02:12:00Z', sequence: 0, exitCode: 0 },
    view: { read: true, handled: false },
  };
}
const result = overrides => ({ summary: '', structured: true, artifacts: [], validation: [], blockers: [], nextSteps: [], ...overrides });
const v2 = overrides => result({ schemaVersion: 2, outcome: 'completed', phase: 'reporting', findings: [], diagnostics: [], nextActions: [], review: null, needsReview: false, cliExitCode: 0, ...overrides });
const v3Finding = (id, overrides = {}) => ({ id, title: `Finding ${id}`, priority: 'P2', status: 'open', confirmed: true, path: 'src/Shortcuts.cs', line: 17,
  details: `Description ${id}`, impact: `Impact ${id}`, trigger: `Trigger ${id}`, rootCause: `Root cause ${id}`, fixSuggestion: `Fix ${id}`, evidence: [`Evidence ${id}`],
  feedback: { body: `Feedback ${id}`, suggestionId: null }, ...overrides });
function v3Run(runId, overrides = {}) {
  const task = proposalRun(runId); task.task.reviewOptions = { mode: 'static' };
  task.result = v2({ schemaVersion: 3, report: { complete: true, rechecked: true, coverage: ['All changed code paths were rechecked.'], limitations: [] },
    summary: 'The final review is complete.', assessment: { subject: 'original-pr', status: 'passed', summary: 'Original revision reviewed.', revisionSha: sha },
    reviewConclusion: { status: 'no-blocking-findings', summary: 'Code review complete.', revisionSha: sha, blockingUncertainties: [] },
    e2eAssessment: { level: 'not_needed', reason: 'Unit coverage addresses this change.', question: '', scenarios: [], expectedResults: [], prerequisites: [], evidence: ['Unit coverage was inspected.'], readiness: 'ready' },
    featureAssessment: null, bugAssessment: null, plans: [], verificationEvidence: [], review: { headSha: sha, body: '', suggestions: [] }, ...overrides,
  });
  return task;
}

function proposalRun(runId, targetType = 'pr') {
  const task = run(runId);
  if (targetType === 'pr') { task.task.actionKind = 'pr-review'; task.task.target = { type: 'pr', number: 456 }; task.task.expectedHeadSha = sha; }
  const checks = targetType === 'pr' ? ['context', 'local-review', 'verification'] : ['reproduction', 'implementation', 'verification'];
  task.result = v2({ summary: 'Verified local work with proposals for review.',
    validation: checks.map(id => ({ id, name: id, required: true, status: 'passed', details: 'Completed locally.', evidence: [`Evidence for ${id}.`] })),
    review: targetType === 'pr' ? { headSha: sha, body: '', suggestions: [] } : null,
  });
  return task;
}

function limitedReviewRun(runId) {
  const task = proposalRun(runId);
  task.result.outcome = 'blocked';
  task.result.assessment = { subject: 'original-pr', status: 'inconclusive', summary: 'Runtime acceptance is unverified.', revisionSha: sha };
  Object.assign(task.result.validation.find(item => item.id === 'verification'), { status: 'not_run', details: 'Acceptance environment was unavailable.' });
  task.reviewSummary = {
    codeReview: 'completed', verification: 'limited', headSha: sha, assessmentStatus: 'inconclusive',
    limitationCount: 1, observationCount: 1, canRequestEvidence: true, canApproveWithLimitations: true, reasons: [],
    limitations: [
      { id: 'assessment', name: 'Original PR assessment', status: 'inconclusive', category: 'assessment', details: 'The startup fix is not yet confirmed.', evidence: [] },
      { id: 'acceptance', name: 'Startup acceptance', status: 'not_run', category: 'validation-gap', details: 'CJK startup matrix was not run.', evidence: ['The available environment used English resources.'] },
      { id: 'initial-build', name: 'Initial local build', status: 'failed', category: 'recorded-observation', details: 'An initial build failed; the final build passed.', evidence: ['The final build exit code was zero.'] },
    ],
  };
  task.result.nextActions = [
    { proposalId: 'normal-approve', kind: 'approve', reason: 'Model-proposed approval', body: 'Ordinary approval.', availability: { enabled: false, reasons: ['The original PR assessment remains inconclusive.'] } },
    { kind: 'configure', reason: 'Inspect local settings', body: '' }, { kind: 'rerun', reason: 'Repeat the workflow', body: '' }, { kind: 'inspectResult', reason: 'Inspect logs', body: '' },
  ];
  return task;
}

function reviewDecisionPreviews(task) {
  return [
    { id: 'review-request-evidence', kind: 'request-evidence', title: 'Request missing evidence', description: 'Ask the author to supply the unverified runtime evidence.', body: 'Please provide the CJK startup acceptance results.', disclosure: '', limitations: task.reviewSummary.limitations.filter(item => item.category !== 'recorded-observation'), enabled: true, reasons: [], requiresAcknowledgement: false },
    { id: 'review-approve-with-limitations', kind: 'approve-with-limitations', title: 'Approve with validation limits', description: 'Approve the code review while explicitly retaining the listed validation limits.', body: 'I reviewed the code changes.', disclosure: 'Validation limits: CJK startup matrix was not run. Runtime acceptance remains unverified.', limitations: task.reviewSummary.limitations, enabled: true, reasons: [], requiresAcknowledgement: true },
  ];
}

async function withPage(task, verify, { rerun, prepareAction, submitOperation, resultActions = [], readPage, readPreview, initialize, reconcile } = {}) {
  const html = await readFile(new URL('../public/details.html', import.meta.url), 'utf8');
  const nodes = new Map([...html.matchAll(/<([a-z][\w-]*)\b([^>]*\bid="([^"]+)"[^>]*)>/gi)].map(([, tag, attributes, id]) => {
    const node = new Node(tag); node.hidden = /\bhidden\b/.test(attributes); node.checked = /\bchecked\b/.test(attributes);
    node.id = id; node.className = /\bclass="([^"]*)"/.exec(attributes)?.[1] ?? ''; return [id, node];
  }));
  nodes.get('log-stream').value = 'activity'; nodes.get('operation-kind').value = 'comment';
  const body = new Node('body'); const main = new Node('main'); const header = new Node('header');
  // Keep the production nesting so disclosure navigation and dynamically inserted dialogs are exercised.
  const stack = []; const voidTags = new Set(['meta', 'link', 'input', 'br', 'hr', 'img']);
  for (const match of html.matchAll(/<\/?([a-z][\w-]*)\b([^>]*)>/gi)) {
    const [, tag, attributes] = match; const closing = match[0].startsWith('</');
    if (closing) { const index = stack.findLastIndex(node => node.tagName === tag.toUpperCase()); if (index >= 0) stack.length = index; continue; }
    const id = /\bid="([^"]*)"/.exec(attributes)?.[1];
    const node = id ? nodes.get(id) : tag === 'body' ? body : tag === 'main' ? main : tag === 'header' ? header : new Node(tag);
    if (stack.length) stack.at(-1).append(node);
    if (!voidTags.has(tag) && !match[0].endsWith('/>')) stack.push(node);
  }
  const optionsButton = new Node('button'); optionsButton.textContent = 'Settings';
  const globals = ['document', 'location', 'chrome', 'setInterval', 'setTimeout', 'clearTimeout', 'localStorage', 'sessionStorage', 'addEventListener'];
  const originals = new Map(globals.map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  const windowEvents = new Map(); const documentEvents = new Map(); const timers = new Map();
  let timerTime = 0; let timerId = 0;
  let poll;
  const storage = new Map(); const session = new Map();
  const page = {
    nodes, body, calls: [], tabs: [], operations: [], resultActions, settingsOpens: 0, optionsButton, storage, previewReads: 0,
    preview: {
      account: 'test-reviewer', target: { ...task.task.target, repository: task.task.repository, title: 'Shortcut conflict', url: targetUrl(task), state: 'OPEN' },
      url: targetUrl(task), state: 'OPEN', expectedHeadSha: task.task.expectedHeadSha, headSha: task.task.expectedHeadSha,
      stale: false, canComment: true, canClose: true, canApprove: true, canRequestChanges: true, canSuggestChanges: true, reasons: [], files: [],
    },
    async refresh() { poll(); await settle(); },
    pendingTimeouts() { return timers.size; },
    async flushTimers(milliseconds = 100) {
      const end = timerTime + milliseconds; let executed = 0;
      while (true) {
        const due = [...timers].filter(([, timer]) => timer.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) break;
        assert.ok(++executed <= 100, 'controlled timers must not spin indefinitely');
        timerTime = due[1].at; timers.delete(due[0]); due[1].callback(); await settle();
      }
      timerTime = end; await settle();
    },
    async focus() { for (const listener of windowEvents.get('focus') ?? []) listener(); await settle(); },
    async visibility(value) { globalThis.document.visibilityState = value; for (const listener of documentEvents.get('visibilitychange') ?? []) listener(); await settle(); },
    async click(node) {
      assert.ok(node?.listeners.has('click'), 'the displayed control has a real click handler'); assert.notEqual(node.disabled, true, 'the control is enabled');
      for (const listener of node.listeners.get('click')) listener(); await nextTurn();
    },
    async change(id, value) {
      const node = nodes.get(id); node.value = value;
      for (const listener of node.listeners.get('change') ?? []) listener(); await nextTurn();
    },
    async fire(node, event) { for (const listener of node.listeners.get(event) ?? []) listener({ preventDefault() {} }); await nextTurn(); },
    async openManual(kind = 'comment') {
      const labels = { comment: 'Write an independent comment', approve: 'Approve pull request', requestChanges: 'Request changes', suggestChanges: 'Suggest changes', close: 'Close issue' };
      const menu = nodes.get('more-actions-list');
      const choices = descendants(menu).filter(node => node.tagName === 'BUTTON');
      const action = kind === 'comment' ? control(menu, labels.comment) : choices.find(node => new RegExp(kind === 'approve' ? '^Approve' : kind === 'close' ? '^Close' : kind === 'requestChanges' ? '^Request changes' : '^Suggest').test(node.textContent));
      await page.click(action); assert.equal(nodes.get('github-section').hidden, false);
    },
    async review() {
      assert.ok(submitOperation, 'submission is simulated only in an explicitly enabled proposal test');
      assert.equal(nodes.get('submit-operation').disabled, false);
      for (const listener of nodes.get('operation-form').listeners.get('submit')) listener({ preventDefault() {} }); await nextTurn();
      assert.equal(nodes.get('feedback-confirmation').open, true, nodes.get('github-error').textContent);
    },
    async submit() {
      const before = page.calls.filter(call => call.type === 'operations.submit').length;
      await page.review(); assert.equal(page.calls.filter(call => call.type === 'operations.submit').length, before, 'review never publishes before the explicit send');
      await page.click(nodes.get('feedback-send')); await nextTurn();
    },
  };
  try {
    globalThis.document = {
      visibilityState: 'visible', addEventListener: (event, listener) => { documentEvents.set(event, [...(documentEvents.get(event) ?? []), listener]); },
      body, getElementById: id => nodes.get(id) ?? descendants(body).find(node => node.id === id), createElement: tag => new Node(tag), createElementNS: (namespaceURI, tag) => Object.assign(new Node(tag), { namespaceURI }),
      createTextNode: text => { const node = new Node('text'); node.textContent = text; return node; },
      querySelectorAll: selector => selector === '[data-options]' ? [optionsButton] : selector === '[data-nav-view], [data-nav-panel]' ? descendants(body).filter(node => node.dataset.navView) : [],
      querySelector: selector => selector === 'body > .topbar' ? header : selector === 'body > main' ? main : null,
    };
    globalThis.location = { href: `chrome-extension://test/details.html?runId=${encodeURIComponent(task.runId)}`, pathname: '/details.html' };
    globalThis.setInterval = callback => { poll = callback; return 1; };
    globalThis.setTimeout = (callback, delay = 0, ...args) => { const id = ++timerId; timers.set(id, { at: timerTime + delay, callback: () => callback(...args) }); return id; };
    globalThis.clearTimeout = id => { timers.delete(id); };
    globalThis.addEventListener = (event, listener) => { windowEvents.set(event, [...(windowEvents.get(event) ?? []), listener]); };
    for (const [key, values] of [['localStorage', storage], ['sessionStorage', session]]) Object.defineProperty(globalThis, key, { configurable: true, value: { getItem: name => values.get(name) ?? null, setItem: (name, value) => values.set(name, String(value)), removeItem: name => values.delete(name) } });
    globalThis.chrome = {
      tabs: { create: async tab => { assert.ok(rerun || prepareAction, 'only retry or proposal scenarios may open a simulated tab'); page.tabs.push(structuredClone(tab)); return {}; } },
      runtime: {
        getURL: path => `chrome-extension://test/${path}`, openOptionsPage: async () => { page.settingsOpens++; },
        sendMessage: async message => {
          page.calls.push(structuredClone(message)); assert.equal(message.channel, 'pulse-ui');
          switch (message.type) {
            case 'connection.status': return { ok: true, data: { state: 'connected' } };
            case 'config.get': return { ok: true, data: { agent: 'codex', agentDefaults: { codex: { model: 'current-model', reasoningEffort: 'high' }, copilot: { model: '', reasoningEffort: '' } }, cliSelections: { codex: 'C:\\Tools\\codex.exe', copilot: 'C:\\Tools\\copilot.exe' } } };
            case 'agents.defaults': return { ok: true, data: { defaultAgent: 'codex', defaults: { codex: { model: 'current-model', reasoningEffort: 'high' }, copilot: { model: '', reasoningEffort: '' } }, reasoningEfforts: { codex: ['low', 'medium', 'high'], copilot: ['low', 'medium', 'high'] } } };
            case 'reviews.related': return { ok: true, data: { parentRunId: task.task.followUp?.parentRunId ?? task.runId, recommendation: null, runs: [], evidence: [], currentConclusion: { status: 'unchanged', summary: 'No compatible supplemental verification has changed this review.' }, errors: [] } };
            case 'tasks.get': assert.equal(message.payload.runId, task.runId); return { ok: true, data: structuredClone(task) };
            case 'tasks.resultPage': assert.ok(readPage, 'paged reports are explicitly mocked'); return { ok: true, data: await readPage(message.payload) };
            case 'tasks.events': return { ok: true, data: { events: [], nextSequence: 0, truncated: false } };
            case 'tasks.logs': {
              const text = 'Captured stderr: worker process stopped.';
              return { ok: true, data: { stream: message.payload.stream, text: message.payload.cursor ? '' : text, nextCursor: new TextEncoder().encode(text).length, eof: true, truncated: false } };
            }
            case 'operations.list': return { ok: true, data: { operations: structuredClone(page.operations) } };
            case 'resultActions.list': assert.deepEqual(message.payload, { runId: task.runId }); return { ok: true, data: { operations: structuredClone(page.resultActions) } };
            case 'operations.preview': {
              assert.deepEqual(message.payload, { runId: task.runId }); page.previewReads++;
              return { ok: true, data: structuredClone(readPreview ? await readPreview(page, page.previewReads) : page.preview) };
            }
            case 'operations.submit': {
              assert.ok(submitOperation, 'GitHub submission is mocked only for explicit form-submit assertions');
              const operation = await submitOperation(message.payload); page.operations.push(structuredClone(operation)); return { ok: true, data: operation };
            }
            case 'operations.reconcile': assert.ok(reconcile, 'reconciliation is explicitly mocked'); return { ok: true, data: await reconcile(message.payload, page) };
            case 'resultActions.prepare': assert.ok(prepareAction, 'result action preparation is explicitly mocked'); return { ok: true, data: await prepareAction(message.payload) };
            // Loading a terminal record automatically marks it read. This no-op touches no Host record.
            case 'tasks.read': assert.deepEqual(message.payload, { runId: task.runId, value: true }); return { ok: true, data: {} };
            case 'tasks.rerun': assert.ok(rerun, 'rerun is mocked only in the retry scenario'); return { ok: true, data: await rerun(message.payload) };
            default: throw new Error(`Unexpected operation ${message.type}; no GitHub publication or arbitrary action is allowed in this test.`);
          }
        },
      },
    };
    await initialize?.(page);
    await import(new URL(`../src/details.ts?details-page-test=${++imports}`, import.meta.url)); await settle(); clean(page); await verify(page);
  } finally {
    timers.clear();
    for (const key of globals) {
      const descriptor = originals.get(key); if (descriptor) Object.defineProperty(globalThis, key, descriptor); else delete globalThis[key];
    }
  }
}

test('v1 history keeps one failure, exact recorded times, raw diagnostics and compatible verified follow-up', async () => {
  const task = run('historical-failure', 'failed'); const failure = 'The task worker lost its process handle.';
  task.status.exitCode = null; task.status.error = { message: failure, code: null, guidance: null };
  task.result = result({ summary: failure, rawOutput: '', structured: false, needsReview: true, blockers: [null, failure], nextSteps: [{ kind: 'inspectResult', reason: 'Check existing artifacts and incomplete validation before deciding whether to start a new run.', body: '' }] });
  await withPage(task, async page => {
    const { nodes } = page;
    assert.equal(nodes.get('run-status').textContent, 'Failed'); assert.equal(nodes.get('run-error').textContent, failure);
    const ended = new Date(task.status.endedAt).toLocaleString('en-GB', { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
    assert.equal(nodes.get('run-ended').textContent, `Ended ${ended}`); assert.match(nodes.get('run-times').textContent, /Duration22s/);
    const facts = Object.fromEntries(nodes.get('execution-facts').children.map(fact => [fact.children[0].textContent, fact.children[1].textContent]));
    assert.deepEqual(facts, { CLI: 'Codex CLI', Model: 'Not recorded', 'Reasoning effort': 'Not recorded' });
    const text = ['run-error', 'run-ended', 'run-times', 'run-facts', 'execution-facts', 'result', 'diagnostics'].map(id => nodes.get(id).textContent).join('\n');
    assert.doesNotMatch(text, /\b(?:null|undefined)\b/); assert.equal(text.split(failure).length - 1, 1); assert.doesNotMatch(nodes.get('run-facts').textContent, /Error code|Exit code/);
    assert.equal(nodes.get('result-section').hidden, true); assert.equal(nodes.get('github-section').hidden, true); assert.ok(control(nodes.get('more-actions-list'), 'Write an independent comment')); assert.equal(control(nodes.get('run-controls'), 'Open on GitHub').href, targetUrl(task));
    task.result.rawOutput = '  I inspected the shortcut registration.\nThe original agent response remains available.\n';
    await page.refresh(); clean(page); assert.equal(nodes.get('result-section').hidden, true); assert.equal(nodes.get('diagnostic-section').hidden, false);
    const response = descendants(nodes.get('diagnostics')).find(node => node.className === 'details-agent-response');
    assert.equal(response.open, false); assert.equal(response.children[0].textContent, 'Agent response'); assert.equal(response.children[1].tagName, 'PRE'); assert.equal(response.children[1].textContent, task.result.rawOutput);
    for (const [id, event] of [['prepare', 'click'], ['operation-kind', 'change'], ['operation-form', 'submit'], ['log-stream', 'change'], ['delete-run', 'click']]) assert.ok(nodes.get(id)?.listeners.has(event));
    for (const id of ['github-error', 'operation-editor', 'operation-target', 'operation-reasons', 'operation-body', 'suggestions', 'operation-explanation', 'submit-operation', 'operations', 'execution-logs', 'log-source', 'log-notice', 'log', 'follow-log']) assert.ok(nodes.has(id));
    await page.openManual(); assert.equal(nodes.get('github-error').hidden, true); assert.equal(page.calls.some(call => call.type === 'operations.preview'), true);
    assert.equal(nodes.get('operation-editor').hidden, false, 'failed investigation does not remove fixed manual operations');
    task.status.state = 'succeeded'; task.status.exitCode = 0; delete task.status.error;
    task.result = result({ summary: 'Analysis is blocked.', validation: [{ name: 'Access', status: 'failed' }], blockers: ['No repository access'], nextSteps: [{ kind: 'configure' }], needsReview: true });
    await page.refresh(); assert.equal(nodes.get('run-status').textContent, 'Needs attention'); assert.equal(nodes.get('github-section').hidden, false);
    task.result = result({ summary: 'Verified issue findings.', validation: [{ name: 'Reproduction', status: 'passed' }], nextSteps: [{ kind: 'comment', reason: 'Share verified findings', body: 'Verified reproduction steps.' }], needsReview: true });
    await page.refresh(); await page.click(nodes.get('prepare')); assert.equal(nodes.get('github-error').hidden, true, nodes.get('github-error').textContent);
    assert.equal(nodes.get('operation-editor').hidden, false); assert.equal(nodes.get('submit-operation').disabled, false); assert.deepEqual(nodes.get('operation-kind').children.map(option => option.value), ['comment', 'close']);
    nodes.get('log-stream').value = 'stderr'; nodes.get('log-stream').listeners.get('change')[0](); await nextTurn(); clean(page);
    assert.equal(nodes.get('log').textContent, 'Captured stderr: worker process stopped.'); assert.match(nodes.get('log-source').textContent, /Raw agent stderr/); noPublish(page);
  });
});

test('active v2 runs hide report panels while fixed manual operations and observed CLI metadata remain available', async () => {
  const task = run('active-v2', 'accepted'); task.config.model = 'requested-model'; task.config.reasoningEffort = 'high';
  task.result = v2({ outcome: 'blocked', phase: 'setup', summary: 'Old saved diagnostic.', diagnostics: [{ code: 'OLD_ERROR', severity: 'error', message: 'Do not display while active.', recovery: 'configure' }] });
  await withPage(task, async page => {
    for (const state of ['accepted', 'running']) {
      task.status.state = state; await page.refresh(); clean(page); assert.equal(page.nodes.get('run-status').textContent, state === 'accepted' ? 'Queued' : 'Running');
      for (const id of ['result-section', 'diagnostic-section', 'run-phase']) assert.equal(page.nodes.get(id).hidden, true);
      assert.equal(page.nodes.get('github-section').hidden, true); assert.ok(control(page.nodes.get('more-actions-list'), 'Write an independent comment'));
      assert.match(page.nodes.get('execution-facts').textContent, /ModelReading from CLI…Reasoning effortReading from CLI…/); assert.match(page.nodes.get('run-facts').textContent, /Requested modelrequested-model/);
    }
    task.status.observedExecution = { model: 'actual-model', source: 'codex-turn-context', observedAt: '2026-09-14T01:00:00Z' };
    await page.refresh(); assert.match(page.nodes.get('execution-facts').textContent, /Modelactual-modelReasoning effortReading from CLI…/);
    assert.equal(page.calls.some(call => call.type === 'tasks.read' || call.type === 'operations.preview'), false); noPublish(page);
  });
});

test('blocked v2 exit-zero tasks expose diagnostics and open an explicit retry configuration dialog', async () => {
  const task = run('blocked-v2'); const artifactPath = `${task.config.repoFolder}\\retained-notes.md`;
  task.result = v2({
    outcome: 'blocked', phase: 'setup', summary: 'The local workflow could not pass setup.',
    diagnostics: [{ code: 'TEST_DRIVER_MISSING', severity: 'error', message: 'The desktop test driver is unavailable.', recovery: 'configure' }],
    artifacts: [{ label: 'Retained reproduction notes', path: artifactPath }],
    validation: [{ id: 'reproduction', name: 'Reproduction setup', status: 'not_run', required: true, details: 'Setup dependency unavailable.', evidence: ['The desktop driver probe returned unavailable.'] }],
    nextActions: [
      { kind: 'configure', reason: 'Choose the required local installation.', body: 'must not become a command' },
      { kind: 'inspectResult', reason: 'Inspect execution output.', body: '' }, { kind: 'viewChanges', reason: 'Inspect retained evidence.', body: '' },
      { kind: 'rerun', reason: 'Try again after configuration.', body: 'must not become a task payload' },
      { kind: 'openTarget', reason: 'Inspect the original target.', body: 'https://example.invalid/alternate', url: 'https://example.invalid/alternate', target: { repository: 'other/repo', number: 999 } },
      { kind: 'comment', reason: 'Must not publish a blocked result.', body: 'Not eligible.' }, { kind: 'execute-shell', reason: 'UNSUPPORTED ACTION', body: 'arbitrary executable text' },
    ],
  });
  const original = structuredClone(task);
  await withPage(task, async page => {
    const { nodes } = page; const panel = nodes.get('diagnostics');
    assert.equal(nodes.get('run-status').textContent, 'Blocked'); assert.equal(nodes.get('run-phase').textContent, 'Phase · Setup');
    assert.equal(nodes.get('run-error').hidden, true); assert.equal(nodes.get('result-section').hidden, true); assert.equal(nodes.get('diagnostic-section').hidden, false); assert.match(nodes.get('run-facts').textContent, /Exit code0/);
    const diagnostic = descendants(panel).find(node => node.className === 'details-diagnostic error');
    assert.match(diagnostic.textContent, /Action needed.*TEST_DRIVER_MISSING/); assert.match(panel.textContent, /Reproduction setup · Not run · Required/);
    assert.match(panel.textContent, /The desktop driver probe returned unavailable/); assert.ok(panel.textContent.includes(artifactPath));
    assert.equal(control(panel, 'View execution logs').href, '#execution-logs'); assert.equal(control(panel, 'Inspect retained changes').href, '#retained-artifacts'); assert.ok(descendants(panel).some(node => node.id === 'retained-artifacts'));
    assert.equal(control(panel, 'Open target on GitHub').href, targetUrl(task)); assert.equal(control(nodes.get('run-controls'), 'Open on GitHub').href, targetUrl(task));
    assert.equal(nodes.get('submit-operation').disabled, true); assert.doesNotMatch(panel.textContent, /UNSUPPORTED ACTION/);
    assert.equal(control(panel, 'Review comment')?.disabled, true, 'blocked publication proposals remain visible but unavailable');
    await page.click(control(panel, 'Review missing prerequisites')); assert.equal(page.settingsOpens, 0); assert.equal(nodes.get('result-evidence').open, true); await page.click(page.optionsButton); assert.equal(page.settingsOpens, 1);
    assert.equal(page.calls.some(call => call.type === 'operations.preview'), false); noPublish(page);
    const retry = control(panel, 'Retry as a new run');
    await page.click(retry); clean(page);
    const dialog = descendants(page.body).find(node => node.tagName === 'DIALOG' && node.className.includes('rerun-dialog') && node !== nodes.get('feedback-confirmation'));
    assert.ok(dialog); assert.equal(dialog.open, true); assert.match(dialog.textContent, /Run task again/);
    const mode = descendants(dialog).find(node => node.id === 'rerun-mode'); assert.equal(mode.value, 'defaults');
    assert.equal(descendants(dialog).find(node => node.id === 'rerun-model').value, 'current-model');
    assert.equal(page.calls.some(call => call.type === 'tasks.rerun'), false, 'opening retry options never starts a task'); assert.equal(page.tabs.length, 0);
    await page.click(control(dialog, 'Cancel')); assert.equal(descendants(page.body).includes(dialog), false);
    assert.equal(page.calls.some(call => call.type === 'tasks.rerun'), false);
    assert.deepEqual(task, original, 'recovery never rewrites the old record or target'); noPublish(page);
  });
});

test('completed v2 PR review exposes findings and required evidence, then prepares only SHA-bound review actions', async () => {
  const task = run('completed-review'); task.task.actionKind = 'pr-review'; task.task.target = { type: 'pr', number: 456 }; task.task.expectedHeadSha = sha;
  task.result = v2({
    summary: 'Local review completed with one actionable finding.', needsReview: true,
    findings: [{ id: 'finding-1', title: 'Duplicate shortcut registration', severity: 'high', status: 'open', path: 'src/Shortcuts.cs', line: 17, details: 'A second registration replaces the original callback.', evidence: ['The replacement branch does not restore the previous callback.'] }],
    validation: [['context', 'Task context'], ['local-review', 'Local review'], ['verification', 'Verification']].map(([id, name]) => ({ id, name, required: true, status: 'passed', details: `${name} completed locally.`, evidence: [`Evidence for ${id}.`] })),
    review: { headSha: sha, body: 'Please fix the duplicate shortcut registration.', suggestions: [] },
    nextActions: [
      { kind: 'comment', reason: 'Share the verified analysis.', body: 'Verified review comment.' },
      { kind: 'requestChanges', reason: 'Review the proposed change request.', body: 'Fix the duplicate registration before merging.' },
      { kind: 'openTarget', reason: 'Open the original pull request.', body: 'https://example.invalid/ignored', target: { repository: 'other/repo', type: 'issue', number: 999 } },
      { kind: 'execute-shell', reason: 'UNSUPPORTED ACTION', body: 'arbitrary command' },
    ],
  });
  await withPage(task, async page => {
    const { nodes } = page; const panel = nodes.get('result');
    assert.equal(nodes.get('run-status').textContent, 'Completed · 1 issue'); assert.equal(nodes.get('run-phase').textContent, 'Phase · Reporting'); assert.equal(nodes.get('result-section').hidden, false); assert.equal(nodes.get('diagnostic-section').hidden, true);
    assert.match(panel.textContent, /Duplicate shortcut registrationHigh · Open.*src\/Shortcuts.cs:17/); assert.match(panel.textContent, /The replacement branch does not restore the previous callback/);
    for (const check of task.result.validation) { assert.ok(panel.textContent.includes(`${check.name} · Passed · Required`), panel.textContent); assert.ok(panel.textContent.includes(`Evidence for ${check.id}.`)); }
    assert.equal(control(panel, 'Open target on GitHub').href, targetUrl(task)); assert.doesNotMatch(panel.textContent, /UNSUPPORTED ACTION/);
    for (const [label, kind, body] of [['Review comment', 'comment', 'Verified review comment.'], ['Review change request', 'requestChanges', 'Fix the duplicate registration before merging.']]) {
      const before = page.calls.filter(call => call.type === 'operations.preview').length; await page.click(control(panel, label)); clean(page);
      assert.equal(page.calls.filter(call => call.type === 'operations.preview').length, Math.max(before, 1), 'proposal choices reuse the already loaded target instead of adding a prerequisite click');
      assert.equal(nodes.get('github-error').hidden, true, nodes.get('github-error').textContent); assert.equal(nodes.get('operation-editor').hidden, false); assert.equal(nodes.get('github-section').open, true);
      assert.equal(nodes.get('operation-kind').value, kind); assert.equal(nodes.get('operation-body').value, body); assert.deepEqual(nodes.get('operation-kind').children.map(option => option.value), fixedPrKinds);
      assert.equal(nodes.get('submit-operation').disabled, false); assert.ok(nodes.get('operation-target').textContent.includes(sha)); assert.equal(descendants(nodes.get('operation-target')).find(node => node.tagName === 'A').href, targetUrl(task)); noPublish(page);
    }
    page.preview.headSha = 'b'.repeat(40); await page.click(nodes.get('prepare'));
    assert.equal(nodes.get('operation-editor').hidden, false, 'changed HEAD preserves the draft for inspection');
    assert.equal(nodes.get('github-section').hidden, false, 'changed HEAD keeps the target and revision explanation visible');
    assert.equal(nodes.get('submit-operation').disabled, true, 'changed HEAD invalidates the previously enabled submit button'); noPublish(page);
    task.result.review.headSha = 'c'.repeat(40); await page.refresh(); clean(page);
    assert.equal(nodes.get('github-section').hidden, false); assert.equal(control(nodes.get('result'), 'Review comment')?.disabled, true); assert.equal(control(nodes.get('result'), 'Review change request')?.disabled, true);
    assert.equal(control(nodes.get('result'), 'Open target on GitHub').href, targetUrl(task));
    page.preview.headSha = sha; await page.click(nodes.get('prepare')); await page.change('operation-kind', 'approve');
    assert.equal(nodes.get('submit-operation').disabled, false, 'an old AI draft SHA does not gate a manually chosen action against the live task SHA'); noPublish(page);
  });
});

test('an incomplete required acceptance keeps its saved outcome while the generic check summary is expandable once', async () => {
  const task = proposalRun('required-review-acceptance');
  task.result.outcome = 'blocked'; task.result.phase = 'validation';
  task.result.diagnostics = [
    { code: 'CJK_RUNTIME_VALIDATION_INCOMPLETE', severity: 'error', message: 'Required startup acceptance could not run in this environment.', recovery: 'configure' },
    { code: 'WORKFLOW_CHECKS_INCOMPLETE', severity: 'error', message: 'One or more required workflow checks were not completed.', recovery: 'inspectResult' },
  ];
  Object.assign(task.result.validation.find(item => item.id === 'verification'), { status: 'not_run', evidence: ['Required CJK acceptance environment unavailable.'] });
  const original = structuredClone(task);
  await withPage(task, async page => {
    const panel = page.nodes.get('diagnostics');
    assert.equal(page.nodes.get('run-status').textContent, 'Blocked');
    assert.equal(page.nodes.get('result-section').hidden, true); assert.equal(page.nodes.get('diagnostic-section').hidden, false);
    const cards = descendants(panel).filter(node => node.className === 'details-diagnostic error');
    assert.equal(cards.length, 1, 'the generic summary is not a second red alert');
    const detail = cards[0].children.find(node => node.tagName === 'DETAILS');
    assert.ok(detail); assert.ok(!detail.open, 'secondary metadata starts collapsed');
    assert.match(detail.textContent, /Diagnostic details.*CJK_RUNTIME_VALIDATION_INCOMPLETE.*WORKFLOW_CHECKS_INCOMPLETE.*One or more required workflow checks were not completed/);
    assert.match(panel.textContent, /Required CJK acceptance environment unavailable/);
    assert.ok(control(panel, 'Review missing prerequisites')); assert.ok(control(panel, 'View execution logs')); assert.equal(page.settingsOpens, 0);
    assert.deepEqual(task, original); noPublish(page);
  });
});

test('a completed code review shows its runtime coverage limit without implying PR approval or task failure', async () => {
  const task = proposalRun('review-limited-coverage');
  task.result.assessment = { subject: 'original-pr', status: 'inconclusive', summary: 'The reported startup fix remains unverified.', revisionSha: sha };
  task.result.needsReview = true;
  task.result.diagnostics = [{ code: 'REVIEW_COVERAGE_LIMITED', severity: 'warning', message: 'Code review completed; CJK runtime acceptance was not included.', recovery: 'none' }];
  task.result.validation.push({ id: 'cjk-acceptance', name: 'CJK startup acceptance', required: false, status: 'not_run', details: 'Requires the separate runtime acceptance workflow.', evidence: [] });
  task.result.nextActions = [
    { proposalId: 'host-limited-report', kind: 'comment', reason: 'Share the review and its limits.', body: 'Review completed. Runtime acceptance remains unverified.', availability: { enabled: true, reasons: [] } },
    { proposalId: 'host-limited-approval', kind: 'approve', reason: 'Approve the original PR', body: 'Proposed approval.', availability: { enabled: false, reasons: ['The original PR assessment remains inconclusive.'] } },
    { proposalId: 'host-limited-merge', kind: 'merge-pr', reason: 'Merge the original PR', body: '', availability: { enabled: false, reasons: ['The original PR assessment remains inconclusive.'] } },
  ];
  const original = structuredClone(task);
  await withPage(task, async page => {
    const { nodes } = page; const panel = nodes.get('result');
    assert.equal(nodes.get('result-title').textContent, 'Review findings');
    assert.equal(nodes.get('run-status').textContent, 'Completed · Needs attention');
    assert.equal(nodes.get('result-section').hidden, false); assert.equal(nodes.get('diagnostic-section').hidden, true);
    assert.equal(descendants(panel).filter(node => node.className === 'details-diagnostic error').length, 0);
    assert.match(panel.textContent, /Original pull request · Inconclusive/);
    assert.match(panel.textContent, /Validation limitCode review completed; CJK runtime acceptance was not included/);
    assert.match(panel.textContent, /CJK startup acceptance · Not run · Optional/);
    assert.equal(control(proposal(panel, 'host-limited-approval'), 'Review approval')?.disabled, true);
    assert.equal(control(proposal(panel, 'host-limited-merge'), 'Review merge')?.disabled, true);
    assert.equal(control(proposal(panel, 'host-limited-report'), 'Review comment')?.disabled, false);
    await page.click(control(proposal(panel, 'host-limited-report'), 'Review comment')); clean(page);
    assert.equal(nodes.get('operation-body').value, task.result.nextActions[0].body);
    assert.equal(nodes.get('submit-operation').disabled, false);
    assert.deepEqual(task, original); noPublish(page);
  });
});

test('historical limited reviews retain their outcome and unrecorded scope alongside fixed manual choices', async () => {
  const task = limitedReviewRun('historical-limited-review'); const original = structuredClone(task);
  await withPage(task, async page => {
    const panel = page.nodes.get('review-scope-section');
    assert.equal(page.nodes.get('run-status').textContent, 'Blocked');
    assert.equal(page.nodes.get('result-section').hidden, true);
    assert.equal(page.nodes.get('diagnostic-section').hidden, false);
    assert.equal(panel.hidden, false); assert.match(panel.textContent, /Selected scopeNot recorded/);
    assert.match(panel.textContent, /historical run did not record its scope/);
    assert.equal(control(panel, 'Request evidence from author'), undefined);
    assert.equal(control(panel, 'Review approval with limits'), undefined);
    assert.equal(descendants(panel).find(node => node.id === 'review-decision-submit'), undefined);
    assert.equal(control(proposal(page.nodes.get('diagnostics'), 'normal-approve'), 'Review approval').disabled, true);
    assert.equal(page.nodes.get('github-section').hidden, true); assert.ok(control(page.nodes.get('more-actions-list'), 'Write an independent comment'));
    await page.openManual('approve');
    assert.equal(page.nodes.get('submit-operation').disabled, false, 'historical assessment limits are advice, not manual approval permissions');
    noPublish(page); assert.deepEqual(task, original);
  });
});

test('legacy server review decisions cannot revive the removed approval bypass', async () => {
  const task = limitedReviewRun('no-manual-review-bypass');
  await withPage(task, async page => {
    page.preview.canApprove = false; page.preview.reviewDecisions = reviewDecisionPreviews(task);
    await page.refresh();
    const panel = page.nodes.get('review-scope-section');
    assert.equal(control(panel, 'Review approval with limits'), undefined);
    assert.equal(control(panel, 'Request evidence from author'), undefined);
    assert.equal(page.calls.some(call => call.payload?.reviewDecisionId), false);
    noPublish(page);
  });
});

test('editable proposals preserve duplicate IDs and exact Markdown through selection, refresh and confirmed submission', async () => {
  const task = proposalRun('editable-proposals');
  const firstBody = '  First proposal.\n\n```text\n  first draft\n```\n  ';
  const secondBody = '\n## Second proposal\n\n- Keep two trailing spaces.  \n\n```diff\n+second change\n```\n  ';
  const actions = [
    { proposalId: 'host-comment-first', kind: 'comment', reason: 'First comment candidate', body: firstBody },
    { proposalId: 'host-comment-second', kind: 'comment', reason: '  Second comment candidate\n', body: secondBody },
    { proposalId: 'host-approval', kind: 'approve', reason: 'Approve this reviewed revision', body: '  Approval from the proposal body.\n' },
    { proposalId: 'host-request-changes', kind: 'requestChanges', reason: 'Request the documented fix', body: '\n  Request changes from the proposal body.  \n' },
    { proposalId: 'host-suggestions', kind: 'suggestChanges', reason: 'Inspect inline suggestions', body: 'Suggested replacement draft.' },
    { proposalId: 'host-close', kind: 'close', reason: 'Close after inspection', body: '' },
  ];
  task.result.nextActions = actions;
  task.result.review.suggestions = [{ path: 'src/Shortcuts.cs', line: 17, side: 'RIGHT', body: 'Preserve the callback.', replacement: 'RegisterShortcutOnce();' }];
  const submissions = [];
  await withPage(task, async page => {
    const { nodes } = page; const panel = nodes.get('result');
    page.preview.files = [{ path: 'src/Shortcuts.cs', status: 'modified', lines: [{ line: 17, kind: 'add', original: 'RegisterShortcut();', hunk: 0 }] }];
    assert.deepEqual(descendants(panel).filter(node => node.className === 'details-next-action').map(node => node.dataset.proposalId), actions.map(action => action.proposalId));
    assert.equal(descendants(panel).filter(node => node.tagName === 'BUTTON' && node.textContent === 'Review comment').length, 2);
    const second = proposal(panel, 'host-comment-second');
    assert.equal(descendants(second).find(node => node.tagName === 'PRE').textContent, secondBody);
    await page.click(control(second, 'Review comment')); clean(page);
    assert.equal(nodes.get('operation-body').value, secondBody); assert.equal(nodes.get('selected-proposal').textContent, actions[1].reason.trim());
    await page.click(nodes.get('prepare')); await page.refresh(); clean(page);
    assert.equal(nodes.get('operation-body').value, secondBody, 'reloading capabilities never replaces the second proposal with the first');
    assert.equal(nodes.get('selected-proposal').textContent, actions[1].reason.trim()); noPublish(page);
    await page.submit(); clean(page);
    assert.equal(submissions.length, 1); assert.equal(submissions[0].body, secondBody); assert.equal(submissions[0].proposalId, 'host-comment-second');
    assert.equal(submissions[0].kind, 'comment'); assert.equal(submissions[0].expectedHeadSha, sha); assert.equal(submissions[0].expectedAccount, 'test-reviewer');
    await page.change('operation-kind', 'approve'); assert.equal(nodes.get('selected-proposal').hidden, true);
    nodes.get('operation-body').value = '  Manually edited approval.\n'; await page.submit();
    assert.equal(submissions[1].kind, 'approve'); assert.equal(submissions[1].body, '  Manually edited approval.\n'); assert.equal(Object.hasOwn(submissions[1], 'proposalId'), false);
    for (const [id, label] of [['host-approval', 'Review approval'], ['host-request-changes', 'Review change request'], ['host-suggestions', 'Review suggested changes'], ['host-close', 'Review close action']]) {
      const action = actions.find(item => item.proposalId === id); await page.click(control(proposal(panel, id), label)); clean(page);
      assert.equal(nodes.get('operation-kind').value, action.kind); assert.equal(nodes.get('operation-body').value, action.body, 'empty review.body does not discard the selected action body');
      assert.equal(nodes.get('selected-proposal').textContent, action.reason);
      if (action.kind === 'suggestChanges') {
        assert.match(nodes.get('suggestions').textContent, /Legacy proposal: its inline suggestions were not individually linked/);
        assert.equal(descendants(nodes.get('suggestions')).filter(node => node.tagName === 'INPUT').every(node => node.checked === false), true, 'legacy suggestions require an explicit selection');
      }
    }
    assert.equal(submissions.length, 2, 'opening the other four editors never submits them');
    assert.deepEqual(nodes.get('operation-kind').children.map(option => option.value), ['comment', 'approve', 'suggestChanges', 'requestChanges', 'close']);
    assert.equal(page.calls.some(call => call.type === 'resultActions.prepare' || call.type.startsWith('webActions.')), false);
  }, { submitOperation: async payload => {
    submissions.push(structuredClone(payload)); return { ...payload, status: 'succeeded', createdAt: '2026-09-14T02:00:00Z' };
  } });
});

test('legacy proposal display IDs never reach the Host and omitted task context is explained in Original task context', async () => {
  const task = proposalRun('legacy-proposal-context'); task.taskContextOmitted = true; delete task.task.prompt;
  const approveBody = '\n  Approve with the saved next-step explanation.  \n';
  const changesBody = '  ## Changes required\n\nKeep this complete Markdown draft.  \n';
  task.result = result({ summary: 'Historical verified review.', validation: [{ name: 'Local review', status: 'passed' }],
    review: { headSha: sha, body: '', suggestions: [] },
    nextSteps: [{ kind: 'approve', reason: 'Legacy approval', body: approveBody }, { kind: 'requestChanges', reason: 'Legacy change request', body: changesBody }],
  });
  const submissions = [];
  await withPage(task, async page => {
    const { nodes } = page; const panel = nodes.get('result');
    assert.equal(nodes.get('prompt').textContent, 'Full task context is retained in the local task record.');
    const html = await readFile(new URL('../public/details.html', import.meta.url), 'utf8');
    assert.match(html, /<summary>Original task context<\/summary><pre id="prompt"/);
    for (const [id, label, body] of [['local-proposal-1', 'Review approval', approveBody], ['local-proposal-2', 'Review change request', changesBody]]) {
      await page.click(control(proposal(panel, id), label)); clean(page); assert.equal(nodes.get('operation-body').value, body);
      await page.submit(); assert.equal(submissions.at(-1).body, body); assert.equal(Object.hasOwn(submissions.at(-1), 'proposalId'), false);
    }
    assert.equal(submissions.length, 2); assert.equal(page.calls.some(call => call.type === 'resultActions.prepare' || call.type.startsWith('webActions.')), false);
  }, { submitOperation: async payload => { submissions.push(structuredClone(payload)); return { ...payload, status: 'succeeded', createdAt: '2026-09-14T02:00:00Z' }; } });
});

test('switching identified review proposals keeps each body and associated suggestion draft separate', async () => {
  const task = proposalRun('independent-review-drafts');
  const first = { id: 'suggestion-first', path: 'src/Shortcuts.cs', line: 17, side: 'RIGHT', body: 'First inline comment.', replacement: 'FirstReplacement();' };
  const second = { id: 'suggestion-second', path: 'src/Shortcuts.cs', line: 23, side: 'RIGHT', body: 'Second inline comment.', replacement: 'SecondReplacement();' };
  task.result.review.suggestions = [first, second];
  task.result.nextActions = [
    { proposalId: 'host-review-first', kind: 'requestChanges', reason: 'First independent review', body: 'First review body.', suggestionIds: [first.id] },
    { proposalId: 'host-review-second', kind: 'requestChanges', reason: 'Second independent review', body: 'Second review body.', suggestionIds: [second.id] },
  ];
  const submissions = [];
  await withPage(task, async page => {
    const { nodes } = page; const panel = nodes.get('result');
    page.preview.files = [{ path: first.path, status: 'modified', lines: [first, second].map(item => ({ line: item.line, kind: 'add', original: `Original${item.line}();`, hunk: 0 })) }];
    const editors = () => descendants(nodes.get('suggestions')).filter(node => node.className === 'suggestion');
    const inputs = editor => ({ checkbox: descendants(editor).find(node => node.tagName === 'INPUT'), fields: descendants(editor).filter(node => node.tagName === 'TEXTAREA') });
    await page.click(control(proposal(panel, 'host-review-first'), 'Review change request')); clean(page);
    assert.equal(editors().length, 1, 'only the first proposal\'s associated suggestion is displayed');
    assert.match(editors()[0].textContent, /src\/Shortcuts\.cs:17/);
    const firstDraft = inputs(editors()[0]); firstDraft.checkbox.checked = true;
    firstDraft.fields[0].value = '  Edited first inline comment.  \n'; firstDraft.fields[1].value = 'EditedFirstReplacement();\n';
    nodes.get('operation-body').value = '  Edited first review body.\n';
    await page.click(control(proposal(panel, 'host-review-second'), 'Review change request')); clean(page);
    assert.equal(nodes.get('operation-body').value, 'Second review body.'); assert.equal(editors().length, 1);
    assert.match(editors()[0].textContent, /src\/Shortcuts\.cs:23/);
    const secondDraft = inputs(editors()[0]);
    assert.equal(secondDraft.checkbox.checked, false, 'selecting another proposal never inherits the previous checkbox');
    assert.equal(secondDraft.fields[0].value, second.body); assert.equal(secondDraft.fields[1].value, second.replacement);
    secondDraft.checkbox.checked = true; secondDraft.fields[0].value = '\nEdited second inline comment.  ';
    nodes.get('operation-body').value = '\n  Edited second review body.  \n';
    await page.click(control(proposal(panel, 'host-review-first'), 'Review change request')); clean(page);
    assert.equal(nodes.get('operation-body').value, '  Edited first review body.\n');
    const restoredFirst = inputs(editors()[0]); assert.equal(restoredFirst.checkbox.checked, true);
    assert.equal(restoredFirst.fields[0].value, '  Edited first inline comment.  \n'); assert.equal(restoredFirst.fields[1].value, 'EditedFirstReplacement();\n');
    await page.click(nodes.get('prepare')); await page.refresh(); clean(page);
    assert.equal(nodes.get('operation-body').value, '  Edited first review body.\n');
    await page.click(control(proposal(panel, 'host-review-second'), 'Review change request')); clean(page);
    assert.equal(nodes.get('operation-body').value, '\n  Edited second review body.  \n'); assert.equal(inputs(editors()[0]).checkbox.checked, true);
    await page.submit(); clean(page);
    assert.equal(submissions.length, 1); assert.equal(submissions[0].proposalId, 'host-review-second');
    assert.equal(submissions[0].body, '\n  Edited second review body.  \n');
    assert.deepEqual(submissions[0].suggestions, [{ ...second, body: '\nEdited second inline comment.  ' }]);
    assert.equal(page.calls.some(call => call.type === 'resultActions.prepare' || call.type.startsWith('webActions.')), false);
  }, { submitOperation: async payload => { submissions.push(structuredClone(payload)); return { ...payload, status: 'succeeded', createdAt: '2026-09-14T02:00:00Z' }; } });
});

test('a missing process exit code leaves fixed manual actions independent from unavailable AI proposals', async () => {
  const task = proposalRun('missing-confirmed-exit'); task.status.exitCode = null;
  task.result.nextActions = [
    { proposalId: 'host-unconfirmed-comment', kind: 'comment', reason: 'Share the report', body: 'The result claims success.', availability: { enabled: true, reasons: [] } },
    { proposalId: 'host-unconfirmed-merge', kind: 'merge-pr', reason: 'Merge the reviewed PR', body: '', availability: { enabled: true, reasons: [] } },
  ];
  await withPage(task, async page => {
    assert.equal(page.nodes.get('submit-operation').disabled, true);
    for (const item of descendants(page.nodes.get('result')).filter(node => node.tagName === 'BUTTON' && ['Review comment', 'Review merge'].includes(node.textContent))) assert.equal(item.disabled, true);
    await page.openManual();
    assert.equal(page.calls.some(call => call.type === 'resultActions.prepare'), false);
    assert.equal(page.nodes.get('submit-operation').disabled, false);
    assert.deepEqual(page.nodes.get('operation-kind').children.map(option => option.value), fixedPrKinds);
    await page.change('operation-kind', 'approve'); assert.equal(page.nodes.get('submit-operation').disabled, false); noPublish(page);
  });
});

test('disabled Host proposals stay visible with their reasons and a denied preview retains target and account diagnostics', async () => {
  const task = proposalRun('visible-unavailable-proposals');
  task.result.nextActions = [
    { proposalId: 'host-disabled-approval', kind: 'approve', reason: 'Approval proposed by the agent', body: 'Review completed.', availability: { enabled: false, reasons: ['An unresolved high-severity finding prevents approval.'] } },
    { proposalId: 'host-disabled-merge', kind: 'merge-pr', reason: 'Merge proposed by the agent', body: '', availability: { enabled: false, reasons: ['An unresolved high-severity finding prevents merging.'] } },
    { proposalId: 'host-comment-permissions', kind: 'comment', reason: 'Share the verified report', body: 'Report body.', availability: { enabled: true, reasons: [] } },
  ];
  await withPage(task, async page => {
    const { nodes } = page; const panel = nodes.get('result');
    for (const [id, label, message] of [
      ['host-disabled-approval', 'Review approval', 'An unresolved high-severity finding prevents approval.'],
      ['host-disabled-merge', 'Review merge', 'An unresolved high-severity finding prevents merging.'],
    ]) {
      const item = proposal(panel, id); assert.ok(item, `${id} is retained in the result`);
      assert.equal(control(item, label)?.disabled, true); assert.ok(item.textContent.includes(message));
    }
    page.preview.account = null; page.preview.canComment = false; page.preview.canApprove = false;
    page.preview.canRequestChanges = false; page.preview.canSuggestChanges = false; page.preview.canClose = false;
    page.preview.reasons = ['The selected GitHub account is not signed in.', 'The account cannot write to this repository.'];
    await page.openManual(); clean(page);
    assert.equal(nodes.get('github-section').hidden, false, 'eligibility failure does not remove the explanation panel');
    assert.equal(nodes.get('operation-editor').hidden, false); assert.equal(nodes.get('submit-operation').disabled, true);
    assert.ok(nodes.get('operation-kind').children.every(option => option.disabled), 'the retained draft has no eligible submission kind');
    assert.equal(nodes.get('operation-target').hidden, false); assert.ok(nodes.get('operation-target').textContent.includes(task.task.repository));
    assert.match(nodes.get('operation-target').textContent, /Not signed in|Unavailable/);
    assert.equal(nodes.get('operation-reasons').hidden, false);
    for (const reason of page.preview.reasons) assert.ok(nodes.get('operation-reasons').textContent.includes(reason));
    noPublish(page);
  });
});

test('result action history resumes pending confirmations and creates a new attempt only after an explicit retry', async () => {
  const task = proposalRun('result-action-attempts');
  const action = { proposalId: 'host-ci-attempts', kind: 'trigger-ci', reason: 'Run the PR CI checks.', body: '' };
  task.result.nextActions = [action];
  const operationId = '44444444-4444-4444-8444-444444444444';
  const nextOperationId = '55555555-5555-4555-8555-555555555555';
  const historical = { operationId, runId: task.runId, proposalId: action.proposalId, attemptId: '66666666-6666-4666-8666-666666666666', kind: action.kind,
    target: { ...task.task.target, repository: task.task.repository }, status: 'prepared', retryAllowed: false, account: 'confirmed-reviewer', completedSteps: [], remainingSteps: ['trigger-ci'], urls: [], createdAt: '2026-09-14T02:00:00Z', updatedAt: '2026-09-14T02:00:00Z' };
  const preparedCalls = [];
  await withPage(task, async page => {
    const { nodes } = page; assert.equal(nodes.get('operations-history-section').hidden, false);
    assert.match(nodes.get('operations').textContent, /Trigger CI · prepared/); assert.match(nodes.get('operations').textContent, /confirmed-reviewer/);
    await page.click(control(nodes.get('operations'), 'Continue confirmation'));
    assert.equal(page.tabs.at(-1).url, `chrome-extension://test/action.html?operationId=${operationId}`); assert.equal(preparedCalls.length, 0);
    await page.click(control(proposal(nodes.get('result'), action.proposalId), 'Continue confirmation'));
    assert.equal(page.tabs.at(-1).url, `chrome-extension://test/action.html?operationId=${operationId}`); assert.equal(preparedCalls.length, 0, 'the result proposal reopens its existing attempt');
    historical.status = 'unknown'; historical.updatedAt = '2026-09-14T02:01:00Z';
    for (let i = 0; i < 3; i++) await page.refresh(); clean(page);
    assert.equal(control(nodes.get('operations'), 'Prepare another attempt'), undefined);
    assert.equal(control(nodes.get('operations'), 'Review another CI run'), undefined);
    await page.click(control(nodes.get('operations'), 'Check action status')); assert.equal(preparedCalls.length, 0);
    historical.status = 'succeeded'; historical.retryAllowed = true; historical.updatedAt = '2026-09-14T02:02:00Z'; historical.urls = ['https://github.com/microsoft/PowerToys/pull/456#issuecomment-123'];
    for (let i = 0; i < 3; i++) await page.refresh(); clean(page);
    await page.click(control(nodes.get('operations'), 'Review another CI run')); clean(page);
    assert.equal(preparedCalls.length, 1); assert.equal(preparedCalls[0].runId, task.runId); assert.equal(preparedCalls[0].proposalId, action.proposalId);
    assert.equal(preparedCalls[0].retry, true); assert.match(preparedCalls[0].attemptId, /^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/i);
    assert.notEqual(preparedCalls[0].attemptId, historical.attemptId); assert.equal(page.tabs.at(-1).url, `chrome-extension://test/action.html?operationId=${nextOperationId}`);
    assert.equal(page.calls.some(call => call.type === 'operations.submit' || call.type.startsWith('webActions.')), false);
  }, { resultActions: [historical], prepareAction: async payload => { preparedCalls.push(structuredClone(payload)); return { ...historical, operationId: nextOperationId, attemptId: payload.attemptId, status: 'prepared', retryAllowed: false }; } });
});

test('a lost result-action preparation response retains the attempt identity until the confirmation is recovered', async () => {
  const task = proposalRun('recover-result-action');
  const action = { proposalId: 'host-recover-merge', kind: 'merge-pr', reason: 'Review this merge proposal.', body: '' }; task.result.nextActions = [action];
  const operationId = '88888888-8888-4888-8888-888888888888'; const calls = []; const history = [];
  await withPage(task, async page => {
    const review = control(proposal(page.nodes.get('result'), action.proposalId), 'Review merge');
    await page.click(review); assert.match(page.nodes.get('error').textContent, /Simulated response lost/); assert.equal(page.tabs.length, 0);
    await page.click(review); clean(page);
    assert.equal(calls.length, 2); assert.deepEqual(calls[0], calls[1], 'recovery retries the identical request, not a fresh action');
    assert.match(calls[0].attemptId, /^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/i);
    assert.equal(page.tabs[0].url, `chrome-extension://test/action.html?operationId=${operationId}`);
    await page.click(control(proposal(page.nodes.get('result'), action.proposalId), 'Continue confirmation'));
    assert.equal(calls.length, 2); assert.equal(page.tabs[1].url, page.tabs[0].url); noPublish(page);
  }, { resultActions: history, prepareAction: async payload => {
    calls.push(structuredClone(payload)); if (calls.length === 1) throw new Error('Simulated response lost');
    const prepared = { operationId, runId: task.runId, proposalId: action.proposalId, attemptId: payload.attemptId, kind: action.kind, target: { ...task.task.target, repository: task.task.repository },
      status: 'prepared', retryAllowed: false, completedSteps: [], remainingSteps: ['merge-pr'], urls: [], createdAt: '2026-09-14T02:00:00Z' };
    history.push(prepared); return structuredClone(prepared);
  } });
});

test('explicitly retryable cancelled and failed result actions keep their history while opening a new attempt', async () => {
  for (const status of ['cancelled', 'failed']) {
    const task = proposalRun(`retry-${status}-action`);
    const action = { proposalId: `host-${status}-merge`, kind: 'merge-pr', reason: 'Review this merge proposal.', body: '' }; task.result.nextActions = [action];
    const originalId = '99999999-9999-4999-8999-999999999999'; const newId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    const original = { operationId: originalId, runId: task.runId, proposalId: action.proposalId, attemptId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', kind: action.kind,
      target: { ...task.task.target, repository: task.task.repository }, status, retryAllowed: true, completedSteps: [], remainingSteps: ['merge-pr'], urls: [], createdAt: '2026-09-14T02:00:00Z' };
    const history = [original]; const calls = [];
    await withPage(task, async page => {
      await page.click(control(proposal(page.nodes.get('result'), action.proposalId), 'View action status'));
      assert.equal(calls.length, 0); assert.equal(page.tabs.at(-1).url, `chrome-extension://test/action.html?operationId=${originalId}`);
      await page.click(control(page.nodes.get('operations'), 'Prepare another attempt')); clean(page);
      assert.equal(calls.length, 1); assert.equal(calls[0].retry, true); assert.notEqual(calls[0].attemptId, original.attemptId);
      assert.equal(page.tabs.at(-1).url, `chrome-extension://test/action.html?operationId=${newId}`);
      assert.equal(history.length, 2); assert.equal(history[0], original); assert.equal(history[0].status, status);
      assert.ok(page.nodes.get('operations').textContent.includes(originalId)); assert.ok(page.nodes.get('operations').textContent.includes(newId));
      assert.equal(control(page.nodes.get('operations'), 'Prepare another attempt'), undefined, 'a new prepared attempt prevents a second retry');
      noPublish(page);
    }, { resultActions: history, prepareAction: async payload => {
      calls.push(structuredClone(payload)); const prepared = { ...original, operationId: newId, attemptId: payload.attemptId, status: 'prepared', retryAllowed: false, createdAt: '2026-09-14T02:01:00Z' };
      history.push(prepared); return structuredClone(prepared);
    } });
  }
});

for (const [kind, label, targetType, operationId] of [
  ['merge-pr', 'Review merge', 'pr', '22222222-2222-4222-8222-222222222222'],
  ['trigger-ci', 'Review CI trigger', 'pr', '33333333-3333-4333-8333-333333333333'],
]) {
  test(`${kind} prepares the exact Host proposal and opens only a stable, target-matching confirmation`, async () => {
    const task = proposalRun(`prepare-${kind}`, targetType);
    const action = { proposalId: `host-${kind}-proposal`, kind, reason: 'Inspect the proposal before publishing.', body: kind === 'trigger-ci' ? '/azp run' : '' };
    if (kind === 'create-pr') action.pullRequest = { head: 'test-user:fix/shortcuts', base: 'main', title: 'Fix shortcut registration', body: '\n  ## Fix\n\nPreserve this Markdown exactly.  \n', draft: true };
    task.result.nextActions = [action];
    const calls = []; const history = [];
    const prepared = payload => ({ operationId, kind, runId: task.runId, proposalId: action.proposalId, attemptId: payload.attemptId, target: { ...task.task.target, repository: task.task.repository }, status: 'prepared', completedSteps: [], remainingSteps: [], urls: ['https://example.invalid/never-open-this'] });
    await withPage(task, async page => {
      const { nodes } = page;
      const item = proposal(nodes.get('result'), action.proposalId); assert.ok(item);
      assert.equal(item.dataset.proposalId, action.proposalId);
      const review = control(item, label);
      await page.click(review); await page.click(review); clean(page);
      assert.equal(calls.length, 1, 'subsequent clicks reopen the existing confirmation');
      assert.equal(calls[0].runId, task.runId); assert.equal(calls[0].proposalId, action.proposalId);
      assert.match(calls[0].attemptId, /^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/i);
      assert.deepEqual(page.tabs, [{ url: `chrome-extension://test/action.html?operationId=${operationId}` }, { url: `chrome-extension://test/action.html?operationId=${operationId}` }]);
      assert.equal(page.calls.some(call => call.type === 'operations.preview'), false); noPublish(page);
      if (targetType === 'pr') {
        task.result.review.headSha = 'b'.repeat(40); await page.refresh(); clean(page);
        const before = calls.length;
        await page.click(control(proposal(nodes.get('result'), action.proposalId), 'Continue confirmation'));
        assert.equal(calls.length, before, 'a stale saved review can inspect an existing confirmation without preparing a new action');
        task.result.review.headSha = sha;
      }
      delete action.proposalId; const before = calls.length; await page.refresh(); clean(page);
      assert.equal(control(nodes.get('result'), label)?.disabled, true, 'a local display fallback cannot authorize a new Host proposal'); assert.equal(calls.length, before);
      assert.ok(calls.every(call => Object.keys(call).sort().join(',') === 'attemptId,proposalId,runId')); noPublish(page);
    }, { resultActions: history, prepareAction: async payload => { calls.push(structuredClone(payload)); const operation = prepared(payload); history.push(operation); return structuredClone(operation); } });
  });
}

test('Create Draft PR opens the exact saved proposal workspace without preparing or publishing it', async () => {
  const task = proposalRun('draft-workspace-source', 'issue');
  const action = { proposalId: 'saved-draft-proposal', kind: 'create-pr', reason: 'Publish the verified candidate as a Draft PR.', body: '', pullRequest: { head: 'test-user:fix/shortcuts', base: 'main', title: 'Fix shortcut registration', body: '## Change\n\nRefs #123', draft: true, sourceHeadSha: sha } };
  task.result.nextActions = [action];
  await withPage(task, async page => {
    const review = control(proposal(page.nodes.get('result'), action.proposalId), 'Create Draft PR');
    assert.ok(review); await page.click(review); await page.click(review);
    assert.deepEqual(page.tabs, Array.from({ length: 2 }, () => ({ url: 'chrome-extension://test/action.html?runId=draft-workspace-source&proposalId=saved-draft-proposal' })));
    assert.equal(page.calls.some(call => call.type === 'resultActions.prepare'), false); noPublish(page);
    delete action.proposalId; await page.refresh();
    assert.equal(control(page.nodes.get('result'), 'Create Draft PR').disabled, true, 'a local fallback cannot identify a saved Draft proposal');
  }, { prepareAction: async () => { throw new Error('The task page must open the workspace before preparation.'); } });
});

test('result action preparation rejects a mismatched target, kind or saved proposal identity before opening any tab', async () => {
  for (const invalid of [
    { target: { type: 'pr', number: 456, repository: 'different/repository' } },
    { operationId: 'invalid-uuid?operationId=injected' },
    { kind: 'comment' },
    { runId: 'a-different-task-record' },
    { proposalId: 'host-a-different-proposal' },
  ]) {
    const task = proposalRun('invalid-result-action');
    const action = { proposalId: 'host-invalid-merge', kind: 'merge-pr', reason: 'Review the suggested merge.', body: '' };
    task.result.nextActions = [action];
    await withPage(task, async page => {
      await page.click(control(proposal(page.nodes.get('result'), action.proposalId), 'Review merge'));
      assert.equal(page.tabs.length, 0); assert.match(page.nodes.get('error').textContent, /different result (?:action|proposal)/); noPublish(page);
    }, { prepareAction: async payload => ({ operationId: '77777777-7777-4777-8777-777777777777', kind: action.kind, runId: task.runId, proposalId: action.proposalId, attemptId: payload.attemptId,
      target: { ...task.task.target, repository: task.task.repository }, status: 'prepared', completedSteps: [], remainingSteps: [], urls: [], ...invalid }) });
  }
});

test('fixed PR actions remain available without AI success and only a confirmed current v3 P0 disables manual approval', async () => {
  for (const [name, mutate, p0] of [
    ['no-result', task => { delete task.result; }, false],
    ['running', task => { task.status.state = 'running'; }, false],
    ['failed', task => { task.status.state = 'failed'; task.status.exitCode = 1; task.result.outcome = 'failed'; task.result.report.complete = false; }, false],
    ['required-e2e-p1', task => { task.result.findings = [v3Finding('p1', { priority: 'P1' })]; task.result.e2eAssessment = { ...task.result.e2eAssessment, level: 'required', question: 'Verify focus.', scenarios: ['Open the dialog.'], expectedResults: ['Focus returns.'] }; }, false],
    ['legacy-high', task => { task.result = v2({ findings: [{ id: 'legacy-high', title: 'Old high finding', severity: 'high', status: 'open', path: 'src/Shortcuts.cs', line: 17, details: 'Old classification.', evidence: [] }] }); }, false],
    ['confirmed-p0', task => { task.result.findings = [v3Finding('p0', { priority: 'P0' })]; }, true],
    ['stale-p0', task => { task.result.findings = [v3Finding('stale-p0', { priority: 'P0' })]; task.result.assessment.revisionSha = 'b'.repeat(40); task.result.reviewConclusion.revisionSha = 'b'.repeat(40); }, false],
  ]) {
    const task = v3Run(`manual-${name}`); mutate(task);
    await withPage(task, async page => {
      const { nodes } = page;
      assert.ok(control(nodes.get('more-actions-list'), 'Write an independent comment'), name); assert.ok(control(nodes.get('more-actions-list'), 'Review merge'), name); assert.equal(nodes.get('manual-pr-actions').hidden, false, name);
      assert.deepEqual(nodes.get('operation-kind').children.map(option => option.value), fixedPrKinds, name);
      await page.openManual('approve'); clean(page);
      assert.equal(nodes.get('submit-operation').disabled, p0, name);
      assert.equal(nodes.get('operation-kind').children.find(option => option.value === 'approve').disabled, p0, name);
      assert.equal(nodes.get('operation-kind').children.find(option => option.value === 'comment').disabled, false, name); noPublish(page);
    });
  }
});

test('a completed v3 review keeps its completed issue-count badge and displays the final report before supporting sections', async () => {
  const task = v3Run('completed-two-findings', { findings: [
    v3Finding('p2', { feedback: { body: 'Comment on P2', suggestionId: 'code' } }),
    v3Finding('p3', { priority: 'P3' }),
  ], review: { headSha: sha, body: '', suggestions: [{ id: 'code', path: 'src/Shortcuts.cs', line: 17, side: 'RIGHT', body: 'Comment on P2', replacement: 'Replacement();' }] } });
  await withPage(task, async page => {
    const { nodes } = page;
    assert.equal(nodes.get('run-status').textContent, 'Completed · 2 issues');
    assert.equal(nodes.get('result-section').hidden, false); assert.equal(nodes.get('diagnostic-section').hidden, true);
    page.preview.files = [{ path: 'src/Shortcuts.cs', status: 'modified', lines: [{ line: 17, original: 'Original();', kind: 'add', hunk: 1 }] }];
    await page.click(nodes.get('prepare'));
    const editor = descendants(nodes.get('recommendation-findings')).find(node => node.className === 'finding-feedback' && node.dataset.findingId === 'p2');
    const selected = descendants(editor).find(node => node.tagName === 'INPUT' && node.type === 'checkbox');
    assert.equal(selected.checked, true); assert.equal(nodes.get('operation-kind').value, 'comment');
    selected.checked = false; await page.fire(selected, 'change');
    assert.equal(nodes.get('operation-kind').value, 'comment', 'the other confirmed finding remains selected');
    const other = descendants(nodes.get('recommendation-findings')).find(node => node.className === 'finding-feedback' && node.dataset.findingId === 'p3');
    const otherSelected = descendants(other).find(node => node.tagName === 'INPUT' && node.type === 'checkbox'); otherSelected.checked = false; await page.fire(otherSelected, 'change');
    assert.equal(nodes.get('operation-kind').value, 'approve'); await page.refresh();
    assert.equal(nodes.get('run-status').textContent, 'Completed · 2 issues'); noPublish(page);
  });
  const html = await readFile(new URL('../public/details.html', import.meta.url), 'utf8');
  const sections = ['id="conclusion-title"', 'id="next-step-title"', 'id="recommendation-findings"', 'id="github-section"', 'id="more-actions"', 'id="submit-operation"', 'id="result-evidence"', 'id="run-status"', 'id="full-report-status"', 'id="result-section"', 'id="review-scope-section"', 'id="diagnostic-section"', 'id="operations-history-section"', 'id="execution-title"', 'id="execution-logs"', 'id="cleanup-section"'].map(marker => html.indexOf(marker));
  assert.ok(sections.every((position, index) => position >= 0 && (!index || position > sections[index - 1])), 'conclusion, recommendation and shared action footer precede supporting evidence');
});

test('v3 selected feedback composes only chosen drafts, preserves an explicit decision, and requires clearing feedback before close', async () => {
  const inlineBody = '  Inline explanation.\n'; const ordinaryBody = '\n## Ordinary finding\nKeep trailing spaces.  \n';
  const task = v3Run('selected-feedback-integration', { findings: [
    v3Finding('inline', { feedback: { body: inlineBody, suggestionId: 'inline-suggestion' } }),
    v3Finding('ordinary', { priority: 'P3', feedback: { body: ordinaryBody, suggestionId: null } }),
    v3Finding('not-selected', { feedback: { body: 'UNSELECTED COMMENT MUST NOT BE SENT', suggestionId: null } }),
  ], review: { headSha: sha, body: '', suggestions: [{ id: 'inline-suggestion', path: 'src/Shortcuts.cs', line: 17, side: 'RIGHT', body: 'Source suggestion body', replacement: 'OriginalReplacement();' }] } });
  const submissions = [];
  await withPage(task, async page => {
    const { nodes } = page;
    page.preview.files = [{ path: 'src/Shortcuts.cs', status: 'modified', lines: [{ line: 17, original: 'OriginalCall();', kind: 'add', hunk: 1 }] }];
    const editor = id => descendants(nodes.get('recommendation-findings')).find(node => node.className === 'finding-feedback' && node.dataset.findingId === id);
    const selected = id => descendants(editor(id)).find(node => node.tagName === 'INPUT' && node.type === 'checkbox');
    assert.equal(nodes.get('operation-kind').value, 'comment', 'all confirmed findings start selected for an ordinary comment');
    await page.click(nodes.get('prepare'));
    assert.equal(selected('inline').checked, true); assert.equal(selected('ordinary').checked, true); assert.equal(selected('not-selected').checked, true);
    assert.equal(nodes.get('operation-kind').value, 'comment');
    selected('not-selected').checked = false; await page.fire(selected('not-selected'), 'change');
    const inline = descendants(editor('inline')).filter(node => node.tagName === 'INPUT' && node.type === 'checkbox')[1]; assert.equal(inline.checked, false); inline.checked = true; await page.fire(inline, 'change');
    const replacement = descendants(editor('inline')).filter(node => node.tagName === 'TEXTAREA')[1]; replacement.value = '  CorrectedCall();\n'; await page.fire(replacement, 'input');
    nodes.get('operation-body').value = '  Additional context.\n'; await page.fire(nodes.get('operation-body'), 'input');
    await page.change('operation-kind', 'requestChanges'); await page.refresh(); await page.click(nodes.get('prepare'));
    assert.equal(nodes.get('operation-kind').value, 'requestChanges'); assert.match(nodes.get('action-selection-mode').textContent, /Your chosen/);
    assert.equal(selected('inline').checked, true); assert.equal(selected('ordinary').checked, true);
    await page.submit();
    assert.deepEqual(submissions[0].findingIds, ['inline', 'ordinary']); assert.equal(submissions[0].kind, 'requestChanges');
    assert.equal(submissions[0].body, `  Additional context.\n\n\n${ordinaryBody}`);
    assert.equal(submissions[0].suggestions.length, 1); assert.equal(submissions[0].suggestions[0].body, inlineBody); assert.equal(submissions[0].suggestions[0].replacement, '  CorrectedCall();\n');
    assert.doesNotMatch(JSON.stringify(submissions[0]), /UNSELECTED COMMENT/); assert.equal(Object.hasOwn(submissions[0], 'proposalId'), false);
    await page.change('operation-kind', 'close');
    assert.equal(nodes.get('close-reason-label').hidden, false); assert.equal(nodes.get('submit-operation').disabled, true);
    assert.equal(selected('inline').checked, true); assert.equal(selected('ordinary').checked, true); assert.equal(nodes.get('selected-feedback').hidden, false);
    assert.match(nodes.get('operation-explanation').textContent, /Closing cannot include selected feedback/);
    for (const id of ['inline', 'ordinary']) { selected(id).checked = false; await page.fire(selected(id), 'change'); }
    assert.equal(nodes.get('operation-kind').value, 'close', 'selection changes cannot replace the explicitly chosen action');
    nodes.get('close-reason').value = '  Upstream fix verified on the recorded revision.\n'; await page.fire(nodes.get('close-reason'), 'input'); await page.submit();
    assert.equal(submissions[1].kind, 'close'); assert.equal(submissions[1].body, ''); assert.equal(submissions[1].closeReason, '  Upstream fix verified on the recorded revision.\n');
    assert.equal(Object.hasOwn(submissions[1], 'findingIds'), false); assert.equal(Object.hasOwn(submissions[1], 'suggestions'), false);
    await page.click(nodes.get('use-suggested-action')); assert.equal(nodes.get('operation-kind').value, 'approve');
  }, { submitOperation: async payload => { submissions.push(structuredClone(payload)); return { ...payload, status: 'succeeded', createdAt: '2026-09-15T00:00:00Z' }; } });
});

test('the unified workspace always states its conclusion and no recommendation, with manual choices in the same footer', async () => {
  const task = v3Run('explicit-no-recommendation', { recommendation: { kind: 'none', reason: 'The recorded checks are complete; no follow-up action was recommended.' } });
  await withPage(task, async page => {
    const { nodes } = page;
    assert.equal(nodes.get('conclusion-title').textContent, 'No blocking code findings');
    assert.equal(nodes.get('next-step-title').textContent, 'No recommended action');
    assert.match(nodes.get('next-step-reason').textContent, /no follow-up action was recommended/);
    assert.equal(nodes.get('more-actions-label').textContent, 'Choose action'); assert.equal(nodes.get('github-section').hidden, true);
    assert.equal(nodes.get('more-actions').parentElement, nodes.get('primary-next-action').parentElement);
    assert.equal(nodes.get('submit-operation').parentElement, nodes.get('more-actions').parentElement);
    assert.equal(nodes.get('more-actions').parentElement.parentElement.tagName, 'FOOTER');
    await page.click(control(nodes.get('more-actions-list'), 'Detailed result and evidence'));
    assert.equal(nodes.get('result-evidence').open, true); noPublish(page);
  });
});

test('independent comments start blank, preserve recommended selections and drafts, and publish only after confirmation', async () => {
  const task = v3Run('independent-manual-workspace', { findings: [v3Finding('first'), v3Finding('second')] }); const submissions = [];
  await withPage(task, async page => {
    const { nodes } = page;
    const editor = id => descendants(nodes.get('recommendation-findings')).find(node => node.className === 'finding-feedback' && node.dataset.findingId === id);
    const selected = id => descendants(editor(id)).find(node => node.tagName === 'INPUT' && node.type === 'checkbox');
    const body = descendants(editor('first')).find(node => node.tagName === 'TEXTAREA'); body.value = 'Edited recommended finding.'; await page.fire(body, 'input');
    selected('second').checked = false; await page.fire(selected('second'), 'change');
    nodes.get('operation-body').value = 'Recommended context.'; await page.fire(nodes.get('operation-body'), 'input');
    await page.openManual();
    assert.equal(nodes.get('operation-body').value, ''); assert.equal(nodes.get('recommendation-findings').hidden, true); assert.equal(nodes.get('selected-feedback').hidden, true);
    nodes.get('operation-body').value = 'Independent manual comment.'; await page.fire(nodes.get('operation-body'), 'input');
    await page.review();
    assert.match(nodes.get('feedback-confirmation-content').textContent, /Independent manual comment/);
    assert.doesNotMatch(nodes.get('feedback-confirmation-content').textContent, /Edited recommended finding|Recommended context|Feedback second/);
    assert.equal(submissions.length, 0); await page.click(nodes.get('feedback-back'));
    assert.equal(nodes.get('feedback-confirmation').open, false); assert.equal(nodes.get('operation-body').value, 'Independent manual comment.'); assert.equal(submissions.length, 0);
    await page.click(nodes.get('use-suggested-action'));
    assert.equal(nodes.get('operation-body').value, 'Recommended context.'); assert.equal(nodes.get('recommendation-findings').hidden, false);
    assert.equal(selected('first').checked, true); assert.equal(selected('second').checked, false);
    assert.equal(descendants(editor('first')).find(node => node.tagName === 'TEXTAREA').value, 'Edited recommended finding.');
    await page.openManual(); assert.equal(nodes.get('operation-body').value, 'Independent manual comment.');
    await page.submit(); assert.equal(submissions.length, 1); assert.equal(submissions[0].body, 'Independent manual comment.');
    for (const key of ['findingIds', 'suggestions', 'proposalId']) assert.equal(Object.hasOwn(submissions[0], key), false, key);
    await page.click(nodes.get('use-suggested-action')); await page.refresh();
    assert.equal(nodes.get('operation-body').value, 'Recommended context.'); assert.equal(selected('second').checked, false);
  }, { submitOperation: async payload => { submissions.push(structuredClone(payload)); return { ...payload, status: 'succeeded', createdAt: '2026-09-17T04:00:00Z' }; } });
});

test('a saved unknown operation opens recovery in the common footer and its matching receipt clears only that pending state', async () => {
  const task = v3Run('saved-operation-recovery', { findings: [v3Finding('retained')] }); const operationId = '12345678-1234-4123-8123-123456789abc';
  await withPage(task, async page => {
    const { nodes } = page;
    const recover = control(nodes.get('primary-next-action'), 'Verify pending action'); assert.ok(recover); assert.equal(nodes.get('submit-operation').hidden, true);
    assert.match(nodes.get('next-step-context').textContent, /earlier submission is not confirmed/);
    await page.click(recover); assert.equal(nodes.get('operations-history-section').open, true); assert.equal(nodes.get('result-evidence').open, true);
    await page.openManual(); assert.equal(nodes.get('submit-operation').disabled, true); assert.equal(nodes.get('submit-operation').hidden, true);
    await page.click(control(nodes.get('operations'), 'Verify submission'));
    assert.equal(Boolean(control(nodes.get('primary-next-action'), 'Verify pending action')), false); assert.equal(nodes.get('submit-operation').disabled, false);
    assert.deepEqual(page.calls.filter(call => call.type === 'operations.reconcile').map(call => call.payload), [{ runId: task.runId, operationId }]);
    assert.equal([...page.storage.keys()].some(key => key.includes('pending-operation')), false);
    assert.equal([...page.storage.keys()].some(key => key.includes('finding-feedback')), true, 'finding drafts are independent of the pending operation receipt');
    noPublish(page);
  }, {
    initialize: () => { assert.equal(writeDraft(resultDraftScope(task, 'pending-operation'), { operationId }), true); },
    reconcile: async (payload, page) => { assert.equal(payload.operationId, operationId); const receipt = { operationId, runId: task.runId, kind: 'comment', body: 'Earlier confirmed comment.', status: 'succeeded', createdAt: '2026-09-17T04:00:00Z' }; page.operations.push(receipt); return receipt; },
  });
});

test('manual merge and CI prepare fixed-target confirmations without an AI proposal or automatic publishing', async () => {
  const task = v3Run('manual-confirmations'); task.status.state = 'failed'; delete task.result;
  const history = []; const calls = [];
  const ids = { 'merge-pr': '88888888-8888-4888-8888-888888888888', 'trigger-ci': '99999999-9999-4999-8999-999999999999' };
  await withPage(task, async page => {
    for (const [id, kind] of [['manual-merge', 'merge-pr'], ['manual-ci', 'trigger-ci']]) {
      await page.click(page.nodes.get(id)); clean(page);
      const prepared = calls.at(-1); assert.deepEqual(Object.keys(prepared).sort(), ['attemptId', 'kind', 'runId']);
      assert.equal(prepared.runId, task.runId); assert.equal(prepared.kind, kind); assert.match(prepared.attemptId, /^[a-f0-9-]{36}$/i);
      assert.equal(page.tabs.at(-1).url, `chrome-extension://test/action.html?operationId=${ids[kind]}`);
      const before = calls.length; await page.click(page.nodes.get(id)); assert.equal(calls.length, before, 'the prepared confirmation is reopened without another request'); noPublish(page);
    }
  }, { resultActions: history, prepareAction: async payload => {
    calls.push(structuredClone(payload)); const operation = { operationId: ids[payload.kind], kind: payload.kind, runId: task.runId, attemptId: payload.attemptId, target: { ...task.task.target, repository: task.task.repository }, status: 'prepared', createdAt: '2026-09-15T00:00:00Z', updatedAt: '2026-09-15T00:00:00Z', completedSteps: [], remainingSteps: [], urls: [] };
    history.push(operation); return operation;
  } });
});

test('the page renders every loaded final finding and keeps fixed actions available when a later result page cannot be read', async () => {
  const all = Array.from({ length: 225 }, (_, index) => v3Finding(`paged-${index + 1}`, { priority: index === 224 ? 'P0' : 'P2' }));
  const task = v3Run('full-paged-report', { findings: [] }); const fingerprint = 'c'.repeat(64);
  task.resultPaging = { fingerprint, sections: [{ path: 'findings', total: all.length, nextOffset: 0 }] };
  await withPage(task, async page => {
    assert.equal(page.nodes.get('full-report-status').hidden, true);
    const findings = descendants(page.nodes.get('result')).filter(node => node.className.includes('final-report-finding') && node.tagName === 'ARTICLE');
    assert.equal(findings.length, 225); assert.ok(page.nodes.get('result').textContent.includes('Finding paged-225'));
    assert.deepEqual(page.calls.filter(call => call.type === 'tasks.resultPage').map(call => call.payload.offset), [0, 100, 200]);
    await page.click(page.nodes.get('prepare')); await page.change('operation-kind', 'approve'); assert.equal(page.nodes.get('submit-operation').disabled, true, 'the late P0 is included in manual business checks'); noPublish(page);
  }, { readPage: async payload => {
    assert.equal(payload.fingerprint, fingerprint); const items = all.slice(payload.offset, payload.offset + 100);
    return { items, total: all.length, nextOffset: payload.offset + items.length === all.length ? null : payload.offset + items.length, fingerprint };
  } });
  const unavailable = v3Run('unavailable-paged-report', { findings: [] }); unavailable.resultPaging = { fingerprint, sections: [{ path: 'findings', total: 225, nextOffset: 0 }] };
  await withPage(unavailable, async page => {
    assert.equal(page.nodes.get('result-section').hidden, true); assert.equal(page.nodes.get('full-report-status').hidden, false);
    assert.match(page.nodes.get('full-report-status').textContent, /Simulated report page unavailable/);
    assert.ok(control(page.nodes.get('more-actions-list'), 'Write an independent comment')); await page.openManual('approve');
    assert.equal(page.nodes.get('submit-operation').disabled, false, 'a display read failure is not an extra AI approval restriction'); noPublish(page);
  }, { readPage: async () => { throw new Error('Simulated report page unavailable.'); } });
});

test('a rejected report leads directly to field diagnostics without implying missing build prerequisites', async () => {
  const task = v3Run('invalid-result-fields', {
    outcome: 'blocked', summary: 'The final response did not match the version 3 result contract.',
    report: { complete: false, rechecked: false, coverage: [], limitations: ['The original response is retained.'] },
    diagnostics: [
      { code: 'INVALID_RESULT', severity: 'error', message: 'The report was rejected by field validation.', recovery: 'inspectResult' },
      { code: 'INVALID_RESULT_FIELD', severity: 'error', message: '$.verificationEvidence[1].runId: Must be null for current-run evidence.', recovery: 'inspectResult' },
    ], rawOutput: '{"source":"current-run","runId":"external-test-identifier"}',
  });
  await withPage(task, async page => {
    const { nodes } = page;
    assert.equal(nodes.get('conclusion-title').textContent, 'Final report format is invalid');
    assert.doesNotMatch(nodes.get('conclusion-title').textContent, /missing requirements|No blocking/);
    const inspect = control(nodes.get('primary-next-action'), 'Inspect original response and diagnostics');
    assert.ok(inspect); await page.click(inspect);
    assert.equal(nodes.get('result-evidence').open, true);
    assert.equal(nodes.get('diagnostic-section').hidden, false);
    assert.match(nodes.get('diagnostics').textContent, /\$\.verificationEvidence\[1\]\.runId/);
    assert.match(nodes.get('diagnostics').textContent, /external-test-identifier/);
    noPublish(page);
  });
});

test('visible feedback and approval editors automatically load the saved GitHub account once per page', async () => {
  for (const kind of ['feedback', 'approval']) {
    const task = v3Run(`automatic-target-${kind}`, kind === 'feedback'
      ? { findings: [v3Finding('first')] }
      : { recommendation: { kind: 'approve', reason: 'Inspect the completed review before approving.' } });
    await withPage(task, async page => {
      const { nodes } = page;
      assert.equal(page.previewReads, 1, `${kind} needs no manual load click`);
      assert.equal(nodes.get('operation-editor').hidden, false);
      assert.match(nodes.get('github-target-status').textContent, /Connected as @test-reviewer/);
      assert.equal(nodes.get('prepare').textContent, 'Refresh');
      assert.equal(nodes.get('submit-operation').disabled, false);
      for (let index = 0; index < 5; index++) await page.refresh();
      assert.equal(page.previewReads, 1, '2.5-second task polling does not keep querying GitHub');
      await page.openManual();
      assert.equal(page.previewReads, 1, 'the manual editor reuses the loaded target/account');
      await page.click(nodes.get('use-suggested-action'));
      assert.equal(page.previewReads, 1, 'returning to recommendation preserves the same preview');
      noPublish(page);
    });
  }
});

test('automatic target loading coalesces concurrent requests and preserves edited findings and action choice', async () => {
  const waiting = deferred();
  const task = v3Run('automatic-target-pending', { findings: [v3Finding('first'), v3Finding('second')] });
  await withPage(task, async page => {
    const { nodes } = page;
    assert.equal(page.previewReads, 1);
    assert.match(nodes.get('github-target-status').textContent, /Checking GitHub target using your saved account/);
    assert.equal(nodes.get('submit-operation').disabled, true);
    assert.equal(nodes.get('prepare').hidden, true);
    const pendingButton = control(nodes.get('primary-next-action'), 'Checking GitHub target…');
    assert.ok(pendingButton); assert.equal(pendingButton.disabled, true);
    const editor = id => descendants(nodes.get('recommendation-findings')).find(node => node.className === 'finding-feedback' && node.dataset.findingId === id);
    const selected = descendants(editor('second')).find(node => node.tagName === 'INPUT' && node.type === 'checkbox');
    selected.checked = false; await page.fire(selected, 'change');
    const findingBody = descendants(editor('first')).find(node => node.tagName === 'TEXTAREA'); findingBody.value = 'Edited while GitHub is loading.'; await page.fire(findingBody, 'input');
    nodes.get('operation-body').value = 'Additional context written during loading.'; await page.fire(nodes.get('operation-body'), 'input');
    await page.change('operation-kind', 'requestChanges');
    await page.fire(nodes.get('prepare'), 'click'); await page.refresh(); await page.refresh();
    assert.equal(page.previewReads, 1, 'forced and polling paths share the pending request');
    waiting.resolve(page.preview); await settle();
    assert.equal(nodes.get('github-error').hidden, true, nodes.get('github-error').textContent);
    assert.equal(nodes.get('operation-kind').value, 'requestChanges');
    assert.equal(nodes.get('operation-body').value, 'Additional context written during loading.');
    assert.equal(descendants(editor('second')).find(node => node.tagName === 'INPUT' && node.type === 'checkbox').checked, false);
    assert.equal(descendants(editor('first')).find(node => node.tagName === 'TEXTAREA').value, 'Edited while GitHub is loading.');
    assert.equal(nodes.get('submit-operation').disabled, false); assert.equal(page.previewReads, 1); noPublish(page);
  }, { readPreview: () => waiting.promise });
});

test('automatic GitHub check failure exposes one explicit retry without a polling retry storm or lost draft', async () => {
  const task = v3Run('automatic-target-failure', { findings: [v3Finding('first'), v3Finding('second')] });
  await withPage(task, async page => {
    const { nodes } = page;
    assert.equal(page.previewReads, 1); assert.equal(nodes.get('submit-operation').disabled, true);
    assert.equal(nodes.get('prepare').textContent, 'Retry'); assert.equal(nodes.get('prepare').hidden, false);
    assert.match(nodes.get('github-error').textContent, /GitHub preview unavailable/);
    const retry = control(nodes.get('primary-next-action'), 'Retry GitHub check'); assert.ok(retry);
    const editor = descendants(nodes.get('recommendation-findings')).find(node => node.className === 'finding-feedback' && node.dataset.findingId === 'second');
    const checkbox = descendants(editor).find(node => node.tagName === 'INPUT' && node.type === 'checkbox'); checkbox.checked = false; await page.fire(checkbox, 'change');
    nodes.get('operation-body').value = 'Preserve my draft across the failed read.'; await page.fire(nodes.get('operation-body'), 'input');
    for (let index = 0; index < 5; index++) await page.refresh();
    assert.equal(page.previewReads, 1, 'a failed automatic read is not retried by task polling');
    await page.click(control(nodes.get('primary-next-action'), 'Retry GitHub check'));
    assert.equal(page.previewReads, 2); assert.equal(nodes.get('github-error').hidden, true);
    assert.match(nodes.get('github-target-status').textContent, /Connected as @test-reviewer/);
    assert.equal(nodes.get('operation-body').value, 'Preserve my draft across the failed read.');
    const restored = descendants(nodes.get('recommendation-findings')).find(node => node.className === 'finding-feedback' && node.dataset.findingId === 'second');
    assert.equal(descendants(restored).find(node => node.tagName === 'INPUT' && node.type === 'checkbox').checked, false);
    assert.equal(nodes.get('submit-operation').disabled, false); noPublish(page);
  }, { readPreview: (page, count) => { if (count === 1) throw new Error('GitHub preview unavailable'); return page.preview; } });
});

test('verification and Draft PR recommendations keep their own entry and defer target preview until manual feedback is chosen', async () => {
  const verification = v3Run('verification-before-manual', { e2eAssessment: { level: 'required', reason: 'Verify the CJK startup path.', question: 'Does the original revision start?', scenarios: ['Start with CJK resources'], expectedResults: ['The window opens'], prerequisites: ['Localized resources'], evidence: ['Runtime not covered'], readiness: 'missing-prerequisites' } });
  const draft = proposalRun('draft-before-manual', 'issue');
  draft.result.assessment = { subject: 'local-candidate', status: 'passed', summary: 'Candidate verified.', revisionSha: sha };
  draft.result.nextActions = [{ proposalId: 'create-draft-proposal', kind: 'create-pr', reason: 'Review the candidate Draft PR.', body: '', recommended: true, pullRequest: { head: 'contributor:fix-focus', base: 'main', sourceHeadSha: sha, title: 'Fix focus', body: 'Refs #123', draft: true } }];
  for (const [task, heading] of [[verification, 'Review verification requirements'], [draft, 'Create Draft PR']]) {
    await withPage(task, async page => {
      const { nodes } = page;
      assert.equal(nodes.get('next-step-title').textContent, heading);
      assert.equal(nodes.get('github-section').hidden, true); assert.equal(page.previewReads, 0);
      assert.ok(control(nodes.get('primary-next-action'), heading));
      for (let index = 0; index < 3; index++) await page.refresh();
      assert.equal(page.previewReads, 0, 'non-feedback recommendation does not cause hidden GitHub traffic');
      await page.openManual();
      assert.equal(page.previewReads, 1, 'choosing the manual editor loads its target automatically');
      assert.equal(nodes.get('operation-editor').hidden, false);
      assert.match(nodes.get('github-target-status').textContent, /Connected as @test-reviewer/);
      await page.click(nodes.get('use-suggested-action'));
      assert.equal(nodes.get('github-section').hidden, true, 'a loaded preview cannot replace the verification or Draft PR recommendation');
      assert.ok(control(nodes.get('primary-next-action'), heading));
      await page.focus(); await page.visibility('visible'); await page.flushTimers();
      assert.equal(page.previewReads, 1, 'a hidden manual editor remains quiet when the page regains focus');
      noPublish(page);
    });
  }
});

test('an automatically loaded preview still requires full feedback review and explicit Send before publication', async () => {
  const task = v3Run('automatic-target-explicit-submit', { findings: [v3Finding('first')] }); const submissions = [];
  await withPage(task, async page => {
    assert.equal(page.previewReads, 1); assert.equal(submissions.length, 0);
    await page.review(); assert.equal(submissions.length, 0);
    assert.match(page.nodes.get('feedback-confirmation-content').textContent, /test-reviewer/);
    assert.match(page.nodes.get('feedback-confirmation-content').textContent, /Feedback first/);
    await page.click(page.nodes.get('feedback-back')); assert.equal(submissions.length, 0);
    await page.submit();
    assert.equal(submissions.length, 1); assert.equal(submissions[0].expectedAccount, 'test-reviewer'); assert.equal(submissions[0].expectedHeadSha, sha);
    assert.deepEqual(submissions[0].findingIds, ['first']); assert.equal(submissions[0].body, 'Feedback first');
  }, { submitOperation: async payload => { submissions.push(structuredClone(payload)); return { operationId: payload.operationId, kind: payload.kind, status: 'succeeded', body: payload.body, createdAt: '2026-09-18T09:00:00Z' }; } });
});

test('secondary Refresh deliberately reloads remote changes without discarding the existing feedback draft', async () => {
  const task = v3Run('automatic-target-refresh', { findings: [v3Finding('first')] });
  await withPage(task, async page => {
    const { nodes } = page;
    assert.equal(page.previewReads, 1); assert.equal(nodes.get('submit-operation').disabled, false);
    nodes.get('operation-body').value = 'My reviewed explanation.'; await page.fire(nodes.get('operation-body'), 'input');
    page.preview.headSha = 'b'.repeat(40); page.preview.stale = true; page.preview.canComment = false;
    await page.click(nodes.get('prepare'));
    assert.equal(page.previewReads, 2); assert.equal(nodes.get('submit-operation').disabled, true);
    assert.equal(nodes.get('operation-body').value, 'My reviewed explanation.');
    assert.match(nodes.get('operation-reasons').textContent, /HEAD has changed/);
    await page.refresh(); assert.equal(page.previewReads, 2); noPublish(page);
  });
});

test('returning from Settings refreshes the saved account once for a focus/visibility burst and keeps the draft', async () => {
  const task = v3Run('automatic-target-settings-return', { findings: [v3Finding('first')] }); const returning = deferred();
  await withPage(task, async page => {
    const { nodes } = page;
    assert.equal(page.previewReads, 1);
    nodes.get('operation-body').value = 'Keep my feedback when the saved account changes.'; await page.fire(nodes.get('operation-body'), 'input');
    page.preview.account = 'changed-reviewer';
    await page.focus(); await page.visibility('visible');
    assert.equal(page.previewReads, 1, 'the return-event burst waits for its trailing debounce');
    assert.equal(nodes.get('submit-operation').disabled, true, 'an account refresh cannot leave the prior authorization enabled');
    assert.equal(page.pendingTimeouts(), 1, 'window focus and visible events retain one trailing timer');
    await page.flushTimers(99); assert.equal(page.previewReads, 1);
    await page.flushTimers(1); assert.equal(page.previewReads, 2, 'the coalesced return triggers one GitHub read');
    returning.resolve(page.preview); await settle();
    assert.match(nodes.get('github-target-status').textContent, /Connected as @changed-reviewer/);
    assert.equal(nodes.get('operation-body').value, 'Keep my feedback when the saved account changes.');
    assert.equal(nodes.get('submit-operation').disabled, false);
    await page.visibility('hidden'); await page.focus(); await page.flushTimers();
    assert.equal(page.previewReads, 2, 'a hidden document does not reload GitHub');
    noPublish(page);
  }, { readPreview: (page, count) => count === 1 ? page.preview : returning.promise });
});

test('a Settings-return event cannot replace the account in an open final confirmation', async () => {
  const task = v3Run('automatic-target-confirmation-account', { findings: [v3Finding('first')] });
  await withPage(task, async page => {
    await page.review(); assert.match(page.nodes.get('feedback-confirmation-content').textContent, /test-reviewer/);
    page.preview.account = 'changed-reviewer'; await page.focus(); await page.flushTimers();
    assert.equal(page.previewReads, 1, 'the final confirmation keeps its frozen account while visible');
    assert.match(page.nodes.get('feedback-confirmation-content').textContent, /test-reviewer/);
    assert.doesNotMatch(page.nodes.get('feedback-confirmation-content').textContent, /changed-reviewer/);
    await page.click(page.nodes.get('feedback-back')); await settle();
    assert.equal(page.previewReads, 1, 'returning to editing schedules the account refresh');
    assert.equal(page.nodes.get('submit-operation').disabled, true);
    await page.flushTimers();
    assert.equal(page.previewReads, 2, 'the deferred refresh runs after returning to editable content');
    assert.match(page.nodes.get('github-target-status').textContent, /Connected as @changed-reviewer/);
    assert.equal(page.calls.filter(call => call.type === 'operations.submit').length, 0);
  }, { submitOperation: async () => { throw new Error('No final send was authorized in this test.'); } });
});

test('a fast Settings return during the first preview discards the old account and loads the queued current account', async () => {
  const task = v3Run('automatic-target-fast-settings-return', { findings: [v3Finding('first')] });
  const firstRead = deferred(); const currentRead = deferred();
  await withPage(task, async page => {
    const originalAccount = structuredClone(page.preview);
    assert.equal(page.previewReads, 1); assert.equal(page.nodes.get('submit-operation').disabled, true);
    page.preview.account = 'changed-reviewer';
    await page.focus(); await page.visibility('visible');
    assert.equal(page.pendingTimeouts(), 1, 'a fast return is retained even while the initial request is pending');
    await page.flushTimers();
    assert.equal(page.previewReads, 1, 'the queued account read waits for the previous request to settle');
    firstRead.resolve(originalAccount); await settle();
    assert.equal(page.previewReads, 2, 'the invalidated request schedules a fresh read after settling');
    assert.equal(page.nodes.get('submit-operation').disabled, true, 'the superseded account never authorizes submission');
    assert.doesNotMatch(page.nodes.get('operation-target').textContent, /test-reviewer/);
    assert.doesNotMatch(page.nodes.get('github-target-status').textContent, /Connected as @test-reviewer/);
    currentRead.resolve(page.preview); await settle();
    assert.match(page.nodes.get('github-target-status').textContent, /Connected as @changed-reviewer/);
    assert.equal(page.nodes.get('submit-operation').disabled, false);
    await page.flushTimers(); await page.refresh(); assert.equal(page.previewReads, 2); noPublish(page);
  }, { readPreview: (_page, count) => count === 1 ? firstRead.promise : currentRead.promise });
});

test('a running task completing during manual final confirmation cannot replace the confirmed account until Back', async () => {
  const task = proposalRun('automatic-target-running-confirmation'); task.status.state = 'running';
  const nextRead = deferred();
  await withPage(task, async page => {
    assert.equal(page.previewReads, 0);
    await page.openManual(); assert.equal(page.previewReads, 1);
    page.nodes.get('operation-body').value = 'The manual comment stays unchanged.'; await page.fire(page.nodes.get('operation-body'), 'input');
    await page.review();
    const confirmed = page.nodes.get('feedback-confirmation-content').textContent;
    assert.match(confirmed, /test-reviewer/); assert.match(confirmed, /The manual comment stays unchanged/);
    task.status.state = 'succeeded'; page.preview.account = 'changed-reviewer';
    await page.refresh(); await page.focus(); await page.flushTimers();
    await page.fire(page.nodes.get('prepare'), 'click');
    assert.equal(page.previewReads, 1, 'polling and refresh cannot reload a preview underneath an open confirmation');
    assert.equal(page.nodes.get('feedback-confirmation-content').textContent, confirmed);
    assert.equal(page.nodes.get('feedback-confirmation').open, true);
    assert.equal(page.calls.filter(call => call.type === 'operations.submit').length, 0);
    await page.click(page.nodes.get('feedback-back')); await settle();
    assert.equal(page.previewReads, 2, 'leaving confirmation applies the deferred task-context refresh exactly once');
    assert.equal(page.nodes.get('submit-operation').disabled, true);
    nextRead.resolve(page.preview); await settle(); await page.flushTimers();
    assert.equal(page.previewReads, 2); assert.match(page.nodes.get('github-target-status').textContent, /Connected as @changed-reviewer/);
    assert.equal(page.nodes.get('operation-body').value, 'The manual comment stays unchanged.');
    assert.equal(page.calls.filter(call => call.type === 'operations.submit').length, 0);
  }, { readPreview: (page, count) => count === 1 ? page.preview : nextRead.promise,
    submitOperation: async () => { throw new Error('The test returns from confirmation without sending.'); } });
});

test('mismatched automatic preview responses cannot enable a GitHub operation or replace the task target', async () => {
  const cases = [
    preview => { preview.target.repository = 'another/repository'; },
    preview => { preview.target.type = 'issue'; },
    preview => { preview.target.number++; },
    preview => { preview.expectedHeadSha = 'b'.repeat(40); },
  ];
  for (const [index, mutate] of cases.entries()) {
    const task = v3Run(`automatic-target-mismatch-${index}`, { findings: [v3Finding('first')] });
    await withPage(task, async page => {
      assert.equal(page.previewReads, 1); assert.equal(page.nodes.get('submit-operation').disabled, true);
      assert.match(page.nodes.get('github-error').textContent, /does not match this task/);
      assert.equal(page.nodes.get('operation-target').childElementCount, 0);
      await page.refresh(); assert.equal(page.previewReads, 1, 'a rejected response waits for an explicit retry'); noPublish(page);
    }, { readPreview: page => { const value = structuredClone(page.preview); mutate(value); return value; } });
  }
});

test('an earlier asynchronous preview is discarded when the target context changes during task refresh', async () => {
  const task = v3Run('automatic-target-context-change', { findings: [v3Finding('first')] });
  const originalRead = deferred(); const nextRead = deferred();
  await withPage(task, async page => {
    const original = structuredClone(page.preview);
    assert.equal(page.previewReads, 1);
    task.task.target.number = 457;
    page.preview.target.number = 457; page.preview.target.url = targetUrl(task); page.preview.url = targetUrl(task);
    await page.refresh(); assert.equal(page.previewReads, 2, 'a changed target cannot inherit the first request');
    originalRead.resolve(original); await settle();
    assert.equal(page.nodes.get('submit-operation').disabled, true);
    assert.doesNotMatch(page.nodes.get('operation-target').textContent, /#456/);
    nextRead.resolve(page.preview); await settle();
    assert.equal(page.nodes.get('submit-operation').disabled, false);
    assert.match(page.nodes.get('operation-target').textContent, /#457/);
    assert.equal(page.previewReads, 2); noPublish(page);
  }, { readPreview: (_page, count) => count === 1 ? originalRead.promise : nextRead.promise });
});
