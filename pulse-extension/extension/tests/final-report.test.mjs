import test from 'node:test';
import assert from 'node:assert/strict';
import { renderFinalReport } from '../src/final-report.ts';

class Node {
  children = []; attributes = new Map(); dataset = {}; listeners = new Map(); className = ''; hidden = false; _text = '';
  constructor(tag = 'div') { this.tagName = tag.toUpperCase(); }
  set textContent(value) { this._text = String(value ?? ''); this.children = []; }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  append(...children) {
    for (const child of children) { const node = typeof child === 'string' ? Object.assign(new Node('#text'), { textContent: child }) : child; node.parentNode = this; this.children.push(node); }
  }
  replaceChildren(...children) { for (const child of this.children) child.parentNode = null; this.children = []; this._text = ''; this.append(...children); }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  addEventListener(name, callback) { this.listeners.set(name, callback); }
  get childElementCount() { return this.children.filter(child => child.tagName !== '#TEXT').length; }
  set innerHTML(_) { assert.fail('Report data must not enter an HTML parser.'); }
  insertAdjacentHTML() { assert.fail('Report data must not enter an HTML parser.'); }
}
const nodes = root => [root, ...root.children.flatMap(nodes)];
const headings = root => nodes(root).filter(node => /^H[1-6]$/.test(node.tagName));
const list = (prefix, count = 3) => Array.from({ length: count }, (_, index) => `${prefix}-${index + 1}`);
const sha = 'abcdef0123456789abcdef0123456789abcdef01';
const relatedIssue = { repository: 'microsoft/PowerToys', number: 27, url: 'https://github.com/microsoft/PowerToys/issues/27' };

function finding(id, overrides = {}) {
  return { id, title: `Title ${id}`, priority: 'P2', status: 'open', confirmed: true, path: `src/${id}.cs`, line: 17,
    details: `Details ${id}`, impact: `Impact ${id}`, trigger: `Trigger ${id}`, rootCause: `Root cause ${id}`,
    fixSuggestion: `Fix suggestion ${id}`, evidence: list(`Evidence ${id}`), feedback: { body: `Feedback ${id}`, suggestionId: null }, ...overrides };
}
function run(overrides = {}) {
  return { runId: '6c9f13d3-e925-4527-9e1b-4a7fcd2d2af2',
    task: { actionKind: 'pr-review', repository: 'microsoft/PowerToys', target: { type: 'pr', number: 42 }, expectedHeadSha: sha, reviewOptions: { mode: 'static' } },
    status: { state: 'succeeded', exitCode: 0 },
    result: { schemaVersion: 3, structured: true, outcome: 'completed', phase: 'reporting', cliExitCode: 0,
      summary: 'Recorded final summary.', report: { complete: true, rechecked: true, coverage: list('Coverage'), limitations: list('Limitation') },
      findings: [], review: null, assessment: { subject: 'original-pr', status: 'passed', summary: 'Original code inspected.', revisionSha: sha },
      reviewConclusion: { status: 'no-blocking-findings', summary: 'Original review completed.', revisionSha: sha, blockingUncertainties: [] },
      e2eAssessment: { level: 'not_needed', reason: 'Recorded unit tests cover the pure code change.', question: '', scenarios: [], expectedResults: [], prerequisites: [], evidence: ['Recorded unit test evidence.'], readiness: 'ready' },
      featureAssessment: null, bugAssessment: null, plans: [], nextActions: [], nextSteps: [], artifacts: [], validation: [], diagnostics: [], verificationEvidence: [], blockers: [], needsReview: false, ...overrides },
  };
}
function render(value, feedback, container = new Node('section'), options = {}) {
  const previous = Object.getOwnPropertyDescriptor(globalThis, 'document');
  try {
    globalThis.document = { createElement: tag => new Node(tag), createTextNode: text => Object.assign(new Node('#text'), { textContent: text }) };
    renderFinalReport(container, value, feedback, options);
    return container;
  } finally { if (previous) Object.defineProperty(globalThis, 'document', previous); else delete globalThis.document; }
}
function includesAll(root, values, label = '') { for (const value of values) assert.ok(root.textContent.includes(value), `${label}: missing ${value}`); }
function visibleText(root) {
  if (root.tagName === 'DETAILS' && !root.open) return root.children.find(child => child.tagName === 'SUMMARY')?.textContent ?? '';
  return root._text + root.children.map(visibleText).join('');
}
function issue(kind, status, reproductionStatus = 'not_run') {
  const value = run(); value.task.actionKind = kind === 'feature' ? 'feature-research' : 'bug-investigation'; value.task.target.type = 'issue';
  delete value.task.expectedHeadSha; delete value.task.reviewOptions;
  value.result.assessment = { subject: 'target', status: 'passed', summary: 'The issue investigation is complete.', revisionSha: null };
  value.result.reviewConclusion = null; value.result.e2eAssessment = null;
  const planKind = kind === 'feature' ? 'feature-implement' : 'issue-fix';
  const savedPlan = { id: `plan-${kind}`, kind: planKind, summary: `Saved ${kind} plan`, steps: list(`${kind} plan step`, 103),
    acceptanceCriteria: list(`${kind} plan acceptance`, 103), prerequisites: list(`${kind} plan prerequisite`, 103), evidence: list(`${kind} plan evidence`, 103) };
  const assessment = { status, summary: `${kind} combined conclusion ${status}`, reasons: list(`${kind} reasons`, 103), evidence: list(`${kind} evidence`, 103),
    questions: list(`${kind} questions`, 103), relatedIssue: status === 'duplicate' ? { ...relatedIssue } : null, planId: savedPlan.id };
  if (kind === 'feature') Object.assign(assessment, { acceptanceCriteria: list('feature acceptance', 103), alternatives: list('feature alternatives', 103) });
  else {
    assessment.reproduction = { status: reproductionStatus, revisionSha: sha, environment: 'Recorded Windows environment.', steps: list('Reproduction step', 103),
      expected: 'Recorded expected behavior.', observed: 'Recorded actual observation.', evidence: list('Reproduction evidence', 103) };
    if (status === 'confirmed') value.result.findings = [finding('confirmed-bug')];
  }
  value.result[kind === 'feature' ? 'featureAssessment' : 'bugAssessment'] = assessment;
  value.result.plans = [savedPlan];
  value.result.nextActions = [{ proposalId: 'next-comment', kind: 'comment', recommended: true, reason: 'ACTION_REASON_RENDERED_ELSEWHERE', body: 'ACTION_BODY_RENDERED_ELSEWHERE' }];
  return value;
}

test('partial, active, and partially loaded reports show coverage and limitations without final findings or feedback controls', () => {
  const cases = [
    value => { value.result.report.complete = false; },
    value => { value.result.report.rechecked = false; },
    value => { value.result.outcome = 'blocked'; },
    value => { value.status.state = 'accepted'; },
    value => { value.status.state = 'running'; },
    value => { value.resultPaging = { fingerprint: 'a'.repeat(64), sections: [{ path: 'findings', total: 130, nextOffset: 1 }] }; },
  ];
  for (const mutate of cases) {
    const value = run({ findings: [finding('HIDDEN_PARTIAL_FINDING')] }); mutate(value);
    const saved = structuredClone(value); let callbacks = 0;
    const root = render(value, () => { callbacks++; return new Node('input'); });
    assert.match(root.textContent, /incomplete|in progress|running|not complete|not available|still being read/i);
    includesAll(root, [...value.result.report.coverage, ...value.result.report.limitations]);
    assert.doesNotMatch(root.textContent, /HIDDEN_PARTIAL_FINDING/);
    assert.equal(callbacks, 0);
    assert.deepEqual(value, saved);
  }
});

test('final PR reports render all confirmed P0-P3 details and keep unconfirmed observations in a separate section', () => {
  const confirmed = Array.from({ length: 129 }, (_, index) => finding(`finding-${index + 1}`, { priority: `P${index % 4}` }));
  confirmed[128].evidence = list('Last finding evidence', 103);
  const candidates = [finding('CANDIDATE_ONLY', { confirmed: false, status: 'unverified', priority: 'P0' }), finding('UNCONFIRMED_ONLY', { confirmed: false })];
  const value = run({ findings: [...confirmed, ...candidates] }); const saved = structuredClone(value); const selected = [];
  const root = render(value, row => { selected.push(row.id); const control = new Node('input'); control.dataset.findingId = row.id; return control; });
  assert.deepEqual(selected, ['P0', 'P1', 'P2', 'P3'].flatMap(priority => confirmed.filter(row => row.priority === priority).map(row => row.id)), 'the callback receives every confirmed finding, grouped by priority and never a candidate');
  for (const row of confirmed) includesAll(root, [row.title, row.priority, row.path, row.details, row.impact, row.trigger, row.rootCause, row.fixSuggestion, ...row.evidence], row.id);
  for (const candidate of candidates) {
    includesAll(root, [candidate.title, candidate.details]);
    const section = nodes(root).find(node => headings(node).some(heading => /unconfirmed|unverified|not confirmed/i.test(heading.textContent)) && node.textContent.includes(candidate.title) && !node.textContent.includes(confirmed[0].title));
    assert.ok(section, 'unconfirmed evidence has a labeled section separate from the confirmed final list');
  }
  assert.equal(nodes(root).filter(node => node.tagName === 'INPUT').length, confirmed.length);
  assert.deepEqual(value, saved);
});

test('PR and Bug final lists group every finding P0-first with stable group order and unchanged source identities', () => {
  const rows = [...Array.from({ length: 225 }, (_, index) => finding(`p2-${index}`)), finding('late-p3', { priority: 'P3' }), finding('late-p1', { priority: 'P1' }), finding('last-p0', { priority: 'P0' })];
  const expected = ['last-p0', 'late-p1', ...rows.slice(0, 225).map(row => row.id), 'late-p3'];
  for (const kind of ['pr-review', 'bug-investigation']) {
    const value = kind === 'pr-review' ? run({ findings: rows }) : issue('bug', 'confirmed');
    value.result.findings = structuredClone(rows); const saved = structuredClone(value);
    const selection = new Map([['last-p0', true], ['p2-17', true]]); const seen = [];
    const root = render(value, row => { seen.push(row.id); const control = new Node('input'); control.dataset.findingId = row.id; control.checked = selection.get(row.id) === true; return control; });
    const groups = nodes(root).filter(node => node.className === 'final-report-priority');
    assert.deepEqual(groups.map(node => node.children[0].textContent), ['P0 · 1 finding', 'P1 · 1 finding', 'P2 · 225 findings', 'P3 · 1 finding']);
    assert.deepEqual(nodes(root).filter(node => node.tagName === 'ARTICLE' && node.dataset.findingId).map(node => node.dataset.findingId), expected);
    assert.deepEqual(seen, expected); assert.equal(new Set(seen).size, rows.length);
    assert.deepEqual(nodes(root).filter(node => node.tagName === 'INPUT' && node.checked).map(node => node.dataset.findingId), ['last-p0', 'p2-17']);
    assert.deepEqual(value, saved, `${kind}: display grouping never sorts or rewrites the saved result`);
  }
});

test('all Feature and Bug states retain a unified conclusion, reproduction facts, and complete read-only plans', () => {
  const states = { feature: ['ready', 'needs_information', 'needs_decision', 'already_supported', 'duplicate', 'not_feasible'], bug: ['confirmed', 'needs_information', 'needs_verification', 'already_fixed', 'duplicate', 'not_a_bug'] };
  const reproductionStatuses = ['reproduced', 'not_reproduced', 'not_run', 'blocked'];
  for (const [kind, statuses] of Object.entries(states)) for (const [index, status] of statuses.entries()) {
    const value = issue(kind, status, reproductionStatuses[index % reproductionStatuses.length]); const saved = structuredClone(value);
    const assessment = value.result[kind === 'feature' ? 'featureAssessment' : 'bugAssessment']; const root = render(value);
    includesAll(root, [assessment.summary, ...assessment.reasons, ...assessment.evidence, ...assessment.questions, ...(assessment.acceptanceCriteria ?? []), ...(assessment.alternatives ?? [])], `${kind}/${status}`);
    assert.equal(root.textContent.split(assessment.summary).length - 1, 1, 'the combined investigation is presented once');
    for (const savedPlan of value.result.plans) includesAll(root, [savedPlan.summary, ...savedPlan.steps, ...savedPlan.acceptanceCriteria, ...savedPlan.prerequisites, ...savedPlan.evidence]);
    if (assessment.relatedIssue) includesAll(root, [assessment.relatedIssue.repository, String(assessment.relatedIssue.number)]);
    if (assessment.reproduction) {
      const reproduction = assessment.reproduction;
      includesAll(root, [reproduction.environment, reproduction.revisionSha, reproduction.expected, reproduction.observed, ...reproduction.steps, ...reproduction.evidence]);
      assert.match(root.textContent, new RegExp(reproduction.status.replaceAll('_', '[ _-]'), 'i'));
    }
    assert.equal(headings(root).some(node => /^feasibility(?: assessment)?$/i.test(node.textContent)), false);
    assert.equal(nodes(root).some(node => ['BUTTON', 'FORM', 'INPUT', 'TEXTAREA', 'SELECT'].includes(node.tagName)), false, 'report plans are read-only; action controls belong to the shared action area');
    assert.doesNotMatch(root.textContent, /ACTION_BODY_RENDERED_ELSEWHERE/, 'the operation body remains in the separate action editor');
    const recommendation = nodes(root).find(node => node.className === 'final-report-recommendation');
    assert.ok(recommendation); assert.match(recommendation.textContent, /Current recommendationSend the proposed comment/);
    const reason = value.result.nextActions[0].reason;
    assert.equal(root.textContent.split(reason).length - 1, 1, 'the current recommendation reason is rendered once');
    assert.deepEqual(value, saved);
  }
});

test('untrusted report text and paths remain plain text and cannot become executable markup or links', () => {
  const payload = '<img src=x onerror="globalThis.compromised=1">';
  const unsafePaths = ['javascript:alert(1)', 'file:///C:/Windows/System32/cmd.exe', 'data:text/html,<script>alert(1)</script>'];
  for (const path of unsafePaths) {
    const row = finding('untrusted', { title: payload, path, details: `<script>${payload}</script>`, trigger: payload, rootCause: payload, fixSuggestion: payload, evidence: [payload] });
    const value = run({ summary: payload, findings: [row] });
    value.result.report.coverage = [payload]; value.result.report.limitations = [payload];
    const root = render(value);
    includesAll(root, [payload, row.details, path]);
    assert.equal(nodes(root).some(node => ['SCRIPT', 'IMG', 'IFRAME', 'OBJECT', 'EMBED'].includes(node.tagName)), false);
    for (const node of nodes(root)) {
      for (const name of node.attributes.keys()) assert.doesNotMatch(name, /^on/i);
      if (node.href) assert.match(node.href, /^https:\/\//);
      assert.equal(node.listeners.size, 0, 'the read-only report never turns evidence into commands');
    }
  }
});

test('rendering a later incomplete result into the same container removes the previous final list and controls', () => {
  const value = run({ findings: [finding('PREVIOUS_FINAL_ONLY')] });
  const root = render(value, () => new Node('input'));
  assert.match(root.textContent, /PREVIOUS_FINAL_ONLY/);
  value.status.state = 'running'; value.result.report.complete = false;
  let callbacks = 0; render(value, () => { callbacks++; return new Node('input'); }, root);
  assert.doesNotMatch(root.textContent, /PREVIOUS_FINAL_ONLY/);
  assert.equal(nodes(root).some(node => node.tagName === 'INPUT'), false);
  assert.equal(callbacks, 0);
});

test('unified report removes duplicated summary and recommendation while keeping compact confirmed and provisional findings lossless', () => {
  const confirmed = finding('confirmed', { priority: 'P1', feedback: { body: 'Original full feedback draft', suggestionId: 'saved-suggestion-7' } });
  const candidate = finding('candidate', { priority: 'P0', confirmed: false, status: 'unverified' });
  const value = run({ summary: 'ROOT_SUMMARY_ONLY', findings: [confirmed, candidate],
    nextActions: [{ proposalId: 'next-comment', kind: 'comment', recommended: true, reason: 'ROOT_RECOMMENDATION_ONLY', body: 'ROOT_ACTION_BODY_ONLY' }] });
  const saved = structuredClone(value);
  const root = render(value, undefined, new Node('section'), { unified: true });
  assert.doesNotMatch(root.textContent, /ROOT_SUMMARY_ONLY|ROOT_RECOMMENDATION_ONLY|ROOT_ACTION_BODY_ONLY|Current recommendation/);
  assert.equal(headings(root).some(node => /^Final (review|investigation) report$/.test(node.textContent)), false);
  includesAll(root, [value.result.reviewConclusion.summary, 'Recorded code conclusion', 'Confirmed findings · 1', 'Unconfirmed observations · 1']);
  const confirmedSection = nodes(root).find(node => node.className === 'final-report-findings');
  assert.doesNotMatch(confirmedSection.textContent, /Title candidate/);
  const provisionalSection = nodes(root).find(node => node.className === 'final-report-unconfirmed-findings');
  assert.match(visibleText(provisionalSection), /Unconfirmed P0.*Unverified/);
  assert.match(visibleText(provisionalSection), /priorities are provisional/);
  for (const row of [confirmed, candidate]) {
    const article = nodes(root).find(node => node.tagName === 'ARTICLE' && node.dataset.findingId === row.id);
    includesAll(article, [row.title, row.path, row.impact, row.details, row.trigger, row.rootCause, row.fixSuggestion, ...row.evidence, row.feedback.body]);
    const visible = visibleText(article);
    assert.ok(visible.includes(row.title) && visible.includes(`${row.path}:${row.line}`) && visible.includes(row.impact));
    for (const detail of [row.details, row.trigger, row.rootCause, row.fixSuggestion, ...row.evidence, row.feedback.body]) assert.ok(!visible.includes(detail), `${detail} belongs to the native disclosure`);
    const disclosure = article.children.find(node => node.className === 'final-report-finding-details');
    assert.equal(disclosure.tagName, 'DETAILS'); assert.equal(disclosure.children[0].tagName, 'SUMMARY'); assert.ok(!disclosure.open);
    disclosure.open = true;
    for (const detail of [row.details, row.trigger, row.rootCause, row.fixSuggestion, ...row.evidence]) assert.ok(visibleText(article).includes(detail));
  }
  assert.match(root.textContent, /Code suggestion reference: saved-suggestion-7/);
  assert.deepEqual(value, saved, 'Compaction never changes saved evidence, feedback or priorities.');
});

test('unified assessment displays the recorded source and revision instead of inheriting the task or code-review revision', () => {
  const subjects = { 'original-pr': 'Original pull request', 'local-candidate': 'Local candidate', target: 'Task target' };
  for (const [subject, label] of Object.entries(subjects)) for (const revisionSha of ['b'.repeat(40), null]) {
    const value = run({ assessment: { subject, status: 'inconclusive', summary: 'Bounded recorded assessment.', revisionSha } });
    const root = render(value, undefined, new Node('section'), { unified: true });
    const assessment = nodes(root).find(node => node.className === 'final-report-assessment');
    includesAll(assessment, ['Recorded assessment', 'Assessed source', label, 'Inconclusive', 'Assessed revision', revisionSha ?? 'Not recorded', value.result.assessment.summary]);
    assert.ok(!assessment.textContent.includes(sha), 'Assessment source identity must not be filled from the task or another conclusion.');
    assert.ok(root.textContent.includes(sha), 'The separate recorded code conclusion retains its own revision.');
  }
});

test('unified Issue plans retain every ordered step and evidence item in a closed disclosure with their summary visible', () => {
  for (const kind of ['feature', 'bug']) {
    const value = issue(kind, kind === 'feature' ? 'ready' : 'confirmed');
    const saved = structuredClone(value); const root = render(value, undefined, new Node('section'), { unified: true });
    for (const plan of value.result.plans) {
      const article = nodes(root).find(node => node.tagName === 'ARTICLE' && node.dataset.planId === plan.id);
      assert.ok(visibleText(article).includes(plan.summary));
      const disclosure = article.children.find(node => node.className === 'final-report-plan-details');
      assert.equal(disclosure.tagName, 'DETAILS'); assert.ok(!disclosure.open);
      assert.equal(disclosure.children[0].textContent, 'Plan details and evidence');
      const ordered = nodes(disclosure).find(node => node.tagName === 'OL');
      assert.deepEqual(ordered.children.map(node => node.textContent), plan.steps, 'All 103 ordered steps remain intact.');
      const content = [...plan.steps, ...plan.acceptanceCriteria, ...plan.prerequisites, ...plan.evidence];
      includesAll(disclosure, content);
      assert.ok(content.every(item => !visibleText(article).includes(item)), 'The summary stays compact before expanding.');
      disclosure.open = true;
      assert.ok(content.every(item => visibleText(article).includes(item)), 'Opening reveals all saved plan content without a page limit.');
    }
    assert.deepEqual(value, saved);
  }
});

test('unified incomplete reports preserve the evidence warning without a final count, assessment or duplicated root summary', () => {
  const value = run({ summary: 'ROOT_INCOMPLETE_SUMMARY', findings: [finding('PARTIAL_ONLY')] });
  value.result.report.complete = false;
  const root = render(value, undefined, new Node('section'), { unified: true });
  assert.match(root.textContent, /complete, rechecked final report is not available/);
  includesAll(root, [...value.result.report.coverage, ...value.result.report.limitations]);
  assert.doesNotMatch(root.textContent, /ROOT_INCOMPLETE_SUMMARY|PARTIAL_ONLY|Confirmed findings|Recorded assessment|Current recommendation/);
});
