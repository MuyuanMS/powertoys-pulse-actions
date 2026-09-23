import test from 'node:test';
import assert from 'node:assert/strict';
import { findingFeedbackController } from '../src/finding-feedback.ts';

class Node {
  children = []; attributes = new Map(); listeners = new Map(); dataset = {}; _text = ''; value = ''; checked = false; disabled = false; hidden = false;
  constructor(tag = 'div') { this.tagName = tag.toUpperCase(); }
  set textContent(value) { this._text = String(value ?? ''); this.children = []; }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this._text = ''; this.children = [...children]; }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  addEventListener(name, callback) { this.listeners.set(name, [...(this.listeners.get(name) ?? []), callback]); }
  set innerHTML(_) { assert.fail('Finding feedback must use plaintext DOM helpers.'); }
}
const descendants = root => [root, ...root.children.flatMap(descendants)];
const sha = 'a'.repeat(40);
const otherSha = 'b'.repeat(40);
const markdown = '  \n## Evidence\n\n- [ ] Keep **Markdown** and trailing spaces.  \n\n';
const inlineMarkdown = '\nA code comment with `literal` text.  \n\n```text\nDo not render this as HTML.\n```\n';
const replacement = '  keepOriginal();  \n\n';

function finding(id, overrides = {}) {
  return { id, title: `Finding ${id}`, priority: 'P2', confirmed: true, status: 'open', path: 'src/Keys.cs', line: 4,
    details: `Details ${id}`, impact: `Impact ${id}`, trigger: `Trigger ${id}`, rootCause: `Root cause ${id}`, fixSuggestion: `Fix ${id}`,
    evidence: [`Recorded evidence ${id}`], feedback: { body: `Feedback ${id}`, suggestionId: null }, ...overrides };
}
function suggestion(id, overrides = {}) {
  return { id, path: 'src/Keys.cs', startLine: 3, line: 4, side: 'RIGHT', body: 'UNSELECTED_SOURCE_SUGGESTION_BODY', replacement, ...overrides };
}
function fixture() {
  const findings = [
    finding('inline-p0', { priority: 'P0', feedback: { body: inlineMarkdown, suggestionId: 'valid-inline' } }),
    finding('ordinary-p1', { priority: 'P1', feedback: { body: markdown, suggestionId: null } }),
    finding('ordinary-p2', { feedback: { body: '\nSecond ordinary comment.  \n', suggestionId: null } }),
    finding('hidden-p3', { priority: 'P3', feedback: { body: 'UNSELECTED_HIDDEN_FINDING_BODY', suggestionId: null } }),
    finding('invalid-location', { feedback: { body: 'Ordinary fallback for a missing inline position.', suggestionId: 'missing-position' } }),
    finding('missing-source', { feedback: { body: 'Ordinary fallback with no source suggestion.', suggestionId: 'unknown-id' } }),
    finding('candidate-only', { priority: 'P0', confirmed: false, status: 'unverified', feedback: { body: 'UNCONFIRMED_CANDIDATE_BODY', suggestionId: 'candidate-inline' } }),
  ];
  const run = {
    runId: '6c9f13d3-e925-4527-9e1b-4a7fcd2d2af2',
    task: { actionKind: 'pr-review', repository: 'microsoft/PowerToys', target: { type: 'pr', number: 42 }, expectedHeadSha: sha, reviewOptions: { mode: 'static' } },
    status: { state: 'succeeded', exitCode: 0 },
    result: { schemaVersion: 3, structured: true, outcome: 'completed', phase: 'reporting', summary: 'A final reviewed report.', cliExitCode: 0,
      report: { complete: true, rechecked: true, coverage: ['All changed paths were rechecked.'], limitations: [] }, findings,
      review: { headSha: sha, body: 'UNSELECTED_GLOBAL_REVIEW_BODY', suggestions: [suggestion('valid-inline'), suggestion('missing-position', { startLine: 25, line: 25 }), suggestion('candidate-inline')] },
      assessment: { subject: 'original-pr', status: 'failed', summary: 'The original PR contains a confirmed defect.', revisionSha: sha },
      reviewConclusion: { status: 'changes-requested', summary: 'Confirmed findings were rechecked.', revisionSha: sha, blockingUncertainties: [] },
      verificationEvidence: [], e2eAssessment: null, featureAssessment: null, bugAssessment: null, plans: [], artifacts: [], validation: [], diagnostics: [], nextActions: [], blockers: [], nextSteps: [], needsReview: true },
  };
  const preview = { account: 'reviewer', target: { type: 'pr', number: 42, repository: 'microsoft/PowerToys', url: 'https://github.com/microsoft/PowerToys/pull/42', state: 'open' },
    url: 'https://github.com/microsoft/PowerToys/pull/42', state: 'open', expectedHeadSha: sha, headSha: sha, stale: false,
    canApprove: true, canRequestChanges: true, canSuggestChanges: true, canComment: true, canClose: true, reasons: [],
    files: [{ path: 'src/Keys.cs', status: 'modified', lines: [{ line: 3, kind: 'context', original: 'if (conflict)', hunk: 1 }, { line: 4, kind: 'add', original: '    replaceOriginal();', hunk: 1 }] }],
  };
  return { run, preview };
}
class MemoryStorage {
  values = new Map();
  getItem(key) { return this.values.get(key) ?? null; }
  setItem(key, value) { this.values.set(key, value); }
  removeItem(key) { this.values.delete(key); }
}
function withController(verify) {
  const previous = Object.getOwnPropertyDescriptor(globalThis, 'document');
  try {
    globalThis.document = { createElement: tag => new Node(tag) };
    const value = fixture(); const storage = new MemoryStorage(); let changes = 0;
    const controller = findingFeedbackController(() => { changes++; }, storage);
    verify({ ...value, storage, controller, changes: () => changes });
  } finally { if (previous) Object.defineProperty(globalThis, 'document', previous); else delete globalThis.document; }
}
function renderFinding(controller, run, id) {
  const root = controller.render(run.result.findings.find(row => row.id === id));
  const inputs = descendants(root).filter(node => node.tagName === 'INPUT');
  const areas = descendants(root).filter(node => node.tagName === 'TEXTAREA');
  return { root, selected: inputs[0], inline: inputs[1], body: areas[0], replacement: areas[1] };
}
function edit(node, property, value, event = 'change') {
  assert.ok(node, 'the real controller rendered the requested control');
  assert.equal(node.disabled, false, 'the user must be able to edit this control');
  node[property] = value;
  const listeners = node.listeners.get(event); assert.ok(listeners?.length, 'the control has a real event handler');
  for (const listener of listeners) listener();
}
function keepOnly(controller, run, ids) {
  for (const row of run.result.findings.filter(row => row.confirmed)) {
    if (ids.includes(row.id)) continue;
    const editor = renderFinding(controller, run, row.id);
    edit(editor.selected, 'checked', false);
  }
}

test('all confirmed findings default to ordinary comments before target loading, while code suggestions require explicit selection', () => withController(({ run, preview, controller, changes }) => {
  controller.setContext(run);
  let inline = renderFinding(controller, run, 'inline-p0');
  assert.equal(inline.selected.checked, true); assert.equal(inline.inline.checked, false); assert.equal(inline.inline.disabled, true);
  controller.setContext(run, preview); inline = renderFinding(controller, run, 'inline-p0');
  assert.equal(inline.selected.checked, true); assert.equal(inline.inline.checked, false); assert.equal(inline.inline.disabled, false);
  assert.equal(inline.body.value, inlineMarkdown); assert.equal(inline.replacement.value, replacement);
  for (const id of ['ordinary-p1', 'ordinary-p2', 'hidden-p3', 'invalid-location', 'missing-source']) {
    const row = renderFinding(controller, run, id); assert.equal(row.selected.checked, true, id); assert.equal(row.body.disabled, false, id);
    if (row.inline) assert.equal(row.inline.disabled, true, id);
  }
  const composed = controller.compose();
  assert.deepEqual(composed.findingIds, run.result.findings.filter(row => row.confirmed).map(row => row.id));
  assert.equal(composed.body, run.result.findings.filter(row => row.confirmed).map(row => row.feedback.body).join('\n\n')); assert.deepEqual(composed.errors, []);
  assert.deepEqual(composed.suggestions, []); assert.doesNotMatch(JSON.stringify(composed), /UNCONFIRMED_CANDIDATE_BODY|UNSELECTED_GLOBAL_REVIEW_BODY|UNSELECTED_SOURCE_SUGGESTION_BODY/);
  assert.equal(changes(), 0, 'reading context and rendering defaults does not pretend the user made a choice');
}));

test('mixed selection composes exact Markdown and replacement text without hidden or deselected feedback', () => withController(({ run, preview, controller }) => {
  const saved = structuredClone(run); controller.setContext(run, preview);
  keepOnly(controller, run, ['inline-p0', 'ordinary-p1', 'ordinary-p2']);
  const inline = renderFinding(controller, run, 'inline-p0');
  edit(inline.inline, 'checked', true);
  const first = renderFinding(controller, run, 'ordinary-p1'); const second = renderFinding(controller, run, 'ordinary-p2');
  edit(first.selected, 'checked', true); edit(second.selected, 'checked', true);
  const editedBody = ' \n# Edited investigation\n\n> Quote with two trailing spaces.  \n';
  const editedInline = '\n**Edited inline feedback**  \n'; const editedReplacement = '\n  preserveOriginal();  \n\n';
  edit(first.body, 'value', editedBody, 'input'); edit(inline.body, 'value', editedInline, 'input'); edit(inline.replacement, 'value', editedReplacement, 'input');
  let composed = controller.compose();
  assert.deepEqual(composed.findingIds, ['inline-p0', 'ordinary-p1', 'ordinary-p2']);
  assert.equal(composed.body, `${editedBody}\n\n${second.body.value}`);
  assert.equal(composed.suggestions[0].body, editedInline); assert.equal(composed.suggestions[0].replacement, editedReplacement);
  assert.deepEqual(composed.errors, []);
  assert.doesNotMatch(JSON.stringify(composed), /UNSELECTED_|UNCONFIRMED_/);
  edit(first.selected, 'checked', false); edit(inline.selected, 'checked', false);
  composed = controller.compose();
  assert.deepEqual(composed.findingIds, ['ordinary-p2']); assert.equal(composed.body, second.body.value); assert.deepEqual(composed.suggestions, []);
  assert.deepEqual(run, saved, 'selection and editing never rewrite the retained model report');
}));

test('explicit selection and ordinary-comment mode survive refresh without reapplying defaults or firing user callbacks', () => withController(({ run, preview, controller, changes }) => {
  controller.setContext(run, preview);
  keepOnly(controller, run, ['inline-p0', 'ordinary-p1']);
  let inline = renderFinding(controller, run, 'inline-p0'); const ordinary = renderFinding(controller, run, 'ordinary-p1');
  edit(inline.inline, 'checked', false); edit(inline.body, 'value', markdown, 'input'); edit(ordinary.selected, 'checked', true);
  edit(inline.selected, 'checked', false);
  const beforeRefresh = changes();
  const refreshed = structuredClone(run); refreshed.result.review.body = 'A refreshed report must not overwrite explicit feedback choices.';
  controller.setContext(refreshed, structuredClone(preview)); inline = renderFinding(controller, refreshed, 'inline-p0');
  assert.equal(inline.selected.checked, false); assert.equal(inline.inline.checked, false); assert.equal(inline.body.value, markdown);
  assert.deepEqual(controller.compose().findingIds, ['ordinary-p1']); assert.equal(changes(), beforeRefresh);
  edit(inline.selected, 'checked', true);
  assert.equal(inline.inline.checked, false, 'reenabling the finding preserves the explicit ordinary-comment choice');
  assert.equal(controller.compose().body, `${markdown}\n\n${ordinary.body.value}`); assert.deepEqual(controller.compose().suggestions, []);
}));

test('a stale inline position keeps selected feedback blocked until the user explicitly switches to an ordinary comment', () => withController(({ run, preview, controller }) => {
  controller.setContext(run, preview); const inline = renderFinding(controller, run, 'inline-p0');
  keepOnly(controller, run, ['inline-p0']); edit(inline.inline, 'checked', true);
  const updatedPreview = structuredClone(preview); updatedPreview.headSha = otherSha;
  controller.setContext(run, updatedPreview);
  const blocked = controller.compose();
  assert.deepEqual(blocked.findingIds, ['inline-p0']); assert.equal(blocked.body, ''); assert.deepEqual(blocked.suggestions, []);
  assert.equal(blocked.errors.length, 1); assert.match(blocked.errors[0], /inline position.*unavailable/i);
  assert.equal(inline.selected.checked, true); assert.equal(inline.inline.checked, true, 'refresh does not silently discard the selected code suggestion');
  edit(inline.inline, 'checked', false);
  const recovered = controller.compose();
  assert.deepEqual(recovered.findingIds, ['inline-p0']); assert.equal(recovered.body, inlineMarkdown);
  assert.deepEqual(recovered.suggestions, []); assert.deepEqual(recovered.errors, []);
}));

test('the selection preview contains only composed feedback and clears for empty, non-final, or different-run context', () => withController(({ run, preview, controller }) => {
  controller.setContext(run, preview); const inline = renderFinding(controller, run, 'inline-p0');
  keepOnly(controller, run, ['inline-p0', 'ordinary-p1']); edit(inline.inline, 'checked', true);
  const ordinary = renderFinding(controller, run, 'ordinary-p1'); edit(ordinary.selected, 'checked', true);
  const summary = new Node('section'); controller.renderSelection(summary);
  assert.equal(summary.hidden, false); assert.match(summary.textContent, /Selected feedback \(2\)/);
  for (const text of [markdown, inlineMarkdown, replacement, 'src/Keys.cs:3–4']) assert.ok(summary.textContent.includes(text), text);
  assert.doesNotMatch(summary.textContent, /UNSELECTED_|UNCONFIRMED_/);
  edit(inline.selected, 'checked', false); edit(ordinary.selected, 'checked', false); controller.renderSelection(summary);
  assert.equal(summary.hidden, true); assert.equal(summary.textContent, '');
  const next = structuredClone(run); next.runId = 'bc82111b-024c-4f59-af3b-937fe990bcb9';
  controller.setContext(next, preview); assert.deepEqual(controller.compose().findingIds, next.result.findings.filter(row => row.confirmed).map(row => row.id), 'a different task begins with all confirmed findings selected as ordinary comments');
  next.status.state = 'running'; controller.setContext(next, preview); controller.renderSelection(summary);
  assert.deepEqual(controller.compose(), { findingIds: [], body: '', suggestions: [], errors: [] }); assert.equal(summary.hidden, true);
  next.status.state = 'succeeded'; next.result.report.complete = false; controller.setContext(next, preview);
  assert.deepEqual(controller.compose().findingIds, [], 'partial reports cannot publish an intermediate finding list');
}));

test('selection, exact body, explicit code mode and deletion survive reload, navigation and source refresh without changing the report', () => withController(({ run, preview, controller, storage }) => {
  const original = structuredClone(run); controller.setContext(run, preview); keepOnly(controller, run, ['inline-p0']);
  const inline = renderFinding(controller, run, 'inline-p0'); edit(inline.inline, 'checked', true);
  edit(inline.body, 'value', '  Edited body.\n\n', 'input'); edit(inline.replacement, 'value', '', 'input');
  assert.match(inline.root.textContent, /empty replacement deletes/);
  assert.equal(controller.persistence(), 'local');
  const reloaded = findingFeedbackController(() => {}, storage); reloaded.setContext(structuredClone(run), structuredClone(preview));
  let row = renderFinding(reloaded, run, 'inline-p0');
  assert.equal(row.selected.checked, true); assert.equal(row.inline.checked, true); assert.equal(row.body.value, '  Edited body.\n\n'); assert.equal(row.replacement.value, '');
  assert.equal(reloaded.compose().suggestions[0].replacement, '', 'an explicitly empty replacement remains a deletion');
  assert.equal(renderFinding(reloaded, run, 'ordinary-p1').selected.checked, false);
  const other = structuredClone(run); other.runId = 'different-run'; reloaded.setContext(other, preview);
  assert.equal(reloaded.compose().suggestions.length, 0);
  reloaded.setContext(run, preview); row = renderFinding(reloaded, run, 'inline-p0'); assert.equal(row.replacement.value, '');
  const changedSource = structuredClone(run); changedSource.result.review.suggestions[0].line = 3;
  reloaded.setContext(changedSource, preview);
  assert.match(reloaded.compose().errors[0], /inline position.*unavailable/i, 'an edited source location does not silently move the saved suggestion');
  assert.deepEqual(run, original);
}));

test('a new target or SHA has independent defaults and returning restores the exact original draft', () => withController(({ run, preview, controller, storage }) => {
  controller.setContext(run, preview); keepOnly(controller, run, ['ordinary-p1']);
  const first = renderFinding(controller, run, 'ordinary-p1'); edit(first.body, 'value', 'Saved for this target and revision.', 'input');
  for (const patch of [{ expectedHeadSha: otherSha }, { target: { type: 'pr', number: 43 } }, { repository: 'owner/another-repo' }]) {
    const next = structuredClone(run); Object.assign(next.task, patch); controller.setContext(next, preview);
    assert.equal(controller.compose().findingIds.length, 6); assert.equal(renderFinding(controller, next, 'ordinary-p1').body.value, markdown);
  }
  const restored = findingFeedbackController(() => {}, storage); restored.setContext(run, preview);
  assert.deepEqual(restored.compose().findingIds, ['ordinary-p1']); assert.equal(restored.compose().body, 'Saved for this target and revision.');
}));

test('unknown replacement is initialized from original lines only on explicit code selection, never interpreted as deletion', () => withController(({ run, preview, controller, storage }) => {
  delete run.result.review.suggestions[0].replacement;
  controller.setContext(run, preview); keepOnly(controller, run, ['inline-p0']);
  const inline = renderFinding(controller, run, 'inline-p0'); assert.equal(inline.replacement.value, ''); assert.equal(inline.replacement.disabled, true);
  assert.deepEqual(controller.compose().suggestions, []);
  edit(inline.inline, 'checked', true);
  assert.equal(inline.replacement.value, 'if (conflict)\n    replaceOriginal();');
  assert.equal(controller.compose().suggestions[0].replacement, inline.replacement.value);
  edit(inline.replacement, 'value', '', 'input'); edit(inline.inline, 'checked', false); edit(inline.inline, 'checked', true);
  assert.equal(inline.replacement.value, '', 'explicit deletion is not replaced by defaults on a mode round trip');
  const reloaded = findingFeedbackController(() => {}, storage); reloaded.setContext(run, preview);
  assert.equal(reloaded.compose().suggestions[0].replacement, '');
}));

test('an unavailable saved inline position survives reload and ordinary-mode navigation does not revalidate it', () => withController(({ run, preview, controller, storage }) => {
  controller.setContext(run, preview); keepOnly(controller, run, ['inline-p0']); const inline = renderFinding(controller, run, 'inline-p0'); edit(inline.inline, 'checked', true);
  edit(inline.replacement, 'value', '  Saved replacement.\n', 'input');
  const stale = structuredClone(preview); stale.headSha = otherSha;
  const reloaded = findingFeedbackController(() => {}, storage); reloaded.setContext(run, stale); const row = renderFinding(reloaded, run, 'inline-p0');
  assert.equal(row.inline.checked, true); assert.equal(row.inline.disabled, false); assert.equal(row.replacement.value, '  Saved replacement.\n');
  assert.equal(reloaded.compose().errors.length, 1); assert.ok(row.root.textContent.includes('if (conflict)\n    replaceOriginal();'), 'saved original lines remain inspectable');
  edit(row.inline, 'checked', false); assert.equal(reloaded.compose().errors.length, 0); assert.equal(row.inline.disabled, true);
  reloaded.setContext(run, stale); assert.equal(row.inline.disabled, true, 'ordinary mode cannot establish a usable code position');
}));

test('empty selected ordinary feedback is blocked instead of silently omitted and unavailable storage preserves session navigation', () => withController(({ run, preview }) => {
  const unavailable = { getItem() { throw Error('blocked'); }, setItem() { throw Error('quota'); }, removeItem() { throw Error('blocked'); } };
  const controller = findingFeedbackController(() => {}, unavailable); controller.setContext(run, preview); keepOnly(controller, run, ['ordinary-p1']);
  const row = renderFinding(controller, run, 'ordinary-p1'); edit(row.body, 'value', ' \n ', 'input');
  assert.match(controller.compose().errors[0], /Enter a comment.*deselect/); assert.equal(controller.persistence(), 'session');
  const next = structuredClone(run); next.runId = 'another-run'; controller.setContext(next, preview); controller.setContext(run, preview);
  assert.equal(renderFinding(controller, run, 'ordinary-p1').body.value, ' \n '); assert.deepEqual(controller.compose().findingIds, ['ordinary-p1']);
}));
