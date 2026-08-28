using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #298 regression coverage: `$count` on a pushed $expand level that ALSO carries a nested $expand
// (`Books($count=true;$expand=Chapters)`) used to compose a SQL Take(cap+1) bound at the SAME level
// that BuildShapedNavAccess then wrapped in a further element-wise Select projecting the deeper
// navigation — a "window this collection AND project a further collection out of it" shape that
// requires SQL APPLY/LATERAL, which SQLite (and not every provider) can translate. The untranslatable
// query threw inside pushedQuery.ToArray(), which the old catch swallowed and quietly re-fetched
// WITHOUT the folded navigations — so Books came back with no Chapters (and, depending on shape, no
// Books data at all) under a 200, never a 400. The fix: the SQL Take(cap+1) count bound is now composed
// ONLY at a projection LEAF (no nested $expand children); a level with children still enforces the
// MaxExpandTop ceiling, just in the JSON pass (materialize-then-check), exactly like the pre-existing
// $levels trade. Reuses the Author/Book/Chapter/Page fixtures and harness from
// MultiLevelExpandPushdownSqliteTests.cs (MlAuthorProfile registers "Authors" with a delegate-less,
// pushable Books → Chapters → Pages chain) — that file itself must stay byte-unchanged.
public sealed class NestedCountWithChildrenExpandPushdownTests : IAsyncLifetime
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
    public async Task Count_WithNestedExpand_ReturnsBooksAndChapters_JoinsInOneQuery_DefaultMaxExpandTop()
    {
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($count=true;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The parent collection must still come back JOIN'd — not collapsed to a bare "SELECT ... FROM
        // Authors" (the #298 silent-degrade symptom).
        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Authors");
        Assert.Contains("\"Books\"", sql);
        Assert.Contains("\"Chapters\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value")[0];

        Assert.Equal(2, author.GetProperty("Books@odata.count").GetInt32()); // B1, B2
        JsonElement books = author.GetProperty("Books");
        Assert.Equal(2, books.GetArrayLength());

        JsonElement b1 = books.EnumerateArray().Single(b => b.GetProperty("Title").GetString() == "B1");
        JsonElement b2 = books.EnumerateArray().Single(b => b.GetProperty("Title").GetString() == "B2");

        JsonElement b1Chapters = b1.GetProperty("Chapters");
        Assert.Equal(2, b1Chapters.GetArrayLength());
        Assert.Contains(b1Chapters.EnumerateArray(), c => c.GetProperty("Heading").GetString() == "Zeta");
        Assert.Contains(b1Chapters.EnumerateArray(), c => c.GetProperty("Heading").GetString() == "Alpha");

        Assert.Equal(0, b2.GetProperty("Chapters").GetArrayLength()); // B2 has no chapters
    }

    [Fact]
    public async Task Count_WithNestedExpand_StillJoins_WithMaxExpandTopNull()
    {
        // MaxExpandTop = null removes the ceiling entirely; the SAME "leaf-only SQL bound" fix must
        // still apply (the bug was in the SQL SHAPE, not specifically the presence of a cap). A FRESH
        // connection/counter — the harness re-seeds the same rows, which would violate the shared
        // in-memory connection's unique constraints if reused from the class fixture.
        using var freshConnection = new SqliteConnection("Data Source=:memory:");
        freshConnection.Open();
        var freshCounter = new MultiLevelDelegateCounter();
        await using TestFixture fxNullCap = await MultiLevelSqliteHarness.BuildAsync(
            freshConnection, freshCounter, sink: null, defaults: d => d.MaxExpandTop = null);

        HttpResponseMessage resp = await fxNullCap.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($count=true;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value")[0];
        Assert.Equal(2, author.GetProperty("Books@odata.count").GetInt32());
        JsonElement books = author.GetProperty("Books");
        JsonElement b1 = books.EnumerateArray().Single(b => b.GetProperty("Title").GetString() == "B1");
        Assert.Equal(2, b1.GetProperty("Chapters").GetArrayLength());
    }

    [Fact]
    public async Task Count_WithNestedExpand_AlsoCarryingCount_AppliesAtBothLevels()
    {
        // Books($count=true;$expand=Chapters($count=true)) — $count-with-children now applies at BOTH
        // the Books level (has children: Chapters) and the Chapters level (a leaf — the pre-existing,
        // already-working leaf case), proving the fix composes correctly at either depth.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($count=true;$expand=Chapters($count=true))");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value")[0];
        Assert.Equal(2, author.GetProperty("Books@odata.count").GetInt32());

        JsonElement books = author.GetProperty("Books");
        JsonElement b1 = books.EnumerateArray().Single(b => b.GetProperty("Title").GetString() == "B1");
        JsonElement b2 = books.EnumerateArray().Single(b => b.GetProperty("Title").GetString() == "B2");

        Assert.Equal(2, b1.GetProperty("Chapters@odata.count").GetInt32());
        Assert.Equal(2, b1.GetProperty("Chapters").GetArrayLength());
        Assert.Equal(0, b2.GetProperty("Chapters@odata.count").GetInt32());
        Assert.Equal(0, b2.GetProperty("Chapters").GetArrayLength());
    }

    [Fact]
    public async Task NestedCountWithChildren_AtDepthTwo_AppliesAtTheDeeperLevel()
    {
        // Books($expand=Chapters($count=true;$expand=Pages($count=true))) — Books itself carries no
        // $count, but the Chapters level (one level down) does, AND ALSO has children (Pages). This
        // exercises the #298 fix at DEPTH 2, not just at the top engaged level.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($expand=Chapters($count=true;$expand=Pages($count=true)))");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value")[0];
        JsonElement books = author.GetProperty("Books");

        JsonElement b1 = books.EnumerateArray().Single(b => b.GetProperty("Title").GetString() == "B1");
        JsonElement b2 = books.EnumerateArray().Single(b => b.GetProperty("Title").GetString() == "B2");

        // B1 has 2 chapters: Alpha (2 pages), Zeta (0 pages).
        Assert.Equal(2, b1.GetProperty("Chapters@odata.count").GetInt32());
        JsonElement b1Chapters = b1.GetProperty("Chapters");
        JsonElement alpha = b1Chapters.EnumerateArray().Single(c => c.GetProperty("Heading").GetString() == "Alpha");
        JsonElement zeta = b1Chapters.EnumerateArray().Single(c => c.GetProperty("Heading").GetString() == "Zeta");
        Assert.Equal(2, alpha.GetProperty("Pages@odata.count").GetInt32());
        Assert.Equal(2, alpha.GetProperty("Pages").GetArrayLength());
        Assert.Equal(0, zeta.GetProperty("Pages@odata.count").GetInt32());

        // B2 has no chapters at all.
        Assert.Equal(0, b2.GetProperty("Chapters@odata.count").GetInt32());
        Assert.Equal(0, b2.GetProperty("Chapters").GetArrayLength());
    }
}
