# #206 `$select` Projection Pushdown (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When `$select` is present on the `GetQueryable` path, compose a member-init projection onto the queryable so providers emit column-pruned SQL — byte-identical wire output, per spec `docs/design/206-select-projection-pushdown.md`.

**Architecture:** All work is inside `MapEntitySet<TKey,TModel>` (fully typed — `IQueryable<TModel> filtered` exists at `OhDataEndpointFactory.cs:2179`) plus metadata plumbing on the profile. Projection = selected structural ∪ key ∪ ETag props; per-request eligibility with silent Debug-logged fallback; `SelectPushdownEnabled` default `true` inherited from `EntitySetDefaults`.

**Tech Stack:** System.Linq.Expressions member-init, xUnit + `TestHostBuilder`, EF Core Sqlite (test-only reference) for SQL-shape assertions, BenchmarkDotNet scratch A/B.

## Global Constraints

- `ImplicitUsings=disable` — all usings explicit. No `Co-Authored-By`. Feature branch `feat/select-pushdown-206`; never commit to develop. Husky dotnet-format; never `--no-verify`. Test filter syntax: `FullyQualifiedName~` only. Full suite must stay green (1097+). Local two-pass review via subagent before merge handoff; BDN A/B in PR body; k6 in CI.

---

### Task 1: Metadata plumbing (profile + defaults + interface)

**Files:** Modify `src/OhData.AspNetCore/StructuralPropertyInfo.cs`, `src/OhData.AspNetCore/EntitySetDefaults.cs`, `src/OhData.AspNetCore/EntitySetProfile.cs`, `src/OhData.AspNetCore/IEntitySetEndpointSource.cs`. Test: `src/OhData.AspNetCore.Tests/SelectPushdownMetadataTests.cs` (new).

**Produces (later tasks rely on):**
- `StructuralPropertyInfo.Property` (`required PropertyInfo`) — Expression.Bind target; setter usability = `Property.SetMethod is { IsPublic: true }` (init-only qualifies).
- `EntitySetDefaults.SelectPushdownEnabled` (`bool`, default `true`); profile `protected bool? SelectPushdownEnabled { get; init; }` resolved in `VisitModelBuilder`; `bool IEntitySetEndpointSource.SelectPushdownEnabled`.
- `IReadOnlyCollection<string>? IEntitySetEndpointSource.ETagPropertyNames` — captured in `UseETag` **before** the `s_etagCache` early return, via a try-style direct-member extraction (any non-direct selector → whole collection `null`). Null + `HasETag` ⇒ pushdown ineligible.

- [ ] Steps: failing tests for each produced member (profile-instantiation unit tests: defaults resolution on/off/inherit; ETag names captured for direct selectors, null for a computed selector, still populated when the cached-delegate early return path runs — construct the same profile type twice; `StructuralPropertyInfo.Property` non-null and setter flags correct incl. init-only + get-only models) → implement → suite green → commit `feat: metadata plumbing for $select pushdown (#206)`.

### Task 2: Projection builder + handler wiring

**Files:** Modify `src/OhData.AspNetCore/OhDataEndpointFactory.cs` (GetQueryable handler, insertion between the Top/Take block ~2242 and `filtered.ToArray()` ~2244; new private static helpers).

**Implementation:**
- `private static IQueryable<TModel> ApplySelectProjection<TModel>(IQueryable<TModel> query, IReadOnlyList<PropertyInfo> members)` — `x => new TModel { M1 = x.M1, ... }` via `Expression.MemberInit(Expression.New(typeof(TModel)), members.Select(m => Expression.Bind(m, Expression.Property(x, m))))`; built per request, deliberately uncached (spec: unbounded-`$select`-combination cache is a growth vector).
- Handler block (uses existing `ExtractSelectedProperties(clause)` — top-level identifiers, `null` when all-selected/empty):
  1. Skip unless `source.SelectPushdownEnabled` and `options.SelectExpand?.SelectExpandClause` yields non-null selected names.
  2. Startup-captured closure `bool hasParameterlessCtor = typeof(TModel).GetConstructor(Type.EmptyTypes) is not null;` — else Debug-log fallback.
  3. Projection set: selected names matched to `source.StructuralProperties` by `OrdinalIgnoreCase` (silently dropping nav names `ExtractSelectedProperties` includes) ∪ key (`IsKey`) ∪ `source.ETagPropertyNames` (when `HasETag`; names null ⇒ fallback). Every member must satisfy the setter check ⇒ else fallback naming the property.
  4. Eligible ⇒ `filtered = ApplySelectProjection(filtered, memberList);` — after `$top`/Take, immediately before `ToArray()`, so `$count`/paging semantics are untouched.
  5. Fallback logging: `logger?.LogDebug("OhData: $select pushdown skipped for {EntitySet}: {Reason}", ...)` (per-request Debug; update the spec doc's "once per reason" phrasing to match).
- Spec-doc touch: simplify the type-erasure note (handler is generically typed; no interface member needed for the projection itself).

- [ ] Steps: build clean → quick manual smoke via existing tests → commit `feat: compose $select member-init projection on the GetQueryable path (#206)` (red/green arrives with Task 3 — wiring and its observable behavior are only testable through the host).

### Task 3: Byte-identity + fallback integration tests

**Files:** `src/OhData.AspNetCore.Tests/SelectPushdownTests.cs` (new).

Two hosts differing only in `WithDefaults(d => d.SelectPushdownEnabled = false)`; identical seeded IQueryable (LINQ-to-objects translates member-init) profiles: normal entity + `UseETag` entity + positional-record entity + get-only-computed-property entity + a nav (`HasMany` batch) for `$expand`.

- [ ] Byte-identity matrix (`Assert.Equal(bodyOff, bodyOn)` and ETag headers): plain `$select`; `$select`+`$filter`+`$orderby`+`$skip`+`$top`; `$select`+`$expand`; `$select` on the ETag host (headers + `@odata.etag`); nested `$select` path (complex prop host); `$select` naming the get-only property (fallback); record host (fallback); no-`$select` control. Also `$select`+paging `@odata.nextLink` equality.
- [ ] Commit `test: $select pushdown byte-identity + fallback matrix (#206)`.

### Task 4: SQL-shape proof (Sqlite)

**Files:** `src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj` (add `Microsoft.EntityFrameworkCore.Sqlite` test-only), `src/OhData.AspNetCore.Tests/SelectPushdownSqliteTests.cs` (new).

Keep-alive `Data Source=:memory:` connection; `DbContext` with a ~6-column entity; `optionsBuilder.LogTo(sink, [RelationalEventId.CommandExecuted])`; profile `GetQueryable = _ => Task.FromResult(db.Wides.AsQueryable())`.

- [ ] Assert: `$select=id,name` request ⇒ captured `SELECT` contains exactly Id/Name (+ETag props if configured) columns and NOT the others; no-`$select` ⇒ all columns; `SelectPushdownEnabled=false` host ⇒ all columns despite `$select`. Commit `test: Sqlite SQL-shape assertions prove column pruning (#206)`.

### Task 5: Docs, benchmark, PR

- [ ] `docs/query-options.md` `$select` section: pushdown paragraph (mechanism, default-on, opt-out flag, fallback conditions, Priority-1 read-the-flag note). `CHANGELOG.md` `[Unreleased]`/`### Added`. Commit `docs:`.
- [ ] Scratch BDN A/B (scratchpad, NOT repo — worktree/duplicate-csproj hazard): Sqlite file DB, 20-column entity ×5k rows, `$select` 2 cols — full-fetch+trim vs projection. Record table for PR body.
- [ ] Push; `gh pr create --base develop` titled `feat: $select projection pushdown on the GetQueryable path (#206)`; body: mechanism, eligibility/fallback, byte-identity guarantee, BDN table, SQL-shape proof snippet, k6-in-CI note, `Fixes #206` is WRONG for phase 1 — use `Part of #206 (phase 1: $select)` so the issue stays open for phase 2. Dispatch delta/full local review subagent; address findings; hand to user.

## Self-review

Spec coverage: mechanism/projection-set/eligibility/flag/logging (T1-T2), byte-identity+fallback matrix (T3), SQL-shape (T4), benchmark+docs (T5), Priority-1/GetById boundaries are doc-only (T5). Types consistent: `Property` PropertyInfo (T1) consumed by `ApplySelectProjection(IReadOnlyList<PropertyInfo>)` (T2). No placeholders.
