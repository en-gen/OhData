# Query Options

OhData supports the OData 4.0 system query options. Which ones are applied depends on the collection handler you choose for the entity set.

## Handler paths

### `GetAll` - simple in-memory path

```csharp
GetAll = (ct) => Task.FromResult<IEnumerable<Product>>(myList);
```

Returns all items. The framework does **not** apply any query options to the returned collection - `$filter`, `$orderby`, `$skip`, and `$top` sent by the client are ignored. Use this when your data source is small and in-memory, or when you want complete control over what is returned.

### `GetODataQueryable` - full OData pushdown (advanced)

```csharp
GetODataQueryable = (opts, ct) => ...
```

The profile receives the raw `ODataQueryOptions<TModel>` and is responsible for applying them to the data source. Use this when:

- You need full control over how query options are translated (e.g. custom SQL, Dapper, a remote API).
- You want to apply paging yourself and return the pre-paging total count alongside the results.

Return an `ODataQueryResult<TModel>` to supply paging metadata:

```csharp
GetODataQueryable = async (opts, ct) =>
{
    // Apply filtering, ordering, paging - however your data source requires.
    var (items, totalCount) = await myDataSource.QueryAsync(opts, ct);

    return new ODataQueryResult<TModel>
    {
        Items = items.AsQueryable(),
        TotalCount = totalCount,   // pre-paging count; used for $count=true
    };
};
```

The framework does not prescribe how `items` or `totalCount` are obtained. That is entirely up to the profile. Some data sources support retrieving both in a single operation (window functions, `COUNT(*) OVER()`); others require two separate requests. Either approach satisfies the contract - the framework only requires that `TotalCount` reflect the number of matching records **before** paging was applied.

If `TotalCount` is not set and the client sends `$count=true`, the count in the response will reflect only the current page size, which is incorrect per the OData spec. Prefer always supplying `TotalCount` when using this handler.

> **Note:** `GetODataQueryable` is available on `ODataEntitySetProfile<TKey, TModel>`, not the base `EntitySetProfile<TKey, TModel>`. It requires the `EnGen.OhData.AspNetCore` package.

### `GetQueryable` - IQueryable with pushdown (recommended for databases)

```csharp
GetQueryable = (_) => Task.FromResult(db.Products.AsQueryable());
```

Returns a base `IQueryable<TModel>`. The framework applies `$filter`, `$orderby`, `$skip`, and `$top` via `ApplyTo(IQueryable)`. With EF Core these become SQL clauses - only matching rows are fetched.

Enable the query capabilities you want to expose:

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled  = true;   // allow $filter
        OrderByEnabled = true;   // allow $orderby
        CountEnabled   = true;   // allow $count
        SelectEnabled  = true;   // allow $select
        ExpandEnabled  = true;   // allow $expand

        GetQueryable = (_) => Task.FromResult(db.Products.AsQueryable());
    }
}
```

Any disabled capability returns `400 Bad Request` if the client sends that query option.

### Production pattern: `IDbContextFactory`

Profiles are singletons, so a scoped `DbContext` cannot be injected directly. Use `IDbContextFactory<T>`:

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(IDbContextFactory<AppDbContext> factory) : base(x => x.Id)
    {
        FilterEnabled  = true;
        OrderByEnabled = true;

        GetQueryable = async (_) =>
        {
            var db = await factory.CreateDbContextAsync();
            return db.Products.AsQueryable();
        };
    }
}

// Registration:
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(connectionString));
```

---

## `$filter`

Enabled via `FilterEnabled = true`. Supports comparison operators (`eq`, `ne`, `gt`, `ge`, `lt`, `le`), logical operators (`and`, `or`, `not`), arithmetic, string functions (`contains`, `startswith`, `endswith`, `tolower`, `toupper`, `trim`), date functions, and more.

```
GET /odata/Products?$filter=Price gt 10 and contains(Name,'Widget')
GET /odata/Products?$filter=year(CreatedAt) eq 2024
```

Restrict which properties may appear in `$filter`:

```csharp
FilterProperties(x => x.Price, x => x.Name, x => x.Category);
// or string overload:
FilterProperties("Price", "Name", "Category");
```

---

## `$orderby`

Enabled via `OrderByEnabled = true`. Supports multiple sort keys, ascending (`asc`, default) and descending (`desc`).

```
GET /odata/Products?$orderby=Category asc,Price desc
```

Restrict which properties may be sorted on:

```csharp
OrderByProperties(x => x.Price, x => x.Name);
```

---

## `$top` and `$skip`

Limit and offset the result set. On the `GetQueryable` path these become SQL `LIMIT`/`OFFSET`.

```
GET /odata/Products?$top=20&$skip=40
```

Cap the maximum `$top` value server-side:

```csharp
// Per profile:
MaxTop = 100;

// Or globally across all profiles in the registration:
builder.Services.AddOhData(o => o
    .WithDefaults(d => d.MaxTop = 500)
    .AddProfile<ProductProfile>());
```

Requests with `$top` exceeding `MaxTop` receive `400 Bad Request`.

---

## `$count`

Enabled via `CountEnabled = true`. Two forms:

**Inline count** - embed the total (pre-pagination) count in the collection envelope:

```
GET /odata/Products?$count=true
```

```json
{
  "@odata.context": "https://host/odata/$metadata#Products",
  "@odata.count": 1234,
  "value": [...]
}
```

**Standalone count** - returns a plain integer, `$filter` is applied if present:

```
GET /odata/Products/$count
GET /odata/Products/$count?$filter=Price gt 10
```

Behaviour depends on the handler path:

| Handler | `$count=true` behaviour |
|---|---|
| `GetODataQueryable` | Uses `TotalCount` from `ODataQueryResult<TModel>`. If not supplied, falls back to the current page size - **incorrect per spec**. Always set `TotalCount` on this path. |
| `GetQueryable` | Framework runs a second `COUNT(*)` query against the `IQueryable` before paging is applied. |
| `GetAll` | Full collection is enumerated and counted. |

---

## `$select`

Enabled via `SelectEnabled = true`. Reduces the response payload to the specified properties:

```
GET /odata/Products?$select=Id,Name,Price
```

The framework fetches the full entity from the data source and removes unselected properties from the JSON response. SQL-level column projection is not currently performed.

Restrict which properties may be selected:

```csharp
SelectProperties(x => x.Id, x => x.Name, x => x.Price);
```

---

## `$expand`

Enabled via `ExpandEnabled = true`. Embeds related entities inline in the parent response:

```
GET /odata/Orders?$expand=Lines
GET /odata/Orders?$expand=Lines($select=ProductName,Quantity)
GET /odata/Orders?$expand=Lines,Customer
```

On the `GetQueryable` path with EF Core, `$expand` calls `Include()` - the related data is loaded in the same query. Navigation properties must be declared in the profile:

```csharp
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        ExpandEnabled = true;

        HasMany(x => x.Lines);       // declares in EDM for $expand
        HasOptional(x => x.Customer);

        GetQueryable = (_) => Task.FromResult(db.Orders.AsQueryable());
    }
}
```

To also expose navigation as a standalone HTTP route (`GET /Orders(id)/Lines`), provide a handler to `HasMany` - see [navigation-routing.md](navigation-routing.md).

---

## `$search`

Register a `Search` handler to support free-text search:

```csharp
Search = (term, ct) => Task.FromResult<IEnumerable<Product>>(
    db.Products
      .Where(p => p.Name.Contains(term) || p.Description.Contains(term))
      .ToList());
```

```
GET /odata/Products?$search=widget
```

Without a `Search` handler, `$search` requests return `501 Not Implemented`. The interpretation of the search term is entirely up to the handler.

---

## Error responses

Invalid query options return `400 Bad Request` with an OData error body:

```json
{ "error": { "code": "InvalidQueryOption", "message": "The query option '$filter' is not allowed." } }
```
