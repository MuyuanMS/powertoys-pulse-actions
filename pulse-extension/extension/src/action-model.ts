import type { ResultNextAction, Run, WebActionDraft, WebActionPreview, WebActionSummary } from './types.js';

export function validOperationId(value: string | null): value is string {
  return value !== null && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

export function actionButtonLabel(draft: WebActionDraft): string {
  switch (draft.kind) {
    case 'comment': return draft.assignSelf ? 'Post comment and assign to me' : 'Post comment';
    case 'review': return draft.review?.event === 'REQUEST_CHANGES' ? 'Request changes' : generalCommentsOnly(draft) ? 'Post review comments' : 'Submit review';
    case 'approve': return 'Approve pull request';
    case 'trigger-ci': return 'Trigger CI';
    case 'merge-pr': return 'Merge pull request';
    case 'create-pr': return draft.pullRequest?.draft ? 'Create Draft PR' : 'Create pull request';
    case 'close-as-duplicate': return 'Comment and close as duplicate';
  }
}

export function actionDescription(draft: WebActionDraft): string {
  switch (draft.kind) {
    case 'comment': return draft.assignSelf ? 'Post this comment, then assign the issue to the GitHub account shown below.' : 'Post this comment to the GitHub target shown below.';
    case 'review': return draft.review?.event === 'REQUEST_CHANGES' ? 'Submit a review requesting changes with the selected comments below.' : generalCommentsOnly(draft) ? 'Post the selected general review comments to this pull request.' : 'Submit a review with the selected comments below.';
    case 'approve': return 'Submit an approving review for the pull request at the reviewed head commit.';
    case 'trigger-ci': return 'Post /azp run to request a CI run for this pull request.';
    case 'merge-pr': return 'Squash and merge this pull request at the reviewed head commit.';
    case 'create-pr': return 'Open a pull request between the existing GitHub branches shown below.';
    case 'close-as-duplicate': return 'Post the explanation and original Issue link, then close this Issue as a duplicate.';
  }
}

function generalCommentsOnly(draft: WebActionDraft): boolean {
  return !draft.body?.trim() && !draft.review?.comments?.length && Boolean(draft.review?.generalComments?.length);
}

export function actionStepLabel(step: string): string {
  const labels: Record<string, string> = { review: 'Submit review', comment: 'Post comment', 'assign-self': 'Assign to the confirmed GitHub account', 'merge-pr': 'Squash and merge pull request', 'trigger-ci': 'Post /azp run', 'create-pr': 'Create pull request', 'duplicate-comment': 'Post explanation with the original Issue link', 'close-issue': 'Close Issue as duplicate' };
  const general = /^general-comment-(\d+)$/.exec(step);
  return general ? `Post general comment ${general[1]}` : labels[step] ?? step;
}

export function canConfirmAction(preview: WebActionPreview | undefined, busy: boolean, attempted: boolean): boolean {
  return Boolean(preview && !busy && (preview.status === 'prepared' && !attempted || preview.kind === 'close-as-duplicate' && preview.status === 'partial' && preview.resumeRequired === true) && preview.canSubmit && preview.account?.trim() && preview.blockers.length === 0);
}

export function duplicateCommentBody(preview: WebActionPreview, rationale?: string): string {
  if (preview.kind !== 'close-as-duplicate') return preview.draft.body ?? '';
  if (preview.bodyEditable && typeof preview.duplicateSuffix === 'string') return (rationale ?? preview.draft.body ?? '') + preview.duplicateSuffix;
  return preview.commentBody ?? preview.draft.body ?? '';
}

export function canReconcileAction(preview: WebActionSummary | undefined, busy: boolean): boolean {
  return !busy && preview?.status === 'unknown';
}

export function canRetryAction(preview: WebActionSummary | undefined, busy: boolean): boolean {
  return Boolean(!busy && preview?.retryAllowed === true && preview.runId && preview.proposalId &&
    (preview.status === 'cancelled' || preview.status === 'failed' || preview.status === 'succeeded' && preview.kind === 'trigger-ci'));
}

export function withActionSummary(preview: WebActionPreview, summary: WebActionSummary): WebActionPreview {
  if (summary.operationId !== preview.operationId) throw new Error('The Host returned a different action status.');
  return { ...preview, ...summary, error: summary.error, canSubmit: false };
}

export function actionStatusMessage(status: WebActionSummary['status'], attempted = false): string {
  switch (status) {
    case 'prepared': return attempted
      ? 'A submission was requested in this tab. Its outcome has not been confirmed. Refresh the status and inspect GitHub before preparing another action.'
      : 'Review the account, target, and complete content before confirming.';
    case 'submitting': return 'Submission is in progress. Refresh the status to check its outcome. Do not submit another copy.';
    case 'succeeded': return 'The action completed. You can inspect the result on GitHub.';
    case 'cancelled': return 'This action was cancelled before submission.';
    case 'partial': return 'Some steps completed. Review the recorded results and GitHub before preparing another action.';
    case 'unknown': return 'GitHub may have accepted some or all of this action. Use “Check GitHub result” to look for confirmation, and inspect GitHub before preparing another action.';
    case 'failed': return 'The action did not complete. Review the recorded details and GitHub before preparing another action.';
  }
}

export function safeActionUrl(value: string | undefined, repository: string): string | undefined {
  if (!value || !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) return undefined;
  try {
    const url = new URL(value);
    if (url.protocol !== 'https:' || url.hostname !== 'github.com' || url.port || url.username || url.password) return undefined;
    if (!url.pathname.toLowerCase().startsWith(`/${repository.toLowerCase()}/`)) return undefined;
    return url.href;
  } catch { return undefined; }
}

export interface ActionPresentation { conclusion: string; nextStep: string; reason: string }

/** Keep the same conclusion and next-step hierarchy through confirmation and recovery. */
export function actionPresentation(value: WebActionPreview, attempted = false): ActionPresentation {
  if (value.status === 'prepared') {
    if (attempted) return { conclusion: 'Submission outcome is not confirmed', nextStep: 'Refresh the action status', reason: actionStatusMessage(value.status, true) };
    if (value.blockers.length || !value.canSubmit || !value.account?.trim()) return { conclusion: 'This action needs attention', nextStep: 'Resolve the requirements below', reason: 'The prepared content is retained. Check the account, target, and reported requirements before confirming.' };
    return { conclusion: 'Ready for your confirmation', nextStep: actionButtonLabel(value.draft), reason: 'Review the complete content and GitHub account, then explicitly confirm the action.' };
  }
  if (value.status === 'unknown') return { conclusion: 'The GitHub outcome is unknown', nextStep: 'Check GitHub result', reason: actionStatusMessage('unknown') };
  if (value.status === 'submitting') return { conclusion: 'The action is being submitted', nextStep: 'Refresh the action status', reason: actionStatusMessage('submitting') };
  if (value.status === 'partial' && value.kind === 'close-as-duplicate' && value.resumeRequired) return { conclusion: 'The explanation was posted; the Issue is still open', nextStep: 'Continue closing as duplicate', reason: 'The confirmed comment is retained. Continuing submits only the remaining close step.' };
  if (value.status === 'succeeded') return { conclusion: value.kind === 'create-pr' && value.draft.pullRequest?.draft ? 'Draft PR created' : 'The GitHub action completed', nextStep: 'No recommended action', reason: 'The recorded action completed. Its GitHub result and step receipts are available below.' };
  if (canRetryAction(value, false)) return { conclusion: value.status === 'cancelled' ? 'The action was cancelled' : 'The action did not complete', nextStep: 'Review a new confirmation', reason: 'The Host has cleared this action for a new preparation. Review the existing receipts before starting another attempt.' };
  return { conclusion: value.status === 'partial' ? 'Only part of the action completed' : value.status === 'cancelled' ? 'The action was cancelled' : 'The action did not complete', nextStep: 'No recommended action', reason: actionStatusMessage(value.status) };
}

export interface ResultDraftContent { title: string; body: string }

function draftHeadIdentity(head: string | undefined): string | undefined {
  if (!head?.includes(':')) return head;
  const separator = head.indexOf(':');
  return head.slice(0, separator).toLowerCase() + head.slice(separator);
}

export function resultDraftKey(run: Run, action: ResultNextAction): string {
  return `pulse-result-draft:v1:${JSON.stringify([run.runId, run.task.repository.toLowerCase(), run.task.target?.type, run.task.target?.number, action.proposalId, draftHeadIdentity(action.pullRequest?.head), action.pullRequest?.base, action.pullRequest?.sourceHeadSha?.toLowerCase()])}`;
}

export function preparedResultDraftKey(value: WebActionPreview): string | undefined {
  if (!value.runId || !value.proposalId || value.kind !== 'create-pr') return undefined;
  return `pulse-result-draft:v1:${JSON.stringify([value.runId, value.target.repository.toLowerCase(), value.target.type, value.target.number, value.proposalId, draftHeadIdentity(value.draft.pullRequest?.head), value.draft.pullRequest?.base, value.draft.pullRequest?.sourceHeadSha?.toLowerCase()])}`;
}

export function validResultDraftContent(value: unknown): value is ResultDraftContent {
  if (!value || typeof value !== 'object') return false;
  const content = value as ResultDraftContent;
  return typeof content.title === 'string' && content.title.length <= 256 && typeof content.body === 'string' && content.body.length <= 60000;
}

export function initialResultDraft(run: Run, action: ResultNextAction): ResultDraftContent {
  const reference = run.task.target?.type === 'issue' ? `https://github.com/${run.task.repository}/issues/${run.task.target.number}` : '';
  const body = action.pullRequest?.body ?? '';
  return { title: action.pullRequest?.title ?? '', body: reference && !body.includes(reference) ? `${body}${body ? '\n\n' : ''}Refs ${reference}` : body };
}

export function resultDraftSourceUrl(runId: string | undefined, proposalId: string | undefined): string | undefined {
  return runId && proposalId ? `action.html?${new URLSearchParams({ runId, proposalId })}` : undefined;
}
