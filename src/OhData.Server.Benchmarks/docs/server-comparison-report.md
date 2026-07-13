# OhData vs Microsoft.AspNetCore.OData — Server Pipeline Benchmark

**Generated:** 2026-07-13
**Project:** `src/OhData.Server.Benchmarks`

**Environment:** BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8655/25H2), AMD Ryzen 9 5950X,
.NET SDK 10.0.301, .NET 10.0.9 X64 RyuJIT x86-64-v3
**Packages:** Microsoft.AspNetCore.OData 9.4.x (same floating range OhData.AspNetCore references),
OhData.AspNetCore from source at this commit

## Summary

OhData's minimal-API pipeline is faster and allocates less than Microsoft.AspNetCore.OData's
ODataController + `[EnableQuery]` pipeline in **all 11** scenarios:

| Scenario | OhData Mean | OhData Alloc | MS OData Mean | MS OData Alloc | Speedup | Alloc ratio |
|---|---:|---:|---:|---:|---:|---:|
| GetAll page (100) | 763 µs | 124 KB | 2,821 µs | 781 KB | **3.7×** | 6.3× |
| $filter | 1,778 µs | 138 KB | 3,393 µs | 824 KB | **1.9×** | 6.0× |
| $orderby | 968 µs | 155 KB | 2,949 µs | 837 KB | **3.0×** | 5.4× |
| $select | 878 µs | 253 KB | 1,858 µs | 339 KB | **2.1×** | 1.3× |
| $top + $skip | 1,262 µs | 103 KB | 2,061 µs | 472 KB | **1.6×** | 4.6× |
| $count=true (+$filter) | 2,831 µs | 157 KB | 4,740 µs | 842 KB | **1.7×** | 5.4× |
| GetById | 37 µs | 16 KB | 112 µs | 48 KB | **3.0×** | 3.0× |
| POST | 51 µs | 19 KB | 286 µs | 144 KB | **5.6×** | 7.7× |
| PUT | 57 µs | 19 KB | 281 µs | 148 KB | **4.9×** | 7.7× |
| PATCH | 53 µs | 19 KB | 325 µs | 137 KB | **6.2×** | 7.1× |
| DELETE | 16 µs | 11 KB | 24 µs | 14 KB | **1.5×** | 1.3× |

> **Re-measured 2026-07-13** after removing a stray `[Authorize]` attribute from
> `BenchWidgetsController` that broke the MS OData host's write endpoints in the committed
> code (the originally published figures predated the attribute and were not reproducible
> from the repo). Direction and rough magnitude are unchanged; the figures above are from
> the current committed state and reproduce via the commands at the bottom of this page.

The biggest deltas are on writes (POST/PUT/PATCH ~5-6× — MS OData's OData-JSON input/output
formatters and EDM-bound serialization dominate) and full-page reads (GetAllPage/OrderBy ~3-3.7× —
MS OData's ODataResourceSerializer per-entity envelope work vs OhData's System.Text.Json
serialization).

## Full BenchmarkDotNet output

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8655/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  Job-NUBXJZ : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

IterationCount=20  WarmupCount=5
```

| Method             | Categories | Mean        | Error      | StdDev     | Ratio | RatioSD | Gen0    | Gen1    | Allocated | Alloc Ratio |
|------------------- |----------- |------------:|-----------:|-----------:|------:|--------:|--------:|--------:|----------:|------------:|
| OhData_CountTrue   | CountTrue  | 2,830.62 μs |  58.959 μs |  57.906 μs |  1.00 |    0.03 |  7.8125 |  3.9063 | 156.64 KB |        1.00 |
| MsOData_CountTrue  | CountTrue  | 4,740.09 μs | 238.258 μs | 274.378 μs |  1.68 |    0.10 | 46.8750 | 15.6250 | 841.55 KB |        5.37 |
| OhData_Delete      | Delete     |    15.62 μs |   2.520 μs |   2.902 μs |  1.03 |    0.27 |  0.6104 |       - |  10.66 KB |        1.00 |
| MsOData_Delete     | Delete     |    23.97 μs |   0.301 μs |   0.335 μs |  1.59 |    0.29 |  0.7324 |       - |  13.72 KB |        1.29 |
| OhData_Filter      | Filter     | 1,777.54 μs | 205.552 μs | 236.714 μs |  1.02 |    0.18 |  7.8125 |  3.9063 | 138.44 KB |        1.00 |
| MsOData_Filter     | Filter     | 3,393.47 μs | 270.389 μs | 311.380 μs |  1.94 |    0.28 | 46.8750 | 15.6250 | 823.91 KB |        5.95 |
| OhData_GetAllPage  | GetAllPage |   762.96 μs |  60.572 μs |  67.325 μs |  1.01 |    0.12 |  5.8594 |  1.9531 | 123.85 KB |        1.00 |
| MsOData_GetAllPage | GetAllPage | 2,820.89 μs | 389.254 μs | 432.654 μs |  3.72 |    0.63 | 46.8750 | 15.6250 | 781.21 KB |        6.31 |
| OhData_GetById     | GetById    |    36.84 μs |   7.535 μs |   7.738 μs |  1.03 |    0.26 |  0.9766 |       - |  15.95 KB |        1.00 |
| MsOData_GetById    | GetById    |   111.52 μs |   7.109 μs |   7.300 μs |  3.12 |    0.51 |  2.9297 |       - |  48.47 KB |        3.04 |
| OhData_OrderBy     | OrderBy    |   967.65 μs |  18.200 μs |  17.875 μs |  1.00 |    0.03 |  7.8125 |  3.9063 | 155.33 KB |        1.00 |
| MsOData_OrderBy    | OrderBy    | 2,949.10 μs | 322.721 μs | 345.308 μs |  3.05 |    0.35 | 46.8750 | 15.6250 | 837.49 KB |        5.39 |
| OhData_Patch       | Patch      |    52.61 μs |   7.039 μs |   7.228 μs |  1.01 |    0.18 |  0.9766 |       - |  19.25 KB |        1.00 |
| MsOData_Patch      | Patch      |   324.69 μs |  96.321 μs | 110.923 μs |  6.26 |    2.21 |  7.8125 |  0.9766 | 137.19 KB |        7.13 |
| OhData_Post        | Post       |    51.26 μs |   7.991 μs |   8.206 μs |  1.02 |    0.20 |  0.9766 |       - |  18.62 KB |        1.00 |
| MsOData_Post       | Post       |   285.62 μs |  65.121 μs |  72.381 μs |  5.67 |    1.56 |  8.7891 |  1.9531 | 143.66 KB |        7.72 |
| OhData_Put         | Put        |    57.05 μs |   8.833 μs |   9.451 μs |  1.02 |    0.22 |  0.9766 |       - |  19.17 KB |        1.00 |
| MsOData_Put        | Put        |   280.82 μs |  71.922 μs |  79.941 μs |  5.03 |    1.56 |  8.7891 |  1.9531 | 148.27 KB |        7.74 |
| OhData_Select      | Select     |   877.91 μs |  85.806 μs |  98.814 μs |  1.01 |    0.15 | 13.6719 |  3.9063 | 252.78 KB |        1.00 |
| MsOData_Select     | Select     | 1,858.28 μs | 141.321 μs | 151.212 μs |  2.14 |    0.28 | 15.6250 |  7.8125 | 339.05 KB |        1.34 |
| OhData_TopSkip     | TopSkip    | 1,261.81 μs |  77.354 μs |  85.978 μs |  1.00 |    0.09 |  5.8594 |  1.9531 | 103.31 KB |        1.00 |
| MsOData_TopSkip    | TopSkip    | 2,061.28 μs | 151.510 μs | 168.403 μs |  1.64 |    0.17 | 23.4375 |  7.8125 | 471.51 KB |        4.56 |

Global total time: 00:05:08 (22 benchmarks). Smoke check (all 11 scenarios) passed before the run.

## Methodology

- **Two in-process TestServer hosts**, one per framework, measured over the **full HTTP
  round-trip** (routing → OData query-option processing → handler → serialization). No real
  network, no database — the comparison isolates the HTTP/OData pipeline itself.
  - **OhData host:** `AddOhData` + `MapOhData` minimal-API endpoints over an
    `EntitySetProfile<int, BenchWidget>` (`GetQueryable` path).
  - **MS OData host:** `AddControllers().AddOData(...)` with a conventional
    `ODataController` + `[EnableQuery]` (`BenchWidgetsController`).
- **Identical dataset:** 1000 deterministic `BenchWidget` entities (id, name, category, price,
  isActive, createdAt, plus a `Tags` complex-type collection of 0–3 items per widget), generated
  by the same `BenchmarkData.CreateWidgets()` code in each host. `List<T>`-backed store, no EF.
- **Identical requests:** every URL and request body is defined once in `BenchmarkRequests` and
  used verbatim against both hosts, and by both the smoke check and the benchmarks.
- **Paging parity:** OhData `MaxTop = 100` vs MS `[EnableQuery(PageSize = 100, MaxTop = 100)]` —
  both return a 100-item first page with an `@odata.nextLink` for unpaged collection queries.
- **Wire-format parity:** MS host uses `EnableLowerCamelCase()` so both servers emit camelCase
  JSON and accept the same camelCase property names in `$filter`/`$orderby`/`$select`.
- **Correctness gate:** `Program.Main` runs `SmokeCheck` before any measurement — all 11
  scenarios must return semantically equivalent responses (status codes, item id sequences,
  `$select` shapes, `@odata.count` values, entity payload equality) on both hosts or the run
  aborts. Re-run anytime with `dotnet run -c Release --project src/OhData.Server.Benchmarks -- --smoke`.
- **Run config:** `[SimpleJob(warmupCount: 5, iterationCount: 20)]` + `[MemoryDiagnoser]` —
  iteration counts trimmed from BenchmarkDotNet's adaptive default so the 22-benchmark suite
  completes in a reasonable time while keeping error bars small relative to the inter-server
  deltas reported.
- Benchmarks are paired per operation via `[BenchmarkCategory]`, with the OhData side as the
  per-category baseline, so the Ratio column reads directly as "MS OData cost relative to OhData".

## Scenario details

| Category | Request |
|---|---|
| GetAllPage | `GET /odata/BenchWidgets` (first 100-item page) |
| Filter | `GET /odata/BenchWidgets?$filter=price gt 500` (495 matches, paged to 100) |
| OrderBy | `GET /odata/BenchWidgets?$orderby=name desc` |
| Select | `GET /odata/BenchWidgets?$select=id,name` |
| TopSkip | `GET /odata/BenchWidgets?$top=50&$skip=100&$orderby=id` |
| CountTrue | `GET /odata/BenchWidgets?$count=true&$filter=price gt 500` |
| GetById | `GET /odata/BenchWidgets(500)` |
| Post | `POST /odata/BenchWidgets` (JSON entity body → 201 + entity) |
| Put | `PUT /odata/BenchWidgets(500)` with `Prefer: return=representation` (→ 200 + entity) |
| Patch | `PATCH /odata/BenchWidgets(500)` with `Prefer: return=representation` (→ 200 + entity) |
| Delete | `DELETE /odata/BenchWidgets(500)` (→ 204) |

Write handlers on **both** sides deliberately do not mutate the seeded store (POST assigns
id 1001 and echoes; PUT/PATCH clone-and-return; DELETE acknowledges) so iteration N+1 measures
the same dataset as iteration N — the same discipline used in
`OhData.Client.Benchmarks/ServerPipelineBenchmarks.cs`.

## Known asymmetries that could not be eliminated

1. **Response envelopes differ by design.** MS OData wraps single entities with
   `@odata.context` and emits OData metadata annotations; OhData's GetById returns the bare
   JSON entity, and its collection envelope carries only `@odata.context` /
   `@odata.count` / `@odata.nextLink`. Payload bytes therefore differ somewhat even though the
   entity data is identical — this is each framework's native wire format, which is exactly
   what a user of each framework would pay for.
2. **`Prefer: return=representation` on PUT/PATCH.** MS OData's `Updated()` returns
   `204 No Content` unless the client requests the representation; OhData returns `200 + body`
   by default. The header is sent to **both** hosts so requests stay identical and both sides
   pay for entity serialization in the response.
3. **`$select` implementations differ.** OhData applies `$select` via JsonNode post-processing;
   MS OData uses `ISelectExpandWrapper`. The observable output shape was asserted equal
   (`id`,`name` only, camelCase) by the smoke check; the internal strategy is part of what is
   being measured.
4. **Stable-ordering insertion.** With `PageSize` set, MS OData appends a stable `$orderby` on
   the key for unpaged queries; OhData takes the source order (which is id order for this
   dataset). Result sequences were asserted identical by the smoke check.
5. **Delta types.** Both PATCH paths use `Microsoft.AspNetCore.OData.Deltas.Delta<T>` — OhData
   reuses the same Delta type — so partial-update semantics are shared code.

## Reproducing

```bash
# Correctness checks only
dotnet run -c Release --project src/OhData.Server.Benchmarks -- --smoke

# Full suite (smoke check runs first automatically)
dotnet run -c Release --project src/OhData.Server.Benchmarks -- --filter "*"
```
