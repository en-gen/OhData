# ![OhData logo](https://raw.githubusercontent.com/en-gen/OhData/develop/assets/icon-64.png) OhData

[![CI](https://github.com/en-gen/OhData/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/en-gen/OhData/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/en-gen/OhData/graph/badge.svg)](https://codecov.io/gh/en-gen/OhData)
[![License: MIT](https://img.shields.io/github/license/en-gen/OhData)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/EnGen.OhData.AspNetCore?label=nuget)](https://www.nuget.org/packages/EnGen.OhData.AspNetCore)

Convention-based OData 4.0 server and typed client for ASP.NET Core. Define a profile class, assign handler delegates, and get a spec-faithful OData API - no controllers required (see [docs/spec-compliance.md](docs/spec-compliance.md) for exactly what's covered). Consume it from .NET with a fluent, LINQ-native client.

## Getting Started

Install the server package:

```
dotnet add package EnGen.OhData.AspNetCore
```

Install the client package:

```
dotnet add package EnGen.OhData.Client
```

## Packages

| Package | What it does |
|---------|--------------|
| [![EnGen.OhData.AspNetCore](https://img.shields.io/nuget/v/EnGen.OhData.AspNetCore?label=EnGen.OhData.AspNetCore)](https://www.nuget.org/packages/EnGen.OhData.AspNetCore) | The server framework. |
| [![EnGen.OhData.Client](https://img.shields.io/nuget/v/EnGen.OhData.Client?label=EnGen.OhData.Client)](https://www.nuget.org/packages/EnGen.OhData.Client) | The typed LINQ client. |
| [![EnGen.OhData.AspNetCore.Swashbuckle](https://img.shields.io/nuget/v/EnGen.OhData.AspNetCore.Swashbuckle?label=EnGen.OhData.AspNetCore.Swashbuckle)](https://www.nuget.org/packages/EnGen.OhData.AspNetCore.Swashbuckle) [![EnGen.OhData.AspNetCore.OpenApi](https://img.shields.io/nuget/v/EnGen.OhData.AspNetCore.OpenApi?label=EnGen.OhData.AspNetCore.OpenApi)](https://www.nuget.org/packages/EnGen.OhData.AspNetCore.OpenApi) [![EnGen.OhData.AspNetCore.NSwag](https://img.shields.io/nuget/v/EnGen.OhData.AspNetCore.NSwag?label=EnGen.OhData.AspNetCore.NSwag)](https://www.nuget.org/packages/EnGen.OhData.AspNetCore.NSwag) | Optional API-documentation companions — each documents the OData query parameters (`$filter`, `$orderby`, `$top`, ...) in its respective OpenAPI stack with [one line of registration](#openapi--swagger-documentation). |

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
        Post         = (p, ct) => { db.Products.Add(p); return db.SaveChangesAsync(ct).ContinueWith(_ => (Product?)p, ct); };
        Put          = (id, p, ct) => { db.Products.Update(p); return db.SaveChangesAsync(ct).ContinueWith(_ => p, ct); };
        Patch        = (id, delta, ct) => { var e = db.Products.Find(id); return Task.FromResult(e is null ? null : delta.Patch(e)); };
        Delete       = (id, ct) => { /* remove by id */ return Task.FromResult(true); };
    }
}

// 3. Register in Program.cs
builder.Services.AddOhData(o => o
    .WithPrefix("/odata")
    .AddProfile<ProductProfile>()                              // list profiles explicitly
    // ...or scan assemblies for every EntitySetProfile they contain:
    .AddProfilesFromAssembly(Assembly.GetExecutingAssembly())  // by assembly instance
    .AddProfilesFromAssemblyOf<ProductProfile>());             // by marker type

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
| `GET` | `/odata/Products({key})/Name` | individual property (OData envelope) - rides `GetById` |
| `GET` | `/odata/Products({key})/Name/$value` | raw property value (`text/plain`) |
| `PUT`/`PATCH` | `/odata/Products({key})/Name` | set an individual property (`{"value":...}`) - rides `Patch` |
| `DELETE` | `/odata/Products({key})/Name` | set an individual property to `null` - rides `Patch` |
| `POST` | `/odata/Products` | `Post` |
| `PUT` | `/odata/Products({key})` | `Put` |
| `PATCH` | `/odata/Products({key})` | `Patch` |
| `DELETE` | `/odata/Products({key})` | `Delete` |

Only routes with a handler assigned are registered. Unassigned handlers produce no route.

### OpenAPI / Swagger documentation

Each OpenAPI stack has an optional companion package that documents the OData query parameters
(`$filter`, `$orderby`, `$top`, `$skip`, `$select`, `$expand`, `$count`, `$search`) on OhData
endpoints, driven by each entity set's capability flags. Install the one matching your stack and
register one line — the core package has no dependency on any OpenAPI stack:

| Your stack | Package | Registration |
|---|---|---|
| `Microsoft.AspNetCore.OpenApi` (built-in `AddOpenApi`) | `EnGen.OhData.AspNetCore.OpenApi` | `builder.Services.AddOpenApi(o => o.AddOperationTransformer<OhDataOpenApiOperationTransformer>());` |
| Swashbuckle | `EnGen.OhData.AspNetCore.Swashbuckle` | `builder.Services.AddSwaggerGen(c => c.OperationFilter<OhDataSwaggerOperationFilter>());` |
| NSwag | `EnGen.OhData.AspNetCore.NSwag` | `builder.Services.AddOpenApiDocument(s => s.OperationProcessors.Add(new OhDataNSwagOperationProcessor()));` |

See [docs/openapi.md](docs/openapi.md), [docs/nswag.md](docs/nswag.md), and
[docs/versioning.md](docs/versioning.md) (Swashbuckle multi-doc setup) for details.

### Beyond the basics

The rest of the surface rides other profile declarations - navigation properties (`HasMany`/`HasOptional`/`HasRequired`), `UseETag`, and `BindFunction`/`BindAction` - rather than the plain CRUD handlers above:

| Method | Route | Registered by |
|--------|-------|---------------|
| `GET` | `/odata/Orders({key})/Lines` | `HasMany`/`HasOptional`/`HasRequired` with a `getAll`/`get`/`batchGetAll`/`batchGet` delegate |
| `GET` | `/odata/Orders({key})/Lines/$count` | same, for collection navigations |
| `POST` | `/odata/Orders({key})/Lines` | `HasMany` with a `post` delegate - creates a related entity |
| `GET`/`POST`/`PUT`/`DELETE` | `/odata/Orders({key})/Lines/$ref` | `HasMany`/`HasOptional` with `addRef`/`setRef`/`removeRef` |
| `GET` | `/odata/Products/{FunctionName}` | `BindFunction` (collection-bound) |
| `POST` | `/odata/Products/{ActionName}` | `BindAction` (collection-bound) |
| `GET` | `/odata/Products({key})/{FunctionName}` | `BindEntityFunction` |
| `POST` | `/odata/Products({key})/{ActionName}` | `BindEntityAction` |

See [docs/navigation-routing.md](docs/navigation-routing.md), [docs/property-access.md](docs/property-access.md), [docs/deep-insert.md](docs/deep-insert.md), and [docs/bound-operations.md](docs/bound-operations.md) for the full details behind each row.

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

// Traverse all pages automatically via IAsyncEnumerable - follows @odata.nextLink
await foreach (Product p in client.For<Product>().Filter(x => x.Price > 0).ToAsyncEnumerable())
{
    Console.WriteLine(p.Name);
}

// Get a single entity - returns null on 404
Product? p = await client.For<Product>().Key(42).GetAsync();

// Mutate
Product created = (await client.For<Product>().InsertAsync(new Product { Name = "Cog", Price = 4.99m }))!;
var updated = await client.For<Product>().Key(created.Id)
    .PutAsync(new Product { Id = created.Id, Name = created.Name, Price = 5.49m });
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

## Performance

OhData's minimal-API pipeline was benchmarked head-to-head against `Microsoft.AspNetCore.OData`'s
`ODataController` + `[EnableQuery]` pipeline over the full HTTP round-trip (routing → OData
query-option processing → handler → serialization), same dataset, same requests, correctness
verified before every run. OhData won all 11 scenarios:

| Scenario | OhData | Microsoft.AspNetCore.OData | Speedup | Alloc ratio |
|---|---:|---:|---:|---:|
| GetAll page (100) | 763 µs | 2,821 µs | **3.7×** | 6.3× |
| `$filter` | 1,778 µs | 3,393 µs | **1.9×** | 6.0× |
| `$orderby` | 968 µs | 2,949 µs | **3.0×** | 5.4× |
| `$select` | 878 µs | 1,858 µs | **2.1×** | 1.3× |
| `$top` + `$skip` | 1,262 µs | 2,061 µs | **1.6×** | 4.6× |
| `$count=true` (+`$filter`) | 2,831 µs | 4,740 µs | **1.7×** | 5.4× |
| GetById | 37 µs | 112 µs | **3.0×** | 3.0× |
| POST | 51 µs | 286 µs | **5.6×** | 7.7× |
| PUT | 57 µs | 281 µs | **4.9×** | 7.7× |
| PATCH | 53 µs | 325 µs | **6.2×** | 7.1× |
| DELETE | 16 µs | 24 µs | **1.5×** | 1.3× |

The biggest gaps are on writes (POST/PUT/PATCH, ~5-6× — MS OData's OData-JSON formatters and
EDM-bound serialization dominate there) and full-page reads (~3-3.7×). "Alloc ratio" is how many
times more memory the MS OData pipeline allocates per request. BenchmarkDotNet over in-process
TestServer hosts, identical 1,000-entity dataset and byte-identical requests on both sides, with a
correctness gate run before measurement; see
[src/OhData.Server.Benchmarks/docs/server-comparison-report.md](src/OhData.Server.Benchmarks/docs/server-comparison-report.md)
for the full methodology, raw output, and known asymmetries between the two pipelines.

## Battle-testing

1,100 automated tests protect the framework end to end: 778 in `OhData.AspNetCore.Tests` (server
routing, query options, navigation, ETags, authorization, malformed-payload hardening), 267 in
`OhData.Client.Tests`, 30 in `OhData.MicrosoftODataClient.Tests` (compatibility against the
official `Microsoft.OData.Client`), and 25 across the OpenAPI-integration suites
(`OhData.AspNetCore.OpenApi.Tests`, `OhData.AspNetCore.NSwag.Tests`). Run them yourself with the
commands in [CLAUDE.md](CLAUDE.md#build--test).

---

## Documentation

| Topic | Guide |
|-------|-------|
| Query options (`$filter`, `$orderby`, `$select`, `$expand`, `$count`, `$search`) | [docs/query-options.md](docs/query-options.md) |
| Navigation property routing, `$ref`, and POST-to-navigation | [docs/navigation-routing.md](docs/navigation-routing.md) |
| Individual property access, reads/writes, and `/$value` | [docs/property-access.md](docs/property-access.md) |
| Deep insert (nested related entities in POST) | [docs/deep-insert.md](docs/deep-insert.md) |
| Bound functions and actions | [docs/bound-operations.md](docs/bound-operations.md) |
| ETags and optimistic concurrency | [docs/etags.md](docs/etags.md) |
| Authorization | [docs/authorization.md](docs/authorization.md) |
| API versioning | [docs/versioning.md](docs/versioning.md) |
| OpenAPI (built-in `AddOpenApi`) integration | [docs/openapi.md](docs/openapi.md) |
| NSwag integration | [docs/nswag.md](docs/nswag.md) |
| Client guide | [docs/client.md](docs/client.md) |
| OData 4.0 spec compliance | [docs/spec-compliance.md](docs/spec-compliance.md) |
| Framework architecture | [docs/architecture.md](docs/architecture.md) |
| Migrating from Microsoft.AspNetCore.OData | [docs/migrating-from-microsoft-odata.md](docs/migrating-from-microsoft-odata.md) |
| Deployment (Dockerfile, Render) | [docs/deployment.md](docs/deployment.md) |
| Releasing to NuGet | [docs/releasing.md](docs/releasing.md) |
