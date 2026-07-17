using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.AspNetCore.Builder;

namespace OhData.Server.Benchmarks.Benchmarks;

/// <summary>
/// Head-to-head server pipeline comparison: OhData minimal-API endpoints vs
/// Microsoft.AspNetCore.OData ODataController + [EnableQuery]. Both hosts run in-process on
/// TestServer over the identical deterministic 1000-widget in-memory dataset, so each
/// measurement is a full HTTP round-trip (routing → query-option processing → handler →
/// serialization) with network and database noise removed.
///
/// Benchmarks are paired per operation via categories; the OhData side is the per-category
/// baseline so the Ratio column reads directly as "MS OData cost relative to OhData".
///
/// Run config: 5 warmup + 20 measured iterations (instead of BenchmarkDotNet's adaptive
/// default) so the 22-benchmark suite completes in well under 25 minutes while keeping
/// per-op error bars small relative to the inter-server deltas being reported.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 20)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ServerComparisonBenchmarks
{
    private WebApplication _ohDataApp = null!;
    private WebApplication _msODataApp = null!;
    private HttpClient _ohData = null!;
    private HttpClient _msOData = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        (_ohDataApp, _ohData) = await BenchmarkHosts.StartOhDataAsync();
        (_msODataApp, _msOData) = await BenchmarkHosts.StartMsODataAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _ohData.Dispose();
        _msOData.Dispose();
        await _ohDataApp.DisposeAsync();
        await _msODataApp.DisposeAsync();
    }

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            using var response = await client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
    }

    // ── GET collection (first page of 100) ────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("GetAllPage")]
    public Task<string> OhData_GetAllPage() => GetAsync(_ohData, BenchmarkRequests.GetAllUrl);

    [Benchmark, BenchmarkCategory("GetAllPage")]
    public Task<string> MsOData_GetAllPage() => GetAsync(_msOData, BenchmarkRequests.GetAllUrl);

    // ── $filter ───────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Filter")]
    public Task<string> OhData_Filter() => GetAsync(_ohData, BenchmarkRequests.FilterUrl);

    [Benchmark, BenchmarkCategory("Filter")]
    public Task<string> MsOData_Filter() => GetAsync(_msOData, BenchmarkRequests.FilterUrl);

    // ── $orderby ──────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("OrderBy")]
    public Task<string> OhData_OrderBy() => GetAsync(_ohData, BenchmarkRequests.OrderByUrl);

    [Benchmark, BenchmarkCategory("OrderBy")]
    public Task<string> MsOData_OrderBy() => GetAsync(_msOData, BenchmarkRequests.OrderByUrl);

    // ── $select ───────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Select")]
    public Task<string> OhData_Select() => GetAsync(_ohData, BenchmarkRequests.SelectUrl);

    [Benchmark, BenchmarkCategory("Select")]
    public Task<string> MsOData_Select() => GetAsync(_msOData, BenchmarkRequests.SelectUrl);

    // ── $top + $skip ──────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("TopSkip")]
    public Task<string> OhData_TopSkip() => GetAsync(_ohData, BenchmarkRequests.TopSkipUrl);

    [Benchmark, BenchmarkCategory("TopSkip")]
    public Task<string> MsOData_TopSkip() => GetAsync(_msOData, BenchmarkRequests.TopSkipUrl);

    // ── $count=true ───────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("CountTrue")]
    public Task<string> OhData_CountTrue() => GetAsync(_ohData, BenchmarkRequests.CountUrl);

    [Benchmark, BenchmarkCategory("CountTrue")]
    public Task<string> MsOData_CountTrue() => GetAsync(_msOData, BenchmarkRequests.CountUrl);

    // ── GET by key ────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("GetById")]
    public Task<string> OhData_GetById() => GetAsync(_ohData, BenchmarkRequests.GetByIdUrl);

    [Benchmark, BenchmarkCategory("GetById")]
    public Task<string> MsOData_GetById() => GetAsync(_msOData, BenchmarkRequests.GetByIdUrl);

    // ── POST ──────────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Post")]
    public Task<string> OhData_Post() => SendAsync(_ohData, BenchmarkRequests.CreatePost());

    [Benchmark, BenchmarkCategory("Post")]
    public Task<string> MsOData_Post() => SendAsync(_msOData, BenchmarkRequests.CreatePost());

    // ── PUT ───────────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Put")]
    public Task<string> OhData_Put() => SendAsync(_ohData, BenchmarkRequests.CreatePut());

    [Benchmark, BenchmarkCategory("Put")]
    public Task<string> MsOData_Put() => SendAsync(_msOData, BenchmarkRequests.CreatePut());

    // ── PATCH ─────────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Patch")]
    public Task<string> OhData_Patch() => SendAsync(_ohData, BenchmarkRequests.CreatePatch());

    [Benchmark, BenchmarkCategory("Patch")]
    public Task<string> MsOData_Patch() => SendAsync(_msOData, BenchmarkRequests.CreatePatch());

    // ── DELETE ────────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Delete")]
    public Task<string> OhData_Delete() => SendAsync(_ohData, BenchmarkRequests.CreateDelete());

    [Benchmark, BenchmarkCategory("Delete")]
    public Task<string> MsOData_Delete() => SendAsync(_msOData, BenchmarkRequests.CreateDelete());
}
