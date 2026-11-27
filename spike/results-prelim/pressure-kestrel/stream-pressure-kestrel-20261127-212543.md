# Streaming collection pressure probe (SPIKE #867)

- commit: `536d8f32676b3a3e7670412e4709af674d45a862` (DIRTY working tree); host: kestrel; requests per cell: 30 (concurrent mode: 30 rounds of N); one fresh child process per cell and arm
- GC env: DOTNET_gcServer=0; gen0 max budget: 4 MB (DOTNET_GCgen0MaxBudget); heap hard limit: none
- machine: AMD Ryzen 9 5950X 16-Core Processor; logical processors 32; OS: Microsoft Windows 10.0.26300; runtime: .NET 10.0.15
- run at (UTC): 2026-11-27T21:25:43.6005205Z
- peak WS = Process.PeakWorkingSet64 of the cell's own child (includes runtime, host, 10k-row fixtures: compare arms, not absolutes). Gen counts and pause are totals over the measured requests; per-request medians in the CSV.

| backend | route | rows | conc | mode | ok | failed | peak WS MB | max heap MB | max promoted MB | gen0 | gen1 | gen2 | GC pause ms | TTFB med ms | total med ms | total max ms | peak heap med KB | alloc med KB | errors |
|---|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| InMemory | GetQueryable | 100 | 1 | Buffered | 30 | 0 | 133 | 9 | 7 | 60 | 60 | 60 | 416.7 | 5.19 | 5.23 | 43.08 | 330 | 314 |  |
| InMemory | GetQueryable | 100 | 1 | Streamed | 30 | 0 | 138 | 9 | 7 | 60 | 60 | 60 | 332.8 | 5.19 | 5.27 | 13.93 | 330 | 313 |  |
| InMemory | GetQueryable | 1000 | 1 | Buffered | 30 | 0 | 135 | 10 | 7 | 60 | 60 | 60 | 399.9 | 17.12 | 17.28 | 58.73 | 1362 | 1337 |  |
| InMemory | GetQueryable | 1000 | 1 | Streamed | 30 | 0 | 138 | 8 | 7 | 60 | 60 | 60 | 475.9 | 8.59 | 20.10 | 69.75 | 946 | 910 |  |
| InMemory | GetQueryable | 10000 | 1 | Buffered | 30 | 0 | 167 | 55 | 49 | 75 | 75 | 75 | 533.4 | 104.71 | 121.27 | 155.06 | 15801 | 15935 |  |
| InMemory | GetQueryable | 10000 | 1 | Streamed | 30 | 0 | 138 | 10 | 0 | 90 | 60 | 60 | 538.9 | 8.33 | 121.34 | 342.07 | 4068 | 6686 |  |
| InMemory | GetODataQueryable | 100 | 1 | Buffered | 30 | 0 | 134 | 9 | 7 | 60 | 60 | 60 | 368.3 | 7.29 | 7.35 | 10.85 | 496 | 480 |  |
| InMemory | GetODataQueryable | 100 | 1 | Streamed | 30 | 0 | 134 | 9 | 7 | 60 | 60 | 60 | 352.7 | 6.07 | 6.12 | 22.51 | 497 | 481 |  |
| InMemory | GetODataQueryable | 1000 | 1 | Buffered | 30 | 0 | 138 | 9 | 7 | 60 | 60 | 60 | 414.2 | 18.84 | 19.08 | 65.08 | 1521 | 1496 |  |
| InMemory | GetODataQueryable | 1000 | 1 | Streamed | 30 | 0 | 138 | 8 | 7 | 60 | 60 | 60 | 389.3 | 7.04 | 21.39 | 44.86 | 1110 | 1074 |  |
| InMemory | GetODataQueryable | 10000 | 1 | Buffered | 30 | 0 | 149 | 26 | 22 | 90 | 90 | 90 | 542.0 | 110.96 | 128.08 | 162.00 | 16785 | 17003 |  |
| InMemory | GetODataQueryable | 10000 | 1 | Streamed | 30 | 0 | 134 | 10 | 0 | 90 | 60 | 60 | 506.5 | 8.76 | 121.23 | 203.92 | 4079 | 6884 |  |
| EfSqlite | GetQueryable | 100 | 1 | Buffered | 30 | 0 | 138 | 5 | 4 | 60 | 60 | 60 | 401.8 | 4.87 | 4.93 | 6.50 | 305 | 289 |  |
| EfSqlite | GetQueryable | 100 | 1 | Streamed | 30 | 0 | 136 | 5 | 4 | 60 | 60 | 60 | 404.9 | 5.09 | 5.15 | 12.18 | 313 | 288 |  |
| EfSqlite | GetQueryable | 1000 | 1 | Buffered | 30 | 0 | 136 | 6 | 4 | 60 | 60 | 60 | 372.8 | 16.66 | 16.85 | 62.60 | 1935 | 1902 |  |
| EfSqlite | GetQueryable | 1000 | 1 | Streamed | 30 | 0 | 143 | 5 | 4 | 60 | 60 | 60 | 349.2 | 4.19 | 16.81 | 28.64 | 1512 | 1472 |  |
| EfSqlite | GetQueryable | 10000 | 1 | Buffered | 30 | 0 | 151 | 27 | 4 | 150 | 105 | 90 | 681.7 | 127.82 | 130.48 | 208.53 | 20063 | 23929 |  |
| EfSqlite | GetQueryable | 10000 | 1 | Streamed | 30 | 0 | 136 | 6 | 0 | 150 | 63 | 60 | 422.2 | 4.12 | 136.35 | 158.86 | 4597 | 13175 |  |
| EfSqlite | GetODataQueryable | 100 | 1 | Buffered | 30 | 0 | 136 | 5 | 4 | 60 | 60 | 60 | 322.0 | 4.35 | 4.40 | 8.54 | 329 | 300 |  |
| EfSqlite | GetODataQueryable | 100 | 1 | Streamed | 30 | 0 | 142 | 5 | 4 | 60 | 60 | 60 | 334.3 | 3.87 | 3.91 | 15.95 | 325 | 300 |  |
| EfSqlite | GetODataQueryable | 1000 | 1 | Buffered | 30 | 0 | 136 | 6 | 4 | 60 | 60 | 60 | 337.4 | 18.45 | 18.62 | 45.49 | 1935 | 1906 |  |
| EfSqlite | GetODataQueryable | 1000 | 1 | Streamed | 30 | 0 | 137 | 5 | 4 | 60 | 60 | 60 | 310.7 | 4.46 | 18.70 | 23.86 | 1520 | 1484 |  |
| EfSqlite | GetODataQueryable | 10000 | 1 | Buffered | 30 | 0 | 151 | 25 | 14 | 120 | 90 | 90 | 721.9 | 120.90 | 129.60 | 284.58 | 19215 | 22274 |  |
| EfSqlite | GetODataQueryable | 10000 | 1 | Streamed | 30 | 0 | 137 | 6 | 0 | 150 | 62 | 60 | 348.3 | 3.91 | 129.50 | 156.49 | 4530 | 13163 |  |
