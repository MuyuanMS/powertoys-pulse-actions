import type { OperationKind, OperationPreview, Result, Suggestion } from './types.js';

export const operationLabels: Record<OperationKind, string> = {
  approve: 'Approve PR', requestChanges: 'Request changes', suggestChanges: 'Comment with code suggestions', comment: 'Post comment', close: 'Close target',
};
export function operationKinds(target: 'pr' | 'issue'): OperationKind[] {
  return target === 'pr' ? ['comment', 'approve', 'suggestChanges', 'requestChanges', 'close'] : ['comment', 'close'];
}
export function allowedOperation(preview: OperationPreview, kind: OperationKind): boolean {
  if (!operationKinds(preview.target.type).includes(kind) || !preview.account || preview.stale) return false;
  return { approve: preview.canApprove, requestChanges: preview.canRequestChanges, suggestChanges: preview.canSuggestChanges, comment: preview.canComment, close: preview.canClose }[kind];
}
export function matchingReview(result: Result | undefined, taskHeadSha: string | undefined, preview: OperationPreview): NonNullable<Result['review']> | undefined {
  const review = result?.review;
  if (!review || preview.stale || preview.target.type !== 'pr') return undefined;
  const shas = [review.headSha, taskHeadSha, preview.expectedHeadSha, preview.headSha];
  if (shas.some(sha => typeof sha !== 'string' || !/^[a-fA-F0-9]{40}$/.test(sha))) return undefined;
  const expected = review.headSha.toLowerCase();
  return shas.every(sha => sha!.toLowerCase() === expected) ? review : undefined;
}
export function suggestionOriginal(preview: OperationPreview, suggestion: Suggestion): string | undefined {
  if (typeof suggestion.path !== 'string' || !suggestion.path.trim() || suggestion.path.length > 1024 || suggestion.path.startsWith('/') || suggestion.path.includes('\\') || suggestion.path.split('/').some(part => !part || part === '.' || part === '..') || /[\u0000-\u001f\u007f-\u009f]/.test(suggestion.path)) return undefined;
  const file = preview.files.find(file => file.path === suggestion.path);
  if (!file || suggestion.side !== 'RIGHT' || !Number.isSafeInteger(suggestion.line) || suggestion.line < 1 || suggestion.line > 2147483647) return undefined;
  const start = suggestion.startLine ?? suggestion.line;
  if (!Number.isSafeInteger(start) || start < 1 || start > suggestion.line || suggestion.line - start > 999) return undefined;
  const selected = [];
  for (let number = start; number <= suggestion.line; number++) {
    const line = file.lines.find(line => line.line === number);
    if (!line || (selected.length && selected[0]?.hunk !== line.hunk)) return undefined;
    selected.push(line);
  }
  return selected.map(line => line.original).join('\n');
}
export function validateDraft(preview: OperationPreview, kind: OperationKind, body: string, suggestions: Suggestion[]): string | undefined {
  if (!allowedOperation(preview, kind)) return 'This action is unavailable for the current account, target, or SHA. Reload the target and permissions.';
  if (!['approve', 'close'].includes(kind) && !body.trim() && !suggestions.length) return 'Enter the text to submit or select an inline suggestion.';
  if (body.length > 60000) return 'The body exceeds 60,000 characters.';
  if (kind === 'suggestChanges' && !suggestions.length) return 'Select at least one valid inline suggestion.';
  if (!['comment', 'approve', 'suggestChanges', 'requestChanges'].includes(kind) && suggestions.length) return 'This action does not accept inline suggestions.';
  if (kind === 'comment' && suggestions.length && !preview.canSuggestChanges) return 'Inline comments are unavailable for the current target or diff.';
  if (suggestions.length > 100) return 'Submit at most 100 suggestions at a time.';
  for (const suggestion of suggestions) {
    if (suggestionOriginal(preview, suggestion) === undefined) return `The suggestion location is no longer valid: ${suggestion.path}:${suggestion.line}. Run the analysis again to create a valid suggestion.`;
    if (suggestion.replacement.includes('```')) return 'Replacement text cannot contain triple backticks. Edit the suggestion or select Post comment.';
  }
  return undefined;
}
