# Navigation Property Routing

OhData supports two complementary ways to expose related entities:

- **Navigation routes** — a standalone `GET /Parents({key})/Children` endpoint that returns the related collection as a top-level response
- **`$expand`** — embeds related data inline inside the parent entity response

Both require the navigation property to be declared in the EDM model. The approach you choose (or both) depends on what your clients need.

## Declaring a navigation property

Use `HasMany`, `HasOptional`, or `HasRequired` inside the profile constructor:

```csharp
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        ExpandEnabled = true;

        // EDM-only — adds nav property to $metadata and enables $expand; no route registered
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

## `$expand`

When `ExpandEnabled = true` and navigation properties are declared, clients can embed related data in the parent response:

```
GET /odata/Orders?$expand=Lines
GET /odata/Orders?$expand=Lines($select=ProductName,Quantity),Customer
GET /odata/Orders(id)?$expand=Lines
```

On the `GetQueryable` path with EF Core, `$expand` translates to `Include()` — the database join is performed in a single query. On the `GetAll` path, the framework calls the registered navigation handler for each entity (N+1).

## Navigation routes vs `$expand` — when to use each

| | Navigation route | `$expand` |
|---|---|---|
| Returns related data as top-level response | ✅ | ❌ (embedded in parent) |
| Supports filtering/ordering on related data | ❌ | ✅ (with nested options) |
| SQL join on `GetQueryable` path | ❌ (separate query per request) | ✅ (EF Core `Include`) |
| Works without `$expand` support on client | ✅ | ❌ |

The two approaches are complementary — declare both to support both access patterns.

## `$ref` — managing links between entities

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
| `POST /Products({key})/Tags/$ref` | `addRef` — body: `{ "@odata.id": "Tags(5)" }` |
| `DELETE /Products({key})/Tags/$ref?$id=Tags(5)` | `removeRef` |

For optional single-entity navigations, use the `setRef` / `removeRef` overload on `HasOptional`:

```csharp
HasOptional(x => x.Category,
    get: (productId, ct) => ...,
    setRef: (productId, categoryId, ct) => ...,
    removeRef: (productId, categoryId, ct) => ...);
```

The `addRef`/`setRef` handler receives the raw `@odata.id` string from the request body (e.g. `"Categories(3)"`). Parse the key from it as needed.
