# Getting started

> **DRAFT — needs maintainer review**
>
> This narrative walkthrough was assembled for the documentation site (issue #208) from the
> `README.md` quick-start snippets. Verify the code compiles against the current API and that
> package/route names are accurate before publishing.

This guide takes you from an empty project to a running, queryable OData API in about ten
minutes. It uses an in-memory list to stay focused on OhData itself; for a real database, follow
the [EF Core + SQLite tutorial](ef-core-sqlite.md) next.

## Prerequisites

- The **.NET 10 SDK** (OhData targets `net8.0` and `net10.0`).
- Any editor — the whole API is plain C#.

## 1. Create a project and add the package

```bash
dotnet new web -o ShopApi
cd ShopApi
dotnet add package EnGen.OhData.AspNetCore
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

## 3. Define an entity set profile

A **profile** is the entire API surface for one entity set. It derives from
`EntitySetProfile<TKey, TModel>`, passes a key selector to the base constructor, and assigns
handler delegates in its constructor. **Only the handlers you assign become routes** — this is
OhData's headline rule. Assign `GetQueryable` and you get a queryable collection; leave `Delete`
unassigned and there is no `DELETE` route at all.

```csharp
// ProductProfile.cs
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OhData.Abstractions;

namespace ShopApi;

public class ProductProfile : EntitySetProfile<int, Product>
{
    // A stand-in data store for this walkthrough. In a real app you would inject a
    // DbContext here — profiles are registered scoped, so constructor injection is safe.
    private static readonly List<Product> _store = new()
    {
        new Product { Id = 1, Name = "Claw Hammer",      Price = 14.99m },
        new Product { Id = 2, Name = "Adjustable Wrench", Price = 12.25m },
        new Product { Id = 3, Name = "Cordless Drill",    Price = 89.00m },
    };

    public ProductProfile() : base(x => x.Id)
    {
        // Query capabilities are OFF by default and enforced at runtime: a request that uses a
        // disabled option gets a 400, not a silently-ignored parameter. Opt into the ones you
        // want to support.
        FilterEnabled  = true;
        OrderByEnabled = true;
        SelectEnabled  = true;
        CountEnabled   = true;

        // Collection GET. The IQueryable path lets the framework compose $filter/$orderby/$skip/
        // $top onto the query; over a real DbSet that becomes SQL. Over an in-memory list it is
        // applied with LINQ-to-Objects.
        GetQueryable = _ => Task.FromResult(_store.AsQueryable());

        // Single entity GET → GET /odata/Products({key}). Return null to yield a 404.
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(p => p.Id == id));

        // Create → POST /odata/Products.
        Post = (product, ct) =>
        {
            product.Id = _store.Count == 0 ? 1 : _store.Max(p => p.Id) + 1;
            _store.Add(product);
            return Task.FromResult<Product?>(product);
        };

        // Delete → DELETE /odata/Products({key}). Return false when the row is absent;
        // IdempotentDelete defaults to true, so a missing row yields 204 (not 404).
        Delete = (id, ct) =>
        {
            Product? existing = _store.FirstOrDefault(p => p.Id == id);
            if (existing is null) return Task.FromResult(false);
            _store.Remove(existing);
            return Task.FromResult(true);
        };
    }
}
```

## 4. Register and map OhData

Two calls: `AddOhData` collects your profiles and prefix at DI-build time; `MapOhData` registers
the routes after `app.Build()`.

```csharp
// Program.cs
using OhData.AspNetCore;
using ShopApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOhData(o => o
    .WithPrefix("/odata")
    .AddProfile<ProductProfile>());
    // ...or scan an assembly: .AddProfilesFromAssemblyOf<ProductProfile>()

var app = builder.Build();

app.MapOhData();

app.Run();
```

## 5. Run and query it

```bash
dotnet run
```

Then exercise the OData surface (adjust the port to what `dotnet run` prints):

```bash
# Service document and the CSDL metadata
curl "http://localhost:5000/odata/"
curl "http://localhost:5000/odata/\$metadata"

# The collection, plus query options (percent-encode spaces as %20)
curl "http://localhost:5000/odata/Products"
curl "http://localhost:5000/odata/Products?\$filter=price%20gt%2013&\$orderby=name"
curl "http://localhost:5000/odata/Products?\$select=name,price&\$top=2"
curl "http://localhost:5000/odata/Products/\$count"

# A single entity
curl "http://localhost:5000/odata/Products(1)"

# Create one
curl -X POST "http://localhost:5000/odata/Products" \
     -H "Content-Type: application/json" \
     -d '{"name":"Stud Finder","price":29.99}'
```

Because you assigned `GetQueryable`, `GetById`, `Post`, and `Delete` — but not `Put` or `Patch` —
those four routes exist and the update routes don't. That is the whole model: the profile is the
contract.

## Where to next

- **[EF Core + SQLite tutorial](ef-core-sqlite.md)** — swap the in-memory list for a real
  relational database and watch OData query options become SQL.
- **[Query options](../docs/query-options.md)** — the full `$filter`/`$orderby`/`$select`/`$expand`/`$count`/`$search` surface and the capability flags that gate it.
- **[Architecture](../docs/architecture.md)** — how profiles become routes, and the design decisions behind it.
- **[Navigation & `$ref` routing](../docs/navigation-routing.md)**, **[bound functions & actions](../docs/bound-operations.md)**, **[ETags & concurrency](../docs/etags.md)**, and **[authorization](../docs/authorization.md)** — grow the surface from here.
