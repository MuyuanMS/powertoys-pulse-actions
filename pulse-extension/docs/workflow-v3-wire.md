# Workflow v3 integration wire

Implemented contract for the approved PR/Issue proposal. New bundled tasks use schema 3; legacy snapshots keep their recorded schema. All fields below are required in new model results; nullable sections use null when irrelevant. Existing Host projections may add proposalId/availability/structured/cliExitCode/nextSteps/blockers/findingSummary; these are not model fields. Validation evidence is recorded in [workflow-v3-validation.md](workflow-v3-validation.md).

Common model fields retained: schemaVersion=3, outcome, phase, summary, assessment, reviewConclusion, validation, diagnostics, artifacts, review, needsReview, verificationEvidence (same value shapes as v2 unless defined below). V3 does not accept model verificationRecommendation; e2eAssessment is authoritative. Host helpers can derive a compatible recommendation.

```ts
type Report = { complete: boolean; rechecked: boolean; coverage: string[]; limitations: string[] };
type Finding = {
  id: string; title: string; priority: 'P0'|'P1'|'P2'|'P3';
  status: 'open'|'fixed'|'unverified'; confirmed: boolean;
  path: string; line: number|null; details: string; impact: string; trigger: string;
  rootCause: string; fixSuggestion: string; evidence: string[];
  feedback: { body: string; suggestionId: string|null };
};
type E2eAssessment = null | {
  level: 'not_needed'|'recommended'|'required'; reason: string; question: string;
  scenarios: string[]; expectedResults: string[]; prerequisites: string[]; evidence: string[];
  readiness: 'ready'|'missing-prerequisites'|'unknown';
};
type IssueReference = { repository: string; number: number; url: string };
type FeatureAssessment = null | {
  status: 'ready'|'needs_information'|'needs_decision'|'already_supported'|'duplicate'|'not_feasible';
  summary: string; reasons: string[]; evidence: string[]; acceptanceCriteria: string[];
  questions: string[]; alternatives: string[]; relatedIssue: IssueReference|null; planId: string|null;
};
type BugAssessment = null | {
  status: 'confirmed'|'needs_information'|'needs_verification'|'already_fixed'|'duplicate'|'not_a_bug';
  summary: string; reasons: string[]; evidence: string[]; questions: string[];
  relatedIssue: IssueReference|null; planId: string|null;
  reproduction: {
    status: 'reproduced'|'not_reproduced'|'not_run'|'blocked'; revisionSha: string|null;
    environment: string; steps: string[]; expected: string; observed: string; evidence: string[];
  };
};
type PlanKind = 'feature-implement'|'issue-fix'|'reproduction-setup'|'issue-verify';
type Plan = { id: string; kind: PlanKind; summary: string; steps: string[];
  acceptanceCriteria: string[]; prerequisites: string[]; evidence: string[] };
```

Top-level additions: report:Report, findings:Finding[], e2eAssessment:E2eAssessment, featureAssessment:FeatureAssessment, bugAssessment:BugAssessment, plans:Plan[]. No Top-N slicing of findings. All report data is preserved; transport paging is separate from completion.

nextActions retains existing typed actions, adds required recommended:boolean to each v3 action, and adds:

```ts
{ kind:'start-task'; reason:string; body:''; recommended:boolean; taskKind:PlanKind; planId:string }
{ kind:'close-as-duplicate'; reason:string; body:string; recommended:boolean; duplicateOf:IssueReference }
```

The common `review` retains headSha/body/suggestions. Each code-capable finding references one review.suggestions[].id through feedback.suggestionId. Selected ordinary findings contribute editable comments; the UI must never include unselected findings by default. Manual GitHub actions do not require nextActions or a model review draft.

Public task actionKinds: pr-review, issue-fix, reproduction-setup, e2e, feature-research, bug-investigation. Host-prepared-only kinds: pr-verify, feature-implement, issue-verify. All planSource metadata is internal-only, including on plan-driven issue-fix and reproduction-setup.

```ts
task.planSource = {
  parentRunId:string; parentResultFingerprint:string; proposalId:string; planId:string;
  repository:string; target:{type:'pr'|'issue';number:number}; revisionSha:string|null;
  sourceKind?:'original'|'local-candidate'; candidateSnapshotHash?:string;
};
```

Host hello advertises workflowKinds (public kinds) and resultSchemaVersions:[2,3]. New feature/bug analysis requires advertised kind and schema3. Preserve legacy handshake/read/lookup behavior.

Native routes:

- tasks.startFromResult {runId,proposalId,requestId,execution?,prerequisitesConfirmed?} → normal Run. Prepare binds the saved full plan, original target/source and frozen request; changing options while recovering is a conflict.
- reviews.verify / reviews.related retain their internal-only routes. V3 recommendations derive from e2eAssessment; supplement requires exact scope coverage and matching original-source evidence.
- operations.preview / operations.submit provide fixed manual PR/Issue operations; findingIds bind selected v3 feedback. Missing result/AI proposal does not remove manual operations. comment plus inline suggestions submits a COMMENT review; otherwise comment uses an ordinary conversation comment. close requires closeReason saved as action metadata, with no implicit comment or Issue-body replacement.
- resultActions.prepare also accepts {runId,kind:'merge-pr'|'trigger-ci',attemptId?,retry?} for manual PR actions, mutually exclusive with proposalId. Duplicate uses its v3 proposalId and existing action confirmation.

Large result transport:

```ts
Run.resultPaging?: {
  fingerprint:string;
  sections:Array<{path:string;
    total:number; nextOffset:number|null}>;
};
// Initial result arrays contain their first bounded page.
tasks.resultPage({runId,path,offset,limit?,fingerprint})
  => {items:unknown[];nextOffset:number|null;total:number;fingerprint:string};
```

The UI loads remaining pages before claiming a full final report, retains fixed manual controls while loading, and surfaces any incomplete read. The fingerprint binds the saved result, avoiding mixed-revision reconstruction. The Host evaluates P0 against full stored findings, never the display page.

Page paths are a Host whitelist: findings, nextActions, validation, artifacts, diagnostics, verificationEvidence, review.suggestions, plans, report coverage/limitations, reviewConclusion.blockingUncertainties and the known array fields of E2E/Feature/Bug assessments. The UI rejects prototype or unknown paths and reconstructs compatibility aliases only from complete canonical arrays. V3 accepts up to 8 MiB of model JSON with separate normalized-result, escaped-event and log budgets; resource exhaustion is reported explicitly, never handled by taking the first N findings.

Direct UI/E2E Review reports per-scenario checks `e2e-scenario-1`, etc., aligned to e2eAssessment.scenarios and expectedResults. Linked checks use the immutable scenarioIdPrefix from the Host context. Complete matching scenario coverage and original-source evidence are required before e2eEvidenceComplete; generic success alone is insufficient. Attributed existing evidence retains its source.

For new local implementations, Host retains an immutable candidate delta in content-addressed candidate-snapshots artifacts and records a run reference. Candidate planSource requires sourceKind local-candidate, a 64-hex candidateSnapshotHash and its 40-hex base revision. Child worktrees restore verified changed/new/deleted content before execution. Parent record deletion does not remove retained artifacts; missing historical snapshots cannot silently fall back to pre-fix source. No branch commit or push is performed by snapshot capture.

Core APIs planned on WorkflowResult: SchemaV3(), IsValidStoredV3(result), HasConfirmedP0(result,task), IsFinalReportComplete(result), Recommendation(result,task), CanPublishManualPr(result,task,kind). Recommendation returns null or {kind:'approve'|'address-findings'|'run-e2e'|'incomplete',reason:string}. Historical CanPublish semantics can remain for legacy AI proposals; new fixed manual entry points must not inherit those old result-completion gates.
