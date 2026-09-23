import { actionButtonLabel, actionDescription, actionPresentation, actionStepLabel, canConfirmAction, canReconcileAction, canRetryAction, duplicateCommentBody, preparedResultDraftKey, resultDraftSourceUrl, safeActionUrl, validOperationId, withActionSummary } from './action-model.js';
import { initResultDraftWorkspace } from './action-draft.js';
import type { WebActionPreview, WebActionSummary } from './types.js';
import { byId, clearError, connectionStatus, date, element, errorText, initNavigation, labeledValue, report, request } from './ui.js';

const operationId = new URLSearchParams(location.search).get('operationId');
const attemptKey = `pulse-web-action-attempt:${operationId}`;
let preview: WebActionPreview | undefined;
let busy = false;
let attempted = false;
let previewCurrent = false;
let retryAttemptId: string | undefined;
let duplicateBody: string | undefined;
try { attempted = sessionStorage.getItem(attemptKey) === 'submitted'; } catch { /* Keep the submission lock in memory if session storage is unavailable. */ }

const statusLabels: Record<WebActionSummary['status'], string> = {
  prepared: 'Awaiting confirmation', submitting: 'Submitting', succeeded: 'Completed', failed: 'Failed', cancelled: 'Cancelled', partial: 'Partly completed', unknown: 'Outcome unknown',
};

function updateButtons(): void {
  const confirm = byId<HTMLButtonElement>('confirm-action');
  confirm.disabled = !previewCurrent || !canConfirmAction(preview, busy, attempted);
  if (preview) {
    confirm.textContent = preview.kind === 'close-as-duplicate' && preview.status === 'partial' && preview.resumeRequired ? 'Continue closing as duplicate' : actionButtonLabel(preview.draft);
    confirm.className = preview.kind === 'merge-pr' ? 'action-merge' : preview.kind === 'close-as-duplicate' ? 'danger' : 'primary';
    confirm.hidden = preview.status !== 'prepared' && !(preview.kind === 'close-as-duplicate' && preview.status === 'partial' && preview.resumeRequired);
  }
  const cancel = byId<HTMLButtonElement>('cancel-action');
  cancel.disabled = !previewCurrent || busy || attempted || preview?.status !== 'prepared';
  cancel.hidden = Boolean(preview && preview.status !== 'prepared');
  const reconcile = byId<HTMLButtonElement>('reconcile-action');
  reconcile.disabled = !previewCurrent || !canReconcileAction(preview, busy);
  reconcile.hidden = preview?.status !== 'unknown';
  byId('action-reconcile-help').hidden = preview?.status !== 'unknown';
  const retry = byId<HTMLButtonElement>('retry-action');
  retry.hidden = !canRetryAction(preview, false);
  retry.disabled = !previewCurrent || !canRetryAction(preview, busy);
  retry.textContent = preview?.kind === 'trigger-ci' && preview.status === 'succeeded' ? 'Review another CI run' : 'Prepare another attempt';
  const duplicateEditor = document.getElementById('duplicate-explanation') as HTMLTextAreaElement | null;
  if (duplicateEditor) duplicateEditor.disabled = busy || attempted || !preview?.bodyEditable;
  byId<HTMLButtonElement>('refresh').disabled = busy || !validOperationId(operationId);
  const refreshOutcome = byId<HTMLButtonElement>('check-action-status');
  refreshOutcome.hidden = !(preview?.status === 'submitting' || preview?.status === 'prepared' && attempted);
  refreshOutcome.disabled = busy;
  const edit = byId<HTMLButtonElement>('edit-action');
  edit.hidden = !preview?.runId || !preview.proposalId || preview.kind !== 'create-pr' || preview.status !== 'prepared' || attempted;
  edit.disabled = busy || !previewCurrent;
  byId('action-more').querySelector('summary')!.textContent = preview && actionPresentation(preview, attempted).nextStep === 'No recommended action' ? 'Choose action' : 'More actions';
}

function safeResultUrl(url: string | undefined): string | undefined {
  return safeActionUrl(url, preview?.target.repository ?? '') ?? safeActionUrl(url, preview?.draft.assignmentTarget?.repository ?? '') ?? safeActionUrl(url, preview?.draft.duplicateOf?.repository ?? '');
}

function resultLink(label: string, url: string | undefined): HTMLElement {
  const safe = safeResultUrl(url);
  if (!safe) return element('span', label);
  const anchor = element('a', label);
  anchor.href = safe; anchor.target = '_blank'; anchor.rel = 'noopener noreferrer';
  return anchor;
}

function contentBlock(title: string, body: string | undefined): HTMLElement {
  const section = element('div', undefined, 'action-content-block');
  section.append(element('h3', title), body?.length ? element('pre', body, 'action-body') : element('p', 'No additional text.', 'muted fine'));
  return section;
}

function renderDraft(value: WebActionPreview): void {
  const node = byId('action-draft');
  node.replaceChildren(element('h3', value.status === 'prepared' ? 'Prepared content' : 'Recorded content'));
  const draft = value.draft;
  if (draft.kind === 'close-as-duplicate') {
    const original = draft.duplicateOf;
    node.append(element('h3', 'Original Issue'));
    if (original) node.append(resultLink(`${original.repository} #${original.number}`, original.url));
    else node.append(element('p', 'The original Issue is unavailable. Refresh this action before confirming.', 'notice'));
    if (duplicateBody === undefined || !value.bodyEditable) duplicateBody = draft.body ?? '';
    if (value.bodyEditable && value.status === 'prepared' && !attempted) {
      const label = element('label', 'Explanation'); const editor = element('textarea'); editor.rows = 6; editor.value = duplicateBody; editor.id = 'duplicate-explanation';
      const outgoing = contentBlock('Complete comment', duplicateCommentBody(value, duplicateBody)); outgoing.id = 'duplicate-complete-comment';
      editor.addEventListener('input', () => { duplicateBody = editor.value; outgoing.replaceChildren(element('h3', 'Complete comment'), element('pre', duplicateCommentBody(value, duplicateBody), 'action-body')); });
      label.append(editor); node.append(label, element('p', 'The original Issue link is fixed and is included in the comment below.', 'muted fine'), outgoing);
    } else node.append(contentBlock('Confirmed comment', duplicateCommentBody(value)));
    node.append(element('p', value.resumeRequired ? 'The linked comment is already confirmed. Continuing only closes this Issue; it does not post the comment again.' : 'After posting this comment, the extension closes the current Issue as a duplicate of the original Issue.', 'muted'));
  } else if (draft.kind === 'create-pr' && draft.pullRequest) {
    const facts = element('dl', undefined, 'facts');
    facts.append(labeledValue('Head branch', draft.pullRequest.head), labeledValue('Base branch', draft.pullRequest.base), labeledValue('Pull request type', draft.pullRequest.draft ? 'Draft' : 'Ready for review'));
    node.append(facts, contentBlock('Title', draft.pullRequest.title), contentBlock('Description', draft.pullRequest.body));
    node.append(element('p', 'Creation scope: one pull request from the existing remote source branch. Local files, commits and branches are not published by this action.', 'muted fine'));
  } else if (draft.kind === 'merge-pr') {
    node.append(element('p', 'Merge method: squash. The extension verifies the reviewed head SHA again before submitting.'));
  } else {
    node.append(contentBlock(draft.kind === 'review' || draft.kind === 'approve' ? 'Review summary' : 'Comment', draft.body));
  }
  if (draft.kind === 'review' && draft.review) {
    const inline = draft.review.comments ?? [];
    node.append(element('h3', `Selected inline comments (${inline.length})`));
    if (!inline.length) node.append(element('p', 'No inline comments selected.', 'muted fine'));
    for (const comment of inline) {
      const block = element('article', undefined, 'action-review-comment');
      const start = comment.startLine === undefined ? '' : `${comment.startSide ?? comment.side} ${comment.startLine} – `;
      block.append(element('p', comment.path, 'action-comment-path'), element('p', `${start}${comment.side} ${comment.line}`, 'muted fine'), element('pre', comment.body, 'action-body'));
      node.append(block);
    }
    const general = draft.review.generalComments ?? [];
    node.append(element('h3', `Selected general comments (${general.length})`));
    if (!general.length) node.append(element('p', 'No general comments selected.', 'muted fine'));
    for (const [index, comment] of general.entries()) node.append(contentBlock(`General comment ${index + 1}`, comment.body));
  }
  if (draft.assignSelf) {
    const target = draft.assignmentTarget ?? draft.target;
    node.append(element('p', `Also assign ${target.repository} ${target.type === 'pr' ? 'pull request' : 'issue'} #${target.number} to ${value.account ? `@${value.account}` : 'the selected GitHub account'} after posting the comment.`, 'notice'));
  }
}

function renderResults(value: WebActionPreview): void {
  const node = byId('action-results');
  node.replaceChildren();
  const steps = (label: string, values: string[]): void => {
    if (!values.length) return;
    const list = element('ul', undefined, 'plain-list');
    for (const value of values) list.append(element('li', actionStepLabel(value)));
    node.append(element('h3', label), list);
  };
  if (value.status !== 'prepared' && value.status !== 'cancelled') {
    steps('Confirmed steps', value.completedSteps);
    steps('Steps without confirmed completion', value.remainingSteps);
  }
  if (value.stepResults) {
    for (const [step, result] of Object.entries(value.stepResults)) {
      const row = element('p', `${actionStepLabel(step)}${result.account ? ` · @${result.account}` : ''}${result.completedAt ? ` · ${date(result.completedAt)}` : ''}${result.observed ? ' · Confirmed by GitHub evidence' : ''}`, 'muted fine');
      if (result.url) row.append(document.createTextNode(' · '), resultLink('Open step result', result.url)); node.append(row);
    }
  }
  const urls = [...new Set(value.urls)].filter(url => safeResultUrl(url));
  if (urls.length) {
    const list = element('ul', undefined, 'plain-list');
    for (const [index, url] of urls.entries()) { const item = element('li'); item.append(resultLink(`Open GitHub result ${index + 1}`, url)); list.append(item); }
    node.append(element('h3', 'Results on GitHub'), list);
  }
  node.hidden = !node.childElementCount;
  const error = byId('action-submit-error');
  error.hidden = !value.error;
  error.textContent = value.error ? `${value.error.message}${value.error.guidance ? ` ${value.error.guidance}` : ''} [${value.error.code}]` : '';
}

function render(value: WebActionPreview): void {
  const presentation = actionPresentation(value, attempted);
  byId('action-title').textContent = presentation.conclusion;
  byId('page-title').textContent = actionButtonLabel(value.draft);
  byId('action-next-step').textContent = presentation.nextStep;
  byId('action-description').textContent = actionDescription(value.draft);
  const status = byId('action-status');
  status.textContent = statusLabels[value.status];
  status.className = `status ${value.status}`;
  const facts = byId('action-facts');
  facts.replaceChildren(labeledValue('GitHub account', value.account ? `@${value.account}` : 'Not available'), labeledValue('Repository', value.target.repository), labeledValue('Target', `${value.target.type === 'pr' ? 'Pull request' : 'Issue'} #${value.target.number}`));
  const context = byId('action-context-facts'); context.replaceChildren(labeledValue('Requested from', value.sourceOrigin));
  if (value.state) context.append(labeledValue('Current target state', value.state));
  if (value.draft.expectedHeadSha) context.append(labeledValue('Reviewed head SHA', value.draft.expectedHeadSha));
  if (value.headSha) context.append(labeledValue('Current head SHA', value.headSha));
  if (value.sourceHeadSha) facts.append(labeledValue('Source branch SHA', value.sourceHeadSha));
  if (value.draft.pullRequest?.sourceHeadSha) context.append(labeledValue('Tested source SHA', value.draft.pullRequest.sourceHeadSha));
  if (value.ciState) context.append(labeledValue('CI state', value.ciState));
  if (value.draftPr !== undefined) context.append(labeledValue('Current pull request type', value.draftPr ? 'Draft' : 'Ready for review'));
  context.append(labeledValue('Last action update', date(value.updatedAt)));
  byId('action-target').replaceChildren(resultLink('Inspect target on GitHub', value.targetUrl));
  renderDraft(value);
  byId('action-blockers').hidden = !['prepared', 'partial'].includes(value.status) || !value.blockers.length;
  const blockers = byId('blocker-list'); blockers.replaceChildren();
  for (const blocker of value.blockers) blockers.append(element('li', `${blocker.message}${blocker.guidance ? ` ${blocker.guidance}` : ''}`));
  const notice = byId('action-notice');
  notice.textContent = presentation.reason;
  notice.className = value.status === 'succeeded' ? 'success' : ['unknown', 'partial', 'submitting'].includes(value.status) || attempted && value.status === 'prepared' ? 'notice' : '';
  byId('action-confirm-account').textContent = (value.status === 'prepared' && !attempted || value.kind === 'close-as-duplicate' && value.status === 'partial' && value.resumeRequired) && value.account ? `Confirming will execute this action on GitHub as @${value.account}.` : '';
  byId('action-id').textContent = `Action ID: ${value.operationId}`;
  const source = byId<HTMLAnchorElement>('action-source'); source.hidden = !value.runId;
  if (value.runId) source.href = chrome.runtime.getURL(`details.html?${new URLSearchParams({ runId: value.runId })}`);
  byId('action-footer-note').textContent = value.status === 'prepared' ? 'Prepared content is fixed for this confirmation.' : 'The recorded content and operation identity are preserved.';
  if (['succeeded', 'partial', 'failed', 'unknown'].includes(value.status)) byId<HTMLDetailsElement>('action-evidence').open = true;
  renderResults(value);
  byId('action-loading').hidden = true;
  byId('action-content').hidden = false;
  updateButtons();
}

async function loadPreview(): Promise<void> {
  if (busy || !validOperationId(operationId)) return;
  busy = true; previewCurrent = false; updateButtons(); clearError();
  try {
    const value = await request<WebActionPreview>('webActions.preview', { operationId });
    if (value.operationId !== operationId) throw new Error('The Host returned a different action. Reopen this action from Pulse.');
    preview = value; previewCurrent = true; render(value);
  } catch (error) {
    byId('action-loading').hidden = true;
    report(error);
  } finally {
    busy = false; updateButtons();
    void connectionStatus();
  }
}

async function submit(): Promise<void> {
  if (!previewCurrent || !canConfirmAction(preview, busy, attempted) || !preview) return;
  const account = preview.account;
  const resume = preview.kind === 'close-as-duplicate' && preview.status === 'partial' && preview.resumeRequired === true;
  const body = preview.kind === 'close-as-duplicate' && !resume && preview.bodyEditable ? duplicateBody ?? preview.draft.body ?? '' : undefined;
  if (body !== undefined) preview = { ...preview, draft: { ...preview.draft, body }, commentBody: duplicateCommentBody(preview, body), bodyEditable: false };
  attempted = true; busy = true;
  try { sessionStorage.setItem(attemptKey, 'submitted'); } catch { /* The current page still prevents another submission. */ }
  clearError(); updateButtons();
  byId('action-notice').textContent = 'Submitting to GitHub. Wait for a confirmed result before taking another action.';
  try {
    const result = await request<WebActionSummary>('webActions.submit', { operationId, expectedAccount: account, ...(resume ? { resume: true } : body !== undefined ? { body } : {}) });
    preview = withActionSummary(preview, result);
    render(preview);
  } catch (error) {
    previewCurrent = false;
    byId('action-notice').className = 'notice';
    byId('action-notice').textContent = 'The submission response is unavailable. GitHub may have accepted this action. Refresh the status to check its outcome; this page will not submit it again.';
    report(new Error(errorText(error)));
  } finally { busy = false; updateButtons(); }
  if (preview?.kind === 'close-as-duplicate' && previewCurrent) await loadPreview();
}

async function cancel(): Promise<void> {
  if (!previewCurrent || busy || attempted || preview?.status !== 'prepared') return;
  busy = true; updateButtons(); clearError();
  try {
    const result = await request<WebActionSummary>('webActions.cancel', { operationId });
    preview = withActionSummary(preview, result); render(preview);
  } catch (error) {
    previewCurrent = false; report(error);
    byId('action-notice').textContent = 'Cancellation has not been confirmed. Refresh the status before taking another action.';
  } finally { busy = false; updateButtons(); }
}

async function reconcile(): Promise<void> {
  if (!previewCurrent || !canReconcileAction(preview, busy) || !preview) return;
  busy = true; updateButtons(); clearError();
  byId('action-notice').textContent = 'Checking existing GitHub results for this action…';
  let checked = false;
  try {
    const result = await request<WebActionSummary>('webActions.reconcile', { operationId });
    preview = withActionSummary(preview, result); render(preview); checked = true;
  } catch (error) {
    report(error);
    byId('action-notice').textContent = 'The GitHub result check could not be completed. The action remains unconfirmed. You can check again; this check only reads GitHub.';
  } finally { busy = false; updateButtons(); }
  if (checked) await loadPreview();
}

async function retry(): Promise<void> {
  if (!previewCurrent || !canRetryAction(preview, busy) || !preview) return;
  if (preview.kind === 'create-pr') {
    const source = resultDraftSourceUrl(preview.runId, preview.proposalId);
    if (source) {
      try { preservePreparedContent(preview); location.href = chrome.runtime.getURL(source); }
      catch (error) { report(error); }
      return;
    }
  }
  busy = true; updateButtons(); clearError();
  try {
    retryAttemptId ??= crypto.randomUUID();
    const prepared = await request<WebActionSummary>('resultActions.prepare', { runId: preview.runId, proposalId: preview.proposalId, attemptId: retryAttemptId, retry: true });
    if (!validOperationId(prepared.operationId) || prepared.runId !== preview.runId || prepared.proposalId !== preview.proposalId || prepared.kind !== preview.kind || prepared.target.type !== preview.target.type || prepared.target.number !== preview.target.number || prepared.target.repository.toLowerCase() !== preview.target.repository.toLowerCase()) throw new Error('The Host returned a different result proposal. Refresh this confirmation.');
    await chrome.tabs.create({ url: chrome.runtime.getURL(`action.html?operationId=${encodeURIComponent(prepared.operationId)}`) });
    retryAttemptId = undefined;
    byId('action-notice').textContent = 'The confirmation is open in a new tab. Review its account, target, and current state before submitting.';
  } catch (error) { report(error); }
  finally { busy = false; updateButtons(); }
}

function preservePreparedContent(value: WebActionPreview): void {
  const key = preparedResultDraftKey(value); const pr = value.draft.pullRequest;
  if (key && pr) {
    try { localStorage.setItem(key, JSON.stringify({ title: pr.title, body: pr.body ?? '' })); }
    catch { throw new Error('The browser could not preserve the prepared Draft PR content. Keep this confirmation open and retry after browser storage is available.'); }
  }
}

async function editDraft(): Promise<void> {
  if (!previewCurrent || busy || attempted || preview?.status !== 'prepared' || preview.kind !== 'create-pr') return;
  const source = resultDraftSourceUrl(preview.runId, preview.proposalId);
  if (!source) return;
  busy = true; updateButtons(); clearError();
  try {
    preservePreparedContent(preview);
    const result = await request<WebActionSummary>('webActions.cancel', { operationId });
    preview = withActionSummary(preview, result);
    if (result.status !== 'cancelled') { render(preview); throw new Error('The existing action could not be cancelled. Check its status before editing.'); }
    location.href = chrome.runtime.getURL(source);
  } catch (error) { previewCurrent = false; report(error); }
  finally { busy = false; updateButtons(); }
}

initNavigation();
const sourceParams = new URLSearchParams(location.search);
const sourceRunId = sourceParams.get('runId'); const sourceProposalId = sourceParams.get('proposalId');
if (!operationId && sourceRunId && sourceProposalId) initResultDraftWorkspace(sourceRunId, sourceProposalId);
else {
  byId('refresh').addEventListener('click', () => { void loadPreview(); });
  byId('check-action-status').addEventListener('click', () => { void loadPreview(); });
  byId('confirm-action').addEventListener('click', () => { void submit(); });
  byId('cancel-action').addEventListener('click', () => { void cancel(); });
  byId('reconcile-action').addEventListener('click', () => { void reconcile(); });
  byId('retry-action').addEventListener('click', () => { void retry(); });
  byId('edit-action').addEventListener('click', () => { void editDraft(); });
  if (validOperationId(operationId)) void loadPreview();
  else { byId('action-loading').hidden = true; updateButtons(); report(new Error('This action link is invalid. Open the action again from PowerToys Pulse.')); }
}
document.addEventListener('keydown', event => {
  if (event.key === 'Escape') for (const menu of document.querySelectorAll<HTMLDetailsElement>('.action-more[open]')) {
    const focused = menu.contains(document.activeElement); menu.open = false;
    if (focused) menu.querySelector('summary')?.focus();
  }
});
document.addEventListener('click', event => {
  const link = (event.target as Element).closest<HTMLAnchorElement>('a[href^="#"]');
  if (link) {
    const disclosure = document.getElementById(link.hash.slice(1));
    if (disclosure instanceof HTMLDetailsElement) disclosure.open = true;
  }
  for (const menu of document.querySelectorAll<HTMLDetailsElement>('.action-more[open]')) if (!menu.contains(event.target as Node) || link) menu.open = false;
});
