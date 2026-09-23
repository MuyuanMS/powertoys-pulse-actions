import { isActive, ProtocolError } from './policy.js';
import { byId, clearError, connectionStatus, date, element, icon, initNavigation, report, request } from './ui.js';
import { filterLoadedTasks, recentTaskLimit, taskExecutionLabel, taskFailureText, taskPrimaryAction, taskResultContext, taskRowTitle, taskSections, taskTypeLabels } from './task-list.js';
import { phaseLabel, reviewModeLabel, workflowOutcome } from './details-model.js';
import type { TaskTargetFilter } from './task-list.js';
import type { Run, RunList } from './types.js';

type Scope = 'tasks' | 'history';
const initialUrl = new URL(location.href);
const expanded = initialUrl.searchParams.has('expanded');
if (expanded) document.body.classList.add('expanded');
let scope: Scope = 'tasks';
let target: TaskTargetFilter = 'all';
let pages = 1;
let runs: Run[] = [];
let recentRuns: Run[] = [];
let hasMore = false;
let busy = false;
let loaded = false;
let snapshotKey = '';
let lastUpdated = '';
let lastCounts: Pick<RunList, 'runningCount' | 'unreadCount'> | undefined;
let snapshotStale = false;
let recentTotal: number | undefined;
let finishedOrderSupported = true;
let previousRunSnapshot = new Map<string, string>();
const rowCache = new Map<string, TaskRow>();
initNavigation({ compactPopup: !expanded });
if (!expanded) {
  const main = byId('task-content');
  const heading = element('div', undefined, 'task-popup-title');
  heading.append(byId('page-title'), byId('counts')); main.prepend(heading);
  byId('task-popup-actions').append(byId('refresh'));
  main.parentElement!.append(byId('task-popup-footer'));
  byId('task-topbar').remove(); byId('task-connection').remove();
  byId('recent-title').textContent = 'Recent results';
  byId('recent-count').hidden = true;
  const historyLink = byId<HTMLAnchorElement>('history-link'); historyLink.target = '_blank'; historyLink.rel = 'noopener noreferrer';
  byId('retry-list').hidden = true;
}

const targetSelect = byId<HTMLSelectElement>('target-filter');
const scopeSelect = byId<HTMLSelectElement>('scope-filter');
const typeSelect = byId<HTMLSelectElement>('type-filter');
const searchInput = byId<HTMLInputElement>('task-search');
for (const [kind, label] of Object.entries(taskTypeLabels)) {
  const option = element('option', label); option.value = kind; typeSelect.append(option);
}
restoreLocation();
targetSelect.addEventListener('change', () => { target = targetSelect.value as TaskTargetFilter; pages = 1; saveLocation(); render(); void refresh(); });
scopeSelect.addEventListener('change', () => { scope = scopeSelect.value as Scope; pages = 1; saveLocation(); render(); void refresh(); });
typeSelect.addEventListener('change', () => { saveLocation(); render(); });
searchInput.addEventListener('input', () => { saveLocation(); render(); });
byId('clear-filters').addEventListener('click', () => { target = 'all'; targetSelect.value = 'all'; typeSelect.value = ''; searchInput.value = ''; pages = 1; saveLocation(); render(); void refresh(); });
byId('refresh').addEventListener('click', () => { pages = 1; saveLocation(); void refresh(true); });
byId('retry-list').addEventListener('click', () => { pages = 1; saveLocation(); void refresh(true); });
byId('more').addEventListener('click', () => { pages++; saveLocation(); void refresh(true); });
window.addEventListener('popstate', () => { restoreLocation(); render(); void refresh(); });

function restoreLocation(): void {
  const params = new URL(location.href).searchParams;
  const oldView = params.get('view');
  scope = oldView === 'history' || oldView === 'prs' || oldView === 'issues' ? 'history' : 'tasks';
  const selectedTarget = params.get('target') ?? (oldView === 'prs' ? 'pr' : oldView === 'issues' ? 'issue' : 'all');
  target = selectedTarget === 'pr' || selectedTarget === 'issue' ? selectedTarget : 'all';
  pages = Math.min(100, Math.max(1, Number(params.get('pages')) || 1));
  targetSelect.value = target; scopeSelect.value = scope;
  typeSelect.value = Object.hasOwn(taskTypeLabels, params.get('type') ?? '') ? params.get('type')! : '';
  searchInput.value = params.get('q') ?? '';
}

function saveLocation(): void {
  const params = new URLSearchParams({ ...(expanded ? { expanded: '1' } : {}), view: scope });
  if (target !== 'all') params.set('target', target);
  if (typeSelect.value) params.set('type', typeSelect.value);
  if (searchInput.value) params.set('q', searchInput.value);
  if (pages > 1) params.set('pages', String(pages));
  history.replaceState(null, '', `?${params}`);
}

function historyUrl(): string {
  return chrome.runtime.getURL(`popup.html?${new URLSearchParams({ expanded: '1', view: 'history', ...(target !== 'all' ? { target } : {}) })}`);
}

function filtersActive(): boolean { return target !== 'all' || Boolean(typeSelect.value || searchInput.value.trim()); }

function render(): void {
  const title = scope === 'history' ? 'All finished runs' : 'Tasks';
  byId('page-title').textContent = title; document.title = `Pulse · ${title}`;
  if (expanded) byId('page-description').textContent = scope === 'history' ? 'Review saved outcomes, local handling status, and earlier attempts.' : 'Local runs and results. Open a run to review its conclusion and next step.';
  byId('counts').textContent = `${!expanded && snapshotStale && lastCounts ? 'Last known · ' : ''}${lastCounts?.runningCount ?? '—'} running · ${lastCounts?.unreadCount ?? '—'} unread${expanded ? ' results' : ''}`;
  const currentSnapshot = loaded && snapshotKey === `${scope}:${target}`;
  const sections = taskSections(runs, recentRuns);
  const filters = { target, taskKind: typeSelect.value, search: searchInput.value };
  const selected = currentSnapshot ? filterLoadedTasks(scope === 'tasks' ? sections.active : runs.filter(run => !isActive(run.status.state)), filters) : [];
  const recent = currentSnapshot && scope === 'tasks' ? filterLoadedTasks(sections.recent, filters) : [];
  byId('recent-section').hidden = scope !== 'tasks';
  byId('primary-title').textContent = scope === 'tasks' ? 'Active' : target === 'pr' ? 'Pull request runs' : target === 'issue' ? 'Issue runs' : 'Finished runs';
  byId('primary-count').textContent = currentSnapshot ? String(selected.length) : '—';
  byId('recent-count').textContent = currentSnapshot ? String(recent.length) : '—';
  byId('clear-filters').hidden = !filtersActive();
  const historyLink = byId<HTMLAnchorElement>('history-link'); historyLink.href = historyUrl();
  if (!expanded) historyLink.textContent = recentTotal === undefined ? 'View all' : `View all (${recentTotal})`;
  byId('more').hidden = !currentSnapshot || !hasMore || !expanded;
  byId('filter-scope').textContent = scope === 'tasks'
    ? `Target, task type, and search filter loaded active runs and the latest ${recentTaskLimit} finished runs. Open history to find earlier results.`
    : 'Task type and search filter loaded records. Load more to search earlier runs.';
  byId('loaded-scope').textContent = currentSnapshot ? scope === 'tasks'
    ? `${sections.active.length} active runs loaded · ${sections.recent.length} recent outcomes loaded`
    : `${runs.length} finished runs loaded${hasMore ? ' · Earlier runs available' : ''}` : 'Loading local task records…';
  byId('empty-state').hidden = !currentSnapshot || selected.length > 0 || (scope === 'tasks' && recent.length > 0);
  byId('empty-title').textContent = filtersActive() ? 'No matching loaded runs' : scope === 'history' ? 'No finished runs yet' : expanded ? 'No local tasks yet' : 'No tasks yet';
  byId('empty-description').textContent = filtersActive() ? 'Clear filters or open history to look through earlier runs.' : scope === 'history' ? 'Finished tasks will remain here so you can review their results and earlier attempts.' : expanded ? 'Start a task from a pull request or issue in PowerToys Pulse.' : 'Start a task from a PR or Issue in Pulse.';
  byId('primary-empty').hidden = !currentSnapshot || selected.length > 0 || !byId('empty-state').hidden;
  byId('primary-empty').textContent = scope === 'tasks' ? expanded ? 'No matching active runs. Recent outcomes remain available below.' : snapshotStale ? 'No active tasks in the saved records.' : 'No tasks running' : 'No matching loaded runs. Clear a filter or load earlier records.';
  byId('recent-empty').hidden = !currentSnapshot || recent.length > 0 || !byId('empty-state').hidden;
  byId('recent-empty').textContent = filtersActive() ? 'No matching runs among the latest outcomes. Open history to find earlier results.' : 'Your latest finished runs will appear here, including failed and cancelled attempts.';
  if (!expanded) {
    const idle = scope === 'tasks' && selected.length === 0;
    byId('primary-section').dataset.idle = String(idle);
    byId('primary-section-heading').hidden = idle;
    byId('primary-section').hidden = !currentSnapshot || selected.length === 0 && (!idle || recent.length === 0);
    byId('recent-section').hidden = scope !== 'tasks' || !currentSnapshot || recent.length === 0;
  }
  const visibleIds = new Set([...selected, ...recent].map(run => run.runId));
  const focused = document.activeElement instanceof HTMLElement ? document.activeElement : undefined;
  syncRows(byId('runs'), selected.slice(0, expanded ? undefined : 2));
  syncRows(byId('recent-runs'), recent.slice(0, expanded ? undefined : 2));
  if (focused?.isConnected && document.activeElement !== focused) focused.focus({ preventScroll: true });
  for (const [id, row] of rowCache) if (!visibleIds.has(id) && !row.node.isConnected) rowCache.delete(id);
  byId('popup-more').hidden = expanded || selected.length <= 2;
  if (!expanded) byId('popup-more').textContent = 'More active tasks are available in the extension.';
}

interface TaskRow {
  node: HTMLElement; title: HTMLAnchorElement; state: HTMLElement; description: HTMLElement; summary: HTMLElement;
  context: HTMLElement; meta: HTMLElement; progress: HTMLElement; action: HTMLAnchorElement; signature: string;
}

function taskRow(run: Run): TaskRow {
  let row = rowCache.get(run.runId);
  if (!row) {
    const node = element('article', undefined, 'task-row'); node.dataset.runId = run.runId;
    const body = element('div', undefined, 'task-row-body');
    const heading = element('div', undefined, 'task-row-heading');
    const title = element('a', undefined, 'task-row-title'); title.target = '_blank'; title.rel = 'noopener';
    const state = element('span', undefined, 'task-execution');
    heading.append(icon(run.task.target?.type === 'pr' ? 'pr' : run.task.target?.type === 'issue' ? 'issue' : 'tasks'), title, state);
    const description = element('p', undefined, 'task-request-title');
    const summary = element('p', undefined, 'task-summary');
    const context = element('p', undefined, 'task-context');
    const meta = element('div', undefined, 'task-row-meta');
    const progress = element('div', undefined, 'task-personal-state');
    body.append(heading, description, summary, context, meta, progress);
    const action = element('a', undefined, 'task-row-action'); action.target = '_blank'; action.rel = 'noopener';
    node.append(body, action);
    row = { node, title, state, description, summary, context, meta, progress, action, signature: '' }; rowCache.set(run.runId, row);
  }
  const signature = JSON.stringify(run);
  if (row.signature === signature) return row;
  row.signature = signature;
  const active = isActive(run.status.state);
  row.node.className = `task-row${!active && !run.view.read ? ' task-unread' : ''}`;
  row.title.textContent = taskRowTitle(run);
  row.title.href = chrome.runtime.getURL(`details.html?runId=${encodeURIComponent(run.runId)}`);
  row.title.setAttribute('aria-label', `${taskRowTitle(run)} · Run ${run.runId.slice(0, 8)} · ${date(run.status.endedAt || run.status.createdAt)}`);
  row.state.textContent = taskExecutionLabel(run);
  row.state.className = `task-execution ${run.status.state === 'failed' || run.status.state === 'interrupted' ? 'task-error-state' : active ? 'task-active-state' : ''}`;
  row.description.textContent = run.task.actionId; row.description.hidden = run.task.actionId === row.title.textContent;
  const failure = taskFailureText(run);
  const workflow = workflowOutcome(run);
  row.summary.textContent = failure || (active ? run.status.latestProgress || (run.status.state === 'accepted' ? 'The Host accepted this task and is preparing its worker.' : 'The agent is running locally. Open the activity to follow its progress.') : run.result?.summary || 'No final report was recorded. Review the captured output and diagnostics.');
  row.summary.className = `task-summary${failure ? ' task-summary-error' : ''}`;
  row.context.textContent = taskResultContext(run).join(' · '); row.context.hidden = !row.context.textContent;
  row.meta.replaceChildren(element('span', run.task.repository), element('span', `Run ${run.runId.slice(0, 8)}`), element('span', date(run.status.endedAt || run.status.updatedAt)), element('span', run.config.agent === 'codex' ? 'Codex CLI' : 'Copilot CLI'));
  if (run.task.actionKind === 'pr-review' || run.task.actionKind === 'pr-verify') row.meta.append(element('span', reviewModeLabel(run.task.reviewOptions?.mode)));
  if (run.result?.phase) row.meta.append(element('span', `Phase: ${phaseLabel(run.result.phase)}`));
  if (workflow && workflow !== 'completed') row.meta.append(element('span', `Outcome: ${workflow}`));
  row.progress.replaceChildren(); row.progress.hidden = active;
  if (!active) row.progress.append(element('span', run.view.read ? 'Read' : 'Unread result', run.view.read ? '' : 'task-unread-label'), element('span', run.view.handled ? 'Handled locally' : 'Unhandled locally'));
  const primary = taskPrimaryAction(run); row.action.textContent = primary.label;
  row.action.href = `${row.title.href}#${primary.anchor}`;
  row.action.setAttribute('aria-label', `${primary.label} · ${taskRowTitle(run)} · Run ${run.runId.slice(0, 8)}`);
  return row;
}

function syncRows(container: HTMLElement, selected: Run[]): void {
  const wanted = new Set(selected.map(run => run.runId));
  for (const node of [...container.children]) if (!wanted.has((node as HTMLElement).dataset.runId!)) node.remove();
  selected.forEach((run, index) => {
    const row = taskRow(run).node;
    if (container.children[index] !== row) container.insertBefore(row, container.children[index] ?? null);
  });
}

async function listSnapshot(view: string, limit: number, cursor?: string): Promise<RunList> {
  const payload = { view, limit, ...(cursor ? { cursor } : {}) };
  if (view === 'history' && finishedOrderSupported) {
    try { return await request<RunList>('tasks.list', { ...payload, order: 'finished' }); }
    catch (error) { if (!(error instanceof ProtocolError) || error.code !== 'INVALID_REQUEST') throw error; finishedOrderSupported = false; }
  }
  return request<RunList>('tasks.list', payload);
}

function requestKey(): string { return `${scope}:${target}:${pages}`; }

async function refresh(announce = false): Promise<void> {
  if (busy) return; busy = true;
  const requested = requestKey(); const requestedScope = scope; const requestedPages = pages;
  const requestedView = scope === 'tasks' ? 'tasks' : target === 'pr' ? 'prs' : target === 'issue' ? 'issues' : 'history';
  const refreshButton = byId<HTMLButtonElement>('refresh'); refreshButton.disabled = true;
  refreshButton.dataset.busy = String(!expanded && announce);
  byId('refresh-label').textContent = !expanded && announce ? 'Refreshing…' : 'Refresh';
  byId<HTMLButtonElement>('more').disabled = true;
  byId<HTMLButtonElement>('retry-list').disabled = true;
  if (!loaded || snapshotKey !== `${scope}:${target}`) byId('initial-loading').hidden = false;
  byId('task-content').setAttribute('aria-busy', 'true');
  try {
    let cursor: string | undefined;
    const loadedRuns = new Map<string, Run>();
    const snapshots: RunList[] = [];
    for (let page = 0; page < requestedPages; page++) {
      const snapshot = await listSnapshot(requestedView, 50, cursor); snapshots.push(snapshot);
      for (const run of snapshot.runs) loadedRuns.set(run.runId, run);
      cursor = snapshot.nextCursor || undefined; if (!cursor) break;
    }
    const recent = requestedScope === 'tasks' ? await listSnapshot('history', recentTaskLimit) : undefined;
    if (requested !== requestKey()) return;
    runs = [...loadedRuns.values()]; hasMore = Boolean(cursor); if (recent) recentRuns = recent.runs;
    const latest = recent ?? snapshots[0];
    lastCounts = latest ? { runningCount: latest.runningCount, unreadCount: latest.unreadCount } : undefined;
    snapshotStale = false;
    // The existing Host counts exclude unlinked history. Only a complete history
    // response establishes an exact total; a paginated/partial response stays unnumbered.
    recentTotal = recent && !recent.nextCursor && !recent.errors?.length ? recent.runs.filter(item => target === 'all' || item.task.target?.type === target).length : undefined;
    if (expanded) byId('finished-counts').textContent = `${latest?.prCount ?? 0} finished PR runs · ${latest?.issueCount ?? 0} finished Issue runs · Stored locally`;
    lastUpdated = new Date().toLocaleTimeString('en-US', { hour12: false });
    if (expanded) byId('updated').textContent = `Updated ${lastUpdated}`;
    const errors = [...new Set([...snapshots, ...(recent ? [recent] : [])].flatMap(snapshot => snapshot.errors ?? []).map(item => typeof item === 'string' ? item : JSON.stringify(item)))];
    byId('record-errors').hidden = !errors.length;
    byId('record-error-details').textContent = errors.join('\n');
    byId('snapshot-message').hidden = true; clearError();
    const currentSnapshot = new Map([...runs, ...recentRuns].map(run => [run.runId, JSON.stringify(run)]));
    const changed = [...currentSnapshot].filter(([id, signature]) => previousRunSnapshot.get(id) !== signature).length;
    if (loaded && changed) byId('task-announcement').textContent = `${changed} ${changed === 1 ? 'task updated' : 'tasks updated'}.`;
    else if (announce || !loaded) byId('task-announcement').textContent = announce ? 'Task records refreshed.' : 'Task records loaded.';
    previousRunSnapshot = currentSnapshot; loaded = true;
    snapshotKey = `${scope}:${target}`;
    byId('initial-loading').hidden = true;
    render();
  } catch (error) {
    if (requested !== requestKey()) return;
    snapshotStale = true;
    if (expanded) report(error); else clearError();
    byId('initial-loading').hidden = true;
    byId('snapshot-message').hidden = false;
    byId('snapshot-text').textContent = !expanded ? loaded && snapshotKey === `${scope}:${target}` ? 'Could not refresh. Showing saved records; their status may have changed.' : 'Local tasks are unavailable. Reconnect to the Host, then select Refresh.' : loaded && snapshotKey === `${scope}:${target}` ? `Showing the last loaded records from ${lastUpdated}. Refresh to check for updates.` : 'Local tasks are unavailable. Reconnect to the Host to load your records.';
    if (!expanded) render();
    if (error instanceof ProtocolError && error.code === 'CURSOR_EXPIRED') { pages = 1; saveLocation(); }
  } finally {
    busy = false; refreshButton.disabled = false; refreshButton.dataset.busy = 'false'; byId('refresh-label').textContent = 'Refresh';
    byId<HTMLButtonElement>('more').disabled = false;
    byId<HTMLButtonElement>('retry-list').disabled = false;
    byId('task-content').setAttribute('aria-busy', 'false');
    await connectionStatus(); if (requested !== requestKey()) void refresh();
  }
}
render();
void refresh();
setInterval(() => { void refresh(); }, 3_000);
