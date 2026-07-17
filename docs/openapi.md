# OpenAPI (Microsoft.AspNetCore.OpenApi)

OhData's core server package carries no OpenAPI dependency. To document the OData query
parameters ($filter, $orderby, $top, $skip, $select, $expand, $count, $search) on collection
endpoints when using ASP.NET Core's built-in `AddOpenApi()`/`MapOpenApi()` pipeline, install the
`EnGen.OhData.AspNetCore.OpenApi` companion package.

```
dotnet add package EnGen.OhData.AspNetCore.OpenApi
```

## Registration

```csharp
using OhData.AspNetCore;

builder.Services.AddOpenApi(o => o.AddOperationTransformer<OhDataOpenApiOperationTransformer>());

// ...

app.MapOpenApi();
```

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

## Same convention as the Swashbuckle companion

This package is the `Microsoft.AspNetCore.OpenApi` counterpart to
[`EnGen.OhData.AspNetCore.Swashbuckle`](versioning.md#openapi--swagger-partitioning) (see
[versioning.md](versioning.md) for the Swashbuckle `IOperationFilter` equivalent). Both packages
read the same `OhDataQueryOptionsMetadata` endpoint metadata and apply the same gating rules, so
switching between the two OpenAPI generation pipelines does not change what gets documented.
