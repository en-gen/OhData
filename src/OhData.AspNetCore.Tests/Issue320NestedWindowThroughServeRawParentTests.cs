using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #320 — a nested $top/$skip was SILENTLY IGNORED on a delegate-backed navigation reached only
// through a delegate-LESS (ServeRaw) parent's already-materialized graph.
//
// #294 rejects a nested $top/$skip with 400 when the navigation ITSELF resolves to RunDelegate at
// the level ExpandLevelAsync is walking. But ExpandLevelAsync's ServeRaw branch `continue`s without
// recursing — correctly, since the raw materialized value IS the answer — so a delegate-backed
// GRANDCHILD under such a parent was never resolved at all: its $top was accepted, never applied,
// and answered 200 with every related row and the delegate never invoked. That is the silent-ignore
// class #294 exists to eliminate, one level deeper.
//
// The fix resolves the rejection from the Model B TREATMENT rather than from which navigation the
// walker happened to reach, so all three spellings now answer identically. It cannot turn a honored
// request into a 400: a nested $top/$skip is honored only when the whole branch was SQL-pushdown-
// windowed, and TryBuildEngagedExpand pushes a branch only when EVERY level of it is ServeRaw — so
// wherever a non-ServeRaw navigation appears, that branch was certainly not pushed. The
// ServeRaw-child controls below pin that direction explicitly.

#region fixtures

public sealed class NwC
{
    public int Id { get; set; }
    public int BId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class NwB
{
    public int Id { get; set; }
    public int AId { get; set; }
    public string Name { get; set; } = "";
    public List<NwC> Cs { get; set; } = new();
}

public sealed class NwA
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<NwB> Bs { get; set; } = new();
}

public sealed class NwDelegateCounter
{
    private int _cCalls;
    public int CCalls => _cCalls;
    public void CountCCall() => Interlocked.Increment(ref _cCalls);
}

// Root set. `Bs` is delegate-LESS -> ServeRaw. GetAll (the in-memory read path, so nothing is ever
// SQL-pushdown-windowed) hands back a fully materialized A -> Bs -> Cs graph, which is exactly the
// precondition for the bug: the Cs rows are already sitting in the payload before any expand
// resolution runs.
public sealed class NwAProfile : EntitySetProfile<int, NwA>
{
    public NwAProfile() : base(x => x.Id)
    {
        EntitySetName = "NwAs";
        ExpandEnabled = true;
        SelectEnabled = true;
        GetAll = _ => Task.FromResult<IEnumerable<NwA>>(NwData.Graph());
        HasMany(x => x.Bs); // delegate-less -> ServeRaw
    }
}

// NwB is exposed by its own entity set, which declares `Cs` DELEGATE-BACKED. That declaration is
// what makes the grandchild's treatment RunDelegate at the level below `Bs`.
public sealed class NwBProfile : EntitySetProfile<int, NwB>
{
    public NwBProfile(NwDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "NwBs";
        ExpandEnabled = true;
        SelectEnabled = true;
        GetAll = _ => Task.FromResult<IEnumerable<NwB>>(NwData.Bs());
        HasMany(x => x.Cs, getAll: (bId, ct) =>
        {
            counter.CountCCall();
            return Task.FromResult<IEnumerable<NwC>>(NwData.CsFor((int)bId));
        });
    }
}

internal static class NwData
{
    public static List<NwC> CsFor(int bId) => new()
    {
        new NwC { Id = bId * 10 + 1, BId = bId, Name = $"c{bId}-1" },
        new NwC { Id = bId * 10 + 2, BId = bId, Name = $"c{bId}-2" },
        new NwC { Id = bId * 10 + 3, BId = bId, Name = $"c{bId}-3" },
    };

    public static List<NwB> Bs() => new()
    {
        new NwB { Id = 1, AId = 1, Name = "b1", Cs = CsFor(1) },
    };

    public static List<NwA> Graph() => new()
    {
        new NwA { Id = 1, Name = "a1", Bs = Bs() },
    };
}

#endregion

public sealed class Issue320NestedWindowThroughServeRawParentTests
{
    private const string DelegateBackedMessage =
        "A nested $top/$skip is not supported on the delegate-backed navigation 'Cs'";

    private static Task<TestFixture> BuildAsync(NwDelegateCounter counter) =>
        TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<NwAProfile>();
                b.AddEntitySetProfile<NwBProfile>();
            },
            configureServices: s => s.AddSingleton(counter));

    [Theory]
    [InlineData("$top=1")]
    [InlineData("$skip=1")]
    [InlineData("$top=1;$skip=1")]
    public async Task NestedWindow_OnDelegateBackedGrandchild_UnderServeRawParent_Is400(string window)
    {
        // THE #320 SHAPE. Before the fix every one of these returned 200 with all three Cs rows
        // straight out of the parent's materialized graph, the option dropped without a trace and
        // the Cs delegate never invoked.
        var counter = new NwDelegateCounter();
        await using TestFixture fx = await BuildAsync(counter);

        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/NwAs?$expand=Bs($expand=Cs({window}))");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains(DelegateBackedMessage, body);
        Assert.Equal(0, counter.CCalls); // rejected before any handler runs
    }

    [Fact]
    public async Task NestedWindow_Rejection_IsByteIdenticalToTheDirectlyReachedOne()
    {
        // The two throw sites share one message builder so they cannot drift. Reaching the SAME
        // delegate-backed navigation directly (via its own entity set, where ExpandLevelAsync walks
        // it as RunDelegate) must produce the identical error body.
        var counter = new NwDelegateCounter();
        await using TestFixture fx = await BuildAsync(counter);

        HttpResponseMessage viaParent = await fx.Client.GetAsync("/odata/NwAs?$expand=Bs($expand=Cs($top=1))");
        HttpResponseMessage direct = await fx.Client.GetAsync("/odata/NwBs?$expand=Cs($top=1)");

        Assert.Equal(HttpStatusCode.BadRequest, viaParent.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, direct.StatusCode);
        Assert.Equal(await direct.Content.ReadAsStringAsync(), await viaParent.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithoutTheNestedWindow_TheServeRawParentStillServesItsMaterializedGraph()
    {
        // THE NO-OP CONTROL. The scan must be invisible to every request that carries no nested
        // window: the delegate-less parent is still authoritative for what it materialized
        // (declaring-set authority, #293), so all three Cs rows still come back and the Cs delegate
        // is still (correctly) never invoked.
        var counter = new NwDelegateCounter();
        await using TestFixture fx = await BuildAsync(counter);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/NwAs?$expand=Bs($expand=Cs)");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement cs = doc.RootElement.GetProperty("value")[0]
            .GetProperty("Bs")[0].GetProperty("Cs");
        Assert.Equal(3, cs.GetArrayLength());
        Assert.Equal(0, counter.CCalls);
    }

    [Fact]
    public async Task NestedWindow_OnTheServeRawParentItself_IsUnchanged()
    {
        // The parent nav `Bs` is ServeRaw, so the rejection must NOT fire for it. (It is still
        // silently ignored on this in-memory path — a separate, deliberately unfixed gap recorded
        // on ClauseHasNestedTopOrSkip; this test pins that the fix did not change it.)
        var counter = new NwDelegateCounter();
        await using TestFixture fx = await BuildAsync(counter);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/NwAs?$expand=Bs($top=0)");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("value")[0].GetProperty("Bs").GetArrayLength());
    }
}

// The Model-B multi-set fixture (LvNodes delegate-less / LvSecureNodes delegate-backed over one
// LvNode type) reaches the two shapes the in-memory fixture above cannot: a BLANK navigation, and a
// rejection raised three levels down by the scan's own recursion.
public sealed class Issue320NestedWindowBlankAndDepthTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private LevelsDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _counter = new LevelsDelegateCounter();
        _fx = await LevelsOptionsSqliteHarness.BuildAsync(_connection, _counter, null);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NestedWindow_OnABlankNavigation_UnderAServeRawParent_Is400()
    {
        // `Children` is ServeRaw at the root (the URL-named set alone is authoritative, #293) and
        // BLANK one level down (LvNodes declares it delegate-less, LvSecureNodes routes it — the
        // candidates disagree). A Blank navigation is emptied outright, so no window can be applied
        // to it either; before the fix this returned 200.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($expand=Children($top=1))");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("disagree about whether it is delegate-backed", body);
        Assert.Equal(0, _counter.ChildCalls);
    }

    [Fact]
    public async Task NestedWindow_ThreeLevelsDown_IsReachedByTheScansOwnRecursion()
    {
        // The $top sits at depth 3. The scan must walk PAST depth 2 (where `Children` is Blank but
        // carries no window, so nothing is rejected there) and only then reject.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($expand=Children($expand=Children($top=1)))");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("InvalidQueryOption", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NestedWindow_OnAServeRawGrandchild_StaysHonored_NotRejected()
    {
        // THE DIRECTION CONTROL. `Tags` is declared delegate-less by LvNodes/LvShallowNodes and not
        // declared at all by LvSecureNodes, so DB(Tags) = ∅ -> ServeRaw. The scan runs (the subtree
        // does carry a nested $top) and must decline to reject it: a ServeRaw branch is pushdown-
        // eligible, which is precisely the case where the window IS applied.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($expand=Tags($top=1))");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task PushedNestedWindow_OnADelegatelessNavigation_IsStillApplied()
    {
        // The whole point of NOT rejecting ServeRaw: this request is windowed in SQL and must keep
        // returning exactly one child. Pins that the fix took nothing away from the honored path.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($top=1)");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(1, LevelsOptionsSqliteHarness.Root(doc).GetProperty("Children").GetArrayLength());
    }
}
