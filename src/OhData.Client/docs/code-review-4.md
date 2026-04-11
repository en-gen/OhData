# Code Review 4 — API Ergonomics & Developer Experience

## Summary

Examined XML docs completeness, method discoverability, fluency, `OhDataClientOptions` defaults, `ODataClientException` messages, and missing overloads. The API is generally fluent and well-documented. Several gaps were found: `KeyedEntitySetClient` is not directly accessible to consumers (no XML doc on the class itself's properties), `InsertAsync` uses the entity set name as the URL instead of the formatted entity set URL (works but is inconsistent with `BuildCollectionUrl`), `OhDataClientOptions` does not expose any way to set a custom `HttpMessageHandler` or timeout, `EntitySetClient` has no `AnyAsync()` convenience method, and the `OhDataClient(string)` constructor does not validate that the base address is a well-formed URI.

## Findings

### FINDING-4-1: `OhDataClient(string)` constructor does not validate the base address [severity: major]

**Current code:**
```csharp
public OhDataClient(string baseAddress, OhDataClientOptions? options = null)
{
    _options        = options ?? new OhDataClientOptions();
    _httpClient     = new HttpClient { BaseAddress = new Uri(baseAddress.TrimEnd('/') + '/') };
    ...
}
```

If `baseAddress` is null, `baseAddress.TrimEnd('/')` throws `NullReferenceException` — an unhelpful error. If it is an empty string, `new Uri("")` throws `UriFormatException` with an unhelpful message. The constructor should validate and produce a clear `ArgumentNullException` or `ArgumentException`.

**Thought tree:**
- Option A: Add `ArgumentNullException.ThrowIfNull(baseAddress)` and `ArgumentException` for empty strings.
  - Pro: Clear, idiomatic .NET validation.
  - Con: Minor extra code.
- Option B: Let it fail with the existing exceptions.
  - Pro: No code change.
  - Con: Confusing NullReferenceException message with no hint that `baseAddress` is the cause.

**Decision:** Option A.

**Proposed fix:**
```csharp
public OhDataClient(string baseAddress, OhDataClientOptions? options = null)
{
    ArgumentNullException.ThrowIfNull(baseAddress);
    if (baseAddress.Length == 0)
        throw new ArgumentException("Base address must not be empty.", nameof(baseAddress));
    ...
}
```

---

### FINDING-4-2: `InsertAsync` passes `_entitySetName` directly instead of using `BuildCollectionUrl()` [severity: minor]

**Current code:**
```csharp
public Task<T> InsertAsync(T entity, CancellationToken ct = default)
    => _http.PostAsync(_entitySetName, entity, ct);
```

This bypasses `BuildCollectionUrl()`. While POST typically does not use query params, if a developer chains `.Filter().InsertAsync(...)` they would expect the filter (or at minimum an informative error) — instead the filter is silently ignored. The inconsistency is confusing; `_entitySetName` (no query string) vs `BuildCollectionUrl()` (may include query string) for POST.

However, the bigger issue is discoverability: a developer looking at `PostAsync` in `ODataHttpClient` would expect the URL to match the entity set. Using `BuildCollectionUrl()` for POST makes the interface uniform. In OData 4.0, POST collections do not use `$filter` or other query options, so `BuildCollectionUrl()` would add unnecessary noise.

**Thought tree:**
- Option A: Use `_entitySetName` (current) — POST target is always the bare entity set URL.
  - Pro: Correct semantics; OData POST doesn't use query options.
  - Con: Internal inconsistency; filters are silently dropped.
- Option B: Use `BuildCollectionUrl()` — includes any query params.
  - Pro: Uniform; consistent with other terminal operations.
  - Con: Query options have no meaning for POST; this would produce malformed POSTs.
- Option C: Keep `_entitySetName` but add a guard that warns if any query state is set.
  - Pro: Alerts the developer to a likely mistake.
  - Con: Cannot easily warn (no logging hook) without breaking the immutable/clean design.

**Decision:** Option A is correct. But add an XML doc note: "Any query options (Filter, Select, etc.) set on the builder are ignored for POST." This makes the behaviour explicit.

---

### FINDING-4-3: `AnyAsync()` convenience method is missing [severity: minor]

**Current code** does not have an `AnyAsync()` method. A common pattern is:
```csharp
var exists = await client.For<Widget>().Filter(x => x.Name == "foo").AnyAsync();
```

Developers must currently write `(await ...).CountAsync() > 0` or `..FirstOrDefaultAsync() != null`. Neither is optimal: `CountAsync` issues a `/$count` request, `FirstOrDefaultAsync` adds `$top=1` and deserializes an entity. Both work but `AnyAsync` via `/$count` with filter is the most direct.

**Thought tree:**
- Option A: Implement `AnyAsync()` as `CountAsync() > 0` (uses `/$count` endpoint).
  - Pro: Correct; uses dedicated count endpoint.
  - Con: Makes an HTTP request that returns a full count when we just need "nonzero".
- Option B: Implement as `FirstOrDefaultAsync() != null` (uses `$top=1`).
  - Pro: Returns less data for the non-exists case (no entity body needed).
  - Con: Must deserialize an entity; slightly more data.
- Option C: Use `CountAsync() > 0` but document it issues a `/$count` request.
  - Pro: Explicit, documented behaviour.
  - Con: Same as Option A.

**Decision:** Option A — implement via `CountAsync() > 0`. This is semantically clear and the `/$count` endpoint is O(1) on most database-backed servers. Document the behaviour.

**Proposed fix:**
```csharp
/// <summary>
/// Returns <see langword="true"/> when at least one entity matches the current query options;
/// <see langword="false"/> otherwise. Executes GET <c>/$count</c>.
/// </summary>
public async Task<bool> AnyAsync(CancellationToken ct = default)
    => await CountAsync(ct) > 0;
```

---

### FINDING-4-4: `EntitySetClient<T>` XML docs on `Select(params string[])` and `Expand(params string[])` are sparse [severity: minor]

**Current code:**
```csharp
/// <summary>Projects the response to a subset of properties by name.</summary>
public EntitySetClient<T> Select(params string[] properties)
    => With(_state with { Select = string.Join(',', properties) });
```

No example showing usage. No documentation that the property names must match the server's property names (case-sensitive on some servers). No note about what happens with an empty array.

**Thought tree:**
- Option A: Add `<example>` XML doc blocks with usage examples.
  - Pro: IDE tooltip shows example; reduces friction.
  - Con: More text.
- Option B: Leave as-is.
  - Pro: Minimal.
  - Con: Developer must guess the correct format.

**Decision:** Option A — add examples and edge-case notes to string overloads of `Select` and `Expand`.

---

### FINDING-4-5: `OhDataClientOptions` has no XML doc explaining when to customize it [severity: minor]

**Current code:**
```csharp
/// <summary>
/// Configuration for <see cref="OhDataClient"/>. All properties have sensible defaults
/// that match the OhData server's out-of-box behaviour (camelCase JSON, case-insensitive reads).
/// </summary>
public sealed class OhDataClientOptions
{
    public JsonSerializerOptions JsonOptions { get; set; } = new JsonSerializerOptions { ... };
}
```

The doc is adequate but `JsonOptions` has no XML doc at all. Developers don't know they can replace the entire `JsonSerializerOptions` or what side effects doing so has (e.g., removing `PropertyNameCaseInsensitive` will break `@odata.count` deserialization since that property name is fixed by the spec in the `ODataCollectionResponse` internal class — although that class uses `[JsonPropertyName]` so case sensitivity doesn't matter for it).

**Decision:** Add XML doc to `JsonOptions` explaining what it controls and recommending immutable-after-registration usage.

---

## Changes made

1. **`OhDataClient.cs`** — Added null and empty-string guards to the `string` constructor.

2. **`EntitySetClient.cs`** — Added `AnyAsync()` method. Added `<example>` blocks and edge-case notes to `Select(params string[])` and `Expand(params string[])`. Added note to `InsertAsync` that query options are ignored.

3. **`OhDataClientOptions.cs`** — Added XML doc to `JsonOptions` property.
