# NSwag Integration

The core `EnGen.OhData.AspNetCore` package carries no OpenAPI-generator dependency. To have NSwag document the OData query parameters (`$filter`, `$orderby`, `$top`, `$skip`, `$select`, `$expand`, `$count`, `$search`) on OhData's collection GET endpoints, add the `EnGen.OhData.AspNetCore.NSwag` companion package:

```
dotnet add package EnGen.OhData.AspNetCore.NSwag
```

## Registration

Register `OhDataNSwagOperationProcessor` with NSwag's document generator:

```csharp
using OhData.AspNetCore;

builder.Services.AddOpenApiDocument(s =>
{
    s.OperationProcessors.Add(new OhDataNSwagOperationProcessor());
});
```

Minimal API endpoints need ASP.NET Core's `ApiExplorer` enabled for NSwag to discover them at all (this is the same prerequisite the Swashbuckle integration relies on):

```csharp
builder.Services.AddEndpointsApiExplorer();
```

Then serve the document as usual:

```csharp
app.UseOpenApi();          // GET /swagger/{documentName}/swagger.json
app.UseSwaggerUi();        // optional interactive UI
```

## What gets documented

`OhDataNSwagOperationProcessor` inspects the `OhDataQueryOptionsMetadata` that OhData attaches to each generated route and adds the corresponding query parameters, mirroring `EnGen.OhData.AspNetCore.Swashbuckle`'s `OhDataSwaggerOperationFilter` parameter-for-parameter:

- `$top` / `$skip` - added once per paged collection endpoint whenever `OhDataQueryOptionsMetadata` is present and `$top` isn't already documented (a duplicate guard, in case another processor already added it). The `$top` description includes the server-side cap when the entity set sets `MaxTop`.
- `$filter` - added when `FilterEnabled`.
- `$orderby` - added when `OrderByEnabled`.
- `$select` - added when `SelectEnabled`.
- `$expand` - added when `ExpandEnabled`.
- `$count` - added when `CountEnabled`.
- `$search` - added when the entity set has a `Search` handler configured.

Routes with no `OhDataQueryOptionsMetadata` (non-OhData minimal API endpoints in the same app, or OhData routes that don't carry query-option metadata) are left untouched.

Note that `OhDataQueryOptionsMetadata` is attached to more than the top-level collection GET route - it's also present on `GET /{EntitySet}/$count` and on the single-entity `GET /{EntitySet}({key})` route (which supports `$select`/`$expand` in its own right). Because the processor's `$top`/`$skip` guard only checks "is `OhDataQueryOptionsMetadata` present and is `$top` absent", both of those routes will also pick up `$top`/`$skip` parameters even though paging doesn't apply to them - this is intentional, existing behavior shared with the Swashbuckle filter, not something specific to the NSwag integration.

## Versioned registrations

For multiple `AddOhData`/`MapOhData` registrations, partition documents by NSwag document name the same way you would with Swashbuckle's `DocInclusionPredicate` - see [versioning.md](versioning.md) for the OhData side of setting `WithGroupName()` on the route group, and configure `AddOpenApiDocument` per document name / API group as usual for NSwag.
