# Streaming collection probe (SPIKE #867)

- commit: `a2f64d73fd84b87dee0b8d2ba15d03b0ae8ce102` (clean)
- host: TestServer; requests per cell per arm: 30 (+3 warm-up pairs, excluded); arms interleaved
- machine: AMD Ryzen 9 5950X 16-Core Processor; logical processors 32; 63.9 GiB available to the GC
- OS: Microsoft Windows 10.0.26300; runtime: .NET 10.0.15; GC: Workstation, latency Interactive
- run at (UTC): 2026-11-27T20:38:15.3984294Z
- peak heap = max sampled GC.GetTotalMemory(false) (~100 us sampler) minus post-GC baseline: a sampled lower bound. Allocated bytes and GC counts are process-wide (server and client share the process).

| backend | route | rows | mode | body KB | TTFB med ms | TTFB max ms | total med ms | peak heap med KB | peak heap max KB | alloc med KB | gen0 med | gen1 med | gen2 med | gen2 max |
|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| InMemory | GetQueryable | 100 | Buffered | 21 | 4.39 | 8.27 | 4.40 | 349 | 357 | 326 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 100 | Streamed | 21 | 4.06 | 5.67 | 4.07 | 348 | 356 | 325 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 1000 | Buffered | 215 | 13.01 | 39.99 | 13.09 | 1371 | 1394 | 1347 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 1000 | Streamed | 215 | 5.02 | 8.90 | 13.18 | 957 | 1020 | 930 | 0 | 0 | 0 | 0 |
| InMemory | GetQueryable | 10000 | Buffered | 2181 | 63.57 | 129.84 | 64.16 | 14774 | 14783 | 14955 | 2 | 2 | 0 | 0 |
| InMemory | GetQueryable | 10000 | Streamed | 2181 | 7.16 | 15.64 | 67.77 | 6771 | 6878 | 6732 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 100 | Buffered | 21 | 4.76 | 9.13 | 4.77 | 500 | 508 | 488 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 100 | Streamed | 21 | 4.11 | 15.06 | 4.12 | 501 | 509 | 488 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 1000 | Buffered | 215 | 5.88 | 13.10 | 5.91 | 1494 | 1534 | 1478 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 1000 | Streamed | 215 | 4.39 | 8.63 | 6.14 | 1094 | 1128 | 1096 | 0 | 0 | 0 | 0 |
| InMemory | GetODataQueryable | 10000 | Buffered | 2181 | 37.67 | 68.84 | 38.11 | 14780 | 15000 | 14992 | 1 | 1 | 0 | 1 |
| InMemory | GetODataQueryable | 10000 | Streamed | 2181 | 4.53 | 13.19 | 25.52 | 6914 | 7022 | 6893 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 100 | Buffered | 21 | 2.24 | 30.61 | 2.24 | 302 | 310 | 289 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 100 | Streamed | 21 | 2.29 | 14.70 | 2.29 | 302 | 310 | 288 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 1000 | Buffered | 215 | 9.58 | 33.82 | 9.65 | 1886 | 1926 | 1876 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 1000 | Streamed | 215 | 3.71 | 10.78 | 9.50 | 1494 | 1526 | 1471 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 10000 | Buffered | 2180 | 64.43 | 128.86 | 64.87 | 21432 | 21445 | 21401 | 0 | 0 | 0 | 0 |
| EfSqlite | GetQueryable | 10000 | Streamed | 2180 | 2.10 | 6.64 | 56.43 | 13223 | 13323 | 13173 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 100 | Buffered | 21 | 1.85 | 5.08 | 1.85 | 307 | 315 | 294 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 100 | Streamed | 21 | 1.84 | 3.67 | 1.85 | 308 | 316 | 295 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 1000 | Buffered | 215 | 6.48 | 16.54 | 6.54 | 1890 | 1925 | 1874 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 1000 | Streamed | 215 | 2.26 | 8.45 | 5.93 | 1502 | 1565 | 1479 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 10000 | Buffered | 2180 | 64.62 | 87.48 | 65.24 | 21315 | 21328 | 21283 | 0 | 0 | 0 | 0 |
| EfSqlite | GetODataQueryable | 10000 | Streamed | 2180 | 2.34 | 10.02 | 57.20 | 13237 | 13303 | 13185 | 0 | 0 | 0 | 0 |
