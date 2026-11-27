# Streaming collection probe (SPIKE #867)

- commit: `a2f64d73fd84b87dee0b8d2ba15d03b0ae8ce102` (DIRTY working tree)
- host: Kestrel; requests per cell per arm: 30 (+3 warm-up pairs, excluded); arms interleaved
- machine: AMD Ryzen 9 5950X 16-Core Processor; logical processors 32; 63.9 GiB available to the GC
- OS: Microsoft Windows 10.0.26300; runtime: .NET 10.0.15; GC: Workstation, latency Interactive
- run at (UTC): 2026-11-27T20:39:07.7953907Z
- peak heap = max sampled GC.GetTotalMemory(false) (~100 us sampler) minus post-GC baseline: a sampled lower bound. Allocated bytes and GC counts are process-wide (server and client share the process).

| backend | route | rows | mode | body KB | TTFB med ms | TTFB max ms | total med ms | peak heap med KB | peak heap max KB | alloc med KB | gen0 med | gen1 med | gen2 med | gen2 max |
|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| InMemory | GetQueryable | 100 | Buffered | 21 | 4.49 | 7.64 | 4.55 | 329 | 337 | 314 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 100 | Streamed | 21 | 4.43 | 7.67 | 4.48 | 336 | 344 | 313 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 1000 | Buffered | 215 | 14.63 | 47.04 | 14.95 | 1338 | 1376 | 1305 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 1000 | Streamed | 215 | 5.26 | 8.63 | 15.03 | 971 | 1034 | 934 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 10000 | Buffered | 2181 | 72.46 | 91.63 | 76.38 | 16637 | 16668 | 16932 | 1 | 1 | 1 | 1 |
| InMemory | GetQueryable | 10000 | Streamed | 2181 | 7.53 | 9.93 | 73.99 | 6799 | 6912 | 6760 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 100 | Buffered | 21 | 5.37 | 11.02 | 5.40 | 499 | 504 | 480 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 100 | Streamed | 21 | 5.36 | 11.80 | 5.38 | 497 | 516 | 481 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 1000 | Buffered | 215 | 6.18 | 11.18 | 6.28 | 1496 | 1521 | 1464 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 1000 | Streamed | 215 | 4.28 | 5.61 | 5.98 | 1119 | 1189 | 1090 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 10000 | Buffered | 2181 | 42.91 | 48.48 | 44.60 | 14248 | 14889 | 14916 | 1 | 1 | 1 | 1 |
| InMemory | GetODataQueryable | 10000 | Streamed | 2181 | 4.59 | 7.99 | 23.76 | 6957 | 7129 | 6927 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 100 | Buffered | 21 | 2.52 | 3.68 | 2.55 | 295 | 307 | 282 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 100 | Streamed | 21 | 2.53 | 5.36 | 2.55 | 289 | 303 | 281 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 1000 | Buffered | 215 | 9.26 | 11.31 | 9.40 | 1888 | 1919 | 1863 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 1000 | Streamed | 215 | 2.61 | 4.75 | 9.64 | 1507 | 1558 | 1484 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 10000 | Buffered | 2180 | 66.24 | 112.25 | 68.62 | 20439 | 21395 | 21331 | 1 | 1 | 1 | 1 |
| EfSqlite | GetQueryable | 10000 | Streamed | 2180 | 2.02 | 3.22 | 48.19 | 13242 | 13360 | 13192 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 100 | Buffered | 21 | 1.84 | 4.11 | 1.87 | 301 | 309 | 287 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 100 | Streamed | 21 | 1.87 | 4.84 | 1.89 | 296 | 304 | 288 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 1000 | Buffered | 215 | 5.81 | 7.80 | 5.96 | 1888 | 1920 | 1861 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 1000 | Streamed | 215 | 1.96 | 3.12 | 6.17 | 1525 | 1556 | 1495 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 10000 | Buffered | 2180 | 66.83 | 85.75 | 68.81 | 20311 | 21271 | 21208 | 1 | 1 | 1 | 1 |
| EfSqlite | GetODataQueryable | 10000 | Streamed | 2180 | 1.86 | 3.79 | 47.48 | 13242 | 13358 | 13183 | 0 | 0 | 0 | 0 |
