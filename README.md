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
        Delete = (id, ct) => { /* delete */ return Task.CompletedTask; };
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
| `GET` | `/odata/Products({key})` | `GetById` |
| `POST` | `/odata/Products` | `Post` |
| `PUT` | `/odata/Products({key})` | `PutById` |
| `PATCH` | `/odata/Products({key})` | `Patch` |
| `DELETE` | `/odata/Products({key})` | `Delete` |

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

## OData model configuration

```csharp
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile() : base(x => x.Id)
    {
        // Enable OData query capabilities
        SelectEnabled = true;
        FilterEnabled = true;
        ExpandEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;

        // Restrict which properties can be used in each operation
        FilterProperties = new[] { "CustomerId", "Status", "CreatedAt" };
        OrderByProperties = new[] { "CreatedAt", "Total" };

        // Declare navigation properties for $expand
        HasOptional(x => x.Customer);
        HasMany(x => x.Lines);

        // Handlers...
    }
}
```

## `IOptions<OhDataOptions>` configuration

Options can be set via the fluent builder or from `appsettings.json`:

```csharp
// Fluent
builder.Services.AddOhData(ohdata => ..., options =>
{
    options.CamelCaseSerialization = true;
});

// Or from config
builder.Services.Configure<OhDataOptions>(
    builder.Configuration.GetSection("OhData"));
```

## OpenAPI / Swagger / Scalar

OhData endpoints are fully compatible with API Explorer. `MapOhData()` returns a `RouteGroupBuilder` for chaining:

```csharp
app.MapOhData().WithOpenApi();
```

Each entity set's routes are tagged with the entity set name automatically, so they group cleanly in Swagger UI and Scalar.

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
