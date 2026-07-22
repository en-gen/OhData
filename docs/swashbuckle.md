# Swashbuckle Integration

OhData's core `EnGen.OhData.AspNetCore` package carries no OpenAPI-generator dependency. To have
Swashbuckle document the OData query parameters (`$filter`, `$orderby`, `$top`, `$skip`, `$select`,
`$expand`, `$count`, `$search`) on OhData's collection GET endpoints — and keep the generated
schemas faithful to the real wire shape — add the `EnGen.OhData.AspNetCore.Swashbuckle` companion
package:

```
dotnet add package EnGen.OhData.AspNetCore.Swashbuckle
```

## Registration

The recommended one-liner is `c.AddOhData()`. It is the canonical wiring recipe — you do not need to
know the filter class names:

```csharp
using OhData.AspNetCore.Swashbuckle;

builder.Services.AddSwaggerGen(c => c.AddOhData());
```

This registers **both** the operation filter (documents the OData query parameters) and the schema
filter (schema fidelity for `Ignore(...)`d properties and response casing — see
[below](#ignored-properties-and-schema-casing)).

### À la carte

Each filter is independent. To register only one, call it directly instead of `AddOhData()`:

```csharp
builder.Services.AddSwaggerGen(c =>
{
    c.OperationFilter<OhDataSwaggerOperationFilter>();
    c.SchemaFilter<OhDataSwaggerSchemaFilter>();
});
```

The one-line minimum is just the operation filter:

```csharp
builder.Services.AddSwaggerGen(c => c.OperationFilter<OhDataSwaggerOperationFilter>());
```

Minimal API endpoints need ASP.NET Core's `ApiExplorer` enabled for Swashbuckle to discover them at
all:

```csharp
builder.Services.AddEndpointsApiExplorer();
```

## What gets documented

`OhDataSwaggerOperationFilter` implements `IOperationFilter`. For every endpoint that carries the
`OhDataQueryOptionsMetadata` OhData attaches at registration time, it adds the corresponding query
parameters, driven by the entity set's capability flags:

| Parameter | Added when |
|---|---|
| `$top` / `$skip` | Always, once per operation (whenever `OhDataQueryOptionsMetadata` is present and `$top` isn't already documented) |
| `$filter` | `FilterEnabled` |
| `$orderby` | `OrderByEnabled` |
| `$select` | `SelectEnabled` |
| `$expand` | `ExpandEnabled` |
| `$count` | `CountEnabled` |
| `$search` | a `Search` handler is configured (`SearchEnabled`) |

The `$top` parameter's description includes the entity set's `MaxTop` value when one is configured,
so consumers of the generated document see the server-enforced page-size cap. The `$top`/`$skip`
guard only checks whether `OhDataQueryOptionsMetadata` is present and `$top` isn't already
documented, so it is idempotent with respect to a parameter another filter added under the same
name.

`OhDataQueryOptionsMetadata` is attached to more than the top-level collection GET route — it is
also present on `GET /{EntitySet}/$count` and on the single-entity `GET /{EntitySet}({key})` route
(which supports `$select`/`$expand` in its own right), so those routes pick up `$top`/`$skip` too.
This is intentional, existing behavior shared with the OpenAPI and NSwag companions.

## Request bodies, typed collection responses, and read-path summaries

Write routes get a real request-body schema and collection GET routes get a typed
`ODataCollectionResponse<T>` envelope automatically, via `OhDataApiDescriptionProvider` in the core
package — no Swashbuckle-specific setup needed beyond `AddSwaggerGen` itself. See
[openapi.md](openapi.md#request-bodies-on-write-routes) for the full description; it applies
identically here since all three doc stacks read the same `ApiDescription`.

`WithSummary()`/`WithDescription()` on collection GET routes (see
[openapi.md](openapi.md#read-path-summaries)) are not surfaced by Swashbuckle automatically;
`OhDataSwaggerOperationFilter` applies them explicitly from
`IEndpointSummaryMetadata`/`IEndpointDescriptionMetadata` endpoint metadata, without overwriting a
summary/description already populated from another source (e.g. XML doc comments).

## Ignored properties and schema casing

`OhDataSwaggerSchemaFilter` implements `ISchemaFilter` and keeps generated schemas faithful to the
real wire shape in two ways, both keyed by CLR model type:

- **Ignored properties omitted.** Properties excluded via `EntitySetProfile.Ignore(...)` never cross
  the wire (see [ignoring-properties.md](ignoring-properties.md#openapi--swagger-documents)), but
  OpenAPI schemas are generated from the CLR type — which still has the property. The filter removes
  each ignored member (and its `required` entry) from its model type's generated schema, request and
  response alike, since both share the component schema. Matching is by CLR member, immune to the
  naming policy, so a same-named property on a different (un-ignored) type is untouched.
- **Schema casing matches the wire.** OhData owns its response JSON casing — PascalCase by default,
  independent of the host's `HttpJsonOptions` (see
  [query-options.md → JSON property casing](query-options.md#json-property-casing)). The filter
  renames each surviving schema property key from the host serializer's casing (camelCase by
  ASP.NET Core default) to OhData's response casing, so the document advertises exactly what
  responses emit. A `[JsonPropertyName]` rename wins over the policy, matching the response
  precedence.

The filter uses Swashbuckle's own `ISerializerDataContractResolver` to map CLR property names to the
JSON names the schema keys use, and resolves the OhData registrations lazily at
document-generation time (by which point `app.MapOhData()` has forced every registration to
resolve, so the maps cannot be stale).

## Versioned / multi-document setup

For multiple `AddOhData`/`MapOhData` registrations, partition documents by Swagger document name
with a `DocInclusionPredicate`, pairing it with `WithGroupName()` on the route group returned by
`MapOhData()`. The two filters above still apply per document — they read the same endpoint
metadata regardless of which document an operation lands in. See
[versioning.md](versioning.md#openapi--swagger-partitioning) for the full multi-document walkthrough
(`SwaggerDoc`, `DocInclusionPredicate`, and `WithGroupName()`).

## Same convention as the other companions

This package is the Swashbuckle counterpart to the built-in
[`Microsoft.AspNetCore.OpenApi`](openapi.md) and [NSwag](nswag.md) companions. All three read the
same `OhDataQueryOptionsMetadata` endpoint metadata and apply the same gating rules, so switching
between the OpenAPI generation pipelines does not change what gets documented.
