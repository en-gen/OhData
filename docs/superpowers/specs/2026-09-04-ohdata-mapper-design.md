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
4. **`+` concatenation translates; interpolation and `string.Format` do not.** `p.First + " " + p.Last` → `"p"."First" || ' ' || "p"."Last"`, filterable. `$"{p.First} {p.Last}"` throws.
5. **`ToQueryString()` forces translation without executing** — the startup-validation mechanism.
6. **View-mapped entities already solve the whole problem with no code** for adopters with DDL rights: real navigations, conditional joins, filterable. This package is for those without.

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
            m.Property(d => d.DisplayName).Compute(o => o.First + " " + o.Last);
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

`Compute` is the single escape hatch, is opt-in, and is the only place EF's translation rules reach
the declaration — which is exactly what the startup probe (fact 5) exists to police, catching fact 4's
traps at build time.

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
| `Compute(…)` | ❌ |
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
| `$compute`, `$apply` | **out of scope** — unimplemented framework-wide |
| property and `/$value` routes | **free** — the core reads the member off the returned DTO |
| `$ref`, navigation `POST` | delegates |
| writes | the generated `DeltaProfile` |

Anything unimplemented is declared via **`HonouredQueryOptions`** (#475), so the core answers it with
a clean `501` rather than dropping it. Partial coverage is honest by construction, and the package can
widen without ever lying.

## Architecture

**The package supplies the delegate set; the core is unchanged.** OhData's route surface is
delegate-driven, so the core keeps seeing only the DTO — its EDM, `$metadata`, property routes and
allowlists all operate on it.

| surface | driven by |
|---|---|
| collection `GET` and every query option | `GetODataQueryable` (Priority-1) |
| `GET /Set({key})` | `GetById` |
| property routes | free |
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

1. **Priority-1 means the package owns every option it declares.** It does not inherit the core's
   pushdown, nested-option handling or `$levels`; `HonouredQueryOptions` bounds the exposure.
2. **~45 sites across 5 core files resolve a model member name.** The package avoids them by supplying
   delegates, but a future core change resolving a member outside the delegate boundary would break
   mapped profiles silently — this repo's recurring defect class (#458, #462, #507, #508, #511, #536).
3. **Scope.** Sixth shipping package: csproj, packaging, ApiCompat baseline, docs, CI, publish
   pipeline, on top of the feature.
