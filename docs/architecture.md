# Architecture

This document describes how OhData works under the hood. It's intended for contributors and for engineers who need to understand the framework's behaviour deeply - for example, when using `AdvancedConfigure` or building an extension on top of OhData.

## Core idea

An OData API is fully derivable from three things:

1. The entity type and its key type
2. Which CRUD operations are supported (handler delegates)
3. Which OData query capabilities are allowed (`FilterEnabled`, `SelectEnabled`, etc.)

`EntitySetProfile<TKey, TModel>` declares all three. At startup, the framework reads these declarations and registers minimal API endpoints - no controllers, no per-request reflection.

### Entity set naming

The OData entity set name (used in routes and `$metadata`) defaults to a pluralized form of the model type name, computed by a small hand-rolled pluralization algorithm (consonant+`y` → `ies`; `s`/`sh`/`ch`/`x`/`z` → `+es`; otherwise `+s`). It is **not** backed by a library like Humanizer and does not know irregular plurals - `Person` becomes `Persons` (not `People`), `Child` becomes `Childs` (not `Children`), etc.

If the default doesn't produce the name you want, override it explicitly in the profile constructor:

```csharp
public class PersonProfile : EntitySetProfile<int, Person>
{
    public PersonProfile() : base(x => x.Id)
    {
        EntitySetName = "People";   // escape hatch for irregular plurals
        // ...
    }
}
```

## Startup flow

```
AddOhData(builder => builder.AddProfile<T>())
  │
  └─► OhDataBuilder collects profile types, prefix, and defaults
        │
        └─► DI factory (keyed singleton) builds OhDataRegistration:
              ├─ Creates a temporary scope; resolves each profile from DI (scoped)
              ├─ Calls IVisitModelBuilder.VisitModelBuilder() on each
              │    → builds ODataConventionModelBuilder → IEdmModel
              ├─ Validates for duplicate entity set names
              └─ Stores prefix + EdmModel + profiles in OhDataRegistration

app.MapOhData()
  │
  └─► OhDataEndpointFactory.MapAll(routes, registration)
        │
        ├─ MapGroup(prefix) → RouteGroupBuilder
        │    └─ endpoint filter: OData-Version header, OData-MaxVersion (§8.2.7), $format, Accept validation
        ├─ GET ""          → service document (JSON)
        ├─ GET /$metadata  → CSDL XML from IEdmModel
        ├─ startup validation: throws InvalidOperationException if a structural property name
        │    collides with an entity-level bound function name (see property routes below), or
        │    if a navigation property's `post` handler collides with an entity-level bound
        │    action name (both POST /{name}({key})/{segment})
        └─ per profile: MakeGenericMethod(KeyType, ModelType).Invoke(MapEntitySet<TKey,TModel>)
              │
              ├─ GET    /{name}                        if HasGetQueryable or HasGetAll
              ├─ GET    /{name}/$count                 if HasGetQueryable or HasGetAll
              ├─ GET    /{name}({key})                  if HasGetById
              ├─ POST   /{name}                         if HasPost (deep insert / @odata.bind handling - see docs/deep-insert.md)
              ├─ PUT    /{name}({key})                  if HasPut
              ├─ PATCH  /{name}({key})                  if HasPatch
              ├─ DELETE /{name}({key})                  if HasDelete
              ├─ GET    /{name}({key})/{nav}            per navigation route with handler (batch or per-entity)
              ├─ GET    /{name}({key})/{nav}/$count     per collection-navigation route with handler
              ├─ GET/POST/PUT/DELETE /{name}({key})/{nav}/$ref   per navigation with addRef/setRef/removeRef
              ├─ POST   /{name}({key})/{nav}            per HasMany with a `post` child-create delegate
              ├─ GET    /{name}({key})/{prop}           per structural property if PropertyAccessEnabled and HasGetById
              ├─ GET    /{name}({key})/{prop}/$value    per structural property, same gate
              ├─ PUT/PATCH/DELETE /{name}({key})/{prop} per structural property if PropertyAccessEnabled and HasPatch
              ├─ GET    /{name}/{fn}                    per collection-bound function
              └─ POST   /{name}/{action}                per collection-bound action
                   (entity-bound ops follow the same pattern with key in the route)
```

See [docs/navigation-routing.md](navigation-routing.md) for `$ref`/POST-to-navigation, [docs/property-access.md](property-access.md) for the structural property routes, and [docs/deep-insert.md](deep-insert.md) for `AllowDeepInsert`/`@odata.bind` behavior on the `POST` route.

## Registering profiles

`AddProfile<TProfile>()` registers a single profile type explicitly - this is the form shown in the README quick start. For larger codebases, `OhDataBuilder` also supports assembly scanning so you don't have to list every profile by hand:

```csharp
builder.Services.AddOhData(o => o
    // Scan one or more assemblies for concrete EntitySetProfile<TKey,TModel> subclasses
    .AddProfilesFrom(s => s
        .InAssemblyOf<Program>()
        .In(typeof(ExternalProfile).Assembly))

    // Shorthand: scan just the assembly containing a given type
    .AddProfilesFromAssemblyOf<Program>()

    // Shorthand: scan one or more assemblies directly
    .AddProfilesFromAssembly(typeof(Program).Assembly));
```

All three forms discover every concrete (non-abstract) `EntitySetProfile<TKey,TModel>` subclass in the scanned assemblies and register each one exactly as if it had been passed to `AddProfile<TProfile>()` individually - same `AddScoped` lifetime, same cross-registration duplicate-type guard. A type already registered (via an earlier `AddProfile<T>()` or a previous scan) is skipped rather than registered twice. `AddProfilesFromAssemblyOf<T>()` is equivalent to `AddProfilesFrom(s => s.InAssemblyOf<T>())`, and `AddProfilesFromAssembly(...)` is equivalent to `AddProfilesFrom(s => s.In(assemblies))`. These can be mixed freely with explicit `AddProfile<T>()` calls in the same builder.

## Type erasure via `IEntitySetEndpointSource`

Profiles are generic (`EntitySetProfile<Guid, Product>`) but the factory iterates `IEntitySetEndpointSource` - a non-generic internal interface. This lets the factory inspect `HasGetAll`, `HasGetById`, etc. and route requests without knowing `TKey`/`TModel` at compile time.

The generic types are reintroduced in exactly one place: `MapEntitySet<TKey, TModel>`, called via `MethodInfo.MakeGenericMethod(profile.KeyType, profile.ModelType).Invoke(...)`. This reflection runs once at startup, not per request.

## Scoped profile resolution

Profiles are registered as **scoped** services, meaning each HTTP request gets a fresh profile instance resolved from the request's DI scope. This enables safe constructor injection of scoped dependencies like `DbContext`:

```csharp
public class ProductProfile(AppDbContext db) : EntitySetProfile<int, Product>
{
    // db is a per-request DbContext — safe to use in all handlers
}
```

At startup, `OhDataBuilder.Register()` creates a temporary scope to resolve profiles for EDM construction only. Route handlers capture two references:

- **Startup `source`** — used for structural queries: `HasGetById`, `MaxTop`, `NavigationRoutes` metadata, auth config, etc. These never change after startup.
- **Per-request `s = ResolveHandlers(ctx)`** — a fresh profile resolved from the request scope, used for all `Invoke*()` calls. This instance carries live scoped dependencies.

Compiled delegates that don't access scoped dependencies (ETag functions, key-to-string) are cached in static `ConcurrentDictionary<Type, ...>` keyed by the concrete profile type, so `Expression.Compile()` runs at most once per type across the application lifetime.

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
