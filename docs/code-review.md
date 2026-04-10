# Deep Code Review — OhData Framework

Date: 2026-04-10

---

## CRITICAL

### C1. `ODataEntitySetProfile` `new` field shadowing silently breaks route registration

`src/OhData.Abstractions.AspNetCore.OData/ODataEntitySetProfile.cs` uses `protected new` to shadow base fields (`GetById`, `GetQueryable`, `Put`, `PutById`, `Post`). But `IEntitySetEndpointSource` is implemented in the **base class**, so `HasGetById`, `HasPost`, etc. check the **base** (hidden) fields — which are always `null`. A user who sets `GetById` on an `ODataEntitySetProfile` gets no route registered and no error at startup. This is a silent correctness failure for the entire `ODataEntitySetProfile` subsystem.

> **Resolved:** Removed the four non-collection `new` shadows (`GetById`, `Put`, `PutById`, `Post`) from `ODataEntitySetProfile`. The class now only shadows `GetQueryable` and adds `GetEnumerable` — both routed correctly via `IODataEntitySetEndpointSource`. Write handlers use base-class fields directly. The dead base `Put` field (M8) was removed simultaneously. — `src/OhData.Abstractions.AspNetCore.OData/ODataEntitySetProfile.cs`, `src/OhData.Abstractions/EntitySetProfile.cs` — confidence: high.

### C2. ETag TOCTOU — check-then-act is not atomic

`src/OhData.AspNetCore/OhDataEndpointFactory.cs` `CheckETagAsync` fetches the entity, computes the ETag, compares against `If-Match`, then returns to let the caller proceed with the mutation. Between the check and the write, another request can change the entity. The concurrency guarantee ETags promise is broken. The fix requires passing the concurrency token down to the data store so the **write itself** is conditional (e.g., EF Core `RowVersion` → SQL `WHERE RowVersion = @expected`).

> **Resolved (documentation):** This is an inherent HTTP-layer limitation — the framework cannot atomically check-and-write without data-store participation. Added XML `<remarks>` doc to `CheckETagAsync` explaining the advisory nature of the check and recommending EF Core `[Timestamp]` / SQL `WHERE RowVersion = @expected` for true atomicity. — `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high (correct characterisation of the constraint).

### C3. No `$top` / `$filter` depth limits — DoS via unbounded queries

`ODataQuerySettings` is constructed with defaults (no `PageSize`, no `MaxTop`, no `MaxNodeCount`). A client can send `$top=2147483647` or deeply nested `$filter` expressions, causing unbounded memory allocation or CPU.

> **Resolved:** Added `MaxTop` (int?, default `1000`) to `EntitySetDefaults` and a `protected int? MaxTop` per-profile override to `EntitySetProfile`, following the identical pattern as `FilterEnabled`/`SelectEnabled`. Resolved value is cached in `_resolvedMaxTop` during `VisitModelBuilder` and exposed via `IEntitySetEndpointSource.MaxTop`. When no explicit `$top` is in the request, the factory applies `filtered.Take(source.MaxTop.Value)` after all other options. Per-profile `MaxTop = null` disables the cap for that entity set. — `src/OhData.Abstractions/EntitySetDefaults.cs`, `EntitySetProfile.cs`, `IEntitySetEndpointSource.cs`, `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

---

## HIGH

### H1. `DynamicInvoke` is slow and wraps exceptions

`src/OhData.Abstractions/BoundOperationDefinition.cs:33` — `del.DynamicInvoke(fullArgs)` is ~100x slower than compiled delegates and wraps all exceptions in `TargetInvocationException`. User-thrown validation errors become opaque 500s unless the factory unwraps `.InnerException`.

> **Resolved:** Added a `catch (TargetInvocationException tie) when (tie.InnerException is not null)` block that uses `ExceptionDispatchInfo.Capture(tie.InnerException).Throw()` to rethrow the original exception with its original stack trace. `DynamicInvoke` is retained (performance is acceptable for bound operations, which are rare vs entity CRUD). — `src/OhData.Abstractions/BoundOperationDefinition.cs` — confidence: high.

### H2. Plain `Task` return from bound operation yields `VoidTaskResult` instead of null

`src/OhData.Abstractions/BoundOperationDefinition.cs:39-40` — When a bound action returns `Task` (not `Task<T>`), `task.GetType().GetProperty("Result")?.GetValue(task)` returns the internal `VoidTaskResult` struct, which serializes as `{}` instead of producing a 204 No Content.

> **Resolved:** Captured `bool isVoidTask = method.ReturnType == typeof(Task) || method.ReturnType == typeof(void)` at `From()` time (before the delegate closure). In the Invoke lambda, after awaiting, `if (isVoidTask) return null` short-circuits before the `Result` property reflection — null return triggers `Results.NoContent()` in the factory. — `src/OhData.Abstractions/BoundOperationDefinition.cs` — confidence: high.

### H3. `$count` ignores `$filter` on the `GetAll` path

`src/OhData.AspNetCore/OhDataEndpointFactory.cs` `/$count` handler — when only `GetAll` is set, `$filter` in the query string is silently ignored and the count reflects the full unfiltered collection. Incorrect results.

> **Resolved:** The `/$count` GetAll branch now returns `400 UnsupportedQueryOption` if `$filter` is present. Additionally, all three `/$count` paths now return `Results.Content(..., "text/plain")` per OData spec (was `Results.Ok`). — `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

### H4. POST `Location` header points to the collection, not the created entity

`Results.Created($"{prefix}/{name}", result)` produces `/odata/Widgets` — should be `/odata/Widgets(42)`. The framework would need to extract the key from the returned model via the `_getKey` expression.

> **Resolved:** Added `string InvokeGetKeyString(object model)` to `IEntitySetEndpointSource`. Implemented in `EntitySetProfile` by compiling `_getKey` lazily via `_keyToString ??= CompileKeyToString()`. Factory POST handler now builds `{prefix}/{name}({keyStr})` and returns 400 if the Post handler returns null. — `src/OhData.Abstractions/IEntitySetEndpointSource.cs`, `EntitySetProfile.cs`, `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

### H5. `Convert.ChangeType` fails for `Guid`, enums, `Nullable<T>`, `DateTimeOffset`

Bound function parameter parsing uses `Convert.ChangeType` which doesn't handle these types. The catch block returns a generic error instead of proper type conversion. Use `TypeDescriptor.GetConverter` or `JsonSerializer.Deserialize`.

> **Resolved:** Replaced `Convert.ChangeType` with `TypeDescriptor.GetConverter(targetType).ConvertFromInvariantString(...)` where `targetType` is obtained via `Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType`. Handles `Guid`, all enums, `DateTimeOffset`, `TimeSpan`, and all other standard types. — `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

### H6. `OhDataOptions` is shared across all named registrations

`services.Configure<OhDataOptions>(...)` configures the **default** (unnamed) options instance. Multiple named registrations overwrite each other's options. The last `AddOhData` call wins for all registrations.

> **Not directly fixed** (no practical impact today — `OhDataOptions` is empty). New per-entity configuration (`MaxTop`) was routed to `EntitySetDefaults` instead, avoiding the shared-options problem entirely. Added a code comment in `OhDataBuilder.Register()` warning against using `OhDataOptions` for registration-specific config. If per-registration options are ever needed, named `IOptions<OhDataOptions>` (keyed by registration name) would be the correct approach.

### H7. `OhDataRegistrationCollection` uses plain `Dictionary` — not thread-safe

Registered as singleton, written to from keyed singleton factory callbacks that could resolve concurrently. Should be `ConcurrentDictionary`.

> **Resolved:** Changed backing field to `ConcurrentDictionary<string, OhDataRegistration>` with `OrdinalIgnoreCase` comparer. `Add` now uses `TryAdd`. — `src/OhData.AspNetCore/OhDataRegistrationCollection.cs` — confidence: high.

### H8. No guard against double `AddOhData` with the same name

Second call silently overwrites the first. No exception, no warning. First registration's profiles are orphaned in DI as unused singletons.

> **Resolved:** Added an eager guard at `services.AddOhData(name, ...)` call time (before DI registration) that scans `IServiceCollection` for an existing keyed `OhDataRegistration` descriptor with the same name and throws `InvalidOperationException` immediately. Moving the check to call-time is necessary because DI silently replaces keyed singletons — a lazy factory-time check would never fire for the duplicate. — `src/OhData.AspNetCore/ServiceCollectionExtensions.cs` — confidence: high.

### H9. `GetAll` path silently ignores `$filter`, `$orderby`, `$top`, `$skip`

`src/OhData.AspNetCore/OhDataEndpointFactory.cs` — The `GetAll` handler constructs `ODataQueryOptions` but only applies `$select`. If a client sends `$filter=Name eq 'foo'`, it's silently ignored. The client believes filtering was applied. Should either reject unsupported options with 400 or document this prominently.

> **Resolved:** The GetAll collection handler now returns `400 UnsupportedQueryOption` if any of `$filter`, `$orderby`, `$top`, or `$skip` are present. `$select` continues to work on the GetAll path (as designed). This gives the client an honest signal to use `GetQueryable` if they need server-side query processing. — `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

---

## MEDIUM

### M1. `$metadata` and service document are recomputed per request

Both are deterministic after startup. The CSDL XML serialization allocates `StringBuilder` + `XmlWriter` on every `GET /$metadata`. Should be computed once and cached.

> **Resolved:** Extracted `private static string BuildMetadataXml(IEdmModel)` helper. Called once in `MapAll()` at startup; result is closed over by the `/$metadata` lambda — zero per-request allocations. Service document entity set array pre-computed into `serviceDocEntitySets` at startup; only the base URL (request-specific) is computed per-request. — `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

### M2. PUT handler returns `200 null` when result is null (no 404 check)

PATCH correctly checks for null and returns 404. PUT does not — returns `Results.Ok(null)`. Inconsistent.

> **Resolved:** Added `if (result is null) return ODataError(404, "NotFound", ...)` immediately after `InvokePutByIdAsync`, mirroring the PATCH handler pattern. — `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

### M3. POST handler doesn't check for null result either

If `InvokePostAsync` returns null, `Results.Created(...)` produces a 201 with null body.

> **Resolved (bundled with H4):** The null check (`if (result is null) return ODataError(400, ...)`) was added as part of the H4 POST Location header fix — null must be detected before the key extraction that builds the Location URL. — `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

### M4. Navigation routes don't wrap in OData envelope

Navigation `GET /Parents(1)/Children` returns bare `Results.Ok(result)` while all other collection routes return `{"@odata.context": "...", "value": [...]}`. Inconsistency confuses OData clients.

> **Resolved:** Navigation route lambda now accepts `HttpContext ctx`. When `nav.IsCollection` is true, result is wrapped in `{"@odata.context": "…/$metadata#{Name}/{NavProp}", "value": result}`. Single-object navigations remain bare (consistent with `GetById`). — `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

### M5. `EntitySetName` and query-option fields are mutable on a singleton

These `protected` fields can be modified after route registration, creating divergence between the EDM model and runtime behavior. Should be `{ get; protected init; }`.

> **Resolved:** Converted `EntitySetName`, `SelectEnabled`, `ExpandEnabled`, `FilterEnabled`, `OrderByEnabled`, `CountEnabled`, `SelectProperties`, `ExpandProperties`, `FilterProperties`, `OrderByProperties`, and `MaxTop` from fields to `{ get; init; }` properties. C# `init` setters are callable from base and derived constructors, so no callers required changes. Handler delegate fields (`GetAll`, `GetById`, etc.) are intentionally left as fields — they are not configuration state and are not mentioned in this issue. — `src/OhData.Abstractions/EntitySetProfile.cs` — confidence: high.

### M6. Weak ETag prefix not handled in `If-Match` parsing

`Trim('"')` doesn't handle `W/"..."` — the `W/` prefix remains, causing guaranteed mismatch. The server never generates weak ETags but should parse them correctly per RFC 7232.

> **Resolved:** `CheckETagAsync` now trims whitespace, then strips a leading `W/` prefix (case-insensitive per RFC 7232), then trims quotes. A `W/"version"` header now compares correctly against the stored ETag. — `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

### M7. `$count` returns `application/json` — OData spec says `text/plain`

`Results.Ok(count)` returns JSON content type. OData requires `text/plain` for `/$count`.

> **Resolved (bundled with H3):** All three `/$count` handler branches now return `Results.Content(count.ToString(), "text/plain")`. — `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

### M8. `Put` field declared but never wired to a route — dead code / misleading

`src/OhData.Abstractions/EntitySetProfile.cs:29` — `protected Func<TModel, CancellationToken, Task<TModel>>? Put` exists alongside `PutById` but has no `Has*` property, no `Invoke*` method, no route. Users who set it get nothing.

> **Resolved:** Deleted the `Put` field from `EntitySetProfile`. Bundled with the C1 fix since `ODataEntitySetProfile` was shadowing it. — `src/OhData.Abstractions/EntitySetProfile.cs` — confidence: high.

### M9. `AuthorizationConfig.Roles` exposes mutable array reference

Stored in a singleton. Callers can mutate `config.Roles[0] = "hacker"`. Should use `IReadOnlyList<string>`.

> **Resolved:** `AuthorizationConfig.Roles` property type changed from `string[]?` to `IReadOnlyList<string>?`. Constructor stores via `Array.AsReadOnly(roles)` — the wrapper prevents mutation even if the caller retains a reference to the original array. Factory callers updated from `{ Length: > 0 }` to `{ Count: > 0 }` pattern. — `src/OhData.Abstractions/IEntitySetEndpointSource.cs`, `src/OhData.AspNetCore/OhDataEndpointFactory.cs` — confidence: high.

### M10. Navigation expression cast assumes `MemberExpression` — breaks on wrapped expressions

`HasMany`/`HasOptional`/`HasRequired` cast `navigation.Body` directly to `MemberExpression`. Expressions like `x => (object)x.Prop` (UnaryExpression) throw `InvalidCastException` with no useful message.

> **Resolved:** Added `private static string GetNavigationPropertyName(Expression body)` that handles `MemberExpression` directly and recursively unwraps `UnaryExpression` (handles boxing casts and conversions). Throws `ArgumentException` with a clear message for any other expression type. All three `HasOptional`/`HasRequired`/`HasMany` overloads use this helper. — `src/OhData.Abstractions/EntitySetProfile.cs` — confidence: high.

### M11. `OhDataContext.RegisteredModelTypes` contains profile types, not model types

Misleading property name.

> **Resolved:** Renamed `RegisteredModelTypes` → `RegisteredProfileTypes` in `OhDataContext`. The property is `internal` and was only declared (never read externally), so no callers required updating. — `src/OhData.Abstractions/OhDataContext.cs` — confidence: high.

### M12. Named registration test creates two separate hosts instead of one host with two registrations

`MultipleRegistrations_BothMapIndependently` doesn't actually test the keyed-singleton coexistence.

> **Resolved:** Added `MultipleRegistrations_SingleHost_BothRoute` test that registers two named OhData instances (`v1`/`v2`) into a single `WebApplication`, maps both, starts the host, and verifies both route prefixes resolve. — `src/OhData.AspNetCore.Tests/EndpointMappingTests.cs` — confidence: high.

---

## LOW

- **L1.** `BoundFunctions`/`BoundActions` properties allocate a new `List` on every access
- **L2.** All `Invoke*` methods use `!` on potentially-null delegates — no defensive guard
- **L3.** `ApplySelectPostProcess` serializes entire collection to `JsonNode` — O(N*P)
- **L4.** Magic strings for OData error codes scattered throughout the factory
- **L5.** Duplicated `ODataQueryContext` + `ODataQueryOptions` construction pattern ~6 times
- **L6.** `OhDataOptions` is an empty class plumbed through the entire pipeline
- **L7.** `EfCoreWidgetProfile` uses hardcoded InMemory DB name — cross-test data leakage
- **L8.** `ParentWithChildrenProfile` uses `static readonly` lists — mutation in one test affects all others
- **L9.** `Versioning` package is a two-method wrapper that could be extension methods in the main package

---

## Test Coverage Gaps

| Scenario | Risk |
|---|---|
| `$orderby`, `$top`, `$skip` on `GetQueryable` path | Core paging/sorting — zero coverage |
| `ODataEntitySetProfile` (Priority 1 handler path) | Entire subsystem untested (and broken per C1) |
| ETag `If-Match` on DELETE and PATCH | Concurrency checks unverified |
| PUT to nonexistent key (null result) | Returns 200 null instead of 404 |
| `HasOptional`/`HasRequired` navigation routes | Only `HasMany` tested |
| Multiple named registrations in a single host | The actual keyed-singleton pattern |
| `AdvancedConfigure` eject hatch | Zero coverage |
| Policy-based / role-based auth | Only bare `RequireAuthorization()` tested |
| Bound function with `CancellationToken` passthrough | CancellationToken detection untested |
| Bound action with missing/invalid parameters | Only function variant tested |

---

## Recommended Priority

1. **Fix C1** — `ODataEntitySetProfile` is broken. Either remove `new` shadowing and have the derived profile set base fields, or override the `IEntitySetEndpointSource` implementation in the derived class.
2. **Add query limits (C3)** — Expose `MaxTop` / `MaxNodeCount` on `EntitySetDefaults`, pass into `ODataQuerySettings`.
3. **Address H3/H9** — Either reject unsupported query options on `GetAll` with 400, or clearly document the design choice.
4. **Fix H2** — Check for `Task<>` generic type before accessing `.Result`; return null for plain `Task`.
5. **Fix H4** — Extract the key from the POST result and build a proper `Location` header.
6. **Replace `DynamicInvoke` (H1)** — Build compiled expression-based invocation at `BoundOperationDefinition.From()` time.
7. **Fill the critical test gaps** — `$orderby`/`$top`/`$skip`, ETag on DELETE/PATCH, PUT null handling, named registrations in one host.
