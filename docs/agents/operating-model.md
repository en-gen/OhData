# Operating model

How the agent sessions divide the work. `CLAUDE.md` holds the rules every
session follows; each role's own file in `.claude/agents/` holds its
instructions. This file holds what spans roles: who does what, the decision
queue, operating modes, the deliverable loop, merging, the board and state.

## Roles

- **The owner** (Brian Engen) rules on design, scope and spec interpretation,
  and is the only one who ratifies a decision.
- **Overwatch** (`claude --agent overwatch`; a plain session acts as Overwatch)
  assigns issues to teams, relays results and owns the PR flow. It does no
  hands-on work and never rules.
- **Planning** (`claude --agent planning`) designs and files issues with the
  owner and agrees priority with Overwatch. It writes no product code.
- **Decisions** (`claude --agent decisions`) walks the decision queue with the
  owner and records rulings.
- **Team lead**: a background subagent named from the NATO alphabet (Alpha,
  Bravo, ...), tied to one issue, on Sonnet. It plans, then runs the
  deliverable loop with the role agents. Teams receive work from Overwatch and
  never pick their own; new work they find goes to Overwatch as a proposal.
- **Role agents**, dispatched by `subagent_type`, never `general-purpose`:

| Role                   | Model  | Work                                                     |
| ---------------------- | ------ | -------------------------------------------------------- |
| `team-lead`            | Sonnet | plan and done contract; runs the loop; writes no code    |
| `implementer`          | Sonnet | code, tests, docs; fixes review and CodeRabbit findings  |
| `simplify-reviewer`    | Sonnet | the simplification pass; applies safe quality-only edits |
| `adversarial-reviewer` | Opus   | the adversarial pass; report-only                        |
| `verifier`             | Sonnet | every Verify step, perf runs included; report-only       |
| `steward`              | Sonnet | refreshes the branch, opens the PR, merge mode, board card |
| `grunt`                | Haiku  | lookups, greps, git inspection, run-and-report           |

Always pass `model` explicitly; a per-call `model` beats the role's
frontmatter, so passing the same value guards against silent inheritance.
Workflow scripts name `model` on every `agent()` call. Overwatch dispatches
team leads and lookups in the background and in parallel; a team lead runs its
role agents in the foreground, one at a time.

- Escalating a task to Opus needs the owner's go-ahead; never use a small
  model for the adversarial gate. Untyped agents inherit Opus, so never spawn
  `general-purpose`.
- Resume a finished agent with `SendMessage` when its context is still useful
  (the implementer answering its own reviews), but never for further git work
  once its worktree may have been removed: its commands would land in the main
  checkout.

## Decisions

Teams bubble questions up to Overwatch, which answers only by citing an
existing ruling (a `docs/internals/` file, an issue's settled design, an owner
comment) and otherwise appends the question to the queue. Overwatch never
rules.

- The queue is `C:/Projects/.overwatch/OhData/decision-queue.md`, outside the
  repo. Only Decisions edits statuses and rulings; every other session appends
  a `## NEW` block at the end.
- Statuses: OPEN, PROVISIONAL (ambiguous, or reverses a standing direction,
  until confirmed), RULED, DEFERRED, WITHDRAWN, SUPERSEDED.
- Route: Decisions records the ruling verbatim and relays it to Overwatch,
  which relays it to the owning team. A ruling that changes behaviour lands in
  the matching `docs/internals/` file in the PR whose work it drives, or on
  the issue.
- Planning works a decision through directly with the owner only when it
  comes up inside design they are doing together. Every other option list goes
  to the queue, never to the owner.
- Decision format: one decision per message, numbered "Decision 1 of 4:
  <title>", each message starting with a `---` line. One lead sentence, two or
  three context bullets, then options as bold bullets
  ("**A. <name> (recommended).** <one line>") with Pros and Cons, exactly one
  recommended, and "Recommendation: <letter>" with a one-sentence reason.
  Describe a decision by content, not number. No tables.
- Bookkeeping (open or close an issue, `Fixes` vs `Refs`, labels, priority)
  is Planning's call and never goes to the queue.

## Operating modes

The active mode is recorded in `C:/Projects/.overwatch/OhData/overwatch_state.md`.
Day shift is active at start. Night shift and side quest are defined and
enacted only by the owner.

- **Day shift.** The owner approves each team lead's plan and done contract
  before implementation starts. Nothing proceeds on a recommendation alone.
- **Night shift.** Planning approves plans and done contracts, recorded as
  "Planning-approved (night shift), not reviewed by the owner". Teams never
  wait on the owner: open choices go to the queue and teams continue with what
  does not depend on them.
- **Both shifts.** CodeRabbit approves PRs. A `needs-owner` PR is always held
  for the owner. No agent casts an approving vote.
- **Side quest.** The owner's word in Overwatch's chat, alongside either
  shift, for chasing one specific problem. Overwatch then works that problem
  hands-on: no team lead or role
  agents, no hand-offs, no issue or state file unless the owner asks. Questions
  go straight to the owner, the hard rules in `CLAUDE.md` still apply, and it
  ends when the owner says so or the problem is solved.

## Deliverable loop

1. **Design.** Planning and the owner settle the design on the issue: the
   matching `docs/internals/` file read first (a design that reverses a
   recorded decision says so), spec questions answered from the spec text with
   the clause cited, `Microsoft.AspNetCore.OData` behaviour from its source,
   acceptance criteria a test can assert, an expected size.
2. **Plan.** The team lead writes the plan with the done contract: each
   acceptance criterion paired with the command or test that proves it. The plan
   summary lists each agent with role and model, the expected size and the merge
   mode. Approval follows the mode; a scope or roster change after approval goes
   back through it.
3. **Implement, test first,** in the worktree the team lead created. Docs are
   part of done.
4. **Simplify.** `simplify-reviewer` first: it applies quality-only edits and
   commits, so it runs alone in the worktree. A finding that removes a check,
   gate or test is reported, not applied.
5. **Adversarial review.** `adversarial-reviewer` is report-only; its findings
   go back to the implementer, who fixes them test first. Relay findings
   verbatim. If the approach is flawed, scrap it rather than patch it.
6. **Verify.** The `verifier` runs the steps in its role file plus any perf
   runs the change needs, and relays its report verbatim. A push after its
   report re-runs it, except the steward's `origin/develop` refresh merge: the
   structural check in the verifier's role file is enough for that. A fix that
   changes logic or removes a check goes to the adversarial reviewer first.
7. **PR.** The `steward` merges the current `origin/develop` into the branch
   (never a rebase), re-runs build and tests, reports the new head SHA, and
   opens the PR. A refresh conflict goes to the implementer to resolve, then
   back through Verify.
8. **Refresh check.** If the refresh merged anything on a verified path, the
   verifier checks the merge commit and the steward is resumed to enable
   auto-merge. The team lead reports the PR number only after this step.
9. **PR phase and close.** Overwatch watches the PR and routes events to the
   team lead (below), who runs the fix; Overwatch runs cleanup and re-index on
   merge (its role file).

Steps 4 to 6 are skipped only when every changed path is in this list:
`CLAUDE.md`, `docs/**`, `.claude/agents/**`, `.github/pull_request_template.md`
and `.github/ISSUE_TEMPLATE/**`. Anything not listed (`src/`, `.github/workflows/`,
`.husky/`, `.claude/settings.json`, build files) takes the full loop.
CodeRabbit and CI still check a skipped path. On it the team lead applies the
`needs-owner` check itself, there is no verifier pass so auto-merge is enabled
when the PR opens, and the steward's pre-flight still runs the secrets scan
and the commit-range check.

Done-contract rules:

- An agent never removes or rewords a criterion; reviewers and the verifier
  grade against it as approved.
- Test removals follow `CLAUDE.md`. The team lead puts each removed test and
  its reason in the brief of the adversarial reviewer and the verifier, which
  each grade every one against it; a disagreement fails the item. The steward
  puts them in the PR body. Agents judge validity themselves and never
  route a test question to Overwatch or the owner.

### The brief

Short headed sections, numbered one-line steps, plain words.

- **Task** and the issue number it is tied to.
- **Scope**: the settled design, what to reuse, what is out of scope.
- **Expected size.** The agent stops and asks if heading past it.
- **Worktree and branch.**
- **Rules it is likely to trip.** Name them and the `docs/internals/` file;
  do not restate `CLAUDE.md`.
- **Docs to update.**
- **PR.** Whether it may open, and the merge mode.
- **Return format.** Default: branch and head SHA, one-paragraph summary,
  files changed, exact test counts per project (passed and skipped), risks for
  the owner. Agents return this, not logs.

## Merging

A PR merges itself: `develop` (ruleset `protect-develop`) requires
`build-and-test` and `k6` green plus one approving review, squash only, and
dismisses stale approvals on push. In practice the approval is CodeRabbit's,
given once its comments are resolved (`.coderabbit.yaml`); GitHub cannot
require it to be CodeRabbit's, so a human approval merges it too. GitHub
refuses a self-approval, so no agent votes.

- The steward turns on auto-merge (`gh pr merge <n> -R en-gen/OhData --auto
  --squash`) once the verifier has passed the head, or at once on a path that
  skips Verify, and confirms it took (`gh pr view <n> -R en-gen/OhData --json
  autoMergeRequest`).
- `needs-owner` instead, auto-merge off, for:
  - a public API change;
  - a breaking or wire-visible behaviour change;
  - a new feature or a spec interpretation;
  - anything touching release or packaging;
  - a change to the agent workflow (`CLAUDE.md`, `.claude/`, `docs/agents/`);
  - a change that reverses a `docs/internals/` decision.

  Bugfixes, tests, docs and internal refactors with no wire change
  auto-merge. Unclear significance defaults to `needs-owner`. The plan names
  which applies; Planning may tighten a mode, never loosen it, and the verifier
  checks the diff against these criteria: a mismatch means `needs-owner`.
- The implementer answers every CodeRabbit review, including "changes
  requested": fix a valid finding, or reply with the
  reason when it is wrong, then resolve the thread. An unresolved thread
  withholds approval.
- Before pushing a fix to a PR with auto-merge on, the implementer disables it
  (`gh pr merge <n> -R en-gen/OhData --disable-auto`); the steward re-enables
  it only after the verifier passes on the new head, and never on a
  `needs-owner` PR.
- CodeRabbit re-reviews each push by itself. Never comment
  `@coderabbitai review`, `full review` or `@coderabbitai approve`.
- Autofix bots can push after the verifier: re-verify the PR head SHA before
  relying on a green result.
- A CI re-run reuses the PR's original merge commit. When the fix is on
  `develop`, merge `develop` into the branch (never rebase or force-push).
- Releases are not this flow: `develop` to `main` release PRs are merge-commit
  PRs the owner merges, and post-release back-merges are direct-pushed merge
  commits (`docs/releasing.md`). Repo admins can bypass the approval; agents
  never do.

### PR phase

Overwatch starts one background `grunt` as the watcher per open PR. It polls
`gh pr view <n> -R en-gen/OhData --json state,reviewDecision,statusCheckRollup,autoMergeRequest,mergeStateStatus,headRefOid`
and the unresolved review threads
(`gh api graphql --paginate -f query='query($endCursor:String){repository(owner:"en-gen",name:"OhData"){pullRequest(number:<n>){reviewThreads(first:100,after:$endCursor){nodes{id isResolved} pageInfo{hasNextPage endCursor}}}}}'`, every page)
at a sensible interval and exits on its first event, reporting it:

- `state` is MERGED or CLOSED;
- `reviewDecision` is CHANGES_REQUESTED;
- on a `needs-owner` PR only, the latest review by `coderabbitai[bot]` is APPROVED, there are zero unresolved review threads, `build-and-test` and `k6` are both present and have completed successfully, and every `statusCheckRollup` entry has finished with no failing `conclusion` (the watcher keeps waiting until all hold);
- any `statusCheckRollup` entry has a failing `conclusion`: a completed conclusion other than SUCCESS, NEUTRAL or SKIPPED (so CANCELLED, ACTION_REQUIRED, STARTUP_FAILURE and STALE count, as do FAILURE and TIMED_OUT);
- a review thread is newly unresolved;
- `headRefOid` changes in a push the team did not make: the team lead disables auto-merge at once, then the verifier and steward run as the Verify rule requires;
- `autoMergeRequest` is null on a PR that is not `needs-owner`;
- `mergeStateStatus` is DIRTY (a conflict).

Overwatch handles the event, then starts a new watcher, until the PR is merged
or closed. It records the event and the head SHA, and tells the new watcher to
suppress that same event for that SHA while it keeps polling for merge,
closure and the other events. To handle one, it resumes the team lead with `SendMessage` and the
lead runs the fix: the implementer (merging `origin/develop` into the branch
for a conflict), then the verifier and steward as the
Verify rule requires. On MERGED, whoever merged, Overwatch runs Close. On CLOSED without a merge it keeps the branch and worktree, moves the issue back to Ready on the board, records it in the state file and tells the owner. On
the `needs-owner` event it tells the owner once; CodeRabbit's approval means
its threads are resolved. `develop` moving alone is not an event, because
`protect-develop` does not require branches to be up to date. A desktop Overwatch may also
use `bind_pr` and `set_monitor` for one PR; they are absent under
`claude --agent`.

## Issues and the board

Every issue gets a GitHub issue type (`Bug`, `Feature`, `Task`), passed with
`gh issue create --type`, never a label. File with `--project OhData`.
Priority labels (`blocker`, `high-priority`, `medium-priority`,
`nice-to-have`) and area labels still apply; `needs-decision` marks an issue
waiting on an owner ruling, which agents skip.

The [board](https://github.com/orgs/en-gen/projects/2) Status is the claim:
Backlog, Ready, In progress, In review, Done. Ready means designed, sized and
free to claim. The team lead checks an issue is not In progress before
starting it, then moves it there; the steward moves it to In review when the PR
opens. Touch only `en-gen` repos and projects.

## State

Working state lives in `C:/Projects/.overwatch/OhData/`, a peer of the repos
and not under git. Durable knowledge goes in `docs/internals/`, on the issue
or in memory.

- `overwatch_state.md`: the current snapshot, rewritten in place and short.
  Holds the active mode and the team cap.
- `overwatch_log.md`: append-only history.
- `team_<name>_state.md`: the team lead appends at every loop step (issue,
  branch, worktree, phase, pending agent ids, [PROP] calls, open questions,
  last updated) and keeps a "Tried and failed" list so a later session does not
  repeat a dead end.
- `planning_state.md` and `decision-queue.md`.

## Superpowers skills

`brainstorming` (Design), `writing-plans` (the plan),
`subagent-driven-development` and `dispatching-parallel-agents` (delegation),
`test-driven-development` (implementers), `verification-before-completion`
(run by the verifier). Wherever a skill says you verify, review, run tests, or
resolve review items yourself, an agent does it and you cite its report. This
file wins on:

- Worktrees: `using-git-worktrees` puts them in `.claude/worktrees/`. The team
  lead creates them with `git worktree add` at
  `C:/Projects/.worktrees/OhData/<task>` instead.
- Reviews: its reviewers are replaced by the role agents above, dispatched by
  `subagent_type`.
- Finishing: `finishing-a-development-branch` is replaced by the PR and
  Merging steps above.
