import type { ActionAvailability, BugAssessment, FeatureAssessment, FinalReport, OperationKind, OperationPreview, Result, ResultActionKind, ResultDiagnostic, ResultFinding, ResultFindingV3, ResultNextAction, ResultOutcome, ResultPhase, ResultRecommendation, ReviewConclusion, ReviewMode, ReviewSummary, Run, Suggestion, VerificationEvidence } from './types.js';
import { isActive } from './policy.js';
import { allowedOperation, matchingReview, operationKinds, suggestionOriginal } from './review.js';

export function textValue(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined;
}

export function executionSelection(value: unknown): string {
  return typeof value === 'string' ? value.trim() || 'CLI default' : 'Not recorded';
}

export function observedExecutionValue(run: Run, field: 'model' | 'reasoningEffort'): string {
  return textValue(run.status.observedExecution?.[field]) ?? (isActive(run.status.state) ? 'Reading from CLI…' : 'Not recorded');
}

const outcomeLabels: Record<ResultOutcome, string> = { completed: 'Completed', blocked: 'Blocked', failed: 'Failed', cancelled: 'Cancelled', interrupted: 'Interrupted' };
const phaseLabels: Record<ResultPhase, string> = { setup: 'Setup', analysis: 'Analysis', implementation: 'Implementation', validation: 'Validation', reporting: 'Reporting' };
const reviewModeLabels: Record<ReviewMode, string> = { static: 'Static code review', 'build-tests': 'Build and automated tests', 'ui-e2e': 'Runtime verification' };
const scopedReviewChecks: Record<ReviewMode, readonly string[]> = {
  static: ['context', 'local-review'],
  'build-tests': ['context', 'local-review', 'build-tests'],
  'ui-e2e': ['context', 'local-review', 'setup', 'e2e'],
};
const requiredChecks: Partial<Record<Run['task']['actionKind'], readonly string[]>> = {
  'pr-review': ['context', 'local-review', 'verification'],
  'issue-fix': ['reproduction', 'implementation', 'verification'],
  e2e: ['setup', 'e2e'],
  'reproduction-setup': ['reproduction', 'instructions'],
};
const actionKinds: readonly ResultActionKind[] = ['viewChanges', 'inspectResult', 'openTarget', 'configure', 'rerun', 'approve', 'suggestChanges', 'requestChanges', 'comment', 'close', 'create-pr', 'merge-pr', 'trigger-ci', 'start-task', 'close-as-duplicate', 'none'];
const publicationKinds: readonly string[] = ['approve', 'suggestChanges', 'requestChanges', 'comment', 'close', 'create-pr', 'merge-pr', 'trigger-ci'];
const manualPrKinds: readonly ResultActionKind[] = ['comment', 'approve', 'suggestChanges', 'requestChanges', 'close', 'merge-pr', 'trigger-ci'];
const diagnosticRecoveries: readonly ResultDiagnostic['recovery'][] = ['configure', 'rerun', 'inspectResult', 'openTarget', 'none'];

/** Historical tasks have no recorded mode; defaults belong to task creation, not result interpretation. */
export function reviewModeLabel(mode?: ReviewMode | null): string { return typeof mode === 'string' && Object.hasOwn(reviewModeLabels, mode) ? reviewModeLabels[mode] : 'Not recorded'; }

function recordedReviewMode(run: Run): ReviewMode | undefined {
  const mode = run.task.reviewOptions?.mode;
  return typeof mode === 'string' && Object.hasOwn(reviewModeLabels, mode) ? mode : undefined;
}

function isScopedReview(run: Run): boolean { return run.task.actionKind === 'pr-review' && Boolean(recordedReviewMode(run)); }

function expectedWorkflowChecks(run: Run): readonly string[] | undefined {
  const mode = recordedReviewMode(run);
  if (run.task.actionKind === 'pr-verify') return mode === 'build-tests' ? ['setup', 'build-tests'] : mode === 'ui-e2e' ? ['setup', 'e2e'] : undefined;
  if (run.task.actionKind === 'pr-review' && Object.hasOwn(run.task, 'reviewOptions')) return mode ? scopedReviewChecks[mode] : undefined;
  return requiredChecks[run.task.actionKind];
}

function taskRevisionMatches(run: Run, revision: unknown): boolean {
  return run.task.target?.type === 'pr' && typeof revision === 'string' && /^[a-f0-9]{40}$/i.test(revision) &&
    /^[a-f0-9]{40}$/i.test(run.task.expectedHeadSha ?? '') && revision.toLowerCase() === run.task.expectedHeadSha?.toLowerCase();
}

function exactFields(value: object, fields: readonly string[]): boolean {
  return Object.keys(value).length === fields.length && fields.every(field => Object.hasOwn(value, field));
}

function boundedReviewText(value: unknown): value is string {
  return typeof value === 'string' && value.length <= 4096 && !value.includes('\0') && Boolean(value.trim());
}

function boundedText(value: unknown, maximum: number, nonempty = false): value is string {
  return typeof value === 'string' && value.length <= maximum && !value.includes('\0') && (!nonempty || Boolean(value.trim()));
}

function reportTextList(value: unknown, nonempty = false): value is string[] {
  return Array.isArray(value) && (!nonempty || value.length > 0) && value.every(boundedReviewText);
}

function identifier(value: unknown): value is string { return typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(value); }

function validV3Finding(value: ResultFinding | ResultFindingV3): value is ResultFindingV3 {
  if (!value || !exactFields(value, ['id', 'title', 'priority', 'status', 'confirmed', 'path', 'line', 'details', 'impact', 'trigger', 'rootCause', 'fixSuggestion', 'evidence', 'feedback'])) return false;
  const row = value as ResultFindingV3;
  return identifier(row.id) && boundedText(row.title, 512, true) && ['P0', 'P1', 'P2', 'P3'].includes(row.priority) &&
    ['open', 'fixed', 'unverified'].includes(row.status) && typeof row.confirmed === 'boolean' && !(row.confirmed && row.status === 'unverified') &&
    boundedText(row.path, 4096) && (row.line === null || Number.isSafeInteger(row.line) && row.line > 0 && row.line <= 2147483647) &&
    boundedText(row.details, 8192, true) && boundedText(row.impact, 4096, true) && boundedText(row.trigger, 4096, true) &&
    boundedText(row.rootCause, 8192, true) && boundedText(row.fixSuggestion, 8192, true) && reportTextList(row.evidence, row.confirmed) &&
    Boolean(row.feedback && exactFields(row.feedback, ['body', 'suggestionId']) && boundedText(row.feedback.body, 32768) && (row.feedback.suggestionId === null || identifier(row.feedback.suggestionId)));
}

function v3ReportFindings(run: Run): ResultFindingV3[] | undefined {
  const result = run.result; const report = result?.report;
  if (result?.schemaVersion !== 3 || !report || !exactFields(report, ['complete', 'rechecked', 'coverage', 'limitations']) ||
    typeof report.complete !== 'boolean' || typeof report.rechecked !== 'boolean' || !reportTextList(report.coverage) || !reportTextList(report.limitations) ||
    !Array.isArray(result.findings) || !result.findings.every(validV3Finding) || new Set(result.findings.map(row => row.id)).size !== result.findings.length) return undefined;
  return result.findings;
}

/** A complete report is shown only after every separately transported result section has been read. */
export function isFinalReportComplete(run: Run): boolean {
  if (run.status.state !== 'succeeded' || runFailure(run) || run.result?.schemaVersion !== 3 || run.result.structured !== true ||
    run.result.outcome !== 'completed' || run.result.report?.complete !== true || run.result.report.rechecked !== true ||
    !reportTextList(run.result.report.coverage, true) || !v3ReportFindings(run)) return false;
  for (const section of run.resultPaging?.sections ?? []) {
    if (typeof section.path !== 'string') return false;
    let values: unknown = run.result;
    for (const key of section.path.split('.')) {
      if (!values || typeof values !== 'object' || Array.isArray(values) || !Object.hasOwn(values, key)) { values = undefined; break; }
      values = (values as Record<string, unknown>)[key];
    }
    if (section.nextOffset !== null || !Array.isArray(values) || values.length !== section.total) return false;
  }
  return true;
}

/** Final findings retain their original ordering and identities; no Top-N or transport-page slicing occurs here. */
export function completedV3Findings(run: Run): ResultFindingV3[] {
  return isFinalReportComplete(run) ? v3ReportFindings(run)!.filter(row => row.confirmed) : [];
}

/** Partial retained evidence can establish P0; missing findings never establish that no P0 exists. */
export function hasConfirmedP0(run: Run): boolean {
  const rows = v3ReportFindings(run);
  if (!rows || run.task.target?.type !== 'pr') return false;
  if (run.task.actionKind === 'e2e' || run.task.actionKind === 'pr-verify') {
    const source = run.provenance;
    if (source?.version !== 1 || source.source !== 'host' || source.subject !== 'original-pr' || !taskRevisionMatches(run, source.expectedHeadSha) ||
      !taskRevisionMatches(run, source.start?.headSha) || !taskRevisionMatches(run, source.end?.headSha) || source.start?.workingTree !== 'clean' || source.end?.workingTree !== 'clean') return false;
  }
  const assessment = run.result?.assessment;
  const bound = assessment?.subject === 'original-pr' && taskRevisionMatches(run, assessment.revisionSha) ||
    run.task.actionKind === 'pr-review' && taskRevisionMatches(run, run.result?.reviewConclusion?.revisionSha);
  return Boolean(bound && (run.result?.relatedConfirmedP0 === true || rows.some(row => row.confirmed && row.priority === 'P0' && row.status === 'open')));
}

/** These are fixed editor choices, independent of analysis progress, AI drafts and old report severity labels. */
export function manualOperationKinds(run: Run): OperationKind[] { return run.task.target ? operationKinds(run.task.target.type) : []; }

export function manualOperationAvailability(run: Run, kind: ResultActionKind): ActionAvailability {
  const target = run.task.target;
  if (!target || !(target.type === 'pr' ? manualPrKinds.includes(kind) : ['comment', 'close'].includes(kind))) return { enabled: false, reasons: ['This operation is not available for the task target.'] };
  if (kind === 'approve' && hasConfirmedP0(run)) return { enabled: false, reasons: ['Resolve the confirmed, still-open P0 on this original PR revision before approving.'] };
  return { enabled: true, reasons: [] };
}

export function eligibleManualOperationKinds(run: Run, preview: OperationPreview | undefined): OperationKind[] {
  const target = run.task.target;
  if (!preview || !target || target.type !== preview.target.type || target.number !== preview.target.number || run.task.repository.toLowerCase() !== preview.target.repository.toLowerCase()) return [];
  if (target.type === 'pr' && run.task.expectedHeadSha && (!taskRevisionMatches(run, preview.expectedHeadSha) || !taskRevisionMatches(run, preview.headSha))) return [];
  return manualOperationKinds(run).filter(kind => (kind === 'approve' && typeof preview.hasConfirmedP0 === 'boolean' ? !preview.hasConfirmedP0 : manualOperationAvailability(run, kind).enabled) && allowedOperation(preview, kind) &&
    (!['approve', 'requestChanges', 'suggestChanges'].includes(kind) || taskRevisionMatches(run, preview.expectedHeadSha) && taskRevisionMatches(run, preview.headSha)));
}

/** Advice uses the final report and canonical Issue recommendation; it never grants or removes manual permissions. */
export function currentRecommendation(run: Run): ResultRecommendation | undefined {
  if (run.result?.schemaVersion !== 3) return undefined;
  if (!isFinalReportComplete(run)) return { kind: 'incomplete', reason: 'The final analysis is incomplete or was not recorded; inspect the retained evidence and limitations.' };
  const projected = run.result.recommendation;
  if (projected && textValue(projected.reason) && (run.task.target?.type === 'pr' ? ['approve', 'address-findings', 'run-e2e', 'incomplete'].includes(projected.kind) : resultActions(run.result).some(action => action.proposalId === projected.proposalId && action.kind === projected.kind))) return { ...projected };
  if (run.task.target?.type === 'pr') {
    if (run.result.relatedConfirmedP0 === true || run.result.relatedConfirmedP1 === true || completedV3Findings(run).some(row => row.status === 'open' && ['P0', 'P1'].includes(row.priority))) return { kind: 'address-findings', reason: 'Confirmed P0 or P1 findings remain on this revision. Review and address the findings before following a positive recommendation.' };
    const e2e = run.result.e2eAssessment;
    if (!e2e || !['not_needed', 'recommended', 'required'].includes(e2e.level)) return { kind: 'incomplete', reason: 'This report did not record the need for E2E verification; missing data does not mean it is unnecessary.' };
    if (e2e.level === 'required' && run.result.e2eEvidenceComplete !== true) return { kind: 'run-e2e', reason: 'Required E2E evidence remains to be supplemented; see the recorded scenarios and prerequisites.' };
    return { kind: 'approve', reason: e2e.level === 'required' ? 'The required E2E evidence is complete and no confirmed open P0 or P1 remains. Inspect the report before choosing a GitHub action.' : 'The final review has no confirmed open P0 or P1 findings, and E2E is not required. Inspect the report before choosing a GitHub action.' };
  }
  const action = resultActions(run.result).find(row => row?.recommended === true && actionKinds.includes(row.kind));
  return action ? { kind: action.kind, reason: action.reason, ...(action.proposalId ? { proposalId: action.proposalId } : {}), ...(action.planId ? { planId: action.planId } : {}) }
    : { kind: 'incomplete', reason: 'No default next action was proposed; inspect the final investigation and available actions.' };
}

/** A code-review conclusion can be useful even when the separately selected verification was blocked. */
export function scopedReviewConclusion(run: Run): ReviewConclusion | undefined {
  const conclusion = run.result?.reviewConclusion;
  if (!isScopedReview(run) || run.status.state !== 'succeeded' || run.status.exitCode !== 0 || runFailure(run) ||
    ![2, 3].includes(run.result?.schemaVersion ?? 0) || run.result?.structured !== true || run.result.schemaVersion === 3 && !isFinalReportComplete(run) || !conclusion ||
    !exactFields(conclusion, ['status', 'summary', 'revisionSha', 'blockingUncertainties']) ||
    !['no-blocking-findings', 'changes-requested', 'inconclusive'].includes(conclusion.status) || !boundedReviewText(conclusion.summary) ||
    !taskRevisionMatches(run, conclusion.revisionSha) || !Array.isArray(conclusion.blockingUncertainties) ||
    conclusion.blockingUncertainties.length > 50 || !conclusion.blockingUncertainties.every(boundedReviewText)) return undefined;
  return conclusion;
}

/** Only evidence about the immutable original PR revision belongs in its verification record. */
export function compatibleVerificationEvidence(run: Run, rows: readonly VerificationEvidence[] | undefined = run.result?.verificationEvidence): VerificationEvidence[] {
  if (isActive(run.status.state) || !Array.isArray(rows)) return [];
  return rows.filter((row: VerificationEvidence) => row && typeof row === 'object' &&
    exactFields(row, ['id', 'source', 'kind', 'status', 'subject', 'revisionSha', 'summary', 'evidence', 'runId']) &&
    row.subject === 'original-pr' && taskRevisionMatches(run, row.revisionSha) && typeof row.id === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(row.id) &&
    ['current-run', 'ci', 'author', 'prior-run'].includes(row.source) && ['build', 'automated-tests', 'runtime'].includes(row.kind) &&
    ['passed', 'failed', 'not_run'].includes(row.status) && boundedReviewText(row.summary) && Array.isArray(row.evidence) && row.evidence.length <= 50 &&
    row.evidence.every(boundedReviewText) && (row.status === 'not_run' || row.evidence.length > 0) &&
    (row.runId === null || row.source === 'prior-run' && typeof row.runId === 'string' && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(row.runId)));
}

export function workflowOutcome(run: Run): ResultOutcome | undefined {
  if (isActive(run.status.state)) return undefined;
  const result = run.result;
  const recorded = result && (result.schemaVersion === 1 || result.schemaVersion === 2 || result.schemaVersion === 3) && result.outcome && Object.hasOwn(outcomeLabels, result.outcome) ? result.outcome : undefined;
  if (run.status.state === 'failed' || run.status.state === 'cancelled' || run.status.state === 'interrupted') return run.status.state;
  return recorded ?? (result?.schemaVersion === 2 || result?.schemaVersion === 3 ? 'blocked' : 'completed');
}

export function phaseLabel(phase: ResultPhase | undefined): string { return phase && Object.hasOwn(phaseLabels, phase) ? phaseLabels[phase] : 'Not recorded'; }

/** This Host projection separates completed code review from a saved workflow's validation outcome. */
export function limitedReviewSummary(run: Run): ReviewSummary | undefined {
  const summary = run.reviewSummary;
  const assessment = run.result?.assessment;
  if (!summary || summary.codeReview !== 'completed' || summary.verification !== 'limited' || run.task.actionKind !== 'pr-review' || run.task.target?.type !== 'pr' ||
    run.status.state !== 'succeeded' || run.status.exitCode !== 0 || run.result?.schemaVersion !== 2 || run.result.structured !== true || runFailure(run) ||
    summary.assessmentStatus !== 'inconclusive' || assessment?.status !== 'inconclusive' || assessment.subject !== 'original-pr' ||
    !/^[a-f0-9]{40}$/i.test(summary.headSha) || summary.headSha.toLowerCase() !== run.task.expectedHeadSha?.toLowerCase() || assessment.revisionSha?.toLowerCase() !== summary.headSha.toLowerCase()) return undefined;
  return summary;
}

export function outcomeLabel(run: Run): string {
  const outcome = workflowOutcome(run);
  if (run.result?.schemaVersion === 3 && run.status.state === 'succeeded' && !isFinalReportComplete(run)) return run.task.actionKind === 'pr-review' ? 'Review incomplete' : 'Analysis incomplete';
  if (outcome === 'completed' && (run.result?.schemaVersion === 2 || run.result?.schemaVersion === 3)) {
    const count = unresolvedFindingCount(run);
    if (count) return `Completed · ${count} ${count === 1 ? 'issue' : 'issues'}`;
    if (resultNeedsAttention(run)) return 'Completed · Needs attention';
  }
  return outcome ? outcomeLabels[outcome] : run.status.state === 'accepted' ? 'Queued' : 'Running';
}

export function unresolvedFindingCount(run: Run): number {
  if (run.result?.schemaVersion === 3) return completedV3Findings(run).filter(row => row.status === 'open').length;
  const counts = run.result?.findingSummary;
  return counts ? Math.max(0, counts.unresolved || 0) : run.result?.findings?.filter(item => item.status === 'open' || item.status === 'unverified').length ?? 0;
}

export function resultNeedsAttention(run: Run): boolean {
  const result = run.result;
  if (result?.schemaVersion === 3) return run.status.state === 'succeeded' && (!isFinalReportComplete(run) || result.needsReview === true || unresolvedFindingCount(run) > 0 || currentRecommendation(run)?.kind !== 'approve');
  const conclusion = scopedReviewConclusion(run);
  const assessmentInconclusive = !isScopedReview(run) && result?.assessment?.status === 'inconclusive';
  return run.status.state === 'succeeded' && Boolean(result && (
    workflowOutcome(run) !== 'completed' || result.needsReview === true || result.structured === false || result.assessment?.status === 'failed' || assessmentInconclusive || unresolvedFindingCount(run) > 0 ||
    isScopedReview(run) && (!conclusion || conclusion.status !== 'no-blocking-findings' || conclusion.blockingUncertainties.length > 0 || compatibleVerificationEvidence(run).some(row => row.status === 'failed')) ||
    (result.schemaVersion === 2
      ? result.diagnostics?.some(item => item?.severity === 'error') || result.validation?.some(item => item?.required === true && item.status !== 'passed') || result.findings?.some(item => item?.status === 'open' || item?.status === 'unverified')
      : result.blockers?.some(item => Boolean(textValue(item))) || result.validation?.some(item => textValue(item?.status)?.toLowerCase() === 'failed'))
  ));
}

function publishableResult(run: Run): boolean {
  const result = run.result;
  const target = run.task.target;
  if (run.status.state !== 'succeeded' || workflowOutcome(run) !== 'completed' || !target || !result || runFailure(run) || result.structured === false) return false;
  const v2 = result.schemaVersion === 2;
  if (v2) {
    if (result.structured !== true) return false;
    const expected = expectedWorkflowChecks(run);
    const scoped = isScopedReview(run) || run.task.actionKind === 'pr-verify';
    const passed = (item: Result['validation'][number]): boolean => item.status === 'passed' && (!scoped || Boolean(textValue(item.details) && Array.isArray(item.evidence) && item.evidence.length && item.evidence.every(value => textValue(value))));
    if (!expected || !Array.isArray(result.validation) || !Array.isArray(result.diagnostics) || result.diagnostics.some(item => !item || item.severity !== 'warning') ||
      result.validation.some(item => !item || !textValue(item.id) || typeof item.required !== 'boolean' || !['passed', 'failed', 'not_run'].includes(item.status) || item.required && !passed(item)) ||
      new Set(result.validation.map(item => item.id)).size !== result.validation.length ||
      expected.some(id => !result.validation.some(item => item.id === id && item.required === true && passed(item))) ||
      run.status.exitCode !== 0 || typeof result.cliExitCode === 'number' && result.cliExitCode !== 0) return false;
  } else if (textValue(result.rawOutput) || !Array.isArray(result.blockers) || result.blockers.some(item => Boolean(textValue(item))) ||
    !Array.isArray(result.validation) || result.validation.some(item => textValue(item?.status)?.toLowerCase() === 'failed')) return false;
  const review = result.review;
  if (target.type === 'pr' && !/^[a-f0-9]{40}$/i.test(run.task.expectedHeadSha ?? '')) return false;
  const boundReview = review && /^[a-f0-9]{40}$/i.test(review.headSha) && review.headSha.toLowerCase() === run.task.expectedHeadSha?.toLowerCase();
  return !(v2 && target.type === 'pr' && review && !boundReview);
}

function resultActions(result: Result | undefined): ResultNextAction[] {
  const actions = result?.schemaVersion === 2 || result?.schemaVersion === 3 ? result.nextActions : result?.nextSteps;
  return Array.isArray(actions) ? actions as ResultNextAction[] : [];
}

function exactText(value: unknown): string { return typeof value === 'string' ? value : ''; }

function validSuggestion(suggestion: Suggestion): boolean {
  if (!suggestion || typeof suggestion.path !== 'string' || !suggestion.path.trim() || suggestion.path.length > 1024 || suggestion.path.startsWith('/') || suggestion.path.includes('\\') ||
    suggestion.path.split('/').some(segment => segment === '' || segment === '.' || segment === '..') || /[\u0000-\u001f\u007f-\u009f]/.test(suggestion.path) || suggestion.side !== 'RIGHT') return false;
  const first = suggestion.startLine ?? suggestion.line;
  return Number.isSafeInteger(first) && Number.isSafeInteger(suggestion.line) && first >= 1 && suggestion.line <= 2147483647 && first <= suggestion.line && suggestion.line - first <= 999 &&
    typeof suggestion.body === 'string' && suggestion.body.length <= 10000 && !suggestion.body.includes('\0') && typeof suggestion.replacement === 'string' && suggestion.replacement.length <= 60000 && !suggestion.replacement.includes('\0') && !suggestion.replacement.includes('```');
}

function verifiedReview(run: Run): NonNullable<Result['review']> | undefined {
  const review = run.result?.review;
  if (!review || run.task.target?.type !== 'pr' || !/^[a-f0-9]{40}$/i.test(review.headSha) || review.headSha.toLowerCase() !== run.task.expectedHeadSha?.toLowerCase() ||
    typeof review.body !== 'string' || !Array.isArray(review.suggestions) || review.suggestions.length > 100 || !review.suggestions.every(validSuggestion)) return undefined;
  return review;
}

function matchesProposal(left: ResultNextAction, right: ResultNextAction): boolean {
  if (left.kind !== right.kind || exactText(left.body) !== exactText(right.body)) return false;
  if (['create-pr', 'merge-pr', 'trigger-ci'].includes(left.kind) && typeof left.body !== typeof right.body) return false;
  if (left.proposalId !== right.proposalId) return false;
  if (left.taskKind !== right.taskKind || left.planId !== right.planId) return false;
  if (left.duplicateOf !== right.duplicateOf && (!left.duplicateOf || !right.duplicateOf || left.duplicateOf.repository !== right.duplicateOf.repository || left.duplicateOf.number !== right.duplicateOf.number || left.duplicateOf.url !== right.duplicateOf.url)) return false;
  const leftPr = left.pullRequest; const rightPr = right.pullRequest;
  if (JSON.stringify(left.suggestionIds) !== JSON.stringify(right.suggestionIds)) return false;
  return leftPr === rightPr || Boolean(leftPr && rightPr && ['head', 'base', 'title', 'body', 'draft', 'sourceHeadSha'].every(key => leftPr[key as keyof typeof leftPr] === rightPr[key as keyof typeof rightPr]));
}

function scopedApprovalFailure(run: Run, kind: string, hostVerifiedAssessment = false): string | undefined {
  const conclusion = scopedReviewConclusion(run);
  if (!conclusion || conclusion.status !== 'no-blocking-findings' || conclusion.blockingUncertainties.length) return 'The code review must conclude that this PR revision has no blocking findings or material unresolved questions.';
  if (run.result?.findings?.some(item => ['high', 'medium'].includes(item.severity ?? '') && ['open', 'unverified'].includes(item.status))) return 'Resolve the open high or medium findings on the reviewed PR revision before approving or merging.';
  const assessment = run.result?.assessment;
  if (assessment && (!['passed', 'inconclusive'].includes(assessment.status) || assessment.subject !== 'original-pr' || !taskRevisionMatches(run, assessment.revisionSha))) return 'A failed or different-revision product assessment cannot support approval of this PR.';
  // Keep recorded failures blocking even if another malformed field prevents displaying the row as trusted evidence.
  if (Array.isArray(run.result?.verificationEvidence) && run.result.verificationEvidence.some(row => row?.status === 'failed' && row.subject === 'original-pr' && taskRevisionMatches(run, row.revisionSha))) return 'Resolve the recorded failing verification of this PR revision before approving or merging.';
  if (kind === 'merge-pr' && assessment && assessment.status !== 'passed' && !hostVerifiedAssessment) return 'Merge requires a passing assessment of the original pull request revision.';
  return undefined;
}

function assessmentAllowsApproval(run: Run, kind: string): boolean {
  if (isScopedReview(run)) return scopedApprovalFailure(run, kind) === undefined;
  const assessment = run.result?.assessment;
  return !assessment || assessment.status === 'passed' && assessment.subject === 'original-pr' &&
    /^[a-f0-9]{40}$/i.test(assessment.revisionSha ?? '') && assessment.revisionSha?.toLowerCase() === run.task.expectedHeadSha?.toLowerCase();
}

function legacyActionEligible(run: Run, action: ResultNextAction): boolean {
  if (!action || isActive(run.status.state) || !actionKinds.includes(action.kind) || action.kind === 'none') return false;
  if (!publicationKinds.includes(action.kind)) return action.kind !== 'openTarget' || Boolean(run.task.target);
  if (!publishableResult(run) || !resultActions(run.result).some(proposal => proposal && matchesProposal(proposal, action))) return false;
  const result = run.result!;
  const kind = action.kind;
  if (['approve', 'merge-pr'].includes(kind) && !assessmentAllowsApproval(run, kind)) return false;
  if (['create-pr', 'merge-pr', 'trigger-ci'].includes(kind)) {
    if (result.schemaVersion !== 2 || !textValue(action.proposalId) || typeof action.body !== 'string') return false;
    if (kind === 'trigger-ci') return run.task.target?.type === 'pr' && (action.body === '' || action.body === '/azp run');
    if (action.body !== '') return false;
    if (kind === 'merge-pr') return run.task.target?.type === 'pr' && !result.findings?.some(item => ['high', 'medium'].includes(item.severity ?? '') && ['open', 'unverified'].includes(item.status));
    const pr = action.pullRequest;
    return run.task.target?.type === 'issue' && Boolean(pr && textValue(pr.head) && textValue(pr.base) && textValue(pr.title) && typeof pr.body === 'string' && typeof pr.draft === 'boolean');
  }
  if (!operationKinds(run.task.target!.type).includes(kind as OperationKind)) return false;
  const review = verifiedReview(run);
  if (['approve', 'requestChanges', 'suggestChanges'].includes(kind) && !review) return false;
  if (kind === 'suggestChanges') return Boolean(review?.suggestions.some(item => !action.suggestionIds || item.id && action.suggestionIds.includes(item.id)));
  if (kind === 'comment' || kind === 'requestChanges') return Boolean(textValue(action.body) || textValue(review?.body) || result.schemaVersion !== 2 && textValue(result.review?.body));
  if (kind === 'approve' && !textValue(action.body) && !textValue(review?.body) && !review?.suggestions.length) return false;
  if (kind === 'approve' && result.findings?.some(item => ['high', 'medium'].includes(item.severity ?? '') && ['open', 'unverified'].includes(item.status))) return false;
  // Historical records did not distinguish optional checks from required workflow checks.
  return result.schemaVersion === 2 || !result.validation.some(item => textValue(item?.status)?.toLowerCase() === 'not_run');
}

function v3ProposalAvailability(run: Run, action: ResultNextAction): ActionAvailability {
  const refuse = (reason: string): ActionAvailability => ({ enabled: false, reasons: [reason] });
  const saved = resultActions(run.result).find(proposal => proposal && matchesProposal(proposal, action));
  const executable = publicationKinds.includes(action.kind) || ['start-task', 'close-as-duplicate'].includes(action.kind);
  if (executable && !saved) return refuse('The proposal no longer matches the saved result. Refresh this task.');
  if (run.task.target?.type === 'pr' && manualPrKinds.includes(action.kind)) {
    const manual = manualOperationAvailability(run, action.kind);
    if (!manual.enabled) return manual;
  } else if (isActive(run.status.state)) return refuse('Wait for this run to finish before starting its proposed follow-up.');
  const host = saved?.availability;
  if (host?.enabled === false && Array.isArray(host.reasons)) return { enabled: false, reasons: host.reasons.filter(reason => textValue(reason)) };
  if (run.task.target?.type === 'pr' && manualPrKinds.includes(action.kind)) return manualOperationAvailability(run, action.kind);
  if (action.kind === 'comment' || action.kind === 'close') return manualOperationAvailability(run, action.kind);
  if (action.kind === 'start-task') {
    const plan = run.result?.plans?.find(item => item.id === action.planId);
    const valid = run.task.target?.type === 'issue' && v3ReportFindings(run) && identifier(action.proposalId) && action.body === '' && plan &&
      plan.kind === action.taskKind && ['feature-implement', 'issue-fix', 'reproduction-setup', 'issue-verify'].includes(plan.kind) && identifier(plan.id) &&
      boundedText(plan.summary, 8192, true) && reportTextList(plan.steps, true) && reportTextList(plan.acceptanceCriteria, true) && reportTextList(plan.prerequisites) && reportTextList(plan.evidence, true);
    return valid ? { enabled: true, reasons: [] } : refuse('This follow-up requires the saved Issue plan and its original task kind.');
  }
  if (action.kind === 'close-as-duplicate') {
    const duplicate = action.duplicateOf;
    if (run.task.target?.type !== 'issue' || !v3ReportFindings(run) || !identifier(action.proposalId) || !duplicate || !boundedText(action.body, 32768, true) ||
      !/^[A-Za-z0-9][A-Za-z0-9-]{0,38}\/[A-Za-z0-9_.-]{1,100}$/.test(duplicate.repository) || !Number.isSafeInteger(duplicate.number) || duplicate.number < 1 || duplicate.number > 2147483647 ||
      duplicate.repository.toLowerCase() === run.task.repository.toLowerCase() && duplicate.number === run.task.target.number) return refuse('Choose the saved original Issue and review the duplicate explanation.');
    try {
      const url = new URL(duplicate.url);
      if (url.protocol !== 'https:' || url.hostname !== 'github.com' || url.port || url.username || url.password || url.search || url.hash || url.pathname.toLowerCase() !== `/${duplicate.repository}/issues/${duplicate.number}`.toLowerCase()) return refuse('The duplicate reference must point to its saved original GitHub Issue.');
    } catch { return refuse('The duplicate reference must point to its saved original GitHub Issue.'); }
    return { enabled: true, reasons: [] };
  }
  if (action.kind === 'create-pr') {
    const pr = action.pullRequest; const assessment = run.result?.assessment;
    const valid = isFinalReportComplete(run) && run.task.target?.type === 'issue' && identifier(action.proposalId) && action.body === '' && pr &&
      boundedText(pr.head, 240, true) && /^[A-Za-z0-9][A-Za-z0-9-]{0,38}:[^\s\0]+$/.test(pr.head) && boundedText(pr.base, 200, true) && boundedText(pr.title, 256, true) && boundedText(pr.body, 60000) && typeof pr.draft === 'boolean' &&
      /^[a-f0-9]{40}$/i.test(pr.sourceHeadSha ?? '') && assessment?.subject === 'local-candidate' && assessment.status === 'passed' && pr.sourceHeadSha?.toLowerCase() === assessment.revisionSha?.toLowerCase();
    return valid ? { enabled: true, reasons: [] } : refuse('Create PR requires a completed Issue report and the verified source branch revision.');
  }
  if (!publicationKinds.includes(action.kind)) return { enabled: action.kind !== 'openTarget' || Boolean(run.task.target), reasons: action.kind === 'openTarget' && !run.task.target ? ['This task has no GitHub target.'] : [] };
  return refuse('This proposal is not available for the task target.');
}

/** The Host owns proposal eligibility; local guards protect lifecycle/identity and older Hosts. */
export function resultActionAvailability(run: Run, action: ResultNextAction): ActionAvailability {
  if (!action || !actionKinds.includes(action.kind) || action.kind === 'none') return { enabled: false, reasons: ['This action is not supported.'] };
  if (run.result?.schemaVersion === 3) return v3ProposalAvailability(run, action);
  if (isActive(run.status.state)) return { enabled: false, reasons: ['Wait for this run to finish.'] };
  if (!publicationKinds.includes(action.kind)) return { enabled: legacyActionEligible(run, action), reasons: action.kind === 'openTarget' && !run.task.target ? ['This task has no GitHub target.'] : [] };
  const savedProposal = resultActions(run.result).find(proposal => proposal && matchesProposal(proposal, action));
  const host = savedProposal?.availability;
  const reasons = host?.enabled === false && Array.isArray(host.reasons) ? host.reasons.filter(reason => textValue(reason)) : [];
  const refuse = (reason: string): ActionAvailability => ({ enabled: false, reasons: [...new Set([...reasons, reason])] });
  if (!savedProposal) return refuse('The proposal no longer matches the saved result. Refresh this task.');
  if (!run.task.target) return refuse('This task has no GitHub target.');
  if (run.status.state !== 'succeeded') return refuse('The CLI did not finish successfully. Inspect its diagnostics before publishing.');
  if (run.result?.schemaVersion === 2 && run.status.exitCode !== 0) return refuse('A successful CLI exit has not been recorded. Refresh the task or inspect its logs.');
  if (run.result?.structured === false || run.result?.schemaVersion === 2 && run.result.structured !== true) return refuse('The saved output is not a verified structured result.');
  if (isScopedReview(run) && ['approve', 'merge-pr'].includes(action.kind)) {
    if (!publishableResult(run) || !verifiedReview(run)) return refuse('Complete the selected review scope with recorded check evidence and a review draft matching this PR revision before approving or merging.');
    // Host availability includes compatible supplemental evidence; the original assessment remains historical.
    const failure = scopedApprovalFailure(run, action.kind, host?.enabled === true && Array.isArray(host.reasons)); if (failure) return refuse(failure);
  }
  if (host && typeof host.enabled === 'boolean' && Array.isArray(host.reasons)) return { enabled: host.enabled, reasons: host.enabled ? [] : reasons.length ? reasons : ['The Host cannot authorize this proposal. Inspect the result and current configuration.'] };
  if (legacyActionEligible(run, action)) return { enabled: true, reasons: [] };
  if (workflowOutcome(run) !== 'completed') return refuse('This workflow is incomplete. Update the Host to check which reporting actions are supported.');
  if (['approve', 'merge-pr'].includes(action.kind) && run.result?.findings?.some(item => ['high', 'medium'].includes(item.severity ?? '') && ['open', 'unverified'].includes(item.status))) return refuse('Resolve the open high or medium findings on the reviewed PR revision before approving or merging.');
  if (['approve', 'merge-pr'].includes(action.kind) && !assessmentAllowsApproval(run, action.kind)) return refuse('Approval and merge require a passing assessment of the original pull request revision.');
  return refuse('The saved result does not meet this action’s requirements. Check its target, revision, required checks, and proposed content.');
}

export function resultActionEligible(run: Run, action: ResultNextAction): boolean { return resultActionAvailability(run, action).enabled; }

export function proposedOperationKinds(run: Run): OperationKind[] {
  return run.task.target && !isActive(run.status.state) ? operationKinds(run.task.target.type).filter(kind => recoveryActions(run).some(action => action.kind === kind)) : [];
}

/** Keep the existing operation editor's five-kind allowlist; newer proposals have their own confirmation page. */
export function resultOperationKinds(run: Run): OperationKind[] {
  const target = run.task.target;
  return target ? operationKinds(target.type).filter(kind => resultActions(run.result).some(action => action?.kind === kind && resultActionEligible(run, action))) : [];
}

/** These fixed kinds choose extension handlers; response text never becomes an executable target or command. */
export function recoveryActions(run: Run): ResultNextAction[] {
  if (isActive(run.status.state)) return [];
  const actions: ResultNextAction[] = [];
  for (const step of resultActions(run.result)) {
    if (!step || !actionKinds.includes(step.kind) || step.kind === 'none' || run.result?.schemaVersion !== 3 && (!publicationKinds.includes(step.kind) || run.result?.schemaVersion !== 2) && !resultActionEligible(run, step)) continue;
    actions.push({ kind: step.kind, reason: exactText(step.reason), body: exactText(step.body),
      ...(typeof step.proposalId === 'string' ? { proposalId: step.proposalId } : {}),
      ...(typeof step.recommended === 'boolean' ? { recommended: step.recommended } : {}),
      ...(step.taskKind ? { taskKind: step.taskKind } : {}), ...(step.planId ? { planId: step.planId } : {}), ...(step.duplicateOf ? { duplicateOf: { ...step.duplicateOf } } : {}),
      ...(step.pullRequest ? { pullRequest: { ...step.pullRequest } } : {}),
      ...(step.suggestionIds ? { suggestionIds: [...step.suggestionIds] } : {}),
      ...(step.availability ? { availability: { enabled: step.availability.enabled, reasons: [...step.availability.reasons] } } : {}),
    });
  }
  for (const diagnostic of resultDiagnostics(run)) {
    const action: ResultNextAction = { kind: diagnostic.recovery, reason: diagnostic.message, body: '' };
    if (!actions.some(existing => existing.kind === action.kind) && resultActionEligible(run, action)) actions.push(action);
  }
  return actions;
}

export function eligibleOperationKinds(run: Run, preview: OperationPreview | undefined): OperationKind[] {
  const target = run.task.target;
  if (!preview || !target || target.type !== preview.target.type || target.number !== preview.target.number ||
    run.task.repository.toLowerCase() !== preview.target.repository.toLowerCase()) return [];
  if (target.type === 'pr' && [preview.expectedHeadSha, preview.headSha].some(sha => !/^[a-f0-9]{40}$/i.test(sha ?? '') || sha?.toLowerCase() !== run.task.expectedHeadSha?.toLowerCase())) return [];
  return resultOperationKinds(run).filter(kind => allowedOperation(preview, kind) &&
    (!['approve', 'requestChanges', 'suggestChanges'].includes(kind) || matchingReview(run.result, run.task.expectedHeadSha, preview)) &&
    (kind !== 'suggestChanges' || run.result?.review?.suggestions.some(suggestion => suggestionOriginal(preview, suggestion) !== undefined)));
}

export function runFailure(run: Run): { message: string; guidance?: string; code?: string; exitCode?: number } | undefined {
  const error = run.status.error;
  const message = textValue(error?.message);
  const code = textValue(error?.code);
  const guidance = textValue(error?.guidance);
  if (!message && !code && !guidance && !['failed', 'interrupted'].includes(run.status.state)) return undefined;
  const failure = message ?? (run.status.state === 'interrupted' ? 'The run was interrupted before it completed.' : 'The task stopped before it completed.');
  return {
    message: failure,
    ...(guidance && normalized(guidance) !== normalized(failure) ? { guidance } : {}),
    ...(code ? { code } : {}),
    ...(typeof run.status.exitCode === 'number' && Number.isInteger(run.status.exitCode) ? { exitCode: run.status.exitCode } : {}),
  };
}

function normalized(value: string): string { return value.trim().replace(/\s+/g, ' ').replace(/[.!]+$/, '').toLowerCase(); }

function resultDiagnostics(run: Run): ResultDiagnostic[] {
  if (![2, 3].includes(run.result?.schemaVersion ?? 0) || isActive(run.status.state)) return [];
  const diagnostics = new Map<string, ResultDiagnostic>();
  const add = (diagnostic: ResultDiagnostic): void => {
    const key = normalized(diagnostic.message);
    const existing = diagnostics.get(key);
    if (!existing) diagnostics.set(key, diagnostic);
    else if (existing.severity !== 'error' && diagnostic.severity === 'error') diagnostics.set(key, diagnostic);
    else if (existing.recovery === 'none' && diagnostic.recovery !== 'none') diagnostics.set(key, { ...existing, recovery: diagnostic.recovery });
  };
  for (const item of Array.isArray(run.result?.diagnostics) ? run.result.diagnostics : []) {
    const message = textValue(item?.message);
    if (!message || !['warning', 'error'].includes(item?.severity)) continue;
    add({ code: textValue(item.code) ?? 'RESULT_DIAGNOSTIC', severity: item.severity, message, recovery: diagnosticRecoveries.includes(item.recovery) ? item.recovery : 'none' });
  }
  const failure = runFailure(run);
  const stopped = ['failed', 'cancelled', 'interrupted'].includes(run.status.state);
  if ((failure || stopped) && (run.status.error || ![...diagnostics.values()].some(item => item.severity === 'error') || run.result?.outcome === 'completed')) {
    add({ code: failure?.code ?? `RUN_${run.status.state.toUpperCase()}`, severity: 'error', message: failure?.message ?? 'The run was cancelled before it completed.', recovery: 'inspectResult' });
  }
  if (failure?.guidance) add({ code: 'HOST_GUIDANCE', severity: 'warning', message: failure.guidance, recovery: 'none' });
  return [...diagnostics.values()];
}

export interface PresentedDiagnostic {
  primary: ResultDiagnostic;
  related: ResultDiagnostic[];
}

/** Presentation only: keep the full diagnostic collection authoritative for recovery and saved history. */
function presentedDiagnostics(run: Run, diagnostics: ResultDiagnostic[]): PresentedDiagnostic[] {
  const groups = diagnostics.map(primary => ({ primary, related: [] as ResultDiagnostic[] }));
  const summary = groups.find(group => group.primary.code === 'WORKFLOW_CHECKS_INCOMPLETE' && group.primary.severity === 'error');
  // A process/output failure is an independent cause. Keep every diagnostic at the top level in that case.
  const executionError = diagnostics.some(item => item.severity === 'error' && /^(CLI_|RUN_|HOST_|WORKER_|TASK_|OUTPUT_|INVALID_RESULT$)/.test(item.code));
  if (!summary || run.status.state !== 'succeeded' || run.status.exitCode !== 0 || run.result?.structured !== true || runFailure(run) || executionError) return groups;
  const specific = groups.find(group => group !== summary && group.primary.severity === 'error' && !group.primary.code.startsWith('WORKFLOW_'));
  if (!specific) return groups;
  specific.related.push(summary.primary);
  return groups.filter(group => group !== summary);
}

export interface ResultPresentation {
  mode: 'active' | 'completed' | 'diagnostic'; findings: ResultFinding[]; diagnostics: ResultDiagnostic[]; diagnosticGroups: PresentedDiagnostic[];
  v3Findings?: ResultFindingV3[]; report?: FinalReport; featureAssessment?: FeatureAssessment | null; bugAssessment?: BugAssessment | null; recommendation?: ResultRecommendation;
  reviewConclusion?: ReviewConclusion; verificationEvidence: VerificationEvidence[];
  summary?: string; rawOutput?: string; artifacts: Result['artifacts']; validation: Result['validation'];
  blockers: string[]; nextSteps: ResultNextAction[]; showReviewNotice: boolean; hasContent: boolean;
}

export function resultPresentation(run: Run): ResultPresentation {
  if (isActive(run.status.state)) return { mode: 'active', findings: [], diagnostics: [], diagnosticGroups: [], verificationEvidence: [], artifacts: [], validation: [], blockers: [], nextSteps: [], showReviewNotice: false, hasContent: false };
  const result = run.result;
  const failure = runFailure(run);
  const v2 = result?.schemaVersion === 2;
  const v3 = result?.schemaVersion === 3;
  const incomplete = result?.structured === false;
  const mode = (v3 ? isFinalReportComplete(run) : workflowOutcome(run) === 'completed' && !failure && !incomplete) ? 'completed' : 'diagnostic';
  const reviewConclusion = scopedReviewConclusion(run);
  const verificationEvidence = compatibleVerificationEvidence(run);
  const diagnostics = resultDiagnostics(run);
  const known = new Set((v2 || v3 ? diagnostics.flatMap(item => [item.message, item.code]) : [failure?.message, failure?.guidance, failure?.code]).filter((value): value is string => Boolean(value)).map(normalized));
  const distinct = (value: unknown): string | undefined => { const text = textValue(value); return text && !known.has(normalized(text)) ? text : undefined; };
  let summary = distinct(result?.summary);
  const rawOutput = textValue(result?.rawOutput) ? result!.rawOutput! : undefined;
  const fallbackSummaries = ['The CLI exited. Inspect the result to confirm completion.', 'The task did not complete. Existing file changes will not be rolled back automatically.'];
  const summaryKey = summary ? normalized(summary) : undefined;
  if (summaryKey && (rawOutput && summaryKey === normalized(rawOutput) || fallbackSummaries.some(item => normalized(item) === summaryKey))) summary = undefined;
  if (summary) known.add(normalized(summary));
  const findings = (v2 && Array.isArray(result.findings) ? result.findings : []).filter((item): item is ResultFinding => Boolean(item && ['high', 'medium', 'low'].includes(item.severity ?? '') && (textValue(item.title) || textValue(item.details) || Array.isArray(item.evidence) && item.evidence.some(value => textValue(value)))));
  const v3Findings = v3 ? completedV3Findings(run) : [];
  const artifacts = (Array.isArray(result?.artifacts) ? result.artifacts : []).filter(item => item && (textValue(item.label) || textValue(item.path) || textValue(item.url)));
  const validation = (Array.isArray(result?.validation) ? result.validation : []).filter(item => item && (textValue(item.name) || textValue(item.status) || textValue(item.details) || Array.isArray(item.evidence) && item.evidence.some(value => textValue(value))));
  const blockers = [...new Map((!v2 && !v3 && Array.isArray(result?.blockers) ? result.blockers : []).map(distinct).filter((value): value is string => Boolean(value)).map(value => [normalized(value), value])).values()];
  const nextSteps = recoveryActions(run).map(step => ({ ...step, reason: distinct(step.reason) ?? '' }));
  if ((v2 || v3) && mode === 'diagnostic' && !summary && !diagnostics.length) summary = v3 ? 'The final analysis report is incomplete. Inspect the saved coverage, limitations and execution logs.' : `The workflow ${workflowOutcome(run) === 'blocked' ? 'is blocked' : 'did not complete'}. Inspect the saved output and execution logs.`;
  const showReviewNotice = mode === 'completed' && Boolean(result?.needsReview);
  return { mode, summary, rawOutput, findings, diagnostics, diagnosticGroups: presentedDiagnostics(run, diagnostics), reviewConclusion, verificationEvidence, artifacts, validation, blockers, nextSteps, showReviewNotice,
    ...(v3 ? { v3Findings, report: result.report, recommendation: currentRecommendation(run), ...(mode === 'completed' ? { featureAssessment: result.featureAssessment, bugAssessment: result.bugAssessment } : {}) } : {}),
    hasContent: Boolean(summary || rawOutput || reviewConclusion || verificationEvidence.length || findings.length || v3Findings.length || diagnostics.length || artifacts.length || validation.length || blockers.length || nextSteps.length) };
}
