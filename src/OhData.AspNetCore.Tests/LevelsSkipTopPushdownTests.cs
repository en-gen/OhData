using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #300: inside the $levels recursion ApplyNavShape composed SQL Skip/Take like the non-$levels path,
// but every $levels level ALSO projects a further self-referential collection out of the SAME windowed
// collection -- the APPLY/LATERAL shape SQLite cannot translate, and #298 hit for $count. The
// untranslatable query threw inside ToArray(), the old catch swallowed it and re-fetched WITHOUT the
// folded projection, so the whole expand came back empty under a 200.
//
// Fix: BuildLevelsNavAccess defers all SQL-side Skip/Take (deferPagingToJson) and ShapeLevelsInJson
// applies the window instead, via the shared ApplyNestedWindow.
//
// $top is untouched here: per #296, MS's validator rejects ANY nested $top on a self-referential
// navigation before OhData's code runs, because the target type is necessarily its own entity set and
// its model-bound MaxTop defaults to 0. $skip has no such ceiling, which is why these tests use it.
public sealed class LevelsSkipTopPushdownTests : IAsyncLifetime
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
    public async Task Levels2_WithSkip_ReturnsWindowedTree_NotEmpty()
    {
        // Root's children are A(id 2), B(id 3); with no nested $orderby the deterministic key tiebreak
        // (Id ascending) orders them [A, B]. $skip=1 drops A, keeps B at level 1. The SAME $skip=1
        // re-applies at level 2 (#254 semantics): B's only child is B1, so skipping 1 of 1 leaves [].
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$skip=1)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = LevelsOptionsSqliteHarness.Root(doc);

        // THE #300 REGRESSION: before the fix this request threw inside EF, was swallowed, and the
        // whole Children graph came back as [] under a 200. It must not be empty here.
        JsonElement level1 = root.GetProperty("Children");
        Assert.Equal(new[] { "B" }, LevelsOptionsSqliteHarness.Names(level1)); // A was skipped at level 1

        // Level 2 (B's single child, B1) skipped away entirely — legitimately empty, not a bug.
        Assert.True(level1[0].TryGetProperty("Children", out JsonElement level2));
        Assert.Equal(0, level2.GetArrayLength());
    }

    [Fact]
    public async Task Levels2_WithOrderByDescAndSkip_WindowsDeterministicallyAtEveryLevel()
    {
        // Root's children ordered by name desc: [B, A]; $skip=1 drops B, keeps [A] at level 1.
        // The SAME $orderby+$skip re-apply at level 2: A's children (A1, A2, A3) ordered by name desc
        // are [A3, A2, A1]; $skip=1 drops A3, keeps [A2, A1] in that order — proving the window is
        // applied AFTER the nested $orderby, deterministically, independently at each level.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$orderby=name desc;$skip=1)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = LevelsOptionsSqliteHarness.Root(doc);

        JsonElement level1 = root.GetProperty("Children");
        Assert.Equal(new[] { "A" }, LevelsOptionsSqliteHarness.Names(level1));

        JsonElement level2 = level1[0].GetProperty("Children");
        Assert.Equal(new[] { "A2", "A1" }, LevelsOptionsSqliteHarness.Names(level2));
    }

    [Fact]
    public async Task Levels2_WithSkip_StillSelfJoinsInSql_NoSqlSkipTakeComposed()
    {
        // Proves the fix mechanism directly: the request succeeds via a genuine self-JOIN (multiple
        // references to the LvNodes table in one SELECT) rather than falling back to a single
        // un-joined "SELECT ... FROM LvNodes" — the #300 silent-degrade symptom.
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$skip=1)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = LevelsOptionsSqliteHarness.LastSelectAgainst(_sink, "LvNodes");
        Assert.True(System.Text.RegularExpressions.Regex.Matches(sql, "\"LvNodes\"").Count >= 2,
            $"$levels=2 must self-JOIN the table even with a nested $skip; got:\n{sql}");
    }

    [Fact]
    public async Task Levels2_WithSkipAndCount_BothApplyInJsonPass()
    {
        // #254 + #300 together: $count alongside $skip on a $levels expand. The count must reflect the
        // FULL filtered collection (pre-window, per §11.2.4.2) while the array is windowed by $skip.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=2;$count=true;$skip=1)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = LevelsOptionsSqliteHarness.Root(doc);

        // Root has 2 children (A, B) — the count is the FULL set, not the post-skip page size (1).
        Assert.Equal(2, root.GetProperty("Children@odata.count").GetInt32());
        Assert.Equal(new[] { "B" }, LevelsOptionsSqliteHarness.Names(root.GetProperty("Children")));
    }
}
