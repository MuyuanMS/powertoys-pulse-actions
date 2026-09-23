import { allowedOrigins } from './environment.js';
import { NativeClient } from './native.js';
import { ProtocolError, asError, externalMethods, isIssueResearch, object, publicAgentDefaults, publicCapabilities, publicEvent, publicReadiness, publicRun, publicTarget, publicWebAction, requireReviewMode, requireWorkflowCapabilities, sameTaskIdentity, senderOrigin, uuid, validateExternal } from './policy.js';
import type { ActionReadiness, AgentDefaults, Capabilities, Events, Reply, Run, RunList, TargetSnapshot, TaskLookup, WebActionSummary } from './types.js';

const native = new NativeClient();
const internalMethods = new Set(['hello', 'config.get', 'config.save', 'prompts.list', 'prompts.sync', 'prompts.get', 'agents.list', 'agents.defaults', 'agents.test.start', 'agents.test.get', 'agents.test.cancel', 'github.accounts', 'actions.check', 'targets.get', 'tasks.list', 'tasks.lookup', 'tasks.get', 'tasks.resultPage', 'tasks.events', 'tasks.logs', 'tasks.cancel', 'tasks.read', 'tasks.handle', 'tasks.rerun', 'tasks.delete', 'tasks.startFromResult', 'reviews.verify', 'reviews.related', 'operations.preview', 'operations.submit', 'operations.list', 'operations.reconcile', 'resultActions.prepare', 'resultActions.list', 'webActions.preview', 'webActions.submit', 'webActions.cancel', 'webActions.reconcile']);
const grantKey = 'pulse.origin-grants.v1';
interface Grant { origin: string; runId: string; requestId: string; actionId: string }
let grants: Promise<Grant[]> = chrome.storage.local.get(grantKey).then(values => Array.isArray(values[grantKey]) ? values[grantKey] as Grant[] : []);
let badgeRunning = false;
let latestCounts = { running: 0, unread: 0 };
let badgeTimer: ReturnType<typeof setTimeout> | undefined;

function reply(data: unknown): Reply { return { id: '', protocolVersion: 1, ok: true, data }; }
function fail(error: unknown): Reply { return { id: '', protocolVersion: 1, ok: false, error: asError(error) }; }

// Grants are origin/run associations only. Config, prompts, logs, and auth stay in the Host.
function addGrant(grant: Grant): Promise<void> {
  const updated = grants.then(async current => {
    if (!current.some(item => item.origin === grant.origin && item.runId === grant.runId)) {
      const next = [...current, grant];
      await chrome.storage.local.set({ [grantKey]: next });
      return next;
    }
    return current;
  });
  grants = updated.catch(() => grantsFallback());
  return updated.then(() => {});
}
async function grantsFallback(): Promise<Grant[]> {
  const values = await chrome.storage.local.get(grantKey);
  return Array.isArray(values[grantKey]) ? values[grantKey] as Grant[] : [];
}

async function external(message: unknown, sender: chrome.runtime.MessageSender): Promise<Reply> {
  if (sender.id) throw new ProtocolError('FORBIDDEN_ORIGIN', 'This message endpoint is restricted to allowed Pulse pages.');
  const origin = senderOrigin(sender.url, allowedOrigins);
  if (sender.origin && sender.origin !== origin) throw new ProtocolError('FORBIDDEN_ORIGIN', 'The message origin does not match the page URL.');
  const request = validateExternal(message);
  // This handshake and the fixed UI routes stay available even when the Host is absent.
  if (request.type === 'bridge.hello') return reply({ protocolVersion: 1, extensionVersion: chrome.runtime.getManifest().version, methods: [...externalMethods] });
  if (request.type === 'ui.openSettings') { await chrome.runtime.openOptionsPage(); return reply({ opened: true }); }
  if (request.type === 'ui.openTasks') { await chrome.tabs.create({ url: chrome.runtime.getURL('popup.html?expanded=1') }); return reply({ opened: true }); }
  if (request.type === 'hello') return reply(publicCapabilities(await native.request<Capabilities>('hello')));
  if (request.type === 'agents.defaults') return reply(publicAgentDefaults(await native.request<AgentDefaults>('agents.defaults')));
  if (request.type === 'actions.check') {
    if (isIssueResearch(request.payload.actionKind)) requireWorkflowCapabilities(await native.request<Capabilities>('hello'), request.payload.actionKind);
    const data = await native.request<ActionReadiness>('actions.check', request.payload).catch(error => {
      // The extension has already validated this payload. Older Hosts reject the new field.
      if (request.payload.reviewOptions !== undefined && error instanceof ProtocolError && error.code === 'INVALID_REQUEST') requireReviewMode(undefined, request.payload.reviewOptions);
      throw error;
    });
    if (data.actionKind !== request.payload.actionKind) throw new ProtocolError('INVALID_RESPONSE', 'The Host returned readiness for a different action.');
    const readiness = publicReadiness(data);
    if (request.payload.reviewOptions !== undefined) requireReviewMode(readiness.reviewModes, request.payload.reviewOptions);
    const requestedMode = request.payload.reviewOptions === undefined ? undefined : object(request.payload.reviewOptions).mode;
    if (readiness.reviewOptions?.mode !== requestedMode) throw new ProtocolError('INVALID_RESPONSE', 'The Host returned readiness for a different review mode.');
    return reply(readiness);
  }
  if (request.type === 'targets.get') {
    const data = await native.request<TargetSnapshot>('targets.get', request.payload);
    if (data.target?.type !== 'pr' || data.target.number !== object(request.payload.target).number || !/^[a-fA-F0-9]{40}$/.test(data.headSha)) throw new ProtocolError('INVALID_RESPONSE', 'The Host returned a mismatched PR target.');
    return reply(publicTarget(data));
  }
  if (request.type === 'github.prepare') {
    const data = await native.request<WebActionSummary>('webActions.prepare', { draft: request.payload.draft, sourceOrigin: origin });
    const draft = object(request.payload.draft);
    const target = object(draft.target);
    if (data.requestId.toLowerCase() !== String(draft.requestId).toLowerCase() || data.actionId !== draft.actionId || data.kind !== draft.kind || data.target?.repository.toLowerCase() !== String(target.repository).toLowerCase() || data.target.type !== target.type || data.target.number !== target.number) throw new ProtocolError('INVALID_RESPONSE', 'The Host returned a mismatched GitHub draft.');
    const operationId = uuid(data.operationId, 'operationId');
    // Preparation is durable. A failed tab opening must return its existing record so the page can recover it.
    let opened = true;
    try { await chrome.tabs.create({ url: chrome.runtime.getURL(`action.html?operationId=${encodeURIComponent(operationId)}`) }); }
    catch { opened = false; }
    return reply({ ...publicWebAction(data), opened });
  }
  if (request.type === 'github.get') {
    const data = await native.request<WebActionSummary>('webActions.get', { operationId: request.payload.operationId, sourceOrigin: origin });
    if (data.operationId.toLowerCase() !== String(request.payload.operationId).toLowerCase()) throw new ProtocolError('INVALID_RESPONSE', 'The Host returned a mismatched GitHub operation.');
    return reply(publicWebAction(data));
  }
  if (request.type === 'tasks.lookup') {
    const data = await native.request<TaskLookup>('tasks.lookup', { task: request.payload.task, sourceOrigin: origin });
    if (data?.run === null) return reply({ run: null });
    const run = data?.run;
    if (!run || typeof run.runId !== 'string' || !run.runId.trim() || run.runId.length > 128 || !sameTaskIdentity(run.task, request.payload.task)) throw new ProtocolError('INVALID_RESPONSE', 'The Host returned a mismatched task lookup.');
    const result = publicRun(run);
    await addGrant({ origin, runId: run.runId, requestId: run.task.requestId, actionId: run.task.actionId });
    return reply({ run: result });
  }
  if (request.type === 'tasks.submit') {
    const submitted = object(request.payload.task);
    const options = submitted.reviewOptions;
    if (options !== undefined || isIssueResearch(submitted.actionKind)) {
      const capabilities = await native.request<Capabilities>('hello');
      requireWorkflowCapabilities(capabilities, submitted.actionKind);
      if (options !== undefined) requireReviewMode(capabilities.reviewModes, options);
    }
    const data = await native.request<Run | { runId: string }>('tasks.submit', { task: request.payload.task, sourceOrigin: origin });
    const run = 'task' in data ? data : await native.request<Run>('tasks.get', { runId: data.runId });
    if (!sameTaskIdentity(run.task, request.payload.task)) throw new ProtocolError('INVALID_RESPONSE', 'The Host returned a mismatched task.');
    await addGrant({ origin, runId: run.runId, requestId: run.task.requestId, actionId: run.task.actionId });
    void refreshBadge();
    return reply(publicRun(run));
  }
  const grant = (await grants).find(item => item.origin === origin && item.runId === request.payload.runId);
  if (!grant) throw new ProtocolError('FORBIDDEN_RUN', 'This origin is not associated with the requested run.', 'Look up the saved task with its original requestId to recover the accepted run.');
  if (request.type === 'ui.openTask') { await chrome.tabs.create({ url: chrome.runtime.getURL(`details.html?runId=${encodeURIComponent(grant.runId)}`) }); return reply({ opened: true }); }
  if (request.type === 'tasks.get') return reply(publicRun(await native.request<Run>('tasks.get', request.payload)));
  const events = await native.request<Events>('tasks.events', request.payload);
  return reply({ events: events.events.map(publicEvent), nextSequence: events.nextSequence, truncated: events.truncated });
}

chrome.runtime.onMessageExternal.addListener((message, sender, sendResponse) => {
  void external(message, sender).then(sendResponse, error => sendResponse(fail(error)));
  return true;
});
chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
  // No content scripts are shipped. Only packaged extension pages can use privileged methods.
  const ownOrigin = chrome.runtime.getURL('');
  if (sender.id !== chrome.runtime.id || !sender.url?.startsWith(ownOrigin)) {
    sendResponse(fail(new ProtocolError('FORBIDDEN_SOURCE', 'This action is restricted to extension pages.')));
    return false;
  }
  const handle = async (): Promise<Reply> => {
    const request = object(message);
    if (request.channel !== 'pulse-ui' || typeof request.type !== 'string') throw new ProtocolError('INVALID_REQUEST', 'Unknown extension message.');
    if (request.type === 'connection.status') return reply(native.connection);
    if (!internalMethods.has(request.type)) throw new ProtocolError('FORBIDDEN_METHOD', 'This action is not supported.');
    const data = await native.request(request.type, request.payload ?? {});
    if (request.type.startsWith('tasks.') || request.type === 'reviews.verify') void refreshBadge();
    return reply(data);
  };
  void handle().then(sendResponse, error => sendResponse(fail(error)));
  return true;
});

async function showBadge(): Promise<void> {
  const offline = native.connection.state === 'disconnected';
  const unread = latestCounts.unread;
  const count = unread || latestCounts.running;
  await Promise.allSettled([
    chrome.action.setBadgeText({ text: offline ? '!' : count ? count > 99 ? '99+' : String(count) : '' }),
    chrome.action.setBadgeBackgroundColor({ color: offline ? '#9a3412' : unread ? '#b45309' : '#0f766e' }),
    chrome.action.setTitle({ title: offline ? 'Pulse: Host disconnected. Open for diagnostics.' : `Pulse: ${latestCounts.running} running · ${unread} unread results` }),
  ]);
}
async function refreshBadge(): Promise<void> {
  if (badgeRunning) return;
  badgeRunning = true;
  try {
    const snapshot = await native.request<RunList>('tasks.list', { limit: 1 });
    latestCounts = { running: snapshot.runningCount, unread: snapshot.unreadCount };
    await showBadge();
  } catch { await showBadge(); }
  finally {
    badgeRunning = false;
    if (badgeTimer) clearTimeout(badgeTimer);
    badgeTimer = setTimeout(() => { void refreshBadge(); }, latestCounts.running > 0 ? 3_000 : 15_000);
  }
}
native.onConnectionChange = connection => {
  void showBadge();
  if (connection.state === 'connected') void refreshBadge();
};
async function start(): Promise<void> {
  await chrome.alarms.create('pulse-reconnect', { periodInMinutes: 0.5 });
  void refreshBadge();
}
chrome.runtime.onStartup.addListener(() => { void start(); });
chrome.runtime.onInstalled.addListener(() => { void start(); });
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === 'pulse-reconnect') void refreshBadge(); });
void start();
