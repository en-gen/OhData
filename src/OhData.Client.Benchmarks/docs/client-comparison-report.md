# OhData Client vs Microsoft.OData.Client — Performance Comparison Report

**Generated:** 2026-04-11  
**Framework:** BenchmarkDotNet v0.14.0, .NET 8.0.25, Windows 11 (10.0.26200.8037), X64 RyuJIT AVX2  
**Server:** OhData in-process TestHost (no real network; loopback latency eliminated)  
**Dataset:** 10 `BenchWidget` entities, 1 entity per key-lookup, 5 entities per filtered result

---

## Summary

| Scenario | OhData.Client | MS OData.Client | OhData advantage |
|---|---|---|---|
| GetAll (10 entities) | 291 µs | 611 µs | **2.1× faster** |
| Filter (Price > 5) | 709 µs | 1,053 µs | **1.5× faster** |
| GetByKey | 40 µs | 745 µs (via $filter) | **18.7× faster** ¹ |
| Top (first 5) | 444 µs | 725 µs | **1.6× faster** |
| Count ($count) | 251 µs | 842 µs | **3.4× faster** |
| Insert (POST) | 926 µs | N/A ² | — |

¹ OhData.Client uses direct key-lookup (`GET /Entities(id)`); MS OData.Client must fall back to `$filter` — see limitation section.  
² MS OData.Client Insert benchmark failed due to a benchmark harness issue; see notes below.

---

## Detailed Results

### OhData.Client (`ToListAsyncBenchmarks`)

Transport: `OhDataClient` → `HttpClient` → in-process `TestServer` → OhData minimal-API endpoints

```
| Method       | Mean      | Error     | StdDev     | Median    | Ratio | Gen0   | Gen1   | Allocated |
|------------- |----------:|----------:|-----------:|----------:|------:|-------:|-------:|----------:|
| GetAll       | 290.88 µs |  5.208 µs |  12.873 µs | 286.52 µs |  1.00 | 1.4648 | 0.4883 |  25.22 KB |
| FilterByName | 709.16 µs |  6.420 µs |   5.361 µs | 707.21 µs |  2.44 | 1.9531 | 0.9766 |  39.12 KB |
| GetByKey     |  39.88 µs |  0.782 µs |   0.836 µs |  39.96 µs |  0.14 | 0.4883 |      - |  11.87 KB |
| Top          | 444.32 µs |  3.665 µs |   3.428 µs | 444.70 µs |  1.53 | 0.9766 |      - |  26.27 KB |
| Count        | 250.82 µs |  1.964 µs |   1.837 µs | 250.44 µs |  0.86 | 0.9766 | 0.4883 |  21.50 KB |
| Insert       | 926.42 µs | 97.177 µs | 286.528 µs | 909.02 µs |  3.19 | 0.4883 |      - |  13.79 KB |
```

**Notes:**
- `GetByKey` is the fastest operation by far — it issues a single `GET /BenchWidgets(1)` with no query-string processing and receives a bare JSON object that the client deserialises directly.
- `FilterByName` and `Top` are slower than `GetAll` because they involve query-string construction and server-side LINQ evaluation before the response body is identical in size.
- `Insert` shows high variance (StdDev 286 µs) because the in-memory store grows across iterations, causing the median (909 µs) to diverge from mean — this is a benchmark-store artifact, not a client overhead.

### Microsoft.OData.Client 8.x (`MsODataClientBenchmarks`)

Transport: `DataServiceContext` → `BenchRequestMessage` adapter → `HttpClient` → in-process `TestServer` → OhData minimal-API endpoints

```
| Method             | Mean       | Error    | StdDev   | Median     | Ratio | Gen0    | Gen1   | Allocated  |
|------------------- |-----------:|---------:|---------:|-----------:|------:|--------:|-------:|-----------:|
| GetAll             |   610.9 µs | 12.04 µs | 26.68 µs |   603.4 µs |  1.00 | 15.6250 | 3.9063 | 285.65 KB  |
| FilterByName       | 1,053.4 µs | 25.30 µs | 71.36 µs | 1,025.4 µs |  1.73 | 15.6250 | 3.9063 | 255.53 KB  |
| GetByKey_ViaFilter |   744.7 µs |  8.97 µs |  9.60 µs |   743.3 µs |  1.22 |  9.7656 | 3.9063 | 167.22 KB  |
| Top                |   725.2 µs | 11.68 µs | 10.92 µs |   725.6 µs |  1.19 | 11.7188 | 3.9063 | 215.29 KB  |
| Count              |   842.0 µs | 16.34 µs | 18.82 µs |   843.2 µs |  1.38 | 15.6250 | 3.9063 | 299.74 KB  |
| Insert             |         NA |       NA |       NA |         NA |     — |      NA |     NA |         NA |
```

**Notes:**
- Memory allocation is **11× higher** on average than OhData.Client across equivalent scenarios. MS OData.Client carries a full OData protocol stack: JSON-OData reader, entity materialiser, EDM-type resolution, change tracker, and response envelope parsing.
- Gen0/Gen1 GC pressure is consistently high, reflecting the protocol stack's object graph per response.
- `Insert` failed — see below.

---

## Side-by-Side Comparison

| Scenario | OhData.Client Mean | OhData.Client Alloc | MS OData.Client Mean | MS OData.Client Alloc | Speedup | Alloc saving |
|---|---|---|---|---|---|---|
| GetAll | 291 µs | 25 KB | 611 µs | 286 KB | 2.1× | 91% less |
| FilterByName | 709 µs | 39 KB | 1,053 µs | 256 KB | 1.5× | 85% less |
| GetByKey | 40 µs | 12 KB | 745 µs (via $filter) | 167 KB | 18.7× | 93% less |
| Top | 444 µs | 26 KB | 725 µs | 215 KB | 1.6× | 88% less |
| Count | 251 µs | 22 KB | 842 µs | 300 KB | 3.4× | 93% less |

---

## Limitations and Known Issues

### 1. Single-entity GET (GetByKey) — MS OData.Client limitation

OhData's `GetById` endpoint returns a bare JSON object:

```json
{"id":1,"name":"Sprocket","price":4.99}
```

Microsoft.OData.Client's key-lookup path (`context.ExecuteAsync<T>(uri, "GET", singleResult: true)`)
requires the response to include `@odata.context` — an OData 4.0 metadata annotation:

```json
{
  "@odata.context": "http://host/odata/$metadata#Widgets/$entity",
  "Id": 1,
  "Name": "Sprocket",
  "Price": 4.99
}
```

Without it the client throws a materialisation exception. The `GetByKey_ViaFilter` benchmark
works around this by sending `GET /MsBenchWidgets?$filter=Id eq 1` instead — a full collection
request that returns the OData envelope. This adds significant overhead compared to OhData.Client's
direct 40 µs key-lookup.

### 2. PascalCase requirement

MS OData.Client 8.x requires property names to match the OData 4.0 specification's PascalCase convention.
ASP.NET Core's default JSON serialiser uses camelCase (`{"id":1,"name":"..."}`), which the MS client cannot deserialise.

To use MS OData.Client against an OhData server you must override the naming policy:

```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = null);
```

This means the same server **cannot simultaneously serve both clients** with their default configurations unless you write a content-negotiation layer or accept the PascalCase override globally.

### 3. MS OData.Client Insert — benchmark failed

The `MsODataClientBenchmarks.Insert` benchmark exited with an error code during the BenchmarkDotNet
out-of-process run. The root cause is a combination of:

- `DataServiceContext.AddObject` registers entities in an internal change tracker. In the warm-up phase
  BenchmarkDotNet calls the method many times in rapid succession, and because `DataServiceContext`
  is shared across iterations, the context accumulates hundreds of tracked (but not yet saved) entities.
- The long warm-up sequence inflated context state until the process was killed (possibly by Windows
  Defender, as BenchmarkDotNet itself noted in the warning output).

The fixed implementation in `MsODataClientBenchmarks.cs` creates a fresh `DataServiceContext` per
iteration to avoid accumulation. Re-running the benchmark after that fix is recommended. The trade-off
is that the fixed Insert benchmark also includes the context-construction overhead (which itself involves
`ODataConventionModelBuilder.GetEdmModel()`), so the number will be higher than a real-world scenario
where a long-lived context is reused for reads and the context is only created once at startup.

### 4. `[Key]` attribute requirement

MS OData.Client 8.x uses `[Key("PropertyName")]` (from `Microsoft.OData.Client`) to identify the entity
key. The older `DataServiceKey` attribute is not supported. Entity types must be `public` so the client
can instantiate them via reflection from a different assembly.

---

## Internal OhData.Client Micro-benchmarks

These measure client-side overhead only (no HTTP round-trip).

### URL construction (`BuildCollectionUrlBenchmarks`)

```
| Method     | Mean       | Error     | StdDev    | Allocated |
|----------- |-----------:|----------:|----------:|----------:|
| NoOptions  |   1.494 ns | 0.0773 ns | 0.0860 ns |         - |
| FilterOnly |  67.320 ns | 1.3677 ns | 2.8549 ns |     320 B |
| AllOptions | 339.158 ns | 2.1089 ns | 2.3441 ns |   1,432 B |
```

Building a URL with all 7 query options (filter, select, orderby, top, skip, expand, count) takes
339 ns and allocates 1.4 KB. This is negligible compared to the 291+ µs HTTP round-trip.

### LINQ filter translation (`FilterTranslatorBenchmarks`)

```
| Method                | Mean          | Error      | Allocated |
|---------------------- |--------------:|-----------:|----------:|
| Simple                |      71 ns    |   1.3 ns   |     216 B |
| Medium                |     163 ns    |   3.3 ns   |     544 B |
| Complex               |     267 ns    |   2.0 ns   |     840 B |
| WithCapturedVariables | 156,243 ns    | 668.8 ns   |   9,171 B |
```

Simple and medium LINQ predicates translate in under 300 ns. The `WithCapturedVariables` case is
156 µs because evaluating closed-over variables requires `Expression.Compile()` to materialise
the constant values — this is a one-time cost when the expression is built at call site, not a
repeated per-request cost in typical usage.

---

## Conclusions

**Choose OhData.Client when:**
- You want minimum latency and memory overhead for CRUD operations
- Your payloads are JSON-only and you control both client and server
- You use the lightweight LINQ filter DSL (`Filter(x => x.Price > 5)`)
- You need direct key-lookup performance (~40 µs vs ~745 µs)

**Choose Microsoft.OData.Client when:**
- You need full OData 4.0 protocol compliance (e.g., interoperability with third-party services)
- You require LINQ-to-OData query composition via the LINQ provider (`context.CreateQuery<T>().Where(...)`)
- Your organisation mandates the official client for governance/support reasons
- You can accept 2–19× higher latency and ~11× higher memory allocation per operation

**Architecture note:** OhData.Client is a thin wrapper (~12–39 KB per operation) that treats the
OhData server as a REST+JSON API. MS OData.Client is a full OData stack (~167–300 KB per operation)
that parses the OData JSON wire format, resolves EDM types, and maintains a change-tracking graph.
The performance difference reflects that architectural trade-off, not implementation quality.
