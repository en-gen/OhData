# Streaming collection pressure probe (SPIKE #867)

- commit: `536d8f32676b3a3e7670412e4709af674d45a862` (DIRTY working tree); host: testserver; requests per cell: 30 (concurrent mode: 30 rounds of N); one fresh child process per cell and arm
- GC env: DOTNET_gcServer=0; gen0 max budget: 4 MB (DOTNET_GCgen0MaxBudget); heap hard limit: none
- machine: AMD Ryzen 9 5950X 16-Core Processor; logical processors 32; OS: Microsoft Windows 10.0.26300; runtime: .NET 10.0.15
- run at (UTC): 2026-11-27T21:18:01.4268091Z
- peak WS = Process.PeakWorkingSet64 of the cell's own child (includes runtime, host, 10k-row fixtures: compare arms, not absolutes). Gen counts and pause are totals over the measured requests; per-request medians in the CSV.

| backend | route | rows | conc | mode | ok | failed | peak WS MB | max heap MB | max promoted MB | gen0 | gen1 | gen2 | GC pause ms | TTFB med ms | total med ms | total max ms | peak heap med KB | alloc med KB | errors |
|---|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| InMemory | GetQueryable | 100 | 1 | Buffered | 30 | 0 | 136 | 9 | 7 | 60 | 60 | 60 | 333.7 | 5.39 | 5.41 | 8.32 | 293 | 273 |  |
| InMemory | GetQueryable | 100 | 1 | Streamed | 30 | 0 | 135 | 9 | 7 | 60 | 60 | 60 | 250.4 | 4.15 | 4.16 | 7.05 | 293 | 272 |  |
| InMemory | GetQueryable | 1000 | 1 | Buffered | 30 | 0 | 134 | 11 | 8 | 60 | 60 | 60 | 209.7 | 12.36 | 12.45 | 31.88 | 875 | 854 |  |
| InMemory | GetQueryable | 1000 | 1 | Streamed | 30 | 0 | 136 | 8 | 7 | 60 | 60 | 60 | 368.3 | 8.15 | 16.68 | 39.08 | 934 | 906 |  |
| InMemory | GetQueryable | 10000 | 1 | Buffered | 30 | 0 | 139 | 25 | 21 | 90 | 90 | 90 | 400.5 | 101.77 | 102.43 | 118.29 | 16668 | 17012 |  |
| InMemory | GetQueryable | 10000 | 1 | Streamed | 30 | 0 | 137 | 10 | 0 | 90 | 60 | 60 | 487.5 | 6.86 | 95.09 | 182.57 | 4075 | 6702 |  |
| InMemory | GetODataQueryable | 100 | 1 | Buffered | 30 | 0 | 135 | 9 | 7 | 60 | 60 | 60 | 330.7 | 5.97 | 6.00 | 15.64 | 457 | 438 |  |
| InMemory | GetODataQueryable | 100 | 1 | Streamed | 30 | 0 | 136 | 9 | 7 | 60 | 60 | 60 | 284.2 | 5.95 | 5.97 | 11.08 | 456 | 439 |  |
| InMemory | GetODataQueryable | 1000 | 1 | Buffered | 30 | 0 | 136 | 9 | 7 | 60 | 60 | 60 | 354.7 | 16.83 | 16.93 | 48.90 | 1541 | 1516 |  |
| InMemory | GetODataQueryable | 1000 | 1 | Streamed | 30 | 0 | 137 | 9 | 7 | 60 | 60 | 60 | 382.2 | 6.92 | 17.01 | 36.22 | 1101 | 1072 |  |
| InMemory | GetODataQueryable | 10000 | 1 | Buffered | 30 | 0 | 139 | 25 | 19 | 90 | 90 | 90 | 473.3 | 87.06 | 87.79 | 133.51 | 16795 | 17051 |  |
| InMemory | GetODataQueryable | 10000 | 1 | Streamed | 30 | 0 | 135 | 10 | 0 | 90 | 60 | 60 | 503.5 | 8.71 | 101.98 | 156.07 | 4083 | 6874 |  |
| EfSqlite | GetQueryable | 100 | 1 | Buffered | 30 | 0 | 136 | 5 | 4 | 60 | 60 | 60 | 193.1 | 3.75 | 3.77 | 10.79 | 324 | 301 |  |
| EfSqlite | GetQueryable | 100 | 1 | Streamed | 30 | 0 | 135 | 5 | 4 | 60 | 60 | 60 | 267.3 | 4.05 | 4.13 | 15.88 | 323 | 300 |  |
| EfSqlite | GetQueryable | 1000 | 1 | Buffered | 30 | 0 | 135 | 6 | 4 | 60 | 60 | 60 | 236.5 | 16.71 | 16.82 | 45.96 | 1952 | 1920 |  |
| EfSqlite | GetQueryable | 1000 | 1 | Streamed | 30 | 0 | 134 | 5 | 4 | 60 | 60 | 60 | 303.0 | 4.47 | 18.68 | 27.01 | 1497 | 1467 |  |
| EfSqlite | GetQueryable | 10000 | 1 | Buffered | 30 | 0 | 144 | 25 | 24 | 150 | 105 | 90 | 833.3 | 147.65 | 148.23 | 282.80 | 19588 | 23469 |  |
| EfSqlite | GetQueryable | 10000 | 1 | Streamed | 30 | 0 | 136 | 5 | 0 | 150 | 63 | 60 | 375.2 | 4.31 | 150.15 | 203.92 | 4589 | 13157 |  |
| EfSqlite | GetODataQueryable | 100 | 1 | Buffered | 30 | 0 | 136 | 5 | 4 | 60 | 60 | 60 | 181.4 | 3.74 | 3.76 | 14.05 | 335 | 312 |  |
| EfSqlite | GetODataQueryable | 100 | 1 | Streamed | 30 | 0 | 136 | 5 | 4 | 60 | 60 | 60 | 164.7 | 3.54 | 3.62 | 10.25 | 335 | 312 |  |
| EfSqlite | GetODataQueryable | 1000 | 1 | Buffered | 30 | 0 | 135 | 6 | 4 | 60 | 60 | 60 | 281.2 | 16.05 | 16.15 | 58.35 | 1949 | 1924 |  |
| EfSqlite | GetODataQueryable | 1000 | 1 | Streamed | 30 | 0 | 134 | 5 | 4 | 60 | 60 | 60 | 267.3 | 4.84 | 18.13 | 67.25 | 1501 | 1477 |  |
| EfSqlite | GetODataQueryable | 10000 | 1 | Buffered | 30 | 0 | 145 | 25 | 4 | 150 | 105 | 90 | 615.4 | 139.99 | 140.46 | 189.82 | 19660 | 23354 |  |
| EfSqlite | GetODataQueryable | 10000 | 1 | Streamed | 30 | 0 | 135 | 5 | 0 | 150 | 63 | 60 | 347.9 | 3.65 | 139.67 | 295.40 | 4586 | 13177 |  |
