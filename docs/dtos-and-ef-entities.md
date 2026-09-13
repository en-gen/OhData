# DTOs and EF entities

`EntitySetProfile<TKey, TModel>`'s `TModel` is the **API model** — the shape on the wire and in
`$metadata`. The getting-started quick start uses the EF entity as its own API model because
that is the shortest thing that works, not because the two must be the same type.

This page covers the dependency-free way to serve a DTO: project it in the handler and let EF
translate the projection. If the difference between model and entity is large enough that you would
rather *declare* it than hand-write the projection — and still have `$filter`/`$orderby` run in SQL
against the entity — [`EnGen.OhData.AspNetCore.Mapper`](api-model-mapping.md) does that instead.

## Reading: project in the handler

`TModel` is the DTO, and EF translates the projection to SQL, so
`$filter`/`$orderby`/`$select`/`$top` still push down:

```csharp
public class ProductProfile : EntitySetProfile<int, ProductDto>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled = OrderByEnabled = SelectEnabled = true;

        GetQueryable = () => db.Products.Select(p => new ProductDto
        {
            Id       = p.Id,
            Name     = p.Name,
            Category = p.Category.Name,   // flattened; still one SQL query
        });
    }
}
```

Query options are bound against `ProductDto`, so `$filter=Category eq 'Tools'` filters on the
projected member and EF pushes it into the `JOIN`. Nothing in the framework needs to know the entity
type exists.

## `$expand` on a DTO: declare the navigation with a batch delegate

Leave it out of the projection and give `HasMany` a `batchGetAll`, which loads the whole page's
children in one query:

```csharp
public class OrderProfile : EntitySetProfile<int, OrderDto>
{
    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        ExpandEnabled = true;

        HasMany<LineDto>(x => x.Lines, batchGetAll: (orderIds, ct) => Task.FromResult(
            db.Lines.Where(l => orderIds.Contains(l.OrderId))
                .Select(l => new { l.OrderId, Dto = new LineDto { Id = l.Id, Sku = l.Sku } })
                .ToLookup(x => x.OrderId, x => x.Dto)));

        // Lines is NOT in the projection.
        GetQueryable = () => db.Orders.Select(o => new OrderDto { Id = o.Id, Code = o.Code });
    }
}
```

`GET /Orders` issues one query and touches no child table. `GET /Orders?$expand=Lines` issues a
second, batched by key — one query for the page, not one per row:

```sql
SELECT "o"."Id", "o"."Code" FROM "Orders" AS "o" ORDER BY "o"."Id" LIMIT @p
SELECT "l"."OrderId", "l"."Id", "l"."Sku" FROM "Lines" AS "l" WHERE "l"."OrderId" IN (@k1, @k2)
```

Nested options work on it — `Lines($filter=…;$orderby=…;$top=…;$count=true)` are all applied,
bound by the same binders as the SQL path and evaluated over the loaded children.

### The alternative: project the navigation eagerly

It costs more than it looks:

```csharp
GetQueryable = () => db.Orders.Select(o => new OrderDto
    {
        Id    = o.Id,
        Code  = o.Code,
        Lines = o.Lines.Select(l => new LineDto { Id = l.Id, Sku = l.Sku }).ToList(),
    });
```

That folds into a member-init projection, so `$expand=Lines` is one query rather than two and nested
options push all the way to SQL. But the `LEFT JOIN` is in **every** query — the framework composes
no projection when the request carries neither `$select` nor `$expand`, so a plain `GET /Orders`
fetches every child row across the wire and discards them at serialization. Measured, that is not
"a JOIN is in the plan"; it is fetch-then-discard scaling with your fan-out.

So: **`batchGetAll` unless the navigation is expanded on essentially every request**, where the
single round-trip wins. A navigation in neither the projection nor a `HasMany` declaration is not
there to fold, and `$expand` of it fails loud rather than serving an empty collection.

**The trade is two-sided, and this is the other side.** A navigation served by `batchGetAll` is not
in the queryable, so a `$filter` *through* it - `?$filter=Lines/any(l: l/Sku eq 'X')` - has nothing
to translate against and answers `400`. The eager projection supports that filter; `batchGetAll`
does not. Choose on which matters more for the entity set in question: paying a `LEFT JOIN` on every
read, or giving up filtering through the navigation.

## You do not have to repeat the projection

The seam is only *"return an `IQueryable<TModel>`"*, so
anything that produces one works. With no dependency at all, declare the projection once and reuse it:

```csharp
public sealed class OrderDto
{
    public static readonly Expression<Func<Order, OrderDto>> Projection = o => new OrderDto
    {
        Id       = o.Id,
        Code     = o.Code,
        Category = o.Category.Name,   // flattened; still one SQL query
    };
    // ...
}

GetQueryable = () => db.Orders.Select(OrderDto.Projection);
```

A mapping library can generate that expression instead — OhData neither requires nor assumes one, and
takes no dependency on any. If you pick one, check its licence and whether it supports `IQueryable`
projection: **Mapperly** is Apache-2.0 and source-generated, **Mapster** is MIT, and **AutoMapper**'s
`ProjectTo` works here too but AutoMapper 16 is licensed under RPL-1.5 or a commercial agreement —
including transitively through the MIT-licensed `AutoMapper.Extensions.ExpressionMapping` and
`AutoMapper.AspNetCore.OData.EFCore` packages, which depend on it.

If you would rather filter against the **entity** and project last — translating a DTO-shaped predicate
into an entity-shaped one, which is what `AutoMapper.Extensions.ExpressionMapping` does — use the
Priority-1 handler
([`GetODataQueryable`](read-handlers.md#getodataqueryable---full-odata-pushdown-advanced)). It
hands your profile the whole `ODataQueryOptions` so you can translate and apply the clauses yourself.
Two obligations come with that seam: declare what you actually honour via `HonouredQueryOptions`, and
set `ODataQueryResult.TotalCount` if you page and support `$count`.

