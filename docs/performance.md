# Performance

OhData's minimal-API pipeline measured head-to-head against `Microsoft.AspNetCore.OData`'s
`ODataController` + `[EnableQuery]` pipeline, over the full HTTP round-trip, on the same dataset
with byte-identical requests and a correctness gate in front of every run.

**Every number on this page carries the commit it was taken at.** A performance figure without its
provenance is a rumour with units — this page was 119 commits stale once ([#636](https://github.com/en-gen/OhData/issues/636)) and the
README was republishing it with no date at all.

| | |
|---|---|
| **Commit** | [`4e123b7`](https://github.com/en-gen/OhData/commit/4e123b7bc9ff763c8614161e441c57a394330cfa) (2.0.0 development head) |
| **Measured** | 2026-09-04, 07:44 UTC |
| **Environment** | BenchmarkDotNet v0.15.8 · Windows 11 (10.0.26200.9168/25H2) · AMD Ryzen 9 5950X, 16 physical cores · .NET SDK 10.0.303 · .NET 10.0.11 X64 RyuJIT x86-64-v3 |
| **Packages** | `Microsoft.AspNetCore.OData` 9.4.x (the same floating range `OhData.AspNetCore` references); OhData from source at that commit |
| **Gate** | The 16-scenario smoke check passed before measurement — the run aborts otherwise |

## Summary

OhData is faster and allocates less in **all 11** scenarios of the head-to-head suite:

| Scenario | OhData | Microsoft.AspNetCore.OData | Speedup | Alloc (OhData → MS) | Alloc ratio |
|---|---:|---:|---:|---:|---:|
| GetAll page (100) | 1,296 µs | 3,119 µs | **2.48×** | 169 → 781 KB | 4.6× |
| `$filter` | 2,357 µs | 4,042 µs | **1.73×** | 177 → 805 KB | 4.6× |
| `$orderby` | 1,583 µs | 4,140 µs | **2.67×** | 189 → 838 KB | 4.4× |
| `$select` | 1,531 µs | 2,380 µs | **1.56×** | 280 → 339 KB | 1.2× |
| `$top` + `$skip` | 1,142 µs | 2,465 µs | **2.17×** | 126 → 472 KB | 3.7× |
| `$count=true` (+`$filter`) | 3,973 µs | 5,884 µs | **1.50×** | 196 → 842 KB | 4.3× |
| GetById | 63.6 µs | 157.5 µs | **2.48×** | 17.4 → 48.8 KB | 2.8× |
| POST | 78.2 µs | 330.2 µs | **4.32×** | 18.8 → 145.5 KB | 7.7× |
| PUT | 79.9 µs | 337.3 µs | **4.24×** | 20.0 → 150.5 KB | 7.5× |
| PATCH | 84.1 µs | 342.3 µs | **4.09×** | 21.3 → 137.4 KB | 6.5× |
| DELETE | 44.5 µs | 50.7 µs | **1.14×** | 11.6 → 13.7 KB | 1.2× |

"Speedup" is BenchmarkDotNet's own `Ratio` column against the OhData baseline in each category, not
a quotient of the two means — it is computed per iteration and so carries the error bars in the raw
output below. "Alloc ratio" is how many times more memory the MS OData pipeline allocates per
request.

The widest gaps are on **writes** (POST/PUT/PATCH, ~4×): MS OData's OData-JSON input/output
formatters and EDM-bound serialization dominate there, and the allocation ratio (6.5–7.7×) is the
clearer signal, being far outside the run-to-run noise the timings carry. Full-page reads sit at
~2.5×. The two narrowest are honest limits rather than rounding: **DELETE** at 1.14× ± 0.07 is a
route where neither framework does much beyond routing, and **`$select`** at 1.56× is the one
scenario where MS OData's `ISelectExpandWrapper` allocates comparably to OhData's JsonNode pass
(1.2×, the only sub-2× allocation ratio in the suite).

## `$expand` / `$levels` — measured, deliberately not headlined

The suite has a second, EF Core/SQLite-backed half covering `$expand` and `$levels`. Its numbers
were withheld from earlier revisions of this page because the shared run config was too noisy on
them to publish trustworthy magnitudes. They now run under their own heavier config
(`InvocationCount=32`, 30 measured iterations, 50–100 warmup), which fixed the bimodality — but the
standing decision was to republish only once numbers hold **across repeated runs**, and this is one
run. So they are recorded here with their error bars visible and stay out of the README:

| Scenario | OhData | Microsoft.AspNetCore.OData | Ratio | Alloc ratio |
|---|---:|---:|---:|---:|
| `$expand` (collection) | 3.13 ms | 11.32 ms | 3.62× ± 0.30 | 2.15× |
| `$select` + `$expand` | 3.30 ms | 9.73 ms | 2.97× ± 0.50 | 2.03× |
| `$expand` (nested) | 8.28 ms | 18.61 ms | 2.27× ± 0.26 | 1.83× |
| `$expand` with nested options | 2.65 ms | 2.78 ms | 1.07× ± 0.19 | 1.29× |
| `$levels` | 2.29 ms | 2.43 ms | 1.10× ± 0.28 | 1.85× |

Read the bottom two rows as **parity, not a win**: both error bars cross 1.0×, so a single run
cannot distinguish them from no difference. The top three are outside their error bars and agree in
direction with the head-to-head suite. The allocation ratios are the more stable half of this table
throughout — they are counted, not timed.

## Methodology

- **Two in-process TestServer hosts**, one per framework, measured over the **full HTTP
  round-trip** (routing → OData query-option processing → handler → serialization). No real
  network — the comparison isolates the HTTP/OData pipeline itself. The 11 `BenchWidget` scenarios
  have no database (`List<T>`-backed store); the 5 `$expand`/`$levels` scenarios are backed by EF
  Core + SQLite (an in-memory keep-alive connection, one independently seeded per host) because
  OhData's `$expand` pushdown is gated to an EF Core-backed `IQueryable` — see `BenchmarkHosts` and
  `Model/BenchOrgData.cs`.
  - **OhData host:** `AddOhData` + `MapOhData` minimal-API endpoints over
    `EntitySetProfile<int, BenchWidget>` (`GetQueryable` path) for the 11 scenarios, and
    `EntitySetProfile<int, BenchDepartment>` / `EntitySetProfile<int, BenchEmployee>` for the 5
    `$expand`/`$levels` scenarios.
  - **MS OData host:** `AddControllers().AddOData(...)` with conventional `ODataController`s +
    `[EnableQuery]` (`BenchWidgetsController`, `BenchDepartmentsController`,
    `BenchEmployeesController`).
- **Identical dataset:** 1000 deterministic `BenchWidget` entities (id, name, category, price,
  isActive, createdAt, plus a `Tags` complex-type collection of 0–3 items per widget), generated by
  the same `BenchmarkData.CreateWidgets()` code in each host. The `$expand`/`$levels` scenarios use
  a separate, also-deterministic `BenchDepartment` (20 rows) / `BenchEmployee` (1000 rows, uniform
  50-per-department fan-out, a branching-factor-5 manager tree) fixture generated by `BenchOrgData`
  and seeded into each host's own EF Core SQLite database.
- **Identical requests:** every URL and request body is defined once in `BenchmarkRequests` and
  used verbatim against both hosts, and by both the smoke check and the benchmarks.
- **Paging parity:** OhData `MaxTop = 100` vs MS `[EnableQuery(PageSize = 100, MaxTop = 100)]` —
  both return a 100-item first page with an `@odata.nextLink` for unpaged collection queries.
- **Wire-format parity:** both servers emit PascalCase JSON (OhData's 1.5.0 default, and
  `ODataConventionModelBuilder`'s own default on the MS host) and accept the same property names in
  `$filter`/`$orderby`/`$select`.
- **Correctness gate:** `Program.Main` runs `SmokeCheck` before any measurement — all **16**
  scenarios must return semantically equivalent responses (status codes, item id sequences,
  `$select` shapes, `@odata.count` values, entity/child payload equality) on both hosts or the run
  aborts.
- **Run config:** the 11 `BenchWidget` scenarios use `[SimpleJob(warmupCount: 5,
  iterationCount: 20)]` + `[MemoryDiagnoser]` — iteration counts trimmed from BenchmarkDotNet's
  adaptive default so that half of the suite completes quickly while keeping error bars small
  relative to the inter-server deltas reported. The 5 `$expand`/`$levels` scenarios run under a
  separate, heavier config chosen specifically to fix noise an adversarial fairness review found —
  see `ExpandComparisonBenchmarks` for the rationale.
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
   (`Id`,`Name` only, PascalCase) by the smoke check; the internal strategy is part of what is
   being measured.
4. **Stable-ordering insertion.** With `PageSize` set, MS OData appends a stable `$orderby` on
   the key for unpaged queries; OhData takes the source order (which is id order for this
   dataset). Result sequences were asserted identical by the smoke check.
5. **Delta types.** Both PATCH paths use `Microsoft.AspNetCore.OData.Deltas.Delta<T>` — OhData
   reuses the same Delta type — so partial-update semantics are shared code.

### `$expand`/`$levels` scenarios (`BenchDepartment`/`BenchEmployee`)

6. **`@odata.nextLink` on every `BenchDepartments` response.** OhData emits one; MS OData emits
   none. OhData applies `MaxTop = 20` (`BenchOrgData.DepartmentPageSize`) as an implicit page
   limit and, because the page comes back exactly full (`DepartmentCount` is also 20), advertises
   a next page that doesn't exist. Row content is identical either way — no timing distortion —
   but the two envelopes are not semantically identical, and the smoke gate does not check for it.
7. **`@odata.context` differs**: `#BenchDepartments` on OhData vs
   `#BenchDepartments(Employees())` on MS OData — OhData omits the expand clause from the context
   URL. Tracked as [#648](https://github.com/en-gen/OhData/issues/648) — read there before treating this as settled; nothing asserts context URLs today.
8. **`MaxTop` means different things on the two hosts.** OhData treats it as an implicit page
   size, applied even to a request that sends no `$top`; MS's `[EnableQuery(MaxTop=...)]` only
   caps a client-*supplied* `$top` and does nothing to an unpaged request on its own. This is
   neutral for every scenario in this suite only because `DepartmentPageSize == DepartmentCount
   == 20` and the one `BenchEmployees` scenario ($levels) additionally `$filter`s down to a single
   row. An unfiltered, unpaged `BenchEmployees` scenario would diverge sharply (OhData 100 rows,
   MS OData 1000 rows) and nothing in this suite would catch it.
9. **`[EnableQuery(PageSize=...)]` is deliberately omitted** on `BenchDepartmentsController` and
   `BenchEmployeesController` (unlike `BenchWidgetsController`, which sets it). `PageSize` wraps
   *every* collection in the response — including expanded/nested ones — in a
   `TruncatedCollection`, and composing that with nested `$expand` requires the SQL `APPLY`
   operation, which SQLite's EF Core provider can't emit (the request 500s). See the reasoning
   documented on each controller before "fixing" this.

## Full BenchmarkDotNet output

### Head-to-head suite (`ServerComparisonBenchmarks`)

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-NUBXJZ : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
IterationCount=20  WarmupCount=5
```

| Method             | Categories | Mean        | Error      | StdDev     | Ratio | RatioSD | Gen0    | Gen1    | Allocated | Alloc Ratio |
|------------------- |----------- |------------:|-----------:|-----------:|------:|--------:|--------:|--------:|----------:|------------:|
| OhData_CountTrue   | CountTrue  | 3,972.99 us | 453.297 us | 522.017 us |  1.02 |    0.18 |  7.8125 |       - | 195.54 KB |        1.00 |
| MsOData_CountTrue  | CountTrue  | 5,883.53 us | 249.308 us | 287.103 us |  1.50 |    0.19 | 46.8750 | 15.6250 | 841.85 KB |        4.31 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Delete      | Delete     |    44.50 us |   1.372 us |   1.580 us |  1.00 |    0.05 |  0.6104 |       - |  11.64 KB |        1.00 |
| MsOData_Delete     | Delete     |    50.66 us |   2.468 us |   2.641 us |  1.14 |    0.07 |  0.7324 |       - |  13.73 KB |        1.18 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Filter      | Filter     | 2,356.60 us | 205.246 us | 236.361 us |  1.01 |    0.14 |  7.8125 |  3.9063 | 176.63 KB |        1.00 |
| MsOData_Filter     | Filter     | 4,042.45 us | 389.555 us | 448.612 us |  1.73 |    0.25 | 46.8750 | 15.6250 | 804.72 KB |        4.56 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_GetAllPage  | GetAllPage | 1,295.92 us | 202.757 us | 233.495 us |  1.03 |    0.25 |  7.8125 |  3.9063 | 169.39 KB |        1.00 |
| MsOData_GetAllPage | GetAllPage | 3,119.32 us | 281.014 us | 323.616 us |  2.48 |    0.49 | 46.8750 | 15.6250 | 781.13 KB |        4.61 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_GetById     | GetById    |    63.58 us |   1.881 us |   2.013 us |  1.00 |    0.04 |  0.9766 |       - |  17.37 KB |        1.00 |
| MsOData_GetById    | GetById    |   157.45 us |   1.597 us |   1.568 us |  2.48 |    0.08 |  2.9297 |       - |  48.76 KB |        2.81 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_OrderBy     | OrderBy    | 1,582.77 us | 200.960 us | 231.426 us |  1.02 |    0.20 |  7.8125 |  3.9063 | 189.44 KB |        1.00 |
| MsOData_OrderBy    | OrderBy    | 4,139.67 us | 460.381 us | 530.176 us |  2.67 |    0.49 | 46.8750 | 15.6250 | 838.29 KB |        4.43 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Patch       | Patch      |    84.11 us |   5.311 us |   5.682 us |  1.00 |    0.10 |  1.2207 |       - |   21.3 KB |        1.00 |
| MsOData_Patch      | Patch      |   342.26 us |  80.832 us |  93.086 us |  4.09 |    1.12 |  7.8125 |  0.9766 | 137.39 KB |        6.45 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Post        | Post       |    78.21 us |  11.874 us |  13.198 us |  1.02 |    0.22 |  0.9766 |       - |  18.84 KB |        1.00 |
| MsOData_Post       | Post       |   330.15 us |  87.204 us | 100.425 us |  4.32 |    1.43 |  8.7891 |  1.9531 | 145.53 KB |        7.73 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Put         | Put        |    79.89 us |   4.602 us |   4.924 us |  1.00 |    0.09 |  1.2207 |       - |  19.96 KB |        1.00 |
| MsOData_Put        | Put        |   337.27 us |  99.672 us | 114.782 us |  4.24 |    1.43 |  8.7891 |  1.9531 | 150.46 KB |        7.54 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Select      | Select     | 1,531.13 us | 119.480 us | 127.842 us |  1.01 |    0.11 | 15.6250 |  7.8125 | 279.87 KB |        1.00 |
| MsOData_Select     | Select     | 2,379.52 us | 389.813 us | 417.095 us |  1.56 |    0.29 | 15.6250 |  7.8125 | 339.44 KB |        1.21 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_TopSkip     | TopSkip    | 1,142.38 us |  84.037 us |  96.778 us |  1.01 |    0.12 |  5.8594 |  1.9531 | 126.01 KB |        1.00 |
| MsOData_TopSkip    | TopSkip    | 2,465.25 us | 177.124 us | 189.521 us |  2.17 |    0.25 | 15.6250 |       - | 471.83 KB |        3.74 |

### `$expand`/`$levels` suite (`ExpandComparisonBenchmarks`)

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.303
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Job-HFIYNB : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
InvocationCount=32  IterationCount=30  MaxWarmupIterationCount=100
MinWarmupIterationCount=50  UnrollFactor=1
```

| Method                      | Categories          | Mean      | Error     | StdDev    | Ratio | RatioSD | Gen0     | Gen1     | Gen2    | Allocated  | Alloc Ratio |
|---------------------------- |-------------------- |----------:|----------:|----------:|------:|--------:|---------:|---------:|--------:|-----------:|------------:|
| OhData_ExpandCollection     | ExpandCollection    |  3.134 ms | 0.0746 ms | 0.1094 ms |  1.00 |    0.05 |  93.7500 |  31.2500 |       - | 1915.07 KB |        1.00 |
| MsOData_ExpandCollection    | ExpandCollection    | 11.316 ms | 0.5909 ms | 0.8844 ms |  3.62 |    0.30 | 218.7500 |  93.7500 |       - | 4116.62 KB |        2.15 |
|                             |                     |           |           |           |       |         |          |          |         |            |             |
| OhData_ExpandNested         | ExpandNested        |  8.283 ms | 0.5597 ms | 0.8377 ms |  1.01 |    0.14 | 250.0000 | 187.5000 | 31.2500 | 4264.44 KB |        1.00 |
| MsOData_ExpandNested        | ExpandNested        | 18.605 ms | 1.0083 ms | 1.3110 ms |  2.27 |    0.26 | 500.0000 | 250.0000 | 62.5000 | 7791.74 KB |        1.83 |
|                             |                     |           |           |           |       |         |          |          |         |            |             |
| OhData_ExpandNestedOptions  | ExpandNestedOptions |  2.647 ms | 0.2545 ms | 0.3809 ms |  1.02 |    0.21 |        - |        - |       - |  438.19 KB |        1.00 |
| MsOData_ExpandNestedOptions | ExpandNestedOptions |  2.784 ms | 0.2007 ms | 0.3004 ms |  1.07 |    0.19 |  31.2500 |        - |       - |  566.06 KB |        1.29 |
|                             |                     |           |           |           |       |         |          |          |         |            |             |
| OhData_Levels               | Levels              |  2.291 ms | 0.3203 ms | 0.4794 ms |  1.04 |    0.30 |        - |        - |       - |  226.25 KB |        1.00 |
| MsOData_Levels              | Levels              |  2.426 ms | 0.2596 ms | 0.3886 ms |  1.10 |    0.28 |        - |        - |       - |  419.05 KB |        1.85 |
|                             |                     |           |           |           |       |         |          |          |         |            |             |
| OhData_SelectExpand         | SelectExpand        |  3.303 ms | 0.2267 ms | 0.3179 ms |  1.01 |    0.13 |  93.7500 |  31.2500 |       - | 1922.19 KB |        1.00 |
| MsOData_SelectExpand        | SelectExpand        |  9.730 ms | 1.0205 ms | 1.4635 ms |  2.97 |    0.50 | 218.7500 |  62.5000 |       - | 3901.24 KB |        2.03 |


## Reproducing

```bash
# Correctness checks only (all 16 scenarios, both hosts)
dotnet run -c Release --project src/OhData.Server.Benchmarks -- --smoke

# Full suite (the smoke check runs first automatically and aborts the run on any mismatch)
dotnet run -c Release --project src/OhData.Server.Benchmarks -- --filter "*"

# Head-to-head suite only
dotnet run -c Release --project src/OhData.Server.Benchmarks -- --filter "*ServerComparisonBenchmarks*"
```

When you republish this page, replace the provenance table at the top in the same commit. A figure
without its commit is not a measurement.
