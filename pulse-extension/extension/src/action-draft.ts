import { initialResultDraft, resultDraftKey, validOperationId, validResultDraftContent, type ResultDraftContent } from './action-model.js';
import { resultActionAvailability } from './details-model.js';
import { loadFullResult } from './result-loader.js';
import type { ResultNextAction, Run, WebActionSummary } from './types.js';
import { byId, clearError, connectionStatus, element, labeledValue, report, request } from './ui.js';

/** Editable content lives before preparation; the resulting operation remains immutable. */
export function initResultDraftWorkspace(runId: string, proposalId: string): void {
  let run: Run | undefined;
  let proposal: ResultNextAction | undefined;
  let existing: WebActionSummary | undefined;
  let content: ResultDraftContent | undefined;
  let storageKey = '';
  let busy = false;
  let current = false;
  let available = false;
  let attempt: { id: string; title: string; body: string; prior?: string } | undefined;
  const title = byId<HTMLInputElement>('draft-pr-title');
  const body = byId<HTMLTextAreaElement>('draft-pr-body');
  const submit = byId<HTMLButtonElement>('prepare-draft-pr');
  const refresh = byId<HTMLButtonElement>('refresh');
  const sourceResult = byId<HTMLAnchorElement>('draft-source-result');
  sourceResult.href = chrome.runtime.getURL(`details.html?${new URLSearchParams({ runId })}`);
  byId('page-title').textContent = 'Create Draft PR';
  byId('page-description').textContent = 'Review the saved candidate and prepare the pull request content.';

  function frozen(): boolean { return Boolean(existing && (['prepared', 'submitting', 'unknown', 'partial', 'succeeded'].includes(existing.status) || existing.status === 'failed' && existing.retryAllowed !== true)); }
  function update(): void {
    title.disabled = body.disabled = busy || !current || frozen();
    refresh.disabled = busy;
    submit.textContent = existing?.status === 'succeeded' ? 'Open created PR receipt' : frozen() ? 'Review existing action' : 'Review GitHub confirmation';
    submit.disabled = busy || !current || !frozen() && (!available || !title.value.trim() || title.value.length > 256 || body.value.length > 60000);
    byId('draft-content-preview').textContent = body.value || 'No description written.';
  }
  function preserve(): void {
    content = { title: title.value, body: body.value };
    try { if (storageKey) localStorage.setItem(storageKey, JSON.stringify(content)); }
    catch { byId('draft-storage-notice').hidden = false; }
    update();
  }
  function render(value: Run, action: ResultNextAction): void {
    run = value; proposal = action;
    const nextKey = resultDraftKey(value, action);
    if (storageKey !== nextKey || !content) {
      storageKey = nextKey; content = initialResultDraft(value, action);
      try {
        const stored: unknown = JSON.parse(localStorage.getItem(storageKey) ?? 'null');
        if (validResultDraftContent(stored)) content = stored;
        const savedAttempt = JSON.parse(localStorage.getItem(`${storageKey}:attempt`) ?? 'null') as typeof attempt;
        if (savedAttempt && validOperationId(savedAttempt.id) && typeof savedAttempt.title === 'string' && typeof savedAttempt.body === 'string') attempt = savedAttempt;
      } catch { /* A missing or malformed local draft does not change saved Host data. */ }
    }
    title.value = content.title; body.value = content.body;
    const target = value.task.target;
    const pr = action.pullRequest;
    const availability = resultActionAvailability(value, action);
    available = availability.enabled;
    const facts = byId('draft-pr-facts');
    facts.replaceChildren(labeledValue('Repository', value.task.repository), labeledValue('Linked Issue', target?.type === 'issue' ? `#${target.number}` : 'Not recorded'),
      labeledValue('Source branch', pr?.head || 'Not recorded'), labeledValue('Base branch', pr?.base || 'Not recorded'),
      labeledValue('Tested source SHA', pr?.sourceHeadSha || 'Not recorded'), labeledValue('Pull request type', 'Draft'));
    const context = byId('draft-pr-context');
    context.replaceChildren(labeledValue('Task worktree', value.config.repoFolder || 'Not recorded'), labeledValue('Task branch', value.config.worktreeBranch || 'Not recorded'),
      labeledValue('Candidate assessment', value.result?.assessment ? `${value.result.assessment.status} · ${value.result.assessment.summary}` : 'Not recorded'));
    const evidence = byId('draft-verification'); evidence.replaceChildren();
    for (const check of value.result?.validation ?? []) {
      const item = element('article', undefined, 'action-content-block');
      item.append(element('h3', `${check.name} · ${check.status}`));
      if (check.details) item.append(element('p', check.details));
      if (check.evidence?.length) { const list = element('ul'); for (const text of check.evidence) list.append(element('li', text)); item.append(list); }
      evidence.append(item);
    }
    if (!evidence.childElementCount) evidence.append(element('p', 'No saved verification records.', 'muted'));
    const blockers = byId('draft-pr-blockers'); blockers.replaceChildren();
    for (const reason of availability.reasons) blockers.append(element('li', reason));
    byId('draft-pr-requirements').hidden = !availability.reasons.length;
    byId('draft-pr-conclusion').textContent = existing?.status === 'succeeded' ? 'A pull request has already been created' : frozen() ? 'A GitHub action already exists for this candidate' : available ? 'The saved candidate is ready for PR preparation' : 'Draft PR preparation needs more information';
    byId('draft-pr-heading').textContent = existing?.status === 'succeeded' ? 'No recommended action' : frozen() ? 'Review the existing GitHub action' : 'Create Draft PR';
    byId('draft-pr-reason').textContent = existing?.status === 'succeeded' ? 'No recommended action. Open the existing receipt to inspect the created pull request.' : frozen() ? 'Open the existing action to inspect its exact frozen content and outcome. Your source draft remains saved.' : 'Prepare a Draft PR for this Issue. The GitHub confirmation checks the actual account, source commit, base branch, and existing pull requests before submission.';
    byId('draft-preparation-state').textContent = frozen() ? 'The existing operation is preserved. Editing requires cancelling an unsubmitted confirmation first.' : 'Not yet checked on GitHub. Saved branch and verification details describe the recorded candidate.';
    byId('action-loading').hidden = true; byId('draft-workspace').hidden = false;
    update();
  }
  async function load(): Promise<void> {
    if (busy) return;
    busy = true; current = false; update(); clearError();
    try {
      const [loaded, actions] = await Promise.all([request<Run>('tasks.get', { runId }), request<{ operations: WebActionSummary[] }>('resultActions.list', { runId })]);
      if (loaded.runId !== runId) throw new Error('The Host returned a different source task.');
      const value = await loadFullResult(loaded);
      const action = value.result?.nextActions?.find(item => item.proposalId === proposalId && item.kind === 'create-pr');
      if (!action || value.task.target?.type !== 'issue') throw new Error('This saved Issue result does not contain the selected Draft PR proposal. Open the source result to inspect its requirements.');
      existing = actions.operations.filter(item => item.proposalId === proposalId).sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0];
      current = true; render(value, action);
    } catch (error) { byId('action-loading').hidden = true; report(error); }
    finally { busy = false; update(); void connectionStatus(); }
  }
  async function prepare(): Promise<void> {
    if (submit.disabled || !current || !run || !proposal || !content) return;
    if (frozen() && existing) { location.href = chrome.runtime.getURL(`action.html?operationId=${encodeURIComponent(existing.operationId)}`); return; }
    preserve(); busy = true; update(); clearError();
    try {
      if (!attempt || attempt.title !== content.title || attempt.body !== content.body || attempt.prior !== existing?.operationId) {
        attempt = { id: crypto.randomUUID(), title: content.title, body: content.body, prior: existing?.operationId };
        try { localStorage.setItem(`${storageKey}:attempt`, JSON.stringify(attempt)); } catch { byId('draft-storage-notice').hidden = false; }
      }
      const prepared = await request<WebActionSummary>('resultActions.prepare', { runId, proposalId, attemptId: attempt.id, content, ...(existing ? { retry: true } : {}) });
      const target = run.task.target!;
      if (!validOperationId(prepared.operationId) || prepared.kind !== 'create-pr' || prepared.runId !== runId || prepared.proposalId !== proposalId || prepared.target.type !== 'issue' || prepared.target.number !== target.number || prepared.target.repository.toLowerCase() !== run.task.repository.toLowerCase()) throw new Error('The Host returned a different Draft PR preparation. Refresh this source result.');
      location.href = chrome.runtime.getURL(`action.html?operationId=${encodeURIComponent(prepared.operationId)}`);
    } catch (error) { report(error); }
    finally { busy = false; update(); }
  }
  title.addEventListener('input', preserve); body.addEventListener('input', preserve);
  submit.addEventListener('click', () => { void prepare(); });
  refresh.addEventListener('click', () => { void load(); });
  byId('draft-preview-toggle').addEventListener('click', () => {
    const details = byId<HTMLDetailsElement>('draft-description-preview'); details.open = !details.open;
    byId('draft-preview-toggle').textContent = details.open ? 'Hide preview' : 'Preview full text';
  });
  void load();
}
