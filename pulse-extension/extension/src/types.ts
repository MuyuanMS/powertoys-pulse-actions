export const protocolVersion = 1;
export type Agent = 'codex' | 'copilot';
export interface AgentProfile { model: string; reasoningEffort: string }
export type AgentProfiles = Record<Agent, AgentProfile>;
export interface TaskExecution { agent?: Agent; model?: string; reasoningEffort?: string }
export interface ExecutionSource { agent: 'task' | 'default'; model: 'task' | 'default' | 'cli'; reasoningEffort: 'task' | 'default' | 'cli' }
export interface ObservedExecution { model?: string; reasoningEffort?: string; source: 'codex-turn-context' | 'cli-event'; observedAt: string }
export interface AgentDefaults { defaultAgent: Agent; defaults: AgentProfiles; reasoningEfforts: Record<Agent, string[]> }
export interface AgentProbe {
  path: string; available: boolean; version?: string | null; error?: string | PulseError;
  source?: string; sources?: string[]; resolvedPath?: string; aliases?: string[];
  capabilities?: { model?: boolean; reasoningEffort?: boolean };
}
export interface AgentInstallations { installations: Record<Agent, AgentProbe[]>; selections: Record<Agent, string> }
export type ActionKind = 'issue-fix' | 'pr-review' | 'reproduction-setup' | 'e2e' | 'feature-research' | 'bug-investigation';
export type TaskActionKind = ActionKind | 'pr-verify' | 'feature-implement' | 'issue-verify';
export type ReviewMode = 'static' | 'build-tests' | 'ui-e2e';
export interface ReviewOptions { mode: ReviewMode }
export type OperationKind = 'approve' | 'requestChanges' | 'suggestChanges' | 'comment' | 'close';
export type RunState = 'accepted' | 'running' | 'succeeded' | 'failed' | 'cancelled' | 'interrupted';
export interface PulseError { code: string; message: string; guidance?: string }
export type Reply<T = unknown> = { id: string; protocolVersion: 1; ok: true; data: T } | { id: string; protocolVersion: 1; ok: false; error: PulseError };
export interface NativeRequest { id: string; protocolVersion: 1; type: string; payload: unknown }
export interface Task {
  requestId: string; actionId: string; actionKind: TaskActionKind; repository: string;
  target?: { type: 'issue' | 'pr'; number: number }; expectedHeadSha?: string;
  context?: unknown; prompt: string; execution?: TaskExecution; reviewOptions?: ReviewOptions;
  followUp?: { parentRunId: string; recommendationId: string; parentResultFingerprint?: string; subject: 'original-pr'; revisionSha: string };
  planSource?: { parentRunId: string; parentResultFingerprint: string; proposalId: string; planId: string; repository: string; target: { type: 'pr' | 'issue'; number: number }; revisionSha: string | null; sourceKind?: 'local-candidate'; candidateSnapshotHash?: string };
}
export interface Config {
  agent: Agent;
  agentDefaults: AgentProfiles;
  cliSelections: Record<Agent, string>;
  permission: 'read-only' | 'workspace-write' | 'yolo';
  mainRepoFolder: string; worktreeRoot: string; githubAccount: string;
  prPrompt: string; issuePrompt: string; e2ePrompt: string; reproductionPrompt: string;
}
export interface PromptMetadata { actionKind?: ActionKind; schemaVersion?: number; source?: string; sourceUrl?: string; revision?: string; hashAlgorithm?: string; requiredChecks?: string[] }
export interface PromptEntry extends PromptMetadata { name: string; title: string; description?: string; path?: string; sha: string; appliesTo: 'pr' | 'issue' | 'both' }
export interface PromptCatalog extends PromptMetadata { prompts: PromptEntry[]; syncedAt?: string; sourceUrl: string; error?: string | PulseError }
export interface PromptContent extends PromptMetadata { name: string; title: string; content: string; sha: string }
export interface AgentTest {
  testId: string; agent: Agent; state: 'running' | 'succeeded' | 'failed' | 'cancelled';
  reply?: string; path?: string; version?: string; error?: string | PulseError;
}
export interface GitHubAccounts {
  available: boolean;
  accounts: { login: string; active: boolean; state: string; available?: boolean }[];
  error?: string | PulseError;
}
export interface Capabilities {
  hostVersion: string; protocolVersion: number;
  reviewModes?: ReviewMode[];
  workflowKinds?: ActionKind[]; resultSchemaVersions?: (1 | 2 | 3)[];
  agents: Record<Agent, { available: boolean; path?: string | null; version?: string | null; error?: string | PulseError }>;
  github: { available: boolean; account?: string | null; error?: string | PulseError };
}
export interface Suggestion { id?: string; path: string; line: number; startLine?: number; side: 'RIGHT'; body: string; replacement: string }
export type ResultOutcome = 'completed' | 'blocked' | 'failed' | 'cancelled' | 'interrupted';
export type ResultPhase = 'setup' | 'analysis' | 'implementation' | 'validation' | 'reporting';
export type ResultActionKind = 'viewChanges' | 'inspectResult' | 'openTarget' | 'configure' | 'rerun' | 'approve' | 'suggestChanges' | 'requestChanges' | 'comment' | 'close' | 'create-pr' | 'merge-pr' | 'trigger-ci' | 'start-task' | 'close-as-duplicate' | 'none';
export type ResultPlanKind = 'feature-implement' | 'issue-fix' | 'reproduction-setup' | 'issue-verify';
export interface FinalReport { complete: boolean; rechecked: boolean; coverage: string[]; limitations: string[] }
export interface IssueReference { repository: string; number: number; url: string }
export interface ResultPlan { id: string; kind: ResultPlanKind; summary: string; steps: string[]; acceptanceCriteria: string[]; prerequisites: string[]; evidence: string[] }
export interface E2eAssessment { level: 'not_needed' | 'recommended' | 'required'; reason: string; question: string; scenarios: string[]; expectedResults: string[]; prerequisites: string[]; evidence: string[]; readiness: 'ready' | 'missing-prerequisites' | 'unknown' }
export interface FeatureAssessment { status: 'ready' | 'needs_information' | 'needs_decision' | 'already_supported' | 'duplicate' | 'not_feasible'; summary: string; reasons: string[]; evidence: string[]; acceptanceCriteria: string[]; questions: string[]; alternatives: string[]; relatedIssue: IssueReference | null; planId: string | null }
export interface BugAssessment { status: 'confirmed' | 'needs_information' | 'needs_verification' | 'already_fixed' | 'duplicate' | 'not_a_bug'; summary: string; reasons: string[]; evidence: string[]; questions: string[]; relatedIssue: IssueReference | null; planId: string | null; reproduction: { status: 'reproduced' | 'not_reproduced' | 'not_run' | 'blocked'; revisionSha: string | null; environment: string; steps: string[]; expected: string; observed: string; evidence: string[] } }
export interface ResultRecommendation { kind: 'approve' | 'address-findings' | 'run-e2e' | 'incomplete' | ResultActionKind; reason: string; proposalId?: string; planId?: string }
export interface ResultPullRequest { head: string; base: string; title: string; body: string; draft: boolean; sourceHeadSha?: string }
export interface ActionAvailability { enabled: boolean; reasons: string[] }
export interface ResultAssessment { subject: 'original-pr' | 'local-candidate' | 'target'; status: 'passed' | 'failed' | 'inconclusive'; summary: string; revisionSha: string | null }
export interface ReviewConclusion { status: 'no-blocking-findings' | 'changes-requested' | 'inconclusive'; summary: string; revisionSha: string | null; blockingUncertainties: string[] }
export interface VerificationEvidence { id: string; source: 'current-run' | 'ci' | 'author' | 'prior-run'; kind: 'build' | 'automated-tests' | 'runtime'; status: 'passed' | 'failed' | 'not_run'; subject: 'original-pr' | 'local-candidate'; revisionSha: string | null; summary: string; evidence: string[]; runId: string | null }
export interface VerificationRecommendation { mode: 'build-tests' | 'ui-e2e'; reason: string; question: string; scenarios: string[]; prerequisites: string[]; evidence: string[]; readiness: 'ready' | 'missing-prerequisites' | 'unknown' }
export interface RelatedVerification {
  parentRunId: string; recommendation: (VerificationRecommendation & { recommendationId: string; level?: E2eAssessment['level']; expectedResults?: string[] }) | null;
  runs: { runId: string; status: Run['status']; outcome?: ResultOutcome; summary?: string; assessment?: ResultAssessment | null; verificationEvidence: VerificationEvidence[]; provenance?: Record<string, unknown> | null; compatibility: { eligible: boolean; reason: string | null }; recommendationId: string }[];
  evidence: VerificationEvidence[]; currentConclusion: { status: 'unchanged' | 'evidence-added' | 'changes-requested' | 'verification-incomplete'; summary: string; canSupplementAssessment?: boolean; evidenceComplete?: boolean; currentRunEvidenceComplete?: boolean; attributedEvidenceComplete?: boolean; completedRunId?: string | null; failedEvidence?: Record<string, unknown> | null };
  totalCount?: number; truncated?: boolean;
  provenance?: Record<string, unknown> | null; errors?: unknown[];
}
export interface ResultNextAction { proposalId?: string; kind: ResultActionKind; reason: string; body: string; recommended?: boolean; pullRequest?: ResultPullRequest; suggestionIds?: string[]; availability?: ActionAvailability; taskKind?: ResultPlanKind; planId?: string; duplicateOf?: IssueReference }
export interface ResultFinding { id: string; title: string; severity: 'high' | 'medium' | 'low'; priority?: never; confirmed?: never; status: 'open' | 'fixed' | 'unverified'; path: string; line: number | null; details: string; evidence: string[] }
export interface ResultFindingV3 { id: string; title: string; priority: 'P0' | 'P1' | 'P2' | 'P3'; severity?: never; confirmed: boolean; status: 'open' | 'fixed' | 'unverified'; path: string; line: number | null; details: string; impact: string; trigger: string; rootCause: string; fixSuggestion: string; evidence: string[]; feedback: { body: string; suggestionId: string | null } }
export interface ResultDiagnostic { code: string; severity: 'warning' | 'error'; message: string; recovery: 'configure' | 'rerun' | 'inspectResult' | 'openTarget' | 'none' }
export interface ReviewLimitation { id: string; name: string; status: string; details: string; evidence: string[]; required?: boolean; category?: 'validation-gap' | 'assessment' | 'recorded-observation' }
export interface ReviewSummary {
  codeReview: 'completed' | 'incomplete'; verification: 'limited' | 'complete'; headSha: string; assessmentStatus: string;
  limitations?: ReviewLimitation[]; limitationCount?: number; observationCount?: number; limitsTruncated?: boolean; canRequestEvidence: boolean; canApproveWithLimitations: boolean; reasons?: string[]; approvalReasons?: string[];
}
export interface ReviewDecision {
  id: string; kind: 'request-evidence' | 'approve-with-limitations'; title: string; description: string; body: string; disclosure: string;
  limitations: ReviewLimitation[]; enabled: boolean; reasons: string[]; requiresAcknowledgement: boolean;
}
export interface Result {
  schemaVersion?: 1 | 2 | 3; outcome?: ResultOutcome; phase?: ResultPhase; structured?: boolean; cliExitCode?: number | null;
  findings?: (ResultFinding | ResultFindingV3)[]; diagnostics?: ResultDiagnostic[]; nextActions?: ResultNextAction[]; assessment?: ResultAssessment | null;
  report?: FinalReport; e2eAssessment?: E2eAssessment | null; featureAssessment?: FeatureAssessment | null; bugAssessment?: BugAssessment | null; plans?: ResultPlan[]; recommendation?: ResultRecommendation; e2eEvidenceComplete?: boolean; relatedConfirmedP0?: boolean; relatedConfirmedP1?: boolean;
  reviewConclusion?: ReviewConclusion | null; verificationEvidence?: VerificationEvidence[]; verificationRecommendation?: VerificationRecommendation | null;
  relatedVerification?: RelatedVerification['currentConclusion'];
  findingSummary?: { unresolved: number; high: number; medium: number; low: number };
  summary: string; artifacts: { label: string; path?: string; url?: string }[];
  validation: { id?: string; name: string; status: string; required?: boolean; details?: string; evidence?: string[] }[];
  blockers: string[];
  nextSteps: { kind: string; reason: string; body?: string }[];
  review?: { headSha: string; body: string; suggestions: Suggestion[] } | null;
  rawOutput?: string;
  needsReview?: boolean;
}
export interface Run {
  runId: string; task: Task;
  provenance?: { version?: number; source?: string; subject?: string; expectedHeadSha?: string; start?: { headSha?: string; workingTree?: string }; end?: { headSha?: string; workingTree?: string } } | null;
  reviewSummary?: ReviewSummary;
  taskContextOmitted?: boolean;
  resultPaging?: { fingerprint: string; sections: { path: ResultPagePath; total: number; nextOffset: number | null }[] };
  config: { agent: Agent; model?: string; reasoningEffort?: string; executionSource?: ExecutionSource; cliPath: string; repoFolder: string; mainRepoFolder?: string; worktreeRoot?: string; worktreeBranch?: string; permission: string };
  status: { state: RunState; createdAt: string; updatedAt: string; startedAt?: string; endedAt?: string; exitCode?: number | null; sequence: number; latestProgress?: string; error?: PulseError; observedExecution?: ObservedExecution };
  view: { read: boolean; handled: boolean }; result?: Result;
  promptTemplate?: { name: string; title: string; sha: string; revision?: string };
}
export type ResultPagePath = string;
export interface ResultPage { items: unknown[]; nextOffset: number | null; total: number; fingerprint: string }
export interface RunList { runs: Run[]; nextCursor?: string | null; runningCount: number; unreadCount: number; prCount: number; issueCount: number; errors?: unknown[] }
export interface TaskLookup { run: Run | null }
export interface TaskEvent { sequence: number; time: string; type: string; text?: string }
export interface Events { events: TaskEvent[]; nextSequence: number; truncated: boolean }
export interface AgentLog { stream: 'stdout' | 'stderr'; text: string; nextCursor: number; truncated: boolean; eof: boolean; sample?: boolean }
export interface Connection { state: 'connecting' | 'connected' | 'disconnected'; lastConnectedAt?: string; error?: PulseError }
export interface OperationPreview {
  reviewDecisions?: ReviewDecision[];
  account: string | null;
  target: { type: 'issue' | 'pr'; number: number; repository: string; title?: string; url: string; state: string };
  url: string; state: string; headSha?: string; expectedHeadSha?: string; stale: boolean;
  canApprove: boolean; canRequestChanges: boolean; canSuggestChanges: boolean; canComment: boolean; canClose: boolean;
  hasConfirmedP0?: boolean;
  files: { path: string; status: string; lines: { line: number; kind: 'add' | 'context'; original: string; hunk: number }[] }[];
  reasons: string[];
}
export interface Operation {
  operationId: string; kind: OperationKind; status: 'prepared' | 'submitting' | 'succeeded' | 'failed' | 'unknown';
  body: string; proposalId?: string; expectedHeadSha?: string; suggestions?: Suggestion[]; remoteId?: string; url?: string;
  findingIds?: string[]; closeReason?: string;
  error?: PulseError | string; createdAt: string; submittedAt?: string;
  bodyTruncated?: boolean; suggestionCount?: number;
  reviewDecision?: { id: string; kind: ReviewDecision['kind']; limitations: ReviewLimitation[]; disclosure: string; acknowledgedLimitations: boolean; headSha: string };
}
export interface ActionReadiness { actionKind: ActionKind; ready: boolean; blockers: PulseError[]; reviewOptions?: ReviewOptions; reviewModes?: ReviewMode[] }
export interface TargetSnapshot { target: { type: 'pr'; number: number }; headSha: string; title: string }
export type WebActionKind = 'comment' | 'review' | 'approve' | 'trigger-ci' | 'merge-pr' | 'create-pr' | 'close-as-duplicate';
export interface WebActionTarget { repository: string; type: 'issue' | 'pr'; number: number }
export interface WebReviewComment { path: string; line: number; startLine?: number; side: 'LEFT' | 'RIGHT'; startSide?: 'LEFT' | 'RIGHT'; body: string }
export interface WebActionDraft {
  requestId: string; actionId: string; kind: WebActionKind; target: WebActionTarget; expectedHeadSha?: string; body?: string;
  review?: { event: 'COMMENT' | 'REQUEST_CHANGES'; comments?: WebReviewComment[]; generalComments?: { body: string }[] };
  pullRequest?: { head: string; base: string; title: string; body?: string; draft?: boolean; sourceHeadSha?: string };
  assignSelf?: boolean; assignmentTarget?: WebActionTarget; duplicateOf?: IssueReference;
}
export interface WebActionSummary {
  operationId: string; requestId: string; actionId: string; kind: WebActionKind; target: WebActionTarget;
  status: 'prepared' | 'submitting' | 'succeeded' | 'failed' | 'cancelled' | 'partial' | 'unknown';
  completedSteps: string[]; remainingSteps: string[]; urls: string[]; error?: PulseError;
  createdAt: string; updatedAt: string;
  runId?: string; proposalId?: string; attemptId?: string; retryAllowed?: boolean; account?: string | null;
  duplicateOf?: IssueReference; resumeRequired?: boolean;
  stepResults?: Record<string, { account?: string; url?: string; completedAt?: string; observed?: boolean }>;
}
export interface WebActionPreview extends WebActionSummary {
  draft: WebActionDraft; account: string | null; canSubmit: boolean; blockers: PulseError[];
  targetUrl: string; state?: string; headSha?: string; sourceHeadSha?: string; draftPr?: boolean; ciState?: string; sourceOrigin: string;
  duplicateSuffix?: string; commentBody?: string; bodyEditable?: boolean;
}
