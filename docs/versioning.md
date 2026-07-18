# API Versioning

OhData supports multiple simultaneous registrations with independent prefixes, EDM models, and profile sets. Each registration is completely isolated - no shared state.

## Named registrations

```csharp
builder.Services.AddOhData("v1", o => o
    .WithPrefix("/v1")
    .AddProfile<ProductProfileV1>());

builder.Services.AddOhData("v2", o => o
    .WithPrefix("/v2")
    .AddProfile<ProductProfileV1>()
    .AddProfile<CustomerProfileV2>());   // new entity set in v2

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
using OhData.AspNetCore.Versioning;

builder.Services.AddOhDataVersion("v1", "/v1", o => o.AddProfile<ProductProfileV1>());
builder.Services.AddOhDataVersion("v2", "/v2", o => o
    .AddProfile<ProductProfileV1>()
    .AddProfile<CustomerProfileV2>());

app.MapOhDataVersion("v1");
app.MapOhDataVersion("v2");
```

## OpenAPI / Swagger partitioning

Chain `WithOpenApi()` and `WithGroupName()` on the `RouteGroupBuilder` returned by `MapOhData()`:

```csharp
app.MapOhData("v1").WithOpenApi().WithGroupName("v1");
app.MapOhData("v2").WithOpenApi().WithGroupName("v2");
```

With Swashbuckle, add a `DocInclusionPredicate` so each endpoint appears in the correct doc:

```csharp
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "My API", Version = "v1" });
    c.SwaggerDoc("v2", new OpenApiInfo { Title = "My API", Version = "v2" });
    c.DocInclusionPredicate((docName, apiDesc) =>
        apiDesc.GroupName is null || apiDesc.GroupName == docName);
});
```

Register `OhDataSwaggerOperationFilter` (from the [`EnGen.OhData.AspNetCore.Swashbuckle`](https://www.nuget.org/packages/EnGen.OhData.AspNetCore.Swashbuckle) companion package — the core server package carries no Swashbuckle dependency) to have Swagger UI show `$filter`/`$orderby`/`$top`/`$skip`/`$select`/`$expand`/`$count`/`$search` as documented query parameters on each collection GET endpoint, driven by the per-entity-set capability flags (`FilterEnabled`, `OrderByEnabled`, etc.) and `MaxTop`:

```
dotnet add package EnGen.OhData.AspNetCore.Swashbuckle
```

```csharp
builder.Services.AddSwaggerGen(c =>
{
    c.OperationFilter<OhDataSwaggerOperationFilter>();
    c.SchemaFilter<OhDataSwaggerSchemaFilter>();
});
```

`OhDataSwaggerSchemaFilter` (same package) omits properties excluded via
`EntitySetProfile.Ignore(...)` from generated schemas, so documents match the real wire shape —
see [ignoring-properties.md](ignoring-properties.md#openapi--swagger-documents). Each filter is
independent; register only the one you need, or both.

Write routes get a real request-body schema and collection GET routes get a typed
`ODataCollectionResponse<T>` response automatically, via `OhDataApiDescriptionProvider` in the
core package - no Swashbuckle-specific setup needed beyond `AddSwaggerGen` itself. See
[openapi.md](openapi.md#request-bodies-on-write-routes) for details (applies identically to
Swashbuckle). `WithSummary()`/`WithDescription()` on collection GET routes are applied by
`OhDataSwaggerOperationFilter` explicitly - see [openapi.md](openapi.md#read-path-summaries).

## Default (unnamed) registration

Calling `AddOhData(...)` without a name uses the key `"__default__"` internally and coexists cleanly with named registrations:

```csharp
builder.Services.AddOhData(o => o.WithPrefix("/odata").AddProfile<ProductProfile>());
builder.Services.AddOhData("v2", o => o.WithPrefix("/v2").AddProfile<ProductProfileV2>());

app.MapOhData();       // maps __default__
app.MapOhData("v2");   // maps v2
```

## Startup validation

Each registration independently validates for duplicate entity set names. Two profiles with the same `EntitySetName` within a single registration throw `InvalidOperationException` at startup. Duplicate names across different registrations are allowed.
