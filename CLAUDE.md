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

OhData is a convention-based OData server framework that turns declarative profile classes into registered ASP.NET Core minimal API endpoints at startup - no controllers required.

### The core flow

```
EntitySetProfile<TKey, TModel>
    └─► IVisitModelBuilder       → builds the OData EDM model (Microsoft.OData.ModelBuilder)
    └─► IEntitySetEndpointSource → runtime-typed interface for OhDataEndpointFactory to call handlers

AddOhData(builder => builder.AddProfile<MyProfile>())
    └─► OhDataBuilder collects profile types + prefix
    └─► Profiles registered as AddScoped (not singleton) to support DbContext injection
    └─► OhDataRegistration (keyed singleton) built lazily:
          temporary scope resolves each profile → visits EDM, collects IEntitySetEndpointSource
    └─► Stored in DI as AddKeyedSingleton<OhDataRegistration>(name)

app.MapOhData()  →  returns RouteGroupBuilder
    └─► OhDataEndpointFactory.MapAll()
        ├─► routes.MapGroup(prefix)  ← outer group for the whole OData surface
        │      endpoint filters: OData-Version response header, OData-MaxVersion request-header
        │      validation (§8.2.7 - rejects < 4.0 with 400), $format/Accept negotiation
        ├─► GET  ""              → service document
        ├─► GET  /$metadata      → CSDL XML
        ├─► startup validation: throws InvalidOperationException if a structural property name
        │      collides with an entity-level bound function name, or if a navigation property's
        │      `post` handler collides with an entity-level bound action name (both POST
        │      /{EntitySet}({key})/{segment})
        └─► per profile (only routes whose handler delegate is non-null):
            GET    /{EntitySet}              (GetAll or GetQueryable)
            GET    /{EntitySet}/$count
            GET    /{EntitySet}({key})       (GetById)
            POST   /{EntitySet}              (Post - deep insert / @odata.bind handling, see AllowDeepInsert below)
            PUT    /{EntitySet}({key})       (Put)
            PATCH  /{EntitySet}({key})       (Patch)
            DELETE /{EntitySet}({key})       (Delete - returns Task<bool>; false→404 or 204, per IdempotentDelete)
            GET    /{EntitySet}({key})/{nav}          (navigation routes with handler, batch or per-entity)
            GET    /{EntitySet}({key})/{nav}/$count   (collection-navigation count)
            GET/POST/PUT/DELETE /{EntitySet}({key})/{nav}/$ref  (addRef/setRef/removeRef)
            POST   /{EntitySet}({key})/{nav}          (HasMany `post` - create a related entity)
            GET    /{EntitySet}({key})/{Property}          (structural property read - rides GetById, gated by PropertyAccessEnabled)
            GET    /{EntitySet}({key})/{Property}/$value   (raw property value, same gate)
            PUT/PATCH/DELETE /{EntitySet}({key})/{Property} (structural property write - rides Patch, gated by PropertyAccessEnabled)
            GET    /{EntitySet}/{FunctionName}  (bound functions, query-string params)
            POST   /{EntitySet}/{ActionName}    (bound actions, JSON body params)
            Each route gets .WithTags(EntitySetName) and .RequireAuthorization(...) if configured.
```

### Key design decisions

**Handler presence drives route registration.** If a profile sets `GetAll = null` (the default), no `GET /EntitySet` route is registered.

**Two paths for GET collection.**
- `GetQueryable` (IQueryable): framework constructs `ODataQueryOptions<TModel>` and applies `$filter`/`$orderby`/`$skip`/`$top` via `ApplyTo(IQueryable)`, enabling EF Core SQL pushdown. `$select` is applied via JsonNode post-processing to keep camelCase consistent.
- `GetAll` (IEnumerable): simple enumeration, no query options applied - developer chose the opt-in simple path.
- `IODataEntitySetEndpointSource` (Priority 1): profile receives `ODataQueryOptions` directly and applies them itself.

**`$select` uses JsonNode post-processing** (not `ISelectExpandWrapper`) to avoid the PascalCase/camelCase inconsistency that `ApplyTo` with `$select` introduces.

**`ODataException` from invalid query options returns 400.** All collection GET handlers wrap `ODataQueryOptions` construction in try/catch to return an OData error body instead of a 500.

**`Delete` returns `Task<bool>`.** `false` → 404 OData error **when `IdempotentDelete = false`**; the framework default is `IdempotentDelete = true`, under which `false` → 204 No Content instead. No `KeyNotFoundException` idiom.

**ETags.** Set `GetETag = model => "..."` to opt in. Adds `ETag` response header to GET/POST/PUT/PATCH. Checks `If-Match` header on PUT/PATCH/DELETE; returns 412 on mismatch.

**Named registrations.** `AddOhData("v1", ...)` / `MapOhData("v1")` uses `AddKeyedSingleton<OhDataRegistration>("v1")`. Unnamed `AddOhData()` uses the `__default__` key. Multiple registrations coexist.

**Bound functions and actions.** `BindFunction(delegate)` / `BindAction(delegate)` in the profile constructor registers HTTP routes at startup and registers the operation in the EDM. Functions: `GET /{EntitySet}/{Name}?param=value`. Actions: `POST /{EntitySet}/{Name}` with JSON body `{ "paramName": value }`. `CancellationToken` parameters are detected and passed automatically.

**Type erasure via `IEntitySetEndpointSource`.** Profiles are generic (`EntitySetProfile<TKey, TModel>`) but the factory iterates them as `IEntitySetEndpointSource` (non-generic, internal). The factory re-introduces the generic types via `MakeGenericMethod(KeyType, ModelType)` once per entity set at startup - not per-request.

**Profiles are scoped; two sources per handler.** Each route handler closure captures two `IEntitySetEndpointSource` references: the startup `source` for structural queries (`HasGetById`, `MaxTop`, auth config, nav route metadata) and a per-request `s = ResolveHandlers(ctx)` resolved from `ctx.RequestServices` for all `Invoke*()` calls. This allows profiles to safely inject scoped dependencies (e.g. `DbContext`) in their constructor. Compiled delegates that don't capture scoped state (ETag, key-to-string) are cached in `static ConcurrentDictionary<Type, ...>` so `Expression.Compile()` runs at most once per type.

**`MapGroup` slash insertion - critical routing rule.** `MapGroup` inserts a `/` between the group prefix and any route template that doesn't start with `/`. This breaks OData key syntax (`Widgets({key})` vs `Widgets/({key})`). All entity-set routes are therefore mapped on the top-level `/prefix` group with the entity set name embedded in the template (e.g. `"/Widgets({key})"`) rather than on a per-entity sub-group. If you add new routes, follow this pattern.

**Authorization is per-profile, all-operations.** `RequireAuthorization()` / `RequireRoles()` on a profile applies the same auth requirement to every route for that entity set. No per-operation granularity.

**`AdvancedConfigure` eject hatch.** Overriding `AdvancedConfigure(EntitySetConfiguration<TModel>)` gives full EDM control and disables automatic EDM config. Detected at startup via `MethodInfo.DeclaringType` comparison.

**Profile types have no ASP.NET Core dependency.** Auth config is stored as plain `AuthorizationConfig` data; the factory applies `RequireAuthorization`. Keep it this way.

**Property routes ride `GetById`/`Patch`; no new handler delegates.** `PropertyAccessEnabled` (profile-level `bool?`, inherits `EntitySetDefaults.PropertyAccessEnabled`, **default `true`**) gates `GET /{EntitySet}({key})/{Property}` and `/$value` (requires `GetById`) and `PUT`/`PATCH`/`DELETE /{EntitySet}({key})/{Property}` (requires `Patch`, built as a one-property `Delta<TModel>`). Structural properties are computed once at startup as every public readable CLR property minus every navigation property name, so property and navigation routes never collide by construction. The one remaining collision risk - an entity-level bound function sharing a name with a structural property - is caught by a startup validation pass in `app.MapOhData()` (`InvalidOperationException`), since two routes can't otherwise register the same `(template, method)` pair.

**`AllowDeepInsert` controls what `Post` receives, not a new route.** Profile-level `bool?` (inherits `EntitySetDefaults.AllowDeepInsert`, **default `false`**), entity-level granularity only - no per-navigation opt-in. Default: nested navigation-property values (`HasMany`/`HasOptional`/`HasRequired`) that System.Text.Json already bound during deserialization are stripped (nulled) before `Post` runs. Opt-in (`true`): the full deserialized graph is passed through; the handler owns atomic persistence (the framework does not open a transaction). `@odata.bind` anywhere in a POST body is detected and rejected with `501 Not Implemented` regardless of `AllowDeepInsert` - it is not implemented at all.

**Batch-aware `$expand` via an additive `BatchHandler`.** `HasMany`/`HasOptional`/`HasRequired` accept an optional `batchGetAll`/`batchGet` delegate (`IReadOnlyList<TKey> → ILookup<TKey,TNavigation>` or `IReadOnlyDictionary<TKey,TNavigation?>`) alongside the existing per-entity `getAll`/`get`. When present, `$expand` calls it once per expanded property per page instead of once per parent entity (collapsing N×P sequential calls to P). A per-entity `Handler` is auto-derived from the batch delegate by calling it with a single-key list, so the standalone nav-GET route, `$count`, and `$ref` keep working without a second handler. Falls back byte-identically to the per-entity path when no batch handler is registered.

**POST/PUT/PATCH deserialize the request body by hand.** All three read and JSON-deserialize the body manually (rather than an ASP.NET Core minimal-API bound parameter) so malformed JSON, non-object bodies, and non-JSON `Content-Type` values return the OData error envelope (400/415) instead of ASP.NET Core's implicit binder short-circuiting with an empty body. This is also why PATCH's non-object-body case (JSON array/string/number/bool/null) is caught explicitly - `JsonElement.EnumerateObject()` throws `InvalidOperationException` for non-`Object` `ValueKind`, which is now caught and mapped to 400.

### Project layout

| Project | Target | Role |
|---|---|---|
| `OhData.AspNetCore` | net10.0 | All core and runtime types: `EntitySetProfile<TKey,TModel>`, `IEntitySetEndpointSource` (internal), `IVisitModelBuilder` (internal), `AuthorizationConfig`, `NavigationRouteDefinition`, `BoundOperationDefinition`, `OhDataBuilder`, `OhDataEndpointFactory`, `OhDataRegistration`, `OhDataRegistrationCollection`, `OhDataDefaults`, `AddOhDataVersion` / `MapOhDataVersion` versioning helpers, `ODataEntitySetProfile<TKey,TModel>`, `IODataEntitySetEndpointSource`, extension methods |
| `OhData.Client` | net10.0 | Typed .NET OData 4.0 client with fluent LINQ-based filter/select/expand translation |
| `OhData.TestBench.AspNetCore` | net10.0 | Runnable demo app with EF Core InMemory, Swagger UI + Scalar, versioned v1/v2 registrations |
| `OhData.ClientTestBench.AspNetCore` | net10.0 | Runnable demo app used as server target for client integration tests |
| `OhData.AspNetCore.Tests` | net10.0 | xUnit integration tests using `WebApplicationFactory` via `TestHostBuilder` |
| `OhData.Client.Tests` | net10.0 | xUnit tests for OhData.Client |
| `OhData.Client.Benchmarks` | net10.0 | BenchmarkDotNet project for client library performance |
| `OhData.Server.Benchmarks` | net10.0 | BenchmarkDotNet project comparing OhData's minimal-API pipeline against `Microsoft.AspNetCore.OData`'s `ODataController`+`[EnableQuery]` pipeline; report in `docs/server-comparison-report.md` |
| `OhData.MicrosoftODataClient.Tests` | net10.0 | Compatibility tests against Microsoft.OData.Client |

### `InternalsVisibleTo`

`OhData.AspNetCore/AssemblyInfo.cs` grants `InternalsVisibleTo` to `OhData.AspNetCore.Tests`, enabling access to the internal `IEntitySetEndpointSource` and `IVisitModelBuilder` interfaces.
