# Core Architecture and Registration Model

Read this when you need the route-registration flow, handler-to-route rules, profile scoping, type erasure, or named registrations.

## Architecture

OhData is a convention-based OData server framework that turns declarative profile classes into registered ASP.NET Core minimal API endpoints at startup - no controllers required.

### The core flow

```
EntitySetProfile<TKey, TModel>
    └─► IVisitModelBuilder       → builds the OData EDM model (Microsoft.OData.ModelBuilder)
    └─► IEntitySetEndpointSource → runtime-typed interface for OhDataEndpointFactory to call handlers

AddOhData(builder => builder.AddEntitySetProfile<MyProfile>())
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
        │      collides with an entity-level bound function name, if a navigation property's
        │      `post` handler collides with an entity-level bound action name (both POST
        │      /{EntitySet}({key})/{segment}), if a navigation ROUTE's name collides with an
        │      entity-level bound function name (#416/#492, both GET
        │      /{EntitySet}({key})/{segment}), if an unbound function/action name collides with
        │      another unbound operation or with an entity set's own collection GET/POST route,
        │      or if a BindEntityFunction/BindEntityAction handler's first parameter isn't the
        │      entity key (TKey), or (#313, opted-in registrations only) if an entity-level bound
        │      function name collides with a PAGEABLE navigation's continuation route, or (#465)
        │      if a Priority-1 profile (GetODataQueryable) also sets a Search handler, or (#468)
        │      if EdmValidator.Validate rejects the built EDM as invalid CSDL, or (#486) if
        │      .RequireResource() covers a KEY-BASED route the profile registers (Read/Update/
        │      Delete, the navigation-POST create route, or an entity-bound operation) while
        │      GetById is null. Every one of these compares OrdinalIgnoreCase (#492).
        │      Earlier still, at BIND time (the profile constructor / AddFunction / AddAction):
        │      duplicate bound-operation names within one kind and binding level (#492), and the
        │      three operation-signature rules of #498 - a void-returning FUNCTION, a non-trailing
        │      or nullable CancellationToken, and an IResult return type - and (#487) a
        │      .RequireResource() on an UNBOUND operation's authorize lambda, which has no {key}
        │      entity to evaluate against.
        └─► per profile (only routes whose handler delegate is non-null):
            GET    /{EntitySet}              (GetAll or GetQueryable)
            GET    /{EntitySet}/$count
            GET    /{EntitySet}({key})       (GetById)
            POST   /{EntitySet}              (Post - deep insert / @odata.bind handling, see AllowDeepWrites below)
            PUT    /{EntitySet}({key})       (Put - nested navs stripped unless AllowDeepWrites)
            PATCH  /{EntitySet}({key})       (Patch - nested navs never enter the Delta unless AllowDeepWrites)
            DELETE /{EntitySet}({key})       (Delete - returns Task<bool>; false→404 or 204, per IdempotentDelete)
            GET    /{EntitySet}({key})/{nav}          (navigation routes with handler, batch or per-entity)
            GET    /{EntitySet}({key})/{nav}?$skip=N  (#313: bare-$expand continuation, delegate-LESS navs only,
                                                       registered only when ExpandPagingEnabled && MaxExpandTop is set)
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
- `GetQueryable` (IQueryable): framework constructs `ODataQueryOptions<TModel>` and applies `$filter`/`$orderby`/`$skip`/`$top` via `ApplyTo(IQueryable)`, enabling EF Core SQL pushdown. `$select` is applied via JsonNode post-processing to keep camelCase consistent. **Its delegate is `Func<IQueryable<TModel>>` — alone among the eight it carries no `OhDataResult`, no `Task` and no `CancellationToken` (#653).** It is the only handler return the framework further COMPOSES before executing, so at the moment it returns there is no result to report on, no I/O has happened for a `Task` to represent, and nothing has started that a token could cancel — the framework owns the execution, and cancellation with it. Each removal is measured, not argued: of the assignments in this repo, **0 of 164 were `async`**, **0 returned a rejection**, and **0 of 263 referenced the token they were handed** (158 discarded it as `_`, 105 named it `ct` and never used it). Rejecting a collection read goes through `ConfigureExceptions`. `GetODataQueryable` (Priority-1) keeps `Task<ODataQueryResult<TModel>>`, correctly — there the profile HAS applied the options and may have executed a count, so it holds a result plus paging metadata.
- `GetAll` (IEnumerable): simple enumeration - developer chose the opt-in simple path. It is **not** "no query options applied": `$top`/`$skip` are applied in-memory as `Skip()`/`Take()` after materialization, and `$select`/`$expand` in the JSON pass. But `$filter` and `$orderby` are **rejected with `501` (`UnsupportedQueryOption`)** - *"This resource does not support $filter or $orderby. Configure GetQueryable to enable server-side query processing."* - not silently ignored. `501` and not `400` because the refusal is flag-INDEPENDENT: this path has no `IQueryable`, `FilterEnabled = true` changes nothing here, and the remedy is a different handler - §9.3.1's *"functionality not implemented"*. Same for the `/$count` route's GetAll-backed `$filter` fallback. Pinned by `OpenTypeLimitationTests.OpenTypeDynamicKeyReadPathTests`, which asserts it for a declared property as well as a dynamic key, since the rejection is about the path having no `IQueryable`, not about what is being filtered.
- `IODataEntitySetEndpointSource` (Priority 1): profile receives `ODataQueryOptions` directly and applies them itself.

**Named registrations.** `AddOhData("v1", ...)` / `MapOhData("v1")` uses `AddKeyedSingleton<OhDataRegistration>("v1")`. Unnamed `AddOhData()` uses the `__default__` key. Multiple registrations coexist.

**Type erasure via `IEntitySetEndpointSource`.** Profiles are generic (`EntitySetProfile<TKey, TModel>`) but the factory iterates them as `IEntitySetEndpointSource` (non-generic, internal). The factory re-introduces the generic types via `MakeGenericMethod(KeyType, ModelType)` once per entity set at startup - not per-request.

**Profiles are scoped; two sources per handler.** Each route handler closure captures two `IEntitySetEndpointSource` references: the startup `source` for structural queries (`HasGetById`, `MaxTop`, auth config, nav route metadata) and a per-request `s = ResolveHandlers(ctx)` resolved from `ctx.RequestServices` for all `Invoke*()` calls. This allows profiles to safely inject scoped dependencies (e.g. `DbContext`) in their constructor. Compiled delegates that don't capture scoped state (ETag, key-to-string, key-to-URL) are cached in `static ConcurrentDictionary<Type, ...>` so `Expression.Compile()` runs at most once per type.

**"Don't capture scoped state" was an ASSUMPTION ABOUT USER CODE, and the cache enforced nothing (#483).** The three caches are keyed by `GetType()` and store a delegate compiled from the **first-constructed** instance's expressions — which is the startup-scope instance, whose scope `OhDataBuilder` disposes as soon as the registration is built. `UseETag`'s comment declared the delegate safe to share because it "accesses model properties only (no DI dependencies)"; nothing checked, and `TryExtractDirectMemberNames` merely disables `$select` pushdown for a non-member selector rather than rejecting one. So `UseETag(x => _scopedDep.Stamp(x.Version))` compiled fine and then ran the **disposed** startup dependency on every request for the process lifetime — and for a *non-disposable* dependency, silently reused another scope's instance with no signal at all. The framework invites exactly that constructor shape: profiles are `AddScoped` **specifically** so they can inject scoped services, `DbContext` being the documented motivating case. The fix is that the caches stop pretending: `CapturedState.IsCapturedByExpression` walks the selector and a capturing one is **compiled per instance** instead, while a selector reading only its lambda parameter is cached exactly as before. **"Per instance" is per REQUEST** — `UseETag` runs in the profile constructor and profiles are `AddScoped`, so losing the cache pays an `Expression.Compile()` on every request that reaches the route, which is what the adjacent `s_keyToStringCache` comment has always said (*"caching avoids per-request compilation under scoped resolution"*). **Measured** end-to-end on `develop` @ `7211a6f` (TestServer, 200 requests each after warm-up, two runs) on `GET /Set(key)`: **0.63–0.69 ms/req** capturing against **0.20–0.26 ms/req** non-capturing — roughly **2.5–3.5x**, about **+0.4 ms per request**. An earlier "~100 µs per construction" figure here was wrong on both counts (per-request, not per-construction; ~4x the quoted magnitude) and is withdrawn (#548). Correctness is unaffected either way — measured by reference identity in `Issue483CapturedSelectorLifetimeTests`, `Assert.Same` for the plain profile and `Assert.NotSame` for the capturing one. Nothing is rejected, so no working configuration breaks; a capturing selector simply becomes correct. **Constants are the whole hazard and statics are not**: C# compiles a capture into a `ConstantExpression` holding the display class (or `this`), frozen when the lambda is compiled, whereas a static field is read at INVOCATION time and is per-process by construction. Value-typed and `string` constants — a literal, an enum such as `StringComparison.Ordinal` — are immutable and belong to no instance, so they do not count; anything else non-null does, and the judgment deliberately errs toward "captured" — which is not free: a false positive costs that `Expression.Compile()` on every request, not one cache entry. The remedy for a selector that really must read an outside value is to hoist it so the lambda reads nothing but its parameter (fold it into a model property); assigning it to a local first does NOT help, because a captured local is still compiled into a display class, and promoting it to a `static` restores caching only for a value that genuinely is per-process. The **key** selector gets the same gate on both its caches even though the constructor's direct-property-access check leaves only `x => captured.Member` reachable there — degenerate as a key, but uniformity is what makes "no cache in this type stores instance-derived state" a property rather than a case analysis. `s_structuralAccessorCache` is untouched: it is keyed by property name and derived from `TModel` alone.

**`MapGroup` slash insertion - critical routing rule.** `MapGroup` inserts a `/` between the group prefix and any route template that doesn't start with `/`. This breaks OData key syntax (`Widgets({key})` vs `Widgets/({key})`). All entity-set routes are therefore mapped on the top-level `/prefix` group with the entity set name embedded in the template (e.g. `"/Widgets({key})"`) rather than on a per-entity sub-group. If you add new routes, follow this pattern.

**Profile types have no ASP.NET Core dependency.** Auth config is stored as plain `AuthorizationConfig` data; the factory applies `RequireAuthorization`. Keep it this way. The one place a profile touches an ASP.NET Core type is #498's `OperationSignatureValidation.Validate`, which asks `typeof(IResult).IsAssignableFrom(returnType)` to refuse a handler that returns the HTTP envelope the framework owns — a bind-time *rejection*, not configuration a profile carries, and it lives in its own file so the division above stays legible.
