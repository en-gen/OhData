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
        // Resolve the dataset seed and export it unconditionally (even the const default) so every
        // BenchmarkDotNet child process this run spawns agrees on it — see BenchSeedResolver.
        (int seed, string seedSource) = BenchSeedResolver.Resolve(args);
        Environment.SetEnvironmentVariable(BenchSeedResolver.EnvVarName, seed.ToString());
        Console.WriteLine($"── Bench data seed: {seed} (source: {seedSource}) — replay with --seed {seed} ──");

        // BenchmarkDotNet rejects unrecognized arguments, so --seed must not reach it.
        string[] switcherArgs = BenchSeedResolver.StripSeedArgument(args);

        // Correctness gate: both hosts must return semantically equivalent responses for every
        // benchmarked scenario before any measurement runs. Skipped inside BenchmarkDotNet's
        // spawned child processes (they re-enter Main with --benchmarkName filters).
        bool isChildBenchmarkProcess = switcherArgs.Any(a => a.StartsWith("--benchmarkName", StringComparison.OrdinalIgnoreCase));
        if (!isChildBenchmarkProcess)
        {
            if (!await SmokeCheck.RunAsync(seed))
                return 1;

            // "--smoke" runs the correctness checks only.
            if (switcherArgs.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
                return 0;
        }

        BenchmarkSwitcher.FromTypes(new[] { typeof(ServerComparisonBenchmarks), typeof(ExpandComparisonBenchmarks) }).Run(switcherArgs);
        return 0;
    }
}
