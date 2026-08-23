using System;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;
using OhData.Server.Benchmarks.Benchmarks;
using OhData.Server.Benchmarks.Probes;
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
        // benchmarked scenario before any measurement runs.
        //
        // No child-process guard is needed, and the one that used to live here was dead code.
        // BenchmarkDotNet's default (out-of-process) toolchain does NOT re-enter this Main: it
        // generates a SEPARATE executable which references this assembly and has its own entry
        // point, then launches that. So this Main runs exactly once, in the parent, and the gate
        // below runs exactly once per invocation. The previous guard tested for a "--benchmarkName"
        // argument that never appears — passing it to BenchmarkSwitcher.Run is in fact rejected
        // ("Option 'benchmarkName' is unknown"), so the condition could never be true.
        //
        // Two consequences worth knowing, because both are load-bearing right above this comment:
        //   * Anything this process needs to hand its benchmark children travels by ordinary
        //     environment-variable inheritance (Process.Start with UseShellExecute=false), not by
        //     argument passing — the children never see this Main's argv. That is exactly why the
        //     resolved seed is exported to the environment rather than appended to switcherArgs.
        //   * Compile-time values (consts) reach them for free, because the generated child
        //     project references THIS assembly — which is why BenchOrgData.DefaultSeed needs no
        //     propagation machinery at all, and only the --seed override does.
        if (!await SmokeCheck.RunAsync(seed))
            return 1;

        // "--smoke" runs the correctness checks only.
        if (switcherArgs.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
            return 0;

        // #333: "--probe-levels" runs the $levels cost-attribution probe instead of any benchmark.
        // Investigation harness, not a measurement this suite publishes — see LevelsCostSplitProbe.
        if (switcherArgs.Contains("--probe-levels", StringComparer.OrdinalIgnoreCase))
        {
            await LevelsCostSplitProbe.RunAsync(seed);
            return 0;
        }

        BenchmarkSwitcher.FromTypes(new[]
        {
            typeof(ServerComparisonBenchmarks),
            typeof(ExpandComparisonBenchmarks),
            // #389: not a host-vs-host comparison at all — one JsonSerializer call, no HTTP. It
            // rides this switcher anyway because it needs the same OhData.AspNetCore project
            // reference and the same pinned-invocation-count discipline.
            typeof(OpenTypeKeyValidationBenchmarks),
            // #396: likewise not a host-vs-host comparison — one JsonSerializer call and one Write,
            // measuring what serializing an operation result eagerly (so a fault can still become a
            // 500 envelope) costs against serializing it straight into the response body.
            typeof(OperationResultBufferingBenchmarks),
            // #313: also not a host-vs-host comparison — both arms are OhData, differing only in
            // MaxExpandTop, measuring what the opt-in bare-$expand bound costs on the under-ceiling
            // requests a deployment that sets the knob will serve through it. MaxExpandTop is unset
            // by default, so this is a price someone chooses, not one the framework imposes.
            typeof(BareExpandCeilingBenchmarks),
            // #426: not a host-vs-host comparison either — both arms build the same
            // ODataQueryOptions, differing only in whether the ODataQueryContext is shared (the
            // pre-fix shape, which raced) or built per request (the shipped, correct one).
            typeof(QueryContextConstructionBenchmarks),
        }).Run(switcherArgs);
        return 0;
    }
}
