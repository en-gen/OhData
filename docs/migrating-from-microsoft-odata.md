# Migrating from Microsoft.AspNetCore.OData

This guide is for teams running `Microsoft.AspNetCore.OData` (`ODataController` + `[EnableQuery]`)
who are evaluating a move to OhData. It maps the concepts you already know onto OhData's
equivalents, walks through one entity migrated end-to-end, and is upfront about what changes,
what you gain, and what you lose.

This is not a "drop-in replacement" pitch. The two libraries build on the same underlying OData
primitives (`Microsoft.OData.ModelBuilder`, `Microsoft.AspNetCore.OData.Deltas.Delta<T>`) but take
different positions on which OData surface to expose and how requests get routed. Read
[docs/spec-compliance.md](spec-compliance.md) before migrating anything you rely on today — it is
the authoritative list of what OhData does and does not implement.

## Concept mapping

| Microsoft.AspNetCore.OData | OhData | Notes |
|---|---|---|
| `ODataController` subclass per entity set | `EntitySetProfile<TKey, TModel>` subclass per entity set | A profile is a plain class, not an MVC controller — no action methods, no model binding attributes. |
| `[EnableQuery]` on an action | Per-option flags on the profile: `FilterEnabled`, `OrderByEnabled`, `SelectEnabled`, `CountEnabled`, `ExpandEnabled`, `PropertyAccessEnabled`, plus `MaxTop` | `[EnableQuery(AllowedQueryOptions = ...)]` becomes explicit booleans set in the constructor instead of an attribute parameter. |
| `IQueryable<T>` action return + `[EnableQuery]` (SQL pushdown) | `GetQueryable` delegate | Framework applies `$filter`/`$orderby`/`$skip`/`$top` via `ApplyTo(IQueryable)` — same EF Core pushdown behavior. |
| Plain `IEnumerable<T>`/`List<T>` action return | `GetAll` delegate | No query options are applied to `GetAll` — it is the deliberately "dumb" path. Use `GetQueryable` if you want `$filter`/`$orderby`/`$top`/`$skip` to work. |
| `ODataConventionModelBuilder` + `modelBuilder.EntitySet<T>("Name")` in `Program.cs` | Implicit: entity set name defaults to a pluralized form of the model type name. Override with `EntitySetName = "..."` in the profile constructor | No central model-builder file; each profile owns its own slice of the EDM. |
| `modelBuilder.EntityType<T>().HasMany(...)` / `HasOptional(...)` for navigation | `HasMany(...)` / `HasOptional(...)` / `HasRequired(...)` called in the profile constructor | Same vocabulary, but the call also optionally registers the `GET .../{Nav}` route and `$ref` routes via delegate parameters — one declaration does both jobs. |
| `services.AddControllers().AddOData(o => o.AddRouteComponents(prefix, model))` + `app.MapControllers()` | `services.AddOhData(o => o.WithPrefix(prefix).AddProfile<T>())` + `app.MapOhData()` | `AddOhData` is `AddScoped`, not singleton, per profile, so constructor injection of a scoped `DbContext` is safe without extra plumbing. |
| `Delta<T>` parameter on a `Patch` action | `Delta<TModel>` parameter on the `Patch` delegate | **Same type** — `Microsoft.AspNetCore.OData.Deltas.Delta<T>`. OhData does not reinvent partial-update semantics; it reuses the type MS OData ships, including `delta.Patch(existing)` and `GetChangedPropertyNames()`. |
| Multiple `AddRouteComponents` calls / a versioning library for `/v1`, `/v2` prefixes | `AddOhData("v1", ...)` / `AddOhData("v2", ...)` named registrations, or the `AddOhDataVersion`/`MapOhDataVersion` convenience pair | Each named registration is fully isolated: its own EDM model, its own profile set, its own prefix. See [docs/versioning.md](versioning.md). |
| `[Authorize]` / `[Authorize(Policy = "...")]` on the controller, or on individual actions | `RequireAuthorization()` / `RequireAuthorization("PolicyName")` / `RequireRoles(...)` for the whole set, or `ConfigureAuthorization(auth => auth.Read(...).Writes(...)...)` for **per-operation** granularity — plus `.RequireResource()` for instance-level (owner) checks | Same ASP.NET Core auth pipeline underneath — OhData calls `RequireAuthorization` on the generated endpoints for you. See [docs/authorization.md](authorization.md). |
| `[HttpGet]`/`[HttpPost]` controller actions with custom route templates for bound functions/actions | `BindFunction(handler)` / `BindAction(handler)` / `BindEntityFunction(handler)` / `BindEntityAction(handler)` called in the constructor | The delegate's method name becomes the OData operation name; no route template or attribute needed. See [docs/bound-operations.md](bound-operations.md). |

## Worked example: `Product`

Both versions below target the same `Product` model and the same EF Core `AppDbContext`, so the
comparison is purely about routing/query plumbing, not about the domain code:

```csharp
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string Category { get; set; } = "";
}
```

### Before — `ODataController` + `[EnableQuery]`

`Program.cs`:

```csharp
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("Store"));
builder.Services
    .AddControllers()
    .AddOData(options => options
        .EnableQueryFeatures(maxTopValue: 100)
        .AddRouteComponents("odata", GetEdmModel()));

var app = builder.Build();
app.MapControllers();
app.Run();

static IEdmModel GetEdmModel()
{
    var modelBuilder = new ODataConventionModelBuilder();
    modelBuilder.EnableLowerCamelCase();
    modelBuilder.EntitySet<Product>("Products");
    return modelBuilder.GetEdmModel();
}
```

`ProductsController.cs`:

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;

namespace MyApi.Controllers;

public class ProductsController : ODataController
{
    private readonly AppDbContext _db;

    public ProductsController(AppDbContext db)
    {
        _db = db;
    }

    [EnableQuery(PageSize = 100, MaxTop = 100)]
    public IQueryable<Product> Get() => _db.Products;

    [EnableQuery]
    public SingleResult<Product> Get(int key) =>
        SingleResult.Create(_db.Products.Where(p => p.Id == key));

    public async Task<IActionResult> Post([FromBody] Product product, CancellationToken ct)
    {
        _db.Products.Add(product);
        await _db.SaveChangesAsync(ct);
        return Created(product);
    }

    public async Task<IActionResult> Put(int key, [FromBody] Product product, CancellationToken ct)
    {
        Product? existing = await _db.Products.FindAsync(new object[] { key }, ct);
        if (existing is null) return NotFound();
        existing.Name = product.Name;
        existing.Price = product.Price;
        existing.Category = product.Category;
        await _db.SaveChangesAsync(ct);
        return Updated(existing);
    }

    public async Task<IActionResult> Patch(int key, [FromBody] Delta<Product> delta, CancellationToken ct)
    {
        Product? existing = await _db.Products.FindAsync(new object[] { key }, ct);
        if (existing is null) return NotFound();
        delta.Patch(existing);
        await _db.SaveChangesAsync(ct);
        return Updated(existing);
    }

    public async Task<IActionResult> Delete(int key, CancellationToken ct)
    {
        Product? existing = await _db.Products.FindAsync(new object[] { key }, ct);
        if (existing is null) return NotFound();
        _db.Products.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
```

### After — `EntitySetProfile<int, Product>`

`Program.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using OhData.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("Store"));
builder.Services.AddOhData(o => o
    .WithPrefix("/odata")
    .AddProfile<ProductProfile>());

var app = builder.Build();
app.MapOhData();
app.Run();
```

`ProductProfile.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OhData.Abstractions;

namespace MyApi.Profiles;

public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        SelectEnabled = true;
        MaxTop = 100;

        GetQueryable = _ => Task.FromResult(db.Products.AsQueryable());
        GetById = (id, ct) => db.Products.FindAsync(new object[] { id }, ct).AsTask();

        Post = async (product, ct) =>
        {
            db.Products.Add(product);
            await db.SaveChangesAsync(ct);
            return product;
        };

        Put = async (id, product, ct) =>
        {
            Product? existing = await db.Products.FindAsync(new object[] { id }, ct);
            if (existing is null) return null!; // framework treats null as "not found" / triggers upsert
            existing.Name = product.Name;
            existing.Price = product.Price;
            existing.Category = product.Category;
            await db.SaveChangesAsync(ct);
            return existing;
        };

        Patch = async (id, delta, ct) =>
        {
            Product? existing = await db.Products.FindAsync(new object[] { id }, ct);
            if (existing is null) return null;
            delta.Patch(existing);
            await db.SaveChangesAsync(ct);
            return existing;
        };

        Delete = async (id, ct) =>
        {
            Product? existing = await db.Products.FindAsync(new object[] { id }, ct);
            if (existing is null) return false;
            db.Products.Remove(existing);
            await db.SaveChangesAsync(ct);
            return true;
        };
    }
}
```

Two things worth calling out about the "after" side:

- **`Put`'s delegate type is `Func<TKey, TModel, CancellationToken, Task<TModel>>`** (non-nullable
  return), but the framework treats a `null` result as "not found" and — if `AllowUpsert = true`
  and `Post` is configured — falls back to calling `Post` to create the entity (OData §11.4.4).
  Returning `null!` on the not-found branch is a documented pattern (used the same way in
  OhData's own test bench), not a workaround you invented.
- Nothing above needs `[FromBody]`, `[EnableQuery]`, `SingleResult.Create`, or a route template —
  the framework derives the route surface entirely from which delegates are non-null. Set a
  handler, get a route; leave it `null` (the default), and no route is registered for that verb.

## What you gain

- **Throughput and allocations.** OhData's minimal-API pipeline was benchmarked head-to-head
  against `ODataController` + `[EnableQuery]` over the full HTTP round trip (routing → OData
  query-option processing → handler → serialization), same in-process `TestServer` setup,
  identical dataset, identical requests, with a correctness smoke-check gating every run. OhData
  won all 11 measured scenarios — writes (POST/PUT/PATCH) were roughly 5-6× faster with up to
  7.7× fewer bytes allocated; reads were 2-3.7× faster. Full methodology, the complete results
  table, and the known asymmetries between the two pipelines are in
  [src/OhData.Server.Benchmarks/docs/server-comparison-report.md](../src/OhData.Server.Benchmarks/docs/server-comparison-report.md).
  Reproduce it yourself with `dotnet run -c Release --project src/OhData.Server.Benchmarks -- --filter "*"`.
- **Minimal-API idiom.** No controllers, no `[FromBody]`/`[EnableQuery]` attributes, no
  `SingleResult.Create` ceremony. A profile is a plain class; the handler surface (`GetAll`,
  `GetQueryable`, `GetById`, `Post`, `Put`, `Patch`, `Delete`) is just delegate fields assigned in
  the constructor, and DI works the same way it does for any other scoped service.
- **Batch-loaded `$expand`.** The `HasMany(navigation, batchGetAll: ...)` overload lets a
  collection navigation resolve for an entire page of parents in one call (e.g. one
  `WHERE ParentId IN (...)` query), instead of the N+1 pattern that a naive per-entity resolver
  produces. `$expand=Lines` on a 100-row page issues one query for the lines, not 100 — see
  `docs/navigation-routing.md`.
- **An honest, row-by-row conformance sheet.** [docs/spec-compliance.md](spec-compliance.md) is
  not a marketing conformance badge — it is a spec-section-by-spec-section table of what's
  implemented, what's partially implemented, and what's a documented non-goal, updated whenever
  the framework's behavior changes. Treat it as the source of truth before you migrate anything
  you depend on.

## What you lose or must rework

OhData does not attempt to be a superset of `Microsoft.AspNetCore.OData`. The following are
implemented in Microsoft's library (to varying degrees) and are **not** implemented in OhData
today — pulled directly from the "not targeted" / "known limitations" sections of
[docs/spec-compliance.md](spec-compliance.md):

- **`$batch` (JSON batch requests).** Not supported. If your clients issue batched multi-request
  payloads against `/$batch`, there is no equivalent endpoint — you would need to split batch
  calls into individual requests, which changes both client code and the number of round trips.
- **`$compute` (OData 4.01).** Unimplemented. OhData's `Microsoft.AspNetCore.OData` package
  reference is pinned to `[9.4.*, 10)` across target frameworks, which predates that package's
  4.01 support — the blocker is the pinned package version, not a deliberate design choice against
  the feature. No other OData 4.01/Advanced-conformance addition (aliases, cross joins, etc.) is
  attempted either.
- **`$apply` / data-aggregation transformations.** Not implemented, and not mentioned anywhere in
  the conformance tables — it was never brought into scope. If you rely on server-side
  `groupby`/`aggregate` transformations, that logic needs to move into your own query/handler code
  (e.g. a bespoke bound function backed by a hand-written aggregation query).
- **Delta links / change-tracking queries and asynchronous (`Prefer: respond-async`) requests.**
  Neither is implemented or discussed in the spec-compliance page — if your integration polls
  `$deltatoken` links for incremental sync, or issues async long-running requests, that
  functionality has no equivalent in OhData and would need to be rebuilt outside the framework
  (e.g. a custom polling/webhook endpoint alongside the OData surface).
- **`@odata.bind` (linking an existing entity inline during insert).** Detected and rejected with
  `501 Not Implemented`, at any nesting depth, rather than silently ignored. Use the `$ref`
  endpoints (`POST`/`DELETE .../{Nav}/$ref`) to link existing entities instead — see
  `docs/navigation-routing.md`.
- **`PATCH` partial-merge on a complex (nested object) property.** `PUT` full-replacement of a
  complex property is supported; a `PATCH` that should merge only some of a nested object's fields
  returns `400 Bad Request` rather than performing the merge.
- **SQL column projection for `$select`.** OhData applies `$select` by trimming the JSON response
  after the full row is fetched (to preserve camelCase naming — see `docs/architecture.md`), not
  by projecting only the selected columns in the SQL query. If your `$select` usage exists
  specifically to reduce database I/O for wide tables, that benefit does not carry over.
- **Per-operation authorization.** `Microsoft.AspNetCore.OData` lets you put `[Authorize]` on
  individual controller actions. OhData matches this with `ConfigureAuthorization(...)` — authorize
  `Read`/`Create`/`Update`/`Delete`/`Invoke` independently, with per-category requirements that mirror
  `AuthorizationPolicyBuilder`, plus `.RequireResource()` for instance-level (owner/tenant) checks.
  `RequireAuthorization`/`RequireRoles` remain the simple all-operations option. See
  `docs/authorization.md`.

None of the above are things OhData plans to silently paper over — each is either called out as
`❌` in the spec-compliance table or discussed under "Known limitations" there. If your workload
depends on any of them today, budget time to rework that part of the integration (or stay on
`Microsoft.AspNetCore.OData` for that specific route) before switching.

## See also

- [docs/spec-compliance.md](spec-compliance.md) — full conformance detail
- [docs/architecture.md](architecture.md) — how the endpoint factory and EDM builder work internally
- [docs/versioning.md](versioning.md) — named registrations for `/v1`, `/v2`, etc.
- [docs/navigation-routing.md](navigation-routing.md) — `HasMany`/`HasOptional`/`HasRequired`, `$ref`, batch `$expand`
- [docs/bound-operations.md](bound-operations.md) — `BindFunction`/`BindAction`/`BindEntityFunction`/`BindEntityAction`
- [docs/authorization.md](authorization.md) — `RequireAuthorization`/`RequireRoles`
- [src/OhData.Server.Benchmarks/docs/server-comparison-report.md](../src/OhData.Server.Benchmarks/docs/server-comparison-report.md) — the benchmark referenced above
