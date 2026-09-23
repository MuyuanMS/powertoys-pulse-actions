import type { Config } from './types.js';

const pathKey = (value: string): string => value.trim().replaceAll('/', '\\').replace(/\\+$/, '').toLowerCase();

/** Compare only persisted settings; diagnostic results never make a draft dirty. */
export function configValues(config: Config): Record<string, string> {
  return {
    'Default agent': config.agent,
    'Codex installation': pathKey(config.cliSelections?.codex ?? ''),
    'Copilot installation': pathKey(config.cliSelections?.copilot ?? ''),
    'Codex model': config.agentDefaults?.codex?.model?.trim() ?? '',
    'Codex reasoning effort': config.agentDefaults?.codex?.reasoningEffort ?? '',
    'Copilot model': config.agentDefaults?.copilot?.model?.trim() ?? '',
    'Copilot reasoning effort': config.agentDefaults?.copilot?.reasoningEffort ?? '',
    'Task access': config.permission,
    'Main repository folder': pathKey(config.mainRepoFolder),
    'Worktree root folder': pathKey(config.worktreeRoot),
    'GitHub account': config.githubAccount.trim().toLowerCase(),
    'Review prompt': config.prPrompt ?? '',
    'Local fix prompt': config.issuePrompt ?? '',
    'E2E prompt': config.e2ePrompt ?? 'powertoys-pr-e2e-test.prompt.md',
    'Reproduction prompt': config.reproductionPrompt ?? '',
  };
}

export function changedSettings(saved: Config, draft: Config): string[] {
  const previous = configValues(saved);
  return Object.entries(configValues(draft)).filter(([key, value]) => previous[key] !== value).map(([key]) => key);
}

export function isConfig(value: unknown): value is Config {
  if (!value || typeof value !== 'object') return false;
  const config = value as Partial<Config>;
  return (config.agent === 'codex' || config.agent === 'copilot')
    && ['read-only', 'workspace-write', 'yolo'].includes(config.permission ?? '')
    && ['mainRepoFolder', 'worktreeRoot', 'githubAccount', 'prPrompt', 'issuePrompt'].every(key => typeof (value as Record<string, unknown>)[key] === 'string');
}

export function saveOutcomeUnconfirmed(error: unknown): boolean {
  const code = error && typeof error === 'object' && 'code' in error ? String(error.code) : '';
  return !code || ['HOST_DISCONNECTED', 'HOST_UNAVAILABLE', 'RESPONSE_UNKNOWN', 'PROTOCOL_MISMATCH'].includes(code);
}

export function settingsErrorField(code: string, message = ''): string | undefined {
  if (['REPO_PATH_INVALID', 'MAIN_REPO_REQUIRED', 'REPOSITORY_MISMATCH'].includes(code)) return 'main-repo-folder';
  if (code === 'WORKTREE_PATH_INVALID') return 'worktree-root';
  if (code === 'GITHUB_ACCOUNT_UNAVAILABLE') return 'github-account';
  if (code === 'INVALID_CONFIG' && message.includes('mainRepoFolder')) return 'main-repo-folder';
  if (code === 'INVALID_CONFIG' && message.includes('worktreeRoot')) return 'worktree-root';
  return undefined;
}
