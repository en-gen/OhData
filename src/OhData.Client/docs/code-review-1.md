# Code Review 1 — Correctness & Edge Cases

## Summary

Examined null inputs, empty collections, edge key types, concurrent use, CancellationToken propagation, and `ToPageAsync` behaviour when the server omits `@odata.count`. Overall the library handles most cases well. Several correctness gaps were found around null key values, negative Top/Skip, string key URL-encoding in the path, and `GetSingleAsync` returning null without checking for other non-2xx codes besides 404.

## Findings

### FINDING-1-1: Null key value passed to `Key()` silently produces wrong URL [severity: major]

**Current code:**
```csharp
// ODataKeyFormatter.cs
public static string Format(object key) => key switch
{
    string s          => $"'{s.Replace("'", "''")}'",
    ...
    _ => string.Format(CultureInfo.InvariantCulture, "{0}", key),
};
```

When `Key(null)` is called, `key` matches `null` but the switch does not have a `null` arm — it falls through to `_` which calls `string.Format` on `null`, producing an empty string. The URL becomes `Widgets()` which is invalid.

**Thought tree:**
- Option A: Add `null => throw new ArgumentNullException(nameof(key))` as first arm.
  - Pro: Fails fast with a clear message instead of silently producing a malformed URL.
  - Con: None; key can never be null in a valid OData request.
- Option B: Return `"null"` for null keys.
  - Pro: Consistent with OData literal format.
  - Con: `null` is not a valid key literal in OData 4.0; it will cause a 400 on the server.
- Option C: Let it fail at the HTTP layer.
  - Pro: Less code.
  - Con: The error message will be confusing (400 from server vs clear ArgumentNullException).

**Decision:** Option A — throw `ArgumentNullException` immediately so the developer gets a helpful error.

**Proposed fix:**
```csharp
public static string Format(object key)
{
    if (key is null) throw new ArgumentNullException(nameof(key), "OData key value must not be null.");
    return key switch
    {
        string s          => $"'{s.Replace("'", "''")}'",
        ...
    };
}
```

---

### FINDING-1-2: String keys with special characters in URL path need URL-encoding [severity: critical]

**Current code:**
```csharp
// KeyedEntitySetClient.cs
private string Url => $"{_entitySetName}({_formattedKey})";
```

`ODataKeyFormatter.Format` for a string key produces `'foo/bar'`. The formatted key is inserted verbatim into the URL template. The forward slash in the quoted value is a real slash in the URL path, causing the HTTP client to route to a different path segment. Per OData spec, the entire key literal inside parentheses must be URL-percent-encoded when used in a URL path. For example `Widgets('foo%2Fbar')`.

**Thought tree:**
- Option A: URL-encode the entire formatted key before embedding it in the URL.
  - Pro: Correct for strings with `/`, `?`, `#`, `%`, space, and other reserved chars.
  - Con: Over-encodes guid and integer keys (no harm, but unnecessary).
- Option B: Only encode for string-type keys.
  - Pro: Minimal encoding, keeps URLs readable for int/guid keys.
  - Con: Requires type dispatch; misses edge cases for new key types.
- Option C: Encode only the actual string value before quoting it in OData format, not the surrounding quotes.
  - Pro: Preserves OData quoting semantics; only the value chars are encoded.
  - Con: `ODataKeyFormatter` would need to know whether to URL-encode, mixing concerns.

**Decision:** Option A — URL-encode the entire formatted key in `ODataKeyFormatter.Format`. Integers and GUIDs only contain `[0-9a-f-]` which are not percent-encoded, so there is no visible difference. The OData spec (section 4.3.1) requires percent-encoding of the key predicate in a URL.

**Proposed fix:**
```csharp
// In ODataKeyFormatter.Format — wrap the switch result:
public static string Format(object key)
{
    if (key is null) throw new ArgumentNullException(nameof(key));
    var literal = key switch { ... };
    return Uri.EscapeDataString(literal);
}
```

However, this will double-encode single-quotes since `'` is encoded as `%27`. OData parsers expect the surrounding single-quotes to be percent-encoded too — `%27foo%27` is the correct URL-safe form. This is correct per RFC 3986 and the OData spec.

---

### FINDING-1-3: Negative or zero `Top`/`Skip` values accepted silently [severity: minor]

**Current code:**
```csharp
public EntitySetClient<T> Top(int count)  => With(_state with { Top  = count });
public EntitySetClient<T> Skip(int count) => With(_state with { Skip = count });
```

`Top(-1)` or `Skip(-5)` produce `$top=-1` / `$skip=-5` in the URL, which OData servers must reject with a 400. The client should guard against this.

**Thought tree:**
- Option A: Throw `ArgumentOutOfRangeException` for values < 0 (Top) or < 0 (Skip).
  - Pro: Fails fast at the builder call site with a clear message.
  - Con: None.
- Option B: Silently clamp to 0.
  - Pro: Tolerant.
  - Con: Hides bugs; clamping is surprising.
- Option C: Allow them through and let the server reject.
  - Pro: No code change.
  - Con: Confusing server error, hard to trace back to the builder.

**Decision:** Option A — guard at entry point. `Top(0)` is technically valid (returns empty), `Skip(0)` is valid (same as no skip). Guard `< 0` only.

**Proposed fix:**
```csharp
public EntitySetClient<T> Top(int count)
{
    if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "$top must be >= 0.");
    return With(_state with { Top = count });
}

public EntitySetClient<T> Skip(int count)
{
    if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "$skip must be >= 0.");
    return With(_state with { Skip = count });
}
```

---

### FINDING-1-4: `GetSingleAsync` swallows all non-404 errors after 404 check [severity: major]

**Current code:**
```csharp
internal async Task<T?> GetSingleAsync<T>(string url, CancellationToken ct)
    where T : class
{
    using var response = await _http.GetAsync(url, ct);
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
    await EnsureSuccessAsync(response, url, ct);
    return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct);
}
```

This code is actually correct — 404 returns null, other non-2xx status codes hit `EnsureSuccessAsync` which throws. This is fine. However there is a subtle issue: if the response body for a 200 response is null (empty 200 body), `ReadFromJsonAsync` returns `null`, which is returned silently instead of throwing. For a single-entity GET, an empty 200 body is a server error.

**Thought tree:**
- Option A: Null-check the deserialized result and throw `InvalidOperationException`.
  - Pro: Makes the error actionable; matches the POST/PUT/PATCH pattern.
  - Con: Adds a null check.
- Option B: Leave as-is.
  - Pro: No code change.
  - Con: Caller gets `null` and may think the entity was not found when actually the server sent a malformed 200.

**Decision:** Option A — add the null guard consistent with POST/PUT/PATCH methods.

**Proposed fix:**
```csharp
return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct)
       ?? throw new InvalidOperationException($"GET '{url}' returned HTTP 200 with an empty body.");
```

However, `GetSingleAsync` is typed as `Task<T?>` — returning null is expected for 404. The fix should only apply when the status is NOT 404 and the deserialized result is null. The current structure already handles this correctly with the 404 check before `EnsureSuccessAsync`. So the null body case is: status 200, body is empty → `ReadFromJsonAsync` returns null → we return null. Adding a null guard here makes sense.

---

### FINDING-1-5: `ToPageAsync` does not force `$count=true` in the URL [severity: major]

**Current code:**
```csharp
public Task<ODataPage<T>> ToPageAsync(CancellationToken ct = default)
    => _http.GetPageAsync<T>(With(_state with { WithCount = true }).BuildCollectionUrl(), ct);
```

This looks correct — `WithCount = true` is forced before building the URL. However, if the caller also called `IncludeCount()` first, `WithCount` is already true and the `with { WithCount = true }` is redundant but harmless. This is actually fine. No fix needed here, but the forced-true is a defensive safeguard worth retaining.

---

## Changes made

After analysis the following fixes were implemented:

1. **`ODataKeyFormatter.cs`** — Added null guard (throws `ArgumentNullException`) and `Uri.EscapeDataString` around the formatted literal so string keys with special characters (`/`, `?`, space) are correctly percent-encoded in the URL path.

2. **`EntitySetClient.cs`** — Added `ArgumentOutOfRangeException` guards to `Top()` and `Skip()` for negative values.

3. **`ODataHttpClient.cs`** — Added null-body guard on `GetSingleAsync` when status is 200 but the body deserializes to null, throwing `InvalidOperationException` with a clear message.
