using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #412: `Prefer: [odata.]maxpagesize=N` on NESTED ($expand) collections. Measured pre-fix: with
// MaxExpandTop=4 and maxpagesize=2, a bare $expand returned FOUR books, and the continuation route
// ignored the header outright.
//
// §8.2.8.5 decides the design in three sentences: the preference applies to "each collection within
// the response"; an over-size collection SHOULD be trimmed WITH a next link (exactly #313's shape);
// and the client MAY send a different value with every request following a next link. That last one
// refutes #412's stated blocker -- the page size travels on the REQUEST, so the $skip-only
// continuation surface does not have to widen, and correctness does not depend on the client
// resending anything.
//
// CLAMPED DOWN, NEVER UP, and only where a link goes out: lifting MaxExpandTop would let a header
// raise the ceiling, and clamping it would turn a 200 into a 400 and trim a non-pageable collection
// with no link -- the silent truncation M1 forbids.
//
// Preference-Applied is deliberately untouched: §8.2.8.5 makes the echo a MAY with ONE value for the
// whole response, so there is no per-collection echo. The 4.0-vs-4.01 token spelling is #372's
// defect, and PreferenceApplied_* pins that non-change so #372 cannot be closed here by accident.
public sealed class NestedMaxPageSizePreferenceTests
{
    private static Task<TestFixture> BuildAsync(
        SqliteConnection connection, int? cap, bool paging, SqlCaptureSink? sink = null) =>
        BareExpandSqliteHarness.BuildAsync(
            connection, sink,
            defaults: d =>
            {
                d.MaxExpandTop = cap;
                d.ExpandPagingEnabled = paging;
            },
            seedExtra: _ => { },
            logSqlParameterValues: sink is not null);

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(
        TestFixture fx, string url, string? prefer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (prefer is not null) request.Headers.TryAddWithoutValidation("Prefer", prefer);
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    // ── The nested page itself ───────────────────────────────────────────────────────────────────

    // FAILS WITHOUT THE FIX: four books and `?$skip=4`.
    [Theory]
    [InlineData("odata.maxpagesize=2")]  // OData 4.0 spelling
    [InlineData("maxpagesize=2")]        // OData 4.01 spelling
    public async Task NestedPage_HonoursASmallerRequestedPageSize_UnderBothSpellings(string prefer)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 4, paging: true);

        (_, string body) = await GetAsync(fx, "/odata/BeAuthors?$filter=Id eq 1&$expand=Books", prefer);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement parent = doc.RootElement.GetProperty("value")[0];

        Assert.Equal(2, parent.GetProperty("Books").GetArrayLength());
        Assert.Equal(
            "http://localhost/odata/BeAuthors(1)/Books?$skip=2",
            parent.GetProperty("Books@odata.nextLink").GetString());
    }

    // FAILS WITHOUT THE FIX: the continuation serves four and links at `?$skip=6`.
    [Fact]
    public async Task Continuation_HonoursASmallerRequestedPageSize_AndAdvancesBySizeServed()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 4, paging: true);

        (_, string body) = await GetAsync(fx, "/odata/BeAuthors(1)/Books?$skip=2", "odata.maxpagesize=2");
        using JsonDocument doc = JsonDocument.Parse(body);

        Assert.Equal(2, doc.RootElement.GetProperty("value").GetArrayLength());
        Assert.Equal(
            "http://localhost/odata/BeAuthors(1)/Books?$skip=4",
            doc.RootElement.GetProperty("@odata.nextLink").GetString());
    }

    // The SQL bound really moves — the narrowed page is fetched, not fetched-then-trimmed. The
    // continuation is the one hop where the framework owns the query, so it is the one place the
    // preference can reach the provider at all.
    // FAILS WITHOUT THE FIX: the LIMIT is the ceiling's 5 (cap + 1), not 3.
    [Fact]
    public async Task Continuation_PushesTheNarrowedPageIntoSql()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BuildAsync(connection, cap: 4, paging: true, sink: sink);

        sink.Clear();
        await GetAsync(fx, "/odata/BeAuthors(1)/Books?$skip=0", "odata.maxpagesize=2");
        string withPreference = BareExpandSqliteHarness.LastSelectAgainst(sink, "Books");

        sink.Clear();
        await GetAsync(fx, "/odata/BeAuthors(1)/Books?$skip=0", prefer: null);
        string withoutPreference = BareExpandSqliteHarness.LastSelectAgainst(sink, "Books");

        // Both are a LIMIT/OFFSET over an INNER JOIN (never the partitioned ROW_NUMBER() window page 1
        // uses); only the row-count PARAMETER differs — it stays a parameter by design, so the plan
        // cache is not defeated, which is why the harness renders parameter values here.
        Assert.Contains("INNER JOIN \"Books\"", withPreference, StringComparison.Ordinal);
        Assert.Contains("LIMIT @p2 OFFSET @p", withPreference, StringComparison.Ordinal);
        Assert.Contains("@p2='3'", withPreference, StringComparison.Ordinal);    // pageSize + 1
        Assert.Contains("@p2='5'", withoutPreference, StringComparison.Ordinal); // cap + 1
    }

    // ── The walk: no row skipped, none repeated, whether or not the client resends the header ────

    private static async Task<(List<int> Ids, List<int> PageSizes)> WalkAsync(
        TestFixture fx, string startUrl, string? preferOnEveryHop, string? preferOnFirstHopOnly = null)
    {
        var ids = new List<int>();
        var pageSizes = new List<int>();

        (_, string firstBody) = await GetAsync(
            fx, startUrl, preferOnFirstHopOnly ?? preferOnEveryHop);
        using JsonDocument first = JsonDocument.Parse(firstBody);
        JsonElement parent = first.RootElement.GetProperty("value")[0];
        JsonElement firstPage = parent.GetProperty("Books");
        foreach (JsonElement book in firstPage.EnumerateArray())
            ids.Add(book.GetProperty("Id").GetInt32());
        pageSizes.Add(firstPage.GetArrayLength());

        string? next = parent.TryGetProperty("Books@odata.nextLink", out JsonElement nl)
            ? nl.GetString() : null;

        // A finite guard: an unhonoured continuation loops forever, and a hanging test is worse than
        // a failing one (ServerDrivenPagingTests' discipline, copied in shape).
        while (next is not null && pageSizes.Count < 20)
        {
            (_, string body) = await GetAsync(
                fx, new Uri(next).PathAndQuery, preferOnFirstHopOnly is null ? preferOnEveryHop : null);
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement value = doc.RootElement.GetProperty("value");
            Assert.True(value.GetArrayLength() > 0, "a continuation served an empty page — spurious link");
            foreach (JsonElement book in value.EnumerateArray())
                ids.Add(book.GetProperty("Id").GetInt32());
            pageSizes.Add(value.GetArrayLength());
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out JsonElement n2) ? n2.GetString() : null;
        }

        Assert.Null(next);
        return (ids, pageSizes);
    }

    // FAILS WITHOUT THE FIX: one page of four plus one of one, not three of two/two/one.
    [Fact]
    public async Task Walk_ClientResendsThePreference_ServesEachRowExactlyOnceInOrder()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 4, paging: true);

        (List<int> ids, List<int> pageSizes) = await WalkAsync(
            fx, "/odata/BeAuthors?$filter=Id eq 1&$expand=Books", preferOnEveryHop: "odata.maxpagesize=2");

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ids);
        Assert.Equal(new[] { 2, 2, 1 }, pageSizes);
    }

    // The correctness argument does NOT depend on the client resending anything: $skip is absolute
    // and each hop advances by what it actually served, so a client that drops the header mid-walk
    // simply gets bigger pages from there on.
    // FAILS WITHOUT THE FIX on the page-size assertion (4 + 1, not 2 + 3); the id sequence is green
    // either way, which is exactly why the per-page sizes are asserted rather than just the ids.
    [Fact]
    public async Task Walk_ClientStopsSendingThePreference_StillServesEachRowExactlyOnceInOrder()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 4, paging: true);

        (List<int> ids, List<int> pageSizes) = await WalkAsync(
            fx, "/odata/BeAuthors?$filter=Id eq 1&$expand=Books",
            preferOnEveryHop: null, preferOnFirstHopOnly: "odata.maxpagesize=2");

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ids);
        // Hop 1 honours the header; hop 2 does not carry it and falls back to the ceiling. Page sizes
        // differ across the walk and that is fine -- what must hold is that nothing is skipped or
        // repeated, which the id sequence above pins.
        Assert.Equal(new[] { 2, 3 }, pageSizes);
    }

    // ── Clamping, and the shapes that must not move ──────────────────────────────────────────────

    // Byte-identical to the same request with no header at all: a preference may not lift the
    // server's ceiling. Green before and after the fix.
    [Fact]
    public async Task ByteIdentical_RequestedPageSizeAboveTheCeiling_IsClampedDownToMaxExpandTop()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 2, paging: true);

        (_, string withHeader) = await GetAsync(
            fx, "/odata/BeAuthors?$filter=Id eq 1&$expand=Books", "odata.maxpagesize=99");
        (_, string withoutHeader) = await GetAsync(
            fx, "/odata/BeAuthors?$filter=Id eq 1&$expand=Books", prefer: null);

        Assert.Equal(withoutHeader, withHeader);
        Assert.Contains("/BeAuthors(1)/Books?$skip=2", withHeader, StringComparison.Ordinal);
    }

    // The preference narrows the PAGE, never the CEILING. On a registration that did not opt into
    // paging there is no link to go with a trim, so the over-ceiling shape keeps its 400 and the
    // header changes nothing — if the ceiling were clamped instead, a request header could turn a 200
    // into a 400 (a cap of 4 with five books is a 200 today).
    // Green before and after the fix.
    [Fact]
    public async Task ByteIdentical_NonPageableRegistration_IgnoresThePreferenceEntirely()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 4, paging: false);

        (HttpResponseMessage withHeader, string withHeaderBody) = await GetAsync(
            fx, "/odata/BeAuthors?$filter=Id eq 1&$expand=Books", "odata.maxpagesize=1");
        (HttpResponseMessage plain, string plainBody) = await GetAsync(
            fx, "/odata/BeAuthors?$filter=Id eq 1&$expand=Books", prefer: null);

        Assert.Equal(HttpStatusCode.BadRequest, withHeader.StatusCode);
        Assert.Equal(plain.StatusCode, withHeader.StatusCode);
        Assert.Equal(plainBody, withHeaderBody);
    }

    // No header at all — the shipping shape. Green before and after the fix.
    [Fact]
    public async Task ByteIdentical_NoPreferenceHeader_IsUntouched()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 4, paging: true);

        (_, string body) = await GetAsync(fx, "/odata/BeAuthors?$filter=Id eq 1&$expand=Books", prefer: null);
        Assert.Equal(
            "{\"@odata.context\":\"http://localhost/odata/$metadata#BeAuthors\",\"value\":[" +
            "{\"Id\":1,\"Name\":\"Ann\",\"PublisherId\":100,\"Books\":[" +
            "{\"Id\":1,\"AuthorId\":1,\"Title\":\"Bk1\"}," +
            "{\"Id\":2,\"AuthorId\":1,\"Title\":\"Bk2\"}," +
            "{\"Id\":3,\"AuthorId\":1,\"Title\":\"Bk3\"}," +
            "{\"Id\":4,\"AuthorId\":1,\"Title\":\"Bk4\"}]," +
            "\"Books@odata.nextLink\":\"http://localhost/odata/BeAuthors(1)/Books?$skip=4\"}]}",
            body);
    }

    // ── Preference-Applied: exactly one header, one value, unchanged spelling ────────────────────

    // Pins the deliberate NON-change. §8.2.8.5 makes the echo a MAY and scopes it to a single "maximum
    // page size applied" for the whole response, so no second header is emitted for the nested
    // collection. The token spelling is left exactly as it was: it is #372's defect (4.0 spells it
    // `odata.maxpagesize`; this echoes the 4.01 `maxpagesize`), milestone 1.9.0, and closing it here
    // by accident would make that fix invisible. Green before and after the fix, in both directions.
    [Fact]
    public async Task PreferenceApplied_IsASingleUnchangedHeader_AndNoNestedEchoIsAdded()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 4, paging: true);

        (HttpResponseMessage response, _) = await GetAsync(
            fx, "/odata/BeAuthors?$filter=Id eq 1&$expand=Books", "odata.maxpagesize=2");

        string applied = Assert.Single(response.Headers.GetValues("Preference-Applied"));
        Assert.Equal("maxpagesize=2", applied);
    }

    // The continuation route emits no Preference-Applied at all, before or after: it never did, and
    // the echo is a MAY.
    [Fact]
    public async Task PreferenceApplied_ContinuationRouteEmitsNone()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 4, paging: true);

        (HttpResponseMessage response, _) = await GetAsync(
            fx, "/odata/BeAuthors(1)/Books?$skip=0", "odata.maxpagesize=2");

        Assert.False(response.Headers.Contains("Preference-Applied"));
    }
}
