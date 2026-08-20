using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks.Benchmarks;

/// <summary>
/// What building the <see cref="ODataQueryContext"/> per request costs (#426), measured as the
/// delta against the shape it replaced.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question.</b> Until #426 the framework built one <see cref="ODataQueryContext"/> per
/// entity set at startup and shared it for the process lifetime — an optimisation that was also a
/// contract violation, since <c>ODataQueryOptions</c>' constructor writes to the context it is
/// handed and <c>Initialize</c> reads one of those fields back. Building it per request is the only
/// correct shape, and it ships regardless of what it costs; this class exists so the price is a
/// number rather than an assumption.
/// </para>
/// <para>
/// <b>Unit under measurement.</b> Exactly what <c>OhDataEndpointFactory.TryBuildQueryOptions</c>
/// does on a read route and nothing else: build the context (or not), then
/// <c>new ODataQueryOptions&lt;BenchWidget&gt;(context, request)</c>. Not a whole HTTP request —
/// <see cref="ServerComparisonBenchmarks"/> already measures that, and burying a sub-microsecond
/// delta inside a request that costs three orders of magnitude more would measure the harness.
/// Read <see cref="C_ContextOnly"/> as the attribution: it is the work B does and A does not.
/// </para>
/// <para>
/// <b>Measured</b> (Ryzen 9 5950X, .NET 10.0.11, 1,048,576 invocations x 50 iterations): B costs
/// <b>+263 to +362 ns and +448 B</b> over A across the four cells — +85%/+88% on the no-query-string
/// shape, +44%/+52% on the typical one. <c>C_ContextOnly</c> lands at 207-253 ns, so the construction
/// itself accounts for most but not all of the delta; the remainder is the cache and GC cost of the
/// extra 448 B object, which the allocation column attributes exactly (1032 -> 1480 B, and 1672 ->
/// 2120 B, both differences exactly C's 448 B). Going from a one- to a three-entity-set model moves
/// C by ~20-45 ns, the linear scan below. <b>In context</b>: the cheapest OhData read route measured
/// end to end in <c>docs/server-comparison-report.md</c> is GetById at 36.8 us / 15.95 KB, so this is
/// under 1% of its time and 2.7% of its allocation — and GetById does not even pay it unless
/// <c>$select</c> or <c>$expand</c> is present. Against a collection page (GetAllPage, 763 us /
/// 123.85 KB) it is under 0.05% of time and 0.4% of allocation. Not material, and it ships either
/// way, but it is a real per-request allocation that did not exist before.
/// </para>
/// <para>
/// <b>Why the entity-set count is a parameter.</b> Neither <c>ODataQueryContext</c> constructor is
/// O(1). Both call <c>GetNavigationSource</c>, which — with a null <c>ODataPath</c>, which is what
/// the framework passes — does
/// <c>entityContainer.EntitySets().Where(e =&gt; e.EntityType == elementType).ToList()</c>: a linear
/// scan of every entity set in the registration, with a <c>List</c> allocation, on every
/// construction. That is the part of this change that scales with the size of someone's model, so
/// it is measured at two model sizes rather than one. It is also why the
/// <c>(IEdmModel, IEdmType, ODataPath)</c> overload is not the cheap escape it looks like: it skips
/// only the CLR→EDM type lookup, not the scan — and it cannot be used here at all, because it
/// leaves <c>ElementClrType</c> null and <c>ODataQueryOptions&lt;TEntity&gt;</c>'s constructor
/// throws on that.
/// </para>
/// <para>
/// <b>Config.</b> <c>InvocationCount</c> is pinned for the same reason as in
/// <see cref="OpenTypeKeyValidationBenchmarks"/> — so BenchmarkDotNet's pilot stage cannot size
/// ops-per-iteration differently between runs — but it is pinned FIVE ORDERS OF MAGNITUDE higher
/// than that class's 32, and that is not a style choice. The unit here is a few hundred nanoseconds,
/// so an 8,192-invocation iteration runs for ~1-9 ms, and BenchmarkDotNet's own MinIterationTime
/// warning fires. A first run at that size produced numbers that were not merely noisy but
/// impossible: <c>B_PerRequestContext</c> measured 2.9x FASTER than <c>A_SharedContext</c> in one
/// cell while doing strictly more work, and <c>C_ContextOnly</c> measured 3.8x slower over a
/// ONE-entity-set model than over a three-entity-set one. 1,048,576 puts every iteration above 100 ms even for the cheapest arm (524,288 left it at 74 ms).
/// The warmup counts come DOWN correspondingly: this class's high warmup floor exists to reach
/// tier-1 + dynamic PGO, and a single half-million-invocation iteration passes every tiering
/// threshold on its own, so 50-100 warmup iterations would only add minutes.
/// </para>
/// </remarks>
[Config(typeof(QueryContextConstructionConfig))]
[MemoryDiagnoser]
public class QueryContextConstructionBenchmarks
{
    private sealed class QueryContextConstructionConfig : ManualConfig
    {
        public QueryContextConstructionConfig()
        {
            AddJob(Job.Default
                .WithInvocationCount(1048576)
                .WithUnrollFactor(16)
                .WithMinWarmupCount(10)
                .WithMaxWarmupCount(30)
                .WithIterationCount(50));
        }
    }

    public enum QueryShape
    {
        /// <summary>No query string at all — the shape the issue reports failing under load.</summary>
        None,

        /// <summary>A typical paged, sorted, projected collection read.</summary>
        TopOrderBySelect,
    }

    /// <summary>1 = BenchWidget alone; 3 = the same model the benchmark hosts serve.</summary>
    [Params(1, 3)]
    public int EntitySets { get; set; }

    [Params(QueryShape.None, QueryShape.TopOrderBySelect)]
    public QueryShape Shape { get; set; }

    private IEdmModel _model = null!;
    private HttpRequest _request = null!;
    private ODataQueryContext _shared = null!;

    [GlobalSetup]
    public void Setup()
    {
        var builder = new ODataConventionModelBuilder();
        builder.EntitySet<BenchWidget>("BenchWidgets");
        if (EntitySets == 3)
        {
            builder.EntitySet<BenchDepartment>("BenchDepartments");
            builder.EntitySet<BenchEmployee>("BenchEmployees");
        }
        _model = builder.GetEdmModel();

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.QueryString = Shape == QueryShape.None
            ? QueryString.Empty
            : new QueryString("?$top=25&$orderby=Name&$select=Id,Name");
        _request = ctx.Request;

        // The pre-#426 shape: built once, reused for the process lifetime.
        _shared = new ODataQueryContext(_model, typeof(BenchWidget), null);
    }

    /// <summary>Pre-#426: one cached context, reused. Correct only single-threaded, which a
    /// benchmark is — that is precisely why the defect survived to production.</summary>
    [Benchmark(Baseline = true)]
    public ODataQueryOptions<BenchWidget> A_SharedContext()
        => new ODataQueryOptions<BenchWidget>(_shared, _request);

    /// <summary>Shipped: a fresh context per construction, as Microsoft.AspNetCore.OData does.</summary>
    [Benchmark]
    public ODataQueryOptions<BenchWidget> B_PerRequestContext()
        => new ODataQueryOptions<BenchWidget>(
            new ODataQueryContext(_model, typeof(BenchWidget), null), _request);

    /// <summary>The added work on its own, so B−A is attributable rather than inferred.</summary>
    [Benchmark]
    public ODataQueryContext C_ContextOnly()
        => new ODataQueryContext(_model, typeof(BenchWidget), null);
}
