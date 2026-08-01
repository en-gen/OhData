using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace OhData.Server.Benchmarks.Benchmarks;

/// <summary>
/// Head-to-head server pipeline comparison: OhData minimal-API endpoints vs
/// Microsoft.AspNetCore.OData ODataController + [EnableQuery]. Both hosts run in-process on
/// TestServer over the identical deterministic 1000-widget in-memory dataset, so each
/// measurement is a full HTTP round-trip (routing → query-option processing → handler →
/// serialization) with network and database noise removed. <c>BenchWidget</c> has no EDM
/// navigations (no EF Core involved) — see <see cref="ExpandComparisonBenchmarks"/> for the
/// EF Core/Sqlite-backed <c>$expand</c>/<c>$levels</c> scenarios.
///
/// Benchmarks are paired per operation via categories; the OhData side is the per-category
/// baseline so the Ratio column reads directly as "MS OData cost relative to OhData".
///
/// Run config: 5 warmup + 20 measured iterations (instead of BenchmarkDotNet's adaptive
/// default) so this 22-benchmark half of the suite completes quickly while keeping per-op
/// error bars small relative to the inter-server deltas being reported. These 11 categories
/// were not flagged by adversarial review for min-iteration-time or bimodality warnings — see
/// <see cref="ExpandComparisonBenchmarks"/> for the categories that needed a heavier config.
/// </summary>
[SimpleJob(warmupCount: 5, iterationCount: 20)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ServerComparisonBenchmarks : ServerComparisonBenchmarksBase
{
    // ── GET collection (first page of 100) ────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("GetAllPage")]
    public Task<string> OhData_GetAllPage() => GetAsync(OhDataClient, BenchmarkRequests.GetAllUrl);

    [Benchmark, BenchmarkCategory("GetAllPage")]
    public Task<string> MsOData_GetAllPage() => GetAsync(MsODataClient, BenchmarkRequests.GetAllUrl);

    // ── $filter ───────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Filter")]
    public Task<string> OhData_Filter() => GetAsync(OhDataClient, BenchmarkRequests.FilterUrl);

    [Benchmark, BenchmarkCategory("Filter")]
    public Task<string> MsOData_Filter() => GetAsync(MsODataClient, BenchmarkRequests.FilterUrl);

    // ── $orderby ──────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("OrderBy")]
    public Task<string> OhData_OrderBy() => GetAsync(OhDataClient, BenchmarkRequests.OrderByUrl);

    [Benchmark, BenchmarkCategory("OrderBy")]
    public Task<string> MsOData_OrderBy() => GetAsync(MsODataClient, BenchmarkRequests.OrderByUrl);

    // ── $select ───────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Select")]
    public Task<string> OhData_Select() => GetAsync(OhDataClient, BenchmarkRequests.SelectUrl);

    [Benchmark, BenchmarkCategory("Select")]
    public Task<string> MsOData_Select() => GetAsync(MsODataClient, BenchmarkRequests.SelectUrl);

    // ── $top + $skip ──────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("TopSkip")]
    public Task<string> OhData_TopSkip() => GetAsync(OhDataClient, BenchmarkRequests.TopSkipUrl);

    [Benchmark, BenchmarkCategory("TopSkip")]
    public Task<string> MsOData_TopSkip() => GetAsync(MsODataClient, BenchmarkRequests.TopSkipUrl);

    // ── $count=true ───────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("CountTrue")]
    public Task<string> OhData_CountTrue() => GetAsync(OhDataClient, BenchmarkRequests.CountUrl);

    [Benchmark, BenchmarkCategory("CountTrue")]
    public Task<string> MsOData_CountTrue() => GetAsync(MsODataClient, BenchmarkRequests.CountUrl);

    // ── GET by key ────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("GetById")]
    public Task<string> OhData_GetById() => GetAsync(OhDataClient, BenchmarkRequests.GetByIdUrl);

    [Benchmark, BenchmarkCategory("GetById")]
    public Task<string> MsOData_GetById() => GetAsync(MsODataClient, BenchmarkRequests.GetByIdUrl);

    // ── POST ──────────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Post")]
    public Task<string> OhData_Post() => SendAsync(OhDataClient, BenchmarkRequests.CreatePost());

    [Benchmark, BenchmarkCategory("Post")]
    public Task<string> MsOData_Post() => SendAsync(MsODataClient, BenchmarkRequests.CreatePost());

    // ── PUT ───────────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Put")]
    public Task<string> OhData_Put() => SendAsync(OhDataClient, BenchmarkRequests.CreatePut());

    [Benchmark, BenchmarkCategory("Put")]
    public Task<string> MsOData_Put() => SendAsync(MsODataClient, BenchmarkRequests.CreatePut());

    // ── PATCH ─────────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Patch")]
    public Task<string> OhData_Patch() => SendAsync(OhDataClient, BenchmarkRequests.CreatePatch());

    [Benchmark, BenchmarkCategory("Patch")]
    public Task<string> MsOData_Patch() => SendAsync(MsODataClient, BenchmarkRequests.CreatePatch());

    // ── DELETE ────────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Delete")]
    public Task<string> OhData_Delete() => SendAsync(OhDataClient, BenchmarkRequests.CreateDelete());

    [Benchmark, BenchmarkCategory("Delete")]
    public Task<string> MsOData_Delete() => SendAsync(MsODataClient, BenchmarkRequests.CreateDelete());
}
