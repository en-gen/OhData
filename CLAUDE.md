# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test

All commands run from the repo root.

```bash
# Build everything
dotnet build src/OhData.sln

# Run all tests
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj

# Run a single test by name
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "FullyQualifiedName~GetAll_Returns200"

# Run a single test class
dotnet test src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj --filter "ClassName~EndpointMappingTests"

# Run the test bench (interactive demo, browse to http://localhost:5099/scalar)
dotnet run --project src/OhData.TestBench.AspNetCore
```

## Architecture

OhData is a convention-based OData server framework that turns declarative profile classes into registered ASP.NET Core minimal API endpoints at startup — no controllers required.

### The core flow

```
EntitySetProfile<TKey, TModel>
    └─► IVisitModelBuilder       → builds the OData EDM model (Microsoft.OData.ModelBuilder)
    └─► IEntitySetEndpointSource → runtime-typed interface for OhDataEndpointFactory to call handlers

AddOhData(builder => builder.AddProfile<MyProfile>())
    └─► OhDataBuilder collects profile types + prefix
    └─► OhDataRegistration (keyed singleton) built lazily:
          resolves each profile, visits the EDM, collects IEntitySetEndpointSource instances
    └─► Stored in DI as AddKeyedSingleton<OhDataRegistration>(name)

app.MapOhData()  →  returns RouteGroupBuilder
    └─► OhDataEndpointFactory.MapAll()
        ├─► routes.MapGroup(prefix)  ← outer group for the whole OData surface
        ├─► GET  ""              → service document
        ├─► GET  /$metadata      → CSDL XML
        └─► per profile (only routes whose handler delegate is non-null):
            GET    /{EntitySet}              (GetAll or GetQueryable)
            GET    /{EntitySet}/$count
            GET    /{EntitySet}({key})       (GetById)
            POST   /{EntitySet}              (Post)
            PUT    /{EntitySet}({key})       (Put)
            PATCH  /{EntitySet}({key})       (Patch)
            DELETE /{EntitySet}({key})       (Delete — returns Task<bool>; false→404)
            GET    /{EntitySet}({key})/{nav} (navigation routes with handler)
            GET    /{EntitySet}/{FunctionName}  (bound functions, query-string params)
            POST   /{EntitySet}/{ActionName}    (bound actions, JSON body params)
            Each route gets .WithTags(EntitySetName) and .RequireAuthorization(...) if configured.
```

### Key design decisions

**Handler presence drives route registration.** If a profile sets `GetAll = null` (the default), no `GET /EntitySet` route is registered.

**Two paths for GET collection.**
- `GetQueryable` (IQueryable): framework constructs `ODataQueryOptions<TModel>` and applies `$filter`/`$orderby`/`$skip`/`$top` via `ApplyTo(IQueryable)`, enabling EF Core SQL pushdown. `$select` is applied via JsonNode post-processing to keep camelCase consistent.
- `GetAll` (IEnumerable): simple enumeration, no query options applied — developer chose the opt-in simple path.
- `IODataEntitySetEndpointSource` (Priority 1): profile receives `ODataQueryOptions` directly and applies them itself.

**`$select` uses JsonNode post-processing** (not `ISelectExpandWrapper`) to avoid the PascalCase/camelCase inconsistency that `ApplyTo` with `$select` introduces.

**`ODataException` from invalid query options returns 400.** All collection GET handlers wrap `ODataQueryOptions` construction in try/catch to return an OData error body instead of a 500.

**`Delete` returns `Task<bool>`.** `false` → 404 OData error. No `KeyNotFoundException` idiom.

**ETags.** Set `GetETag = model => "..."` to opt in. Adds `ETag` response header to GET/POST/PUT/PATCH. Checks `If-Match` header on PUT/PATCH/DELETE; returns 412 on mismatch.

**Named registrations.** `AddOhData("v1", ...)` / `MapOhData("v1")` uses `AddKeyedSingleton<OhDataRegistration>("v1")`. Unnamed `AddOhData()` uses the `__default__` key. Multiple registrations coexist.

**Bound functions and actions.** `BindFunction(delegate)` / `BindAction(delegate)` in the profile constructor registers HTTP routes at startup and registers the operation in the EDM. Functions: `GET /{EntitySet}/{Name}?param=value`. Actions: `POST /{EntitySet}/{Name}` with JSON body `{ "paramName": value }`. `CancellationToken` parameters are detected and passed automatically.

**Type erasure via `IEntitySetEndpointSource`.** Profiles are generic (`EntitySetProfile<TKey, TModel>`) but the factory iterates them as `IEntitySetEndpointSource` (non-generic, internal). The factory re-introduces the generic types via `MakeGenericMethod(KeyType, ModelType)` once per entity set at startup — not per-request.

**`MapGroup` slash insertion — critical routing rule.** `MapGroup` inserts a `/` between the group prefix and any route template that doesn't start with `/`. This breaks OData key syntax (`Widgets({key})` vs `Widgets/({key})`). All entity-set routes are therefore mapped on the top-level `/prefix` group with the entity set name embedded in the template (e.g. `"/Widgets({key})"`) rather than on a per-entity sub-group. If you add new routes, follow this pattern.

**Authorization is per-profile, all-operations.** `RequireAuthorization()` / `RequireRoles()` on a profile applies the same auth requirement to every route for that entity set. No per-operation granularity.

**`AdvancedConfigure` eject hatch.** Overriding `AdvancedConfigure(EntitySetConfiguration<TModel>)` gives full EDM control and disables automatic EDM config. Detected at startup via `MethodInfo.DeclaringType` comparison.

**`OhData.Abstractions` has no ASP.NET Core dependency.** Auth config is stored as plain `AuthorizationConfig` data; the factory applies `RequireAuthorization`. Keep it this way.

### Project layout

| Project | Target | Role |
|---|---|---|
| `OhData.Abstractions` | net8.0 | Core types: `EntitySetProfile<TKey,TModel>`, `IEntitySetEndpointSource` (internal), `IVisitModelBuilder` (internal), `AuthorizationConfig`, `NavigationRouteDefinition`, `BoundOperationDefinition` |
| `OhData.AspNetCore` | net8.0 | Runtime: `OhDataBuilder`, `OhDataEndpointFactory`, `OhDataRegistration`, `OhDataRegistrationCollection`, `OhDataDefaults`, extension methods |
| `OhData.AspNetCore.Versioning` | net8.0 | `AddOhDataVersion` / `MapOhDataVersion` convenience wrappers |
| `OhData.Abstractions.AspNetCore.OData` | net8.0 | `ODataEntitySetProfile<TKey,TModel>` — extends base with `ODataQueryOptions<T>` handler signatures; `IODataEntitySetEndpointSource` bridge interface |
| `OhData.TestBench.AspNetCore` | net8.0 | Runnable demo app with EF Core InMemory, Swagger UI + Scalar, versioned v1/v2 registrations |
| `OhData.AspNetCore.Tests` | net8.0 | xUnit integration tests using `WebApplicationFactory` via `TestHostBuilder` |

### `InternalsVisibleTo`

`OhData.Abstractions/AssemblyInfo.cs` grants `InternalsVisibleTo` to `OhData.AspNetCore` and `OhData.AspNetCore.Tests`, enabling access to the internal `IEntitySetEndpointSource` and `IVisitModelBuilder` interfaces.
