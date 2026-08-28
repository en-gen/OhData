using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #318 — $levels SERVES a self-referential descent that the EXPLICIT nested spelling of the same
// descent comes back EMPTY for, when the node type is exposed by several entity sets that disagree
// about the self-navigation.
//
// THIS IS A CHARACTERIZATION PIN, NOT A FIX. Both halves of the asymmetry are owner-settled on the
// FROZEN Model B spec (issue #293) and neither is a defect:
//
//   - micro-decision (A): "explicit nested self-expand through a multi-set-exposed self-nav — ship
//     the fail-closed BLANK now (literal candidate-disagreement rule), document the $levels-vs-
//     explicit-nested difference; provenance-threading to serve raw = separate follow-up" (= #318).
//   - micro-decision (B): "delegate-less pushable parent empties whole branch vs delegate-backed
//     parent blanks only child: both leak-safe; DEFER PARITY (out of scope)" — that is the extra,
//     PARENT level this shape loses on top of the blanked grandchild.
//   - the same spec lists the entire $levels suite (LvNodes / LvShallowNodes / LvSecureNodes
//     resolving from the URL-named set alone) under "tests that STAY GREEN (confirm, don't gut)",
//     so making $levels blank to match is explicitly NOT the resolution.
//
// What was NOT true, and is why this file exists, is the recorded description of the symptom. Both
// the issue body and the in-source comment said the GRANDCHILD blanks. Measured, the PARENT level is
// lost as well: a non-ServeRaw child makes TryBuildEngagedExpand defer the whole branch off
// pushdown, so the parent navigation is never loaded either and ExpandLevelAsync's ServeRaw branch
// is a no-op over it. That understates the data loss by a whole level, which is exactly the kind of
// error that gets a real inconsistency under-prioritised. Pinning it here means the next person to
// take #318 seriously starts from the measurement rather than the description, and any future
// provenance-threading work has to change these assertions deliberately.
//
// Fixture: LvNode is exposed by LvNodes (Children delegate-LESS), LvShallowNodes (delegate-LESS) and
// LvSecureNodes (Children delegate-BACKED). At the root the URL-named set alone is authoritative, so
// Children is ServeRaw; one level down the candidate set is all three sets, which disagree -> Blank.

public sealed class Issue318LevelsVsExplicitNestedSelfExpandTests : IAsyncLifetime
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

    private async Task<JsonElement> GetRootAsync(string query, JsonDocument[] keepAlive)
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=" + query);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        keepAlive[0] = doc;
        return LevelsOptionsSqliteHarness.Root(doc);
    }

    [Fact]
    public async Task PlainSingleLevelSelfExpand_Serves()
    {
        // The baseline the other two are read against: with no nesting at all, the root's own
        // delegate-less declaration wins and the level serves raw.
        var keep = new JsonDocument[1];
        JsonElement root = await GetRootAsync("Children", keep);
        Assert.Equal(new[] { "A", "B" }, LevelsOptionsSqliteHarness.Names(root.GetProperty("Children")));
        keep[0].Dispose();
    }

    [Fact]
    public async Task LevelsTwo_ServesBothLevelsRaw_FromTheUrlNamedSetAlone()
    {
        // $levels recurses BuildLevelsNavBinding's already-resolved binding and never re-resolves a
        // candidate set, so the URL-named set stays authoritative all the way down. This is the
        // frozen decision, not an oversight — do not "unify" by making it blank.
        var keep = new JsonDocument[1];
        JsonElement root = await GetRootAsync("Children($levels=2)", keep);

        JsonElement level1 = root.GetProperty("Children");
        Assert.Equal(new[] { "A", "B" }, LevelsOptionsSqliteHarness.Names(level1));
        Assert.Equal(
            new[] { "A1", "A2", "A3" },
            LevelsOptionsSqliteHarness.Names(level1[0].GetProperty("Children")));
        Assert.Equal(
            new[] { "B1" },
            LevelsOptionsSqliteHarness.Names(level1[1].GetProperty("Children")));

        // Served entirely off the pushed self-JOIN; the delegate-backed sibling set is not consulted.
        Assert.Equal(0, _counter.ChildCalls);
        keep[0].Dispose();
    }

    [Fact]
    public async Task ExplicitNestedSelfExpand_LosesTheParentLevelToo_NotJustTheGrandchild()
    {
        // THE CORRECTED SYMPTOM. The grandchild's Blank treatment defers the WHOLE branch off
        // pushdown, so the PARENT navigation is never loaded either — `Children` comes back empty
        // rather than "[A, B] each with Children: []". Fail-closed and leak-safe, but a whole level
        // more data than the issue body and the old source comment recorded.
        var keep = new JsonDocument[1];
        JsonElement root = await GetRootAsync("Children($expand=Children)", keep);

        JsonElement children = root.GetProperty("Children");
        Assert.Equal(0, children.GetArrayLength());
        Assert.Equal(0, _counter.ChildCalls);
        keep[0].Dispose();
    }

    [Fact]
    public async Task TheTwoSpellingsOfTheSameDescent_StillDisagree()
    {
        // The inconsistency #318 is actually about, asserted directly so it cannot be closed by
        // accident: $levels=2 and the explicit two-level nesting request the same logical descent
        // and return different data. Delete this assertion only when unifying them is a deliberate,
        // owner-approved change.
        var levelsKeep = new JsonDocument[1];
        var explicitKeep = new JsonDocument[1];

        JsonElement viaLevels = await GetRootAsync("Children($levels=2)", levelsKeep);
        JsonElement viaExplicit = await GetRootAsync("Children($expand=Children)", explicitKeep);

        int levelsCount = viaLevels.GetProperty("Children").GetArrayLength();
        int explicitCount = viaExplicit.GetProperty("Children").GetArrayLength();

        Assert.Equal(2, levelsCount);
        Assert.Equal(0, explicitCount);
        Assert.NotEqual(levelsCount, explicitCount);

        levelsKeep[0].Dispose();
        explicitKeep[0].Dispose();
    }

    [Fact]
    public async Task ExplicitNestedSelfExpand_IsNotAnUnconditionalPenaltyOnNesting()
    {
        // CONTROL — isolates the cause to candidate DISAGREEMENT, not to nesting itself. `Tags` is
        // declared delegate-less by LvNodes/LvShallowNodes and not declared at all by
        // LvSecureNodes, so DB(Tags) = empty -> ServeRaw, and the identically-shaped nested request
        // keeps its parent level. Without this, the test above would be equally consistent with
        // "any nested $expand empties its parent", which is false.
        var keep = new JsonDocument[1];
        JsonElement root = await GetRootAsync("Children($expand=Tags)", keep);

        Assert.Equal(
            new[] { "A", "B" },
            LevelsOptionsSqliteHarness.Names(root.GetProperty("Children")));
        keep[0].Dispose();
    }

    [Fact]
    public async Task ExplicitNestedSelfExpand_IsNotDependentOnProfileRegistrationOrder()
    {
        // Model B is a set operation over the candidate list, so the blank must not depend on which
        // of the disagreeing profiles registered first. Rebuild with the order reversed.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new LevelsDelegateCounter();
        await using TestFixture fx = await LevelsOptionsSqliteHarness.BuildAsync(
            connection, counter, sink: null, delegatelessFirst: false);

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($expand=Children)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(
            0,
            LevelsOptionsSqliteHarness.Root(doc).GetProperty("Children").GetArrayLength());
        Assert.Equal(0, counter.ChildCalls);
    }
}
