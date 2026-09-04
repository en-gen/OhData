# API Versioning

OhData supports multiple simultaneous registrations, each with its own prefix, EDM model, profile
set, and [`EntitySetDefaults`](query-options.md). What one registration exposes has no bearing on
what another exposes — including entity set names, which may repeat freely across registrations.

One thing is **not** per-registration: **a profile type belongs to exactly one registration.**
Registrations share the host's DI container and a process-wide record of which profile types have
been registered, so passing the same profile type to two registrations throws at call time. See
[Sharing behaviour between versions](#sharing-behaviour-between-versions) for how to express "the
same entity set in v1 and v2".

## Named registrations

```csharp
builder.Services.AddOhData("v1", o => o
    .WithPrefix("/v1")
    .AddEntitySetProfile<ProductProfileV1>());

builder.Services.AddOhData("v2", o => o
    .WithPrefix("/v2")
    .AddEntitySetProfile<ProductProfileV2>()      // a distinct type, also named "Products"
    .AddEntitySetProfile<CustomerProfileV2>());   // new entity set in v2

app.MapOhData("v1");
app.MapOhData("v2");
```

Each call produces its own EDM model and route group at its prefix:

```
GET /v1/Products       ← v1 registration
GET /v2/Products       ← v2 registration
GET /v2/Customers      ← v2 only
```

`ProductProfileV1` and `ProductProfileV2` are different types that both set
`EntitySetName = "Products"`. That is what puts `Products` under both prefixes: the entity set
**name** repeats, the profile **type** does not.

## Versioning convenience helpers

`AddOhDataVersion` and `MapOhDataVersion` are included in `EnGen.OhData.AspNetCore` and combine name and prefix into a single call:

```csharp
// AddOhDataVersion / MapOhDataVersion live in Microsoft.Extensions.DependencyInjection /
// Microsoft.AspNetCore.Builder, so no OhData-specific using is required.
builder.Services.AddOhDataVersion("v1", "/v1", o => o.AddEntitySetProfile<ProductProfileV1>());
builder.Services.AddOhDataVersion("v2", "/v2", o => o
    .AddEntitySetProfile<ProductProfileV2>()
    .AddEntitySetProfile<CustomerProfileV2>());

app.MapOhDataVersion("v1");
app.MapOhDataVersion("v2");
```

## Sharing behaviour between versions

Because a profile type can only belong to one registration, "v2's Products behaves exactly like
v1's" is expressed by **subclassing**, not by registering the same type twice. A subclass is a
distinct type, so it satisfies the rule while the behaviour stays declared in one place:

```csharp
public class GenreProfile : EntitySetProfile<string, Genre>
{
    public GenreProfile() : base(x => x.Code)
    {
        EntitySetName = "Genres";
        GetAll = _ => OhDataResult.Success<IEnumerable<Genre>>(DbSeeder.Genres);
    }
}

// v2 exposes the same surface — override members here as v2 diverges.
public class GenreProfileV2 : GenreProfile { }
```

That is the shipped test bench verbatim (`OhData.TestBench.AspNetCore/Profiles.cs`), alongside a
fully independent `MovieProfileV2` for the entity set whose v2 surface genuinely differs.

**If the base profile injects services, the subclass must forward the constructor** — C# does not
inherit constructors, so an empty `{ }` body will not compile against a base that has no
parameterless constructor:

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Products";
        GetQueryable = _ => db.Products.AsQueryable();
    }
}

// Forwards the DbContext; profiles are resolved from DI per request (scoped).
public class ProductProfileV2(AppDbContext db) : ProductProfile(db);
```

Registering one profile type in two registrations throws immediately, at the
`AddEntitySetProfile` call rather than at `MapOhData()`:

```
InvalidOperationException: Profile type 'ProductProfileV1' has already been registered in a
different OhData registration. A profile type cannot be shared across registrations.
```

## OpenAPI / Swagger partitioning

Chain `WithOpenApi()` and `WithGroupName()` on the `RouteGroupBuilder` returned by `MapOhData()`:

```csharp
app.MapOhData("v1").WithOpenApi().WithGroupName("v1");
app.MapOhData("v2").WithOpenApi().WithGroupName("v2");
```

With Swashbuckle, add a `DocInclusionPredicate` so each endpoint appears in the correct doc. To
have Swagger UI also show the OData query parameters on each collection GET endpoint (driven by the
per-entity-set capability flags and `MaxTop`), call the one-line `c.AddOhData()` from the
[`EnGen.OhData.AspNetCore.Swashbuckle`](swashbuckle.md) companion package inside the same
`AddSwaggerGen` call — it registers both the operation filter and the schema-fidelity filter. Both
read the same endpoint metadata regardless of which document an operation is partitioned into, so
they apply per document without extra configuration:

```csharp
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "My API", Version = "v1" });
    c.SwaggerDoc("v2", new OpenApiInfo { Title = "My API", Version = "v2" });
    c.DocInclusionPredicate((docName, apiDesc) =>
        apiDesc.GroupName is null || apiDesc.GroupName == docName);

    c.AddOhData();
});
```

See [swashbuckle.md](swashbuckle.md) for the full filter setup, what gets documented, and the
schema-casing/`Ignore(...)` behavior.

## Default (unnamed) registration

Calling `AddOhData(...)` without a name uses the key `"__default__"` internally and coexists cleanly with named registrations:

```csharp
builder.Services.AddOhData(o => o.WithPrefix("/odata").AddEntitySetProfile<ProductProfile>());
builder.Services.AddOhData("v2", o => o.WithPrefix("/v2").AddEntitySetProfile<ProductProfileV2>());

app.MapOhData();       // maps __default__
app.MapOhData("v2");   // maps v2
```

## Startup validation

Two checks, on two different keys, at two different moments:

| Check | Scope | When it fires |
|---|---|---|
| Duplicate `EntitySetName` | Within one registration | `MapOhData()` |
| Duplicate profile **type** | Across **all** registrations | `AddEntitySetProfile<T>()` |

Two profiles with the same `EntitySetName` in a single registration throw
`InvalidOperationException`. The same name in *different* registrations is fine — that is how
`/v1/Products` and `/v2/Products` coexist.

The profile-type check is the cross-cutting one: the same type in two registrations throws, as does
the same type twice in one registration. It fires from `AddEntitySetProfile<T>()` — and, since
#424, identically from the assembly-scanning overloads (`AddProfilesFrom`,
`AddProfilesFromAssemblyOf<T>`, `AddProfilesFromAssembly`), which route through the same guard — so
the failure surfaces while services are being configured rather than at map time, regardless of
which registration path discovered the type first.

Every example on this page is executed as a test — `VersioningDocExampleTests` in
`OhData.AspNetCore.Tests` boots each one and asserts the documented routes respond. If you change a
snippet here, change the matching test.
