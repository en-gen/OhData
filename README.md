# ![OhData logo](https://raw.githubusercontent.com/en-gen/OhData/develop/assets/icon-64.png) OhData

[![CI](https://github.com/en-gen/OhData/actions/workflows/ci.yml/badge.svg?branch=develop)](https://github.com/en-gen/OhData/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/en-gen/OhData/graph/badge.svg)](https://codecov.io/gh/en-gen/OhData)
[![License: MIT](https://img.shields.io/github/license/en-gen/OhData)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/EnGen.OhData.AspNetCore?label=nuget)](https://www.nuget.org/packages/EnGen.OhData.AspNetCore)
[![Docs](https://img.shields.io/badge/docs-en--gen.github.io%2FOhData-2b6cb0)](https://en-gen.github.io/OhData/)

Convention-based OData 4.0 server and typed client for ASP.NET Core. Define a profile class, assign handler delegates, and get a spec-faithful OData API - no controllers required (see [docs/spec-compliance.md](docs/spec-compliance.md) for exactly what's covered). Consume it from .NET with a fluent, LINQ-native client.

📖 **Documentation site: [en-gen.github.io/OhData](https://en-gen.github.io/OhData/)** — getting started, the EF Core walkthrough, and every feature guide.

Try it live — fire real `$filter`/`$orderby`/`$expand` queries (writes too) at a deployed OhData demo service from an interactive API reference, or hit the raw [v2 service document](https://ohdata.onrender.com/v2) directly:

[![Scalar](https://img.shields.io/badge/Scalar-1A1A1A?logo=scalar&logoColor=white)](https://ohdata.onrender.com/scalar/v2)
[![Swagger UI](https://img.shields.io/badge/Swagger_UI-85EA2D?logo=swagger&logoColor=black)](https://ohdata.onrender.com/swagger)

(Free-tier hosting: the first load after a quiet spell takes a moment to wake up, and demo data is ephemeral — anything you write disappears whenever the instance recycles.)

Or run it locally: the clone-and-run [EF Core + SQLite sample](samples/OhData.Sample.EfCoreSqlite/) puts a real relational database behind OhData and logs the SQL, so you can watch `$filter`/`$orderby`/`$top` become `WHERE`/`ORDER BY`/`LIMIT`.

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
| [![EnGen.OhData.AspNetCore.Mapper](https://img.shields.io/nuget/v/EnGen.OhData.AspNetCore.Mapper?label=EnGen.OhData.AspNetCore.Mapper)](https://www.nuget.org/packages/EnGen.OhData.AspNetCore.Mapper) | Optional. Serve an API model (DTO) that differs from your EF Core entity, with `$filter`/`$orderby`/`$top`/`$skip`/`$count` still running in SQL — see [API model / entity separation](docs/api-model-mapping.md). Also carries the DTO write path, [delta mapping](docs/delta-mapping.md), which moved here in 2.0.0. |
| [![EnGen.OhData.AspNetCore.Swashbuckle](https://img.shields.io/nuget/v/EnGen.OhData.AspNetCore.Swashbuckle?label=EnGen.OhData.AspNetCore.Swashbuckle)](https://www.nuget.org/packages/EnGen.OhData.AspNetCore.Swashbuckle) [![EnGen.OhData.AspNetCore.OpenApi](https://img.shields.io/nuget/v/EnGen.OhData.AspNetCore.OpenApi?label=EnGen.OhData.AspNetCore.OpenApi)](https://www.nuget.org/packages/EnGen.OhData.AspNetCore.OpenApi) [![EnGen.OhData.AspNetCore.NSwag](https://img.shields.io/nuget/v/EnGen.OhData.AspNetCore.NSwag?label=EnGen.OhData.AspNetCore.NSwag)](https://www.nuget.org/packages/EnGen.OhData.AspNetCore.NSwag) | Optional API-documentation companions — each documents the OData query parameters (`$filter`, `$orderby`, `$top`, ...) in its respective OpenAPI stack with [one line of registration](#openapi--swagger-documentation). |

---

## Server quick start

<!-- compile -->
```csharp
// 1. Define your entity and your EF Core context
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
}

// 2. Create a profile - assign only the handlers you need. Each assignment IS a route;
//    a handler you never assign is a route that does not exist.
public class ProductProfile : EntitySetProfile<int, Product>
{
    private readonly AppDbContext _db;

    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        _db = db;

        FilterEnabled  = true;
        OrderByEnabled = true;
        CountEnabled   = true;
        SelectEnabled  = true;

        // The constructor reads as the surface: which routes exist, and what serves each.
        GetQueryable = GetProducts;
        GetById      = GetProduct;
        Post         = CreateProduct;
        Put          = ReplaceProduct;
        Patch        = UpdateProduct;
        Delete       = DeleteProduct;
    }

    // GetQueryable returns the query itself - no Task, no OhDataResult, no CancellationToken.
    // Composing an IQueryable does no I/O and produces no result; the framework appends
    // $filter/$orderby/$skip/$top and owns the execution, which is where the await and the
    // cancellation belong. Every other handler DOES return Task<OhDataResult<T>>.
    //
    // `Product` here is the API MODEL, which in this quickstart happens to be the EF entity.
    // They do not have to be the same type - see "DTOs and EF entities" below.
    private IQueryable<Product> GetProducts() => _db.Products;

    // GetById, Put and Patch are OhDataResult<Product?> — null is a legitimate outcome there
    // (404, or an upsert on Put). Post is OhDataResult<Product>: a null from it is a contract
    // violation and answers 500.
    private async Task<OhDataResult<Product?>> GetProduct(int id, CancellationToken ct) =>
        OhDataResult.Success<Product?>(await _db.Products.FirstOrDefaultAsync(p => p.Id == id, ct));

    private async Task<OhDataResult<Product>> CreateProduct(Product product, CancellationToken ct)
    {
        _db.Products.Add(product);
        await _db.SaveChangesAsync(ct);
        return OhDataResult.Success(product);
    }

    private async Task<OhDataResult<Product?>> ReplaceProduct(int id, Product product, CancellationToken ct)
    {
        _db.Products.Update(product);
        await _db.SaveChangesAsync(ct);
        return OhDataResult.Success<Product?>(product);
    }

    private async Task<OhDataResult<Product?>> UpdateProduct(int id, Delta<Product> delta, CancellationToken ct)
    {
        var existing = await _db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (existing is null) return OhDataResult.Success<Product?>(null);   // -> 404

        delta.Patch(existing);
        await _db.SaveChangesAsync(ct);
        return OhDataResult.Success<Product?>(existing);
    }

    private async Task<OhDataResult<bool>> DeleteProduct(int id, CancellationToken ct)
    {
        var existing = await _db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (existing is null) return OhDataResult.Success(false);           // -> 204 (IdempotentDelete)

        _db.Products.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return OhDataResult.Success(true);
    }
}

```

<!-- compile -->
```csharp
// 3. Register in Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("Shop"));
builder.Services.AddOhData(o => o
    .WithPrefix("/odata")
    .AddEntitySetProfile<ProductProfile>()                     // list profiles explicitly
    // ...or scan assemblies for every EntitySetProfile they contain:
    .AddProfilesFromAssembly(Assembly.GetExecutingAssembly())  // by assembly instance
    .AddProfilesFromAssemblyOf<ProductProfile>());             // by marker type

// 4. Map endpoints after app.Build()
var app = builder.Build();
app.MapOhData();
app.Run();
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
call its one-line `AddOhData()` — the canonical wiring recipe that registers both the operation and
schema components; the core package has no dependency on any OpenAPI stack:

| Package | Registration |
|---|---|
| `EnGen.OhData.AspNetCore.OpenApi` | `builder.Services.AddOpenApi(o => o.AddOhData());` |
| `EnGen.OhData.AspNetCore.Swashbuckle` | `builder.Services.AddSwaggerGen(c => c.AddOhData());` |
| `EnGen.OhData.AspNetCore.NSwag` | `builder.Services.AddOpenApiDocument((s, sp) => s.AddOhData(sp));` |

On the OpenApi and NSwag variants, `AddOhData` takes optional `authRequirements` / `securitySchemeId`
parameters to also reflect OhData's per-operation authorization (security requirement + `401`/`403`)
into the document.

See [docs/openapi.md](docs/openapi.md), [docs/swashbuckle.md](docs/swashbuckle.md),
[docs/nswag.md](docs/nswag.md), and [docs/versioning.md](docs/versioning.md) (multi-doc / versioned
setup) for details.

### Beyond CRUD

The rest of the surface rides other profile declarations — navigation properties
(`HasMany`/`HasOptional`/`HasRequired`), `UseETag`, `BindFunction`/`BindAction`, and `Ignore()` to
*shrink* the wire shape — rather than the plain CRUD handlers above. Each declaration registers its
own routes. Worked example in
**[Beyond the basics](docs-site/getting-started.md#beyond-the-basics)**;
full details in [docs/navigation-routing.md](docs/navigation-routing.md),
[docs/property-access.md](docs/property-access.md),
[docs/bound-operations.md](docs/bound-operations.md), [docs/etags.md](docs/etags.md), and
[docs/ignoring-properties.md](docs/ignoring-properties.md).

### DTOs and EF entities

`TModel` is the **API model** — the shape on the wire and in `$metadata`. The quick start above uses
the EF entity as its own API model because that is the shortest thing that works, not because the
two must be the same type. Project a DTO in the handler and EF still translates
`$filter`/`$orderby`/`$select`/`$top` to SQL; `$expand` rides a `batchGetAll` navigation delegate.
See **[docs/dtos-and-ef-entities.md](docs/dtos-and-ef-entities.md)** for the projection recipes and
the `batchGetAll`-versus-eager-`JOIN` trade, or
**[docs/api-model-mapping.md](docs/api-model-mapping.md)** to declare the correspondence instead of
writing the projection.

### Authorization

OhData rides ASP.NET Core's own authentication and authorization — you keep your existing scheme,
policies, roles and `IAuthorizationHandler`s, and profiles never reference an ASP.NET Core type.
What OhData adds is a *declaration* layer: `RequireAuthorization()`/`RequireRoles()` to gate a whole
entity set, `ConfigureAuthorization(...)` to gate the five operation categories independently, and
`.RequireResource()` for instance-level "can this user touch *this row*" checks against the loaded
`{key}` entity. Requirements are stored as plain policy/role/claim names and replayed onto the
endpoints; the evaluation is entirely ASP.NET Core's.

```csharp
ConfigureAuthorization(auth => auth
    .Read(r   => r.AllowAnonymous())                           // catalog reads are public
    .Create(c => c.RequirePolicy("Editors"))
    .Update(u => u.RequireRole("Editors").RequireResource())   // Editor AND owns the row
    .Delete(d => d.RequireRole("Admin"))
    .Invoke("Approve", i => i.RequirePolicy("Approvers")));
```

One scope caveat worth knowing before you rely on it: **a rule is per profile, and it does not
compose across a navigation** — a navigation is authorized by the profile that *declares* it, never
by the profile owning its target entity set. `MapOhData()` warns at startup for every such pair.

See **[docs/authorization.md](docs/authorization.md)** for the resource handler, the route tables,
the `$metadata` and unbound-operation seams, and the
[full reasoning on that caveat](docs/authorization.md#authorization-is-per-profile-and-does-not-compose-across-a-navigation).

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
// Registration - the typed-client overload configures the HttpClient and registers
// OhDataClient to be constructed with it (OhDataClient has an HttpClient constructor).
builder.Services.AddHttpClient<OhDataClient>(c =>
    c.BaseAddress = new Uri("https://api.example.com/odata/"));

// Injection
public class MyService(OhDataClient client) { ... }
```

---

## Performance

OhData's minimal-API pipeline was benchmarked head-to-head against `Microsoft.AspNetCore.OData`'s
`ODataController` + `[EnableQuery]` pipeline over the full HTTP round-trip (routing → OData
query-option processing → handler → serialization), same dataset, byte-identical requests,
correctness verified before every run. OhData is **faster on 10 of the 11 scenarios and allocates
less on all 11**:

| Scenario | OhData | Microsoft.AspNetCore.OData | Speedup | Alloc ratio |
|---|---:|---:|---:|---:|
| GetAll page (100) | 1,056 µs | 3,346 µs | **3.2×** | 4.6× |
| `$filter` | 2,176 µs | 3,831 µs | **1.8×** | 4.7× |
| `$orderby` | 1,599 µs | 4,016 µs | **2.6×** | 4.3× |
| `$select` | 1,634 µs | 2,093 µs | **1.3×** | 1.2× |
| `$top` + `$skip` | 1,019 µs | 2,666 µs | **2.6×** | 3.7× |
| `$count=true` (+`$filter`) | 3,436 µs | 5,792 µs | **1.7×** | 4.3× |
| GetById | 55 µs | 123 µs | **2.2×** | 2.8× |
| POST | 60 µs | 301 µs | **5.0×** | 7.7× |
| PUT | 63 µs | 299 µs | **4.8×** | 7.5× |
| PATCH | 67 µs | 309 µs | **4.6×** | 6.5× |
| DELETE | 34.7 µs | 35.2 µs | tie (1.02× ± 0.08) | 1.2× |

<sub>Measured 2026-09-04 at commit `4e123b7` · BenchmarkDotNet v0.15.8 · .NET 10.0.11 · AMD Ryzen 9
5950X · Windows 11 25H2. Speedup is BenchmarkDotNet's per-iteration `Ratio` against the OhData
baseline; "alloc ratio" is how many times more memory the MS OData pipeline allocates per
request.</sub>

The widest gaps are on writes (POST/PUT/PATCH, ~4.6–5× — MS OData's OData-JSON formatters and
EDM-bound serialization dominate there) and full-page reads (2.6–3.2×). DELETE is reported as a
tie rather than a win because 1.02× ± 0.08 is indistinguishable from parity: neither framework does
much on that route beyond routing.

**[docs/performance.md](docs/performance.md)** has the full methodology, the raw BenchmarkDotNet
capture, the `$expand`/`$levels` half, the known asymmetries between the two pipelines, and a
v1.7.0 control run establishing that 2.0.0 introduced no timing or allocation regression.

## Battle-testing

OhData sits on your request path, so it's tested like it belongs there:

- **Integration tests, not mocks.** The server suite spins up a real ASP.NET Core host and drives it over HTTP — every route, every query option, navigation and `$ref` link management, ETag concurrency, and per-operation *and* instance-level authorization. A large share is deliberately **adversarial**: malformed JSON bodies, hostile and oversized query options, and concurrent or cancelled requests, each asserted to fail cleanly with the correct OData error envelope rather than a 500.
- **Proven against a real database.** EF Core + SQLite tests capture the SQL the provider actually emits and assert that `$filter`/`$orderby`/`$select` are translated *into the SQL query itself* — executed by the database, not by fetching every row and filtering in memory.
- **Exercised end-to-end, client and server together.** OhData's own typed client is integration-tested against a live server spun up in-process, so every query, write, and concurrency path round-trips through the real HTTP pipeline. A separate suite drives the server through the official `Microsoft.OData.Client`, proving on-the-wire interoperability with a widely used third-party consumer — conformance you can see, not conformance on paper.
- **OpenAPI across every supported stack.** The generated document is tested against the built-in `AddOpenApi`, NSwag, and Swashbuckle, so it's correct whichever you wire up.
- **Load and performance, on every change.** CI runs a [k6](https://k6.io/) load test against a live server on each build, and BenchmarkDotNet suites track server and client throughput and allocations so a regression shows up in review, not in production.

Run the whole thing yourself with `dotnet test src/OhData.sln`.

## Versioning & support

OhData follows [SemVer](https://semver.org/): patch releases fix bugs, minor releases add
functionality without breaking the public API, and any breaking change means a major version.
The no-breaking-changes half of that contract is **enforced at build time**, not just promised —
every release is diffed against the previously published API surface via .NET package validation
(`PackageValidationBaselineVersion`), so an unintended breaking change fails the release build.
Behavior changes that don't break the API are called out explicitly in the
[CHANGELOG](CHANGELOG.md).

**The latest 1.x release is the supported version.** Fixes — including security fixes — ship as a
new release on top of it; older releases receive no back-ports. `develop` carries pre-release
work and is not for production use. See [SECURITY.md](SECURITY.md) for vulnerability reporting
and the full support policy.

---

## Documentation

The full documentation — getting started, the EF Core + SQLite walkthrough, and every guide below — is published at **[en-gen.github.io/OhData](https://en-gen.github.io/OhData/)**. The same guides live in [`docs/`](docs/):

| Topic | Guide |
|-------|-------|
| Choosing a read handler (`GetQueryable` / `GetAll` / `GetODataQueryable`) | [docs/read-handlers.md](docs/read-handlers.md) |
| Query options (`$filter`, `$orderby`, `$select`, `$count`, `$search`) | [docs/query-options.md](docs/query-options.md) |
| `$expand`, pushdown, and nested server-driven paging | [docs/expand.md](docs/expand.md) |
| Complexity limits (`MaxExpandTop`, depth and breadth ceilings) | [docs/complexity-limits.md](docs/complexity-limits.md) |
| Unsupported system query options (the `501`/`400` taxonomy) | [docs/unsupported-query-options.md](docs/unsupported-query-options.md) |
| Navigation property routing, `$ref`, and POST-to-navigation | [docs/navigation-routing.md](docs/navigation-routing.md) |
| Individual property access, reads/writes, and `/$value` | [docs/property-access.md](docs/property-access.md) |
| Deep insert (nested related entities in POST), and deep update's enforced non-support | [docs/deep-insert.md](docs/deep-insert.md) |
| DTOs and EF entities (hand-projected API models, `batchGetAll` navigations) | [docs/dtos-and-ef-entities.md](docs/dtos-and-ef-entities.md) |
| API model / entity separation (the Mapper package) | [docs/api-model-mapping.md](docs/api-model-mapping.md) |
| Delta mapping (DTO → entity write path) | [docs/delta-mapping.md](docs/delta-mapping.md) |
| Error handling (`OhDataResult<T>`, the rejection factories, the error envelope) | [docs/error-handling.md](docs/error-handling.md) |
| Open types (dynamic property bags on complex types) | [docs/open-types.md](docs/open-types.md) |
| Polymorphic entity sets (TPH inheritance), `@odata.type`, and what differs | [docs/polymorphism.md](docs/polymorphism.md) |
| Bound functions and actions | [docs/bound-operations.md](docs/bound-operations.md) |
| ETags and optimistic concurrency | [docs/etags.md](docs/etags.md) |
| Authorization | [docs/authorization.md](docs/authorization.md) |
| API versioning | [docs/versioning.md](docs/versioning.md) |
| Observability (metrics, tracing, logging) | [docs/observability.md](docs/observability.md) |
| OpenAPI (built-in `AddOpenApi`) integration | [docs/openapi.md](docs/openapi.md) |
| Swashbuckle integration | [docs/swashbuckle.md](docs/swashbuckle.md) |
| NSwag integration | [docs/nswag.md](docs/nswag.md) |
| Client guide | [docs/client/index.md](docs/client/index.md) |
| OData 4.0 spec compliance | [docs/spec-compliance.md](docs/spec-compliance.md) |
| Framework architecture | [docs/architecture.md](docs/architecture.md) |
| Migrating from Microsoft.AspNetCore.OData | [docs/migrating-from-microsoft-odata.md](docs/migrating-from-microsoft-odata.md) |
| Differences from Microsoft.AspNetCore.OData | [docs/differences-from-microsoft-odata.md](docs/differences-from-microsoft-odata.md) |
| Performance & benchmarks | [docs/performance.md](docs/performance.md) |
| Deployment (Dockerfile, Render) | [docs/deployment.md](docs/deployment.md) |
| Releasing to NuGet | [docs/releasing.md](docs/releasing.md) |
