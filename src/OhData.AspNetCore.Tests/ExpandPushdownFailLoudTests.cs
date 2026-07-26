using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #304 (owner-settled "make it work", #298-style): a nested $top/$skip alongside a nested $expand
// child, WITHOUT $count (Books($top=1;$expand=Chapters)) used to compose an untranslatable SQL
// APPLY/LATERAL shape — windowing Books AND projecting Books' own further Chapters collection in the
// SAME query — for exactly the same reason #298 (the $count case) and #300 (the $levels case) hit it.
// This suite used to prove that shape failed loud (400); #304 instead defers the window to the JSON
// pass (ApplyNavShape no longer composes a SQL Skip/Take at a level with children; the window is
// applied afterward in ShapePushedExpandsInJson), exactly like #298/#300 already did for their shapes —
// so the request now WORKS (200), with the windowed parent present together with its own children.
//
// Reuses the Author/Book/Chapter fixtures and harness from MultiLevelExpandPushdownSqliteTests.cs (that
// file itself must stay byte-unchanged).
public sealed class ExpandPushdownFailLoudTests : IAsyncLifetime
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
    public async Task NestedTopWithChildren_NowWorks_200_WindowedParentWithItsChildren()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($top=1;$expand=Chapters)");

        // Before #304: 400 (the SQL shape was untranslatable). Now: 200, with the windowed Books entry
        // present WITH its own Chapters — no lying empty/omitted navigation, no fail-loud 400 either.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value").EnumerateArray().Single();
        JsonElement books = author.GetProperty("Books");

        // $top=1 windows Books to exactly one entry. Author 1's books are B1 (Id 10, Year 2001) and B2
        // (Id 11, Year 1999); the deterministic tiebreaker (nav element's key, ascending) selects the
        // lowest Id when no explicit $orderby was given on the nested clause — B1.
        Assert.Equal(1, books.GetArrayLength());
        JsonElement b1 = books.EnumerateArray().Single();
        Assert.Equal("B1", b1.GetProperty("Title").GetString());

        // The windowed parent's own children (Chapters) are present and correct — the whole point of
        // #304 is that windowing a level no longer drops its nested $expand.
        JsonElement chapters = b1.GetProperty("Chapters");
        Assert.Equal(2, chapters.GetArrayLength());
        Assert.Contains(chapters.EnumerateArray(), c => c.GetProperty("Heading").GetString() == "Zeta");
        Assert.Contains(chapters.EnumerateArray(), c => c.GetProperty("Heading").GetString() == "Alpha");
    }

    [Fact]
    public async Task NestedSkipWithChildren_NowWorks_200_WindowedParentWithItsChildren()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($skip=1;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value").EnumerateArray().Single();
        JsonElement books = author.GetProperty("Books");

        // $skip=1 over the deterministically-ordered (by Id) two books leaves exactly B2 (Id 11).
        Assert.Equal(1, books.GetArrayLength());
        JsonElement b2 = books.EnumerateArray().Single();
        Assert.Equal("B2", b2.GetProperty("Title").GetString());

        // B2 has no chapters in the shared fixture — an empty (not omitted) Chapters array proves the
        // nested $expand still rode along on the skipped-to survivor.
        Assert.Equal(0, b2.GetProperty("Chapters").GetArrayLength());
    }

    [Fact]
    public async Task FailLoud_DoesNotRegressTheFixedShapes()
    {
        // Sanity check alongside the two tests above: the #298 ($count + children) shape was already
        // fixed before #304 (composes no SQL bound at a level with children) — it must still succeed
        // now that the sibling $top/$skip-without-$count shape also works.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($count=true;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
