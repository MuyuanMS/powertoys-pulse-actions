import test from 'node:test';
import assert from 'node:assert/strict';
import { changedSettings, isConfig, saveOutcomeUnconfirmed, settingsErrorField } from '../src/setup-model.ts';

const config = () => ({ agent: 'codex', permission: 'workspace-write', cliSelections: { codex: 'C:\\Tools\\codex.exe', copilot: '' }, agentDefaults: { codex: { model: '', reasoningEffort: '' }, copilot: { model: '', reasoningEffort: '' } }, mainRepoFolder: 'C:\\Source\\PowerToys', worktreeRoot: 'C:\\Source\\Worktrees', githubAccount: 'Reviewer', prPrompt: 'review.md', issuePrompt: 'fix.md', e2ePrompt: 'e2e.md', reproductionPrompt: '' });

test('saved comparison normalizes Windows paths and account case while preserving meaningful model and permission edits', () => {
  const saved = config();
  const draft = structuredClone(saved);
  draft.mainRepoFolder = ' c:/source/powertoys/ ';
  draft.cliSelections.codex = 'c:/tools/codex.exe';
  draft.githubAccount = 'reviewer';
  assert.deepEqual(changedSettings(saved, draft), []);
  draft.agentDefaults.codex.model = 'different-model'; draft.permission = 'read-only';
  assert.deepEqual(changedSettings(saved, draft), ['Codex model', 'Task access']);
  assert.equal(isConfig({}), false);
  assert.equal(isConfig(saved), true);
});

test('unknown transport outcomes require readback; rejected configuration errors are actionable without a blind retry', () => {
  for (const code of ['HOST_DISCONNECTED', 'HOST_UNAVAILABLE', 'RESPONSE_UNKNOWN', 'PROTOCOL_MISMATCH']) assert.equal(saveOutcomeUnconfirmed({ code }), true);
  assert.equal(saveOutcomeUnconfirmed(new Error('Port closed')), true);
  for (const code of ['REPO_PATH_INVALID', 'GITHUB_ACCOUNT_UNAVAILABLE', 'INVALID_EXECUTION', 'INVALID_CONFIG']) assert.equal(saveOutcomeUnconfirmed({ code }), false);
  assert.equal(settingsErrorField('WORKTREE_PATH_INVALID'), 'worktree-root');
  assert.equal(settingsErrorField('REPOSITORY_MISMATCH'), 'main-repo-folder');
  assert.equal(settingsErrorField('GITHUB_ACCOUNT_UNAVAILABLE'), 'github-account');
  assert.equal(settingsErrorField('INVALID_CONFIG', 'mainRepoFolder must contain a valid folder path.'), 'main-repo-folder');
});
