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

# Run the test bench (manual HTTP smoke test)
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
    └─► OhDataRegistration (singleton) built lazily:
          resolves each profile, visits the EDM, collects IEntitySetEndpointSource instances
    └─► Stored in DI

app.MapOhData()  →  returns RouteGroupBuilder
    └─► OhDataEndpointFactory.MapAll()
        ├─► routes.MapGroup(prefix)  ← outer group for the whole OData surface
        ├─► GET  ""              → service document
        ├─► GET  /$metadata      → CSDL XML
        └─► per profile (only routes whose handler delegate is non-null):
            GET    /{EntitySet}
            GET    /{EntitySet}({key})
            POST   /{EntitySet}
            PUT    /{EntitySet}({key})
            PATCH  /{EntitySet}({key})
            DELETE /{EntitySet}({key})
            Each route gets .WithTags(EntitySetName) and .RequireAuthorization(...) if configured.
```

### Key design decisions

**Handler presence drives route registration.** If a profile sets `GetAll = null` (the default), no `GET /EntitySet` route is registered.

**Type erasure via `IEntitySetEndpointSource`.** Profiles are generic (`EntitySetProfile<TKey, TModel>`) but the factory iterates them as `IEntitySetEndpointSource` (non-generic, internal). The factory re-introduces the generic types via `MakeGenericMethod(KeyType, ModelType)` once per entity set at startup — not per-request.

**`MapGroup` slash insertion — critical routing rule.** `MapGroup` inserts a `/` between the group prefix and any route template that doesn't start with `/`. This breaks OData key syntax (`Widgets({key})` vs `Widgets/({key})`). All entity-set routes are therefore mapped on the top-level `/prefix` group with the entity set name embedded in the template (e.g. `"/Widgets({key})"`) rather than on a per-entity sub-group. If you add new routes, follow this pattern.

**Authorization is per-profile, all-operations.** `RequireAuthorization()` / `RequireRoles()` on a profile applies the same auth requirement to every route for that entity set via `.RequireAuthorization(...)` on each `RouteHandlerBuilder`. No per-operation granularity.

**`AdvancedConfigure` eject hatch.** Overriding `AdvancedConfigure(EntitySetConfiguration<TModel>)` gives full EDM control and disables automatic EDM config. Detected at startup via `MethodInfo.DeclaringType` comparison.

**`OhData.Abstractions` has no ASP.NET Core dependency.** Auth config is stored as plain `AuthorizationConfig` data; the factory applies `RequireAuthorization`. Keep it this way.

### Project layout

| Project | Target | Role |
|---|---|---|
| `OhData.Abstractions` | netstandard2.1 | Core types: `EntitySetProfile<TKey,TModel>`, `IEntitySetEndpointSource` (internal), `IVisitModelBuilder` (internal), `AuthorizationConfig`, options/context |
| `OhData.AspNetCore` | net8.0 | Runtime: `OhDataBuilder`, `OhDataEndpointFactory`, `OhDataRegistration`, extension methods |
| `OhData.Abstractions.AspNetCore.OData` | net6/5/3.1 | `ODataEntitySetProfile<TKey,TModel>` — extends base with `ODataQueryOptions<T>` handler signatures (not yet wired into endpoint factory; awaiting OData 9.4+ upgrade) |
| `OhData.TestBench.AspNetCore` | net8.0 | Runnable demo app with `Parent`/`Child` profiles |
| `OhData.Tests` | net8.0 | xUnit integration tests using `WebApplicationFactory` via `TestHostBuilder` |

### `InternalsVisibleTo`

`OhData.Abstractions/AssemblyInfo.cs` grants `InternalsVisibleTo` to `OhData.AspNetCore` and `OhData.Tests`, enabling access to the internal `IEntitySetEndpointSource` and `IVisitModelBuilder` interfaces.

### OData query options status

`Microsoft.AspNetCore.OData` 8.x (current pin) does not support injecting `ODataQueryOptions<T>` into minimal API handlers. The `OhData.Abstractions.AspNetCore.OData` project anticipates this but the factory doesn't use it yet. Upgrading to 9.4.0+ would unlock proper query parsing for minimal APIs.
