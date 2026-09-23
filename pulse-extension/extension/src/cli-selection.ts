import type { AgentProbe } from './types.js';

export interface CliOption { value: string; label: string; disabled: boolean; missing?: boolean }

function pathKey(path: string): string {
  return path.replaceAll('\\', '/').toLowerCase();
}

function installationPath(installation: AgentProbe): string {
  return installation.resolvedPath || installation.path;
}

export function selectedInstallation(installations: readonly AgentProbe[], selectedPath: string): AgentProbe | undefined {
  if (!selectedPath) return undefined;
  const selectedKey = pathKey(selectedPath);
  // Prefer the observed or canonical path over another installation's alias.
  return installations.find(installation => [installation.path, installation.resolvedPath].some(path => path && pathKey(path) === selectedKey))
    ?? installations.find(installation => installation.aliases?.some(path => pathKey(path) === selectedKey));
}

export function cliOptions(installations: readonly AgentProbe[], selectedPath: string): CliOption[] {
  const options: CliOption[] = [{ value: '', label: 'Choose installation', disabled: false }];
  const selected = selectedInstallation(installations, selectedPath);
  const selectedKey = selected ? pathKey(installationPath(selected)) : undefined;
  const unique = new Map<string, AgentProbe>();
  for (const installation of installations) {
    const path = installationPath(installation);
    if (!path) continue;
    const key = pathKey(path);
    // Host normally groups aliases. Keep repeated canonical paths from creating duplicate choices.
    if (!unique.has(key) || installation === selected) unique.set(key, installation);
  }
  for (const [key, installation] of unique) {
    const sources = [...new Set([...(installation.sources ?? []), ...(installation.source ? [installation.source] : [])].filter(Boolean))];
    const label = [installation.version || 'Version unknown', sources.join(', ') || 'Detected locally', installationPath(installation)];
    options.push({ value: key === selectedKey ? selectedPath : installationPath(installation), label: label.join(' · '), disabled: false });
  }
  if (selectedPath && !selected) options.push({ value: selectedPath, label: `${selectedPath} · Not detected`, disabled: true, missing: true });
  return options;
}
