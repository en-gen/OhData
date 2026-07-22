# API Versioning

OhData supports multiple simultaneous registrations with independent prefixes, EDM models, and profile sets. Each registration is completely isolated - no shared state.

## Named registrations

```csharp
builder.Services.AddOhData("v1", o => o
    .WithPrefix("/v1")
    .AddEntitySetProfile<ProductProfileV1>());

builder.Services.AddOhData("v2", o => o
    .WithPrefix("/v2")
    .AddEntitySetProfile<ProductProfileV1>()
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

## Versioning convenience helpers

`AddOhDataVersion` and `MapOhDataVersion` are included in `EnGen.OhData.AspNetCore` and combine name and prefix into a single call:

```csharp
// AddOhDataVersion / MapOhDataVersion live in Microsoft.Extensions.DependencyInjection /
// Microsoft.AspNetCore.Builder, so no OhData-specific using is required.
builder.Services.AddOhDataVersion("v1", "/v1", o => o.AddEntitySetProfile<ProductProfileV1>());
builder.Services.AddOhDataVersion("v2", "/v2", o => o
    .AddEntitySetProfile<ProductProfileV1>()
    .AddEntitySetProfile<CustomerProfileV2>());

app.MapOhDataVersion("v1");
app.MapOhDataVersion("v2");
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

Each registration independently validates for duplicate entity set names. Two profiles with the same `EntitySetName` within a single registration throw `InvalidOperationException` at startup. Duplicate names across different registrations are allowed.
