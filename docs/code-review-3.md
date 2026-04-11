# Deep Code Review (3rd Pass) -- OhData Framework

Date: 2026-04-10
Scope: Full codebase scrutiny -- correctness, style, design, edge cases, test coverage
Method: Four parallel review agents read every source file; all findings cross-validated against actual code.

---

## CRITICAL

### C1. Authorization logic inconsistency: group vs per-route auth apply different rules

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:183-202, 524-598`

Three distinct auth application strategies exist for the same `AuthorizationConfig`:

| Route type | Auth source | Logic |
|---|---|---|
| Collection routes (GET all, POST, $count) | `entityGroup` group-level (lines 194-202) | `if/else if/else` -- mutually exclusive |
| Key-based routes (GetById, PUT, PATCH, DELETE) | `ApplyAuth` per-route (lines 183-192) | Independent `if` checks -- additive |
| Bound functions/actions | **BOTH** group-level + `ApplyAuth` | Double authorization |

**Consequences when a profile sets BOTH `RequireAuthorization("Policy")` AND `RequireRoles("Admin")`:**

1. **Collection routes** get ONLY the policy (group `if/else if` skips the roles branch). **Roles are not enforced.**
2. **Key-based routes** get both policy AND roles via `ApplyAuth` (independent `if` blocks). Correct.
3. **Bound functions/actions** get the policy from the group AND policy + roles from `ApplyAuth`. **Policy evaluated twice.**

The group-level code uses `if/else if/else` (line 194-202), meaning at most one of policy/roles/plain-auth is applied. `ApplyAuth` uses independent `if` blocks (line 186-191), meaning both policy and roles are applied. This is two different authorization semantics for the same `AuthorizationConfig` object.

> **Resolved:** Fixed group-level auth to use additive `if` checks matching `ApplyAuth` logic: policy and roles are both applied when both configured. Changed `new AuthorizeAttribute { Roles = ... }` to `policy => policy.RequireRole(...)` for mechanism consistency. Removed `ApplyAuth` from bound functions/actions (they inherit group auth, eliminating double auth). Key-based routes on `parentGroup` retain per-route `ApplyAuth`. Hierarchical auth preserved: profile defines base requirements on the entity group, `parentGroup` routes get the same via `ApplyAuth`. -- `src/OhData.AspNetCore/OhDataEndpointFactory.cs` -- confidence: high.

### C2. PATCH key mismatch validation produces false 400 for partial updates

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:433-441`

The PATCH handler binds the full `TModel` from the request body. When a PATCH body omits the key property (e.g., `{ "name": "New" }` on a `Widget` with `int Id`), the deserializer sets `Id = 0` (default). Then:

```csharp
var bodyKeyStr = source.InvokeGetKeyString(model);  // returns "0"
if (!string.Equals(parsedKey.ToString(), bodyKeyStr, StringComparison.Ordinal))  // "1" != "0"
    return ODataError(400, "BadRequest", "Key in URL does not match key in request body.");
```

Every PATCH that omits the key property returns 400. This makes PATCH unusable for its intended purpose (partial updates). The existing tests work only because they were updated to include `Id` in the body -- masking the problem for real clients.

**Fix:** Skip key mismatch validation when the body key is the default value for `TKey`, or skip it entirely on PATCH (URL key is authoritative for PATCH).

> **Resolved:** Changed PATCH handler to manual body reading via `JsonElement`. Added `KeyPropertyName` to `IEntitySetEndpointSource` (extracted from key expression). Before validating, `TryGetJsonProperty` checks if the key property was explicitly present in the JSON body. If present and mismatched: 400. If absent (partial update): skip validation, URL key is authoritative. PUT retains unconditional validation. Added `.Accepts<TModel>("application/json")` for OpenAPI metadata. Tests updated: PATCH bodies no longer include `Id`; new `Patch_OmittedKey_Succeeds` test; `Patch_KeyMismatch_Returns400` retained (explicit mismatch still caught). -- `src/OhData.Abstractions/IEntitySetEndpointSource.cs`, `src/OhData.Abstractions/EntitySetProfile.cs`, `src/OhData.AspNetCore/OhDataEndpointFactory.cs` -- confidence: high.

---

## HIGH

### H1. `$count` GetAll path: null-forgiving cast unfixed

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:349`

```csharp
var items = (IEnumerable<TModel>)(await source.InvokeGetAllAsync(ct))!;
```

The collection GET handler (line 304) was fixed in review 2 to use `result as IEnumerable<TModel> ?? Enumerable.Empty<TModel>()`. The `$count` handler still uses the old null-forgiving cast. If `GetAll` returns null, this throws `NullReferenceException` with no OData error body.

> **Resolved:** Applied same null-safe pattern: `as IEnumerable<TModel> ?? Enumerable.Empty<TModel>()`. -- confidence: high.

### H2. ETag 412 response uses RFC 7807 format instead of OData error format

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:117-118`

```csharp
return Results.Problem(statusCode: 412, title: "Precondition Failed",
    detail: "The ETag does not match the current resource version.");
```

Every other error in the file uses `ODataError()` producing `{ "error": { "code": "...", "message": "..." } }`. The 412 produces `{ "type": "...", "title": "...", "status": 412, "detail": "..." }` (RFC 7807). An OData client parsing error responses will fail on 412.

> **Resolved:** Replaced `Results.Problem(...)` with `ODataError(412, "PreconditionFailed", "...")`. -- confidence: high.

### H3. `$count` for `IODataEntitySetEndpointSource` double-applies `$filter`

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:333-339`

`InvokeGetODataQueryableAsync(options, ct)` passes the full `ODataQueryOptions` to the profile. The profile is expected to apply query options itself (that's the purpose of Priority 1). Then `options.Filter.ApplyTo(queryable, ...)` applies `$filter` **again**. The collection GET handler for Priority 1 (lines 207-231) does NOT double-apply. The `$count` path is inconsistent and double-filters.

> **Resolved:** Removed `Filter.ApplyTo` from the Priority 1 `$count` path. Profile applies query options itself; factory just calls `LongCount()`. -- confidence: high.

### H4. Key mismatch validation uses culture-dependent `ToString()`

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:411, 440`

```csharp
if (!string.Equals(parsedKey.ToString(), bodyKeyStr, StringComparison.Ordinal))
```

`parsedKey` was parsed with `CultureInfo.InvariantCulture` by `ODataKeyParser`. But `parsedKey.ToString()` and `InvokeGetKeyString(model)` (which calls `compiled(model)?.ToString()`) both use `CultureInfo.CurrentCulture`. For `decimal` keys on a system with comma-decimal culture (e.g., `de-DE`): `1.5m.ToString()` produces `"1,5"` on both sides (matching), but different `decimal` precisions (e.g., `1.50m` vs `1.5m`) produce different strings despite representing the same value. For `DateTimeOffset`, `ToString()` format varies by culture.

**Fix:** Use `InvariantCulture`-aware formatting, e.g., `string.Format(CultureInfo.InvariantCulture, "{0}", parsedKey)`.

> **Resolved:** Used `string.Format(CultureInfo.InvariantCulture, "{0}", parsedKey)` in PUT and PATCH key comparisons. Updated `CompileKeyToString` in `EntitySetProfile` to also use `InvariantCulture`. -- confidence: high.

### H5. ODataKeyParser: string key quote stripping does not handle escaped quotes

`src/OhData.AspNetCore/ODataKeyParser.cs:19`

OData spec allows `'O''Brien'` (doubled single quotes) to represent a literal `O'Brien` in string keys. The current code strips outer quotes but does NOT unescape `''` to `'`:

```csharp
return rawKey.StartsWith("'") && rawKey.EndsWith("'") && rawKey.Length >= 2
    ? rawKey[1..^1]  // "O''Brien" -- doubled quotes not unescaped
    : rawKey;
```

**Fix:** After stripping outer quotes, `rawKey[1..^1].Replace("''", "'")`.

> **Resolved:** Added `.Replace("''", "'")` after stripping outer quotes. -- confidence: high.

### H6. ODataKeyParser: `NotSupportedException` not caught in fallback path

`src/OhData.AspNetCore/ODataKeyParser.cs:50-54`

If `keyType` has no registered `TypeConverter`, `TypeDescriptor.GetConverter(keyType).ConvertFromInvariantString(rawKey)` throws `NotSupportedException`. The `catch` clause (line 54) filters on `FormatException | InvalidCastException | OverflowException | ArgumentException` -- `NotSupportedException` is not included. Unhandled, it produces a 500 with no OData error body.

> **Resolved:** Added `NotSupportedException` to the `when` clause. -- confidence: high.

### H7. `BoundOperationDefinition.Invoke` does not handle `ValueTask` / `ValueTask<T>`

`src/OhData.Abstractions/BoundOperationDefinition.cs:23, 43-51`

`isVoidTask` checks for `Task` and `void` only. If a bound function returns `ValueTask<T>`, the code falls through to `return raw;` (line 51), returning the `ValueTask<T>` **struct** as the result instead of awaiting it. The HTTP response will serialize the `ValueTask<T>` internals, not the actual result.

> **Resolved:** Added `ValueTask` and `ValueTask<T>` handling. `ValueTask` is treated as void-like. `ValueTask<T>` is converted to `Task<T>` via `.AsTask()` then processed through the existing `Task<T>` path. -- confidence: high.

### H8. `BoundOperationDefinition.Invoke` uses `DynamicInvoke` -- orders of magnitude slower than direct invocation

`src/OhData.Abstractions/BoundOperationDefinition.cs:36`

`Delegate.DynamicInvoke` uses reflection internally and is ~100x slower than a typed delegate invocation. For bound operations called frequently, this is a per-request tax. The framework could pre-compile a strongly-typed invoker at startup.

> **By design:** Absolute cost ~1-5us vs HTTP overhead ~1-50ms. Added comment documenting the trade-off. Pre-compiled invoker deferred to future optimization pass if profiling shows need.

### H9. Bound action EDM registration does not set return type

`src/OhData.Abstractions/EntitySetProfile.cs:190-200`

Bound functions (lines 152-188) resolve the return type and call `Returns<T>()` / `ReturnsCollection<T>()` on the `FunctionConfiguration`. Bound actions (lines 190-200) register parameters but **never set the return type** on the `ActionConfiguration`. The CSDL `$metadata` will not reflect action return types.

> **Resolved:** Added return type resolution to action EDM registration, mirroring the function logic. `ActionConfiguration.Returns<T>()` / `ReturnsCollection<T>()` called via reflection. -- confidence: high.

### H10. `CsdlWriter.TryWriteCsdl` return value ignored

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:33`

```csharp
CsdlWriter.TryWriteCsdl(model, xmlWriter, CsdlTarget.OData, out _);
```

If it returns `false`, the `$metadata` endpoint serves partial/empty XML with a 200 status code. The discarded `out` error is lost.

> **Resolved:** Captured `out IEnumerable<EdmError> errors`. On failure, throws `InvalidOperationException` with error details. Runs at startup, so failure is fast and visible. -- confidence: high.

### H11. Protected mutable delegate fields on a singleton profile

`src/OhData.Abstractions/EntitySetProfile.cs:27-37, 62`

The CRUD handler delegates (`GetAll`, `GetById`, `Post`, `PutById`, `Patch`, `Delete`, `GetETag`) are `protected` **fields**, not properties with `init` accessors. A subclass can reassign them at any time after construction. Since profiles are registered as singletons, a reassignment during request processing (e.g., from a misguided decorator pattern) would affect all subsequent requests without any thread-safety guarantees.

> **Partially resolved:** Made `GetETag` private (`_getETag`); it's only accessed via the `UseETag()` method and the interface. The CRUD delegate fields remain `protected` -- they are the intentional API surface set in derived constructors. `init` properties are not viable because `UseETag()` (called from constructors) cannot set `init` properties. -- confidence: high.

---

## MEDIUM

### M1. PATCH binds full `TModel`, not a delta -- behaves identically to PUT

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:433`

The PATCH handler binds `TModel model` from the request body. ASP.NET Core sets unspecified properties to defaults. The handler cannot distinguish "client sent `name: null`" from "client omitted `name`". True PATCH requires `JsonPatchDocument<TModel>`, `Delta<TModel>`, or a custom partial-tracking approach. The current PATCH is functionally a PUT with a different HTTP method.

> **Resolved:** Added `PatchDelta` delegate to `ODataEntitySetProfile<TKey, TModel>` accepting `Delta<TModel>` from `Microsoft.AspNetCore.OData.Deltas`. Factory constructs `Delta<TModel>` from the `JsonElement` body, setting only properties present in the JSON via `TrySetPropertyValue`. Profiles that set `PatchDelta` get true partial-update semantics; base `Patch` delegate retained for the simple path. -- `src/OhData.Abstractions.AspNetCore.OData/ODataEntitySetProfile.cs`, `src/OhData.AspNetCore/OhDataEndpointFactory.cs` -- confidence: high.

### M2. POST `Location` header is relative, not absolute

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:397`

```csharp
return Results.Created($"{prefix}/{name}({keyStr})", result);
```

Produces relative URI `/odata/Widgets(42)`. RFC 7231 strongly prefers absolute URIs for `Location`. All `@odata.context` URLs in the file use absolute `scheme://host/path`.

> **Resolved:** Changed to absolute URL via `BuildBaseUrl(ctx, prefix)`. -- confidence: high.

### M3. Navigation route `@odata.context` omits parent key

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:502`

```csharp
["@odata.context"] = $"{baseUrl}/$metadata#{name}/{navPropertyName}",
```

OData v4 spec says `@odata.context` for a navigation result should include the parent key: `$metadata#EntitySet({key})/NavProperty`. Without it, the context URL is ambiguous.

> **Resolved:** Changed to `$"{baseUrl}/$metadata#{name}({key})/{navPropertyName}"`. -- confidence: high.

### M4. `baseUrl` computation duplicated 6 times

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:52, 220, 273, 308, 397, 499`

The interpolation `$"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}{prefix}"` appears in 6 handler lambdas. Should be a `static string BuildBaseUrl(HttpContext ctx, string prefix)` helper.

> **Resolved:** Extracted `private static string BuildBaseUrl(HttpContext ctx, string prefix)`. All 6 call sites replaced. -- confidence: high.

### M5. `EntitySetName` default pluralization is naive

`src/OhData.Abstractions/EntitySetProfile.cs:104`

```csharp
EntitySetName = $"{typeof(TModel).Name}s";
```

Produces `Entitys`, `Personss`, `Childs`, `Datums`. The user can override, but the default is incorrect for many English words.

> **By design:** `+s` works for the majority of English entity names. Edge cases should set `EntitySetName` explicitly. Adding a pluralization library conflicts with the framework's lightweight goal.

### M6. `UseETag` allocates `new byte[] { 0x00 }` per model invocation

`src/OhData.Abstractions/EntitySetProfile.cs:78`

The separator byte array is allocated inside the per-model lambda. Should be `static readonly byte[] Sep = { 0x00 }` at class or closure level.

> **Resolved:** Moved `var sep = new byte[] { 0x00 }` outside the lambda (closure capture instead of per-invocation allocation). -- confidence: high.

### M7. `_keyToString` lazy init is not thread-safe

`src/OhData.Abstractions/EntitySetProfile.cs:364-365`

```csharp
(_keyToString ??= CompileKeyToString())((TModel)model)
```

Non-atomic read-modify-write. `_getKey.Compile()` may execute multiple times under concurrent requests. Not a correctness bug (reference writes are atomic) but wasteful. Use `Lazy<T>` or `Interlocked.CompareExchange`.

> **Resolved:** Replaced with `LazyInitializer.EnsureInitialized(ref _keyToString, CompileKeyToString)`. -- confidence: high.

### M8. `InternalsVisibleTo("OhData.Tests")` references a project that does not exist

`src/OhData.Abstractions/AssemblyInfo.cs:4`

The solution has `OhData.AspNetCore.Tests` but no `OhData.Tests`. This is stale configuration.

> **Resolved:** Removed the stale `InternalsVisibleTo("OhData.Tests")` line. -- confidence: high.

### M9. `OhDataOptions` is an empty class plumbed through DI

`src/OhData.Abstractions/OhDataOptions.cs`

Zero members. The `configureOptions` parameter in `AddOhData` configures nothing. Multiple `AddOhData` calls accumulate options configurations that silently overwrite each other. Either add properties or remove the class and its DI plumbing.

> **Resolved:** Deleted `OhDataOptions.cs`. Removed `configureOptions` parameter from `AddOhData` and `AddOhDataVersion`. Removed from `OhDataRegistration` constructor and `OhDataBuilder.Register()`. -- confidence: high.

### M10. `OhDataRegistrationCollection.Add` is a side effect of DI singleton resolution

`src/OhData.AspNetCore/OhDataBuilder.cs:124`

The collection is populated when the keyed singleton is resolved (during `MapOhData`), not when `AddOhData` is called. If `MapOhData` is never called, `OhDataRegistrationCollection.Get()` throws even though `AddOhData` was called.

> **By design:** Lazy population via keyed singleton resolution is the standard ASP.NET Core pattern for DI-driven startup.

### M11. `BoundOperationDefinition.From` uses `task.GetType().GetProperty("Result")` per invocation

`src/OhData.Abstractions/BoundOperationDefinition.cs:48`

Reflection per invocation to extract `Task<T>.Result`. Should be cached at `From()` time since the return type is known statically.

> **Resolved:** Cached `PropertyInfo` at `From()` time when the return type is known. The cached `resultProp` is captured in the closure. -- confidence: high.

### M12. Group-level auth uses `AuthorizeAttribute.Roles` (comma-separated string) for roles

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:199`

```csharp
entityGroup.RequireAuthorization(new AuthorizeAttribute { Roles = string.Join(",", authConfig.Roles) });
```

This uses the legacy `AuthorizeAttribute.Roles` property (comma-separated, OR semantics within one attribute). `ApplyAuth` (line 189) uses `policy.RequireRole(...)` (programmatic policy). These are two different ASP.NET Core authorization mechanisms producing different `IAuthorizationRequirement` objects. Even without C1's double-auth issue, the two code paths should use the same mechanism.

> **Resolved by C1 fix.** Group-level auth now uses `policy.RequireRole(...)`, matching `ApplyAuth`.

### M13. `AddProfile<TProfile>` allows duplicate registrations

`src/OhData.AspNetCore/OhDataBuilder.cs:52-57`

Calling `AddProfile<ProductProfile>()` twice adds the type to `_profileTypes` twice. The duplicate entity set name check (line 100) catches this at runtime, but the error message says "duplicate entity set name" when the actual problem is a duplicate `AddProfile` call.

> **Resolved:** Added guard: `if (_profileTypes.Contains(typeof(TProfile))) throw InvalidOperationException`. Error message names the duplicate profile type. -- confidence: high.

### M14. Bound function `ConvertFromInvariantString` does not handle `NotSupportedException`

`src/OhData.AspNetCore/OhDataEndpointFactory.cs:538`

For types without a registered `TypeConverter`, `ConvertFromInvariantString` throws `NotSupportedException`. The `catch` on line 538 filters on `FormatException | NotSupportedException | InvalidCastException | OverflowException` -- this one IS caught (unlike the `ODataKeyParser` version). No bug here; noting for completeness. *(Corrected from agent report after re-reading.)*

---

## LOW

- **L1.** `loggerFactory!` null-forgiving operator on line 71 is misleading -- `GetService` can return null. The `!` suppresses a valid warning. `MapEntitySet` already handles null via `loggerFactory?.CreateLogger(...)` but the `!` is cosmetically wrong.
- **L2.** Service document entity sets use anonymous types (`new { name, kind, url }`) for JSON serialization. Fragile if global `JsonSerializerOptions` are changed.
- **L3.** `Produces(200)` on collection GET handlers doesn't specify a response type. OpenAPI metadata shows no schema. Key-based handlers use `.Produces<TModel>(200)` -- inconsistent.
- **L4.** `$count` `.Produces<long>(200)` metadata tells Swagger the response is `long`, but the actual content type is `text/plain`. Misleading for client code generators.
- **L5.** Service document and `$metadata` routes lack `.WithTags(...)`. They appear in "Untagged" group in Swagger UI.
- **L6.** Magic string `"OhData"` for logger category (lines 68, 172). Should use `typeof(OhDataEndpointFactory).FullName`.
- **L7.** `EntitySetDefaults` has no XML doc. Public class with public settable properties; consumers can't determine defaults without reading source.
- **L8.** `AuthorizationConfig` record is defined in `IEntitySetEndpointSource.cs`. A public type should be in its own file.
- **L9.** `NavigationRouteDefinition.Handler` initialized to `null!`. Should be `required` to prevent silent NRE.
- **L10.** `NavigationRouteDefinition.PropertyName` defaults to `""`. An empty property name produces a malformed route. Should be `required`.
- **L11.** `IEntitySetProfile` is an empty marker interface. Provides no compile-time safety beyond preventing `AddProfile<string>()`.
- **L12.** `OhDataBuilder._defaults` captures a mutable reference. If mutated between `Register()` and DI factory execution, values would be wrong. Safe in practice (builder goes out of scope) but technically unsafe.
- **L13.** `WithPrefix("//odata")` produces `"//odata"` -- only single leading slashes are trimmed.
- **L14.** `HasDefaultValue` serialization uses `$"{param.DefaultValue}"` (lines 160, 198). For `null`, produces `""`. For `bool`, produces `"True"`/`"False"` instead of OData-standard `"true"`/`"false"`.

---

## STYLE

- **S1.** `fnDef = fn` and `actionDef = action` loop variable aliases (lines 523, 565) are unnecessary in modern C# (5+). `foreach` variables have per-iteration scope.
- **S2.** `new[] { "PATCH" }` allocated per `MapMethods` call (line 433). Should be `static readonly`.
- **S3.** `= null;` initializers on nullable fields (lines 22-25, 27-37, 62) are redundant -- null is the default for reference types.
- **S4.** `System.Reflection.BindingFlags` fully qualified on line 128 despite `using System.Reflection;` at line 2.
- **S5.** `using System.Security.Cryptography` at file scope in Abstractions (line 3) -- only used inside `UseETag`. Minor namespace pollution.
- **S6.** `ICollection<>` field type for `_configurators`, `_functions`, `_actions` (lines 96-98) -- unnecessary abstraction for private fields that are always `List<>`.
- **S7.** Priority 1/2/3 comments reference a priority system not defined in the file.

---

## TEST SUITE

### Critical test gaps

| Gap | Risk |
|---|---|
| **`IODataEntitySetEndpointSource` / `ODataEntitySetProfile` path** | Entire Priority 1 code path (lines 205-231) untested |
| **`$orderby`, `$skip`, `$top` on GetQueryable** | Core OData paging/sorting -- zero coverage |
| **`AdvancedConfigure` eject hatch** | Zero coverage |
| **`HasOptional` / `HasRequired` navigation** | Only `HasMany` tested |
| **Positive auth test** (200 with valid credentials) | `NoOpAuthHandler` always returns `NoResult`; no test verifies successful access |
| **DELETE + ETag (If-Match)** | ETag checked in code (line 467) but never tested |
| **POST with empty/malformed body** | Deserialization failure path untested |

### Test isolation issues

- **`EfCoreWidgetProfile`** (`Fixtures.cs:101`): Uses fixed database name `"EfCoreWidgets"` -- shared across all tests in the process. Compare with `ExpandoProjectionTests` which correctly uses `Guid.NewGuid()` per instance.
- **`ParentWithChildrenProfile`** (`Fixtures.cs:150-151`): `static readonly List<>` fields. If any test mutates these lists, all tests are affected.
- **`DecimalKeyProfile`, `DateTimeOffsetKeyProfile`, `DateTimeKeyProfile`, `DateOnlyKeyProfile`** (`Fixtures.cs:307-362`): All use `static readonly` lists -- inconsistent with the instance-field pattern used by other profiles.

### Test quality issues

- **`EmptyProfile_GetAll_Returns404`** (`EndpointMappingTests.cs:157`): Tests `/odata/Widgets`, not `/odata/EmptyWidgets` (the empty profile's entity set name). The 404 comes from routing, not from OhData's empty-profile logic.
- **`WithDefaults_MaxTop_AppliedToProfiles`** (`EndpointMappingTests.cs:904`): Sets `MaxTop = 3` but the profile only has 2 items. `2 <= 3` always passes regardless of MaxTop enforcement.
- **`Select_NoSelectParam_ReturnsAllProperties`** (`EndpointMappingTests.cs:326`): Uses `TryGetProperty("id", ...) || TryGetProperty("Id", ...)` -- test doesn't know the actual casing. Should assert deterministically.
- **`EndpointMappingTests`**: 991 lines, ~60 test methods. God class covering CRUD, select, count, ETags, auth, bound ops, navigation, key parsing, MaxTop, prefix normalization, etc. Should be split by concern.
- **`Post_ResponseBodyContainsNewEntity`** (`EndpointMappingTests.cs:136`): Uses `ContinueWith` + `Unwrap` instead of two `await` calls. Unnecessarily complex.
- **`EfCoreWidgetProfile.GetQueryable`** (`Fixtures.cs:99-116`): Creates and never disposes `DbContext`. Resource leak in test fixtures.

---

## Recommended Priority

1. **Fix C1** -- Unify auth logic: use `ApplyAuth` everywhere (remove group-level auth, apply per-route consistently). This is a correctness bug where roles are not enforced on collection routes.
2. **Fix C2** -- Skip key mismatch validation on PATCH, or only validate when the body key differs from `default(TKey)`.
3. **Fix H1** -- Null-check `$count` GetAll result (one-line fix, same pattern as the collection path).
4. **Fix H2** -- Replace `Results.Problem` with `ODataError(412, "PreconditionFailed", "...")`.
5. **Fix H3** -- Remove double-filter on `$count` for `IODataEntitySetEndpointSource` path.
6. **Fix H4** -- Use `InvariantCulture` formatting for key mismatch comparison.
7. **Fix H5** -- Add `rawKey[1..^1].Replace("''", "'")` after stripping string key quotes.
8. **Fix H6** -- Add `NotSupportedException` to `ODataKeyParser` catch clause.
9. **Fix H10** -- Check `TryWriteCsdl` return value; throw or return error.
10. **Fill test gaps** -- `IODataEntitySetEndpointSource`, `$orderby/$skip/$top`, positive auth, DELETE+ETag.
