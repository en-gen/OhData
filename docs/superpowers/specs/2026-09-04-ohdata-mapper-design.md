# OhData.Mapper — API model / persistence model separation, design

**Status:** design, pre-implementation
**Milestone:** 2.0.0 (owner decision)
**Issue:** to file

## Problem

An adopter wants the type on the wire to differ from the EF entity. Today OhData offers three ways
and each fails at something, all measured on 2026-09-04 against EF Core 10 / SQLite with the SQL
captured:

| approach | plain `GET` | `$expand` | `$filter` through the nav |
|---|---|---|---|
| entity as model | ✅ | ✅ | ✅ (no separation at all) |
| DTO, nav projected eagerly | ❌ `LEFT JOIN` + **every child row fetched and discarded** | ✅ 1 query | ✅ `WHERE EXISTS` |
| DTO, nav via `batchGetAll` | ✅ clean | ✅ 2 queries | ❌ **500** (issue #662) |

There is no DTO configuration that gets all three. The eager form is the one that "supports
`$expand`", and it obliges the adopter to hand-write **every navigation they might ever expand** into
one `Select` — paying for all of them on every request, including requests that expand nothing.

That fights the premise of the stack: OData + EF Core is valuable because the client's query
translates end-to-end, with `IQueryable` as the glue. Projecting away from EF breaks the glue.

## What is already true, and must not be re-derived

Measured this session; each is load-bearing for the design.

1. **EF composes a predicate back through a projection.** `db.Prods.Select(p => new Dto { CategoryName = p.Cat.Name })` filtered by `CategoryName eq 'Tools'` emits `INNER JOIN "Cats" … WHERE "c"."Name" = @p`. Scalar mapping needs **no** substitution machinery for the root read.
2. **An outer projection prunes an inner join.** Composing `Select(d => new Dto { Id = d.Id, Code = d.Code })` over the eager projection turns `SELECT …, l.Id, l.Sku FROM Orders LEFT JOIN Lines` into `SELECT Id, Code FROM Orders`. EF eliminates the collection subquery entirely.
3. **Binding substitution works through a reshaped collection.** `d => d.Tags.Any(t => t.Label == "sale")` rewritten from the map's bindings becomes `p => p.Links.Any(l => l.Tag.Label == "sale")` and translates to a correlated `EXISTS`. This is the M2M-elision case and it is the feature's reason to exist.
4. **`+` concatenation translates; interpolation and `string.Format` do not.** `p.First + " " + p.Last` becomes `"p"."First" || ' ' || "p"."Last"` and is filterable; `$"{p.First} {p.Last}"` throws.
5. **`ToQueryString()` forces translation without executing** — the startup-validation mechanism.
6. **View-mapped entities already solve this with no code**, when the adopter has DDL rights: real navigations, conditional joins, filterable. The package is for adopters who do not.

## Architecture

**The package supplies delegates. The core is unchanged.**

OhData's entire route surface is delegate-driven, so a package that generates the delegate set has
full control while the core continues to see only the DTO — its EDM, its `$metadata`, its property
routes, its allowlists.

| surface | driven by |
|---|---|
| collection `GET` and every query option | `GetODataQueryable` (Priority-1) |
| `GET /Set({key})` | `GetById` |
| `GET /Set({key})/{Property}`, `/$value` | **free** — the core reads the member off the returned DTO |
| navigation routes, `/$count`, `/$ref` | the `HasMany`/`HasOptional` delegates |
| writes | `Post`/`Put`/`Patch` + the existing `DeltaProfile`/`IDeltaFactory` |
| `$metadata` | the core, from the DTO |

### The declaration

One map per type; navigations declared with their own element map.

```csharp
public sealed class OrderProfile : MappedEntitySetProfile<int, Order, OrderDto>
{
    public OrderProfile(AppDbContext db) : base(db.Orders, x => x.Id)
    {
        Map(o => new OrderDto
        {
            Id           = o.Id,
            Code         = o.Code,
            CategoryName = o.Category.Name,
        });

        MapMany(d => d.Tags, o => o.Links, l => new TagDto { Id = l.Tag.Id, Label = l.Tag.Label });
    }
}
```

### The primitive

Everything reduces to one operation:

> **resolve a model member path → an expression over the entity**

`$filter`, `$orderby`, `$select`, `$expand`, `$levels`, property routes, `/$count`, key segments and
the ETag selector all consume it. One resolver, N consumers — the rule #467 exists to enforce.

### Conditional graph composition

The member-init is assembled **per request** from the engaged expand tree, so a navigation costs
nothing when it is not expanded (fact 2). This is the defect the eager projection has today.

## Scope — the full query surface

The owner's requirement is full breadth and depth. Each row is in scope unless marked.

| construct | handling |
|---|---|
| `$filter` — comparison, logical, arithmetic, `in`, `has` | substitution, then EF |
| `$filter` — string/date/math canonical functions | substitution, then EF |
| `$filter` — `any`/`all` over a mapped collection | range-variable substitution through the element map (fact 3) |
| `$filter` — nested navigation paths | path substitution |
| `$orderby`, multi-key, asc/desc, through paths | substitution |
| `$select`, incl. nested under `$expand` | member → entity column, preserving #206 pushdown |
| `$expand` + nested `$filter`/`$orderby`/`$top`/`$skip`/`$count`/`$select` | conditional composition |
| `$levels` on a self-referential mapped navigation | conditional composition, bounded by `MaxExpansionDepth` |
| `$top`/`$skip`/`$count`, `/$count` segment | applied to the entity query |
| `$search` | only if a `Search` handler is supplied; otherwise unhonoured |
| `$compute`, `$apply` | **out of scope** — unimplemented framework-wide |
| property + `/$value` routes | free (core reads the DTO) |
| `$ref`, navigation `POST` | delegates |
| writes | `DeltaProfile` (exists) |

Anything not implemented is declared through **`HonouredQueryOptions`** (#475), so the core answers
an unsupported option with a clean `501` rather than silently dropping it. Partial coverage is
therefore honest by construction, and the package can widen over time without ever lying.

## Startup validation

Mirrors `DeltaMappingCompiler`, which already does this for the write side.

1. **Completeness** — every DTO member has a mapping or an explicit `Ignore()`. Reflection only.
2. **Translatability** — each declared map translates, probed with `ToQueryString()` (fact 5). Fails
   naming the member, converting #662's runtime 500 into a startup error.
3. **Type compatibility** — reuse delta mapping's rules.

**Default:** validate unconditionally, matching delta mapping, with `Ignore()` to exempt a member and
a flag to opt out wholesale. The owner asked for opt-in; the counter-argument is that two mapping
subsystems in one framework with different safety defaults is the split #647 exists to catch. **Open
for the owner's ruling.**

## Testing

Requirement: 100% coverage of the package.

- **Unit** — the substituter, per construct: every operator, every canonical function, `any`/`all`,
  nested paths, multi-key ordering.
- **SQL-shape** — EF Core/SQLite with command capture, asserting the emitted SQL, in the style of
  `ExpandPushdownSqliteTests`. The claims in this document are assertions, not prose.
- **Conformance** — the same request against a mapped profile and an entity-backed profile must
  produce byte-identical responses wherever both are supported. This is the strongest available
  oracle and should be the backbone of the suite.
- **Startup validation** — every rejection, each naming the member.
- **Negative** — every unhonoured option answers `501`, never a silent drop.

## Non-goals

- A general-purpose mapper. The vocabulary is deliberately closed: direct member, member path,
  translatable expression, collection projection. Anything EF cannot translate is refused at startup.
- Replacing view-mapped entities, which remain the zero-code recommendation for adopters with DDL
  rights.
- `$compute`/`$apply`.

## Risks

1. **The core has ~45 sites resolving a model member name across 5 files.** The package avoids them
   by supplying delegates, but any future core change that resolves a member name outside the
   delegate boundary would break mapped profiles silently. This repo's recurring defect class
   (#458, #462, #507, #508, #511, #536) is exactly "two things answering one question differently".
2. **Priority-1 means the package owns every option it declares.** It does not inherit the core's
   pushdown, nested-option handling or `$levels`. `HonouredQueryOptions` bounds the exposure.
3. **Scope.** This is the sixth shipping package: csproj, packaging metadata, ApiCompat baseline,
   docs, CI, publish pipeline — on top of the feature.
