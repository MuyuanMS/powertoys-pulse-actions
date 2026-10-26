# Multiple independent PR reviews

Use one coordinator and one worker per distinct active PR. The worker follows
the same `SKILL.md`/cloud-loop contract as a single review; batch mode adds no
convergence requirement. Clean, cap and architecture outcomes all return.

## Share only what must be shared

- Recover existing lanes and sync the fork base once before dispatch.
  Workers use independent review branches/worktrees; do not race fork-main
  syncs or reset divergent shared history.
- The coordinator alone writes dashboard/review-data files and publishes.
  Workers return drafts and private state; fork pushes/replies are permitted.
- Serialize actual compilations under one owned build slot. Do not hold the
  lock while waiting for Copilot, reading code or drafting. Validation after
  cap/architecture exits is allowed; it never waits for clean convergence.
- No nested workers. Direct batches normally use three workers, up to five
  when appropriate; dashboard runs use their planner's configured cap.

## Dispatch and handoff

Seed one row per PR, recover saved requests, and start available work.
Release a worker on cloud/build-slot wait; use completion notifications and
due read-only checks to resume work and refill slots. Never keep a PR stranded
at round one merely because a worker returned a durable wait.

Pass the PR number, actual fork config, saved state, suite revision, assigned
operation and any normal-run deadline. Give workers the compact PR entry point
and only the operation's needed reference, not the coordinator's whole history.
Each returns the worker outcome contract with exact upstream/request identities.

If subagents are unavailable, pipeline requests/waits in the coordinator:
prepare/request ready PRs, process arrived results, serialize validation.
Do not wait for one PR's entire loop before requesting another ready PR.
Use one run-level wake mechanism, not a separate schedule for every PR.

## Deliver independently

Publish/present each validated result without waiting for unrelated failures.
A clean review needs the clean evidence; cap/architecture drafts need their
grounding/validation, not zero unresolved threads. Preserve real unresolved
concerns truthfully. Never treat an operational failure as an upstream defect.

For direct reviews, `approval-dashboard.md` is an optional approval UI, not a
prerequisite for starting review. Upstream posting always remains separately
approved and validated through the existing publisher.
