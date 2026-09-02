using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #335: `$levels=N` emitted N+1 join levels. The innermost was the recursion's terminating leaf,
// `n.Children.Take(0).ToList()` -- an expression that still NAMES the navigation, so EF composed a
// real join for it: a full-table ROW_NUMBER() window whose every row was discarded by WHERE row <= 0.
//
// Not a constant cost: translation costs ~3x per collection level (#328), so the dead level is a full
// factor of 3 on the whole request. Measured end-to-end on a 16-node chain:
//
//   $levels   before    after
//        5      309 ms    94 ms
//        6      883 ms   238 ms     <- the #328 depth ceiling
//        7    2,404 ms   677 ms
//        9    9,856 ms  2,196 ms
//
// A PURE optimisation, so both halves are pinned: the join count is exactly N with no ROW_NUMBER(),
// and the response bytes below were captured from the PRE-fix build and asserted unchanged. Do not
// regenerate them from a passing run -- they are the before-image, and that is the point.
public sealed class LevelsJoinCountSqliteTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _fx = await LevelsOptionsSqliteHarness.BuildAsync(_connection, new LevelsDelegateCounter(), _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LevelsN_EmitsExactlyNJoinLevels_AndNoDiscardedWindow(int levels)
    {
        _sink.Clear();
        HttpResponseMessage response = await _fx.Client.GetAsync(
            $"/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels={levels})");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        string sql = LevelsOptionsSqliteHarness.LastSelectAgainst(_sink, "LvNodes");

        // N, not N+1. The pre-#335 build emitted levels + 1 here.
        Assert.Equal(levels, Regex.Matches(sql, "LEFT JOIN").Count);

        // The discarded window is gone entirely: no ROW_NUMBER(), and therefore no `row <= 0`.
        // A nested $top/$skip WOULD legitimately emit one, so this assertion holds only for the
        // bare $levels shape under test.
        Assert.DoesNotContain("ROW_NUMBER()", sql);
        Assert.DoesNotContain("<= 0", sql);
    }

    // The before-image, captured on the pre-#335 build. Byte-identity is the acceptance criterion
    // for #335: it is an optimisation, not a behaviour change.
    private const string Levels1Body =
        "{\"@odata.context\":\"http://localhost/odata/$metadata#LvNodes\",\"value\":[{\"Id\":1,\"Name\":\"Root\",\"Active\":true,\"ParentId\":null,\"Children\":[{\"Id\":2,\"Name\":\"A\",\"Active\":true,\"ParentId\":1},{\"Id\":3,\"Name\":\"B\",\"Active\":false,\"ParentId\":1}]}]}";

    private const string Levels2Body =
        "{\"@odata.context\":\"http://localhost/odata/$metadata#LvNodes\",\"value\":[{\"Id\":1,\"Name\":\"Root\",\"Active\":true,\"ParentId\":null,\"Children\":[{\"Id\":2,\"Name\":\"A\",\"Active\":true,\"ParentId\":1,\"Children\":[{\"Id\":4,\"Name\":\"A1\",\"Active\":true,\"ParentId\":2},{\"Id\":5,\"Name\":\"A2\",\"Active\":false,\"ParentId\":2},{\"Id\":6,\"Name\":\"A3\",\"Active\":true,\"ParentId\":2}]},{\"Id\":3,\"Name\":\"B\",\"Active\":false,\"ParentId\":1,\"Children\":[{\"Id\":7,\"Name\":\"B1\",\"Active\":true,\"ParentId\":3}]}]}]}";

    // Note the `"Children":[]` on every leaf: the terminating level must still serialize as an
    // EMPTY ARRAY, not null and not absent. That is the one observable the old Take(0) leaf was
    // there to produce, and `new List<T>()` produces it identically.
    private const string Levels3Body =
        "{\"@odata.context\":\"http://localhost/odata/$metadata#LvNodes\",\"value\":[{\"Id\":1,\"Name\":\"Root\",\"Active\":true,\"ParentId\":null,\"Children\":[{\"Id\":2,\"Name\":\"A\",\"Active\":true,\"ParentId\":1,\"Children\":[{\"Id\":4,\"Name\":\"A1\",\"Active\":true,\"ParentId\":2,\"Children\":[{\"Id\":8,\"Name\":\"A1a\",\"Active\":true,\"ParentId\":4}]},{\"Id\":5,\"Name\":\"A2\",\"Active\":false,\"ParentId\":2,\"Children\":[]},{\"Id\":6,\"Name\":\"A3\",\"Active\":true,\"ParentId\":2,\"Children\":[]}]},{\"Id\":3,\"Name\":\"B\",\"Active\":false,\"ParentId\":1,\"Children\":[{\"Id\":7,\"Name\":\"B1\",\"Active\":true,\"ParentId\":3,\"Children\":[]}]}]}]}";

    // $levels carrying nested options (#254) rides the SAME leaf, and its per-level $count/$top
    // windowing is applied in the JSON pass — so it is the shape most at risk from a change to the
    // recursion's terminator. Its `Children@odata.count":0` on the leaves is the tell that the leaf
    // is still an empty collection rather than an absent one.
    private const string Levels3OptionedBody =
        "{\"@odata.context\":\"http://localhost/odata/$metadata#LvNodes\",\"value\":[{\"Id\":1,\"Name\":\"Root\",\"Active\":true,\"ParentId\":null,\"Children\":[{\"Id\":3,\"Name\":\"B\",\"Active\":false,\"ParentId\":1,\"Children\":[{\"Id\":7,\"Name\":\"B1\",\"Active\":true,\"ParentId\":3,\"Children\":[],\"Children@odata.count\":0}],\"Children@odata.count\":1},{\"Id\":2,\"Name\":\"A\",\"Active\":true,\"ParentId\":1,\"Children\":[{\"Id\":6,\"Name\":\"A3\",\"Active\":true,\"ParentId\":2,\"Children\":[],\"Children@odata.count\":0},{\"Id\":5,\"Name\":\"A2\",\"Active\":false,\"ParentId\":2,\"Children\":[],\"Children@odata.count\":0}],\"Children@odata.count\":3}],\"Children@odata.count\":2}]}";

    [Theory]
    [InlineData(1, Levels1Body)]
    [InlineData(2, Levels2Body)]
    [InlineData(3, Levels3Body)]
    public async Task LevelsN_ResponseBytesAreUnchangedByDroppingTheDeadLeaf(int levels, string expected)
    {
        HttpResponseMessage response = await _fx.Client.GetAsync(
            $"/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels={levels})");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LevelsWithNestedOptions_ResponseBytesAreUnchanged()
    {
        HttpResponseMessage response = await _fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children($levels=3;$top=2;$orderby=Id desc;$count=true)");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Levels3OptionedBody, await response.Content.ReadAsStringAsync());
    }
}
