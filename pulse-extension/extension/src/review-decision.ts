import { compatibleVerificationEvidence, isFinalReportComplete, reviewModeLabel, scopedReviewConclusion, textValue } from './details-model.js';
import { isActive, ProtocolError } from './policy.js';
import { verificationExecutionOptions } from './rerun-dialog.js';
import { button, element, errorText, labeledValue, request } from './ui.js';
import type { AgentDefaults, RelatedVerification, ReviewLimitation, Run, TaskExecution, VerificationEvidence } from './types.js';

export function limitationList(limitations: ReviewLimitation[]): HTMLElement {
  const list = element('ul', undefined, 'plain-list');
  for (const limitation of limitations) {
    const row = element('li', undefined, 'details-check');
    row.append(element('strong', limitation.name || limitation.id), element('span', ` · ${limitation.category === 'recorded-observation' ? 'Recorded observation · ' : ''}${limitation.status === 'not_run' ? 'Not run' : limitation.status === 'failed' ? 'Failed' : limitation.status}`));
    if (limitation.category === 'recorded-observation') row.append(element('p', 'Preserved historical observation; this is not an additional request for the author to resolve.', 'muted fine'));
    if (limitation.details) row.append(element('p', limitation.details, 'muted fine'));
    if (limitation.evidence?.length) {
      const evidence = element('ul', undefined, 'details-evidence');
      for (const text of limitation.evidence) evidence.append(element('li', text));
      row.append(evidence);
    }
    list.append(row);
  }
  return list;
}

type VerificationRecommendation = NonNullable<RelatedVerification['recommendation']>;
interface VerificationAttempt { parentRunId: string; requestId: string; recommendationId: string; prerequisitesConfirmed?: boolean; execution?: TaskExecution; acceptedRunId?: string; expectedMode?: 'build-tests' | 'ui-e2e'; expectedHeadSha?: string; recommendation?: VerificationRecommendation }
interface PreparationDraft { prerequisitesConfirmed: boolean; execution?: TaskExecution }
const attempts = new Map<string, VerificationAttempt>();
const uuid = (value: unknown): value is string => typeof value === 'string' && /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(value);
const attemptKey = (parentRunId: string): string => 'pulse-review-verification:' + parentRunId;
const preparationKey = (parentRunId: string, recommendationId: string): string => `pulse-verification-draft:${parentRunId}:${recommendationId}`;
function validExecution(value: unknown): value is TaskExecution | undefined {
  if (value === undefined) return true;
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const execution = value as TaskExecution;
  return Object.keys(execution).every(key => ['agent', 'model', 'reasoningEffort'].includes(key)) &&
    (execution.agent === undefined || execution.agent === 'codex' || execution.agent === 'copilot') &&
    (execution.model === undefined || typeof execution.model === 'string') && (execution.reasoningEffort === undefined || typeof execution.reasoningEffort === 'string');
}
function validRecommendation(value: unknown): value is VerificationRecommendation {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const item = value as VerificationRecommendation;
  return /^[a-f0-9]{64}$/i.test(item.recommendationId) && ['build-tests', 'ui-e2e'].includes(item.mode) &&
    typeof item.reason === 'string' && typeof item.question === 'string' && ['ready', 'unknown', 'missing-prerequisites'].includes(item.readiness) &&
    [item.scenarios, item.prerequisites, item.evidence].every(values => Array.isArray(values) && values.every(value => typeof value === 'string')) &&
    (item.expectedResults === undefined || Array.isArray(item.expectedResults) && item.expectedResults.every(value => typeof value === 'string'));
}
function readPreparation(parentRunId: string, recommendationId: string): PreparationDraft | undefined {
  try {
    const draft = JSON.parse(sessionStorage.getItem(preparationKey(parentRunId, recommendationId)) ?? 'null') as PreparationDraft | null;
    if (draft && typeof draft.prerequisitesConfirmed === 'boolean' && validExecution(draft.execution)) return draft;
  } catch { /* A draft is optional; saved Host request identity stays separate. */ }
  return undefined;
}
function pendingVerification(parentRunId: string): VerificationAttempt | undefined {
  if (attempts.has(parentRunId)) return attempts.get(parentRunId);
  try {
    const saved = JSON.parse(sessionStorage.getItem(attemptKey(parentRunId)) ?? 'null') as VerificationAttempt | null;
    if (saved?.parentRunId === parentRunId && uuid(saved.requestId) && /^[a-f0-9]{64}$/i.test(saved.recommendationId) &&
      (saved.acceptedRunId === undefined || uuid(saved.acceptedRunId)) && (saved.prerequisitesConfirmed === undefined || typeof saved.prerequisitesConfirmed === 'boolean') &&
      (saved.expectedMode === undefined || saved.expectedMode === 'build-tests' || saved.expectedMode === 'ui-e2e') && (saved.expectedHeadSha === undefined || /^[a-f0-9]{40}$/i.test(saved.expectedHeadSha)) &&
      (saved.recommendation === undefined || validRecommendation(saved.recommendation) && saved.recommendation.recommendationId === saved.recommendationId) &&
      (saved.execution === undefined || saved.execution && typeof saved.execution === 'object' && !Array.isArray(saved.execution) && Object.keys(saved.execution).every(key => ['agent', 'model', 'reasoningEffort'].includes(key)) &&
        (saved.execution.agent === undefined || ['codex', 'copilot'].includes(saved.execution.agent)) && (saved.execution.model === undefined || typeof saved.execution.model === 'string') && (saved.execution.reasoningEffort === undefined || typeof saved.execution.reasoningEffort === 'string'))) {
      attempts.set(parentRunId, saved); return saved;
    }
  } catch { /* The Host still preserves accepted request identities. */ }
  return undefined;
}
function saveVerification(parentRunId: string, attempt?: VerificationAttempt): void {
  if (attempt) attempts.set(parentRunId, attempt); else attempts.delete(parentRunId);
  try { if (attempt) sessionStorage.setItem(attemptKey(parentRunId), JSON.stringify(attempt)); else sessionStorage.removeItem(attemptKey(parentRunId)); } catch { /* Keep retry identity in memory. */ }
}
function strings(title: string, values: string[]): HTMLElement | undefined {
  if (!values.some(value => textValue(value))) return undefined;
  const section = element('div'); section.append(element('h4', title));
  const list = element('ul', undefined, 'plain-list');
  for (const value of values) if (textValue(value)) list.append(element('li', value));
  section.append(list); return section;
}
function evidenceRow(value: VerificationEvidence): HTMLElement {
  const row = element('article', undefined, 'scoped-review-evidence');
  const source = { 'current-run': 'This run', ci: 'CI', author: 'Author', 'prior-run': 'Related run' }[value.source];
  const kind = { build: 'Build', 'automated-tests': 'Automated tests', runtime: 'Runtime' }[value.kind];
  row.append(element('strong', source + ' · ' + kind), element('span', ' · ' + (value.status === 'passed' ? 'Passed' : value.status === 'failed' ? 'Failed' : 'Not run')));
  row.append(element('p', value.summary, 'prewrap'));
  if (value.revisionSha) row.append(element('p', 'Original PR revision: ' + value.revisionSha, 'muted fine path'));
  const evidence = strings('Evidence', value.evidence); if (evidence) row.append(evidence);
  if (value.runId && uuid(value.runId)) {
    const anchor = element('a', 'Open evidence run'); anchor.href = chrome.runtime.getURL('details.html?runId=' + encodeURIComponent(value.runId)); row.append(anchor);
  }
  return row;
}

/** Scope and related verification are projections; no manual GitHub publishing bypass is exposed. */
export function scopedReviewController(container: HTMLElement, onChange?: () => void) {
  let run: Run | undefined;
  let related: RelatedVerification | undefined;
  let loading = false; let sending = false; let loadError = ''; let ticket = 0;
  let signature = ''; let loadedParent = ''; let prerequisitesConfirmed = false;
  let executionOptions: ReturnType<typeof verificationExecutionOptions> | undefined;
  let executionError = '';
  let preparationDialog: HTMLDialogElement | undefined;
  let refreshPromise: Promise<void> | undefined;
  let updatePreparation: (() => void) | undefined;
  const parentId = (): string | undefined => run?.task.followUp?.parentRunId ?? run?.runId;
  const supports = (): boolean => Boolean(run && ['pr-review', 'pr-verify'].includes(run.task.actionKind) && run.task.target?.type === 'pr');
  const covered = (): boolean => run?.result?.schemaVersion === 3
    ? related?.currentConclusion.evidenceComplete === true && Boolean(related.currentConclusion.completedRunId && uuid(related.currentConclusion.completedRunId))
    : related?.currentConclusion.canSupplementAssessment === true;
  const preparationState = () => {
    const pending = parentId() ? pendingVerification(parentId()!) : undefined;
    const complete = Boolean(run && (run.result?.schemaVersion !== 3 || run.task.actionKind !== 'pr-review' || isFinalReportComplete(run)));
    return { available: Boolean(supports() && run && !isActive(run.status.state) && complete && (pending || related?.recommendation && related.recommendation.level !== 'not_needed')),
      loading, covered: covered(), pending: Boolean(pending), label: pending?.acceptedRunId ? 'Open accepted verification' : pending ? 'Recover verification request' : 'Review verification requirements' };
  };
  const savePreparation = (): void => {
    const parent = parentId(); const recommendation = related?.recommendation;
    if (!parent || !recommendation || pendingVerification(parent)) return;
    try { sessionStorage.setItem(preparationKey(parent, recommendation.recommendationId), JSON.stringify({ prerequisitesConfirmed, ...(executionOptions?.read() ? { execution: executionOptions.read() } : {}) })); } catch { /* The open dialog retains the draft in memory. */ }
  };
  const draw = (): void => {
    container.hidden = !supports(); container.replaceChildren();
    if (!run || !supports()) return;
    const v3 = run.result?.schemaVersion === 3;
    container.append(element('h2', run.task.actionKind === 'pr-verify' ? 'Verification scope' : v3 ? 'E2E verification assessment' : 'Review scope'));
    const scope = element('dl', undefined, 'facts');
    scope.append(labeledValue('Selected scope', reviewModeLabel(run.task.reviewOptions?.mode)), labeledValue('PR revision', run.task.expectedHeadSha ?? 'Not recorded'));
    container.append(scope);
    if (!run.task.reviewOptions) container.append(element('p', 'This historical run did not record its scope. Its original report remains unchanged.', 'muted fine'));
    if (isActive(run.status.state)) { container.append(element('p', 'This run is in progress. Its conclusion and verification evidence will appear when it finishes.', 'muted')); return; }
    if (v3 && run.task.actionKind === 'pr-review') {
      if (!isFinalReportComplete(run)) { container.append(element('p', 'The final report and E2E assessment are incomplete. Missing assessment data is not treated as “not needed”.', 'muted')); return; }
      const assessment = run.result?.e2eAssessment;
      if (!assessment) container.append(element('p', 'E2E necessity was not recorded. It is not treated as unnecessary.', 'notice'));
      else {
        const supplied = related ? related.currentConclusion.evidenceComplete === true && Boolean(related.currentConclusion.completedRunId) : run.result?.e2eEvidenceComplete === true;
        const level = assessment.level === 'not_needed' ? 'Not needed' : assessment.level === 'recommended' ? 'Recommended' : 'Required';
        container.append(element('strong', `${level}${supplied && assessment.level !== 'not_needed' ? ' · Evidence supplied' : ''}`, 'e2e-level'));
        if (!related || assessment.level === 'not_needed') {
          container.append(element('p', assessment.reason));
          if (assessment.question) container.append(element('p', assessment.question, 'prewrap'));
          const scenarios = strings('Scenarios and expected results', assessment.scenarios.map((scenario, index) => `${scenario}${assessment.expectedResults[index] ? `\nExpected: ${assessment.expectedResults[index]}` : ''}`)); if (scenarios) container.append(scenarios);
          for (const block of [strings('Prerequisites', assessment.prerequisites), strings('Recorded evidence', assessment.evidence)]) if (block) container.append(block);
        }
      }
    }

    const conclusion = scopedReviewConclusion(run);
    if (run.task.actionKind === 'pr-review' && !v3) {
      container.append(element('h3', 'Recorded code conclusion'));
      if (conclusion) {
        const label = conclusion.status === 'no-blocking-findings' ? 'No blocking findings' : conclusion.status === 'changes-requested' ? 'Changes requested' : 'Inconclusive';
        container.append(element('strong', label), element('p', conclusion.summary, 'prewrap'));
        const uncertainties = strings('Blocking uncertainties', conclusion.blockingUncertainties); if (uncertainties) container.append(uncertainties);
      } else container.append(element('p', 'A code conclusion for this exact PR revision was not recorded.', 'muted'));
    } else if (run.task.actionKind === 'pr-verify') {
      container.append(element('p', 'This task adds verification evidence to its parent review; it does not replace the code conclusion.', 'muted'));
      if (parentId()) { const link = element('a', 'Open parent review'); link.href = chrome.runtime.getURL('details.html?runId=' + encodeURIComponent(parentId()!)); container.append(link); }
    }

    const recorded = compatibleVerificationEvidence(run);
    if (recorded.length) { container.append(element('h3', 'Evidence for this PR revision')); for (const row of recorded) container.append(evidenceRow(row)); }
    const other = (run.result?.verificationEvidence ?? []).filter(row => !recorded.includes(row));
    if (other.length) {
      const excluded = element('details'); excluded.append(element('summary', 'Other recorded evidence'));
      excluded.append(element('p', 'Local-candidate or different-revision evidence is preserved separately and does not verify the original PR.', 'muted fine'));
      for (const row of other) {
        const item = element('p', row.summary, 'prewrap'); excluded.append(item);
        excluded.append(element('p', [row.subject === 'local-candidate' ? 'Local candidate' : 'Unmatched original PR revision', row.revisionSha || 'Revision not recorded'].join(' · '), 'muted fine path'));
        const references = strings('Recorded evidence', row.evidence); if (references) excluded.append(references);
      }
      container.append(excluded);
    }
    if (related) {
      container.append(element('h3', 'Current conclusion with related verification'), element('p', related.currentConclusion.summary, 'prewrap'));
      const accepted = compatibleVerificationEvidence(run, related.evidence).filter(row => row.source === 'prior-run' && row.runId && related!.runs.find(child => child.runId === row.runId)?.compatibility.eligible !== false);
      for (const row of accepted) container.append(evidenceRow(row));
      if (related.truncated) container.append(element('p', `Showing ${related.runs.length}${typeof related.totalCount === 'number' ? ` of ${related.totalCount}` : ''} related runs. Some runs or evidence are omitted from this view; open the linked records for their full details.`, 'muted fine'));
      if (related.runs.length) {
        const history = element('details'); history.append(element('summary', 'Related verification runs'));
        for (const child of related.runs) {
          const item = element('article', undefined, 'scoped-review-related');
          const label = child.status.state === 'succeeded' ? child.outcome === 'completed' ? 'Finished' : 'Verification incomplete' : child.status.state === 'running' || child.status.state === 'accepted' ? 'In progress' : child.status.state;
          const link = element('a', 'Open verification run'); link.href = chrome.runtime.getURL('details.html?runId=' + encodeURIComponent(child.runId));
          item.append(element('strong', label), element('p', child.summary || 'No final summary yet.'), link);
          if (!child.compatibility.eligible) item.append(element('p', child.compatibility.reason || 'This evidence is not compatible with the current review.', 'muted fine'));
          history.append(item);
        }
        container.append(history);
      }
    }

    const recommendation = related?.recommendation;
    if (recommendation && (!v3 || recommendation.level !== 'not_needed')) {
      const section = element('section', undefined, 'scoped-review-recommendation');
      const pending = parentId() ? pendingVerification(parentId()!) : undefined;
      const supplied = covered();
      section.append(element('h3', supplied ? 'Verification evidence available' : 'Saved verification requirements'));
      const description = element('details', undefined, 'dialog-disclosure');
      description.append(element('summary', supplied ? 'Original verification recommendation' : `${recommendation.scenarios.length} scenarios · ${recommendation.prerequisites.length} preparation requirements`)); section.append(description);
      description.append(element('strong', reviewModeLabel(recommendation.mode)), element('p', recommendation.reason), element('p', recommendation.question, 'prewrap'));
      for (const node of [strings('Scenarios', recommendation.scenarios.map((scenario, index) => `${scenario}${recommendation.expectedResults?.[index] ? `\nExpected: ${recommendation.expectedResults[index]}` : ''}`)), strings('Prerequisites', recommendation.prerequisites), strings('Why this check is useful', recommendation.evidence)]) if (node) description.append(node);
      if (supplied && !pending) {
        section.append(element('p', related?.currentConclusion.currentRunEvidenceComplete ? 'Verified in this run. The original necessity assessment is retained above.' : related?.currentConclusion.attributedEvidenceComplete ? 'Covered by cited evidence. The original necessity assessment is retained above.' : 'Compatible verification has supplied the recommended evidence. The original recommendation is retained above.', 'muted'));
        const evidence = compatibleVerificationEvidence(run, related?.evidence).find(row => row.source === 'prior-run' && row.status === 'passed' && row.runId && uuid(row.runId));
        const completedId = v3 ? related?.currentConclusion.completedRunId : evidence?.runId;
        if (completedId) { const sameRun = completedId === run.runId; const open = element('a', sameRun ? 'View checks from this run' : 'Open verification result', 'button'); open.href = sameRun ? '#recorded-checks' : chrome.runtime.getURL('details.html?runId=' + encodeURIComponent(completedId)); section.append(open); }
        section.append(button('Review a repeat verification', openPreparation));
      } else {
        section.append(element('p', recommendation.reason));
        const review = button(preparationState().label, openPreparation); review.id = 'review-verification-requirements'; review.disabled = sending || loading;
        section.append(review, element('p', 'Review the saved scope and prepare the environment before starting a separate verification task.', 'muted fine'));
      }
      container.append(section);
    } else if (parentId() && pendingVerification(parentId()!)) {
      const recover = button(preparationState().label, openPreparation); recover.id = 'review-verification-requirements'; recover.disabled = sending || loading;
      container.append(element('p', 'A previous verification request is awaiting confirmation. Recover that request before starting another one.', 'notice'), recover);
    } else if (related) container.append(element('p', v3 && run.result?.e2eAssessment?.level === 'not_needed' ? 'This report explicitly records that additional E2E verification is not needed.' : 'No further verification was recommended for this saved review.', 'muted fine'));
    if (loading) container.append(element('p', 'Loading related verification…', 'muted fine'));
    if (loadError) { const error = element('p', loadError, 'error'); error.setAttribute('role', 'alert'); container.append(error); }
    if (executionError) container.append(element('p', executionError, 'error'));
    const refresh = button('Refresh related evidence', refreshRelated); refresh.disabled = loading || sending; container.append(refresh);
    updatePreparation?.(); onChange?.();
  };
  const loadRelated = async (): Promise<void> => {
    if (!run || !supports() || isActive(run.status.state)) return;
    loading = true; loadError = ''; draw(); const currentTicket = ++ticket; const expectedParent = parentId();
    try {
      const [relationResult, defaultsResult] = await Promise.allSettled([request<RelatedVerification>('reviews.related', { runId: run.runId }), request<AgentDefaults>('agents.defaults')]);
      if (relationResult.status === 'rejected') throw relationResult.reason;
      const value = relationResult.value;
      if (currentTicket !== ticket) return;
      if (!value || value.parentRunId !== expectedParent || !Array.isArray(value.runs) || !Array.isArray(value.evidence) || !value.currentConclusion) throw new Error('The Host returned related evidence for a different review.');
      if (value.recommendation && !validRecommendation(value.recommendation)) throw new Error('The Host returned an invalid verification recommendation.');
      const changed = related?.recommendation?.recommendationId !== value.recommendation?.recommendationId;
      const draft = expectedParent && value.recommendation ? readPreparation(expectedParent, value.recommendation.recommendationId) : undefined;
      if (changed) { if (preparationDialog && !pendingVerification(expectedParent!)) preparationDialog.close(); prerequisitesConfirmed = draft?.prerequisitesConfirmed ?? false; }
      const pending = expectedParent ? pendingVerification(expectedParent) : undefined;
      const selectedExecution = pending ? pending.execution : changed ? draft?.execution : executionOptions?.read();
      if (defaultsResult.status === 'fulfilled') {
        if (!preparationDialog || !executionOptions) { executionOptions = verificationExecutionOptions(defaultsResult.value); executionOptions.restore(selectedExecution); executionOptions.node.addEventListener('change', savePreparation); executionOptions.node.addEventListener('input', savePreparation); }
        executionError = '';
      }
      else { executionOptions = undefined; executionError = `Run options could not be loaded. ${errorText(defaultsResult.reason)}`; }
      related = value; loadedParent = expectedParent ?? '';
      if (value.errors?.length) loadError = 'Some related verification records could not be read. The original report remains available.';
    } catch (error) { loadError = error instanceof ProtocolError && error.code === 'UNSUPPORTED_OPERATION' ? 'This Host does not support related verification. The saved scope and evidence are shown above.' : errorText(error); }
    finally { loading = false; draw(); }
  };
  const refreshRelated = (): Promise<void> => {
    if (refreshPromise) return refreshPromise;
    refreshPromise = loadRelated().finally(() => { refreshPromise = undefined; }); return refreshPromise;
  };
  /** This entry point only opens preparation. It never submits a verification request. */
  const openPreparation = async (): Promise<void> => {
    if (preparationDialog) { preparationDialog.focus(); return; }
    if (loading || !related) await refreshRelated();
    if (!run || !preparationState().available) return;
    const parent = parentId()!; const pending = pendingVerification(parent);
    const recommendation = pending?.recommendation ?? (pending && pending.recommendationId !== related?.recommendation?.recommendationId ? undefined : related?.recommendation);
    const dialog = element('dialog', undefined, 'rerun-dialog task-preparation-dialog'); dialog.id = 'verification-preparation-dialog'; dialog.setAttribute('aria-labelledby', 'verification-preparation-title');
    const form = element('form'); const title = element('h2', 'Review verification requirements'); title.id = 'verification-preparation-title';
    form.append(title, element('p', 'This saved plan adds evidence to the original review. It keeps the same target, revision and scenarios.', 'muted'));
    const context = element('dl', undefined, 'facts dialog-context');
    context.append(labeledValue('Target', `${run.task.repository} · PR #${run.task.target!.number}`), labeledValue('PR revision', pending?.expectedHeadSha ?? run.task.expectedHeadSha ?? 'Not recorded'), labeledValue('Verification scope', reviewModeLabel(pending?.expectedMode ?? recommendation?.mode)));
    form.append(context);
    if (recommendation) {
      const scope = element('section', undefined, 'dialog-section'); scope.append(element('h3', 'What this verification will establish'), element('p', recommendation.question || recommendation.reason, 'prewrap'));
      for (const node of [strings('Scenarios and expected results', recommendation.scenarios.map((scenario, index) => `${scenario}${recommendation.expectedResults?.[index] ? `\nExpected: ${recommendation.expectedResults[index]}` : ''}`)), strings('Prepare before starting', recommendation.prerequisites)]) if (node) scope.append(node);
      const readiness = recommendation.readiness === 'missing-prerequisites' ? 'The saved assessment records missing prerequisites. Prepare the listed environment before starting.' : recommendation.readiness === 'unknown' ? 'Environment readiness has not been established. Setup checks will run before the scenarios.' : 'The saved assessment records the environment as ready. The new task still checks it before running.';
      scope.append(element('p', readiness, recommendation.readiness === 'missing-prerequisites' ? 'notice' : 'muted fine')); form.append(scope);
      const evidence = element('details', undefined, 'dialog-disclosure'); evidence.append(element('summary', 'Reason and source evidence'), element('p', recommendation.reason, 'prewrap'));
      const evidenceList = strings('Recorded evidence', recommendation.evidence); if (evidenceList) evidence.append(evidenceList);
      evidence.append(element('p', `Parent review: ${parent}\nSaved recommendation: ${pending?.recommendationId ?? recommendation.recommendationId}`, 'path muted fine')); form.append(evidence);
    } else form.append(element('p', 'This older pending request did not save its scenario preview. Recover its exact request identity below; the current recommendation is not substituted for it.', 'notice'));
    if (executionOptions) { executionOptions.lock(sending || Boolean(pending)); form.append(executionOptions.node); }
    const confirmation = element('label', undefined, 'checkbox dialog-confirmation'); const check = element('input'); check.type = 'checkbox'; check.id = 'verification-prerequisites-confirmed';
    check.checked = pending ? true : prerequisitesConfirmed;
    confirmation.append(check, element('span', recommendation?.readiness === 'missing-prerequisites' ? 'I reviewed this scope and prepared the listed environment. Check it before running.' : 'I reviewed this scope and am ready for the environment checks and saved scenarios.'));
    form.append(confirmation, element('p', 'This acknowledgment does not establish that the environment or verification has passed.', 'muted fine'));
    const notice = element('p', undefined, 'notice'); const error = element('p', undefined, 'error'); error.setAttribute('role', 'alert');
    const actions = element('div', undefined, 'footer-actions dialog-actions'); const cancel = button('Close', () => dialog.close());
    const start = element('button', 'Start verification', 'primary'); start.type = 'submit'; start.id = 'start-review-verification';
    actions.append(cancel, start); form.append(notice, error, actions); dialog.append(form); document.body.append(dialog); preparationDialog = dialog;
    updatePreparation = (): void => {
      const current = pendingVerification(parent); const locked = sending || Boolean(current);
      check.disabled = locked; executionOptions?.lock(locked); cancel.disabled = sending;
      start.disabled = sending || loading || !current && (!executionOptions || !prerequisitesConfirmed);
      start.textContent = sending ? 'Starting verification…' : current?.acceptedRunId ? 'Open accepted verification' : current ? 'Recover verification request' : 'Start verification';
      notice.hidden = !current; notice.textContent = current?.acceptedRunId ? 'This task was accepted. Open its saved result without starting it again.' : current ? 'The response is unconfirmed. Recovery keeps the same request, saved scope and execution options.' : '';
      error.textContent = [loadError, executionError].filter(Boolean).join('\n'); error.hidden = !error.textContent;
    };
    check.addEventListener('change', () => { prerequisitesConfirmed = check.checked; savePreparation(); updatePreparation?.(); });
    form.addEventListener('submit', event => { event.preventDefault(); if (!start.disabled) void verify(); });
    dialog.addEventListener('cancel', event => { if (sending) event.preventDefault(); });
    dialog.addEventListener('close', () => { savePreparation(); preparationDialog = undefined; updatePreparation = undefined; dialog.remove(); });
    updatePreparation(); dialog.showModal();
  };
  const verify = async (): Promise<void> => {
    const parent = parentId(); let attempt = parent ? pendingVerification(parent) : undefined;
    const recommendation = attempt?.recommendation ?? related?.recommendation;
    if (!run || !parent || !attempt && !recommendation || sending) return;
    if (!attempt) {
      if (!recommendation || !preparationDialog || !executionOptions || !prerequisitesConfirmed) return;
      attempt = { parentRunId: parent, requestId: crypto.randomUUID(), recommendationId: recommendation.recommendationId,
        expectedMode: recommendation.mode, expectedHeadSha: run.task.expectedHeadSha, recommendation: structuredClone(recommendation),
        ...(recommendation.readiness !== 'ready' ? { prerequisitesConfirmed: true } : {}), ...(executionOptions?.read() ? { execution: executionOptions.read() } : {}) };
      saveVerification(parent, attempt);
      try { sessionStorage.removeItem(preparationKey(parent, attempt.recommendationId)); } catch { /* The pending request now preserves the choices. */ }
    }
    if ((!attempt.expectedMode || !attempt.expectedHeadSha) && recommendation && attempt.recommendationId === recommendation.recommendationId) {
      attempt.expectedMode ??= recommendation.mode; attempt.expectedHeadSha ??= run.task.expectedHeadSha; saveVerification(parent, attempt);
    }
    sending = true; loadError = ''; draw();
    try {
      if (!attempt.acceptedRunId) {
        const payload = { parentRunId: attempt.parentRunId, requestId: attempt.requestId, recommendationId: attempt.recommendationId,
          ...(attempt.prerequisitesConfirmed !== undefined ? { prerequisitesConfirmed: attempt.prerequisitesConfirmed } : {}), ...(attempt.execution ? { execution: attempt.execution } : {}) };
        const accepted = await request<Run>('reviews.verify', payload);
        const acceptedTask = accepted.task; const target = run.task.target;
        if (!uuid(accepted.runId) || !acceptedTask || acceptedTask.requestId !== attempt.requestId || acceptedTask.actionKind !== 'pr-verify' ||
          acceptedTask.followUp?.parentRunId !== attempt.parentRunId || acceptedTask.followUp.recommendationId !== attempt.recommendationId ||
          !attempt.expectedMode || acceptedTask.reviewOptions?.mode !== attempt.expectedMode || !attempt.expectedHeadSha ||
          acceptedTask.followUp.subject !== 'original-pr' || acceptedTask.followUp.revisionSha?.toLowerCase() !== attempt.expectedHeadSha.toLowerCase() ||
          acceptedTask.expectedHeadSha?.toLowerCase() !== attempt.expectedHeadSha.toLowerCase() || acceptedTask.target?.type !== 'pr' ||
          acceptedTask.target.number !== target?.number || acceptedTask.repository?.toLowerCase() !== run.task.repository.toLowerCase())
          throw new Error('The Host response did not match the confirmed verification request. Its outcome is unknown; recover this same request before starting another one.');
        attempt.acceptedRunId = accepted.runId; saveVerification(parent, attempt);
      }
      await chrome.tabs.create({ url: chrome.runtime.getURL('details.html?runId=' + encodeURIComponent(attempt.acceptedRunId)) });
      prerequisitesConfirmed = false; preparationDialog?.close(); saveVerification(parent);
      try { sessionStorage.removeItem(preparationKey(parent, attempt.recommendationId)); } catch { /* The accepted Host task remains authoritative. */ }
      await refreshRelated();
    } catch (error) {
      if (!attempt.acceptedRunId && error instanceof ProtocolError && ['INVALID_REQUEST', 'INVALID_EXECUTION', 'RECOMMENDATION_CHANGED', 'VERIFICATION_SUBJECT_MISMATCH', 'VERIFICATION_PREREQUISITES_MISSING'].includes(error.code)) saveVerification(parent);
      loadError = errorText(error);
    } finally { sending = false; draw(); }
  };
  return {
    update: draw,
    refresh: refreshRelated,
    openPreparation,
    preparationState,
    render(value: Run): void {
      const next = JSON.stringify([value.runId, value.status.state, value.task.reviewOptions, value.result?.reviewConclusion, value.result?.verificationEvidence, value.result?.verificationRecommendation, value.result?.report, value.result?.e2eAssessment, value.result?.e2eEvidenceComplete]);
      if (run?.runId !== value.runId) { preparationDialog?.close(); related = undefined; executionOptions = undefined; prerequisitesConfirmed = false; loadedParent = ''; ticket++; }
      run = value;
      if (next === signature) return;
      signature = next; draw();
      if (supports() && !isActive(value.status.state) && loadedParent !== parentId()) void refreshRelated();
    },
  };
}
