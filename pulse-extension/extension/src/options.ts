import { byId, clearError, connectionStatus, element, errorText, initNavigation, labeledValue, report, request } from './ui.js';
import { ProtocolError, reasoningEfforts, validateExecution } from './policy.js';
import { cliOptions, selectedInstallation } from './cli-selection.js';
import { changedSettings, configValues, isConfig, saveOutcomeUnconfirmed, settingsErrorField } from './setup-model.js';
import type { Agent, AgentInstallations, AgentProfile, AgentTest, Capabilities, Config, GitHubAccounts, PromptCatalog, PromptContent, PulseError } from './types.js';

initNavigation();
byId('extension-id').textContent = chrome.runtime.id;
const fields = byId<HTMLFieldSetElement>('config-fields');
const accountSelect = byId<HTMLSelectElement>('github-account');
const agents = ['codex', 'copilot'] as const;
const testStorageKey = 'pulse.agent-tests.v1';
interface TestView { test?: AgentTest; busy: boolean; error?: string; timer?: ReturnType<typeof setTimeout> }
const tests: Record<Agent, TestView> = { codex: { busy: false }, copilot: { busy: false } };
let savedAccount = '';
let accounts: GitHubAccounts | undefined;
let detectingAccounts = false;
let loading = false;
let baseline: Config | undefined;
let saving = false;
let unconfirmedSave: Config | undefined;
let saveMessage = '';
let accountsError = '';
let hostConnected = false;
let checkingHost = false;
let previewRequest = 0;
const draftStorageKey = 'pulse.settings-draft.v1';
const sections = ['overview', 'agents', 'prompts', 'access', 'workspace', 'github', 'host'] as const;
type SettingsSection = typeof sections[number];
const inputIds = ['agent', 'codex-installation', 'copilot-installation', 'codex-default-model', 'copilot-default-model', 'codex-default-effort', 'copilot-default-effort', 'permission', 'main-repo-folder', 'worktree-root', 'github-account', 'pr-prompt', 'issue-prompt', 'e2e-prompt', 'reproduction-prompt'];
let catalog: PromptCatalog | undefined;
let catalogLoaded = false;
let loadingPrompts = false;
let installations: AgentInstallations | undefined;
let detectingInstallations = false;
let installationError = '';
let installationInventoryCurrent = false;
const selectedCliPaths: Record<Agent, string> = { codex: '', copilot: '' };
const savedCliPaths: Record<Agent, string> = { codex: '', copilot: '' };
const promptTargets = ['pr', 'issue', 'e2e', 'reproduction'] as const;
type PromptTarget = typeof promptTargets[number];
const selectedPrompts: Record<PromptTarget, string> = { pr: '', issue: '', e2e: '', reproduction: '' };

function showSection(section: SettingsSection): void {
  for (const item of sections) {
    byId(`settings-${item}`).hidden = item !== section;
    const button = byId(`show-${item}`);
    if (item === section) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
  }
}
function readForm(): Config {
  return {
    agent: byId<HTMLSelectElement>('agent').value as Agent,
    cliSelections: { ...selectedCliPaths },
    agentDefaults: Object.fromEntries(agents.map(agent => [agent, { model: byId<HTMLInputElement>(`${agent}-default-model`).value.trim(), reasoningEffort: byId<HTMLSelectElement>(`${agent}-default-effort`).value }])) as Config['agentDefaults'],
    permission: byId<HTMLSelectElement>('permission').value as Config['permission'],
    mainRepoFolder: byId<HTMLInputElement>('main-repo-folder').value.trim(),
    worktreeRoot: byId<HTMLInputElement>('worktree-root').value.trim(),
    githubAccount: accountSelect.value,
    prPrompt: byId<HTMLSelectElement>('pr-prompt').value,
    issuePrompt: byId<HTMLSelectElement>('issue-prompt').value,
    e2ePrompt: byId<HTMLSelectElement>('e2e-prompt').value,
    reproductionPrompt: byId<HTMLSelectElement>('reproduction-prompt').value,
  };
}
function cacheDraft(): void {
  if (!baseline) return;
  try { sessionStorage.setItem(draftStorageKey, JSON.stringify({ baseline, draft: readForm(), unconfirmedSave })); }
  catch { /* A storage failure does not discard the open draft. */ }
}
function updateSetup(): void {
  const config = baseline ? readForm() : undefined;
  const changes = baseline && config ? changedSettings(baseline, config) : [];
  byId('config-state').textContent = !baseline ? loading ? 'Loading settings' : 'Settings unavailable' : changes.length ? 'Unsaved changes' : 'Saved configuration';
  byId('save-state').textContent = saving ? 'Saving settings…' : unconfirmedSave ? 'Save outcome unconfirmed' : !baseline ? loading ? 'Loading saved settings…' : 'Settings could not be loaded' : saveMessage || (changes.length ? `${changes.length} unsaved ${changes.length === 1 ? 'change' : 'changes'}` : 'No unsaved changes');
  byId('save-help').textContent = unconfirmedSave ? 'Check saved settings to resolve this attempt.' : changes.length ? 'Changes apply only after you save. Existing tasks keep their configuration.' : 'New tasks check their required setup before starting.';
  byId<HTMLButtonElement>('save').disabled = !baseline || saving || loading || Boolean(unconfirmedSave) || changes.length === 0;
  byId<HTMLButtonElement>('save').textContent = saving ? 'Saving…' : 'Save changes';
  byId<HTMLButtonElement>('reload').disabled = loading || saving || Boolean(unconfirmedSave);
  byId<HTMLButtonElement>('reload').textContent = !baseline && !loading ? 'Retry loading settings' : 'Reload settings';
  fields.disabled = !baseline || saving || Boolean(unconfirmedSave);
  if (!config) {
    byId('setup-summary').textContent = loading ? 'Reading your saved configuration…' : 'Retry loading settings, or check the local Host connection. Unknown values remain locked.';
    return;
  }
  const name = config.agent === 'codex' ? 'Codex CLI' : 'GitHub Copilot CLI';
  const missing = [!config.cliSelections[config.agent] && 'an installation for the default agent', !config.mainRepoFolder && 'a main checkout', !config.worktreeRoot && 'a worktree root'].filter(Boolean);
  byId('setup-summary').textContent = missing.length ? `${changes.length ? 'This draft still needs' : 'Complete your setup with'} ${missing.join(', ')}. You can save partial setup and return later.` : `${changes.length ? 'Your draft includes' : 'Your saved configuration includes'} an agent installation and workspace folders. Task-specific readiness has not been checked here.`;
  byId('summary-agents').textContent = `${name} · ${config.cliSelections[config.agent] ? 'Installation selected' : 'Choose an installation'}`;
  byId('summary-workspace').textContent = config.mainRepoFolder && config.worktreeRoot ? `${config.mainRepoFolder} · Worktrees: ${config.worktreeRoot}` : 'Main checkout and worktree root are needed for local tasks.';
  const unavailable = promptTargets.filter(target => target !== 'reproduction' || selectedPrompts[target]).filter(target => !catalog?.prompts.some(prompt => prompt.name === selectedPrompts[target] && promptAppliesTo(target, prompt))).length;
  byId('summary-prompts').textContent = !catalogLoaded ? 'Loading bundled prompts…' : !catalog ? 'Prompt catalog unavailable' : unavailable ? `${unavailable} prompt selections need attention` : 'Review, local fix, E2E, and reproduction · Bundled locally';
  byId('summary-access').textContent = { 'read-only': 'Read only', 'workspace-write': 'Allow workspace changes', yolo: 'Full access · No approvals' }[config.permission];
  byId('summary-github').textContent = config.githubAccount ? `Extension account: @${config.githubAccount}${accountsError ? ' · Availability unknown' : ''}` : 'No extension account selected. Configure when needed.';
  for (const agent of agents) byId(`${agent}-default-label`).hidden = config.agent !== agent;
}
function markEdited(): void {
  if (!baseline || saving || unconfirmedSave) return;
  saveMessage = ''; byId('saved').hidden = true; clearError();
  for (const id of inputIds) byId(id).removeAttribute('aria-invalid');
  updateSetup(); cacheDraft();
}
function showSettingsError(error: unknown): void {
  report(error);
  const field = error instanceof ProtocolError ? settingsErrorField(error.code, error.message) : undefined;
  if (field) {
    showSection(field === 'github-account' ? 'github' : 'workspace');
    byId(field).setAttribute('aria-invalid', 'true'); byId(field).focus();
  } else byId('error').focus();
}
function diagnostic(error: string | PulseError | undefined): string {
  if (!error) return '';
  return typeof error === 'string' ? error : `${error.message}${error.guidance ? ` ${error.guidance}` : ''} [${error.code}]`;
}
function showPermission(): void {
  const descriptions: Record<string, string> = {
    'read-only': 'Read and analyze code without changing files.',
    'workspace-write': 'Allow the agent to modify this task’s worktree. Other access follows the CLI permission rules.',
    yolo: 'Allow full local file and command access without approvals.',
  };
  byId('permission-help').textContent = descriptions[byId<HTMLSelectElement>('permission').value] || '';
}
function showConfig(config: Config, saved = true): void {
  if (!isConfig(config)) throw new Error('The Host returned incomplete settings. Reload after checking the Host connection.');
  if (saved) baseline = config;
  byId<HTMLSelectElement>('agent').value = config.agent;
  for (const agent of agents) {
    selectedCliPaths[agent] = config.cliSelections?.[agent] ?? '';
    if (saved) savedCliPaths[agent] = selectedCliPaths[agent];
    const profile = config.agentDefaults?.[agent];
    byId<HTMLInputElement>(`${agent}-default-model`).value = profile?.model ?? '';
    const select = byId<HTMLSelectElement>(`${agent}-default-effort`);
    const inherited = element('option', 'CLI default'); inherited.value = '';
    select.replaceChildren(inherited);
    for (const effort of reasoningEfforts[agent]) {
      const option = element('option', effort); option.value = effort; select.append(option);
    }
    const selected = profile?.reasoningEffort ?? '';
    if (selected && !reasoningEfforts[agent].includes(selected)) {
      const unavailable = element('option', `${selected} · Unsupported by this extension`); unavailable.value = selected; unavailable.disabled = true; select.append(unavailable);
    }
    select.value = selected;
  }
  renderInstallations();
  byId<HTMLSelectElement>('permission').value = config.permission;
  byId<HTMLInputElement>('main-repo-folder').value = config.mainRepoFolder;
  byId<HTMLInputElement>('worktree-root').value = config.worktreeRoot;
  if (saved) savedAccount = config.githubAccount;
  selectedPrompts.pr = config.prPrompt || '';
  selectedPrompts.issue = config.issuePrompt || '';
  selectedPrompts.e2e = config.e2ePrompt ?? 'powertoys-pr-e2e-test.prompt.md';
  selectedPrompts.reproduction = config.reproductionPrompt || '';
  renderPrompts();
  renderAccounts(config.githubAccount);
  showPermission();
  updateSetup();
}
function readAgentProfile(agent: Agent): AgentProfile {
  const model = byId<HTMLInputElement>(`${agent}-default-model`).value.trim();
  const reasoningEffort = byId<HTMLSelectElement>(`${agent}-default-effort`).value;
  validateExecution({ agent, model, reasoningEffort });
  return { model, reasoningEffort };
}
function renderInstallations(): void {
  for (const agent of agents) {
    const select = byId<HTMLSelectElement>(`${agent}-installation`);
    select.replaceChildren();
    const probes = installations?.installations[agent] ?? [];
    const selected = selectedInstallation(probes, selectedCliPaths[agent]);
    for (const row of cliOptions(probes, selectedCliPaths[agent])) {
      const option = element('option', row.label); option.value = row.value; option.disabled = row.disabled; select.append(option);
    }
    select.value = selectedCliPaths[agent];
    select.disabled = detectingInstallations || !installationInventoryCurrent;
    byId(`${agent}-inventory-title`).textContent = `All detected installations (${probes.length})`;
    const inventory = byId(`${agent}-inventory`); inventory.replaceChildren();
    if (!probes.length) inventory.append(element('p', installationInventoryCurrent ? 'No installations found.' : 'Refresh to load the installation list.', 'muted fine'));
    for (const probe of probes) {
      const item = element('article', undefined, 'cli-installation-row');
      const heading = element('div', undefined, 'row');
      const sources = [...new Set([...(probe.sources ?? []), ...(probe.source ? [probe.source] : [])])];
      heading.append(element('strong', [probe.version || 'Version unknown', sources.join(', ') || 'Detected locally'].join(' · ')), element('span', probe === selected ? 'Selected' : 'Detected', 'status'));
      item.append(heading, element('p', probe.resolvedPath || probe.path, 'cli-full-path'));
      if (probe.path !== probe.resolvedPath && probe.resolvedPath) item.append(element('p', `Detected through ${probe.path}`, 'muted fine path'));
      inventory.append(item);
    }
    const detail = byId(`${agent}-installation-detail`); detail.replaceChildren();
    const help = byId(`${agent}-installation-help`);
    const unsaved = selectedCliPaths[agent] !== savedCliPaths[agent];
    if (selectedCliPaths[agent]) {
      detail.append(labeledValue('Selected path', selectedCliPaths[agent]));
      if (selected?.resolvedPath && selected.resolvedPath.toLowerCase() !== selectedCliPaths[agent].toLowerCase()) detail.append(labeledValue('Resolved executable', selected.resolvedPath));
      if (selected?.version) detail.append(labeledValue('Version', selected.version));
      if (selected?.source) detail.append(labeledValue('Source', selected.source));
    }
    detail.hidden = !selectedCliPaths[agent];
    help.textContent = detectingInstallations ? 'Refreshing the installation list…' : installationError ? 'The installation list could not be loaded. Your selection is retained.'
      : !probes.length ? 'No installations were found for this CLI. Install it locally, then refresh installations.'
      : !selectedCliPaths[agent] ? 'Choose an installation before running tasks with this CLI.'
      : !selected ? 'This selected path was not detected in this scan. Your selection is retained.'
      : unsaved ? 'Save settings to use this path for new tasks.' : 'New tasks will use this path.';
    help.className = 'muted fine cli-installation-help';
    renderTest(agent);
  }
  const error = byId('cli-installation-error'); error.hidden = !installationError; error.textContent = installationError;
  const count = agents.reduce((total, agent) => total + (installations?.installations[agent]?.length ?? 0), 0);
  byId('cli-installation-status').textContent = detectingInstallations ? 'Finding local CLI installations…' : installationInventoryCurrent ? `${count} installations detected. Select one for each CLI you use.` : 'Installation discovery is unavailable.';
  byId<HTMLButtonElement>('refresh-installations').disabled = detectingInstallations;
  updateSetup();
}
async function detectInstallations(): Promise<void> {
  if (detectingInstallations) return;
  detectingInstallations = true; installationError = ''; installationInventoryCurrent = false; renderInstallations();
  try {
    installations = await request<AgentInstallations>('agents.list'); installationInventoryCurrent = true;
  } catch (error) {
    installationError = error instanceof ProtocolError && ['UNSUPPORTED_OPERATION', 'FORBIDDEN_METHOD'].includes(error.code)
      ? 'This Host does not support installation selection. Update the local Host, then refresh installations. Your saved selection is unchanged.' : errorText(error);
  } finally { detectingInstallations = false; renderInstallations(); }
}
function promptAppliesTo(target: PromptTarget, prompt: PromptCatalog['prompts'][number]): boolean {
  const appliesTo = target === 'e2e' ? 'pr' : target === 'reproduction' ? 'issue' : target;
  const actionKind = { pr: 'pr-review', issue: 'issue-fix', e2e: 'e2e', reproduction: 'reproduction-setup' }[target];
  return prompt.actionKind ? prompt.actionKind === actionKind : prompt.appliesTo === appliesTo || prompt.appliesTo === 'both';
}
function renderPrompts(): void {
  for (const target of promptTargets) {
    const select = byId<HTMLSelectElement>(`${target}-prompt`);
    const preferred = selectedPrompts[target];
    const placeholder = element('option', target === 'reproduction' ? 'Default reproduction prompt' : !catalogLoaded ? 'Loading bundled prompts…' : catalog?.prompts.length ? 'Select a prompt' : 'Update Host to load prompts'); placeholder.value = '';
    select.replaceChildren(placeholder);
    const available = (catalog?.prompts ?? []).filter(prompt => promptAppliesTo(target, prompt));
    for (const prompt of available) {
      const option = element('option', `${prompt.title} · ${prompt.name}`); option.value = prompt.name; select.append(option);
    }
    if (preferred && !available.some(prompt => prompt.name === preferred)) {
      const missing = element('option', `${preferred} · ${catalogLoaded ? 'Not available locally' : 'Loading…'}`); missing.value = preferred; missing.disabled = true; select.append(missing);
    }
    select.value = preferred;
    renderPromptHelp(target);
  }
  byId('prompt-status').textContent = catalog
    ? `${catalog.prompts.length} ${catalog.sourceUrl?.startsWith('bundled:') ? 'bundled prompts' : 'prompts · Update Host for bundled local workflows'}${catalog.revision ? ` · Version ${catalog.revision.slice(0, 8)}` : ''}`
    : catalogLoaded ? 'Bundled prompts could not be loaded. Reload or update the local Host.' : 'Loading bundled prompts…';
  const error = byId('prompt-error'); error.textContent = diagnostic(catalog?.error); error.hidden = !catalog?.error;
  updateSetup();
}
function selectedPrompt(target: PromptTarget) {
  const name = byId<HTMLSelectElement>(`${target}-prompt`).value;
  return catalog?.prompts.find(item => item.name === (target === 'reproduction' && !name ? 'powertoys-issue-reproduction-setup.prompt.md' : name) && promptAppliesTo(target, item));
}
function renderPromptHelp(target: PromptTarget): void {
  const selected = byId<HTMLSelectElement>(`${target}-prompt`).value;
  const prompt = selectedPrompt(target);
  byId(`${target}-prompt-help`).textContent = target === 'reproduction' && !selected ? `Uses the bundled reproduction prompt to prepare a minimal reproduction and report evidence.${catalogLoaded && !prompt ? ' This Host does not expose its default prompt for preview.' : ''}` : !catalogLoaded ? 'Loading bundled prompts…' : prompt ? `${prompt.description ? `${prompt.description}\n` : ''}${prompt.path || prompt.name}` : selected ? 'This saved prompt is unavailable in this Host. Select a bundled prompt.' : 'Choose a bundled prompt for new tasks.';
  byId<HTMLButtonElement>(`view-${target}-prompt`).disabled = !prompt;
}
async function loadPrompts(): Promise<void> {
  if (loadingPrompts) return;
  loadingPrompts = true;
  previewRequest++; byId('prompt-preview').hidden = true;
  const button = byId<HTMLButtonElement>('reload-prompts'); button.disabled = true;
  byId('prompt-status').textContent = 'Loading bundled prompts…';
  try { catalog = await request<PromptCatalog>('prompts.list'); catalogLoaded = true; renderPrompts(); }
  catch (error) { catalogLoaded = true; renderPrompts(); report(error, byId('prompt-error')); }
  finally { loadingPrompts = false; button.disabled = false; }
}
async function viewPrompt(target: PromptTarget): Promise<void> {
  const name = selectedPrompt(target)?.name;
  if (!name) return;
  const token = ++previewRequest;
  const preview = byId<HTMLDetailsElement>('prompt-preview'); preview.hidden = false; preview.open = true;
  byId('prompt-preview-title').textContent = `${selectedPrompt(target)?.title ?? 'Prompt'} · ${name}`;
  byId('prompt-preview-content').textContent = '';
  byId('prompt-preview-status').textContent = 'Loading prompt content…';
  clearError(byId('prompt-preview-error'));
  try {
    const prompt = await request<PromptContent>('prompts.get', { name });
    if (token !== previewRequest || selectedPrompt(target)?.name !== name) return;
    byId('prompt-preview-title').textContent = `${prompt.title} · ${prompt.name}`;
    byId('prompt-preview-content').textContent = prompt.content;
    byId('prompt-preview-status').textContent = 'Read-only bundled prompt. Previewing does not change your selection.';
  } catch (error) {
    if (token !== previewRequest) return;
    byId('prompt-preview-status').textContent = 'The selection is retained. Use View prompt again to retry.';
    report(error, byId('prompt-preview-error'));
  }
}
function renderAccountHelp(): void {
  const selected = accounts?.accounts.find(item => item.login === accountSelect.value && item.state === 'success' && item.available !== false);
  byId('github-account-help').textContent = selected
    ? `@${selected.login}${selected.active ? ' is the current active gh account.' : ' will be used for this extension’s GitHub actions.'}${accountSelect.value !== savedAccount ? ' Save settings to apply.' : ''}`
    : accountSelect.value ? accountsError || !accounts ? `Availability of @${accountSelect.value} is unknown. Detect accounts to check it. Your selection is retained.` : `Saved @${accountSelect.value} is currently unavailable. Detect accounts again or choose another account.` : 'Choose a signed-in account and save. You can configure local tasks before choosing an account.';
}
function renderAccounts(preferred = accountSelect.value): void {
  const available = accounts?.accounts.filter(item => item.state === 'success' && item.available !== false) ?? [];
  const placeholder = element('option', detectingAccounts ? 'Checking accounts…' : accountsError || !accounts ? 'Account availability unknown' : available.length ? 'Select a GitHub account' : 'No GitHub accounts available'); placeholder.value = '';
  accountSelect.replaceChildren(placeholder);
  for (const account of available) {
    const option = element('option', `@${account.login}${account.active ? ' · Active gh account' : ''}`);
    option.value = account.login; accountSelect.append(option);
  }
  if (preferred && !available.some(item => item.login === preferred)) {
    const missing = element('option', `@${preferred} · ${accountsError || !accounts ? 'Not checked' : 'Currently unavailable'}`); missing.value = preferred; missing.disabled = true; accountSelect.append(missing);
  }
  accountSelect.value = preferred;
  if (!accountSelect.value) accountSelect.value = '';
  accountSelect.disabled = detectingAccounts || available.length === 0 && !preferred;
  const active = accounts?.accounts.find(item => item.active);
  byId('github-status').textContent = detectingAccounts ? 'Checking local gh authentication…' : accountsError || !accounts ? 'Account availability is unknown. Detect accounts again.' : available.length
    ? `Detected ${available.length} available accounts`
    : 'No signed-in gh accounts were found.';
  byId('github-terminal-default').textContent = active ? `Terminal default: @${active.login}. Selecting an extension account does not change it.` : 'The extension account is separate from your terminal’s default gh account.';
  byId('github-login-help').hidden = !accounts || Boolean(accountsError) || detectingAccounts || available.length > 0;
  const error = byId('github-error'); error.textContent = accountsError || diagnostic(accounts?.error); error.hidden = !error.textContent;
  renderAccountHelp();
  updateSetup();
}
async function detectAccounts(): Promise<void> {
  if (detectingAccounts) return;
  detectingAccounts = true; accountsError = '';
  const detect = byId<HTMLButtonElement>('detect-github'); detect.disabled = true;
  byId('github-status').textContent = 'Checking local gh authentication…';
  try { accounts = await request<GitHubAccounts>('github.accounts'); accountsError = diagnostic(accounts.error); }
  catch (error) {
    accountsError = errorText(error);
  } finally { detectingAccounts = false; detect.disabled = false; renderAccounts(accountSelect.value); }
}
async function detectHost(): Promise<void> {
  if (checkingHost) return;
  checkingHost = true; byId<HTMLButtonElement>('detect').disabled = true;
  byId('host-detail').textContent = 'Checking local Host connection…';
  try {
    const caps = await request<Capabilities>('hello');
    hostConnected = true;
    byId('capabilities').replaceChildren(labeledValue('Host', caps.hostVersion), labeledValue('Protocol', String(caps.protocolVersion)));
    byId('host-detail').textContent = 'The Host responded. This connection check does not validate a task or CLI model access.';
    byId('summary-host').textContent = `Connected · Host ${caps.hostVersion}`;
    clearError(byId('host-error'));
  } catch (error) {
    byId('host-detail').textContent = hostConnected ? 'The Host could not be reached. Version information above is from the last successful check.' : 'The Host could not be reached. Check the Host installation and this browser’s extension ID registration.';
    byId('summary-host').textContent = 'Host connection unavailable';
    report(error, byId('host-error'));
  } finally { checkingHost = false; byId<HTMLButtonElement>('detect').disabled = false; }
}
function saveTests(): void {
  try { sessionStorage.setItem(testStorageKey, JSON.stringify(Object.fromEntries(agents.map(agent => [agent, tests[agent].test])))); }
  catch { /* Tests still run when session storage is unavailable. */ }
}
function renderTest(agent: Agent): void {
  const view = tests[agent];
  const result = view.test;
  const selected = selectedInstallation(installations?.installations[agent] ?? [], selectedCliPaths[agent]);
  const sameInstallation = Boolean(result?.path && (result.path.replaceAll('/', '\\').toLowerCase() === selectedCliPaths[agent].replaceAll('/', '\\').toLowerCase() || selected && selectedInstallation(installations?.installations[agent] ?? [], result.path) === selected));
  const running = result?.state === 'running';
  const succeeded = result?.state === 'succeeded' && Boolean(result.reply?.trim()) && !result.error;
  const state = view.busy && !result ? 'running' : view.error && !result ? 'failed' : succeeded ? 'succeeded' : result?.state === 'succeeded' ? 'failed' : result?.state;
  const labels = { running: 'Testing', succeeded: 'Test passed', failed: 'Test failed', cancelled: 'Cancelled' };
  const status = byId(`${agent}-status`); status.className = `status ${result && !sameInstallation && !running ? '' : state || ''}`; status.textContent = result && !sameInstallation && !running ? 'Previous installation' : state ? labels[state] : 'Not tested';
  let message = result ? running ? "Sending what's your model and waiting for a response. You can cancel at any time."
    : succeeded ? 'A model response was received from the tested installation.'
    : result.state === 'cancelled' ? 'The test was cancelled. You can test again.'
    : diagnostic(result.error) || 'No valid model response was received. Check the CLI sign-in and retry.'
    : view.busy ? "Starting the CLI and sending what's your model…" : `Uses your local ${agent === 'codex' ? 'Codex' : 'Copilot'} sign-in.`;
  if (view.error) message = view.error;
  else if (result && !sameInstallation) message = `${running ? 'A test is running for another installation.' : 'This result belongs to a previously tested installation.'} ${selectedCliPaths[agent] ? 'Test the selected path to check it.' : 'Choose an installation to test.'}`;
  byId(`${agent}-message`).textContent = message;
  const info = byId(`${agent}-info`); info.textContent = [result?.version && `Reported version: ${result.version}`, result?.path && `Tested path: ${result.path}`].filter(Boolean).join(' · '); info.hidden = !info.textContent;
  const reply = byId(`${agent}-reply`); reply.textContent = result?.reply || ''; reply.hidden = !reply.textContent;
  const test = byId<HTMLButtonElement>(`test-${agent}`); test.disabled = view.busy || running || !installationInventoryCurrent || !selectedCliPaths[agent]; test.textContent = result && !running && sameInstallation ? 'Test again' : `Test ${agent === 'codex' ? 'Codex' : 'Copilot'}`;
  const cancel = byId<HTMLButtonElement>(`cancel-${agent}`); cancel.hidden = !running; cancel.disabled = view.busy;
}
function acceptTest(agent: Agent, test: AgentTest): void {
  if (test.agent !== agent || !test.testId || !['running', 'succeeded', 'failed', 'cancelled'].includes(test.state)) throw new Error('The Host returned a mismatched test state.');
  tests[agent].test = test; tests[agent].error = undefined;
  saveTests(); renderTest(agent);
}
function scheduleTest(agent: Agent): void {
  const view = tests[agent];
  if (view.timer) clearTimeout(view.timer);
  if (view.test?.state === 'running') view.timer = setTimeout(() => { void pollTest(agent); }, 1500);
}
async function pollTest(agent: Agent): Promise<void> {
  const view = tests[agent];
  if (!view.test || view.test.state !== 'running') return;
  if (view.busy) { scheduleTest(agent); return; }
  const testId = view.test.testId;
  try {
    const next = await request<AgentTest>('agents.test.get', { testId });
    if (view.test?.testId === testId && view.test.state === 'running' && !view.busy) acceptTest(agent, next);
  } catch (error) {
    if (error instanceof ProtocolError && error.code === 'AGENT_TEST_NOT_FOUND') {
      acceptTest(agent, { ...view.test, state: 'failed', error: { code: error.code, message: error.message, guidance: 'Start a new test.' } });
    } else {
      view.error = `${errorText(error)} Retrying the test status. The test continues locally.`;
      renderTest(agent);
    }
  } finally { scheduleTest(agent); }
}
async function startTest(agent: Agent): Promise<void> {
  const view = tests[agent];
  if (view.busy || view.test?.state === 'running') return;
  const cliPath = selectedCliPaths[agent];
  if (!installationInventoryCurrent || !cliPath) return;
  view.busy = true; view.test = undefined; view.error = undefined; saveTests(); renderTest(agent);
  try { acceptTest(agent, await request<AgentTest>('agents.test.start', { agent, cliPath })); }
  catch (error) { view.error = errorText(error); }
  finally { view.busy = false; renderTest(agent); scheduleTest(agent); void connectionStatus(); }
}
async function cancelTest(agent: Agent): Promise<void> {
  const view = tests[agent];
  if (view.busy || view.test?.state !== 'running') return;
  view.busy = true; renderTest(agent);
  try { acceptTest(agent, await request<AgentTest>('agents.test.cancel', { testId: view.test.testId })); }
  catch (error) { view.error = `${errorText(error)} Reload the status or cancel again.`; }
  finally { view.busy = false; renderTest(agent); scheduleTest(agent); }
}
async function load(restoreDraft = false): Promise<void> {
  if (loading) return;
  loading = true; saveMessage = ''; previewRequest++; byId('prompt-preview').hidden = true;
  updateSetup();
  try {
    clearError();
    const results = await Promise.allSettled([request<Config>('config.get').then(config => {
      showConfig(config);
      byId<HTMLDetailsElement>(`${config.agent}-profile`).open = true;
      if (restoreDraft) {
        try {
          const cached = JSON.parse(sessionStorage.getItem(draftStorageKey) || '{}') as { baseline?: Config; draft?: Config; unconfirmedSave?: Config };
          if (isConfig(cached.draft) && isConfig(cached.baseline) && (changedSettings(cached.baseline, cached.draft).length || isConfig(cached.unconfirmedSave))) {
            showConfig(cached.draft, false);
            saveMessage = changedSettings(cached.baseline, config).length ? 'Draft restored · Saved settings have changed; review your edits' : 'Unsaved draft restored';
            if (isConfig(cached.unconfirmedSave)) {
              unconfirmedSave = cached.unconfirmedSave;
              byId('save-recovery').hidden = false;
            }
          }
        } catch { /* Ignore an unavailable or obsolete draft cache. */ }
      }
      cacheDraft();
    }), detectAccounts(), detectHost(), loadPrompts(), detectInstallations()]);
    if (results[0].status === 'rejected') showSettingsError(results[0].reason);
  } finally { loading = false; updateSetup(); await connectionStatus(); }
}
async function save(): Promise<void> {
  if (!baseline || saving || unconfirmedSave) return;
  let submitted: Config | undefined;
  let sent = false;
  byId('saved').hidden = true; clearError(); saveMessage = '';
  try {
    const githubAccount = accountSelect.value;
    if (githubAccount && (accountsError || !accounts?.accounts.some(item => item.login === githubAccount && item.state === 'success' && item.available !== false))) throw new ProtocolError('GITHUB_ACCOUNT_UNAVAILABLE', 'The selected GitHub account is unavailable or has not been checked. Detect accounts and choose a signed-in account, or leave the account empty for now.');
    readAgentProfile('codex'); readAgentProfile('copilot');
    submitted = readForm();
    saving = true; updateSetup(); sent = true;
    const response = await request<unknown>('config.save', submitted);
    let confirmed: Config = submitted;
    let readbackFailed = false;
    if (isConfig(response)) confirmed = response;
    else {
      // Older Hosts may acknowledge a save without returning its normalized values.
      // The acknowledgement is still success even when this optional readback fails.
      try { const readback = await request<Config>('config.get'); if (!isConfig(readback)) throw new Error('Incomplete settings readback.'); confirmed = readback; }
      catch { readbackFailed = true; }
    }
    showConfig(confirmed);
    saveMessage = 'Settings saved';
    const saved = byId('saved'); saved.textContent = readbackFailed ? 'The Host confirmed the save. The saved values could not be reloaded; reload settings to check their normalized values.' : 'Settings saved. New tasks will use this configuration.'; saved.hidden = false;
    byId('save-recovery').hidden = true;
    cacheDraft();
  } catch (error) {
    if (sent && submitted && saveOutcomeUnconfirmed(error)) {
      unconfirmedSave = submitted;
      byId('save-recovery-title').textContent = 'Save outcome unconfirmed';
      byId('save-recovery-message').textContent = `${errorText(error)} Your submitted draft is retained. Check the saved settings before submitting again.`;
      byId('save-recovery').hidden = false; byId('save-comparison').hidden = true;
      byId('check-saved').hidden = false;
      cacheDraft();
    } else {
      saveMessage = 'Settings were not saved · Your draft is retained';
      saving = false; updateSetup();
      showSettingsError(error);
    }
  } finally { saving = false; updateSetup(); void connectionStatus(); }
}
async function checkSaved(): Promise<void> {
  if (!unconfirmedSave || saving) return;
  const button = byId<HTMLButtonElement>('check-saved'); button.disabled = true;
  try {
    const saved = await request<Config>('config.get');
    if (!isConfig(saved)) throw new Error('The Host returned incomplete settings. Your submitted draft is retained.');
    const differences = changedSettings(saved, unconfirmedSave);
    if (!differences.length) {
      unconfirmedSave = undefined; showConfig(saved);
      byId('save-recovery').hidden = true;
      const message = byId('saved'); message.textContent = 'Current saved values match your submitted changes.'; message.hidden = false;
      saveMessage = 'Saved settings match your changes'; clearError();
    } else {
      // There is no config revision/CAS contract. A difference is not proof of a concurrent edit.
      const draft = unconfirmedSave;
      unconfirmedSave = undefined; showConfig(saved); showConfig(draft, false);
      byId('save-recovery-title').textContent = 'Saved settings differ from your draft';
      byId('save-recovery-message').textContent = 'Your draft is retained. Review the differing fields, then save your changes or discard and reload.';
      const comparison = byId('save-comparison'); comparison.hidden = false;
      const currentValues = configValues(saved); const draftValues = configValues(draft);
      const table = element('table', undefined, 'settings-comparison');
      const heading = element('tr'); heading.append(element('th', 'Field'), element('th', 'Currently saved'), element('th', 'Your draft')); table.append(heading);
      for (const key of differences) { const row = element('tr'); row.append(element('th', key), element('td', currentValues[key] || 'Not set'), element('td', draftValues[key] || 'Not set')); table.append(row); }
      comparison.replaceChildren(table);
      saveMessage = 'Comparison complete · Review your retained draft';
    }
    cacheDraft();
  } catch (error) {
    byId('save-recovery-message').textContent = `${errorText(error)} Your draft is retained. Check again when the Host is available.`;
  } finally { button.disabled = false; button.hidden = !unconfirmedSave; updateSetup(); }
}
for (const section of sections) {
  byId(`show-${section}`).addEventListener('click', () => { showSection(section); });
  if (section !== 'overview') byId(`edit-${section}`).addEventListener('click', () => { showSection(section); byId(`show-${section}`).focus(); });
}
for (const agent of agents) {
  byId(`${agent}-installation`).addEventListener('change', () => { selectedCliPaths[agent] = byId<HTMLSelectElement>(`${agent}-installation`).value; tests[agent].error = undefined; renderInstallations(); byId('saved').hidden = true; });
  byId(`test-${agent}`).addEventListener('click', () => { void startTest(agent); });
  byId(`cancel-${agent}`).addEventListener('click', () => { void cancelTest(agent); });
}
byId('permission').addEventListener('change', showPermission);
byId('refresh-installations').addEventListener('click', () => { void detectInstallations(); });
byId('reload-prompts').addEventListener('click', () => { void loadPrompts(); });
for (const target of promptTargets) {
  byId(`${target}-prompt`).addEventListener('change', () => {
    selectedPrompts[target] = byId<HTMLSelectElement>(`${target}-prompt`).value;
    renderPromptHelp(target);
    previewRequest++; byId('prompt-preview').hidden = true;
  });
  byId(`view-${target}-prompt`).addEventListener('click', () => { void viewPrompt(target).catch(error => report(error, byId('prompt-error'))); });
}
accountSelect.addEventListener('change', renderAccountHelp);
for (const id of inputIds) {
  byId(id).addEventListener('input', markEdited);
  byId(id).addEventListener('change', markEdited);
}
byId('reload').addEventListener('click', () => {
  if (baseline && changedSettings(baseline, readForm()).length) { byId('reload-confirmation').hidden = false; byId('keep-editing').focus(); }
  else void load();
});
byId('keep-editing').addEventListener('click', () => { byId('reload-confirmation').hidden = true; byId('reload').focus(); });
byId('discard-reload').addEventListener('click', () => {
  byId('reload-confirmation').hidden = true; byId('save-recovery').hidden = true;
  void load();
});
byId('check-saved').addEventListener('click', () => { void checkSaved(); });
byId('detect-github').addEventListener('click', () => { void detectAccounts(); });
byId('detect').addEventListener('click', () => { void detectHost().finally(connectionStatus); });
byId('config-form').addEventListener('submit', event => {
  event.preventDefault();
  void save();
});
try {
  const restored = JSON.parse(sessionStorage.getItem(testStorageKey) || '{}') as Partial<Record<Agent, AgentTest>>;
  for (const agent of agents) if (restored[agent]) { acceptTest(agent, restored[agent]); scheduleTest(agent); }
} catch { /* Ignore an outdated or unavailable session cache. */ }
void load(true);
