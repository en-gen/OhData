using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #254 (item 2): a $levels expand may now ALSO carry $filter / $orderby / $skip / $top / $count /
// $select and still push down. Those options apply at EVERY level of the recursion — the reading ODL
// itself implements (SelectExpandQueryOption.ProcessLevels rewrites $levels=N into N nested expand
// items each carrying the SAME Filter/OrderBy/Top/Skip/Count and the same nested $select clause), and
// the reading the spec's own equivalence example implies ($expand=ReportsTo($levels=3) returns "the
// manager, manager's manager, and manager's manager's manager").
//
// STILL DEFERRED: a $levels item that also carries its own nested $expand (depth accounting between
// the $levels budget and the nested branch's remainingDepth is ambiguous against MaxExpansionDepth).
//
// THE SAFETY INVARIANT is unchanged and pinned below: a self-referential navigation declared WITH a
// delegate is never EF-included by the $levels path, whatever nested options the request carries and
// whatever order the profiles registered in.

// ── Fixtures ────────────────────────────────────────────────────────────────────────────────────

// Self-referential hierarchy with a filterable/sortable column, plus a NON-self navigation (Tags) so
// the still-deferred "$levels + nested $expand" case has a real EDM navigation to name.
public sealed class LvNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public int? ParentId { get; set; }
    public List<LvNode> Children { get; set; } = new();
    public List<LvTag> Tags { get; set; } = new();
}

public sealed class LvTag
{
    public int Id { get; set; }
    public int NodeId { get; set; }
    public string Label { get; set; } = "";
}

// Self-referential nav renamed with [JsonPropertyName] — the $select self-nav augmentation must key
// off the EDM name, never the raw CLR name.
public sealed class LvRenamedNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }

    [JsonPropertyName("kids")]
    public List<LvRenamedNode> Children { get; set; } = new();
}

public sealed class LevelsOptionsDbContext : DbContext
{
    public LevelsOptionsDbContext(DbContextOptions<LevelsOptionsDbContext> options) : base(options) { }

    public DbSet<LvNode> LvNodes => Set<LvNode>();
    public DbSet<LvTag> LvTags => Set<LvTag>();
    public DbSet<LvRenamedNode> LvRenamedNodes => Set<LvRenamedNode>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<LvNode>().HasMany(n => n.Children).WithOne().HasForeignKey(n => n.ParentId);
        b.Entity<LvNode>().HasMany(n => n.Tags).WithOne().HasForeignKey(t => t.NodeId);
        b.Entity<LvRenamedNode>().HasMany(n => n.Children).WithOne().HasForeignKey(n => n.ParentId);
    }
}

public sealed class LevelsDelegateCounter
{
    private int _childCalls;
    public int ChildCalls => _childCalls;
    public void CountChildCall() => Interlocked.Increment(ref _childCalls);
}

// Delegate-LESS self-referential set (default MaxExpansionDepth of 3).
public sealed class LvNodeProfile : EntitySetProfile<int, LvNode>
{
    public LvNodeProfile(LevelsOptionsDbContext db) : base(x => x.Id)
    {
        EntitySetName = "LvNodes";
        ExpandEnabled = true;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        GetQueryable = _ => Task.FromResult(db.LvNodes.AsQueryable());
        HasMany(x => x.Children); // delegate-less, self-referential → $levels-pushable
        HasMany(x => x.Tags);     // delegate-less non-self nav
    }
}

// Same CLR type, per-profile MaxExpansionDepth override — the ceiling $levels=max resolves to.
public sealed class LvShallowNodeProfile : EntitySetProfile<int, LvNode>
{
    public LvShallowNodeProfile(LevelsOptionsDbContext db) : base(x => x.Id)
    {
        EntitySetName = "LvShallowNodes";
        ExpandEnabled = true;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        MaxExpansionDepth = 2;
        GetQueryable = _ => Task.FromResult(db.LvNodes.AsQueryable());
        HasMany(x => x.Children);
        HasMany(x => x.Tags);
    }
}

// DELEGATE-BACKED self-referential set over the SAME CLR type. The delegate is the security boundary
// (imagine it filtering/authorizing) and must never be bypassed by a self-JOIN, in either
// registration order.
public sealed class LvSecureNodeProfile : EntitySetProfile<int, LvNode>
{
    public LvSecureNodeProfile(LevelsOptionsDbContext db, LevelsDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "LvSecureNodes";
        ExpandEnabled = true;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        GetQueryable = _ => Task.FromResult(db.LvNodes.AsQueryable());
        HasMany(x => x.Children,
            getAll: (nodeId, ct) =>
            {
                counter.CountChildCall();
                return Task.FromResult<IEnumerable<LvNode>>(
                    db.LvNodes.Where(n => n.ParentId == nodeId).ToList());
            });
    }
}

public sealed class LvRenamedNodeProfile : EntitySetProfile<int, LvRenamedNode>
{
    public LvRenamedNodeProfile(LevelsOptionsDbContext db) : base(x => x.Id)
    {
        EntitySetName = "LvRenamedNodes";
        ExpandEnabled = true;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        GetQueryable = _ => Task.FromResult(db.LvRenamedNodes.AsQueryable());
        HasMany(x => x.Children);
    }
}

internal static class LevelsOptionsSqliteHarness
{
    // Hierarchy (4 deep), sized so the per-level assertions are unambiguous:
    //   Root(1)
    //     ├─ A(2, active)      ├─ A1(4, active) ── A1a(8, active)
    //     │                    ├─ A2(5, inactive)
    //     │                    └─ A3(6, active)
    //     └─ B(3, inactive)    └─ B1(7, active)
    // Root has 2 children; A has 3 (more than the MaxExpandTop=2 fixture's ceiling, so the per-level
    // count bound can be proven to bite at a DEEPER level than the root).
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection, LevelsDelegateCounter counter, SqlCaptureSink? sink,
        bool delegatelessFirst = true, Action<EntitySetDefaults>? defaults = null)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                if (defaults is not null) b.WithDefaults(defaults);
                if (delegatelessFirst)
                {
                    b.AddEntitySetProfile<LvNodeProfile>();
                    b.AddEntitySetProfile<LvSecureNodeProfile>();
                }
                else
                {
                    b.AddEntitySetProfile<LvSecureNodeProfile>();
                    b.AddEntitySetProfile<LvNodeProfile>();
                }
                b.AddEntitySetProfile<LvShallowNodeProfile>();
                b.AddEntitySetProfile<LvRenamedNodeProfile>();
            },
            configureServices: services =>
            {
                services.AddSingleton(counter);
                if (sink is not null) services.AddSingleton(sink);
                services.AddDbContext<LevelsOptionsDbContext>(o =>
                {
                    o.UseSqlite(connection);
                    if (sink is not null)
                    {
                        o.LogTo(
                            message => sink.Add(message),
                            (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted);
                    }
                });
            });

        using IServiceScope scope = fx.App.Services.CreateScope();
        LevelsOptionsDbContext db = scope.ServiceProvider.GetRequiredService<LevelsOptionsDbContext>();
        db.Database.EnsureCreated();

        db.LvNodes.AddRange(
            new LvNode { Id = 1, Name = "Root", Active = true, ParentId = null },
            new LvNode { Id = 2, Name = "A", Active = true, ParentId = 1 },
            new LvNode { Id = 3, Name = "B", Active = false, ParentId = 1 },
            new LvNode { Id = 4, Name = "A1", Active = true, ParentId = 2 },
            new LvNode { Id = 5, Name = "A2", Active = false, ParentId = 2 },
            new LvNode { Id = 6, Name = "A3", Active = true, ParentId = 2 },
            new LvNode { Id = 7, Name = "B1", Active = true, ParentId = 3 },
            new LvNode { Id = 8, Name = "A1a", Active = true, ParentId = 4 });
        db.LvTags.Add(new LvTag { Id = 100, NodeId = 1, Label = "tag-root" });

        db.LvRenamedNodes.AddRange(
            new LvRenamedNode { Id = 1, Name = "R-Root", ParentId = null },
            new LvRenamedNode { Id = 2, Name = "R-A", ParentId = 1 },
            new LvRenamedNode { Id = 3, Name = "R-A1", ParentId = 2 });

        db.SaveChanges();
        return fx;
    }

    public static string LastSelectAgainst(SqlCaptureSink sink, string table) => sink.Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal) && s.Contains($"\"{table}\"", StringComparison.Ordinal))
        .Last();

    /// <summary>Names of the entities in a serialized navigation array, in payload order.</summary>
    public static string[] Names(JsonElement arr) =>
        arr.EnumerateArray().Select(e => e.GetProperty("Name").GetString()!).ToArray();

    /// <summary>The single root entity of a collection response filtered to <c>parentId eq null</c>.</summary>
    public static JsonElement Root(JsonDocument doc) => doc.RootElement.GetProperty("value")[0];
}

// ── $levels + nested options, pushed ─────────────────────────────────────────────────────────────

public sealed class LevelsWithOptionsPushdownSqliteTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private LevelsDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new LevelsDelegateCounter();
        _fx = await LevelsOptionsSqliteHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task Levels2_WithSelect_KeepsSelfNavAtEveryLevel_AndPrunesOtherProperties()
    {
        // THE $levels + $select TRAP: the nested clause holds no ExpandedNavigationSelectItem for the
        // self-navigation (the recursion is implicit in ODL), so without the plan-time self-nav
        // augmentation the strip would delete "Children" at every level.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$select=name)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = LevelsOptionsSqliteHarness.Root(doc);

        JsonElement level1 = root.GetProperty("Children");
        Assert.Equal(new[] { "A", "B" }, LevelsOptionsSqliteHarness.Names(level1));

        JsonElement a = level1[0];
        Assert.False(a.TryGetProperty("Active", out _), "$select=name must prune Active at level 1");
        Assert.False(a.TryGetProperty("Id", out _), "$select=name must prune Id at level 1");
        Assert.True(a.TryGetProperty("Children", out JsonElement level2),
            "the self-navigation must survive the nested $select strip at every level");
        Assert.Equal(new[] { "A1", "A2", "A3" }, LevelsOptionsSqliteHarness.Names(level2));

        JsonElement a1 = level2[0];
        Assert.False(a1.TryGetProperty("Active", out _), "$select=name must prune Active at level 2");
        // Level 3 is beyond $levels=2, so the deepest loaded level carries no self-navigation at all.
        Assert.False(a1.TryGetProperty("Children", out _));
        Assert.DoesNotContain("A1a", body);
    }

    [Fact]
    public async Task Levels2_WithFilter_FiltersAtBothLevels_AndPushesToSql()
    {
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$filter=active eq true)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // One self-JOIN'd query with the nested predicate pushed into it at each level.
        string sql = LevelsOptionsSqliteHarness.LastSelectAgainst(_sink, "LvNodes");
        Assert.True(Regex.Matches(sql, "\"LvNodes\"").Count >= 3,
            $"$levels=2 must self-JOIN the table at each level; got:\n{sql}");
        Assert.True(Regex.Matches(sql, "WHERE").Count >= 3,
            $"the nested $filter must be pushed into SQL at each level; got:\n{sql}");

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = LevelsOptionsSqliteHarness.Root(doc);

        // Level 1: B is inactive → filtered out (and so is its whole subtree).
        Assert.Equal(new[] { "A" }, LevelsOptionsSqliteHarness.Names(root.GetProperty("Children")));
        Assert.DoesNotContain("\"B1\"", body);
        // Level 2: A2 is inactive → filtered out too, proving the filter is not first-level-only.
        Assert.Equal(new[] { "A1", "A3" },
            LevelsOptionsSqliteHarness.Names(root.GetProperty("Children")[0].GetProperty("Children")));
    }

    [Fact]
    public async Task LevelsMax_WithFilter_HonorsPerProfileMaxExpansionDepth()
    {
        // LvShallowNodes overrides MaxExpansionDepth = 2, so $levels=max loads exactly 2 levels.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvShallowNodes?$filter=parentId eq null&$expand=Children($levels=max;$filter=active eq true)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = LevelsOptionsSqliteHarness.Root(doc);

        Assert.Equal(new[] { "A" }, LevelsOptionsSqliteHarness.Names(root.GetProperty("Children")));
        Assert.Equal(new[] { "A1", "A3" },
            LevelsOptionsSqliteHarness.Names(root.GetProperty("Children")[0].GetProperty("Children")));
        Assert.DoesNotContain("A1a", body); // level 3 beyond the overridden ceiling of 2
    }

    [Fact]
    public async Task Levels2_WithCount_EmitsFullCountAtEveryLevel()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$count=true)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = LevelsOptionsSqliteHarness.Root(doc);

        Assert.Equal(2, root.GetProperty("Children@odata.count").GetInt32());       // Root → A, B
        JsonElement level1 = root.GetProperty("Children");
        Assert.Equal(3, level1[0].GetProperty("Children@odata.count").GetInt32());  // A → A1, A2, A3
        Assert.Equal(1, level1[1].GetProperty("Children@odata.count").GetInt32());  // B → B1
    }

    [Fact]
    public async Task Levels2_OrderByDesc_OrdersEveryLevelIndependently()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$orderby=name desc)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = LevelsOptionsSqliteHarness.Root(doc);

        // Level 1 by name desc: B, A.
        JsonElement level1 = root.GetProperty("Children");
        Assert.Equal(new[] { "B", "A" }, LevelsOptionsSqliteHarness.Names(level1));

        // Level 2 ordered independently under each parent — proving the $orderby is not first-level-only.
        Assert.Equal(new[] { "B1" }, LevelsOptionsSqliteHarness.Names(level1[0].GetProperty("Children")));
        Assert.Equal(new[] { "A3", "A2", "A1" },
            LevelsOptionsSqliteHarness.Names(level1[1].GetProperty("Children")));
    }

    // #296: a nested $top/$skip on a SELF-REFERENTIAL navigation is rejected by Microsoft's
    // SelectExpandQueryValidator before OhData ever sees it ("The limit of '0' for Top query has been
    // exceeded"): the model-bound MaxTop on the navigation's target type defaults to 0 as soon as that
    // type carries any model-bound settings, which it always does when it is its own entity set. That
    // is a PRE-EXISTING framework limitation — it reproduces identically on a plain
    // $expand=Children($top=1) with no $levels — so $levels + $top is unreachable today. OhData plumbs
    // the options through anyway (EngagedExpand.Skip/Top + ShapeLevelsInJson window per level), so the
    // combination lights up if that limitation is lifted. Pinned here so the diagnosis is not lost.
    [Fact]
    public async Task NestedTop_OnSelfReferentialNav_RejectedByModelBoundValidator_WithAndWithoutLevels()
    {
        HttpResponseMessage withLevels = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$top=1)");
        HttpResponseMessage withoutLevels = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($top=1)");

        Assert.Equal(HttpStatusCode.BadRequest, withLevels.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutLevels.StatusCode);
        Assert.Contains("for Top query has been exceeded", await withoutLevels.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Levels2_WithSelect_OnRenamedSelfNav_KeepsRenamedKeyAtEveryLevel()
    {
        // The self-nav augmentation resolves the EDM name via ODataPropertyNaming.ResolveEdmName, so a
        // [JsonPropertyName("kids")] self-navigation survives the strip under its renamed key.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvRenamedNodes?$filter=parentId eq null&$expand=kids($levels=2;$select=name)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = LevelsOptionsSqliteHarness.Root(doc);

        Assert.True(root.TryGetProperty("kids", out JsonElement level1));
        Assert.Equal("R-A", level1[0].GetProperty("Name").GetString());
        Assert.False(level1[0].TryGetProperty("Id", out _), "$select=name prunes Id at level 1");
        Assert.True(level1[0].TryGetProperty("kids", out JsonElement level2),
            "the renamed self-navigation must survive the nested $select strip");
        Assert.Equal("R-A1", level2[0].GetProperty("Name").GetString());
        Assert.False(body.Contains("\"Children\"", StringComparison.Ordinal), "no CLR-name leak");
    }

    [Fact]
    public async Task Levels2_WithNestedExpand_StillDeferred_NoJoin_But200()
    {
        _sink.Clear();
        // $levels + its own nested $expand remains deferred off pushdown: no self-JOIN, no Tags JOIN,
        // the delegate-less navigation just stays EDM-only for the request. Never a 500.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$expand=Tags)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = LevelsOptionsSqliteHarness.LastSelectAgainst(_sink, "LvNodes");
        Assert.Single(Regex.Matches(sql, "\"LvNodes\"")); // no self-JOIN → deferred
        Assert.DoesNotContain("\"LvTags\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Children\":[]", body);
    }

    [Fact]
    public async Task Levels_ExceedingMaxExpansionDepth_StillReturns400()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$expand=Children($levels=99;$select=name)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
    }
}

// ── Delegate safety under $levels + nested options (pins the routeBackedNavNames skip) ───────────

public sealed class LevelsWithOptionsDelegateSafetyTests
{
    // A DELEGATE-BACKED self-referential navigation must never be EF-included by the $levels path,
    // whatever nested options ride along — the delegate is where user filtering/authorization lives.
    // Asserted in BOTH registration orders because the SAME CLR type is also exposed by a
    // delegate-LESS set: a delegate-carrying set must never be bypassed just because a delegate-less
    // one happened to register first (the #273 class of bug).
    [Theory]
    [InlineData(true, "$levels=2;$select=name")]
    [InlineData(false, "$levels=2;$select=name")]
    [InlineData(true, "$levels=2;$filter=active eq true")]
    [InlineData(false, "$levels=2;$filter=active eq true")]
    [InlineData(true, "$levels=2;$count=true")]
    [InlineData(false, "$levels=2;$count=true")]
    public async Task DelegateBackedSelfNav_WithLevelsOptions_NeverSelfJoined_DelegateInvoked(
        bool delegatelessFirst, string levelsOptions)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        var counter = new LevelsDelegateCounter();
        await using TestFixture fx = await LevelsOptionsSqliteHarness.BuildAsync(
            connection, counter, sink, delegatelessFirst);
        sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync(
            $"/odata/LvSecureNodes?$filter=parentId eq null&$expand=Children({levelsOptions})");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // NOT ONE captured statement may self-join the table: the pushed $levels projection would
        // reference "LvNodes" more than once, the delegate's own per-parent query exactly once.
        foreach (string statement in sink.Snapshot())
        {
            Assert.True(Regex.Matches(statement, "\"LvNodes\"").Count <= 1,
                $"a delegate-backed self-referential nav must never be EF-included; got:\n{statement}");
        }

        Assert.True(counter.ChildCalls > 0, "the delegate-backed Children nav must expand through its delegate");

        // The delegate path loads exactly one level; the deeper self-references stay stripped.
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"A\"", body);
        Assert.DoesNotContain("\"A1\"", body);
    }

    // Control: the delegate-LESS set over the SAME CLR type still pushes — the deferral is scoped to
    // the set that actually declared the delegate, so it never over-defers its sibling.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DelegatelessSiblingSet_StillPushesLevelsWithOptions(bool delegatelessFirst)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        var counter = new LevelsDelegateCounter();
        await using TestFixture fx = await LevelsOptionsSqliteHarness.BuildAsync(
            connection, counter, sink, delegatelessFirst);
        sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$filter=active eq true)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = LevelsOptionsSqliteHarness.LastSelectAgainst(sink, "LvNodes");
        Assert.True(Regex.Matches(sql, "\"LvNodes\"").Count >= 3, "the delegate-less sibling still self-JOINs");
        Assert.Equal(0, counter.ChildCalls); // the other set's delegate was not involved
    }
}

// ── Item 3 × item 2: the MaxExpandTop count bound applies PER LEVEL of a $levels recursion ───────

public sealed class LevelsWithCountCeilingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private LevelsDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _counter = new LevelsDelegateCounter();
        // Root has 2 children (within the ceiling); A has 3 (above it) — so a breach can only be
        // detected if the bound is enforced at the DEEPER level, not just once at the top.
        _fx = await LevelsOptionsSqliteHarness.BuildAsync(
            _connection, _counter, sink: null, defaults: d => d.MaxExpandTop = 2);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task LevelsCount_BreachAtDeeperLevel_Returns400()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$count=true)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 2", body);
    }

    [Fact]
    public async Task LevelsCount_WithinCeilingAtEveryLevel_Succeeds()
    {
        // $levels=1 stops before A's 3-child level, so every counted collection is within the ceiling.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=1;$count=true)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Children@odata.count\":2", body);
    }

}
