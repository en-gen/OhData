# API Versioning

OhData supports multiple simultaneous registrations with different prefixes. Each registration has its own EDM model, route prefix, and set of profiles.

## Named registrations

```csharp
builder.Services.AddOhData("v1", ohdata =>
    ohdata
        .WithPrefix("/v1")
        .AddProfile<ProductProfileV1>()
);

builder.Services.AddOhData("v2", ohdata =>
    ohdata
        .WithPrefix("/v2")
        .AddProfile<ProductProfileV1>()
        .AddProfile<CustomerProfileV2>()   // new in v2
);

app.MapOhData("v1").WithOpenApi().WithGroupName("v1");
app.MapOhData("v2").WithOpenApi().WithGroupName("v2");
```

Each named registration is stored as `AddKeyedSingleton<OhDataRegistration>(name)`, so multiple calls don't overwrite each other.

The unnamed overloads (`AddOhData(...)` / `MapOhData()`) use the default key `"__default__"` internally, so they coexist cleanly with named registrations.

## `OhData.AspNetCore.Versioning` convenience package

```csharp
using OhData.AspNetCore.Versioning;

// Combines name + prefix in one call
builder.Services.AddOhDataVersion("v1", "/v1", o => o.AddProfile<ProductProfileV1>());
builder.Services.AddOhDataVersion("v2", "/v2", o => o.AddProfile<ProductProfileV2>());

app.MapOhDataVersion("v1");
app.MapOhDataVersion("v2");
```

`AddOhDataVersion(name, prefix, configure)` is equivalent to:
```csharp
services.AddOhData(name, o => { o.WithPrefix(prefix); configure(o); });
```

## Swagger partitioning

With Swashbuckle, add a `DocInclusionPredicate` to route each endpoint to the matching Swagger doc:

```csharp
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "My API v1", Version = "v1" });
    c.SwaggerDoc("v2", new() { Title = "My API v2", Version = "v2" });
    c.DocInclusionPredicate((docName, apiDesc) =>
        apiDesc.GroupName is null || apiDesc.GroupName == docName);
});

app.MapOhData("v1").WithOpenApi().WithGroupName("v1");
app.MapOhData("v2").WithOpenApi().WithGroupName("v2");
```

## Startup validation

Each registration independently validates for duplicate entity set names within that registration. A registration with two profiles that have the same `EntitySetName` will throw `InvalidOperationException` at startup.
