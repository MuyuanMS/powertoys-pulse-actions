# Migrating Active Reviews to This Skill Version

Teammates using an older skill should stop before it posts malformed or unvalidated payloads. Existing code fixes and review-loop progress are durable in their own PowerToys repository and can be resumed.

## Message to teammates

Send:

> At a safe checkpoint, preserve current PR state without posting or deleting
> work. Update/verify the complete suite from canonical actions main and reload
> the compact coordinator/worker entry points. Continue the same PRs from their
> actual saved requests, commits and validation receipts, not inherited old
> procedural prompts. Preserve consumed allowance and regenerate current-head
> payloads before any newly approved upstream posting.

## Resume workflow

1. At a safe checkpoint, preserve the old session's private state and reconcile
   running workers/processes. Do not abort an owned live build or delete its
   branch, worktree, review PR, commits, replies, or resolved threads.
2. Update the complete suite from canonical actions `main`, verify installation,
   and reload the compact coordinator/worker entry points. Do not carry forward
   superseded procedural prompts as extra approval/convergence requirements.
3. Start a new session in the teammate's PowerToys clone.
4. Run:
   ```powershell
   ./scripts/Get-ReviewResumeState.ps1 -PRNumber 43741,49427 -AsJson
   ```
5. Resume each PR according to `resumeAction`:

| `resumeAction` | Continue at |
| --- | --- |
| `fresh-mirror` | Step 1 |
| `push-and-create-review-pr` | Push the existing local review branch, then create its review PR without remirroring |
| `create-review-pr` | Create the review PR from the existing branch; do not overwrite the branch |
| `reopen-or-create-review-pr` | Reopen the matching review PR or create a new one from its existing branch |
| `create-worktree` | Step 2 using the existing review branch |
| `resume-review-loop` | Steps 5–6 using the discovered latest review timestamp and unresolved threads |
| `rebuild-and-draft` | Verify exact-input validation receipts, rerun only missing/changed checks, then draft under the worker outcome contract |

6. Verify retained receipts/logs using `build-and-test.md`. Cross-session resume
   alone does not require recompiling identical inputs. Preserve consumed
   requests across lanes; at or above three, consume results and finalize
   supported drafts, not another request.
7. Preserve old review data as evidence, but regenerate current-head payloads
   and approvals before any posting. Run `Test-ReviewData.ps1 -CheckGitHub`.

## What is preserved

- review branch and commits;
- review PR and its current head;
- Copilot review timestamps and rounds;
- replies and resolved/unresolved review threads; and
- a matching local worktree, when it still exists.

Session-local dashboard state, unpublished prose drafts, and prior build claims are not trusted. Reconstruct and validate them.
