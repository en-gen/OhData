using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OhData.Server.Benchmarks.Streaming;

/// <summary>
/// SPIKE #867 console harness (invoked as <c>--stream-probe</c>): per request, peak managed heap,
/// allocated bytes, gen0/1/2 collection counts, time-to-first-byte (ResponseHeadersRead, then first
/// body byte) and total time, for buffered vs streamed, over {GetQueryable, GetODataQueryable} x
/// {in-memory, EF SQLite} x {rows}. Buffered and streamed requests are interleaved within each cell
/// so thermal/GC drift hits both arms equally.
/// </summary>
/// <remarks>
/// What the numbers are, honestly. Server and client share this process, so allocated bytes and GC
/// counts are process-wide (client side drains into a fixed 16 KB buffer, so its share is small and
/// constant). "Peak heap" is the maximum of <c>GC.GetTotalMemory(false)</c> sampled by a spinning
/// thread every ~100 us during the request, minus a post-GC baseline taken just before it: it sees
/// garbage that has not been collected yet, which is exactly the buffering cost, but it is a sampled
/// lower bound on the true peak, not the peak. Allocated bytes (precise) is the stable companion.
/// </remarks>
internal static class StreamProbe
{
    internal sealed record Sample(
        double TtfbMs, double TotalMs, long PeakHeapDeltaBytes, long AllocatedBytes,
        int Gen0, int Gen1, int Gen2, long BodyBytes);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--cell-child", StringComparer.OrdinalIgnoreCase))
            return await StreamProbeChild.RunChildAsync(args);
        if (args.Contains("--pressure", StringComparer.OrdinalIgnoreCase) || args.Contains("--sanity", StringComparer.OrdinalIgnoreCase))
            return await StreamProbeChild.RunParentAsync(args);

        string host = Arg(args, "--host", "testserver").ToLowerInvariant();
        bool smoke = args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
        int runs = int.Parse(Arg(args, "--runs", smoke ? "3" : "30"), CultureInfo.InvariantCulture);
        int[] rowCounts = Arg(args, "--rows", smoke ? "100,1000" : "100,1000,10000")
            .Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        string outDir = Arg(args, "--out", "stream-probe-out");
        bool kestrel = host == "kestrel";
        if (host is not ("testserver" or "kestrel"))
        {
            Console.Error.WriteLine("--host must be testserver or kestrel");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        await using StreamBenchHost bench = await StreamBenchHost.StartAsync(kestrel);

        var results = new List<(StreamBackend Backend, StreamRoute Route, int Rows, string Mode, List<Sample> Samples)>();
        foreach (StreamBackend backend in Enum.GetValues<StreamBackend>())
        {
            foreach (StreamRoute route in Enum.GetValues<StreamRoute>())
            {
                foreach (int rows in rowCounts)
                {
                    string url = StreamBenchHost.Url(backend, route, rows);
                    var buffered = new List<Sample>();
                    var streamed = new List<Sample>();

                    // Warm-up: JIT, EF query cache, connection, JsonTypeInfo caches, both arms.
                    for (int w = 0; w < 3; w++)
                    {
                        bench.Registration.StreamCollections = false; await MeasureAsync(bench.Client, url);
                        bench.Registration.StreamCollections = true; await MeasureAsync(bench.Client, url);
                    }

                    for (int i = 0; i < runs; i++)
                    {
                        // Alternate which arm goes first so order bias cancels.
                        bool streamedFirst = (i & 1) == 1;
                        foreach (bool streamedArm in streamedFirst ? new[] { true, false } : new[] { false, true })
                        {
                            bench.Registration.StreamCollections = streamedArm;
                            long streamedBefore = Interlocked.Read(ref bench.Registration.StreamedResponseCount);
                            (streamedArm ? streamed : buffered).Add(await MeasureAsync(bench.Client, url));
                            long streamedDelta = Interlocked.Read(ref bench.Registration.StreamedResponseCount) - streamedBefore;
                            if (streamedDelta != (streamedArm ? 1 : 0))
                            {
                                throw new InvalidOperationException(
                                    $"{url}: expected {(streamedArm ? 1 : 0)} streamed response(s), saw {streamedDelta}; the arm is not what it claims.");
                            }
                        }
                    }

                    results.Add((backend, route, rows, "Buffered", buffered));
                    results.Add((backend, route, rows, "Streamed", streamed));
                    Console.WriteLine($"cell {backend}/{route}/{rows} rows: " +
                        $"buffered ttfb {Median(buffered.Select(s => s.TtfbMs)):F2} ms, streamed ttfb {Median(streamed.Select(s => s.TtfbMs)):F2} ms");
                }
            }
        }

        WriteOutputs(outDir, bench.Kind, runs, results);
        return 0;
    }

    internal static async Task<Sample> MeasureAsync(HttpClient client, string url)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(forceFullCollection: false);
        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            long next = 0;
            while (!stop.IsCancellationRequested)
            {
                long now = sw.ElapsedTicks;
                if (now < next) { Thread.SpinWait(50); continue; }
                long mem = GC.GetTotalMemory(forceFullCollection: false);
                if (mem > peak) peak = mem;
                next = now + Stopwatch.Frequency / 10_000; // ~100 us
            }
        })
        { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        sampler.Start();

        byte[] buffer = new byte[16 * 1024];
        long start = Stopwatch.GetTimestamp();
        double ttfb;
        long bytes = 0;
        using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using Stream body = await response.Content.ReadAsStreamAsync();
            int read = await body.ReadAsync(buffer);
            ttfb = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            bytes += read;
            while ((read = await body.ReadAsync(buffer)) > 0) bytes += read;
        }

        double total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        stop.Cancel();
        sampler.Join();

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
        return new Sample(
            ttfb, total, Math.Max(0, peak - baseline), allocated,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2, bytes);
    }

    private static void WriteOutputs(
        string outDir, string hostKind, int runs,
        List<(StreamBackend Backend, StreamRoute Route, int Rows, string Mode, List<Sample> Samples)> results)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string commit = Git("rev-parse HEAD") ?? "(unknown)";
        string dirty = string.IsNullOrWhiteSpace(Git("status --porcelain")) ? "clean" : "DIRTY working tree";
        string cpu = CpuName();
        string ram = $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):F1} GiB available to the GC";

        var csv = new StringBuilder();
        csv.AppendLine("host,backend,route,rows,mode,runs,body_bytes," +
            "ttfb_ms_median,ttfb_ms_max,total_ms_median,total_ms_max," +
            "peak_heap_delta_bytes_median,peak_heap_delta_bytes_max,allocated_bytes_median,allocated_bytes_max," +
            "gen0_median,gen0_max,gen1_median,gen1_max,gen2_median,gen2_max");
        var md = new StringBuilder();
        md.AppendLine("# Streaming collection probe (SPIKE #867)");
        md.AppendLine();
        md.AppendLine($"- commit: `{commit}` ({dirty})");
        md.AppendLine($"- host: {hostKind}; requests per cell per arm: {runs} (+3 warm-up pairs, excluded); arms interleaved");
        md.AppendLine($"- machine: {cpu}; logical processors {Environment.ProcessorCount}; {ram}");
        md.AppendLine($"- OS: {RuntimeInformation.OSDescription}; runtime: {RuntimeInformation.FrameworkDescription}; " +
            $"GC: {(GCSettings.IsServerGC ? "Server" : "Workstation")}, latency {GCSettings.LatencyMode}");
        md.AppendLine($"- run at (UTC): {DateTime.UtcNow:O}");
        md.AppendLine("- peak heap = max sampled GC.GetTotalMemory(false) (~100 us sampler) minus post-GC baseline: a sampled lower bound. " +
            "Allocated bytes and GC counts are process-wide (server and client share the process).");
        md.AppendLine();
        md.AppendLine("| backend | route | rows | mode | body KB | TTFB med ms | TTFB max ms | total med ms | peak heap med KB | peak heap max KB | alloc med KB | gen0 med | gen1 med | gen2 med | gen2 max |");
        md.AppendLine("|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach ((StreamBackend backend, StreamRoute route, int rows, string mode, List<Sample> s) in results)
        {
            double Med(Func<Sample, double> f) => Median(s.Select(f));
            double Max(Func<Sample, double> f) => s.Max(f);
            csv.AppendLine(string.Join(",", new[]
            {
                hostKind, backend.ToString(), route.ToString(), rows.ToString(CultureInfo.InvariantCulture), mode,
                s.Count.ToString(CultureInfo.InvariantCulture), s[0].BodyBytes.ToString(CultureInfo.InvariantCulture),
                F(Med(x => x.TtfbMs)), F(Max(x => x.TtfbMs)), F(Med(x => x.TotalMs)), F(Max(x => x.TotalMs)),
                F(Med(x => x.PeakHeapDeltaBytes)), F(Max(x => x.PeakHeapDeltaBytes)),
                F(Med(x => x.AllocatedBytes)), F(Max(x => x.AllocatedBytes)),
                F(Med(x => x.Gen0)), F(Max(x => x.Gen0)), F(Med(x => x.Gen1)), F(Max(x => x.Gen1)),
                F(Med(x => x.Gen2)), F(Max(x => x.Gen2)),
            }));
            md.AppendLine(
                $"| {backend} | {route} | {rows} | {mode} | {s[0].BodyBytes / 1024.0:F0} | {Med(x => x.TtfbMs):F2} | {Max(x => x.TtfbMs):F2} | " +
                $"{Med(x => x.TotalMs):F2} | {Med(x => x.PeakHeapDeltaBytes) / 1024:F0} | {Max(x => x.PeakHeapDeltaBytes) / 1024:F0} | " +
                $"{Med(x => x.AllocatedBytes) / 1024:F0} | {Med(x => x.Gen0):F0} | {Med(x => x.Gen1):F0} | {Med(x => x.Gen2):F0} | {Max(x => x.Gen2):F0} |");
        }

        string baseName = Path.Combine(outDir, $"stream-probe-{hostKind.ToLowerInvariant()}-{stamp}");
        File.WriteAllText(baseName + ".csv", csv.ToString());
        File.WriteAllText(baseName + ".md", md.ToString());
        Console.WriteLine($"wrote {baseName}.md and .csv");
    }

    internal static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    internal static double Median(IEnumerable<double> values)
    {
        double[] a = values.OrderBy(v => v).ToArray();
        if (a.Length == 0) return double.NaN;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2.0;
    }

    internal static string Arg(string[] args, string name, string fallback)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    internal static string? Git(string arguments)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("git", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("ProcessorNameString") is string name) return name.Trim();
            }
        }
        catch
        {
            // fall through
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
    }
}
