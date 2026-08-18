using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData;

namespace OhData.Server.Benchmarks.Benchmarks;

// ── Fixture ──────────────────────────────────────────────────────────────────────────────────────
// Deliberately its own tiny parent/child pair rather than BenchDepartment/BenchEmployee: this class
// varies the child-collection size with [Params], and BenchOrgData's row counts are compile-time
// constants that the published server-comparison report depends on. A second fixture is cheaper than
// making the shared one parametric.

internal sealed class CeilParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<CeilChild> Children { get; set; } = new();
}

internal sealed class CeilChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
}

internal sealed class CeilDbContext : DbContext
{
    public CeilDbContext(DbContextOptions<CeilDbContext> options) : base(options) { }

    public DbSet<CeilParent> CeilParents => Set<CeilParent>();
    public DbSet<CeilChild> CeilChildren => Set<CeilChild>();

    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<CeilParent>().HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
}

internal sealed class CeilParentProfile : EntitySetProfile<int, CeilParent>
{
    public CeilParentProfile(CeilDbContext db) : base(x => x.Id)
    {
        EntitySetName = "CeilParents";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.CeilParents.AsQueryable());
        HasMany(x => x.Children); // delegate-less → $expand pushdown
    }
}

/// <summary>
/// #313: what an opted-in <c>MaxExpandTop</c> bound costs on the shape it newly covers — a BARE
/// <c>$expand</c> of a collection navigation, with no nested <c>$count</c> and no explicit nested
/// <c>$top</c>.
///
/// <para>
/// This is <b>not</b> a host-vs-host comparison like <see cref="ExpandComparisonBenchmarks"/>. Both
/// arms are OhData; they differ in exactly one setting, and therefore in exactly one thing about the
/// query EF Core composes:
/// </para>
/// <code>
/// MaxExpandTop = null  (the DEFAULT; == pre-#313 behavior on every shape)
///     LEFT JOIN "CeilChildren" AS "c" ON "p"."Id" = "c"."ParentId"
///     ORDER BY "p"."Id"
///
/// MaxExpandTop = 1000  (opted in)
///     LEFT JOIN (
///         SELECT ... FROM (
///             SELECT ..., ROW_NUMBER() OVER(PARTITION BY "c"."ParentId" ORDER BY "c"."Id") AS "row"
///             FROM "CeilChildren" AS "c") AS "c0"
///         WHERE "c0"."row" &lt;= 1001) AS "c1" ON "p"."Id" = "c1"."ParentId"
///     ORDER BY "p"."Id", "c1"."ParentId", "c1"."Id"
/// </code>
/// <para>
/// That second form is EF Core's own translation of <c>.Take(n)</c> inside a collection projection —
/// the standard top-N-per-group shape, not something OhData chooses — and it is the same shape an
/// explicit nested <c>$top</c> has always produced. The question this class answers is whether paying
/// for the window function (plus the key tiebreaker it drags into the <c>ORDER BY</c>) costs anything
/// that matters on a request that is comfortably UNDER the ceiling, since that is the case an
/// implementor who sets the knob pays on every request thereafter.
/// </para>
/// <para>
/// <b>This measures an opt-in price, not an imposed one.</b> <c>MaxExpandTop</c> is unset by default
/// (#313 stage 1), so the "capped" arm below is what a deployment chooses when it decides an
/// unbounded related-collection materialization is the larger risk. The number belongs in the knob's
/// documentation so that choice can be made with it in hand — it is not a regression budget.
/// </para>
/// <para>
/// <b>Every parameterization stays under the ceiling on purpose.</b> The over-ceiling case is a
/// <c>400</c> that short-circuits before serialization, so it is strictly cheaper than the baseline's
/// <c>200</c> and measuring it would flatter the change. <see cref="ChildrenPerParent"/> tops out at
/// 500 against a cap of 1000 for that reason.
/// </para>
/// <para>
/// Data is deterministic by construction (index-derived names and amounts) rather than seeded: both
/// arms read the SAME rows, so the <c>--seed</c> machinery the host-vs-host suite needs to keep two
/// independently-seeded servers in agreement buys nothing here.
/// </para>
/// <para>
/// Config is copied from <see cref="ExpandComparisonBenchmarks"/> deliberately — pinned
/// <c>InvocationCount</c> with <c>UnrollFactor(1)</c> (a floating invocation count is what produced
/// the pilot-stage bifurcation misread as GC bimodality on this project) and a high adaptive-warmup
/// floor. Read that class's remarks before changing anything here. As there, prefer <c>Median</c> and
/// the ordered per-iteration series over <c>Mean</c> for any scenario reported with
/// <c>MValue &gt; 2</c>.
/// </para>
/// </summary>
[Config(typeof(CeilingBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[MemoryDiagnoser]
public class BareExpandCeilingBenchmarks
{
    private sealed class CeilingBenchmarkConfig : ManualConfig
    {
        public CeilingBenchmarkConfig()
        {
            AddJob(Job.Default
                .WithInvocationCount(32)
                .WithUnrollFactor(1)
                .WithMinWarmupCount(50)
                .WithMaxWarmupCount(100)
                .WithIterationCount(30));
        }
    }

    /// <summary>Fixed parent fan-out; only the child-collection size varies.</summary>
    private const int ParentCount = 20;

    /// <summary>
    /// The ceiling the "capped" arm opts in to. <c>1000</c> is not a default any more (#313 stage 1
    /// moved the default to <c>null</c>) — it is kept here as the representative value an implementor
    /// would plausibly pick, and because every parameterization below stays under it on purpose.
    /// </summary>
    private const int OptedInCeiling = 1000;

    /// <summary>
    /// Children per parent. 5 is the everyday shape (small related collection), 50 a mid-size one, 500
    /// a genuinely large child table (10,000 rows) that is still under the 1,000 ceiling — the regime
    /// where a window function has the most room to cost something a plain join would not.
    /// </summary>
    [Params(5, 50, 500)]
    public int ChildrenPerParent { get; set; }

    private static readonly string Url = $"CeilParents?$orderby=Id&$top={ParentCount}&$expand=Children";

    private WebApplication _cappedApp = null!;
    private WebApplication _uncappedApp = null!;
    private SqliteConnection _cappedConnection = null!;
    private SqliteConnection _uncappedConnection = null!;
    private HttpClient _cappedClient = null!;
    private HttpClient _uncappedClient = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        (_cappedApp, _cappedClient, _cappedConnection) = await StartAsync(OptedInCeiling, ChildrenPerParent);
        (_uncappedApp, _uncappedClient, _uncappedConnection) = await StartAsync(null, ChildrenPerParent);

        // Correctness gate, in the same spirit as SmokeCheck: a measurement of two arms that return
        // different things is meaningless. Both must be 200 and carry every child row.
        foreach ((string arm, HttpClient client) in new[] { ("capped", _cappedClient), ("uncapped", _uncappedClient) })
        {
            using HttpResponseMessage response = await client.GetAsync(Url);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{arm} arm returned {(int)response.StatusCode}: {body}");

            int expected = ParentCount * ChildrenPerParent;
            int actual = System.Text.RegularExpressions.Regex.Matches(body, "\"ParentId\":").Count;
            if (actual != expected)
                throw new InvalidOperationException($"{arm} arm returned {actual} child rows, expected {expected}.");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _cappedClient.Dispose();
        _uncappedClient.Dispose();
        await _cappedApp.DisposeAsync();
        await _uncappedApp.DisposeAsync();
        _cappedConnection.Dispose();
        _uncappedConnection.Dispose();
    }

    // Baseline is the UNCAPPED arm: it is the SHIPPING DEFAULT (MaxExpandTop unset) and it is also
    // byte-for-byte what develop did on this shape, so the reported ratio reads directly as "what
    // setting the ceiling costs the deployment that sets it".
    [Benchmark(Baseline = true), BenchmarkCategory("BareExpand")]
    public Task<string> Uncapped_Default() => GetAsync(_uncappedClient, Url);

    [Benchmark, BenchmarkCategory("BareExpand")]
    public Task<string> Capped_OptedIn1000() => GetAsync(_cappedClient, Url);

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<(WebApplication App, HttpClient Client, SqliteConnection Connection)> StartAsync(
        int? maxExpandTop, int childrenPerParent)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddDbContext<CeilDbContext>(o => o.UseSqlite(connection));
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(BenchmarkHosts.Prefix);
            // The ONLY difference between the two arms.
            o.WithDefaults(d => d.MaxExpandTop = maxExpandTop);
            o.AddEntitySetProfile<CeilParentProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();

        using (IServiceScope scope = app.Services.CreateScope())
        {
            CeilDbContext db = scope.ServiceProvider.GetRequiredService<CeilDbContext>();
            await db.Database.EnsureCreatedAsync();
            Seed(db, childrenPerParent);
        }

        HttpClient client = ((IHost)app).GetTestClient();
        client.BaseAddress = new Uri(client.BaseAddress!, "odata/");
        return (app, client, connection);
    }

    private static void Seed(CeilDbContext db, int childrenPerParent)
    {
        int childId = 1;
        for (int p = 1; p <= ParentCount; p++)
        {
            db.CeilParents.Add(new CeilParent { Id = p, Name = $"Parent {p}" });
            for (int c = 0; c < childrenPerParent; c++, childId++)
            {
                db.CeilChildren.Add(new CeilChild
                {
                    Id = childId,
                    ParentId = p,
                    Name = $"Child {childId}",
                    Amount = childId * 1.5m,
                });
            }
        }
        db.SaveChanges();
    }
}
