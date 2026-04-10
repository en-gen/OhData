# OhData

A convention-based OData server framework for ASP.NET Core that turns declarative profile classes into minimal API endpoints — no controllers required.

## Why

Standard OData frameworks require controllers, heavy configuration, and deep coupling to the OData routing infrastructure. OhData is different:

- **Profiles over controllers** — declare what your API looks like in a plain class; the framework wires up the routes at startup.
- **Opt-in operations** — only the HTTP methods you assign a handler for get a route. No handler = no route.
- **Minimal API native** — endpoints are registered as standard ASP.NET Core minimal API lambdas and work with all standard middleware (auth, rate limiting, OpenAPI/Swagger).

## Quick start

```csharp
// 1. Define your entity and its profile
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile() : base(x => x.Id)
    {
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;

        GetAll = (ct) => Task.FromResult<IEnumerable<Product>>(/* your data source */);
        GetById = (id, ct) => Task.FromResult(/* lookup by id */);
        Post = (product, ct) => Task.FromResult(/* create */);
        PutById = (id, product, ct) => Task.FromResult(/* replace */);
        Patch = (id, product, ct) => Task.FromResult(/* partial update, return null → 404 */);
        Delete = (id, ct) => Task.FromResult(/* deleted: true/false → false returns 404 */);
    }
}

// 2. Register in Program.cs
builder.Services.AddOhData(ohdata =>
    ohdata
        .WithPrefix("/odata")          // optional, defaults to /odata
        .AddProfile<ProductProfile>()
);

// 3. Map endpoints
app.MapOhData();
```

This produces:

| Method | Route | Handler |
|--------|-------|---------|
| `GET` | `/odata` | Service document |
| `GET` | `/odata/$metadata` | CSDL XML (EDM model) |
| `GET` | `/odata/Products` | `GetAll` |
| `GET` | `/odata/Products/$count` | row count |
| `GET` | `/odata/Products({key})` | `GetById` |
| `POST` | `/odata/Products` | `Post` |
| `PUT` | `/odata/Products({key})` | `PutById` |
| `PATCH` | `/odata/Products({key})` | `Patch` |
| `DELETE` | `/odata/Products({key})` | `Delete` |

## IQueryable handler — EF Core query pushdown

Set `GetQueryable` instead of (or in addition to) `GetAll` to let the framework apply `$filter`, `$orderby`, `$skip`, `$top`, and `$count` directly to an `IQueryable<T>`. With EF Core, these translate to SQL — only matching rows are fetched:

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled  = true;
        OrderByEnabled = true;
        CountEnabled   = true;
        SelectEnabled  = true;

        // Framework applies OData query options via LINQ — EF Core translates to SQL
        GetQueryable = (_) => Task.FromResult(db.Products.AsQueryable());

        GetById = (id, _) => Task.FromResult(db.Products.Find(id));
        // ... other handlers
    }
}
```

`GetAll` (IEnumerable) remains the simple opt-in path — no query options are applied; the handler returns all items.

**Handler priority for `GET /EntitySet`:**
1. `IODataEntitySetEndpointSource` (receives `ODataQueryOptions<T>` directly)
2. `GetQueryable` — framework applies OData options via `ApplyTo(IQueryable)`
3. `GetAll` — no query options applied

## OData response envelope

All collection responses follow the OData JSON format:

```json
{
  "@odata.context": "http://host/odata/$metadata#Products",
  "@odata.count": 42,
  "value": [ ... ]
}
```

`@odata.count` is only included when the client sends `$count=true`. Error responses use the OData error format:

```json
{ "error": { "code": "InvalidQueryOption", "message": "..." } }
```

## Authorization

Declare auth requirements inside the profile constructor:

```csharp
// Any authenticated user
RequireAuthorization();

// Named ASP.NET Core policy
RequireAuthorization("AdminPolicy");

// Role-based
RequireRoles("Admin", "SuperAdmin");
```

Auth is applied to all routes for that entity set. Standard ASP.NET Core authentication middleware must be configured separately:

```csharp
builder.Services.AddAuthentication(...).Add...();
builder.Services.AddAuthorization(...);

app.UseAuthentication();
app.UseAuthorization();
app.MapOhData();
```

## Navigation property routing

Extend `HasMany` / `HasOptional` / `HasRequired` with a handler to register navigation routes:

```csharp
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        ExpandEnabled = true;

        // Registers: GET /Orders(id)/Lines
        HasMany(x => x.Lines,
            getAll: (orderId, ct) =>
                Task.FromResult<IEnumerable<OrderLine>>(
                    db.OrderLines.Where(l => l.OrderId == orderId).ToList()));

        GetQueryable = (_) => Task.FromResult(db.Orders.AsQueryable());
        GetById = (id, _) => Task.FromResult(
            db.Orders.Include(o => o.Lines).FirstOrDefault(o => o.Id == id));
    }
}
```

This registers `GET /odata/Orders({key})/Lines`.

## OData model configuration

```csharp
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile() : base(x => x.Id)
    {
        // Enable OData query capabilities
        SelectEnabled  = true;
        FilterEnabled  = true;
        ExpandEnabled  = true;
        OrderByEnabled = true;
        CountEnabled   = true;

        // Restrict which properties can be used in each operation
        FilterProperties  = new[] { "CustomerId", "Status", "CreatedAt" };
        OrderByProperties = new[] { "CreatedAt", "Total" };

        // Declare navigation properties for $expand
        HasOptional(x => x.Customer);
        HasMany(x => x.Lines);

        // Handlers...
    }
}
```

## Bound functions and actions

Register OData bound operations with `BindFunction` / `BindAction`:

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile() : base(x => x.Id)
    {
        BindFunction(GetCheapest);    // GET /Products/GetCheapest?maxPrice=10.00
        BindAction(ClearDiscounted);  // POST /Products/ClearDiscounted  { "threshold": 5.0 }

        GetAll = (ct) => Task.FromResult<IEnumerable<Product>>(store);
    }

    private Task<IEnumerable<Product>> GetCheapest(decimal maxPrice) =>
        Task.FromResult(store.Where(p => p.Price <= maxPrice));

    private void ClearDiscounted(decimal threshold) =>
        store.RemoveAll(p => p.Price < threshold);
}
```

- **Functions** (`GET`): parameters are passed as query strings (`?paramName=value`)
- **Actions** (`POST`): parameters are read from a JSON body (`{ "paramName": value }`)
- Both are declared in the EDM model and visible in `$metadata`

## ETags and optimistic concurrency

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile() : base(x => x.Id)
    {
        // Opt in by providing an ETag computation function
        GetETag = product => Convert.ToBase64String(product.RowVersion);

        GetById  = ...;
        PutById  = ...;  // will check If-Match before proceeding → 412 on mismatch
        Delete   = ...;  // same
    }
}
```

The framework adds an `ETag` response header to GET/POST/PUT/PATCH responses. If a `PUT`/`PATCH`/`DELETE` request includes `If-Match`, it fetches the current state and returns 412 if the ETag doesn't match.

## API versioning

```csharp
// Multiple named registrations with different prefixes
builder.Services.AddOhData("v1", o => o.WithPrefix("/v1").AddProfile<ProductProfileV1>());
builder.Services.AddOhData("v2", o => o.WithPrefix("/v2").AddProfile<ProductProfileV2>());

app.MapOhData("v1").WithOpenApi();
app.MapOhData("v2").WithOpenApi();
```

Or use the convenience package:

```csharp
// OhData.AspNetCore.Versioning
builder.Services.AddOhDataVersion("v1", "/v1", o => o.AddProfile<ProductProfileV1>());
builder.Services.AddOhDataVersion("v2", "/v2", o => o.AddProfile<ProductProfileV2>());

app.MapOhDataVersion("v1");
app.MapOhDataVersion("v2");
```

## OpenAPI / Swagger / Scalar

OhData endpoints are fully compatible with API Explorer. `MapOhData()` returns a `RouteGroupBuilder` for chaining:

```csharp
app.MapOhData().WithOpenApi();
```

Each entity set's routes are tagged with the entity set name automatically, so they group cleanly in Swagger UI and Scalar. The TestBench (`src/OhData.TestBench.AspNetCore`) demonstrates both at `/swagger` and `/scalar`.

## Advanced: full EDM control

Override `AdvancedConfigure` to take complete control of the OData entity set configuration. This opts out of all automatic query capability and navigation property configuration:

```csharp
protected override void AdvancedConfigure(EntitySetConfiguration<Product> config)
{
    config.EntityType.HasKey(x => x.Id);
    config.EntityType.Select().OrderBy().Filter();
    // full Microsoft.OData.ModelBuilder API available
}
```

## Project structure

| Package | Role |
|---------|------|
| `OhData.Abstractions` | `EntitySetProfile<TKey,TModel>` and supporting types. No ASP.NET Core dependency. |
| `OhData.AspNetCore` | DI registration, endpoint factory, key parsing. Targets net8.0. |
| `OhData.AspNetCore.Versioning` | `AddOhDataVersion` / `MapOhDataVersion` convenience wrappers. |
