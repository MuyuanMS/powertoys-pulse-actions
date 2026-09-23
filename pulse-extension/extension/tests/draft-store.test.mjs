import test from 'node:test';
import assert from 'node:assert/strict';
import { deleteDraft, readDraft, resultDraftScope, writeDraft } from '../src/draft-store.ts';

class Storage {
  values = new Map();
  getItem(key) { return this.values.get(key) ?? null; }
  setItem(key, value) { this.values.set(key, value); }
  removeItem(key) { this.values.delete(key); }
}
const run = { runId: 'run-1', task: { repository: 'microsoft/PowerToys', target: { type: 'pr', number: 42 }, expectedHeadSha: 'A'.repeat(40) } };

test('run, repository, target, SHA and editor purpose isolate drafts while identity casing remains stable', () => {
  const scope = resultDraftScope(run, 'recommended-comment');
  const casing = structuredClone(run); casing.task.repository = casing.task.repository.toLowerCase(); casing.task.expectedHeadSha = casing.task.expectedHeadSha.toLowerCase();
  assert.equal(resultDraftScope(casing, 'recommended-comment'), scope);
  const storage = new Storage(); const draft = { body: '\nPreserve **Markdown**.  \n', replacement: '', selected: false };
  assert.equal(writeDraft(scope, draft, storage), true); assert.deepEqual(readDraft(scope, storage), draft);
  const manual = resultDraftScope(run, 'manual-comment'); assert.equal(readDraft(manual, storage), undefined, 'independent manual comments do not copy the recommendation');
  writeDraft(manual, { body: '' }, storage); assert.deepEqual(readDraft(scope, storage), draft);
  for (const next of [{ ...run, runId: 'run-2' }, { ...run, task: { ...run.task, repository: 'owner/elsewhere' } },
    { ...run, task: { ...run.task, target: { type: 'issue', number: 42 } } }, { ...run, task: { ...run.task, target: { type: 'pr', number: 43 } } },
    { ...run, task: { ...run.task, expectedHeadSha: 'b'.repeat(40) } }]) assert.equal(readDraft(resultDraftScope(next, 'recommended-comment'), storage), undefined);
  assert.equal(deleteDraft(manual, storage), true); assert.equal(readDraft(manual, storage), undefined); assert.deepEqual(readDraft(scope, storage), draft);
});

test('corrupt, foreign-version, missing, or unavailable storage never throws or supplies a draft', () => {
  const storage = new Storage(); const scope = resultDraftScope(run, 'manual-comment');
  assert.equal(readDraft(scope, storage), undefined); writeDraft(scope, { body: 'valid' }, storage);
  const [key] = storage.values.keys();
  for (const value of ['{', 'null', '[]', '{}', '{"version":2,"data":{"body":"old"}}']) { storage.setItem(key, value); assert.equal(readDraft(scope, storage), undefined); }
  const unavailable = { getItem() { throw Error('blocked'); }, setItem() { throw Error('quota'); }, removeItem() { throw Error('blocked'); } };
  assert.equal(readDraft(scope, unavailable), undefined); assert.equal(writeDraft(scope, { body: 'kept in editor' }, unavailable), false); assert.equal(deleteDraft(scope, unavailable), false);
});
