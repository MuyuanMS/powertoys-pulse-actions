import type { Task, Run, TaskEvent, Capabilities, PulseError, ActionReadiness, TargetSnapshot, WebActionDraft, WebActionSummary, Agent, AgentDefaults, TaskExecution, ObservedExecution, ResultOutcome, ResultPhase } from './types.js';

export const reasoningEfforts: Record<Agent, readonly string[]> = {
  codex: ['minimal', 'low', 'medium', 'high', 'xhigh', 'ultra'],
  copilot: ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'],
};

export const reviewModes = ['static', 'build-tests', 'ui-e2e'] as const;
export const publicWorkflowKinds = ['issue-fix', 'pr-review', 'reproduction-setup', 'e2e', 'feature-research', 'bug-investigation'] as const;
const issueResearchKinds = ['feature-research', 'bug-investigation'] as const;
function publicWorkflowCapabilities(data: { workflowKinds?: unknown; resultSchemaVersions?: unknown }): { workflowKinds?: typeof publicWorkflowKinds[number][]; resultSchemaVersions?: (1 | 2 | 3)[] } {
  const kinds = data.workflowKinds ?? undefined;
  const versions = data.resultSchemaVersions ?? undefined;
  if (kinds !== undefined && (!Array.isArray(kinds) || kinds.length > publicWorkflowKinds.length || kinds.some(kind => !(publicWorkflowKinds as readonly unknown[]).includes(kind)) || new Set(kinds).size !== kinds.length)) throw new ProtocolError('INVALID_RESPONSE', 'The Host returned invalid workflow capabilities.');
  if (versions !== undefined && (!Array.isArray(versions) || versions.length > 3 || versions.some(version => ![1, 2, 3].includes(version)) || new Set(versions).size !== versions.length)) throw new ProtocolError('INVALID_RESPONSE', 'The Host returned invalid result schema capabilities.');
  return { ...(kinds === undefined ? {} : { workflowKinds: [...kinds] as typeof publicWorkflowKinds[number][] }), ...(versions === undefined ? {} : { resultSchemaVersions: [...versions] as (1 | 2 | 3)[] }) };
}
export function isIssueResearch(kind: unknown): boolean { return (issueResearchKinds as readonly unknown[]).includes(kind); }
export function requireWorkflowCapabilities(data: { workflowKinds?: unknown; resultSchemaVersions?: unknown }, kind: unknown): void {
  if (!isIssueResearch(kind)) return;
  const capabilities = publicWorkflowCapabilities(data);
  if (!capabilities.workflowKinds?.includes(kind as typeof publicWorkflowKinds[number]) || !capabilities.resultSchemaVersions?.includes(3)) throw new ProtocolError('HOST_UPDATE_REQUIRED', 'Update Pulse Extension and its local Host to research this issue.', 'This workflow requires matching Issue research and v3 result support. Update both components, then check again.');
}
type PublicReviewMode = typeof reviewModes[number];
export function validateReviewOptions(value: unknown): { mode: PublicReviewMode } {
  const options = object(value);
  keys(options, ['mode']);
  if (!(reviewModes as readonly unknown[]).includes(options.mode)) throw new ProtocolError('INVALID_REQUEST', 'Choose static review, builds and tests, or UI/E2E verification.');
  return { mode: options.mode as PublicReviewMode };
}
function taskReviewOptions(task: Record<string, unknown>): { mode: PublicReviewMode } | undefined {
  if (task.reviewOptions === undefined) return undefined;
  if (task.actionKind !== 'pr-review') throw new ProtocolError('INVALID_REQUEST', 'Review options are supported only for PR review tasks.');
  return validateReviewOptions(task.reviewOptions);
}
function publicTaskReviewOptions(task: unknown): { mode: PublicReviewMode } | undefined {
  try { return taskReviewOptions(object(task)); }
  catch { throw new ProtocolError('INVALID_RESPONSE', 'The Host returned invalid review options.'); }
}
function publicReviewModes(value: unknown): PublicReviewMode[] | undefined {
  if (value === undefined) return undefined;
  if (!Array.isArray(value) || value.length > reviewModes.length || value.some(mode => !(reviewModes as readonly unknown[]).includes(mode)) || new Set(value).size !== value.length) throw new ProtocolError('INVALID_RESPONSE', 'The Host returned invalid review modes.');
  return [...value] as PublicReviewMode[];
}
export function requireReviewMode(supported: unknown, options: unknown): void {
  const requested = validateReviewOptions(options);
  if (!publicReviewModes(supported)?.includes(requested.mode)) throw new ProtocolError('HOST_UPDATE_REQUIRED', 'Update Pulse Extension and its local Host to use the selected review mode.', 'Install matching extension and Host versions, then check again.');
}

export class ProtocolError extends Error {
  readonly code: string;
  readonly guidance: string;
  constructor(code: string, message: string, guidance = '') {
    super(message); this.name = 'ProtocolError'; this.code = code; this.guidance = guidance;
  }
}
export function asError(value: unknown): PulseError {
  if (value instanceof ProtocolError) return { code: value.code, message: value.message, guidance: value.guidance };
  return { code: 'CLIENT_ERROR', message: value instanceof Error ? value.message : 'The request could not be completed.', guidance: 'Reload the status. Check the saved records before retrying an unconfirmed task or GitHub submission.' };
}
export function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ProtocolError('INVALID_REQUEST', 'The message must be a JSON object.');
  return value as Record<string, unknown>;
}
function keys(value: Record<string, unknown>, allowed: readonly string[]): void {
  for (const key of Object.keys(value)) if (!allowed.includes(key)) throw new ProtocolError('INVALID_REQUEST', `Unsupported field: ${key}`);
}
function boundedString(value: unknown, name: string, max: number): string {
  if (typeof value !== 'string' || !value.trim() || value.includes('\0') || value.length > max) throw new ProtocolError('INVALID_REQUEST', `${name} must be nonempty, contain no NUL, and have a maximum length of ${max}.`);
  return value;
}
function optionalText(value: unknown, name: string, max: number): void {
  if (value !== undefined && (typeof value !== 'string' || value.includes('\0') || value.length > max)) throw new ProtocolError('INVALID_REQUEST', `${name} must be text without NUL and at most ${max} characters.`);
}
export function uuid(value: unknown, name: string): string {
  if (typeof value !== 'string' || !/^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$/.test(value)) throw new ProtocolError('INVALID_REQUEST', `${name} must be a UUID.`);
  return value;
}
function positiveInteger(value: unknown, name: string): void {
  if (!Number.isSafeInteger(value) || Number(value) <= 0 || Number(value) > 2147483647) throw new ProtocolError('INVALID_REQUEST', `${name} must be a positive 32-bit integer.`);
}
function actionKind(value: unknown): void {
  if (!(publicWorkflowKinds as readonly unknown[]).includes(value)) throw new ProtocolError('INVALID_REQUEST', 'Unknown actionKind.');
}
export function validateExecution(value: unknown): TaskExecution {
  const execution = object(value);
  keys(execution, ['agent', 'model', 'reasoningEffort']);
  if (execution.agent !== undefined && execution.agent !== 'codex' && execution.agent !== 'copilot') throw new ProtocolError('INVALID_REQUEST', 'Choose Codex or Copilot for this task.');
  if (execution.model !== undefined && (typeof execution.model !== 'string' || execution.model.trim() !== execution.model || !/^([A-Za-z0-9][A-Za-z0-9._-]{0,127})?$/.test(execution.model))) throw new ProtocolError('INVALID_REQUEST', 'Model must be a model ID of at most 128 letters, digits, dots, underscores, or hyphens, or empty to use the CLI default.');
  const efforts = execution.agent === undefined ? [...reasoningEfforts.codex, ...reasoningEfforts.copilot] : reasoningEfforts[execution.agent as Agent];
  if (execution.reasoningEffort !== undefined && (typeof execution.reasoningEffort !== 'string' || (execution.reasoningEffort !== '' && !efforts.includes(execution.reasoningEffort)))) throw new ProtocolError('INVALID_REQUEST', 'Choose a reasoning effort supported by the selected CLI, or empty to use its default.');
  return execution as TaskExecution;
}
export function safeHttpsUrl(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && !url.username && !url.password ? url.href : undefined;
  } catch { return undefined; }
}
export function senderOrigin(url: string | undefined, origins: readonly string[]): string {
  if (!url) throw new ProtocolError('FORBIDDEN_ORIGIN', 'The page origin is missing.');
  let origin: string;
  try { origin = new URL(url).origin; } catch { throw new ProtocolError('FORBIDDEN_ORIGIN', 'The page origin is invalid.'); }
  if (!origins.includes(origin)) throw new ProtocolError('FORBIDDEN_ORIGIN', 'This page origin is not allowed.');
  return origin;
}
/** New requests require a linked business target. Saved request lookup keeps its original identity. */
export function validateTask(value: unknown): Task { return validateTaskShape(value, true); }
export function validateSavedTask(value: unknown): Task { return validateTaskShape(value, false); }
function validateTaskShape(value: unknown, requireTarget: boolean): Task {
  const task = object(value);
  keys(task, ['requestId', 'actionId', 'actionKind', 'repository', 'target', 'expectedHeadSha', 'context', 'prompt', 'execution', 'reviewOptions']);
  boundedString(task.requestId, 'requestId', 128);
  boundedString(task.actionId, 'actionId', 256);
  const repository = boundedString(task.repository, 'repository', 200);
  if (!/^[A-Za-z0-9][A-Za-z0-9-]{0,38}\/[A-Za-z0-9_.-]{1,100}$/.test(repository)) throw new ProtocolError('INVALID_REQUEST', 'repository must be a valid owner/repo.');
  if (repository.toLowerCase() !== 'microsoft/powertoys') throw new ProtocolError('UNSUPPORTED_REPOSITORY', 'This extension supports only microsoft/PowerToys.');
  actionKind(task.actionKind);
  taskReviewOptions(task);
  if (task.execution !== undefined) validateExecution(task.execution);
  boundedString(task.prompt, 'prompt', 128 * 1024);
  if (new TextEncoder().encode(String(task.prompt)).length > 160 * 1024) throw new ProtocolError('INPUT_TOO_LARGE', 'The task prompt exceeds 160 KiB.');
  if (task.context !== undefined && new TextEncoder().encode(JSON.stringify(task.context)).length > 32 * 1024) throw new ProtocolError('INPUT_TOO_LARGE', 'The task context exceeds 32 KiB.');
  if (task.target !== undefined) {
    const target = object(task.target);
    keys(target, ['type', 'number']);
    if (!['issue', 'pr'].includes(String(target.type)) || !Number.isSafeInteger(target.number) || Number(target.number) <= 0 || Number(target.number) > 2147483647) throw new ProtocolError('INVALID_REQUEST', 'The target type or number is invalid.');
  }
  const expectedTarget = ['pr-review', 'e2e'].includes(String(task.actionKind)) ? 'pr' : 'issue';
  // Older issue-fix, reproduction-setup and E2E requests may have no target. This
  // exception is read/recovery-only; it must never authorize a new submission.
  const targetRequired = requireTarget || task.actionKind === 'pr-review' || isIssueResearch(task.actionKind);
  if ((targetRequired && !task.target) || (task.target && object(task.target).type !== expectedTarget)) {
    const label = expectedTarget === 'pr' ? 'pull request' : 'issue';
    throw new ProtocolError('INVALID_REQUEST', `This task requires a ${label} target.`, `Your draft is unchanged. Open the matching ${label} in Pulse to start a new task. Use the original saved request only to look up an unconfirmed submission.`);
  }
  const bindsPr = task.target && object(task.target).type === 'pr' && ['pr-review', 'e2e'].includes(String(task.actionKind));
  if (bindsPr && task.expectedHeadSha === undefined) throw new ProtocolError('MISSING_HEAD_SHA', 'PR review and E2E tasks must include expectedHeadSha.', 'Reload the PR HEAD before submitting.');
  if (task.expectedHeadSha !== undefined && (!task.target || object(task.target).type !== 'pr' || typeof task.expectedHeadSha !== 'string' || !/^[a-fA-F0-9]{40}$/.test(task.expectedHeadSha))) throw new ProtocolError('INVALID_REQUEST', 'expectedHeadSha requires a pull request target and a full 40-character Git SHA.');
  if (new TextEncoder().encode(JSON.stringify(task)).length > 240 * 1024) throw new ProtocolError('MESSAGE_TOO_LARGE', 'The task request exceeds 240 KiB.');
  return task as unknown as Task;
}
export function sameTaskIdentity(actual: unknown, expected: unknown): boolean {
  const canonical = (value: unknown): string => JSON.stringify(value, (_key, item: unknown) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) return item;
    const record = item as Record<string, unknown>;
    return Object.fromEntries(Object.keys(record).sort().map(key => [key, record[key]]));
  });
  const normalized = (value: unknown): Task => {
    const task = validateSavedTask(value);
    return { ...task, repository: task.repository.toLowerCase(), ...(task.expectedHeadSha === undefined ? {} : { expectedHeadSha: task.expectedHeadSha.toLowerCase() }) };
  };
  try { return canonical(normalized(actual)) === canonical(normalized(expected)); }
  catch { return false; }
}
export function validateWebActionDraft(value: unknown): WebActionDraft {
  const draft = object(value);
  keys(draft, ['requestId', 'actionId', 'kind', 'target', 'expectedHeadSha', 'body', 'review', 'pullRequest', 'assignSelf', 'assignmentTarget']);
  uuid(draft.requestId, 'requestId');
  boundedString(draft.actionId, 'actionId', 256);
  if (!['comment', 'review', 'approve', 'trigger-ci', 'merge-pr', 'create-pr'].includes(String(draft.kind))) throw new ProtocolError('INVALID_REQUEST', 'Unknown GitHub action kind.');
  const target = object(draft.target);
  keys(target, ['repository', 'type', 'number']);
  const repository = boundedString(target.repository, 'repository', 200).toLowerCase();
  if (repository !== 'microsoft/powertoys' && !(repository === 'muyuanms/powertoys' && draft.kind === 'comment')) throw new ProtocolError('UNSUPPORTED_REPOSITORY', 'This GitHub action does not support the requested repository.');
  if (!['pr', 'issue'].includes(String(target.type))) throw new ProtocolError('INVALID_REQUEST', 'Unknown GitHub target type.');
  positiveInteger(target.number, 'target.number');
  if (target.type === 'pr' && draft.expectedHeadSha === undefined) throw new ProtocolError('MISSING_HEAD_SHA', 'GitHub actions on a PR require expectedHeadSha.', 'Reload the PR HEAD before preparing the action.');
  if (draft.expectedHeadSha !== undefined && (target.type !== 'pr' || typeof draft.expectedHeadSha !== 'string' || !/^[a-fA-F0-9]{40}$/.test(draft.expectedHeadSha))) throw new ProtocolError('INVALID_REQUEST', 'expectedHeadSha requires a PR target and a full 40-character Git SHA.');
  if (['review', 'approve', 'trigger-ci', 'merge-pr'].includes(String(draft.kind)) && target.type !== 'pr') throw new ProtocolError('INVALID_REQUEST', 'This GitHub action requires a PR target.');
  if (draft.kind === 'create-pr' && target.type !== 'issue') throw new ProtocolError('INVALID_REQUEST', 'Creating a PR requires its source issue target.');
  optionalText(draft.body, 'body', 60000);
  const forkComment = repository === 'muyuanms/powertoys' && draft.kind === 'comment';
  if (draft.assignSelf !== undefined && typeof draft.assignSelf !== 'boolean') throw new ProtocolError('INVALID_REQUEST', 'assignSelf must be a boolean.');
  if (draft.assignSelf === true && ['merge-pr', 'trigger-ci'].includes(String(draft.kind))) throw new ProtocolError('INVALID_REQUEST', 'Merge and CI actions do not include self-assignment.');
  if (draft.assignmentTarget !== undefined) {
    if (!forkComment || draft.assignSelf !== true) throw new ProtocolError('INVALID_REQUEST', 'assignmentTarget requires a fork comment with assignSelf.');
    const assignment = object(draft.assignmentTarget); keys(assignment, ['repository', 'type', 'number']);
    if (typeof assignment.repository !== 'string' || assignment.repository.toLowerCase() !== 'microsoft/powertoys' || !['issue', 'pr'].includes(String(assignment.type))) throw new ProtocolError('INVALID_REQUEST', 'assignmentTarget must refer to microsoft/PowerToys.');
    positiveInteger(assignment.number, 'assignmentTarget.number');
  } else if (forkComment && draft.assignSelf === true) throw new ProtocolError('INVALID_REQUEST', 'A fork comment with assignSelf requires its upstream assignmentTarget.');
  if (draft.kind === 'comment') boundedString(draft.body, 'body', 60000);
  if (draft.kind === 'trigger-ci' && draft.body !== undefined && draft.body !== '' && draft.body !== '/azp run') throw new ProtocolError('INVALID_REQUEST', 'Trigger CI posts the fixed /azp run command only.');
  if (['merge-pr', 'create-pr'].includes(String(draft.kind)) && draft.body !== undefined && draft.body !== '') throw new ProtocolError('INVALID_REQUEST', 'This action does not accept a top-level comment body.');
  if (draft.review !== undefined) {
    if (draft.kind !== 'review') throw new ProtocolError('INVALID_REQUEST', 'review is supported only for review actions.');
    const review = object(draft.review);
    keys(review, ['event', 'comments', 'generalComments']);
    if (!['COMMENT', 'REQUEST_CHANGES'].includes(String(review.event))) throw new ProtocolError('INVALID_REQUEST', 'Unknown review event.');
    for (const field of ['comments', 'generalComments'] as const) if (review[field] !== undefined && !Array.isArray(review[field])) throw new ProtocolError('INVALID_REQUEST', `${field} must be an array.`);
    const comments = (review.comments ?? []) as unknown[];
    const generalComments = (review.generalComments ?? []) as unknown[];
    if (comments.length + generalComments.length > 50) throw new ProtocolError('INPUT_TOO_LARGE', 'A review can include at most 50 comments.');
    for (const value of comments) {
      const comment = object(value);
      keys(comment, ['path', 'line', 'startLine', 'side', 'startSide', 'body']);
      boundedString(comment.path, 'comment.path', 1000);
      if (String(comment.path).startsWith('/') || String(comment.path).includes('\\') || String(comment.path).split('/').some(segment => ['', '.', '..'].includes(segment)) || /[\u0000-\u001f\u007f-\u009f]/.test(String(comment.path))) throw new ProtocolError('INVALID_REQUEST', 'Review comment paths must be relative paths in the PR diff.');
      boundedString(comment.body, 'comment.body', 60000);
      positiveInteger(comment.line, 'comment.line');
      if (!['LEFT', 'RIGHT'].includes(String(comment.side))) throw new ProtocolError('INVALID_REQUEST', 'Review comments require LEFT or RIGHT side.');
      if (comment.startLine !== undefined) positiveInteger(comment.startLine, 'comment.startLine');
      if (comment.startSide !== undefined && (comment.startLine === undefined || !['LEFT', 'RIGHT'].includes(String(comment.startSide)))) throw new ProtocolError('INVALID_REQUEST', 'startSide requires startLine and LEFT or RIGHT.');
      if (comment.startLine !== undefined && (Number(comment.startLine) > Number(comment.line) || Number(comment.line) - Number(comment.startLine) > 1000)) throw new ProtocolError('INVALID_REQUEST', 'Review comment line ranges must be ordered and span at most 1001 lines.');
      if (comment.startSide !== undefined && comment.startSide !== comment.side) throw new ProtocolError('INVALID_REQUEST', 'A review comment range must stay on the same diff side.');
    }
    for (const value of generalComments) { const comment = object(value); keys(comment, ['body']); boundedString(comment.body, 'generalComment.body', 60000); }
    if (!comments.length && !generalComments.length && (typeof draft.body !== 'string' || !draft.body.trim())) throw new ProtocolError('INVALID_REQUEST', 'Select review findings or write a review body.');
  } else if (draft.kind === 'review') throw new ProtocolError('INVALID_REQUEST', 'A review action requires review details.');
  if (draft.pullRequest !== undefined) {
    if (draft.kind !== 'create-pr') throw new ProtocolError('INVALID_REQUEST', 'pullRequest is supported only for create-pr actions.');
    const pr = object(draft.pullRequest);
    keys(pr, ['head', 'base', 'title', 'body', 'draft']);
    boundedString(pr.head, 'pullRequest.head', 240); boundedString(pr.base, 'pullRequest.base', 200); boundedString(pr.title, 'pullRequest.title', 256);
    if (!/^[A-Za-z0-9][A-Za-z0-9-]{0,38}:[^\s\0]+$/.test(String(pr.head))) throw new ProtocolError('INVALID_REQUEST', 'pullRequest.head must be owner:branch.');
    optionalText(pr.body, 'pullRequest.body', 60000);
    if (pr.draft !== undefined && typeof pr.draft !== 'boolean') throw new ProtocolError('INVALID_REQUEST', 'pullRequest.draft must be a boolean.');
  } else if (draft.kind === 'create-pr') throw new ProtocolError('INVALID_REQUEST', 'Creating a PR requires pullRequest details.');
  if (new TextEncoder().encode(JSON.stringify(draft)).length > 200 * 1024) throw new ProtocolError('MESSAGE_TOO_LARGE', 'The GitHub draft exceeds 200 KiB.');
  return draft as unknown as WebActionDraft;
}
export const externalMethods = ['bridge.hello', 'hello', 'agents.defaults', 'actions.check', 'targets.get', 'tasks.submit', 'tasks.lookup', 'tasks.get', 'tasks.events', 'ui.openSettings', 'ui.openTask', 'ui.openTasks', 'github.prepare', 'github.get'] as const;
export interface ExternalRequest { protocolVersion: 1; type: typeof externalMethods[number]; payload: Record<string, unknown> }
export function validateExternal(value: unknown): ExternalRequest {
  const request = object(value);
  keys(request, ['protocolVersion', 'type', 'payload']);
  if (request.protocolVersion !== 1) throw new ProtocolError('PROTOCOL_MISMATCH', 'The protocol version is incompatible.', 'Update the page, extension, and Host to matching protocol versions.');
  if (!(externalMethods as readonly string[]).includes(String(request.type))) throw new ProtocolError('FORBIDDEN_METHOD', 'Web pages can only check readiness, submit local tasks, prepare GitHub drafts, read associated records, and open fixed extension pages.');
  const payload = request.payload === undefined ? {} : object(request.payload);
  if (['bridge.hello', 'hello', 'agents.defaults', 'ui.openSettings', 'ui.openTasks'].includes(String(request.type))) keys(payload, []);
  if (request.type === 'actions.check') { keys(payload, ['actionKind', 'execution', 'reviewOptions']); actionKind(payload.actionKind); taskReviewOptions(payload); if (payload.execution !== undefined) validateExecution(payload.execution); }
  if (request.type === 'targets.get') {
    keys(payload, ['target']); const target = object(payload.target); keys(target, ['type', 'number']);
    if (target.type !== 'pr') throw new ProtocolError('INVALID_REQUEST', 'Only PR target snapshots are supported.');
    positiveInteger(target.number, 'target.number');
  }
  if (request.type === 'github.prepare') { keys(payload, ['draft']); validateWebActionDraft(payload.draft); }
  if (request.type === 'github.get') { keys(payload, ['operationId']); uuid(payload.operationId, 'operationId'); }
  if (request.type === 'tasks.submit' || request.type === 'tasks.lookup') {
    keys(payload, ['task']);
    if (request.type === 'tasks.lookup') validateSavedTask(payload.task);
    else validateTask(payload.task);
  }
  if (request.type === 'tasks.get' || request.type === 'tasks.events' || request.type === 'ui.openTask') {
    keys(payload, request.type === 'tasks.events' ? ['runId', 'afterSequence', 'limit'] : ['runId']);
    boundedString(payload.runId, 'runId', 128);
    if (request.type === 'tasks.events') {
      if (!Number.isSafeInteger(payload.afterSequence) || Number(payload.afterSequence) < 0) throw new ProtocolError('INVALID_REQUEST', 'afterSequence must be a nonnegative integer.');
      if (payload.limit !== undefined && (!Number.isSafeInteger(payload.limit) || Number(payload.limit) < 1 || Number(payload.limit) > 100)) throw new ProtocolError('INVALID_REQUEST', 'The event limit must be between 1 and 100.');
    }
  }
  return { protocolVersion: 1, type: request.type as ExternalRequest['type'], payload };
}
function publicError(error: PulseError): PulseError { return { code: error.code, message: error.message, guidance: error.guidance }; }
export function publicAgentDefaults(data: AgentDefaults): AgentDefaults {
  if (data.defaultAgent !== 'codex' && data.defaultAgent !== 'copilot') throw new ProtocolError('INVALID_RESPONSE', 'The Host returned an unsupported default agent.');
  for (const agent of ['codex', 'copilot'] as const) {
    const profile = data.defaults?.[agent];
    if (!profile || typeof profile.model !== 'string' || typeof profile.reasoningEffort !== 'string') throw new ProtocolError('INVALID_RESPONSE', 'The Host returned invalid agent defaults.');
    validateExecution({ agent, model: profile.model, reasoningEffort: profile.reasoningEffort });
    if (!Array.isArray(data.reasoningEfforts?.[agent]) || !data.reasoningEfforts[agent].every(effort => reasoningEfforts[agent].includes(effort))) throw new ProtocolError('INVALID_RESPONSE', 'The Host returned unsupported reasoning efforts.');
  }
  return {
    defaultAgent: data.defaultAgent,
    defaults: {
      codex: { model: data.defaults.codex.model, reasoningEffort: data.defaults.codex.reasoningEffort },
      copilot: { model: data.defaults.copilot.model, reasoningEffort: data.defaults.copilot.reasoningEffort },
    },
    reasoningEfforts: { codex: [...data.reasoningEfforts.codex], copilot: [...data.reasoningEfforts.copilot] },
  };
}
export function publicReadiness(data: ActionReadiness): ActionReadiness {
  const options = publicTaskReviewOptions(data);
  const modes = publicReviewModes(data.reviewModes);
  return { actionKind: data.actionKind, ready: Boolean(data.ready), blockers: data.blockers.map(publicError),
    ...(options === undefined ? {} : { reviewOptions: options }), ...(modes === undefined ? {} : { reviewModes: modes }) };
}
export function publicTarget(data: TargetSnapshot): TargetSnapshot {
  return { target: { type: data.target.type, number: data.target.number }, headSha: data.headSha, title: data.title };
}
export function publicWebAction(data: WebActionSummary): WebActionSummary {
  return {
    operationId: data.operationId, requestId: data.requestId, actionId: data.actionId, kind: data.kind,
    target: { repository: data.target.repository, type: data.target.type, number: data.target.number }, status: data.status,
    completedSteps: [...data.completedSteps], remainingSteps: [...data.remainingSteps],
    urls: data.urls.map(safeHttpsUrl).filter((value): value is string => value !== undefined),
    error: data.error && publicError(data.error), createdAt: data.createdAt, updatedAt: data.updatedAt,
  };
}
export function publicCapabilities(data: Capabilities): unknown {
  const modes = publicReviewModes(data.reviewModes);
  return {
    protocolVersion: data.protocolVersion, hostVersion: data.hostVersion,
    agents: { codex: { available: Boolean(data.agents?.codex?.available) }, copilot: { available: Boolean(data.agents?.copilot?.available) } },
    ...(modes === undefined ? {} : { reviewModes: modes }),
    ...publicWorkflowCapabilities(data),
  };
}
export function publicObservedExecution(value: unknown): ObservedExecution | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  const observed = value as Record<string, unknown>;
  if (observed.source !== 'codex-turn-context' && observed.source !== 'cli-event') return undefined;
  if (typeof observed.observedAt !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(observed.observedAt) || !Number.isFinite(Date.parse(observed.observedAt))) return undefined;
  const result: ObservedExecution = { source: observed.source, observedAt: observed.observedAt };
  if (typeof observed.model === 'string' && observed.model.trim() === observed.model && /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(observed.model)) result.model = observed.model;
  if (typeof observed.reasoningEffort === 'string' && [...reasoningEfforts.codex, ...reasoningEfforts.copilot].includes(observed.reasoningEffort)) result.reasoningEffort = observed.reasoningEffort;
  return result.model !== undefined || result.reasoningEffort !== undefined ? result : undefined;
}
export function publicResultMetadata(value: unknown): { schemaVersion?: 1 | 2 | 3; outcome?: ResultOutcome; phase?: ResultPhase; structured?: boolean } {
  const invalid = (): never => { throw new ProtocolError('INVALID_RESPONSE', 'The Host returned invalid workflow outcome metadata.', 'Use matching extension and Host versions, then reload the saved task.'); };
  if (!value || typeof value !== 'object' || Array.isArray(value)) return invalid();
  const result = value as Record<string, unknown>;
  const schemaVersion = result.schemaVersion ?? undefined;
  const outcome = result.outcome ?? undefined;
  const phase = result.phase ?? undefined;
  const structured = result.structured ?? undefined;
  if (schemaVersion !== undefined && schemaVersion !== 1 && schemaVersion !== 2 && schemaVersion !== 3) return invalid();
  if (outcome !== undefined && (typeof outcome !== 'string' || !['completed', 'blocked', 'failed', 'cancelled', 'interrupted'].includes(outcome))) return invalid();
  if (phase !== undefined && (typeof phase !== 'string' || !['setup', 'analysis', 'implementation', 'validation', 'reporting'].includes(phase))) return invalid();
  if (structured !== undefined && typeof structured !== 'boolean') return invalid();
  if ((schemaVersion === 2 || schemaVersion === 3) && (outcome === undefined || phase === undefined || structured === undefined)) return invalid();
  return { schemaVersion, outcome: outcome as ResultOutcome | undefined, phase: phase as ResultPhase | undefined, structured: structured as boolean | undefined };
}
export function publicRun(run: Run): unknown {
  const options = publicTaskReviewOptions(run.task);
  const agent = run.config?.agent;
  const model = run.config?.model;
  const reasoningEffort = run.config?.reasoningEffort;
  const executionRecorded = (agent === 'codex' || agent === 'copilot') && typeof model === 'string' && typeof reasoningEffort === 'string';
  if (executionRecorded) validateExecution({ agent, model, reasoningEffort });
  const source = (value: string | undefined) => value === 'task' ? 'task' : value === 'default' ? 'agent-default' : 'cli-default';
  return {
    runId: run.runId, requestId: run.task.requestId, actionId: run.task.actionId,
    actionKind: run.task.actionKind, repository: run.task.repository, target: run.task.target ?? undefined,
    expectedHeadSha: run.task.expectedHeadSha ?? undefined,
    ...(options === undefined ? {} : { reviewOptions: options }),
    execution: executionRecorded ? { agent, model, reasoningEffort, modelSource: source(run.config.executionSource?.model), reasoningEffortSource: source(run.config.executionSource?.reasoningEffort) } : undefined,
    status: { state: run.status.state, createdAt: run.status.createdAt, updatedAt: run.status.updatedAt,
      startedAt: run.status.startedAt, endedAt: run.status.endedAt, exitCode: run.status.exitCode,
      sequence: run.status.sequence, error: run.status.error ? { code: run.status.error.code, message: run.status.error.message, guidance: run.status.error.guidance } : undefined,
      observedExecution: publicObservedExecution(run.status.observedExecution) },
    result: run.result ? { ...publicResultMetadata(run.result), summary: run.result.summary, needsReview: run.result.needsReview,
      artifacts: (run.result.artifacts ?? []).map(artifact => ({ label: artifact.label, url: safeHttpsUrl(artifact.url) })),
      validation: (run.result.validation ?? []).map(item => ({ name: item.name, status: item.status, details: item.details })),
      blockers: run.result.blockers,
      nextSteps: (run.result.nextSteps ?? []).map(step => ({ kind: step.kind, reason: step.reason, body: step.body })) } : undefined,
  };
}
export function publicEvent(event: TaskEvent): TaskEvent {
  return { sequence: event.sequence, time: event.time, type: event.type, text: event.text };
}
export function isActive(state: string): boolean { return state === 'accepted' || state === 'running'; }
