# Pulse Actions integration

## Responsibilities

Pulse displays suggested actions and editable drafts. A shared browser client sends structured intentions to the installed Chrome/Edge extension. The extension validates the sender and opens its own UI. The Native Messaging Host validates the request again and owns local agent execution, selected GitHub credentials, durable records, and GitHub writes.

There is no web-server relay or localhost listener in production. Neither a web page nor an agent response supplies executable paths, shell arguments, arbitrary REST endpoints, credentials, or permission overrides.

## Local tasks

The website dialog contains only extension status and local workflows. It detects the extension when opened, displays installation guidance when unavailable, and exposes Run plus task-specific Run options when connected. The previous website review editor, copy/preview/feedback controls, and direct action UI are removed.

Extension Settings stores separate model and reasoning-effort defaults for each CLI. A task may supply only bounded `execution.agent/model/reasoningEffort` overrides. Missing fields inherit the chosen CLI's settings; explicit empty model/effort fields use the CLI default. The Host freezes effective values and provenance before accepting the task and passes them through dedicated CLI arguments. Web callers cannot alter local permissions, paths, provider credentials, or shared settings. Requested settings may be labelled **CLI default**; the actual model is read separately from runtime metadata.

Each agent also has an explicit installation selected from the internal `agents.list` inventory. Unselected or unavailable installations block new tasks rather than silently selecting another executable. The worker observes actual model/effort from its correlated Codex `turn_context` or root Copilot runtime events and persists `status.observedExecution` during execution. Task cards use these actual values; requested defaults remain separate. Missing observed fields show Reading from CLI while active or Not recorded afterward.

Active tasks show progress; stopped or incomplete tasks retain diagnostics and logs. A v3 final report is shown as complete only after its required checks, recheck/completion markers and all paged sections are available. It retains every finding, assessment, coverage limit and saved plan without Top-N truncation. Process exit zero alone does not establish business completion. Historical v1/v2 results remain readable under their original contracts; prior submission history stays separately inspectable.

Public workflows are `issue-fix`, `pr-review`, `reproduction-setup`, `e2e`, `feature-research` and `bug-investigation`. PR review accepts a fixed static, build/tests or UI/E2E scope and binds the current full PR HEAD SHA, as does E2E. New workflows use eight embedded business templates plus shared verification guidance where applicable; there is no prompt download prerequisite. The Host freezes schema v3, instructions, required checks and scope with each new accepted run. Static PR review omits runtime guidance. The webpage prompt is retained as context and request identity; it cannot replace those bindings.

Feature research integrates feasibility into its final assessment; Bug investigation separates confirmed findings from reproduction observations and unresolved verification. Selecting a saved plan starts a separate Host-prepared `feature-implement`, `issue-fix`, `reproduction-setup` or `issue-verify` task. PR verification uses the existing internal `pr-verify` path. When verifying a local implementation, the child restores its retained candidate snapshot rather than silently testing pre-fix source. Websites cannot supply plan provenance or candidate paths. Full report paging, source binding and these follow-up contracts are defined in [workflow v3 wire](workflow-v3-wire.md).

The client saves the complete request before submission. A confirmed explicit submission closes the workflow dialog and shows a nonblocking notification identifying the workflow and target, with a View task action. The website does not poll task progress; the extension owns that view. A timeout or lost connection preserves the original request ID and content. Reopening the dialog performs read-only recovery without announcing a new task. A definitive validation rejection permits an explicit corrected request; uncertain outcomes cannot be replaced. An explicit new run creates a new request ID and captures current context. A successful protocol reply can still contain an immediately failed task; that failure stays visible instead of closing the dialog as a success.

## GitHub actions

Fixed manual PR/Issue controls do not require a successful model run, a complete report or an eligible AI proposal. Fresh account, target, permission and revision checks still apply. The additional Approve restriction is a confirmed unresolved P0 on the current original PR; P1, missing E2E or incomplete analysis affect advice without creating that permission gate. The Host evaluates P0 using full stored findings and compatible original-source evidence, not the visible result page. The user chooses which finding feedback and suggestions to include; unselected findings are not silently submitted. Closing records an explicit reason and does not add an implicit comment.

Comment, review, approve, trigger CI, squash merge, and create PR use durable typed drafts independent of local task runs. The webpage may prepare a draft and read its status. It cannot invoke the internal preview/submit/cancel methods. The extension shows the actual account, target, pinned SHA, final body, selected comments, and additional assignment before an explicit confirmation.

The Host builds fixed GitHub API requests from validated fields. It rechecks current target state, permissions, revision, applicable merge checks, and duplicate markers before writing. Multi-step writes persist each step; partial or unknown outcomes must not be silently replayed. A read-only result check can establish which uncertain step completed. Any explicit remaining-content draft is confirmed separately and must exclude completed writes. The configured account is applied to the individual `gh` call, without switching the user's global account.

## Detection and installation

`bridge.hello` is answered by the extension independently of Host availability. Capability/configuration checks follow separately. No extension response is described as "not detected" because a disabled extension or a disallowed origin can appear the same as a missing installation. A Host failure links to repair; configuration failures link to extension Settings.

The website installation route explains both required components and preserves the original action for retry. Store URLs and IDs are deployment configuration. Until published IDs are available, the checked-in manifest public key gives unpacked Chrome/Edge installations the stable ID `nlpkbkhlnocknpgkjnmhpahapffdhblo`. This public key identifies an unpacked preview; it is not a release signing credential or a published store listing.

Production origin remains `https://cautious-memory-r38ze9j.pages.github.io`. Local integration permits only `http://localhost:8080`, `http://127.0.0.1:8080`, `http://localhost:8081`, and `http://127.0.0.1:8081` with both the development extension and a Host installed with development origins enabled. Run the Pulse development server on port 8080 or 8081 for this flow; other local ports are rejected.

## Validation boundary

Tasks also shows five recently finished runs ordered by completion time, including failures, with direct details/log links. Raw CLI logs are saved before parsing. Temporary progress-snapshot contention is diagnostic rather than fatal; a genuine output failure records stream, stage, exception type and error code. The original 22-second failed user run could not establish its exact historic trigger because the earlier Host discarded the inner exception; its saved stdout/stderr replayed successfully and its records are preserved.

Automated checks use fake browser messaging and offline GitHub responses. Browser UI validation must label sample operations as sample data. No test of this integration should submit a real review/comment, merge a PR, create a PR, or execute a real PowerToys agent task as an incidental verification step. The current real Codex/Copilot and Chrome/Edge matrix remains pending separate user testing; neither this documentation update nor sample validation establishes those passes.
