# OhData

Convention-based OData 4.0 server and typed client for ASP.NET Core. Define a profile class, assign handler delegates, and get a fully spec-compliant OData API - no controllers required. Consume it from .NET with a fluent, LINQ-native client.

## Getting Started

Install the server package:

```
dotnet add package OhData.AspNetCore
```

Install the client package:

```
dotnet add package OhData.Client
```

## Packages

| Package | NuGet | What it does |
|---------|-------|--------------|
| `OhData.AspNetCore` | [![NuGet](https://img.shields.io/nuget/v/OhData.AspNetCore)](https://www.nuget.org/packages/OhData.AspNetCore) | `EntitySetProfile<TKey,TModel>` base class, DI registration, endpoint factory, minimal API routes. Includes `AddOhDataVersion` / `MapOhDataVersion` versioning helpers and `ODataEntitySetProfile<TKey,TModel>` for full OData pushdown control. |
| `OhData.Client` | [![NuGet](https://img.shields.io/nuget/v/OhData.Client)](https://www.nuget.org/packages/OhData.Client) | Typed .NET client. Fluent builder with LINQ-based `$filter`, `$select`, `$expand`, `$orderby`, and pagination. |

---

## Server quick start

```csharp
// 1. Define your entity
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

// 2. Create a profile - assign only the handlers you need
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled  = true;
        OrderByEnabled = true;
        CountEnabled   = true;
        SelectEnabled  = true;

        // IQueryable path: $filter/$orderby/$skip/$top push down to SQL via EF Core
        GetQueryable = (_) => Task.FromResult(db.Products.AsQueryable());
        GetById      = (id, ct) => db.Products.FindAsync(id, ct).AsTask();
        Post         = (p, ct) => { db.Products.Add(p); return db.SaveChangesAsync(ct).ContinueWith(_ => (Product?)p); };
        PutById      = (id, p, ct) => { db.Products.Update(p); return db.SaveChangesAsync(ct).ContinueWith(_ => (Product?)p); };
        Patch        = (id, delta, ct) => { var e = db.Products.Find(id); return Task.FromResult(e is null ? null : delta.Patch(e)); };
        Delete       = (id, ct) => { /* remove by id */ return Task.FromResult(true); };
    }
}

// 3. Register in Program.cs
builder.Services.AddOhData(o => o
    .WithPrefix("/odata")
    .AddProfile<ProductProfile>());

// 4. Map endpoints after app.Build()
app.MapOhData();
```

This produces:

| Method | Route | Handler |
|--------|-------|---------|
| `GET` | `/odata` | Service document |
| `GET` | `/odata/$metadata` | CSDL (EDM) |
| `GET` | `/odata/Products` | `GetQueryable` - supports `$filter`, `$orderby`, `$skip`, `$top`, `$select`, `$count` |
| `GET` | `/odata/Products/$count` | filtered row count |
| `GET` | `/odata/Products({key})` | `GetById` |
| `POST` | `/odata/Products` | `Post` |
| `PUT` | `/odata/Products({key})` | `PutById` |
| `PATCH` | `/odata/Products({key})` | `Patch` |
| `DELETE` | `/odata/Products({key})` | `Delete` |

Only routes with a handler assigned are registered. Unassigned handlers produce no route.

---

## Client quick start

```csharp
// Setup - inject via IHttpClientFactory or create directly
var client = new OhDataClient("https://api.example.com/odata");

// Query with LINQ-style filter, ordering, and pagination
var page = await client.For<Product>()
    .Filter(x => x.Price > 10 && x.Name.StartsWith("W"))
    .OrderBy(x => x.Name)
    .Top(20)
    .Skip(0)
    .ToPageAsync();     // returns ODataPage<Product> with Items, TotalCount, NextLink

// Get a single entity - returns null on 404
Product? p = await client.For<Product>().Key(42).GetAsync();

// Mutate
var created = await client.For<Product>().InsertAsync(new Product { Name = "Cog", Price = 4.99m });
var updated = await client.For<Product>().Key(created.Id).PutAsync(created with { Price = 5.49m });
await client.For<Product>().Key(42).PatchAsync(new { Price = 3.99m });
await client.For<Product>().Key(42).DeleteAsync();
```

With `IHttpClientFactory`:

```csharp
// Registration
builder.Services.AddHttpClient<OhDataClient>(c => c.BaseAddress = new Uri("https://api.example.com/odata/"));
builder.Services.AddTransient(sp => new OhDataClient(sp.GetRequiredService<HttpClient>()));

// Injection
public class MyService(OhDataClient client) { ... }
```

---

## Documentation

| Topic | Guide |
|-------|-------|
| Query options (`$filter`, `$orderby`, `$select`, `$expand`, `$count`, `$search`) | [docs/query-options.md](docs/query-options.md) |
| Navigation property routing and `$ref` | [docs/navigation-routing.md](docs/navigation-routing.md) |
| Bound functions and actions | [docs/bound-operations.md](docs/bound-operations.md) |
| ETags and optimistic concurrency | [docs/etags.md](docs/etags.md) |
| Authorization | [docs/authorization.md](docs/authorization.md) |
| API versioning | [docs/versioning.md](docs/versioning.md) |
| Client guide | [docs/client.md](docs/client.md) |
| OData 4.0 spec compliance | [docs/spec-compliance.md](docs/spec-compliance.md) |
| Framework architecture | [docs/architecture.md](docs/architecture.md) |
