# Code Review 3 — HTTP Contract Adherence

## Summary

Examined correct OData URL format for all key types, Content-Type headers on POST/PUT/PATCH, response handling for 204 No Content, OData error envelope parsing, and cancellation. The HTTP contract is mostly correct, but several issues were found: the `EnsureSuccessAsync` method creates a `new JsonSerializerOptions` on every non-2xx response (wasteful), `DeleteAsync` could receive 204 No Content from conformant servers and the code handles it correctly already, but `PutAsync`/`PatchAsync` do not handle 204 responses (server may return 204 on update without returning the updated entity), and the OData error parser uses a hard-coded `PropertyNameCaseInsensitive` options object instead of the configured one.

## Findings

### FINDING-3-1: `EnsureSuccessAsync` allocates a new `JsonSerializerOptions` on every error [severity: minor]

**Current code:**
```csharp
private static async Task EnsureSuccessAsync(
    HttpResponseMessage response, string url, CancellationToken ct)
{
    if (response.IsSuccessStatusCode) return;
    throw await ODataClientException.FromResponseAsync(
        response, url, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
}
```

A new `JsonSerializerOptions` instance is constructed on every non-2xx response. While this happens on the error path only, it wastes memory and adds unnecessary GC pressure in high-frequency scenarios (e.g., a service that frequently returns 404). `JsonSerializerOptions` are heavy objects; they should be cached.

**Thought tree:**
- Option A: Use a static read-only `JsonSerializerOptions` instance for error parsing.
  - Pro: Zero allocation on the error path; consistent.
  - Con: None.
- Option B: Pass `_options.JsonOptions` (the caller's options) to `FromResponseAsync`.
  - Pro: Uses the configured case sensitivity. However, `PropertyNameCaseInsensitive = true` is important for OData error envelopes which may have different casing from user models.
  - Con: The user's `JsonOptions` may have `PropertyNameCaseInsensitive = false`.
- Option C: Combine both — always use case-insensitive for error parsing but use a static.
  - Pro: Correct and zero-allocation.
  - Con: Slightly more code.

**Decision:** Option A — add a static readonly field for the error-parsing options. The OData error envelope has well-known field names (`error`, `code`, `message`) that are reliably lowercase from OhData servers, so case-insensitive is defensive best practice.

**Proposed fix:**
```csharp
private static readonly JsonSerializerOptions _errorParseOptions =
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

private static async Task EnsureSuccessAsync(
    HttpResponseMessage response, string url, CancellationToken ct)
{
    if (response.IsSuccessStatusCode) return;
    throw await ODataClientException.FromResponseAsync(
        response, url, _errorParseOptions, ct);
}
```

---

### FINDING-3-2: `PutAsync`/`PatchAsync` throw on 204 No Content responses [severity: major]

**Current code:**
```csharp
internal async Task<T> PutAsync<T>(string url, T body, CancellationToken ct)
    where T : class
{
    ...
    return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct)
           ?? throw new InvalidOperationException($"PUT to '{url}' returned an empty body.");
}
```

OData 4.0 services may return HTTP 204 No Content on PUT and PATCH (preference `return=minimal`). When the body is empty (Content-Length: 0), `ReadFromJsonAsync` returns null and the client throws `InvalidOperationException`. This is a surprising failure for valid server behaviour.

The OhData server currently returns 200 with the full entity, so existing tests pass. However, consumers may use this client against other OData services that implement 204 correctly.

**Thought tree:**
- Option A: Return `null` (as `T?`) on 204, requiring callers to handle null.
  - Pro: Accurate representation of server response.
  - Con: Requires changing return type to `T?`, breaking the current API.
- Option B: On 204, return the entity that was sent as the body (the `body` parameter for PUT).
  - Pro: Callers get back a value; sensible for PUT where the sent entity IS the result.
  - Con: For PATCH, the `body` is a patch delta, not the full entity — returning it as `T` would be wrong.
- Option C: Return `default(T)!` on 204 (null for reference types) and document that 204 yields null.
  - Pro: Simple; callers can check for null.
  - Con: Breaks the non-nullable `Task<T>` contract.
- Option D: Change return type to `Task<T?>` for PutAsync/PatchAsync and return null on 204.
  - Pro: Type-safe; explicit.
  - Con: API change affecting `KeyedEntitySetClient`.

**Decision:** Option D — change `PutAsync`/`PatchAsync` in `ODataHttpClient` to return `Task<T?>`. Update `KeyedEntitySetClient` accordingly. This is the most honest representation of the contract. Callers who always get 200 from OhData server are unaffected since `T?` is assignable to `T`.

**Proposed fix:**
```csharp
internal async Task<T?> PutAsync<T>(string url, T body, CancellationToken ct)
    where T : class
{
    ...
    if (response.StatusCode == HttpStatusCode.NoContent) return null;
    return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct)
           ?? throw new InvalidOperationException($"PUT to '{url}' returned an empty body.");
}
```

---

### FINDING-3-3: `GetCountAsync` does not validate the response body is a parseable long [severity: minor]

**Current code:**
```csharp
internal async Task<long> GetCountAsync(string url, CancellationToken ct)
{
    ...
    var text = await response.Content.ReadAsStringAsync(ct);
    return long.Parse(text.Trim(), CultureInfo.InvariantCulture);
}
```

If the server returns a body that is not a valid long (e.g. an OData error as JSON accidentally slipping through `EnsureSuccessAsync`, or a server that returns `"42\n"` with extra whitespace), `long.Parse` throws a `FormatException` with no context about which URL was called. Should use `long.TryParse` with fallback error.

**Thought tree:**
- Option A: Use `long.TryParse` and throw `InvalidOperationException` with the URL on failure.
  - Pro: Better diagnostics; includes the actual body text and URL.
  - Con: Slightly more code.
- Option B: Leave as-is; `FormatException` is reasonable.
  - Pro: Less code.
  - Con: `FormatException` message gives no context about which endpoint returned the bad body.

**Decision:** Option A — wrap the parse in a try/catch and re-throw with context.

**Proposed fix:**
```csharp
if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
    throw new InvalidOperationException(
        $"GET '{url}' returned a non-numeric $count body: '{text.Trim()}'");
return count;
```

---

### FINDING-3-4: `ODataClientException` truncates error body at 500 chars — may hide useful info [severity: minor]

**Current code:**
```csharp
var raw = body is { Length: > 500 } ? body[..500] + "…" : body ?? "";
```

500 characters is arbitrary and may truncate important context in a long error body (e.g., a validation error with multiple field messages). 1000 characters would be a more practical limit, and the truncation should be made configurable.

**Thought tree:**
- Option A: Increase limit to 1000 chars.
  - Pro: Captures more context.
  - Con: Still arbitrary.
- Option B: Make the limit configurable via `OhDataClientOptions`.
  - Pro: Developer control.
  - Con: Adds complexity to options class.
- Option C: Keep 500 but also include the truncation count.
  - Pro: Lets the developer know how much was omitted.
  - Con: Minor improvement.

**Decision:** Option A — increase to 1000 for a better default. This is a minor ergonomic improvement.

---

## Changes made

1. **`ODataHttpClient.cs`** — Added static `_errorParseOptions` field. Changed `PutAsync`/`PatchAsync` return types to `Task<T?>` with 204 No Content handling. Added `long.TryParse` guard in `GetCountAsync`.

2. **`KeyedEntitySetClient.cs`** — Updated `PutAsync`/`PatchAsync` return types to `T?` to match.

3. **`ODataClientException.cs`** — Increased truncation limit from 500 to 1000 characters.
