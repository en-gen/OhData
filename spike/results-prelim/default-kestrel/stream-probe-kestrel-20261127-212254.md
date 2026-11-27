# Streaming collection probe (SPIKE #867)

- commit: `536d8f32676b3a3e7670412e4709af674d45a862` (DIRTY working tree)
- host: Kestrel; requests per cell per arm: 30 (+3 warm-up pairs, excluded); arms interleaved
- machine: AMD Ryzen 9 5950X 16-Core Processor; logical processors 32; 63.9 GiB available to the GC
- OS: Microsoft Windows 10.0.26300; runtime: .NET 10.0.15; GC: Workstation, latency Interactive
- run at (UTC): 2026-11-27T21:22:54.2090282Z
- peak heap = max sampled GC.GetTotalMemory(false) (~100 us sampler) minus post-GC baseline: a sampled lower bound. Allocated bytes and GC counts are process-wide (server and client share the process).

| backend | route | rows | mode | body KB | TTFB med ms | TTFB max ms | total med ms | peak heap med KB | peak heap max KB | alloc med KB | gen0 med | gen1 med | gen2 med | gen2 max |
|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| InMemory | GetQueryable | 100 | Buffered | 21 | 4.58 | 17.32 | 4.64 | 329 | 344 | 314 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 100 | Streamed | 21 | 4.62 | 10.76 | 4.71 | 329 | 344 | 313 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 1000 | Buffered | 215 | 14.38 | 46.09 | 14.74 | 1342 | 1378 | 1306 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 1000 | Streamed | 215 | 4.74 | 10.53 | 15.04 | 959 | 1045 | 922 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 10000 | Buffered | 2181 | 51.94 | 328.07 | 68.43 | 16633 | 16669 | 16932 | 1 | 1 | 1 | 1 |
| InMemory | GetQueryable | 10000 | Streamed | 2181 | 6.23 | 13.92 | 52.94 | 6812 | 6928 | 6764 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 100 | Buffered | 21 | 5.38 | 9.55 | 5.41 | 494 | 504 | 480 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 100 | Streamed | 21 | 5.05 | 9.36 | 5.07 | 495 | 505 | 481 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 1000 | Buffered | 215 | 6.51 | 26.67 | 6.71 | 1490 | 1519 | 1464 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 1000 | Streamed | 215 | 4.81 | 20.44 | 7.02 | 1114 | 1172 | 1090 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 10000 | Buffered | 2181 | 36.25 | 44.42 | 40.16 | 14234 | 14674 | 14914 | 1 | 1 | 1 | 1 |
| InMemory | GetODataQueryable | 10000 | Streamed | 2181 | 3.68 | 5.07 | 21.60 | 6921 | 7098 | 6891 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 100 | Buffered | 21 | 2.33 | 2.90 | 2.35 | 303 | 311 | 290 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 100 | Streamed | 21 | 2.15 | 2.75 | 2.17 | 302 | 304 | 289 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 1000 | Buffered | 215 | 8.95 | 11.64 | 9.05 | 1895 | 1931 | 1870 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 1000 | Streamed | 215 | 2.63 | 8.07 | 9.20 | 1515 | 1593 | 1491 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 10000 | Buffered | 2180 | 61.55 | 88.56 | 66.02 | 20439 | 21347 | 21334 | 1 | 1 | 1 | 1 |
| EfSqlite | GetQueryable | 10000 | Streamed | 2180 | 1.84 | 7.80 | 44.93 | 13233 | 13412 | 13180 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 100 | Buffered | 21 | 1.80 | 2.19 | 1.82 | 309 | 317 | 296 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 100 | Streamed | 21 | 1.70 | 2.19 | 1.72 | 310 | 310 | 296 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 1000 | Buffered | 215 | 5.75 | 8.37 | 5.84 | 1896 | 1927 | 1869 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 1000 | Streamed | 215 | 1.84 | 15.14 | 5.87 | 1525 | 1568 | 1499 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 10000 | Buffered | 2180 | 61.96 | 87.84 | 69.77 | 20327 | 21267 | 21220 | 1 | 1 | 1 | 1 |
| EfSqlite | GetODataQueryable | 10000 | Streamed | 2180 | 2.46 | 5.55 | 49.59 | 13264 | 13449 | 13218 | 0 | 0 | 0 | 0 |
