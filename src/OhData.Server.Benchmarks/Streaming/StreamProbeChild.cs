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
using System.Text.Json;
using System.Threading.Tasks;

namespace OhData.Server.Benchmarks.Streaming;

/// <summary>
/// SPIKE #867 pressure mode. One FRESH child process per (cell, arm), so GC environment knobs
/// (DOTNET_GCgen0MaxBudget, DOTNET_GCHeapHardLimit) actually apply and PeakWorkingSet64 is that
/// cell's own. The parent aggregates the children's one-line JSON results into markdown and CSV.
/// </summary>
internal static class StreamProbeChild
{
    internal sealed class ChildResult
    {
        public string Backend { get; set; } = "";
        public string Route { get; set; } = "";
        public int Rows { get; set; }
        public string Mode { get; set; } = "";
        public int Concurrency { get; set; }
        public int Requests { get; set; }
        public int Ok { get; set; }
        public int Failed { get; set; }
        public string Errors { get; set; } = "";
        public long PeakWorkingSetBytes { get; set; }
        public long MaxHeapSizeBytes { get; set; }
        public long MaxPromotedBytes { get; set; }
        public double PauseMsTotal { get; set; }
        public int Gen0Total { get; set; }
        public int Gen1Total { get; set; }
        public int Gen2Total { get; set; }
        public long BodyBytes { get; set; }
        public List<double> TtfbMs { get; set; } = new();
        public List<double> TotalMs { get; set; } = new();
        public List<double> PeakHeapDelta { get; set; } = new();
        public List<double> Allocated { get; set; } = new();
        public List<double> Gen0 { get; set; } = new();
        public List<double> Gen1 { get; set; } = new();
        public List<double> Gen2 { get; set; } = new();
        public List<double> PauseMs { get; set; } = new();
    }

    // ---------------------------------------------------------------- child

    internal static async Task<int> RunChildAsync(string[] args)
    {
        var r = new ChildResult
        {
            Backend = StreamProbe.Arg(args, "--backend", "InMemory"),
            Route = StreamProbe.Arg(args, "--route", "GetQueryable"),
            Rows = int.Parse(StreamProbe.Arg(args, "--rows", "1000"), CultureInfo.InvariantCulture),
            Mode = StreamProbe.Arg(args, "--mode", "Buffered"),
            Concurrency = int.Parse(StreamProbe.Arg(args, "--concurrency", "1"), CultureInfo.InvariantCulture),
        };
        int runs = int.Parse(StreamProbe.Arg(args, "--runs", "30"), CultureInfo.InvariantCulture);
        bool kestrel = StreamProbe.Arg(args, "--host", "testserver").Equals("kestrel", StringComparison.OrdinalIgnoreCase);
        var backend = Enum.Parse<StreamBackend>(r.Backend);
        var route = Enum.Parse<StreamRoute>(r.Route);
        string url = StreamBenchHost.Url(backend, route, r.Rows);

        await using StreamBenchHost bench = await StreamBenchHost.StartAsync(kestrel);
        bench.Registration.StreamCollections = r.Mode == "Streamed";

        for (int w = 0; w < 3; w++)
        {
            await StreamProbe.MeasureAsync(bench.Client, url);
        }

        TimeSpan pauseStart = GC.GetTotalPauseDuration();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var errors = new HashSet<string>();

        if (r.Concurrency <= 1)
        {
            for (int i = 0; i < runs; i++)
            {
                TimeSpan p0 = GC.GetTotalPauseDuration();
                try
                {
                    StreamProbe.Sample s = await StreamProbe.MeasureAsync(bench.Client, url);
                    r.Ok++;
                    r.BodyBytes = s.BodyBytes;
                    r.TtfbMs.Add(s.TtfbMs);
                    r.TotalMs.Add(s.TotalMs);
                    r.PeakHeapDelta.Add(s.PeakHeapDeltaBytes);
                    r.Allocated.Add(s.AllocatedBytes);
                    r.Gen0.Add(s.Gen0);
                    r.Gen1.Add(s.Gen1);
                    r.Gen2.Add(s.Gen2);
                    r.PauseMs.Add((GC.GetTotalPauseDuration() - p0).TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    r.Failed++;
                    errors.Add(ex.GetType().Name + ": " + ex.Message);
                }

                Observe(r);
            }

            r.Requests = runs;
        }
        else
        {
            // Concurrent rounds: `runs` rounds of `Concurrency` simultaneous requests.
            for (int i = 0; i < runs; i++)
            {
                Task<(bool Ok, double Ms, string? Error)>[] tasks = Enumerable.Range(0, r.Concurrency)
                    .Select(_ => OneAsync(bench.Client, url)).ToArray();
                foreach ((bool ok, double ms, string? error) in await Task.WhenAll(tasks))
                {
                    r.Requests++;
                    if (ok)
                    {
                        r.Ok++;
                        r.TotalMs.Add(ms);
                    }
                    else
                    {
                        r.Failed++;
                        if (error is not null)
                        {
                            errors.Add(error);
                        }
                    }
                }

                Observe(r);
            }

        }

        r.PauseMsTotal = (GC.GetTotalPauseDuration() - pauseStart).TotalMilliseconds;
        r.Gen0Total = GC.CollectionCount(0) - g0;
        r.Gen1Total = GC.CollectionCount(1) - g1;
        r.Gen2Total = GC.CollectionCount(2) - g2;
        r.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
        r.Errors = string.Join(" | ", errors.Take(3));
        Console.WriteLine("CELLRESULT " + JsonSerializer.Serialize(r));
        return 0;
    }

    private static void Observe(ChildResult r)
    {
        GCMemoryInfo mi = GC.GetGCMemoryInfo(GCKind.Any);
        r.MaxHeapSizeBytes = Math.Max(r.MaxHeapSizeBytes, mi.HeapSizeBytes);
        r.MaxPromotedBytes = Math.Max(r.MaxPromotedBytes, mi.PromotedBytes);
        r.MaxHeapSizeBytes = Math.Max(r.MaxHeapSizeBytes, GC.GetTotalMemory(false));
    }

    private static async Task<(bool Ok, double Ms, string? Error)> OneAsync(HttpClient client, string url)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            using HttpResponseMessage resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, 0, "HTTP " + (int)resp.StatusCode);
            }

            await using Stream body = await resp.Content.ReadAsStreamAsync();
            byte[] buffer = new byte[16 * 1024];
            while (await body.ReadAsync(buffer) > 0)
            {
            }

            return (true, Stopwatch.GetElapsedTime(start).TotalMilliseconds, null);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.GetType().Name + ": " + ex.Message);
        }
    }

    // --------------------------------------------------------------- parent

    internal static async Task<int> RunParentAsync(string[] args)
    {
        bool sanity = args.Contains("--sanity", StringComparer.OrdinalIgnoreCase);
        bool smoke = args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
        string host = StreamProbe.Arg(args, "--host", "testserver");
        string outDir = StreamProbe.Arg(args, "--out", "stream-probe-out");
        int gen0Mb = int.Parse(StreamProbe.Arg(args, "--gen0-mb", sanity ? "0" : "4"), CultureInfo.InvariantCulture);
        int heapMb = int.Parse(StreamProbe.Arg(args, "--heap-limit-mb", sanity ? "1024" : "0"), CultureInfo.InvariantCulture);
        int runs = int.Parse(StreamProbe.Arg(args, "--runs", smoke ? "3" : (sanity ? "10" : "30")), CultureInfo.InvariantCulture);
        int[] rowCounts = StreamProbe.Arg(args, "--rows", sanity ? "10000" : (smoke ? "100,1000" : "100,1000,10000"))
            .Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        int[] concurrencies = StreamProbe.Arg(args, "--concurrency", sanity ? "8,16,32" : "1")
            .Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        Directory.CreateDirectory(outDir);

        var results = new List<ChildResult>();
        foreach (StreamBackend backend in Enum.GetValues<StreamBackend>())
        {
            foreach (StreamRoute route in Enum.GetValues<StreamRoute>())
            {
                foreach (int rows in rowCounts)
                {
                    foreach (int conc in concurrencies)
                    {
                        foreach (string mode in new[] { "Buffered", "Streamed" })
                        {
                            ChildResult? cr = RunOneChild(host, backend, route, rows, mode, conc, runs, gen0Mb, heapMb, out string? fail);
                            cr ??= new ChildResult
                            {
                                Backend = backend.ToString(),
                                Route = route.ToString(),
                                Rows = rows,
                                Mode = mode,
                                Concurrency = conc,
                                Failed = 1,
                                Errors = fail ?? "child crashed",
                            };
                            results.Add(cr);
                            Console.WriteLine($"{backend}/{route}/{rows}/c{conc}/{mode}: ok={cr.Ok} failed={cr.Failed} " +
                                $"gen0={cr.Gen0Total} peakWS={cr.PeakWorkingSetBytes / 1048576.0:F0} MB {cr.Errors}");
                        }
                    }
                }
            }
        }

        WritePressureOutputs(outDir, host, runs, gen0Mb, heapMb, sanity, results);
        return 0;
    }

    private static ChildResult? RunOneChild(
        string host, StreamBackend backend, StreamRoute route, int rows, string mode, int conc, int runs,
        int gen0Mb, int heapMb, out string? failure)
    {
        string dll = typeof(StreamProbeChild).Assembly.Location;
        string exe = Environment.ProcessPath ?? "dotnet";
        string childArgs = $"--stream-probe --cell-child --host {host} --backend {backend} --route {route} " +
                           $"--rows {rows} --mode {mode} --concurrency {conc} --runs {runs}";
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.Arguments = $"\"{dll}\" {childArgs}";
        }
        else
        {
            psi.Arguments = childArgs;
        }

        psi.Environment["DOTNET_gcServer"] = "0";
        if (gen0Mb > 0)
            psi.Environment["DOTNET_GCgen0MaxBudget"] = ((long)gen0Mb * 1024 * 1024).ToString("x", CultureInfo.InvariantCulture);
        if (heapMb > 0)
            psi.Environment["DOTNET_GCHeapHardLimit"] = ((long)heapMb * 1024 * 1024).ToString("x", CultureInfo.InvariantCulture);

        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        string? line = stdout.Split('\n').FirstOrDefault(l => l.StartsWith("CELLRESULT ", StringComparison.Ordinal));
        if (p.ExitCode != 0 || line is null)
        {
            failure = $"child exit {p.ExitCode}: " + (stderr.Length > 200 ? stderr[..200] : stderr).Replace('\n', ' ').Replace('\r', ' ');
            return null;
        }

        failure = null;
        return JsonSerializer.Deserialize<ChildResult>(line["CELLRESULT ".Length..]);
    }

    private static void WritePressureOutputs(
        string outDir, string host, int runs, int gen0Mb, int heapMb, bool sanity, List<ChildResult> results)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string commit = StreamProbe.Git("rev-parse HEAD") ?? "(unknown)";
        string dirty = string.IsNullOrWhiteSpace(StreamProbe.Git("status --porcelain")) ? "clean" : "DIRTY working tree";
        string kind = sanity ? "sanity" : "pressure";

        var md = new StringBuilder();
        md.AppendLine($"# Streaming collection {kind} probe (SPIKE #867)");
        md.AppendLine();
        md.AppendLine($"- commit: `{commit}` ({dirty}); host: {host}; requests per cell: {runs} (concurrent mode: {runs} rounds of N); one fresh child process per cell and arm");
        md.AppendLine($"- GC env: DOTNET_gcServer=0; gen0 max budget: {(gen0Mb > 0 ? gen0Mb + " MB (DOTNET_GCgen0MaxBudget)" : "default")}; heap hard limit: {(heapMb > 0 ? heapMb + " MB (DOTNET_GCHeapHardLimit)" : "none")}");
        md.AppendLine($"- machine: {StreamProbe.CpuName()}; logical processors {Environment.ProcessorCount}; OS: {RuntimeInformation.OSDescription}; runtime: {RuntimeInformation.FrameworkDescription}");
        md.AppendLine($"- run at (UTC): {DateTime.UtcNow:O}");
        md.AppendLine("- peak WS = Process.PeakWorkingSet64 of the cell's own child (includes runtime, host, 10k-row fixtures: compare arms, not absolutes). Gen counts and pause are totals over the measured requests; per-request medians in the CSV.");
        md.AppendLine();
        md.AppendLine("| backend | route | rows | conc | mode | ok | failed | peak WS MB | max heap MB | max promoted MB | gen0 | gen1 | gen2 | GC pause ms | TTFB med ms | total med ms | total max ms | peak heap med KB | alloc med KB | errors |");
        md.AppendLine("|---|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");

        var csv = new StringBuilder();
        csv.AppendLine("host,backend,route,rows,concurrency,mode,runs,ok,failed,peak_ws_bytes,max_heap_bytes,max_promoted_bytes," +
            "gen0_total,gen1_total,gen2_total,gc_pause_ms_total,gen0_per_req_median,gen1_per_req_median,gen2_per_req_median," +
            "pause_ms_per_req_median,ttfb_ms_median,ttfb_ms_max,total_ms_median,total_ms_max," +
            "peak_heap_delta_bytes_median,peak_heap_delta_bytes_max,allocated_bytes_median,allocated_bytes_max,body_bytes,errors");

        foreach (ChildResult c in results)
        {
            double Med(List<double> l) => l.Count == 0 ? double.NaN : StreamProbe.Median(l);
            double Max(List<double> l) => l.Count == 0 ? double.NaN : l.Max();
            md.AppendLine($"| {c.Backend} | {c.Route} | {c.Rows} | {c.Concurrency} | {c.Mode} | {c.Ok} | {c.Failed} | " +
                $"{c.PeakWorkingSetBytes / 1048576.0:F0} | {c.MaxHeapSizeBytes / 1048576.0:F0} | {c.MaxPromotedBytes / 1048576.0:F0} | " +
                $"{c.Gen0Total} | {c.Gen1Total} | {c.Gen2Total} | {c.PauseMsTotal:F1} | {Med(c.TtfbMs):F2} | {Med(c.TotalMs):F2} | {Max(c.TotalMs):F2} | " +
                $"{Med(c.PeakHeapDelta) / 1024:F0} | {Med(c.Allocated) / 1024:F0} | {c.Errors.Replace('|', '/')} |");
            csv.AppendLine(string.Join(",", new[]
            {
                host, c.Backend, c.Route, c.Rows.ToString(CultureInfo.InvariantCulture), c.Concurrency.ToString(CultureInfo.InvariantCulture),
                c.Mode, runs.ToString(CultureInfo.InvariantCulture), c.Ok.ToString(CultureInfo.InvariantCulture), c.Failed.ToString(CultureInfo.InvariantCulture),
                c.PeakWorkingSetBytes.ToString(CultureInfo.InvariantCulture), c.MaxHeapSizeBytes.ToString(CultureInfo.InvariantCulture),
                c.MaxPromotedBytes.ToString(CultureInfo.InvariantCulture), c.Gen0Total.ToString(CultureInfo.InvariantCulture),
                c.Gen1Total.ToString(CultureInfo.InvariantCulture), c.Gen2Total.ToString(CultureInfo.InvariantCulture), StreamProbe.F(c.PauseMsTotal),
                StreamProbe.F(Med(c.Gen0)), StreamProbe.F(Med(c.Gen1)), StreamProbe.F(Med(c.Gen2)), StreamProbe.F(Med(c.PauseMs)),
                StreamProbe.F(Med(c.TtfbMs)), StreamProbe.F(Max(c.TtfbMs)), StreamProbe.F(Med(c.TotalMs)), StreamProbe.F(Max(c.TotalMs)),
                StreamProbe.F(Med(c.PeakHeapDelta)), StreamProbe.F(Max(c.PeakHeapDelta)), StreamProbe.F(Med(c.Allocated)), StreamProbe.F(Max(c.Allocated)),
                c.BodyBytes.ToString(CultureInfo.InvariantCulture), "\"" + c.Errors.Replace("\"", "'") + "\"",
            }));
        }

        string baseName = Path.Combine(outDir, $"stream-{kind}-{host.ToLowerInvariant()}-{stamp}");
        File.WriteAllText(baseName + ".csv", csv.ToString());
        File.WriteAllText(baseName + ".md", md.ToString());
        Console.WriteLine($"wrote {baseName}.md and .csv");
    }
}
