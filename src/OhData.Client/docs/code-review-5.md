# Code Review 5 — Performance & Allocation Efficiency

## Summary

Examined `Uri.EscapeDataString` overhead, string concatenation patterns in URL building, async/await overhead, dispose patterns on `HttpResponseMessage`, boxing/unboxing in key formatting, and `List<string>` allocation in `BuildCollectionUrl`. The library is generally well-structured for performance, but several improvements were identified: the `QueryState` record allocates a new instance on every builder call even when the state is unchanged, `BuildCollectionUrl` allocates a `List<string>(6)` even when there are no query params, `FilterTranslator` compiles captured-variable closures which is expensive, and the `EntitySetNameConvention.Resolve` calls `GetCustomAttribute` via reflection on every `For<T>()` call without caching.

## Findings

### FINDING-5-1: `EntitySetNameConvention.Resolve` calls reflection on every `For<T>()` [severity: major]

**Current code:**
```csharp
public static string Resolve(Type entityType)
{
    var attr = entityType.GetCustomAttribute<ODataEntitySetAttribute>();
    if (attr is not null) return attr.Name;
    return Pluralize(entityType.Name);
}
```

`GetCustomAttribute<T>()` scans the type's custom attributes via reflection on every call. When `For<T>()` is called in a tight loop (e.g., in a background sync job that calls `For<Widget>().ToListAsync()` 1000 times per minute), this adds unnecessary overhead. The result is deterministic and can be cached per-type.

**Thought tree:**
- Option A: Add a `ConcurrentDictionary<Type, string>` cache in `EntitySetNameConvention`.
  - Pro: O(1) amortized lookup after first call; thread-safe; no allocations after warm-up.
  - Con: Tiny memory overhead per entity type (acceptable).
- Option B: Use a generic static field `EntitySetNameCache<T>.Name` pattern.
  - Pro: Zero dictionary overhead; JIT-inlined after warm-up.
  - Con: Requires generic type parameter, which requires the call site to be generic.
- Option C: Leave as-is; reflection is not a hot path.
  - Pro: Simpler.
  - Con: Reflection is measurably slower; the cache is trivial to add.

**Decision:** Option A — `ConcurrentDictionary<Type, string>` cache.

**Proposed fix:**
```csharp
private static readonly ConcurrentDictionary<Type, string> _cache = new();

public static string Resolve(Type entityType)
    => _cache.GetOrAdd(entityType, static t =>
    {
        var attr = t.GetCustomAttribute<ODataEntitySetAttribute>();
        return attr is not null ? attr.Name : Pluralize(t.Name);
    });
```

---

### FINDING-5-2: `BuildCollectionUrl` always allocates `List<string>(6)` [severity: minor]

**Current code:**
```csharp
internal string BuildCollectionUrl()
{
    var parts = new List<string>(6);
    if (_state.Filter  is not null) parts.Add($"$filter={Uri.EscapeDataString(_state.Filter)}");
    ...
    return parts.Count == 0
        ? _entitySetName
        : $"{_entitySetName}?{string.Join('&', parts)}";
}
```

Even for the common case of no query options (just `For<T>().ToListAsync()`), the method allocates a `List<string>` and then immediately discards it when returning `_entitySetName`. This is a micro-allocation on every no-options collection GET.

**Thought tree:**
- Option A: Check if all state fields are null/false before allocating the list.
  - Pro: Zero allocation for the no-options case.
  - Con: Adds a guard check at the top.
- Option B: Use `StringBuilder` directly to avoid `List<string>` overhead.
  - Pro: Single allocation; slightly faster concatenation.
  - Con: Code becomes more complex with separator handling.
- Option C: Cache the URL when state is immutable (which it is — `QueryState` is a record).
  - Pro: Zero allocation after first call per state instance.
  - Con: Adds a mutable field to `EntitySetClient` (breaking immutability of the builder itself, not of the URL result). The URL string itself is already cheap to recompute.

**Decision:** Option A — add a short-circuit check. The immutable builder pattern means the check adds no correctness risk.

**Proposed fix:**
```csharp
internal string BuildCollectionUrl()
{
    // Fast path: no query options set.
    if (_state.Filter  is null &&
        _state.Select  is null &&
        _state.OrderBy is null &&
        _state.Expand  is null &&
        !_state.Top.HasValue   &&
        !_state.Skip.HasValue  &&
        !_state.WithCount)
        return _entitySetName;

    var parts = new List<string>(6);
    ...
}
```

---

### FINDING-5-3: `FilterTranslator` compiles closures on every execution for captured variables [severity: minor]

**Current code:**
```csharp
protected override Expression VisitMember(MemberExpression node)
{
    if (TryGetPropertyPath(node, out var path))
    {
        _sb.Append(path);
    }
    else
    {
        // Captured variable or outer-scope field — evaluate at translation time
        var value = Expression.Lambda<Func<object?>>(Expression.Convert(node, typeof(object))).Compile()();
        _sb.Append(FormatLiteral(value));
    }
    return node;
}
```

`Expression.Lambda(...).Compile()` is relatively expensive (it JIT-compiles a method body at runtime). This is called for every captured variable in every filter expression. For a filter like `x => x.Price > minPrice && x.Name.StartsWith(prefix)`, two closures are compiled per translation call.

In practice, expression translation is not on a hot path in a client library (it is dominated by network I/O). However, the closure approach has an alternative: since a `MemberExpression` on a captured variable refers to a field or property on a closure object, we can use `member.Expression` evaluation and `member.Member` reflection to get the value without `Compile()`.

**Thought tree:**
- Option A: Use `Expression.Lambda(...).Compile()()` — current approach.
  - Pro: Works for any expression, including nested closures.
  - Con: JIT compilation overhead per call.
- Option B: Use `GetValue(member.Expression.Compile()())` — evaluate the enclosing object first.
  - Pro: Avoids creating a new delegate; reuses the member accessor.
  - Con: Still calls `Compile()` on the outer expression; no net improvement.
- Option C: Use a cached `GetValue` via `FieldInfo`/`PropertyInfo` reflection.
  - Pro: Can be faster for repeated calls with the same closure variable.
  - Con: Complexity; reflection `GetValue` is also slow compared to compiled code.
- Option D: Leave as-is; this is not a hot path in a client library.
  - Pro: Simplicity.
  - Con: Minor overhead for complex expressions.

**Decision:** Option D — leave as-is. The compilation is O(1) per distinct captured variable per translation call, and translation is dominated by I/O in real usage. Document the behaviour.

---

### FINDING-5-4: `OhDataClient` disposes `HttpClient` even in the external-client constructor branch [severity: critical]

**Current code:**
```csharp
public void Dispose()
{
    if (_ownsHttpClient) _httpClient.Dispose();
}
```

This looks correct at first glance. However, the `_http` (`ODataHttpClient`) field is never disposed. `ODataHttpClient` wraps an `HttpClient` and has no disposable resources itself — so this is fine. But `OhDataClient` stores both `_httpClient` and `_http`, and if `_http` ever gains disposable state in the future, it would be leaked. Consider adding a guard.

Actually, examining the code more carefully: `ODataHttpClient` does not implement `IDisposable` and holds no I/O resources beyond the reference to `HttpClient`. No fix is needed. This finding is informational.

---

### FINDING-5-5: `string.Join('&', parts)` in `BuildCollectionUrl` allocates an intermediate array for `List<string>` [severity: minor]

**Current code:**
```csharp
$"{_entitySetName}?{string.Join('&', parts)}"
```

`string.Join(char, IEnumerable<string>)` internally enumerates the list. Since `parts` is already a `List<string>`, using `string.Join(char, List<string>)` calls the `IEnumerable` overload which is optimized in .NET 8 for `List<T>`. No change needed. This is a non-issue in .NET 8+.

---

## Changes made

1. **`EntitySetNameConvention.cs`** — Added `ConcurrentDictionary<Type, string>` cache to avoid repeated reflection lookups. Added `using System.Collections.Concurrent`.

2. **`EntitySetClient.cs`** — Added fast-path early return in `BuildCollectionUrl()` when all state fields are default (no query options set).
