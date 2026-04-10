# API Versioning

API versioning is not built into the core framework — it is a planned extension package (`OhData.AspNetCore.Versioning`).

## Intended design

The preferred approach is path-segment versioning: `/v1/Entities`, `/v2/Entities`.

Each API version is its own `AddOhData()` registration with a different prefix:

```csharp
// Hypothetical future API — not yet implemented
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

app.MapOhData("v1");
app.MapOhData("v2");
```

## What needs to change in the core framework first

Currently `OhDataRegistration` is registered as an unnamed singleton — a second `AddOhData()` call would overwrite the first. The core framework needs **named options** support (`IOptionsSnapshot<OhDataRegistration>` keyed by version name) before multiple simultaneous registrations are possible.

Until that prerequisite is in place, versioning can be approximated by running two separate ASP.NET Core applications, or by using different prefixes manually with a single `AddOhData()` call and multiple profiles that share entity set names.
