import type { Run } from './types.js';

export interface DraftStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

const prefix = 'pulse.result-draft.v1:';

/** A new run, target, revision, or editor purpose always starts an independent draft. */
export function resultDraftScope(run: Pick<Run, 'runId' | 'task'>, purpose: string): string {
  const { repository, target, expectedHeadSha } = run.task;
  return JSON.stringify([run.runId, repository.toLowerCase(), target?.type ?? null, target?.number ?? null, expectedHeadSha?.toLowerCase() ?? null, purpose]);
}

function browserStorage(storage?: DraftStorage): DraftStorage | undefined {
  try { return storage ?? globalThis.localStorage; } catch { return undefined; }
}

/** Storage is draft data only. Callers validate their own shape before restoring it. */
export function readDraft<T = unknown>(scope: string, storage?: DraftStorage): T | undefined {
  try {
    const value = browserStorage(storage)?.getItem(prefix + scope);
    if (!value) return undefined;
    const saved: unknown = JSON.parse(value);
    if (!saved || typeof saved !== 'object' || !('version' in saved) || saved.version !== 1 || !('data' in saved)) return undefined;
    return saved.data as T;
  } catch { return undefined; }
}

/** A storage failure never clears the caller's current in-memory editor. */
export function writeDraft(scope: string, data: unknown, storage?: DraftStorage): boolean {
  try {
    const target = browserStorage(storage); if (!target) return false;
    target.setItem(prefix + scope, JSON.stringify({ version: 1, data }));
    return true;
  } catch { return false; }
}

export function deleteDraft(scope: string, storage?: DraftStorage): boolean {
  try {
    const target = browserStorage(storage); if (!target) return false;
    target.removeItem(prefix + scope); return true;
  } catch { return false; }
}
