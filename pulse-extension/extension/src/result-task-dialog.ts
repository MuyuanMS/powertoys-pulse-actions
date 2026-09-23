import { ProtocolError } from './policy.js';
import { verificationExecutionOptions } from './rerun-dialog.js';
import { actionLabels, element, errorText, request } from './ui.js';
import type { AgentDefaults, ResultNextAction, ResultPlan, Run, TaskExecution } from './types.js';

interface Pending { runId: string; proposalId: string; requestId: string; execution?: TaskExecution; plan: ResultPlan; acceptedRunId?: string }
const pending = new Map<string, Pending>(); const dialogs = new Map<string, Promise<void>>();
const uuid = (value: unknown): value is string => typeof value === 'string' && /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(value);
const storageKey = (key: string): string => `pulse-plan-start:${key}`;
const draftKey = (key: string, plan: ResultPlan): string => `pulse-plan-draft:${key}:${plan.id}`;
function readDraft(key: string, plan: ResultPlan): { execution?: TaskExecution; acknowledged?: boolean } | undefined {
  try {
    const value = JSON.parse(sessionStorage.getItem(draftKey(key, plan)) ?? 'null');
    const execution = value?.execution;
    if (value && JSON.stringify(value.plan) === JSON.stringify(plan) && (value.acknowledged === undefined || typeof value.acknowledged === 'boolean') && (execution === undefined || execution && typeof execution === 'object' && !Array.isArray(execution) &&
      Object.keys(execution).every(name => ['agent', 'model', 'reasoningEffort'].includes(name)) &&
      (execution.agent === undefined || ['codex', 'copilot'].includes(execution.agent)) && (execution.model === undefined || typeof execution.model === 'string') && (execution.reasoningEffort === undefined || typeof execution.reasoningEffort === 'string'))) return value;
  } catch { /* An unsent draft is optional. */ }
  return undefined;
}
function saved(key: string): Pending | undefined {
  if (pending.has(key)) return pending.get(key);
  try { const value = JSON.parse(sessionStorage.getItem(storageKey(key)) ?? 'null') as Pending | null;
    if (value && `${value.runId}:${value.proposalId}` === key && uuid(value.requestId) && value.plan && (value.acceptedRunId === undefined || uuid(value.acceptedRunId))) { pending.set(key, value); return value; }
  } catch { /* Existing Host request receipts remain authoritative. */ }
  return undefined;
}
function save(key: string, value?: Pending): void {
  if (value) pending.set(key, value); else pending.delete(key);
  try { if (value) sessionStorage.setItem(storageKey(key), JSON.stringify(value)); else sessionStorage.removeItem(storageKey(key)); } catch { /* Preserve the receipt in memory. */ }
}
export function openResultTaskDialog(run: Run, action: ResultNextAction): Promise<void> {
  const key = `${run.runId}:${action.proposalId}`;
  if (dialogs.has(key)) return dialogs.get(key)!;
  const opening = show(run, action, key).finally(() => dialogs.delete(key)); dialogs.set(key, opening); return opening;
}
async function show(run: Run, action: ResultNextAction, key: string): Promise<void> {
  if (action.kind !== 'start-task' || !action.proposalId || !action.planId) throw new Error('This proposal does not identify a saved task plan.');
  const source = run.result?.plans?.find(plan => plan.id === action.planId && plan.kind === action.taskKind);
  if (!source || run.task.target?.type !== 'issue') throw new Error('The saved plan or its original Issue target is unavailable.');
  const defaults = await request<AgentDefaults>('agents.defaults');
  const existing = saved(key); const plan = existing?.plan ?? source;
  const draft = readDraft(key, plan); const verification = plan.kind === 'issue-verify';
  const options = verificationExecutionOptions(defaults); options.restore(existing ? existing.execution : draft?.execution); options.lock(Boolean(existing));
  const dialog = element('dialog', undefined, 'rerun-dialog task-preparation-dialog'); dialog.setAttribute('aria-labelledby', 'plan-start-title');
  const form = element('form'); const title = element('h2', verification ? 'Review verification requirements' : 'Review saved task plan'); title.id = 'plan-start-title';
  form.append(title, element('p', `${run.task.repository} · Issue #${run.task.target.number} · ${actionLabels[plan.kind]}`, 'muted dialog-context'), element('p', plan.summary));
  const add = (parent: HTMLElement, label: string, values: string[]): void => { if (!values.length) return; const section = element('section', undefined, 'dialog-section'); section.append(element('h3', label)); const list = element('ul', undefined, 'plain-list'); for (const value of values) list.append(element('li', value)); section.append(list); parent.append(section); };
  add(form, 'Saved plan', plan.steps); add(form, 'Expected results', plan.acceptanceCriteria); add(form, 'Prepare before starting', plan.prerequisites);
  const evidence = element('details', undefined, 'dialog-disclosure'); evidence.append(element('summary', 'Source evidence and plan identity')); add(evidence, 'Source evidence', plan.evidence);
  evidence.append(element('p', `Parent task: ${run.runId}\nPlan: ${plan.id}\nProposal: ${action.proposalId}`, 'path muted fine')); form.append(evidence);
  form.append(options.node, element('p', 'This starts the saved plan as a new task. The original investigation and plan remain unchanged.', 'muted fine'));
  const acknowledge = element('input'); acknowledge.type = 'checkbox'; acknowledge.id = 'plan-requirements-confirmed'; acknowledge.checked = Boolean(existing || draft?.acknowledged); acknowledge.disabled = Boolean(existing);
  if (verification) { const label = element('label', undefined, 'checkbox dialog-confirmation'); label.append(acknowledge, element('span', 'I reviewed the saved scope and prepared the listed environment. Check it before running.')); form.append(label, element('p', 'Confirmation does not establish that the environment or verification has passed.', 'muted fine')); }
  const error = element('p', undefined, 'error'); error.hidden = true; error.setAttribute('role', 'alert');
  const note = element('p', undefined, 'notice'); note.hidden = !existing; note.textContent = existing ? 'Recover the same request and frozen options before starting another task.' : '';
  const startLabel = verification ? 'Start verification' : 'Start planned task';
  const actions = element('div', undefined, 'footer-actions dialog-actions'); const cancel = element('button', 'Cancel'); cancel.type = 'button'; const start = element('button', existing?.acceptedRunId ? 'Open accepted task' : existing ? 'Recover task request' : startLabel, 'primary'); start.type = 'submit'; start.id = 'start-planned-task'; start.disabled = verification && !acknowledge.checked;
  actions.append(cancel, start); form.append(note, error, actions); dialog.append(form); document.body.append(dialog);
  let sending = false; let completed = false; let finish: () => void = () => {}; const closed = new Promise<void>(resolve => { finish = resolve; });
  const preserveDraft = (): void => { if (saved(key) || completed) return; try { sessionStorage.setItem(draftKey(key, plan), JSON.stringify({ plan, execution: options.read(), acknowledged: acknowledge.checked })); } catch { /* Retain the current dialog state in memory. */ } };
  options.node.addEventListener('input', preserveDraft); options.node.addEventListener('change', preserveDraft);
  acknowledge.addEventListener('change', () => { start.disabled = sending || verification && !acknowledge.checked && !saved(key); preserveDraft(); });
  cancel.addEventListener('click', () => dialog.close()); dialog.addEventListener('cancel', event => { if (sending) event.preventDefault(); }); dialog.addEventListener('close', () => { preserveDraft(); dialog.remove(); finish(); });
  form.addEventListener('submit', event => { event.preventDefault(); void (async () => {
    if (sending || start.disabled) return;
    const attempt = saved(key) ?? { runId: run.runId, proposalId: action.proposalId!, requestId: crypto.randomUUID(), plan: structuredClone(plan), ...(options.read() ? { execution: options.read() } : {}) };
    save(key, attempt); sending = true; start.disabled = cancel.disabled = acknowledge.disabled = true; options.lock(true); error.hidden = true;
    try { sessionStorage.removeItem(draftKey(key, plan)); } catch { /* The pending request now preserves these choices. */ }
    try {
      if (!attempt.acceptedRunId) {
        const accepted = await request<Run>('tasks.startFromResult', { runId: attempt.runId, proposalId: attempt.proposalId, requestId: attempt.requestId, ...(attempt.execution ? { execution: attempt.execution } : {}) });
        const task = accepted.task; const origin = task?.planSource;
        if (!uuid(accepted.runId) || !task || task.requestId !== attempt.requestId || task.actionKind !== attempt.plan.kind || origin?.parentRunId !== run.runId || origin.proposalId !== attempt.proposalId || origin.planId !== attempt.plan.id ||
          task.repository?.toLowerCase() !== run.task.repository.toLowerCase() || task.target?.type !== 'issue' || task.target.number !== run.task.target?.number || origin.repository?.toLowerCase() !== run.task.repository.toLowerCase() || origin.target?.type !== 'issue' || origin.target.number !== run.task.target.number)
          throw new Error('The Host response did not match the confirmed plan. Recover this same request to check its outcome.');
        attempt.acceptedRunId = accepted.runId; save(key, attempt);
      }
      await chrome.tabs.create({ url: chrome.runtime.getURL(`details.html?runId=${encodeURIComponent(attempt.acceptedRunId)}`) }); completed = true; save(key); try { sessionStorage.removeItem(draftKey(key, plan)); } catch { /* The accepted task is already saved by the Host. */ } dialog.close();
    } catch (failure) {
      if (!attempt.acceptedRunId && failure instanceof ProtocolError && ['INVALID_REQUEST', 'INVALID_EXECUTION', 'PLAN_MISMATCH', 'PROPOSAL_NOT_FOUND', 'PLAN_CHANGED', 'RESULT_NOT_ACTIONABLE'].includes(failure.code)) { save(key); options.lock(false); }
      error.textContent = errorText(failure); error.hidden = false; note.hidden = !saved(key); note.textContent = 'The request is not confirmed. Recover this same request and options before starting another one.';
    } finally { sending = false; cancel.disabled = false; acknowledge.disabled = Boolean(saved(key)); start.disabled = verification && !acknowledge.checked && !saved(key); start.textContent = saved(key)?.acceptedRunId ? 'Open accepted task' : saved(key) ? 'Recover task request' : startLabel; }
  })(); });
  dialog.showModal(); await closed;
}
