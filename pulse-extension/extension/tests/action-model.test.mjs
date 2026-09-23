import test from 'node:test';
import assert from 'node:assert/strict';
import { actionButtonLabel, actionDescription, actionPresentation, actionStatusMessage, actionStepLabel, canConfirmAction, canReconcileAction, canRetryAction, duplicateCommentBody, initialResultDraft, preparedResultDraftKey, resultDraftKey, resultDraftSourceUrl, safeActionUrl, validOperationId, validResultDraftContent, withActionSummary } from '../src/action-model.ts';

const draft = { kind: 'review', review: { event: 'REQUEST_CHANGES' } };
const preview = { draft, status: 'prepared', account: 'tester', canSubmit: true, blockers: [] };

test('only a Host-cleared result proposal offers an explicit fresh confirmation', () => {
  const operation = { runId: 'saved-run', proposalId: 'saved-proposal', retryAllowed: true, kind: 'merge-pr', status: 'cancelled' };
  assert.equal(canRetryAction(operation, false), true);
  assert.equal(canRetryAction({ ...operation, status: 'failed' }, false), true);
  assert.equal(canRetryAction({ ...operation, status: 'succeeded', kind: 'trigger-ci' }, false), true);
  for (const status of ['prepared', 'submitting', 'unknown', 'partial', 'succeeded']) assert.equal(canRetryAction({ ...operation, status }, false), false, status);
  for (const field of ['runId', 'proposalId', 'retryAllowed']) assert.equal(canRetryAction({ ...operation, [field]: undefined }, false), false);
  assert.equal(canRetryAction(operation, true), false);
});

test('confirmation requires a current prepared action, actual account, no blockers, and no prior submission attempt', () => {
  assert.equal(canConfirmAction(preview, false, false), true);
  for (const value of [undefined, { ...preview, account: null }, { ...preview, account: '  ' }, { ...preview, canSubmit: false }, { ...preview, blockers: [{ code: 'STALE_HEAD', message: 'Head changed' }] }]) assert.equal(canConfirmAction(value, false, false), false);
  for (const status of ['submitting', 'succeeded', 'partial', 'unknown', 'failed', 'cancelled']) assert.equal(canConfirmAction({ ...preview, status }, false, false), false);
  assert.equal(canConfirmAction(preview, true, false), false);
  assert.equal(canConfirmAction(preview, false, true), false);
});

test('confirmation names the exact GitHub action and fixed command or merge method', () => {
  assert.equal(actionButtonLabel(draft), 'Request changes');
  assert.equal(actionButtonLabel({ kind: 'review', review: { event: 'COMMENT' } }), 'Submit review');
  assert.equal(actionButtonLabel({ kind: 'review', review: { event: 'COMMENT', generalComments: [{ body: 'General finding' }] } }), 'Post review comments');
  assert.equal(actionButtonLabel({ kind: 'approve' }), 'Approve pull request');
  assert.equal(actionButtonLabel({ kind: 'comment', assignSelf: true }), 'Post comment and assign to me');
  assert.equal(actionButtonLabel({ kind: 'comment' }), 'Post comment');
  assert.equal(actionButtonLabel({ kind: 'merge-pr' }), 'Merge pull request');
  assert.equal(actionButtonLabel({ kind: 'trigger-ci' }), 'Trigger CI');
  assert.equal(actionButtonLabel({ kind: 'create-pr', pullRequest: { draft: true } }), 'Create Draft PR');
  assert.match(actionDescription({ kind: 'merge-pr' }), /Squash and merge/);
  assert.match(actionDescription({ kind: 'trigger-ci' }), /\/azp run/);
  assert.equal(actionStepLabel('assign-self'), 'Assign to the confirmed GitHub account');
  assert.equal(actionStepLabel('general-comment-2'), 'Post general comment 2');
});

test('uncertain and partial responses do not claim failure or invite blind retry', () => {
  assert.match(actionStatusMessage('unknown'), /may have accepted/);
  assert.match(actionStatusMessage('partial'), /Some steps completed/);
  assert.match(actionStatusMessage('prepared', true), /outcome has not been confirmed/);
  assert.match(actionStatusMessage('submitting'), /Do not submit another copy/);
});

test('only unknown actions offer a read-only result check and no reconciliation outcome enables submission', () => {
  const unknown = { ...preview, status: 'unknown' };
  assert.equal(canReconcileAction(unknown, false), true);
  assert.equal(canReconcileAction(unknown, true), false);
  assert.equal(canReconcileAction(undefined, false), false);
  for (const status of ['prepared', 'submitting', 'succeeded', 'partial', 'failed', 'cancelled']) assert.equal(canReconcileAction({ ...preview, status }, false), false);
  for (const status of ['unknown', 'succeeded', 'partial']) assert.equal(canConfirmAction({ ...preview, status }, false, false), false);
});

test('a confirmed reconciliation clears the old uncertainty error while preserving the reviewed draft', () => {
  const previous = { ...preview, operationId: 'operation-1', status: 'unknown', error: { code: 'OPERATION_UNKNOWN', message: 'Response lost' } };
  const result = withActionSummary(previous, { operationId: 'operation-1', status: 'succeeded', completedSteps: ['comment'], remainingSteps: [] });
  assert.equal(result.status, 'succeeded'); assert.equal(result.error, undefined); assert.equal(result.canSubmit, false);
  assert.equal(result.draft, previous.draft); assert.equal(result.account, previous.account);
  assert.throws(() => withActionSummary(previous, { operationId: 'other-action', status: 'succeeded' }), /different action/);
});

test('operation URL parameters accept only UUIDs', () => {
  assert.equal(validOperationId('11111111-1111-4111-8111-111111111111'), true);
  for (const id of [null, '', '../popup.html', 'javascript:alert(1)', '11111111-1111-4111-8111-111111111111&x=1']) assert.equal(validOperationId(id), false);
});

test('result links stay on the intended GitHub repository without credentials or alternate origins', () => {
  const repository = 'microsoft/PowerToys';
  assert.equal(safeActionUrl('https://github.com/microsoft/PowerToys/pull/1#issuecomment-1', repository), 'https://github.com/microsoft/PowerToys/pull/1#issuecomment-1');
  assert.equal(safeActionUrl('https://github.com/MICROSOFT/POWERTOYS/pull/1', repository), 'https://github.com/MICROSOFT/POWERTOYS/pull/1');
  for (const url of ['javascript:alert(1)', 'http://github.com/microsoft/PowerToys/pull/1', 'https://evil.example/microsoft/PowerToys/pull/1', 'https://github.com.evil.example/microsoft/PowerToys/pull/1', 'https://user:password@github.com/microsoft/PowerToys/pull/1', 'https://github.com:444/microsoft/PowerToys/pull/1', 'https://github.com/other/repo/pull/1', 'https://github.com/microsoft/PowerToys-other/pull/1', 'https://github.com/microsoft/PowerToys/../../other/repo', '/microsoft/PowerToys/pull/1']) assert.equal(safeActionUrl(url, repository), undefined);
  assert.equal(safeActionUrl('https://github.com/microsoft/PowerToys/pull/1', '../microsoft/PowerToys'), undefined);
});

test('only an explicitly resumable duplicate can continue after a confirmed partial result', () => {
  const duplicate = { ...preview, kind: 'close-as-duplicate', draft: { kind: 'close-as-duplicate' }, status: 'partial', resumeRequired: true };
  assert.equal(canConfirmAction(duplicate, false, true), true, 'a fresh Host preview authorizes only the remaining step');
  for (const changed of [{ status: 'unknown' }, { resumeRequired: false }, { kind: 'comment' }, { canSubmit: false }, { blockers: [{ code: 'TARGET_CLOSED', message: 'Already closed' }] }]) assert.equal(canConfirmAction({ ...duplicate, ...changed }, false, true), false);
  assert.equal(canConfirmAction(duplicate, true, true), false);
  assert.equal(canConfirmAction(withActionSummary({ ...duplicate, operationId: 'same' }, { operationId: 'same', status: 'partial', resumeRequired: true }), false, true), false, 'a summary alone is not a fresh confirmation preview');
  assert.equal(canRetryAction({ ...duplicate, runId: 'run', proposalId: 'proposal', retryAllowed: true }, false), false, 'partial duplicates never start a replacement operation');
});

test('duplicate rationale edits preserve whitespace and the immutable original-Issue suffix; confirmed text is frozen', () => {
  const duplicate = { ...preview, kind: 'close-as-duplicate', draft: { kind: 'close-as-duplicate', body: 'Original rationale' }, bodyEditable: true, duplicateSuffix: '\n\nDuplicate of https://github.com/microsoft/PowerToys/issues/99\n<!-- saved-marker -->' };
  const edit = '  **Same trigger**\n\nExact Markdown and trailing spaces.  \n';
  assert.equal(duplicateCommentBody(duplicate, edit), edit + duplicate.duplicateSuffix);
  assert.equal(duplicateCommentBody({ ...duplicate, bodyEditable: false, commentBody: 'Frozen complete comment' }, 'replacement'), 'Frozen complete comment');
  assert.equal(actionButtonLabel(duplicate.draft), 'Comment and close as duplicate');
  assert.match(actionDescription(duplicate.draft), /original Issue link/);
  assert.equal(actionStepLabel('duplicate-comment'), 'Post explanation with the original Issue link');
  assert.equal(actionStepLabel('close-issue'), 'Close Issue as duplicate');
});

test('all action outcomes retain a conclusion and explicit recommendation without uncertain retries', () => {
  for (const status of ['prepared', 'submitting', 'succeeded', 'failed', 'cancelled', 'partial', 'unknown']) {
    const shown = actionPresentation({ ...preview, status });
    assert.ok(shown.conclusion && shown.nextStep && shown.reason, status);
    if (['succeeded', 'failed', 'cancelled', 'partial'].includes(status)) assert.equal(shown.nextStep, 'No recommended action');
  }
  assert.equal(actionPresentation({ ...preview, status: 'unknown', retryAllowed: true, runId: 'run', proposalId: 'p' }).nextStep, 'Check GitHub result');
  assert.equal(actionPresentation(preview, true).nextStep, 'Refresh the action status');
  assert.equal(actionPresentation({ ...preview, blockers: [{ message: 'Source unavailable' }] }).nextStep, 'Resolve the requirements below');
  assert.equal(actionPresentation({ ...preview, account: null }).conclusion, 'This action needs attention');
  assert.equal(actionPresentation({ ...preview, kind: 'create-pr', draft: { kind: 'create-pr', pullRequest: { draft: true } }, status: 'succeeded' }).conclusion, 'Draft PR created');
});

test('editable Draft PR storage is bound to target, proposal and exact candidate and matches prepared return', () => {
  const run = { runId: 'run-1', task: { repository: 'Microsoft/PowerToys', target: { type: 'issue', number: 7 } } };
  const action = { kind: 'create-pr', proposalId: 'proposal-1', pullRequest: { head: 'owner:fix', base: 'main', sourceHeadSha: 'a'.repeat(40), title: 'Fix focus', body: '  Keep spacing.  \n' } };
  const first = resultDraftKey(run, action);
  assert.equal(first, resultDraftKey({ ...run, task: { ...run.task, repository: 'microsoft/powertoys' } }, action));
  assert.equal(first, resultDraftKey(run, { ...action, pullRequest: { ...action.pullRequest, head: 'OWNER:fix' } }));
  assert.notEqual(first, resultDraftKey(run, { ...action, pullRequest: { ...action.pullRequest, head: 'owner:Fix' } }));
  assert.notEqual(first, resultDraftKey({ ...run, runId: 'run-2' }, action));
  assert.notEqual(first, resultDraftKey(run, { ...action, proposalId: 'proposal-2' }));
  assert.notEqual(first, resultDraftKey(run, { ...action, pullRequest: { ...action.pullRequest, sourceHeadSha: 'b'.repeat(40) } }));
  assert.notEqual(first, resultDraftKey(run, { ...action, pullRequest: { ...action.pullRequest, base: 'release' } }));
  const prepared = { kind: 'create-pr', runId: run.runId, proposalId: action.proposalId, target: { ...run.task.target, repository: run.task.repository }, draft: { pullRequest: action.pullRequest } };
  assert.equal(preparedResultDraftKey(prepared), first);
  assert.equal(preparedResultDraftKey({ ...prepared, runId: undefined }), undefined);
  const content = initialResultDraft(run, action);
  assert.equal(content.title, 'Fix focus');
  assert.equal(content.body, '  Keep spacing.  \n\n\nRefs https://github.com/Microsoft/PowerToys/issues/7');
  assert.equal(initialResultDraft(run, { ...action, pullRequest: { ...action.pullRequest, body: content.body } }).body, content.body);
  assert.equal(validResultDraftContent({ title: '', body: '' }), true, 'intentionally empty edits remain restorable');
  assert.equal(validResultDraftContent({ title: 'x'.repeat(257), body: '' }), false);
  assert.equal(validResultDraftContent({ title: 'Title', body: null }), false);
  assert.equal(resultDraftSourceUrl('run&1', 'proposal?2'), 'action.html?runId=run%261&proposalId=proposal%3F2');
});
