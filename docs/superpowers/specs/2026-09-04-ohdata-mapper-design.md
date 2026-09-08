# OhData.Mapper — API model / persistence model separation, design

**Status:** design, pre-implementation
**Milestone:** 2.0.0 (owner decision)
**Repo:** this one, `src/OhData.AspNetCore.Mapper` (see *Placement*)

## Problem

An adopter wants the type on the wire to differ from the EF entity. Every option available today
fails at something. Measured 2026-09-04, EF Core 10 / SQLite, SQL captured:

| approach | plain `GET` | `$expand` | `$filter` through the nav |
|---|---|---|---|
| entity as model | ✅ | ✅ | ✅ — but no separation at all |
| DTO, nav projected eagerly | ❌ `LEFT JOIN` + **every child row fetched, then discarded** | ✅ 1 query | ✅ `WHERE EXISTS` |
| DTO, nav via `batchGetAll` | ✅ clean | ✅ 2 queries | ❌ **500** (#662) |

No DTO configuration gets all three. The eager form — the only one that supports `$expand` — obliges
the adopter to hand-write **every navigation they might ever expand** into one `Select`, paying for
all of them on every request. That fights the premise of the stack: OData + EF Core is valuable
because the client's query translates end-to-end, with `IQueryable` as the glue. Hand-projecting away
from EF breaks the glue.

**OhData already ships half the answer.** `DeltaProfile`/`IDeltaFactory` (#243) maps DTO → entity for
writes, because a projection has no inverse. The read half was left to the adopter's own projection
on the assumption it was already solved. It is not, and `DeltaProfile` has no reason to exist except
this scenario — the two are halves of one feature that shipped a release apart.

## Measured facts this design rests on

Established this session. Each is load-bearing; none should be re-derived.

1. **EF composes a predicate back through a projection.** `Select(p => new Dto { CategoryName = p.Cat.Name })` filtered on `CategoryName` emits `INNER JOIN "Cats" … WHERE "c"."Name" = @p`.
2. **An outer projection prunes an inner join.** Composing a member-init that omits a collection turns `SELECT …, l.Id FROM Orders LEFT JOIN Lines` into `SELECT Id, Code FROM Orders`. This is why an unexpanded navigation can cost nothing.
3. **Binding substitution works through a reshaped collection.** A spike rewrote `d => d.Tags.Any(t => t.Label == "sale")` into `p => p.Links.Any(l => l.Tag.Label == "sale")`, which translated to a correlated `EXISTS` — with the M2M join entity absent from the DTO. ~120 lines. This is the feature's reason to exist.
4. **String interpolation is *projectable* but not *queryable*; folded `Concat` is both.** `$"{p.First} {p.Last}"` in a final `Select` is evaluated client-side and works; the same text in a `Where` throws. `p.First + " " + p.Last` — i.e. folded two-argument `string.Concat` — becomes `"p"."First" || ' ' || "p"."Last"` and is filterable. The **params-array** `Concat(string[])` overload is client-evaluated and is *not*.
5. **A `FormattableString` interpolation can be decomposed and rebuilt as folded `Concat`.** A spike took `o => $"{o.First} {o.Last}"`, read `FormattableStringFactory.Create`'s format and arguments out of the tree, and emitted `Concat(Concat(o.First, " "), o.Last)` — which translated in **both** projection and filter. This is the `Compute` surface: interpolation ergonomics, guaranteed translation, and no way for an adopter to write the untranslatable form by accident.
6. **`ToQueryString()` forces translation without executing** — the startup-validation mechanism.
7. **View-mapped entities already solve the whole problem with no code** for adopters with DDL rights: real navigations, conditional joins, filterable. This package is for those without.

## Declaration: path correspondences, not projections

**The adopter never writes `Select(x => new Dto { … })`.** That strategy is rejected: it is arbitrary
EF code, so translatability is discovered at runtime rather than guaranteed, and it lets an adopter
express something that only fails on a request.

Instead, each DTO member declares *where it comes from*:

```csharp
public sealed class OrderProfile : MappedEntitySetProfile<int, Order, OrderDto>
{
    public OrderProfile(AppDbContext db) : base(db.Orders, x => x.Id)
    {
        Map(m =>
        {
            m.Property(d => d.Id).From(o => o.Id);                         // direct
            m.Property(d => d.OrderCode).From(o => o.Code);                // rename
            m.Property(d => d.CategoryName).From(o => o.Category.Name);    // path
            m.Collection(d => d.Tags).From(o => o.Links).Element(l => l.Tag);  // M2M elision
            m.Property(d => d.DisplayName).Format(o => $"{o.First} {o.Last}");
            m.Ignore(d => d.RenderedAt);
        });
    }
}
```

`Tag → TagDto` is itself a registered map; collections reference it by element type rather than
repeating it inline.

### Why paths rather than expressions

- **Translatability is guaranteed by construction.** A member path always translates. #662's failure
  mode cannot be expressed.
- **Invertibility is computable per binding**, not inferred from an object initializer.
- **The declaration is data**, so it can be validated, enumerated and tested exhaustively — and an EF
  composer becomes one backend rather than the only conceivable one.

`Format` takes a `FormattableString` and is decomposed by the mapper into folded `Concat` (fact 5),
so the adopter gets interpolation ergonomics and cannot express the untranslatable form. `Compute`
remains for anything else and is the only place EF's translation rules reach the declaration — which
is what the startup probe (fact 6) polices, catching fact 4's traps at build time. A binding that
projects but cannot be filtered is marked `NotFilterable`/`NotSortable` rather than refused.

## One declaration, four consumers

The rule this codebase already enforces (#467: one site, N consumers).

| consumer | derived how |
|---|---|
| **read projection** | member-init composed **per request** from the engaged expand tree, so an unexpanded navigation costs nothing (fact 2) |
| **predicate/sort substitution** | model member path → entity expression; lambdas over collections substitute the range variable through the element path (fact 3) |
| **write map** | invertible bindings emit a `DeltaProfile`; see below |
| **EDM annotations** | a member with no queryable binding is marked `NotFilterable`/`NotSortable`, so `$filter` over it is a clean `400` from machinery that already exists |

## The write half: generate, don't rebuild

`OhData.Mapper` **emits a `DeltaProfile`** through the existing public `AddDeltaProfile` surface. No
second write engine; `DeltaMappingCompiler`'s validation runs unchanged.

| binding | invertible? |
|---|---|
| `Property.From(o => o.X)` — direct | ✅ |
| `Property.From(o => o.Y)` — rename | ✅ |
| `Property.From(o => o.A.B)` — path | ❌ which `A`? create one? |
| `Collection.From(…).Element(…)` | ❌ relationship management |
| `Format(…)` / `Compute(…)` | ❌ |
| `Ignore(…)` | n/a |

Non-invertible bindings must be explicitly mapped, converted or ignored for writes — which is
precisely what `DeltaMappingCompiler` already requires and reports. `DeltaProfile` remains usable
standalone, so #243's "separate, dependency-free subsystem" intent survives: the mapper *drives* it.

This also settles the validation-default question. One subsystem, one default: **unconditional**,
matching delta mapping, with `Ignore()` to exempt a member.

## Query surface

The requirement is full breadth and depth. In scope unless marked.

| construct | handling |
|---|---|
| `$filter` — comparison, logical, arithmetic, `in`, `has` | substitution, then EF |
| `$filter` — string/date/math canonical functions | substitution, then EF |
| `$filter` — `any`/`all` over a mapped collection | range-variable substitution through the element path (fact 3) |
| `$filter`/`$orderby` — nested navigation paths | path substitution |
| `$orderby` — multi-key, asc/desc | substitution |
| `$select`, incl. nested under `$expand` | member → entity column, preserving #206 pushdown |
| `$expand` + nested `$filter`/`$orderby`/`$top`/`$skip`/`$count`/`$select` | conditional composition |
| `$levels` on a self-referential mapped navigation | conditional composition, bounded by `MaxExpansionDepth` |
| `$top`/`$skip`/`$count`, `/$count` segment | applied to the entity query |
| `$search` | only with a supplied `Search` handler |
| `$compute`, `$apply` | **out of scope** — unimplemented framework-wide (owner ruling: the mapper is complete relative to what OhData supports, not to the spec as a whole) |
| property and `/$value` routes | **free** — the core reads the member off the returned DTO |
| `$ref`, navigation `POST` | delegates |
| writes | the generated `DeltaProfile` |

**`HonouredQueryOptions` is not needed on this architecture** — it exists for Priority-1, where the
framework cannot know what the profile honoured. Here the core applies the options itself, so its
existing per-route implemented-option sets and the `501`/`400` taxonomy apply unchanged. That is a
second reason the seam beats P1: there is no partial-coverage story to manage, because coverage is
the core's and is already complete.

## Architecture: the mapper owns mapping, the core owns query execution

**Priority-1 (`GetODataQueryable`) is the wrong seam and is rejected.** It hands the profile the
parsed `ODataQueryOptions` and applies *nothing* — which is why `HonouredQueryOptions` (#475) exists
and why #379 obliges a P1 profile to compute `TotalCount` itself. Implementing it means owning
`$filter`, `$orderby`, `$skip`/`$top`, `MaxTop`, `@odata.nextLink`, `$count`, `$select` pushdown and
**the whole of `$expand`** — engaged navigations, nested options, `$levels`, `MaxExpandTop`, expand
paging. That is thousands of lines in the core (`BindNavShape`, `ApplyNavShape`,
`TryBuildProjectionInit`, `ExpandLevelAsync`, the #334 count carrier, `ShapeLevelsInJson`) which the
package would have to clone.

Query execution is not mapping. The division is:

| owns | what |
|---|---|
| **mapper** | model ↔ entity: the projection with the engaged navigations bound, predicate/sort substitution, the write map |
| **core** | everything it already does — applies `$filter`/`$orderby`/`$select`/`$top`/`$skip`/`$count`/`$expand`/`$levels`/paging to whatever queryable it is handed |

### The one new core seam

`GetQueryable` takes no request context, which is the only reason the mapper cannot bind navigations
conditionally. Add a second, **additive** delegate alongside it:

```csharp
protected Func<OhDataQueryShape, IQueryable<TModel>>? GetShapedQueryable = null;
```

`OhDataQueryShape` carries the engaged expand tree (top-level and nested). Both delegates are
checked; startup refuses a profile that sets both, exactly as #378 warns for `GetAll` +
`GetQueryable`. Everything downstream is unchanged — the core applies every option to the returned
queryable as it does today, and does not need to know it was mapped.

This is the same additive shape recorded for a future `GetQueryableAsync` in #653: a second optional
delegate, both checked at registration, nothing existing breaks.

### The rest of the surface

| surface | driven by |
|---|---|
| collection `GET` and every query option | `GetShapedQueryable` + the core's existing pipeline |
| `GET /Set({key})` | `GetById` |
| property and `/$value` routes | **free** — the core reads the member off the returned DTO |
| navigation routes, `/$count`, `/$ref` | `HasMany`/`HasOptional` delegates |
| writes | `Post`/`Put`/`Patch` + the generated `DeltaProfile` |

## Startup validation

1. **Completeness** — every DTO member has a binding or an explicit `Ignore()`. Reflection only.
2. **Translatability** — every `Compute` binding translates, probed with `ToQueryString()`. Fails
   naming the member, turning #662's runtime 500 into a startup error.
3. **Write completeness** — `DeltaMappingCompiler`, unchanged.

## Testing

Requirement: 100% coverage.

- **Conformance oracle (the backbone).** The same request against a mapped profile and an
  entity-backed profile must produce **byte-identical** responses wherever both are supported. This
  is a far stronger check than line coverage and catches the defect class this repo keeps hitting.
- **SQL-shape tests** — EF Core/SQLite with command capture, asserting emitted SQL, in the style of
  `ExpandPushdownSqliteTests`. Every claim in this document becomes an assertion.
- **Unit** — the substituter per construct: every operator, canonical function, `any`/`all`, nested
  path, multi-key ordering.
- **Invertibility table** — exhaustive, one case per row above.
- **Startup validation** — every rejection, each naming the member.
- **Negative** — every unhonoured option answers `501`, never a silent drop.

## Placement

Same repo, `src/OhData.AspNetCore.Mapper`, shipping as `EnGen.OhData.AspNetCore.Mapper`,
multi-targeting `net8.0;net10.0`.

- The conformance oracle only works in-repo; split, it would assert against a *released* core.
- The version coupling is unusually tight — the handler surface changed three times in 2.0.0 alone
  (#581, #641, #653); a separate repo would have been broken for the entire cycle.
- Precedent: all three existing companions live here as `EnGen.OhData.AspNetCore.*`.
- `net8.0` is load-bearing per `CLAUDE.md` — the substituter may not use .NET 9+ reflection APIs.

## Non-goals

- A general-purpose mapper. The vocabulary is closed: `Property/From`, `Collection/From/Element`,
  `Compute`, `Ignore`.
- Replacing view-mapped entities, which remain the zero-code recommendation for adopters with DDL
  rights.
- `$compute`/`$apply`.

## Risks

1. **The new seam must not fork the read path.** `GetShapedQueryable` has to enter the *same*
   pipeline as `GetQueryable`, not a parallel one, or the two will drift — this repo's recurring
   defect class. The implementation should resolve to one queryable and continue, with the shape
   parameter the only difference.
2. **~45 sites across 5 core files resolve a model member name.** The package avoids them by supplying
   delegates, but a future core change resolving a member outside the delegate boundary would break
   mapped profiles silently — this repo's recurring defect class (#458, #462, #507, #508, #511, #536).
3. **Scope.** Sixth shipping package: csproj, packaging, ApiCompat baseline, docs, CI, publish
   pipeline, on top of the feature.


---

## Revision, after implementation: the seam is Priority-1 after all

The section *"Architecture: the mapper owns mapping, the core owns query execution"* above rejects
Priority-1 and proposes a new additive core delegate, `GetShapedQueryable`, returning
`IQueryable<TModel>` with the engaged navigations bound. **That is withdrawn.** It cannot be built
without the thing the owner ruled out twice — a DTO-typed EF queryable produced by
`Select(o => new Dto { … })` — because handing the core an `IQueryable<TModel>` means the provider
must translate members the entity does not have. *"I no longer want to rely on efcore
`.Select(x => new Dto{...})` strategies because they are flawed"* and *"why do we keep caring if
dto-typed ef queryable works? this is not a use case I'm interested in supporting"* both foreclose
it. The section's own argument against P1 stands on its merits and was simply outweighed.

What made P1 affordable is a fact the rejection did not account for: **the P1 route calls the same
`ApplyCollectionPipelineAsync` every other collection route calls.** So the feared cost — "owning
`$select` pushdown and the whole of `$expand`, `$levels`, `MaxExpandTop`, the #334 count carrier,
`ShapeLevelsInJson`" — is not incurred. The profile owns `$filter`, `$orderby`, `$top`, `$skip`,
`$count` and paging; `$select`, `$expand` (through navigation delegates, including #650's nested
options), ETags and the envelope stay the core's, unchanged. `HonouredQueryOptions` (#475) then does
exactly the job it was built for: it declares that set, and everything outside it is refused with
`501` rather than dropped.

Two other decisions moved with it.

**The projection is composed onto the entity query, not applied after materialisation.** The
"map after materialisation" plan was implemented first and the conformance oracle rejected it on its
first run: EF returns an entity with its references unloaded, so `CategoryName` — `o.Category.Name` —
rendered `null` on every row. Loading the graph first would mean `Include`ing every reference on
every request, which is the over-fetch this design exists to avoid. Composing a **scalar-only**
projection onto the already-filtered, already-paged entity query has neither problem, and it is not
the strategy that was ruled out: no navigation appears in it, the core never receives a model-typed
queryable, and `$expand` is served by separate batched queries.

**Delta mapping does not move in this change.** It is a mechanical break across seven projects with
its own compatibility surface, and bundling it would hide the new code in the diff. `DeltaExtensions`
stays in the core either way, per the owner's ruling.
