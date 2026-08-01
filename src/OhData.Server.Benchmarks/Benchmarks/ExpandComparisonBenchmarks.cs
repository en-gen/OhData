using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Perfolizer.Horology;

namespace OhData.Server.Benchmarks.Benchmarks;

/// <summary>
/// <c>$expand</c>/<c>$levels</c> half of the server-comparison suite: OhData minimal-API endpoints vs
/// Microsoft.AspNetCore.OData ODataController + [EnableQuery], both over the identical deterministic
/// EF Core/Sqlite-backed <c>BenchDepartment</c>/<c>BenchEmployee</c> fixture (see <c>BenchOrgData</c>
/// and <c>BenchmarkHosts</c> for why this pair is EF Core/Sqlite-backed rather than the plain
/// <c>List&lt;T&gt;</c> store <see cref="ServerComparisonBenchmarks"/> uses — OhData's <c>$expand</c>
/// pushdown is gated to an EF Core-backed <c>IQueryable</c>).
///
/// <para>
/// Split into its own class, with its own run config, because an adversarial fairness review found
/// these five categories specifically too noisy at the original shared config (5 warmup + 20 measured,
/// invocation count auto-determined by BenchmarkDotNet's pilot stage) to publish magnitudes from:
/// BenchmarkDotNet warned of a minimum iteration time of ~90 ms on <c>MsOData_ExpandNested</c> (below
/// BDN's own 100 ms guidance) and bimodal iteration-time distributions on <c>OhData_ExpandNested</c>
/// and <c>MsOData_Levels</c>. Run-to-run swings on unchanged code were as large as
/// <c>ExpandCollection</c> 6.019 ms → 9.408 ms and <c>SelectExpand</c> ratio 1.40× → 2.31× — the
/// *direction* (who wins) was stable across every run, but magnitudes were not trustworthy.
/// </para>
/// <para>
/// Fix: <see cref="ExpandBenchmarkConfig"/> sets an explicit <c>MinIterationTime</c> instead of a
/// fixed <c>invocationCount</c>, so BenchmarkDotNet's own pilot stage sizes the invocation count per
/// benchmark to comfortably clear the 100 ms guidance regardless of whether a given scenario's actual
/// per-call cost is ~6 ms (<c>ExpandCollection</c>) or ~90 ms (<c>MsOData_ExpandNested</c>) — a single
/// hand-picked <c>invocationCount</c> could not fit both without either leaving the fast scenarios
/// under-sampled or making the slow ones needlessly long. Warmup and iteration counts are also raised
/// (10 warmup / 30 measured, vs 5/20 for <see cref="ServerComparisonBenchmarks"/>) for more stable
/// distributions on the categories BenchmarkDotNet flagged as bimodal.
/// </para>
/// </summary>
[Config(typeof(ExpandBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ExpandComparisonBenchmarks : ServerComparisonBenchmarksBase
{
    private sealed class ExpandBenchmarkConfig : ManualConfig
    {
        public ExpandBenchmarkConfig()
        {
            AddJob(Job.Default
                .WithWarmupCount(10)
                .WithIterationCount(30)
                .WithMinIterationTime(TimeInterval.FromMilliseconds(250)));
        }
    }

    // ── $expand of a collection navigation (pushdown JOIN) ───────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("ExpandCollection")]
    public Task<string> OhData_ExpandCollection() => GetAsync(OhDataClient, BenchmarkRequests.DeptExpandCollectionUrl);

    [Benchmark, BenchmarkCategory("ExpandCollection")]
    public Task<string> MsOData_ExpandCollection() => GetAsync(MsODataClient, BenchmarkRequests.DeptExpandCollectionUrl);

    // ── nested $expand=A($expand=B) ───────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("ExpandNested")]
    public Task<string> OhData_ExpandNested() => GetAsync(OhDataClient, BenchmarkRequests.DeptExpandNestedUrl);

    [Benchmark, BenchmarkCategory("ExpandNested")]
    public Task<string> MsOData_ExpandNested() => GetAsync(MsODataClient, BenchmarkRequests.DeptExpandNestedUrl);

    // ── $expand with nested $top/$orderby/$count/$select ─────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("ExpandNestedOptions")]
    public Task<string> OhData_ExpandNestedOptions() => GetAsync(OhDataClient, BenchmarkRequests.DeptExpandNestedOptionsUrl);

    [Benchmark, BenchmarkCategory("ExpandNestedOptions")]
    public Task<string> MsOData_ExpandNestedOptions() => GetAsync(MsODataClient, BenchmarkRequests.DeptExpandNestedOptionsUrl);

    // ── $select + $expand combined ────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("SelectExpand")]
    public Task<string> OhData_SelectExpand() => GetAsync(OhDataClient, BenchmarkRequests.DeptSelectExpandUrl);

    [Benchmark, BenchmarkCategory("SelectExpand")]
    public Task<string> MsOData_SelectExpand() => GetAsync(MsODataClient, BenchmarkRequests.DeptSelectExpandUrl);

    // ── $levels on a self-referential navigation ──────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Levels")]
    public Task<string> OhData_Levels() => GetAsync(OhDataClient, BenchmarkRequests.EmployeeLevelsUrl);

    [Benchmark, BenchmarkCategory("Levels")]
    public Task<string> MsOData_Levels() => GetAsync(MsODataClient, BenchmarkRequests.EmployeeLevelsUrl);
}
