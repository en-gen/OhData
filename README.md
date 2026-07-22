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

        // IQueryable path: EF Core translates $filter/$orderby/$skip/$top into the SQL query
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
    .AddEntitySetProfile<ProductProfile>()                              // list profiles explicitly
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

| Package | Registration |
|---|---|
| `EnGen.OhData.AspNetCore.OpenApi` | `builder.Services.AddOpenApi(o => o.AddOperationTransformer<OhDataOpenApiOperationTransformer>());` |
| `EnGen.OhData.AspNetCore.Swashbuckle` | `builder.Services.AddSwaggerGen(c => c.OperationFilter<OhDataSwaggerOperationFilter>());` |
| `EnGen.OhData.AspNetCore.NSwag` | `builder.Services.AddOpenApiDocument(s => s.OperationProcessors.Add(new OhDataNSwagOperationProcessor()));` |

See [docs/openapi.md](docs/openapi.md), [docs/swashbuckle.md](docs/swashbuckle.md),
[docs/nswag.md](docs/nswag.md), and [docs/versioning.md](docs/versioning.md) (multi-doc / versioned
setup) for details.

### Beyond the basics

The rest of the surface rides other profile declarations - navigation properties (`HasMany`/`HasOptional`/`HasRequired`), `UseETag`, and `BindFunction`/`BindAction` - rather than the plain CRUD handlers above. Each declaration registers its routes; the trailing comments show what you get:

```csharp
public class OrdersProfile : EntitySetProfile<int, Order>
{
    public OrdersProfile(AppDbContext db) : base(x => x.Id)
    {
        GetQueryable = _ => Task.FromResult(db.Orders.AsQueryable());

        // Collection navigation. getAll gives the read routes; every parameter after it is
        // OPTIONAL - supply only the ones whose route you want:
        HasMany(
            navigation: x => x.Lines,
            getAll:    (orderId, ct) => Task.FromResult(db.Lines.Where(l => l.OrderId == orderId).AsEnumerable()),
                                          // GET /Orders({key})/Lines  (+ /Lines/$count)
            post:      (orderId, line, ct) => /* … */,   // optional → POST /Orders({key})/Lines  (create a related entity)
            addRef:    (orderId, lineId, ct) => /* … */, // optional → POST/PUT /Orders({key})/Lines/$ref  (link existing)
            removeRef: (orderId, lineId, ct) => /* … */, // optional → DELETE   /Orders({key})/Lines/$ref  (unlink)
            refTargetEntitySet: "Lines");                // optional → $ref routes emit @odata.id links

        // Single-valued navigation → GET /Orders({key})/Customer.
        HasOptional(
            navigation: x => x.Customer,
            get: (orderId, ct) => Task.FromResult(db.Orders.Find(orderId)?.Customer));

        // ETag response header + If-Match concurrency on GET/PUT/PATCH/DELETE.
        UseETag(x => x.RowVersion);

        // Bound operations become routes. The entity-bound pair takes the key as its first parameter.
        BindFunction(Discounted);       // GET  /Orders/Discounted?minOff=…
        BindAction(Archive);            // POST /Orders/Archive
        BindEntityFunction(Total);      // GET  /Orders({key})/Total
        BindEntityAction(Approve);      // POST /Orders({key})/Approve
    }

    static Task<IEnumerable<Order>> Discounted(decimal minOff) => /* … */;
    static Task Archive() => /* … */;
    static Task<decimal> Total(int key) => /* … */;          // first parameter is the entity key
    static Task Approve(int key, string note) => /* … */;    // first parameter is the entity key
}
```

`HasMany(x => x.Lines)` on its own — with no handlers — registers no routes at all; it just declares the navigation for `$metadata` and `$expand`. The same optional-parameter pattern applies to `HasOptional`/`HasRequired`.

See [docs/navigation-routing.md](docs/navigation-routing.md), [docs/property-access.md](docs/property-access.md), [docs/deep-insert.md](docs/deep-insert.md), and [docs/bound-operations.md](docs/bound-operations.md) for the full details behind each declaration.

And to *shrink* the surface instead of growing it: `Ignore(x => x.CostBasis)` hides a property
from `$metadata`, query options, routes, and every request/response body — without touching the
CLR model. See [docs/ignoring-properties.md](docs/ignoring-properties.md).

### Authorization

OhData rides ASP.NET Core's own authentication and authorization - there's no OhData-specific auth system. Protect a whole entity set with one call:

```csharp
RequireAuthorization("AdminOnly");   // or RequireAuthorization() / RequireRoles("Admin")
```

…or authorize **per operation** with `ConfigureAuthorization`, whose per-category lambdas mirror `AuthorizationPolicyBuilder` (requirements accumulate and AND):

```csharp
ConfigureAuthorization(auth => auth
    .Read(r   => r.AllowAnonymous())                     // catalog reads are public
    .Create(c => c.RequirePolicy("Editors"))
    .Update(u => u.RequireRole("Editors").RequireResource())   // Editor AND owns the row
    .Delete(d => d.RequireRole("Admin"))
    .Invoke("Approve", i => i.RequirePolicy("Approvers")));
```

The requirements above are coarse — they answer "can this *kind* of user touch this operation." `.RequireResource()` adds the **instance-level** check "can this user touch *this row*" (owner checks, tenant isolation). OhData loads the `{key}` entity and hands it to ASP.NET Core's native resource-based authorization, so you write one standard handler:

```csharp
// profile: an Update must come from an Editor who also owns the row
ConfigureAuthorization(auth => auth
    .Update(u => u.RequireRole("Editors").RequireResource()));

// handler: the resource IS the loaded entity; requirement.Name selects the operation
public sealed class OrderAuthorizationHandler
    : AuthorizationHandler<OperationAuthorizationRequirement, Order>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, OperationAuthorizationRequirement req, Order order)
    {
        if (req.Name == OhDataOperations.Update.Name &&
            order.OwnerId == ctx.User.FindFirst("sub")?.Value)
            ctx.Succeed(req);   // this Editor owns this order → allow
        return Task.CompletedTask;
    }
}
// Program.cs:  services.AddScoped<IAuthorizationHandler, OrderAuthorizationHandler>();
```

So a request to `PATCH /odata/Orders(42)` runs the role check (must be an `Editors` member) **and** loads order 42 and asks the handler whether this caller owns it — both must pass. The check covers property/navigation/`$ref` routes too (the resource is the parent entity in the path), so there's no bypass. `.RequireResource("PolicyName")` evaluates a **named policy** against the entity instead. Profiles stay free of ASP.NET Core types — requirements are stored as plain policy/role/claim names. See [docs/authorization.md](docs/authorization.md).

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
| Query options (`$filter`, `$orderby`, `$select`, `$expand`, `$count`, `$search`) | [docs/query-options.md](docs/query-options.md) |
| Navigation property routing, `$ref`, and POST-to-navigation | [docs/navigation-routing.md](docs/navigation-routing.md) |
| Individual property access, reads/writes, and `/$value` | [docs/property-access.md](docs/property-access.md) |
| Deep insert (nested related entities in POST) | [docs/deep-insert.md](docs/deep-insert.md) |
| Delta mapping (DTO → entity write path, dependency-free) | [docs/delta-mapping.md](docs/delta-mapping.md) |
| Bound functions and actions | [docs/bound-operations.md](docs/bound-operations.md) |
| ETags and optimistic concurrency | [docs/etags.md](docs/etags.md) |
| Authorization | [docs/authorization.md](docs/authorization.md) |
| API versioning | [docs/versioning.md](docs/versioning.md) |
| OpenAPI (built-in `AddOpenApi`) integration | [docs/openapi.md](docs/openapi.md) |
| Swashbuckle integration | [docs/swashbuckle.md](docs/swashbuckle.md) |
| NSwag integration | [docs/nswag.md](docs/nswag.md) |
| Client guide | [docs/client/index.md](docs/client/index.md) |
| OData 4.0 spec compliance | [docs/spec-compliance.md](docs/spec-compliance.md) |
| Framework architecture | [docs/architecture.md](docs/architecture.md) |
| Migrating from Microsoft.AspNetCore.OData | [docs/migrating-from-microsoft-odata.md](docs/migrating-from-microsoft-odata.md) |
| Deployment (Dockerfile, Render) | [docs/deployment.md](docs/deployment.md) |
| Releasing to NuGet | [docs/releasing.md](docs/releasing.md) |
