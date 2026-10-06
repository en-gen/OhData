# CLAUDE.md

Guidance for every Claude Code session in this repository. The main session (one not launched with a brief) is the orchestrator and works from `docs/agents/orchestrator.md`, which `.claude/settings.json` injects at session start.
Role agents follow their own file in `.claude/agents/` plus this file.

## Build & Test

All commands run from the repo root.

```bash
# Build everything
dotnet build src/OhData.sln

# Run all tests - all eight test projects in one pass. CI (.github/workflows/ci.yml) runs the
# same eight one project at a time, so this is the local equivalent, not a superset.
dotnet test src/OhData.sln

# Run the main suite only - the fast inner loop (~80% of the tests; no companion-package,
# client, or test-bench coverage)
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj

# Run a single test by name
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "FullyQualifiedName~GetAll_Returns200"

# Run a single test class
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "ClassName~EndpointMappingTests"

# Run the test bench (interactive demo, browse to http://localhost:5099/scalar)
dotnet run --project src/OhData.TestBench.AspNetCore
```

## Architecture

OhData is a convention-based OData server framework that turns declarative profile classes into registered ASP.NET Core minimal API endpoints at startup - no controllers required.

Core flow:

- `EntitySetProfile<TKey, TModel>` declares an entity set: handler delegates (`GetAll`, `GetQueryable`, `GetById`, `Post`, `Put`, `Patch`, `Delete`, navigations, bound operations), auth, ETag and query settings. It also builds the EDM (`IVisitModelBuilder`) and exposes a runtime-typed `IEntitySetEndpointSource`.
- `AddOhData(builder => builder.AddEntitySetProfile<MyProfile>())` collects profile types; profiles are `AddScoped` (so they can inject a `DbContext`). The `OhDataRegistration` (keyed singleton, one per name) is built lazily from a temporary scope.
- `app.MapOhData()` returns a `RouteGroupBuilder`; `OhDataEndpointFactory.MapAll()` maps the service document, `$metadata`, and per profile only the routes whose handler delegate is non-null (collection, `$count`, by key, writes, navigations, `$ref`, property routes, bound operations), after running startup validation.
- Group endpoint filters supply `OData-Version`, version negotiation, `$format`/`Accept` handling, body limits, observability and the OData error envelope for unhandled exceptions.

The files below under `docs/internals/` are binding design decisions, with the measurements and reasoning behind them. An agent changing behaviour in an area reads the matching file first.

| File | Read when |
|---|---|
| `docs/internals/overview.md` | you need the full route list and flow diagram, handler-to-route rules, scoped profiles and the two-sources rule, type erasure, `MapGroup` routing, named registrations, or captured-state caches |
| `docs/internals/query-options.md` | touching query option handling, a 400/501 decision, `$count`, `$format`, `$select`, capability flags/allowlists, or `ODataQueryContext` |
| `docs/internals/error-handling.md` | touching exception handling, the error envelope, provider-fault classification (#494/#662), or deferred response serialization |
| `docs/internals/edm-and-startup-validation.md` | changing `$metadata`, startup validation, operation signature checks, `AdvancedConfigure`, or `Ignore()` |
| `docs/internals/operations.md` | touching bound/unbound functions or actions, `MaxTop` paging of operation results, or entity returns from operations |
| `docs/internals/etags.md` | touching ETags, `If-Match`/`If-None-Match`, or `CheckETagAsync` |
| `docs/internals/authorization.md` | touching authorization config, per-operation rules, the anonymous-route audit, or auth across navigations |
| `docs/internals/write-path.md` | touching POST/PUT/PATCH/DELETE, body parsing/scanning, deep writes, body limits, nullability validation, key handling, `@odata.bind`, or entity-id URLs |
| `docs/internals/open-types.md` | touching open complex types or dynamic properties |
| `docs/internals/runtime-type-resolution.md` | touching per-type config lookup, TPH/polymorphic serialization, navigation suppression, `EdmClrTypeMap`, or the expand projection |
| `docs/internals/expand.md` | touching batch-aware `$expand` (`BatchHandler`) |
| `docs/internals/mapper-and-delta.md` | touching the Mapper package, `MappedEntitySetProfile`, `DeltaProfile`, or `IDeltaFactory` |
| `docs/internals/project-layout.md` | you need the project list, target frameworks, or `InternalsVisibleTo` grants |

## Branches and commits

- `develop` is the default branch and the base of every PR. `main` is releases only: release PRs are merge-commit PRs, and post-release back-merges are direct-pushed merge commits, never PRs.
- Work on `feature/<name>` or `bugfix/<name>` branches cut from `develop`, one concern per branch.
- No `Co-Authored-By` trailers, no "Generated with Claude Code" footers, and no other AI attribution in commits or PR bodies.

# Quality gates

Each one exists because its absence let a defect reach review in this repo.

## Enforced mechanically

- Husky runs `dotnet format` over staged `.cs`/`.csproj` files on pre-commit
  (`.husky/task-runner.json`); CI runs the same format check.
- `develop` requires `build-and-test` and `k6` green plus one approving review,
  squash merges only, stale approvals dismissed on push (ruleset
  `protect-develop`). CodeRabbit is the approving reviewer
  (`.coderabbit.yaml`).
- Codecov (coverage and Test Analytics) is informational; it never reds CI.

## Claim discipline

- A measurement carries the commit it was taken at, the machine and the run
  count. A number without its commit is a rumour; re-measure before reasoning
  from a figure in an issue.
- Spec claims cite the clause (OData Part 1 §x.y) and are checked against the
  spec text, never paraphrased from memory. Claims about
  `Microsoft.AspNetCore.OData` behaviour are verified against its source, with
  `file:line`.
- Work with Microsoft OData conventions, not against them; a deliberate
  divergence names the spec clause that outweighs alignment.

## Tests must be able to fail

- Test first: see the test go red on the old code before the fix lands.
- Assert the real effect (status, body bytes, SQL, handler-reached), not that
  the code ran. A guard against a defect asserts the handler was or was not
  reached, not only the status code.
- A fixture for a defect class varies the property that produced it (runtime
  type vs declared type, multi-word names under a naming policy, a non-default
  host option). Three defects once lived in three green suites because every
  fixture had runtime type equal to declared type.
- Report exact test counts, passed and skipped, per project.

## Code, comments and docs

- `ImplicitUsings` is disabled; every `.cs` file states its `using`s.
- Comments say what the code cannot: 1-3 lines. Measurements, archaeology and
  rejected alternatives go in the issue, CHANGELOG or `docs/internals/`.
- `docs/` describes current behaviour, never history: no "used to", no
  unreleased version stamps. `CHANGELOG.md` owns what changed.
- A behaviour change updates the matching `docs/internals/` file and the
  public guide under `docs/` in the same PR.

## Agent workflow

Every agent follows these. Role-specific rules are in `.claude/agents/`.

- Every bug found gets its own GitHub issue, even when fixed in passing; the
  fix PR says `Fixes #N`.
- One agent per worktree, at `C:/Projects/.worktrees/OhData/<task>`, never
  inside the repo or beside it.
- Never `git stash`: worktrees share one stash stack. Use a WIP commit. Commit
  a fix before reverting a file to prove causality.
- Never certify your own work. Never merge, rebase a pushed branch, or
  force-push.
- Remove agent worktrees before running BenchmarkDotNet from the repo root;
  duplicate `.csproj` names break it.
- PRs that touch the server request path include k6 and BenchmarkDotNet
  results, with the commit they were taken at.
- Spawn a sub-agent only when it saves cost or context, by role, with an
  explicit `model`.
- After a merge, re-index: `GITNEXUS_MAX_FILE_SIZE=1024 node .gitnexus/run.cjs analyze`
  (see below).

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **OhData** (13559 symbols, 48898 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? `npx gitnexus analyze` (npm 11 crash → `npm i -g gitnexus`; #1939).

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows. For regression review, compare against the default branch: `detect_changes({scope: "compare", base_ref: "develop"})`.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `query({search_query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `context({name: "symbolName"})`.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method without first running `impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit changes without running `detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/OhData/context` | Codebase overview, check index freshness |
| `gitnexus://repo/OhData/clusters` | All functional areas |
| `gitnexus://repo/OhData/processes` | All execution flows |
| `gitnexus://repo/OhData/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->

## Re-indexing OhData with GitNexus

The block above is regenerated by the analyzer, so these three facts live outside it.

> **Always set `GITNEXUS_MAX_FILE_SIZE=1024` when re-indexing.** `OhDataEndpointFactory.cs` is ~469&nbsp;KB — under the analyzer's 512&nbsp;KB default cap, so a plain `analyze` indexes it, but the margin is ~43&nbsp;KB, thin enough that ordinary edits trip the cap again. `ExpandEngine.cs` (~271&nbsp;KB, the `$expand` pushdown, nav suppression, levels/windowing and EF-Include reflection machinery), `QueryOptionGate.cs` (~46&nbsp;KB, the sigil/capability query-option gate and the #358/#385 arithmetic-fault and #494/#662 translation-fault classifiers) and `BoundOperationResults.cs` (~26&nbsp;KB, the bound-operation result envelope, its `MaxTop` ceiling and the metadata an operation route advertises) are all comfortably under the cap. Pass the override regardless so all four are indexed the same way and re-indexing does not start silently skipping the factory file the next time it crosses 512&nbsp;KB. The threshold is not persisted in `.gitnexus/gitnexus.json`, so it has to be passed every time:
>
> ```bash
> GITNEXUS_MAX_FILE_SIZE=1024 node .gitnexus/run.cjs analyze
> ```
>
> If `analyze` dies with `FTS index 'file_fts' is inconsistent`, the incremental path cannot recover it: `node .gitnexus/run.cjs clean --force` and re-run the full analysis. Multiple repos are registered on this machine, so the CLI query commands need `--repo OhData`.
