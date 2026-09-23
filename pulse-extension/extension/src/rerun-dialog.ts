import { ProtocolError, isActive } from './policy.js';
import { element, errorText, labeledValue, request } from './ui.js';
import type { Agent, AgentDefaults, Config, ReviewMode, ReviewOptions, Run, TargetSnapshot, TaskExecution } from './types.js';

interface RerunPayload { runId: string; requestId: string; execution?: TaskExecution | null; expectedHeadSha?: string; reviewOptions?: ReviewOptions }
interface PendingRerun { payload: RerunPayload; acceptedRunId?: string }
interface RerunDraft { mode: 'defaults' | 'previous' | 'custom'; execution: TaskExecution; reviewMode: ReviewMode; revision: 'original' | 'current'; currentHead?: string; headConfirmed: boolean }
const pending = new Map<string, PendingRerun>();
const dialogs = new Map<string, Promise<void>>();
const storageKey = (runId: string): string => `pulse-rerun-pending:${runId}`;
const draftKey = (runId: string): string => `pulse-rerun-draft:${runId}`;
const uuid = (value: unknown): value is string => typeof value === 'string' && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(value);
const sha = (value: unknown): value is string => typeof value === 'string' && /^[a-f\d]{40}$/i.test(value);
const option = (value: string, title: string): HTMLOptionElement => { const node = element('option', title); node.value = value; return node; };

/** Compact per-task options shared with targeted verification; omission uses current Host defaults. */
export function verificationExecutionOptions(defaults: AgentDefaults) {
  const node = element('details', undefined, 'dialog-disclosure'); node.append(element('summary', 'Run options'));
  const mode = element('select'); mode.id = 'verify-execution-mode'; mode.append(option('defaults', 'Use current extension defaults'), option('custom', 'Customize this run')); mode.value = 'defaults';
  const agent = element('select'); agent.id = 'verify-agent'; agent.append(option('codex', 'Codex CLI'), option('copilot', 'Copilot CLI'));
  const model = element('input'); model.id = 'verify-model'; model.type = 'text'; model.maxLength = 128; model.placeholder = 'CLI default'; model.autocomplete = 'off';
  const effort = element('select'); effort.id = 'verify-effort';
  const field = (title: string, control: HTMLElement): HTMLElement => { const label = element('label', title); label.append(control); return label; };
  const choices = element('div', undefined, 'columns'); choices.append(field('CLI', agent), field('Model', model), field('Reasoning effort', effort));
  const summary = element('p', undefined, 'muted fine');
  node.append(summary, field('Execution options', mode), choices);
  let locked = false;
  let custom: TaskExecution | undefined;
  const set = (selected: Agent, chosen?: TaskExecution): void => {
    agent.value = selected; model.value = chosen?.model ?? defaults.defaults[selected].model;
    effort.replaceChildren(option('', 'CLI default'), ...defaults.reasoningEfforts[selected].map(value => option(value, value)));
    const value = chosen?.reasoningEffort ?? defaults.defaults[selected].reasoningEffort;
    if (value && !defaults.reasoningEfforts[selected].includes(value)) effort.append(option(value, value));
    effort.value = value;
  };
  const update = (): void => {
    mode.disabled = locked; agent.disabled = model.disabled = effort.disabled = locked || mode.value !== 'custom'; choices.hidden = mode.value !== 'custom';
    summary.textContent = `${agent.value === 'copilot' ? 'Copilot CLI' : 'Codex CLI'} · ${model.value || 'CLI default model'} · ${effort.value || 'CLI default effort'}${mode.value === 'defaults' ? ' · Current extension defaults' : ' · This task only'}`;
  };
  set(defaults.defaultAgent); update();
  mode.addEventListener('change', () => { if (mode.value === 'defaults') { custom = { agent: agent.value as Agent, model: model.value, reasoningEffort: effort.value }; set(defaults.defaultAgent); } else if (custom) set(custom.agent ?? defaults.defaultAgent, custom); update(); });
  agent.addEventListener('change', () => { set(agent.value as Agent); update(); });
  model.addEventListener('input', update); effort.addEventListener('change', update);
  return { node, read: (): TaskExecution | undefined => mode.value === 'custom' ? { agent: agent.value as Agent, model: model.value, reasoningEffort: effort.value } : undefined,
    restore(value?: TaskExecution): void { custom = value; mode.value = value ? 'custom' : 'defaults'; set(value?.agent ?? defaults.defaultAgent, value); update(); },
    lock(value: boolean): void { locked = value; update(); } };
}

function validPending(value: unknown, runId: string): value is PendingRerun {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const stored = value as Partial<PendingRerun>; const payload = stored.payload;
  if (!payload || typeof payload !== 'object' || Array.isArray(payload) || payload.runId !== runId || !uuid(payload.requestId) || (stored.acceptedRunId !== undefined && !uuid(stored.acceptedRunId))) return false;
  if (Object.keys(payload).some(key => !['runId', 'requestId', 'execution', 'expectedHeadSha', 'reviewOptions'].includes(key)) || (payload.expectedHeadSha !== undefined && !sha(payload.expectedHeadSha))) return false;
  if (payload.reviewOptions !== undefined && (!payload.reviewOptions || typeof payload.reviewOptions !== 'object' || Array.isArray(payload.reviewOptions) || Object.keys(payload.reviewOptions).some(key => key !== 'mode') || !['static', 'build-tests', 'ui-e2e'].includes(payload.reviewOptions.mode))) return false;
  const execution = payload.execution;
  return execution === undefined || execution === null || (typeof execution === 'object' && !Array.isArray(execution)
    && !Object.keys(execution).some(key => !['agent', 'model', 'reasoningEffort'].includes(key))
    && (execution.agent === undefined || execution.agent === 'codex' || execution.agent === 'copilot')
    && (execution.model === undefined || typeof execution.model === 'string')
    && (execution.reasoningEffort === undefined || typeof execution.reasoningEffort === 'string'));
}

function savePending(runId: string, value?: PendingRerun): void {
  if (value) pending.set(runId, value); else pending.delete(runId);
  try { if (value) sessionStorage.setItem(storageKey(runId), JSON.stringify(value)); else sessionStorage.removeItem(storageKey(runId)); } catch { /* Keep in-memory retry identity if browser storage is unavailable. */ }
}
function readPending(runId: string): PendingRerun | undefined {
  if (pending.has(runId)) return pending.get(runId);
  try {
    const value: unknown = JSON.parse(sessionStorage.getItem(storageKey(runId)) ?? 'null');
    if (validPending(value, runId)) {
      pending.set(runId, value); return value;
    }
  } catch { /* A missing browser retry cache does not affect saved Host tasks. */ }
  return undefined;
}

/** Opens an explicit rerun confirmation. The original record is never rewritten. */
export function openRerunDialog(run: Run): Promise<void> {
  if (isActive(run.status.state)) return Promise.resolve();
  const existing = dialogs.get(run.runId); if (existing) return existing;
  const opened = showRerunDialog(run).finally(() => dialogs.delete(run.runId));
  dialogs.set(run.runId, opened); return opened;
}

async function showRerunDialog(run: Run): Promise<void> {
  const [config, supported] = await Promise.all([request<Config>('config.get'), request<AgentDefaults>('agents.defaults')]);
  const dialog = element('dialog', undefined, 'rerun-dialog task-preparation-dialog');
  dialog.setAttribute('aria-labelledby', 'rerun-title');
  const form = element('form'); const title = element('h2', 'Run task again'); title.id = 'rerun-title';
  const target = run.task.target;
  const allowRevisionChoice = target?.type === 'pr' && run.task.actionKind !== 'pr-verify';
  const description = element('p', `${target ? `${target.type === 'pr' ? 'Pull request' : 'Issue'} #${target.number} · ` : 'Unlinked historical record · '}${run.task.repository}`, 'muted dialog-context');
  const note = element('p', target ? 'A new task uses the current CLI installation and local permissions. The previous task, changes and evidence are retained.' : 'This historical record has no linked target. Open its PR or Issue to start a new task; any pending request below can still be recovered.', target ? 'muted' : 'notice');
  const error = element('p', undefined, 'error'); error.setAttribute('role', 'alert'); error.hidden = true;
  const mode = element('select'); mode.id = 'rerun-mode';
  mode.append(option('defaults', 'Use current extension defaults'), option('previous', 'Keep previous task overrides'), option('custom', 'Customize this run'));
  mode.value = 'defaults';
  const scoped = run.task.actionKind === 'pr-review' || run.task.actionKind === 'pr-verify';
  const reviewScope = element('select'); reviewScope.id = 'rerun-review-scope';
  if (run.task.actionKind !== 'pr-verify') reviewScope.append(option('static', 'Static code review'));
  reviewScope.append(option('build-tests', 'Build and automated tests'), option('ui-e2e', 'Runtime verification'));
  reviewScope.value = run.task.reviewOptions?.mode ?? 'build-tests';
  const scopeSection = element('section'); scopeSection.hidden = !scoped;
  const scopeLabel = element('label', 'Review scope'); scopeLabel.append(reviewScope);
  const scopeHelp = element('p', 'Choose the work for this run. Runtime verification uses the relevant scenarios; it is not a requirement to run every build or test.', 'muted');
  if (run.task.actionKind === 'pr-verify') scopeHelp.textContent = 'The saved recommendation fixes this verification scope.';
  const previousScope = element('p', run.task.reviewOptions ? `Previous scope: ${run.task.reviewOptions.mode === 'static' ? 'Static code review' : run.task.reviewOptions.mode === 'build-tests' ? 'Build and automated tests' : 'Runtime verification'}.` : 'Previous scope: Not recorded. The new run explicitly defaults to build and automated tests.', 'muted');
  scopeSection.append(scopeLabel, scopeHelp, previousScope);
  const agent = element('select'); agent.id = 'rerun-agent'; agent.append(option('codex', 'Codex CLI'), option('copilot', 'Copilot CLI'));
  const model = element('input'); model.id = 'rerun-model'; model.type = 'text'; model.maxLength = 128; model.placeholder = 'CLI default'; model.autocomplete = 'off';
  model.pattern = '[A-Za-z0-9][A-Za-z0-9._-]{0,127}';
  const effort = element('select'); effort.id = 'rerun-effort';
  const field = (text: string, control: HTMLElement): HTMLElement => { const label = element('label', text); label.append(control); return label; };
  const choices = element('div', undefined, 'columns');
  choices.append(field('CLI', agent), field('Model', model), field('Reasoning effort', effort));
  const executionNote = element('p', undefined, 'muted');
  const cliPath = element('p', undefined, 'muted rerun-path');
  const previous = element('dl', undefined, 'facts');
  previous.append(labeledValue('Previous CLI', run.config.agent === 'copilot' ? 'Copilot CLI' : 'Codex CLI'), labeledValue('Previous requested model', run.config.model || 'CLI default'), labeledValue('Previous requested reasoning effort', run.config.reasoningEffort || 'CLI default'));
  const revision = element('select'); revision.id = 'rerun-revision';
  revision.append(option('original', 'Keep original PR revision'), option('current', 'Review the current PR HEAD')); revision.value = 'original';
  const revisionSection = element('section');
  const revisionText = element('p', undefined, 'rerun-revision');
  const refreshHead = element('button', 'Read current PR HEAD'); refreshHead.type = 'button';
  const confirmHead = element('input'); confirmHead.id = 'rerun-confirm-head'; confirmHead.type = 'checkbox';
  const confirmHeadLabel = field('Use the displayed PR HEAD for this new task', confirmHead); confirmHeadLabel.hidden = true;
  const revisionHelp = element('p', 'The Host checks this exact revision before accepting the new task. It will not switch to a newer commit automatically.', 'muted');
  revisionSection.append(field('PR revision', revision), revisionText, refreshHead, confirmHeadLabel, revisionHelp);
  revisionSection.hidden = !allowRevisionChoice;
  let currentHead: string | undefined; let loadingHead = false; let sending = false;
  const retryNotice = element('p', undefined, 'notice'); retryNotice.hidden = true;
  const actions = element('div', undefined, 'footer-actions dialog-actions');
  const cancel = element('button', 'Cancel'); cancel.type = 'button';
  const submit = element('button', 'Start new run', 'primary'); submit.type = 'submit';
  actions.append(cancel, submit);
  const previousDetails = element('details', undefined, 'dialog-disclosure'); previousDetails.append(element('summary', 'Previous run settings'), previous);
  const executionDetails = element('details', undefined, 'dialog-disclosure'); executionDetails.append(element('summary', 'Run options'), field('Execution options', mode), choices, cliPath, executionNote);
  form.append(title, description, note, scopeSection, revisionSection, executionDetails, previousDetails, retryNotice, error, actions);
  dialog.append(form); document.body.append(dialog);

  function displayedExecution(override?: TaskExecution): Required<TaskExecution> {
    const selected = override?.agent ?? config.agent;
    const defaults = config.agentDefaults[selected];
    return { agent: selected, model: override?.model ?? defaults.model, reasoningEffort: override?.reasoningEffort ?? defaults.reasoningEffort };
  }
  function setExecution(value: Required<TaskExecution>): void {
    agent.value = value.agent; model.value = value.model;
    const values = supported.reasoningEfforts[value.agent];
    effort.replaceChildren(option('', 'CLI default'), ...values.map(item => option(item, item)));
    if (value.reasoningEffort && !values.includes(value.reasoningEffort)) effort.append(option(value.reasoningEffort, `${value.reasoningEffort} (previous setting)`));
    effort.value = value.reasoningEffort;
  }
  function updateControls(): void {
    const saved = readPending(run.runId); const locked = sending || !!saved;
    mode.disabled = locked; agent.disabled = model.disabled = effort.disabled = locked || mode.value !== 'custom'; choices.hidden = mode.value !== 'custom';
    reviewScope.disabled = locked || run.task.actionKind === 'pr-verify';
    revision.disabled = locked || loadingHead || !allowRevisionChoice; refreshHead.disabled = locked || loadingHead || !allowRevisionChoice;
    confirmHead.disabled = locked || loadingHead; cancel.disabled = sending;
    refreshHead.hidden = revision.value !== 'current'; confirmHeadLabel.hidden = revision.value !== 'current';
    revisionText.textContent = revision.value === 'current' ? currentHead ? `PR HEAD to review: ${currentHead}` : 'Read the current PR HEAD, then confirm the displayed commit.' : `Original PR revision: ${run.task.expectedHeadSha ?? 'Not recorded'}`;
    submit.disabled = sending || loadingHead || !saved && (!target || allowRevisionChoice && revision.value === 'current' && (!currentHead || !confirmHead.checked));
    submit.textContent = sending ? 'Starting…' : saved?.acceptedRunId ? 'Open accepted task' : saved ? 'Retry same request' : 'Start new run';
    cancel.textContent = saved ? 'Close' : 'Cancel'; retryNotice.hidden = !saved;
    retryNotice.textContent = saved?.acceptedRunId ? 'The Host accepted this task. Open its saved task details.' : saved ? 'The previous request has no confirmed response. Retry the same request and options to recover it without creating a duplicate.' : '';
    cliPath.textContent = `Selected installation: ${config.cliSelections[agent.value as Agent] || 'Choose an installation in Settings'}`;
    executionNote.textContent = mode.value === 'custom' ? 'These model and effort values apply only to this new task. An empty model or CLI default effort lets the selected CLI choose.' : mode.value === 'previous' ? 'Previous task overrides are retained. Fields without an override use current extension defaults when the task is accepted.' : 'Previous task overrides are cleared. Shown defaults are from current Settings; the Host resolves them when the task is accepted.';
  }
  let completed = false;
  const preserveDraft = (): void => {
    if (readPending(run.runId) || completed) return;
    const value: RerunDraft = { mode: mode.value as RerunDraft['mode'], execution: { agent: agent.value as Agent, model: model.value, reasoningEffort: effort.value }, reviewMode: reviewScope.value as ReviewMode, revision: revision.value as RerunDraft['revision'], ...(currentHead ? { currentHead } : {}), headConfirmed: confirmHead.checked };
    try { sessionStorage.setItem(draftKey(run.runId), JSON.stringify(value)); } catch { /* The open dialog keeps unsent choices in memory. */ }
  };
  const saved = readPending(run.runId);
  if (saved) {
    if (saved.payload.reviewOptions) reviewScope.value = saved.payload.reviewOptions.mode;
    else if (scoped) { reviewScope.append(option('', 'Scope not recorded in the pending request')); reviewScope.value = ''; previousScope.textContent = 'Recovering this pending request preserves its original unrecorded scope.'; }
    mode.value = saved.payload.execution === null ? 'defaults' : saved.payload.execution ? 'custom' : 'previous';
    setExecution(displayedExecution(saved.payload.execution ?? (mode.value === 'previous' ? run.task.execution : undefined)));
    if (saved.payload.expectedHeadSha) { revision.value = 'current'; currentHead = saved.payload.expectedHeadSha; confirmHead.checked = true; }
  } else {
    setExecution(displayedExecution());
    try {
      const draft = JSON.parse(sessionStorage.getItem(draftKey(run.runId)) ?? 'null') as RerunDraft | null;
      if (draft && ['defaults', 'previous', 'custom'].includes(draft.mode) && ['original', 'current'].includes(draft.revision) && typeof draft.headConfirmed === 'boolean' &&
        ['static', 'build-tests', 'ui-e2e'].includes(draft.reviewMode) && (draft.currentHead === undefined || sha(draft.currentHead)) &&
        validPending({ payload: { runId: run.runId, requestId: run.runId, execution: draft.execution } }, run.runId)) {
        mode.value = draft.mode; setExecution(mode.value === 'custom' ? displayedExecution(draft.execution) : displayedExecution(mode.value === 'previous' ? run.task.execution : undefined));
        if (scoped && run.task.actionKind !== 'pr-verify') reviewScope.value = draft.reviewMode;
        if (allowRevisionChoice) { revision.value = draft.revision; currentHead = draft.currentHead; confirmHead.checked = Boolean(currentHead && draft.headConfirmed); }
      }
    } catch { /* Ignore malformed optional drafts; pending request recovery is separate. */ }
  }
  updateControls();
  let lastMode = mode.value; let customExecution: Required<TaskExecution> | undefined;
  mode.addEventListener('change', () => {
    if (lastMode === 'custom') customExecution = { agent: agent.value as Agent, model: model.value, reasoningEffort: effort.value };
    if (mode.value !== 'custom') setExecution(displayedExecution(mode.value === 'previous' ? run.task.execution : undefined)); else if (customExecution) setExecution(customExecution);
    lastMode = mode.value; updateControls();
  });
  agent.addEventListener('change', () => { setExecution(displayedExecution({ agent: agent.value as Agent })); updateControls(); });
  revision.addEventListener('change', () => { currentHead = undefined; confirmHead.checked = false; error.hidden = true; updateControls(); });
  confirmHead.addEventListener('change', updateControls);
  form.addEventListener('change', preserveDraft); form.addEventListener('input', preserveDraft);
  refreshHead.addEventListener('click', () => { void (async () => {
    if (loadingHead || sending || readPending(run.runId) || !allowRevisionChoice || target?.type !== 'pr') return;
    loadingHead = true; currentHead = undefined; confirmHead.checked = false; error.hidden = true; updateControls();
    try {
      const fresh = await request<TargetSnapshot>('targets.get', { target: { type: 'pr', number: target.number } });
      if (fresh.target?.type !== 'pr' || fresh.target.number !== target.number || !sha(fresh.headSha)) throw new Error('The Host returned a different PR or an invalid revision. Read the current PR HEAD again.');
      currentHead = fresh.headSha.toLowerCase();
    } catch (failure) { error.textContent = errorText(failure); error.hidden = false; }
    finally { loadingHead = false; updateControls(); }
  })(); });

  let finish: () => void = () => {};
  const closed = new Promise<void>(resolve => { finish = resolve; });
  dialog.addEventListener('close', () => { preserveDraft(); dialog.remove(); finish(); });
  dialog.addEventListener('cancel', event => { if (sending) event.preventDefault(); });
  cancel.addEventListener('click', () => dialog.close());
  form.addEventListener('submit', event => { event.preventDefault(); void (async () => {
    if (submit.disabled || sending) return;
    const existing = readPending(run.runId);
    const payload: RerunPayload = existing?.payload ?? {
      runId: run.runId, requestId: crypto.randomUUID(),
      ...(mode.value === 'defaults' ? { execution: null } : mode.value === 'custom' ? { execution: { agent: agent.value as Agent, model: model.value, reasoningEffort: effort.value } } : {}),
      ...(allowRevisionChoice && revision.value === 'current' ? { expectedHeadSha: currentHead! } : {}),
      ...(scoped ? { reviewOptions: { mode: reviewScope.value as ReviewMode } } : {})
    };
    const attempt = existing ?? { payload }; savePending(run.runId, attempt);
    try { sessionStorage.removeItem(draftKey(run.runId)); } catch { /* The pending request now preserves these choices. */ }
    sending = true; error.hidden = true; updateControls();
    try {
      if (!attempt.acceptedRunId) {
        const next = await request<Run>('tasks.rerun', payload);
        if (!uuid(next.runId)) throw new Error('The Host returned an invalid task identifier. Retry the same request to recover its accepted task.');
        attempt.acceptedRunId = next.runId; savePending(run.runId, attempt);
      }
      await chrome.tabs.create({ url: chrome.runtime.getURL(`details.html?runId=${encodeURIComponent(attempt.acceptedRunId)}`) });
      completed = true; savePending(run.runId); dialog.close();
    } catch (failure) {
      // These errors are raised before admission. Other errors may hide an accepted
      // request, so keep its identity and freeze its options until it is recovered.
      if (!attempt.acceptedRunId && failure instanceof ProtocolError && ['INVALID_REQUEST', 'INVALID_EXECUTION', 'STALE_CONTEXT', 'REPOSITORY_BUSY'].includes(failure.code)) savePending(run.runId);
      error.textContent = errorText(failure); error.hidden = false;
    } finally { sending = false; updateControls(); }
  })(); });
  dialog.showModal();
  await closed;
}
