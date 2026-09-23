import { ProtocolError, safeHttpsUrl } from './policy.js';
import type { TaskActionKind, Connection, Reply, RunState } from './types.js';

export function element<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string, className?: string): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}
const iconPaths = {
  tasks: ['M4 2.5h8a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-10a1 1 0 0 1 1-1Z', 'M6 1.5h4v3H6z', 'M6 8h4M6 11h4'],
  running: ['M1 8h3l2-5 4 10 2-5h3'],
  pending: ['M2 2.5h12v11H2z', 'M2 9h3l1 2h4l1-2h3'],
  history: ['M2 5a6 6 0 1 1 0 6', 'M2 1.5V5h3.5', 'M8 4.5V8l2.5 1.5'],
  settings: ['M6.5 1.5h3l.5 2 1.5.9 2-.5 1.5 2.6-1.5 1.5v1.8l1.5 1.5-1.5 2.6-2-.5-1.5.9-.5 2h-3l-.5-2-1.5-.9-2 .5L1 10.8l1.5-1.5V7.5L1 6l1.5-2.6 2 .5L6 3z', 'M10 8.3a2 2 0 1 1-4 0 2 2 0 0 1 4 0Z'],
  external: ['M9 2h5v5', 'M14 2 7 9', 'M6 3H2v11h11v-4'],
  repo: ['M3 1.5h10v13H4a2 2 0 0 1-2-2v-9a2 2 0 0 1 2-2', 'M2 11.5h11', 'M6 2v5l1.5-1L9 7V2'],
  pr: ['M4 4v8M12 12V6a3 3 0 0 0-3-3H8', 'm10 1-2 2 2 2', 'M5.5 2.5a1.5 1.5 0 1 1-3 0 1.5 1.5 0 0 1 3 0ZM5.5 13.5a1.5 1.5 0 1 1-3 0 1.5 1.5 0 0 1 3 0ZM13.5 13.5a1.5 1.5 0 1 1-3 0 1.5 1.5 0 0 1 3 0Z'],
  issue: ['M14 8A6 6 0 1 1 2 8a6 6 0 0 1 12 0Z', 'M8 5v3.5M8 11h.01'],
  folder: ['M1.5 4V2.5h5L8 4h6.5v9.5h-13z'],
  refresh: ['M13 5a5.5 5.5 0 1 0 .4 5', 'M13 1.5V5H9.5'],
} as const;
export type IconName = keyof typeof iconPaths;
export function icon(name: IconName): SVGSVGElement {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  for (const [key, value] of Object.entries({ viewBox: '0 0 16 16', width: '16', height: '16', fill: 'none', stroke: 'currentColor', 'stroke-width': '1.35', 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true', class: 'icon' })) svg.setAttribute(key, value);
  for (const d of iconPaths[name]) { const path = document.createElementNS(svg.namespaceURI, 'path'); path.setAttribute('d', d); svg.append(path); }
  return svg;
}
export function byId<T extends HTMLElement = HTMLElement>(id: string): T {
  const node = document.getElementById(id);
  if (!node) throw new Error(`Missing UI element #${id}`);
  return node as T;
}
export async function request<T>(type: string, payload: unknown = {}): Promise<T> {
  const reply = await chrome.runtime.sendMessage({ channel: 'pulse-ui', type, payload }) as Reply<T> | undefined;
  if (!reply) throw new ProtocolError('HOST_UNAVAILABLE', 'The extension background did not respond.', 'Reopen the extension task panel.');
  if (!reply.ok) throw new ProtocolError(reply.error.code, reply.error.message, reply.error.guidance);
  return reply.data;
}
export function errorText(error: unknown): string {
  return error instanceof ProtocolError ? `${error.message}${error.guidance ? ` ${error.guidance}` : ''} [${error.code}]` : error instanceof Error ? error.message : 'An unknown error occurred.';
}
export function report(error: unknown, node = byId('error')): void { node.textContent = errorText(error); node.hidden = false; }
export function clearError(node = byId('error')): void { node.hidden = true; node.textContent = ''; }
export function date(value?: string): string {
  if (!value) return '—';
  const timestamp = new Date(value);
  return Number.isNaN(timestamp.getTime()) ? value : timestamp.toLocaleString('en-US', { hour12: false });
}
export const states: Record<RunState, string> = { accepted: 'Accepted', running: 'Running', succeeded: 'Completed', failed: 'Failed', cancelled: 'Cancelled', interrupted: 'Interrupted' };
export const actionLabels: Record<TaskActionKind, string> = { 'pr-review': 'PR review', 'pr-verify': 'Additional verification', 'issue-fix': 'Issue fix', 'reproduction-setup': 'Reproduction setup', e2e: 'E2E validation', 'feature-research': 'Feature research', 'bug-investigation': 'Bug investigation', 'feature-implement': 'Feature implementation', 'issue-verify': 'Issue verification' };
export function badge(state: RunState): HTMLElement { return element('span', states[state] ?? state, `status ${state}`); }
export function link(label: string, url: string | undefined): HTMLElement {
  const safe = safeHttpsUrl(url);
  if (!safe) return element('span', label);
  const anchor = element('a', label); anchor.href = safe; anchor.target = '_blank'; anchor.rel = 'noopener noreferrer';
  return anchor;
}
export function button(label: string, action: () => void | Promise<void>, className?: string): HTMLButtonElement {
  const node = element('button', label, className); node.type = 'button';
  node.addEventListener('click', () => {
    node.disabled = true;
    void Promise.resolve().then(action).catch(error => report(error)).finally(() => { node.disabled = false; });
  });
  return node;
}
export function labeledValue(label: string, value: string): HTMLElement {
  const wrapper = element('div', undefined, 'fact'); wrapper.append(element('dt', label), element('dd', value)); return wrapper;
}
export async function connectionStatus(): Promise<void> {
  const node = byId('connection');
  const compact = node.dataset.compact === 'true';
  try {
    const connection = await request<Connection>('connection.status');
    node.className = `connection ${connection.state}`;
    const description = connection.state === 'connected' ? 'Connected to local Host' : connection.state === 'connecting' ? 'Connecting to local Host…' : `Host disconnected${!compact && connection.lastConnectedAt ? ` · Last connected ${date(connection.lastConnectedAt)}` : ''}. Showing the last known state. Local tasks continue independently.`;
    node.textContent = compact ? connection.state === 'connected' ? 'Connected' : connection.state === 'connecting' ? 'Connecting…' : 'Disconnected' : description;
    node.setAttribute('aria-label', description);
  } catch (error) { node.textContent = compact ? 'Disconnected' : errorText(error); node.setAttribute('aria-label', errorText(error)); node.className = 'connection disconnected'; }
}
export function initNavigation(options: { compactPopup?: boolean } = {}): void {
  const page = location.pathname.split('/').pop();
  const brand = (className: string): HTMLAnchorElement => {
    const link = element('a', undefined, className); link.href = chrome.runtime.getURL('popup.html?expanded=1');
    const logo = element('img'); logo.src = chrome.runtime.getURL('powertoys-logo.png'); logo.alt = ''; logo.width = logo.height = 24;
    const name = element('span', undefined, 'brand-name'); name.append(document.createTextNode('PowerToys '), element('strong', 'Pulse'));
    link.append(logo, name); return link;
  };
  const sidebar = element('aside', undefined, 'sidebar'); sidebar.setAttribute('aria-label', 'Local workspace navigation');
  sidebar.append(brand('brand'), element('p', 'Local workspace', 'brand-caption'));
  const section = element('div', undefined, 'sidebar-section'); section.append(element('div', 'Workspace', 'sidebar-label'));
  const nav = element('nav', undefined, 'side-nav'); nav.setAttribute('aria-label', 'Workspace');
  const tasks = element('a', undefined, 'nav-item'); tasks.href = chrome.runtime.getURL('popup.html?expanded=1&view=tasks');
  tasks.append(icon('tasks'), element('span', 'Tasks'));
  if (page !== 'options.html') tasks.setAttribute('aria-current', 'page');
  nav.append(tasks);
  section.append(nav, element('div', 'Configuration', 'sidebar-label'));
  const settings = element('a', undefined, 'nav-item'); settings.href = chrome.runtime.getURL('options.html'); settings.append(icon('settings'), element('span', 'Settings'));
  if (page === 'options.html') settings.setAttribute('aria-current', 'page');
  section.append(settings); sidebar.append(section);
  const footer = element('div', undefined, 'sidebar-footer');
  const pulse = element('a', undefined, 'nav-item'); pulse.href = 'https://cautious-memory-r38ze9j.pages.github.io'; pulse.target = '_blank'; pulse.rel = 'noopener noreferrer'; pulse.append(icon('external'), element('span', 'Open PowerToys Pulse'));
  footer.append(pulse, element('p', 'Runs locally · Chrome / Edge', 'muted fine')); sidebar.append(footer);
  const workspace = element('div', undefined, 'workspace');
  const compactBrand = brand('compact-brand');
  if (options.compactPopup) {
    compactBrand.target = '_blank'; compactBrand.rel = 'noopener noreferrer';
    const brandbar = element('div', undefined, 'task-popup-brandbar');
    const connection = byId('connection'); connection.dataset.compact = 'true'; connection.textContent = 'Connecting…';
    brandbar.append(compactBrand, connection); workspace.append(brandbar);
  } else workspace.append(compactBrand);
  const header = document.querySelector('body > .topbar'); const main = document.querySelector('body > main');
  if (header) workspace.append(header); if (main) workspace.append(main);
  const shell = element('div', undefined, 'app-shell'); shell.append(sidebar, workspace); document.body.append(shell);
  for (const node of document.querySelectorAll<HTMLButtonElement>('[data-options]')) { node.prepend(icon('settings')); node.addEventListener('click', () => { void chrome.runtime.openOptionsPage(); }); }
  for (const node of document.querySelectorAll<HTMLButtonElement>('[data-panel]')) { node.prepend(icon('external')); node.addEventListener('click', () => { void chrome.tabs.create({ url: chrome.runtime.getURL('popup.html?expanded=1') }); }); }
  const refresh = document.getElementById('refresh'); if (refresh) refresh.prepend(icon('refresh'));
}
