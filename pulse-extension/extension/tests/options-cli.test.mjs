import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { setImmediate as nextTurn } from 'node:timers/promises';

class Node {
  children = []; attributes = new Map(); listeners = new Map(); dataset = {}; hidden = false; disabled = false; checked = false; selected = false; className = ''; _text = ''; _value = '';
  constructor(tag = 'div') { this.tagName = tag.toUpperCase(); }
  set textContent(value) { this._text = String(value); this.children = []; }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  set value(value) {
    this._value = String(value);
    if (this.tagName === 'SELECT') {
      let found = false;
      for (const option of this.children) {
        option.selected = !found && option.value === this._value;
        found ||= option.selected;
      }
    }
  }
  get value() { return this.tagName === 'SELECT' ? this.children.find(option => option.selected)?.value ?? '' : this._value; }
  append(...children) {
    this.children.push(...children);
    // A single-select initially selects its first available option. Explicit .value can select a disabled saved option.
    if (this.tagName === 'SELECT' && !this.children.some(option => option.selected)) {
      const first = this.children.find(option => !option.disabled);
      if (first) first.selected = true;
    }
  }
  prepend(...children) { this.children.unshift(...children); }
  replaceChildren(...children) { this._text = ''; this.children = []; this.append(...children); }
  setAttribute(key, value) { this.attributes.set(key, value); }
  removeAttribute(key) { this.attributes.delete(key); }
  addEventListener(type, callback) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), callback]); }
  focus() { this.focused = true; }
  get childElementCount() { return this.children.length; }
}

test('Settings preserves explicit CLI choices and unsaved edits through discovery, tests the current path, and saves only on submit', async () => {
  const html = await readFile(new URL('../public/options.html', import.meta.url), 'utf8');
  const nodes = new Map([...html.matchAll(/<([a-z][\w-]*)\b([^>]*\bid="([^"]+)"[^>]*)>/gi)].map(([, tag, attributes, id]) => {
    const node = new Node(tag);
    node.hidden = /\bhidden\b/.test(attributes); node.disabled = /\bdisabled\b/.test(attributes);
    node.className = /\bclass="([^"]*)"/.exec(attributes)?.[1] ?? '';
    return [id, node];
  }));
  for (const [, id, contents] of html.matchAll(/<select\b[^>]*\bid="([^"]+)"[^>]*>([\s\S]*?)<\/select>/gi)) {
    for (const [, attributes, label] of contents.matchAll(/<option\b([^>]*)>([\s\S]*?)<\/option>/gi)) {
      const option = new Node('option'); option.value = /\bvalue="([^"]*)"/.exec(attributes)?.[1] ?? label;
      option.textContent = label; option.disabled = /\bdisabled\b/.test(attributes); option.selected = /\bselected\b/.test(attributes);
      nodes.get(id).append(option);
    }
  }
  const body = new Node('body'); const main = new Node('main'); const header = new Node('header');
  const globals = ['document', 'location', 'chrome', 'sessionStorage', 'fetch'];
  const originals = new Map(globals.map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  const session = new Map(); const calls = [];
  const first = {
    path: 'C:\\Fixture\\npm\\node_modules\\codex\\codex.exe', resolvedPath: 'C:\\Fixture\\npm\\node_modules\\codex\\codex.exe',
    aliases: ['C:\\Fixture\\shim\\codex.exe'], available: true, version: '0.153.4', source: 'npm',
  };
  const chosen = { path: 'D:\\Fixture\\Codex\\codex.exe', resolvedPath: 'D:\\Fixture\\Codex\\codex.exe', available: true, version: '0.153.4', source: 'Custom location' };
  const incompatible = { path: 'C:\\Fixture\\OldCodex\\codex.exe', available: false, version: '0.145.0', source: 'Standalone history', error: { code: 'CLI_INCOMPATIBLE', message: 'Required exec interface is missing.' } };
  const unknown = { path: 'C:\\Fixture\\UnknownVersion\\codex.exe', available: false, version: null, source: 'PATH', error: { code: 'VERSION_UNKNOWN', message: 'Version output could not be read.' } };
  const copilot = { path: 'C:\\Fixture\\Copilot\\copilot.exe', available: true, version: '1.0.83', source: 'PATH' };
  let config = {
    agent: 'codex', cliSelections: { codex: '', copilot: '' },
    agentDefaults: { codex: { model: 'gpt-5.3-codex', reasoningEffort: 'medium' }, copilot: { model: '', reasoningEffort: '' } },
    permission: 'workspace-write', mainRepoFolder: 'C:\\Fixture\\PowerToys', worktreeRoot: 'C:\\Fixture\\worktrees', githubAccount: 'fixture-reviewer',
    prPrompt: 'review.prompt.md', issuePrompt: 'fix.prompt.md', e2ePrompt: 'e2e.prompt.md', reproductionPrompt: '',
  };
  let inventory = {
    installations: { codex: [first, chosen, incompatible, unknown], copilot: [copilot] },
    selections: { codex: first.resolvedPath, copilot: copilot.path },
  };
  let unsupported = false;
  let accountFailure = false;
  let configGetFailure = false;
  let saveMode = 'legacy';
  let finishReviewPreview;
  const prompts = [
    ['review.prompt.md', 'Review', 'pr'], ['review-alt.prompt.md', 'Alternate review', 'pr'],
    ['fix.prompt.md', 'Fix', 'issue'], ['fix-alt.prompt.md', 'Alternate fix', 'issue'],
    ['e2e.prompt.md', 'E2E', 'pr'], ['e2e-alt.prompt.md', 'Alternate E2E', 'pr'],
    ['reproduce.prompt.md', 'Reproduce', 'issue'], ['powertoys-issue-reproduction-setup.prompt.md', 'Default reproduction', 'issue'],
  ].map(([name, title, appliesTo]) => ({ name, title, appliesTo, actionKind: name.startsWith('review') ? 'pr-review' : name.startsWith('fix') ? 'issue-fix' : name.startsWith('e2e') ? 'e2e' : 'reproduction-setup', sha: 'a'.repeat(64) }));
  const formIds = ['codex-installation', 'copilot-installation', 'agent', 'codex-default-model', 'codex-default-effort', 'copilot-default-model', 'copilot-default-effort', 'permission', 'main-repo-folder', 'worktree-root', 'github-account', 'pr-prompt', 'issue-prompt', 'e2e-prompt', 'reproduction-prompt'];
  const formValues = () => Object.fromEntries(formIds.map(id => [id, nodes.get(id).value]));
  const callsOf = type => calls.filter(call => call.type === type);
  const dispatch = (id, type) => {
    const handlers = nodes.get(id).listeners.get(type);
    assert.ok(handlers?.length, `${id} has a ${type} handler`);
    for (const handler of handlers) handler({ preventDefault() {} });
  };
  const edit = (id, value) => { nodes.get(id).value = value; if (nodes.get(id).listeners.has('change')) dispatch(id, 'change'); };
  const refresh = async () => { dispatch('refresh-installations', 'click'); await nextTurn(); };
  try {
    globalThis.document = {
      body, getElementById: id => nodes.get(id),
      createElement: tag => new Node(tag), createElementNS: (namespaceURI, tag) => Object.assign(new Node(tag), { namespaceURI }),
      createTextNode: text => { const node = new Node('text'); node.textContent = text; return node; },
      querySelectorAll: () => [],
      querySelector: selector => selector === 'body > .topbar' ? header : selector === 'body > main' ? main : null,
    };
    globalThis.location = { href: 'chrome-extension://fixture/options.html', pathname: '/options.html' };
    globalThis.sessionStorage = { getItem: key => session.get(key) ?? null, setItem: (key, value) => session.set(key, value) };
    globalThis.fetch = () => { throw new Error('Settings integration tests must not use the network.'); };
    globalThis.chrome = {
      tabs: { create: () => { throw new Error('Settings integration tests must not open tabs.'); } },
      runtime: {
        id: 'fixture-extension', getURL: path => `chrome-extension://fixture/${path}`,
        sendMessage: async message => {
          calls.push(structuredClone(message));
          assert.equal(message.channel, 'pulse-ui');
          switch (message.type) {
            case 'connection.status': return { ok: true, data: { state: 'connected' } };
            case 'config.get': return configGetFailure ? { ok: false, error: { code: 'HOST_DISCONNECTED', message: 'Fixture connection lost.' } } : { ok: true, data: structuredClone(config) };
            case 'config.save': {
              if (saveMode === 'rejected') return { ok: false, error: { code: 'WORKTREE_PATH_INVALID', message: 'Fixture worktree path is invalid.' } };
              if (saveMode !== 'unknown-no-save') config = structuredClone(message.payload);
              if (saveMode.startsWith('unknown')) return { ok: false, error: { code: 'RESPONSE_UNKNOWN', message: 'Fixture save acknowledgement was lost.' } };
              return { ok: true, data: saveMode === 'direct' ? structuredClone(config) : {} };
            }
            case 'agents.list': return unsupported
              ? { ok: false, error: { code: 'UNSUPPORTED_OPERATION', message: 'Unknown method agents.list.' } }
              : { ok: true, data: structuredClone(inventory) };
            case 'github.accounts': return accountFailure ? { ok: false, error: { code: 'HOST_DISCONNECTED', message: 'Fixture account scan unavailable.' } } : { ok: true, data: { available: true, accounts: [{ login: 'fixture-reviewer', active: true, state: 'success', available: true }] } };
            case 'hello': return { ok: true, data: { hostVersion: 'fixture-host', protocolVersion: 1, agents: { codex: first, copilot }, github: { available: true, account: 'fixture-reviewer' } } };
            case 'prompts.list': return { ok: true, data: { prompts: structuredClone(prompts), sourceUrl: 'bundled://Pulse.Host/prompts', revision: 'a'.repeat(64) } };
            case 'prompts.get': return message.payload.name === 'review.prompt.md'
              ? new Promise(resolve => { finishReviewPreview = () => resolve({ ok: true, data: { name: message.payload.name, title: 'Old review preview', content: 'This delayed content must not overwrite another preview.' } }); })
              : { ok: true, data: { name: message.payload.name, title: message.payload.name, content: `Content of ${message.payload.name}` } };
            case 'agents.test.start': return { ok: true, data: { testId: 'fixture-test-codex', agent: message.payload.agent, state: 'failed', path: message.payload.cliPath, version: '0.153.5', error: { code: 'FIXTURE_TEST_FAILED', message: 'The optional sample test failed.' } } };
            default: throw new Error(`Unexpected operation: ${message.type}. Only in-memory settings and test fixtures are allowed.`);
          }
        },
      },
    };
    await import('../src/options.ts'); await nextTurn();
    assert.equal(nodes.get('error').hidden, true, nodes.get('error').textContent);
    assert.equal(nodes.get('config-fields').disabled, false);
    assert.equal(callsOf('config.get').length, 1);
    assert.equal(callsOf('agents.list').length, 1);
    assert.match(nodes.get('prompt-status').textContent, /bundled prompts/);
    dispatch('reload-prompts', 'click'); await nextTurn();
    assert.equal(callsOf('prompts.list').length, 2);
    assert.equal(callsOf('prompts.sync').length, 0, 'reloading bundled prompts never synchronizes a remote repository');
    assert.equal(nodes.get('pr-prompt').children.some(option => option.value === 'e2e.prompt.md'), false, 'review cannot select the E2E workflow for the same target type');
    assert.equal(nodes.get('issue-prompt').children.some(option => option.value === 'reproduce.prompt.md'), false, 'fix cannot select the reproduction-only workflow');
    assert.equal(nodes.get('view-reproduction-prompt').disabled, false, 'the bundled default reproduction prompt can be previewed without changing its empty saved selector');
    dispatch('view-reproduction-prompt', 'click'); await nextTurn();
    assert.match(nodes.get('prompt-preview-content').textContent, /powertoys-issue-reproduction-setup/);
    assert.equal(nodes.get('reproduction-prompt').value, '');
    dispatch('view-pr-prompt', 'click'); await nextTurn();
    edit('pr-prompt', 'review-alt.prompt.md');
    dispatch('view-pr-prompt', 'click'); await nextTurn();
    finishReviewPreview(); await nextTurn();
    assert.equal(nodes.get('prompt-preview-content').textContent, 'Content of review-alt.prompt.md', 'late content cannot overwrite the currently selected prompt preview');
    edit('pr-prompt', 'review.prompt.md');
    for (const agent of ['codex', 'copilot']) {
      assert.equal(nodes.get(`${agent}-installation`).value, '', 'available inventory and inventory selections do not auto-select a CLI');
      assert.equal(nodes.get(`${agent}-installation`).children[0].value, '');
      assert.equal(nodes.get(`test-${agent}`).disabled, true);
    }
    const unavailableOption = nodes.get('codex-installation').children.find(option => option.value === incompatible.path);
    assert.equal(unavailableOption?.disabled, false, 'detected legacy files remain selectable regardless of stale compatibility metadata');
    assert.doesNotMatch(unavailableOption.textContent, /Required exec interface is missing|Unavailable/);
    assert.doesNotMatch(nodes.get('codex-inventory').textContent, /Required exec interface|Version output could not|Unavailable/);
    assert.equal(nodes.get('codex-installation').children.filter(option => option.textContent.includes('0.153.4')).length, 2, 'same-version installations remain separate choices');
    const unknownOption = nodes.get('codex-installation').children.find(option => option.value === unknown.path);
    assert.equal(unknownOption?.disabled, false); assert.match(unknownOption.textContent, /Version unknown/);
    edit('codex-installation', unknown.path);
    assert.equal(nodes.get('test-codex').disabled, false);
    assert.equal(callsOf('agents.test.start').length, 0, 'choosing an installation never runs a test automatically');

    edit('codex-installation', chosen.resolvedPath);
    edit('codex-default-model', 'gpt-5.5'); edit('codex-default-effort', 'high');
    edit('copilot-default-model', 'gpt-4.1'); edit('copilot-default-effort', 'low');
    edit('agent', 'copilot'); edit('permission', 'read-only');
    edit('main-repo-folder', 'D:\\Fixture\\PowerToys-edited'); edit('worktree-root', 'D:\\Fixture\\worktrees-edited');
    edit('pr-prompt', 'review-alt.prompt.md'); edit('issue-prompt', 'fix-alt.prompt.md');
    edit('e2e-prompt', 'e2e-alt.prompt.md'); edit('reproduction-prompt', 'reproduce.prompt.md');
    const edited = formValues();
    assert.equal(nodes.get('test-codex').disabled, false);
    assert.match(nodes.get('codex-installation-help').textContent, /Save settings/);
    inventory = {
      installations: { codex: [incompatible, { ...chosen, version: '0.153.5' }, first, { ...first, path: 'E:\\Fixture\\codex.exe', resolvedPath: 'E:\\Fixture\\codex.exe', aliases: [] }], copilot: [copilot] },
      selections: { codex: first.resolvedPath, copilot: copilot.path },
    };
    dispatch('refresh-installations', 'click');
    assert.equal(nodes.get('refresh-installations').disabled, true);
    assert.equal(nodes.get('test-codex').disabled, true, 'testing is blocked while the selected installation is being checked');
    await nextTurn();
    assert.equal(nodes.get('refresh-installations').disabled, false);
    assert.deepEqual(formValues(), edited, 'refreshing a changed inventory preserves all unsaved form values');
    assert.match(nodes.get('codex-installation-detail').textContent, /0\.153\.5/, 'refresh updates selected installation metadata');
    assert.equal(callsOf('config.get').length, 1, 'discovery does not reload saved config over form edits');
    assert.equal(callsOf('config.save').length, 0);

    dispatch('test-codex', 'click'); await nextTurn();
    assert.deepEqual(callsOf('agents.test.start').map(call => call.payload), [{ agent: 'codex', cliPath: chosen.resolvedPath }]);
    assert.equal(callsOf('config.save').length, 0, 'testing an unsaved installation does not save it');
    assert.equal(config.cliSelections.codex, '', 'the stored selection remains blank until Save settings');
    assert.equal(nodes.get('codex-status').textContent, 'Test failed');
    assert.equal(nodes.get('codex-reply').textContent, '');
    assert.equal(nodes.get('codex-installation').disabled, false, 'an optional test failure does not prevent selection');
    assert.deepEqual(formValues(), edited);

    dispatch('config-form', 'submit'); await nextTurn();
    const expectedConfig = {
      agent: 'copilot', cliSelections: { codex: chosen.resolvedPath, copilot: '' },
      agentDefaults: { codex: { model: 'gpt-5.5', reasoningEffort: 'high' }, copilot: { model: 'gpt-4.1', reasoningEffort: 'low' } },
      permission: 'read-only', mainRepoFolder: edited['main-repo-folder'], worktreeRoot: edited['worktree-root'], githubAccount: 'fixture-reviewer',
      prPrompt: 'review-alt.prompt.md', issuePrompt: 'fix-alt.prompt.md', e2ePrompt: 'e2e-alt.prompt.md', reproductionPrompt: 'reproduce.prompt.md',
    };
    assert.deepEqual(callsOf('config.save').map(call => call.payload), [expectedConfig]);
    assert.deepEqual(config, expectedConfig, 'saving after a failed optional test retains the chosen CLI and every edited field');
    assert.equal(callsOf('config.get').length, 2, 'save reloads the confirmed stored settings');
    assert.equal(nodes.get('saved').hidden, false);
    assert.equal(nodes.get('error').hidden, true, nodes.get('error').textContent);
    assert.deepEqual(formValues(), edited);
    edit('codex-installation', first.resolvedPath); edit('codex-default-model', 'temporary-unsaved-model');
    dispatch('reload', 'click'); await nextTurn();
    assert.equal(nodes.get('reload-confirmation').hidden, false, 'reload first offers to preserve the edited draft');
    assert.equal(nodes.get('codex-default-model').value, 'temporary-unsaved-model');
    assert.equal(callsOf('config.get').length, 2, 'opening the reload decision does not overwrite the draft');
    dispatch('keep-editing', 'click');
    assert.equal(nodes.get('reload-confirmation').hidden, true);
    assert.equal(nodes.get('codex-default-model').value, 'temporary-unsaved-model');
    dispatch('reload', 'click'); dispatch('discard-reload', 'click'); await nextTurn();
    assert.deepEqual(formValues(), edited, 'explicit discard and reload restores the saved installation and profile');
    assert.equal(callsOf('config.get').length, 3);

    inventory = { installations: { codex: [first, incompatible], copilot: [copilot] }, selections: { codex: first.resolvedPath, copilot: copilot.path } };
    await refresh();
    assert.equal(nodes.get('codex-installation').value, chosen.resolvedPath, 'a removed saved installation is retained instead of switching to another path');
    const missingOption = nodes.get('codex-installation').children.find(option => option.value === chosen.resolvedPath);
    assert.equal(missingOption?.disabled, true); assert.match(missingOption.textContent, /Not detected/);
    assert.match(nodes.get('codex-installation-help').textContent, /not detected in this scan/);
    assert.equal(nodes.get('test-codex').disabled, false, 'a user may explicitly test a retained path and inspect its actual launch result');
    dispatch('test-codex', 'click'); await nextTurn();
    assert.equal(callsOf('agents.test.start').length, 2); assert.equal(callsOf('agents.test.start')[1].payload.cliPath, chosen.resolvedPath);
    assert.deepEqual(formValues(), edited);

    unsupported = true; await refresh();
    assert.equal(nodes.get('cli-installation-error').hidden, false);
    assert.match(nodes.get('cli-installation-error').textContent, /Host does not support installation selection.*Update the local Host.*saved selection is unchanged/);
    assert.equal(nodes.get('codex-installation').disabled, true);
    assert.equal(nodes.get('test-codex').disabled, true);
    assert.deepEqual(formValues(), edited);
    unsupported = false;
    inventory = { installations: { codex: [first, chosen, incompatible], copilot: [copilot] }, selections: { codex: '', copilot: '' } };
    await refresh();
    assert.equal(nodes.get('cli-installation-error').hidden, true, 'successful discovery clears the unsupported-Host message');
    assert.equal(nodes.get('cli-installation-error').textContent, '');
    assert.equal(nodes.get('codex-installation').disabled, false);
    assert.equal(nodes.get('test-codex').disabled, false);
    assert.deepEqual(formValues(), edited);
    assert.equal(callsOf('config.save').length, 1, 'discovery, testing, and reload do not cause additional saves');
    assert.ok(calls.every(call => ['connection.status', 'config.get', 'config.save', 'agents.list', 'github.accounts', 'hello', 'prompts.list', 'prompts.get', 'agents.test.start'].includes(call.type)));

    edit('codex-default-model', 'draft-after-save');
    assert.equal(nodes.get('saved').hidden, true, 'every field edit clears the stale saved message');
    assert.match(nodes.get('save-state').textContent, /1 unsaved change/);
    dispatch('edit-workspace', 'click');
    assert.equal(nodes.get('settings-workspace').hidden, false); assert.equal(nodes.get('settings-overview').hidden, true);
    assert.equal(nodes.get('codex-default-model').value, 'draft-after-save', 'changing settings groups retains other groups’ values');

    accountFailure = true; dispatch('detect-github', 'click'); await nextTurn();
    assert.equal(nodes.get('github-account').value, 'fixture-reviewer');
    assert.match(nodes.get('github-status').textContent, /unknown/);
    assert.equal(nodes.get('github-login-help').hidden, true, 'a failed scan does not claim no accounts are signed in');
    accountFailure = false; dispatch('detect-github', 'click'); await nextTurn();
    edit('github-account', ''); dispatch('detect-github', 'click'); await nextTurn();
    assert.equal(nodes.get('github-account').value, '', 'explicitly clearing the account survives discovery');
    edit('github-account', 'fixture-reviewer');

    saveMode = 'rejected'; dispatch('config-form', 'submit'); await nextTurn();
    assert.equal(nodes.get('worktree-root').attributes.get('aria-invalid'), 'true');
    assert.equal(nodes.get('worktree-root').focused, true);
    assert.equal(nodes.get('config-fields').disabled, false);
    assert.equal(nodes.get('codex-default-model').value, 'draft-after-save', 'rejected save retains every field');
    assert.match(nodes.get('save-state').textContent, /not saved/);

    saveMode = 'unknown-applied'; dispatch('config-form', 'submit'); await nextTurn();
    const unknownSaves = callsOf('config.save').length;
    assert.equal(nodes.get('save-recovery').hidden, false);
    assert.equal(nodes.get('save').disabled, true); assert.equal(nodes.get('config-fields').disabled, true);
    dispatch('config-form', 'submit'); await nextTurn();
    assert.equal(callsOf('config.save').length, unknownSaves, 'an unknown attempt cannot be blindly submitted again');
    dispatch('check-saved', 'click'); await nextTurn();
    assert.equal(nodes.get('save-recovery').hidden, true);
    assert.match(nodes.get('saved').textContent, /match your submitted changes/);
    assert.equal(nodes.get('config-fields').disabled, false);

    edit('codex-default-model', 'draft-not-saved');
    saveMode = 'unknown-no-save'; dispatch('config-form', 'submit'); await nextTurn();
    assert.equal(nodes.get('check-saved').hidden, false, 'a new unknown attempt exposes recovery again');
    dispatch('check-saved', 'click'); await nextTurn();
    assert.match(nodes.get('save-recovery-title').textContent, /differ/);
    assert.match(nodes.get('save-comparison').textContent, /draft-after-save.*draft-not-saved/);
    assert.equal(nodes.get('codex-default-model').value, 'draft-not-saved');
    assert.equal(nodes.get('save').disabled, false, 'a completed comparison permits an explicit apply of the retained draft');
    const beforeDirect = callsOf('config.get').length;
    saveMode = 'direct'; dispatch('config-form', 'submit'); await nextTurn();
    assert.equal(callsOf('config.get').length, beforeDirect, 'a complete saved-config response is already confirmation; no fragile second request is required');
    assert.equal(nodes.get('saved').hidden, false);

    edit('codex-default-model', 'acknowledged-save');
    saveMode = 'legacy'; configGetFailure = true; dispatch('config-form', 'submit'); await nextTurn();
    assert.equal(nodes.get('error').hidden, true);
    assert.match(nodes.get('saved').textContent, /Host confirmed the save.*could not be reloaded/);
    assert.equal(nodes.get('save-recovery').hidden, true, 'an acknowledged save is not mislabeled unknown because optional readback failed');

    // A fresh Settings page with failed config.get still exposes its own retry and Host controls.
    for (const node of nodes.values()) node.listeners.clear();
    await import('../src/options.ts?initial-load-failure'); await nextTurn();
    assert.equal(nodes.get('config-fields').disabled, true);
    assert.equal(nodes.get('reload').disabled, false);
    assert.equal(nodes.get('reload').textContent, 'Retry loading settings');
    assert.equal(nodes.get('detect').disabled, false);
    configGetFailure = false; dispatch('reload', 'click'); await nextTurn();
    assert.equal(nodes.get('config-fields').disabled, false);
    assert.equal(nodes.get('error').hidden, true);
  } finally {
    for (const key of globals) {
      const descriptor = originals.get(key);
      if (descriptor) Object.defineProperty(globalThis, key, descriptor); else delete globalThis[key];
    }
  }
});
