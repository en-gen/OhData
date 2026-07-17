```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8037/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3


```
| Method     | Mean       | Error     | StdDev     | Ratio  | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------- |-----------:|----------:|-----------:|-------:|--------:|-------:|----------:|------------:|
| NoOptions  |   2.261 ns | 0.2294 ns |  0.6765 ns |   1.09 |    0.47 |      - |         - |          NA |
| FilterOnly |  83.941 ns | 4.7854 ns | 14.1098 ns |  40.58 |   14.12 | 0.0191 |     320 B |          NA |
| AllOptions | 350.159 ns | 6.8096 ns |  7.2862 ns | 169.29 |   51.07 | 0.0854 |    1432 B |          NA |
