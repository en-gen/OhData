# API model / entity separation

`EnGen.OhData.AspNetCore.Mapper` lets an entity set serve an **API model** (a DTO) that differs
from the **EF Core entity** behind it, without giving up OData semantics. `$filter`, `$orderby`,
`$top`, `$skip` and `$count` still run in SQL, against the entity, even when the client writes them
in terms of members the entity does not have.

```
dotnet add package EnGen.OhData.AspNetCore.Mapper
```

## Why this needs a package

The obvious approach — project the entity into the DTO and let OData query the result — does not
work, for two independent reasons.

**Project, then filter, filters in memory.** `db.Products.ToList().Select(p => new ProductDto {…})`
materialises the whole table before the predicate is applied.

**Project into an `IQueryable<TDto>` and `$expand` stops working.** A `Select(p => new ProductDto
{…})` has no request context, so it has to bind every navigation any request might ever ask for, on
every request — the opposite of what `$expand` is for. And a member the provider cannot translate
fails at the row, as a 500, rather than at startup.

So the mapper does not ask you for a projection. You declare **where each model member comes
from**, and it composes whatever query a given request needs.

## Declaring the correspondence

The examples below are written against this pair — an entity shaped for storage, and a model shaped
for the wire. The join entity, the internal cost and the split name are exactly the things the API
should not have to expose.

<!-- compile -->
```csharp
namespace ShopApi;

public class Category { public int Id { get; set; } public string Name { get; set; } = ""; }
public class Tag      { public int Id { get; set; } public string Label { get; set; } = ""; }
public class Review   { public int Id { get; set; } public int Stars { get; set; } }
public class ProductTag { public Product Product { get; set; } = null!; public Tag Tag { get; set; } = null!; }

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string First { get; set; } = "";
    public string Last { get; set; } = "";
    public decimal InternalCost { get; set; }
    public Category Category { get; set; } = null!;
    public List<ProductTag> Links { get; set; } = new();
    public List<Review> Reviews { get; set; } = new();
}

public class ProductDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public CategoryDto? Category { get; set; }
    public List<TagDto> Tags { get; set; } = new();
    public List<ReviewDto> Reviews { get; set; } = new();
    public DateTime RenderedAt { get; set; }
}

public class CategoryDto { public int Id { get; set; } public string Name { get; set; } = ""; }
public class TagDto      { public int Id { get; set; } public string Label { get; set; } = ""; }
public class ReviewDto   { public int Id { get; set; } public int Stars { get; set; } }

public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
}
```

<!-- compile -->
```csharp
namespace ShopApi;

public sealed class ProductProfile : MappedEntitySetProfile<int, ProductDto, Product>
{
    public ProductProfile(ShopDb db) : base(d => d.Id)
    {
        EntitySetName = "Products";
        FilterEnabled = OrderByEnabled = SelectEnabled = ExpandEnabled = CountEnabled = true;

        UseMap(() => db.Products.AsNoTracking(), m => m
            .Root(r =>
            {
                r.Property(d => d.Id).From(o => o.Id);
                r.Property(d => d.Title).From(o => o.Name);
                r.Property(d => d.CategoryName).From(o => o.Category.Name);
                r.Property(d => d.DisplayName).Format(o => $"{o.First} {o.Last}");
                r.Reference(d => d.Category, o => o.Category);
                r.Collection(d => d.Tags).From(o => o.Links).Element(l => l.Tag);
                r.Collection(d => d.Reviews).From(o => o.Reviews).AsIs();
                r.Ignore(d => d.RenderedAt);
            })
            .Nested<Category, CategoryDto>(c =>
            {
                c.Property(d => d.Id).From(o => o.Id);
                c.Property(d => d.Name).From(o => o.Name);
            })
            .Nested<Tag, TagDto>(t =>
            {
                t.Property(d => d.Id).From(o => o.Id);
                t.Property(d => d.Label).From(o => o.Label);
            })
            .Nested<Review, ReviewDto>(v =>
            {
                v.Property(d => d.Id).From(o => o.Id);
                v.Property(d => d.Stars).From(o => o.Stars);
            }));
    }
}
```

Register it like any other profile:

<!-- compile -->
```csharp
using ShopApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOhData(o => o.AddEntitySetProfile<ProductProfile>());
```

## The binding kinds

The vocabulary is deliberately **closed**. Every model member resolves to exactly one of these,
which is what makes the correspondence enumerable, exhaustively testable and — for the path kinds —
translatable to SQL *by construction* rather than by hope.

| kind | declaration | translatable | invertible |
|---|---|---|---|
| Direct | `Property(d => d.Code).From(o => o.Code)` | yes | yes |
| Rename | `Property(d => d.Title).From(o => o.Name)` | yes | yes |
| Path | `Property(d => d.CategoryName).From(o => o.Category.Name)` | yes (a JOIN) | no |
| Format | `Property(d => d.DisplayName).Format(o => $"{o.First} {o.Last}")` | yes | no |
| Compute | `Property(d => d.X).Compute(o => …)` | probe it | no |
| Reference | `Reference(d => d.Category, o => o.Category)` | yes | no |
| Collection | `Collection(d => d.Tags).From(o => o.Links).Element(l => l.Tag)` | yes | no |
| Ignored | `Ignore(d => d.RenderedAt)` | n/a — leaves the EDM | n/a |

**"Invertible"** means a write can be routed back through it: only a member that names exactly one
entity member can be. A path cannot decide whether to update the related row or create one, a
reshaped collection is relationship management, and a computed value has no inverse at all.

### `Format`, and why it is not just `Compute`

`Format` takes a string interpolation and the mapper decomposes it into folded two-argument
`string.Concat`, which SQL evaluates as `||`. That matters: measured on EF Core 10, the
interpolation as written and the params-array `Concat(string[])` overload both *project* — the final
`Select` is one of the few things EF still evaluates on the client — but both **throw** when they
appear in a `WHERE`. Writing the same interpolation inside a `Compute` therefore gives you a member
that renders correctly and cannot be filtered or sorted on.

An alignment or a format specifier — `$"{o.Price:C}"`, `$"{o.Name,10}"` — has no SQL equivalent and
is **refused at startup**, naming the member. Format the value in the database with `Compute`, or
expose the raw member and format it on the client.

### `Ignore` withdraws the member entirely

`Ignore(d => d.RenderedAt)` forwards to the profile's own `EntitySetProfile.Ignore`, so the member
leaves `$metadata` and the payload together. That is deliberate: a member with no entity source
cannot be evaluated, so leaving it in the EDM would let a client write `$filter=RenderedAt eq …`
and get a server fault for a query only the map could refuse. With it withdrawn, the framework's own
`400` answers.

### `Collection` and the join entity

`Collection(d => d.Tags).From(o => o.Links).Element(l => l.Tag)` says the model's `Tags` comes from
the entity's `Links` collection, one hop further through `l => l.Tag`. The many-to-many join entity
never appears in the model, in `$metadata`, or on the wire — and a lambda written against the model
still reaches the database:

```
GET /odata/Products?$filter=Tags/any(t: t/Label eq 'sale')
```

becomes a correlated `EXISTS` over `ProductTags` joined to `Tags`. Use `AsIs<TSource>()` instead of
`Element(...)` when the source elements already are the element entity.

## What is supported

`$filter` and `$orderby` are parsed against the model and bound by
`Microsoft.AspNetCore.OData`'s **own** `FilterBinder`/`OrderByBinder` — the same binders the core
uses for a nested expand — and only then rewritten into entity terms. So every operator, canonical
function and lambda the framework supports is bound by the framework's binder; this package adds a
mechanical member swap, which cannot introduce a semantic difference of its own.

| construct | handled |
|---|---|
| `$filter` — comparison, logical, arithmetic, `in` | yes, in SQL |
| `$filter` — canonical string/math functions | yes, in SQL |
| `$filter` — `any`/`all` over a mapped collection | yes, as a correlated `EXISTS` |
| `$filter`/`$orderby` — paths through a reference | yes, as a JOIN |
| `$orderby` — multi-key, asc/desc | yes, in SQL |
| `$top`, `$skip`, `$count` | yes; the count is a separate round-trip, before any window |
| `$select` | yes — the core's own JSON pass, unchanged |
| `$expand`, incl. nested `$filter`/`$orderby`/`$top`/`$skip`/`$count`/`$select` | yes — one batched query per navigation per page |
| server-driven paging, `@odata.nextLink`, `Prefer: maxpagesize` | yes |
| `GET /Set({key})`, property and `/$value` routes | yes |
| `$search` | only with your own handler; see below |
| `$apply`, `$compute` | **no** — unimplemented framework-wide, so they are refused with `501` |
| writes | your own `Post`/`Put`/`Patch`, plus [delta mapping](delta-mapping.md) |

Anything not in that list is **refused**, never silently dropped — §11.2.5 is a MUST-fail on an
unsupported system query option. A `$skiptoken` from the client is refused too: this profile pages
with `$skip`-bearing continuations, which it provably re-reads.

## How a request is served

1. `$filter` and `$orderby` are bound against the model, rewritten, and applied to
   `IQueryable<TEntity>`.
2. `$count`, if asked for, is taken **after** the filter and **before** any window.
3. A tie-breaking sort on the key is appended, so paging is deterministic.
4. `$skip`/`$top` are applied, bounded by `MappedPageSize`, fetching one row past the page.
5. That page — and only that page — is projected into the model, in the provider.
6. `$expand` runs one batched query per navigation, keyed by the page's parent keys.
7. `$select`, ETags and the envelope come from the core's shared collection pipeline, unchanged.

Nothing is filtered, sorted or paged in memory.

## Paging

A mapped profile is a Priority-1 profile, so it owns paging and serves at most `MappedPageSize`
rows (default **1000**, which is `EntitySetDefaults.MaxTop`'s own default) with an
`@odata.nextLink` when more remain:

<!-- compile -->
```csharp
namespace ShopApi;

public sealed class SmallPageProfile : MappedEntitySetProfile<int, ProductDto, Product>
{
    public SmallPageProfile(ShopDb db) : base(d => d.Id)
    {
        MappedPageSize = 50;
        MaxTop = 200;
        UseMap(() => db.Products.AsNoTracking(), m => m.Root(r => r.Property(d => d.Id).From(o => o.Id)));
    }
}
```

`Prefer: maxpagesize=N` narrows the page and is echoed as `Preference-Applied`; a preference that
would *widen* it is neither honoured nor announced, which is what RFC 7240 requires. A client `$top`
larger than the page is carried forward on the continuation, reduced by what has already been
served.

## Startup validation

Validation is **unconditional**, because every condition it catches produces a plausible `200` with
the wrong body rather than a loud failure:

- every model member has a binding or an explicit `Ignore()`;
- the model type is constructible;
- every navigation reaches a model type that has a `Nested<,>` map, declared from the right entity;
- every `Format(...)` really is an interpolation.

Each message names the member and the remedy.

`Compute(...)` is the one kind whose translatability its shape cannot guarantee, so probe it
against your own provider — from a health check, or a test:

<!-- compile -->
```csharp
using ShopApi;

public static class MapHealthCheck
{
    public static void CheckMap(ModelMap map, ModelMapRegistry registry, IQueryable<Product> source)
{
        IReadOnlyList<(string Member, string Reason)> untranslatable =
            ModelMapValidator.ProbeTranslatability(map, registry, source, q => q.ToQueryString());

        if (untranslatable.Count > 0)
        {
            throw new InvalidOperationException(string.Join(
            "; ", untranslatable.Select(f => $"{f.Member}: {f.Reason}")));
        }
    }
}
```

The probe composes each binding into an `OrderBy` rather than a `Select`, deliberately: a final
`Select` is one of the few clauses EF Core is still allowed to evaluate on the client, so a `Select`
probe would pass for a member no `$filter` could ever use.

## `$search`

`$search` is not in `HonouredQueryOptions` by default, so it is refused with `501` — the same answer
every unimplemented system query option gets. Declare it and read `options.Search` yourself if your
store can answer it:

```csharp
HonouredQueryOptions |= OhDataSystemQueryOption.Search;
```

## Writes

The mapper covers the read path. Writes stay yours — `Post`, `Put`, `Patch` receive the model, and
[delta mapping](delta-mapping.md) turns a `Delta<TModel>` into a `Delta<TEntity>` you apply. Only
Direct and Rename bindings are invertible, so a model whose writable members are all one of those
maps cleanly; anything else needs a handler that decides what a write to it means.

## When you do not need this

If you have DDL rights on the database, a **view** plus an entity mapped to it is still the
zero-code answer: EF Core sees a normal entity with real navigations, and OhData needs to know
nothing. Reach for this package when the API shape has to differ from the storage shape and you
cannot or do not want to express that difference in the database.
