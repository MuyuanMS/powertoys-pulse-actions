/* Fixture mode is session-only. Real diagnostics require an explicit diagnostics=1 URL. */
(() => {
  'use strict';
  if (!['127.0.0.1', 'localhost', '[::1]'].includes(location.hostname)) {
    throw new Error('The sample bridge only runs in a local preview.');
  }

  const storageKey = 'pulse-ui-preview-v17';
  const diagnosticsEnabled = new URL(location.href).searchParams.get('diagnostics') === '1';
  // Omit preview chrome when measuring the action popup's own intrinsic size.
  // Connection text still labels this as UI sample data; no native popup is claimed.
  const intrinsicPopupFixture = new URL(location.href).searchParams.get('sizing') === 'native-popup';
  // Source pages use their normal draft store; this preview keeps those drafts in this session too.
  Object.defineProperty(window, 'localStorage', { value: sessionStorage, configurable: true });
  const diagnosticMethods = new Set(['agents.test.start', 'agents.test.get', 'agents.test.cancel', 'github.accounts', 'prompts.list', 'prompts.sync', 'prompts.get']);
  const sha = 'b27e8c56d3aa4189ee274cd41e8b83b91ac902f6';
  const now = Date.now();
  const at = minutes => new Date(now - minutes * 60_000).toISOString();
  const copy = value => JSON.parse(JSON.stringify(value));
  const active = run => ['accepted', 'running'].includes(run.status.state);
  const requiredChecks = { 'pr-review': ['context', 'local-review', 'verification'], 'issue-fix': ['reproduction', 'implementation', 'verification'], e2e: ['setup', 'e2e'], 'reproduction-setup': ['reproduction', 'instructions'] };
  const publishingActions = new Set(['approve', 'suggestChanges', 'requestChanges', 'comment', 'close', 'create-pr', 'merge-pr', 'trigger-ci', 'close-as-duplicate']);
  function workflowChecks(task) {
    const mode = task.reviewOptions?.mode;
    if (task.actionKind === 'pr-verify') return mode === 'build-tests' ? ['setup', 'build-tests'] : ['setup', 'e2e'];
    if (task.actionKind === 'pr-review' && mode) return { static: ['context', 'local-review'], 'build-tests': ['context', 'local-review', 'build-tests'], 'ui-e2e': ['context', 'local-review', 'setup', 'e2e'] }[mode] ?? [];
    return requiredChecks[task.actionKind] ?? [];
  }
  const agentDefaults = { codex: { model: '', reasoningEffort: '' }, copilot: { model: '', reasoningEffort: '' } };
  const reasoningEfforts = { codex: ['minimal', 'low', 'medium', 'high', 'xhigh', 'ultra'], copilot: ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'] };
  const cliInstallations = {
    codex: [
      { path: 'C:\\Demo\\Tools\\Codex-0.145.0\\codex.exe', resolvedPath: 'C:\\Demo\\Tools\\Codex-0.145.0\\codex.exe', source: 'Standalone history', sources: ['Standalone history'], aliases: ['C:\\Demo\\Tools\\Codex-0.145.0\\codex.exe'], available: true, version: '0.145.0' },
      { path: 'C:\\Demo\\Apps\\Codex\\resources\\codex.exe', resolvedPath: 'C:\\Demo\\Apps\\Codex\\resources\\codex.exe', source: 'Desktop app', sources: ['Desktop app'], aliases: ['C:\\Demo\\Apps\\Codex\\resources\\codex.exe'], available: true, version: '0.153.4', capabilities: { model: true, reasoningEffort: true } },
      { path: 'C:\\Demo\\Tools\\codex.exe', resolvedPath: 'C:\\Demo\\Tools\\codex.exe', source: 'Standalone current', sources: ['Standalone current', 'PATH'], aliases: ['C:\\Demo\\Tools\\codex.exe'], available: true, version: '0.154.0' },
    ],
    copilot: [
      { path: 'C:\\Demo\\npm\\node_modules\\@github\\copilot\\copilot.exe', resolvedPath: 'C:\\Demo\\npm\\node_modules\\@github\\copilot\\copilot.exe', source: 'npm', sources: ['npm', 'PATH'], aliases: ['C:\\Demo\\npm\\node_modules\\@github\\copilot\\copilot.exe'], available: true, version: '1.0.83', capabilities: { model: true, reasoningEffort: true } },
      { path: 'C:\\Demo\\WinGet\\Copilot\\copilot.exe', resolvedPath: 'C:\\Demo\\WinGet\\Copilot\\copilot.exe', source: 'WinGet', sources: ['WinGet'], aliases: ['C:\\Demo\\WinGet\\Copilot\\copilot.exe'], available: true, version: '1.0.79', capabilities: { model: true, reasoningEffort: false } },
    ],
  };
  const cliProbe = (agent, path) => (cliInstallations[agent] ?? []).find(item => [item.path, item.resolvedPath, ...(item.aliases ?? [])].some(value => value.toLowerCase() === String(path || '').toLowerCase()));
  function resolveExecution(config, execution = {}) {
    const agent = execution.agent ?? config.agent;
    if (!['codex', 'copilot'].includes(agent)) throw new Error('Choose a sample Codex or Copilot agent.');
    const defaults = config.agentDefaults?.[agent] ?? agentDefaults[agent];
    const model = execution.model ?? defaults.model;
    const reasoningEffort = execution.reasoningEffort ?? defaults.reasoningEffort;
    if (model && !/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(model)) throw new Error('The sample model name is invalid.');
    if (reasoningEffort && !reasoningEfforts[agent].includes(reasoningEffort)) throw new Error('The sample reasoning effort is unsupported for this CLI.');
    return { agent, model, reasoningEffort, executionSource: { agent: execution.agent === undefined ? 'default' : 'task', model: !model ? 'cli' : execution.model === undefined ? 'default' : 'task', reasoningEffort: !reasoningEffort ? 'cli' : execution.reasoningEffort === undefined ? 'default' : 'task' } };
  }
  const suggestion = {
    id: 'windows-path-comparer',
    path: 'src/modules/launcher/Plugins/Microsoft.PowerToys.Run.Plugin.Program/Programs/Win32Program.cs',
    line: 148,
    startLine: 147,
    side: 'RIGHT',
    body: 'Compare paths without case sensitivity to avoid duplicate search results for the same program.',
    replacement: 'var programs = discoveredPrograms\n    .DistinctBy(program => program.FullPath, StringComparer.OrdinalIgnoreCase);',
  };
  const runConfig = {
    agent: 'codex', model: '', reasoningEffort: '', executionSource: { agent: 'default', model: 'cli', reasoningEffort: 'cli' }, cliPath: 'C:\\Demo\\Tools\\codex.exe',
    mainRepoFolder: 'C:\\Demo\\Repos\\PowerToys', worktreeRoot: 'C:\\Demo\\PowerToys-worktrees', permission: 'read-only',
  };
  function makeRun(runId, state, age, target, actionKind, actionId, progress) {
    return {
      runId,
      task: {
        requestId: `${runId}-request`, actionId, actionKind, repository: 'microsoft/PowerToys',
        target, ...(target.type === 'pr' ? { expectedHeadSha: sha } : {}),
        prompt: `[Sample task] Analyze microsoft/PowerToys ${target.type.toUpperCase()} #${target.number}. Follow repository instructions and provide an English summary, validation results, and next steps.\nThis record is a UI sample. No real CLI was started.`,
      },
      config: { ...copy(runConfig), repoFolder: `${runConfig.worktreeRoot}\\pulse-${runId}`, worktreeBranch: `codex/pulse-${runId}` },
      status: {
        state, createdAt: at(age), startedAt: at(age - 1), updatedAt: at(Math.max(age - 6, 0)),
        sequence: 5, latestProgress: progress,
        ...(['succeeded', 'failed', 'cancelled', 'interrupted'].includes(state) ? { endedAt: at(age - 6), exitCode: state === 'succeeded' ? 0 : state === 'failed' ? 1 : null } : {}),
      },
      view: { read: state === 'succeeded' && actionKind === 'issue-fix', handled: actionKind === 'issue-fix' },
    };
  }
  function v2Result(result) {
    const value = { schemaVersion: 2, assessment: null, findings: [], artifacts: [], validation: [], diagnostics: [], nextActions: [], review: null, needsReview: true, structured: true, cliExitCode: 0, ...result };
    value.blockers = value.diagnostics.filter(item => item.severity === 'error').map(item => item.message);
    value.nextSteps = copy(value.nextActions.filter(item => value.outcome === 'completed' || !publishingActions.has(item.kind)));
    return value;
  }
  function v3Result(result) {
    const value = { schemaVersion: 3, outcome: 'completed', phase: 'reporting', assessment: null, reviewConclusion: null, verificationEvidence: [], report: { complete: true, rechecked: true, coverage: ['Sample selected scope was inspected, rechecked, and checked for omissions.'], limitations: [] }, findings: [], artifacts: [], validation: [], diagnostics: [], nextActions: [], review: null, needsReview: true, e2eAssessment: null, featureAssessment: null, bugAssessment: null, plans: [], structured: true, cliExitCode: 0, ...result };
    value.blockers = value.diagnostics.filter(row => row.severity === 'error').map(row => row.message);
    value.nextSteps = copy(value.nextActions);
    return value;
  }
  function workflowV3Samples() {
    const repository = 'example/pulse-workflow-samples';
    const issueRef = number => ({ repository, number, url: `https://github.com/${repository}/issues/${number}` });
    const action = (kind, reason, body = '', recommended = false, extra = {}) => ({ kind, reason, body, recommended, ...extra });
    const finding = (id, priority, inline = false) => ({ id, title: `Sample ${priority}: ${id}`, priority, status: 'open', confirmed: true, path: inline ? suggestion.path : '', line: inline ? suggestion.line : null, details: `Sample confirmed defect ${id}; the recorded source path produces the described incorrect result.`, impact: 'The sample operation can lose a queued task or return an incorrect result.', trigger: 'The sample operation processes an item in the recorded edge state.', rootCause: 'The retained sample branch excludes only running items. Other nonterminal states reach the incorrect branch; no unsupported runtime claim is made.', fixSuggestion: 'Match the intended terminal states explicitly and preserve the recorded regression coverage.', evidence: [`Sample source inspection and recheck establish the incorrect branch for ${id}.`], feedback: { body: `### ${priority}: ${id}\n\nThe sample evidence confirms the incorrect state check. Preserve nonterminal items and add the focused regression coverage.  \n`, suggestionId: inline ? suggestion.id : null } });
    const e2e = level => ({ level, reason: level === 'not_needed' ? 'Sample source analysis and existing unit-test coverage fully cover this pure comparison change.' : 'A focused runtime observation can establish the remaining first-dialog focus behavior.', question: level === 'not_needed' ? '' : 'Does focus return to the original setting after the first dialog closes?', scenarios: level === 'not_needed' ? [] : ['Open and close the first Settings dialog on the original PR revision.', 'Repeat the same action after switching the display scale.'], expectedResults: level === 'not_needed' ? [] : ['Focus returns to the original setting.', 'Focus still returns to the same setting after the display-scale change.'], prerequisites: level === 'not_needed' ? [] : ['An isolated interactive Windows desktop with the sample UI automation driver.'], evidence: ['Sample source review identifies the exact behavior and existing coverage. No current runtime execution is claimed.'], readiness: level === 'required' ? 'missing-prerequisites' : level === 'recommended' ? 'unknown' : 'ready' });
    const pr = (name, number, level, findings = []) => {
      const run = makeRun(`demo-v3-pr-${name}`, 'succeeded', 100 + number - 200, { type: 'pr', number }, 'pr-review', `Sample v3 PR: ${name}`, 'The sample internal review loop converged; all final findings are retained.');
      run.task.repository = repository; run.task.reviewOptions = { mode: 'static' };
      run.provenance = { version: 1, source: 'host', observation: 'boundary-snapshots', subject: 'original-pr', expectedHeadSha: sha, start: { capturedAt: run.status.startedAt, headSha: sha, workingTree: 'clean', diffHash: 'a'.repeat(64) }, end: { capturedAt: run.status.endedAt, headSha: sha, workingTree: 'clean', diffHash: 'a'.repeat(64) } };
      run.result = v3Result({ summary: `Sample: the ${name} static review completed its selected scope and recheck. Inspect the full final finding list and the separate E2E assessment.`, assessment: { subject: 'original-pr', status: findings.length ? 'failed' : 'inconclusive', summary: 'The sample product assessment is distinct from code-review completion.', revisionSha: sha }, reviewConclusion: { status: findings.some(row => ['P0', 'P1'].includes(row.priority)) ? 'changes-requested' : 'no-blocking-findings', summary: 'All sample confirmed findings were rechecked and the selected source scope was covered.', revisionSha: sha, blockingUncertainties: [] }, findings, e2eAssessment: e2e(level), validation: workflowChecks(run.task).map(id => ({ id, name: id, status: 'passed', required: true, details: 'Completed the selected sample static review work without running builds, tests, or UI.', evidence: ['Sample source inspection and internal recheck completed.'] })), review: { headSha: sha, body: 'Sample final review. Select only the findings you intend to send.', suggestions: findings.some(row => row.feedback.suggestionId) ? [copy(suggestion)] : [] } });
      return run;
    };
    const p0 = pr('p0', 200, 'required', [...Array.from({ length: 224 }, (_, index) => finding(`reviewed-item-${String(index + 1).padStart(3, '0')}`, 'P2', index === 0)), finding('blocking-data-loss', 'P0')]);
    p0.result.report.coverage = Array.from({ length: 45 }, (_, index) => `Sample reviewed and rechecked affected path ${index + 1}.`);
    const p1 = pr('p1-required', 201, 'required', [finding('queued-task-deletion', 'P1', true), finding('secondary-message', 'P3')]);
    const minor = pr('p2-p3', 202, 'recommended', [finding('path-comparison', 'P2', true), finding('missing-detail', 'P3')]);
    const unnecessary = pr('not-needed', 203, 'not_needed');
    unnecessary.result.nextActions = [action('none', 'No follow-up work was identified in the recorded sample scope. Manual actions remain available.', '', true)];
    const required = pr('required', 204, 'required');
    const incomplete = pr('incomplete', 205, 'recommended');
    incomplete.result.outcome = 'blocked'; incomplete.result.report = { complete: false, rechecked: false, coverage: ['Sample first source path inspected.'], limitations: ['The sample review stopped before the remaining paths and internal recheck were completed.'] };
    incomplete.result.summary = 'Sample: review incomplete. The retained source observations are partial and do not establish a complete final review.';
    incomplete.result.reviewConclusion = null; incomplete.result.e2eAssessment = null;
    const verificationReady = pr('verification-ready', 206, 'required');
    verificationReady.result.e2eAssessment.readiness = 'ready';
    verificationReady.result.e2eAssessment.evidence = ['Sample recorded requirements are available. Starting verification still performs its own preflight; no runtime result is claimed.'];
    const pageError = pr('page-error', 207, 'not_needed', Array.from({ length: 26 }, (_, index) => finding(`paged-finding-${index + 1}`, index === 25 ? 'P1' : 'P2')));
    const unknownOperation = pr('unknown-operation', 208, 'not_needed');
    const candidate = makeRun('demo-v3-issue-candidate', 'succeeded', 130, { type: 'issue', number: 450 }, 'issue-fix', 'Sample verified candidate ready for Draft PR', 'The saved candidate and publication draft are ready for review.');
    candidate.task.repository = repository; candidate.config.permission = 'workspace-write'; candidate.config.worktreeBranch = 'fix/settings-focus';
    candidate.result = v3Result({ summary: 'Sample: the focused local fix passed its recorded verification. Review the saved candidate, title, and complete description before creating a Draft PR.',
      assessment: { subject: 'local-candidate', status: 'passed', summary: 'The sample candidate passed focused checks; the target Issue remains open.', revisionSha: 'd'.repeat(40) },
      artifacts: [{ label: 'Candidate changes and validation (sample)', path: 'C:\\Demo\\Pulse\\runs\\demo-v3-issue-candidate\\candidate.md' }],
      validation: workflowChecks(candidate.task).map(id => ({ id, name: id, status: 'passed', required: true, details: 'Recorded sample check passed for the saved candidate. No real command ran in this preview.', evidence: ['Sample candidate SHA: ' + 'd'.repeat(40)] })),
      nextActions: [action('create-pr', 'Create a Draft PR from the saved candidate after reviewing the source, base, and description. This preview simulates the existing remote-branch path only.', '', true,
        { pullRequest: { head: 'pulse-demo:fix/settings-focus', sourceHeadSha: 'd'.repeat(40), base: 'main', title: 'Restore keyboard focus after closing a Settings dialog', body: '## Changes\n\nRestore focus to the original setting after the dialog closes.\n\n## Validation\n\nSample focused checks passed on the saved candidate.\n\nFixes #450\n', draft: true } })] });
    const plan = (id, kind, summary, prerequisites = []) => ({ id, kind, summary, steps: ['Read the selected saved investigation and preserve its target.', 'Perform the focused change or experiment described by this plan.', 'Run and interpret the relevant validation within the selected scope.'], acceptanceCriteria: ['The sample expected behavior is established with retained evidence.', 'Unrelated behavior and the immutable parent report remain unchanged.'], prerequisites, evidence: ['The sample investigation identifies the affected behavior and a bounded implementation or experiment.'] });
    const featureRows = [
      ['ready', 'Saving task-list filters is feasible and the implementation scope is defined; this is not maintainer acceptance.', 'Persist repository and status filters using the existing sample settings store.', [], []],
      ['needs_information', 'The required notification lifecycle is not specified in the issue or attachments.', 'Popup closure and full browser shutdown require different delivery mechanisms.', ['Should notifications continue after the popup closes, or after the entire browser process exits?'], []],
      ['needs_decision', 'Two supported history-retention policies need a product choice.', 'Both bounded-count and bounded-age retention can be implemented using the existing sample storage.', [], ['Keep the most recent 500 records: predictable capacity.', 'Keep 30 days: predictable time range but variable record count.']],
      ['already_supported', 'Repository filtering already supports the requested view.', 'Sample source, help text, and operation notes all show the existing repository selector.', [], []],
      ['duplicate', 'The requested behavior matches the original Issue #120; this report adds only a shortcut placement preference.', 'The sample requirement comparison shows the same goal and acceptance criteria.', [], []],
      ['not_feasible', 'Real-time GitHub updates cannot be obtained while all external connectivity is prohibited.', 'The requested live information exists only on the remote service; local tool availability is not the reason.', [], ['Show cached data with its last synchronization time.', 'Refresh the remote data when connectivity is allowed.']],
    ];
    const features = featureRows.map(([status, summary, reason, questions, alternatives], index) => {
      const run = makeRun(`demo-v3-feature-${status}`, 'succeeded', 110 + index, { type: 'issue', number: 300 + index }, 'feature-research', `Sample feature investigation: ${status}`, 'The sample feature research produced a unified conclusion and supporting plan.');
      run.task.repository = repository; run.config.worktreeBase = sha;
      const implementation = plan('implement-filter-memory', 'feature-implement', 'Persist the selected repository and status filters, with compatible defaults for older settings.');
      const assessment = { status, summary: `Sample: ${summary}`, reasons: [reason], evidence: [`Sample research evidence: ${reason}`], acceptanceCriteria: status === 'ready' ? ['Reload restores the selected filters.', 'Clearing filters and reading older settings preserve their intended defaults.'] : [], questions, alternatives, relatedIssue: status === 'duplicate' ? issueRef(120) : null, planId: status === 'ready' ? implementation.id : null };
      let actions = [action('comment', 'Inspect and send the sample investigation explanation.', status === 'already_supported' ? 'Sample usage: Tasks → Repository → choose a repository. This filters the currently loaded sample tasks.' : `${assessment.summary}\n\n${questions.join('\n') || alternatives.join('\n') || reason}`, true)];
      const plans = status === 'ready' ? [implementation] : [];
      if (status === 'ready') actions = [action('start-task', 'Start the explicitly selected implementation plan.', '', true, { taskKind: implementation.kind, planId: implementation.id })];
      if (status === 'needs_decision') {
        for (const [option, text] of alternatives.entries()) { const selected = plan(`retention-option-${option + 1}`, 'feature-implement', text); plans.push(selected); actions.push(action('start-task', `Choose and implement this sample alternative: ${text}`, '', false, { taskKind: selected.kind, planId: selected.id })); }
      }
      if (status === 'duplicate') actions = [action('close-as-duplicate', 'Associate this request with the fixed original Issue #120 and close only this issue.', 'Sample: this feature request has the same goal and acceptance criteria. Preserve the shortcut placement detail in the original discussion.', true, { duplicateOf: issueRef(120) }), action('comment', 'Send only the association explanation without closing.', `Sample related request: ${issueRef(120).url}. The shortcut placement is the only recorded difference.`)];
      run.result = v3Result({ summary: assessment.summary, featureAssessment: assessment, plans, nextActions: actions });
      return run;
    });
    const bugRows = [
      ['confirmed', 'not_run', 'Clearing history deletes queued tasks because the predicate excludes only running tasks.', 'Complete sample code paths prove the defect without claiming UI reproduction.', 'issue-fix'],
      ['needs_information', 'not_reproduced', 'The report cannot be resolved without the failing CLI wrapper and task output.', 'Three sample native-CLI attempts did not reproduce the report; this does not disprove it.', null],
      ['needs_verification', 'blocked', 'Logs place duplicate notifications after resume, but duplicate timer registration is still a hypothesis.', 'The sample environment cannot enter real sleep, so the focused experiment needs another test environment.', 'reproduction-setup'],
      ['already_fixed', 'not_reproduced', 'The sample upstream fix in PR #150 is included in sample version 1.4.2.', 'Source comparison and old-version reproduction identify the fix; the repaired version no longer reproduced it.', null],
      ['duplicate', 'not_run', 'The same trigger, log signature, and affected versions are tracked by original Issue #123.', 'The sample investigation compared the two reports; the original issue reproduction is attributed, not claimed as this run.', null],
      ['not_a_bug', 'reproduced', 'Read-only mode rejects writes as documented and configured.', 'The reported refusal was reproduced and matches the selected sample permission policy.', null],
    ];
    const bugs = bugRows.map(([status, reproductionStatus, summary, reason, taskKind], index) => {
      const run = makeRun(`demo-v3-bug-${status}`, 'succeeded', 120 + index, { type: 'issue', number: 400 + index }, 'bug-investigation', `Sample bug investigation: ${status}`, 'The sample investigation completed its internal fact, hypothesis, and omission checks.');
      run.task.repository = repository; run.config.worktreeBase = sha;
      const selectedPlan = taskKind ? plan(`bug-${status}-plan`, taskKind, status === 'confirmed' ? 'Preserve queued tasks by selecting only terminal history records.' : 'Test sleep/resume notifications and distinguish duplicate timer registration from repeated source events.', status === 'needs_verification' ? ['A Windows test device that can enter and resume from real sleep.'] : []) : null;
      const assessment = { status, summary: `Sample: ${summary}`, reasons: [reason], evidence: [`Sample investigation evidence: ${reason}`], questions: status === 'needs_information' ? ['Provide the failing sample task ID, redacted exit output, and whether a CLI wrapper script was used.'] : [], relatedIssue: status === 'duplicate' ? issueRef(123) : null, planId: selectedPlan?.id ?? null, reproduction: { status: reproductionStatus, revisionSha: status === 'already_fixed' ? 'd'.repeat(40) : sha, environment: status === 'needs_verification' ? 'Sample virtual environment without real sleep capability.' : status === 'confirmed' || status === 'duplicate' ? 'Sample static investigation; no current runtime reproduction was executed.' : 'Sample isolated Windows environment with the explicitly recorded CLI and permission setting.', steps: ['Inspect the saved sample version and environment.', 'Follow the issue-specific sample trigger and compare expected with observed behavior.'], expected: status === 'not_a_bug' ? 'Read-only mode refuses to write.' : 'The sample operation produces one correct result and preserves nonterminal work.', observed: reproductionStatus === 'not_run' ? 'No current-run reproduction was performed; the conclusion uses attributed code or issue evidence.' : reproductionStatus === 'blocked' ? 'The required sleep/resume experiment could not run.' : reproductionStatus === 'not_reproduced' ? 'The reported symptom was not observed in the recorded sample attempts; the remaining evidence still determines the conclusion.' : 'The sample write was refused exactly as the read-only policy specifies.', evidence: reproductionStatus === 'reproduced' || reproductionStatus === 'not_reproduced' ? [reason] : [] } };
      let actions = [action('comment', 'Inspect and send the complete sample investigation conclusion.', `${assessment.summary}\n\n${assessment.questions.join('\n') || reason}`, true)];
      if (selectedPlan) actions.unshift(action('start-task', 'Run the selected saved fix or reproduction plan after checking its prerequisites.', '', true, { taskKind: selectedPlan.kind, planId: selectedPlan.id }));
      if (selectedPlan) actions[1].recommended = false;
      if (status === 'duplicate') actions = [action('close-as-duplicate', 'Associate the verified duplicate with original Issue #123 and close only this issue.', 'Sample: the trigger, log signature, and affected versions match the original report.', true, { duplicateOf: issueRef(123) }), action('comment', 'Send only the duplicate association without closing.', `Sample matching report: ${issueRef(123).url}.`)];
      if (['already_fixed', 'not_a_bug'].includes(status)) actions.push(action('close', `Close after confirming the recorded ${status === 'already_fixed' ? 'upstream fix and released version' : 'expected behavior and configuration'} explanation.`));
      const plans = selectedPlan ? [selectedPlan] : [];
      if (status === 'needs_information') { const later = plan('verify-wrapper-behavior', 'issue-verify', 'Compare the supplied wrapper with the native CLI after the missing report is supplied.', ['The reporter has supplied the missing failing output and wrapper details.']); plans.push(later); actions.push(action('start-task', 'After obtaining the missing facts, run this focused verification plan.', '', false, { taskKind: later.kind, planId: later.id })); }
      run.result = v3Result({ summary: assessment.summary, bugAssessment: assessment, plans, nextActions: actions, findings: status === 'confirmed' ? [finding('queued-history-loss', 'P1')] : [] });
      return run;
    });
    return [p0, p1, minor, unnecessary, required, incomplete, verificationReady, pageError, unknownOperation, ...features, ...bugs, candidate];
  }
  function structuredResultSamples() {
    const check = (id, name, status, details, evidence = []) => ({ id, name, status, required: true, details, evidence });
    const step = (kind, reason, body = '') => ({ kind, reason, body });
    const completed = makeRun('demo-v2-completed-review', 'succeeded', 10, { type: 'pr', number: 41282 }, 'pr-review', 'Completed local review with an open finding', 'Local review and applicable verification completed. Inspect the finding before preparing a GitHub review.');
    completed.result = v2Result({
      outcome: 'completed', phase: 'reporting',
      assessment: { subject: 'original-pr', status: 'failed', summary: 'The reviewed PR still contains a path-comparison defect; the review itself is complete.', revisionSha: sha },
      summary: 'Sample: the local review is complete. One open path-comparison issue and its inline suggestion need human inspection before any GitHub publication.',
      findings: [{ id: 'path-case-duplicate', title: 'Path casing can duplicate a discovered program', severity: 'medium', status: 'open', path: suggestion.path, line: suggestion.line, details: 'The sample change deduplicates the raw path with a case-sensitive comparer, so two differently cased paths can represent the same Windows program.', evidence: ['Sample PR diff: RIGHT lines 147–148 use DistinctBy without a Windows path comparer.', 'Focused sample comparison: C:\\Apps\\Tool.exe and c:\\apps\\tool.exe remain two entries.'] }],
      artifacts: [{ label: 'Local review notes (sample)', path: 'C:\\Demo\\Pulse\\runs\\demo-v2-completed-review\\review.md' }],
      validation: [
        check('context', 'Target and local repository context', 'passed', 'Inspected the immutable sample target and repository instructions.', [`Sample PR #41282 reviewed at ${sha}.`]),
        check('local-review', 'Local code review', 'passed', 'Reviewed the changed call paths and recorded the remaining issue.', ['Inspected program discovery, path normalization, and the RIGHT-side suggestion range.']),
        check('verification', 'Applicable focused verification', 'passed', 'Verified the sample comparer behavior and suggestion coordinates. No build, desktop session, or hardware behavior is claimed.', ['Sample comparison confirms the case-sensitive duplicate.', 'The suggestion covers RIGHT lines 147–148 in the retained sample diff.']),
      ],
      review: { headSha: sha, body: 'Use a case-insensitive comparer when deduplicating program paths. This sample draft is not submitted to GitHub.', suggestions: [copy(suggestion)] },
      nextActions: [step('inspectResult', 'Inspect the retained review notes and finding evidence.'), step('suggestChanges', 'Inspect and select the SHA-bound inline suggestion.', 'Use a case-insensitive comparer when deduplicating program paths. This is a simulated review draft.'), step('requestChanges', 'Inspect the finding and review body before requesting changes.', 'Please address the duplicate Windows program paths described in this sample finding.'), step('comment', 'Inspect the path-comparison finding before posting this comment.', '    var comparer = StringComparer.OrdinalIgnoreCase;\n\nThe local sample review found a case-sensitive path deduplication issue.  \n\n'), step('comment', 'Inspect the separate verification note before posting this comment.', '  ### Verification note\n\n- Inspect the retained sample evidence.  \n- No desktop or hardware verification is claimed.\n\n  ')],
    });
    completed.result.review.suggestions.push({ ...copy(suggestion), id: 'program-discovery-order', body: 'Keep program ordering deterministic after deduplication.', replacement: 'var programs = discoveredPrograms\n    .OrderBy(program => program.FullPath, StringComparer.OrdinalIgnoreCase);' });
    completed.result.nextActions.find(item => item.kind === 'suggestChanges').suggestionIds = ['windows-path-comparer'];
    completed.result.nextActions.find(item => item.kind === 'requestChanges').suggestionIds = ['windows-path-comparer'];
    completed.result.nextActions.push({ ...step('suggestChanges', 'Inspect this separate ordering proposal without reusing selections from the path-comparison proposal.', 'Keep the sample program results in deterministic order.'), suggestionIds: ['program-discovery-order'] }, step('approve', 'This proposed approval is unavailable while the original PR has an unresolved medium finding.'), step('merge-pr', 'This proposed merge is unavailable even when remote approval and CI are present.'));
    const blocked = makeRun('demo-v2-blocked-setup', 'succeeded', 20, { type: 'issue', number: 41283 }, 'reproduction-setup', 'Setup blocked by a missing reproduction driver', 'The CLI reported a missing reproduction driver and exited with code zero.');
    blocked.status.error = { code: 'REPRODUCTION_DRIVER_MISSING', message: 'The sample reproduction driver is unavailable.', guidance: 'Review the execution configuration and required test environment, then retry as a new run.' };
    blocked.result = v2Result({
      outcome: 'blocked', phase: 'setup',
      summary: 'Sample: the CLI reported that a required reproduction driver is unavailable and exited with code zero. Reproduction setup remains blocked.',
      artifacts: [{ label: 'Setup diagnostics (sample)', path: 'C:\\Demo\\Pulse\\runs\\demo-v2-blocked-setup\\setup.txt' }],
      validation: [check('reproduction', 'Reproduce the reported behavior', 'not_run', 'The required reproduction driver must be available before reproduction can begin.'), check('instructions', 'Document reproduction instructions', 'not_run', 'No verified reproduction steps are available yet.')],
      diagnostics: [{ code: 'REPRODUCTION_DRIVER_MISSING', severity: 'error', message: blocked.status.error.message, recovery: 'configure' }],
      nextActions: [step('configure', 'Review the execution configuration in Settings and the test environment described in the diagnostics.'), step('inspectResult', 'Inspect the retained setup diagnostics and missing driver information.'), step('rerun', 'After the reproduction driver is available, retry as a new run using the current bundled prompt.')],
    });
    const invalid = makeRun('demo-v2-invalid-result', 'succeeded', 30, { type: 'pr', number: 41284 }, 'pr-review', 'Invalid output despite a zero CLI exit code', 'The CLI exited with code zero, but its response did not contain a valid structured result.');
    invalid.status.error = { code: 'INVALID_RESULT', message: 'The sample response is missing required structured-result fields.', guidance: 'Inspect the original output and retry as a new run.' };
    invalid.result = v2Result({
      outcome: 'blocked', phase: 'reporting', structured: false,
      summary: 'Sample: the CLI exited with code zero, but its incomplete response cannot establish that the local review completed.',
      diagnostics: [{ code: 'INVALID_RESULT', severity: 'error', message: invalid.status.error.message, recovery: 'inspectResult' }],
      nextActions: [step('inspectResult', 'Inspect the original output and execution logs.'), step('rerun', 'Retry as a new run with the current bundled prompt and settings.')],
      rawOutput: '{"schemaVersion":2,"outcome":"completed","summary":"Sample incomplete model response"}',
    });
    const failed = makeRun('demo-v2-process-failed', 'failed', 40, { type: 'issue', number: 41285 }, 'issue-fix', 'CLI failure with retained reproduction evidence', 'The CLI stopped while applying the focused fix.');
    failed.view = { read: false, handled: false };
    failed.status.exitCode = 17;
    failed.status.error = { code: 'CLI_EXECUTION_FAILED', message: 'The sample CLI exited with code 17 while applying the fix.', guidance: 'Inspect the retained changes and execution logs before retrying.' };
    failed.result = v2Result({
      outcome: 'failed', phase: 'implementation', cliExitCode: 17,
      summary: 'Sample: reproduction evidence was captured, but the CLI failed before completing the fix or verification.',
      findings: [{ id: 'focus-regression', title: 'Keyboard focus is not restored after the dialog closes', severity: 'medium', status: 'unverified', path: '', line: null, details: 'The sample reproduction notes capture the reported symptom. The attempted implementation has not been reviewed or verified.', evidence: ['Retained sample reproduction notes describe focus returning to the page body.'] }],
      artifacts: [{ label: 'Reproduction and partial implementation notes (sample)', path: 'C:\\Demo\\Pulse\\runs\\demo-v2-process-failed\\partial-result.md' }],
      validation: [check('reproduction', 'Reproduce the reported behavior', 'passed', 'Captured the reported behavior in the sample reproduction notes.', ['Sample keyboard sequence: open Settings dialog, close it, inspect the focused element.']), check('implementation', 'Complete the focused fix', 'failed', 'The CLI stopped before finishing the implementation.', ['Sample process exit code: 17.']), check('verification', 'Verify the completed fix', 'not_run', 'The implementation is incomplete.')],
      diagnostics: [{ code: 'CLI_EXECUTION_FAILED', severity: 'error', message: failed.status.error.message, recovery: 'inspectResult' }],
      nextActions: [step('viewChanges', 'Inspect the retained worktree paths and partial implementation notes.'), step('inspectResult', 'Inspect execution logs and retained evidence.'), step('rerun', 'Retry as a new run after inspecting the partial changes.')],
      rawOutput: 'SAMPLE STDERR: agent process exited with code 17 during implementation. No real process was started.',
    });
    const cancelled = makeRun('demo-v2-cancelled-review', 'cancelled', 60, { type: 'pr', number: 41286 }, 'pr-review', 'Cancelled review with remaining work', 'The sample review was cancelled after context collection.');
    cancelled.result = v2Result({
      outcome: 'cancelled', phase: 'analysis', cliExitCode: null,
      summary: 'Sample: the local review was cancelled after context collection. Code review was not run, and no verification result was produced.',
      artifacts: [{ label: 'Collected context (sample)', path: 'C:\\Demo\\Pulse\\runs\\demo-v2-cancelled-review\\context.md' }],
      validation: [check('context', 'Target and local repository context', 'passed', 'Collected the sample target and repository instructions.', [`Sample target SHA: ${sha}.`]), check('local-review', 'Local code review', 'not_run', 'Cancellation occurred before the code review started.')],
      diagnostics: [{ code: 'TASK_CANCELLED', severity: 'warning', message: 'The sample task was cancelled before its required review work finished.', recovery: 'rerun' }],
      nextActions: [step('inspectResult', 'Inspect the collected context; the verification check is missing.'), step('rerun', 'Start a new run when you are ready to complete the review.')],
    });
    const interrupted = makeRun('demo-v2-interrupted-review', 'interrupted', 65, { type: 'pr', number: 41287 }, 'pr-review', 'Interrupted review before verification', 'The sample Host stopped before verification could complete.');
    interrupted.status.error = { code: 'TASK_INTERRUPTED', message: 'The sample Host stopped while verification was pending.', guidance: 'Inspect retained findings and logs, then retry as a new run.' };
    interrupted.result = v2Result({
      outcome: 'interrupted', phase: 'validation', cliExitCode: null,
      summary: 'Sample: local inspection found a possible path-comparison issue, but interruption prevented the required verification. No review can be published from this run.',
      findings: [{ id: 'unverified-path-case', title: 'Path comparison needs verification', severity: 'medium', status: 'unverified', path: suggestion.path, line: suggestion.line, details: 'A possible case-sensitive comparison was found during inspection. Required verification remains incomplete.', evidence: ['Sample local inspection identified DistinctBy without an explicit comparer.'] }],
      artifacts: [{ label: 'Interrupted review notes (sample)', path: 'C:\\Demo\\Pulse\\runs\\demo-v2-interrupted-review\\review-partial.md' }],
      validation: [check('context', 'Target and local repository context', 'passed', 'Inspected the sample target and repository instructions.', [`Sample target SHA: ${sha}.`]), check('local-review', 'Local code review', 'passed', 'Inspected the changed sample call paths and retained an unverified finding.', ['Sample notes retain the inspected path and line.']), check('verification', 'Applicable focused verification', 'not_run', 'The Host stopped before verification could begin.')],
      diagnostics: [{ code: 'TASK_INTERRUPTED', severity: 'error', message: interrupted.status.error.message, recovery: 'rerun' }],
      nextActions: [step('inspectResult', 'Inspect the incomplete review notes and execution logs.'), step('openTarget', 'Open the immutable GitHub target to inspect its current state.'), step('rerun', 'Retry as a new run to complete review and verification.')],
    });
    const prActions = makeRun('demo-v2-proposed-pr-actions', 'succeeded', 12, { type: 'pr', number: 41288 }, 'pr-review', 'Completed review with merge and CI proposals', 'Local review is complete. Each proposed GitHub operation requires its own confirmation.');
    prActions.result = v2Result({
      outcome: 'completed', phase: 'reporting', needsReview: false,
      assessment: { subject: 'original-pr', status: 'passed', summary: 'The reviewed original PR passed the applicable sample review checks.', revisionSha: sha },
      summary: 'Sample: local review and focused verification completed without open findings. Inspect the merge or CI proposal before confirming it.',
      validation: [check('context', 'Target and local repository context', 'passed', 'Inspected the immutable sample target.', [`Sample PR #41288 reviewed at ${sha}.`]), check('local-review', 'Local code review', 'passed', 'Reviewed the sample cleanup change.', ['No open findings remain in this sample.']), check('verification', 'Applicable focused verification', 'passed', 'Completed the applicable sample checks without claiming a desktop session or hardware verification.', ['Sample focused checks passed.'])],
      review: { headSha: sha, body: 'Sample local review completed without open findings.', suggestions: [] },
      nextActions: [step('merge-pr', 'Inspect the current target, required checks, account, and reviewed SHA before squash merging.'), step('trigger-ci', 'Inspect the fixed CI command and target SHA before requesting a new CI run.', '/azp run')],
    });
    const create = makeRun('demo-v2-proposed-create-pr', 'succeeded', 32, { type: 'issue', number: 41289 }, 'issue-fix', 'Completed local fix with an Open PR proposal', 'The sample local fix is complete. Inspect the full pull request draft before confirming publication.');
    create.view = { read: false, handled: false };
    create.result = v2Result({
      outcome: 'completed', phase: 'reporting',
      assessment: { subject: 'local-candidate', status: 'passed', summary: 'The local candidate passed focused verification. The upstream issue remains open.', revisionSha: 'd'.repeat(40) },
      summary: 'Sample: the focused fix and applicable verification are complete. A draft pull request is proposed for human confirmation.',
      artifacts: [{ label: 'Fix and verification notes (sample)', path: 'C:\\Demo\\Pulse\\runs\\demo-v2-proposed-create-pr\\result.md' }],
      validation: [check('reproduction', 'Reproduce the reported behavior', 'passed', 'Captured the sample keyboard focus regression.', ['Sample reproduction notes retain the keyboard sequence.']), check('implementation', 'Complete the focused fix', 'passed', 'Prepared a focused sample fix on the proposed head branch.', ['Sample branch: pulse-demo:fix/settings-focus.']), check('verification', 'Verify the completed fix', 'passed', 'Completed the applicable focused sample checks.', ['Sample focus restoration checks passed; no hardware verification is claimed.'])],
      nextActions: [{ ...step('create-pr', 'Inspect the existing remote branches, title, complete description, and draft status. This action does not commit or push changes.'), pullRequest: { head: 'pulse-demo:fix/settings-focus', sourceHeadSha: 'd'.repeat(40), base: 'main', title: 'Restore keyboard focus after closing a Settings dialog', body: '    PreserveKeyboardFocus();\n\nRestore focus to the original setting after the dialog closes.  \n\nValidation: sample focused checks only.\n\n  ', draft: true } }],
    });
    const stale = makeRun('demo-v2-stale-pr-actions', 'succeeded', 34, { type: 'pr', number: 41290 }, 'pr-review', 'Review proposals blocked by a changed PR head', 'The reviewed SHA is retained, but the current sample PR head has changed.');
    stale.result = copy(prActions.result);
    stale.result.summary = 'Sample: the local review completed, but the current PR head changed afterward. Merge and CI proposals must remain blocked until a new review is completed.';
    stale.result.validation[0].evidence = [`Sample PR #41290 reviewed at ${sha}; the current target now has a different head.`];
    const negative = makeRun('demo-v2-negative-e2e', 'succeeded', 14, { type: 'pr', number: 41291 }, 'e2e', 'E2E completed with a confirmed regression', 'The E2E procedure finished and produced a negative product result.');
    negative.result = v2Result({
      outcome: 'completed', phase: 'reporting', assessment: { subject: 'original-pr', status: 'failed', summary: 'The complete E2E procedure reproduced a keyboard-focus regression.', revisionSha: sha },
      summary: 'Sample: E2E execution completed and found a regression. Publish the failure report only after confirmation; approval and merge remain unavailable.',
      findings: [{ id: 'e2e-focus-regression', title: 'Keyboard focus is lost after closing Settings', severity: 'medium', status: 'open', path: '', line: null, details: 'The sample E2E procedure reproduced focus moving to the page body.', evidence: ['Sample E2E trace captured the active element before and after closing the dialog.'] }],
      validation: [check('setup', 'Prepare the E2E environment', 'passed', 'Prepared the controlled sample test environment.', ['Sample driver ready.']), check('e2e', 'Execute and interpret the E2E procedure', 'passed', 'Completed the procedure and retained the negative product evidence.', ['All sample steps executed.']), { ...check('product-focus', 'Product keyboard-focus behavior', 'failed', 'Focus moved to the page body after the dialog closed.', ['Sample expected: original control. Observed: page body.']), required: false }],
      nextActions: [step('comment', 'Review and publish the clearly labelled negative test report.', '### E2E report: regression found\n\nThe full sample procedure completed, but keyboard focus was lost after closing Settings.\n\nThis is a failure report, not a passing validation.'), step('merge-pr', 'This proposal is disabled because the E2E result found a regression.'), step('rerun', 'Repeat the E2E procedure after the regression is fixed.')],
    });
    const unpublished = makeRun('demo-v2-local-unpublished-fix', 'succeeded', 36, { type: 'issue', number: 41292 }, 'issue-fix', 'Verified local fix awaiting branch publication', 'The fix is retained locally. The proposed remote source branch does not exist.');
    unpublished.result = copy(create.result);
    unpublished.result.summary = 'Sample: the local candidate passed focused checks, but its branch has not been published. Preserve the changes and publish the verified commit before preparing a pull request.';
    unpublished.result.nextActions[0].pullRequest.head = 'pulse-demo:fix/unpublished-focus';
    unpublished.result.nextActions[0].reason = 'The verified commit must exist on the named remote source branch before creating a pull request. No automatic commit or push is performed.';
    unpublished.result.nextActions.unshift(step('viewChanges', 'Inspect the retained local fix and verified commit before publishing the source branch.'));
    const sourceChanged = makeRun('demo-v2-create-pr-source-changed', 'succeeded', 38, { type: 'issue', number: 41293 }, 'issue-fix', 'Create PR blocked by changed source commit', 'The remote source branch changed after local verification and before the first preview.');
    sourceChanged.result = copy(create.result);
    sourceChanged.result.summary = 'Sample: the candidate was verified at the retained SHA, but the remote source branch now points to a different commit. A pull request must not be created from unverified code.';
    sourceChanged.result.nextActions[0].pullRequest.head = 'pulse-demo:fix/changed-focus';
    const ciRetry = makeRun('demo-v2-ci-retry', 'succeeded', 16, { type: 'pr', number: 41294 }, 'pr-review', 'CI failed after a previously confirmed trigger', 'The previously triggered CI run failed. An explicit new attempt is available after checking current CI.');
    ciRetry.result = copy(prActions.result);
    ciRetry.result.summary = 'Sample: the review passed, but a CI run triggered afterward failed. Inspect the earlier action history and explicitly confirm a new CI attempt.';
    ciRetry.result.nextActions = [step('trigger-ci', 'Explicitly request a new CI attempt after confirming that the current-head CI has failed.', '/azp run')];
    const limitedReview = makeRun('demo-v2-review-limited-coverage', 'succeeded', 70, { type: 'pr', number: 101 }, 'pr-review', 'Review complete with CJK acceptance outside its scope', 'The sample code review is complete. CJK runtime acceptance remains unverified.');
    limitedReview.task.repository = 'example/pulse-review-samples';
    limitedReview.task.prompt = '[Sample task] Review the source changes in example/pulse-review-samples PR #101. Complete the applicable local review checks and record runtime coverage limits. Full CJK acceptance was not requested. No real CLI was started.';
    limitedReview.result = v2Result({
      outcome: 'completed', phase: 'reporting',
      assessment: { subject: 'original-pr', status: 'inconclusive', summary: 'Sample static review and focused checks found no confirmed defect, but cannot establish that the reported CJK startup crash is fixed.', revisionSha: sha },
      summary: 'Sample: code review and applicable checks completed without a confirmed actionable finding. CJK startup acceptance was outside this review scope and remains unverified.',
      validation: [
        check('context', 'Target and local repository context', 'passed', 'Inspected the immutable sample target and its source-review scope.', [`Sample PR #101 reviewed at ${sha}; full CJK acceptance was not requested.`]),
        check('local-review', 'Local code review', 'passed', 'Inspected initialization and title-assignment paths without identifying a confirmed actionable code defect.', ['Sample source-review notes retain the inspected paths and conclusions.']),
        check('verification', 'Applicable review verification', 'passed', 'Completed the focused source-level checks needed to support this review. These checks do not claim full runtime crash acceptance.', ['Sample focused initialization checks passed.']),
        { ...check('cjk-acceptance', 'CJK and elevation startup acceptance', 'not_run', 'The sample review environment has only English resources. The CJK and elevation matrix was not requested as part of this code review.'), required: false },
      ],
      diagnostics: [{ code: 'REVIEW_COVERAGE_LIMITED', severity: 'warning', message: 'Sample review coverage is limited: CJK and elevation startup acceptance was not run. A completed code review does not establish that the reported crash is fixed.', recovery: 'inspectResult' }],
      review: { headSha: sha, body: '### Sample review: complete with runtime limits\n\nNo confirmed actionable code defects were found in the inspected source. Focused review checks passed.\n\nCJK and elevation startup acceptance was not run; the reported crash fix remains unverified.', suggestions: [] },
      nextActions: [step('comment', 'Inspect the review conclusion and its explicit CJK runtime limits before publishing.', '### Sample review: complete with runtime limits\n\nNo confirmed actionable code defects were found. Focused source-review checks passed.\n\nCJK and elevation startup acceptance was not run. This report does not confirm that the reported crash is fixed.'), step('approve', 'Approval remains unavailable while the original PR assessment is inconclusive.'), step('merge-pr', 'Merge remains unavailable while the original PR assessment is inconclusive.'), step('inspectResult', 'Inspect the review evidence and the runtime coverage that remains unverified.')],
    });
    const requiredAcceptance = makeRun('demo-v2-review-required-acceptance', 'succeeded', 75, { type: 'pr', number: 102 }, 'pr-review', 'Explicit CJK acceptance blocked by its test environment', 'The user-requested CJK acceptance could not run in the sample environment.');
    requiredAcceptance.task.repository = 'example/pulse-review-samples';
    requiredAcceptance.task.prompt = '[Sample task] Review example/pulse-review-samples PR #102 and complete the CJK and elevation startup acceptance matrix. This acceptance is explicitly required before the requested review workflow is complete. No real CLI was started.';
    requiredAcceptance.result = v2Result({
      outcome: 'blocked', phase: 'validation',
      assessment: { subject: 'original-pr', status: 'inconclusive', summary: 'Sample source inspection cannot establish crash acceptance because the explicitly requested CJK startup matrix did not run.', revisionSha: sha },
      summary: 'Sample: source inspection completed, but the explicitly requested CJK and elevation startup acceptance could not run. The requested review workflow remains blocked.',
      validation: [
        check('context', 'Target and local repository context', 'passed', 'Inspected the immutable sample target and the explicit CJK acceptance requirement.', [`Sample PR #102 reviewed at ${sha}; the user requested the CJK and elevation matrix.`]),
        check('local-review', 'Local code review', 'passed', 'Inspected the sample initialization paths without a confirmed actionable code defect.', ['Sample source-review notes retain the inspected paths.']),
        check('verification', 'Required CJK and elevation startup acceptance', 'not_run', 'The explicitly requested CJK acceptance requires resources and runtime settings absent from the sample environment.'),
      ],
      diagnostics: [
        { code: 'CJK_ACCEPTANCE_UNAVAILABLE', severity: 'error', message: 'Sample required crash acceptance remains incomplete: this environment only supplies English resources. The explicitly requested CJK and elevation startup matrix was not run.', recovery: 'configure' },
        { code: 'WORKFLOW_CHECKS_INCOMPLETE', severity: 'error', message: 'One or more required workflow checks failed, were not completed, or lack supporting evidence. Inspect validation details and remaining checks.', recovery: 'inspectResult' },
      ],
      nextActions: [step('configure', 'Inspect the CJK resources and runtime environment required for the requested acceptance.'), step('inspectResult', 'Inspect the specific environment blocker and incomplete required validation.'), step('rerun', 'Run the requested review again after the CJK acceptance environment is available.')],
    });
    const legacyCoverage = makeRun('demo-v2-review-legacy-coverage-blocked', 'succeeded', 80, { type: 'pr', number: 103 }, 'pr-review', 'Historical review with incomplete product acceptance', 'The retained sample review completed source inspection but classified missing CJK acceptance as a workflow blocker.');
    legacyCoverage.task.repository = 'example/pulse-review-samples';
    legacyCoverage.task.prompt = '[Sample historical task] Review the source changes in example/pulse-review-samples PR #103. This saved sample used an older prompt that treated full product acceptance as required. No real CLI was started.';
    legacyCoverage.result = copy(requiredAcceptance.result);
    legacyCoverage.result.summary = 'Sample: the historical model reported blocked because the CJK acceptance matrix was not available. Source review is complete, but product acceptance remains unverified. The saved result is retained unchanged.';
    legacyCoverage.result.assessment.summary = 'Sample source review is complete, but the missing CJK product acceptance leaves the original PR assessment inconclusive.';
    legacyCoverage.result.validation[0].details = 'Inspected the immutable sample target under the historical review prompt.';
    legacyCoverage.result.validation[0].evidence = [`Sample PR #103 reviewed at ${sha}.`];
    legacyCoverage.result.validation.find(item => item.id === 'verification').details = 'The historical prompt classified the unavailable CJK product acceptance matrix as a required workflow check.';
    legacyCoverage.result.diagnostics[0].message = 'Sample CJK product acceptance remains incomplete because the environment only supplies English resources. The historical report classified this unverified product behavior as a workflow blocker.';
    legacyCoverage.result.blockers = legacyCoverage.result.diagnostics.filter(item => item.severity === 'error').map(item => item.message);
    legacyCoverage.result.validation.push({ ...check('initial-build-attempt', 'Initial sample build observation', 'failed', 'An initial sample build attempt lacked a local dependency; a later focused sample check passed. This historical observation is retained without claiming CJK acceptance.', ['Sample initial build attempt reported a missing dependency.']), required: false });
    legacyCoverage.result.review = { headSha: sha, body: 'Sample historical source review found no confirmed actionable defect. CJK and elevation startup acceptance did not run, so the reported crash fix remains unverified.', suggestions: [] };
    return [completed, blocked, invalid, failed, cancelled, interrupted, prActions, create, stale, negative, unpublished, sourceChanged, ciRetry, limitedReview, requiredAcceptance, legacyCoverage];
  }
  function scopedReviewSamples() {
    const repository = 'example/pulse-review-samples';
    const provenance = (subject, revisionSha, run) => ({ version: 1, source: 'host', observation: 'boundary-snapshots', expectedHeadSha: sha, subject, start: { capturedAt: run.status.startedAt, headSha: revisionSha, workingTree: subject === 'local-candidate' ? 'modified' : 'clean', diffHash: 'a'.repeat(64) }, end: { capturedAt: run.status.endedAt, headSha: revisionSha, workingTree: subject === 'local-candidate' ? 'modified' : 'clean', diffHash: 'a'.repeat(64) } });
    const evidence = (id, kind, status, source = 'current-run', subject = 'original-pr', revisionSha = sha) => ({ id, kind, status, source, subject, revisionSha, summary: `Sample ${kind}: ${status}; ${subject} at the explicitly recorded revision.`, evidence: [`Sample ${kind} observation retained for ${revisionSha}.`], runId: null });
    const parents = [
      ['demo-scope-static', 'static', 110, 55, 'no-blocking-findings', 'missing-prerequisites'],
      ['demo-scope-build-tests', 'build-tests', 111, 56, 'inconclusive', 'unknown'],
      ['demo-scope-ui-e2e', 'ui-e2e', 112, 57, 'changes-requested', 'ready'],
    ].map(([runId, mode, number, age, conclusion, readiness]) => {
      const run = makeRun(runId, 'succeeded', age, { type: 'pr', number }, 'pr-review', `Review scope: ${mode}`, `The selected ${mode} review scope completed. Supplemental verification is a separate action.`);
      run.task.repository = repository; run.task.reviewOptions = { mode };
      run.task.prompt = `[Sample task] Review ${repository} PR #${number} only within the explicitly selected ${mode} scope. No real process was started.`;
      run.provenance = provenance('original-pr', sha, run);
      run.result = v2Result({ outcome: 'completed', phase: 'reporting',
        summary: `Sample: ${mode} review completed. Code-review conclusion: ${conclusion}. Verification evidence retains its source and revision.`,
        assessment: { subject: 'original-pr', status: mode === 'ui-e2e' ? 'failed' : 'inconclusive', summary: mode === 'ui-e2e' ? 'The selected sample runtime scenario demonstrated a focus regression.' : 'The selected review scope does not establish the unanswered runtime behavior.', revisionSha: sha },
        reviewConclusion: { status: conclusion, summary: mode === 'static' ? 'No blocking code finding was identified in the static review; runtime behavior was not exercised.' : mode === 'build-tests' ? 'Builds and focused automated tests passed, but one runtime question prevents a firm code-review conclusion.' : 'The selected runtime scenario exposed a product regression that requires changes.', revisionSha: sha, blockingUncertainties: mode === 'build-tests' ? ['Does keyboard focus return correctly after the first launch under elevation?'] : [] },
        validation: workflowChecks(run.task).map(id => ({ id, name: id === 'local-review' ? 'Local code review' : id, status: 'passed', required: true, details: `Completed and interpreted the selected sample ${id} work.`, evidence: [`Sample ${id} work completed within ${mode} scope.`] })),
        verificationEvidence: mode === 'static' ? [evidence('same-sha-ci-build', 'build', 'passed', 'ci'), evidence('author-runtime-note', 'runtime', 'not_run', 'author')] : mode === 'build-tests' ? [evidence('local-build', 'build', 'passed'), evidence('focused-tests', 'automated-tests', 'passed')] : [evidence('keyboard-focus-runtime', 'runtime', 'failed')],
        verificationRecommendation: { mode: 'ui-e2e', reason: 'A focused runtime observation would answer the remaining behavior question.', question: 'Does keyboard focus return to the original setting after closing the first dialog under elevation?', scenarios: ['At the saved original PR SHA, launch the sample app under elevation and open the first Settings dialog.', 'Close the dialog and compare the focused control with the expected original setting.'], prerequisites: ['An isolated interactive Windows desktop.', 'The sample app and an available UI automation driver at the original PR SHA.'], evidence: [readiness === 'missing-prerequisites' ? 'The sample environment does not currently contain an interactive desktop driver.' : readiness === 'unknown' ? 'The parent run did not establish whether the interactive desktop driver is available.' : 'The sample runtime driver was available during the selected review.'], readiness },
        review: { headSha: sha, body: `Sample ${mode} code-review conclusion: ${conclusion}.`, suggestions: [] },
      });
      if (mode === 'ui-e2e') run.result.findings = [{ id: 'runtime-focus-regression', title: 'Focus leaves the original setting', severity: 'medium', status: 'open', path: '', line: null, details: 'The selected sample runtime scenario moved focus to the page body after the first dialog closed.', evidence: ['Sample expected focus: original setting. Observed: page body.'] }];
      return run;
    });
    const child = (runId, parent, age, subject = 'original-pr', revisionSha = sha, outcome = 'completed', observation = 'passed') => {
      const run = makeRun(runId, 'succeeded', age, copy(parent.task.target), 'pr-verify', 'Supplemental verification: first-dialog keyboard focus', 'The sample supplemental verification is separate from the original code review.');
      run.task.repository = repository; run.task.expectedHeadSha = subject === 'local-candidate' ? sha : revisionSha; run.task.reviewOptions = { mode: 'ui-e2e' };
      run.task.followUp = { parentRunId: parent.runId, recommendationId: '', parentResultFingerprint: '', subject: 'original-pr', revisionSha: run.task.expectedHeadSha };
      run.task.prompt = '[Sample task] Execute only the saved focused verification scenarios. Do not repeat the full parent code review. No real process was started.';
      run.provenance = provenance(subject, revisionSha, run);
      run.result = v2Result({ outcome, phase: outcome === 'blocked' ? 'setup' : 'reporting', summary: `Sample: supplemental verification ${outcome}; ${subject} evidence is retained separately from the parent review.`,
        assessment: { subject, status: outcome === 'blocked' ? 'inconclusive' : observation, summary: outcome === 'blocked' ? 'The sample verification driver is still unavailable.' : `The focused sample runtime observation ${observation}.`, revisionSha }, reviewConclusion: null,
        verificationEvidence: [evidence('supplemental-focus', 'runtime', outcome === 'blocked' ? 'not_run' : observation, 'current-run', subject, revisionSha)], verificationRecommendation: null,
        validation: workflowChecks(run.task).map(id => ({ id, name: id === 'setup' ? 'Check verification prerequisites' : 'Execute focused runtime scenarios', status: outcome === 'blocked' ? 'not_run' : 'passed', required: true, details: outcome === 'blocked' ? 'The sample interactive desktop driver remains unavailable.' : 'Executed and interpreted the focused sample scenario.', evidence: outcome === 'blocked' ? [] : ['Sample verification steps and expected/observed focus were retained.'] })),
        diagnostics: outcome === 'blocked' ? [{ code: 'VERIFICATION_PREREQUISITES_MISSING', severity: 'error', message: 'The sample UI automation driver is unavailable; repeating the same request does not establish readiness.', recovery: 'configure' }] : [],
      });
      return run;
    };
    return [...parents,
      child('10101010-1010-4010-8010-101010101010', parents[0], 44),
      child('20202020-2020-4020-8020-202020202020', parents[0], 45, 'local-candidate', 'd'.repeat(40)),
      child('30303030-3030-4030-8030-303030303030', parents[0], 46, 'original-pr', 'e'.repeat(40)),
      child('40404040-4040-4040-8040-404040404040', parents[1], 47, 'original-pr', sha, 'blocked'),
      child('50505050-5050-4050-8050-505050505050', parents[2], 49, 'original-pr', sha, 'completed', 'failed'),
    ];
  }
  function webActionSamples() {
    const target = { repository: 'microsoft/PowerToys', type: 'pr', number: 41276 };
    const sample = (digit, kind, overrides = {}) => {
      const operationId = `${digit.repeat(8)}-${digit.repeat(4)}-4${digit.repeat(3)}-8${digit.repeat(3)}-${digit.repeat(12)}`;
      const draft = { requestId: operationId, actionId: `Sample ${kind}`, kind, target: copy(target), expectedHeadSha: sha, body: 'This is sample content. Confirming creates a record only in this browser session.', ...overrides };
      return {
        operationId, requestId: draft.requestId, actionId: draft.actionId, kind, target: draft.target, draft,
        status: 'prepared', completedSteps: [], remainingSteps: [], urls: [], createdAt: at(2), updatedAt: at(2),
        account: 'pulse-demo', canSubmit: true, blockers: [], targetUrl: `https://github.com/${draft.target.repository}/${draft.target.type === 'pr' ? 'pull' : 'issues'}/${draft.target.number}`,
        state: 'OPEN', headSha: sha, draftPr: false, ciState: 'success', sourceOrigin: 'https://cautious-memory-r38ze9j.pages.github.io',
      };
    };
    const review = sample('1', 'review', { review: { event: 'REQUEST_CHANGES', comments: [{ path: suggestion.path, line: suggestion.line, startLine: suggestion.startLine, side: suggestion.side, body: `${suggestion.body}\n\n\`\`\`suggestion\n${suggestion.replacement}\n\`\`\`` }], generalComments: [{ body: 'Please keep the regression coverage focused on the Windows path comparison behavior.' }] } });
    const merge = sample('2', 'merge-pr'); delete merge.draft.body;
    const comment = sample('3', 'comment', { target: { repository: 'muyuanms/powertoys', type: 'issue', number: 12 }, expectedHeadSha: undefined, body: 'I will work on the keyboard focus issue.\n\nThis is a UI sample and is not sent to GitHub.', assignSelf: true, assignmentTarget: { repository: 'microsoft/PowerToys', type: 'issue', number: 41192 } });
    delete comment.headSha; delete comment.draftPr; delete comment.ciState;
    const create = sample('4', 'create-pr', { target: { repository: 'microsoft/PowerToys', type: 'issue', number: 41192 }, expectedHeadSha: undefined, body: undefined, pullRequest: { head: 'pulse-demo:fix/settings-focus', base: 'main', title: 'Restore keyboard focus after closing a Settings dialog', body: 'Preserve keyboard focus when a settings dialog closes.\n\nValidation: UI fixture only.', draft: true } });
    delete create.headSha; delete create.draftPr; delete create.ciState;
    const unknown = sample('5', 'comment'); unknown.status = 'unknown'; unknown.canSubmit = false; unknown.remainingSteps = ['comment']; unknown.urls = [unknown.targetUrl]; unknown.error = { code: 'REMOTE_OUTCOME_UNKNOWN', message: 'Sample: the response was lost after GitHub accepted a request.', guidance: 'Inspect GitHub before preparing another action.' };
    const blocked = sample('6', 'merge-pr'); delete blocked.draft.body; blocked.canSubmit = false; blocked.headSha = 'e'.repeat(40); blocked.ciState = 'failure'; blocked.blockers = [{ code: 'STALE_HEAD', message: 'The pull request head changed after this action was prepared.', guidance: 'Review the latest commit and prepare a new action.' }, { code: 'CI_NOT_PASSING', message: 'Required checks are not passing.' }];
    const approve = sample('7', 'approve');
    const ci = sample('8', 'trigger-ci', { body: '/azp run' });
    const recoverable = sample('9', 'comment'); recoverable.status = 'unknown'; recoverable.canSubmit = false; recoverable.remainingSteps = ['comment']; recoverable.error = copy(unknown.error);
    const partlyRecoverable = sample('a', 'review', { review: copy(review.draft.review) }); partlyRecoverable.status = 'unknown'; partlyRecoverable.canSubmit = false; partlyRecoverable.remainingSteps = ['review', 'general-comment-1']; partlyRecoverable.error = copy(unknown.error);
    return Object.fromEntries([review, merge, comment, create, unknown, blocked, approve, ci, recoverable, partlyRecoverable].map(value => [value.operationId, value]));
  }
  function seed() {
    const running = makeRun('demo-running-review', 'running', 8, { type: 'pr', number: 41280 }, 'pr-review', 'Review FancyZones window dragging', 'Checking cross-monitor dragging and layout switching. Read 6 of 9 changed files.');
    running.task.execution = { agent: 'codex', model: 'gpt-5.3-codex', reasoningEffort: 'high' };
    Object.assign(running.config, resolveExecution({ agent: 'codex', agentDefaults }, running.task.execution));
    running.status.observedExecution = { model: 'gpt-6-astra', reasoningEffort: 'ultra', source: 'codex-turn-context', observedAt: at(0) };
    const quickFailure = makeRun('demo-quick-failure', 'failed', 1, { type: 'pr', number: 41281 }, 'pr-review', 'Inspect a task that stopped after 22 seconds', 'The sample CLI exited before completing its response.');
    quickFailure.task.execution = { model: '', reasoningEffort: '' };
    quickFailure.status.startedAt = at(1); quickFailure.status.endedAt = quickFailure.status.updatedAt = at(38 / 60);
    quickFailure.status.error = { code: 'CLI_EXECUTION_FAILED', message: 'The sample CLI exited after 22 seconds without a complete result.', guidance: 'Open the execution logs to inspect the CLI error before starting another run.' };
    const historical = makeRun('demo-historical-failure', 'failed', 180, { type: 'pr', number: 41260 }, 'pr-review', 'Historical task with missing execution metadata', 'The historical sample run has already ended.');
    historical.config.model = null; historical.config.reasoningEffort = null; delete historical.config.executionSource;
    historical.status.exitCode = null;
    historical.status.error = { code: null, message: 'The sample CLI stopped before completing this task.', guidance: 'Inspect the captured logs before starting another run.' };
    historical.result = { summary: historical.status.error.message, artifacts: [], validation: [], blockers: [historical.status.error.message], nextSteps: [{ kind: 'inspectResult', reason: 'Check existing artifacts and incomplete validation before deciding whether to start a new run.', body: '' }], needsReview: true, structured: false, rawOutput: '' };
    const pending = makeRun('demo-pending-review', 'succeeded', 24, { type: 'pr', number: 41276 }, 'pr-review', 'Review duplicate Run search results', 'Review complete. Inline suggestions are ready to inspect.');
    pending.status.observedExecution = { model: 'gpt-6-astra', reasoningEffort: 'ultra', source: 'codex-turn-context', observedAt: pending.status.endedAt };
    pending.result = {
      summary: 'Found one issue: case-sensitive program paths can produce duplicate search results on Windows. An editable inline suggestion is ready.',
      artifacts: [{ label: 'Review summary (sample)', path: 'C:\\Demo\\Pulse\\runs\\demo-pending-review\\review.md' }],
      validation: [
        { name: 'Changes and call paths', status: 'Passed', details: 'Reviewed program discovery, path normalization, and deduplication.' },
        { name: 'Suggestion location', status: 'Passed', details: 'The suggestion matches RIGHT lines 147–148 in the sample PR diff.' },
        { name: 'Build', status: 'Not run', details: 'PowerToys builds are reserved for final acceptance validation.' },
      ],
      blockers: ['The same program may appear twice when path casing differs.'],
      nextSteps: [{ kind: 'suggestChanges', reason: 'Review the comment and replacement code, then select the inline suggestion.', body: 'Use a case-insensitive comparer when deduplicating program paths to match Windows file path semantics.\n\nThis is a UI sample. Submit only creates a simulated local record.' }],
      review: { headSha: sha, body: 'Use a case-insensitive comparer when deduplicating program paths.', suggestions: [copy(suggestion)] },
    };
    const actionable = makeRun('demo-actionable-review', 'succeeded', 15, { type: 'pr', number: 41278 }, 'pr-review', 'Review a verified cleanup change', 'Review finished. The selected GitHub follow-up is ready to inspect.');
    actionable.status.observedExecution = { model: 'gpt-6-astra', reasoningEffort: 'ultra', source: 'codex-turn-context', observedAt: actionable.status.endedAt };
    actionable.result = {
      summary: 'The change has been reviewed. A verified inline cleanup suggestion and a findings comment are available to inspect before submission.',
      artifacts: [], validation: [{ name: 'Review', status: 'passed', details: 'Reviewed the target SHA and validated the suggestion coordinates in this sample.' }], blockers: [], structured: true, needsReview: true,
      review: { headSha: sha, body: 'Verified findings for the reviewed change.', suggestions: [copy(suggestion)] },
      nextSteps: [{ kind: 'suggestChanges', reason: 'Inspect this verified inline cleanup suggestion.', body: 'Verified suggestion for the reviewed change.' }, { kind: 'comment', reason: 'Post the verified findings after inspecting the draft.', body: 'Verified findings for the reviewed change.' }],
    };
    const fixed = makeRun('demo-completed-fix', 'succeeded', 95, { type: 'issue', number: 41192 }, 'issue-fix', 'Restore keyboard focus in Settings', 'Fix complete. The result is marked handled.');
    fixed.config.permission = 'workspace-write';
    fixed.status.observedExecution = { model: 'gpt-6-astra', source: 'codex-turn-context', observedAt: fixed.status.endedAt };
    fixed.result = {
      summary: 'Focus returns to the original setting after its dialog closes, so keyboard users can continue navigating. This sample result is marked handled.',
      artifacts: [{ label: 'Fix notes (sample)', path: 'C:\\Demo\\Pulse\\runs\\demo-completed-fix\\result.md' }],
      validation: [{ name: 'Code inspection', status: 'Passed', details: 'Focus is restored only when the original control still exists and is visible.' }, { name: 'Build', status: 'Not run' }],
      blockers: [], nextSteps: [],
    };
    const failed = makeRun('demo-failed-e2e', 'failed', 48, { type: 'issue', number: 41213 }, 'e2e', 'Check shortcut conflict regression', 'The sample environment is missing a desktop test dependency.');
    failed.config.agent = 'copilot'; failed.config.cliPath = 'C:\\Demo\\Tools\\copilot.exe';
    failed.status.error = { code: 'DEMO_TEST_DEPENDENCY', message: 'Cannot start desktop automation: the test driver is missing.', guidance: 'This is a sample failure. You can rerun the preview task.' };
    failed.result = {
      summary: 'Shortcut conflict analysis finished, but desktop validation stopped because the sample environment lacks a test driver.',
      artifacts: [], validation: [{ name: 'Shortcut configuration', status: 'Passed' }, { name: 'Desktop automation', status: 'Incomplete', details: 'The test driver is unavailable.' }],
      blockers: ['Install the test dependencies, then rerun desktop validation.'], nextSteps: [],
    };
    const runs = [quickFailure, running, actionable, pending, failed, fixed, historical, ...structuredResultSamples(), ...scopedReviewSamples(), ...workflowV3Samples()];
    for (const run of runs) {
      if (!run.result) continue;
      const proposals = [2, 3].includes(run.result.schemaVersion) ? run.result.nextActions : run.result.nextSteps;
      proposals.forEach((proposal, index) => { proposal.proposalId = `preview-${run.runId}-proposal-${index + 1}`; });
      if (run.result.schemaVersion === 2) run.result.nextSteps = copy(proposals.filter(proposal => run.result.outcome === 'completed' || !publishingActions.has(proposal.kind)));
      if (run.result.schemaVersion === 3) run.result.nextSteps = copy(proposals);
    }
    const webActions = webActionSamples();
    const resultActions = {};
    const unknownRun = runs.find(run => run.runId === 'demo-v3-pr-unknown-operation');
    const unknownAction = copy(webActions['55555555-5555-4555-8555-555555555555']);
    Object.assign(unknownAction, { operationId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee', requestId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee', runId: unknownRun.runId, sourceOrigin: `pulse-task://${unknownRun.runId}` });
    unknownAction.target = { ...copy(unknownRun.task.target), repository: unknownRun.task.repository };
    Object.assign(unknownAction.draft, { requestId: unknownAction.requestId, target: copy(unknownAction.target), expectedHeadSha: sha });
    unknownAction.targetUrl = `https://github.com/${unknownRun.task.repository}/pull/${unknownRun.task.target.number}`; unknownAction.urls = [unknownAction.targetUrl];
    webActions[unknownAction.operationId] = unknownAction;
    resultActions[unknownAction.operationId] = { runId: unknownRun.runId, proposalId: 'sample-unconfirmed-comment', attemptId: unknownAction.operationId, operationId: unknownAction.operationId };
    const ciRun = runs.find(run => run.runId === 'demo-v2-ci-retry');
    const ciProposal = ciRun.result.nextActions[0];
    const previousCi = copy(webActions['88888888-8888-4888-8888-888888888888']);
    Object.assign(previousCi, { operationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', requestId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', attemptId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', runId: ciRun.runId, proposalId: ciProposal.proposalId, status: 'succeeded', canSubmit: false, ciBaseline: 'sample-ci-before-request', completedSteps: ['Sample CI request previously confirmed'], createdAt: at(8), updatedAt: at(8), sourceOrigin: `pulse-task://${ciRun.runId}` });
    previousCi.target = { ...copy(ciRun.task.target), repository: ciRun.task.repository };
    previousCi.draft.target = copy(previousCi.target); previousCi.draft.requestId = previousCi.requestId;
    previousCi.targetUrl = `https://github.com/${ciRun.task.repository}/pull/${ciRun.task.target.number}`; previousCi.urls = [previousCi.targetUrl];
    webActions[previousCi.operationId] = previousCi;
    resultActions[previousCi.operationId] = { runId: ciRun.runId, proposalId: ciProposal.proposalId, attemptId: previousCi.attemptId, operationId: previousCi.operationId };
    for (const [operationId, runId, status] of [['cccccccc-cccc-4ccc-8ccc-cccccccccccc', 'demo-v3-feature-duplicate', 'partial'], ['dddddddd-dddd-4ddd-8ddd-dddddddddddd', 'demo-v3-bug-duplicate', 'unknown']]) {
      const run = runs.find(item => item.runId === runId), proposal = run.result.nextActions.find(item => item.kind === 'close-as-duplicate');
      const target = { ...copy(run.task.target), repository: run.task.repository };
      if (status === 'unknown') target.number = 499;
      webActions[operationId] = { operationId, requestId: operationId, actionId: `${run.runId}:${proposal.proposalId}`, kind: 'close-as-duplicate', target, draft: { requestId: operationId, actionId: `${run.runId}:${proposal.proposalId}`, kind: 'close-as-duplicate', target: copy(target), body: proposal.body, duplicateOf: copy(proposal.duplicateOf) }, confirmedBody: proposal.body, status, completedSteps: status === 'partial' ? ['duplicate-comment'] : [], remainingSteps: status === 'partial' ? ['close-issue'] : ['duplicate-comment', 'close-issue'], urls: [], createdAt: at(3), updatedAt: at(2), account: 'pulse-demo', canSubmit: status === 'partial', blockers: [], targetUrl: `https://github.com/${target.repository}/issues/${target.number}`, state: 'OPEN', sourceOrigin: status === 'partial' ? `pulse-task://${run.runId}` : 'chrome-extension://pulse-local-ui-preview', error: { code: status === 'partial' ? 'PARTIAL_ACTION' : 'REMOTE_OUTCOME_UNKNOWN', message: status === 'partial' ? 'Sample: the association comment was confirmed, but closing the Issue failed.' : 'Sample: the association response was lost; its remote result is unknown.', guidance: status === 'partial' ? 'Continue only the remaining close step after confirmation.' : 'Check the existing result before trying another write.' } };
      if (status === 'partial') resultActions[operationId] = { runId, proposalId: proposal.proposalId, attemptId: operationId, operationId };
    }
    return {
      config: { agent: 'codex', agentDefaults: copy(agentDefaults), cliSelections: { codex: '', copilot: '' }, mainRepoFolder: runConfig.mainRepoFolder, worktreeRoot: runConfig.worktreeRoot, permission: 'yolo', githubAccount: '', prPrompt: 'powertoys-pr-loop-review.prompt.md', issuePrompt: 'powertoys-issue-local-fix.prompt.md', e2ePrompt: 'powertoys-pr-e2e-test.prompt.md', reproductionPrompt: '' },
      runs, operations: {}, webActions, resultActions, resultActionRequests: {}, remoteTargets: { 'microsoft/PowerToys:pr:41290': { headSha: 'e'.repeat(40), ciState: 'success', state: 'OPEN', draftPr: false }, 'microsoft/PowerToys:pr:41294': { ciState: 'failure', ciEvidence: 'sample-ci-failed-after-request' } },
      remoteBranches: { 'pulse-demo:fix/settings-focus': 'd'.repeat(40), 'pulse-demo:fix/changed-focus': 'e'.repeat(40) }, verificationRequests: {}, resultTaskRequests: {}, pageFailures: { 'demo-v3-pr-page-error': true },
    };
  }
  let state;
  try { state = JSON.parse(sessionStorage.getItem(storageKey) || 'null'); } catch { /* Use an isolated in-memory session if storage is unavailable. */ }
  if (!state?.config || !Array.isArray(state.runs) || !state.operations) state = seed();
  if (!state.webActions) state.webActions = webActionSamples();
  state.resultActions ||= {};
  state.resultActionRequests ||= {};
  state.remoteTargets ||= {};
  state.remoteBranches ||= {};
  state.verificationRequests ||= {};
  state.resultTaskRequests ||= {};
  function save() { try { sessionStorage.setItem(storageKey, JSON.stringify(state)); } catch { /* Preview remains usable in memory. */ } }
  save();
  const canonical = value => Array.isArray(value) ? value.map(canonical) : value && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
  const fingerprint = async value => Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(JSON.stringify(canonical(value))))), byte => byte.toString(16).padStart(2, '0')).join('');
  const sampleError = (code, message) => Object.assign(new Error(message), { code });
  const verificationRecommendation = result => result?.schemaVersion === 3 ? result.e2eAssessment ? { ...copy(result.e2eAssessment), mode: 'ui-e2e' } : null : result?.verificationRecommendation ?? null;
  function savedResult(result) {
    const value = copy(result);
    for (const field of ['recommendation', 'relatedVerification', 'e2eEvidenceComplete', 'findingSummary', 'nextSteps', 'blockers']) delete value[field];
    for (const action of value.nextActions ?? []) { delete action.availability; delete action.proposalId; }
    return value;
  }
  const pagePaths = ['findings', 'nextActions', 'validation', 'artifacts', 'diagnostics', 'verificationEvidence', 'review.suggestions', 'plans', 'report.coverage', 'report.limitations', 'reviewConclusion.blockingUncertainties', 'e2eAssessment.scenarios', 'e2eAssessment.expectedResults', 'e2eAssessment.prerequisites', 'e2eAssessment.evidence', 'featureAssessment.reasons', 'featureAssessment.evidence', 'featureAssessment.acceptanceCriteria', 'featureAssessment.questions', 'featureAssessment.alternatives', 'bugAssessment.reasons', 'bugAssessment.evidence', 'bugAssessment.questions', 'bugAssessment.reproduction.steps', 'bugAssessment.reproduction.evidence'];
  const pathValue = (value, path) => path.split('.').reduce((node, key) => node?.[key], value);
  async function projectedRun(value) {
    const run = copy(getRun(value.runId));
    if (run.result?.schemaVersion !== 3) return run;
    if (run.task.target?.type === 'pr' && run.task.actionKind === 'pr-review') {
      const related = await relatedReviews(run.runId);
      run.result.relatedVerification = copy(related.currentConclusion); run.result.e2eEvidenceComplete = related.currentConclusion.evidenceComplete === true;
    }
    run.result.recommendation = resultRecommendation(run);
    const sections = [];
    for (const path of pagePaths) {
      const items = pathValue(run.result, path);
      if (!Array.isArray(items) || items.length <= 25) continue;
      const names = path.split('.'); const field = names.pop();
      const owner = names.length ? pathValue(run.result, names.join('.')) : run.result;
      owner[field] = items.slice(0, 25); sections.push({ path, total: items.length, nextOffset: 25 });
    }
    if (sections.length) run.resultPaging = { fingerprint: await fingerprint(savedResult(value.result)), sections };
    return run;
  }
  async function resultPage(payload) {
    if (!pagePaths.includes(payload.path) || !Number.isInteger(payload.offset) || payload.offset < 0 || payload.limit !== undefined && (!Number.isInteger(payload.limit) || payload.limit < 1 || payload.limit > 200)) throw sampleError('INVALID_REQUEST', 'Choose a supported saved-result array and a valid page offset.');
    const run = getRun(payload.runId); const currentFingerprint = await fingerprint(savedResult(run.result));
    if (run.result?.schemaVersion !== 3) throw sampleError('INVALID_REQUEST', 'Only saved v3 reports support this result paging route.');
    if (payload.fingerprint !== currentFingerprint) throw sampleError('RESULT_CHANGED', 'The saved sample result changed. Reload it before combining more pages.');
    if (state.pageFailures?.[run.runId]) { state.pageFailures[run.runId] = false; save(); throw sampleError('RESULT_PAGE_UNAVAILABLE', 'Sample: the remaining report page could not be read. Reload the complete report to retry without discarding its evidence.'); }
    const items = pathValue(run.result, payload.path);
    if (!Array.isArray(items) || payload.offset > items.length) throw sampleError('INVALID_REQUEST', 'The saved sample result does not contain this page.');
    const end = Math.min(items.length, payload.offset + (payload.limit ?? 100));
    return { items: copy(items.slice(payload.offset, end)), nextOffset: end < items.length ? end : null, total: items.length, fingerprint: currentFingerprint };
  }
  function resultRecommendation(run) {
    const result = run.result;
    if (result?.schemaVersion !== 3 || result.outcome !== 'completed' || !result.report?.complete || !result.report.rechecked) return { kind: 'incomplete', reason: 'The sample final report is incomplete or was not recorded. Inspect the retained facts without inferring a positive review.' };
    const explicitNone = result.nextActions.find(row => row.recommended && row.kind === 'none');
    if (explicitNone) return { kind: 'none', reason: explicitNone.reason, proposalId: explicitNone.proposalId };
    if (run.task.target.type === 'pr') {
      if (result.findings.some(row => row.confirmed && row.status === 'open' && ['P0', 'P1'].includes(row.priority))) return { kind: 'address-findings', reason: 'Address the confirmed open P0 or P1 sample findings. This recommendation is distinct from manual approval permissions.' };
      if (!result.e2eAssessment) return { kind: 'incomplete', reason: 'E2E necessity was not recorded; absence does not mean it is unnecessary.' };
      if (result.e2eAssessment.level === 'required' && !result.e2eEvidenceComplete) return { kind: 'run-e2e', reason: 'Supplement the required sample E2E scenarios before following a positive recommendation.' };
      return { kind: 'approve', reason: 'No confirmed open P0 or P1 remains, and E2E is not required. Inspect the sample report before choosing an action.' };
    }
    const proposal = result.nextActions.find(row => row.recommended);
    return proposal ? { kind: proposal.kind, reason: proposal.reason, proposalId: proposal.proposalId, ...(proposal.planId ? { planId: proposal.planId } : {}) } : { kind: 'incomplete', reason: 'No default next action was recorded.' };
  }
  async function startResultTask(payload) {
    if (Object.keys(payload).some(key => !['runId', 'proposalId', 'requestId', 'execution', 'prerequisitesConfirmed'].includes(key)) || typeof payload.requestId !== 'string' || !payload.requestId.trim() || payload.requestId.length > 128 || payload.prerequisitesConfirmed !== undefined && typeof payload.prerequisitesConfirmed !== 'boolean') throw sampleError('INVALID_REQUEST', 'Choose one saved plan and a new request ID.');
    if (payload.execution != null && (typeof payload.execution !== 'object' || Array.isArray(payload.execution) || Object.keys(payload.execution).some(key => !['agent', 'model', 'reasoningEffort'].includes(key)) || Object.values(payload.execution).some(value => typeof value !== 'string'))) throw sampleError('INVALID_REQUEST', 'Choose only a supported sample agent, model, and reasoning effort.');
    const requestFingerprint = await fingerprint(payload);
    const existing = state.resultTaskRequests[payload.requestId];
    if (existing) {
      if (existing.fingerprint !== requestFingerprint) throw sampleError('REQUEST_CONFLICT', 'This sample request already used a different proposal or execution configuration.');
      return getRun(existing.runId);
    }
    const parent = getRun(payload.runId);
    if (active(parent)) throw sampleError('TASK_STILL_RUNNING', 'Wait for the current sample task to finish before starting a saved plan.');
    if (state.runs.some(run => run.task.requestId === payload.requestId)) throw sampleError('INVALID_REQUEST', 'Starting a saved plan requires a new request ID.');
    if (parent.result?.schemaVersion !== 3 || !parent.result.structured || parent.task.target?.type !== 'issue') throw sampleError('RESULT_NOT_ACTIONABLE', 'This sample result does not contain a valid Issue plan.');
    const proposal = findProposal(parent, payload.proposalId);
    const selected = parent.result.plans.find(plan => plan.id === proposal.planId);
    if (proposal.kind !== 'start-task' || !selected || selected.kind !== proposal.taskKind || !['feature-implement', 'issue-fix', 'reproduction-setup', 'issue-verify'].includes(selected.kind)) throw sampleError('PLAN_MISMATCH', 'The selected task must match its saved plan.');
    const execution = resolveExecution(state.config, payload.execution ?? undefined);
    const selectedCli = state.config.cliSelections[execution.agent];
    const installation = selectedCli ? cliProbe(execution.agent, selectedCli) : cliInstallations[execution.agent].find(item => item.available);
    if (!installation) throw sampleError('CLI_UNAVAILABLE', 'The selected sample CLI is unavailable.');
    const runId = crypto.randomUUID(), revisionSha = /^[a-f0-9]{40}$/i.test(parent.config.worktreeBase ?? '') ? parent.config.worktreeBase : null;
    const run = { runId, task: { requestId: payload.requestId, actionId: `result-task:${parent.runId}:${proposal.planId}`, actionKind: selected.kind, repository: parent.task.repository, target: copy(parent.task.target), execution: { agent: execution.agent, model: execution.model, reasoningEffort: execution.reasoningEffort }, planSource: { parentRunId: parent.runId, parentResultFingerprint: await fingerprint(savedResult(parent.result)), proposalId: proposal.proposalId, planId: selected.id, repository: parent.task.repository, target: copy(parent.task.target), revisionSha }, prompt: '[Sample task] Execute only the explicitly selected saved Issue plan. Reuse the parent investigation and verify prerequisites first. Do not publish GitHub changes, push commits, or rewrite the parent report. No real process was started.', context: { resultPlan: { parentRunId: parent.runId, parentReportPath: `C:\\Demo\\Pulse\\runs\\${parent.runId}\\result.json`, parentSummary: parent.result.summary, parentActionKind: parent.task.actionKind, parentOutcome: parent.result.outcome, plan: copy(selected), prerequisitesReportedReady: payload.prerequisitesConfirmed === true, initialRevisionSha: revisionSha } } }, config: { ...execution, cliPath: installation.resolvedPath || installation.path, permission: state.config.permission, mainRepoFolder: state.config.mainRepoFolder, worktreeRoot: state.config.worktreeRoot, repoFolder: `${state.config.worktreeRoot}\\pulse-${runId}`, worktreeBranch: `codex/pulse-${runId}`, ...(revisionSha ? { worktreeBase: revisionSha } : {}) }, status: { state: 'running', createdAt: at(0), startedAt: at(0), updatedAt: at(0), sequence: 1, latestProgress: 'Sample saved-plan task: verifying prerequisites. No real CLI is running.' }, view: { read: false, handled: false } };
    state.runs.unshift(run); state.resultTaskRequests[payload.requestId] = { fingerprint: requestFingerprint, runId }; save(); return run;
  }
  let reviewInitialization;
  async function initializeReviewLinks() {
    if (state.reviewSnapshots) return;
    if (reviewInitialization) return reviewInitialization;
    reviewInitialization = (async () => {
      const snapshots = {};
      for (const run of state.runs.filter(item => item.task.actionKind === 'pr-review' && verificationRecommendation(item.result))) snapshots[run.runId] = { parentResultFingerprint: await fingerprint(savedResult(run.result)), recommendationId: await fingerprint(verificationRecommendation(run.result)) };
      for (const run of state.runs.filter(item => item.task.followUp && !item.task.followUp.recommendationId)) Object.assign(run.task.followUp, snapshots[run.task.followUp.parentRunId]);
      state.reviewSnapshots = snapshots; save();
    })();
    return reviewInitialization;
  }
  function originalProvenance(value, expected) {
    return value?.version === 1 && value.source === 'host' && value.subject === 'original-pr' && value.expectedHeadSha === expected && ['start', 'end'].every(key => value[key]?.headSha === expected && value[key]?.workingTree === 'clean');
  }
  async function relatedReviews(runId) {
    const requested = getRun(runId);
    const parent = getRun(requested.task.followUp?.parentRunId || runId);
    if (parent.task.actionKind !== 'pr-review') throw sampleError('INVALID_REQUEST', 'Related sample verification belongs to a PR code review.');
    const recommendation = verificationRecommendation(parent.result);
    const recommendationId = recommendation ? await fingerprint(recommendation) : null;
    const parentResultFingerprint = parent.result ? await fingerprint(savedResult(parent.result)) : null;
    const runs = [], evidence = [];
    let acceptedFailure = false, acceptedSuccess = false, incomplete = false, passingAssessment = false, failedEvidence = null, completedRunId = null;
    for (const child of state.runs.filter(item => item.task.followUp?.parentRunId === parent.runId)) {
      const link = child.task.followUp, result = child.result;
      let reason = null;
      if (child.task.actionKind !== 'pr-verify' || child.task.repository !== parent.task.repository || JSON.stringify(child.task.target) !== JSON.stringify(parent.task.target) || child.task.expectedHeadSha !== parent.task.expectedHeadSha || link.revisionSha !== parent.task.expectedHeadSha || link.subject !== 'original-pr') reason = 'This verification targets a different PR revision or subject.';
      else if (link.parentResultFingerprint !== parentResultFingerprint) reason = 'The verification was not linked to this exact saved review result.';
      else if (link.recommendationId !== recommendationId) reason = 'The verification belongs to a different recommendation.';
      else if (!originalProvenance(parent.provenance, parent.task.expectedHeadSha) || !originalProvenance(child.provenance, parent.task.expectedHeadSha)) reason = 'The sample Host could not establish matching clean original-PR snapshots. Local candidate or unknown-source evidence stays separate.';
      else if (!result?.structured || ![2, 3].includes(result.schemaVersion)) reason = 'A valid structured supplemental result is not available yet.';
      else if (result.assessment?.subject === 'local-candidate') reason = 'This verification assessed a local candidate, not the original PR.';
      const eligible = reason === null;
      const finished = child.status.state === 'succeeded' && child.status.exitCode === 0;
      const complete = finished && result?.outcome === 'completed';
      if (!complete) incomplete = true;
      const context = child.task.context?.reviewVerification;
      const prefix = `e2e-${link.recommendationId.slice(0, 24)}-`;
      const scenarioIds = context?.scenarioIdPrefix === prefix ? (context.recommendation?.scenarios ?? []).map((_, index) => `${prefix}${index + 1}`) : (context?.scenarioChecks ?? []).map(row => row.id);
      const scenarioCoverageComplete = eligible && complete && scenarioIds.length > 0 && scenarioIds.every(id => { const rows = result.validation.filter(row => row.id === id); return rows.length === 1 && rows[0].status === 'passed' && rows[0].evidence?.length; });
      let childPassedEvidence = false;
      if (eligible && finished) for (const row of result.verificationEvidence ?? []) {
        const applicable = child.task.reviewOptions.mode === 'ui-e2e' ? row.kind === 'runtime' : ['build', 'automated-tests'].includes(row.kind);
        if (row.source !== 'current-run' || row.subject !== 'original-pr' || row.revisionSha !== parent.task.expectedHeadSha || !['passed', 'failed'].includes(row.status) || !row.evidence.length || !applicable) continue;
        const projected = { ...copy(row), source: 'prior-run', runId: child.runId };
        evidence.push(projected);
        if (row.status === 'failed') failedEvidence ??= copy(projected);
        if (row.status === 'passed') childPassedEvidence = true;
        if (complete && row.status === 'passed') acceptedSuccess = true;
      }
      if (eligible && complete) for (const row of result.validation ?? []) if (scenarioIds.includes(row.id) && row.status === 'failed') failedEvidence ??= { id: row.id, source: 'prior-run', kind: 'runtime', status: 'failed', subject: 'original-pr', revisionSha: parent.task.expectedHeadSha, summary: `Requested sample scenario failed: ${row.name}`, evidence: copy(row.evidence ?? []), runId: child.runId };
      if (scenarioCoverageComplete && result.assessment?.subject === 'original-pr' && result.assessment.revisionSha === parent.task.expectedHeadSha && result.assessment.status === 'passed') { passingAssessment = true; completedRunId ??= child.runId; }
      if (eligible && complete && result.assessment?.subject === 'original-pr' && result.assessment.revisionSha === parent.task.expectedHeadSha && result.assessment.status === 'failed') acceptedFailure = true;
      runs.push({ runId: child.runId, status: copy(child.status), summary: result?.summary ?? null, outcome: result?.outcome ?? null, scenarioCoverageComplete, assessment: copy(result?.assessment ?? null), verificationEvidence: copy(result?.verificationEvidence ?? []), provenance: copy(child.provenance ?? null), compatibility: { eligible, reason }, recommendationId: link.recommendationId });
    }
    const status = acceptedFailure ? 'changes-requested' : failedEvidence ? 'verification-incomplete' : acceptedSuccess ? 'evidence-added' : incomplete ? 'verification-incomplete' : 'unchanged';
    const summaries = { 'changes-requested': 'Supplemental verification found a failing behavior in the original PR revision. Inspect that evidence before approving.', 'evidence-added': 'Supplemental verification adds evidence for the same original PR revision. The original report and its selected scope are preserved.', 'verification-incomplete': 'Supplemental verification has not completed. Inspect its prerequisites and retained diagnostics; the original code review remains available.', unchanged: 'No compatible supplemental verification has changed the evidence for this original PR revision.' };
    return { parentRunId: parent.runId, recommendation: recommendation ? { ...copy(recommendation), recommendationId } : null, runs: runs.slice(0, 32), evidence: evidence.slice(0, 32), provenance: copy(parent.provenance ?? null), errors: [], totalCount: runs.length, truncated: runs.length > 32 || evidence.length > 32, currentConclusion: { status, canSupplementAssessment: !acceptedFailure && !failedEvidence && passingAssessment, evidenceComplete: !acceptedFailure && !failedEvidence && passingAssessment, completedRunId: !acceptedFailure && !failedEvidence && passingAssessment ? completedRunId : null, failedEvidence, summary: !acceptedFailure && failedEvidence ? 'Supplemental verification includes a failed check. Determine whether it reflects the product or unavailable infrastructure before treating this evidence gap as resolved.' : summaries[status] } };
  }
  async function verifyReview(payload) {
    if (Object.keys(payload).some(key => !['parentRunId', 'requestId', 'recommendationId', 'execution', 'prerequisitesConfirmed'].includes(key))) throw sampleError('INVALID_REQUEST', 'Use the saved sample recommendation and its parent review to prepare verification.');
    if (typeof payload.requestId !== 'string' || !payload.requestId.trim() || payload.requestId.length > 128) throw sampleError('INVALID_REQUEST', 'Supplemental verification requires a new request ID.');
    if (payload.prerequisitesConfirmed !== undefined && typeof payload.prerequisitesConfirmed !== 'boolean') throw sampleError('INVALID_REQUEST', 'Prerequisite confirmation must be a boolean.');
    if (payload.execution != null && (typeof payload.execution !== 'object' || Array.isArray(payload.execution) || Object.keys(payload.execution).some(key => !['agent', 'model', 'reasoningEffort'].includes(key)) || Object.values(payload.execution).some(value => typeof value !== 'string'))) throw sampleError('INVALID_REQUEST', 'Choose only a supported sample agent, model, and reasoning effort.');
    const requestFingerprint = await fingerprint(payload);
    const existing = state.verificationRequests[payload.requestId];
    if (existing) {
      if (existing.fingerprint !== requestFingerprint) throw sampleError('REQUEST_CONFLICT', 'This sample verification request already used different options. Recover the original request unchanged.');
      return getRun(existing.runId);
    }
    const parent = getRun(payload.parentRunId);
    if (parent.task.actionKind !== 'pr-review' || parent.task.target?.type !== 'pr') throw sampleError('INVALID_REQUEST', 'Supplemental verification must start from a PR code review.');
    if (active(parent)) throw sampleError('TASK_STILL_RUNNING', 'Wait for the sample code review to finish.');
    if (state.runs.some(run => run.task.requestId === payload.requestId)) throw sampleError('INVALID_REQUEST', 'Supplemental verification requires a new request ID.');
    const recommendation = verificationRecommendation(parent.result);
    if (!parent.result?.structured || !recommendation || await fingerprint(recommendation) !== payload.recommendationId) throw sampleError('RECOMMENDATION_CHANGED', 'The sample review does not contain this verification recommendation. Refresh the saved result.');
    if (recommendation.level === 'not_needed') throw sampleError('VERIFICATION_NOT_RECOMMENDED', 'This sample report explicitly records that supplemental E2E is not needed.');
    if (!['build-tests', 'ui-e2e'].includes(recommendation.mode)) throw sampleError('INVALID_REQUEST', 'Choose build and test checks or runtime scenarios for supplemental verification.');
    if (!/^[a-f0-9]{40}$/i.test(parent.task.expectedHeadSha ?? '')) throw sampleError('INVALID_REQUEST', 'The original sample PR revision is not established.');
    if (['missing-prerequisites', 'unknown'].includes(recommendation.readiness) && payload.prerequisitesConfirmed !== true) throw sampleError('VERIFICATION_PREREQUISITES_MISSING', 'Review the listed prerequisites, then explicitly confirm readiness to attempt setup. This acknowledgement does not establish that the environment is available.');
    if (parent.result.assessment?.subject === 'local-candidate') throw sampleError('VERIFICATION_SUBJECT_MISMATCH', 'Keep local candidate evidence separate. Start verification from a review of the original PR revision.');
    const runId = crypto.randomUUID();
    const execution = resolveExecution(state.config, payload.execution ?? undefined);
    const selected = state.config.cliSelections[execution.agent];
    const installation = selected ? cliProbe(execution.agent, selected) : cliInstallations[execution.agent].find(item => item.available);
    if (!installation) throw sampleError('CLI_UNAVAILABLE', 'The selected sample CLI is unavailable. Choose an installation in Settings.');
    const run = { runId, task: { requestId: payload.requestId, actionId: `pr-verify:${parent.runId}:${payload.recommendationId.slice(0, 16)}`, actionKind: 'pr-verify', repository: parent.task.repository, target: copy(parent.task.target), expectedHeadSha: parent.task.expectedHeadSha, reviewOptions: { mode: recommendation.mode }, followUp: { parentRunId: parent.runId, recommendationId: payload.recommendationId, parentResultFingerprint: await fingerprint(parent.result), subject: 'original-pr', revisionSha: parent.task.expectedHeadSha }, ...(payload.execution ? { execution: copy(payload.execution) } : {}), prompt: '[Sample task] Perform only the saved supplemental verification scenarios. Reuse the parent review context; do not repeat a complete code review. No real CLI was started.', context: { reviewVerification: { parentRunId: parent.runId, recommendation: copy(recommendation), parentSummary: parent.result.summary, reviewConclusion: copy(parent.result.reviewConclusion ?? null), assessment: copy(parent.result.assessment ?? null), prerequisitesReportedReady: payload.prerequisitesConfirmed === true, preflight: 'Check the listed prerequisites first. User confirmation reports changed conditions; it does not prove that the environment is available. Unknown readiness requires agent preflight before any scenario.' } } }, config: { ...execution, cliPath: installation.resolvedPath || installation.path, permission: state.config.permission, mainRepoFolder: state.config.mainRepoFolder, worktreeRoot: state.config.worktreeRoot, repoFolder: `${state.config.worktreeRoot}\\pulse-${runId}`, worktreeBranch: `codex/pulse-${runId}` }, status: { state: 'running', createdAt: at(0), startedAt: at(0), updatedAt: at(0), sequence: 1, latestProgress: 'Sample supplemental verification: checking the saved prerequisites first. No real CLI is running.' }, view: { read: false, handled: false } };
    run.task.followUp.parentResultFingerprint = await fingerprint(savedResult(parent.result));
    const verification = run.task.context.reviewVerification;
    verification.scenarioIdPrefix = `e2e-${payload.recommendationId.slice(0, 24)}-`;
    verification.scenarioChecks = parent.result.schemaVersion === 3 ? null : recommendation.scenarios.map((_, index) => ({ id: `${verification.scenarioIdPrefix}${index + 1}`, index }));
    verification.scenarioReporting = 'Report every saved scenario by its 1-based index after scenarioIdPrefix, with actual status and evidence. A generic passing E2E row does not establish complete scenario coverage.';
    verification.parentReviewStatus = parent.result.reviewConclusion?.status ?? null; verification.parentAssessmentStatus = parent.result.assessment?.status ?? null;
    delete verification.reviewConclusion; delete verification.assessment;
    state.runs.unshift(run); state.verificationRequests[payload.requestId] = { fingerprint: requestFingerprint, runId }; save(); return run;
  }
  function getRun(runId) {
    const run = state.runs.find(item => item.runId === runId);
    if (!run) throw new Error('Sample task not found. Return to Tasks or reset the samples.');
    if ([2, 3].includes(run.result?.schemaVersion)) {
      for (const proposal of run.result.nextActions) {
        const reasons = publishingActions.has(proposal.kind) ? resultActionBlockers(run, proposal, false).map(item => item.message) : [];
        proposal.availability = { enabled: !reasons.length, reasons };
      }
      run.result.nextSteps = copy(run.result.nextActions);
    }
    run.reviewSummary = reviewSummary(run);
    return run;
  }
  function getWebAction(operationId) {
    const action = state.webActions[operationId];
    if (!action) throw new Error('Sample action not found. Use a sample action ID from the preview fixtures.');
    return action;
  }
  function webActionSummary(action) {
    const { operationId, requestId, actionId, kind, target, status, completedSteps, remainingSteps, urls, error, createdAt, updatedAt } = action;
    const association = state.resultActions[action.operationId];
    return { operationId, requestId, actionId, kind, target, status, completedSteps, remainingSteps, urls, ...(error ? { error } : {}), createdAt, updatedAt, ...(kind === 'close-as-duplicate' ? { duplicateOf: copy(action.draft.duplicateOf), resumeRequired: duplicateResume(action) } : {}), ...(association ? { runId: association.runId, proposalId: association.proposalId, attemptId: association.attemptId, retryAllowed: retryAllowed(action), retryRequested: !!action.retry, account: action.account } : {}) };
  }
  const duplicateResume = action => action.kind === 'close-as-duplicate' && action.status === 'partial' && action.completedSteps.includes('duplicate-comment') && !action.completedSteps.includes('close-issue');
  function duplicatePreview(action) {
    if (action.kind !== 'close-as-duplicate') return action;
    const body = action.confirmedBody ?? action.draft.body;
    const duplicateSuffix = `\n\nDuplicate of ${action.draft.duplicateOf.url}\n\n<!-- powertoys-pulse:duplicate:sample-${action.target.number}-${action.draft.duplicateOf.number} -->`;
    return { ...action, draft: { ...copy(action.draft), body }, duplicateSuffix, commentBody: body + duplicateSuffix, bodyEditable: action.confirmedBody === undefined, resumeRequired: duplicateResume(action), canSubmit: action.status === 'prepared' ? action.canSubmit : duplicateResume(action) && !action.blockers.length };
  }
  function retryAllowed(action) {
    return action.status === 'cancelled' || action.status === 'failed' && action.completedSteps.length === 0 && action.writeStarted !== true || action.kind === 'trigger-ci' && action.status === 'succeeded';
  }
  const currentSampleAccount = () => state.config.githubAccount || 'pulse-demo';
  function sampleTarget(run) {
    return { state: 'OPEN', headSha: sha, draftPr: false, ciState: 'success', ciEvidence: 'sample-ci-initial', ...state.remoteTargets[`${run.task.repository}:${run.task.target.type}:${run.task.target.number}`] };
  }
  function completedWorkflow(run) {
    const result = run.result;
    return run.status.state === 'succeeded' && [2, 3].includes(result?.schemaVersion) && result.outcome === 'completed' && result.structured !== false && (result.schemaVersion !== 3 || result.report?.complete && result.report?.rechecked) &&
      !result.diagnostics.some(item => item.severity === 'error') && !result.validation.some(item => item.required && item.status !== 'passed') &&
      workflowChecks(run.task).every(id => result.validation.some(item => item.id === id && item.required && item.status === 'passed'));
  }
  function reviewSummary(run) {
    const result = run.result;
    if (run.task.target?.type !== 'pr' || run.task.actionKind !== 'pr-review' || result?.schemaVersion !== 2) return null;
    const passed = item => item.status === 'passed' && item.details.trim() && item.evidence.length > 0 && item.evidence.every(value => value.trim());
    const reviewed = ['context', 'local-review'].every(id => result.validation.filter(item => item.id === id && passed(item)).length === 1);
    const limitations = result.validation.filter(item => !passed(item)).map(item => ({ ...copy(item), category: item.status === 'failed' && !item.required ? 'recorded-observation' : 'validation-gap' }));
    const assessment = result.assessment;
    if (assessment?.status === 'inconclusive') limitations.push({ id: 'assessment', name: 'Original pull request assessment', status: 'inconclusive', required: false, details: assessment.summary, evidence: [], category: 'assessment' });
    const concreteGapCount = limitations.filter(item => item.category === 'validation-gap').length;
    const limitationCount = concreteGapCount || (limitations.some(item => item.category === 'assessment') ? 1 : 0);
    const observationCount = limitations.filter(item => item.category === 'recorded-observation').length;
    const expected = (run.task.expectedHeadSha || '').toLowerCase();
    const sameSha = /^[a-f0-9]{40}$/.test(expected) && (assessment?.subject === 'original-pr' && assessment?.revisionSha?.toLowerCase() === expected || result.review?.headSha?.toLowerCase() === expected) && (!assessment || assessment.subject === 'original-pr' && assessment.revisionSha?.toLowerCase() === expected) && (!result.review || result.review.headSha?.toLowerCase() === expected);
    const validReport = run.status.state === 'succeeded' && run.status.exitCode === 0 && result.cliExitCode === 0 && result.structured === true && !result.diagnostics.some(item => ['INVALID_RESULT', 'OUTPUT_INCOMPLETE'].includes(item.code));
    const reasons = [];
    if (!validReport) reasons.push('The sample CLI must have finished successfully with a valid structured review report.');
    if (!reviewed) reasons.push('The sample target context and local code review must both be complete and supported by evidence.');
    if (!sameSha) reasons.push('The sample review evidence must be bound to the saved PR SHA.');
    const approvalReasons = [...reasons];
    if (!['completed', 'blocked'].includes(result.outcome)) approvalReasons.push('A failed, cancelled or interrupted sample workflow cannot be approved with limitations.');
    if (!limitationCount) approvalReasons.push('This sample review has no remaining verification limitations to accept.');
    if (assessment?.subject !== 'original-pr' || assessment?.status !== 'inconclusive' || assessment?.revisionSha?.toLowerCase() !== expected) approvalReasons.push('The sample original PR assessment must remain inconclusive at the saved review SHA.');
    if (result.findings.some(item => ['open', 'unverified'].includes(item.status) && ['high', 'medium'].includes(item.severity))) approvalReasons.push('Unresolved high or medium findings prevent sample approval with limitations.');
    if (result.validation.some(item => item.required && item.status === 'failed')) approvalReasons.push('A failed required check prevents sample approval with limitations.');
    return { codeReview: reasons.length ? 'incomplete' : 'completed', verification: limitationCount ? 'limited' : 'complete', headSha: expected, assessmentStatus: assessment?.status || '', limitations, limitationCount, observationCount, limitsTruncated: false, canRequestEvidence: !reasons.length && limitationCount > 0 && ['completed', 'blocked', 'failed'].includes(result.outcome), canApproveWithLimitations: !approvalReasons.length, reasons, approvalReasons };
  }
  function reviewDecisions(run, closed) {
    const summary = reviewSummary(run);
    if (!summary?.limitationCount) return [];
    // An opaque sample fingerprint keeps a stale confirmation from accepting changed saved evidence.
    const identity = JSON.stringify({ repository: run.task.repository, target: run.task.target, headSha: summary.headSha, result: run.result });
    const fingerprint = [0x811c9dc5, 0x9e3779b9, 0x85ebca6b, 0xc2b2ae35].map(seed => { let value = seed; for (let index = 0; index < identity.length; index++) value = Math.imul(value ^ identity.charCodeAt(index), 0x01000193); return (value >>> 0).toString(16).padStart(8, '0'); }).join('');
    const remote = sampleTarget(run);
    const remoteReasons = [];
    if (closed || remote.state !== 'OPEN') remoteReasons.push('The sample pull request is no longer open.');
    if (summary.headSha !== remote.headSha) remoteReasons.push('The sample pull request head changed after the local review. Review the current SHA before submitting a decision.');
    const describe = observations => {
      const context = observations ? '' : summary.limitations.filter(item => item.category === 'assessment').map(item => `Assessment context: ${item.details}`).join('\n\n');
      const items = summary.limitations.filter(item => item.category === (observations ? 'recorded-observation' : 'validation-gap')).map(item => `- **${item.name}** (${item.status}${item.required ? '; required' : ''}): ${item.details}`).join('\n\n');
      return `${context}${context && items ? '\n\n' : ''}${items}`;
    };
    const verification = describe(false);
    const observations = describe(true);
    const evidenceBody = `The local code review of commit \`${summary.headSha}\` is complete. The following behavior or acceptance evidence is still unverified:\n\n${verification}\n\nCould you provide the environment and configuration, exact steps, expected and observed results, and relevant logs or recordings for these checks on this revision? Please identify the tested commit if the branch changes. Successful builds or checks in other environments should be distinguished from evidence for the unverified behavior.`;
    const disclosure = `### Validation limitations accepted by the reviewer\n\nThis approval applies to commit \`${summary.headSha}\`. Local code review is complete, but the original pull request's product behavior remains inconclusive. The reviewer explicitly accepts the following verification limitations; this approval does not claim that these checks passed.\n\n${verification}${observations ? `\n\n### Recorded failed observations\n\nThese are retained observations, not additional claims that the issue remains unresolved.\n\n${observations}` : ''}\n\nThe saved workflow outcome and validation evidence are unchanged. Pulse does not perform a merge as part of this action; missing checks remain unverified.`;
    return [
      { id: `review-request-evidence-${fingerprint}`, kind: 'request-evidence', title: 'Ask the author for evidence', description: 'Ask the PR author to supply evidence for the remaining validation limits.', body: evidenceBody, disclosure: '', limitations: copy(summary.limitations), enabled: summary.canRequestEvidence && !remoteReasons.length, reasons: [...summary.reasons, ...remoteReasons], requiresAcknowledgement: false },
      { id: `review-approve-with-limitations-${fingerprint}`, kind: 'approve-with-limitations', title: 'Approve with validation limitations', description: 'This sends an APPROVE review on GitHub with the listed limitations in its body. GitHub has no separate partial-approval state. The saved assessment remains inconclusive; Pulse does not merge as part of this action.', body: 'I have reviewed the source changes and am accepting the explicitly listed validation limitations for this revision.', disclosure, limitations: copy(summary.limitations), enabled: summary.canApproveWithLimitations && !remoteReasons.length, reasons: [...summary.approvalReasons, ...remoteReasons], requiresAcknowledgement: true },
    ];
  }
  function findProposal(run, proposalId) {
    const proposals = [2, 3].includes(run.result?.schemaVersion) ? run.result.nextActions : run.result?.nextSteps;
    const proposal = proposals?.find(item => item.proposalId === proposalId);
    if (!proposal) throw new Error('This sample proposal is unavailable. Reload the task result.');
    return proposal;
  }
  function hasConfirmedP0(run) {
    return state.runs.some(other => other.task.repository.toLowerCase() === run.task.repository.toLowerCase() && other.task.target?.type === 'pr' && other.task.target.number === run.task.target.number && other.task.expectedHeadSha === run.task.expectedHeadSha && other.result?.schemaVersion === 3 &&
      (other.result.assessment?.subject === 'original-pr' && other.result.assessment.revisionSha === run.task.expectedHeadSha || other.task.actionKind === 'pr-review' && other.result.reviewConclusion?.revisionSha === run.task.expectedHeadSha) &&
      other.result.findings?.some(finding => finding.priority === 'P0' && finding.confirmed === true && finding.status === 'open'));
  }
  function manualPrBlockers(run, kind, checkRemote = true) {
    const reasons = [];
    if (run.task.target?.type !== 'pr') return [{ code: 'TARGET_MISMATCH', message: 'This manual sample action requires a saved PR target.' }];
    if (!/^[a-f0-9]{40}$/i.test(run.task.expectedHeadSha || '')) reasons.push({ code: 'MISSING_HEAD_SHA', message: 'The sample task must retain a complete original PR SHA.' });
    if (kind === 'approve' && hasConfirmedP0(run)) reasons.push({ code: 'CONFIRMED_P0', message: 'This original PR revision has a confirmed unresolved P0 finding.' });
    if (checkRemote) {
      const current = sampleTarget(run);
      if (run.task.expectedHeadSha !== current.headSha) reasons.push({ code: 'STALE_HEAD', message: 'The current sample PR SHA differs from the immutable task revision.' });
      if (current.authenticated === false || current.archived || current.locked && current.writer === false) reasons.push({ code: 'GITHUB_PERMISSION_REQUIRED', message: 'The current sample GitHub account cannot perform this operation.' });
      if (kind !== 'comment' && current.state !== 'OPEN') reasons.push({ code: 'TARGET_CLOSED', message: 'The sample PR is no longer open.' });
      if (['approve', 'requestChanges'].includes(kind) && current.author === true) reasons.push({ code: 'SELF_REVIEW', message: 'GitHub does not permit approving or requesting changes on your own PR.' });
      if (kind === 'merge-pr' && current.draftPr) reasons.push({ code: 'DRAFT_PR', message: 'The sample PR is still a draft.' });
      if (kind === 'merge-pr' && current.ciState !== 'success') reasons.push({ code: 'CI_NOT_PASSING', message: 'The sample required CI checks are not passing.' });
    }
    return reasons;
  }
  function resultActionBlockers(run, proposal, checkRemote = true) {
    if (run.task.target?.type === 'pr' && (run.result?.schemaVersion === 3 || ['merge-pr', 'trigger-ci'].includes(proposal.kind))) return manualPrBlockers(run, proposal.kind, checkRemote);
    const blockers = [];
    const current = checkRemote ? sampleTarget(run) : undefined;
    if (run.result?.schemaVersion === 3 && ['comment', 'close', 'close-as-duplicate'].includes(proposal.kind)) {
      if (run.task.target?.type !== 'issue') blockers.push({ code: 'TARGET_MISMATCH', message: 'This sample proposal requires an Issue target.' });
      if (proposal.kind === 'close-as-duplicate' && (!proposal.duplicateOf || proposal.duplicateOf.url !== `https://github.com/${proposal.duplicateOf.repository}/issues/${proposal.duplicateOf.number}` || proposal.duplicateOf.repository === run.task.repository && proposal.duplicateOf.number === run.task.target.number)) blockers.push({ code: 'INVALID_DUPLICATE_TARGET', message: 'The saved sample duplicate proposal must identify one different original Issue.' });
      if (checkRemote && proposal.kind !== 'comment' && current.state !== 'OPEN') blockers.push({ code: 'TARGET_CLOSED', message: 'The sample Issue is already closed.' });
      return blockers;
    }
    if (!completedWorkflow(run)) blockers.push({ code: 'RESULT_NOT_ELIGIBLE', message: 'The sample workflow has not completed its required checks.' });
    if (run.task.target.type === 'pr' && (!/^[a-f0-9]{40}$/.test(run.task.expectedHeadSha || '') || (run.task.actionKind === 'pr-review' || run.result?.review) && run.result?.review?.headSha !== run.task.expectedHeadSha)) blockers.push({ code: 'REVIEW_SHA_MISMATCH', message: 'The saved sample review SHA does not match the immutable task SHA.' });
    if (['approve', 'merge-pr'].includes(proposal.kind) && (run.result?.assessment?.status !== 'passed' || run.result.findings.some(item => item.status !== 'fixed' && ['high', 'medium'].includes(item.severity)))) blockers.push({ code: 'UNRESOLVED_FINDINGS', message: 'The original PR has unresolved findings or has not received a passing assessment.' });
    if (run.task.target.type === 'pr' && current && run.task.expectedHeadSha !== current.headSha) blockers.push({ code: 'STALE_HEAD', message: 'The sample pull request head changed after the reviewed result.', guidance: 'Complete a new review for the current target SHA.' });
    if (['merge-pr', 'trigger-ci'].includes(proposal.kind)) {
      if (run.task.target.type !== 'pr') blockers.push({ code: 'TARGET_MISMATCH', message: 'This sample action requires the immutable task target to be a pull request.' });
      if (current && current.state !== 'OPEN') blockers.push({ code: 'TARGET_CLOSED', message: 'The sample target is no longer open.' });
    }
    if (proposal.kind === 'merge-pr' && current?.draftPr) blockers.push({ code: 'DRAFT_PR', message: 'The sample pull request is still a draft.' });
    if (proposal.kind === 'merge-pr' && current && current.ciState !== 'success') blockers.push({ code: 'CI_NOT_PASSING', message: 'The sample required checks are not passing.' });
    if (proposal.kind === 'create-pr') {
      if (run.task.target.type !== 'issue') blockers.push({ code: 'TARGET_MISMATCH', message: 'Create PR requires an issue task in this sample.' });
      if (!/^[a-f0-9]{40}$/.test(proposal.pullRequest?.sourceHeadSha || '')) blockers.push({ code: 'SOURCE_SHA_REQUIRED', message: 'The proposal must retain the source commit that was actually verified.' });
      if (checkRemote) {
        const remoteSha = state.remoteBranches[proposal.pullRequest?.head];
        if (!remoteSha) blockers.push({ code: 'SOURCE_BRANCH_UNPUBLISHED', message: 'The source branch has not been published. Preserve the local fix and publish the verified commit before creating a pull request.' });
        else if (remoteSha !== proposal.pullRequest?.sourceHeadSha) blockers.push({ code: 'SOURCE_HEAD_CHANGED', message: 'The current source branch does not match the verified source commit. Review and verify the changed source before creating a pull request.' });
      }
    }
    return blockers;
  }
  function refreshResultAction(action) {
    const association = state.resultActions[action.operationId];
    if (!association) { if (action.status === 'prepared') action.account = currentSampleAccount(); return duplicatePreview(action); }
    if (action.status !== 'prepared' && !duplicateResume(action)) return duplicatePreview(Object.assign(action, webActionSummary(action)));
    const run = getRun(association.runId);
    const proposal = association.manualKind ? { kind: association.manualKind } : findProposal(run, association.proposalId);
    const current = sampleTarget(run);
    action.blockers = association.manualKind ? manualPrBlockers(run, proposal.kind) : resultActionBlockers(run, proposal);
    if (action.retry && action.kind === 'trigger-ci' && (current.ciState !== 'failure' || current.ciEvidence === action.previousCiBaseline)) action.blockers.push({ code: 'CI_RETRY_NOT_REQUIRED', message: 'An explicit CI retry requires a newer failed CI result for the current head, after the previous trigger.' });
    action.canSubmit = !action.blockers.length;
    action.account = currentSampleAccount();
    action.state = current.state;
    if (action.kind === 'create-pr') {
      if (state.remoteBranches[proposal.pullRequest.head]) action.sourceHeadSha = proposal.pullRequest.sourceHeadSha;
      else delete action.sourceHeadSha;
    }
    if (run.task.target.type === 'pr') Object.assign(action, { headSha: current.headSha, draftPr: current.draftPr, ciState: current.ciState });
    Object.assign(action, webActionSummary(action));
    return duplicatePreview(action);
  }
  function prepareResultAction(payload) {
    if (Object.keys(payload).some(key => !['runId', 'proposalId', 'kind', 'attemptId', 'retry', 'content'].includes(key))) throw sampleError('INVALID_REQUEST', 'Prepare a sample result action using its saved source and optional edited Draft title and body.');
    if (payload.attemptId !== undefined && !/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(payload.attemptId)) throw new Error('The sample attempt ID must be a UUID.');
    if (payload.retry !== undefined && typeof payload.retry !== 'boolean') throw new Error('The sample retry flag must be a boolean when supplied.');
    if (payload.retry && !payload.attemptId) throw new Error('A deliberate sample retry requires a new attempt ID.');
    const run = getRun(payload.runId);
    const manual = payload.kind !== undefined;
    if (manual && (payload.proposalId !== undefined || !['merge-pr', 'trigger-ci'].includes(payload.kind))) throw new Error('Choose a manual Merge or CI action, without a model proposal ID.');
    const proposal = manual ? { kind: payload.kind, proposalId: `manual-${payload.kind}`, body: payload.kind === 'trigger-ci' ? '/azp run' : '' } : findProposal(run, payload.proposalId);
    if (!['create-pr', 'merge-pr', 'trigger-ci', 'close-as-duplicate'].includes(proposal.kind)) throw new Error('This sample proposal uses the existing editable action controls.');
    let content;
    if (payload.content !== undefined) {
      if (proposal.kind !== 'create-pr' || !payload.attemptId || !payload.content || typeof payload.content !== 'object' || Array.isArray(payload.content) ||
        Object.keys(payload.content).some(key => !['title', 'body'].includes(key)) || typeof payload.content.title !== 'string' || !payload.content.title.trim() || payload.content.title.length > 256 || payload.content.title.includes('\0') ||
        typeof payload.content.body !== 'string' || payload.content.body.length > 60000 || payload.content.body.includes('\0')) throw sampleError('INVALID_REQUEST', 'Edited Draft content requires a title, complete body, and explicit attempt ID. Its saved source and Draft status cannot be changed.');
      content = { title: payload.content.title.replaceAll('\r\n', '\n'), body: payload.content.body.replaceAll('\r\n', '\n') };
    }
    const contentFingerprint = content ? JSON.stringify([content.title, content.body]) : '';
    const requestKey = JSON.stringify([run.runId, proposal.proposalId, payload.attemptId || '']);
    const remembered = state.resultActionRequests[requestKey];
    if (remembered) {
      if (remembered.retry !== !!payload.retry || (remembered.contentFingerprint ?? '') !== contentFingerprint) throw sampleError('REQUEST_CONFLICT', 'This sample attempt ID already belongs to different content or retry intent.');
      return webActionSummary(getWebAction(remembered.operationId));
    }
    const remember = action => { state.resultActionRequests[requestKey] = { operationId: action.operationId, retry: !!payload.retry, contentFingerprint }; save(); return webActionSummary(action); };
    const associations = Object.values(state.resultActions).filter(item => item.runId === run.runId && item.proposalId === proposal.proposalId);
    const sameAttempt = payload.attemptId && associations.find(item => item.attemptId === payload.attemptId);
    if (sameAttempt) {
      const existing = getWebAction(sameAttempt.operationId);
      if ((sameAttempt.contentFingerprint ?? existing.resultContentFingerprint ?? '') !== contentFingerprint || !!existing.retry !== !!payload.retry) throw sampleError('REQUEST_CONFLICT', 'This sample attempt ID already belongs to different content or retry intent.');
      return remember(existing);
    }
    const unresolved = associations.map(item => getWebAction(item.operationId)).find(item => ['prepared', 'unknown', 'submitting', 'partial'].includes(item.status));
    if (unresolved) return remember(unresolved);
    const previous = associations.length ? getWebAction(associations.at(-1).operationId) : undefined;
    if (previous && (!payload.attemptId || !retryAllowed(previous) || previous.status === 'succeeded' && (proposal.kind !== 'trigger-ci' || payload.retry !== true))) return remember(previous);
    const blockers = manual ? manualPrBlockers(run, proposal.kind, false) : resultActionBlockers(run, proposal, false);
    if (blockers.length) throw new Error(blockers.map(item => `${item.message} [${item.code}]`).join(' '));
    if (proposal.kind === 'trigger-ci' && !['', '/azp run'].includes(proposal.body)) throw new Error('This sample CI proposal does not contain the supported fixed command.');
    if (['create-pr', 'merge-pr'].includes(proposal.kind) && proposal.body !== '') throw new Error('This sample proposal must use an empty top-level body.');
    if (proposal.kind === 'create-pr' && (!proposal.pullRequest || !['head', 'sourceHeadSha', 'base', 'title', 'body'].every(key => typeof proposal.pullRequest[key] === 'string') || !['head', 'base', 'title'].every(key => proposal.pullRequest[key].trim()) || typeof proposal.pullRequest.draft !== 'boolean')) throw new Error('This sample pull request proposal is incomplete.');
    const operationId = crypto.randomUUID();
    const target = { ...copy(run.task.target), repository: run.task.repository };
    const draft = { requestId: operationId, actionId: `${run.task.actionId}: ${proposal.kind}`, kind: proposal.kind, target, ...(target.type === 'pr' ? { expectedHeadSha: run.task.expectedHeadSha } : {}), ...(proposal.kind === 'create-pr' ? { pullRequest: { ...copy(proposal.pullRequest), ...content, draft: true } } : proposal.kind === 'trigger-ci' ? { body: '/azp run' } : proposal.kind === 'close-as-duplicate' ? { body: proposal.body, duplicateOf: copy(proposal.duplicateOf) } : {}) };
    const action = { operationId, requestId: operationId, actionId: draft.actionId, kind: proposal.kind, target, draft, status: 'prepared', completedSteps: [], remainingSteps: [], urls: [], createdAt: at(0), updatedAt: at(0), account: currentSampleAccount(), canSubmit: true, blockers: [], targetUrl: `https://github.com/${target.repository}/${target.type === 'pr' ? 'pull' : 'issues'}/${target.number}`, state: 'OPEN', sourceOrigin: `pulse-task://${run.runId}`, retry: !!payload.retry, ...(payload.retry && previous ? { previousCiBaseline: previous.ciBaseline } : {}) };
    state.webActions[operationId] = action;
    action.resultContentFingerprint = contentFingerprint;
    state.resultActions[operationId] = { runId: run.runId, proposalId: proposal.proposalId, operationId, attemptId: payload.attemptId || operationId, contentFingerprint, ...(manual ? { manualKind: proposal.kind } : {}) };
    return remember(action);
  }
  function getPreview(run) {
    if (!run.task.target) throw new Error('This sample task has no GitHub target.');
    const isPr = run.task.target.type === 'pr';
    const closed = (state.operations[run.runId] || []).some(op => op.kind === 'close' && op.status === 'succeeded');
    const url = `https://github.com/${run.task.repository}/${isPr ? 'pull' : 'issues'}/${run.task.target.number}`;
    const result = run.result;
    const isV2 = [2, 3].includes(result?.schemaVersion);
    const current = sampleTarget(run);
    const stale = isPr && run.task.expectedHeadSha !== current.headSha;
    const completed = !isV2 || (completedWorkflow(run) && !stale);
    const allowed = kind => isPr ? !manualPrBlockers(run, kind).length : result?.schemaVersion === 3 && ['comment', 'close'].includes(kind) ? !resultActionBlockers(run, { kind }).length : completed && (!isV2 || result.nextActions.some(item => item.kind === kind && !resultActionBlockers(run, item).length));
    return {
      account: currentSampleAccount(),
      target: { ...run.task.target, repository: run.task.repository, title: `${run.task.actionId} (sample target)`, url, state: closed ? 'CLOSED' : 'OPEN' },
      url, state: closed ? 'CLOSED' : 'OPEN',
      ...(isPr ? { headSha: current.headSha, expectedHeadSha: run.task.expectedHeadSha } : {}), stale,
      canApprove: isPr && !closed && allowed('approve'), canRequestChanges: isPr && !closed && allowed('requestChanges'), canSuggestChanges: isPr && !closed && allowed('suggestChanges'), canComment: allowed('comment'), canClose: !closed && allowed('close'),
      hasConfirmedP0: isPr && hasConfirmedP0(run),
      reviewSummary: reviewSummary(run), reviewDecisions: [],
      files: isPr ? [{
        path: suggestion.path, status: 'modified', lines: [
          { line: 146, kind: 'context', original: '// Normalize discovered programs before returning search results.', hunk: 0 },
          { line: 147, kind: 'add', original: 'var programs = discoveredPrograms', hunk: 0 },
          { line: 148, kind: 'add', original: '    .DistinctBy(program => program.FullPath);', hunk: 0 },
          { line: 149, kind: 'context', original: 'return programs.ToList();', hunk: 0 },
        ],
      }] : [],
      reasons: ['UI sample: the account, target, permissions, and SHA are examples. Submissions are saved only in this browser session.', ...(isPr ? manualPrBlockers(run, 'approve').map(item => item.message) : isV2 && !completed ? ['Inspect this sample workflow and its recorded evidence before preparing an Issue action.'] : [])],
    };
  }
  function handle(type, payload = {}) {
    switch (type) {
      case 'connection.status': return { state: 'connected', lastConnectedAt: at(0) };
      case 'hello': return { hostVersion: '0.1.0 · Sample task UI', protocolVersion: 1, reviewModes: ['static', 'build-tests', 'ui-e2e'], workflowKinds: ['pr-review', 'issue-fix', 'reproduction-setup', 'e2e', 'feature-research', 'bug-investigation'], resultSchemaVersions: [2, 3], agents: { codex: { available: false }, copilot: { available: false } }, github: { available: false } };
      case 'agents.defaults': return { defaultAgent: state.config.agent, defaults: state.config.agentDefaults, reasoningEfforts };
      case 'agents.list': return { installations: cliInstallations, selections: state.config.cliSelections };
      case 'config.get': return state.config;
      case 'config.save': state.config = copy({ ...payload, agentDefaults: payload.agentDefaults ?? state.config.agentDefaults, cliSelections: payload.cliSelections ?? state.config.cliSelections }); save(); return state.config;
      case 'targets.get': {
        const run = state.runs.find(item => item.task.target?.type === payload.target?.type && item.task.target?.number === payload.target?.number);
        if (!run) throw new Error('The sample GitHub target is not available.');
        return { target: copy(run.task.target), headSha: sampleTarget(run).headSha, title: `${run.task.actionId} (sample target)` };
      }
      case 'tasks.list': {
        if (payload.order !== undefined && (payload.order !== 'finished' || payload.view !== 'history')) throw new Error('Completion ordering is only available for the sample history view.');
        const runs = state.runs.map(run => getRun(run.runId)).filter(run => ['tasks', 'running'].includes(payload.view) ? active(run) : payload.view === 'prs' ? !active(run) && run.task.target?.type === 'pr' : payload.view === 'issues' ? !active(run) && run.task.target?.type === 'issue' : payload.view === 'pending' ? !active(run) && !run.view.handled : payload.view === 'history' ? !active(run) : true);
        runs.sort((first, second) => {
          const time = run => payload.order === 'finished' ? run.status.endedAt || run.status.updatedAt || run.status.createdAt : run.status.createdAt;
          return time(second).localeCompare(time(first)) || second.runId.localeCompare(first.runId);
        });
        const start = Math.max(0, Number(payload.cursor) || 0);
        const limit = Math.max(1, Math.min(100, Number(payload.limit) || 50));
        return Promise.all(runs.slice(start, start + limit).map(projectedRun)).then(page => ({ runs: page, nextCursor: start + limit < runs.length ? String(start + limit) : null, runningCount: state.runs.filter(active).length, unreadCount: state.runs.filter(run => !active(run) && !run.view.read).length, prCount: state.runs.filter(run => !active(run) && run.task.target?.type === 'pr').length, issueCount: state.runs.filter(run => !active(run) && run.task.target?.type === 'issue').length }));
      }
      case 'tasks.get': return projectedRun(getRun(payload.runId));
      case 'tasks.resultPage': return resultPage(payload);
      case 'tasks.startFromResult': return startResultTask(payload);
      case 'reviews.related': return relatedReviews(payload.runId);
      case 'reviews.verify': return verifyReview(payload);
      case 'tasks.logs': {
        const run = getRun(payload.runId);
        if (!['stdout', 'stderr'].includes(payload.stream)) throw new Error('Choose stdout or stderr.');
        const sample = payload.stream === 'stdout'
          ? `[SAMPLE OUTPUT — no real agent process]\nAgent: ${run.config.agent}\nReading task context for ${run.task.repository}…\n${run.status.latestProgress}\n${run.result?.summary || 'Waiting for the next sample event.'}\n`
          : run.status.error ? `[SAMPLE STDERR]\n${run.status.error.message}\n` : '[SAMPLE STDERR]\nNo errors in this sample run.\n';
        const bytes = new TextEncoder().encode(sample);
        const cursor = Math.max(0, Math.min(bytes.length, Number(payload.cursor) || 0));
        let end = Math.min(bytes.length, cursor + Math.max(4, Math.min(65536, Number(payload.limitBytes) || 65536)));
        while (end < bytes.length && (bytes[end] & 0xc0) === 0x80) end--;
        return { stream: payload.stream, text: new TextDecoder().decode(bytes.slice(cursor, end)), nextCursor: end, eof: end === bytes.length, truncated: false, sample: true };
      }
      case 'tasks.events': {
        const run = getRun(payload.runId);
        const texts = ['Sample task accepted. Loading the configuration snapshot.', `Agent: ${run.config.agent}; repository: ${run.task.repository}`, 'Inspected relevant source code and change context.', run.status.latestProgress, active(run) ? 'Waiting for output… (static UI sample; no CLI is running)' : 'Sample run finished. Structured results are available.'];
        const all = texts.map((text, index) => ({ sequence: index + 1, time: run.status.updatedAt, type: index === 4 ? 'status' : 'progress', text }));
        const events = all.filter(event => event.sequence > (Number(payload.afterSequence) || 0)).slice(0, Number(payload.limit) || 100);
        return { events, nextSequence: events.at(-1)?.sequence ?? (Number(payload.afterSequence) || 0), truncated: false };
      }
      case 'tasks.read': case 'tasks.handle': {
        const run = getRun(payload.runId); run.view[type === 'tasks.read' ? 'read' : 'handled'] = payload.value ?? true; save(); return run;
      }
      case 'tasks.cancel': {
        const run = getRun(payload.runId); if (!active(run)) throw new Error('Only active sample tasks can be cancelled.');
        run.status.state = 'cancelled'; run.status.updatedAt = run.status.endedAt = new Date().toISOString(); run.status.latestProgress = 'Sample task cancelled. No real CLI was running.'; save(); return run;
      }
      case 'tasks.rerun': {
        const original = getRun(payload.runId);
        const existing = state.runs.find(run => run.task.requestId === payload.requestId); if (existing) return existing;
        const run = copy(original); run.runId = `demo-rerun-${crypto.randomUUID()}`; run.task.requestId = payload.requestId || crypto.randomUUID();
        if (payload.execution === null) delete run.task.execution;
        else if (payload.execution !== undefined) run.task.execution = copy(payload.execution);
        if (payload.reviewOptions !== undefined) {
          const options = payload.reviewOptions;
          if (!['pr-review', 'pr-verify'].includes(run.task.actionKind) || !options || typeof options !== 'object' || Array.isArray(options) || Object.keys(options).some(key => key !== 'mode') || !['static', 'build-tests', 'ui-e2e'].includes(options.mode) || run.task.actionKind === 'pr-verify' && options.mode === 'static') throw sampleError('INVALID_REQUEST', 'Choose a valid explicit scope for the sample PR review or supplemental verification.');
          run.task.reviewOptions = copy(options);
        }
        if (payload.expectedHeadSha !== undefined) {
          if (run.task.target?.type !== 'pr' || !/^[a-f0-9]{40}$/i.test(payload.expectedHeadSha)) throw new Error('Choose an explicit 40-character PR SHA for the sample rerun.');
          run.task.expectedHeadSha = payload.expectedHeadSha;
        }
        const execution = resolveExecution(state.config, run.task.execution);
        const installation = cliProbe(execution.agent, state.config.cliSelections[execution.agent]);
        if (!installation) throw new Error('The selected sample CLI path was not found. Choose a detected installation in Settings.');
        run.config = { ...execution, cliPath: installation.resolvedPath || installation.path, permission: state.config.permission, mainRepoFolder: state.config.mainRepoFolder, worktreeRoot: state.config.worktreeRoot, repoFolder: `${state.config.worktreeRoot}\\pulse-${run.runId}`, worktreeBranch: `codex/pulse-${run.runId}` };
        delete run.result; delete run.reviewSummary; delete run.provenance; run.status = { state: 'running', createdAt: at(0), startedAt: at(0), updatedAt: at(0), sequence: 5, latestProgress: 'Created a new sample run. Loading context…' }; run.view = { read: false, handled: false };
        state.runs.unshift(run); save(); return run;
      }
      case 'tasks.delete': {
        const run = getRun(payload.runId); if (active(run)) throw new Error('Cancel the active sample task first.');
        state.runs = state.runs.filter(item => item.runId !== run.runId); delete state.operations[run.runId];
        for (const [key, item] of Object.entries(state.resultActions)) if (item.runId === run.runId) { delete state.resultActions[key]; delete state.webActions[item.operationId]; }
        for (const [key, item] of Object.entries(state.resultActionRequests)) if (!state.webActions[item.operationId]) delete state.resultActionRequests[key];
        save(); return { deleted: true };
      }
      case 'operations.preview': return getPreview(getRun(payload.runId));
      case 'operations.list': {
        getRun(payload.runId); const operations = state.operations[payload.runId] || []; return { operations, totalCount: operations.length, truncated: false };
      }
      case 'operations.submit': {
        const run = getRun(payload.runId); const preview = getPreview(run);
        if (payload.reviewDecisionId !== undefined || payload.acknowledgedLimitations !== undefined) throw sampleError('INVALID_REQUEST', 'Choose a fixed manual PR operation. Historical review limitations do not require a separate approval state.');
        const records = state.operations[run.runId] ||= [];
        const existing = records.find(op => op.operationId === payload.operationId); if (existing) return existing;
        if (payload.reviewDecisionId !== undefined) {
          if (payload.proposalId !== undefined || payload.suggestions !== undefined) throw new Error('A sample human review decision cannot include a model proposal or inline suggestions.');
          const decision = preview.reviewDecisions.find(item => item.id === payload.reviewDecisionId);
          if (!decision || !decision.enabled) throw new Error(decision?.reasons.join(' ') || 'This sample human review decision is unavailable.');
          if (payload.kind !== (decision.kind === 'request-evidence' ? 'comment' : 'approve')) throw new Error('The sample decision does not match the requested GitHub operation.');
          if (typeof payload.acknowledgedLimitations !== 'boolean' || decision.requiresAcknowledgement && !payload.acknowledgedLimitations) throw new Error('Explicitly acknowledge every sample validation limitation before approving.');
          if (payload.expectedAccount !== preview.account) throw new Error('The sample account changed. Reload the target.');
          if (payload.expectedHeadSha !== preview.headSha) throw new Error('The sample SHA does not match. Reload the target.');
          if (decision.kind === 'request-evidence' && !String(payload.body || '').trim()) throw new Error('Enter the sample validation-evidence request.');
          const body = `${payload.body || ''}${decision.disclosure ? `${payload.body ? '\n\n' : ''}${decision.disclosure}` : ''}`;
          const operation = { operationId: payload.operationId || crypto.randomUUID(), kind: payload.kind, status: 'succeeded', account: preview.account, body, expectedHeadSha: payload.expectedHeadSha, suggestions: [], reviewDecision: { id: decision.id, kind: decision.kind, limitations: copy(decision.limitations), disclosure: decision.disclosure, acknowledgedLimitations: payload.acknowledgedLimitations, headSha: preview.headSha }, remoteId: 'preview-only', createdAt: new Date().toISOString(), submittedAt: new Date().toISOString() };
          records.unshift(operation); save(); return operation;
        }
        if (payload.acknowledgedLimitations !== undefined) throw new Error('A sample limitations acknowledgment must belong to a human review decision.');
        const allowed = { approve: preview.canApprove, requestChanges: preview.canRequestChanges, suggestChanges: preview.canSuggestChanges, comment: preview.canComment, close: preview.canClose };
        if (!allowed[payload.kind]) throw new Error('This action is unavailable for the sample target. Reload the target.');
        if (payload.proposalId !== undefined && findProposal(run, payload.proposalId).kind !== payload.kind) throw new Error('The selected sample proposal does not match this action kind.');
        if (payload.proposalId !== undefined) {
          const proposal = findProposal(run, payload.proposalId);
          if (proposal.suggestionIds && (payload.suggestions || []).some(item => !proposal.suggestionIds.includes(item.id))) throw new Error('A selected inline suggestion belongs to another sample proposal.');
        }
        if (payload.findingIds !== undefined) {
          if (!Array.isArray(payload.findingIds) || payload.findingIds.length > 200 || new Set(payload.findingIds).size !== payload.findingIds.length || payload.findingIds.some(id => typeof id !== 'string')) throw sampleError('INVALID_REQUEST', 'Select each saved sample finding at most once, within the submission limit.');
          const findings = payload.findingIds.map(id => run.result?.schemaVersion === 3 && run.result.findings.find(row => row.id === id && row.confirmed));
          if (findings.some(row => !row)) throw sampleError('RESULT_FINDING_NOT_FOUND', 'Every selected finding must be a confirmed finding in the saved v3 report.');
          const allowedSuggestions = new Set(findings.map(row => row.feedback.suggestionId).filter(Boolean));
          for (const selected of payload.suggestions ?? []) {
            const source = run.result.review?.suggestions.find(row => row.id === selected.id);
            if (!allowedSuggestions.has(selected.id) || !source || run.result.review.headSha !== run.task.expectedHeadSha || ['path', 'line', 'side'].some(key => source[key] !== selected[key]) || (source.startLine ?? source.line) !== (selected.startLine ?? selected.line)) throw sampleError('INVALID_SUGGESTION', 'The selected code suggestion must belong to a selected finding at its saved source location.');
          }
        }
        if (payload.expectedAccount !== preview.account) throw new Error('The sample account changed. Reload the target.');
        if (['approve', 'requestChanges', 'suggestChanges'].includes(payload.kind) && payload.expectedHeadSha !== preview.headSha) throw new Error('The sample SHA does not match. Reload the target.');
        if (!['approve', 'close'].includes(payload.kind) && !String(payload.body || '').trim() && !(run.task.target.type === 'pr' && ['comment', 'requestChanges', 'suggestChanges'].includes(payload.kind) && payload.suggestions?.length)) throw new Error('Enter the sample submission body or select a valid inline comment.');
        if (payload.kind === 'close' && (typeof payload.closeReason !== 'string' || !payload.closeReason.trim() || String(payload.body || '').length)) throw sampleError('CLOSE_REASON_REQUIRED', 'Provide an explicit close reason. Closing does not publish a comment body.');
        if (payload.kind === 'suggestChanges' && !payload.suggestions?.length) throw new Error('Select at least one sample inline suggestion.');
        const operation = { operationId: payload.operationId || crypto.randomUUID(), kind: payload.kind, status: 'succeeded', account: preview.account, body: payload.body || '', ...(payload.closeReason ? { closeReason: payload.closeReason } : {}), ...(payload.proposalId ? { proposalId: payload.proposalId } : {}), ...(payload.findingIds ? { findingIds: copy(payload.findingIds) } : {}), ...(payload.expectedHeadSha ? { expectedHeadSha: payload.expectedHeadSha } : {}), suggestions: copy(payload.suggestions || []), remoteId: 'preview-only', createdAt: new Date().toISOString(), submittedAt: new Date().toISOString() };
        records.unshift(operation); save(); return operation;
      }
      case 'operations.reconcile': {
        getRun(payload.runId); const operation = (state.operations[payload.runId] || []).find(op => op.operationId === payload.operationId);
        if (!operation) throw new Error('Sample operation record not found.'); return operation;
      }
      case 'resultActions.prepare': return prepareResultAction(payload);
      case 'resultActions.list': {
        getRun(payload.runId);
        const all = Object.values(state.resultActions).filter(item => item.runId === payload.runId).map(item => webActionSummary(getWebAction(item.operationId))).reverse();
        return { operations: all.slice(0, 100), totalCount: all.length, truncated: all.length > 100 };
      }
      case 'webActions.preview': return refreshResultAction(getWebAction(payload.operationId));
      case 'webActions.get': return webActionSummary(getWebAction(payload.operationId));
      case 'webActions.reconcile': {
        const action = getWebAction(payload.operationId);
        if (action.status !== 'unknown') return webActionSummary(action);
        if (action.kind === 'close-as-duplicate' && action.operationId === 'dddddddd-dddd-4ddd-8ddd-dddddddddddd') {
          action.status = 'partial'; action.completedSteps = ['duplicate-comment']; action.remainingSteps = ['close-issue'];
          action.error = { code: 'PARTIAL_ACTION', message: 'Sample: the original association comment is now confirmed; the Issue has not been closed.', guidance: 'Continue only the remaining close step after explicit confirmation.' };
        } else if (action.operationId === '99999999-9999-4999-8999-999999999999') {
          action.status = 'succeeded'; action.completedSteps = ['comment']; action.remainingSteps = []; action.urls = [action.targetUrl]; delete action.error;
        } else if (action.operationId === 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa') {
          action.status = 'partial'; action.completedSteps = ['review']; action.remainingSteps = ['general-comment-1']; action.urls = [action.targetUrl];
          action.error = { code: 'PARTIAL_ACTION', message: 'Sample: the pending review is confirmed; the general comment was not submitted.', guidance: 'Review the completed results before preparing a new draft containing only the remaining work.' };
        } else {
          action.error = { code: 'OPERATION_UNKNOWN', message: 'Sample: GitHub does not provide a unique match for this pending write.', guidance: 'A missing or ambiguous match does not prove the write failed. Inspect GitHub before preparing another action.' };
        }
        action.canSubmit = false; action.updatedAt = new Date().toISOString(); save(); return webActionSummary(action);
      }
      case 'webActions.submit': {
        const action = getWebAction(payload.operationId);
        const latest = refreshResultAction(action);
        if (action.kind === 'close-as-duplicate') {
          if (Object.keys(payload).some(key => !['operationId', 'expectedAccount', 'body', 'resume'].includes(key))) throw sampleError('INVALID_REQUEST', 'The original duplicate target cannot be changed through an edited explanation.');
          if (payload.body !== undefined && action.confirmedBody !== undefined && payload.body !== action.confirmedBody) throw sampleError('REQUEST_CONFLICT', 'The sample explanation was already confirmed and cannot be replaced. Continuation does not post another comment.');
          if (action.status !== 'prepared' && !(duplicateResume(action) && payload.resume === true)) return webActionSummary(action);
          if (payload.resume && !duplicateResume(action)) throw sampleError('INVALID_REQUEST', 'Only a confirmed association can continue with the remaining close step.');
          if (!latest.canSubmit || latest.blockers.length) throw new Error('This sample duplicate action is blocked.');
          if (payload.expectedAccount !== latest.account) throw new Error('The sample GitHub account changed. Refresh the status.');
          const body = payload.body ?? action.confirmedBody ?? action.draft.body;
          if (typeof body !== 'string' || !body.trim()) throw sampleError('BODY_REQUIRED', 'Explain why this sample Issue duplicates the fixed original Issue.');
          action.confirmedBody ??= body;
          if (!action.completedSteps.includes('duplicate-comment')) action.completedSteps.push('duplicate-comment');
          if (!action.completedSteps.includes('close-issue')) action.completedSteps.push('close-issue');
          action.status = 'succeeded'; action.canSubmit = false; action.remainingSteps = []; action.urls = [action.targetUrl]; delete action.error;
          state.remoteTargets[`${action.target.repository}:issue:${action.target.number}`] = { ...state.remoteTargets[`${action.target.repository}:issue:${action.target.number}`], state: 'CLOSED' };
          action.updatedAt = new Date().toISOString(); save(); return webActionSummary(action);
        }
        if (payload.body !== undefined || payload.resume !== undefined) throw sampleError('INVALID_REQUEST', 'Only duplicate closure accepts an edited explanation or continuation.');
        if (action.status !== 'prepared') return webActionSummary(action);
        if (!action.canSubmit || action.blockers.length) throw new Error('This sample action is blocked.');
        if (payload.expectedAccount !== action.account) throw new Error('The sample GitHub account changed. Refresh the status.');
        if (action.kind === 'trigger-ci' && state.resultActions[action.operationId]) action.ciBaseline = sampleTarget(getRun(state.resultActions[action.operationId].runId)).ciEvidence;
        action.status = 'succeeded'; action.canSubmit = false; action.completedSteps = ['Simulated GitHub action · saved only in this browser session']; action.urls = [action.targetUrl]; action.updatedAt = new Date().toISOString();
        save(); return webActionSummary(action);
      }
      case 'webActions.cancel': {
        const action = getWebAction(payload.operationId);
        if (action.status !== 'prepared') throw new Error('Only an unsubmitted sample action can be cancelled.');
        action.status = 'cancelled'; action.canSubmit = false; action.updatedAt = new Date().toISOString(); save(); return webActionSummary(action);
      }
      default: throw new Error(`This UI preview does not support: ${type}`);
    }
  }
  const localUrl = path => { const url = new URL(path, `${location.origin}/`); if (diagnosticsEnabled && url.pathname.endsWith('.html')) url.searchParams.set('diagnostics', '1'); return url.href; };
  const navigationUrl = options => { const url = new URL(typeof options === 'string' ? options : options.url, location.href); if (url.origin !== location.origin) throw new Error('The preview only supports navigation to local pages.'); return url.href; };
  const navigate = options => { const url = navigationUrl(options); location.href = url; return Promise.resolve({ id: 1, url }); };
  const openPreviewTab = async options => {
    const url = navigationUrl(options);
    const opened = window.open(url, '_blank');
    if (!opened) throw new Error('The browser blocked the preview tab. Allow the requested local tab and try again.');
    opened.opener = null;
    return { id: 1, url };
  };
  const api = window.chrome || {};
  const runtime = {
    id: 'pulse-local-ui-preview',
    getURL: localUrl,
    openOptionsPage: () => navigate(localUrl('options.html')),
    sendMessage: async message => {
      await initializeReviewLinks();
      if (message.type === 'agents.test.start' && cliProbe(message.payload?.agent, message.payload?.cliPath)) {
        const installation = cliProbe(message.payload.agent, message.payload.cliPath);
        return { id: 'preview-sample-test', protocolVersion: 1, ok: true, data: { testId: crypto.randomUUID(), agent: message.payload.agent, state: 'failed', path: message.payload.cliPath, version: installation.version, error: { code: 'PREVIEW_SAMPLE_INSTALLATION', message: 'This sample installation cannot run a real CLI test.', guidance: 'No model was called. Use the installed extension to test a real local installation.' } } };
      }
      if (diagnosticMethods.has(message.type)) {
        if (!diagnosticsEnabled) return { id: 'preview-diagnostic', protocolVersion: 1, ok: false, error: { code: 'PREVIEW_FIXTURE_ONLY', message: 'Local Host diagnostics are disabled in this UI fixture.', guidance: 'This preview uses session data only. No Host, GitHub, or model request was made.' } };
        try {
          const response = await fetch('/__pulse/diagnostics', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ type: message.type, payload: message.payload || {} }) });
          if (!response.ok) throw new Error(`The local diagnostics service returned HTTP ${response.status}。`);
          const reply = await response.json();
          if (reply.protocolVersion !== 1 || typeof reply.ok !== 'boolean') throw new Error('The local diagnostics service returned an invalid response.');
          return reply;
        } catch (error) {
          return { id: 'preview-diagnostic', protocolVersion: 1, ok: false, error: { code: 'PREVIEW_DIAGNOSTICS_UNAVAILABLE', message: error instanceof Error ? error.message : 'Cannot connect to the local diagnostics service.', guidance: 'Check that the local diagnostic Host is running, then retry. No sample diagnostic result was substituted.' } };
        }
      }
      try { return { id: 'preview', protocolVersion: 1, ok: true, data: copy(await handle(message.type, message.payload)) }; }
      catch (error) { return { id: 'preview', protocolVersion: 1, ok: false, error: { code: typeof error?.code === 'string' ? error.code : 'PREVIEW_ERROR', message: error instanceof Error ? error.message : 'The sample action failed.', guidance: 'Sample actions only affect this browser session.' } }; }
    },
  };
  Object.defineProperty(api, 'runtime', { value: runtime, configurable: true });
  Object.defineProperty(api, 'tabs', { value: { create: openPreviewTab }, configurable: true });
  if (!window.chrome) Object.defineProperty(window, 'chrome', { value: api, configurable: true });

  function decorate() {
    const banner = document.createElement('div');
    banner.id = 'preview-banner'; banner.setAttribute('role', 'note');
    banner.style.cssText = 'display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:8px 16px;padding:9px 22px;border-bottom:1px solid #c8dce9;background:#eaf5fc;color:#185477;font:12px/1.5 Segoe UI,Microsoft YaHei,sans-serif;';
    const fixtureDescription = diagnosticsEnabled ? 'UI fixtures · Tasks and GitHub actions use session data; explicit diagnostics use the local Host.' : 'UI fixtures · Session data only · Host, models, and GitHub writes disabled.';
    const label = document.createElement('span'); label.textContent = fixtureDescription; label.title = 'This is the product source UI with sample data. It does not establish an installed-extension, Native Messaging, PowerToys runtime, or GitHub integration result.';
    const cases = document.createElement('select'); cases.id = 'preview-case'; cases.setAttribute('aria-label', 'Choose a UI fixture'); cases.style.cssText = 'max-width:100%;font:inherit;padding:4px 6px;color:inherit;background:white;border:1px solid #b6c9d6;border-radius:6px;';
    for (const [title, path] of [['Choose a UI fixture…', ''], ['PR · Confirmed findings', 'details.html?runId=demo-v3-pr-p1-required'], ['PR · No recommended action', 'details.html?runId=demo-v3-pr-not-needed'], ['PR · Verification prerequisites missing', 'details.html?runId=demo-v3-pr-required'], ['PR · Verification ready to prepare', 'details.html?runId=demo-v3-pr-verification-ready'], ['Issue · Create Draft PR', 'details.html?runId=demo-v3-issue-candidate'], ['Issue · More information needed', 'details.html?runId=demo-v3-feature-needs_information'], ['Result · Incomplete analysis', 'details.html?runId=demo-v3-pr-incomplete'], ['Result · Report-page failure, then reload', 'details.html?runId=demo-v3-pr-page-error'], ['Result · Unconfirmed GitHub action', 'details.html?runId=demo-v3-pr-unknown-operation'], ['Historical · Saved original review', 'details.html?runId=demo-pending-review'], ['Tasks', 'popup.html?expanded=1'], ['Settings', 'options.html']]) { const option = document.createElement('option'); option.value = path; option.textContent = title; cases.append(option); }
    cases.addEventListener('change', () => { if (cases.value) { if (cases.value.includes('demo-v3-pr-page-error')) { state.pageFailures ??= {}; state.pageFailures['demo-v3-pr-page-error'] = true; save(); } location.href = localUrl(cases.value); } });
    const reset = document.createElement('button'); reset.type = 'button'; reset.textContent = 'Reset samples'; reset.style.cssText = 'border:0;background:transparent;padding:0;color:inherit;font:inherit;text-decoration:underline;cursor:pointer;';
    reset.addEventListener('click', () => { for (let index = sessionStorage.length - 1; index >= 0; index--) { const key = sessionStorage.key(index); if (key?.startsWith('pulse.result-draft.v1:')) sessionStorage.removeItem(key); } state = seed(); reviewInitialization = undefined; save(); location.href = localUrl('popup.html?expanded=1'); });
    banner.append(label, cases, reset); if (!intrinsicPopupFixture) document.body.prepend(banner);
    function clarify() {
      const connection = document.getElementById('connection');
      const connectionLabel = connection?.dataset.compact === 'true' ? 'UI sample' : fixtureDescription;
      if (connection && connection.textContent !== connectionLabel) connection.textContent = connectionLabel;
      if (connection) connection.setAttribute('aria-label', fixtureDescription);
      const saved = document.getElementById('saved');
      if (saved && !saved.hidden && saved.textContent !== 'Sample settings saved in this browser session.') saved.textContent = 'Sample settings saved in this browser session.';
      const notice = document.getElementById('notice');
      if (notice && !notice.hidden && !notice.textContent.startsWith('[UI sample] ')) notice.textContent = `[UI sample] ${notice.textContent} (session data only)`;
      for (const id of ['submit-operation', 'confirm-action']) {
        const submit = document.getElementById(id);
        if (submit && !submit.textContent.startsWith('Simulate · ')) submit.textContent = `Simulate · ${submit.textContent}`;
      }
      const actionAccount = document.getElementById('action-confirm-account');
      if (actionAccount?.textContent.startsWith('Confirming will execute')) actionAccount.textContent = 'This sample confirmation is saved only in this browser session. Nothing is submitted to GitHub.';
    }
    clarify();
    new MutationObserver(clarify).observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['hidden'] });
    document.addEventListener('click', event => {
      const anchor = event.target.closest?.('a[href]');
      if (anchor?.href === 'https://github.com/MuyuanMS/powertoys-pulse-actions/tree/main/.github/prompts') return;
      if (anchor && new URL(anchor.href).origin !== location.origin) { event.preventDefault(); label.textContent = 'UI preview · Sample target link. GitHub was not opened.'; }
    }, true);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', decorate, { once: true });
  else decorate();
})();
