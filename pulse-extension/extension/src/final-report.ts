import { completedV3Findings, currentRecommendation, isFinalReportComplete, textValue } from './details-model.js';
import { isActive } from './policy.js';
import { element, labeledValue, link } from './ui.js';
import type { BugAssessment, FeatureAssessment, IssueReference, ResultAssessment, ResultFindingV3, ResultPlan, Run } from './types.js';

export interface FinalReportOptions { unified?: boolean }

const featureLabels: Record<FeatureAssessment['status'], string> = {
  ready: 'Ready for implementation', needs_information: 'More information needed', needs_decision: 'Decision needed',
  already_supported: 'Already supported', duplicate: 'Duplicate', not_feasible: 'Not feasible under the recorded constraints',
};
const bugLabels: Record<BugAssessment['status'], string> = {
  confirmed: 'Confirmed bug', needs_information: 'More information needed', needs_verification: 'Verification needed',
  already_fixed: 'Already fixed', duplicate: 'Duplicate', not_a_bug: 'Not a bug',
};
const reproductionLabels: Record<BugAssessment['reproduction']['status'], string> = { reproduced: 'Reproduced', not_reproduced: 'Not reproduced', not_run: 'Not run', blocked: 'Blocked' };
const planLabels: Record<ResultPlan['kind'], string> = { 'feature-implement': 'Feature implementation', 'issue-fix': 'Bug fix', 'reproduction-setup': 'Reproduction setup', 'issue-verify': 'Issue verification' };

function paragraph(parent: HTMLElement, title: string, value?: string | null): void {
  if (!textValue(value)) return;
  const section = element('div', undefined, 'details-check');
  section.append(element('strong', title), element('p', value!, 'prewrap')); parent.append(section);
}

function list(parent: HTMLElement, title: string, values: readonly string[] | undefined, ordered = false): void {
  if (!Array.isArray(values) || !values.some(value => textValue(value))) return;
  const section = element('div', undefined, 'details-check'); section.append(element('strong', title));
  const items = element(ordered ? 'ol' : 'ul', undefined, 'details-evidence'); items.setAttribute('aria-label', title);
  for (const value of values) if (textValue(value)) items.append(element('li', value, 'prewrap'));
  section.append(items); parent.append(section);
}

function reference(parent: HTMLElement, value: IssueReference | null): void {
  if (!value) return;
  const section = element('div', undefined, 'details-check'); section.append(element('strong', 'Related Issue'));
  const valid = /^[A-Za-z0-9][A-Za-z0-9-]{0,38}\/[A-Za-z0-9_.-]{1,100}$/.test(value.repository) && Number.isSafeInteger(value.number) && value.number > 0;
  const reference = element('p'); reference.append(link(`${value.repository} #${value.number}`, valid ? `https://github.com/${value.repository}/issues/${value.number}` : undefined)); section.append(reference);
  parent.append(section);
}

function coverage(parent: HTMLElement, run: Run): void {
  const report = run.result?.report;
  if (!report) return;
  const section = element('section', undefined, 'final-report-coverage'); section.append(element('h3', 'Coverage and limitations'));
  list(section, 'Coverage', report.coverage);
  list(section, 'Remaining limitations', report.limitations);
  if (!report.coverage?.length) section.append(element('p', 'Coverage was not recorded.', 'muted'));
  parent.append(section);
}

function recordedAssessment(parent: HTMLElement, value: ResultAssessment | null | undefined): void {
  if (!value) return;
  const section = element('section', undefined, 'final-report-assessment');
  const subjects = { 'original-pr': 'Original pull request', 'local-candidate': 'Local candidate', target: 'Task target' };
  const statuses = { passed: 'Passed', failed: 'Failed', inconclusive: 'Inconclusive' };
  section.append(element('h3', 'Recorded assessment'));
  const facts = element('dl', undefined, 'facts');
  facts.append(labeledValue('Assessed source', subjects[value.subject] ?? 'Not recorded'), labeledValue('Result', statuses[value.status] ?? 'Not recorded'),
    labeledValue('Assessed revision', textValue(value.revisionSha) ?? 'Not recorded'));
  section.append(facts);
  paragraph(section, 'Assessment summary', value.summary);
  parent.append(section);
}

function featureConclusion(parent: HTMLElement, value: FeatureAssessment, reportSummary: string): void {
  const section = element('section', undefined, 'final-report-assessment');
  section.append(element('h3', 'Feature request conclusion'), element('strong', featureLabels[value.status] ?? 'Not recorded'));
  if (value.summary !== reportSummary) section.append(element('p', value.summary, 'prewrap'));
  list(section, 'Reasons', value.reasons); list(section, 'Research evidence', value.evidence);
  list(section, 'Acceptance criteria', value.acceptanceCriteria); list(section, 'Questions', value.questions); list(section, 'Alternatives and tradeoffs', value.alternatives);
  reference(section, value.relatedIssue);
  paragraph(section, 'Saved plan', value.planId);
  parent.append(section);
}

function bugConclusion(parent: HTMLElement, value: BugAssessment, reportSummary: string): void {
  const section = element('section', undefined, 'final-report-assessment');
  section.append(element('h3', 'Bug investigation conclusion'), element('strong', bugLabels[value.status] ?? 'Not recorded'));
  if (value.summary !== reportSummary) section.append(element('p', value.summary, 'prewrap'));
  list(section, 'Reasons', value.reasons); list(section, 'Investigation evidence', value.evidence); list(section, 'Questions', value.questions);
  reference(section, value.relatedIssue); paragraph(section, 'Saved plan', value.planId);
  const reproduction = value.reproduction;
  const actual = element('section', undefined, 'final-report-reproduction'); actual.append(element('h4', 'Actual reproduction'));
  const facts = element('dl', undefined, 'facts');
  facts.append(labeledValue('Result', reproductionLabels[reproduction.status] ?? 'Not recorded'), labeledValue('Tested revision', textValue(reproduction.revisionSha) ?? 'Not recorded'));
  actual.append(facts);
  paragraph(actual, 'Environment', reproduction.environment); list(actual, 'Reproduction steps', reproduction.steps, true);
  paragraph(actual, 'Expected result', reproduction.expected); paragraph(actual, 'Observed result', reproduction.observed); list(actual, 'Reproduction evidence', reproduction.evidence);
  section.append(actual); parent.append(section);
}

function findingArticle(finding: ResultFindingV3, confirmed: boolean, feedback?: (finding: ResultFindingV3) => HTMLElement | undefined, unified = false): HTMLElement {
  const article = element('article', undefined, `details-finding ${confirmed ? 'final-report-finding' : 'final-report-unconfirmed'}`);
  article.dataset.findingId = finding.id;
  article.dataset.priority = finding.priority;
  const heading = element('div', undefined, 'row');
  const status = finding.status === 'fixed' ? 'Fixed' : finding.status === 'unverified' ? 'Unverified' : 'Open';
  heading.append(element('h4', finding.title), element('span', `${confirmed ? finding.priority : `Unconfirmed ${finding.priority}`} · ${status}`, 'chip'));
  article.append(heading);
  article.append(element('p', finding.path ? `${finding.path}${finding.line === null ? '' : `:${finding.line}`}` : 'General finding', 'path muted fine'));
  let fullFinding = article;
  if (unified) {
    paragraph(article, 'Impact', finding.impact);
    fullFinding = element('details', undefined, 'final-report-finding-details');
    fullFinding.append(element('summary', 'Description, evidence and fix')); article.append(fullFinding);
  }
  paragraph(fullFinding, 'Description', finding.details);
  if (!unified) paragraph(fullFinding, 'Impact', finding.impact);
  paragraph(fullFinding, 'Trigger', finding.trigger);
  paragraph(fullFinding, 'Root cause and its evidence limits', finding.rootCause); list(fullFinding, 'Evidence', finding.evidence); paragraph(fullFinding, 'Fix suggestion', finding.fixSuggestion);
  const editor = confirmed ? feedback?.(finding) : undefined;
  if (editor) article.append(editor);
  else if (textValue(finding.feedback.body)) {
    const draft = element('details'); draft.append(element('summary', 'Recorded feedback draft'), element('pre', finding.feedback.body, 'log')); fullFinding.append(draft);
  }
  if (!editor && finding.feedback.suggestionId) fullFinding.append(element('p', `Code suggestion reference: ${finding.feedback.suggestionId}`, 'muted fine'));
  return article;
}

function savedPlans(parent: HTMLElement, plans: readonly ResultPlan[] | undefined, unified = false): void {
  if (!Array.isArray(plans) || !plans.length) return;
  const section = element('section', undefined, 'final-report-plans'); section.append(element('h3', 'Saved plans'));
  for (const plan of plans as readonly ResultPlan[]) {
    const article = element('article', undefined, 'details-finding final-report-plan'); article.dataset.planId = plan.id;
    article.append(element('h4', `${planLabels[plan.kind] ?? plan.kind} · ${plan.id}`), element('p', plan.summary, 'prewrap'));
    let fullPlan = article;
    if (unified) {
      fullPlan = element('details', undefined, 'final-report-plan-details');
      fullPlan.append(element('summary', 'Plan details and evidence')); article.append(fullPlan);
    }
    list(fullPlan, 'Steps', plan.steps, true); list(fullPlan, 'Acceptance criteria', plan.acceptanceCriteria); list(fullPlan, 'Prerequisites', plan.prerequisites); list(fullPlan, 'Plan evidence', plan.evidence);
    section.append(article);
  }
  parent.append(section);
}

/** A read-only final report; feedback controls and action execution remain owned by the calling view. */
export function renderFinalReport(container: HTMLElement, run: Run, feedback?: (finding: ResultFindingV3) => HTMLElement | undefined, options: FinalReportOptions = {}): void {
  container.replaceChildren(); container.hidden = run.result?.schemaVersion !== 3;
  if (container.hidden) return;
  const result = run.result!;
  if (!isFinalReportComplete(run)) {
    const active = isActive(run.status.state);
    if (!options.unified) container.append(element('h3', active ? 'Analysis in progress' : 'Final report incomplete'));
    container.append(element('p', active
      ? 'The analysis and rechecking are in progress. The complete finding list will appear with the final report.'
      : 'A complete, rechecked final report is not available. Recorded coverage and limitations are preserved below.', 'muted'));
    if (active) paragraph(container, 'Latest progress', run.status.latestProgress);
    else if (!options.unified) paragraph(container, 'Recorded summary', result.summary);
    coverage(container, run); return;
  }
  const issue = run.task.target?.type === 'issue';
  if (!options.unified) container.append(element('h3', issue ? 'Final investigation report' : 'Final review report'), element('p', result.summary, 'result-summary prewrap'));
  const recommendation = options.unified ? undefined : currentRecommendation(run);
  if (recommendation) {
    const labels: Record<string, string> = { approve: 'Approve', 'address-findings': 'Address the findings', 'run-e2e': 'Complete the required E2E verification', incomplete: 'Analysis incomplete', 'start-task': 'Start the saved plan', comment: 'Send the proposed comment', close: 'Review closing the Issue', 'close-as-duplicate': 'Review the duplicate reference and close', 'create-pr': 'Review creating a pull request', viewChanges: 'Inspect the retained changes', none: 'No follow-up proposed' };
    const advice = element('section', undefined, 'final-report-recommendation');
    advice.append(element('h3', 'Current recommendation'), element('strong', labels[recommendation.kind] ?? 'Review the proposed next step'), element('p', recommendation.reason, 'prewrap'));
    if (!issue) advice.append(element('p', 'This recommendation is separate from your chosen GitHub action.', 'muted fine'));
    container.append(advice);
  }
  if (issue && result.featureAssessment) featureConclusion(container, result.featureAssessment, result.summary);
  if (issue && result.bugAssessment) bugConclusion(container, result.bugAssessment, result.summary);
  if (!issue && result.reviewConclusion) {
    const conclusion = result.reviewConclusion;
    const labels = { 'no-blocking-findings': 'No blocking findings', 'changes-requested': 'Changes requested', inconclusive: 'Inconclusive' };
    const section = element('section', undefined, 'final-report-conclusion'); section.append(element('h3', 'Recorded code conclusion'), element('strong', labels[conclusion.status] ?? 'Not recorded'));
    if (conclusion.summary !== result.summary) section.append(element('p', conclusion.summary, 'prewrap'));
    const revision = element('dl', undefined, 'facts'); revision.append(labeledValue('Reviewed revision', textValue(conclusion.revisionSha) ?? 'Not recorded')); section.append(revision);
    list(section, 'Blocking uncertainties', conclusion.blockingUncertainties); container.append(section);
  }
  recordedAssessment(container, result.assessment);
  const confirmed = completedV3Findings(run);
  const findings = element('section', undefined, 'final-report-findings'); findings.append(element('h3', `Confirmed findings · ${confirmed.length}`));
  for (const priority of ['P0', 'P1', 'P2', 'P3'] as const) {
    const rows = confirmed.filter(finding => finding.priority === priority);
    if (!rows.length) continue;
    const group = element('section', undefined, 'final-report-priority'); group.dataset.priority = priority;
    group.append(element('h4', `${priority} · ${rows.length} ${rows.length === 1 ? 'finding' : 'findings'}`));
    for (const finding of rows) group.append(findingArticle(finding, true, feedback, options.unified));
    findings.append(group);
  }
  if (!confirmed.length) findings.append(element('p', 'No confirmed findings were recorded in this final report.', 'muted'));
  container.append(findings);
  const unconfirmed = (result.findings ?? []).filter((finding): finding is ResultFindingV3 => typeof finding.priority === 'string' && (!finding.confirmed || finding.status === 'unverified'));
  if (unconfirmed.length) {
    const section = element('section', undefined, 'final-report-unconfirmed-findings'); section.append(element('h3', `Unconfirmed observations · ${unconfirmed.length}`), element('p', 'These recorded observations have not been confirmed as defects. Their recorded priorities are provisional, including any P0 label.', 'notice'));
    for (const finding of unconfirmed) section.append(findingArticle(finding, false, undefined, options.unified));
    container.append(section);
  }
  savedPlans(container, result.plans, options.unified); coverage(container, run);
}
