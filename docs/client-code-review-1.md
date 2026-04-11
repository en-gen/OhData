# Deep Code Review — OhData.Client

Date: 2026-04-11

---

## HIGH

### C2 — `DynamicInvoke` for captured variables: reflection overhead and exception wrapping

**File:** `src/OhData.Client/Internal/FilterTranslator.cs` line ~103

`FilterTranslator.VisitMember` evaluates captured outer-scope variables by compiling a lambda and invoking it via `DynamicInvoke`:

```csharp
var value = Expression.Lambda(node).Compile().DynamicInvoke();
```

`DynamicInvoke` is roughly 100x slower than a direct delegate call because it uses late-bound reflection. It also wraps any exception thrown by the compiled delegate in a `TargetInvocationException`, making debugging harder when a captured expression itself raises.

> **Resolved:** Replaced with a typed `Func<object?>` lambda that is compiled and immediately invoked:
> ```csharp
> var value = Expression.Lambda<Func<object?>>(Expression.Convert(node, typeof(object))).Compile()();
> ```
> `Expression.Convert` boxes the result to `object` so the typed delegate always succeeds regardless of the captured value's type. Direct invocation — no reflection overhead, no exception wrapping.
> — `src/OhData.Client/Internal/FilterTranslator.cs` — confidence: high.

---

## MEDIUM

### C3 — Pluralization rules undocumented; no unit tests

**File:** `src/OhData.Client/Internal/EntitySetNameConvention.cs`

`Pluralize` implements four rules (consonant+y → ies, vowel+y → s, sibilant endings → es, default → s) but had no XML documentation and no dedicated test class. Users have no way to know when to apply `[ODataEntitySet("Name")]` without reading the source.

> **Resolved:** Added full XML `<summary>` doc comment on `Pluralize` listing all four rules with examples and an explicit call-out that irregular nouns must use `[ODataEntitySet]`.
> Added `EntitySetNameConventionTests.cs` with 14 tests covering all rule branches (including `Box/Boxes`, `Match/Matches`, `Category/Categories`, `Key/Keys`, `Status/Statuses`) and verifying that the attribute takes precedence over convention.
> — `src/OhData.Client/Internal/EntitySetNameConvention.cs`, `src/OhData.Client.Tests/EntitySetNameConventionTests.cs` — confidence: high.

### C4 — `ODataClientException` crashes on `null` response Content

**File:** `src/OhData.Client/ODataClientException.cs` lines 48–49

`FromResponseAsync` calls `response.Content.ReadAsStringAsync(ct)` unconditionally. `HttpResponseMessage.Content` is typed `HttpContent` (non-nullable in the BCL declaration) but can be null in practice with certain `HttpMessageHandler` mocks and older middleware. A `NullReferenceException` here would swallow the original HTTP error and produce a confusing secondary failure.

> **Resolved:** Added a `if (response.Content is not null)` guard inside the existing `try` block before calling `ReadAsStringAsync`. When `Content` is null the `body` variable stays null and the fallback empty-string error path is used — the correct HTTP status code is still reported.
> — `src/OhData.Client/ODataClientException.cs` — confidence: high.

### C5 — `Select` primary overload accepts only anonymous-type expressions; no multi-property typed overload

**File:** `src/OhData.Client/EntitySetClient.cs`

The existing `Select(Expression<Func<T, object?>> selector)` routes to `SelectTranslator` which supports anonymous types (`x => new { x.Id, x.Name }`) but accepts any expression — including arbitrary projections that have no OData equivalent. There was no typed multi-property overload for the common case of selecting named members individually (e.g. `Select(x => x.Id, x => x.Name)`).

> **Resolved:** Added `Select(params Expression<Func<T, object?>>[] properties)` that validates each expression is a **direct** (non-chained) member access on `T`. Navigation paths such as `x => x.Category.Name` throw `ArgumentException("$select does not support navigation paths; use Expand for navigation properties.")`. The existing single-expression anonymous-type overload is unchanged — C# overload resolution prefers the non-params overload for single-argument calls, so existing code is unaffected.
> Added `ExtractDirectMember` private helper (shared with C7) that strips boxing `Convert` wrappers and enforces the direct-member invariant.
> — `src/OhData.Client/EntitySetClient.cs` — confidence: high.

### C6 — No way to retrieve inline `@odata.count` alongside results

**File:** `src/OhData.Client/EntitySetClient.cs`, `src/OhData.Client/Internal/ODataHttpClient.cs`

`ODataCollectionResponse<T>` already deserialises `@odata.count` into its `Count` property, but there was no public API to request `$count=true` and retrieve the count alongside the items. The only workaround was two separate HTTP calls — `ToListAsync` and `CountAsync` — which is a TOCTOU race and doubles latency.

> **Resolved:**
> - Added `ODataPage<T>` public type (`Items: IReadOnlyList<T>`, `TotalCount: long?`).
> - Added `WithCount` flag to `QueryState`; `BuildCollectionUrl()` appends `$count=true` when set.
> - Added `IncludeCount()` fluent method that sets the flag (immutable, returns new instance).
> - Added `ToPageAsync(CancellationToken)` terminal operation that forces `$count=true` and returns `ODataPage<T>`.
> - Added `GetPageAsync<T>` to `ODataHttpClient` for the envelope deserialization.
> — `src/OhData.Client/ODataPage.cs` (new), `src/OhData.Client/EntitySetClient.cs`, `src/OhData.Client/Internal/ODataHttpClient.cs` — confidence: high.

### C7 — `Expand` primary overload is single-property only; no multi-property typed overload

**File:** `src/OhData.Client/EntitySetClient.cs`

`Expand(Expression<Func<T, object?>> navProperty)` only accepts a single navigation property. Expanding multiple properties required the magic-string overload `Expand("Category,Supplier")`.

> **Resolved:** Added `Expand(params Expression<Func<T, object?>>[] navProperties)` that validates each expression is a direct member access (same `ExtractDirectMember` helper as C5). Navigation chains such as `x => x.Category.Name` throw `ArgumentException("$expand does not support nested expansion; use the string overload for complex $expand syntax.")`. The string overload is retained as the escape hatch for complex nested OData `$expand` syntax (e.g. `Category($select=Name)`).
> — `src/OhData.Client/EntitySetClient.cs` — confidence: high.

---

## LOW

### C1 — GUID formatting in `FormatLiteral` does not add quotes — non-issue

**File:** `src/OhData.Client/Internal/FilterTranslator.cs`

`FormatLiteral` formats a `Guid` as `g.ToString()` (no surrounding quotes). OData 4.0 specifies that GUIDs are represented without quotes in `$filter` expressions (e.g. `Id eq 12345678-1234-1234-1234-123456789012`), so the current implementation is correct per the spec.

> **Not a defect.** No change needed.

### C8 — Extra parentheses in `not` filter output

**File:** `src/OhData.Client/Internal/FilterTranslator.cs`

`VisitUnary` for `ExpressionType.Not` emits `not (IsActive)` with parentheses around the operand. OData 4.0 does not require these parentheses for simple property references, but they are valid and cause no interoperability issues with any known OData server.

> **Cosmetic.** No change made — the parentheses are harmless and make the output slightly more readable for complex `not` expressions.

### C9 — `Expand(string rawExpand)` signature comment says "navigation properties by name" but allows arbitrary syntax

**File:** `src/OhData.Client/EntitySetClient.cs`

The doc comment says "Expands navigation properties by name" but the parameter is intentionally a raw OData `$expand` string that can contain complex expressions like `Category($select=Name;$expand=Parent)`.

> **Cosmetic documentation gap.** Updated as part of C7 — the revised doc comment for the string overload explicitly notes it accepts complex nested OData syntax.

---

## Summary of changes

| Item | Files changed | New tests |
|---|---|---|
| C2 — Eliminate DynamicInvoke | `FilterTranslator.cs` | 0 (existing captured-variable tests cover it) |
| C3 — Pluralization doc + tests | `EntitySetNameConvention.cs` | 14 |
| C4 — null Content guard | `ODataClientException.cs` | 0 (defensive guard, no observable behaviour change) |
| C5 — Select params expression overload | `EntitySetClient.cs` | 3 |
| C6 — IncludeCount / ToPageAsync | `EntitySetClient.cs`, `ODataHttpClient.cs`, `ODataPage.cs` (new) | 6 |
| C7 — Expand params expression overload | `EntitySetClient.cs` | 2 |

Total: 57 original tests + 25 new = **82 tests, all passing**.
