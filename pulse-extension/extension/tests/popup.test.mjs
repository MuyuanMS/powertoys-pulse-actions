import test from 'node:test';
import assert from 'node:assert/strict';
import { setImmediate as nextTurn } from 'node:timers/promises';
import { readFile } from 'node:fs/promises';

class Node {
  children = []; attributes = new Map(); listeners = new Map(); dataset = {}; hidden = false; className = ''; _text = ''; value = ''; parentNode = null;
  constructor(tag = 'div') { this.tagName = tag.toUpperCase(); }
  set textContent(value) { this._text = String(value); for (const child of this.children) child.parentNode = null; this.children = []; }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  append(...children) { for (const child of children) { child.remove(); child.parentNode = this; this.children.push(child); } }
  prepend(...children) { for (const child of children.reverse()) { child.remove(); child.parentNode = this; this.children.unshift(child); } }
  replaceChildren(...children) { for (const child of this.children) child.parentNode = null; this._text = ''; this.children = []; this.append(...children); }
  insertBefore(child, next) { child.remove(); child.parentNode = this; const index = next ? this.children.indexOf(next) : this.children.length; this.children.splice(index, 0, child); }
  remove() { if (this.parentNode) this.parentNode.children.splice(this.parentNode.children.indexOf(this), 1); this.parentNode = null; }
  get isConnected() { return this === globalThis.document?.body || Boolean(this.parentNode?.isConnected); }
  setAttribute(key, value) { this.attributes.set(key, String(value)); }
  getAttribute(key) { return this.attributes.get(key) ?? null; }
  removeAttribute(key) { this.attributes.delete(key); }
  addEventListener(type, callback) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), callback]); }
  get classList() { return { contains: name => this.className.split(' ').includes(name), add: name => { this.className += ` ${name}`; }, remove: name => { this.className = this.className.split(' ').filter(value => value !== name).join(' '); }, toggle: (name, value) => { const selected = value ?? !this.className.split(' ').includes(name); this.className = [...this.className.split(' ').filter(item => item && item !== name), ...(selected ? [name] : [])].join(' '); return selected; } }; }
  get parentElement() { return this.parentNode; }
  querySelectorAll(selector) { return descendants(this).slice(1).filter(node => selector.split(',').some(part => matches(node, part.trim()))); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
  closest(selector) { return matches(this, selector) ? this : this.parentNode?.closest(selector) ?? null; }
  focus() { globalThis.document.activeElement = this; }
}
const descendants = node => [node, ...node.children.flatMap(descendants)];
const settle = async () => { await nextTurn(); await nextTurn(); await nextTurn(); };
const matches = (node, selector) => {
  if (selector.startsWith('#')) return node.id === selector.slice(1);
  if (selector.startsWith('.')) return node.className.split(' ').includes(selector.slice(1));
  if (/^\[[\w-]+\]$/.test(selector)) return node.attributes.has(selector.slice(1, -1));
  return node.tagName === selector.toUpperCase();
};

let popupImports = 0;
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const popupRun = (id, state = 'succeeded', target = { type: 'pr', number: 100 + id }) => ({
  runId: `popup-run-${id}`, task: { actionId: `Review task ${id}`, actionKind: target?.type === 'issue' ? 'issue-fix' : 'pr-review', repository: 'microsoft/PowerToys', ...(target ? { target } : {}), ...(target?.type === 'pr' ? { expectedHeadSha: 'a'.repeat(40) } : {}) },
  config: { agent: 'codex', model: null, repoFolder: 'C:\\Demo\\worktree' },
  status: { state, createdAt: '2026-09-18T00:00:00Z', updatedAt: `2026-09-18T01:${String(59 - id).padStart(2, '0')}:00Z`, ...(!['running', 'accepted'].includes(state) ? { endedAt: `2026-09-18T01:${String(59 - id).padStart(2, '0')}:00Z` } : {}), ...(state === 'failed' ? { error: { code: 'CLI_EXECUTION_FAILED', message: 'Agent process exited before its final report.' } } : {}) },
  view: { read: false, handled: false }, result: { schemaVersion: 3, outcome: 'completed', structured: true, summary: `Saved review conclusion ${id}.`, report: { complete: true, rechecked: true, coverage: ['Saved coverage'], limitations: [] }, findings: [], nextActions: [], validation: [], artifacts: [], diagnostics: [], nextSteps: [], blockers: [] },
});

function parsePopup(html) {
  const root = new Node('document'); const nodes = new Map(); const stack = [root];
  const voidTags = new Set(['meta', 'link', 'input', 'br', 'hr', 'img']);
  const decode = text => text.replaceAll('&amp;', '&').replaceAll('&lt;', '<').replaceAll('&gt;', '>').replaceAll('&quot;', '"').replaceAll('&#39;', "'");
  for (const match of html.matchAll(/<!--[\s\S]*?-->|<![^>]*>|<\/?([a-z][\w-]*)\b([^>]*)>|([^<]+)/gi)) {
    if (match[3] !== undefined) { if (match[3].trim()) { const text = new Node('text'); text.textContent = decode(match[3]); stack.at(-1).append(text); } continue; }
    if (!match[1]) continue;
    const tag = match[1].toLowerCase();
    if (match[0].startsWith('</')) { const index = stack.findLastIndex(node => node.tagName === tag.toUpperCase()); if (index > 0) stack.length = index; continue; }
    const node = new Node(tag);
    for (const attribute of match[2].matchAll(/([\w-]+)(?:="([^"]*)"|='([^']*)')?/g)) {
      const name = attribute[1]; const value = decode(attribute[2] ?? attribute[3] ?? ''); node.setAttribute(name, value);
      if (name === 'id') { node.id = value; nodes.set(value, node); }
      else if (name === 'class') node.className = value;
      else if (['href', 'target', 'rel', 'type', 'value'].includes(name)) node[name] = value;
      else if (name === 'hidden' || name === 'disabled' || name === 'checked') node[name] = true;
    }
    stack.at(-1).append(node); if (!voidTags.has(tag) && !match[0].endsWith('/>')) stack.push(node);
  }
  return { nodes, body: descendants(root).find(node => node.tagName === 'BODY') };
}

async function withPopup(verify, { expanded = false, catalog = [], list, connection } = {}) {
  const globals = ['document', 'location', 'history', 'window', 'HTMLElement', 'chrome', 'setInterval'];
  const originals = new Map(globals.map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  const html = await readFile(new URL('../public/popup.html', import.meta.url), 'utf8');
  const { nodes, body } = parsePopup(html); const events = new Map(); let poll;
  const page = {
    nodes, body, calls: [], tabs: [], catalog, disconnected: false, partial: false,
    node(id) { return nodes.get(id) ?? descendants(body).find(node => node.id === id); },
    async poll() { poll(); await settle(); },
    async fire(node, event = 'click') { assert.ok(node?.listeners.has(event), 'the production control has an event handler'); for (const listener of node.listeners.get(event)) listener({ preventDefault() {} }); await settle(); },
    async click(node) { assert.notEqual(node.disabled, true, 'the clicked control is enabled'); await this.fire(node); },
    listResponse(payload) {
      if (this.disconnected) throw new Error('Saved tasks are temporarily unavailable.');
      const finished = this.catalog.filter(run => !['accepted', 'running'].includes(run.status.state));
      const active = this.catalog.filter(run => ['accepted', 'running'].includes(run.status.state));
      let selected = payload.view === 'tasks' ? active : finished;
      if (payload.view === 'prs') selected = selected.filter(run => run.task.target?.type === 'pr');
      if (payload.view === 'issues') selected = selected.filter(run => run.task.target?.type === 'issue');
      const offset = payload.cursor ? Number(payload.cursor) : 0; const visible = selected.slice(offset, offset + payload.limit);
      return { runs: structuredClone(visible), nextCursor: offset + visible.length < selected.length ? String(offset + visible.length) : null,
        runningCount: active.length, unreadCount: finished.filter(run => !run.view.read).length, prCount: finished.filter(run => run.task.target?.type === 'pr').length, issueCount: finished.filter(run => run.task.target?.type === 'issue').length,
        ...(this.partial ? { errors: [{ runId: 'unreadable', error: { code: 'RECORD_UNREADABLE' } }] } : {}) };
    },
  };
  try {
    globalThis.HTMLElement = Node;
    globalThis.document = { body, activeElement: body, getElementById: id => page.node(id), createElement: tag => new Node(tag),
      createElementNS: (namespaceURI, tag) => Object.assign(new Node(tag), { namespaceURI }), createTextNode: text => { const node = new Node('text'); node.textContent = text; return node; },
      querySelectorAll: selector => body.querySelectorAll(selector),
      querySelector: selector => selector === 'body > .topbar' ? body.children.find(node => node.classList.contains('topbar')) : selector === 'body > main' ? body.children.find(node => node.tagName === 'MAIN') : body.querySelector(selector) };
    globalThis.location = new URL(`chrome-extension://test/popup.html${expanded ? '?expanded=1' : ''}`);
    globalThis.history = { replaceState(_state, _title, url) { location.href = new URL(url, location.href).href; } };
    globalThis.window = { addEventListener(event, listener) { events.set(event, listener); } };
    globalThis.setInterval = callback => { poll = callback; return 1; };
    globalThis.chrome = { tabs: { create: async tab => { page.tabs.push(structuredClone(tab)); return {}; } }, runtime: {
      getURL: path => `chrome-extension://test/${path}`, openOptionsPage: async () => { throw new Error('The compact popup does not expose Settings.'); },
      sendMessage: async message => {
        page.calls.push(structuredClone(message)); assert.equal(message.channel, 'pulse-ui');
        if (message.type === 'connection.status') return { ok: true, data: connection ? await connection(page) : { state: page.disconnected ? 'disconnected' : 'connected' } };
        assert.equal(message.type, 'tasks.list', 'popup interactions must never start or publish work');
        try { return { ok: true, data: list ? await list(message.payload, page) : page.listResponse(message.payload) }; }
        catch (error) { return { ok: false, error: { code: 'HOST_UNAVAILABLE', message: error.message } }; }
      },
    } };
    await import(new URL(`../src/popup.ts?popup-test=${++popupImports}`, import.meta.url)); await settle(); await verify(page);
  } finally {
    for (const key of globals) { const descriptor = originals.get(key); if (descriptor) Object.defineProperty(globalThis, key, descriptor); else delete globalThis[key]; }
  }
}

test('task center preserves run identity, filters and last snapshot while a task finishes or the Host disconnects', async () => {
  const globals = ['document', 'location', 'history', 'window', 'HTMLElement', 'chrome', 'setInterval'];
  const originals = new Map(globals.map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  const html = await readFile(new URL('../public/popup.html', import.meta.url), 'utf8');
  const nodes = new Map([...html.matchAll(/id="([^"]+)"/g)].map(([, id]) => [id, new Node()]));
  const body = new Node('body'); const main = nodes.get('task-content'); const header = new Node('header');
  main.append(...[...nodes].filter(([id]) => id !== 'task-content').map(([, node]) => node)); body.append(main, header);
  let poll; let failed = false; let terminalState = 'failed'; let disconnected = false; let partial = false; let extraHistory = []; const calls = [];
  const run = {
    runId: 'kept-failure', task: { actionId: 'Investigate the failed review', actionKind: 'pr-review', repository: 'microsoft/PowerToys', target: { type: 'pr', number: 123 }, expectedHeadSha: 'a'.repeat(40) },
    config: { agent: 'codex', model: 'requested-model', reasoningEffort: 'high', repoFolder: 'C:\\Demo\\task' },
    status: { state: 'running', createdAt: '2026-09-10T00:00:00Z', updatedAt: '2026-09-10T00:00:00Z' }, view: { read: false, handled: false },
  };
  try {
    globalThis.HTMLElement = Node;
    globalThis.document = {
      body, activeElement: body, getElementById: id => nodes.get(id),
      createElement: tag => new Node(tag), createElementNS: (namespaceURI, tag) => Object.assign(new Node(tag), { namespaceURI }),
      createTextNode: text => { const node = new Node('text'); node.textContent = text; return node; },
      querySelectorAll: () => [],
      querySelector: selector => selector === 'body > .topbar' ? header : selector === 'body > main' ? main : null,
    };
    globalThis.location = { href: 'chrome-extension://test/popup.html?expanded=1', pathname: '/popup.html' };
    globalThis.history = { replaceState(_state, _title, url) { location.href = new URL(url, location.href).href; } };
    globalThis.window = { addEventListener() {} };
    globalThis.setInterval = callback => { poll = callback; return 1; };
    globalThis.chrome = { runtime: {
      getURL: path => `chrome-extension://test/${path}`,
      sendMessage: async message => {
        calls.push(message);
        if (message.type === 'connection.status') return { ok: true, data: { state: disconnected ? 'disconnected' : 'connected' } };
        assert.equal(message.type, 'tasks.list');
        if (disconnected) return { ok: false, error: { code: 'HOST_UNAVAILABLE', message: 'The Host is unavailable.' } };
        const current = structuredClone(run);
        if (failed) current.status = { ...current.status, state: terminalState, endedAt: '2026-09-10T00:00:22Z', updatedAt: '2026-09-10T00:00:22Z', ...(terminalState === 'failed' ? { error: { code: 'CLI_EXECUTION_FAILED', message: 'CLI exited after 22 seconds.', guidance: 'Inspect the execution logs.' } } : {}) };
        const active = message.payload.view === 'tasks';
        let visible = [...(!failed && active || failed && !active ? [current] : []), ...(!active ? extraHistory : [])];
        if (message.payload.view === 'prs') visible = visible.filter(item => item.task.target?.type === 'pr');
        if (message.payload.view === 'issues') visible = visible.filter(item => item.task.target?.type === 'issue');
        return { ok: true, data: { runs: visible, nextCursor: null, runningCount: failed ? 0 : 1, prCount: failed ? 1 : 0, issueCount: 0, unreadCount: failed ? 1 : 0, ...(partial ? { errors: [{ runId: 'unreadable-record', error: { code: 'INVALID_RESULT' } }] } : {}) } };
      },
    } };
    await import('../src/popup.ts'); await settle();
    assert.match(nodes.get('runs').textContent, /PR review · #123RunningInvestigate the failed review/);
    assert.equal(nodes.get('page-title').textContent, 'Tasks');
    assert.equal(nodes.get('counts').textContent, '1 running · 0 unread results');
    const originalRow = nodes.get('runs').children[0];
    const title = descendants(originalRow).find(node => node.className === 'task-row-title'); title.focus();
    failed = true; poll(); await settle();
    assert.equal(nodes.get('error').hidden, true);
    assert.equal(nodes.get('runs').children.length, 0);
    assert.equal(nodes.get('recent-runs').children[0], originalRow, 'completion moves the same row and preserves the same task link');
    assert.equal(document.activeElement, title);
    assert.match(nodes.get('recent-runs').textContent, /PR review · #123Failed/);
    assert.match(nodes.get('recent-runs').textContent, /CLI_EXECUTION_FAILED/);
    assert.match(nodes.get('recent-runs').textContent, /Unread resultUnhandled locally/);
    assert.doesNotMatch(nodes.get('recent-runs').textContent, /requested-model|C:\\Demo/);
    const link = descendants(nodes.get('recent-runs')).find(node => node.textContent === 'Review task');
    assert.equal(link.href, 'chrome-extension://test/details.html?runId=kept-failure#result-workspace');
    assert.ok(calls.some(call => call.payload?.view === 'history' && call.payload.order === 'finished' && call.payload.limit === 5));

    nodes.get('scope-filter').value = 'history'; nodes.get('scope-filter').listeners.get('change')[0](); await settle();
    nodes.get('target-filter').value = 'pr'; nodes.get('target-filter').listeners.get('change')[0](); await settle();
    assert.equal(nodes.get('page-title').textContent, 'All finished runs');
    assert.equal(nodes.get('recent-section').hidden, true);
    assert.ok(calls.some(call => call.payload?.view === 'prs'));
    nodes.get('task-search').value = 'kept-failure'; nodes.get('task-search').listeners.get('input')[0]();
    assert.match(location.href, /target=pr/); assert.match(location.href, /q=kept-failure/);
    const snapshotRow = nodes.get('runs').children[0];
    disconnected = true; poll(); await settle();
    assert.equal(nodes.get('runs').children[0], snapshotRow, 'disconnect retains the last successfully loaded records');
    assert.equal(nodes.get('snapshot-message').hidden, false); assert.match(nodes.get('snapshot-text').textContent, /last loaded records/);
    assert.equal(nodes.get('task-search').value, 'kept-failure');

    disconnected = false; terminalState = 'succeeded'; partial = true;
    run.result = { schemaVersion: 2, outcome: 'blocked', phase: 'setup', summary: 'A test device is required.', diagnostics: [], findings: [], validation: [], nextActions: [], blockers: [], nextSteps: [] };
    poll(); await settle();
    assert.match(nodes.get('runs').textContent, /Finished/); assert.match(nodes.get('runs').textContent, /Phase: Setup/);
    assert.match(nodes.get('runs').textContent, /A test device is required/); assert.match(nodes.get('runs').textContent, /Outcome: blocked/);
    assert.equal(nodes.get('record-errors').hidden, false); assert.match(nodes.get('record-error-details').textContent, /unreadable-record/);
    assert.doesNotMatch(nodes.get('runs').textContent, /Completed|Passed/);

    extraHistory = [{ ...structuredClone(run), runId: 'saved-targetless', task: { actionId: 'Original saved task', actionKind: 'issue-fix', repository: 'microsoft/PowerToys' }, status: { ...run.status, state: 'failed' } }];
    nodes.get('clear-filters').listeners.get('click')[0](); await settle();
    assert.match(nodes.get('runs').textContent, /Unlinked historical record/);
    assert.doesNotMatch(html, /Legacy|legacy/);
    assert.equal(calls.filter(call => call.type !== 'tasks.list' && call.type !== 'connection.status').length, 0, 'opening/filtering/polling the task center never changes run state or submits a task');
  } finally {
    for (const key of globals) {
      const descriptor = originals.get(key);
      if (descriptor) Object.defineProperty(globalThis, key, descriptor); else delete globalThis[key];
    }
  }
});

test('compact popup places account-independent navigation and Refresh together without Settings or an updated timestamp', async () => {
  await withPopup(async page => {
    const { body } = page; const footer = page.node('task-popup-footer');
    const open = footer.querySelector('[data-panel]'); const refresh = page.node('refresh');
    assert.equal(open.textContent.trim(), 'Open extension');
    assert.equal(open.parentNode, refresh.parentNode, 'the primary entry and Refresh form one footer control group');
    assert.equal(open.parentNode.children.indexOf(refresh), open.parentNode.children.indexOf(open) + 1, 'Refresh is adjacent to Open extension');
    assert.equal(page.node('task-topbar').isConnected, false, 'the full-page task header is removed in the compact popup');
    assert.equal(page.node('updated').isConnected, false);
    assert.equal(body.querySelectorAll('[data-options]').length, 0);
    assert.equal(page.node('counts').parentNode, page.node('page-title').parentNode, 'Tasks and global counts share the compact title row');
    assert.doesNotMatch(body.textContent, /\bUpdated\b/);
    const pulse = descendants(footer).find(node => node.tagName === 'A' && node.textContent.trim() === 'Open Pulse');
    assert.ok(pulse); assert.equal(pulse.target, '_blank'); assert.match(pulse.rel, /noopener/);
    const locationBefore = location.href; await page.click(open);
    assert.equal(page.tabs.length, 1); assert.match(page.tabs[0].url, /popup\.html\?expanded=1/);
    assert.equal(location.href, locationBefore, 'opening the full extension does not replace the toolbar popup');
  }, { catalog: [popupRun(1), popupRun(2)] });
});

test('compact rows are a bounded preview and View all displays a total only when the complete finished catalog is known', async () => {
  const catalog = [popupRun(1), popupRun(2), popupRun(3, 'succeeded', { type: 'issue', number: 203 }), popupRun(4, 'succeeded', null)];
  await withPopup(async page => {
    assert.equal(page.node('recent-runs').children.length, 2, 'only two recent results are displayed');
    assert.match(page.node('history-link').textContent, /^View all\s*\(4\)$/);
    assert.notEqual(page.node('history-link').textContent, 'View all (3)', 'the total includes finished records without a target');
    assert.equal(page.node('history-link').target, '_blank'); assert.match(page.node('history-link').rel, /noopener/);
    const href = new URL(page.node('history-link').href); assert.equal(href.searchParams.get('expanded'), '1'); assert.equal(href.searchParams.get('view'), 'history');
    assert.ok(page.calls.some(call => call.type === 'tasks.list' && call.payload.view === 'history' && call.payload.limit === 5));
  }, { catalog });
  await withPopup(async page => {
    assert.equal(page.node('recent-runs').children.length, 2);
    assert.equal(page.node('history-link').textContent.trim(), 'View all', 'a five-row response with a next cursor cannot establish the global total');
  }, { catalog: Array.from({ length: 8 }, (_, index) => popupRun(index + 1)) });
  await withPopup(async page => {
    assert.equal(page.node('history-link').textContent.trim(), 'View all (5)', 'five is a valid total when the Host explicitly establishes that no later page exists');
  }, { catalog: Array.from({ length: 5 }, (_, index) => popupRun(index + 1)) });
  await withPopup(async page => {
    assert.equal(page.node('history-link').textContent.trim(), 'View all', 'unreadable records make an apparent short-page total incomplete');
  }, { catalog, list: (payload, page) => { page.partial = true; return page.listResponse(payload); } });
});

test('zero active tasks use a plain statement while completed P1 findings and task execution failures stay distinct', async () => {
  const finding = popupRun(1); finding.result.summary = 'One confirmed P1 finding. Tests: 19 passed and 1 failed.';
  finding.result.assessment = { subject: 'original-pr', status: 'failed', summary: 'The regression test failed.', revisionSha: 'a'.repeat(40) };
  const failed = popupRun(2, 'failed');
  await withPopup(async page => {
    const empty = page.node('primary-empty'); assert.equal(empty.hidden, false); assert.match(empty.textContent.trim(), /^No tasks running\.?$/);
    assert.equal(empty.tagName, 'P'); assert.equal(page.node('empty-state').hidden, true);
    assert.equal(page.node('runs').children.length, 0);
    const rows = page.node('recent-runs').children; assert.equal(rows.length, 2);
    const completed = descendants(rows[0]); const execution = completed.find(node => node.className.split(' ').includes('task-execution'));
    assert.equal(execution.textContent, 'Finished'); assert.equal(execution.classList.contains('task-error-state'), false);
    assert.match(rows[0].textContent, /P1/); assert.match(rows[0].textContent, /19 passed and 1 failed/);
    assert.ok(completed.some(node => node.tagName === 'A' && node.textContent === 'Review result' && node.href.endsWith('#result-workspace')));
    assert.ok(descendants(rows[1]).some(node => node.className.split(' ').includes('task-execution') && node.textContent === 'Failed'));
    assert.ok(descendants(rows[1]).some(node => node.tagName === 'A' && node.textContent === 'Review task'));
  }, { catalog: [finding, failed] });
});

test('compact loading and an initially unavailable Host keep counts unknown instead of inventing an empty successful snapshot', async () => {
  const pending = deferred();
  await withPopup(async page => {
    assert.equal(page.node('initial-loading').hidden, false);
    assert.match(page.node('counts').textContent, /—/); assert.doesNotMatch(page.node('counts').textContent, /0 running|0 unread/);
    assert.equal(page.node('primary-empty').hidden, true);
    assert.equal(page.node('empty-state').hidden, true);
    pending.resolve(page.listResponse({ view: 'tasks', limit: 50 })); await settle();
    assert.equal(page.node('initial-loading').hidden, true);
    assert.match(page.node('counts').textContent, /0 running/);
    assert.match(page.node('primary-empty').textContent, /No tasks running/);
  }, { list: (payload, page) => payload.view === 'tasks' ? pending.promise : page.listResponse(payload) });
  await withPopup(async page => {
    assert.match(page.node('counts').textContent, /—/); assert.doesNotMatch(page.node('counts').textContent, /0 running|0 unread/);
    assert.equal(page.node('snapshot-message').hidden, false);
    assert.equal(page.node('empty-state').hidden, true); assert.equal(page.node('primary-empty').hidden, true);
    assert.match(page.node('connection').textContent, /offline|disconnected/i);
  }, { list: () => { throw new Error('The Host is unavailable.'); }, connection: () => ({ state: 'disconnected' }) });
});

test('compact offline recovery preserves the last rows and labels their counts as last known without a timestamp', async () => {
  await withPopup(async page => {
    const rows = [...page.node('recent-runs').children];
    assert.equal(rows.length, 2); assert.doesNotMatch(page.node('counts').textContent, /Last known/);
    page.disconnected = true; await page.poll();
    assert.deepEqual(page.node('recent-runs').children, rows);
    assert.match(page.node('counts').textContent, /Last known/i);
    assert.match(page.node('counts').textContent, /2 unread/);
    assert.equal(page.node('snapshot-message').hidden, false);
    assert.match(page.node('connection').textContent, /offline|disconnected/i);
    assert.doesNotMatch(page.body.textContent, /\bUpdated\b|last loaded records from/i);
    page.disconnected = false; await page.click(page.node('refresh'));
    assert.equal(page.node('snapshot-message').hidden, true);
    assert.doesNotMatch(page.node('counts').textContent, /Last known/i);
    assert.match(page.node('connection').textContent, /connected/i);
  }, { catalog: [popupRun(1), popupRun(2)] });
});

test('manual compact Refresh coalesces clicks and polling while providing completion feedback', async () => {
  const pending = deferred(); let hold = false;
  await withPopup(async page => {
    const before = page.calls.filter(call => call.type === 'tasks.list').length;
    hold = true; await page.click(page.node('refresh'));
    assert.equal(page.node('refresh').disabled, true);
    assert.match(page.node('refresh-label').textContent, /refreshing/i);
    await page.fire(page.node('refresh')); await page.poll();
    assert.equal(page.calls.filter(call => call.type === 'tasks.list').length, before + 1, 'busy refresh does not queue duplicate reads');
    hold = false; pending.resolve(page.listResponse({ view: 'tasks', limit: 50 })); await settle();
    assert.equal(page.node('refresh').disabled, false);
    assert.equal(page.node('refresh-label').textContent, 'Refresh');
    assert.match(page.node('task-announcement').textContent, /refreshed/i);
    assert.doesNotMatch(page.body.textContent, /\bUpdated\b/);
    assert.equal(page.calls.filter(call => !['tasks.list', 'connection.status'].includes(call.type)).length, 0);
  }, { catalog: [popupRun(1)], list: (payload, page) => hold && payload.view === 'tasks' ? pending.promise : page.listResponse(payload) });
});

test('compact active preview limits rows without turning the global running count into the displayed count', async () => {
  await withPopup(async page => {
    assert.equal(page.node('runs').children.length, 2);
    assert.match(page.node('counts').textContent, /3 running/);
    assert.equal(page.node('primary-empty').hidden, true);
    for (const row of page.node('runs').children) {
      assert.ok(descendants(row).some(node => node.tagName === 'A' && node.textContent === 'View activity' && node.href.endsWith('#execution-logs')));
    }
  }, { catalog: [popupRun(1, 'running'), popupRun(2, 'accepted'), popupRun(3, 'running'), popupRun(4)] });
});

test('expanded Tasks retains its full header, Settings, update time and complete recent snapshot', async () => {
  await withPopup(async page => {
    const header = page.node('task-topbar');
    assert.equal(header.isConnected, true); assert.equal(page.node('updated').isConnected, true);
    assert.match(page.node('updated').textContent, /^Updated /);
    assert.equal(header.querySelectorAll('[data-options]').length, 1);
    assert.ok(descendants(header).includes(page.node('refresh')), 'expanded Refresh remains in its full header');
    assert.notEqual(page.node('refresh').parentNode, page.node('task-popup-actions'));
    for (const id of ['target-filter', 'scope-filter', 'type-filter', 'task-search']) assert.equal(page.node(id).isConnected, true, id);
    assert.equal(page.node('recent-runs').children.length, 4, 'expanded Tasks is not limited to the compact two-row preview');
    assert.equal(page.node('history-link').textContent.trim(), 'Open all history');
    assert.equal(page.body.classList.contains('expanded'), true);
  }, { expanded: true, catalog: [popupRun(1), popupRun(2), popupRun(3), popupRun(4)] });
});
