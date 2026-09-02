using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #304: a nested $top/$skip on a pushed level that ALSO carries a nested $expand composed a SQL
// Skip/Take at the same level BuildShapedNavAccess then wrapped in a further element-wise Select --
// the APPLY/LATERAL shape #298 fixed for $count and #300 for $levels. It failed loud with a 400.
//
// Fix: no SQL Skip/Take at a level with children (gated on isProjectionLeaf, mirroring #298's
// countBound gate); the window moves to the JSON pass, bounded by the same MaxExpandTop ceiling.
//
// Two fold-ins: a degenerate $skip=0 must be a no-op WINDOW and never trim (#313 revisits the other
// half -- a bare children level is still fully materialized and now gets a pure ceiling check), and
// #316 enforces the same ceiling on the $levels JSON-windowing path.
public sealed class NestedTopSkipWithChildrenExpandPushdownTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private MultiLevelDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new MultiLevelDelegateCounter();
        _fx = await MultiLevelSqliteHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task Top_WithNestedExpand_ReturnsWindowedBookAndItsChapters_JoinsInOneQuery()
    {
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($top=1;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The parent collection must come back JOIN'd in one query — not a fallback re-fetch that drops
        // the folded navigation (the #298-family silent-degrade symptom this shape used to trip).
        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Authors");
        Assert.Contains("\"Books\"", sql);
        Assert.Contains("\"Chapters\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value").EnumerateArray().Single();
        JsonElement books = author.GetProperty("Books");

        // Author 1's books are B1 (Id 10) and B2 (Id 11); with no explicit nested $orderby, the
        // deterministic tiebreaker (the nav element's own key, ascending) selects the lowest Id — B1 —
        // deterministically under $top=1.
        Assert.Equal(1, books.GetArrayLength());
        JsonElement b1 = books.EnumerateArray().Single();
        Assert.Equal("B1", b1.GetProperty("Title").GetString());

        // The windowed parent's own children (Chapters) are present and correct.
        JsonElement chapters = b1.GetProperty("Chapters");
        Assert.Equal(2, chapters.GetArrayLength());
        Assert.Contains(chapters.EnumerateArray(), c => c.GetProperty("Heading").GetString() == "Zeta");
        Assert.Contains(chapters.EnumerateArray(), c => c.GetProperty("Heading").GetString() == "Alpha");
    }

    [Fact]
    public async Task Skip_WithNestedExpand_ReturnsRemainingBookAndItsChapters()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($skip=1;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value").EnumerateArray().Single();
        JsonElement books = author.GetProperty("Books");

        // $skip=1 over the deterministically Id-ordered pair (B1 Id 10, B2 Id 11) leaves exactly B2.
        Assert.Equal(1, books.GetArrayLength());
        JsonElement b2 = books.EnumerateArray().Single();
        Assert.Equal("B2", b2.GetProperty("Title").GetString());

        // B2 has no chapters in the shared fixture — an empty (not omitted) array proves the nested
        // $expand still rode along on the skipped-to survivor.
        Assert.Equal(0, b2.GetProperty("Chapters").GetArrayLength());
    }

    [Fact]
    public async Task MiddleLevel_TopWithNestedExpand_ThreeDeep_WindowsCorrectlyAtDepthTwo()
    {
        _sink.Clear();
        // Book level: $filter narrows to B1 only (no $top there, still has children — unaffected by
        // #304). Chapters level (the MIDDLE level of a 3-deep chain): $orderby=ordinal + $top=1 + a
        // nested $expand=Pages — the shape #304 targets, one level down from the root expand.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id" +
            "&$expand=Books($filter=year eq 2001;$expand=Chapters($orderby=ordinal;$top=1;$expand=Pages))");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Authors");
        Assert.Contains("\"Chapters\"", sql);
        Assert.Contains("\"Pages\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value").EnumerateArray().Single();
        JsonElement books = author.GetProperty("Books");
        Assert.Equal(1, books.GetArrayLength()); // level-1 $filter: only B1 (Year 2001)

        JsonElement b1 = books.EnumerateArray().Single();
        Assert.Equal("B1", b1.GetProperty("Title").GetString());

        JsonElement chapters = b1.GetProperty("Chapters");
        // $orderby=ordinal asc puts Alpha (ordinal 1) before Zeta (ordinal 2); $top=1 keeps Alpha.
        Assert.Equal(1, chapters.GetArrayLength());
        JsonElement alpha = chapters.EnumerateArray().Single();
        Assert.Equal("Alpha", alpha.GetProperty("Heading").GetString());

        // Alpha's own children (Pages) survived the windowing at the level above them.
        JsonElement pages = alpha.GetProperty("Pages");
        Assert.Equal(2, pages.GetArrayLength());
        Assert.Contains(pages.EnumerateArray(), p => p.GetProperty("Number").GetInt32() == 1);
        Assert.Contains(pages.EnumerateArray(), p => p.GetProperty("Number").GetInt32() == 2);
    }

    [Fact]
    public async Task OverMaxExpandTop_WithChildren_NoCount_Returns400_ActionableCeilingMessage()
    {
        // Ceiling 1, Author 1 has 2 Books, no $count on this shape — the JSON-deferred window still has
        // to materialize the full collection before it can page it (there is no SQL LIMIT to bound the
        // fetch — that's the whole reason this shape is deferred at all), so the same MaxExpandTop
        // ceiling WriteNestedCountAndWindow already enforces for the $count case is enforced here too.
        using var freshConnection = new SqliteConnection("Data Source=:memory:");
        freshConnection.Open();
        var freshCounter = new MultiLevelDelegateCounter();
        await using TestFixture fxCapped = await MultiLevelSqliteHarness.BuildAsync(
            freshConnection, freshCounter, sink: null, defaults: d => d.MaxExpandTop = 1);

        HttpResponseMessage resp = await fxCapped.Client.GetAsync(
            "/odata/Authors?$expand=Books($top=1;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Books", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 1", body);
        Assert.Contains("Narrow it with a nested $filter", body);
        // The message must stay generic — never the raw EF/provider exception text (which could leak
        // schema/SQL details).
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("SQLITE", body);
    }

    [Fact]
    public async Task Skip0_WithNestedExpand_StillCeilingChecked_Returns400()
    {
        // #313 reverses this shape's premise: a "bare children" level (no $count, no $skip/$top that
        // actually engages the deferred window — $skip=0 is a no-op exactly like ApplyNavShape's own
        // `sk > 0` guard) used to be VISITED by ShapePushedExpandsInJson but matched no windowing arm, so
        // it fell through completely unbounded. #313 adds a pure ceiling check for exactly this shape
        // (hasChildren && maxExpandTop is int, no windowing — there's nothing to window since $skip=0 is
        // a no-op), so Books($skip=0;$expand=Chapters) is now bounded the same as every other pushed
        // $expand shape. Ceiling 1, Author 1 has 2 Books → over the ceiling → 400, even though $skip=0
        // itself windows nothing away (see the MaxExpandTop=2 companion below for that invariant).
        using var freshConnection = new SqliteConnection("Data Source=:memory:");
        freshConnection.Open();
        var freshCounter = new MultiLevelDelegateCounter();
        await using TestFixture fxCapped = await MultiLevelSqliteHarness.BuildAsync(
            freshConnection, freshCounter, sink: null, defaults: d => d.MaxExpandTop = 1);

        HttpResponseMessage resp = await fxCapped.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($skip=0;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Books", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 1", body);
        Assert.Contains("Narrow it with a nested $filter", body);
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("SQLITE", body);
    }

    [Fact]
    public async Task Skip0_WithNestedExpand_AtCeilingTwo_StillDoesNotWindow_Returns200()
    {
        // Companion to the 400 above, preserving the pre-#313 "$skip=0 is a no-op window" invariant:
        // raise the ceiling to 2 (Author 1's true Book count) so the bare-children ceiling check no
        // longer trips, and confirm $skip=0 still windows NOTHING away — both books (and B1's chapters)
        // survive, exactly as before #313 changed only the ceiling enforcement, never the windowing.
        using var freshConnection = new SqliteConnection("Data Source=:memory:");
        freshConnection.Open();
        var freshCounter = new MultiLevelDelegateCounter();
        await using TestFixture fxCapped = await MultiLevelSqliteHarness.BuildAsync(
            freshConnection, freshCounter, sink: null, defaults: d => d.MaxExpandTop = 2);

        HttpResponseMessage resp = await fxCapped.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($skip=0;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value").EnumerateArray().Single();
        JsonElement books = author.GetProperty("Books");

        // Both of Author 1's books survive — $skip=0 windowed nothing away.
        Assert.Equal(2, books.GetArrayLength());
        JsonElement b1 = books.EnumerateArray().Single(b => b.GetProperty("Title").GetString() == "B1");
        JsonElement b2 = books.EnumerateArray().Single(b => b.GetProperty("Title").GetString() == "B2");

        // B1's own children (Chapters) are present — the whole point of #304, still true for the
        // $skip=0 no-op path.
        JsonElement chapters = b1.GetProperty("Chapters");
        Assert.Equal(2, chapters.GetArrayLength());
        Assert.Contains(chapters.EnumerateArray(), c => c.GetProperty("Heading").GetString() == "Zeta");
        Assert.Contains(chapters.EnumerateArray(), c => c.GetProperty("Heading").GetString() == "Alpha");
        // B2 has no chapters in the shared fixture — an empty (not omitted) array.
        Assert.Equal(0, b2.GetProperty("Chapters").GetArrayLength());
    }

    [Fact]
    public async Task Top0_WithNestedExpand_WindowsToEmpty_NeverAllRows()
    {
        // Correctness guard for the SAME fold-in as Skip0 above, but the opposite direction: unlike
        // $skip=0 (a genuine no-op, guarded out), $top=0 is NOT guarded on > 0 — it must still window
        // Books down to an EMPTY array. Guarding $top the same way as $skip would be a correctness bug:
        // ApplyNestedWindow's `end` computation treats a MISSING $top as "no limit" (end = arr.Count),
        // so skipping the window entirely for $top=0 would silently return ALL of Author 1's books
        // instead of none.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($top=0;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value").EnumerateArray().Single();
        JsonElement books = author.GetProperty("Books");

        // Empty (not omitted, and definitely not all 2 of Author 1's books).
        Assert.Equal(0, books.GetArrayLength());
    }

    [Fact]
    public async Task FailLoud_DoesNotRegressTheFixedShapes()
    {
        // Migrated from the now-retired ExpandPushdownFailLoudTests.cs: the #298 ($count + children)
        // shape was already fixed before #304 (composes no SQL bound at a level with children) — it
        // must still succeed now that the sibling $top/$skip-without-$count shape also works.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($count=true;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}

// #316 fold-in: the SAME MaxExpandTop ceiling #304 enforces on the pushed-expand-with-children JSON
// window (WriteNestedWindowOnly, via ShapePushedExpandsInJson, tested above) must ALSO be enforced on
// the $levels JSON window (ShapeLevelsInJson) — before this fix, ShapeLevelsInJson called a bare
// ApplyNestedWindow with no ceiling check at all, so a $levels expand with $skip/$top and no $count
// could materialize an unbounded collection at every level of the recursion. Reuses the $levels fixture
// (LvNode / LevelsOptionsSqliteHarness) from LevelsWithOptionsPushdownSqliteTests.cs rather than
// inventing a new DbContext.
//
// Uses $skip, not $top: a nested $top on a SELF-REFERENTIAL navigation is rejected by Microsoft's own
// SelectExpandQueryValidator before OhData's own ceiling check ever runs (the model-bound MaxTop on a
// type used as both a root entity set AND its own nav target defaults to 0 — see
// LevelsWithOptionsPushdownSqliteTests.NestedTop_OnSelfReferentialNav_RejectedByModelBoundValidator_WithAndWithoutLevels
// for the pinned diagnosis of that PRE-EXISTING, unrelated limitation). $skip exercises the identical
// WriteNestedWindowOnly guard ((sk > 0) || Top is int) without tripping that unrelated 400.
public sealed class NestedTopSkipWithChildrenLevelsCeilingTests
{
    [Fact]
    public async Task LevelsWithSkip_OverCeiling_NoCount_Returns400_ActionableCeilingMessage()
    {
        // MaxExpandTop=1: Root already has 2 Children (A, B) — over the ceiling at the very FIRST level
        // of the recursion, so the breach is detected before ShapeLevelsInJson even recurses deeper.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new LevelsDelegateCounter();
        await using TestFixture fx = await LevelsOptionsSqliteHarness.BuildAsync(
            connection, counter, sink: null, defaults: d => d.MaxExpandTop = 1);

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$skip=1)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Children", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 1", body);
        Assert.Contains("Narrow it with a nested $filter", body);
        // The message must stay generic — never the raw EF/provider exception text.
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("SQLITE", body);
    }
}
