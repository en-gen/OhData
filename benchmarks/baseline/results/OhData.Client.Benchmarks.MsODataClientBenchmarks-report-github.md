```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8037/25H2/2025Update/HudsonValley2)
AMD Ryzen 9 5950X 3.40GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3


```
| Method             | Mean | Error | Ratio | RatioSD | Alloc Ratio |
|------------------- |-----:|------:|------:|--------:|------------:|
| GetAll             |   NA |    NA |     ? |       ? |           ? |
| FilterByName       |   NA |    NA |     ? |       ? |           ? |
| GetByKey_ViaFilter |   NA |    NA |     ? |       ? |           ? |
| Top                |   NA |    NA |     ? |       ? |           ? |
| Count              |   NA |    NA |     ? |       ? |           ? |
| Insert             |   NA |    NA |     ? |       ? |           ? |

Benchmarks with issues:
  MsODataClientBenchmarks.GetAll: DefaultJob
  MsODataClientBenchmarks.FilterByName: DefaultJob
  MsODataClientBenchmarks.GetByKey_ViaFilter: DefaultJob
  MsODataClientBenchmarks.Top: DefaultJob
  MsODataClientBenchmarks.Count: DefaultJob
  MsODataClientBenchmarks.Insert: DefaultJob
