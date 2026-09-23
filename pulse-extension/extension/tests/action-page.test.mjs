import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { setImmediate as nextTurn } from 'node:timers/promises';

class Node {
  children = []; attributes = new Map(); listeners = new Map(); dataset = {}; hidden = false; disabled = false; open = false; className = ''; _text = ''; value = '';
  constructor(tag = 'div') { this.tagName = tag.toUpperCase(); }
  set textContent(value) { this._text = String(value); this.children = []; }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  set innerHTML(_value) { throw new Error('Confirmation content must be rendered as text, not HTML.'); }
  append(...children) { for (const child of children) child.parentNode = this; this.children.push(...children); }
  prepend(...children) { for (const child of children) child.parentNode = this; this.children.unshift(...children); }
  replaceChildren(...children) { this._text = ''; this.children = []; this.append(...children); }
  setAttribute(key, value) { this.attributes.set(key, value); }
  removeAttribute(key) { this.attributes.delete(key); }
  addEventListener(type, callback) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), callback]); }
  querySelector(selector) { return descendants(this).slice(1).find(node => matches(node, selector)) ?? null; }
  querySelectorAll(selector) { return descendants(this).slice(1).filter(node => matches(node, selector)); }
  contains(node) { return descendants(this).includes(node); }
  focus() { globalThis.document.activeElement = this; }
  get childElementCount() { return this.children.length; }
}

const descendants = node => [node, ...node.children.flatMap(descendants)];
const matches = (node, selector) => selector === 'summary' ? node.tagName === 'SUMMARY' : selector.startsWith('#') ? node.id === selector.slice(1) : selector.startsWith('.') ? node.className.split(' ').includes(selector.slice(1).split('[')[0]) && (!selector.includes('[open]') || node.open) : false;
const operationId = '12345678-1234-4234-8234-123456789abc';
const target = { repository: 'microsoft/PowerToys', type: 'issue', number: 6201 };
const original = { repository: 'microsoft/PowerToys', number: 6200, url: 'https://github.com/microsoft/PowerToys/issues/6200' };
const targetUrl = 'https://github.com/microsoft/PowerToys/issues/6201';
const suffix = `\n\nDuplicate of ${original.url}\n\n<!-- powertoys-pulse:duplicate:fixture -->`;
const attemptKey = `pulse-web-action-attempt:${operationId}`;
let imports = 0;

function duplicate(status = 'prepared', overrides = {}) {
  const body = 'The sample trigger and affected versions match the original report.  \n';
  const completedSteps = status === 'succeeded' ? ['duplicate-comment', 'close-issue'] : status === 'partial' ? ['duplicate-comment'] : [];
  const value = {
    operationId, requestId: operationId, actionId: 'sample-duplicate-closure', kind: 'close-as-duplicate', target: structuredClone(target),
    draft: { requestId: operationId, actionId: 'sample-duplicate-closure', kind: 'close-as-duplicate', target: structuredClone(target), body, duplicateOf: structuredClone(original) },
    status, account: 'test-reviewer', canSubmit: status === 'prepared' || status === 'partial', blockers: [],
    bodyEditable: status === 'prepared', duplicateSuffix: suffix, commentBody: body + suffix, resumeRequired: status === 'partial',
    completedSteps, remainingSteps: ['duplicate-comment', 'close-issue'].filter(step => !completedSteps.includes(step)), urls: [],
    targetUrl, state: 'OPEN', sourceOrigin: 'pulse-task://sample-run', createdAt: '2026-09-15T01:00:00Z', updatedAt: '2026-09-15T01:01:00Z',
    ...overrides,
  };
  if (status === 'unknown' && !value.error) value.error = { code: 'REMOTE_OUTCOME_UNKNOWN', message: 'The sample response was lost after the request may have reached GitHub.' };
  return value;
}

function summary(value) {
  const fields = ['operationId', 'requestId', 'actionId', 'kind', 'target', 'status', 'account', 'completedSteps', 'remainingSteps', 'urls', 'error', 'createdAt', 'updatedAt', 'resumeRequired', 'stepResults', 'runId', 'proposalId', 'retryAllowed'];
  return Object.fromEntries(fields.filter(field => value[field] !== undefined).map(field => [field, structuredClone(value[field])]));
}

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

const settle = async () => { await nextTurn(); await nextTurn(); };

async function withPage(initial, verify, { submit, reconcile, readPreview, storage = new Map(), localStorage = new Map(), source, getRun, resultActions = [], resultPage, prepare, cancel, expectInitialError = false } = {}) {
  const html = await readFile(new URL('../public/action.html', import.meta.url), 'utf8');
  const nodes = new Map([...html.matchAll(/<([a-z][\w-]*)\b([^>]*\bid="([^"]+)"[^>]*)>/gi)].map(([, tag, attributes, id]) => {
    const node = new Node(tag); node.id = id; node.hidden = /\bhidden\b/.test(attributes); node.disabled = /\bdisabled\b/.test(attributes);
    node.className = /\bclass="([^"]*)"/.exec(attributes)?.[1] ?? ''; return [id, node];
  }));
  const body = new Node('body'), main = new Node('main'), header = new Node('header');
  for (const node of nodes.values()) if (node.tagName === 'DETAILS') node.append(new Node('summary'));
  const globals = ['document', 'location', 'chrome', 'sessionStorage', 'localStorage'];
  const originals = new Map(globals.map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  const page = {
    nodes, body, calls: [], storage, localStorage, server: structuredClone(initial), previewReads: 0, tabs: [],
    node(id) { return nodes.get(id) ?? [...nodes.values()].flatMap(descendants).find(node => node.id === id); },
    count(type) { return this.calls.filter(call => call.type === type).length; },
    async fire(id, event = 'click') {
      const node = typeof id === 'string' ? this.node(id) : id;
      assert.ok(node?.listeners.has(event), `The real page registered ${event} for ${node?.id ?? 'the displayed control'}.`);
      for (const listener of node.listeners.get(event)) listener({ preventDefault() {} });
      await settle();
    },
    async click(id) { assert.equal(this.node(id).disabled, false, `${id} is enabled`); assert.equal(this.node(id).hidden, false, `${id} is visible`); await this.fire(id); },
    async edit(value) { const editor = this.node('duplicate-explanation'); assert.ok(editor); editor.value = value; await this.fire(editor, 'input'); },
    async fill(id, value) { assert.equal(this.node(id).disabled, false, `${id} is editable`); this.node(id).value = value; await this.fire(id, 'input'); },
  };
  try {
    globalThis.document = {
      body, getElementById: id => page.node(id), createElement: tag => new Node(tag),
      createElementNS: (namespaceURI, tag) => Object.assign(new Node(tag), { namespaceURI }),
      createTextNode: text => { const node = new Node('text'); node.textContent = text; return node; },
      querySelectorAll: () => [],
      addEventListener: () => {},
      querySelector: selector => selector === 'body > .topbar' ? header : selector === 'body > main' ? main : null,
    };
    globalThis.location = new URL(`chrome-extension://test/action.html?${source ? new URLSearchParams(source) : `operationId=${operationId}`}`);
    globalThis.sessionStorage = { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) };
    globalThis.localStorage = { getItem: key => localStorage.get(key) ?? null, setItem: (key, value) => localStorage.set(key, value) };
    globalThis.chrome = {
      tabs: { create: async value => { page.tabs.push(structuredClone(value)); return {}; } },
      runtime: {
        getURL: path => `chrome-extension://test/${path}`, openOptionsPage: async () => {},
        sendMessage: async message => {
          page.calls.push(structuredClone(message)); assert.equal(message.channel, 'pulse-ui');
          switch (message.type) {
            case 'connection.status': return { ok: true, data: { state: 'connected' } };
            case 'tasks.get': {
              assert.ok(getRun, 'A source run responder is explicitly configured.');
              assert.deepEqual(message.payload, { runId: source.runId });
              return { ok: true, data: structuredClone(await getRun(page)) };
            }
            case 'tasks.resultPage': {
              assert.ok(resultPage, 'A report page responder is explicitly configured.');
              return { ok: true, data: structuredClone(await resultPage(message.payload, page)) };
            }
            case 'resultActions.list': return { ok: true, data: { operations: structuredClone(resultActions) } };
            case 'resultActions.prepare': {
              assert.ok(prepare, 'A draft preparation responder is configured only for an explicit preparation test.');
              return { ok: true, data: structuredClone(await prepare(message.payload, page)) };
            }
            case 'webActions.cancel': {
              assert.ok(cancel, 'A cancellation responder is explicitly configured.');
              return { ok: true, data: structuredClone(await cancel(message.payload, page)) };
            }
            case 'webActions.preview': {
              assert.deepEqual(message.payload, { operationId }); page.previewReads++;
              const value = readPreview ? await readPreview(page, page.previewReads) : page.server;
              return { ok: true, data: structuredClone(value) };
            }
            case 'webActions.submit':
              assert.ok(submit, 'A submission responder is enabled only in an explicit confirmation test.');
              return { ok: true, data: await submit(message.payload, page) };
            case 'webActions.reconcile':
              assert.ok(reconcile, 'Reconciliation is a separately mocked read-only operation.');
              assert.deepEqual(message.payload, { operationId });
              return { ok: true, data: await reconcile(message.payload, page) };
            default: throw new Error(`Unexpected page operation ${message.type}; this test cannot contact Host or GitHub.`);
          }
        },
      },
    };
    await import(new URL(`../src/action.ts?action-page-test=${++imports}`, import.meta.url));
    await settle();
    assert.equal(nodes.get('error').hidden, !expectInitialError, nodes.get('error').textContent);
    await verify(page);
  } finally {
    await settle();
    for (const key of globals) {
      const descriptor = originals.get(key); if (descriptor) Object.defineProperty(globalThis, key, descriptor); else delete globalThis[key];
    }
  }
}

test('first duplicate confirmation preserves edited Markdown and its immutable suffix, then freezes the fresh Host preview', async () => {
  const edited = '    Keep this indentation and trailing spaces.  \n\n<img src=x onerror=alert(1)>\n\n  ';
  await withPage(duplicate(), async page => {
    assert.equal(page.node('page-title').textContent, 'Comment and close as duplicate');
    assert.equal(page.node('action-title').textContent, 'Ready for your confirmation');
    assert.match(page.node('action-confirm-account').textContent, /@test-reviewer/);
    await page.edit(edited);
    const complete = page.node('duplicate-complete-comment');
    assert.equal(complete.children[1].tagName, 'PRE'); assert.equal(complete.children[1].textContent, edited + suffix);
    assert.equal(descendants(page.node('action-draft')).some(node => node.tagName === 'IMG'), false);
    await page.click('refresh');
    assert.equal(page.node('duplicate-explanation').value, edited, 'an editable refresh preserves local unsaved text');
    await page.click('confirm-action');
    const submitCall = page.calls.find(call => call.type === 'webActions.submit');
    assert.deepEqual(submitCall.payload, { operationId, expectedAccount: 'test-reviewer', body: edited });
    assert.equal(page.previewReads, 3, 'initial load, manual refresh, and post-submit authoritative preview');
    assert.equal(page.node('duplicate-explanation'), undefined, 'the confirmed explanation has no editable input');
    const confirmed = descendants(page.node('action-draft')).find(node => node.tagName === 'PRE');
    assert.equal(confirmed.textContent, edited + suffix);
    assert.deepEqual(page.server.draft.duplicateOf, original);
    assert.equal(page.node('action-status').textContent, 'Completed'); assert.equal(page.node('confirm-action').hidden, true);
    assert.equal(page.storage.get(attemptKey), 'submitted'); assert.equal(page.count('webActions.submit'), 1);
  }, { submit: async (payload, page) => {
    const draft = { ...page.server.draft, body: payload.body };
    page.server = duplicate('succeeded', { draft, commentBody: payload.body + suffix, urls: [targetUrl] });
    return summary(page.server);
  } });
});

test('partial duplicate closure requires an explicit resume without another body or association comment', async () => {
  const initial = duplicate('partial');
  await withPage(initial, async page => {
    assert.equal(page.node('confirm-action').textContent, 'Continue closing as duplicate');
    assert.equal(page.node('duplicate-explanation'), undefined);
    assert.match(page.node('action-draft').textContent, /does not post the comment again/);
    assert.match(page.node('action-results').textContent, /Post explanation with the original Issue link/);
    assert.equal(page.count('webActions.submit'), 0);
    await page.click('confirm-action');
    assert.deepEqual(page.calls.find(call => call.type === 'webActions.submit').payload, { operationId, expectedAccount: 'test-reviewer', resume: true });
    assert.equal(page.server.completedSteps.filter(step => step === 'duplicate-comment').length, 1);
    assert.deepEqual(page.server.completedSteps, ['duplicate-comment', 'close-issue']);
    assert.equal(page.previewReads, 2); assert.equal(page.node('action-status').textContent, 'Completed');
    assert.equal(page.node('confirm-action').hidden, true); assert.equal(page.node('cancel-action').hidden, true);
  }, { submit: async (payload, page) => {
    assert.equal(payload.body, undefined); assert.equal(payload.resume, true);
    page.server = duplicate('succeeded', { draft: page.server.draft, commentBody: page.server.commentBody, urls: [targetUrl] });
    return summary(page.server);
  } });
});

test('unknown duplicate results use read-only reconciliation and a fresh partial preview before explicit resume', async () => {
  await withPage(duplicate('unknown'), async page => {
    assert.equal(page.node('confirm-action').hidden, true); assert.equal(page.node('confirm-action').disabled, true);
    assert.equal(page.node('reconcile-action').hidden, false); assert.equal(page.node('reconcile-action').disabled, false);
    await page.fire('confirm-action'); assert.equal(page.count('webActions.submit'), 0);
    await page.click('reconcile-action');
    assert.equal(page.count('webActions.reconcile'), 1); assert.equal(page.count('webActions.submit'), 0, 'a result check does not resume any write');
    assert.equal(page.previewReads, 2); assert.equal(page.node('action-status').textContent, 'Partly completed');
    assert.equal(page.node('confirm-action').textContent, 'Continue closing as duplicate'); assert.equal(page.node('confirm-action').disabled, false);
    assert.equal(page.node('reconcile-action').hidden, true); assert.equal(page.node('action-submit-error').hidden, true);
    await page.click('confirm-action');
    const businessCalls = page.calls.filter(call => call.type.startsWith('webActions.'));
    assert.deepEqual(businessCalls.map(call => call.type), ['webActions.preview', 'webActions.reconcile', 'webActions.preview', 'webActions.submit', 'webActions.preview']);
    assert.deepEqual(businessCalls.find(call => call.type === 'webActions.submit').payload, { operationId, expectedAccount: 'test-reviewer', resume: true });
    assert.equal(page.node('action-status').textContent, 'Completed');
  }, { reconcile: async (_payload, page) => { page.server = duplicate('partial'); return summary(page.server); },
    submit: async (_payload, page) => { page.server = duplicate('succeeded'); return summary(page.server); } });
});

test('a pending duplicate request locks re-entrant confirmation and refresh until the response and fresh preview finish', async () => {
  const pendingSubmit = deferred(), pendingPreview = deferred();
  await withPage(duplicate(), async page => {
    await page.click('confirm-action');
    assert.equal(page.count('webActions.submit'), 1); assert.equal(page.node('confirm-action').disabled, true);
    assert.equal(page.node('refresh').disabled, true); assert.equal(page.node('cancel-action').disabled, true);
    await page.fire('confirm-action'); await page.fire('refresh'); await page.fire('cancel-action');
    assert.equal(page.count('webActions.submit'), 1); assert.equal(page.previewReads, 1);
    page.server = duplicate('partial'); pendingSubmit.resolve(summary(page.server)); await settle();
    assert.equal(page.previewReads, 2); assert.equal(page.node('confirm-action').disabled, true, 'partial summary alone cannot authorize a continuation');
    await page.fire('confirm-action'); assert.equal(page.count('webActions.submit'), 1);
    pendingPreview.resolve(page.server); await settle();
    assert.equal(page.node('confirm-action').disabled, false); assert.equal(page.node('confirm-action').textContent, 'Continue closing as duplicate');
  }, { submit: async () => pendingSubmit.promise, readPreview: async (page, reads) => reads === 1 ? page.server : pendingPreview.promise });
});

test('a lost first-submit response keeps the tab and reloaded confirmation locked even if Host still reports prepared', async () => {
  const storage = new Map();
  await withPage(duplicate(), async page => {
    await page.click('confirm-action');
    assert.equal(page.node('confirm-action').disabled, true); assert.equal(page.node('cancel-action').disabled, true);
    assert.match(page.node('action-notice').textContent, /GitHub may have accepted this action/);
    assert.match(page.node('error').textContent, /sample response lost/); assert.equal(storage.get(attemptKey), 'submitted');
    await page.fire('confirm-action'); assert.equal(page.count('webActions.submit'), 1);
    await page.click('refresh');
    assert.equal(page.node('confirm-action').disabled, true); assert.match(page.node('action-notice').textContent, /outcome has not been confirmed/);
    assert.equal(page.node('duplicate-explanation'), undefined);
  }, { storage, submit: async () => { throw new Error('sample response lost'); } });
  await withPage(duplicate(), async page => {
    assert.equal(page.node('confirm-action').disabled, true); assert.equal(page.node('cancel-action').disabled, true);
    assert.equal(page.node('duplicate-explanation'), undefined); assert.match(page.node('action-notice').textContent, /submission was requested/);
    await page.fire('confirm-action'); assert.equal(page.count('webActions.submit'), 0);
  }, { storage });
});

test('completed duplicate results show both step labels and retain only safe target or original-Issue links', async () => {
  const commentUrl = `${targetUrl}#issuecomment-555`;
  const value = duplicate('succeeded', {
    urls: [commentUrl, commentUrl, original.url, 'javascript:alert(1)', 'https://github.com/another/repo/issues/1', 'https://github.com/microsoft/PowerToys.evil/issues/1'],
    stepResults: {
      'duplicate-comment': { account: 'test-reviewer', completedAt: '2026-09-15T01:02:00Z', url: commentUrl },
      'close-issue': { account: 'test-reviewer', completedAt: '2026-09-15T01:03:00Z', observed: true, url: 'https://github.com/another/repo/issues/1' },
    },
  });
  await withPage(value, async page => {
    assert.equal(page.node('action-status').textContent, 'Completed'); assert.match(page.node('action-notice').textContent, /action completed/);
    const content = page.node('action-results').textContent;
    assert.match(content, /Post explanation with the original Issue link/); assert.match(content, /Close Issue as duplicate/);
    assert.match(content, /@test-reviewer/); assert.match(content, /Confirmed by GitHub evidence/);
    const links = ['action-target', 'action-draft', 'action-results'].flatMap(id => descendants(page.node(id))).filter(node => node.tagName === 'A');
    assert.ok(links.some(node => node.href === original.url)); assert.ok(links.some(node => node.href === targetUrl));
    assert.ok(links.every(node => node.href.startsWith('https://github.com/microsoft/PowerToys/')));
    assert.ok(links.every(node => node.target === '_blank' && node.rel === 'noopener noreferrer'));
    const resultLinks = descendants(page.node('action-results')).filter(node => node.tagName === 'A' && node.textContent.startsWith('Open GitHub result'));
    assert.equal(resultLinks.length, 2, 'result URLs are deduplicated and unrelated repositories are excluded');
    assert.equal(page.node('confirm-action').hidden, true); assert.equal(page.node('retry-action').hidden, true);
    assert.equal(page.count('webActions.submit'), 0); assert.equal(page.count('webActions.reconcile'), 0); assert.deepEqual(page.tabs, []);
  });
});

let draftRunSequence = 100;
function draftSource() {
  const runId = `00000000-0000-4000-8000-${String(++draftRunSequence).padStart(12, '0')}`;
  const proposalId = 'proposal-saved-draft';
  const sha = 'a'.repeat(40);
  const proposal = { proposalId, kind: 'create-pr', body: '', reason: 'Publish the verified candidate for review.', pullRequest: { head: 'contributor:fix-focus', base: 'main', sourceHeadSha: sha, title: 'Restore dialog focus', body: 'Preserve the verified keyboard focus fix.\n\nValidation: focused checks passed.', draft: false } };
  const run = { runId, task: { requestId: runId, actionId: 'saved-source', actionKind: 'issue-fix', repository: target.repository, target: { type: 'issue', number: target.number }, prompt: 'Fix the recorded Issue.' },
    config: { repoFolder: 'C:\\Worktrees\\task-focus', worktreeBranch: 'codex/task-focus' }, status: { state: 'succeeded', exitCode: 0 }, view: { read: true, handled: false },
    result: { schemaVersion: 3, structured: true, outcome: 'completed', phase: 'reporting', cliExitCode: 0, summary: 'Candidate completed.', report: { complete: true, rechecked: true, coverage: ['Focused source and behavior checks.'], limitations: ['No hardware testing claimed.'] },
      findings: [], nextActions: [proposal], nextSteps: [proposal], validation: [{ id: 'first', name: 'Build', status: 'passed', details: 'Saved build details.', evidence: ['Saved build evidence.'] }, { id: 'last', name: 'Focus restoration', status: 'passed', details: 'Last paged verification details.', evidence: ['Complete final evidence.'] }],
      assessment: { subject: 'local-candidate', status: 'passed', summary: 'Saved candidate checks passed.', revisionSha: sha }, artifacts: [], blockers: [], diagnostics: [], review: null } };
  const action = { ...duplicate('prepared'), kind: 'create-pr', runId, proposalId, bodyEditable: false, duplicateSuffix: undefined, commentBody: undefined, resumeRequired: undefined,
    draft: { requestId: operationId, actionId: `${runId}:${proposalId}`, kind: 'create-pr', target: structuredClone(target), pullRequest: { ...structuredClone(proposal.pullRequest), draft: true } }, sourceOrigin: `pulse-task://${runId}` };
  return { run, proposal, action, source: { runId, proposalId } };
}

test('Draft PR source waits for the complete paged report and preserves edits across refresh and a reopened page', async () => {
  const fixture = draftSource(); const pendingFinalPage = deferred(); const localStorage = new Map();
  const paged = structuredClone(fixture.run); paged.result.nextActions = []; paged.result.nextSteps = []; paged.result.validation = paged.result.validation.slice(0, 1);
  paged.resultPaging = { fingerprint: `draft-report-${fixture.source.runId}`, sections: [{ path: 'nextActions', total: 1, nextOffset: 0 }, { path: 'validation', total: 2, nextOffset: 1 }] };
  await withPage(undefined, async page => {
    assert.equal(page.node('prepare-draft-pr').disabled, true);
    assert.equal(page.node('draft-workspace').hidden, true, 'the source editor is not presented as complete while evidence is still loading');
    assert.equal(page.count('resultActions.prepare'), 0);
    pendingFinalPage.resolve({ items: [fixture.run.result.validation[1]], total: 2, nextOffset: null, fingerprint: paged.resultPaging.fingerprint }); await settle();
    assert.equal(page.node('draft-workspace').hidden, false);
    assert.equal(page.node('prepare-draft-pr').disabled, false);
    assert.match(page.node('draft-verification').textContent, /Complete final evidence/);
    assert.match(page.node('draft-pr-facts').textContent, /contributor:fix-focus/);
    assert.match(page.node('draft-pr-facts').textContent, new RegExp('a'.repeat(40)));
    assert.equal(page.node('draft-pr-facts').textContent.includes('Ready for review'), false);
    assert.match(page.node('draft-pr-body').value, /Refs https:\/\/github.com\/microsoft\/PowerToys\/issues\/6201/);
    assert.match(page.node('draft-preparation-state').textContent, /Not yet checked on GitHub/);
    await page.fill('draft-pr-title', 'My edited focus title');
    await page.fill('draft-pr-body', '  My complete description.  \n\nRefs #6201\n');
    await page.click('draft-preview-toggle');
    assert.equal(page.node('draft-content-preview').textContent, '  My complete description.  \n\nRefs #6201\n');
    await page.click('refresh');
    assert.equal(page.node('draft-pr-title').value, 'My edited focus title');
    assert.equal(page.node('draft-pr-body').value, '  My complete description.  \n\nRefs #6201\n');
    assert.equal(page.count('resultActions.prepare'), 0); assert.equal(page.count('webActions.submit'), 0);
  }, { source: fixture.source, localStorage, getRun: () => paged, resultPage: async payload => payload.path === 'nextActions' ? { items: [fixture.proposal], total: 1, nextOffset: null, fingerprint: paged.resultPaging.fingerprint } : pendingFinalPage.promise });
  await withPage(undefined, async page => {
    assert.equal(page.node('draft-pr-title').value, 'My edited focus title');
    assert.equal(page.node('draft-pr-body').value, '  My complete description.  \n\nRefs #6201\n');
    await page.fill('draft-pr-title', '');
    assert.equal(page.node('prepare-draft-pr').disabled, true);
    await page.fire('prepare-draft-pr'); assert.equal(page.count('resultActions.prepare'), 0);
  }, { source: fixture.source, localStorage, getRun: () => fixture.run });
  await withPage(undefined, async page => {
    assert.equal(page.node('draft-pr-title').value, '', 'intentionally empty edits survive a page reload');
    assert.equal(page.node('prepare-draft-pr').disabled, true);
  }, { source: fixture.source, localStorage, getRun: () => fixture.run });
});

test('explicit Draft PR preparation sends only edited content and a durable attempt, then opens the frozen confirmation', async () => {
  const fixture = draftSource(); const localStorage = new Map(); const pending = deferred();
  await withPage(undefined, async page => {
    assert.equal(page.count('resultActions.prepare'), 0); assert.equal(page.count('webActions.preview'), 0);
    await page.fill('draft-pr-title', 'The reviewed title'); await page.fill('draft-pr-body', 'Complete **authored** description.  \n');
    await page.click('prepare-draft-pr');
    assert.equal(page.node('prepare-draft-pr').disabled, true); assert.equal(page.node('draft-pr-title').disabled, true);
    await page.fire('prepare-draft-pr'); assert.equal(page.count('resultActions.prepare'), 1);
    const call = page.calls.find(call => call.type === 'resultActions.prepare');
    assert.deepEqual(Object.keys(call.payload).sort(), ['attemptId', 'content', 'proposalId', 'runId']);
    assert.deepEqual(call.payload.content, { title: 'The reviewed title', body: 'Complete **authored** description.  \n' });
    assert.equal(call.payload.runId, fixture.source.runId); assert.equal(call.payload.proposalId, fixture.source.proposalId);
    assert.match(call.payload.attemptId, /^[a-f0-9-]{36}$/i);
    pending.resolve(summary(fixture.action)); await settle();
    assert.equal(globalThis.location.href, `chrome-extension://test/action.html?operationId=${operationId}`);
    assert.equal(page.count('webActions.submit'), 0, 'preparation never performs the subsequent GitHub write');
    assert.ok([...localStorage.keys()].some(key => key.endsWith(':attempt')));
  }, { source: fixture.source, localStorage, getRun: () => fixture.run, prepare: () => pending.promise });
});

test('a failed full-report read retains source edits and blocks preparation until a successful refresh', async () => {
  const fixture = draftSource(); let failRead = false;
  await withPage(undefined, async page => {
    await page.fill('draft-pr-title', 'Do not lose this title'); await page.fill('draft-pr-body', 'Retained description.');
    failRead = true; await page.click('refresh');
    assert.equal(page.node('prepare-draft-pr').disabled, true); assert.equal(page.node('draft-pr-title').disabled, true);
    assert.equal(page.node('draft-pr-title').value, 'Do not lose this title'); assert.match(page.node('error').textContent, /report transport failed/);
    await page.fire('prepare-draft-pr'); assert.equal(page.count('resultActions.prepare'), 0);
    failRead = false; await page.click('refresh');
    assert.equal(page.node('prepare-draft-pr').disabled, false); assert.equal(page.node('draft-pr-title').value, 'Do not lose this title');
  }, { source: fixture.source, getRun: () => { if (failRead) throw new Error('report transport failed'); return fixture.run; } });
});

test('a lost preparation response reuses the durable attempt after reopening and never submits to GitHub', async () => {
  const fixture = draftSource(); const localStorage = new Map(); let firstAttempt;
  await withPage(undefined, async page => {
    await page.fill('draft-pr-title', 'Recover this draft'); await page.fill('draft-pr-body', 'Recovery description.');
    await page.click('prepare-draft-pr');
    assert.match(page.node('error').textContent, /preparation response lost/);
    firstAttempt = page.calls.find(call => call.type === 'resultActions.prepare').payload.attemptId;
    assert.equal(page.node('draft-pr-title').value, 'Recover this draft'); assert.equal(page.count('webActions.submit'), 0);
  }, { source: fixture.source, localStorage, getRun: () => fixture.run, prepare: () => { throw new Error('preparation response lost'); } });
  await withPage(undefined, async page => {
    assert.equal(page.node('draft-pr-title').value, 'Recover this draft');
    await page.click('prepare-draft-pr');
    const payload = page.calls.find(call => call.type === 'resultActions.prepare').payload;
    assert.equal(payload.attemptId, firstAttempt); assert.deepEqual(payload.content, { title: 'Recover this draft', body: 'Recovery description.' });
    assert.equal(page.count('webActions.submit'), 0);
  }, { source: fixture.source, localStorage, getRun: () => fixture.run, prepare: () => summary(fixture.action) });
});

test('missing saved source branches remain explicit gaps while title and description stay editable', async () => {
  const fixture = draftSource(); delete fixture.run.result.nextActions[0].pullRequest.base; fixture.run.result.nextActions[0].pullRequest.head = '';
  await withPage(undefined, async page => {
    assert.equal(page.node('prepare-draft-pr').disabled, true);
    assert.equal(page.node('draft-pr-requirements').hidden, false);
    assert.match(page.node('draft-pr-facts').textContent, /Source branchNot recordedBase branchNot recorded/);
    assert.match(page.node('draft-pr-blockers').textContent, /verified source branch revision/);
    await page.fill('draft-pr-title', 'Keep this draft while preparing the source');
    await page.fire('prepare-draft-pr');
    assert.equal(page.count('resultActions.prepare'), 0); assert.equal(page.count('webActions.submit'), 0);
    assert.equal(page.node('draft-pr-title').value, 'Keep this draft while preparing the source');
  }, { source: fixture.source, getRun: () => fixture.run });
});

test('an unknown Draft PR keeps both source and prepared payload immutable and only opens or reconciles the same operation', async () => {
  const fixture = draftSource(); const unknown = { ...fixture.action, status: 'unknown', canSubmit: false, retryAllowed: false };
  await withPage(undefined, async page => {
    assert.equal(page.node('draft-pr-title').disabled, true); assert.equal(page.node('draft-pr-body').disabled, true);
    assert.equal(page.node('prepare-draft-pr').textContent, 'Review existing action');
    await page.click('prepare-draft-pr');
    assert.equal(globalThis.location.href, `chrome-extension://test/action.html?operationId=${operationId}`);
    assert.equal(page.count('resultActions.prepare'), 0); assert.equal(page.count('webActions.submit'), 0);
  }, { source: fixture.source, getRun: () => fixture.run, resultActions: [summary(unknown)] });
  await withPage(unknown, async page => {
    assert.equal(page.node('confirm-action').hidden, true); assert.equal(page.node('edit-action').hidden, true); assert.equal(page.node('retry-action').hidden, true);
    assert.equal(page.node('action-next-step').textContent, 'Check GitHub result');
    assert.equal(descendants(page.node('action-draft')).some(node => ['INPUT', 'TEXTAREA'].includes(node.tagName)), false);
    const frozenText = page.node('action-draft').textContent;
    await page.fire('confirm-action'); await page.fire('edit-action'); await page.fire('retry-action');
    assert.equal(page.count('webActions.submit'), 0); assert.equal(page.count('webActions.cancel'), 0); assert.equal(page.count('resultActions.prepare'), 0);
    await page.click('reconcile-action');
    assert.equal(page.count('webActions.reconcile'), 1); assert.equal(page.node('action-draft').textContent, frozenText);
  }, { reconcile: () => summary(unknown) });
});

test('editing a prepared Draft PR preserves its exact content and cancels it before returning to the source editor', async () => {
  const fixture = draftSource(); const localStorage = new Map();
  fixture.action.draft.pullRequest.title = 'Previously edited title'; fixture.action.draft.pullRequest.body = '  Previous complete text.  \n';
  await withPage(fixture.action, async page => {
    assert.equal(page.node('edit-action').hidden, false);
    await page.click('edit-action');
    assert.equal(page.count('webActions.cancel'), 1); assert.equal(page.count('resultActions.prepare'), 0); assert.equal(page.count('webActions.submit'), 0);
    assert.equal(globalThis.location.href, `chrome-extension://test/action.html?${new URLSearchParams(fixture.source)}`);
  }, { localStorage, cancel: () => summary({ ...fixture.action, status: 'cancelled', canSubmit: false, retryAllowed: true }) });
  await withPage(undefined, async page => {
    assert.equal(page.node('draft-pr-title').value, 'Previously edited title');
    assert.equal(page.node('draft-pr-body').value, '  Previous complete text.  \n');
    assert.equal(page.node('draft-pr-title').disabled, false);
  }, { localStorage, source: fixture.source, getRun: () => fixture.run, resultActions: [summary({ ...fixture.action, status: 'cancelled', retryAllowed: true })] });
});
