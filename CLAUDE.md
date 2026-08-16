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
        │      /{EntitySet}({key})/{segment}), if an unbound function/action name collides with
        │      another unbound operation or with an entity set's own collection GET/POST route,
        │      or if a BindEntityFunction/BindEntityAction handler's first parameter isn't the
        │      entity key (TKey)
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

**Query-capability flags and property allowlists are enforced at runtime, not just advertised.** `FilterEnabled`/`OrderByEnabled`/`SelectEnabled`/`ExpandEnabled`/`CountEnabled` (all default `false`) and `FilterProperties`/`OrderByProperties`/`SelectProperties`/`ExpandProperties` gate the `GetQueryable`, `GetAll`, and Priority-1 collection paths, plus `$select`/`$expand` on `GetById`: a disabled option in the request returns `400` (`UnsupportedQueryOption`); a non-allowlisted property returns `400` (`InvalidQueryOption`) via the EDM's model-bound `NotFilterable`/`NotSortable`/`NotSelectable`/`NotExpandable` annotations. `$expand` is also implemented on the single-entity `GetById` route (reuses the collection expansion pipeline). Navigation-collection routes reject unimplemented system query options (`$filter`, `$expand`, `$search`, `$apply`, `$compute`, `$skiptoken`, `$deltatoken`) with `400` rather than silently ignoring them.

**Unhandled handler exceptions get the OData error envelope, not an empty 500.** A group-level endpoint filter (registered alongside the `OData-Version`/`OData-MaxVersion` filters in `OhDataEndpointFactory.MapAll`) catches any exception a handler throws - as opposed to an `ODataError` result a handler deliberately returns - and converts it into `500` + `{"error":{"code":"InternalServerError",...}}`, logging the real exception (never leaking its message/stack trace to the client). Does not catch `OperationCanceledException` (left to ASP.NET Core's own client-disconnect handling) or startup-time validation exceptions (`MapOhData()` throws those once, before any request is served).

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

**Entity-id URLs use a shared canonical key formatter.** `Location`/`Content-Location`/`OData-EntityId`/`@odata.id` values built from a CLR key (POST, and the parsed key on `GetById`/`PUT`/`PATCH`/`$ref`/nav-POST responses) go through `ODataEntityKeyUrlFormatter`, which single-quotes and percent-encodes string keys and doubles embedded quotes - the same escaping `ODataKeyParser` expects on the way back in. `int`/`Guid`/other non-string keys format unchanged.

**POST/PUT/PATCH deserialize the request body by hand.** All three read and JSON-deserialize the body manually (rather than an ASP.NET Core minimal-API bound parameter) so malformed JSON, non-object bodies, and non-JSON `Content-Type` values return the OData error envelope (400/415) instead of ASP.NET Core's implicit binder short-circuiting with an empty body. This is also why PATCH's non-object-body case (JSON array/string/number/bool/null) is caught explicitly - `JsonElement.EnumerateObject()` throws `InvalidOperationException` for non-`Object` `ValueKind`, which is now caught and mapped to 400.

**Open types are EDM-driven, complex-only, attribute-free, and ON BY DEFAULT (#389).** `OhDataRegistration.OpenTypesEnabled` defaults to `true`; `AddOhData(o => o.WithOpenTypes(false))` is the escape hatch, and with it `MapAll` never builds the container map, `OpenTypeJsonOptions.Build` returns the base options reference-equal, and no write-path validation runs — that registration is byte-identical to pre-#389. Default-on because a complex type with a dictionary member **is** an open type: the CSDL has always said `OpenType="true"` and omitted the container, so a nested wire shape made `$metadata` and the payload disagree, and `Microsoft.AspNetCore.OData`'s `ODataResourceSerializer.AppendDynamicProperties` reads the same annotation and flattens with no opt-in flag at all. The hazard that justified opt-in is unchanged and is mitigated by a **startup warning**: flattening *re-binds* an existing adopter's body — once the container is extension data it is no longer a declared property, so `{"Meta":{"Bag":{...}}}` becomes a dynamic key named `Bag`, and the echo of that mis-bound value is byte-identical to the correct one (so it is **not** detectable by diffing responses). `OpenTypeJsonOptions.WarnWireShapeIsFlat` therefore logs one `Warning` per open complex type at `MapOhData()`, naming the CLR type and the container member and pointing at `WithOpenTypes(false)`; a model with no open complex type logs nothing. `OpenTypeJsonOptions` reads `ODataConventionModelBuilder`'s own `DynamicPropertyDictionaryAnnotation` (via `EdmAnnotationExtensions.GetDynamicPropertyDictionary`) for every complex type the EDM marks `IsOpen`, and layers one more `TypeInfoResolver` modifier onto `effectiveJsonOptions` that sets `JsonPropertyInfo.IsExtensionData = true` on exactly that member. Ordering: after `IgnoredPropertyJsonOptions.Build` and before the per-request nav-suppression modifier (which derives from these options, so `WithAddedModifier` chains all three) — but the three can never contend for a member. Nav suppression only removes EDM navigations, of which a complex type has none; and the ignored-property map is keyed by `profile.ModelType` (an **entity** type) while `Ignore(...)` takes a root member of that entity, so it and the open-type modifier never see the same `JsonTypeInfo` at all. The member is matched by `MemberInfo.HasSameMetadataDefinitionAs`, **not** `==`/`ReferenceEquals` — the builder's annotation and STJ's `AttributeProvider` come from independent reflection walks that disagree on `ReflectedType` (the builder can report a *derived* type there), so `PropertyInfo` equality returns false for the same member; `HasSameMetadataDefinitionAs` is *not* whole-member identity (it matches across generic instantiations), and what makes it safe is that the container is only ever resolved by walking the looked-up type's own CLR base chain against a map keyed by `DeclaringType`. Three behaviors that ride along: a dynamic key that is not an OData simple identifier is rejected with 400 (`OpenTypeJsonOptions.FindInvalidDynamicKey`, walked over the raw `JsonElement` against `JsonTypeInfo`) on **every** route that binds a body reaching a bag — POST/PUT/PATCH, the property-route writes, the navigation-POST create route, and each bound/unbound **action parameter** (checked per parameter against its declared type, so the `{"paramName": value}` envelope is never treated as a bag) — and the walk descends into the **value** of an accepted dynamic key at every depth, arrays and dictionary-valued declared members included (a `IDictionary<string, TOpenComplex>` member is `JsonTypeInfoKind.Dictionary`, so the walk recurses into its VALUES; its own map keys are declared-member keys, not dynamic property names, and are not policed), since everything below a bag key is stored verbatim; the grammar is the ABNF's Unicode categories (`L`/`Nl` leading, `L`/`Nl`/`Nd`/`Mn`/`Mc`/`Pc`/`Cf` following) counted in **code points**, not `char.IsLetter` over UTF-16 units, so NFD and NFC spellings of the same key cannot get different status codes for any name within the 128-code-point cap (decomposition adds code points, so only a name already at the cap can differ); and **two conditions throw** `InvalidOperationException` from the wrapped `JsonPropertyInfo.Get` (→ 500 + the OData error envelope via the group filter), checked in one pass over the bag per serialized instance. (1) A bag key equal to a declared property name, instead of emitting a duplicate JSON key — matching MS's `DynamicPropertyNameAlreadyUsedAsDeclaredPropertyName`, since the spec does not decide it (CSDL §6.3/§9.3 "uniquely named"; JSON Format defers to RFC 8259's SHOULD) and the condition is systematic rather than per-row (a client cannot cause it — STJ binds a body key matching a declared name to the declared property). (2) A bag key that is **not a valid `odataIdentifier`** — the same grammar the write path applies, so keys are policed identically in both directions (400 in, 500 out) and the container's contents are fully validated rather than merely checked for emptiness. This was widened from a bare `string.IsNullOrWhiteSpace` ("not a name at all"), which had let `"has space"`, `"@odata.type"` and a key of only `Cf` format characters through; the widening is a **deliberate divergence from MS**, which skips the empty key (`ODataResourceSerializer.cs:820`) and polices nothing else on this path. Affordability comes from `IsValidDynamicPropertyNameCached`: an ASCII `SearchValues<char>` + `ContainsAnyExcept` fast path (within `[A-Za-z0-9_]` the grammar reduces to a length test plus a leading-digit test) falling back to the unchanged rune-and-category walk — `IsValidDynamicPropertyNameByRuneWalk`, kept as the normative oracle the tests prove the fast path equivalent to — plus a 1024-entry process-wide cache of validated **non-ASCII** keys only, since an ordinal cache lookup hashes the whole string and so costs *more* than the fast path it would be memoising. Measured in isolation: 4.6 ns/key vs 16.9 naive and 5.4 for the declared-name lookup beside it. **In situ** (BenchmarkDotNet, 1,000 rows × 20 dynamic keys, full-grammar validation vs the whitespace-only check) the widening costs **+4.0%** on repeating ASCII keys — the common shape — at 5.8 ns/key marginal; **+6.1%** (9.0 ns/key) on 20,000 distinct ASCII keys; and **+14.7%** (28.8 ns/key) on 20,000 distinct non-ASCII keys, the only shape that saturates the 1024-entry cache and therefore implies a handler synthesising per-row non-ASCII key names rather than a schema-like vocabulary. An earlier stopwatch harness put this at ~+26% (~56 ns/key); that number is **refuted** and should not be requoted. Neither throw is quoted with the offending key when it is the grammar failure (it can carry control characters into a log line); the collision message does quote it, since a declared name is bounded developer-authored text. The getter now only *inspects* and always returns the same reference, which is what removed the old drop-and-clone machinery (`TryCreateEmptyLike`, the `Activator.CreateInstance` fallback, the read-only degradation) and with it the #389 M2 corner where a pre-seeded container silently lost every write. Comparison is **ordinal**, so a case-differing key does not throw. And a container STJ cannot use as extension data (getter-only, most often) throws from `MapOhData()` rather than being skipped. The per-request gate is `OhDataRegistration.OpenTypesActive` (enabled **and** the EDM really has an open complex type), **not** `OpenTypesEnabled` — and now that the flag defaults to `true`, that EDM half is the whole blast-radius bound; gating on the flag alone left a registration with no open types buffering every PUT body, which changed its malformed-body error message and broke the documented byte-identical no-op (`OpenTypeDefaultOnIsByteIdenticalTests` pins it across every write route and body shape). Entity-root containers and `$filter`/`$orderby` over individual dynamic keys are deliberately out of scope, and `PATCH` of a complex member is whole-value **replace**, not merge; `docs/open-types.md` and `OpenTypeLimitationTests` record all of it.

**Delta mapping is a separate, dependency-free subsystem (#243).** `DeltaProfile` (non-generic, `For<TModel,TEntity>()` + fluent `.Rename`/`.Ignore`/`.Convert`, no `.Build()`) declares DTO→entity write mappings; the single DI-singleton `IDeltaFactory` exposes `Create<TModel,TEntity>(Delta<TModel>)` and `Create<TModel,TEntity>(TModel)`, both returning a `Delta<TEntity>` the handler applies with `Delta<TEntity>.Patch(entity)` — the framework never applies or persists. Registration is symmetric: `AddEntitySetProfile<T>()` (the former `AddProfile<T>()` — hard-renamed, no alias) and `AddDeltaProfile<T>()`; the existing `AddProfilesFrom*` scanner discovers **both** profile kinds in one pass (widened predicate in `ProfileScanner`, routed in `OhDataBuilder.AddProfilesFrom`). Delta profiles accumulate in a cross-registration `DeltaProfileRegistry` (instance singleton, like `GlobalProfileRegistry`); `IDeltaFactory` is built lazily from it and **forced at `MapOhData()`** for startup fail-fast. `DeltaMappingCompiler` validates every writable model property is mapped/renamed/converted/ignored and every mapping is type-compatible (automatic subset = identity / `target.IsAssignableFrom(source)` / nullable-wrap `T→T?`; everything else needs an explicit `.Convert` — never `Convert.ChangeType`), then compiles an immutable `DeltaMappingPlan` keyed by `(TModel,TEntity)`. `Delta<TEntity>.UpdatableProperties` is seeded from the model allowlist minus `Ignore()`d names, translated through the rename map, so security constraints survive the boundary. Scalars/structural only — no navigation writes. Also ships `Delta<T>` sugar `IsChanged`/`TryGetChanged` (expression-based). Files: `DeltaProfile.cs`, `DeltaFactory.cs`, `IDeltaFactory.cs`, `DeltaExtensions.cs`.

### Project layout

| Project | Target | Role |
|---|---|---|
| `OhData.AspNetCore` | net10.0 | All core and runtime types: `EntitySetProfile<TKey,TModel>`, `IEntitySetEndpointSource` (internal), `IVisitModelBuilder` (internal), `AuthorizationConfig`, `NavigationRouteDefinition`, `BoundOperationDefinition`, `OhDataBuilder`, `OhDataEndpointFactory`, `OhDataRegistration`, `OhDataRegistrationCollection`, `OhDataDefaults`, `AddOhDataVersion` / `MapOhDataVersion` versioning helpers, `ODataEntitySetProfile<TKey,TModel>`, `IODataEntitySetEndpointSource`, `DeltaProfile` / `DeltaMapping<TModel,TEntity>` / `IDeltaFactory` (delta mapping) and `Delta<T>` sugar, extension methods |
| `OhData.Client` | net10.0 | Typed .NET OData 4.0 client with fluent LINQ-based filter/select/expand translation |
| `OhData.TestBench.AspNetCore` | net10.0 | Runnable demo app with EF Core InMemory, Swagger UI + Scalar, versioned v1/v2 registrations |
| `OhData.ClientTestBench.AspNetCore` | net10.0 | Runnable demo app used as server target for client integration tests |
| `OhData.AspNetCore.Tests` | net10.0 | xUnit integration tests using `WebApplicationFactory` via `TestHostBuilder` |
| `OhData.Client.Tests` | net10.0 | xUnit tests for OhData.Client |
| `OhData.Client.Benchmarks` | net10.0 | BenchmarkDotNet project for client library performance |
| `OhData.Server.Benchmarks` | net10.0 | BenchmarkDotNet project comparing OhData's minimal-API pipeline against `Microsoft.AspNetCore.OData`'s `ODataController`+`[EnableQuery]` pipeline; report in `docs/server-comparison-report.md` |
| `OhData.MicrosoftODataClient.Tests` | net10.0 | Compatibility tests against Microsoft.OData.Client |

### `InternalsVisibleTo`

There is no `AssemblyInfo.cs`. The grants are MSBuild `<InternalsVisibleTo Include="..." />` items in `src/OhData.AspNetCore/OhData.AspNetCore.csproj` (the SDK turns each into an assembly attribute), five of them:

| Grantee | Why |
|---|---|
| `OhData.AspNetCore.Tests` | Access to the internal `IEntitySetEndpointSource` and `IVisitModelBuilder` interfaces. |
| `OhData.AspNetCore.OpenApi` | #228: the OpenAPI companion packages read the internal per-profile `IgnoredPropertyNames` (via `IgnoredPropertyDocsMap`) so generated schemas omit `Ignore()`d properties, matching the real wire shape. |
| `OhData.AspNetCore.NSwag` | Same as above. |
| `OhData.AspNetCore.Swashbuckle` | Same as above. |
| `OhData.Server.Benchmarks` | #389: the open-type serialize path is reachable only through internals (`OpenTypeJsonOptions`, its `Build`/`BuildOpenComplexTypeContainerMap` entry points, `IsValidDynamicPropertyNameCached`). The grant exists so `OpenTypeKeyValidationBenchmarks` measures the **shipped** validator rather than a transcribed copy of it — a copy is exactly the mistake that produced a 12x-wrong number for this code path once already. It widens no public API and changes no behaviour. |

`src/OhData.Client/OhData.Client.csproj` carries the same pattern for `OhData.Client.Tests` and `OhData.Client.Benchmarks`.
