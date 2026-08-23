using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks.Probes;

/// <summary>
/// #333 row 5 (<c>$levels</c>) attribution probe — investigation only, not a shipped benchmark.
///
/// <para>
/// #333's remaining row claims Microsoft.AspNetCore.OData is ~2.07x faster on <c>$levels</c> while
/// OhData allocates LESS (219 KB vs 358 KB). The standing hypothesis (#333's own comment, and #328's
/// diagnosis) is that this is EF Core <b>query-translation</b> cost — <c>RelationalProjectionBinding-
/// ExpressionVisitor</c> re-translating each nested-collection subtree three times, giving Theta(3^n) —
/// rather than the JSON-shaping cost #333 is titled after. If that holds, row 5 belongs to #430
/// (one flat query per level) and #333 can close as resolved-by-#442.
/// </para>
///
/// <para>
/// <b>Experiment 1 — the counting experiment (decisive for the stated hypothesis).</b> EF Core caches
/// compiled queries in <c>ICompiledQueryCache</c>, keyed by the expression tree. If the <c>$levels</c>
/// query is compiled ONCE and served from cache thereafter, translation contributes <c>1/N</c> of an
/// N-operation steady state — and <c>ExpandBenchmarkConfig</c> gives every arm at least 1,600 warmup
/// operations before the first measured one, so a one-time translation cost is provably NOT inside the
/// number #333 quotes. Counted directly off EF's own <c>QueryCompilationStarting</c> event.
/// </para>
///
/// <para>
/// <b>Experiment 2 — the depth ladder (decisive for what the cost actually IS).</b> Row 5 is by far the
/// SMALLEST scenario in the suite (219 KB, single-digit ms), so a FIXED per-request overhead would
/// dominate it while staying invisible in the four large scenarios OhData wins. Running the identical
/// root request at expand depth 0, 1, 2 and 3 separates the two: if the OhData-minus-Microsoft delta is
/// roughly CONSTANT across the ladder, the gap is fixed per-request pipeline overhead and has nothing to
/// do with <c>$levels</c>; if it GROWS with depth, the cost is genuinely depth-driven.
/// </para>
///
/// <para>
/// Also reported: SQL shape (statements/JOINs/ROW_NUMBER/length) so an execution-side difference cannot
/// be mistaken for a shaping one, and steady-state wall time, allocation and response size per host.
/// </para>
/// </summary>
internal static class LevelsCostSplitProbe
{
    private const int ColdSampleCount = 8;
    // OhData's pipeline converges FAR later than Microsoft's — an earlier pass of this probe at
    // WarmupCount=300 measured OhData depth 2 at 4.82 ms in one ordering and 2.47 ms in another,
    // purely because the second ordering had run 1,400 more requests through the process first.
    // ExpandComparisonBenchmarks documents the same two-stage tiered-JIT transient and sizes its
    // own warmup floor at 1,600 operations for exactly this reason. Match that, with headroom.
    private const int WarmupCount = 2000;
    private const int SteadyStateCount = 400;

    /// <summary>
    /// The depth ladder. Depth 0 is the identical root request with NO expand at all — the fixed
    /// per-request pipeline cost both hosts pay before any <c>$levels</c> work exists to measure.
    /// </summary>
    private static readonly (string Label, string Url)[] Ladder =
    {
        ("depth 0 (no expand)", $"BenchEmployees?$filter=Id eq {BenchOrgData.RootEmployeeId}"),
        ("depth 1 ($expand)", $"BenchEmployees?$filter=Id eq {BenchOrgData.RootEmployeeId}&$expand=Reports"),
        ("depth 2 ($levels=2)*", $"BenchEmployees?$filter=Id eq {BenchOrgData.RootEmployeeId}&$expand=Reports($levels=2)"),
        ("depth 3 ($levels=3)", $"BenchEmployees?$filter=Id eq {BenchOrgData.RootEmployeeId}&$expand=Reports($levels=3)"),
    };

    public static async Task RunAsync(int seed)
    {
        Console.WriteLine();
        Console.WriteLine("################################################################");
        Console.WriteLine("#  #333 row 5 ($levels) attribution probe");
        Console.WriteLine($"#  seed={seed}  benchmarked url={BenchmarkRequests.EmployeeLevelsUrl}");
        Console.WriteLine($"#  LevelsExpandDepth={BenchOrgData.LevelsExpandDepth}  MaxExpansionDepth={BenchOrgData.MaxExpansionDepth}");
        Console.WriteLine("################################################################");

        Dictionary<(string Host, string Label), Sample> results = new Dictionary<(string, string), Sample>();

        await RunHostAsync("OhData", seed, BenchmarkHosts.StartOhDataAsync, results);
        await RunHostAsync("MsOData", seed, BenchmarkHosts.StartMsODataAsync, results);

        Console.WriteLine();
        Console.WriteLine("################################################################");
        Console.WriteLine("#  EXPERIMENT 2 SUMMARY — depth ladder (median ms, steady state)");
        Console.WriteLine("#  Flat delta across depths => FIXED per-request overhead, not $levels cost.");
        Console.WriteLine("#  (* = the row #333 measures)");
        Console.WriteLine("################################################################");
        Console.WriteLine($"{"scenario",-22} {"OhData",10} {"MsOData",10} {"ratio",8} {"delta ms",10} {"OhData KB",11} {"MsOData KB",11}");
        foreach ((string label, string _) in Ladder)
        {
            Sample oh = results[("OhData", label)];
            Sample ms = results[("MsOData", label)];
            Console.WriteLine($"{label,-22} {oh.Median,10:F3} {ms.Median,10:F3} {oh.Median / ms.Median,7:F2}x " +
                              $"{oh.Median - ms.Median,10:F3} {oh.AllocKb,11:F1} {ms.AllocKb,11:F1}");
        }
    }

    private sealed record Sample(double Median, double Mean, double AllocKb, int BodyLength);

    private static async Task RunHostAsync(
        string label,
        int seed,
        Func<int, Action<DbContextOptionsBuilder>?, Action<ILoggingBuilder>?, Task<(WebApplication, HttpClient, SqliteConnection)>> start,
        Dictionary<(string, string), Sample> results)
    {
        Console.WriteLine();
        Console.WriteLine($"==================== {label} ====================");

        // ---- Arm A: instrumented host — compilation counting + SQL capture. ----
        CompilationCounter counter = new CompilationCounter();
        SqlCaptureInterceptor sql = new SqlCaptureInterceptor();

        (WebApplication app, HttpClient client, SqliteConnection conn) = await start(
            seed,
            o => o.AddInterceptors(sql).LogTo(counter.Filter, counter.OnEvent),
            null);

        try
        {
            string url = BenchmarkRequests.EmployeeLevelsUrl;

            counter.Reset();
            sql.Reset();
            Stopwatch sw = Stopwatch.StartNew();
            string body = await Get(client, url);
            sw.Stop();
            IReadOnlyList<string> coldSql = sql.Snapshot();

            Console.WriteLine($"[{label}] EXPERIMENT 1 — query compilations (EF QueryCompilationStarting)");
            Console.WriteLine($"[{label}]   COLD request: {sw.Elapsed.TotalMilliseconds,9:F2} ms   " +
                              $"queryCompilations={counter.Count}   sqlStatements={coldSql.Count}   bodyLen={body.Length}");

            for (int i = 2; i <= ColdSampleCount; i++)
            {
                counter.Reset();
                sw.Restart();
                await Get(client, url);
                sw.Stop();
                Console.WriteLine($"[{label}]   req #{i,-3} {sw.Elapsed.TotalMilliseconds,9:F2} ms   queryCompilations={counter.Count}");
            }

            for (int i = 0; i < 100; i++)
                await Get(client, url);
            counter.Reset();
            for (int i = 0; i < 200; i++)
                await Get(client, url);
            Console.WriteLine($"[{label}]   STEADY STATE: {counter.Count} query compilations over 200 requests " +
                              $"({counter.Count / 200.0:F3} per request)");

            Console.WriteLine($"[{label}] SQL shape ({coldSql.Count} statement(s)) —");
            foreach (string statement in coldSql)
            {
                int joins = Regex.Matches(statement, @"\bJOIN\b", RegexOptions.IgnoreCase).Count;
                int selects = Regex.Matches(statement, @"\bSELECT\b", RegexOptions.IgnoreCase).Count;
                int rowNumbers = Regex.Matches(statement, @"\bROW_NUMBER\b", RegexOptions.IgnoreCase).Count;
                Console.WriteLine($"[{label}]   joins={joins} selects={selects} rowNumber={rowNumbers} len={statement.Length}");
            }
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
            conn.Dispose();
        }

        // ---- Arm B: clean host, byte-identical config to the benchmark — wall time + the ladder. ----
        (WebApplication app2, HttpClient client2, SqliteConnection conn2) = await start(seed, null, null);
        try
        {
            Console.WriteLine($"[{label}] EXPERIMENT 2 — depth ladder, steady state " +
                              $"({SteadyStateCount} measured after {WarmupCount} warmup)");
            // TWO passes over the ladder. Pass 2 runs with the whole process already converged, so
            // pass1 ~= pass2 is the evidence that the numbers are warm; a pass-to-pass drift means
            // they are not, and nothing here should be quoted.
            for (int pass = 1; pass <= 2; pass++)
            {
                foreach ((string ladderLabel, string ladderUrl) in Ladder)
                {
                    Sample sample = await MeasureAsync(client2, ladderUrl);
                    if (pass == 2)
                        results[(label, ladderLabel)] = sample;
                    Console.WriteLine($"[{label}]   pass{pass} {ladderLabel,-22} median={sample.Median,7:F3} ms  mean={sample.Mean,7:F3} ms  " +
                                      $"alloc={sample.AllocKb,7:F1} KB  bodyLen={sample.BodyLength}");
                }
            }
        }
        finally
        {
            client2.Dispose();
            await app2.DisposeAsync();
            conn2.Dispose();
        }
    }

    private static async Task<Sample> MeasureAsync(HttpClient client, string url)
    {
        string body = "";
        for (int i = 0; i < WarmupCount; i++)
            body = await Get(client, url);

        double[] samples = new double[SteadyStateCount];
        long allocBefore = GC.GetTotalAllocatedBytes(true);
        Stopwatch sw = new Stopwatch();
        for (int i = 0; i < SteadyStateCount; i++)
        {
            sw.Restart();
            body = await Get(client, url);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        long allocAfter = GC.GetTotalAllocatedBytes(true);

        double mean = samples.Average();
        Array.Sort(samples);
        return new Sample(
            samples[SteadyStateCount / 2],
            mean,
            (allocAfter - allocBefore) / (double)SteadyStateCount / 1024.0,
            body.Length);
    }

    private static async Task<string> Get(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"probe request failed {(int)response.StatusCode} for {url}: {body}");
        return body;
    }

    /// <summary>
    /// Counts EF Core <c>QueryCompilationStarting</c> events via <c>DbContextOptionsBuilder.LogTo</c> —
    /// EF's own logging hook, so it cannot be filtered out by the host's <c>ClearProviders()</c>.
    /// </summary>
    private sealed class CompilationCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Reset() => Volatile.Write(ref _count, 0);

        public bool Filter(EventId eventId, LogLevel level) => eventId.Id == CoreEventId.QueryCompilationStarting.Id;

        public void OnEvent(EventData data) => Interlocked.Increment(ref _count);
    }

    /// <summary>Captures the SQL each host emits for the first request.</summary>
    private sealed class SqlCaptureInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _statements = new();
        private volatile bool _capture = true;

        public IReadOnlyList<string> Snapshot() => _statements.ToArray();

        public void Reset()
        {
            while (_statements.TryDequeue(out _)) { }
            _capture = true;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            _capture = false;
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            _capture = false;
            return new ValueTask<DbDataReader>(result);
        }

        private void Record(DbCommand command)
        {
            if (_capture)
                _statements.Enqueue(command.CommandText);
        }
    }
}
