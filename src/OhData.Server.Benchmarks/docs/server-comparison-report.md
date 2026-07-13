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
| GetAll page (100) | 981 µs | 124 KB | 2,847 µs | 781 KB | **2.9×** | 6.3× |
| $filter | 2,203 µs | 138 KB | 3,940 µs | 825 KB | **1.8×** | 6.0× |
| $orderby | 1,317 µs | 155 KB | 3,856 µs | 838 KB | **2.9×** | 5.4× |
| $select | 1,047 µs | 253 KB | 2,102 µs | 339 KB | **2.0×** | 1.3× |
| $top + $skip | 1,627 µs | 103 KB | 2,499 µs | 472 KB | **1.5×** | 4.6× |
| $count=true (+$filter) | 3,805 µs | 157 KB | 5,538 µs | 842 KB | **1.5×** | 5.4× |
| GetById | 57 µs | 16 KB | 129 µs | 48 KB | **2.3×** | 3.0× |
| POST | 67 µs | 19 KB | 329 µs | 144 KB | **4.9×** | 7.7× |
| PUT | 66 µs | 19 KB | 327 µs | 148 KB | **4.9×** | 7.7× |
| PATCH | 68 µs | 19 KB | 343 µs | 137 KB | **5.0×** | 7.1× |
| DELETE | 24 µs | 11 KB | 39 µs | 14 KB | **1.6×** | 1.3× |

The biggest deltas are on writes (POST/PUT/PATCH ~5× — MS OData's OData-JSON input/output
formatters and EDM-bound serialization dominate) and full-page reads (GetAllPage/OrderBy ~2.9× —
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
| OhData_CountTrue   | CountTrue  | 3,804.75 μs | 225.944 μs | 251.137 μs |  1.00 |    0.09 |  7.8125 |       - | 157.09 KB |        1.00 |
| MsOData_CountTrue  | CountTrue  | 5,538.03 μs | 551.152 μs | 565.992 μs |  1.46 |    0.17 | 31.2500 |       - | 841.64 KB |        5.36 |
| OhData_Delete      | Delete     |    24.02 μs |   2.436 μs |   2.805 μs |  1.01 |    0.17 |  0.6104 |       - |  10.69 KB |        1.00 |
| MsOData_Delete     | Delete     |    38.62 μs |   1.988 μs |   2.289 μs |  1.63 |    0.22 |  0.7324 |       - |  13.79 KB |        1.29 |
| OhData_Filter      | Filter     | 2,202.69 μs | 107.154 μs | 119.102 μs |  1.00 |    0.08 |  7.8125 |  3.9063 | 138.31 KB |        1.00 |
| MsOData_Filter     | Filter     | 3,939.51 μs | 144.318 μs | 141.739 μs |  1.79 |    0.11 | 31.2500 |       - | 824.91 KB |        5.96 |
| OhData_GetAllPage  | GetAllPage |   980.68 μs |  59.074 μs |  65.661 μs |  1.00 |    0.10 |  5.8594 |  1.9531 | 123.74 KB |        1.00 |
| MsOData_GetAllPage | GetAllPage | 2,846.72 μs | 224.493 μs | 258.527 μs |  2.92 |    0.33 | 46.8750 | 15.6250 | 780.71 KB |        6.31 |
| OhData_GetById     | GetById    |    56.96 μs |   9.419 μs |  10.469 μs |  1.03 |    0.25 |  0.9766 |       - |  15.95 KB |        1.00 |
| MsOData_GetById    | GetById    |   128.96 μs |   9.666 μs |   9.927 μs |  2.33 |    0.40 |  2.9297 |       - |  48.33 KB |        3.03 |
| OhData_OrderBy     | OrderBy    | 1,317.10 μs |  40.729 μs |  43.579 μs |  1.00 |    0.05 |  7.8125 |  3.9063 | 155.48 KB |        1.00 |
| MsOData_OrderBy    | OrderBy    | 3,856.49 μs | 229.954 μs | 264.816 μs |  2.93 |    0.22 | 46.8750 | 15.6250 | 837.74 KB |        5.39 |
| OhData_Patch       | Patch      |    68.36 μs |   3.193 μs |   3.279 μs |  1.00 |    0.06 |  0.9766 |       - |  19.25 KB |        1.00 |
| MsOData_Patch      | Patch      |   342.58 μs |  63.232 μs |  70.282 μs |  5.02 |    1.03 |  7.8125 |  0.9766 | 137.26 KB |        7.13 |
| OhData_Post        | Post       |    66.86 μs |   3.542 μs |   3.637 μs |  1.00 |    0.07 |  0.9766 |       - |  18.62 KB |        1.00 |
| MsOData_Post       | Post       |   328.83 μs |  62.556 μs |  69.531 μs |  4.93 |    1.05 |  8.7891 |  1.9531 | 143.58 KB |        7.71 |
| OhData_Put         | Put        |    66.07 μs |   3.119 μs |   3.203 μs |  1.00 |    0.07 |  0.9766 |       - |  19.17 KB |        1.00 |
| MsOData_Put        | Put        |   326.88 μs |  68.218 μs |  78.560 μs |  4.96 |    1.19 |  8.7891 |  0.9766 | 148.27 KB |        7.74 |
| OhData_Select      | Select     | 1,046.65 μs |  39.404 μs |  43.798 μs |  1.00 |    0.06 | 13.6719 |  3.9063 | 252.67 KB |        1.00 |
| MsOData_Select     | Select     | 2,101.56 μs | 232.826 μs | 258.786 μs |  2.01 |    0.26 | 15.6250 |  7.8125 | 339.17 KB |        1.34 |
| OhData_TopSkip     | TopSkip    | 1,626.58 μs |  38.157 μs |  42.411 μs |  1.00 |    0.04 |  5.8594 |  1.9531 | 103.01 KB |        1.00 |
| MsOData_TopSkip    | TopSkip    | 2,499.03 μs | 227.889 μs | 243.838 μs |  1.54 |    0.15 | 15.6250 |       - | 471.67 KB |        4.58 |

Global total time: 00:05:31 (22 benchmarks). Smoke check (all 11 scenarios) passed before the run.

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
