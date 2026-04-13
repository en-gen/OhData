# Architecture

This document describes how OhData works under the hood. It's intended for contributors and for engineers who need to understand the framework's behaviour deeply - for example, when using `AdvancedConfigure` or building an extension on top of OhData.

## Core idea

An OData API is fully derivable from three things:

1. The entity type and its key type
2. Which CRUD operations are supported (handler delegates)
3. Which OData query capabilities are allowed (`FilterEnabled`, `SelectEnabled`, etc.)

`EntitySetProfile<TKey, TModel>` declares all three. At startup, the framework reads these declarations and registers minimal API endpoints - no controllers, no per-request reflection.

## Startup flow

```
AddOhData(builder => builder.AddProfile<T>())
  │
  └─► OhDataBuilder collects profile types, prefix, and defaults
        │
        └─► DI factory (keyed singleton) builds OhDataRegistration:
              ├─ Resolves each profile from DI (singletons)
              ├─ Calls IVisitModelBuilder.VisitModelBuilder() on each
              │    → builds ODataConventionModelBuilder → IEdmModel
              ├─ Validates for duplicate entity set names
              └─ Stores prefix + EdmModel + profiles in OhDataRegistration

app.MapOhData()
  │
  └─► OhDataEndpointFactory.MapAll(routes, registration)
        │
        ├─ MapGroup(prefix) → RouteGroupBuilder
        │    └─ endpoint filter: OData-Version header, $format, Accept validation
        ├─ GET ""          → service document (JSON)
        ├─ GET /$metadata  → CSDL XML from IEdmModel
        └─ per profile: MakeGenericMethod(KeyType, ModelType).Invoke(MapEntitySet<TKey,TModel>)
              │
              ├─ GET  /{name}               if HasGetQueryable or HasGetAll
              ├─ GET  /{name}/$count        if HasGetQueryable or HasGetAll
              ├─ GET  /{name}({key})        if HasGetById
              ├─ POST /{name}               if HasPost
              ├─ PUT  /{name}({key})        if HasPut
              ├─ PATCH/{name}({key})        if HasPatch
              ├─ DELETE/{name}({key})       if HasDelete
              ├─ GET  /{name}({key})/{nav}  per navigation route with handler
              ├─ GET  /{name}/{fn}          per collection-bound function
              └─ POST /{name}/{action}      per collection-bound action
                   (entity-bound ops follow the same pattern with key in the route)
```

## Type erasure via `IEntitySetEndpointSource`

Profiles are generic (`EntitySetProfile<Guid, Product>`) but the factory iterates `IEntitySetEndpointSource` - a non-generic internal interface. This lets the factory inspect `HasGetAll`, `HasGetById`, etc. and route requests without knowing `TKey`/`TModel` at compile time.

The generic types are reintroduced in exactly one place: `MapEntitySet<TKey, TModel>`, called via `MethodInfo.MakeGenericMethod(profile.KeyType, profile.ModelType).Invoke(...)`. This reflection runs once at startup, not per request.

## GET collection - handler priority

When both `GetQueryable` and `GetAll` are set, `GetQueryable` wins:

1. **`IODataEntitySetEndpointSource`** (from `OhData.AspNetCore`) - profile receives `ODataQueryOptions<TModel>` directly and applies options itself
2. **`GetQueryable`** - framework applies `$filter`/`$orderby`/`$skip`/`$top` via `ApplyTo(IQueryable)`, enabling EF Core SQL pushdown
3. **`GetAll`** - framework returns all items; no query options applied

## `$select` - JSON post-processing

When `$select` is active, the framework:
1. Materializes the full entity array
2. Serializes to `JsonNode`
3. Removes non-selected property nodes from each item

This is done instead of using `ISelectExpandWrapper.ToDictionary()` because OData's wrapper produces PascalCase keys while normal serialization produces camelCase - the `JsonNode` approach preserves the existing naming policy.

## Route templates and `MapGroup`

`MapGroup` inserts a `/` between the group prefix and any template that doesn't start with `/`. This breaks OData key syntax:

```
// Wrong: group("/odata/Products") + MapGet("({key})") → /odata/Products/({key})
// Right: group("/odata")          + MapGet("/Products({key})") → /odata/Products({key})
```

All entity-set routes with key syntax are mapped on the top-level `/prefix` group with the entity set name embedded in the template. Collection-level routes (GET/POST, `/$count`, bound operations) are mapped on a per-entity sub-group - this works because they don't have key syntax.

## `AdvancedConfigure` - full EDM control

Overriding `AdvancedConfigure(EntitySetConfiguration<TModel>)` bypasses all automatic EDM configuration (query capabilities, navigation properties, key setup). Detection is via `MethodInfo.DeclaringType` comparison at startup:

```csharp
protected override void AdvancedConfigure(EntitySetConfiguration<Product> config)
{
    config.EntityType.HasKey(x => x.Id);
    config.EntityType.Select().OrderBy().Filter();
    // full Microsoft.OData.ModelBuilder API
}
```

## Dependency structure

```
OhData.AspNetCore (net10.0)
  └─ Microsoft.OData.ModelBuilder
  └─ Microsoft.AspNetCore.App (framework reference)
  └─ Microsoft.AspNetCore.OData

OhData.Client (net10.0)
  └─ [no OhData server reference - standalone]
```

`OhData.AspNetCore` contains all core types (profiles, EDM builder interfaces, auth config, navigation and operation definitions) alongside the runtime (endpoint factory, DI registration, extension methods). The profile base classes have no ASP.NET Core dependency by design; authorization configuration is stored as plain `AuthorizationConfig` data and applied to the endpoint builder at startup.
