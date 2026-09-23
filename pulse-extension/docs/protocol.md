# Pulse Extension protocol (envelope v1)

The transport envelope remains version 1. New workflow results use [schema v3](workflow-v3-wire.md); that document defines the current workflow capabilities, saved-plan tasks, result pagination, manual PR/Issue operations and duplicate closure. Historical result formats remain readable. Fixed manual actions do not inherit the completion gates of historical AI-proposal contracts.

The extension and `com.powertoys.pulse` Host use the same envelope. Native Messaging frames are a four-byte little-endian unsigned length followed by UTF-8 JSON. Host stdout contains protocol frames only; CLI streams are captured separately.

```json
{"id":"correlation-id","protocolVersion":1,"type":"tasks.get","payload":{"runId":"c2ac7528-f96d-4c08-b9b3-7c6704687494"}}
```

Success returns `{id,protocolVersion:1,ok:true,data}`; failure returns `{id,protocolVersion:1,ok:false,error:{code,message,guidance}}`. Input is limited to 256 KiB and output to 900 KiB. The extension allows at most 64 pending requests. Paging, truncation, and failures are explicit.

## External web entry point

Production permits only `https://cautious-memory-r38ze9j.pages.github.io`. Development additionally permits HTTP origins on `localhost` or `127.0.0.1`, with exactly port `8080` or `8081`, when both extension and Host enable development origins. Other ports and hosts remain rejected. The web page uses `chrome.runtime.sendMessage(extensionId,message,callback)`; see `examples/`. The actual installed extension ID is required.

The external envelope has `protocolVersion/type/payload` without the Native `id`. New clients use `bridge.hello` to detect the extension independently of Host availability, then `actions.check {actionKind}` for public-safe readiness. `targets.get {target:{type:"pr",number}}` reads a fresh full PowerToys PR SHA through the configured local GitHub account. Legacy `hello`, `tasks.submit`, and `tasks.get/tasks.events` remain available. Fixed navigation methods `ui.openSettings`, `ui.openTasks`, and origin-granted `ui.openTask {runId}` open packaged pages. Submit uses `{task: ...}`:

`agents.defaults {}` returns only `defaultAgent`, per-agent `defaults` (`model/reasoningEffort`), and available `reasoningEfforts`. A task can include `execution:{agent?,model?,reasoningEffort?}`. Omitted fields inherit the selected agent's defaults; explicit empty model/effort strings use the CLI default. `actions.check` accepts the same optional execution object. Accepted public task responses include a sanitized execution snapshot and source labels; provider credentials, local paths, and permissions remain private.

New PR review requests also include `reviewOptions:{mode}`. Modes are `static`, `build-tests`, and `ui-e2e`. The website exposes one Review entry with these scope choices, defaults visibly to `build-tests`, and remembers an explicit user choice separately from saved request identity. These are execution presets, not cumulative quality levels: static reads code and existing evidence without executing builds/tests/UI; build-tests adds relevant final builds and automated tests without actual UI interaction; ui-e2e verifies relevant running scenarios, including UI or non-UI E2E as needed, and does not require rerunning every build or test.

`actions.check {actionKind:"pr-review",reviewOptions:{mode},execution?}` returns `reviewModes` and the exact `reviewOptions` alongside `ready/blockers`. A scoped request requires support for its selected mode and a matching echo. Unsupported older Hosts return `HOST_UPDATE_REQUIRED`; mismatched scope data returns `INVALID_RESPONSE`. `bridge.hello` stays an extension-only handshake and does not advertise Host modes. The extension separately checks actual Host capabilities before scoped submission. Public submission/get/lookup responses echo `reviewOptions`; clients verify it before acknowledging acceptance. An unconfirmed request retains its original mode and request ID. Legacy requests with no mode remain unscoped and recover unchanged; they are not silently labelled static.

```json
{
  "requestId":"client-generated-unique-id",
  "actionId":"microsoft/PowerToys:pr:42:review",
  "actionKind":"pr-review",
  "repository":"microsoft/PowerToys",
  "target":{"type":"pr","number":42},
  "expectedHeadSha":"0123456789abcdef0123456789abcdef01234567",
  "reviewOptions":{"mode":"build-tests"},
  "context":{"source":"Pulse","title":"Improve the example module"},
  "prompt":"Original Pulse action prompt retained for request identity."
}
```

Only `microsoft/PowerToys` is accepted, case-insensitively. Public action kinds are `issue-fix/pr-review/reproduction-setup/e2e/feature-research/bug-investigation`. Every new task requires a target with a positive integer number: `issue-fix/reproduction-setup/feature-research/bug-investigation` require an `issue`; `pr-review/e2e` require a `pr` and its full 40-character expected HEAD SHA. Public `reviewOptions` is valid only for `pr-review`. Internal `pr-verify`, `feature-implement` and `issue-verify` tasks, `followUp` and all `planSource` provenance cannot be supplied by a website; internal PR verification also requires its PR/SHA, and internal Issue workflows require their Issue. New Feature/Bug requests require the actual Host to advertise the kind and schema 3 support; see [v3 capabilities](workflow-v3-wire.md).

Historical targetless `issue-fix/reproduction-setup/e2e` records remain readable by run ID and recoverable through `tasks.lookup` with their exact saved request. Identity validation does not add a target or change the original request. The Host checks existing request identity before new admission: an exact replay returns the accepted run, conflicts and tombstones retain their existing errors, and only a request that has not been accepted is subject to the target requirement. The external extension `tasks.submit` rejects targetless requests before contacting the Host; website recovery must use `tasks.lookup`. A new request ID or rerun is a new execution and cannot reuse the historical exception.

The Host validates remote PR context through the selected `gh` account before acceptance. The worker fetches the fixed PowerToys ref `refs/pull/<number>/head` into `refs/pulse/runs/<runId>` and verifies the SHA again before worktree creation. Git preparation is cancellable. Tasks without an expected PR SHA use the captured main-checkout HEAD.

`requestId` is 1–128 characters and `actionId` is 1–256. The original prompt is at most 128 Ki characters and 160 KiB UTF-8; context is at most 32 KiB. The total envelope must still fit 256 KiB. Web callers may supply only the bounded execution choices above; they cannot supply commands, argument lists, directories, permissions, GitHub identity, or local template selections.

The extension verifies the browser-provided sender origin and supplies Native `{task,sourceOrigin}`. The Host repeats validation. An identical normalized task and source with the same request ID returns the original run. Changed content returns `REQUEST_CONFLICT`; an explicit rerun needs a new request ID. Selected templates do not change the original task fingerprint.

When a submission acknowledgement is lost, external `tasks.lookup {task}` accepts the full frozen original task, including its prompt, context, and execution choices. The extension supplies Native `{task,sourceOrigin}`; the Host returns `{run:null}` when no accepted request exists, or `{run:detail}` for the matching run. Lookup applies the same normalized request fingerprint and source checks as submission: changed content or a different source returns `REQUEST_CONFLICT`, and a deleted request's tombstone returns `TASK_REMOVED`. It never creates a task, starts or retries a CLI, or performs GitHub work. Lookup remains available during maintenance; it may repair the local request index or reconcile an existing run's status. A null result is not permission to submit automatically: preserve the saved request, and require an explicit new submission from the matching PR or Issue if the user wants to run new work.

External task reads return only necessary business fields. They exclude configuration, other runs, original prompt/context, template contents, and local artifact paths. For a found lookup, the extension verifies the returned task, restores the origin/run access grant, and returns `{run:publicRun}`; a missing lookup returns `{run:null}` without granting access. Raw `tasks.logs`, prompt management, configuration changes, cancellation, and GitHub writes are internal-only. Website callers may prepare and read typed GitHub drafts as described below. Clearing browser cache does not delete Host records; looking up the identical original request can restore its origin/run association without submitting again.

## Internal extension methods

Website GitHub drafts use a separate lifecycle from local task results. External `github.prepare {draft}` persists a typed draft and opens `action.html` for confirmation; it never writes to GitHub. `github.get {operationId}` reads its outcome. Both are bound by the Host to the browser-provided origin. Only packaged extension pages can call `webActions.preview/submit/cancel/reconcile`; reconciliation is read-only and never resends a mutation.

Draft fields are `requestId`, `actionId`, `kind`, `target`, optional `expectedHeadSha`, `body`, `review`, `pullRequest`, `assignSelf`, and `assignmentTarget`. Kinds are `comment/review/approve/trigger-ci/merge-pr/create-pr`. Targets contain `repository/type/number`. Targets are fixed to PowerToys upstream, except comments may target the existing `MuyuanMS/PowerToys` fork; fork self-assignment uses an explicit upstream `assignmentTarget`. Reviews contain an event, selected inline comments, and separate general comments. PR creation contains `head/base/title/body/draft`. Arbitrary REST endpoints and commands are rejected.

Preview shows the actual account and rechecks the target, revision, permissions, and applicable merge/CI checks. Submit requires the preview account and records each write step. Outcomes are `prepared/submitting/succeeded/failed/cancelled/partial/unknown`. Public outcomes contain IDs, target, state, completed and remaining steps, result URLs, and safe errors, without the draft body or credentials. See [Actions integration](actions-integration.md).

Only trusted extension pages may invoke these methods through the service worker. The installed extension origin must also be authorized by the Host installation and Native manifest.

| Type | Payload | Data |
| --- | --- | --- |
| `hello` | `{}` | Host version, `reviewModes`, `workflowKinds`, `resultSchemaVersions:[2,3]`, selected CLI file availability, selected GitHub identity and limits; no model request |
| `config.get` | `{}` | Shared local configuration |
| `config.save` | Complete configuration | Validated saved configuration |
| `github.accounts` | `{}` | `{available,accounts:[{login,active,state}],error?}` |
| `prompts.list` | `{}` | Embedded local catalog and bundle revision |
| `prompts.get` | `{name}` | Local prompt metadata, exact content, SHA, revision |
| `prompts.sync` | `{githubAccount?}` | Compatibility alias for reading the embedded local catalog; no synchronization |
| `agents.list` | `{}` | `{installations:{codex:[AgentProbe],copilot:[AgentProbe]},selections:{codex,copilot}}`; internal-only installation inventory |
| `agents.test.start` | `{agent,cliPath?}` | Diagnostic `testId` and current status, with the selected executable frozen before launch |
| `agents.test.get/cancel` | `{testId}` | Diagnostic status, response, exit code, error |
| `tasks.submit` | `{task,sourceOrigin}` | Accepted task details |
| `tasks.lookup` | `{task,sourceOrigin}` with the full frozen original task | `{run:null\|detail}`; finds an accepted request without submitting or executing work |
| `tasks.list` | `{cursor?,limit?,view?}` | `{runs,nextCursor,runningCount,prCount,issueCount,unreadCount,errors}` |
| `tasks.get` | `{runId}` | Task details, with `resultPaging` when a v3 report requires additional pages |
| `tasks.resultPage` | `{runId,path,offset,limit?,fingerprint}` | Lossless bounded section of the saved v3 report; see [v3 paging](workflow-v3-wire.md) |
| `tasks.events` | `{runId,afterSequence,limit?}` | `{events,nextSequence,truncated}` |
| `tasks.logs` | `{runId,stream,cursor?,limitBytes?}` | `{stream,text,nextCursor,truncated,eof}` |
| `tasks.cancel` | `{runId}` | Details after saving cancellation |
| `tasks.read/handle` | `{runId,value?:true}` | Details after changing the view flag |
| `tasks.rerun` | `{runId,requestId,execution?,expectedHeadSha?,reviewOptions?}` | New run; omitted execution/scope preserves old choices, null execution uses current defaults, object replaces overrides; a verification-only rerun cannot change its scope or SHA |
| `reviews.verify` | `{parentRunId,requestId,recommendationId,execution?,prerequisitesConfirmed?}` | New verification-only task from the exact saved recommendation; freezes current execution defaults/overrides and original PR revision |
| `reviews.related` | `{runId}` | Parent review, bounded related verification/evidence, compatibility reasons and current conclusion; no execution or history rewrite |
| `tasks.startFromResult` | `{runId,proposalId,requestId,execution?,prerequisitesConfirmed?}` | New Host-prepared task bound to the full saved Issue plan and source; internal-only |
| `tasks.delete` | `{runId}` | `{deleted:true}`, only for read and handled terminal records |
| `operations.preview` | `{runId}` | Target, identity, state, SHA, permission, valid diff lines and fixed manual-action availability |
| `operations.submit` | Operation draft below | Independent submission record |
| `operations.list` | `{runId}` | Saved operations |
| `operations.reconcile` | `{runId,operationId}` | Read-only verification of an uncertain submission |
| `resultActions.prepare` | `{runId,proposalId,attemptId?,retry?}` or manual PR `{runId,kind,attemptId?,retry?}` | Confirmation for a saved proposal, including v3 duplicate closure, or fixed manual Merge/CI; forms are mutually exclusive; no GitHub write |
| `resultActions.list` | `{runId}` | `{operations,totalCount,truncated}` for associated typed-action attempts, including Duplicate; includes proposal/attempt identity and retry eligibility |

Task pages default to 20 rows, maximum 50. Views are `tasks` (accepted/running), `prs` (terminal PR runs), and `issues` (terminal issue runs). Terminal states are `succeeded/failed/cancelled/interrupted`. Filtering happens before pagination; counts cover all readable records. Compatibility views remain `running` (active), `pending` (terminal and unhandled), and `history` (all terminal records). The English UI uses the three new views.

Event pages default to 50 entries, maximum 100, with a separate byte budget. Consumers deduplicate by `(runId,sequence)`. Reconnecting reads snapshots and later events; it does not submit again. Connection state is independent of task state.

Raw logs accept only `stdout` and `stderr`, mapped to fixed files rather than caller paths. The cursor is an on-disk byte offset at a complete line boundary, initially `0`. `limitBytes` defaults to `65536` and accepts `1024..131072`. Reuse `nextCursor` separately for each stream. Only complete UTF-8 lines advance the cursor; partial trailing lines remain for a later poll. An oversized complete line returns a bounded prefix and an explicit truncation marker. Each persisted stream is capped at 64 MiB for a saved schema-v3 task and 8 MiB for legacy tasks; result JSON and escaped-event limits are separate. `eof` means the observed file end, not task completion; keep polling while the task remains active. Known credentials are redacted.

## Configuration and prompt snapshots

The existing configurable selections remain `prPrompt` for PR review, `issuePrompt` for local fixes, `e2ePrompt` for E2E/PR verification, and `reproductionPrompt` for reproduction. An empty reproduction selection uses its bundled reproduction workflow. Feature research, Bug investigation, Feature implementation and Issue verification use their fixed bundled templates without adding configuration fields. Older settings clients that omit E2E/reproduction fields preserve the saved selections.

```json
{
  "agent":"codex",
  "cliSelections":{"codex":"","copilot":""},
  "permission":"read-only",
  "mainRepoFolder":"C:\\source\\PowerToys",
  "worktreeRoot":"C:\\source\\PowerToys-worktrees",
  "githubAccount":"",
  "prPrompt":"powertoys-pr-loop-review.prompt.md",
  "issuePrompt":"powertoys-issue-local-fix.prompt.md",
  "e2ePrompt":"powertoys-pr-e2e-test.prompt.md",
  "reproductionPrompt":"",
  "agentDefaults":{
    "codex":{"model":"","reasoningEffort":""},
    "copilot":{"model":"","reasoningEffort":""}
  }
}
```

Only these eleven configuration fields are accepted. Agent is `codex/copilot`; permission is `read-only/workspace-write/yolo`. Each agent's model/effort defaults and selected installation are saved independently. There is no execution timeout, including for old snapshots containing `timeoutSeconds`. Users can cancel explicitly.

`agents.list` lists discovered executable files for local Settings. Rows include optional version, primary `path`, canonical `resolvedPath`, source labels and aliases. `isCurrent` and `historical` describe standalone updater entries. Every detected executable is selectable: version-query failure, version number, adjacent helpers and advertised interfaces are not compatibility gates. `available`/`fileExists` denote file presence only. A blank choice returns `CLI_SELECTION_REQUIRED` and never selects the first match. Selections save the canonical path when available; an older client omitting `cliSelections` preserves the saved choices. Missing saved paths remain visible and never fall back automatically. Per-task agent overrides use that agent's saved installation. Inventory, paths and source metadata remain private to extension pages.

The main folder must be an absolute PowerToys main-checkout path, not a linked worktree. A fork may retain its origin with a matching upstream remote. The worktree root is external to the main checkout. Each run uses `pulse-<runId>` and `codex/pulse-<runId>`. Accepted configuration adds Host-generated CLI path/version, worktree path/base SHA, and common-repository lock identity. Web callers and configuration saves cannot override those fields.

Account detection uses `gh auth status --hostname github.com --json hosts`. Each account exposes only `login/active/state`; state is `success/error/timeout`. Failed accounts do not hide valid ones. A saved nonempty account must be a successful local login. Empty means unconfigured; `active` is the existing global account, not necessarily the selected extension account. Selection is applied per command without global switching.

Prompt sources are eight repository-owned business Markdown files in `prompts/`, embedded in the Host assembly, plus a shared PR verification fragment. `prompts.list` and `prompts.get` read that bundle offline. The legacy `prompts.sync` method is a local reload alias; its optional old `githubAccount` input is ignored. Settings uses only `prompts.list` and `prompts.get`. Existing synchronized caches are neither read for new tasks nor deleted.

Catalog shape remains compatible with `{prompts:[{name,title,description?,appliesTo,path,sha}],sourceUrl,revision}` and adds bundled-source/hash metadata. The source URL is `bundled://Pulse.Host/prompts`; `sha` and bundle `revision` use SHA-256. Get additionally returns exact `content`. Nonempty saved selections must match a bundled template and target. Among configurable prompts, empty choices permit initial setup and only reproduction has an empty-selection fallback to its bundled workflow.

Supported variables are exactly `<PRNumber>`, `<PRTitle>`, `<IssueNumber>`, and `<IssueTitle>`. Numbers come from the validated target. Optional `context.title` is bounded, stripped of control characters, and JSON-quoted as data; without it, the template receives `Title not supplied; read it from the linked GitHub target.` Unknown placeholders and opposite-target variables produce explicit errors.

Before acknowledgment, `task.json` records a top-level `promptTemplate` containing the body, source/template/fragment hashes, schema version, required checks, review scope and capture metadata. Static PR review omits the runtime fragment; the other applicable review/verification scopes include it. The Host wrapper injects the relevant task/scope protocol and one full schema for either CLI. The original webpage prompt stays unchanged and is treated as untrusted context. Host/prompt updates and settings edits affect new tasks only. Historical targetless records retain their saved data and request identity; new execution requires the matching linked target.

Agent diagnostics send only `what's your model`, not caller-provided prompts or arguments. An omitted `cliPath` uses the saved installation; a supplied path allows testing a currently selected, unsaved inventory entry without changing configuration. The Host validates the choice and freezes its exact executable before starting the diagnostic worker. Later settings changes cannot redirect the test. Success requires an actual nonempty assistant response, exit code zero, and no CLI error. Versions, initialization, and usage events alone cannot pass. Tests use temporary directories, are cancellable, and have no model deadline. Diagnostic workers may remain in the current Windows Job; production action workers retain their detachment guard.

Task details include `runId,task,config,status,view,result,promptTemplate`. List responses omit large prompts, context, template bodies, and full results while retaining bounded `schemaVersion,outcome,phase,structured,summary,needsReview` metadata. New bundled tasks use [workflow v3](workflow-v3-wire.md), including complete rechecked findings, coverage/limitations, assessments and saved plans. Large reports use fingerprint-bound pages rather than dropping findings. `status.state` remains the execution lifecycle; `result.outcome` reports business completion. New terminal events use `workflow.<outcome>`. Historical [v2 results](structured-results-v2.md) and legacy v1 results remain readable without rewriting their records or promoting them to v3. Completion, cancellation, and record cleanup preserve worktrees.

Every displayed proposal receives a stable Host-owned `proposalId`; distinct same-kind proposals and exact body whitespace are retained. Full details include per-proposal `availability:{enabled,reasons}`; disabled proposals are not removed. `operations.submit` may include that ID to associate an edited review/comment/close draft with its stored proposal. Suggestion IDs are checked against the proposal's `suggestionIds` and original locations; metadata is removed from GitHub payloads. `resultActions.prepare` resolves all target/content fields from saved records, while `attemptId` distinguishes explicit new confirmation attempts from retransmission. A succeeded CI proposal needs explicit `retry:true` and newly failed CI evidence to start another attempt. Unknown/partial writes remain reconciliation-only.

Task and list responses may include a bounded `assessment` and `findingSummary`; legacy severity counts remain compatible, while v3 also reports confirmed unresolved P0–P3 counts. For oversized full details, `taskContextOmitted: true` means only the response copy of `task.prompt/context` was omitted; `resultPaging` identifies separately readable report sections. The immutable task file and complete result/proposals remain stored. A full completion journal commits result and terminal state before their projections are updated, allowing recovery without overwriting prior evidence. New confirmations use the current configured GitHub account, then retain that account for all outcome reconciliation.

Scoped results distinguish `reviewConclusion` from product `assessment` and attributed `verificationEvidence`. V3 uses `e2eAssessment` as the authoritative necessity/coverage recommendation; the Host derives a compatible verification recommendation for existing follow-up methods. Historical v2 `verificationRecommendation` and older `reviewSummary` remain readable without becoming a v3 report or a separate permission path. The former fixed `request-evidence` / `approve-with-limitations` pair is superseded. `reviewDecisionId` and `acknowledgedLimitations` do not bypass `operations.submit` validation. Fixed manual controls remain independent of model advice.

`reviews.verify` is restricted to packaged extension pages. The Host resolves the recommendation from the immutable parent result, requires a new request ID, and creates `actionKind:"pr-verify"` with `reviewOptions` and `followUp:{parentRunId,recommendationId,parentResultFingerprint,subject:"original-pr",revisionSha}`. The Host supplies the saved question, scenarios, prerequisites and review context. The child performs only the selected verification rather than a full review; its `reviewConclusion` and `review` are null. Website clients cannot supply these linkage fields, local paths or provenance. Repeated requests must retain the same payload and recover the frozen submission instead of silently changing settings, recommendations or SHA.

A recommendation marked `missing-prerequisites` or `unknown` requires `prerequisitesConfirmed:true` before starting. This records the user's report that conditions changed; it is not proof of environment readiness. The child checks those prerequisites before executing scenarios. A stale PR, changed parent result, missing source, or different local candidate cannot silently rebind verification to another revision. A retry of a verification-only task keeps its original scope and revision.

`reviews.related` returns `parentRunId`, optional recommendation with a Host-generated `recommendationId`, related runs, attributed evidence, provenance, errors, `totalCount/truncated`, and `currentConclusion:{status,summary}`. Conclusion statuses are `unchanged/evidence-added/changes-requested/verification-incomplete`. Compatibility binds the exact repository, PR, original SHA, saved parent-result fingerprint, recommendation, and Host-observed source snapshots. Compatible evidence can update recommendations and the confirmed-original-P0 assessment; neither related reads nor successful verification rewrite the original result, create a new GitHub proposal, or submit approval automatically.

Full task details expose Host-owned `provenance` from `provenance.json`: `{version:1,source:"host",observation:"boundary-snapshots",expectedHeadSha,subject,start,end}`. Each boundary records `capturedAt/headSha/workingTree/diffHash`; source is classified as original PR, local candidate, or unknown. Clean matching boundaries are required for original-PR evidence aggregation. They are observations before and after execution, not continuous proof that every intermediate test ran unmodified code. Temporary changes reverted before the final snapshot and ignored files are not fully attested. Model-attributed evidence remains inspectable and cannot replace these Host observations.

## GitHub drafts and submissions

```json
{
  "runId":"c2ac7528-f96d-4c08-b9b3-7c6704687494",
  "operationId":"client-generated-operation-id",
  "expectedAccount":"reviewer",
  "kind":"requestChanges",
  "expectedHeadSha":"0123456789abcdef0123456789abcdef01234567",
  "body":"Please address this issue before approval.",
  "suggestions":[{
    "path":"src/example.ts","startLine":10,"line":11,"side":"RIGHT",
    "body":"Preserve the fallback behavior.","replacement":"return value ?? fallback;"
  }]
}
```

Kinds are `approve/requestChanges/suggestChanges/comment/close`. PRs allow review, comment and close actions; issues allow comment and close. Approve does not merge; close records an explicit `closeReason` and adds no extra comment. Suggest changes creates a COMMENT review with suggestions. V3 `findingIds` binds the user-selected feedback; unselected findings are excluded. The target comes from the saved task and cannot be overridden. Operation IDs are 8–128 letters, digits, underscores, or hyphens. Full current manual-action details are in [workflow v3 wire](workflow-v3-wire.md).

The UI shows the account, target, reviewed SHA, editable body, and selected suggestions before the user chooses an action. Fixed manual controls do not require model success, a complete result or a proposed action. A confirmed unresolved P0 on the current original PR adds an Approve restriction; P1 or incomplete analysis does not. The Host still rechecks identity, permissions, state, HEAD, and valid RIGHT-side diff ranges. It does not rebind stale suggestions or silently convert them to ordinary comments. Historical AI-proposal eligibility is not a gate on these manual entry points.

All extension GitHub requests use `gh api` with fixed canonical URLs and argument arrays; JSON bodies go through stdin. The selected credential stays in memory and one child-process environment. Tokens are absent from extension messages, records, and logs. HTTP transport injection exists only for offline tests.

Submission state is independent of CLI state: `prepared/submitting/succeeded/failed/unknown`. Persisted intent and normalized-draft deduplication prevent duplicate writes across browsers. An uncertain response remains unknown until a read-only reconciliation finds a unique matching remote record. It is never automatically resent.

## Local preview boundary

The development loopback `POST /__pulse/diagnostics` forwards only `agents.test.start/get/cancel`, `github.accounts`, and `prompts.list/get/sync`. These invoke real local CLIs/Host reads and local prompt storage. Preview tasks, raw task logs, results, and form settings are sample/session data. The proxy uses the locally compiled Host and does not execute PowerToys tasks or send GitHub writes. This is separate from the production web protocol.
