using BenchmarkDotNet.Running;
using OhData.Client.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
