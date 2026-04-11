# Architecture

## Core idea

OhData takes the position that an OData API is fully derivable from three things:
1. The entity type and its key type
2. Which CRUD operations are supported
3. Which OData query capabilities are allowed

A `EntitySetProfile<TKey, TModel>` subclass declares all three. At startup, the framework reads these declarations and registers minimal API endpoints — no controllers, no runtime reflection on request.

## Startup flow

```
AddOhData(builder => builder.AddProfile<T>())
  │
  └─► OhDataBuilder collects profile types, stores prefix + name
        │
        └─► .Register() adds a keyed singleton factory to DI:
              │
              ├─ Resolves each profile from DI (singletons)
              ├─ Calls IVisitModelBuilder.VisitModelBuilder() on each
              │    → builds ODataConventionModelBuilder → IEdmModel
              ├─ Casts each profile to IEntitySetEndpointSource
              ├─ Validates for duplicate entity set names
              └─ Stores prefix + EdmModel + sources in OhDataRegistration

app.MapOhData()
  │
  └─► OhDataEndpointFactory.MapAll(routes, registration)
        │
        ├─ routes.MapGroup(prefix) → RouteGroupBuilder
        ├─ GET ""          → service document
        ├─ GET /$metadata  → CSDL XML from IEdmModel
        └─ per profile: MakeGenericMethod(KeyType, ModelType).Invoke(MapEntitySet)
              │
              ├─ GET  /{name}               if HasGetQueryable or HasGetAll
              ├─ GET  /{name}/$count        if HasGetQueryable or HasGetAll
              ├─ GET  /{name}({key})        if HasGetById
              ├─ POST /{name}               if HasPost
              ├─ PUT  /{name}({key})        if HasPutById
              ├─ PATCH/{name}({key})        if HasPatch
              ├─ DELETE/{name}({key})       if HasDelete
              ├─ GET  /{name}({key})/{nav}  per NavigationRoute
              ├─ GET  /{name}/{fn}          per BoundFunction
              └─ POST /{name}/{action}      per BoundAction
                   └─ each gets .WithTags(name) + .ApplyAuth(profile.Authorization)
```

## Type erasure via `IEntitySetEndpointSource`

Profiles are generic (`EntitySetProfile<Guid, Product>`) but the factory iterates `IEntitySetEndpointSource` — a non-generic internal interface. This lets the factory inspect `HasGetAll`, `HasGetById`, etc. and invoke handlers without knowing `TKey`/`TModel` at compile time.

The generic types are reintroduced in exactly one place: `MapEntitySet<TKey, TModel>`, called via `MethodInfo.MakeGenericMethod(profile.KeyType, profile.ModelType).Invoke(...)`. This is the only reflection in the hot path — it runs once at startup, not per-request.

## GET collection — three handler paths

**Priority 1: `IODataEntitySetEndpointSource`** (from `OhData.Abstractions.AspNetCore.OData`)
- Profile implements `IODataEntitySetEndpointSource` and has `HasGetODataQueryable = true`
- Factory constructs `ODataQueryOptions<TModel>`, passes directly to the handler
- Handler applies options and returns an `IQueryable<object>`
- Factory materializes and applies `$select` post-processing

**Priority 2: `GetQueryable`** (base `EntitySetProfile`)
- Profile provides `Func<CancellationToken, Task<IQueryable<TModel>>>`
- Factory constructs `ODataQueryOptions<TModel>` from the request and EDM model
- Applies filter/orderby/skip/top individually via `ApplyTo(IQueryable)` (EF Core pushdown)
- `$select` applied via JsonNode post-processing (avoids `ISelectExpandWrapper` casing issues)
- `$count=true` → count applied before skip/top, embedded in envelope as `@odata.count`

**Priority 3: `GetAll`** (IEnumerable, simple path)
- No query options applied — developer chose the simple path
- `$select` applied via JsonNode post-processing

## `$select` implementation (JsonNode post-processing)

When `$select` is active, the framework:
1. Materializes the result as `TModel[]` (for GetQueryable) or as-is (for GetAll)
2. Serializes to `JsonNode`
3. Removes non-selected property nodes from each item
4. Returns the stripped array

This approach is used instead of `ISelectExpandWrapper.ToDictionary()` to avoid a PascalCase/camelCase inconsistency: `ApplyTo` with `$select` produces wrapper objects with EDM PascalCase keys, while normal serialization produces camelCase. JsonNode post-processing preserves the existing naming policy.

## Route template gotcha: `MapGroup` slash insertion

ASP.NET Core's `MapGroup` inserts a `/` separator between the group prefix and a route template that doesn't start with `/`. This breaks OData key syntax:

```
// Wrong: MapGroup("/odata/Widgets") + MapGet("({key})") → /odata/Widgets/({key})
// Right: MapGroup("/odata")         + MapGet("/Widgets({key})") → /odata/Widgets({key})
```

All entity-set routes are therefore mapped directly on the top-level `MapGroup("/prefix")` group with the entity set name baked into each template (e.g. `"/Widgets({key})"`, not `"({key})"` on a sub-group).

Collection-level routes (GET "", POST, bound functions, bound actions, `/$count`) are mapped on a per-entity sub-group using `MapGroup($"/{name}")` — this works because they don't have key syntax.

## Named registrations

`OhDataRegistration` is stored as `AddKeyedSingleton<OhDataRegistration>(name)` so multiple calls to `AddOhData("v1", ...)` / `AddOhData("v2", ...)` coexist without overwriting each other. The default (unnamed) registration uses the key `"__default__"` and is also exposed as an unkeyed singleton for backwards compatibility.

`OhDataRegistrationCollection` is a singleton dictionary that maps names to registrations for introspection.

## EDM model building

`IVisitModelBuilder` (internal) is the visitor interface. `EntitySetProfile` implements it to register entity sets, query capabilities, nav properties, and bound functions/actions on `ODataConventionModelBuilder`. The resulting `IEdmModel` is stored in `OhDataRegistration` and served at `$metadata`.

For bound functions, the framework uses reflection to call the generic `FunctionConfiguration.Returns<T>()` / `FunctionConfiguration.ReturnsCollection<T>()` methods with the runtime return type derived from the delegate.

If `AdvancedConfigure(EntitySetConfiguration<TModel>)` is overridden in a subclass, all automatic configuration is bypassed (detected via `MethodInfo.DeclaringType` comparison at startup, not per-request).

## Authorization

`EntitySetProfile` stores an `AuthorizationConfig` (policy, roles, or bare require-auth) set by `RequireAuthorization()` / `RequireRoles()`. At endpoint registration time, `OhDataEndpointFactory` calls `.RequireAuthorization(...)` on each `RouteHandlerBuilder` individually — all operations on the entity set get the same auth requirement. Auth requirements are stored as plain data in `OhData.Abstractions` (no ASP.NET Core reference); the factory in `OhData.AspNetCore` performs the actual `RequireAuthorization` call.

## ETags

`EntitySetProfile` stores `Func<TModel, string>? GetETag`. At endpoint registration, the factory:
- Adds `ETag` response header to GET/POST/PUT/PATCH responses
- On PUT/PATCH/DELETE, reads `If-Match` header, fetches current entity via `GetById`, compares ETags, returns 412 if mismatched

## Dependency structure

```
OhData.Abstractions (net8.0)
  └─ Microsoft.OData.ModelBuilder [2.*, 3)
  └─ [no ASP.NET Core reference]

OhData.AspNetCore (net8.0)
  └─ OhData.Abstractions
  └─ OhData.Abstractions.AspNetCore.OData
  └─ Microsoft.AspNetCore.App (framework reference)
  └─ Microsoft.AspNetCore.OData [9.4.*, 10)

OhData.AspNetCore.Versioning (net8.0)
  └─ OhData.AspNetCore

OhData.Abstractions.AspNetCore.OData (net8.0)
  └─ OhData.Abstractions
  └─ Microsoft.AspNetCore.OData [9.4.*, 10)
```
