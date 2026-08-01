using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

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
/// That <c>MinIterationTime</c> + fixed-<c>WarmupCount</c> config was itself the cause, and this is the
/// real fix. A research spike traced the ~2x <c>ExpandCollection</c> swing to a <b>pilot-stage
/// bifurcation</b>, not to GC and not to any server code path:
/// </para>
/// <list type="number">
/// <item><description>
/// With <c>MinIterationTime(250 ms)</c> and no fixed invocation count, BenchmarkDotNet's pilot sizes
/// ops-per-iteration from a <b>cold</b> measurement. Its first probe is 16 ops, so the acceptance
/// threshold is exactly 250 ms / 16 = <b>15.625 ms/op</b>. <c>ExpandCollection</c>'s cold cost
/// straddles it (15.04 ms/op in one run, 19.28 ms/op in the next), so the pilot accepted
/// <c>invocationCount</c> = 32 in one run and 16 in the other. It was the <b>only</b> benchmark in the
/// suite whose invocation count differed between runs, and the <b>only</b> one whose mean moved &gt;5%.
/// </description></item>
/// <item><description>
/// <c>WithWarmupCount(10)</c> replaced BenchmarkDotNet's <b>adaptive</b> warmup (which runs until
/// measurements stabilise) with exactly 10 iterations. The warmup transient here is &gt;400 operations,
/// so 10 iterations buys 320 warmup ops at invocationCount=32 but only 160 at 16 — decisive.
/// </description></item>
/// <item><description>
/// The measured series is therefore not bimodal at all; it is a <b>monotone decay</b> (18.00 ms →
/// 5.38 ms across 30 iterations, still falling at the end). BenchmarkDotNet's <c>MValue</c> flagged it
/// "multimodal" because that statistic cannot distinguish a drift from a mixture. Tukey's fence then
/// amplified the gap: the run that converged had IQR 0.947 ms and discarded 5 leading iterations as
/// outliers (N=25, mean 5.785 ms); the run that never converged had IQR 8.487 ms, discarded nothing
/// (N=30, mean 11.314 ms).
/// </description></item>
/// </list>
/// <para>
/// Fix, part 1: pin <c>InvocationCount</c> so the pilot cannot vary between runs, and set
/// <c>UnrollFactor</c> to 1 (required whenever <c>InvocationCount</c> is set explicitly). Do not
/// reintroduce <c>MinIterationTime</c> here: it is what let the invocation count float.
/// </para>
/// <para>
/// Fix, part 2 — <c>MinWarmupCount</c>. Restoring <b>adaptive</b> warmup was necessary but not
/// sufficient: this workload has a <b>two-stage transient</b>, and adaptive warmup is fooled by the
/// first stage. Observed warmup series on one run: <c>14.27, 13.80, 13.42, 13.99, 13.75, 13.41,
/// 14.19, 12.97</c> — flat, low variance, so BenchmarkDotNet declared convergence after 8 iterations
/// and began measuring. The workload then <b>stepped down to ~4.5 ms/op</b> at measured iteration 9,
/// i.e. the real convergence happened <i>inside</i> the measurement window, producing a right-skewed
/// distribution (median 4.933 ms vs mean 6.779 ms, StdDev 48%).
/// </para>
/// <para>
/// The step is <b>tiered JIT reaching tier-1 with dynamic PGO</b>, confirmed by a control run with
/// <c>DOTNET_TieredCompilation=0</c>: the ramp and the plateau both vanish and StdDev falls to 3.2%.
/// <b>That control run is NOT a valid config to adopt</b> — disabling tiering also disables dynamic
/// PGO, and steady state moves from ~4.5 ms to ~7.6 ms. It buys stability by measuring slower,
/// non-representative code. The fix is to <i>reach</i> tier-1+PGO before measuring, not to disable it.
/// </para>
/// <para>
/// <c>MinWarmupCount</c> floors the warmup while leaving adaptive warmup free to run longer.
/// </para>
/// <para>
/// <b>The floor is sized in OPERATIONS, not iterations — and the fastest benchmark sets it.</b> A
/// floor of 20 fixed <c>ExpandCollection</c> (48% -> 5.6% StdDev) but left <c>MsOData_Levels</c> a
/// clean monotone decay across the entire measured window (4.46 -> 1.74 ms/op, still falling at
/// iteration 30, StdDev 37%). Because <c>InvocationCount</c> is fixed at 32 for every benchmark in the
/// job, 20 iterations buys every benchmark the same 640 operations — but the JIT transient is a
/// per-operation phenomenon, so the *fastest* scenario needs the most iterations to clear it.
/// <c>MsOData_Levels</c> converges at roughly 1,400 operations. Hence a floor of <b>50</b>
/// (50 x 32 = 1,600 operations), sized against the slowest-converging scenario, not the average.
/// </para>
/// <para>
/// If a new scenario is added, check its measured series is flat at both ends before trusting it, and
/// raise this floor if it is not. Do not switch back to a fixed <c>WarmupCount</c>, and do not disable
/// tiered compilation. The cost of the higher floor is run time on the slow scenarios, which is the
/// right trade against publishing an unconverged number.
/// </para>
/// <para>
/// When reading results from this class, prefer <c>Median</c> and the ordered per-iteration series over
/// <c>Mean</c> for any scenario BenchmarkDotNet reports <c>MValue &gt; 2</c> on.
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
                .WithInvocationCount(32)
                .WithUnrollFactor(1)
                // MaxWarmupCount must be raised too: BenchmarkDotNet's default max is 50 and it
                // validates Min < Max strictly, so a floor of 50 alone fails config validation
                // ("MaxWarmupIterationCount must be greater than MinWarmupIterationCount").
                .WithMinWarmupCount(50)
                .WithMaxWarmupCount(100)
                .WithIterationCount(30));
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
