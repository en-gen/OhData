# Performance

OhData's minimal-API pipeline measured head-to-head against `Microsoft.AspNetCore.OData`'s
`ODataController` + `[EnableQuery]` pipeline, over the full HTTP round-trip, on the same dataset
with byte-identical requests and a correctness gate in front of every run.

**Every number on this page carries the commit it was taken at.** A performance figure without its
provenance is a rumour with units — this page was 119 commits stale once
([#636](https://github.com/en-gen/OhData/issues/636)), and the README republished it with no date
at all.

| | |
|---|---|
| **Commit** | [`4e123b7`](https://github.com/en-gen/OhData/commit/4e123b7bc9ff763c8614161e441c57a394330cfa) (2.0.0 development head) |
| **Measured** | 2026-09-04 |
| **Run** | `--filter "*ServerComparisonBenchmarks*"` — the head-to-head suite **alone**, which is what the 1.7.0 control below also ran, and what every historically published figure on this page ran |
| **Environment** | BenchmarkDotNet v0.15.8 · Windows 11 (10.0.26200.9168/25H2) · AMD Ryzen 9 5950X, 16 physical cores · .NET SDK 10.0.303 · .NET 10.0.11 X64 RyuJIT x86-64-v3 |
| **Packages** | `Microsoft.AspNetCore.OData` 9.4.x (the same floating range `OhData.AspNetCore` references); OhData from source at that commit |
| **Gate** | The 16-scenario smoke check passed before measurement — the run aborts otherwise |

## Summary

OhData is **faster on 10 of the 11 scenarios and allocates less on all 11**:

| Scenario | OhData | Microsoft.AspNetCore.OData | Speedup | Alloc (OhData → MS) | Alloc ratio |
|---|---:|---:|---:|---:|---:|
| GetAll page (100) | 1,056 µs | 3,346 µs | **3.18×** ± 0.43 | 169.4 → 781.1 KB | 4.6× |
| `$filter` | 2,176 µs | 3,831 µs | **1.77×** ± 0.30 | 176.5 → 824.2 KB | 4.7× |
| `$orderby` | 1,599 µs | 4,016 µs | **2.56×** ± 0.54 | 189.6 → 818.7 KB | 4.3× |
| `$select` | 1,634 µs | 2,093 µs | **1.30×** ± 0.24 | 280.0 → 339.4 KB | 1.2× |
| `$top` + `$skip` | 1,019 µs | 2,666 µs | **2.62×** ± 0.31 | 126.0 → 471.7 KB | 3.7× |
| `$count=true` (+`$filter`) | 3,436 µs | 5,792 µs | **1.72×** ± 0.33 | 195.3 → 841.7 KB | 4.3× |
| GetById | 55.1 µs | 123.1 µs | **2.24×** ± 0.16 | 17.4 → 48.6 KB | 2.8× |
| POST | 60.2 µs | 300.6 µs | **5.00×** ± 1.42 | 18.8 → 145.5 KB | 7.7× |
| PUT | 63.0 µs | 298.7 µs | **4.75×** ± 1.29 | 20.0 → 150.4 KB | 7.5× |
| PATCH | 66.6 µs | 308.5 µs | **4.63×** ± 1.50 | 21.3 → 137.4 KB | 6.5× |
| DELETE | 34.7 µs | 35.2 µs | **1.02×** ± 0.08 | 11.7 → 13.7 KB | 1.2× |
"Speedup" is BenchmarkDotNet's own `Ratio` column against the OhData baseline in each category —
computed per iteration, not as a quotient of the two means — and it is quoted **with its standard
deviation**, because two rows need it:

- **DELETE is a tie on time, not a win.** 1.02× ± 0.08 is indistinguishable from parity; earlier
  revisions of this page claimed 1.59× and then 1.14× for it, and both were within-noise reads of
  the same tie. Neither framework does much on that route beyond routing. It is still a 1.2×
  allocation win, which is why the claim above splits time from allocations.
- **`$select` at 1.30× ± 0.24** is the narrowest real win, and the one scenario where MS OData's
  `ISelectExpandWrapper` allocates comparably to OhData's JsonNode pass (1.2×, the only sub-2×
  allocation ratio in the suite).

The widest gaps are on **writes** (POST/PUT/PATCH, 4.6–5.0×): MS OData's OData-JSON input/output
formatters and EDM-bound serialization dominate there. Note their ratio SDs (±1.3–1.5) — the
allocation ratios (6.5–7.7×) are the firmer statement, being counted rather than timed. Full-page
reads sit at 2.6–3.2×.

## Did 2.0.0 regress? — the 1.7.0 control

Every ratio here came in below the previously published figure, so this is not a bare republish.
2.0.0 changed the hot path in ways [#636](https://github.com/en-gen/OhData/issues/636) names
itself — above all [#581](https://github.com/en-gen/OhData/issues/581), which wraps every handler
return in `OhDataResult<T>`, a sealed **class**, i.e. a heap object per request on every route.

So v1.7.0 was checked out and run **under the same runtime, on the same machine, with the same
suite and the same scope**:

| Scenario | 1.7.0 | 2.0.0 | Δ time | Δ allocation |
|---|---:|---:|---:|---:|
| GetAll page (100) | 1,106 µs | 1,056 µs | -4.5% | 169.50 → 169.40 KB |
| `$filter` | 2,070 µs | 2,176 µs | +5.1% | 176.67 → 176.51 KB |
| `$orderby` | 1,545 µs | 1,599 µs | +3.5% | 189.40 → 189.59 KB |
| `$select` | 1,636 µs | 1,634 µs | -0.1% | 279.68 → 280.02 KB |
| `$top` + `$skip` | 1,040 µs | 1,019 µs | -2.0% | 126.28 → 126.05 KB |
| `$count=true` (+`$filter`) | 3,810 µs | 3,436 µs | -9.8% | 195.10 → 195.35 KB |
| GetById | 54.4 µs | 55.1 µs | +1.3% | 17.33 → 17.37 KB |
| POST | 61.3 µs | 60.2 µs | -1.8% | 18.80 → 18.84 KB |
| PUT | 64.1 µs | 63.0 µs | -1.8% | 19.92 → 19.96 KB |
| PATCH | 67.7 µs | 66.6 µs | -1.6% | 21.26 → 21.30 KB |
| DELETE | 29.6 µs | 34.7 µs | +17.2% | 11.52 → 11.74 KB |
**No timing regression.** The three write routes are *faster* by 1.6–1.8%, and everything except
DELETE sits inside run-to-run noise in both directions.

**No allocation regression either, and this is the firmer half** — allocations are counted, not
timed, so they are immune to machine state. The largest move on any route is **+340 bytes**, and
on the four key-addressed routes it is a uniform **+41 bytes**. Read that as an upper bound rather
than as a measurement of #581's object: the MS OData control, whose code is **byte-identical in
both trees**, moved by 0 to +72 bytes on the same routes with no pattern, which puts +41 bytes at
the measurement floor. Whatever `OhDataResult<T>` costs per request, it does not resolve above
noise at this scale.

**DELETE's +17.2% is the one deviation, and it has a mechanism.** Its handler went from
`Task<bool>` — for which the runtime hands back a **cached** completed task — to
`Task<OhDataResult<bool>>`, which cannot be cached; its allocation rose +225 B where every other
route moved ≤ +41 B, and its MS control moved +0 B. It is ~5 µs on the cheapest route in the
suite, and still inside 2 SD, so it is named here rather than treated as a finding.

### Why this control was necessary

A first attempt measured 2.0.0 inside a **full six-suite run** and 1.7.0 alone, and reported the
write routes as 24–28% *slower*. That was entirely the difference in run scope: re-measured with
matched scope, the same routes are marginally faster. Two consequences worth keeping:

1. **Never compare runs of different scope.** The confound is larger than every effect being
   looked for.
2. It is the concrete reason this repo has **no CI benchmark threshold**. MS OData's byte-identical
   code moved by −8% to +45% between two runs on one quiet desktop; a threshold picked without
   first measuring that spread is either a flaky red build or a gate that catches nothing. See
   `.github/workflows/benchmarks.yml`.

## `$expand` / `$levels` — measured, deliberately not headlined

The suite has a second, EF Core/SQLite-backed half covering `$expand` and `$levels`. Its numbers
were withheld from earlier revisions of this page because the shared run config was too noisy on
them to publish trustworthy magnitudes. They now run under their own heavier config
(`InvocationCount=32`, 30 measured iterations, 50–100 warmup), which fixed the bimodality — but the
standing decision was to republish only once numbers hold **across repeated runs**, and this is one
run. So they are recorded here with their error bars visible and stay out of the README.

They also come from a **six-suite run**, not the single-suite run the table above uses, and the
control section showed that scope difference inflating short-route timings by up to 30%. The
ratios are less exposed to it than the absolute figures (both hosts pay the same contention), but
it is one more reason these are recorded rather than headlined:

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
| OhData_CountTrue   | CountTrue  | 3,436.01 us | 466.648 us | 537.393 us |  1.02 |    0.22 |  7.8125 |       - | 195.35 KB |        1.00 |
| MsOData_CountTrue  | CountTrue  | 5,792.49 us | 650.315 us | 748.904 us |  1.72 |    0.33 | 46.8750 | 15.6250 | 841.67 KB |        4.31 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Delete      | Delete     |    34.68 us |   1.318 us |   1.517 us |  1.00 |    0.06 |  0.6104 |       - |  11.74 KB |        1.00 |
| MsOData_Delete     | Delete     |    35.21 us |   1.910 us |   2.199 us |  1.02 |    0.08 |  0.7324 |       - |  13.73 KB |        1.17 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Filter      | Filter     | 2,176.05 us | 185.331 us | 205.995 us |  1.01 |    0.13 |  7.8125 |  3.9063 | 176.51 KB |        1.00 |
| MsOData_Filter     | Filter     | 3,831.36 us | 496.131 us | 571.346 us |  1.77 |    0.30 | 46.8750 | 15.6250 | 824.18 KB |        4.67 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_GetAllPage  | GetAllPage | 1,056.26 us |  68.483 us |  78.866 us |  1.01 |    0.10 |  9.7656 |  3.9063 |  169.4 KB |        1.00 |
| MsOData_GetAllPage | GetAllPage | 3,345.69 us | 340.606 us | 392.243 us |  3.18 |    0.43 | 46.8750 | 15.6250 | 781.09 KB |        4.61 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_GetById     | GetById    |    55.10 us |   3.485 us |   3.579 us |  1.00 |    0.09 |  0.9766 |       - |  17.37 KB |        1.00 |
| MsOData_GetById    | GetById    |   123.11 us |   5.189 us |   5.329 us |  2.24 |    0.16 |  2.9297 |       - |  48.64 KB |        2.80 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_OrderBy     | OrderBy    | 1,598.73 us | 194.889 us | 224.434 us |  1.02 |    0.20 |  7.8125 |  3.9063 | 189.59 KB |        1.00 |
| MsOData_OrderBy    | OrderBy    | 4,016.27 us | 570.874 us | 657.420 us |  2.56 |    0.54 | 46.8750 | 15.6250 | 818.71 KB |        4.32 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Patch       | Patch      |    66.62 us |   1.638 us |   1.753 us |  1.00 |    0.04 |  1.2207 |       - |   21.3 KB |        1.00 |
| MsOData_Patch      | Patch      |   308.47 us |  88.670 us | 102.112 us |  4.63 |    1.50 |  7.8125 |  0.9766 | 137.45 KB |        6.45 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Post        | Post       |    60.17 us |   1.132 us |   1.112 us |  1.00 |    0.03 |  0.9766 |       - |  18.84 KB |        1.00 |
| MsOData_Post       | Post       |   300.60 us |  75.756 us |  87.240 us |  5.00 |    1.42 |  8.7891 |  1.9531 | 145.46 KB |        7.72 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Put         | Put        |    62.95 us |   0.931 us |   0.914 us |  1.00 |    0.02 |  1.2207 |       - |  19.96 KB |        1.00 |
| MsOData_Put        | Put        |   298.74 us |  72.019 us |  82.937 us |  4.75 |    1.29 |  8.7891 |  1.9531 | 150.44 KB |        7.54 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_Select      | Select     | 1,633.93 us | 189.240 us | 217.929 us |  1.02 |    0.18 | 15.6250 |  7.8125 | 280.02 KB |        1.00 |
| MsOData_Select     | Select     | 2,092.79 us | 269.057 us | 299.056 us |  1.30 |    0.24 | 15.6250 |  7.8125 | 339.42 KB |        1.21 |
|                    |            |             |            |            |       |         |         |         |           |             |
| OhData_TopSkip     | TopSkip    | 1,018.97 us |  45.517 us |  44.704 us |  1.00 |    0.06 |  5.8594 |  1.9531 | 126.05 KB |        1.00 |
| MsOData_TopSkip    | TopSkip    | 2,665.51 us | 283.040 us | 302.849 us |  2.62 |    0.31 | 15.6250 |       - | 471.72 KB |        3.74 |

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
