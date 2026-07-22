# OpenAPI (Microsoft.AspNetCore.OpenApi)

OhData's core server package carries no OpenAPI dependency. To document the OData query
parameters ($filter, $orderby, $top, $skip, $select, $expand, $count, $search) on collection
endpoints when using ASP.NET Core's built-in `AddOpenApi()`/`MapOpenApi()` pipeline, install the
`EnGen.OhData.AspNetCore.OpenApi` companion package.

```
dotnet add package EnGen.OhData.AspNetCore.OpenApi
```

## Registration

The recommended one-liner is `o.AddOhData()`. It is the canonical wiring recipe — you do not need
to know the transformer class names:

```csharp
using OhData.AspNetCore.OpenApi;

builder.Services.AddOpenApi(o => o.AddOhData());

// ...

app.MapOpenApi();
```

This registers **both** the operation transformer (documents the OData query parameters) and the
schema transformer (keeps generated schemas honest for profiles that use `Ignore(...)` — see
[below](#ignored-properties-omitted-from-schemas)).

To also surface OhData's per-operation authorization in the document (#219/#220), pass the opt-in
parameters — `securitySchemeId` emits an operation-level `security` requirement plus `401`/`403`
responses referencing a scheme your app already defines, and `authRequirements` appends a
human-readable requirements section to each secured operation's description:

```csharp
builder.Services.AddOpenApi(o => o.AddOhData(
    authRequirements: AuthRequirementDisclosure.Kinds,
    securitySchemeId: "Bearer"));
```

Both default to off (`null`). See [authorization.md](authorization.md) for the auth-reflection
boundary — OhData references the scheme by id but never defines it.

### À la carte

Each transformer is independent. To register only one, call it directly instead of `AddOhData()`:

```csharp
builder.Services.AddOpenApi(o =>
{
    o.AddOperationTransformer<OhDataOpenApiOperationTransformer>();
    o.AddSchemaTransformer<OhDataOpenApiSchemaTransformer>();
});
```

The opt-in auth transformers have à la carte equivalents too —
`o.AddOperationTransformer(new OhDataOpenApiSecurityOperationTransformer("Bearer"))` and
`o.AddOperationTransformer(new OhDataOpenApiAuthRequirementsOperationTransformer(AuthRequirementDisclosure.Kinds))`.

## What gets documented

`OhDataOpenApiOperationTransformer` implements `IOpenApiOperationTransformer`. For every endpoint
that carries `OhDataQueryOptionsMetadata` (collection GET routes, `$count`, and `GetById`), it adds
query parameters to the generated OpenAPI document driven by the entity set's capability flags:

| Parameter | Added when |
|---|---|
| `$top` / `$skip` | Always, once per operation (paged collection endpoints) |
| `$filter` | `FilterEnabled` |
| `$orderby` | `OrderByEnabled` |
| `$select` | `SelectEnabled` |
| `$expand` | `ExpandEnabled` |
| `$count` | `CountEnabled` |
| `$search` | a `Search` handler is configured |

The `$top` parameter's description includes the entity set's `MaxTop` value when one is
configured, so consumers of the generated document see the server-enforced page-size cap.

The transformer is idempotent with respect to parameters another transformer may have already
added under the same name (e.g. `$top`) - it will not add a duplicate.

## Request bodies on write routes

Entity POST/PUT/PATCH, nav-POST, property PUT/PATCH, `$ref` POST/PUT, and bound/unbound actions
all read and JSON-deserialize their request bodies by hand (see the "POST/PUT/PATCH deserialize
the request body by hand" note in `CLAUDE.md`) rather than via a bound minimal-API parameter, so
ApiExplorer sees no request body for them by default - no body editor, no schema, in any OpenAPI
UI. `AddOhData` registers `OhDataApiDescriptionProvider` (an `IApiDescriptionProvider`) to fix
this: it reads a plain `OhDataRequestBodyMetadata` marker attached to each write route at
registration time and adds the corresponding body parameter/schema to the route's
`ApiDescription`, which every OpenAPI document generator built on ApiExplorer - Microsoft.AspNetCore.OpenApi,
NSwag, and Swashbuckle alike - then picks up automatically. No package installation or
configuration is required beyond `AddOhData` itself; this part of docs-fidelity ships in the core
package, unlike the query-parameter documentation above (which needs the doc-stack-specific
companion package).

Body types documented: entity POST/PUT/PATCH use the model type (`TModel`); nav POST uses the
navigation's item type; property PUT/PATCH and `$ref` POST/PUT use small documentation-only
wrapper types (`ODataPropertyWriteRequest<T>` for `{"value": ...}`, `ODataRefWriteRequest` for
`{"@odata.id": "..."}`); bound/unbound actions with parameters use a generic object schema, with
the parameter names and CLR types listed in the body's description (an action's parameters are
deserialized by name out of one JSON object, not bound to a single CLR type, so there is no single
schema to generate).

## Property routes omitted by default

Individual structural-property routes — `GET /{Set}({key})/{Property}`, its `/$value` variant, and
the `PUT`/`PATCH`/`DELETE` property writes — are **excluded from the generated document by
default**. They number up to four per property, per entity set, and would otherwise dominate the
docs. This is applied at the ApiExplorer level (`ExcludeFromDescription`), so it covers this
package, Swashbuckle, and NSwag identically, and it does not affect runtime behavior — the routes
stay fully functional. Opt them back in per profile or server-wide via `PropertyRouteDocsEnabled`;
see [property-access.md](property-access.md#api-documentation-visibility).

## Typed collection responses

Collection GET routes (on `GetQueryable`, `GetAll`, and Priority-1) and collection-valued
navigation GET routes document their response as `ODataCollectionResponse<T>` - a small public
DTO (`@odata.context`, `@odata.count`, `@odata.nextLink`, `value`) that mirrors the real OData
collection envelope - instead of a bare, schema-less `200`. It exists purely for documentation:
the actual response is still built by hand as an ordered dictionary so annotations serialize
before entity properties. `$ref` routes similarly document `ODataRefResponse`/
`ODataRefCollectionResponse`, and structural-property GET routes document
`ODataPropertyResponse<T>`.

## Schema property casing matches the wire

OhData owns its response JSON casing — PascalCase by default, independent of the host's
`HttpJsonOptions` (see [query-options.md → JSON property casing](query-options.md#json-property-casing)).
`OhDataOpenApiSchemaTransformer` renames each generated schema property key to that same
response casing, so the document advertises exactly what responses emit rather than the host
serializer's casing (camelCase by ASP.NET Core default). A `[JsonPropertyName]` rename wins over the
policy — in the schema and on the wire alike — matching the response precedence. Renaming is keyed by
CLR model type (the same key the ignore suppression below uses).

Renaming follows the whole response graph, not just the top-level entity: nested complex types (a
`HomeAddress` property, a `List<Tag>` collection, a dictionary value) and inherited base classes each
get their own component schema, and every one of them is renamed to the response casing. The
transformer drives that descent itself — because renaming a property key removes the host-cased key
the runtime uses to locate a child schema, so left to its own traversal the runtime would stop at any
renamed complex property and leave nested-only component schemas at host casing (#260).

## Ignored properties omitted from schemas

Properties excluded via `EntitySetProfile.Ignore(...)` never cross the wire (see
[ignoring-properties.md](ignoring-properties.md)), but OpenAPI schemas are generated from the CLR
type — which still has the property. `OhDataOpenApiSchemaTransformer` implements
`IOpenApiSchemaTransformer` and removes each ignored member from its model type's generated schema
(request and response alike, since both share the component schema), so the document matches the
real wire shape. Matching is by CLR member, immune to the naming policy — the profile ignores the
CLR name (`CostBasis`), and the surviving keys are emitted in OhData's response casing (`CostBasis`
by default; `costBasis` under a camelCase opt-in). Suppression is keyed by CLR model type, so a
same-named property on a different (un-ignored) type is untouched.

## Read-path summaries

Collection GET routes carry a `WithSummary`/`WithDescription` distinguishing which read path
backs them: `GetQueryable` routes get "List {Set} (queryable)" with a description naming the live
query options (driven by the profile's capability flags); `GetAll` routes get "List {Set} (simple
read path)" with a description noting that `$top`/`$skip`/`$select`/`$expand`/`$count` are applied
server-side post-materialization while `$filter`/`$orderby` are not supported. These flow through
`IEndpointSummaryMetadata`/`IEndpointDescriptionMetadata`, which Microsoft.AspNetCore.OpenApi reads
natively; the NSwag and Swashbuckle companion packages apply the same metadata explicitly (see
their respective docs) since neither doc stack surfaces it automatically.

## Same convention as the Swashbuckle companion

This package is the `Microsoft.AspNetCore.OpenApi` counterpart to
[`EnGen.OhData.AspNetCore.Swashbuckle`](swashbuckle.md) (see [swashbuckle.md](swashbuckle.md) for
the Swashbuckle `IOperationFilter`/`ISchemaFilter` equivalents). Both packages read the same
`OhDataQueryOptionsMetadata` endpoint metadata and apply the same gating rules, so switching
between the two OpenAPI generation pipelines does not change what gets documented.
