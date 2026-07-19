# #206 — `$select` projection pushdown (phase 1) + `$expand` pushdown direction (phase 2)

- **Issue:** [#206](https://github.com/en-gen/OhData/issues/206)
- **Milestone:** 1.5.0
- **Status:** Approved design (2026-07-19). Phase 1 (`$select`) implemented now; phase 2
  (`$expand`) is direction-sketched here and gets its own spec + PR.

## Problem

`$select` and `$expand` are implemented via JSON post-processing, not query pushdown:

- `$select` fetches the **full entity** and trims JSON afterward — no SQL column projection.
  A 40-column table with `$select=id,name` still reads 40 columns per row.
- `$expand` invokes per-navigation delegates. Batch handlers eliminated N+1, but expansion is
  still separate queries — never a SQL join driven by the parent queryable.

The original reason `$select` avoided `ApplyTo`: Microsoft's `$select` pipeline materializes
`ISelectExpandWrapper` objects whose serialization breaks the camelCase-consistent output the
framework guarantees. Any pushdown design must preserve today's wire bytes exactly.

## Phase 1: `$select` member-init projection

### Mechanism

On the `GetQueryable` path, when `$select` is present and eligible, the framework composes a
**member-init projection** onto the profile's queryable before enumeration:

```csharp
query.Select(x => new TModel { Id = x.Id, Name = x.Name /* projection set only */ })
```

EF Core (and any LINQ provider that translates member-init — all EF relational providers and
InMemory do) turns this into a column-pruned `SELECT`. The materialized objects are **plain
`TModel` instances**, so the entire existing JSON pipeline — camelCase serialization, `$select`
JSON trim, un-expanded-navigation omission, `$expand` batch correlation by key — runs
byte-identically. Pushdown changes the SQL, never the wire. (The `$select` JSON trim stays: it
is what removes the CLR-default values of un-projected properties from the response.)

The projection lambda is built **per request** (microseconds for small expression trees) and
deliberately **not cached**: `$select` combinations are client-controlled and unbounded, so a
lambda cache keyed by select-set would be an unbounded-growth vector (same hardening ethos as
#202). EF's own query cache keys structurally and handles repeated shapes.

### Projection set

`selected structural properties` ∪ `entity key` ∪ `UseETag properties`:

- **Entity key** — always projected: `@odata.id` construction, `$expand` batch correlation,
  and `$skiptoken` continuation all read it.
- **`UseETag` properties** — projected so `@odata.etag`/`ETag` values are identical with and
  without pushdown. `UseETag(...)` today compiles its selectors and discards the names, so the
  profile additionally captures the ETag property **names** at `UseETag` time (direct-member
  selectors only). A `UseETag` selector that is not a direct property access means the names
  are unknowable → pushdown is ineligible whenever ETags are enabled on that profile (fallback,
  see below).
- **Nested `$select` paths** (`$select=address/city`) project the whole top-level member
  (`Address = x.Address`); the JSON trim shapes the nested object exactly as today.
- Navigation properties appearing in `$select` are not structural and are ignored by the
  projection (expansion remains delegate-driven and correlates by the always-projected key).

### Eligibility — per request, cheap, silent fallback

Pushdown applies only when ALL hold; otherwise the request silently uses today's full fetch
(logged once per reason at Debug):

1. `SelectPushdownEnabled` resolves `true` — new profile-level `bool?` inheriting
   `EntitySetDefaults.SelectPushdownEnabled`, **default `true`**. The opt-out exists for
   exotic `IQueryable` providers that cannot translate member-init.
2. The request has a `$select` that is not `*` (no `$select` → nothing to prune).
3. `TModel` has a public parameterless constructor (positional records → fallback), computed
   once at startup.
4. Every property in the projection set has a usable setter (init-only counts; get-only
   computed properties disable pushdown **only when actually in the projection set**). Setter
   availability is captured per property at startup; the per-request check is a set
   intersection.
5. If `UseETag` is configured, its property names were capturable (direct-member selectors).

Wire-shape invariants (tested): responses with pushdown on vs off are **byte-identical** —
including `@odata.etag`, `@odata.id`, `@odata.nextLink`, `$expand`-ed content, and `$count`.

### Scope boundaries

- **`GetQueryable` collection path only.** `GetAll` (IEnumerable) has nothing to push to.
  `GetById` is a delegate with no queryable. The **Priority-1** path
  (`ODataEntitySetProfile.GetODataQueryable`) owns its `ApplyTo` call — same posture as
  `RoundingMode`: the framework does not auto-apply there; the profile can read the resolved
  flag and project itself (documented).
- The `$count` companion path is unaffected (no columns to prune in a COUNT).
- Type-erasure note: the factory works with type-erased sources; the projection is built where
  the generics live — a new internal `IEntitySetEndpointSource` member implemented by
  `EntitySetProfile<TKey, TModel>` receives the projection-set names and returns the projected
  `IQueryable` (typed member-init inside, `Cast<object>` at the boundary as today).

### Verification

- **Byte-identity matrix**: same host, `SelectPushdownEnabled` on vs off — identical bodies
  and headers for: plain `$select`, `$select`+`$filter`+`$orderby`+paging, `$select`+`$expand`,
  `$select` with ETags enabled, nested `$select` paths, `$select` naming a get-only property
  (fallback case), record model (fallback case).
- **SQL-shape assertions**: EF Core Sqlite (`Data Source=:memory:`) test host with command-text
  capture (`DbContextOptionsBuilder.LogTo`), asserting the `SELECT` list contains exactly the
  projection-set columns for a pushdown request and all columns for a fallback request.
  (Test-project-only package reference to `Microsoft.EntityFrameworkCore.Sqlite`.)
- **A/B BenchmarkDotNet** (project rule): wide entity (~20 columns) over Sqlite, `$select` of 2
  properties — full-fetch+trim vs projection+trim; results in the PR body.

## Phase 2 (direction sketch): `$expand` pushdown via navigation projection

Same idea, not EF `Include`: a navigation member inside the member-init —
`Tags = x.Tags.ToList()` / `Category = x.Category` — translates to a SQL join **without adding
any EF dependency to the core package** (`Include` is an EF extension method; member-init is
pure LINQ). Per-navigation opt-in via a new `HasMany`/`HasOptional`/`HasRequired` overload
flavor declaring "this navigation is loaded by the queryable itself" (no delegate). Delegates
remain the model for non-EF-mapped navigations (e.g. the #209 sample's FK-only model). Open
questions deferred to the phase-2 spec: nested `$expand` depth, `$expand` query options
(`$filter`/`$top` inside expand) interaction, and how loaded-nav serialization composes with
the un-expanded-navigation omission (#176). Phase 2 does not start until phase 1 is merged.
