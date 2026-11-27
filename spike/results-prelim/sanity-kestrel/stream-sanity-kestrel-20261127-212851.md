# Streaming collection sanity probe (SPIKE #867)

- commit: `536d8f32676b3a3e7670412e4709af674d45a862` (DIRTY working tree); host: kestrel; requests per cell: 10 (concurrent mode: 10 rounds of N); one fresh child process per cell and arm
- GC env: DOTNET_gcServer=0; gen0 max budget: default; heap hard limit: 1024 MB (DOTNET_GCHeapHardLimit)
- machine: AMD Ryzen 9 5950X 16-Core Processor; logical processors 32; OS: Microsoft Windows 10.0.26300; runtime: .NET 10.0.15
- run at (UTC): 2026-11-27T21:28:52.0196119Z
- peak WS = Process.PeakWorkingSet64 of the cell's own child (includes runtime, host, 10k-row fixtures: compare arms, not absolutes). Gen counts and pause are totals over the measured requests; per-request medians in the CSV.

| backend | route | rows | conc | mode | ok | failed | peak WS MB | max heap MB | max promoted MB | gen0 | gen1 | gen2 | GC pause ms | TTFB med ms | total med ms | total max ms | peak heap med KB | alloc med KB | errors |
|---|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| InMemory | GetQueryable | 10000 | 8 | Buffered | 80 | 0 | 482 | 353 | 111 | 19 | 16 | 6 | 104.8 | NaN | 131.53 | 229.00 | NaN | NaN |  |
| InMemory | GetQueryable | 10000 | 8 | Streamed | 80 | 0 | 155 | 28 | 1 | 34 | 7 | 1 | 60.2 | NaN | 123.06 | 199.39 | NaN | NaN |  |
| InMemory | GetQueryable | 10000 | 16 | Buffered | 160 | 0 | 622 | 460 | 260 | 40 | 28 | 14 | 345.0 | NaN | 172.89 | 371.93 | NaN | NaN |  |
| InMemory | GetQueryable | 10000 | 16 | Streamed | 160 | 0 | 168 | 40 | 12 | 72 | 30 | 5 | 195.1 | NaN | 164.52 | 322.98 | NaN | NaN |  |
| InMemory | GetQueryable | 10000 | 32 | Buffered | 320 | 0 | 1090 | 868 | 477 | 63 | 44 | 16 | 1272.4 | NaN | 314.40 | 711.12 | NaN | NaN |  |
| InMemory | GetQueryable | 10000 | 32 | Streamed | 320 | 0 | 182 | 40 | 2 | 143 | 102 | 8 | 1258.8 | NaN | 383.06 | 558.14 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 8 | Buffered | 80 | 0 | 407 | 294 | 95 | 19 | 17 | 6 | 160.8 | NaN | 133.00 | 404.82 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 8 | Streamed | 80 | 0 | 153 | 30 | 1 | 35 | 12 | 1 | 66.7 | NaN | 134.50 | 162.85 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 16 | Buffered | 160 | 0 | 659 | 483 | 276 | 37 | 29 | 10 | 400.3 | NaN | 186.66 | 379.72 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 16 | Streamed | 160 | 0 | 166 | 45 | 2 | 75 | 30 | 6 | 208.3 | NaN | 141.63 | 298.74 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 32 | Buffered | 320 | 0 | 1034 | 881 | 219 | 68 | 55 | 15 | 1590.3 | NaN | 334.80 | 810.63 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 32 | Streamed | 320 | 0 | 187 | 42 | 2 | 147 | 111 | 8 | 718.3 | NaN | 281.36 | 742.03 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 8 | Buffered | 80 | 0 | 396 | 228 | 171 | 60 | 53 | 14 | 1202.4 | NaN | 291.86 | 642.74 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 8 | Streamed | 80 | 0 | 156 | 27 | 6 | 70 | 27 | 3 | 154.9 | NaN | 177.79 | 237.37 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 16 | Buffered | 160 | 0 | 587 | 455 | 177 | 113 | 105 | 21 | 2399.0 | NaN | 511.25 | 674.95 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 16 | Streamed | 160 | 0 | 165 | 26 | 1 | 139 | 105 | 5 | 758.6 | NaN | 319.00 | 433.30 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 32 | Buffered | 320 | 0 | 1036 | 873 | 14 | 203 | 185 | 22 | 5162.7 | NaN | 887.00 | 1157.82 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 32 | Streamed | 320 | 0 | 178 | 32 | 2 | 292 | 260 | 22 | 1996.0 | NaN | 504.62 | 681.78 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 8 | Buffered | 80 | 0 | 373 | 226 | 18 | 62 | 60 | 17 | 845.2 | NaN | 263.72 | 350.00 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 8 | Streamed | 80 | 0 | 158 | 28 | 7 | 70 | 27 | 3 | 193.4 | NaN | 240.18 | 327.43 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 16 | Buffered | 160 | 0 | 594 | 448 | 16 | 112 | 104 | 22 | 2278.7 | NaN | 494.94 | 620.61 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 16 | Streamed | 160 | 0 | 162 | 26 | 2 | 140 | 114 | 6 | 581.1 | NaN | 277.03 | 357.33 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 32 | Buffered | 320 | 0 | 1108 | 874 | 581 | 200 | 185 | 21 | 5607.5 | NaN | 953.52 | 1203.47 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 32 | Streamed | 320 | 0 | 180 | 33 | 3 | 292 | 269 | 22 | 1944.2 | NaN | 478.84 | 569.34 | NaN | NaN |  |
