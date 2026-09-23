import test from 'node:test';
import assert from 'node:assert/strict';
import { suggestionOriginal, validateDraft, allowedOperation, matchingReview, operationKinds } from '../src/review.ts';

const preview = { target: { type: 'pr', number: 1 }, account: 'tester', stale: false, canApprove: true, canComment: true, canClose: true, canRequestChanges: true, canSuggestChanges: true, files: [{ path: 'src/file.ts', lines: [{ line: 3, hunk: 1, original: 'before', kind: 'context' }, { line: 4, hunk: 1, original: 'old', kind: 'add' }, { line: 5, hunk: 2, original: 'other', kind: 'add' }] }] };
const suggestion = { path: 'src/file.ts', line: 4, side: 'RIGHT', body: 'explanation', replacement: 'new' };
test('suggestions must point to valid RIGHT lines within one diff hunk', () => {
  assert.equal(suggestionOriginal(preview, suggestion), 'old');
  assert.equal(suggestionOriginal(preview, { ...suggestion, startLine: 3 }), 'before\nold');
  for (const invalid of [{ ...suggestion, side: 'LEFT' }, { ...suggestion, line: 100 }, { ...suggestion, startLine: 4, line: 5 }, { ...suggestion, path: 'missing' }, { ...suggestion, startLine: 6 }]) assert.equal(suggestionOriginal(preview, invalid), undefined);
});
test('inline preview accepts the Host maximum 1000-line range and 1024-character path without accepting larger or unsafe locations', () => {
  const path = 'a'.repeat(1024);
  const current = { ...preview, files: [{ path, lines: Array.from({ length: 1001 }, (_, index) => ({ line: index + 1, hunk: 0, original: `line ${index + 1}`, kind: 'add' })) }] };
  const maximum = { ...suggestion, path, startLine: 1, line: 1000 };
  assert.equal(suggestionOriginal(current, maximum)?.split('\n').length, 1000);
  assert.equal(validateDraft(current, 'suggestChanges', 'Review', [maximum]), undefined);
  assert.equal(suggestionOriginal(current, { ...maximum, line: 1001 }), undefined);
  for (const invalid of [' ', 'a'.repeat(1025), '/absolute.cs', '../parent.cs', 'src/../file.cs', 'src\\file.cs', 'src//file.cs', 'src/\u0001file.cs']) {
    const invalidPreview = { ...current, files: [{ ...current.files[0], path: invalid }] };
    assert.equal(suggestionOriginal(invalidPreview, { ...maximum, path: invalid }), undefined);
  }
});
test('no SHA, account, permission or comment-type downgrade is inferred', () => {
  for (const denied of [{ ...preview, stale: true }, { ...preview, account: null }, { ...preview, canApprove: false }]) assert.equal(allowedOperation(denied, 'approve'), false);
  assert.equal(validateDraft(preview, 'suggestChanges', 'review', [suggestion]), undefined);
  assert.equal(validateDraft(preview, 'requestChanges', 'review', [suggestion]), undefined);
  assert.equal(validateDraft(preview, 'comment', 'review', []), undefined);
  assert.equal(validateDraft(preview, 'comment', 'review', [suggestion]), undefined);
  assert.ok(validateDraft(preview, 'suggestChanges', 'review', []));
  assert.ok(validateDraft(preview, 'requestChanges', '', []));
  assert.ok(validateDraft(preview, 'suggestChanges', 'review', [{ ...suggestion, replacement: '```suggestion\ninjected' }]));
  assert.equal(validateDraft(preview, 'close', '', []), undefined);
});

test('inline-only comments and reviews keep their chosen decision while real permissions and diff positions remain required', () => {
  const inline = { ...suggestion, body: '  Exact inline explanation.\n', replacement: '  Replacement();\n' };
  const saved = structuredClone(inline);
  for (const kind of ['comment', 'approve', 'suggestChanges', 'requestChanges']) {
    assert.equal(validateDraft(preview, kind, '', [inline]), undefined, kind);
    assert.ok(validateDraft({ ...preview, stale: true }, kind, '', [inline]), `${kind}/stale`);
    assert.match(validateDraft(preview, kind, '', [{ ...inline, line: 100 }]), /location/, `${kind}/invalid location`);
  }
  assert.ok(validateDraft({ ...preview, canSuggestChanges: false }, 'comment', '', [inline]));
  assert.ok(validateDraft({ ...preview, canApprove: false }, 'approve', '', [inline]));
  assert.ok(validateDraft(preview, 'comment', '', []), 'an empty ordinary comment still has no content');
  assert.ok(validateDraft(preview, 'close', '', [inline]), 'closing cannot silently drop selected inline feedback');
  assert.deepEqual(inline, saved, 'validation preserves exact selected Markdown and replacement text');
});
test('runtime review drafts only supply suggestions for the same task, reviewed and current PR SHA', () => {
  const sha = 'a'.repeat(40);
  const result = { review: { headSha: sha, body: 'verified review draft', suggestions: [suggestion] } };
  const current = { ...preview, expectedHeadSha: sha, headSha: sha };
  assert.deepEqual(matchingReview(result, sha, current)?.suggestions, [suggestion]);
  assert.equal(matchingReview(result, sha.toUpperCase(), current)?.body, 'verified review draft');
  assert.equal(matchingReview({ review: null }, sha, current), undefined);
  assert.equal(matchingReview(result, undefined, current), undefined);
  assert.equal(matchingReview(result, 'b'.repeat(40), current), undefined);
  assert.equal(matchingReview(result, sha, { ...current, expectedHeadSha: 'b'.repeat(40) }), undefined);
  assert.equal(matchingReview(result, sha, { ...current, headSha: 'b'.repeat(40) }), undefined);
  assert.equal(matchingReview(result, sha, { ...current, stale: true }), undefined);
  assert.equal(matchingReview(result, sha, { ...current, expectedHeadSha: undefined }), undefined);
  assert.equal(matchingReview(result, sha, { ...current, target: { type: 'issue', number: 1 } }), undefined);
});
test('issues only offer comments and closing even when PR capability flags are present', () => {
  const issue = { ...preview, target: { type: 'issue', number: 1 } };
  assert.deepEqual(operationKinds('issue'), ['comment', 'close']);
  assert.deepEqual(operationKinds('pr'), ['comment', 'approve', 'suggestChanges', 'requestChanges', 'close']);
  for (const kind of ['approve', 'requestChanges', 'suggestChanges']) {
    assert.equal(allowedOperation(issue, kind), false);
    assert.ok(validateDraft(issue, kind, 'draft', []));
  }
  assert.equal(validateDraft(issue, 'comment', 'The fix is ready.', []), undefined);
  assert.equal(validateDraft(issue, 'close', '', []), undefined);
});
