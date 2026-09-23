# Result contract audit: generated schema and Host validation

This audit started from commit `6268cbb` in the UI/UX packaging worktree. It compares the generated model contracts (`WorkflowResult.Schema()` / `SchemaV3()`) with `ValidV2`, `ValidV3`, every nested validator they invoke, and the input-envelope checks in `FromModel`. It also records the separate normalization and publication rules so an honest incomplete report is not mislabeled a schema failure.

The audit uses source inspection and offline fixtures. Final implementation validation is recorded below. No model workflow, application UI or GitHub write was executed, and the original saved task and evidence files remain unchanged.

## Real failure and minimal regression

Task `ecd4c660-3a38-4387-aa69-97aa67392390` returned a report of six application builds and 449 adjacent tests. Four `verificationEvidence` rows had `source: "current-run"` but placed their TRX `TestRun.id` in `runId`. The original schema allowed a GUID for every source; `ValidVerificationEvidence` allows non-null `runId` only for `prior-run`.

Read-only inspection established that all four GUIDs are the XML root IDs of the corresponding TRX files, not local Pulse task directories. Their test totals are 32, 151, 43 and 223. That explains an identity-contract failure; it does not invalidate the independently retained build and test evidence, and it does not establish CJK runtime behavior.

`tests/result-contract-real-case.fixture.json` is a deliberately reduced, sanitized reproduction. It contains no user directory, machine name, account or task-store path. Public original revision identity and the four external TRX IDs are retained. Artifact references are illustrative relative names. It preserves the important distinction between a completed build-tests review, an inconclusive product assessment, and required but unexecuted CJK verification.

The corrected **synthetic** fixture moves each external ID into evidence text and uses `runId: null`. No live record is corrected, rewritten or automatically accepted. `ResultContractRegressionScenarios` checks the four exact diagnostic paths, immutable input, accepted corrected evidence, continued runtime limitations, bounded diagnostics and independent workflow-state precedence.

## Classification

| Class | Meaning and implementation boundary |
|---|---|
| Aligned | Generated shape, type, enum or bound already agrees with Host admission. Keep it covered. |
| Static gap | An accepted generated-schema instance can fail a local Host rule. Express the local rule using supported schema branches/bounds/patterns and retain Host validation. |
| Compatibility subset | The generation schema intentionally emits more explicit fields than historic Host acceptance requires. Keep legacy acceptance; do not broaden or tighten history inadvertently. |
| Cross-field/item | References, identity equality, ranges, cardinalities or uniqueness by a property. Preserve authoritative Host checks and provide field diagnostics plus prompt guidance. Some are expressible in full JSON Schema, but not necessarily in the supported structured-output subset. |
| Resource/encoding | JSON parsing, wire size, .NET string units or serialized aggregate byte limits. Explain explicitly; a per-field JSON Schema bound alone does not establish these. |
| Workflow/policy | Valid data can produce a blocked/incomplete result or an unavailable action. These are not schema rejection. |

## Complete scalar and envelope inventory

The limits below describe the audited Host, including deliberate historical compatibility. They must not be inferred from UI truncation or action eligibility.

| Surface | Host rule | Baseline schema comparison |
|---|---|---|
| Input text | Trim; optionally unwrap a Markdown fence; parse JSON object at maximum depth 32; reject duplicate property names recursively | Parsing/lexical constraints are outside the object schema. Retain the original output and diagnose the input boundary. |
| Result version | `schemaVersion` must parse as an `int`, exactly 2 or 3 for the selected contract; an expected V3 task cannot downgrade | Version `const` is aligned. JSON Schema integer semantics can admit numeric spellings such as `3.0`/`3e0` that `TryGetValue<int>` rejects; this is a lexical/resource diagnostic boundary. |
| Raw model bytes | V2: 320 KiB; V3: 8 MiB, counted as UTF-8 of the candidate before JSON parsing | Not derivable from individual schema fields or array sizes. |
| V2 top level | Closed object; required: schemaVersion, outcome, phase, summary, findings, artifacts, validation, diagnostics, nextActions, review, needsReview. Optional historic fields: assessment, reviewConclusion, verificationEvidence, verificationRecommendation | Schema requires all 15 fields for new generation: intentional compatibility subset. Null is allowed for the three optional nullable objects; a present verificationEvidence must be an array. |
| V3 top level | Closed object; exactly schemaVersion, outcome, phase, summary, assessment, reviewConclusion, verificationEvidence, report, findings, artifacts, validation, diagnostics, nextActions, review, needsReview, e2eAssessment, featureAssessment, bugAssessment, plans | Aligned 19 required fields. Canonical model input does **not** contain Host projections such as `recommendation`, `structured`, `proposalId` or `availability`. |
| Enums | Outcome: completed/blocked/failed/cancelled/interrupted; phase: setup/analysis/implementation/validation/reporting; all nested enums are exact, case-sensitive strings | Aligned. |
| Boolean | Must be JSON boolean, not a string or number | Aligned, including needsReview, required, confirmed, report flags, recommended, draft. |
| General `String` | String only, no U+0000; maximum uses `.NET string.Length` (UTF-16 code units); nonempty means not null/empty/whitespace | Baseline schema maxLength/minLength misses NUL and whitespace-only rejection. JSON Schema maxLength counts Unicode scalar values, so non-BMP boundary differences remain an explicit Host diagnostic even after pattern tightening. |
| Identifier | 1–64 UTF-16 units, ASCII `[A-Za-z0-9][A-Za-z0-9._-]{0,63}`, absolute full-string match | Baseline `$` patterns can admit a final newline when length permits, unlike Host `\z`; schema patterns need full-string anchoring. |
| SHA | Exactly 40 ASCII hex characters, either case | Schema type, fixed-length regex and maximum agree. Contextual equality with accepted task revision is separate. |
| Positive integer | Int32, 1–2,147,483,647; JSON value must parse as `int` | Minimum/maximum/type agree for canonical integer tokens; decimal/exponent spellings are a lexical difference. |
| Closed nested objects | `Exact` or `ExactOptional`: required members exist, no unknown members | Schema uses additionalProperties:false. Exceptions for historic omitted members are listed below. |
| Text-list item | Nonblank, NUL-free string of at most 4096 UTF-16 units | Baseline minLength:1 did not reject whitespace/NUL. |
| Legacy `Evidence` item | Nonempty and NUL-free, at most 4096 units; **whitespace-only is historically accepted structurally** | Schema may deliberately emit a stricter nonblank subset; do not change old Host acceptance while fixing generation. Workflow passed-check evidence still requires nonblank text. |

## V2 and shared nested inventory

| Field / validator | Complete Host constraints | Baseline gap / disposition |
|---|---|---|
| summary | Nonblank, max 32768 | Generic scalar gap. |
| assessment | Null or exact subject/status/summary/revisionSha; subject original-pr/local-candidate/target; status passed/failed/inconclusive; nonblank summary4096; SHA or null | Shape/enums/bounds aligned; scalar gap. No structural requirement that SHA equal task. |
| reviewConclusion | Null or exact status/summary/revisionSha/blockingUncertainties; status no-blocking-findings/changes-requested/inconclusive; summary4096 nonblank; nullable SHA; max50 nonblank list entries | Aligned except generic scalar gap. |
| findings(V2) | Max200; exact id/title/severity/status/path/line/details/evidence; unique id; title512 nonblank; severity high/medium/low; status open/fixed/unverified; path4096; nullable positive line; details4096; evidence max50 | Shapes/bounds aligned; unique IDs need cross-item diagnostics. |
| artifacts | V2 max200, V3 unbounded by item count; exact path/label; path4096 nonblank; label512 nonblank | Aligned except scalar gap. Paths are display evidence, not shell commands. |
| validation | V2 max200 input (204 only for restored Host-expanded records); V3 unbounded; exact id/name/status/required/details/evidence; unique id; name512 nonblank; passed/failed/not_run; details4096; V2 evidence max50; V3 evidence unbounded nonblank TextList | **Reverse mismatch:** V3 schema inherited evidence.maxItems50, whereas V3 Host TextList is unbounded. Remove this accidental generation cap. Whether required checks passed belongs to normalization. |
| diagnostics | V2 max100 input (104 for Host-expanded stored records); V3 unbounded; exact code/severity/message/recovery; code64 uppercase ASCII pattern; severity warning/error; nonblank message8192; recovery configure/rerun/inspectResult/openTarget/none | Scalar/full-anchor gap; other shapes/bounds align. Model error diagnostics can block completion but are structurally legal. |
| verificationEvidence | V2 max100, V3 unbounded; exact id/source/kind/status/subject/revisionSha/summary/evidence/runId; unique id; source current-run/ci/author/prior-run; kind build/automated-tests/runtime; status passed/failed/not_run; subject original-pr/local-candidate; nullable SHA; summary4096 nonblank; evidence max50 | Shared V3 evidence item cap remains intentional (uses `Evidence`, not TextList). |
| verificationEvidence.evidence | At least one entry when status is passed or failed; may be empty for not_run | Static conditional gap. |
| verificationEvidence.runId | Null for current-run/ci/author. For prior-run: null **or** D-format GUID | Static conditional gap that caused the real report failure. GUID shape alone cannot prove the prior Pulse task exists; provenance/resolution remains separate. Do not require prior-run GUID when Host allows null. |
| verificationRecommendation(V2) | Null or exact mode/reason/question/scenarios/prerequisites/evidence/readiness; mode build-tests/ui-e2e; reason/question4096 nonblank; scenarios1–30; prerequisites0–30, min1 when missing-prerequisites; evidence0–50; readiness ready/missing-prerequisites/unknown | Static scenario/prerequisite minimum gaps. Whole serialized object ≤24576 UTF-8 bytes including JSON escaping is Host-only; existing schema description already states this budget. |
| review | Null or exact headSha/body/suggestions; SHA; body32768 | Aligned except scalar gap. |
| review.suggestions | V2 max100, V3 unbounded; id optional for V2 compatibility, required V3; path1024 nonblank; positive line/startLine; side RIGHT; body8192; replacement32768; IDs unique when present | Schema always requires id for generation: V2 compatibility subset. startLine≤line and line−startLine≤999 are Host cross-field checks (at most1000 selected lines). Empty replacement is legal explicit deletion. |
| nextActions | V2 max50, V3 unbounded; reason4096 nonblank; body32768; closed variant-specific shape | Supported V2 kinds: viewChanges, inspectResult, openTarget, configure, rerun, approve, suggestChanges, requestChanges, comment, close, create-pr, merge-pr, trigger-ci, none. V3 adds start-task and close-as-duplicate plus required boolean recommended on every variant. |
| approve/suggestChanges/requestChanges | Optional suggestionIds in historical Host shape; if present ≤100 valid identifiers, unique, each resolving to review.suggestions | Schema requires the list for new generation (compatibility subset); uniqueness of strings can be expressed if supported; reference resolution stays Host-owned. V3 keeps the same100 association bound even though review suggestions themselves are unbounded. |
| merge-pr / trigger-ci / create-pr body | merge-pr and create-pr require exactly empty string; trigger-ci is empty or `/azp run` | Already aligned; never interpret arbitrary result text as a command. |
| create-pr.pullRequest | Exact head/base/title/body/draft with optional historical sourceHeadSha; head240 nonblank matching owner39max:`nonwhitespace branch`; base200/title256 nonblank; body60000; draft boolean; provided sourceHeadSha exact SHA | Schema requires sourceHeadSha for new output (compatibility subset), but lacked head owner:branch syntax. `draft:true` is a result-action policy, not this general input boolean validator. Remote ref validity and source identity are later checks. |

## V3 additions and conditional constraints

V3 does not impose a Top N cutoff on complete findings, plans, artifacts, diagnostics, evidence rows or actions. Raw/normalized byte ceilings remain in force.

| Field / validator | Complete Host constraints | Baseline gap / disposition |
|---|---|---|
| report | Exact complete/rechecked/coverage/limitations; two booleans; unbounded TextLists | Shape aligned. Empty coverage or rechecked:false is structurally legal; claimed completion is corrected by normalization. |
| findings | Exact id/title/priority/status/confirmed/path/line/details/impact/trigger/rootCause/fixSuggestion/evidence/feedback; title512; priority P0/P1/P2/P3; details/rootCause/fixSuggestion8192 nonblank; impact/trigger4096 nonblank; path4096; nullable positive line; unbounded nonblank evidence | Shape aligned. Unique IDs and feedback suggestion reference require Host checks. |
| findings.confirmed | confirmed:true cannot have status unverified and must have at least1 evidence entry | Static conditional gaps. An unconfirmed finding may legally be open or fixed structurally; normalization prevents an unconfirmed hypothesis from making a PR/bug final report complete. |
| findings.feedback | Exact body/suggestionId; body32768; null or valid identifier; non-null resolves to review.suggestions | Local shape aligned; reference remains Host-owned. Blank body is structurally accepted; selection/submission UX validates usable feedback separately. |
| plans | Exact id/kind/summary/steps/acceptanceCriteria/prerequisites/evidence; id unique; kind feature-implement/issue-fix/reproduction-setup/issue-verify; summary8192 nonblank; steps, acceptanceCriteria and evidence each min1; prerequisites may be empty; all lists unbounded | Static minItems gaps. Each whole escaped compact object ≤24576 bytes, not just the longest string. Plans are instructions, not automatically executed shell text. |
| e2eAssessment | Null or exact level/reason/question/scenarios/expectedResults/prerequisites/evidence/readiness; level not_needed/recommended/required; reason4096 nonblank; question4096, nonblank except not_needed; evidence min1 | Static conditional/minimum gaps. |
| e2eAssessment arrays/readiness | Required/recommended scenarios min1; not_needed scenarios may be empty; scenarios and expectedResults counts exactly match; missing-prerequisites requires min1 prerequisite; all entries nonblank4096; whole object≤24576 serialized UTF-8 bytes | Level/readiness conditions are static; cardinality equality and aggregate byte budget stay diagnosed by Host. not_needed does not force all lists empty or readiness ready. |
| featureAssessment | Null or exact status/summary/reasons/evidence/acceptanceCriteria/questions/alternatives/relatedIssue/planId; summary8192 nonblank; reasons/evidence min1; status ready/needs_information/needs_decision/already_supported/duplicate/not_feasible | Static minItems gaps. |
| featureAssessment conditions | ready→acceptanceCriteria min1 and non-null planId; needs_information→questions min1; needs_decision→alternatives min1; duplicate→non-null relatedIssue; supplied planId resolves to feature-implement | Local status dependencies are static; plan existence/type is cross-item. Other statuses may retain a related issue or plan; do not discard them. |
| bugAssessment | Null or exact status/summary/reasons/evidence/questions/relatedIssue/planId/reproduction; summary8192 nonblank; reasons/evidence min1; status confirmed/needs_information/needs_verification/already_fixed/duplicate/not_a_bug | Static minItems gaps. |
| bugAssessment conditions | needs_information→questions min1; duplicate→non-null relatedIssue; needs_verification→non-null planId; supplied plan resolves and is not feature-implement; confirmed→at least one confirmed finding | Local dependencies static; references and confirmed finding relationship cross-item. confirmed does **not** structurally require reproduced status or a fix plan. |
| reproduction | Exact status/revisionSha/environment/steps/expected/observed/evidence; status reproduced/not_reproduced/not_run/blocked; nullable SHA; environment/expected/observed max8192; steps/evidence TextLists; evidence min1 for reproduced or not_reproduced | Static evidence minimum gap. Host does not require nonblank environment/steps/expected/observed or non-null revision for executed reproduction. Do not introduce an unrelated new requirement. |
| relatedIssue / duplicateOf | Exact repository/number/url; repository256 nonblank with owner39max/repo100max ASCII repository pattern; positive issue number; URL2048 nonblank; absolute HTTPS github.com, default port, no userinfo/query/fragment; URL path equals repository/issues/number case-insensitively | Repository and broad URL form can be emitted statically. Equality with sibling values and exact Uri semantics remain Host-owned. Valid HTTPS links to another issue are still invalid for this object. |
| start-task | Exact kind/reason/body/recommended/taskKind/planId; body empty; taskKind allowed plan kind; identifier planId; referenced plan exists and kind equals taskKind | Local shape aligned; saved-plan reference/type needs diagnostics. |
| close-as-duplicate | Exact kind/reason/body/recommended/duplicateOf; body32768 nonblank; valid related issue; feature or bug investigation status duplicate; investigation.relatedIssue deeply equals duplicateOf | Local shape aligned; investigation state and immutable target equality need Host diagnostics. |
| Top-level investigation exclusivity | featureAssessment and bugAssessment cannot both be non-null | Whole-object conditional. Preserve Host diagnosis if not encoded in provider-supported root schema. |

## Cross-item rules that remain authoritative in the Host

The following cannot be replaced by local item validity: unique IDs across findings/validation/verificationEvidence/plans; unique review suggestion IDs; unique action suggestionIds; referenced suggestion membership; start-task plan existence and kind; feature/bug plan type; confirmed-bug finding existence; close-as-duplicate investigation/target matching; exact E2E scenario/expectation counts; line-range ordering/width; canonical relatedIssue identity.

Generic `uniqueItems:true` is insufficient for arrays of objects because objects with the same id and different text are distinct JSON values. It can express uniqueness for the string-only suggestionIds array if that keyword is supported. Prompt guidance and `ValidationIssues` must continue to explain the property-level requirement.

`WorkflowResult.ValidationIssues(JsonObject,int)` provides bounded, read-only JSONPath issues. For invalid model results, existing-format `INVALID_RESULT_FIELD` diagnostics identify the rule while `INVALID_RESULT` and original output remain. This is explanation, not repair: there is no fallback that deletes malformed rows, substitutes task identities, invents evidence, or promotes a rejected report.

Older rejected reports with a complete retained raw JSON response also receive these explanations in `WithProposalIds` read projections. This neither saves a replacement report nor changes the rejected outcome. An incomplete raw excerpt does not produce an inferred new diagnosis. The result page labels this state as an invalid final-report format and links directly to its diagnostics instead of describing it as missing build prerequisites.

## Normalization and action policy are not schema admission

| Rule | Correct interpretation |
|---|---|
| Accepted task checks | Add absent required rows as not_run. Scoped PR review/verification uses Host-selected check IDs and resets other `required` flags; model text cannot expand accepted execution scope. |
| PassedWithEvidence | Required checks need passed status, nonblank details, ≥1 nonblank evidence item. Failed/not_run rows need explanatory details. A valid but incomplete check yields WORKFLOW_CHECKS_INCOMPLETE, not INVALID_RESULT_FIELD. |
| Process outcome | Cancelled/interrupted/failed state, a nonzero exit or observed error overrides a model completed claim. Schema acceptance cannot turn process failure into success. |
| Scope/revision | Runtime passed/failed current-run evidence outside build-tests, any executed evidence outside static, a wrong PR conclusion revision, or a review draft on verification-only tasks yields scope diagnostics after admission. |
| V3 final coverage | Complete requires rechecked, nonempty recorded coverage, complete required checks, selected workflow assessment fields, no blocking error diagnostic, complete output. Honest limitations remain visible. |
| V3 candidate findings | Unconfirmed/unverified candidates in a claimed complete PR/bug report make it incomplete. A local candidate's fixed finding cannot erase an original-PR defect: sourceStatusCorrections preserves the reported status and retains the original finding as open. |
| Product assessment | Inconclusive original-PR runtime acceptance may coexist with a complete source/build-tests review. Required E2E does not alone make the completed accepted build-tests scope a schema error. |
| Normalized budgets | V2 projection≤704 KiB plus validation≤204/diagnostics≤104; V3 projection≤16.5 MiB. Host projections and compatibility copies have a separate budget from original model output. |
| Publication | Target/account/current SHA, fixed proposal identity, source candidate, existing PR, confirmation, immutable prepared payload and unknown-operation recovery remain separate. Parse errors do not authorize writes; a parsed report is not permission to publish. |
| Manual PR actions | Manual choices remain independent of model success/completeness; only confirmed current-original-PR open P0 blocks manual approval under the existing business rule. Missing E2E does not create a new blanket approval ban. |

## Regression ownership and verification status

- `ResultSchemaParityScenarios.cs`: generated-schema branch and scalar parity, using a package-free evaluator for the emitted subset.
- `ResultContractRegressionScenarios.cs` plus sanitized JSON: real failure, field paths, correction without policy promotion, references/ranges/byte budgets, bounded diagnostics and source immutability.
- `PromptScenarios.cs`: both CLI agents and both result versions receive identity, reference, range and encoding guidance.
- Frontend result summary tests: INVALID_RESULT accurately means report rejection and keeps original output/evidence reachable.

## Completed final validation · 2026-09-18

- Host Release build: zero warnings and errors; the complete automated scenario suite passed, including schema parity, all conditional field families, references, malformed/duplicate/deep JSON, diagnostic redaction and bounds, historical V2 omissions, and read-only older-report projection. Existing background-job environment limitations remain explicitly reported by the runtime fixtures.
- Extension: all 319 source tests and TypeScript checks passed. The details integration regression checks that the error button opens the specific field diagnostics and retained original response without publishing anything.
- Generated V2 schema: 126 properties, depth 4, 1,697 constrained-name characters and 95 enum values. V3: 424 properties, depth 4, 4,735 constrained-name characters and 156 enum values. These fit the [official Structured Outputs subset and limits](https://platform.openai.com/docs/guides/structured-outputs), retrieved on 2026-09-18. No live model request was used to test generation.
- PowerShell's independent JSON Schema evaluator rejected the actual retained report against the new V3 schema. An in-memory copy changing only the four invalid links to null passed, with zero Host validation issues; actual task files were not changed.
- A real Native Messaging `tasks.get` read using the rebuilt Host returned all four exact paths for the retained failed report, kept its blocked/incomplete outcome, and preserved hashes of all 11 original execution/result files checked. The mutable read/handled UI state was excluded from that immutability check.

The constraints are enforced without weakening authoritative acceptance, provenance, completion or publication rules. This change does not retroactively claim that CJK runtime verification passed, and does not rerun or silently repair a user's recorded report.
