import { request } from './ui.js';
import type { Result, ResultPage, Run } from './types.js';

const paths = new Set(['findings', 'nextActions', 'validation', 'artifacts', 'verificationEvidence', 'diagnostics', 'review.suggestions', 'plans',
  'report.coverage', 'report.limitations', 'reviewConclusion.blockingUncertainties', 'e2eAssessment.scenarios', 'e2eAssessment.expectedResults', 'e2eAssessment.prerequisites', 'e2eAssessment.evidence',
  'featureAssessment.reasons', 'featureAssessment.evidence', 'featureAssessment.acceptanceCriteria', 'featureAssessment.questions', 'featureAssessment.alternatives',
  'bugAssessment.reasons', 'bugAssessment.evidence', 'bugAssessment.questions', 'bugAssessment.reproduction.steps', 'bugAssessment.reproduction.evidence']);
const cache = new Map<string, { fingerprint: string; arrays: Map<string, unknown[]> }>();

export function hasCompleteResultCache(run: Run): boolean {
  const paging = run.resultPaging; const saved = cache.get(run.runId);
  return Boolean(paging && saved?.fingerprint === paging.fingerprint && paging.sections.every(section => saved.arrays.get(section.path)?.length === section.total));
}
type PageReader = (payload: { runId: string; path: string; offset: number; fingerprint: string }) => Promise<ResultPage>;

function location(result: Result, path: string): { parent: Record<string, unknown>; key: string; value: unknown[] } {
  if (!paths.has(path) || path.split('.').some(part => ['__proto__', 'constructor', 'prototype'].includes(part))) throw new Error('The Host returned an unsupported result section. The full report could not be loaded.');
  const segments = path.split('.'); const key = segments.pop()!;
  let value: unknown = result;
  for (const segment of segments) {
    if (!value || typeof value !== 'object' || Array.isArray(value) || !Object.hasOwn(value, segment)) throw new Error('A required result section is missing. Refresh the report.');
    value = (value as Record<string, unknown>)[segment];
  }
  if (!value || typeof value !== 'object' || Array.isArray(value) || !Object.hasOwn(value, key) || !Array.isArray((value as Record<string, unknown>)[key])) throw new Error('A paged result section is unavailable. The report is not complete.');
  return { parent: value as Record<string, unknown>, key, value: (value as Record<string, unknown>)[key] as unknown[] };
}

/** Fetch every bound array page before exposing a complete report. No finding or proposal is sliced away. */
export async function loadFullResult(run: Run, read: PageReader = payload => request<ResultPage>('tasks.resultPage', payload)): Promise<Run> {
  const paging = run.resultPaging;
  if (!paging) return run;
  if (!run.result || !paging.fingerprint || !Array.isArray(paging.sections)) throw new Error('The full saved result is unavailable. Refresh to load its report.');
  const full = structuredClone(run); const result = full.result!;
  const previous = cache.get(run.runId);
  const arrays = new Map<string, unknown[]>(); const visitedPaths = new Set<string>();
  for (const section of paging.sections) {
    if (!section || visitedPaths.has(section.path) || !Number.isSafeInteger(section.total) || section.total < 0) throw new Error('The Host returned inconsistent result pagination. No partial report is shown.');
    visitedPaths.add(section.path);
    const current = location(result, section.path);
    if (current.value.length > section.total || section.nextOffset === null && current.value.length !== section.total || section.nextOffset !== null && section.nextOffset !== current.value.length) throw new Error('The initial report page is incomplete or inconsistent. Refresh this task.');
    const cached = previous?.fingerprint === paging.fingerprint ? previous.arrays.get(section.path) : undefined;
    let values: unknown[];
    if (cached?.length === section.total) {
      values = structuredClone(cached);
      for (let index = 0; index < current.value.length; index++) values[index] = current.value[index];
    } else {
      values = [...current.value]; let offset = section.nextOffset;
      while (offset !== null) {
        if (!Number.isSafeInteger(offset) || offset !== values.length || offset >= section.total) throw new Error('The result page cursor is invalid. The full report could not be loaded.');
        const page = await read({ runId: run.runId, path: section.path, offset, fingerprint: paging.fingerprint });
        if (!page || page.fingerprint !== paging.fingerprint || page.total !== section.total || !Array.isArray(page.items) || !page.items.length || values.length + page.items.length > section.total ||
          page.nextOffset !== null && page.nextOffset !== values.length + page.items.length) throw new Error('The saved report changed or a result page was incomplete. Refresh to load one consistent report.');
        values.push(...page.items); offset = page.nextOffset;
      }
    }
    if (values.length !== section.total) throw new Error('Some saved result items could not be read. The final report remains unavailable.');
    current.parent[current.key] = values; arrays.set(section.path, structuredClone(values));
  }
  if (Array.isArray(result.nextActions)) result.nextSteps = structuredClone(result.nextActions);
  if (Array.isArray(result.diagnostics)) result.blockers = result.diagnostics.filter(item => item.severity === 'error').map(item => item.message);
  delete full.resultPaging;
  cache.set(run.runId, { fingerprint: paging.fingerprint, arrays });
  return full;
}
