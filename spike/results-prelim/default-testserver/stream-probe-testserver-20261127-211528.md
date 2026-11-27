# Streaming collection probe (SPIKE #867)

- commit: `536d8f32676b3a3e7670412e4709af674d45a862` (clean)
- host: TestServer; requests per cell per arm: 30 (+3 warm-up pairs, excluded); arms interleaved
- machine: AMD Ryzen 9 5950X 16-Core Processor; logical processors 32; 63.9 GiB available to the GC
- OS: Microsoft Windows 10.0.26300; runtime: .NET 10.0.15; GC: Workstation, latency Interactive
- run at (UTC): 2026-11-27T21:15:28.2877746Z
- peak heap = max sampled GC.GetTotalMemory(false) (~100 us sampler) minus post-GC baseline: a sampled lower bound. Allocated bytes and GC counts are process-wide (server and client share the process).

| backend | route | rows | mode | body KB | TTFB med ms | TTFB max ms | total med ms | peak heap med KB | peak heap max KB | alloc med KB | gen0 med | gen1 med | gen2 med | gen2 max |
|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| InMemory | GetQueryable | 100 | Buffered | 21 | 4.59 | 8.67 | 4.61 | 293 | 345 | 273 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 100 | Streamed | 21 | 4.47 | 66.02 | 4.48 | 293 | 301 | 272 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 1000 | Buffered | 215 | 13.73 | 53.68 | 13.84 | 1366 | 1386 | 1343 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 1000 | Streamed | 215 | 6.02 | 12.91 | 14.30 | 930 | 969 | 899 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 10000 | Buffered | 2181 | 78.59 | 149.77 | 79.20 | 16669 | 16693 | 17008 | 1 | 1 | 1 | 1 |
| InMemory | GetQueryable | 10000 | Streamed | 2181 | 7.36 | 28.60 | 70.67 | 6762 | 6832 | 6731 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 100 | Buffered | 21 | 4.95 | 7.06 | 4.96 | 511 | 520 | 488 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 100 | Streamed | 21 | 4.52 | 7.26 | 4.57 | 511 | 520 | 488 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 1000 | Buffered | 215 | 5.84 | 9.33 | 5.91 | 1498 | 1531 | 1478 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 1000 | Streamed | 215 | 4.37 | 9.65 | 6.10 | 1103 | 1124 | 1082 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 10000 | Buffered | 2181 | 39.68 | 56.51 | 40.10 | 14262 | 14958 | 14987 | 1 | 1 | 1 | 1 |
| InMemory | GetODataQueryable | 10000 | Streamed | 2181 | 3.32 | 6.14 | 21.37 | 6908 | 6951 | 6893 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 100 | Buffered | 21 | 2.45 | 4.43 | 2.45 | 309 | 317 | 297 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 100 | Streamed | 21 | 2.18 | 4.35 | 2.21 | 317 | 320 | 296 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 1000 | Buffered | 215 | 9.71 | 15.79 | 9.80 | 1902 | 1939 | 1884 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 1000 | Streamed | 215 | 2.97 | 8.73 | 10.20 | 1503 | 1536 | 1480 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 10000 | Buffered | 2180 | 54.45 | 85.19 | 54.99 | 21443 | 21452 | 21409 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 10000 | Streamed | 2180 | 2.00 | 3.38 | 51.26 | 13227 | 13331 | 13183 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 100 | Buffered | 21 | 1.77 | 2.98 | 1.77 | 315 | 324 | 303 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 100 | Streamed | 21 | 1.67 | 2.98 | 1.69 | 316 | 324 | 303 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 1000 | Buffered | 215 | 5.81 | 14.74 | 5.86 | 1901 | 1939 | 1883 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 1000 | Streamed | 215 | 1.70 | 3.59 | 6.00 | 1509 | 1532 | 1486 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 10000 | Buffered | 2180 | 59.70 | 244.76 | 60.24 | 21323 | 21332 | 21291 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 10000 | Streamed | 2180 | 2.38 | 5.90 | 52.51 | 13235 | 13302 | 13192 | 0 | 0 | 0 | 0 |
