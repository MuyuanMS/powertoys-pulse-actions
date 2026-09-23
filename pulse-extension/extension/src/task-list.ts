import { isActive } from './policy.js';
import { observedExecutionValue, scopedReviewConclusion, textValue, workflowOutcome } from './details-model.js';
import type { Run, TaskActionKind } from './types.js';

export const recentTaskLimit = 5;

export type TaskTargetFilter = 'all' | 'pr' | 'issue';
export interface TaskFilters { target: TaskTargetFilter; taskKind: string; search: string }

// These are filters over a known snapshot, never a claim to search unread pages.
export function filterLoadedTasks(runs: readonly Run[], filters: TaskFilters): Run[] {
  const words = filters.search.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
  return runs.filter(run => {
    if (filters.target !== 'all' && run.task.target?.type !== filters.target) return false;
    if (filters.taskKind && run.task.actionKind !== filters.taskKind) return false;
    const searchable = [run.runId, run.task.actionId, run.task.actionKind, run.task.repository,
      run.task.target ? `${run.task.target.type} #${run.task.target.number}` : 'Unlinked historical record',
      run.task.expectedHeadSha, run.result?.assessment?.revisionSha, run.result?.summary].join(' ').toLocaleLowerCase();
    return words.every(word => searchable.includes(word));
  });
}

export const taskTypeLabels: Record<TaskActionKind, string> = {
  'pr-review': 'PR review', 'pr-verify': 'PR verification', 'issue-fix': 'Issue fix',
  'reproduction-setup': 'Reproduction setup', e2e: 'E2E validation', 'feature-research': 'Feature research',
  'bug-investigation': 'Bug investigation', 'feature-implement': 'Feature implementation', 'issue-verify': 'Issue verification',
};

export function taskRowTitle(run: Run): string {
  const kind = taskTypeLabels[run.task.actionKind] ?? run.task.actionKind;
  return run.task.target ? `${kind} · #${run.task.target.number}` : `${kind} · Unlinked historical record`;
}

export function taskExecutionLabel(run: Run): string {
  if (run.status.state === 'accepted') return 'Accepted · Preparing worker';
  if (run.status.state === 'running') return 'Running';
  return run.status.state === 'succeeded' ? 'Finished' : run.status.state === 'failed' ? 'Failed' : run.status.state === 'interrupted' ? 'Interrupted' : 'Cancelled';
}

export function taskPrimaryAction(run: Run): { label: string; anchor: string } {
  if (isActive(run.status.state)) return { label: 'View activity', anchor: 'execution-logs' };
  if (taskFailureText(run) || !run.result || run.status.state === 'cancelled') return { label: 'Review task', anchor: 'result-workspace' };
  return { label: 'Review result', anchor: 'result-workspace' };
}

export function taskResultContext(run: Run): string[] {
  if (isActive(run.status.state)) return [];
  const context: string[] = [];
  const code = scopedReviewConclusion(run);
  if (code) context.push(code.status === 'no-blocking-findings' ? 'Code: no blocking findings' : code.status === 'changes-requested' ? 'Code: changes requested' : 'Code: inconclusive');
  const assessment = run.result?.assessment;
  if (assessment) {
    const subject = assessment.subject === 'original-pr' ? 'Original PR' : assessment.subject === 'local-candidate' ? 'Local candidate' : 'Target';
    const outcome = assessment.status === 'passed' ? 'passed' : assessment.status === 'failed' ? 'issues found' : 'inconclusive';
    context.push(`${subject}: ${outcome}${assessment.revisionSha ? ` · ${assessment.revisionSha.slice(0, 8)}` : ''}`);
  } else if (run.task.target?.type === 'pr' && run.task.expectedHeadSha) context.push(`PR revision ${run.task.expectedHeadSha.slice(0, 8)}`);
  return context;
}

export function taskSections(activeRuns: readonly Run[], finishedRuns: readonly Run[]): { active: Run[]; recent: Run[] } {
  const finished = new Map<string, Run>();
  for (const run of finishedRuns) if (!isActive(run.status.state)) finished.set(run.runId, run);
  const recent = [...finished.values()].sort((first, second) => {
    const firstTime = first.status.endedAt || first.status.updatedAt || first.status.createdAt;
    const secondTime = second.status.endedAt || second.status.updatedAt || second.status.createdAt;
    return secondTime.localeCompare(firstTime) || second.runId.localeCompare(first.runId);
  }).slice(0, recentTaskLimit);
  // The later history snapshot wins when a run completes between the two reads.
  const active = activeRuns.filter(run => isActive(run.status.state) && !finished.has(run.runId));
  return { active, recent };
}

export function taskFailureText(run: Run): string | undefined {
  if (isActive(run.status.state)) return undefined;
  const error = run.status.error;
  if (textValue(error?.message)) return `${error!.message}${textValue(error?.guidance) ? ` ${error!.guidance}` : ''}${textValue(error?.code) ? ` [${error!.code}]` : ''}`;
  const outcome = workflowOutcome(run);
  if (run.result?.schemaVersion === 2 && outcome !== 'completed' && outcome === run.result.outcome) return textValue(run.result.summary) ?? run.result.diagnostics?.find(item => item.severity === 'error')?.message;
  return run.status.state === 'failed' ? 'Task failed. Open the details and execution logs to inspect the error.' : run.status.state === 'interrupted' ? 'Task interrupted. Open the details and execution logs to inspect its last recorded state.' : undefined;
}

export function executionChips(run: Run): { label: string; title: string }[] {
  const observed = run.status.observedExecution;
  const observedTitle = (field: 'model' | 'reasoningEffort'): string => textValue(observed?.[field]) ? `Reported by the CLI (${observed!.source}) at ${observed!.observedAt}.` : isActive(run.status.state) ? 'Waiting for the CLI to report this execution detail.' : 'The CLI did not record this execution detail.';
  return [
    { label: `Agent: ${run.config.agent === 'codex' ? 'Codex CLI' : 'Copilot CLI'}`, title: 'CLI launched for this run.' },
    { label: `Model: ${observedExecutionValue(run, 'model')}`, title: observedTitle('model') },
    { label: `Reasoning: ${observedExecutionValue(run, 'reasoningEffort')}`, title: observedTitle('reasoningEffort') },
  ];
}
