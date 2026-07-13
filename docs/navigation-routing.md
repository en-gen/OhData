# Navigation Property Routing

OhData supports two complementary ways to expose related entities:

- **Navigation routes** - a standalone `GET /Parents({key})/Children` endpoint that returns the related collection as a top-level response
- **`$expand`** - embeds related data inline inside the parent entity response

Both require the navigation property to be declared in the EDM model. The approach you choose (or both) depends on what your clients need.

## Declaring a navigation property

Use `HasMany`, `HasOptional`, or `HasRequired` inside the profile constructor:

```csharp
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        ExpandEnabled = true;

        // EDM-only - adds nav property to $metadata and enables $expand; no route registered
        HasMany(x => x.Lines);
        HasOptional(x => x.Customer);

        GetQueryable = (_) => Task.FromResult(db.Orders.AsQueryable());
    }
}
```

## Registering a navigation route

Pass a handler delegate to register a `GET /Parents({key})/Children` route:

```csharp
HasMany(x => x.Lines,
    getAll: (orderId, ct) =>
        Task.FromResult<IEnumerable<OrderLine>>(
            db.OrderLines.Where(l => l.OrderId == orderId).ToList()));
```

This registers: `GET /odata/Orders({key})/Lines`

For single-entity navigations (`HasOptional`, `HasRequired`):

```csharp
HasOptional(x => x.Customer,
    get: (orderId, ct) =>
        Task.FromResult(db.Customers.Find(order.CustomerId)));
```

This registers: `GET /odata/Orders({key})/Customer`

## Navigation route behaviour

- Returning `null` from the handler produces `404 Not Found`
- Authorization from the parent profile's `RequireAuthorization()` / `RequireRoles()` is applied to navigation routes automatically
- Navigation routes are tagged with the parent entity set name in OpenAPI/Swagger
- For collection navigations, `$top`, `$skip`, `$count`, and `$select` are honored on the returned collection via ad-hoc in-memory `IEnumerable` operations (not pushed down to the handler or to SQL). `$orderby` is **not** applied - it is silently accepted and ignored (a no-op) rather than sorting or returning an error.

## `$expand`

When `ExpandEnabled = true` and navigation properties are declared, clients can embed related data in the parent response:

```
GET /odata/Orders?$expand=Lines
GET /odata/Orders?$expand=Lines($select=ProductName,Quantity),Customer
GET /odata/Orders(id)?$expand=Lines
```

`$expand` does **not** translate to EF Core's `Include()`. Instead, for each requested navigation property, the framework calls the registered navigation route handler (the `getAll`/`get` delegate passed to `HasMany`/`HasOptional`/`HasRequired`) once per parent entity in the result set - an N+1 pattern. This is identical across all three collection GET paths (`GetQueryable`, `GetAll`, and the `IODataEntitySetEndpointSource` priority-1 path); none of them push the expand down into the underlying `IQueryable`/SQL query. A navigation property with no registered handler is silently skipped during expansion.

## Navigation routes vs `$expand` - when to use each

| | Navigation route | `$expand` |
|---|---|---|
| Returns related data as top-level response | ✅ | ❌ (embedded in parent) |
| Supports filtering/ordering on related data | ❌ | ✅ (with nested options) |
| Single SQL join (vs. N+1 handler calls) | ❌ (separate query per request) | ❌ (also N+1 - see note above; not pushed down to SQL) |
| Works without `$expand` support on client | ✅ | ❌ |

The two approaches are complementary - declare both to support both access patterns.

## `$ref` - managing links between entities

For many-to-many or reference relationships, OhData supports `$ref` link management endpoints that add or remove associations without transferring full entity bodies.

```csharp
HasMany(x => x.Tags,
    getAll: (productId, ct) => Task.FromResult<IEnumerable<Tag>>(
        db.ProductTags.Where(pt => pt.ProductId == productId).Select(pt => pt.Tag).ToList()),
    addRef: (productId, tagId, ct) =>
    {
        db.ProductTags.Add(new ProductTag { ProductId = productId, TagId = int.Parse(tagId) });
        return db.SaveChangesAsync(ct);
    },
    removeRef: (productId, tagId, ct) =>
    {
        var link = db.ProductTags.Find(productId, int.Parse(tagId));
        if (link is not null) db.ProductTags.Remove(link);
        return db.SaveChangesAsync(ct);
    });
```

This registers:

| Route | Handler |
|-------|---------|
| `GET /Products({key})/Tags` | `getAll` |
| `POST /Products({key})/Tags/$ref` | `addRef` - body: `{ "@odata.id": "Tags(5)" }` |
| `DELETE /Products({key})/Tags/$ref?$id=Tags(5)` | `removeRef` |

For optional single-entity navigations, use the `setRef` / `removeRef` overload on `HasOptional`:

```csharp
HasOptional(x => x.Category,
    get: (productId, ct) => ...,
    setRef: (productId, categoryId, ct) => ...,
    removeRef: (productId, categoryId, ct) => ...);
```

This registers:

| Route | Handler |
|-------|---------|
| `GET /Products({key})/Category` | `get` |
| `PUT /Products({key})/Category/$ref` | `setRef` - body: `{ "@odata.id": "Categories(3)" }` |
| `DELETE /Products({key})/Category/$ref` | `removeRef` (no `$id` — there is only one link) |

The `addRef`/`setRef` handler receives the raw `@odata.id` string from the request body (e.g. `"Categories(3)"`). Parse the key from it as needed.

> **HTTP method note:** OData 4.0 §11.4.6 requires `POST /$ref` for collection navigations (adding a link)
> and `PUT /$ref` for single-value navigations (replacing the link). OhData enforces this automatically.
