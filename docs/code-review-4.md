# Deep Code Review (4th Pass) -- OhData Framework

Date: 2026-04-10
Scope: Full codebase re-examination after 3 prior review rounds
Method: Three parallel Opus review agents; findings deduplicated and cross-validated.

---

## HIGH

### H1. PATCH handler missing `JsonException` catch for malformed body

`src/OhData.AspNetCore/OhDataEndpointFactory.cs`

PATCH manually reads the body via `JsonSerializer.DeserializeAsync<JsonElement>`. Malformed JSON throws `JsonException`. The catch clause only handled `FormatException`, producing an unhandled 500. The bound action handler correctly caught `JsonException` — PATCH did not.

> **Resolved:** Added `catch (JsonException ex)` returning `ODataError(400, "InvalidBody", ex.Message)` before the `FormatException` catch. Matches the bound action pattern. -- confidence: high.

### H2. `AdvancedConfigure` eject skips `_resolvedMaxTop` and `_resolvedIdempotentDelete`

`src/OhData.Abstractions/EntitySetProfile.cs`

When a profile overrides `AdvancedConfigure`, `VisitModelBuilder` returned early before assigning `_resolvedMaxTop` and `_resolvedIdempotentDelete`. MaxTop defaulted to `null` (no limit — DoS risk) and IdempotentDelete to `false` (contradicting the documented default of `true`).

> **Resolved:** Moved both assignments before the `AdvancedConfigure` call and eject check. They now run unconditionally. -- confidence: high.

### H3. `IODataEntitySetEndpointSource.GetEnumerable` / `HasGetODataEnumerable` — dead code

`src/OhData.Abstractions.AspNetCore.OData/ODataEntitySetProfile.cs`, `IODataEntitySetEndpointSource.cs`

The `GetEnumerable` delegate and its interface members were declared but never consumed by the factory. A profile setting `GetEnumerable` would silently get no collection GET route.

> **Resolved:** Removed `GetEnumerable` field, `HasGetODataEnumerable`, and `InvokeGetODataEnumerableAsync` from both the profile and interface. -- confidence: high.

### H4. EDM return-type unwrapping doesn't handle `ValueTask<T>` for bound operations

`src/OhData.Abstractions/EntitySetProfile.cs`

The H7 fix from review 3 added `ValueTask<T>` runtime handling, but the EDM registration code that determines `$metadata` return types only checked `Task<>`, not `ValueTask<>`. A bound function returning `ValueTask<int>` would produce incorrect metadata.

> **Resolved:** Added `typeof(ValueTask<>)` as a second generic-unwrap condition and `typeof(ValueTask)` to the void-return check, in both function and action EDM blocks. -- confidence: high.

---

## MEDIUM

### M1. `GetAll` materializes entire dataset before rejecting unsupported query options

`src/OhData.AspNetCore/OhDataEndpointFactory.cs`

Both the collection GET and `$count` handlers for the `GetAll` path called `InvokeGetAllAsync` before checking for unsupported query options. An attacker could force full materialization with any `$filter` parameter, then receive a 400.

> **Resolved:** Reordered: `ODataQueryOptions` construction and unsupported-options rejection now happen BEFORE `InvokeGetAllAsync`. Applied to both collection GET and `$count` paths. -- confidence: high.

### M2. `UseETag()` with zero selectors produces constant ETag

`src/OhData.Abstractions/EntitySetProfile.cs`

Calling `UseETag()` with no property selectors produced the same SHA-256 hash for every entity, silently disabling concurrency protection.

> **Resolved:** Added `ArgumentException` guard: at least one property selector required. -- confidence: high.

### M3. `RequireRoles()` with empty array silently degrades to plain auth

`src/OhData.Abstractions/EntitySetProfile.cs`

`RequireRoles(Array.Empty<string>())` set `_authRoles` to a zero-length list, which the factory treated as "no roles" and fell through to plain `RequireAuthorization()`.

> **Resolved:** Added `ArgumentException` guard: at least one role required. -- confidence: high.

### M4. PATCH double-deserializes `TModel` when key present in body

`src/OhData.AspNetCore/OhDataEndpointFactory.cs`

The model was deserialized once for key validation and again for the patch call. Wasteful for large models.

> **Resolved:** Hoisted `body.Deserialize<TModel>` to a single call; result reused for both key validation and patch invocation. -- confidence: high.

### M5. `HasDefaultValue` bool serialization produces `"True"` instead of `"true"`

`src/OhData.Abstractions/EntitySetProfile.cs`

C# `$"{true}"` produces `"True"`. OData standard uses lowercase.

> **Resolved:** Added special-case: `param.DefaultValue is bool b ? (b ? "true" : "false") : $"{param.DefaultValue}"`. Applied to both function and action EDM blocks. -- confidence: high.

### M6. `WithPrefix(" /odata ")` preserves embedded whitespace

`src/OhData.AspNetCore/OhDataBuilder.cs`

`Trim('/')` only strips slashes, not whitespace. `" /odata "` became `"/ /odata "`.

> **Resolved:** Changed to `prefix.Trim().Trim('/')` — whitespace stripped before slashes. -- confidence: high.

---

## LOW / STYLE

- **S1:** Extracted `new[] { "PATCH" }` to `private static readonly string[] PatchMethod`.
- **L1:** `$count` endpoint `Produces<long>(200)` metadata vs `text/plain` response — noted, not fixed (requires custom `IEndpointMetadataProvider`).
- **L2:** `GetById` response omits `@odata.context` — noted, deferred (single-entity context URLs have different OData format requirements).

---

## TEST FIXES

- **EmptyProfile tests**: Fixed 3 tests to target `/odata/EmptyWidgets` instead of `/odata/Widgets`.
- **Escaped string key**: Added `O'Brien` to `ThingProfile` store; new test `GetById_StringKey_EscapedQuotes_Returns200`.
- **Duplicate AddProfile guard**: New test `AddProfile_Duplicate_Throws` in `OhDataBuilderTests.cs`.
- **WithDefaults MaxTop**: Changed from `MaxTop=3` (tautological with 2-item store) to `MaxTop=1` (actually caps results).
- **MultipleRegistrations resource leak**: Wrapped `app` and `client` in `await using`/`using`.

**Test count: 117 passing.**
