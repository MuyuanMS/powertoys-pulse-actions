import { ProtocolError, isActive, safeHttpsUrl } from './policy.js';
import { matchingReview, operationLabels, suggestionOriginal, validateDraft } from './review.js';
import { completedV3Findings, currentRecommendation, eligibleManualOperationKinds, executionSelection, manualOperationKinds, observedExecutionValue, outcomeLabel, phaseLabel, proposedOperationKinds, recoveryActions, resultActionAvailability, resultActionEligible, resultNeedsAttention, resultPresentation, reviewModeLabel, runFailure, textValue, workflowOutcome } from './details-model.js';
import { resultWorkspaceSummary } from './result-workspace-model.js';
import { deleteDraft, readDraft, resultDraftScope, writeDraft } from './draft-store.js';
import { validOperationId } from './action-model.js';
import { openRerunDialog } from './rerun-dialog.js';
import { scopedReviewController } from './review-decision.js';
import { findingFeedbackController } from './finding-feedback.js';
import { hasCompleteResultCache, loadFullResult } from './result-loader.js';
import { renderFinalReport } from './final-report.js';
import { openResultTaskDialog } from './result-task-dialog.js';
import { actionLabels, badge, button, byId, clearError, connectionStatus, date, element, initNavigation, labeledValue, link, report, request } from './ui.js';
import type { AgentLog, Events, Operation, OperationKind, OperationPreview, ResultNextAction, Run, Suggestion, WebActionSummary } from './types.js';

const runId = new URL(location.href).searchParams.get('runId');
let run: Run | undefined;
let eventSequence = 0;
let logLines: string[] = [];
type LogStream = 'activity' | 'stdout' | 'stderr';
let logStream: LogStream = 'activity';
const rawLogs = { stdout: { cursor: 0, text: '', notice: '', sample: false }, stderr: { cursor: 0, text: '', notice: '', sample: false } };
let activityNotice = '';
let refreshing = false;
let readMarked = false;
let resultSignature = '';
let controlsSignature = '';
let preview: OperationPreview | undefined;
let previewState: 'idle' | 'loading' | 'ready' | 'error' = 'idle';
let previewPromise: Promise<void> | undefined;
let previewContext = '';
let previewGeneration = 0;
let previewRefreshQueued = false;
let previewReturnTimer: ReturnType<typeof setTimeout> | undefined;
let refreshPreviewAfterSubmit = false;
let operations: Operation[] = [];
let resultOperations: WebActionSummary[] = [];
let operationsTruncated = false;
let operationCount = 0;
let operationsSignature = '';
let submitting = false;
let localUnknown = false;
let pendingOperationId: string | undefined;
let draftLoaded = false;
let selectedProposal: ResultNextAction | undefined;
let proposalSelectionSequence = 0;
const preparingProposals = new Set<string>();
const proposalAttempts = new Map<string, string>();
interface ProposalDraft { body: string; suggestions: Map<string, { checked: boolean; body: string; replacement: string }> }
const proposalDrafts = new Map<string, ProposalDraft>();
let activeDraftKey = '';
let rerunning = false;
let deleted = false;
let refreshCount = 0;
let githubDisclosureInitialized = false;
let loadingResult = false; let resultReadError = ''; let explicitAction = false; let manualTargetKey = '';
let independentManual = false; let workspaceSignature = ''; let shellSignature = ''; let draftContext = ''; let draftDurable = true;
let confirmationResolve: ((confirmed: boolean) => void) | undefined;
let initialAnchorApplied = false;
interface SuggestionEditor { suggestion: Suggestion; checkbox: HTMLInputElement; body: HTMLTextAreaElement; replacement: HTMLTextAreaElement }
let suggestionEditors: SuggestionEditor[] = [];
const githubError = byId('github-error');
const scopedReview = scopedReviewController(byId('review-scope-section'), () => { if (run) renderWorkspace(); });
const feedback = findingFeedbackController(() => { applyDefaultAction(); updateSubmit(); });
initNavigation();

function notice(message: string): void { const node = byId('notice'); node.textContent = message; node.hidden = false; }
function recordedTime(value: unknown): string {
  const text = textValue(value); if (!text) return 'Not recorded';
  const time = new Date(text);
  return Number.isNaN(time.getTime()) ? 'Not recorded' : time.toLocaleString('en-GB', { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
}
function duration(value: Run): string {
  if (isActive(value.status.state)) return value.status.state === 'accepted' ? 'Waiting to start' : 'In progress';
  const start = Date.parse(textValue(value.status.startedAt) ?? ''); const end = Date.parse(textValue(value.status.endedAt) ?? '');
  if (!Number.isFinite(start) || !Number.isFinite(end) || end < start) return 'Not recorded';
  const seconds = Math.round((end - start) / 1000);
  return seconds < 60 ? `${seconds}s` : seconds < 3600 ? `${Math.floor(seconds / 60)}m ${seconds % 60}s` : `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`;
}
function executionFact(label: string, value: unknown, source?: 'task' | 'default' | 'cli'): HTMLElement {
  const fact = element('div', undefined, 'fact'); const selection = element('dd', executionSelection(value));
  const origin = source === 'task' ? 'Task override' : source === 'default' ? 'Extension default' : undefined;
  if (origin && textValue(value)) selection.append(element('small', origin, 'details-selection-source'));
  fact.append(element('dt', label), selection);
  return fact;
}
async function rerun(): Promise<void> {
  if (!run || isActive(run.status.state) || rerunning) return;
  rerunning = true;
  try {
    await openRerunDialog(run);
  } finally { rerunning = false; }
}
function evidenceList(items: string[] | undefined): HTMLElement | undefined {
  if (!Array.isArray(items) || !items.some(item => textValue(item))) return undefined;
  const list = element('ul', undefined, 'details-evidence');
  list.setAttribute('aria-label', 'Evidence');
  for (const item of items) if (textValue(item)) list.append(element('li', item));
  return list;
}
function suggestionKey(suggestion: Suggestion): string { return suggestion.id ?? JSON.stringify([suggestion.path, suggestion.startLine, suggestion.line, suggestion.side]); }
function proposalKey(action: ResultNextAction | undefined): string { return action?.proposalId ?? (action ? JSON.stringify(action) : `manual:${selectedKind()}`); }
function saveDraft(): void {
  if (!draftLoaded || !run) return;
  proposalDrafts.set(activeDraftKey, { body: byId<HTMLTextAreaElement>('operation-body').value, suggestions: new Map(suggestionEditors.map(editor => [suggestionKey(editor.suggestion), { checked: editor.checkbox.checked, body: editor.body.value, replacement: editor.replacement.value }])) });
  draftDurable = writeDraft(resultDraftScope(run, 'operation-editor'), { activeDraftKey, independentManual, explicitAction, kind: selectedKind(), proposalId: selectedProposal?.proposalId, closeReason: byId<HTMLTextAreaElement>('close-reason').value, drafts: [...proposalDrafts].map(([key, value]) => [key, { body: value.body, suggestions: [...value.suggestions] }]) });
}
function chooseProposal(action: ResultNextAction | undefined): void {
  saveDraft();
  selectedProposal = action;
  activeDraftKey = proposalKey(action);
  const summary = byId('selected-proposal'); summary.hidden = !action;
  summary.textContent = action ? action.reason || 'Editing the selected result proposal.' : '';
  byId<HTMLTextAreaElement>('operation-body').value = proposalDrafts.get(activeDraftKey)?.body ?? proposalBody(action, preview && matchingReview(run?.result, run?.task.expectedHeadSha, preview)?.body);
  if (action?.kind === 'close') byId<HTMLTextAreaElement>('close-reason').value = textValue(action.body) ? action.body : action.reason;
  renderSuggestions();
}
function proposalBody(action: ResultNextAction | undefined, reviewBody: string | undefined): string {
  return action && textValue(action.body) ? action.body : action ? reviewBody ?? action.body ?? '' : '';
}
function applyDefaultAction(): void {
  if (!run || explicitAction) return;
  const choice = byId<HTMLSelectElement>('operation-kind');
  const recommendation = currentRecommendation(run);
  choice.value = feedback.compose().findingIds.length ? recommendation?.kind === 'address-findings' ? 'requestChanges' : 'comment' : recommendation?.kind === 'approve' && run.task.target?.type === 'pr' ? 'approve' : 'comment';
}
function selectedFeedback(): ReturnType<typeof feedback.compose> {
  return independentManual ? { body: '', findingIds: [], suggestions: [], errors: [] } : feedback.compose();
}
function manualBody(): string {
  const additional = byId<HTMLTextAreaElement>('operation-body').value;
  const selected = run?.result?.schemaVersion === 3 ? selectedFeedback().body : '';
  return additional.trim() && selected.trim() ? `${additional}\n\n${selected}` : additional.trim() ? additional : selected;
}
function reveal(id: string): void {
  const node = byId(id); let parent = node.parentElement;
  while (parent) { if (parent.tagName === 'DETAILS') (parent as HTMLDetailsElement).open = true; parent = parent.parentElement; }
  if (node.tagName === 'DETAILS') (node as HTMLDetailsElement).open = true;
  node.scrollIntoView?.({ block: 'start', behavior: 'smooth' });
}
function chooseManual(kind: OperationKind): void {
  saveDraft(); independentManual = true; explicitAction = true; selectedProposal = undefined;
  byId<HTMLSelectElement>('operation-kind').value = kind; activeDraftKey = `manual:${kind}`;
  byId<HTMLTextAreaElement>('operation-body').value = proposalDrafts.get(activeDraftKey)?.body ?? '';
  byId('selected-proposal').hidden = true; byId<HTMLDetailsElement>('github-section').open = true;
  byId<HTMLDetailsElement>('more-actions').open = false; saveDraft(); renderWorkspace(); updateSubmit();
  byId<HTMLTextAreaElement>(kind === 'close' ? 'close-reason' : 'operation-body').focus?.();
}
function restoreRecommended(): void {
  if (!run) return;
  saveDraft(); independentManual = false; explicitAction = false;
  const proposal = resultWorkspaceSummary(run).next.proposal;
  chooseProposal(proposal && ['comment', 'requestChanges', 'suggestChanges', 'approve', 'close'].includes(proposal.kind) ? proposal : undefined);
  activeDraftKey = selectedProposal?.proposalId ?? 'recommended';
  byId<HTMLTextAreaElement>('operation-body').value = proposalDrafts.get(activeDraftKey)?.body ?? (completedV3Findings(run).length ? '' : selectedProposal?.body ?? '');
  applyDefaultAction(); saveDraft(); renderWorkspace(); updateSubmit();
}
function restoreEditor(): void {
  if (!run || draftContext === run.runId) return;
  draftContext = run.runId; draftLoaded = true;
  const saved = readDraft<{ activeDraftKey: string; independentManual: boolean; explicitAction: boolean; kind: OperationKind; proposalId?: string; closeReason?: string; drafts?: [string, { body: string; suggestions: [string, { checked: boolean; body: string; replacement: string }][] }][] }>(resultDraftScope(run, 'operation-editor'));
  if (saved && Array.isArray(saved.drafts)) {
    for (const [key, value] of saved.drafts) if (typeof key === 'string' && typeof value?.body === 'string' && Array.isArray(value.suggestions)) proposalDrafts.set(key, { body: value.body, suggestions: new Map(value.suggestions) });
    independentManual = saved.independentManual === true; explicitAction = saved.explicitAction === true;
    selectedProposal = recoveryActions(run).find(action => action.proposalId === saved.proposalId);
    if (manualOperationKinds(run).includes(saved.kind)) byId<HTMLSelectElement>('operation-kind').value = saved.kind;
    activeDraftKey = saved.activeDraftKey || 'recommended';
    byId<HTMLTextAreaElement>('operation-body').value = proposalDrafts.get(activeDraftKey)?.body ?? '';
    byId<HTMLTextAreaElement>('close-reason').value = saved.closeReason ?? '';
  } else {
    activeDraftKey = 'recommended';
    const proposal = resultWorkspaceSummary(run).next.proposal;
    if (proposal && ['comment', 'requestChanges', 'suggestChanges', 'approve', 'close'].includes(proposal.kind)) {
      selectedProposal = proposal; activeDraftKey = proposalKey(proposal);
      byId<HTMLTextAreaElement>('operation-body').value = completedV3Findings(run).length ? '' : proposal.body || '';
      if (proposal.kind === 'close') byId<HTMLTextAreaElement>('close-reason').value = proposal.body || proposal.reason;
    }
  }
  const pending = readDraft<{ operationId: string }>(resultDraftScope(run, 'pending-operation'));
  if (pending && validOperationId(pending.operationId)) { pendingOperationId = pending.operationId; localUnknown = true; }
}
function renderWorkspace(): void {
  if (!run) return;
  const summary = resultWorkspaceSummary(run, { loading: loadingResult, error: resultReadError });
  const shellKey = JSON.stringify({ result: run.result, summary, preview, previewState, operations, resultOperations, localUnknown, independentManual, explicitAction, handled: run.view.handled, preparation: scopedReview.preparationState() });
  if (shellSignature === shellKey) return;
  shellSignature = shellKey;
  byId('conclusion-title').textContent = summary.title; byId('conclusion-detail').textContent = summary.detail;
  byId('result-workspace').dataset.tone = summary.tone;
  byId('conclusion-facts').replaceChildren(...summary.facts.map(fact => { const node = labeledValue(fact.label, /^[a-f0-9]{40}$/i.test(fact.value) ? fact.value.slice(0, 12) : fact.value); if (/^[a-f0-9]{40}$/i.test(fact.value)) node.title = fact.value; return node; }));
  byId('revision-status').textContent = run.task.target?.type === 'pr' ? previewState === 'loading' ? 'Checking the current PR revision…' : previewState === 'error' ? 'The current PR revision could not be checked.' : preview ? preview.stale ? 'Current PR revision has changed. Saved evidence and drafts remain tied to the analyzed revision.' : `Current revision ${preview.headSha === run.task.expectedHeadSha ? 'matches this task' : 'was not established for this task'}.` : 'The current PR revision will be checked when preparing a GitHub action.' : '';
  byId('next-step-title').textContent = summary.next.title;
  byId('next-step-reason').textContent = summary.next.reason;
  const feedbackAction = ['address-findings', 'comment', 'requestChanges', 'suggestChanges', 'approve', 'close'].includes(summary.next.kind);
  const editorVisible = Boolean(run.task.target) && (independentManual || feedbackAction || explicitAction);
  byId('github-section').hidden = !editorVisible;
  byId('submit-operation').hidden = !editorVisible || !preview || previewState !== 'ready';
  const targetStatus = byId('github-target-status');
  targetStatus.textContent = previewState === 'loading' ? 'Checking GitHub target using your saved account…' : previewState === 'error' ? 'GitHub could not be checked. Your feedback draft is preserved.' : preview?.account ? `Connected as @${preview.account} · Saved GitHub account` : previewState === 'ready' ? 'GitHub account unavailable. Check Settings.' : 'Your saved GitHub account will be used automatically.';
  const refreshTarget = byId<HTMLButtonElement>('prepare');
  refreshTarget.textContent = previewState === 'error' ? 'Retry' : 'Refresh';
  refreshTarget.hidden = previewState === 'idle' || previewState === 'loading';
  refreshTarget.disabled = previewState === 'loading' || submitting;
  byId('recommendation-findings').hidden = independentManual || !feedbackAction;
  byId('more-actions-label').textContent = summary.next.kind === 'none' ? 'Choose action' : 'More actions';
  const primary = byId('primary-next-action'); primary.replaceChildren();
  const context = byId('next-step-context'); context.replaceChildren();
  const unresolved = localUnknown || operations.some(item => ['prepared', 'submitting', 'unknown'].includes(item.status)) || resultOperations.some(item => ['submitting', 'unknown', 'partial'].includes(item.status));
  if (unresolved) {
    byId('next-step-title').textContent = 'Verify the pending GitHub action';
    byId('next-step-reason').textContent = 'A previous submission has no confirmed outcome. Check the same operation before choosing another action.';
    byId('github-section').hidden = !independentManual && !explicitAction; byId('recommendation-findings').hidden = true;
    primary.append(button('Verify pending action', () => reveal('operations-history-section'), 'primary'));
    byId('submit-operation').hidden = true;
    context.append(element('p', 'An earlier submission is not confirmed. Review that same operation before starting another.', 'notice'));
  } else if (editorVisible && previewState !== 'ready') {
    const control = button(previewState === 'error' ? 'Retry GitHub check' : 'Checking GitHub target…', () => prepare(true).catch(error => report(error, githubError)), 'primary');
    control.disabled = previewState !== 'error'; primary.append(control);
  }
  else if (!editorVisible) {
    let control: HTMLElement | undefined;
    if (summary.next.kind === 'reload-report') control = button(summary.next.title, refresh, 'primary');
    else if (summary.next.kind === 'inspectResult') control = button(summary.next.title, () => reveal(byId('diagnostic-section').hidden ? 'execution-logs' : 'diagnostic-section'), 'primary');
    else if (summary.next.kind === 'run-e2e') { const state = scopedReview.preparationState(); control = button(state?.pending ? state.label : 'Review verification requirements', () => scopedReview.openPreparation(), 'primary'); (control as HTMLButtonElement).disabled = !state?.available; }
    else if (summary.next.proposal) control = nextActionControl(summary.next.proposal);
    if (control) { control.className = `${control.className} primary`.trim(); primary.append(control); }
  }
  if (summary.next.proposal?.pullRequest) {
    const pr = summary.next.proposal.pullRequest; const box = element('div', undefined, 'workspace-context');
    const facts = element('dl', undefined, 'facts'); facts.append(labeledValue('Pull request', 'Draft'), labeledValue('Source branch', pr.head || 'Not recorded'), labeledValue('Base branch', pr.base || 'Not recorded'), labeledValue('Verified candidate', pr.sourceHeadSha || 'Not recorded'));
    box.append(facts, element('p', pr.title, 'prewrap'), element('p', 'Review the saved source and editable title/description in the Draft PR workspace before preparing the action.', 'muted fine')); context.append(box);
  }
  const menu = byId('more-actions-list'); menu.replaceChildren();
  if (run.task.target) {
    menu.append(element('p', 'Manual actions', 'menu-label'));
    for (const kind of manualOperationKinds(run)) menu.append(button(kind === 'comment' ? 'Write an independent comment' : operationLabels[kind], () => chooseManual(kind)));
    if (run.task.target.type === 'pr') menu.append(button('Review merge', () => prepareManualAction('merge-pr')), button('Review CI trigger', () => prepareManualAction('trigger-ci')));
  }
  menu.append(element('p', 'Records and evidence', 'menu-label'), button('Detailed result and evidence', () => reveal('result-evidence')), button('Submission history', () => { byId('operations-history-section').hidden = false; reveal('operations-history-section'); }), button('Execution logs', () => reveal('execution-logs')));
  if (!isActive(run.status.state)) menu.append(button('Review a new run', rerun), button(run.view.handled ? 'Mark unhandled' : 'Mark handled', async () => { await request('tasks.handle', { runId, value: !run?.view.handled }); await refresh(); }), button('Delete local record…', () => reveal('cleanup-section')));
  else menu.append(button('Cancel run', async () => { await request('tasks.cancel', { runId }); await refresh(); }));
  const signature = JSON.stringify({ result: run.result, loadingResult, resultReadError });
  if (workspaceSignature !== signature) {
    workspaceSignature = signature; const items = byId('recommendation-findings'); items.replaceChildren();
    for (const finding of loadingResult || resultReadError ? [] : completedV3Findings(run)) {
      const editor = feedback.render(finding); if (!editor) continue;
      const item = element('article', undefined, 'workspace-finding');
      item.append(element('h4', `${finding.priority} · ${finding.title}`), element('p', finding.path ? `${finding.path}${finding.line ? `:${finding.line}` : ''}` : 'General finding', 'muted fine path'), editor);
      items.append(item);
    }
  }
  byId('draft-status').textContent = unresolved ? 'Existing drafts are preserved' : editorVisible ? independentManual ? 'Independent manual draft' : 'Review the prepared feedback before sending' : 'Actions are reviewed before submission';
  // Reuse the configured account; only this target's live state needs loading.
  // Do not retry on the task polling loop or start GitHub work for a hidden editor.
  if (editorVisible && !unresolved && !submitting && (independentManual || explicitAction || !loadingResult && !resultReadError) && previewState === 'idle' &&
    (!isActive(run.status.state) || independentManual || explicitAction)) void prepare().catch(error => report(error, githubError));
}
async function prepareManualAction(kind: 'merge-pr' | 'trigger-ci'): Promise<void> {
  if (!run || run.task.target?.type !== 'pr') return;
  const existing = resultOperations.filter(item => item.kind === kind && !item.proposalId).sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0];
  if (existing && ['prepared', 'submitting', 'unknown', 'partial'].includes(existing.status)) { await openConfirmation(existing); return; }
  const key = `manual:${kind}:${existing?.operationId ?? 'initial'}`;
  const attemptId = proposalAttempts.get(key) ?? crypto.randomUUID(); proposalAttempts.set(key, attemptId);
  const operation = await request<WebActionSummary>('resultActions.prepare', { runId, kind, attemptId, ...(existing ? { retry: true } : {}) });
  if (operation.kind !== kind || operation.target.type !== 'pr' || operation.target.number !== run.task.target.number || operation.target.repository.toLowerCase() !== run.task.repository.toLowerCase()) throw new Error('The Host returned a different manual action target.');
  await openConfirmation(operation); await refreshOperations();
}
async function openConfirmation(operation: WebActionSummary): Promise<void> {
  if (!validOperationId(operation.operationId)) throw new Error('The Host returned an invalid action link. Refresh this task.');
  await chrome.tabs.create({ url: chrome.runtime.getURL(`action.html?operationId=${encodeURIComponent(operation.operationId)}`) });
}
function latestResultOperation(proposalId: string | undefined): WebActionSummary | undefined {
  return proposalId ? resultOperations.filter(item => item.proposalId === proposalId).sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0] : undefined;
}
async function prepareResultAction(action: ResultNextAction, retry = false): Promise<void> {
  const proposalId = action.proposalId;
  if (!run || !proposalId || !resultActionEligible(run, action) || preparingProposals.has(proposalId)) return;
  preparingProposals.add(proposalId);
  clearError();
  try {
    const existing = latestResultOperation(proposalId);
    if (existing && !retry) { await openConfirmation(existing); return; }
    if (retry && existing?.retryAllowed !== true) throw new Error('This action has not been cleared for another attempt. Check its existing confirmation first.');
    const key = `${proposalId}:${retry ? existing?.operationId ?? 'retry' : 'initial'}`;
    const attemptId = proposalAttempts.get(key) ?? crypto.randomUUID(); proposalAttempts.set(key, attemptId);
    const prepared = await request<WebActionSummary>('resultActions.prepare', { runId, proposalId, attemptId, ...(retry ? { retry: true } : {}) });
    const target = run.task.target;
    if (!validOperationId(prepared.operationId) || prepared.kind !== action.kind || !target || prepared.target.type !== target.type || prepared.target.number !== target.number || prepared.target.repository.toLowerCase() !== run.task.repository.toLowerCase()) throw new Error('The Host returned a different result action. Refresh the task before reviewing it.');
    if (prepared.runId && prepared.runId !== runId || prepared.proposalId && prepared.proposalId !== proposalId) throw new Error('The Host returned a different result proposal. Refresh this task.');
    await openConfirmation(prepared);
    await refreshOperations();
  } finally { preparingProposals.delete(proposalId); }
}
function nextActionControl(action: ResultNextAction): HTMLElement | undefined {
  if (!run || isActive(run.status.state)) return undefined;
  switch (action.kind) {
    case 'none': return undefined;
    case 'configure': return button('Review missing prerequisites', () => reveal('diagnostic-section'));
    case 'rerun': return button('Retry as a new run', rerun);
    case 'inspectResult': { const anchor = element('a', 'View execution logs', 'button'); anchor.href = '#execution-logs'; return anchor; }
    case 'viewChanges': { const anchor = element('a', 'Inspect retained changes', 'button'); anchor.href = '#retained-artifacts'; return anchor; }
    case 'openTarget': return run.task.target ? link('Open target on GitHub', `https://github.com/${run.task.repository}/${run.task.target.type === 'pr' ? 'pull' : 'issues'}/${run.task.target.number}`) : undefined;
    case 'start-task': {
      const node = button('Review and start plan', () => openResultTaskDialog(run!, action));
      node.disabled = !resultActionEligible(run, action); return node;
    }
    case 'create-pr': case 'merge-pr': case 'trigger-ci': case 'close-as-duplicate': {
      const labels = { 'create-pr': 'Create Draft PR', 'merge-pr': 'Review merge', 'trigger-ci': 'Review CI trigger', 'close-as-duplicate': 'Review duplicate and close' };
      const existing = latestResultOperation(action.proposalId);
      const node = button(existing ? existing.status === 'prepared' ? 'Continue confirmation' : 'View action status' : labels[action.kind], () => existing ? openConfirmation(existing) : action.kind === 'create-pr' && action.proposalId ? chrome.tabs.create({ url: chrome.runtime.getURL(`action.html?runId=${encodeURIComponent(runId!)}&proposalId=${encodeURIComponent(action.proposalId)}`) }).then(() => {}) : prepareResultAction(action));
      node.disabled = !existing && (action.kind === 'create-pr' ? !action.proposalId : !resultActionEligible(run, action));
      if (action.kind !== 'close-as-duplicate' || !action.duplicateOf) return node;
      const group = element('div', undefined, 'footer-actions'); group.append(node);
      const original = action.duplicateOf;
      const canonical = `https://github.com/${original.repository}/issues/${original.number}`;
      group.append(button('Only draft a linked comment', async () => {
        independentManual = true; explicitAction = true; await prepare();
        byId<HTMLSelectElement>('operation-kind').value = 'comment'; chooseProposal(undefined);
        byId<HTMLTextAreaElement>('operation-body').value = `${action.body}${action.body ? '\n\n' : ''}Duplicate of ${canonical}`;
        byId<HTMLDetailsElement>('github-section').open = true; updateSubmit();
        byId('github-section').scrollIntoView?.({ block: 'start', behavior: 'smooth' });
      })); return group;
    }
    default: {
      const kind = action.kind as OperationKind;
      if (!proposedOperationKinds(run).includes(kind)) return undefined;
      const labels: Record<OperationKind, string> = { approve: 'Review approval', suggestChanges: 'Review suggested changes', requestChanges: 'Review change request', comment: 'Review comment', close: 'Review close action' };
      const node = button(labels[kind], async () => {
        independentManual = false; explicitAction = true;
        const selection = ++proposalSelectionSequence;
        await prepare();
        if (selection !== proposalSelectionSequence) return;
        if (!run) return;
        byId<HTMLSelectElement>('operation-kind').value = kind;
        chooseProposal(action);
        byId<HTMLDetailsElement>('github-section').open = true;
        saveDraft(); renderWorkspace(); updateSubmit(); byId('github-section').scrollIntoView?.({ block: 'start', behavior: 'smooth' });
      });
      node.disabled = !resultActionEligible(run, action); return node;
    }
  }
}
function renderRun(): void {
  if (!run) return;
  const task = run.task;
  const context = JSON.stringify([run.runId, task.repository, task.target?.type, task.target?.number, task.expectedHeadSha, run.status.state]);
  if (context !== previewContext) {
    if (submitting) refreshPreviewAfterSubmit = true;
    else {
      previewContext = context; previewGeneration++; previewPromise = undefined; previewRefreshQueued = false; preview = undefined; previewState = 'idle';
      if (previewReturnTimer !== undefined) { clearTimeout(previewReturnTimer); previewReturnTimer = undefined; }
      byId('operation-target').replaceChildren(); byId('operation-reasons').hidden = true; clearError(githubError);
    }
  }
  const isPr = task.target?.type === 'pr';
  byId('details-description').textContent = task.repository;
  byId('github-heading').textContent = isPr ? 'Pull request actions' : 'Issue actions';
  byId('github-description').textContent = isPr ? 'Review the target, account, and analysis SHA. Edit the review or comment before submitting.' : 'Review the target, account, and your explanation before commenting or closing the issue.';
  byId('title').textContent = `${actionLabels[task.actionKind]}${task.target ? ` · #${task.target.number}` : ''}`;
  document.title = `Pulse · ${byId('title').textContent}`;
  const outcome = badge(run.status.state);
  const workflow = workflowOutcome(run);
  outcome.textContent = outcomeLabel(run);
  if (workflow) outcome.className = `status ${workflow === 'completed' ? 'succeeded' : workflow}`;
  if (workflow === 'completed' && resultNeedsAttention(run)) outcome.className = 'status needs-attention';
  if (resultNeedsAttention(run) && ![2, 3].includes(run.result?.schemaVersion ?? 0) && !(run.result?.schemaVersion === 1 && run.result.outcome)) { outcome.textContent = 'Needs attention'; outcome.className = 'status needs-attention'; }
  byId('run-status').replaceChildren(outcome);
  const phase = byId('run-phase'); phase.hidden = !workflow || !run.result?.phase; phase.textContent = phase.hidden ? '' : `Phase · ${phaseLabel(run.result?.phase)}`;
  const started = recordedTime(run.status.startedAt); const ended = recordedTime(run.status.endedAt);
  byId('run-ended').textContent = isActive(run.status.state) ? run.status.state === 'accepted' ? `Accepted ${recordedTime(run.status.createdAt)}` : started === 'Not recorded' ? 'Run in progress' : `Started ${started}` : ended !== 'Not recorded' ? `Ended ${ended}` : 'This saved run has ended. Its end time was not recorded.';
  const runError = byId('run-error');
  const failure = runFailure(run);
  runError.hidden = !failure || isActive(run.status.state) || run.result?.schemaVersion === 2;
  runError.textContent = !runError.hidden && failure ? [failure.message, failure.guidance].filter(Boolean).join('\n') : '';
  const progress = byId('run-progress');
  progress.hidden = !isActive(run.status.state) || !textValue(run.status.latestProgress);
  progress.textContent = !progress.hidden ? textValue(run.status.latestProgress)! : '';
  const execution = byId('execution-facts');
  execution.replaceChildren(
    executionFact('CLI', run.config.agent === 'codex' ? 'Codex CLI' : run.config.agent === 'copilot' ? 'Copilot CLI' : undefined),
    executionFact('Model', observedExecutionValue(run, 'model')),
    executionFact('Reasoning effort', observedExecutionValue(run, 'reasoningEffort')),
  );
  byId('run-times').replaceChildren(labeledValue('Started', run.status.state === 'accepted' && started === 'Not recorded' ? 'Not started' : started), labeledValue('Duration', duration(run)));
  const facts = byId('run-facts'); facts.replaceChildren();
  const permissions: Record<string, string> = { 'read-only': 'Read only', 'workspace-write': 'Allow workspace changes', yolo: 'YOLO · Full access, no approvals' };
  facts.append(labeledValue('Run ID', run.runId), labeledValue('Action ID', textValue(task.actionId) ?? 'Not recorded'), labeledValue('PowerToys main repository', textValue(run.config.mainRepoFolder) ?? 'Not recorded'), labeledValue(run.config.worktreeBranch ? 'Task worktree' : 'Working folder', textValue(run.config.repoFolder) ?? 'Not recorded'), labeledValue('Worktree branch', textValue(run.config.worktreeBranch) ?? 'Not recorded'), labeledValue('CLI path', textValue(run.config.cliPath) ?? 'Not recorded'), labeledValue('Local file permissions', permissions[run.config.permission] || textValue(run.config.permission) || 'Not recorded'), labeledValue('Created', recordedTime(run.status.createdAt)), labeledValue('Last updated', recordedTime(run.status.updatedAt)));
  facts.append(labeledValue('Actual execution state', run.status.state), labeledValue('Recorded workflow outcome', textValue(run.result?.outcome) ?? 'Not recorded'));
  facts.append(executionFact('Requested model', run.config.model, run.config.executionSource?.model), executionFact('Requested reasoning effort', run.config.reasoningEffort, run.config.executionSource?.reasoningEffort));
  if (run.task.actionKind === 'pr-review' || run.task.actionKind === 'pr-verify') facts.append(labeledValue('Recorded review scope', reviewModeLabel(run.task.reviewOptions?.mode)));
  if (run.status.observedExecution) facts.append(labeledValue('CLI metadata source', run.status.observedExecution.source), labeledValue('CLI metadata observed', recordedTime(run.status.observedExecution.observedAt)));
  if (failure?.code) facts.append(labeledValue('Error code', failure.code));
  if (typeof run.status.exitCode === 'number' && Number.isInteger(run.status.exitCode)) facts.append(labeledValue('Exit code', String(run.status.exitCode)));
  if (Number.isSafeInteger(run.status.sequence)) facts.append(labeledValue('Event cursor', String(run.status.sequence)));
  if (task.expectedHeadSha) facts.append(labeledValue('Analysis SHA', task.expectedHeadSha));
  if (run.promptTemplate) facts.append(labeledValue('Task prompt', textValue(run.promptTemplate.name) ?? 'Not recorded'), labeledValue('Prompt version', textValue(run.promptTemplate.sha) ?? 'Not recorded'));
  const controlsKey = `${run.status.state}/${run.view.handled}`;
  if (controlsKey !== controlsSignature) {
    controlsSignature = controlsKey;
    const controls = byId('run-controls'); controls.replaceChildren();
    const logsLink = element('a', failure ? 'View error log' : 'View execution logs', 'button'); logsLink.href = '#execution-logs';
    if (failure) logsLink.addEventListener('click', () => { logStream = 'stderr'; byId<HTMLSelectElement>('log-stream').value = 'stderr'; renderLog(); void refresh(); });
    controls.append(logsLink);
    if (isActive(run.status.state)) controls.append(button('Cancel run', async () => { await request('tasks.cancel', { runId }); notice('Cancellation requested. Existing changes and artifacts are preserved.'); await refresh(); }, 'danger'));
    else {
      controls.append(button('Run again', rerun));
      controls.append(button(run.view.handled ? 'Mark unhandled' : 'Mark handled', async () => {
        await request('tasks.handle', { runId, value: !run?.view.handled });
        notice('Handling status saved locally.'); await refresh();
      }));
    }
    if (task.target) controls.append(link('Open on GitHub', `https://github.com/${task.repository}/${task.target.type === 'pr' ? 'pull' : 'issues'}/${task.target.number}`));
    controls.append(button('Refresh status', refresh));
  }
  byId('cleanup-section').hidden = isActive(run.status.state);
  const actionable = Boolean(run.task.target);
  byId('operation-editor').hidden = !actionable;
  const targetKey = run.task.target ? `${run.task.repository}/${run.task.target.type}/${run.task.target.number}` : '';
  if (targetKey !== manualTargetKey) {
    manualTargetKey = targetKey;
    const select = byId<HTMLSelectElement>('operation-kind');
    select.replaceChildren(...manualOperationKinds(run).map(kind => { const option = element('option', operationLabels[kind]); option.value = kind; return option; }));
    select.value = 'comment';
  }
  restoreEditor(); feedback.setContext(run, preview); applyDefaultAction();
  byId('manual-pr-actions').hidden = run.task.target?.type !== 'pr';
  if (!githubDisclosureInitialized && actionable) { byId<HTMLDetailsElement>('github-section').open = true; githubDisclosureInitialized = true; }
  updateSubmit();
  const prompt = typeof task.prompt === 'string' ? task.prompt : run.taskContextOmitted ? 'Full task context is retained in the local task record.' : 'Not recorded';
  if (byId('prompt').textContent !== prompt) byId('prompt').textContent = prompt;
  if (!loadingResult && !resultReadError) scopedReview.render(run); else byId('review-scope-section').hidden = true;
  const signature = JSON.stringify({ result: run.result, reviewSummary: run.reviewSummary, error: run.status.error, state: run.status.state, resultOperations, loadingResult, resultReadError });
  if (signature !== resultSignature) { resultSignature = signature; renderResult(); }
  renderWorkspace();
  if (!initialAnchorApplied) {
    initialAnchorApplied = true;
    const anchor = new URL(location.href).hash.slice(1);
    if (anchor && document.getElementById(anchor)) reveal(anchor);
  }
}
function renderResult(): void {
  if (!run) return;
  const result = resultPresentation(run);
  const reportStatus = byId('full-report-status'); reportStatus.hidden = !loadingResult && !resultReadError;
  reportStatus.textContent = resultReadError || (loadingResult ? 'Loading the complete report and every finding… GitHub actions remain available below.' : '');
  byId('result').replaceChildren(); byId('diagnostics').replaceChildren();
  if (loadingResult || resultReadError) { byId('result-section').hidden = true; byId('diagnostic-section').hidden = true; return; }
  byId('result-section').hidden = result.mode !== 'completed' || !result.hasContent;
  byId('diagnostic-section').hidden = result.mode !== 'diagnostic' || !result.hasContent;
  byId('result-title').textContent = result.mode === 'completed' && run.task.actionKind === 'pr-review' ? 'Review findings' : 'Results';
  const container = byId(result.mode === 'diagnostic' ? 'diagnostics' : 'result');
  if (result.mode === 'active' || !result.hasContent) return;
  if (run.result?.schemaVersion === 3) renderFinalReport(container, run, undefined, { unified: true });
  else if (result.summary) container.append(element('p', result.summary, 'result-summary'));
  const assessment = run.result?.assessment;
  if (assessment && run.result?.schemaVersion !== 3) {
    const subject = { 'original-pr': 'Original pull request', 'local-candidate': 'Local candidate', target: 'Task target' }[assessment.subject];
    const section = element('article', undefined, `details-assessment ${assessment.status}`);
    section.append(element('h3', `${subject} · ${assessment.status === 'passed' ? 'Passed' : assessment.status === 'failed' ? 'Issues found' : 'Inconclusive'}`), element('p', assessment.summary, 'prewrap'));
    if (assessment.revisionSha) section.append(element('p', `Assessed revision: ${assessment.revisionSha}`, 'muted fine path'));
    if (assessment.subject === 'local-candidate') section.append(element('p', 'This assessment describes the local candidate. It does not mean the upstream target has been updated.', 'muted fine'));
    container.append(section);
  }
  if (result.showReviewNotice) container.append(element('p', 'Review the reported findings and validation before following up.', 'notice'));
  const diagnosticContainer = container;
  for (const { primary: diagnostic, related } of result.diagnosticGroups) {
    const item = element('article', undefined, `details-diagnostic ${diagnostic.severity}`);
    item.append(element('strong', diagnostic.severity === 'error' ? 'Action needed' : diagnostic.code === 'REVIEW_COVERAGE_LIMITED' ? 'Validation limit' : 'Note'), element('p', diagnostic.message));
    const metadata = element('details'); metadata.append(element('summary', related.length ? 'Diagnostic details' : 'Diagnostic code'), element('code', diagnostic.code));
    for (const detail of related) metadata.append(element('p', 'Required workflow checks', 'fine'), element('code', detail.code), element('p', detail.message, 'fine'));
    item.append(metadata);
    diagnosticContainer.append(item);
  }
  if (result.findings.length && run.result?.schemaVersion !== 3) container.append(element('h3', 'Findings'));
  for (const finding of run.result?.schemaVersion === 3 ? [] : result.findings) {
    const item = element('article', undefined, 'details-finding');
    const heading = element('div', undefined, 'row'); heading.append(element('h4', finding.title), element('span', `${finding.severity === 'high' ? 'High' : finding.severity === 'medium' ? 'Medium' : 'Low'} · ${finding.status === 'fixed' ? 'Fixed' : finding.status === 'unverified' ? 'Unverified' : 'Open'}`, 'chip'));
    item.append(heading);
    if (finding.path) item.append(element('p', `${finding.path}${finding.line ? `:${finding.line}` : ''}`, 'muted fine path'));
    if (finding.details) item.append(element('p', finding.details, 'prewrap'));
    const evidence = evidenceList(finding.evidence); if (evidence) item.append(evidence);
    container.append(item);
  }
  if (result.rawOutput) {
    const raw = element('details', undefined, 'details-agent-response'); raw.open = result.mode === 'completed';
    raw.append(element('summary', 'Agent response'), element('pre', result.rawOutput, 'log'));
    container.append(raw);
  }
  const artifacts = element('ul', undefined, 'plain-list');
  for (const artifact of result.artifacts) {
    const item = element('li'); item.append(link(textValue(artifact.label) ?? 'Artifact', artifact.url));
    if (artifact.path) item.append(element('div', artifact.path, 'path muted fine'));
    if (artifact.url && !safeHttpsUrl(artifact.url)) item.append(element('span', '(URL is not a valid HTTPS link)', 'muted fine'));
    artifacts.append(item);
  }
  if (artifacts.childElementCount || result.nextSteps.some(step => step.kind === 'viewChanges')) {
    const section = element('section'); section.id = 'retained-artifacts'; section.append(element('h3', 'Retained artifacts and changes'));
    if (textValue(run.config.repoFolder)) section.append(element('p', `Task worktree: ${run.config.repoFolder}`, 'muted fine path'));
    if (artifacts.childElementCount) section.append(artifacts);
    section.append(element('p', 'Inspect the retained paths in your local workspace.', 'muted fine')); container.append(section);
  }
  const validation = element('ul', undefined, 'plain-list');
  validation.id = 'recorded-checks';
  const completedChecks = element('ul', undefined, 'plain-list');
  const checkNames: Record<string, string> = { context: 'Task context', 'local-review': 'Local review', verification: 'Verification', reproduction: 'Reproduction', implementation: 'Implementation', setup: 'Test setup', e2e: 'End-to-end validation', instructions: 'Reproduction instructions' };
  for (const item of result.validation) {
    const status = textValue(item.status)?.toLowerCase();
    const check = element('li', undefined, 'details-check');
    const checkName = !textValue(item.name) || item.name === item.id ? checkNames[item.id ?? ''] ?? textValue(item.name) ?? 'Check' : item.name;
    check.append(element('strong', checkName), element('span', ` · ${status === 'passed' ? 'Passed' : status === 'failed' ? 'Failed' : status === 'not_run' ? 'Not run' : textValue(item.status) ?? 'Not recorded'}${item.required === true ? ' · Required' : item.required === false ? ' · Optional' : ''}`));
    if (textValue(item.details)) check.append(element('p', item.details, 'muted fine'));
    const evidence = evidenceList(item.evidence); if (evidence) check.append(evidence);
    (result.mode === 'diagnostic' && status === 'passed' ? completedChecks : validation).append(check);
  }
  if (completedChecks.childElementCount) container.append(element('h3', 'Completed checks'), completedChecks);
  if (validation.childElementCount) container.append(element('h3', result.mode === 'diagnostic' ? 'Remaining checks' : 'Recorded checks'), validation);
  if (result.blockers.length) {
    container.append(element('h3', run.status.state === 'succeeded' ? 'Blockers' : 'Additional blockers'));
    const blockers = element('ul', undefined, 'plain-list'); for (const blocker of result.blockers) blockers.append(element('li', blocker)); container.append(blockers);
  }
  const steps = element('div', undefined, 'details-next-actions');
  const auxiliary = element('div', undefined, 'footer-actions');
  const auxiliaryKinds = ['configure', 'rerun', 'inspectResult', 'viewChanges', 'openTarget'];
  let proposalIndex = 0;
  for (const step of result.nextSteps) {
    const control = nextActionControl(step as ResultNextAction); if (!control) continue;
    if (auxiliaryKinds.includes(step.kind)) { auxiliary.append(control); continue; }
    const index = proposalIndex++;
    const item = element('div', undefined, 'details-next-action');
    item.dataset.proposalId = (step as ResultNextAction).proposalId ?? `local-proposal-${index + 1}`;
    item.append(element('span', step.recommended ? 'Recommended next step' : `Proposal ${index + 1}`, 'details-proposal-label'), control);
    if (textValue(step.reason)) item.append(element('p', step.reason, 'muted fine'));
    if (textValue(step.body)) { const draft = element('details'); draft.append(element('summary', 'View proposed text'), element('pre', step.body, 'log')); item.append(draft); }
    const availability = resultActionAvailability(run, step);
    if (!availability.enabled && availability.reasons.length) {
      const reasons = element('ul', undefined, 'details-action-reasons');
      for (const reason of availability.reasons) reasons.append(element('li', reason));
      item.append(element('strong', 'Unavailable', 'muted fine'), reasons);
    }
    const previous = latestResultOperation(step.proposalId);
    if (previous) item.append(element('p', `Latest confirmation: ${previous.status}${previous.account ? ` · @${previous.account}` : ''}`, 'muted fine'));
    steps.append(item);
  }
  if (steps.childElementCount) { const saved = element('details'); saved.append(element('summary', result.mode === 'diagnostic' ? 'Recorded recovery actions' : 'All saved proposals'), steps); container.append(saved); }
  if (auxiliary.childElementCount) { const section = element('details'); section.append(element('summary', 'Supporting actions'), auxiliary); container.append(section); }
}
async function refreshEvents(): Promise<void> {
  let caughtUp = false;
  for (let page = 0; page < 8; page++) {
    const data = await request<Events>('tasks.events', { runId, afterSequence: eventSequence, limit: 100 });
    if (data.truncated) activityNotice = 'The Host has truncated some detailed events. Results and next steps are stored separately.';
    for (const event of data.events) {
      if (event.sequence <= eventSequence) continue;
      logLines.push(`[${date(event.time)}] #${event.sequence} ${event.type}${event.text ? `\n${event.text}` : ''}`);
      eventSequence = event.sequence;
    }
    eventSequence = Math.max(eventSequence, data.nextSequence);
    if (data.events.length < 100 || eventSequence >= (run?.status.sequence ?? 0)) { caughtUp = true; break; }
  }
  if (logLines.length > 2000) {
    logLines = logLines.slice(-2000);
    activityNotice = 'Showing the latest 2,000 activity events. The Host retains the available log records. Reload this page to read again from the beginning.';
  }
  if (!caughtUp) activityNotice = 'Loading earlier events. The next refresh will continue from the current position.';
  renderLog();
}
function renderLog(): void {
  const stream = logStream === 'activity' ? undefined : rawLogs[logStream];
  const text = stream ? stream.text : logLines.join('\n\n');
  const log = byId('log');
  const display = text || (isActive(run?.status.state ?? '') ? 'Waiting for output…' : 'No output was captured for this stream.');
  if (log.textContent !== display) log.textContent = display;
  byId('log-source').textContent = stream ? stream.sample ? `Sample agent ${logStream} · Preview fixture, no real CLI was started.` : `Raw agent ${logStream} captured by the local Host.` : 'Task activity and normalized agent events.';
  const message = stream ? stream.notice : activityNotice;
  byId('log-notice').textContent = message; byId('log-notice').hidden = !message;
  if (byId<HTMLInputElement>('follow-log').checked) log.scrollTop = log.scrollHeight;
}
async function refreshRawLog(stream: 'stdout' | 'stderr'): Promise<void> {
  const current = rawLogs[stream];
  for (let page = 0; page < 8; page++) {
    const data = await request<AgentLog>('tasks.logs', { runId, stream, cursor: current.cursor, limitBytes: 64 * 1024 });
    if (data.stream !== stream || !Number.isSafeInteger(data.nextCursor) || data.nextCursor < current.cursor) throw new Error('The Host returned an invalid agent log cursor.');
    const advanced = data.nextCursor > current.cursor;
    current.text += data.text; current.cursor = data.nextCursor; current.sample = Boolean(data.sample);
    if (data.truncated) current.notice = 'Some output was truncated by the Host. Available output is shown below.';
    if (current.text.length > 1024 * 1024) { current.text = current.text.slice(-1024 * 1024); current.notice = 'Showing the latest 1,048,576 characters. The Host retains the available captured log.'; }
    if (data.eof || !advanced) break;
  }
  renderLog();
}
byId('log-stream').addEventListener('change', () => {
  logStream = byId<HTMLSelectElement>('log-stream').value as LogStream;
  renderLog(); void refresh();
});
async function refreshOperations(): Promise<void> {
  if (!run?.task.target) return;
  const [data, related] = await Promise.all([
    request<{ operations: Operation[]; truncated?: boolean; totalCount?: number }>('operations.list', { runId }),
    request<{ operations: WebActionSummary[] }>('resultActions.list', { runId }).catch(error => {
      if (error instanceof ProtocolError && ['UNKNOWN_METHOD', 'INVALID_REQUEST', 'UNSUPPORTED_METHOD'].includes(error.code)) return { operations: [] };
      throw error;
    }),
  ]);
  operations = data.operations;
  resultOperations = related.operations.filter(operation => operation.runId === runId && validOperationId(operation.operationId) && operation.target.type === run!.task.target!.type && operation.target.number === run!.task.target!.number && operation.target.repository.toLowerCase() === run!.task.repository.toLowerCase());
  operationsTruncated = Boolean(data.truncated); operationCount = data.totalCount ?? operations.length;
  byId('operations-history-section').hidden = !operations.length && !resultOperations.length && !localUnknown;
  if (pendingOperationId && operations.some(item => item.operationId === pendingOperationId)) { localUnknown = false; pendingOperationId = undefined; if (run) deleteDraft(resultDraftScope(run, 'pending-operation')); }
  renderOperations(); updateSubmit(); renderRun();
}
function renderOperations(): void {
  const signature = JSON.stringify({ operations, resultOperations, localUnknown, pendingOperationId, operationsTruncated, operationCount }); if (signature === operationsSignature) return; operationsSignature = signature;
  const container = byId('operations'); container.replaceChildren();
  if (!operations.length && !resultOperations.length) container.append(element('p', 'No GitHub actions submitted yet.'));
  if (operationsTruncated) container.append(element('p', `Total: ${operationCount} submissions. Showing unconfirmed actions and the latest ${operations.length}. Full records are stored in the local task folder.`, 'notice'));
  if (localUnknown && pendingOperationId) {
    container.append(element('p', 'The previous request has no confirmed result. Its operation ID is saved. Verify its status before submitting again.', 'notice'));
    container.append(button('Verify submission', async () => {
      await request('operations.reconcile', { runId, operationId: pendingOperationId });
      await refreshOperations();
    }));
  }
  const states = { prepared: 'Prepared', submitting: 'Submitting', succeeded: 'Submitted', failed: 'Failed', unknown: 'Unconfirmed' };
  for (const operation of operations) {
    const node = element('article', undefined, 'operation');
    const label = operation.reviewDecision?.kind === 'approve-with-limitations' ? 'Approve with validation limits' : operation.reviewDecision?.kind === 'request-evidence' ? 'Request evidence from author' : operationLabels[operation.kind];
    node.append(element('strong', `${label} · ${states[operation.status]}`), element('p', `${date(operation.createdAt)} · ${operation.operationId}`, 'muted fine'));
    if (operation.url) node.append(link('View on GitHub', operation.url));
    if (operation.error) node.append(element('p', typeof operation.error === 'string' ? operation.error : `${operation.error.message}${operation.error.guidance ? ` ${operation.error.guidance}` : ''}`, 'prewrap'));
    const details = element('details'); details.append(element('summary', operation.bodyTruncated ? 'View body excerpt (truncated)' : 'View saved submission body'), element('p', operation.body || 'No body', 'prewrap'));
    if (operation.bodyTruncated) details.append(element('p', 'The list shows a body excerpt. The full body is saved in the local operations record.', 'muted fine'));
    if (operation.suggestionCount) details.append(element('p', `Includes ${operation.suggestionCount} inline suggestions.`, 'muted fine'));
    if (operation.closeReason) details.append(element('strong', 'Recorded closing reason'), element('p', operation.closeReason, 'prewrap'));
    if (operation.reviewDecision) {
      details.append(element('p', `Review decision: ${operation.reviewDecision.kind} · ${operation.reviewDecision.headSha}`, 'muted fine path'));
      if (operation.reviewDecision.disclosure) details.append(element('p', operation.reviewDecision.disclosure, 'prewrap'));
      details.append(element('p', operation.reviewDecision.acknowledgedLimitations ? 'Validation limits explicitly acknowledged.' : 'No approval acknowledgement was given.', 'muted fine'));
    }
    node.append(details);
    if (operation.status === 'unknown' || operation.status === 'submitting') node.append(button('Verify GitHub result', async () => {
      await request('operations.reconcile', { runId, operationId: operation.operationId });
      await refreshOperations(); notice('Verification refreshed. Unconfirmed submissions are not retried automatically.');
    }));
    container.append(node);
  }
  const resultLabels: Record<string, string> = { 'create-pr': 'Create pull request', 'merge-pr': 'Merge pull request', 'trigger-ci': 'Trigger CI', 'close-as-duplicate': 'Close as duplicate' };
  for (const operation of [...resultOperations].sort((left, right) => right.createdAt.localeCompare(left.createdAt))) {
    const node = element('article', undefined, 'operation');
    node.append(element('strong', `${resultLabels[operation.kind] ?? operation.kind} · ${operation.status}`), element('p', `${date(operation.createdAt)}${operation.account ? ` · @${operation.account}` : ''} · ${operation.operationId}`, 'muted fine'));
    if (operation.error) node.append(element('p', [operation.error.message, operation.error.guidance].filter(Boolean).join(' '), 'prewrap'));
    for (const url of operation.urls) node.append(link('View on GitHub', url));
    node.append(button(operation.status === 'prepared' ? 'Continue confirmation' : ['unknown', 'submitting', 'partial'].includes(operation.status) ? 'Check action status' : 'View confirmation', () => openConfirmation(operation)));
    const proposal = run && recoveryActions(run).find(action => action.proposalId === operation.proposalId);
    if (proposal && operation.retryAllowed === true && latestResultOperation(operation.proposalId)?.operationId === operation.operationId) {
      node.append(button(operation.kind === 'trigger-ci' && operation.status === 'succeeded' ? 'Review another CI run' : 'Prepare another attempt', () => prepareResultAction(proposal, true)));
    }
    container.append(node);
  }
}
function selectedKind(): OperationKind { return byId<HTMLSelectElement>('operation-kind').value as OperationKind; }
function selectedSuggestions(): Suggestion[] {
  if (run?.result?.schemaVersion === 3) return selectedFeedback().suggestions;
  if (!['comment', 'approve', 'suggestChanges', 'requestChanges'].includes(selectedKind())) return [];
  if (!preview || !matchingReview(run?.result, run?.task.expectedHeadSha, preview)) return [];
  return suggestionEditors.filter(editor => editor.checkbox.checked && !editor.checkbox.disabled).map(editor => ({ ...editor.suggestion, body: editor.body.value, replacement: editor.replacement.value }));
}
function updateSubmit(): void {
  const kind = selectedKind();
  const node = byId<HTMLButtonElement>('submit-operation');
  node.textContent = kind === 'close' ? 'Review closing this target' : kind === 'approve' ? 'Review approval' : kind === 'requestChanges' ? 'Review change request' : 'Review feedback';
  const uncertain = localUnknown || operations.some(item => ['prepared', 'submitting', 'unknown'].includes(item.status));
  const selected = selectedFeedback();
  const incompatibleClose = kind === 'close' && (selected.findingIds.length > 0 || suggestionEditors.some(editor => editor.checkbox.checked));
  node.disabled = !run || previewState !== 'ready' || !eligibleManualOperationKinds(run, preview).includes(kind) || submitting || uncertain || selected.errors.length > 0 || incompatibleClose || kind === 'close' && !byId<HTMLTextAreaElement>('close-reason').value.trim();
  byId('body-label').hidden = kind === 'close';
  byId('close-reason-label').hidden = kind !== 'close';
  byId('suggestions').hidden = run?.result?.schemaVersion === 3 || !['comment', 'approve', 'suggestChanges', 'requestChanges'].includes(kind);
  if (!independentManual) feedback.renderSelection(byId('selected-feedback')); else { byId('selected-feedback').replaceChildren(); byId('selected-feedback').hidden = true; }
  const finalBody = byId('final-feedback-body'); finalBody.textContent = kind === 'close' ? '' : manualBody(); finalBody.hidden = !finalBody.textContent;
  byId('feedback-preview').hidden = kind === 'close';
  byId('action-selection-mode').textContent = independentManual ? 'Independent manual draft. Recommended findings and drafts are preserved separately.' : explicitAction ? 'Your chosen GitHub action is retained when findings or target details refresh.' : 'All confirmed findings start selected. You can edit or remove any finding before sending.';
  byId('use-suggested-action').hidden = !independentManual && !explicitAction;
  byId('operation-explanation').textContent = uncertain ? 'A GitHub action is unconfirmed. Verify it in Submission history before continuing.' : incompatibleClose ? 'Closing cannot include selected feedback. Choose a comment/review, or explicitly clear the selected findings and suggestions.' : previewState === 'error' ? 'Retry the GitHub check before confirming this action. Your edits are preserved.' : previewState !== 'ready' ? 'Checking the target automatically. You can continue editing your feedback.' : kind === 'close' ? 'Close the displayed target. The reason is saved with this action; no comment is posted implicitly.' : kind === 'comment' ? 'Post the additional text and selected feedback; inline suggestions use a comment review.' : kind === 'approve' ? 'Approve this PR revision with exactly the displayed text and selected feedback.' : 'Submit the edited body and selected inline suggestions.';
}
function renderSuggestions(): void {
  if (!preview) return;
  const container = byId('suggestions'); container.replaceChildren(); suggestionEditors = [];
  const verifiedReview = matchingReview(run?.result, run?.task.expectedHeadSha, preview);
  const review = run?.result?.review;
  const draft = proposalDrafts.get(activeDraftKey);
  if (review && !verifiedReview) container.append(element('p', 'The review SHA does not match the task, preview, or current PR HEAD. Refresh the Pulse context and start a new analysis to generate valid suggestions.', 'notice'));
  const seen = new Set<string>();
  if (selectedProposal && !selectedProposal.suggestionIds && review?.suggestions.length) container.append(element('p', 'Legacy proposal: its inline suggestions were not individually linked. Select the suggestions that belong to this proposal before submitting.', 'notice'));
  for (const suggestion of review?.suggestions ?? []) {
      if (selectedProposal?.suggestionIds && (!suggestion.id || !selectedProposal.suggestionIds.includes(suggestion.id))) continue;
      const key = suggestionKey(suggestion); if (seen.has(key)) continue; seen.add(key);
      const saved = draft?.suggestions.get(key);
      const original = suggestionOriginal(preview, suggestion);
      const wrapper = element('div', undefined, 'suggestion');
      const label = element('label', undefined, 'checkbox');
      const checkbox = element('input'); checkbox.type = 'checkbox'; checkbox.disabled = !verifiedReview || original === undefined; checkbox.checked = !checkbox.disabled && Boolean(saved?.checked);
      label.append(checkbox, element('span', `${suggestion.path}:${suggestion.startLine ? `${suggestion.startLine}–` : ''}${suggestion.line}`, 'suggestion-location')); wrapper.append(label);
      wrapper.append(element('h3', 'Original code'), element('pre', original ?? 'This location is outside the current PR diff and cannot be submitted.'));
      const bodyLabel = element('label', 'Comment'); const body = element('textarea'); body.rows = 2; body.value = saved?.body ?? suggestion.body ?? ''; bodyLabel.append(body);
      const replacementLabel = element('label', 'Replacement code (leave empty to delete the selected lines)'); const replacement = element('textarea'); replacement.rows = 4; replacement.value = saved?.replacement ?? suggestion.replacement ?? ''; replacementLabel.append(replacement); wrapper.append(bodyLabel, replacementLabel);
      body.disabled = replacement.disabled = checkbox.disabled;
      suggestionEditors.push({ suggestion, checkbox, body, replacement }); container.append(wrapper);
  }
  if (!suggestionEditors.length) container.append(element('p', 'No inline suggestions are available. Post a comment or run a new analysis to generate suggestions with valid diff locations.', 'notice'));
}
function prepare(force = false): Promise<void> {
  if (!run?.task.target) return Promise.reject(new Error('This task has no GitHub target.'));
  if (submitting) { refreshPreviewAfterSubmit = true; return Promise.resolve(); }
  if (previewPromise) return previewPromise;
  if (!force && previewState === 'ready') return Promise.resolve();
  clearError(githubError);
  saveDraft();
  previewState = 'loading';
  const generation = previewGeneration;
  previewPromise = loadTarget(generation).catch(error => {
    if (generation !== previewGeneration || deleted || previewRefreshQueued) return;
    if (generation === previewGeneration && !deleted) { preview = undefined; previewState = 'error'; byId('operation-target').replaceChildren(); byId('operation-reasons').hidden = true; }
    throw error;
  }).finally(() => {
    if (generation === previewGeneration && !deleted) {
      previewPromise = undefined;
      if (previewRefreshQueued && previewReturnTimer === undefined) { previewRefreshQueued = false; void prepare(true).catch(error => report(error, githubError)); }
      renderWorkspace(); updateSubmit();
    }
  });
  renderWorkspace(); updateSubmit();
  return previewPromise;
}
async function loadTarget(generation: number): Promise<void> {
  const loaded = await request<OperationPreview>('operations.preview', { runId });
  if (generation !== previewGeneration || deleted || !run || previewRefreshQueued) return;
  if (loaded.target.repository.toLowerCase() !== run.task.repository.toLowerCase() || loaded.target.type !== run.task.target?.type || loaded.target.number !== run.task.target.number ||
    loaded.expectedHeadSha?.toLowerCase() !== run.task.expectedHeadSha?.toLowerCase()) throw new Error('The GitHub response does not match this task. Refresh the target before continuing.');
  preview = loaded;
  const eligible = eligibleManualOperationKinds(run, preview);
  const target = byId('operation-target'); target.replaceChildren();
  target.append(link(`${preview.target.repository} · ${preview.target.type.toUpperCase()} #${preview.target.number}${preview.target.title ? ` · ${preview.target.title}` : ''}`, preview.target.url || preview.url));
  const facts = element('dl', undefined, 'facts');
  facts.append(labeledValue('Submitting account', preview.account ? `@${preview.account}` : 'Not signed in / Unavailable'), labeledValue('Target state', preview.target.state || preview.state));
  if (preview.target.type === 'pr') facts.append(labeledValue('Review SHA', preview.expectedHeadSha ?? '—'), labeledValue('Current PR HEAD', preview.headSha ?? '—'));
  target.append(facts);
  const reasons = [...new Set([...(preview.reasons ?? []), ...(preview.stale ? ['The PR HEAD has changed. Reload the current revision before submitting.'] : []), ...(!preview.account ? ['Select a signed-in GitHub account in Settings, then reload the target and permissions.'] : [])])];
  if (!eligible.length && !reasons.length) reasons.push('The current account or target does not permit these operations.');
  byId('operation-reasons').hidden = !reasons.length; byId('operation-reasons').textContent = reasons.join('\n');
  const kindSelect = byId<HTMLSelectElement>('operation-kind'); const previousKind = kindSelect.value;
  const proposed = manualOperationKinds(run);
  kindSelect.replaceChildren(...proposed.map(kind => {
    const option = element('option', kind === 'close' ? `Close ${preview!.target.type === 'pr' ? 'pull request' : 'issue'}` : operationLabels[kind]);
    option.value = kind; option.disabled = !eligible.includes(kind); return option;
  }));
  kindSelect.value = proposed.includes(previousKind as OperationKind) ? previousKind : eligible[0] ?? proposed[0]!;
  if (!draftLoaded) {
    draftLoaded = true; activeDraftKey ||= proposalKey(undefined);
    if (run.result?.schemaVersion !== 3) renderSuggestions();
  } else {
    // Keep edits while rechecking the remote target; a now-invalid location cannot be selected.
    for (const editor of suggestionEditors) {
      editor.checkbox.disabled = !matchingReview(run.result, run.task.expectedHeadSha, preview) || suggestionOriginal(preview, editor.suggestion) === undefined;
      editor.body.disabled = editor.replacement.disabled = editor.checkbox.disabled;
      if (editor.checkbox.disabled) editor.checkbox.checked = false;
    }
  }
  byId('operation-editor').hidden = false;
  byId('github-section').hidden = false;
  feedback.setContext(run, preview); applyDefaultAction();
  await refreshOperations();
  if (generation === previewGeneration && !deleted && !previewRefreshQueued) previewState = 'ready';
}
byId('prepare').addEventListener('click', () => {
  void prepare(true).catch(error => report(error, githubError));
});
function refreshSavedAccount(): void {
  if (deleted || !run || document.visibilityState === 'hidden' || byId('github-section').hidden || previewState === 'idle') return;
  if (submitting) { refreshPreviewAfterSubmit = true; return; }
  // Keep every return invalidation, including a quick Settings change while an
  // older request is pending. A short trailing debounce merges browser events.
  previewRefreshQueued = true; previewState = 'loading';
  if (previewReturnTimer !== undefined) clearTimeout(previewReturnTimer);
  previewReturnTimer = setTimeout(() => {
    previewReturnTimer = undefined;
    if (deleted) return;
    if (submitting) { previewRefreshQueued = false; refreshPreviewAfterSubmit = true; return; }
    if (!previewPromise) { previewRefreshQueued = false; void prepare(true).catch(error => report(error, githubError)); }
  }, 100);
  renderWorkspace(); updateSubmit();
}
globalThis.addEventListener?.('focus', refreshSavedAccount);
document.addEventListener?.('visibilitychange', refreshSavedAccount);
byId('operation-kind').addEventListener('change', () => { explicitAction = true; proposalSelectionSequence++; selectedProposal = undefined; byId('selected-proposal').hidden = true; saveDraft(); updateSubmit(); });
for (const id of ['operation-body', 'close-reason', 'suggestions']) byId(id).addEventListener('input', () => { saveDraft(); updateSubmit(); byId('draft-status').textContent = draftDurable ? 'Draft saved on this device' : 'Draft retained for this session'; });
byId('manual-merge').addEventListener('click', () => { void prepareManualAction('merge-pr').catch(report); });
byId('manual-ci').addEventListener('click', () => { void prepareManualAction('trigger-ci').catch(report); });
byId('use-suggested-action').addEventListener('click', restoreRecommended);
async function reviewSubmission(kind: OperationKind, body: string, suggestions: Suggestion[], closeReason?: string): Promise<boolean> {
  const dialog = byId<HTMLDialogElement>('feedback-confirmation');
  const content = byId('feedback-confirmation-content'); content.replaceChildren();
  const facts = element('dl', undefined, 'facts'); facts.append(labeledValue('Action', operationLabels[kind]), labeledValue('Target', `${preview!.target.repository} · ${preview!.target.type.toUpperCase()} #${preview!.target.number}`), labeledValue('Account', preview!.account ? `@${preview!.account}` : 'Not available'));
  if (preview!.expectedHeadSha) facts.append(labeledValue('Revision', preview!.expectedHeadSha));
  content.append(facts);
  if (closeReason) content.append(element('h3', 'Reason for closing'), element('p', closeReason, 'prewrap'), element('p', 'This closes the target. No comment is posted implicitly.', 'muted'));
  else content.append(element('h3', 'Complete feedback'), element('pre', body || 'No additional text.', 'log'));
  for (const suggestion of suggestions) {
    const item = element('section', undefined, 'dialog-section');
    item.append(element('h3', `${suggestion.path}:${suggestion.startLine ? `${suggestion.startLine}–` : ''}${suggestion.line}`), element('p', suggestion.body, 'prewrap'), element('strong', suggestion.replacement === '' ? 'Delete the selected lines' : 'Replacement code'), element('pre', suggestion.replacement, 'log')); content.append(item);
  }
  byId('feedback-send').textContent = operationLabels[kind]; clearError(byId('feedback-confirmation-error'));
  dialog.showModal(); byId('feedback-confirmation-title').focus?.();
  return new Promise(resolve => { confirmationResolve = resolve; });
}
byId('feedback-back').addEventListener('click', () => { confirmationResolve?.(false); confirmationResolve = undefined; byId<HTMLDialogElement>('feedback-confirmation').close(); byId('submit-operation').focus?.(); });
byId('feedback-send').addEventListener('click', () => { confirmationResolve?.(true); confirmationResolve = undefined; byId<HTMLDialogElement>('feedback-confirmation').close(); });
byId('feedback-confirmation').addEventListener('cancel', () => { confirmationResolve?.(false); confirmationResolve = undefined; });
byId('operation-form').addEventListener('submit', event => {
  event.preventDefault();
  if (!preview || previewState !== 'ready' || submitting) return;
  const submit = async (): Promise<void> => {
    const kind = selectedKind(); const body = kind === 'close' ? '' : manualBody();
    const selected = selectedFeedback();
    const proposalId = !selected.findingIds.length && selectedProposal?.kind === kind ? selectedProposal.proposalId : undefined;
    if (!run || !eligibleManualOperationKinds(run, preview).includes(kind)) throw new Error('This action is unavailable for the current target, account or PR revision.');
    if (selected.errors.length) throw new Error(selected.errors.join('\n'));
    if (kind === 'close' && (selected.findingIds.length || suggestionEditors.some(editor => editor.checkbox.checked))) throw new Error('Clear selected feedback explicitly before closing, or choose a comment/review.');
    const closeReason = kind === 'close' ? byId<HTMLTextAreaElement>('close-reason').value : undefined;
    if (kind === 'close' && !closeReason?.trim()) throw new Error('Enter the reason for closing this target.');
    const expectedAccount = preview!.account;
    const expectedHeadSha = preview!.expectedHeadSha;
    const suggestions = selectedSuggestions();
    const invalid = validateDraft(preview!, kind, body, suggestions); if (invalid) throw new Error(invalid);
    submitting = true; updateSubmit(); clearError(githubError); saveDraft();
    if (!await reviewSubmission(kind, body, suggestions, closeReason)) return;
    await refreshOperations();
    if (localUnknown || operations.some(item => ['prepared', 'submitting', 'unknown'].includes(item.status))) throw new Error('A GitHub action is unconfirmed. Verify Submission history before continuing.');
    const operationId = crypto.randomUUID();
    pendingOperationId = operationId;
    writeDraft(resultDraftScope(run, 'pending-operation'), { operationId });
    try {
      const operation = await request<Operation>('operations.submit', { runId, operationId, kind, body, expectedAccount, ...(proposalId ? { proposalId } : {}), ...(selected.findingIds.length ? { findingIds: selected.findingIds } : {}), ...(closeReason !== undefined ? { closeReason } : {}), ...(expectedHeadSha ? { expectedHeadSha } : {}), ...(suggestions.length ? { suggestions } : {}) });
      notice(operation.status === 'succeeded' ? `${operationLabels[kind]} submitted. See the GitHub result and submission history below.` : `Operation status: ${operation.status}. See Submission history.`);
    } catch (error) {
      localUnknown = !(error instanceof ProtocolError) || ['RESPONSE_UNKNOWN', 'HOST_DISCONNECTED', 'HOST_UNAVAILABLE', 'CLIENT_ERROR', 'HOST_ERROR', 'RECORD_UNREADABLE', 'OUTPUT_TOO_LARGE', 'PROTOCOL_MISMATCH'].includes(error.code);
      if (!localUnknown) { pendingOperationId = undefined; deleteDraft(resultDraftScope(run, 'pending-operation')); }
      throw error;
    } finally { await refreshOperations(); }
  };
  void submit().catch(error => report(error, githubError)).finally(() => {
    submitting = false; updateSubmit();
    if (refreshPreviewAfterSubmit) { refreshPreviewAfterSubmit = false; renderRun(); if (previewState !== 'loading') refreshSavedAccount(); }
  });
});
byId('delete-confirmed').addEventListener('change', () => { byId<HTMLButtonElement>('delete-run').disabled = !byId<HTMLInputElement>('delete-confirmed').checked; });
byId('delete-run').addEventListener('click', () => {
  if (!byId<HTMLInputElement>('delete-confirmed').checked) return;
  const node = byId<HTMLButtonElement>('delete-run'); node.disabled = true;
  void request('tasks.delete', { runId }).then(() => {
    deleted = true; document.title = 'Record deleted'; byId('title').textContent = 'This task record was deleted'; byId('run-controls').replaceChildren(); byId('github-section').hidden = true; byId('cleanup-section').hidden = true; notice('Local history deleted. Repository changes and artifacts are preserved.');
  }).catch(report).finally(() => { node.disabled = false; });
});
byId('more-actions').addEventListener('keydown', event => { if ((event as KeyboardEvent).key === 'Escape') { byId<HTMLDetailsElement>('more-actions').open = false; byId('more-actions-label').focus?.(); } });
byId('more-actions-list').addEventListener('click', () => { byId<HTMLDetailsElement>('more-actions').open = false; });
byId('result-evidence').addEventListener('click', event => {
  const target = event.target as HTMLElement | null;
  const anchor = target?.closest?.('a[href^="#"]') as HTMLAnchorElement | null;
  if (anchor?.hash) { const id = decodeURIComponent(anchor.hash.slice(1)); if (document.getElementById(id)) reveal(id); }
});
async function refresh(forceReport = true): Promise<void> {
  if (!runId || refreshing || deleted) return;
  // Keep a report-read error reviewable until the user asks to load that complete snapshot again.
  if (resultReadError && !forceReport) { await connectionStatus(); return; }
  refreshing = true;
  try {
    const snapshot = await request<Run>('tasks.get', { runId });
    run = snapshot; loadingResult = Boolean(snapshot.resultPaging) && !hasCompleteResultCache(snapshot); resultReadError = '';
    if (loadingResult) renderRun();
    try { run = await loadFullResult(snapshot); } catch (error) { resultReadError = error instanceof Error ? error.message : 'The complete report could not be read.'; }
    loadingResult = false; renderRun();
    const work: Promise<unknown>[] = [logStream === 'activity' ? refreshEvents() : refreshRawLog(logStream)];
    if (refreshCount++ % 3 === 0 || operations.some(item => ['submitting', 'unknown'].includes(item.status))) work.push(refreshOperations());
    if (!readMarked && !isActive(run.status.state)) {
      work.push(request('tasks.read', { runId, value: true }).then(() => { readMarked = true; }));
    }
    const results = await Promise.allSettled(work);
    const failed = results.find(result => result.status === 'rejected');
    if (failed?.status === 'rejected') report(failed.reason); else clearError();
  } catch (error) { report(error); }
  finally { refreshing = false; await connectionStatus(); }
}
if (!runId) report(new Error('Missing runId. Open a run from the task panel.'));
else { void refresh(); setInterval(() => { void refresh(false); }, 2_500); }
