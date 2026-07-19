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

### Batch-loaded navigation routes

`HasMany`, `HasOptional`, and `HasRequired` also accept a **batch** delegate instead of a
per-entity one. It receives every requested parent key at once instead of a single key,
which is what makes batch-aware `$expand` possible (see below):

```csharp
HasMany(x => x.Lines, batchGetAll: async (orderIds, ct) =>
{
    var lines = await db.OrderLines.Where(l => orderIds.Contains(l.OrderId)).ToListAsync(ct);
    return lines.ToLookup(l => l.OrderId);        // ILookup<TKey, TNavigation>
});

HasOptional(x => x.Customer, batchGet: async (orderIds, ct) =>
{
    var customerIds = await db.Orders.Where(o => orderIds.Contains(o.Id))
        .Select(o => new { o.Id, o.CustomerId }).ToListAsync(ct);
    var customers = await db.Customers
        .Where(c => customerIds.Select(x => x.CustomerId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
    return customerIds.ToDictionary(x => x.Id, x => (Customer?)customers.GetValueOrDefault(x.CustomerId));
});
```

Registering only the batch overload is sufficient: the framework auto-derives a per-entity
handler from it (by calling the batch delegate with a single-element key list), so
`GET /Orders({key})/Lines`, nav `$count`, and `$ref` all keep working exactly as if you had
written a separate per-entity handler. A parent key absent from the batch result is treated
as "no children" (`[]`) for `HasMany`, or "no related entity" (`null`) for
`HasOptional`/`HasRequired`.

## Navigation route behaviour

- Returning `null` from the handler produces `404 Not Found`
- Authorization from the parent profile is applied to navigation routes automatically — both the all-operations `RequireAuthorization()`/`RequireRoles()` and per-operation `ConfigureAuthorization(...)` (a nav read is `Read`, a nav `POST`/`$ref` write is `Create`/`Update`; `.RequireResource()` checks against the parent entity)
- Navigation routes are tagged with the parent entity set name in OpenAPI/Swagger
- For collection navigations, `$orderby`, `$top`, `$skip`, `$count`, and `$select` are honored on the returned collection via ad-hoc in-memory `IEnumerable`/`IEnumerable<T>.OrderBy` operations (not pushed down to the handler or to SQL). Options are applied in standard OData order: `$orderby`, then `$skip`, then `$top` (`$count` is captured after `$skip` but before `$top`, per spec - the count reflects the collection after skipping but before the page limit is applied). `$orderby` supports multiple sort keys (`Prop1 asc,Prop2 desc`) and is case-insensitive on the property name. An unknown property name in `$orderby` returns `400 Bad Request` (`InvalidQueryOption`), matching `$select`'s validation behavior.
- Any other system query option (`$filter`, `$expand`, `$search`, `$apply`, `$compute`, `$skiptoken`, `$deltatoken`) is **not implemented on navigation routes** and returns `400 Bad Request` (`UnsupportedQueryOption`) rather than being silently ignored (OData 4.0 Minimal conformance item 7: parse the option or reject it). To filter related data, expose the child entity set with its own profile and query it directly.

## `$expand`

When `ExpandEnabled = true` and navigation properties are declared, clients can embed related data in the parent response:

```
GET /odata/Orders?$expand=Lines
GET /odata/Orders?$expand=Lines($select=ProductName,Quantity),Customer
GET /odata/Orders(id)?$expand=Lines
```

`$expand` does **not** translate to EF Core's `Include()` - it's a post-processing step over the
already-serialized parent page, driven by the same handler delegates used for navigation routes.
None of the three collection GET paths (`GetQueryable`, `GetAll`, the `IODataEntitySetEndpointSource`
priority-1 path) push the expand down into the underlying `IQueryable`/SQL query. A navigation
property with no registered handler (batch or per-entity) is silently skipped during expansion.

What differs is **how many times the handler is called**, and it depends on which overload you
registered:

- **Per-entity handler** (`getAll`/`get`) - called once per parent entity per expanded property:
  *N×P* calls for a page of *N* items and *P* expanded properties (an N+1 pattern against a
  database-backed handler).
- **Batch handler** (`batchGetAll`/`batchGet`) - called once per expanded property for the whole
  page: *P* calls total, regardless of *N*. Use this for EF Core-backed navigations; see
  [query-options.md](query-options.md#expand) for a worked example and
  [Batch-loaded navigation routes](#batch-loaded-navigation-routes) above for the API.

Both forms produce byte-identical `$expand` output; batch registration only changes the number
of handler invocations, not the response shape.

## Navigation routes vs `$expand` - when to use each

| | Navigation route | `$expand` |
|---|---|---|
| Returns related data as top-level response | ✅ | ❌ (embedded in parent) |
| Supports filtering/ordering on related data | ❌ | ✅ (with nested options) |
| Single SQL join (vs. handler calls) | ❌ (separate query per request) | ❌ (not pushed down to SQL - see note above) — call count is *P* with a batch handler, *N×P* with a per-entity handler |
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

## Creating a related entity — `POST /Parents({key})/Children`

For collection navigations, `HasMany` accepts a `post` handler that registers
`POST /{EntitySet}({key})/{Property}` — creating a brand-new related entity, rather than linking
to one that already exists (`$ref` above is for the latter; OData §11.4.2.1):

```csharp
HasMany(x => x.Lines,
    getAll: (orderId, ct) =>
        Task.FromResult<IEnumerable<OrderLine>>(db.OrderLines.Where(l => l.OrderId == orderId).ToList()),
    post: async (orderId, line, ct) =>
    {
        if (!await db.Orders.AnyAsync(o => o.Id == orderId, ct)) return null; // parent not found → 404
        line.OrderId = orderId;
        db.OrderLines.Add(line);
        await db.SaveChangesAsync(ct);
        return line;
    },
    refTargetEntitySet: "OrderLines");
```

This registers: `POST /odata/Orders({key})/Lines`

**Handler contract:**

- The request body is deserialized as the navigation's item type (the same `TNavigation` used
  by `getAll`) using the profile's configured JSON options.
- The handler receives the parent key and the deserialized child; it is responsible for
  assigning any server-side values (e.g. the foreign key, the child's own primary key) and
  persisting the result.
- Return the created child (with its final values) on success.
- Return `null` to indicate the parent was not found — the framework maps this to
  `404 Not Found`, mirroring how `GetById`/nav-GET handlers signal "not found."

**Response semantics:**

| Condition | Response |
|-----------|----------|
| Success, no `Prefer` header | `201 Created` with the created child in the body (`@odata.context`, and `@odata.id`/`Location` header when `refTargetEntitySet` is configured) |
| Success, `refTargetEntitySet` not configured | `201 Created` with the created child in the body; no `Location`/`@odata.id` — the framework cannot compute a URL for the child without knowing its entity set and key property |
| `Prefer: return=minimal` | `204 No Content` with `Preference-Applied: return=minimal`; `Location`/`OData-EntityId` headers are set only when `refTargetEntitySet` is configured |
| Parent not found (handler returns `null`) | `404 Not Found` (OData error envelope) |
| Malformed / empty JSON body | `400 Bad Request` (OData error envelope) |
| Non-JSON `Content-Type` | `415 Unsupported Media Type` (OData error envelope) |
| No `post` handler configured | The route is not registered at all — `POST` to the nav path returns `405 Method Not Allowed` (the `GET` nav route occupies the same template) |

The `Location`/`@odata.id` are built the same way `$ref` builds populated references: from
`refTargetEntitySet` plus the child's key property, detected by convention (`Id` or
`{TypeName}Id`) — the same `ChildEntitySetName`/`ChildKeyPropertyName` machinery `$ref` uses.

Authorization is inherited from the parent profile — both the all-operations
`RequireAuthorization()`/`RequireRoles()` and per-operation `ConfigureAuthorization(...)` (see
[docs/authorization.md](authorization.md)) — same as every other route on the entity set.

> **POST-to-nav vs. deep insert:** the `post` handler above creates ONE related entity on an
> already-existing parent (`POST /Orders(id)/Lines`). To create a parent **and** its related
> entities in a single request (`POST /Orders` with a nested `lines` array in the body), see
> [deep insert](deep-insert.md) (OData §11.4.2.2).
