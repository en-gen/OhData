using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #466: `$levels=N` served exactly ONE level on every read path except EF pushdown, silently, while
// the explicit nested spelling served all N.
//
// Both $levels implementations were pushdown-only, and BuildExpandLookup/TryKeepNav seeds a levels
// budget only for a name in `levelsNavNames` -- which was CollectPushedLevelsNavNames' answer, null
// on GetAll, GetById, Priority-1 and non-EF GetQueryable. So the self-navigation was dropped at
// level 2 with no indication that more had been asked for.
//
// Nothing had to be LOADED: on these paths the rows are already in the CLR graph the handler
// returned (here EF's own relationship fixup wires the whole tree onto the tracked entities), and
// SerializeBounded reads them by reflection. Seeding the budget is the whole fix.
//
// Fixture is #254's, unchanged. What is new is three profiles over the SAME model exposing the three
// non-pushdown read paths, through the harness's additive hook.

/// <summary>
/// #466: the GetAll path — an in-memory enumeration, no IQueryable, no pushdown of any kind. Also
/// carries GetById, so the single-entity read is exercised on the same data.
/// </summary>
public sealed class LvGetAllNodeProfile : EntitySetProfile<int, LvNode>
{
    public LvGetAllNodeProfile(LevelsOptionsDbContext db) : base(x => x.Id)
    {
        EntitySetName = "LvAllNodes";
        ExpandEnabled = true;
        SelectEnabled = true;
        // ToList() materializes every node; EF Core's relationship fixup then populates Children on
        // each tracked entity, which is what makes "the graph the handler returned" multi-level.
        GetAll = ct => OhDataResult.SuccessTask<IEnumerable<LvNode>>(db.LvNodes.Include(n => n.Tags).ToList());
        GetById = (id, ct) =>
        {
            List<LvNode> all = db.LvNodes.Include(n => n.Tags).ToList();
            return OhDataResult.SuccessTask(all.FirstOrDefault(n => n.Id == id));
        };
        HasMany(x => x.Children);
        HasMany(x => x.Tags);
    }
}

/// <summary>
/// #464/#466: a GetQueryable whose IQueryable is NOT EF-backed, so ResolveEfCoreAssembly returns null
/// and the whole pushdown planner is skipped. ($search takes this same path by swapping in an
/// in-memory queryable.)
/// </summary>
public sealed class LvMemoryNodeProfile : EntitySetProfile<int, LvNode>
{
    public LvMemoryNodeProfile(LevelsOptionsDbContext db) : base(x => x.Id)
    {
        EntitySetName = "LvMemNodes";
        ExpandEnabled = true;
        SelectEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.LvNodes.ToList().AsQueryable());
        HasMany(x => x.Children);
        HasMany(x => x.Tags);
    }
}

/// <summary>#466: the Priority-1 path — the profile owns query application.</summary>
public sealed class LvODataNodeProfile : ODataEntitySetProfile<int, LvNode>
{
    public LvODataNodeProfile(LevelsOptionsDbContext db) : base(x => x.Id)
    {
        EntitySetName = "LvODataNodes";
        ExpandEnabled = true;
        SelectEnabled = true;
        GetODataQueryable = (options, ct) =>
            Task.FromResult(new ODataQueryResult<LvNode> { Items = db.LvNodes.ToList().AsQueryable() });
        HasMany(x => x.Children);
        HasMany(x => x.Tags);
    }
}

public sealed class LevelsOffPushdownTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await LevelsOptionsSqliteHarness.BuildAsync(
            _connection, new LevelsDelegateCounter(), sink: null,
            configureExtraProfiles: b =>
            {
                b.AddEntitySetProfile<LvGetAllNodeProfile>();
                b.AddEntitySetProfile<LvMemoryNodeProfile>();
                b.AddEntitySetProfile<LvODataNodeProfile>();
            });
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    /// <summary>
    /// The hierarchy root ("Root") out of a collection response. These paths cannot take a $filter
    /// (GetAll rejects $filter outright), so the root is found by name rather than by narrowing the
    /// query — which also keeps the three paths' helper identical.
    /// </summary>
    private static JsonElement RootNode(JsonDocument doc) =>
        doc.RootElement.GetProperty("value").EnumerateArray()
            .First(e => e.GetProperty("Name").GetString() == "Root");

    private async Task<JsonDocument> GetAsync(string url)
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    // ── The bug itself, on all three non-pushdown collection paths + GetById ─────────────────────

    // FAILS WITHOUT THE FIX: Children is served, but every child's own Children is stripped.
    [Theory]
    [InlineData("LvAllNodes")]   // GetAll
    [InlineData("LvMemNodes")]   // non-EF GetQueryable
    [InlineData("LvODataNodes")] // Priority-1
    public async Task LevelsTwo_ServesTwoLevels_OnEveryNonPushdownCollectionPath(string set)
    {
        using JsonDocument doc = await GetAsync($"/odata/{set}?$expand=Children($levels=2)");
        JsonElement level1 = RootNode(doc).GetProperty("Children");

        Assert.Equal(new[] { "A", "B" }, LevelsOptionsSqliteHarness.Names(level1));
        Assert.Equal(
            new[] { "A1", "A2", "A3" },
            LevelsOptionsSqliteHarness.Names(level1[0].GetProperty("Children")));
        Assert.Equal(
            new[] { "B1" },
            LevelsOptionsSqliteHarness.Names(level1[1].GetProperty("Children")));
    }

    // FAILS WITHOUT THE FIX: the same divergence on the single-entity read.
    [Fact]
    public async Task LevelsTwo_ServesTwoLevels_OnGetById()
    {
        using JsonDocument doc = JsonDocument.Parse(
            await _fx.Client.GetStringAsync("/odata/LvAllNodes(1)?$expand=Children($levels=2)"));

        JsonElement level1 = doc.RootElement.GetProperty("Children");
        Assert.Equal(new[] { "A", "B" }, LevelsOptionsSqliteHarness.Names(level1));
        Assert.Equal(
            new[] { "A1", "A2", "A3" },
            LevelsOptionsSqliteHarness.Names(level1[0].GetProperty("Children")));
    }

    // THE DIVERGENCE, stated as one assertion. Two spellings of one request, byte-identical answers
    // required. The explicit half was already green before the fix — that is what made the $levels
    // half a defect rather than a limitation.
    [Theory]
    [InlineData("LvAllNodes")]
    [InlineData("LvMemNodes")]
    [InlineData("LvODataNodes")]
    public async Task LevelsTwo_AndExplicitNesting_AgreeExactly(string set)
    {
        string viaLevels = await _fx.Client.GetStringAsync($"/odata/{set}?$expand=Children($levels=2)");
        string viaExplicit = await _fx.Client.GetStringAsync($"/odata/{set}?$expand=Children($expand=Children)");
        Assert.Equal(viaExplicit, viaLevels);
    }

    // ── The budget is a BUDGET, not "keep everything" ────────────────────────────────────────────

    // A1a lives at level 3. $levels=2 must not reach it — otherwise the fix would have replaced a
    // truncation with an unbounded walk of the graph, which is the failure mode #325/#326 exist for.
    //
    // Asserted through the ROOT NODE'S OWN SUBTREE, never as a substring of the whole body: these
    // paths take no $filter, so the response is every node in the table and A1a is a top-level
    // element of it — and a child of the top-level A1 — whatever the levels budget does.
    [Fact]
    public async Task LevelsTwo_DoesNotServeTheThirdLevel()
    {
        using JsonDocument doc = await GetAsync("/odata/LvAllNodes?$expand=Children($levels=2)");
        JsonElement a1 = RootNode(doc).GetProperty("Children")[0].GetProperty("Children")[0];
        Assert.Equal("A1", a1.GetProperty("Name").GetString());
        Assert.False(a1.TryGetProperty("Children", out _));
    }

    [Fact]
    public async Task LevelsThree_ServesTheThirdLevel()
    {
        using JsonDocument doc = await GetAsync("/odata/LvAllNodes?$expand=Children($levels=3)");
        JsonElement a1 = RootNode(doc).GetProperty("Children")[0].GetProperty("Children")[0];
        Assert.Equal(new[] { "A1a" }, LevelsOptionsSqliteHarness.Names(a1.GetProperty("Children")));
    }

    // $levels=max resolves against the profile's MaxExpansionDepth (3 by default) through the SAME
    // ResolveLevelsBudget the pushdown path uses (#428), so it must reach exactly as far as
    // $levels=3 — no further, and not one level.
    [Fact]
    public async Task LevelsMax_MatchesTheResolvedDepth()
    {
        string viaMax = await _fx.Client.GetStringAsync("/odata/LvAllNodes?$expand=Children($levels=max)");
        string viaThree = await _fx.Client.GetStringAsync("/odata/LvAllNodes?$expand=Children($levels=3)");
        Assert.Equal(viaThree, viaMax);
    }

    // ── Byte-identity: shapes that must not move ─────────────────────────────────────────────────
    //
    // Captured from the PRE-FIX build and passing on it unchanged.

    // $levels=1 is a spec-equivalent restatement of a bare $expand, and served one level before the
    // fix as well — so it is the control that proves the change is scoped to N > 1.
    [Fact]
    public async Task ByteIdentical_LevelsOne_MatchesBareExpand()
    {
        string viaLevels = await _fx.Client.GetStringAsync("/odata/LvAllNodes?$expand=Children($levels=1)");
        string bare = await _fx.Client.GetStringAsync("/odata/LvAllNodes?$expand=Children");
        Assert.Equal(bare, viaLevels);

        // One level and no more: the root's children carry no Children of their own.
        using JsonDocument doc = JsonDocument.Parse(bare);
        Assert.False(RootNode(doc).GetProperty("Children")[0].TryGetProperty("Children", out _));
    }

    // No $levels anywhere in the request: the union at the pipeline's Stage 1 must not run at all
    // (ClauseHasLevels gates it), and nothing about the response may move.
    [Fact]
    public async Task ByteIdentical_NoLevels_IsUntouched()
    {
        using JsonDocument doc = await GetAsync("/odata/LvAllNodes?$expand=Tags");
        JsonElement root = RootNode(doc);
        Assert.Equal(1, root.GetProperty("Tags").GetArrayLength());
        Assert.False(root.TryGetProperty("Children", out _)); // un-expanded nav stays omitted
    }

    // ── The interaction with #463/#464: serving N levels must not outrun the ceiling ─────────────
    //
    // Serving the deeper levels is only half an answer. A $levels recursion carries no clause item
    // for levels 2..N — the recursion is implicit, the SAME navigation repeating — so those levels
    // are invisible to a walk over clause.SelectedItems, which is exactly the hole #463 found on the
    // depth axis. EnforceRawExpandCeiling therefore walks the self-navigation for the resolved budget
    // and checks each level, mirroring what ShapeLevelsInJson does for the pushed recursion.
    //
    // The fixture makes the two levels differ deliberately: Root has 2 children, A has 3. At a
    // ceiling of 2, level 1 is inside it and level 2 is not — so a level-1-only check passes the
    // request through and a per-level check rejects it.
    //
    // FAILS WITHOUT THE PER-LEVEL WALK: 200, with A's three children served under a ceiling of 2.
    [Fact]
    public async Task LevelsRecursion_IsBoundedAtEveryLevel_NotJustTheFirst()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await LevelsOptionsSqliteHarness.BuildAsync(
            connection, new LevelsDelegateCounter(), sink: null,
            defaults: d => d.MaxExpandTop = 2,
            configureExtraProfiles: b => b.AddEntitySetProfile<LvGetAllNodeProfile>());

        // Level 1 alone is inside the ceiling, so this is the control: one level is served.
        HttpResponseMessage oneLevel = await fx.Client.GetAsync("/odata/LvAllNodes(1)?$expand=Children");
        Assert.Equal(HttpStatusCode.OK, oneLevel.StatusCode);

        // Two levels reaches A's three children, which is over it.
        HttpResponseMessage twoLevels =
            await fx.Client.GetAsync("/odata/LvAllNodes(1)?$expand=Children($levels=2)");
        Assert.Equal(HttpStatusCode.BadRequest, twoLevels.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await twoLevels.Content.ReadAsStringAsync());
        JsonElement error = doc.RootElement.GetProperty("error");
        Assert.Equal("InvalidQueryOption", error.GetProperty("code").GetString());
        Assert.Contains("maximum of 2 entities", error.GetProperty("message").GetString()!);
    }

    // The EF pushdown path is untouched by all of this — it had its own working $levels recursion,
    // and CollectPushedLevelsNavNames still supplies the same set it always did. The union only ever
    // ADDS names, and adding one already present is a no-op.
    [Fact]
    public async Task ByteIdentical_PushdownPath_StillServesLevelsItself()
    {
        using JsonDocument doc = JsonDocument.Parse(await _fx.Client.GetStringAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2)"));
        JsonElement level1 = LevelsOptionsSqliteHarness.Root(doc).GetProperty("Children");
        Assert.Equal(new[] { "A", "B" }, LevelsOptionsSqliteHarness.Names(level1));
        Assert.Equal(
            new[] { "A1", "A2", "A3" },
            LevelsOptionsSqliteHarness.Names(level1[0].GetProperty("Children")));
    }
}
