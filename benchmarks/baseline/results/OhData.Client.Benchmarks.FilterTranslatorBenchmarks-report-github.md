```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8037/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3


```
| Method                | Mean       | Error    | StdDev   | Median     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------- |-----------:|---------:|---------:|-----------:|------:|--------:|-------:|----------:|------------:|
| Simple                |   105.1 ns |  5.57 ns | 16.41 ns |   101.6 ns |  1.02 |    0.22 | 0.0138 |     232 B |        1.00 |
| Medium                |   234.0 ns |  7.10 ns | 20.04 ns |   237.2 ns |  2.28 |    0.38 | 0.0334 |     560 B |        2.41 |
| Complex               |   416.2 ns | 17.35 ns | 51.15 ns |   400.4 ns |  4.05 |    0.77 | 0.0510 |     856 B |        3.69 |
| WithCapturedVariables | 2,545.6 ns | 29.07 ns | 24.27 ns | 2,538.1 ns | 24.77 |    3.58 | 0.1755 |    3016 B |       13.00 |
