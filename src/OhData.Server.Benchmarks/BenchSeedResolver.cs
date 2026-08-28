using System;
using System.Collections.Generic;
using System.Linq;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks;

/// <summary>
/// Resolves the RNG seed driving <see cref="BenchOrgData"/>'s generated dataset, most-specific-wins:
/// <list type="number">
/// <item><description><c>--seed N</c> on the command line — ad-hoc replay/exploration of a specific
/// data shape.</description></item>
/// <item><description><see cref="EnvVarName"/> environment variable — CI pinning without touching the
/// command line, and (see below) the channel that carries a <c>--seed</c> override into BenchmarkDotNet's
/// spawned child processes.</description></item>
/// <item><description><see cref="BenchOrgData.DefaultSeed"/> — the committed, version-controlled default.
/// A plain <c>const</c> is baked into the compiled assembly, so every BenchmarkDotNet child process
/// resolves it identically with no runtime propagation involved at all.</description></item>
/// </list>
/// <para>
/// <b>Why the environment variable matters despite the const default:</b> BenchmarkDotNet's toolchain
/// spawns each benchmark in a separate child process re-invoking <c>Program.Main</c> with its OWN
/// arguments (<c>--benchmarkName ...</c>) — never the original process's command line — so a runtime
/// <c>--seed 42</c> override does not reach children by itself. <c>Program.Main</c> exports whatever
/// seed it resolves to <see cref="EnvVarName"/> via <see cref="Environment.SetEnvironmentVariable(string, string)"/>
/// unconditionally (even when the value came from the const default) before handing control to
/// BenchmarkDotNet, and every child process inherits the parent's environment when spawned via
/// <c>Process.Start</c> with <c>UseShellExecute = false</c> (BenchmarkDotNet's mechanism) — verified
/// empirically for this exact pattern (runtime-set env var, not one set before the parent itself was
/// launched) rather than assumed; see the PR description for the reproduction.
/// </para>
/// </summary>
internal static class BenchSeedResolver
{
    public const string EnvVarName = "OHDATA_BENCH_SEED";

    public readonly record struct Resolution(int Seed, string Source);

    public static Resolution Resolve(string[] args)
    {
        if (TryGetCliSeed(args, out int cliSeed))
            return new Resolution(cliSeed, "--seed argument");

        if (TryParseSeed(Environment.GetEnvironmentVariable(EnvVarName), out int envSeed))
            return new Resolution(envSeed, $"{EnvVarName} environment variable");

        return new Resolution(BenchOrgData.DefaultSeed, "BenchOrgData.DefaultSeed (fixture default)");
    }

    /// <summary>
    /// Removes a <c>--seed &lt;value&gt;</c> pair from <paramref name="args"/>. Must happen before
    /// <c>BenchmarkSwitcher.Run(args)</c> — BenchmarkDotNet rejects unrecognized arguments.
    /// </summary>
    public static string[] StripSeedArgument(string[] args)
    {
        int idx = FindSeedIndex(args);
        if (idx < 0)
            return args;

        var result = new List<string>(args.Length);
        result.AddRange(args.Take(idx));
        result.AddRange(args.Skip(idx + 2));
        return result.ToArray();
    }

    private static bool TryGetCliSeed(string[] args, out int seed)
    {
        int idx = FindSeedIndex(args);
        if (idx >= 0 && idx + 1 < args.Length)
            return TryParseSeed(args[idx + 1], out seed);

        seed = 0;
        return false;
    }

    private static int FindSeedIndex(string[] args) =>
        Array.FindIndex(args, a => a.Equals("--seed", StringComparison.OrdinalIgnoreCase));

    private static bool TryParseSeed(string? raw, out int seed)
    {
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out seed))
            return true;

        seed = 0;
        return false;
    }
}
