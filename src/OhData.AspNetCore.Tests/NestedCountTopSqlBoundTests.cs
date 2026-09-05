using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #334: a nested `$count=true` used to DISCARD the nested `$top` SQL bound. Under the "count defers
// paging to JSON" design the count was the materialized array's length, so ApplyNavShape composed the
// MaxExpandTop count bound INSTEAD of the client's $top -- correct for the count, but throwing away a
// far tighter bound.
//
// The fix gives the projection an ExpandCountCarrier and takes the count as an INDEPENDENT correlated
// scalar subquery, as MS's SelectExpandBinder splits CreateTotalCountExpression from ProjectAsWrapper.
// The two chains never read each other.
//
// Measured pre-fix, the issue's own shapes: `$top=10` alone bounded at 10; `$top=10;$count=true`
// bounded at 1001 with cap=1000 and NOT AT ALL with cap=null. Note that second column -- the issue
// text describes only the 1001-row over-fetch, but #313 made MaxExpandTop default to null, so on the
// SHIPPING DEFAULT the symptom was a completely unbounded fetch.
//
// Fixture: P1 has 25 children, P2 has 3, so a nested $top=10 is smaller than the collection, smaller
// than any ceiling used here, and different from the count -- three independently observable facts.

public sealed class NcParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<NcChild> Children { get; set; } = new();
}

public sealed class NcChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
    public int Rank { get; set; }
}

public sealed class NcDbContext : DbContext
{
    public NcDbContext(DbContextOptions<NcDbContext> options) : base(options) { }

    public DbSet<NcParent> NcParents => Set<NcParent>();
    public DbSet<NcChild> NcChildren => Set<NcChild>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<NcParent>().HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
}

public sealed class NcParentProfile : EntitySetProfile<int, NcParent>
{
    public NcParentProfile(NcDbContext db) : base(x => x.Id)
    {
        EntitySetName = "NcParents";
        SelectEnabled = true;
        ExpandEnabled = true;
        OrderByEnabled = true;
        FilterEnabled = true;
        CountEnabled = true;
        GetQueryable = () => db.NcParents.AsQueryable();
        HasMany(x => x.Children); // delegate-less → pushed
    }
}

internal static class NestedCountSqlHarness
{
    public const int P1ChildCount = 25;
    public const int P2ChildCount = 3;

    /// <summary>The five nested clauses from #334's evidence table, in issue order.</summary>
    public static readonly (string Id, string Clause)[] Shapes =
    {
        ("A", "$top=10"),
        ("B", "$top=10;$orderby=id"),
        ("C", "$top=10;$select=Id,Name"),
        ("D", "$top=10;$count=true"),
        ("E", "$top=10;$orderby=id;$count=true;$select=Id,Name"),
    };

    /// <summary>
    /// The window/count shapes beyond the issue's table that the fix has to keep byte-identical:
    /// nested $skip/$top/$filter/$orderby combined with $count, the degenerate $top=0, and the
    /// un-windowed $count that must stay on the pre-#334 path entirely.
    /// </summary>
    public static readonly (string Id, string Clause)[] ExtraShapes =
    {
        ("F", "$skip=5;$top=3;$count=true"),
        ("G", "$skip=20;$count=true"),
        ("H", "$top=0;$count=true"),
        ("I", "$filter=rank lt 5;$top=2;$count=true"),
        ("J", "$count=true"),
        ("K", "$orderby=name desc;$top=3;$count=true"),
        ("L", "$skip=1;$top=2;$count=true;$select=Id"),
    };

    public static IEnumerable<(string Id, string Clause)> AllShapes => Shapes.Concat(ExtraShapes);

    public static string ClauseOf(string id) => AllShapes.First(s => s.Id == id).Clause;

    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection, SqlCaptureSink sink, int? maxExpandTop)
    {
        var fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.WithDefaults(d => d.MaxExpandTop = maxExpandTop);
                b.AddEntitySetProfile<NcParentProfile>();
            },
            configureServices: services =>
            {
                services.AddSingleton(sink);
                services.AddDbContext<NcDbContext>(o =>
                {
                    o.UseSqlite(connection);
                    o.LogTo(
                        message => sink.Add(message),
                        (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted);
                });
            });

        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NcDbContext>();
        db.Database.EnsureCreated();

        db.NcParents.AddRange(
            new NcParent { Id = 1, Name = "P1" },
            new NcParent { Id = 2, Name = "P2" });
        for (int i = 0; i < P1ChildCount; i++)
            db.NcChildren.Add(new NcChild { Id = 100 + i, ParentId = 1, Name = $"C1-{i:00}", Rank = i });
        for (int i = 0; i < P2ChildCount; i++)
            db.NcChildren.Add(new NcChild { Id = 200 + i, ParentId = 2, Name = $"C2-{i:00}", Rank = i });
        db.SaveChanges();
        return fx;
    }

    public static string LastParentsSelect(SqlCaptureSink sink) => sink.Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal) && s.Contains("\"NcParents\"", StringComparison.Ordinal))
        .Last();

    /// <summary>
    /// Every upper row-window bound the emitted SQL carries — the <c>N</c> of each
    /// <c>"row" &lt;= N</c> EF's ROW_NUMBER windowing produces, plus any bare <c>LIMIT N</c> that is
    /// not the root page's parameterised one. An empty result means the related collection was
    /// fetched with NO upper bound at all.
    /// </summary>
    public static IReadOnlyList<int> RowBounds(string sql) =>
        Regex.Matches(sql, @"""row""\s*<=\s*(\d+)").Select(m => int.Parse(m.Groups[1].Value))
            .Concat(Regex.Matches(sql, @"\bLIMIT\s+(\d+)").Select(m => int.Parse(m.Groups[1].Value)))
            .ToList();
}

/// <summary>
/// #334 (the defect itself): with a nested <c>$count=true</c>, the SQL row bound on the related
/// collection must reflect the REQUESTED WINDOW, not the MaxExpandTop ceiling — and must exist at
/// all when no ceiling is configured.
/// </summary>
public sealed class NestedCountTopSqlBoundTests
{
    // The issue's five shapes, at the shipping default (no ceiling) and at an explicit one. The
    // three shapes WITHOUT $count already bounded correctly and are here as the control: they must
    // keep the exact same bound, which is what proves the assertion is measuring the $count
    // interaction and not the $top pushdown in general.
    [Theory]
    [InlineData(null, "A", 10)]
    [InlineData(null, "B", 10)]
    [InlineData(null, "C", 10)]
    [InlineData(null, "D", 10)]   // pre-fix: NO bound at all (unbounded fetch)
    [InlineData(null, "E", 10)]   // pre-fix: NO bound at all (unbounded fetch)
    [InlineData(1000, "A", 10)]
    [InlineData(1000, "B", 10)]
    [InlineData(1000, "C", 10)]
    [InlineData(1000, "D", 10)]   // pre-fix: 1001 (MaxExpandTop + 1)
    [InlineData(1000, "E", 10)]   // pre-fix: 1001 (MaxExpandTop + 1)
    public async Task NestedTop_BoundsTheSqlFetch_WhetherOrNotCountIsRequested(
        int? cap, string shapeId, int expectedBound)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await NestedCountSqlHarness.BuildAsync(connection, sink, cap);
        sink.Clear();

        var resp = await fx.Client.GetAsync(
            $"/odata/NcParents?$orderby=id&$expand=Children({NestedCountSqlHarness.ClauseOf(shapeId)})");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = NestedCountSqlHarness.LastParentsSelect(sink);
        Assert.Equal(new[] { expectedBound }, NestedCountSqlHarness.RowBounds(sql));
    }

    // The nested window composes to SQL for $skip too, and for the degenerate $top=0 — neither of
    // which the pre-fix path bounded at all under the shipping default.
    [Theory]
    [InlineData("F", 8)]   // $skip=5;$top=3  → skip + top
    [InlineData("H", 0)]   // $top=0          → an empty page costs one bounded probe, not a table scan
    [InlineData("I", 2)]   // nested $filter rides the page AND the count
    [InlineData("K", 3)]
    [InlineData("L", 3)]   // $skip=1;$top=2
    public async Task NestedWindowWithCount_BoundsTheSqlFetch_NoCeilingConfigured(string shapeId, int expectedBound)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await NestedCountSqlHarness.BuildAsync(connection, sink, maxExpandTop: null);
        sink.Clear();

        var resp = await fx.Client.GetAsync(
            $"/odata/NcParents?$orderby=id&$expand=Children({NestedCountSqlHarness.ClauseOf(shapeId)})");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal(
            new[] { expectedBound },
            NestedCountSqlHarness.RowBounds(NestedCountSqlHarness.LastParentsSelect(sink)));
    }

    // #300 established that SQLite cannot translate the APPLY/LATERAL shape a windowed collection
    // projected out of a windowed collection needs. The whole fix rests on a correlated COUNT(*)
    // being a DIFFERENT shape — a scalar aggregate, which composes fine beside the ROW_NUMBER
    // window. Pinned as a live regression rather than trusted, because if it ever stops translating
    // the failure mode is a 400 on a previously-working request.
    [Fact]
    public async Task CorrelatedCountScalarSubquery_TranslatesOnSqlite_AlongsideTheRowNumberWindow()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await NestedCountSqlHarness.BuildAsync(connection, sink, maxExpandTop: null);
        sink.Clear();

        var resp = await fx.Client.GetAsync("/odata/NcParents?$orderby=id&$expand=Children($top=10;$count=true)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = NestedCountSqlHarness.LastParentsSelect(sink);
        Assert.Contains("SELECT COUNT(*)", sql, StringComparison.Ordinal);   // the count leg
        Assert.Contains("ROW_NUMBER()", sql, StringComparison.Ordinal);      // the page leg
        Assert.Contains("\"row\" <= 10", sql, StringComparison.Ordinal);     // bounded by the WINDOW
        // Not an APPLY/LATERAL — the shape #300 proved SQLite rejects. SQLite has no CROSS APPLY at
        // all, so its appearance would mean the provider had produced something untranslatable.
        Assert.DoesNotContain("APPLY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LATERAL", sql, StringComparison.OrdinalIgnoreCase);
    }

    // THE DATA-CORRECTNESS INVARIANT. OData §11.2.4.2: Nav@odata.count is the size of the FULL
    // filtered collection, never the returned page. Bounding the fetch is only admissible because
    // the count stopped riding on the fetched array's length — a fix that bounded the fetch and
    // under-reported the count would be far worse than the perf bug it replaced.
    [Theory]
    [InlineData(null)]
    [InlineData(1000)]
    public async Task NestedCount_ReportsTheFullCollection_NotTheWindowedPage(int? cap)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await NestedCountSqlHarness.BuildAsync(connection, sink, cap);

        var resp = await fx.Client.GetAsync("/odata/NcParents?$orderby=id&$expand=Children($top=3;$count=true)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();

        // P1: 25 children, page of 3. P2: 3 children, page of 3 (the whole collection).
        Assert.Contains($"\"Children@odata.count\":{NestedCountSqlHarness.P1ChildCount}", body);
        Assert.Contains($"\"Children@odata.count\":{NestedCountSqlHarness.P2ChildCount}", body);
        // …and the page really is 3 long, so the count is not merely the page by coincidence.
        Assert.Equal(3, Regex.Matches(body, "\"Name\":\"C1-").Count);
    }

    // The count leg carries the nested $filter (it counts the FILTERED collection) but never the
    // $orderby/$skip/$top (it counts the UNWINDOWED one). Rank < 5 selects 5 of P1's 25 children
    // and all 3 of P2's, so every one of those numbers is distinct from both the page size and the
    // unfiltered total — the assertion cannot pass by accident.
    [Fact]
    public async Task NestedCount_WithNestedFilterAndWindow_CountsTheFilteredButUnwindowedCollection()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await NestedCountSqlHarness.BuildAsync(connection, sink, maxExpandTop: null);

        var resp = await fx.Client.GetAsync(
            "/odata/NcParents?$orderby=id&$expand=Children($filter=rank lt 5;$top=2;$count=true)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();

        Assert.Contains("\"Children@odata.count\":5", body);  // P1: filtered 5, NOT 25 and NOT 2
        Assert.Contains("\"Children@odata.count\":3", body);  // P2: filtered 3, NOT 2
        Assert.Contains("\"Name\":\"C1-00\"", body);
        Assert.Contains("\"Name\":\"C1-01\"", body);
        Assert.DoesNotContain("\"Name\":\"C1-02\"", body);    // windowed out by $top=2
    }

    // The MaxExpandTop ceiling is RE-SITED, not removed: the breach signal moves from "the
    // materialized array is longer than the cap" to "the exact count is greater than the cap". The
    // proof case is a breach WITH a small $top — only 2 rows are fetched, yet the request still
    // 400s, because the scalar count reports the true 25. Under the pre-fix design that could not
    // work: the array WAS the count, so detecting the breach required over-fetching to cap + 1.
    [Fact]
    public async Task CeilingBreach_StillRejected_EvenThoughOnlyTheWindowWasFetched()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await NestedCountSqlHarness.BuildAsync(connection, sink, maxExpandTop: 5);
        sink.Clear();

        var resp = await fx.Client.GetAsync("/odata/NcParents?$orderby=id&$expand=Children($top=2;$count=true)");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("The nested '$count' on 'Children' cannot be computed", body);
        Assert.Contains("exceeds the maximum of 5 entities", body);

        // The fetch really was bounded to the window — the 400 came from the count subquery, not
        // from having materialized a cap+1 probe.
        Assert.Equal(
            new[] { 2 },
            NestedCountSqlHarness.RowBounds(NestedCountSqlHarness.LastParentsSelect(sink)));
    }

    // A collection exactly AT the ceiling still succeeds, with the exact count — the boundary the
    // re-sited check has to agree with the pre-fix one on. P2 has exactly 3 children.
    [Fact]
    public async Task CollectionAtTheCeiling_Succeeds_WithTheExactCount()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await NestedCountSqlHarness.BuildAsync(connection, sink, maxExpandTop: 3);

        var resp = await fx.Client.GetAsync(
            "/odata/NcParents?$filter=id eq 2&$orderby=id&$expand=Children($top=1;$count=true)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Children@odata.count\":3", body);
        Assert.Contains("\"Name\":\"C2-00\"", body);
        Assert.DoesNotContain("\"Name\":\"C2-01\"", body);
    }

    // SCOPE GUARD. The carrier is engaged only for a counted nav that carries an actual nested
    // window; without one there is nothing to bound, so engaging it would add a count subquery for
    // no benefit. This pins that the un-windowed $count stays on the pre-#334 path — same bound
    // (cap + 1), and NO count subquery in the SQL.
    [Fact]
    public async Task CountWithoutAWindow_StaysOnThePreFixPath_NoCountSubquery()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await NestedCountSqlHarness.BuildAsync(connection, sink, maxExpandTop: 1000);
        sink.Clear();

        var resp = await fx.Client.GetAsync("/odata/NcParents?$orderby=id&$expand=Children($count=true)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = NestedCountSqlHarness.LastParentsSelect(sink);
        Assert.Equal(new[] { 1001 }, NestedCountSqlHarness.RowBounds(sql));
        Assert.DoesNotContain("SELECT COUNT(*)", sql, StringComparison.Ordinal);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains($"\"Children@odata.count\":{NestedCountSqlHarness.P1ChildCount}", body);
    }

    // SCOPE GUARD. A counted nav that itself carries nested $expand children keeps the pre-#334
    // path in full: windowing THIS level while also projecting a further collection out of each of
    // its elements is the APPLY/LATERAL shape #298/#304 established SQLite cannot translate, so the
    // carrier deliberately does not decorate it.
    [Fact]
    public async Task CountedNavWithNestedChildren_StaysOnThePreFixPath()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new OptionedExpandDelegateCounter();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await OptionedExpandSqliteHarness.BuildAsync(
            connection, counter, sink, d => d.MaxExpandTop = 1000);
        sink.Clear();

        var resp = await fx.Client.GetAsync(
            "/odata/MixParents?$orderby=id&$expand=Pushed($top=1;$count=true;$expand=Subs)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = OptionedExpandSqliteHarness.LastSelectAgainst(sink, "MixParents");
        Assert.DoesNotContain("SELECT COUNT(*)", sql, StringComparison.Ordinal);

        string body = await resp.Content.ReadAsStringAsync();
        // Count is still the full collection (2 pushed children) and the page is still 1 long.
        Assert.Contains("\"Pushed@odata.count\":2", body);
        Assert.Contains("\"Push-A\"", body);
        Assert.DoesNotContain("\"Push-B\"", body);
    }

    // SAFETY INVARIANT (unchanged by #334, asserted because the carrier touches the projection
    // builder). A delegate-backed navigation is never in engagedExpandNavs, so it can never be
    // carrier-decorated. Asserted on a MIXED request — the carrier IS engaged for the delegate-less
    // `Pushed` nav in the same query — because that is the shape where a leak could actually happen:
    // the count subqueries are appended to the very projection the sibling navs are bound into.
    //
    // (A nested $top/$skip on a delegate-backed nav is a 400 by design since #294, so `Delegated`
    // carries only $count here; that rejection is pushdown-independent and untouched by #334.)
    [Fact]
    public async Task MixedExpand_CarrierOnThePushedNav_LeavesTheDelegateBackedNavAlone()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new OptionedExpandDelegateCounter();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await OptionedExpandSqliteHarness.BuildAsync(connection, counter, sink);
        sink.Clear();

        var resp = await fx.Client.GetAsync(
            "/odata/MixParents?$orderby=id&$expand=Pushed($top=1;$count=true),Delegated($count=true)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = OptionedExpandSqliteHarness.LastSelectAgainst(sink, "MixParents");
        // The pushed nav rides the carrier: bounded to the requested window, count as a scalar.
        Assert.Contains("\"MixPushChildren\"", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT COUNT(*)", sql, StringComparison.Ordinal);
        Assert.Contains("\"row\" <= 1", sql, StringComparison.Ordinal);
        // The delegate-backed nav is nowhere in that query — neither joined nor counted.
        Assert.DoesNotContain("\"MixDelChildren\"", sql, StringComparison.Ordinal);
        Assert.True(counter.DelegatedCalls > 0, "the delegate-backed navigation must load via its delegate");

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Pushed@odata.count\":2", body);   // full collection, page is 1
        Assert.Contains("\"Push-A\"", body);
        Assert.DoesNotContain("\"Push-B\"", body);
        Assert.Contains("\"Del-A\"", body);
        // #650 repaired what this line used to document as a pre-existing gap ("a nested $count on
        // a DELEGATE-BACKED nav emits no Nav@odata.count at all"). It emits one now, and it is
        // honest: the delegate's answer is never windowed, so the array IS the full related
        // collection. The #334 invariant this test exists for is unchanged and asserted above —
        // the count comes from the materialized children, NOT from a carrier subquery, which is why
        // "MixDelChildren" is still absent from the SQL.
        Assert.Contains("\"Delegated@odata.count\":1", body);
    }
}

/// <summary>
/// #334 BYTE-IDENTITY GOLDEN MASTER. Every expected value below was captured from the PRE-FIX build
/// (develop at 3c48782) and pasted verbatim, so these assertions pass on the pre-fix tree as well
/// as the fixed one. That is the point: #334 is an optimisation of how the page is fetched, not a
/// change to what is returned, so the only thing that may differ between the two trees is the
/// emitted SQL — which <see cref="NestedCountTopSqlBoundTests"/> asserts separately.
/// <para>
/// 12 clauses × 3 ceilings (none / 1000 / 5) = 36 responses, status line included, so the ceiling
/// breaches at cap=5 are pinned as 400s with their exact error bodies too.
/// </para>
/// </summary>
public sealed class NestedCountTopByteIdentityTests
{
    // Key: "{cap}|{shapeId}". Value: the pre-fix (status, body).
    private static readonly Dictionary<string, (int Status, string Body)> Expected = new(StringComparer.Ordinal)
    {
        // GENERATED FROM THE PRE-FIX BUILD — do not hand-edit. See the class remarks.
        ["null|A"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""ParentId"":1,""Name"":""C1-00"",""Rank"":0},{""Id"":101,""ParentId"":1,""Name"":""C1-01"",""Rank"":1},{""Id"":102,""ParentId"":1,""Name"":""C1-02"",""Rank"":2},{""Id"":103,""ParentId"":1,""Name"":""C1-03"",""Rank"":3},{""Id"":104,""ParentId"":1,""Name"":""C1-04"",""Rank"":4},{""Id"":105,""ParentId"":1,""Name"":""C1-05"",""Rank"":5},{""Id"":106,""ParentId"":1,""Name"":""C1-06"",""Rank"":6},{""Id"":107,""ParentId"":1,""Name"":""C1-07"",""Rank"":7},{""Id"":108,""ParentId"":1,""Name"":""C1-08"",""Rank"":8},{""Id"":109,""ParentId"":1,""Name"":""C1-09"",""Rank"":9}]},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1},{""Id"":202,""ParentId"":2,""Name"":""C2-02"",""Rank"":2}]}]}"),
        ["null|B"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""ParentId"":1,""Name"":""C1-00"",""Rank"":0},{""Id"":101,""ParentId"":1,""Name"":""C1-01"",""Rank"":1},{""Id"":102,""ParentId"":1,""Name"":""C1-02"",""Rank"":2},{""Id"":103,""ParentId"":1,""Name"":""C1-03"",""Rank"":3},{""Id"":104,""ParentId"":1,""Name"":""C1-04"",""Rank"":4},{""Id"":105,""ParentId"":1,""Name"":""C1-05"",""Rank"":5},{""Id"":106,""ParentId"":1,""Name"":""C1-06"",""Rank"":6},{""Id"":107,""ParentId"":1,""Name"":""C1-07"",""Rank"":7},{""Id"":108,""ParentId"":1,""Name"":""C1-08"",""Rank"":8},{""Id"":109,""ParentId"":1,""Name"":""C1-09"",""Rank"":9}]},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1},{""Id"":202,""ParentId"":2,""Name"":""C2-02"",""Rank"":2}]}]}"),
        ["null|C"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""Name"":""C1-00""},{""Id"":101,""Name"":""C1-01""},{""Id"":102,""Name"":""C1-02""},{""Id"":103,""Name"":""C1-03""},{""Id"":104,""Name"":""C1-04""},{""Id"":105,""Name"":""C1-05""},{""Id"":106,""Name"":""C1-06""},{""Id"":107,""Name"":""C1-07""},{""Id"":108,""Name"":""C1-08""},{""Id"":109,""Name"":""C1-09""}]},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""Name"":""C2-00""},{""Id"":201,""Name"":""C2-01""},{""Id"":202,""Name"":""C2-02""}]}]}"),
        ["null|D"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""ParentId"":1,""Name"":""C1-00"",""Rank"":0},{""Id"":101,""ParentId"":1,""Name"":""C1-01"",""Rank"":1},{""Id"":102,""ParentId"":1,""Name"":""C1-02"",""Rank"":2},{""Id"":103,""ParentId"":1,""Name"":""C1-03"",""Rank"":3},{""Id"":104,""ParentId"":1,""Name"":""C1-04"",""Rank"":4},{""Id"":105,""ParentId"":1,""Name"":""C1-05"",""Rank"":5},{""Id"":106,""ParentId"":1,""Name"":""C1-06"",""Rank"":6},{""Id"":107,""ParentId"":1,""Name"":""C1-07"",""Rank"":7},{""Id"":108,""ParentId"":1,""Name"":""C1-08"",""Rank"":8},{""Id"":109,""ParentId"":1,""Name"":""C1-09"",""Rank"":9}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1},{""Id"":202,""ParentId"":2,""Name"":""C2-02"",""Rank"":2}],""Children@odata.count"":3}]}"),
        ["null|E"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""Name"":""C1-00""},{""Id"":101,""Name"":""C1-01""},{""Id"":102,""Name"":""C1-02""},{""Id"":103,""Name"":""C1-03""},{""Id"":104,""Name"":""C1-04""},{""Id"":105,""Name"":""C1-05""},{""Id"":106,""Name"":""C1-06""},{""Id"":107,""Name"":""C1-07""},{""Id"":108,""Name"":""C1-08""},{""Id"":109,""Name"":""C1-09""}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""Name"":""C2-00""},{""Id"":201,""Name"":""C2-01""},{""Id"":202,""Name"":""C2-02""}],""Children@odata.count"":3}]}"),
        ["null|F"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":105,""ParentId"":1,""Name"":""C1-05"",""Rank"":5},{""Id"":106,""ParentId"":1,""Name"":""C1-06"",""Rank"":6},{""Id"":107,""ParentId"":1,""Name"":""C1-07"",""Rank"":7}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[],""Children@odata.count"":3}]}"),
        ["null|G"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":120,""ParentId"":1,""Name"":""C1-20"",""Rank"":20},{""Id"":121,""ParentId"":1,""Name"":""C1-21"",""Rank"":21},{""Id"":122,""ParentId"":1,""Name"":""C1-22"",""Rank"":22},{""Id"":123,""ParentId"":1,""Name"":""C1-23"",""Rank"":23},{""Id"":124,""ParentId"":1,""Name"":""C1-24"",""Rank"":24}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[],""Children@odata.count"":3}]}"),
        ["null|H"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[],""Children@odata.count"":3}]}"),
        ["null|I"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""ParentId"":1,""Name"":""C1-00"",""Rank"":0},{""Id"":101,""ParentId"":1,""Name"":""C1-01"",""Rank"":1}],""Children@odata.count"":5},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1}],""Children@odata.count"":3}]}"),
        ["null|J"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""ParentId"":1,""Name"":""C1-00"",""Rank"":0},{""Id"":101,""ParentId"":1,""Name"":""C1-01"",""Rank"":1},{""Id"":102,""ParentId"":1,""Name"":""C1-02"",""Rank"":2},{""Id"":103,""ParentId"":1,""Name"":""C1-03"",""Rank"":3},{""Id"":104,""ParentId"":1,""Name"":""C1-04"",""Rank"":4},{""Id"":105,""ParentId"":1,""Name"":""C1-05"",""Rank"":5},{""Id"":106,""ParentId"":1,""Name"":""C1-06"",""Rank"":6},{""Id"":107,""ParentId"":1,""Name"":""C1-07"",""Rank"":7},{""Id"":108,""ParentId"":1,""Name"":""C1-08"",""Rank"":8},{""Id"":109,""ParentId"":1,""Name"":""C1-09"",""Rank"":9},{""Id"":110,""ParentId"":1,""Name"":""C1-10"",""Rank"":10},{""Id"":111,""ParentId"":1,""Name"":""C1-11"",""Rank"":11},{""Id"":112,""ParentId"":1,""Name"":""C1-12"",""Rank"":12},{""Id"":113,""ParentId"":1,""Name"":""C1-13"",""Rank"":13},{""Id"":114,""ParentId"":1,""Name"":""C1-14"",""Rank"":14},{""Id"":115,""ParentId"":1,""Name"":""C1-15"",""Rank"":15},{""Id"":116,""ParentId"":1,""Name"":""C1-16"",""Rank"":16},{""Id"":117,""ParentId"":1,""Name"":""C1-17"",""Rank"":17},{""Id"":118,""ParentId"":1,""Name"":""C1-18"",""Rank"":18},{""Id"":119,""ParentId"":1,""Name"":""C1-19"",""Rank"":19},{""Id"":120,""ParentId"":1,""Name"":""C1-20"",""Rank"":20},{""Id"":121,""ParentId"":1,""Name"":""C1-21"",""Rank"":21},{""Id"":122,""ParentId"":1,""Name"":""C1-22"",""Rank"":22},{""Id"":123,""ParentId"":1,""Name"":""C1-23"",""Rank"":23},{""Id"":124,""ParentId"":1,""Name"":""C1-24"",""Rank"":24}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1},{""Id"":202,""ParentId"":2,""Name"":""C2-02"",""Rank"":2}],""Children@odata.count"":3}]}"),
        ["null|K"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":124,""ParentId"":1,""Name"":""C1-24"",""Rank"":24},{""Id"":123,""ParentId"":1,""Name"":""C1-23"",""Rank"":23},{""Id"":122,""ParentId"":1,""Name"":""C1-22"",""Rank"":22}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":202,""ParentId"":2,""Name"":""C2-02"",""Rank"":2},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1},{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0}],""Children@odata.count"":3}]}"),
        ["null|L"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":101},{""Id"":102}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":201},{""Id"":202}],""Children@odata.count"":3}]}"),
        ["1000|A"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""ParentId"":1,""Name"":""C1-00"",""Rank"":0},{""Id"":101,""ParentId"":1,""Name"":""C1-01"",""Rank"":1},{""Id"":102,""ParentId"":1,""Name"":""C1-02"",""Rank"":2},{""Id"":103,""ParentId"":1,""Name"":""C1-03"",""Rank"":3},{""Id"":104,""ParentId"":1,""Name"":""C1-04"",""Rank"":4},{""Id"":105,""ParentId"":1,""Name"":""C1-05"",""Rank"":5},{""Id"":106,""ParentId"":1,""Name"":""C1-06"",""Rank"":6},{""Id"":107,""ParentId"":1,""Name"":""C1-07"",""Rank"":7},{""Id"":108,""ParentId"":1,""Name"":""C1-08"",""Rank"":8},{""Id"":109,""ParentId"":1,""Name"":""C1-09"",""Rank"":9}]},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1},{""Id"":202,""ParentId"":2,""Name"":""C2-02"",""Rank"":2}]}]}"),
        ["1000|B"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""ParentId"":1,""Name"":""C1-00"",""Rank"":0},{""Id"":101,""ParentId"":1,""Name"":""C1-01"",""Rank"":1},{""Id"":102,""ParentId"":1,""Name"":""C1-02"",""Rank"":2},{""Id"":103,""ParentId"":1,""Name"":""C1-03"",""Rank"":3},{""Id"":104,""ParentId"":1,""Name"":""C1-04"",""Rank"":4},{""Id"":105,""ParentId"":1,""Name"":""C1-05"",""Rank"":5},{""Id"":106,""ParentId"":1,""Name"":""C1-06"",""Rank"":6},{""Id"":107,""ParentId"":1,""Name"":""C1-07"",""Rank"":7},{""Id"":108,""ParentId"":1,""Name"":""C1-08"",""Rank"":8},{""Id"":109,""ParentId"":1,""Name"":""C1-09"",""Rank"":9}]},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1},{""Id"":202,""ParentId"":2,""Name"":""C2-02"",""Rank"":2}]}]}"),
        ["1000|C"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""Name"":""C1-00""},{""Id"":101,""Name"":""C1-01""},{""Id"":102,""Name"":""C1-02""},{""Id"":103,""Name"":""C1-03""},{""Id"":104,""Name"":""C1-04""},{""Id"":105,""Name"":""C1-05""},{""Id"":106,""Name"":""C1-06""},{""Id"":107,""Name"":""C1-07""},{""Id"":108,""Name"":""C1-08""},{""Id"":109,""Name"":""C1-09""}]},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""Name"":""C2-00""},{""Id"":201,""Name"":""C2-01""},{""Id"":202,""Name"":""C2-02""}]}]}"),
        ["1000|D"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""ParentId"":1,""Name"":""C1-00"",""Rank"":0},{""Id"":101,""ParentId"":1,""Name"":""C1-01"",""Rank"":1},{""Id"":102,""ParentId"":1,""Name"":""C1-02"",""Rank"":2},{""Id"":103,""ParentId"":1,""Name"":""C1-03"",""Rank"":3},{""Id"":104,""ParentId"":1,""Name"":""C1-04"",""Rank"":4},{""Id"":105,""ParentId"":1,""Name"":""C1-05"",""Rank"":5},{""Id"":106,""ParentId"":1,""Name"":""C1-06"",""Rank"":6},{""Id"":107,""ParentId"":1,""Name"":""C1-07"",""Rank"":7},{""Id"":108,""ParentId"":1,""Name"":""C1-08"",""Rank"":8},{""Id"":109,""ParentId"":1,""Name"":""C1-09"",""Rank"":9}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1},{""Id"":202,""ParentId"":2,""Name"":""C2-02"",""Rank"":2}],""Children@odata.count"":3}]}"),
        ["1000|E"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""Name"":""C1-00""},{""Id"":101,""Name"":""C1-01""},{""Id"":102,""Name"":""C1-02""},{""Id"":103,""Name"":""C1-03""},{""Id"":104,""Name"":""C1-04""},{""Id"":105,""Name"":""C1-05""},{""Id"":106,""Name"":""C1-06""},{""Id"":107,""Name"":""C1-07""},{""Id"":108,""Name"":""C1-08""},{""Id"":109,""Name"":""C1-09""}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""Name"":""C2-00""},{""Id"":201,""Name"":""C2-01""},{""Id"":202,""Name"":""C2-02""}],""Children@odata.count"":3}]}"),
        ["1000|F"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":105,""ParentId"":1,""Name"":""C1-05"",""Rank"":5},{""Id"":106,""ParentId"":1,""Name"":""C1-06"",""Rank"":6},{""Id"":107,""ParentId"":1,""Name"":""C1-07"",""Rank"":7}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[],""Children@odata.count"":3}]}"),
        ["1000|G"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":120,""ParentId"":1,""Name"":""C1-20"",""Rank"":20},{""Id"":121,""ParentId"":1,""Name"":""C1-21"",""Rank"":21},{""Id"":122,""ParentId"":1,""Name"":""C1-22"",""Rank"":22},{""Id"":123,""ParentId"":1,""Name"":""C1-23"",""Rank"":23},{""Id"":124,""ParentId"":1,""Name"":""C1-24"",""Rank"":24}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[],""Children@odata.count"":3}]}"),
        ["1000|H"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[],""Children@odata.count"":3}]}"),
        ["1000|I"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""ParentId"":1,""Name"":""C1-00"",""Rank"":0},{""Id"":101,""ParentId"":1,""Name"":""C1-01"",""Rank"":1}],""Children@odata.count"":5},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1}],""Children@odata.count"":3}]}"),
        ["1000|J"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""ParentId"":1,""Name"":""C1-00"",""Rank"":0},{""Id"":101,""ParentId"":1,""Name"":""C1-01"",""Rank"":1},{""Id"":102,""ParentId"":1,""Name"":""C1-02"",""Rank"":2},{""Id"":103,""ParentId"":1,""Name"":""C1-03"",""Rank"":3},{""Id"":104,""ParentId"":1,""Name"":""C1-04"",""Rank"":4},{""Id"":105,""ParentId"":1,""Name"":""C1-05"",""Rank"":5},{""Id"":106,""ParentId"":1,""Name"":""C1-06"",""Rank"":6},{""Id"":107,""ParentId"":1,""Name"":""C1-07"",""Rank"":7},{""Id"":108,""ParentId"":1,""Name"":""C1-08"",""Rank"":8},{""Id"":109,""ParentId"":1,""Name"":""C1-09"",""Rank"":9},{""Id"":110,""ParentId"":1,""Name"":""C1-10"",""Rank"":10},{""Id"":111,""ParentId"":1,""Name"":""C1-11"",""Rank"":11},{""Id"":112,""ParentId"":1,""Name"":""C1-12"",""Rank"":12},{""Id"":113,""ParentId"":1,""Name"":""C1-13"",""Rank"":13},{""Id"":114,""ParentId"":1,""Name"":""C1-14"",""Rank"":14},{""Id"":115,""ParentId"":1,""Name"":""C1-15"",""Rank"":15},{""Id"":116,""ParentId"":1,""Name"":""C1-16"",""Rank"":16},{""Id"":117,""ParentId"":1,""Name"":""C1-17"",""Rank"":17},{""Id"":118,""ParentId"":1,""Name"":""C1-18"",""Rank"":18},{""Id"":119,""ParentId"":1,""Name"":""C1-19"",""Rank"":19},{""Id"":120,""ParentId"":1,""Name"":""C1-20"",""Rank"":20},{""Id"":121,""ParentId"":1,""Name"":""C1-21"",""Rank"":21},{""Id"":122,""ParentId"":1,""Name"":""C1-22"",""Rank"":22},{""Id"":123,""ParentId"":1,""Name"":""C1-23"",""Rank"":23},{""Id"":124,""ParentId"":1,""Name"":""C1-24"",""Rank"":24}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1},{""Id"":202,""ParentId"":2,""Name"":""C2-02"",""Rank"":2}],""Children@odata.count"":3}]}"),
        ["1000|K"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":124,""ParentId"":1,""Name"":""C1-24"",""Rank"":24},{""Id"":123,""ParentId"":1,""Name"":""C1-23"",""Rank"":23},{""Id"":122,""ParentId"":1,""Name"":""C1-22"",""Rank"":22}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":202,""ParentId"":2,""Name"":""C2-02"",""Rank"":2},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1},{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0}],""Children@odata.count"":3}]}"),
        ["1000|L"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":101},{""Id"":102}],""Children@odata.count"":25},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":201},{""Id"":202}],""Children@odata.count"":3}]}"),
        ["5|A"] = (400, @"{""error"":{""code"":""InvalidQueryOption"",""message"":""The value of '$top' (10) on the expanded navigation 'Children' exceeds the maximum allowed value (5).""}}"),
        ["5|B"] = (400, @"{""error"":{""code"":""InvalidQueryOption"",""message"":""The value of '$top' (10) on the expanded navigation 'Children' exceeds the maximum allowed value (5).""}}"),
        ["5|C"] = (400, @"{""error"":{""code"":""InvalidQueryOption"",""message"":""The value of '$top' (10) on the expanded navigation 'Children' exceeds the maximum allowed value (5).""}}"),
        ["5|D"] = (400, @"{""error"":{""code"":""InvalidQueryOption"",""message"":""The value of '$top' (10) on the expanded navigation 'Children' exceeds the maximum allowed value (5).""}}"),
        ["5|E"] = (400, @"{""error"":{""code"":""InvalidQueryOption"",""message"":""The value of '$top' (10) on the expanded navigation 'Children' exceeds the maximum allowed value (5).""}}"),
        ["5|F"] = (400, @"{""error"":{""code"":""InvalidQueryOption"",""message"":""The nested '$count' on 'Children' cannot be computed: the related collection exceeds the maximum of 5 entities. Narrow it with a nested $filter.""}}"),
        ["5|G"] = (400, @"{""error"":{""code"":""InvalidQueryOption"",""message"":""The nested '$count' on 'Children' cannot be computed: the related collection exceeds the maximum of 5 entities. Narrow it with a nested $filter.""}}"),
        ["5|H"] = (400, @"{""error"":{""code"":""InvalidQueryOption"",""message"":""The nested '$count' on 'Children' cannot be computed: the related collection exceeds the maximum of 5 entities. Narrow it with a nested $filter.""}}"),
        ["5|I"] = (200, @"{""@odata.context"":""http://localhost/odata/$metadata#NcParents"",""value"":[{""Id"":1,""Name"":""P1"",""Children"":[{""Id"":100,""ParentId"":1,""Name"":""C1-00"",""Rank"":0},{""Id"":101,""ParentId"":1,""Name"":""C1-01"",""Rank"":1}],""Children@odata.count"":5},{""Id"":2,""Name"":""P2"",""Children"":[{""Id"":200,""ParentId"":2,""Name"":""C2-00"",""Rank"":0},{""Id"":201,""ParentId"":2,""Name"":""C2-01"",""Rank"":1}],""Children@odata.count"":3}]}"),
        ["5|J"] = (400, @"{""error"":{""code"":""InvalidQueryOption"",""message"":""The nested '$count' on 'Children' cannot be computed: the related collection exceeds the maximum of 5 entities. Narrow it with a nested $filter.""}}"),
        ["5|K"] = (400, @"{""error"":{""code"":""InvalidQueryOption"",""message"":""The nested '$count' on 'Children' cannot be computed: the related collection exceeds the maximum of 5 entities. Narrow it with a nested $filter.""}}"),
        ["5|L"] = (400, @"{""error"":{""code"":""InvalidQueryOption"",""message"":""The nested '$count' on 'Children' cannot be computed: the related collection exceeds the maximum of 5 entities. Narrow it with a nested $filter.""}}"),
    };

    public static TheoryData<string> Keys
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string k in Expected.Keys) data.Add(k);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task ResponseIsByteIdenticalToThePreFixBuild(string key)
    {
        string[] parts = key.Split('|');
        int? cap = parts[0] == "null" ? null : int.Parse(parts[0]);
        string clause = NestedCountSqlHarness.ClauseOf(parts[1]);

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await NestedCountSqlHarness.BuildAsync(connection, sink, cap);

        var resp = await fx.Client.GetAsync($"/odata/NcParents?$orderby=id&$expand=Children({clause})");
        string body = await resp.Content.ReadAsStringAsync();

        (int expectedStatus, string expectedBody) = Expected[key];
        Assert.Equal(expectedStatus, (int)resp.StatusCode);
        Assert.Equal(expectedBody, body);
    }
}
