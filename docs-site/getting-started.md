# Getting started

This guide takes you from an empty project to a running, queryable OData API in about ten
minutes. It uses EF Core's in-memory provider, so there is no database server to install — yet the
data access is **genuinely async**, exactly what you would write against a real database. To watch
those same queries turn into real SQL, follow the [EF Core + SQLite tutorial](ef-core-sqlite.md)
next.

## Prerequisites

- The **.NET 10 SDK** (OhData targets `net8.0` and `net10.0`).
- Any editor — the whole API is plain C#.

## 1. Create a project and add the packages

```bash
dotnet new web -o ShopApi
cd ShopApi
dotnet add package EnGen.OhData.AspNetCore
dotnet add package Microsoft.EntityFrameworkCore.InMemory
```

## 2. Define an entity

Your model is an ordinary CLR class. Nothing OData-specific here.

```csharp
// Product.cs
namespace ShopApi;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}
```

## 3. Define a DbContext

OhData reads and writes through your EF Core `DbContext` — the profile in the next step injects it.
This one exposes a single `Products` set and seeds a few rows so there is something to query.

```csharp
// AppDbContext.cs
using Microsoft.EntityFrameworkCore;

namespace ShopApi;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Product>().HasData(
            new Product { Id = 1, Name = "Claw Hammer",      Price = 14.99m },
            new Product { Id = 2, Name = "Adjustable Wrench", Price = 12.25m },
            new Product { Id = 3, Name = "Cordless Drill",    Price = 89.00m });
}
```

## 4. Define an entity set profile

A **profile** is the entire API surface for one entity set. It derives from
`EntitySetProfile<TKey, TModel>`, passes a key selector to the base constructor, and assigns
handler delegates in its constructor. **Only the handlers you assign become routes** — this is
OhData's headline rule. Assign `GetQueryable` and you get a queryable collection; leave `Delete`
unassigned and there is no `DELETE` route at all.

The profile injects `AppDbContext` through its constructor. Profiles are registered **scoped**, so
each request gets its own profile instance holding that request's `DbContext` — the ordinary
ASP.NET Core lifetime, no extra plumbing.

```csharp
// ProductProfile.cs
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OhData;

namespace ShopApi;

public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        // Query capabilities are OFF by default and enforced at runtime: a request that uses a
        // disabled option gets a 400, not a silently-ignored parameter. Opt into the ones you
        // want to support.
        FilterEnabled  = true;
        OrderByEnabled = true;
        SelectEnabled  = true;
        CountEnabled   = true;

        // Collection GET. GetQueryable hands the framework an un-materialized IQueryable, so it can
        // compose $filter/$orderby/$skip/$top onto it; EF Core then materializes the result
        // asynchronously (over a real database provider that becomes one SQL query). Returning the
        // IQueryable is itself synchronous — that is why this single handler stays Task.FromResult
        // while the reads and writes below are genuinely async.
        GetQueryable = _ => Task.FromResult<IQueryable<Product>>(db.Products);

        // Single entity GET → GET /odata/Products({key}). Returns null (→ 404) when the row is absent.
        GetById = async (id, ct) => await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);

        // Create → POST /odata/Products. Add the row and let the database assign its key.
        Post = async (product, ct) =>
        {
            db.Products.Add(product);
            await db.SaveChangesAsync(ct);
            return product;
        };

        // Delete → DELETE /odata/Products({key}). Return false when the row is absent;
        // IdempotentDelete defaults to true, so a missing row yields 204 (not 404).
        Delete = async (id, ct) =>
        {
            Product? existing = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (existing is null) return false;
            db.Products.Remove(existing);
            await db.SaveChangesAsync(ct);
            return true;
        };
    }
}
```

## 5. Register and map OhData

Three moves now: register the `DbContext`, then `AddOhData` (collects your profiles and prefix at
DI-build time) and `MapOhData` (registers the routes after `app.Build()`).

`AddOhData` and `MapOhData` live in the framework's `Microsoft.Extensions.DependencyInjection`
and `Microsoft.AspNetCore.Builder` namespaces, so no extra `using` is needed for them.

```csharp
// Program.cs
using Microsoft.EntityFrameworkCore;
using ShopApi;

var builder = WebApplication.CreateBuilder(args);

// EF Core's in-memory provider — no database server to install. The SQLite tutorial swaps this
// one line for real SQLite. Profiles are scoped, so injecting this scoped context is safe.
builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("Shop"));

builder.Services.AddOhData(o => o
    .WithPrefix("/odata")
    .AddEntitySetProfile<ProductProfile>());
    // ...or scan an assembly: .AddProfilesFromAssemblyOf<ProductProfile>()

var app = builder.Build();

// Materialize the in-memory store and apply the seed rows declared in AppDbContext.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
}

app.MapOhData();

app.Run();
```

## 6. Run and query it

```bash
dotnet run
```

Then exercise the OData surface (adjust the port to what `dotnet run` prints):

```bash
# Service document and the CSDL metadata
curl "http://localhost:5000/odata/"
curl "http://localhost:5000/odata/\$metadata"

# The collection, plus query options (percent-encode spaces as %20). Property names in
# $filter/$orderby/$select are the canonical PascalCase names from $metadata; matching is
# case-insensitive, but the responses come back PascalCase, so prefer the canonical form.
curl "http://localhost:5000/odata/Products"
curl "http://localhost:5000/odata/Products?\$filter=Price%20gt%2013&\$orderby=Name"
curl "http://localhost:5000/odata/Products?\$select=Name,Price&\$top=2"
curl "http://localhost:5000/odata/Products/\$count"

# A single entity
curl "http://localhost:5000/odata/Products(1)"

# Create one (request bodies bind case-insensitively, so either casing is accepted)
curl -X POST "http://localhost:5000/odata/Products" \
     -H "Content-Type: application/json" \
     -d '{"Name":"Stud Finder","Price":29.99}'
```

The created row comes back in the canonical PascalCase shape that matches `$metadata`, with the
key the database assigned:

```json
{ "@odata.context": "http://localhost:5000/odata/$metadata#Products/$entity", "Id": 4, "Name": "Stud Finder", "Price": 29.99 }
```

Because you assigned `GetQueryable`, `GetById`, `Post`, and `Delete` — but not `Put` or `Patch` —
those four routes exist and the update routes don't. That is the whole model: the profile is the
contract.

## Where to next

- **[EF Core + SQLite tutorial](ef-core-sqlite.md)** — swap the in-memory provider for a real
  relational database and watch OData query options become SQL.
- **[Query options](../docs/query-options.md)** — the full `$filter`/`$orderby`/`$select`/`$expand`/`$count`/`$search` surface and the capability flags that gate it.
- **[Architecture](../docs/architecture.md)** — how profiles become routes, and the design decisions behind it.
- **[Navigation & `$ref` routing](../docs/navigation-routing.md)**, **[bound functions & actions](../docs/bound-operations.md)**, **[ETags & concurrency](../docs/etags.md)**, and **[authorization](../docs/authorization.md)** — grow the surface from here.
