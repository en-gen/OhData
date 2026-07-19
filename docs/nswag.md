# NSwag Integration

The core `EnGen.OhData.AspNetCore` package carries no OpenAPI-generator dependency. To have NSwag document the OData query parameters (`$filter`, `$orderby`, `$top`, `$skip`, `$select`, `$expand`, `$count`, `$search`) on OhData's collection GET endpoints, add the `EnGen.OhData.AspNetCore.NSwag` companion package:

```
dotnet add package EnGen.OhData.AspNetCore.NSwag
```

## Registration

Register `OhDataNSwagOperationProcessor` (query parameters) and `OhDataNSwagSchemaProcessor`
(schema fidelity for `Ignore(...)`d properties — see
[below](#ignored-properties-omitted-from-schemas)) with NSwag's document generator. The schema
processor needs the host's `IServiceProvider` to reach the OhData registrations, so use the
service-provider overload of `AddOpenApiDocument`:

```csharp
using OhData.AspNetCore;

builder.Services.AddOpenApiDocument((s, sp) =>
{
    s.OperationProcessors.Add(new OhDataNSwagOperationProcessor());
    s.SchemaSettings.SchemaProcessors.Add(new OhDataNSwagSchemaProcessor(sp));
});
```

(Each processor is independent — if no profile uses `Ignore(...)` you can register only the
operation processor with the single-argument overload, as before.)

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

## Request bodies, typed collection responses, and read-path summaries

`OhDataApiDescriptionProvider` (registered by `AddOhData` in the core package - no NSwag-specific
setup needed) gives write routes a real request-body schema, and collection GET routes document a
typed `ODataCollectionResponse<T>` envelope instead of a bare `200`. See
[openapi.md](openapi.md#request-bodies-on-write-routes) for the full description; it applies
identically here since all three doc stacks read the same `ApiDescription`.

One thing NSwag does *not* pick up automatically: `WithSummary()`/`WithDescription()` on
collection GET routes (see [openapi.md](openapi.md#read-path-summaries)). `OhDataNSwagOperationProcessor`
applies them explicitly from `IEndpointSummaryMetadata`/`IEndpointDescriptionMetadata` endpoint
metadata, without overwriting a summary/description NSwag already populated from another source
(e.g. XML doc comments).

## Ignored properties omitted from schemas

Properties excluded via `EntitySetProfile.Ignore(...)` never cross the wire (see
[ignoring-properties.md](ignoring-properties.md)), but NSwag generates schemas from the CLR type —
which still has the property. `OhDataNSwagSchemaProcessor` implements NJsonSchema's
`ISchemaProcessor` and removes each ignored member from its model type's generated schema (request
and response alike, since both share the component schema), so the document matches the real wire
shape. Matching respects `[JsonPropertyName]` and the System.Text.Json naming policy the document
generator is configured with — the profile ignores the CLR name (`CostBasis`), the schema key is
the JSON name (`costBasis`). Suppression is keyed by CLR model type, so a same-named property on a
different (un-ignored) type is untouched.

## Versioned registrations

For multiple `AddOhData`/`MapOhData` registrations, partition documents by NSwag document name the same way you would with Swashbuckle's `DocInclusionPredicate` - see [versioning.md](versioning.md) for the OhData side of setting `WithGroupName()` on the route group, and configure `AddOpenApiDocument` per document name / API group as usual for NSwag.
