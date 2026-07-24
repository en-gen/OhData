using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace OhData.AspNetCore.Tests;

// FAIL LOUD (owner directive, post-#298/#300 adversarial review): when the SQL shape a pushed $expand
// composes cannot be translated by the underlying provider, the request must now throw a clear 400
// OData error instead of silently degrading to a 200 with the affected navigation(s) quietly empty.
// #298 and #300 fixed the two SPECIFIC shapes that were reachable (nested $count with a nested $expand
// child; $skip/$top inside a $levels recursion) by no longer composing the untranslatable SQL in the
// first place. This suite proves the GENERAL mechanism: for any OTHER combination this provider still
// cannot translate — a genuine capability gap, not something A/B could pre-empt — the request now fails
// loud (400 InvalidQueryOption) rather than returning wrong/empty data under a 200.
//
// The reproducer below (Books($top=1;$expand=Chapters) — a nested $top or $skip alongside a nested
// $expand, WITHOUT $count) is empirically confirmed (via a throwaway probe run against this same SQLite
// harness before this file was written) to still trip an untranslatable SQL shape on SQLite: windowing
// Books AND projecting Books' own further Chapters collection in the same query requires SQL
// APPLY/LATERAL, exactly like the #298/#300 shapes, but this specific combination (no $count) is not
// something A or B changed — it is genuinely out of this task's scope (which named only #298's $count
// case and #300's $levels case), so it is exactly the kind of "any OTHER combination" case FAIL LOUD is
// for. Before this fix it returned 200 with Books quietly empty; it must now be a 400.
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
    public async Task UntranslatableNestedTopWithChildren_FailsLoud_400_NotSilentEmpty200()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($top=1;$expand=Chapters)");

        // Before this fix: 200, with Books silently empty (the fallback re-fetch dropped the folded
        // navigations without telling the client). Now: a clear 400, never a lying 200.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Authors", body);
        Assert.Contains("$expand", body);
        // The message must stay generic — never the raw EF/provider exception text (which could leak
        // schema/SQL details) — per this file's existing InternalServerError convention (S7).
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("SQLITE", body);
    }

    [Fact]
    public async Task UntranslatableNestedSkipWithChildren_FailsLoud_400()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($skip=1;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
    }

    [Fact]
    public async Task FailLoud_DoesNotRegressTheFixedShapes()
    {
        // Sanity check alongside the two tests above: the #298 ($count + children) and #300-adjacent
        // ($top/$skip + children) shapes are NOT the same shape as far as translatability goes — #298's
        // specific shape is now fixed (composes no SQL bound at a level with children), so it must still
        // succeed even though the sibling $top/$skip-without-$count shape above still 400s.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($count=true;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
