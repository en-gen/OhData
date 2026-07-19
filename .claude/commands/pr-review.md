---
description: Two-pass PR review — adversarial implementation scrutiny + holistic patterns/simplification
argument-hint: <pr-number>
---

Run the OhData two-pass review of PR #$ARGUMENTS locally.

**Independence rule:** if any code under review was authored in the current session, dispatch a
fresh general-purpose subagent to perform the review (pass it this entire prompt plus the PR
number) so the author's context doesn't bias it, then relay its verified findings. Otherwise
review directly.

Read CLAUDE.md first for the architecture. Get the diff with `gh pr diff $ARGUMENTS` and read
every touched file IN FULL (not just hunks) before judging anything.

## Pass 1 — adversarial implementation scrutiny

Assume the diff is wrong and try to prove it. For each candidate finding, verify it against the
actual surrounding code before reporting; report ONLY findings you can defend with a concrete
failure scenario (specific input/state → specific wrong output, crash, leak, or spec violation).
Hunt specifically for:

- correctness bugs and unhandled edge cases (null/empty, key types, casing, concurrency,
  cancellation, large payloads)
- OData 4.01 spec violations (error envelope shape, status codes, $-query option semantics,
  header contracts)
- security issues (data leaks across the serialization surface, authz gaps, injection, resource
  exhaustion)
- behavior changes to existing routes that the PR does not declare
- missing or weak tests for the risky paths the diff introduces

## Pass 2 — holistic patterns & simplification

Review the change in the context of the files it touches, /simplify-style:

- duplication the diff introduces or could have removed (an existing helper that already does
  this; two near-identical branches)
- unnecessary complexity: indirection, flags, or abstraction the change does not need — name the
  simpler equivalent concretely
- inconsistency with the codebase's established patterns (cite the pattern and where it lives)
- dead code or vestigial config left behind by the change

Suggestions here must be concrete enough to act on (before/after sketch or named helper), not
vibes.

## Reporting

Report the findings **in the conversation** (do not post to GitHub unless explicitly asked; if
asked, submit ONE COMMENT-only review via
`gh api repos/en-gen/OhData/pulls/$ARGUMENTS/reviews -f event=COMMENT -f body=...` — never
approve or request changes; the human owner decides).

Structure: two sections — "Adversarial findings" (each: file:line, severity high/medium/low, the
concrete failure scenario) and "Patterns & simplification" (each: file:line, the concrete
suggestion). If a pass has no findings, say "No findings." under its heading — do NOT pad. Do not
comment on formatting, naming taste, or anything dotnet-format/CodeQL already enforces.
