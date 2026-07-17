using System;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;
using OhData.Server.Benchmarks.Benchmarks;
using OhData.Server.Benchmarks.Smoke;

namespace OhData.Server.Benchmarks;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Correctness gate: both hosts must return semantically equivalent responses for every
        // benchmarked scenario before any measurement runs. Skipped inside BenchmarkDotNet's
        // spawned child processes (they re-enter Main with --benchmarkName filters).
        bool isChildBenchmarkProcess = args.Any(a => a.StartsWith("--benchmarkName", StringComparison.OrdinalIgnoreCase));
        if (!isChildBenchmarkProcess)
        {
            if (!await SmokeCheck.RunAsync())
                return 1;

            // "--smoke" runs the correctness checks only.
            if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
                return 0;
        }

        BenchmarkSwitcher.FromTypes(new[] { typeof(ServerComparisonBenchmarks) }).Run(args);
        return 0;
    }
}
