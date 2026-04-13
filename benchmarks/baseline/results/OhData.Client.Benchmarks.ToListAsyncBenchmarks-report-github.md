```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8037/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3


```
| Method                 | Mean       | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------- |-----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| ToListAsync_NoOptions  |   632.7 μs | 12.38 μs | 11.58 μs |  1.00 |    0.02 | 0.9766 |  26.87 KB |        1.00 |
| ToListAsync_WithFilter | 1,679.6 μs | 33.23 μs | 72.23 μs |  2.66 |    0.12 |      - |   39.9 KB |        1.49 |
| CountAsync             |   379.6 μs |  7.24 μs |  6.41 μs |  0.60 |    0.01 | 0.9766 |  20.57 KB |        0.77 |
