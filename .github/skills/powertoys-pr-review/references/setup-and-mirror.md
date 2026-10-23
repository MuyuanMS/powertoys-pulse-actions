# Setup & Mirror (Steps 0–2c)

The mirroring engine. In **self-check mode** skip Steps 1–2c — you already own the branch; build and review in the author's own worktree.

## Step 0: Resume check

A review may have been interrupted partway through. **Before mirroring, detect whether this skill already ran on PR `N` and pick up from where it stopped** instead of starting over (re-mirroring would clobber commits/replies from the prior run). Skip this step in self-check mode.

Run the bundled read-only discovery first:

Before acting on that discovery, check the original upstream PR with
`Get-UpstreamPrReviewGate.ps1 -PRNumber N -HeadSha <saved-upstream-head>`.
For fresh work, read and pin the original head first. Closed, merged or draft
PRs stop; a changed head is reconciled before continuing the prior fork loop.
This gate applies in self-check mode too. See the per-round lifecycle/head
contract in `copilot-review-loop.md`; preserve consumed request counts.

```powershell
./scripts/Get-ReviewResumeState.ps1 -PRNumber N -AsJson
```

For a batch, pass every number in one call. Discovery is account-independent: it resolves the authenticated teammate's own PowerToys repository and finds durable `pr-iterate/<number>` branches, review PRs, local worktrees, Copilot review timestamps, commits, and unresolved threads. Follow its `resumeAction`; do not re-mirror an existing branch.

For a saved outstanding request, also run `Get-CopilotReviewStatus.ps1` with its
fork PR, fork `HeadSha`, original `RequestedAt` and optional `AfterReviewId`.
This read-only check traverses all review pages. Consume an arrived result
before changing the branch or requesting again; an empty assignment is not
proof of absence. See [the review loop](./copilot-review-loop.md#step-4-request-copilot-review).

**0a. Detect prior artifacts** for PR `N`:

```powershell
# Fork PR mirrored for this PR? (Step 1 creates a "[PR N] ..." PR in the fork)
gh pr list --repo <FORK_REPO> --state all --search "[PR N] in:title" --json number,title,state,headRefName,url

# Fork branch pushed? (Step 1 pushes pr-iterate/N to the fork)
gh api "repos/<FORK_REPO>/git/ref/heads/pr-iterate/N" --jq '.ref' 2>$null

# Local worktree already created for the branch? (Step 2)
git -C <CLONE_PATH> worktree list | Select-String "pr-iterate/N"
```

If a fork PR exists, use [scripts/Get-UnresolvedCopilotThreads.ps1](../scripts/Get-UnresolvedCopilotThreads.ps1) to count all unresolved Copilot threads. Zero is necessary but insufficient: require a fresh submitted review on the exact fork head, inspect that review's findings, and validate the build.

**0b. Classify the prior state and jump** (state it to the user first, e.g. "Found an existing fork PR 123 for PR N with 2 unresolved Copilot threads — resuming the review loop"):

| Prior state | Evidence | Resume at |
| --- | --- | --- |
| **None** | No `[PR N]` fork PR and no `pr-iterate/N` fork branch | Step 1 (fresh mirror) |
| **Branch pushed, no fork PR** | `pr-iterate/N` ref exists, no `[PR N]` PR | Step 1 (create the fork PR from the existing branch) |
| **Fork PR exists, no local worktree** | `[PR N]` PR exists, `git worktree list` has no `pr-iterate/N` | Step 2 (create worktree), then re-sync per Step 2b and verify scope |
| **Worktree exists, no validation plan** | Worktree is present but the checkpoint has no saved focused commands | Step 2b (re-sync) → Step 2c (verify scope) → Step 3 (derive and save the plan) |
| **Review request pending or loop unfinished** | A saved Copilot request is pending, there are unresolved threads, or newest commit post-dates newest Copilot review | Step 4 (resume/check the saved request) or Step 5/6 (continue the loop); do not build while waiting |
| **Loop clean, not yet built** | Fresh zero-comment review, zero unresolved threads, no passing build for the converged head | Step 7 (run the saved plan under the build lock) |
| **Loop clean and built** | Fresh zero-comment review, zero unresolved threads, passing build for the exact converged head | Step 8–9 (summarize + draft the review) |

Across sessions, verify exact-tree validation receipts and retained logs before
drafting; reuse unchanged passing inputs per `build-and-test.md`, otherwise
rebuild. Build claims and old dashboard payloads alone are not durable evidence.
Do not build an obsolete pre-review head just to populate the checkpoint.
Preserve the validation plan and code/review traces, then regenerate
schema-version-2 `review-data.json` from the current head.

**0c. Re-sync before continuing.** Whenever you resume onto an existing branch/worktree, first bring the fork's `main` and the PR branch up to date (Step 2 sync + Step 2b rebase) so you do not build or review against stale `main`. If a rebase conflicts and you are not confident, stop and ask rather than guessing.

For teammates migrating from an older skill version, follow [migration-and-resume.md](./migration-and-resume.md). Never publish an old decisions file; regenerate and reapprove the validated public payload.

### Recovery when fork main cannot be safely synchronized

If fork `main` has private/divergent history or policy forbids changing it,
do not turn base synchronization into a permanent operator-approval loop.
The normal-main instructions below apply only when that synchronization is
safe and authorized. Otherwise, use isolated review feature branches with
an exact upstream base, leaving remote main and dirty shared worktrees intact.

Under the operator's fork-work authorization, fetch the pinned upstream base
and head, preserve the existing fork branch, create a uniquely named
`review-base/<N>-<sha>` branch at the upstream base and a corresponding
review head in a clean worktree. Carry forward only adjudicated review fixes;
do not replace existing history or transfer old-head validation. Push these
feature refs normally, without force, and reuse an existing compatible review
PR or create one isolated recovery PR when necessary. Sanitize references.
Verify the fork base/head and changed-file scope before requesting review.
Avoid changing the base and head of a live PR in separate steps that briefly
expose a broad unrelated diff to automation. Record the superseded lane and
request identities so subsequent resumes do not duplicate it. Ask only for
genuine ambiguous product/conflict decisions or missing permissions, not for
ordinary feature-branch creation already authorized by the operator.

## Step 1: Mirror the PR

Given PR number `N` from `microsoft/PowerToys`:

1. Fetch PR details: `gh pr view N --repo microsoft/PowerToys --json title,body,headRefName,headRepository,headRepositoryOwner,number,commits,files`
2. Fetch linked issue(s): parse issue references from the body, then `gh issue view <issue_number> --repo microsoft/PowerToys --json title,body`.
3. Create the same branch in the fork:
   ```powershell
   git fetch origin pull/N/head:pr-iterate/N
   git push <FORK_REMOTE> pr-iterate/N
   ```
4. Create a PR in the fork (`<FORK_REPO>`):
   - **Title**: `[PR N] <original title>` (number without `#`).
   - **Description**: sanitized summary combining the original description and linked issue(s) — **replace every `#<number>` with plain text** like "PR 12345" — plus a header `> Mirrored from microsoft/PowerToys PR N for review iteration`.
   - Base branch: `main`.
   - **Label**: apply `pr-review` so this skill's fork PRs are distinguishable from those created by other skills (e.g. `powertoys-issue-to-pr`) in the same fork. Ensure the label exists first (idempotent):
   ```powershell
   gh label create pr-review --repo <FORK_REPO> --color 1D76DB --description "Fork PR created by the powertoys-pr-review skill" --force
   gh pr create --repo <FORK_REPO> --head pr-iterate/N --base main --title "[PR N] <sanitized title>" --body "<sanitized description>" --label pr-review
   ```

> **Critical Rule 5:** never use `#<number>` in a fork PR title/body — it notifies the upstream thread. Use plain numbers.

## Step 2: Create local worktree

1. `cd <CLONE_PATH>`
2. Sync the clone's main with upstream first, then push it to the fork (run [scripts/Sync-ForkMain.ps1](../scripts/Sync-ForkMain.ps1) or):
   ```powershell
   git fetch origin main
   git checkout main
   git merge origin/main --ff-only
   git push <FORK_REMOTE> main
   ```
3. Create the worktree with the repo's existing script:
   ```powershell
   .\tools\build\New-WorktreeFromBranch.ps1 -Branch pr-iterate/N
   ```
4. Note the worktree path from the output (typically `C:\PowerToys-XXXX`).
5. Initialize submodules in the new worktree:
   ```powershell
   cd <worktree-path>
   git submodule update --init --recursive
   ```

## Step 2b: Sync the PR branch with latest main

**Mandatory before any build or review.** The PR branch must be rebased on the latest main to avoid stale-dependency issues and to ensure Copilot reviews against current main.

1. Rebase in the worktree:
   ```powershell
   cd <worktree-path>
   git fetch origin main
   git rebase origin/main
   ```
2. If the rebase conflicts:
   - **Resolve if confident** — you understand both sides and can preserve the PR's intent plus main's changes, regardless of file count.
   - **Stop if uncertain** — if any conflict needs a design decision you cannot make confidently, **stop and ask the user**, explaining the conflict and the decision needed.
   - Then `git rebase --continue`.
3. Update submodules after rebase: `git submodule update --init --recursive`.
4. Force-push the rebased branch to the fork:
   ```powershell
   git push <FORK_REMOTE> pr-iterate/N --force-with-lease
   ```
   If force-push is blocked by repo rules, push to a new branch name (e.g., `pr-iterate/N-v2`) and update the fork PR accordingly.
5. **Re-sync the fork's main** (rebase moved the branch forward, but the fork's main may be stale):
   ```powershell
   cd <CLONE_PATH>
   git push <FORK_REMOTE> main
   ```
   Without this, the fork PR diff includes every commit between old-main and new-main as "changed files".

## Step 2c: Validate the fork PR diff matches the original scope

Prevents meaningless Copilot reviews caused by diff bloat. After rebase and push, the fork PR diff may include hundreds of unrelated files if the fork's `main` is behind upstream.

1. Get the original PR's changed files (ground truth): `gh pr view N --repo microsoft/PowerToys --json files --jq '.files[].path'`.
2. Get the fork PR's changed files: `gh pr view <fork_pr_number> --repo <FORK_REPO> --json files --jq '.files[].path'`.
3. The fork PR should contain **only** files from the original PR (possibly fewer if conflicts were resolved differently, but never MORE unrelated files).
4. **If the fork PR has significantly more files** (e.g., 10+ extra files not in the original):
   a. Sync the fork's main (run [Sync-ForkMain.ps1](../scripts/Sync-ForkMain.ps1)).
   b. Close the stale fork PR: `gh pr close <fork_pr_number> --repo <FORK_REPO> --comment "Closing: diff was bloated due to stale base. Recreating after syncing main."`
   c. Force-push the rebased branch (or a new `-v2` branch if force-push is blocked).
   d. Create a new fork PR against the now-synced main (Step 1's `gh pr create`, including `--label pr-review`).
   e. Re-validate: repeat 1–3.
5. Once the diff is validated, derive and checkpoint the focused validation
   plan (Step 3), then request Copilot (Step 4) without a pre-review build.

**Why this matters:** Copilot reviews ALL files in the PR diff. If the fork PR shows 110 files changed when the original touches 3, Copilot wastes its review capacity on unrelated code and produces useless comments.
