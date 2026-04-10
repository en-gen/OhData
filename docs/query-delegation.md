# Query Delegation

OhData supports two handler patterns for `GET /EntitySet` collection requests. Choosing between them is a design decision about where query filtering happens.

## `GetAll` — simple IEnumerable path

```csharp
GetAll = (ct) => Task.FromResult<IEnumerable<Product>>(store);
```

The handler returns all items. The framework does **not** apply any OData query options to the returned collection. If the client sends `$filter=Price lt 10.00`, the full list is returned unchanged.

Use `GetAll` when:
- Your data source is small and in-memory
- You want complete control over what is returned
- You don't need `$filter`, `$orderby`, `$top`, `$skip` applied by the framework

## `GetQueryable` — IQueryable path with EF Core pushdown

```csharp
GetQueryable = (_) => Task.FromResult(db.Products.AsQueryable());
```

The handler returns a base `IQueryable<TModel>`. The framework constructs `ODataQueryOptions<TModel>` from the HTTP request and applies `$filter`, `$orderby`, `$skip`, `$top` via `ApplyTo(IQueryable, ODataQuerySettings)`. With EF Core, these LINQ operations translate to SQL — only matching rows are fetched from the database.

Use `GetQueryable` when:
- Your data source is a database (EF Core, Dapper with IQueryable wrapper, etc.)
- You want `$filter`/`$orderby`/`$top`/`$skip` to push down to the data layer
- You want `$count=true` to work correctly (count before skip/top)
- You want `$select` to reduce response payload size

### Example with EF Core

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled  = true;
        OrderByEnabled = true;
        CountEnabled   = true;
        SelectEnabled  = true;

        GetQueryable = (_) => Task.FromResult(db.Products.AsQueryable());
    }
}
```

Registration as singleton requires `AppDbContext` also registered as singleton (for demo/test) or via factory pattern for production:

```csharp
// Demo/test: singleton DbContext
builder.Services.AddDbContext<AppDbContext>(
    o => o.UseInMemoryDatabase("demo"),
    ServiceLifetime.Singleton);

// Production: use IDbContextFactory to avoid scoped-in-singleton issues
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(connectionString));

public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(IDbContextFactory<AppDbContext> factory) : base(x => x.Id)
    {
        GetQueryable = async (_) =>
        {
            var db = await factory.CreateDbContextAsync();
            return db.Products.AsQueryable();
        };
    }
}
```

## `$select` response shaping

When `SelectEnabled = true` and the request includes `$select=Name,Price`, the framework:
1. Materializes the full `TModel[]` (all columns fetched)
2. Serializes to JSON
3. Removes unselected properties from each item in the response

This works on both `GetQueryable` and `GetAll` paths.

**Note:** Column-level SQL projection for `$select` (fetching only selected columns from the DB) is not currently implemented. All columns are always fetched; `$select` affects only the response shape.

## `$count`

The `GET /EntitySet/$count` endpoint is registered automatically when `GetQueryable` or `GetAll` is set.

- `GetQueryable` path: applies `$filter` to the queryable before counting (EF Core → `COUNT` SQL)
- `GetAll` path: counts the full enumerated collection

Inline count (`$count=true`) in the collection envelope is only supported on the `GetQueryable` path.

## Error handling

Invalid query option values (unknown property names in `$filter`, etc.) return a 400 OData error:

```json
{ "error": { "code": "InvalidQueryOption", "message": "..." } }
```

The `ODataException` thrown by the OData library during query construction is caught and converted to this response.
