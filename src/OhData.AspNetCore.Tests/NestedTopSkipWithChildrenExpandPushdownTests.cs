using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #304 regression coverage: a nested $top/$skip on a pushed $expand level that ALSO carries a nested
// $expand (`Books($top=1;$expand=Chapters)`), WITHOUT $count, used to compose a SQL Skip/Take at the
// SAME level that BuildShapedNavAccess then wrapped in a further element-wise Select projecting the
// deeper navigation — the same "window this collection AND project a further collection out of it" SQL
// APPLY/LATERAL shape #298 fixed for the $count case and #300 fixed for $levels. The fix: ApplyNavShape
// composes NO SQL Skip/Take at a level with children (gated on isProjectionLeaf, mirroring #298's
// countBound gate); the window is instead applied in the JSON pass (ShapePushedExpandsInJson →
// ApplyNestedWindow), bounded by the same MaxExpandTop ceiling WriteNestedCountAndWindow already
// enforces for the $count case. Reuses the Author/Book/Chapter/Page fixtures and harness from
// MultiLevelExpandPushdownSqliteTests.cs (MlAuthorProfile registers "Authors" with a delegate-less,
// pushable Books → Chapters → Pages chain) — that file itself must stay byte-unchanged.
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
}
