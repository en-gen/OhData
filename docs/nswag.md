# NSwag Integration

The core `EnGen.OhData.AspNetCore` package carries no OpenAPI-generator dependency. To have NSwag document the OData query parameters (`$filter`, `$orderby`, `$top`, `$skip`, `$select`, `$expand`, `$count`, `$search`) on OhData's collection GET endpoints, add the `EnGen.OhData.AspNetCore.NSwag` companion package:

```
dotnet add package EnGen.OhData.AspNetCore.NSwag
```

## Registration

The recommended one-liner is `s.AddOhData(sp)`. It is the canonical wiring recipe — you do not need
to know the processor class names. The schema processor needs the host's `IServiceProvider` to reach
the OhData registrations, so call it from the service-provider overload of `AddOpenApiDocument` and
pass `sp` through:

```csharp
using OhData.AspNetCore.NSwag;

builder.Services.AddOpenApiDocument((s, sp) => s.AddOhData(sp));
```

This registers **both** the operation processor (documents the OData query parameters) and the
schema processor (schema fidelity for `Ignore(...)`d properties — see
[below](#ignored-properties-omitted-from-schemas)).

To also surface OhData's per-operation authorization in the document (#219/#220), pass the opt-in
parameters — `securitySchemeId` emits an operation-level `security` requirement plus `401`/`403`
responses referencing a scheme your app already defines, and `authRequirements` appends a
human-readable requirements section to each secured operation's description:

```csharp
builder.Services.AddOpenApiDocument((s, sp) => s.AddOhData(sp,
    authRequirements: AuthRequirementDisclosure.Kinds,
    securitySchemeId: "Bearer"));
```

Both default to off (`null`). See [authorization.md](authorization.md) for the auth-reflection
boundary — OhData references the scheme by id but never defines it.

### À la carte

Each processor is independent. To register only one, add it directly instead of `AddOhData(sp)`:

```csharp
builder.Services.AddOpenApiDocument((s, sp) =>
{
    s.OperationProcessors.Add(new OhDataNSwagOperationProcessor());
    s.SchemaSettings.SchemaProcessors.Add(new OhDataNSwagSchemaProcessor(sp));
});
```

If no profile uses `Ignore(...)` you can register only the operation processor with the
single-argument `AddOpenApiDocument(s => ...)` overload.

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

## Schema property casing matches the wire

OhData owns its response JSON casing — PascalCase by default, independent of the host's
`HttpJsonOptions` (see [query-options.md → JSON property casing](query-options.md#json-property-casing)).
`OhDataNSwagSchemaProcessor` renames each generated schema property key to that same response casing,
so the document advertises exactly what responses emit rather than the host serializer's casing
(camelCase by ASP.NET Core default). A `[JsonPropertyName]` rename wins over the policy — in the
schema and on the wire alike. Renaming is keyed by CLR model type (the same key the ignore
suppression below uses).

Renaming covers the whole response graph, not just the top-level entity: nested complex types (a
`HomeAddress` property, a `List<Tag>` collection, a dictionary value) and inherited base classes each
get their own component schema and are renamed too. NSwag models an inherited type as
`allOf: [{$ref base}, {own props}]`, so the processor renames a derived type's own keys on that
inline `allOf` member as well as on the schema itself, and the base class gets its own renamed
component (#260).

## Ignored properties omitted from schemas

Properties excluded via `EntitySetProfile.Ignore(...)` never cross the wire (see
[ignoring-properties.md](ignoring-properties.md)), but NSwag generates schemas from the CLR type —
which still has the property. `OhDataNSwagSchemaProcessor` implements NJsonSchema's
`ISchemaProcessor` and removes each ignored member from its model type's generated schema (request
and response alike, since both share the component schema), so the document matches the real wire
shape. Matching is by CLR member (honoring `[JsonPropertyName]`), immune to the naming policy — the
profile ignores the CLR name (`CostBasis`), and the surviving keys are emitted in OhData's response
casing (`CostBasis` by default; `costBasis` under a camelCase opt-in). Suppression is keyed by CLR
model type, so a same-named property on a different (un-ignored) type is untouched.

## Versioned registrations

For multiple `AddOhData`/`MapOhData` registrations, partition documents by NSwag document name the same way you would with Swashbuckle's `DocInclusionPredicate` - see [versioning.md](versioning.md) for the OhData side of setting `WithGroupName()` on the route group, and configure `AddOpenApiDocument` per document name / API group as usual for NSwag.
