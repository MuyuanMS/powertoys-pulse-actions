# Upstream finding grounding

This is a semantic review gate plus mechanical evidence verification. It applies
to inline prose, suggestions, companion notes and code claims in review bodies,
including legacy drafts being rewritten. Formatting is not validation.

## Required investigation

1. Pin the current upstream PR head. Read the PR description, validation,
   relevant discussion and exact source at that SHA, not just the diff or fork.
2. For each claim, trace the actual caller/guard/fallthrough and reproduce the
   problem on upstream where feasible. Explicitly check whether the requested
   guard/fix already exists, whether the named API is called, and whether the
   author deliberately chose this behavior. Record the strongest counterevidence
   and why it does or does not defeat the claim.
3. Track the first affected commit and classify every accepted/rejected fork
   finding privately: `upstream`, `review_introduced`, `already_fixed`,
   `optional`, or `unsupported`. Later fixes to a review-introduced design stay
   internal. Evaluate the net alternative against upstream; do not turn its
   intermediate defects into author requests. Optional optimization is not a
   correctness defect. A clean fork review is not proof of upstream necessity.
4. Separate the defect proof from the proposed remedy. For focus, keyboard,
   accessibility or timing claims, record a focused interaction test on the
   upstream tree and candidate where needed. A compile-only result must be
   described as compile-only; never label a behavioral hypothesis reproduced.
   If evidence is insufficient, omit the public finding and checkpoint the
   investigation. Do not inflate confidence or severity to compensate.
5. Draft only claims that survived those checks. Every meaningful public claim
   must be supported, including claims added while making prose more detailed.
   Re-run the analysis after any rewrite. Hash the final body only after that
   review; updating a hash is not the review itself.

Keep a private delta ledger for all fork-only hunks, including dismissed
experiments and their reasons. Do not expose local paths, fork URLs or private
review provenance in public comments/artifacts. Preserve prior branch history;
remove unnecessary experiments with ordinary commits if continuing that branch.
An exact-head clean build plus rejected findings does not inherit a fresh code
review from a divergent fork. Re-run the required review on the retained tree.

## Incident regression: PR 50472

At upstream `bc8ed60536693892953b2723f1bfc966aba2fe48`, the title-bar focus
sink was declarative XAML; there was no `ClearInitialFocus`/`FocusManager` call
in its code-behind. The review agent introduced those calls, then exported
follow-up lifecycle/null-root problems as upstream defects. The hook already
required `altPressed`; an early-return optimization was incorrectly described
as fixing unguarded arrows. Neither claim was made valid by the clean fork
review or by expanding it into an implementation-ready general comment.

Before reusing any rejected finding, address its upstream counterevidence.
An author rebuttal is new review evidence even when the head is unchanged.
Withdraw invalid drafts, clear unjustified author-waiting state, and retain
the explanation so later runs do not resurrect them. Never edit/delete posted
upstream comments or post an apology/retraction without explicit approval.

## Private evidence contract (version 1)

For review-data.json, store this at
`prs[].internalEvidence.validation.findingGrounding`. For dashboard artifacts,
write a separate **private session file**, not under public `data/`, containing
`{"prs":[{"number":50472,"findingGrounding":{...}}]}`.

```jsonc
{
  "version": 1,
  "head_sha": "<exact upstream 40-character SHA>",
  "findings": [
    {
      "id": "<exact public item/comment ID>",
      "origin": "upstream",
      "body_sha256": "<Get-FindingBodyHash of the final author-facing body>",
      "claim": "Specific defect asserted by this comment.",
      "upstream_failure": "Reachable upstream path and observable failure.",
      "counterevidence": "Existing guards, PR intent and author validation checked; explain the result.",
      "why_not_already_fixed": "Why the current guard/fallthrough does not solve this defect.",
      "verification": "Actual test/trace results and limitations, not merely a proposed test.",
      "sources": [
        {
          "path": "src/example.cs",
          "start_line": 10,
          "end_line": 12,
          "excerpt": "Exact lines 10-12, LF separated, no extra trailing newline.",
          "symbols": ["ExistingMethod"]
        }
      ]
    }
  ]
}
```

`findings` covers publishable items only; retain rejected ones in the separate
private ledger. IDs must match exactly without duplicates. `sources` must cover
the upstream evidence for the claim, including the relevant guard/caller when
needed. Missing-behavior claims cite the actual existing path, not fabricated
lines containing the requested new method. `symbols` lists existing symbols
claimed in each excerpt; suggested new symbols belong in the remedy instead.

The body digest is SHA-256 of UTF-8 text with CRLF normalized to LF, preserving
all other whitespace. Dot-source `scripts/FindingGrounding.Common.ps1` and use
`Get-FindingBodyHash` rather than hand-calculating it.

Review-data items use their normal IDs. Put code findings in items rather than
burying them in `contextBody`; that field is for process context only. Dashboard
review body prefixes, if present, require an additional record with ID
`review-body:<action.id>` and the exact prefix body.

## Gates

```powershell
pwsh -NoProfile -File .\scripts\Test-ReviewData.ps1 `
  -DataPath <private-review-data.json> -RequireFindingGrounding -CheckGitHub
```

The live publisher requires this gate. Offline dry-runs retain their existing
test-only behavior and do not establish source verification.

For **every processed PR with proposed comments or a review body**, after the
final emitter/sanitizer pass and before committing:

```powershell
pwsh -NoProfile -File <dashboard-skill>\scripts\Test-DashboardArtifacts.ps1 `
  -Dashboard <canonical-main-checkout> -Numbers <processed-numbers> `
  -RequireDetailedDesign -RequireIssueContext -RequireFindingGrounding `
  -GroundingPath <private-session-grounding.json> `
  -SourceRepository <PowerToys-clone-containing-pinned-upstream-commits>
```

SourceRepository is optional: without it, the checker reads exact upstream
blobs from GitHub. With it, the checker uses `git show <SHA>:<path>`, never the
working-tree contents. Fetch missing commits explicitly; do not substitute a
different tree if the object is unavailable.

The checks reject missing/mismatched evidence, rewritten bodies, non-upstream
origins, invented excerpts and missing claimed symbols. They cannot prove an
arbitrary natural-language defect: the contradiction/reproduction review above
is mandatory. A passing excerpt check is not a proof of behavior.

Compatibility: ordinary whole-feed validation still accepts older artifacts.
The strict gate is scoped to explicitly processed numbers so a bounded update
does not invalidate the entire backlog. Never use that compatibility mode to
publish a newly evaluated or rewritten PR. If grounding fails, remove its
executable review proposal, checkpoint `review_in_progress`/needs revalidation,
and report it as unfinished. Do not silently relabel unsupported findings as
approval-ready. Do not upload the private dossier or put it in public bodies.
