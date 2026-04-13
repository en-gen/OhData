# OhData Deep Review

Code review of OhData.AspNetCore (server) and OhData.Client projects. Findings are categorized by severity: **H** = high, **M** = medium, **L** = low.

Resolved items have been closed and removed. This file contains only open findings.

---

## Server: OhData.AspNetCore

### OData Spec Compliance

#### H-1: `$count` on `GetODataQueryable` returns post-page count, not total

`OhDataEndpointFactory.cs:588` -- When using the `IODataEntitySetEndpointSource` path (Priority 1), `$count=true` returns `items.Length` after the profile has already applied `$top/$skip`. OData **11.2.6.5** specifies that `@odata.count` represents the total matching entities before server-driven paging. The comment acknowledges this but the behavior is incorrect for any profile that applies `$top` inside `GetODataQueryable`.

**Suggested fix:** Accept an optional `long? totalCount` return from the profile, or require profiles that apply their own paging to supply the pre-page count.

#### M-1: `$expand` is N+1 per entity per navigation property

`OhDataEndpointFactory.cs:480-496` -- `ApplyExpandAsync` iterates every item and calls `navRoute.Handler(keyVal, ct)` individually. For 100 items with 2 expanded properties, this is 200 sequential async calls. OData **11.2.4.2** allows `$expand`, and users will reasonably expect it to perform well.

**Suggested fix:** Consider a batch-aware expand callback signature (accepting `IEnumerable<TKey>` and returning a dictionary), or document the performance characteristics clearly.

#### M-2: `GET $ref` endpoints return empty/minimal envelopes

`OhDataEndpointFactory.cs:1227-1244` -- The `GET /{EntitySet}({key})/{nav}/$ref` route returns an envelope without actual `@odata.id` values because the related entity's key property is not available. This makes the endpoint non-compliant with OData **11.4.6.1** which expects reference links.

**Suggested fix:** Thread the navigation target's key property name through `NavigationRouteDefinition` so `$ref` can build actual `@odata.id` URLs.

---

### Public API Design

#### L-6: `BindFunction`/`BindAction` name comes from delegate method name

When users pass lambdas, the function name is the compiler-generated closure method name (e.g. `<.ctor>b__0`). The workaround (use named methods) should be documented or validated at registration time.

#### L-8: Pluralization is naive

`PluralizationHelper.Pluralize` handles common English patterns but fails on irregulars: `Person` -> `Persons` (not `People`), `Child` -> `Childs`. The escape hatch (`EntitySetName = "..."`) exists but this should be documented prominently.

---

## Client: OhData.Client

### API Design

#### M-8: No ETag/concurrency support on the client

`KeyedEntitySetClient<T>` exposes `GetWithETagAsync` to retrieve the ETag from GET responses, but there is no way to set `If-None-Match` headers. The server supports ETags for optimistic concurrency; the client can send `If-Match` on writes (PUT/PATCH/DELETE) via the `ifMatch` parameter, and `GetWithETagAsync` returns the ETag alongside the entity.

**Remaining gap:** `If-None-Match` support for conditional GETs.

---

### Performance and Memory

#### M-9: `Expression.Compile()` on every captured variable in filter translation

`FilterTranslator.cs:105` -- When a filter expression references a captured variable (e.g. `int minPrice = 10; .Filter(x => x.Price > minPrice)`), the translator evaluates it by compiling a fresh lambda: `Expression.Lambda<Func<object?>>(...).Compile()()`. This allocates a new delegate per variable per query with no caching.

**Suggested fix:** Use `Expression.Lambda(...).Compile(preferInterpretation: true)()` for evaluation (uses the interpreter, cheaper than JIT compilation), or evaluate via the expression interpreter directly.

#### L-12: No `HttpCompletionOption.ResponseHeadersRead`

`ODataHttpClient` uses default `HttpCompletionOption.ResponseContentRead` for all GET requests, buffering the entire response body before deserialization begins. For large collections, streaming would reduce peak memory.

---

## Summary

| Sev | ID | Area | Finding |
|-----|------|------|---------|
| H | H-1 | Server/Spec | `$count` on `GetODataQueryable` returns post-page count |
| M | M-1 | Server/Spec | `$expand` is N+1 |
| M | M-2 | Server/Spec | `GET $ref` returns empty envelopes |
| M | M-8 | Client/API | `If-None-Match` not supported |
| M | M-9 | Client/Perf | `Expression.Compile()` per captured variable |
| L | L-6 | Server/API | `BindFunction` lambda naming |
| L | L-8 | Server/API | Naive pluralization |
| L | L-12 | Client/Perf | No streaming HTTP reads |
