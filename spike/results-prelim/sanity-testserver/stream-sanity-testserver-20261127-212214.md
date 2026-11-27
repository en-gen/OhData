# Streaming collection sanity probe (SPIKE #867)

- commit: `536d8f32676b3a3e7670412e4709af674d45a862` (DIRTY working tree); host: testserver; requests per cell: 10 (concurrent mode: 10 rounds of N); one fresh child process per cell and arm
- GC env: DOTNET_gcServer=0; gen0 max budget: default; heap hard limit: 1024 MB (DOTNET_GCHeapHardLimit)
- machine: AMD Ryzen 9 5950X 16-Core Processor; logical processors 32; OS: Microsoft Windows 10.0.26300; runtime: .NET 10.0.15
- run at (UTC): 2026-11-27T21:22:14.5625128Z
- peak WS = Process.PeakWorkingSet64 of the cell's own child (includes runtime, host, 10k-row fixtures: compare arms, not absolutes). Gen counts and pause are totals over the measured requests; per-request medians in the CSV.

| backend | route | rows | conc | mode | ok | failed | peak WS MB | max heap MB | max promoted MB | gen0 | gen1 | gen2 | GC pause ms | TTFB med ms | total med ms | total max ms | peak heap med KB | alloc med KB | errors |
|---|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| InMemory | GetQueryable | 10000 | 8 | Buffered | 80 | 0 | 415 | 284 | 129 | 27 | 22 | 7 | 269.8 | NaN | 159.70 | 284.15 | NaN | NaN |  |
| InMemory | GetQueryable | 10000 | 8 | Streamed | 80 | 0 | 153 | 34 | 1 | 33 | 3 | 0 | 64.7 | NaN | 128.98 | 289.18 | NaN | NaN |  |
| InMemory | GetQueryable | 10000 | 16 | Buffered | 160 | 0 | 601 | 459 | 247 | 50 | 39 | 12 | 595.6 | NaN | 197.23 | 356.51 | NaN | NaN |  |
| InMemory | GetQueryable | 10000 | 16 | Streamed | 160 | 0 | 162 | 37 | 12 | 74 | 25 | 6 | 350.0 | NaN | 195.68 | 387.97 | NaN | NaN |  |
| InMemory | GetQueryable | 10000 | 32 | Buffered | 320 | 0 | 980 | 806 | 384 | 80 | 63 | 15 | 1586.8 | NaN | 379.45 | 624.00 | NaN | NaN |  |
| InMemory | GetQueryable | 10000 | 32 | Streamed | 320 | 0 | 176 | 37 | 1 | 144 | 95 | 9 | 1091.4 | NaN | 317.41 | 465.74 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 8 | Buffered | 80 | 0 | 376 | 242 | 140 | 27 | 20 | 7 | 267.5 | NaN | 172.08 | 218.84 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 8 | Streamed | 80 | 0 | 152 | 32 | 1 | 34 | 3 | 0 | 299.8 | NaN | 223.75 | 484.98 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 16 | Buffered | 160 | 0 | 575 | 434 | 221 | 48 | 35 | 11 | 1169.1 | NaN | 324.87 | 795.01 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 16 | Streamed | 160 | 0 | 165 | 36 | 1 | 74 | 21 | 5 | 855.8 | NaN | 282.08 | 408.28 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 32 | Buffered | 320 | 0 | 929 | 722 | 403 | 93 | 78 | 17 | 1307.1 | NaN | 305.54 | 440.22 | NaN | NaN |  |
| InMemory | GetODataQueryable | 10000 | 32 | Streamed | 320 | 0 | 179 | 47 | 2 | 147 | 86 | 8 | 1324.0 | NaN | 337.05 | 700.04 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 8 | Buffered | 80 | 0 | 379 | 244 | 131 | 66 | 62 | 17 | 1949.2 | NaN | 420.73 | 731.13 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 8 | Streamed | 80 | 0 | 152 | 28 | 1 | 70 | 24 | 3 | 494.1 | NaN | 288.43 | 405.76 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 16 | Buffered | 160 | 0 | 577 | 438 | 214 | 116 | 102 | 18 | 3798.1 | NaN | 654.88 | 883.23 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 16 | Streamed | 160 | 0 | 162 | 30 | 9 | 139 | 107 | 5 | 888.2 | NaN | 303.94 | 567.41 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 32 | Buffered | 320 | 0 | 1113 | 847 | 515 | 215 | 194 | 21 | 6840.4 | NaN | 1078.37 | 1454.56 | NaN | NaN |  |
| EfSqlite | GetQueryable | 10000 | 32 | Streamed | 320 | 0 | 175 | 33 | 2 | 293 | 231 | 24 | 3686.8 | NaN | 657.91 | 1376.29 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 8 | Buffered | 80 | 0 | 381 | 222 | 139 | 66 | 57 | 17 | 1298.5 | NaN | 322.81 | 465.95 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 8 | Streamed | 80 | 0 | 149 | 26 | 6 | 71 | 23 | 4 | 235.0 | NaN | 229.32 | 358.42 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 16 | Buffered | 160 | 0 | 602 | 458 | 235 | 121 | 107 | 19 | 3281.2 | NaN | 595.07 | 1037.08 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 16 | Streamed | 160 | 0 | 160 | 23 | 1 | 142 | 105 | 8 | 1365.5 | NaN | 363.41 | 732.50 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 32 | Buffered | 320 | 0 | 915 | 776 | 472 | 218 | 199 | 25 | 8964.2 | NaN | 1269.80 | 1820.09 | NaN | NaN |  |
| EfSqlite | GetODataQueryable | 10000 | 32 | Streamed | 320 | 0 | 181 | 34 | 10 | 302 | 230 | 31 | 3330.8 | NaN | 634.08 | 967.94 | NaN | NaN |  |
