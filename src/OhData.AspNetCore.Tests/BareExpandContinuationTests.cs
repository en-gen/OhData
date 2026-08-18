using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #313 stage 5: the bare-$expand continuation route and the trim-and-link emission.
//
// Stage 2 gave every bare over-ceiling $expand a 400. This stage flips ONE subset of that — a truly
// bare leaf, on a profile that opted in with BOTH knobs — into 200 + `Nav@odata.nextLink`, and
// registers `GET /{Set}({key})/{Nav}?$skip=N` to serve the rest. Everything else in the fail-closed
// matrix keeps its 400, and a registration that did not opt in is untouched.
//
// FIXTURE PROVENANCE. Every test below rides BeAuthor/BeBook/BeChapter/BareExpandDbContext/
// BeAuthorProfile — authored by stage 2, not by this change — with the knob turned on and, where a
// particular child count is needed, extra rows seeded through the harness's additive `seedExtra`
// hook and isolated with a root $filter. That is deliberate: a suite whose fixtures were written in
// the same commit as the behaviour they pin can be green while shipping a defect, because the model
// was shaped around the code rather than the other way round. The two exceptions are called out at
// their definitions: a pre-ordered-parent profile (T2 cannot exist without one) and a
// delegate-declaring sibling profile (T4 cannot exist without one). Both are over the same model.

internal static class BareExpandContinuation
{
    /// <summary>Author 1 keeps stage 2's five books; these are the shapes the walk invariants need.</summary>
    internal static void SeedWalkShapes(BareExpandDbContext db)
    {
        db.Authors.AddRange(
            new BeAuthor { Id = 2, Name = "Bob" },       // exactly one book
            new BeAuthor { Id = 3, Name = "Cal" },       // no books at all
            new BeAuthor { Id = 4, Name = "Dee" },       // seven books
            new BeAuthor { Id = 5, Name = "Eve" });      // one book with five chapters (depth-2 probe)

        db.Books.Add(new BeBook { Id = 20, AuthorId = 2, Title = "Solo" });

        for (int i = 0; i < 7; i++)
            db.Books.Add(new BeBook { Id = 40 + i, AuthorId = 4, Title = $"D{i}" });

        db.Books.Add(new BeBook { Id = 50, AuthorId = 5, Title = "Deep" });
        for (int i = 0; i < 5; i++)
            db.Chapters.Add(new BeChapter { Id = 60 + i, BookId = 50, Heading = $"C{i}" });
    }

    internal static Task<TestFixture> BuildAsync(
        SqliteConnection connection, int? cap, bool pagingEnabled,
        SqlCaptureSink? sink = null, int? maxTop = null,
        Action<BareExpandDbContext>? seed = null,
        Action<OhDataBuilder>? extraProfiles = null) =>
        BareExpandSqliteHarness.BuildAsync(
            connection, sink,
            defaults: d =>
            {
                d.MaxExpandTop = cap;
                d.ExpandPagingEnabled = pagingEnabled;
                if (maxTop is int mt) d.MaxTop = mt;
            },
            seedExtra: seed ?? SeedWalkShapes,
            configureExtraProfiles: extraProfiles);

    /// <summary>The nested continuation link on <c>value[0]</c>, or null when there is none.</summary>
    internal static string? NestedLink(JsonElement root, string nav = "Books")
    {
        JsonElement parent = root.GetProperty("value")[0];
        return parent.TryGetProperty($"{nav}@odata.nextLink", out JsonElement nl) ? nl.GetString() : null;
    }

    /// <summary>Every <c>@odata.nextLink</c> key anywhere in the document, at any depth.</summary>
    internal static List<string> AllNextLinkKeys(JsonElement el, List<string>? into = null)
    {
        into ??= new List<string>();
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty p in el.EnumerateObject())
                {
                    if (p.Name.EndsWith("@odata.nextLink", StringComparison.Ordinal)) into.Add(p.Name);
                    AllNextLinkKeys(p.Value, into);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in el.EnumerateArray()) AllNextLinkKeys(item, into);
                break;
        }
        return into;
    }
}

// ── T1: the walk invariants ──────────────────────────────────────────────────────────────────────
//
// ServerDrivenPagingTests' helpers, copied in SHAPE (that file contains no $expand at all — it is a
// root-collection suite — so only its discipline transfers, not its coverage):
//   * a finite page guard, because an unhonoured continuation loops forever and a hanging test is
//     worse than a failing one;
//   * a non-empty assertion on every page, which is what catches a SPURIOUS link;
//   * PathAndQuery normalization, so the link is followed as an opaque server-issued URL rather than
//     reconstructed by the test;
//   * AssertServedExactlyOnceInOrder — the STRONG form. A bare expand always has a declared order
//     (the framework composes OrderBy(child key) on both sides), so the weak set form would pass a
//     resequencing defect at the page boundary, which is precisely the class of defect this feature
//     can introduce.
public sealed class BareExpandContinuationWalkTests
{
    private static async Task<(List<int> Ids, int Pages)> WalkAsync(
        TestFixture fx, string startUrl, string nav = "Books")
    {
        var ids = new List<int>();
        int pages = 0;

        // Hop 1 is the $expand itself: the parent's page carries the trimmed child array and, if
        // there is more, the nested link.
        JsonElement root = await fx.Client.GetFromJsonAsync<JsonElement>(startUrl);
        JsonElement parent = root.GetProperty("value")[0];
        foreach (JsonElement child in parent.GetProperty(nav).EnumerateArray())
            ids.Add(child.GetProperty("Id").GetInt32());
        pages++;

        string? relative = parent.TryGetProperty($"{nav}@odata.nextLink", out JsonElement nl)
            ? new Uri(nl.GetString()!).PathAndQuery
            : null;

        // Hops 2..n are the continuation route, an ordinary OData collection envelope.
        while (relative is not null)
        {
            Assert.True(++pages <= 25,
                "the nested nextLink walk did not terminate within 25 pages — the server is emitting " +
                "a continuation it does not honour, or an empty-page loop.");

            JsonElement page = await fx.Client.GetFromJsonAsync<JsonElement>(relative);
            JsonElement value = page.GetProperty("value");
            Assert.True(value.GetArrayLength() > 0,
                $"page {pages} came back EMPTY, so the previous page's nextLink was spurious.");

            foreach (JsonElement child in value.EnumerateArray())
                ids.Add(child.GetProperty("Id").GetInt32());

            relative = page.TryGetProperty("@odata.nextLink", out JsonElement nnl)
                ? new Uri(nnl.GetString()!).PathAndQuery
                : null;
        }
        return (ids, pages);
    }

    private static void AssertServedExactlyOnceInOrder(IReadOnlyList<int> ids, IEnumerable<int> expected)
        => Assert.Equal(expected, ids);

    /// <summary>
    /// The headline: five children, ceiling three. Stage 2 answered this shape with a 400; stage 5
    /// serves 3 + a link and the walk recovers all five, in order, once each.
    /// </summary>
    [Fact]
    public async Task FiveChildren_CeilingThree_WalksToExhaustion_InOrder()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(connection, cap: 3, pagingEnabled: true);

        var (ids, pages) = await WalkAsync(fx, "/odata/BeAuthors?$filter=Id eq 1&$expand=Books");

        Assert.Equal(2, pages);
        AssertServedExactlyOnceInOrder(ids, new[] { 1, 2, 3, 4, 5 });
    }

    /// <summary>
    /// The <c>rows % pageSize == 0</c> trap #360 fixed at the root, restated for the nested link: a
    /// collection of EXACTLY the ceiling must emit NO link. Getting this wrong produces a spurious
    /// continuation into an empty trailing page — which the walk helper's non-empty assertion would
    /// catch, but this asserts it directly at the boundary rather than downstream of it.
    /// </summary>
    [Fact]
    public async Task ChildCountExactlyEqualToTheCeiling_EmitsNoLink()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(connection, cap: 5, pagingEnabled: true);

        JsonElement root = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeAuthors?$filter=Id eq 1&$expand=Books");

        Assert.Equal(5, root.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        Assert.Null(BareExpandContinuation.NestedLink(root));
        Assert.Empty(BareExpandContinuation.AllNextLinkKeys(root));
    }

    /// <summary>
    /// And the same trap one hop in: seven children at a ceiling of 7 is one page; at a ceiling that
    /// DIVIDES the count the final continuation page is exactly full and must still be the last one.
    /// </summary>
    [Theory]
    [InlineData(7, 1)]  // exactly the count → single page, no link
    [InlineData(1, 7)]  // divides it evenly, seven full pages, the seventh must not link on
    [InlineData(6, 2)]  // cap - 1
    [InlineData(8, 1)]  // cap + 1
    public async Task SevenChildren_AcrossCeilings_WalkTerminatesWithoutASpuriousPage(int cap, int expectedPages)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(connection, cap: cap, pagingEnabled: true);

        var (ids, pages) = await WalkAsync(fx, "/odata/BeAuthors?$filter=Id eq 4&$expand=Books");

        Assert.Equal(expectedPages, pages);
        AssertServedExactlyOnceInOrder(ids, Enumerable.Range(40, 7));
    }

    [Fact]
    public async Task OneChild_UnderTheCeiling_EmitsNoLink()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(connection, cap: 3, pagingEnabled: true);

        JsonElement root = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeAuthors?$filter=Id eq 2&$expand=Books");

        Assert.Equal(1, root.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        Assert.Empty(BareExpandContinuation.AllNextLinkKeys(root));
    }

    [Fact]
    public async Task ZeroChildren_EmitsNoLink_AndTheArrayIsEmptyNotAbsent()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(connection, cap: 3, pagingEnabled: true);

        JsonElement root = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeAuthors?$filter=Id eq 3&$expand=Books");

        Assert.Equal(0, root.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        Assert.Empty(BareExpandContinuation.AllNextLinkKeys(root));
    }

    /// <summary>
    /// The link must survive a root <c>$select</c> that strips the parent key. This is G6 in the
    /// design and the reason the CLR page is threaded into the shaping pass at all: the key is not in
    /// the payload by the time the link is built, so reading it from there would produce a link
    /// containing an empty or wrong key for exactly the requests that ask for the least data.
    /// </summary>
    [Fact]
    public async Task RootSelectThatStripsTheKey_StillProducesAWorkingLink()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(connection, cap: 3, pagingEnabled: true);

        JsonElement root = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeAuthors?$filter=Id eq 1&$select=Name&$expand=Books");

        // The premise: the key really is gone from the payload.
        Assert.False(root.GetProperty("value")[0].TryGetProperty("Id", out _));

        string link = Assert.IsType<string>(BareExpandContinuation.NestedLink(root));
        Assert.Contains("BeAuthors(1)/Books", link, StringComparison.Ordinal);

        JsonElement page2 = await fx.Client.GetFromJsonAsync<JsonElement>(new Uri(link).PathAndQuery);
        Assert.Equal(2, page2.GetProperty("value").GetArrayLength());
    }

    /// <summary>
    /// A missing parent key is <c>200</c> + empty <c>value</c> + no link, NOT <c>404</c> (O3 on
    /// #313). <c>SelectMany</c> cannot distinguish "no such parent" from "a parent with no children",
    /// and an existence probe would cost a second round trip on EVERY continuation. Microsoft returns
    /// 404 here; this is a documented divergence, pinned so it cannot drift silently.
    /// </summary>
    [Fact]
    public async Task ContinuationForAMissingParent_Is200EmptyValue_NotA404()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(connection, cap: 3, pagingEnabled: true);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors(9999)/Books?$skip=3");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("value").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("@odata.nextLink", out _));
    }

    /// <summary>
    /// The continuation's envelope is an ordinary OData collection response, and the related entity's
    /// own navigations are omitted (§4.5.1/§11.2.4.2) exactly as the delegate-backed nav route omits
    /// them — this route takes no <c>$expand</c>, so nothing on the element may appear inline.
    /// </summary>
    [Fact]
    public async Task ContinuationEnvelope_HasContext_AndOmitsTheElementsOwnNavigations()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(connection, cap: 3, pagingEnabled: true);

        JsonElement page = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BeAuthors(1)/Books?$skip=3");

        Assert.EndsWith("/$metadata#BeAuthors(1)/Books",
            page.GetProperty("@odata.context").GetString(), StringComparison.Ordinal);
        JsonElement book = page.GetProperty("value")[0];
        Assert.True(book.TryGetProperty("Title", out _));
        Assert.False(book.TryGetProperty("Chapters", out _));
    }
}

// ── T2: determinism across the page boundary ─────────────────────────────────────────────────────

/// <summary>
/// A profile whose <c>GetQueryable</c> is PRE-ORDERED on a NON-UNIQUE column. This is the shape the
/// whole determinism argument turns on, and the only reason a second profile exists in this file.
/// </summary>
public sealed class BePreOrderedAuthorProfile : EntitySetProfile<int, BeAuthor>
{
    public BePreOrderedAuthorProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BePreOrderedAuthors";
        ExpandEnabled = true;
        FilterEnabled = true;
        // Descending on Name, which the seed deliberately duplicates — so this order alone is NOT a
        // total order over parents, let alone over children.
        GetQueryable = _ => Task.FromResult(db.Authors.OrderByDescending(a => a.Name).AsQueryable());
        HasMany(x => x.Books);
    }
}

public sealed class BareExpandContinuationDeterminismTests
{
    /// <summary>
    /// THE SHAPE THAT FALSIFIED THE PREVIOUS DESIGN, and the one
    /// <c>Microsoft.AspNetCore.OData</c> gets wrong: a parent collection that is already ordered
    /// (here on a duplicated, non-unique column) with a child collection spanning the page boundary.
    /// <para>
    /// The previous design reached for <c>EnsureStableOrder</c>, which has a path that SKIPS
    /// appending the key when the source is already ordered — so page 2+ would carry only the
    /// parent's non-total order and the walk would be unsound in principle, while a small SQLite
    /// fixture happened to return a stable order and hid it. This design composes an unconditional
    /// <c>OrderBy</c> on the child key resolved through the SAME <c>TryGetKeyClrProperty</c> call
    /// <c>ApplyNavShape</c> uses for page 1's tiebreaker, so the two sides agree by construction.
    /// </para>
    /// <para>
    /// The assertions are on the emitted SQL, not only on the rows: rows can be right by luck on a
    /// five-row table, and an ORDER BY that does not end in the child key is the defect regardless of
    /// what this particular provider returned.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PreOrderedParentOnANonUniqueColumn_BothPagesOrderByTheChildKey_AndNoRowMovesOrRepeats()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap: 3, pagingEnabled: true, sink: sink,
            seed: db =>
            {
                BareExpandContinuation.SeedWalkShapes(db);
                // Two more authors sharing author 1's name, so OrderByDescending(Name) is provably
                // non-unique and cannot by itself decide which parent — or which child — comes first.
                db.Authors.AddRange(
                    new BeAuthor { Id = 6, Name = "Ann" },
                    new BeAuthor { Id = 7, Name = "Ann" });
            },
            extraProfiles: b => b.AddEntitySetProfile<BePreOrderedAuthorProfile>());

        sink.Clear();
        JsonElement root = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BePreOrderedAuthors?$filter=Id eq 1&$expand=Books");

        string page1Sql = BareExpandSqliteHarness.LastSelectAgainst(sink, "Books");
        var page1Ids = root.GetProperty("value")[0].GetProperty("Books")
            .EnumerateArray().Select(b => b.GetProperty("Id").GetInt32()).ToList();

        string link = Assert.IsType<string>(BareExpandContinuation.NestedLink(root));
        sink.Clear();
        JsonElement page2 = await fx.Client.GetFromJsonAsync<JsonElement>(new Uri(link).PathAndQuery);
        string page2Sql = BareExpandSqliteHarness.LastSelectAgainst(sink, "Books");
        var page2Ids = page2.GetProperty("value")
            .EnumerateArray().Select(b => b.GetProperty("Id").GetInt32()).ToList();

        // 1. Both queries order by the CHILD key, and the parent's own order never reaches the child
        //    collection: page 1 orders it inside a partitioned window, page 2 in a plain ORDER BY.
        Assert.Contains("\"b\".\"Id\"", page1Sql, StringComparison.Ordinal);
        Assert.EndsWith("\"Id\"", LastOrderByClause(page2Sql), StringComparison.Ordinal);

        // 2. No row duplicates and none vanishes, and the sequence is the child key ascending.
        Assert.Equal(new[] { 1, 2, 3 }, page1Ids);
        Assert.Equal(new[] { 4, 5 }, page2Ids);
        Assert.Equal(5, page1Ids.Concat(page2Ids).Distinct().Count());
    }

    /// <summary>
    /// The <b>SQL SHAPE</b> gate. The continuation must be an index seek — an INNER JOIN with
    /// LIMIT/OFFSET — and must NOT be the partitioned <c>ROW_NUMBER()</c> window page 1 composes.
    /// A window function over the whole child table would make the continuation O(all children) per
    /// hop, which is the DoS #313 exists to close arriving through the fix for it.
    /// </summary>
    [Fact]
    public async Task ContinuationSql_IsAnInnerJoinWithLimitOffset_NotARowNumberWindow()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap: 3, pagingEnabled: true, sink: sink);

        // The contrast that makes the assertion meaningful: page 1 IS the ROW_NUMBER() plan.
        sink.Clear();
        await fx.Client.GetAsync("/odata/BeAuthors?$filter=Id eq 1&$expand=Books");
        string page1Sql = BareExpandSqliteHarness.LastSelectAgainst(sink, "Books");
        Assert.Contains("ROW_NUMBER()", page1Sql, StringComparison.Ordinal);

        sink.Clear();
        await fx.Client.GetAsync("/odata/BeAuthors(1)/Books?$skip=3");
        string page2Sql = BareExpandSqliteHarness.LastSelectAgainst(sink, "Books");

        Assert.Contains("INNER JOIN", page2Sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", page2Sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET", page2Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ROW_NUMBER()", page2Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The parent key reaches the provider as a query PARAMETER, not a literal baked into the command
    /// text. Pinned because the expression that achieves it (a one-field box read through
    /// <c>Expression.Field</c>, mimicking a C# closure) looks like an over-complication next to a
    /// plain <c>Expression.Constant</c>, and would be "simplified" into a per-key SQL string that
    /// defeats the provider's plan cache on a route designed to be called repeatedly.
    /// </summary>
    [Fact]
    public async Task ContinuationSql_ParameterizesTheParentKey()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap: 3, pagingEnabled: true, sink: sink);

        sink.Clear();
        await fx.Client.GetAsync("/odata/BeAuthors(1)/Books?$skip=3");
        string sql = BareExpandSqliteHarness.LastSelectAgainst(sink, "Books");

        Assert.Contains("@", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("= 1", sql, StringComparison.Ordinal);
    }

    private static string LastOrderByClause(string sql)
    {
        int idx = sql.LastIndexOf("ORDER BY", StringComparison.Ordinal);
        Assert.True(idx >= 0, $"no ORDER BY in the continuation SQL:\n{sql}");
        string tail = sql[idx..];
        int limit = tail.IndexOf("LIMIT", StringComparison.Ordinal);
        return (limit >= 0 ? tail[..limit] : tail).TrimEnd();
    }
}

// ── T3: root and nested links coexist ────────────────────────────────────────────────────────────

public sealed class BareExpandContinuationCoexistenceTests
{
    /// <summary>
    /// §4.6 of the design was argued, not measured, because it needed stage 2's bound in place. This
    /// converts it. The root's continuation is a <c>$skiptoken</c> on the collection path; the
    /// nested one is a plain <c>$skip</c> on a different path served by a different route whose
    /// handler has no <c>$skiptoken</c> concept at all. Neither link builder reads the response body,
    /// so neither can rewrite the other.
    /// </summary>
    [Fact]
    public async Task RootNextLinkAndNestedNextLink_CoexistAndDoNotPerturbEachOther()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap: 3, pagingEnabled: true, maxTop: 1);

        JsonElement page1 = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BeAuthors?$expand=Books");

        // Envelope-level link: the root's own paging, re-expressed as an opaque $skiptoken.
        // BuildNextPageLink round-trips the query string through HttpUtility, so '$' arrives
        // percent-encoded — compare on the option NAME rather than on a literal '$' spelling.
        string rootLink = page1.GetProperty("@odata.nextLink").GetString()!;
        Assert.Contains("skiptoken=", rootLink, StringComparison.Ordinal);
        Assert.DoesNotContain("skip=", rootLink.Replace("skiptoken=", "", StringComparison.Ordinal));

        // Per-entity link: the nested continuation, a plain $skip on the nav path.
        Assert.Single(page1.GetProperty("value").EnumerateArray());
        string nested = Assert.IsType<string>(BareExpandContinuation.NestedLink(page1));
        Assert.Contains("/BeAuthors(1)/Books?$skip=3", nested, StringComparison.Ordinal);

        // Walking the NESTED link does not disturb the root walk...
        JsonElement nestedPage = await fx.Client.GetFromJsonAsync<JsonElement>(new Uri(nested).PathAndQuery);
        Assert.Equal(2, nestedPage.GetProperty("value").GetArrayLength());

        // ...and re-fetching the root page 1 is unchanged by having followed it.
        JsonElement page1Again = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BeAuthors?$expand=Books");
        Assert.Equal(page1.ToString(), page1Again.ToString());
    }

    /// <summary>
    /// A parent that appears on ROOT page 2 gets its OWN correct child link — page 1's child offset
    /// is always 0 (a bare expand carries no <c>$skip</c>), so the root's offset must never leak into
    /// the nested link's <c>$skip</c>.
    /// </summary>
    [Fact]
    public async Task AParentOnRootPageTwo_GetsItsOwnCorrectChildLink()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap: 3, pagingEnabled: true, maxTop: 3);

        JsonElement page1 = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BeAuthors?$expand=Books");
        string rootNext = new Uri(page1.GetProperty("@odata.nextLink").GetString()!).PathAndQuery;

        JsonElement page2 = await fx.Client.GetFromJsonAsync<JsonElement>(rootNext);

        // Author 4 (seven books) lands on root page 2 and links from child offset 3, not from 3 + the
        // root's own offset.
        JsonElement author4 = page2.GetProperty("value").EnumerateArray()
            .Single(a => a.GetProperty("Id").GetInt32() == 4);
        string nested = author4.GetProperty("Books@odata.nextLink").GetString()!;
        Assert.Contains("/BeAuthors(4)/Books?$skip=3", nested, StringComparison.Ordinal);

        JsonElement rest = await fx.Client.GetFromJsonAsync<JsonElement>(new Uri(nested).PathAndQuery);
        Assert.Equal(3, rest.GetProperty("value").GetArrayLength());
        Assert.Contains("$skip=6", rest.GetProperty("@odata.nextLink").GetString()!, StringComparison.Ordinal);
    }
}

// ── T4: the delegate-safety partition ────────────────────────────────────────────────────────────

/// <summary>
/// A SIBLING profile over the SAME <c>BeAuthor</c> EDM entity type that declares <c>Books</c> WITH a
/// delegate. Its whole purpose is the hole a per-profile registration predicate would leave: this
/// sibling makes <c>ResolveNavTreatment</c> return <c>Blank</c> for BOTH sets, and a predicate that
/// only looked at <c>BeAuthorProfile</c>'s own <c>NavigationRoutes</c> would still register a
/// continuation route on <c>BeAuthors</c> serving those rows raw.
/// </summary>
public sealed class BeDelegatedAuthorProfile : EntitySetProfile<int, BeAuthor>
{
    internal static int Invocations;

    public BeDelegatedAuthorProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BeDelegatedAuthors";
        ExpandEnabled = true;
        FilterEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Authors.AsQueryable());
        HasMany(x => x.Books, (key, _) =>
        {
            System.Threading.Interlocked.Increment(ref Invocations);
            return Task.FromResult<IEnumerable<BeBook>>(db.Books.Where(b => b.AuthorId == key).ToList());
        });
    }
}

public sealed class BareExpandContinuationDelegateSafetyTests
{
    /// <summary>
    /// STRUCTURAL: no continuation route exists for a navigation that any profile in the candidate
    /// set routes through a delegate. Read off the live route table rather than off the predicate, so
    /// it fails if registration ever stops consulting <c>ResolveNavTreatment</c>.
    /// </summary>
    [Fact]
    public async Task NoContinuationRouteExists_ForANavigationASiblingProfileDelegates()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap: 3, pagingEnabled: true,
            extraProfiles: b => b.AddEntitySetProfile<BeDelegatedAuthorProfile>());

        var patterns = fx.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .ToList();

        // BeAuthors declares Books delegate-LESS, but its sibling delegates it — so the treatment is
        // Blank for both and NEITHER set may have a raw continuation route.
        Assert.DoesNotContain(patterns, p => p.Contains("BeAuthors({key})/Books", StringComparison.Ordinal));

        // The sibling's own delegate-backed nav route is untouched and still there.
        Assert.Contains(patterns, p => p.Contains("BeDelegatedAuthors({key})/Books", StringComparison.Ordinal));
    }

    /// <summary>
    /// BEHAVIOURAL: with the sibling present, the delegate-less set emits NO link and its continuation
    /// URL is a hard 404 — the over-ceiling shape keeps the <c>400</c> stage 2 gave it, unchanged.
    /// <para>
    /// MEASURED CORRECTION to the premise this test was written against. The brief asserted that a
    /// sibling delegate "blanks the expand", so a per-profile registration predicate would be the only
    /// thing serving those rows raw. That is <b>false at depth 1</b>: <c>ApplyCollectionPipelineAsync</c>
    /// calls <c>ExpandLevelAsync</c> with <c>new[] { requestSource }</c> — the URL-named profile ALONE
    /// — so at the root <c>ResolveNavTreatment</c> never sees the sibling, returns <c>ServeRaw</c> for
    /// <c>BeAuthors</c>, and the rows really are served raw. (Measured: <c>/BeAuthors?$expand=Books</c>
    /// with the sibling registered returns the stage-2 ceiling <c>400</c>, which is only reachable
    /// once the rows have been materialized and counted — a blanked array would be empty and pass.)
    /// That is a pre-existing property of the root level, not something this stage introduces.
    /// </para>
    /// <para>
    /// What the shared predicate therefore buys is narrower than the brief claimed, and still the
    /// thing that matters: stage 5 does not WIDEN that exposure. Registering on
    /// <c>ResolveNavTreatment</c> over the parent type's candidate set means the sibling's delegate
    /// suppresses both the route and the link, so the shape stays exactly where stage 2 left it
    /// instead of gaining a raw continuation endpoint. Fail-closed in the safe direction.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WithASiblingDelegate_NoLinkAndNoRoute_AndTheShapeKeepsItsStageTwo400()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap: 3, pagingEnabled: true,
            extraProfiles: b => b.AddEntitySetProfile<BeDelegatedAuthorProfile>());

        HttpResponseMessage overCeiling = await fx.Client.GetAsync(
            "/odata/BeAuthors?$filter=Id eq 1&$expand=Books");
        Assert.Equal(HttpStatusCode.BadRequest, overCeiling.StatusCode);
        string body = await overCeiling.Content.ReadAsStringAsync();
        Assert.Contains("maximum of 3", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@odata.nextLink", body, StringComparison.Ordinal);

        HttpResponseMessage cont = await fx.Client.GetAsync("/odata/BeAuthors(1)/Books?$skip=3");
        Assert.Equal(HttpStatusCode.NotFound, cont.StatusCode);

        // The delegate's own path still works, and is the only thing that serves those rows.
        BeDelegatedAuthorProfile.Invocations = 0;
        JsonElement viaDelegate = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BeDelegatedAuthors(1)/Books");
        Assert.Equal(5, viaDelegate.GetProperty("value").GetArrayLength());
        Assert.Equal(1, BeDelegatedAuthorProfile.Invocations);
    }

    /// <summary>
    /// The contrast that makes the two tests above mean something: with NO sibling, the very same
    /// registration DOES get the route. Without this, both assertions above would pass against a
    /// build that registered no continuation routes at all.
    /// </summary>
    [Fact]
    public async Task WithoutTheSibling_TheSameNavigationDoesGetAContinuationRoute()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(connection, cap: 3, pagingEnabled: true);

        var patterns = fx.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .ToList();

        Assert.Contains(patterns, p => p.Contains("BeAuthors({key})/Books", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the knob itself is the other half of the partition: with <c>ExpandPagingEnabled</c> off,
    /// no continuation route is registered at all, so the route table of a default registration is
    /// what it was before this change.
    /// </summary>
    [Fact]
    public async Task WithTheKnobOff_NoContinuationRouteIsRegistered()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(connection, cap: 3, pagingEnabled: false);

        var patterns = fx.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .ToList();

        Assert.DoesNotContain(patterns, p => p.Contains("BeAuthors({key})/Books", StringComparison.Ordinal));

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors(1)/Books?$skip=3");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// And with the knob on but NO ceiling, the flag is still inert: the page size a continuation
    /// would use does not exist, so there is nothing to register and nothing to link.
    /// </summary>
    [Fact]
    public async Task WithTheKnobOnButNoCeiling_NoRouteAndNoLink()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(connection, cap: null, pagingEnabled: true);

        var patterns = fx.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .ToList();
        Assert.DoesNotContain(patterns, p => p.Contains("BeAuthors({key})/Books", StringComparison.Ordinal));

        JsonElement root = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeAuthors?$filter=Id eq 1&$expand=Books");
        Assert.Equal(5, root.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        Assert.Empty(BareExpandContinuation.AllNextLinkKeys(root));
    }
}

// ── T5: the fail-closed matrix ───────────────────────────────────────────────────────────────────

public sealed class BareExpandContinuationFailClosedTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await BareExpandContinuation.BuildAsync(_connection, cap: 3, pagingEnabled: true);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    /// <summary>
    /// One row of §5's matrix per case. Each asserts BOTH halves — the <c>400</c>, and the absence of
    /// any <c>@odata.nextLink</c> key anywhere in the body. The second half is what catches a
    /// half-applied change that trims and links and THEN rejects.
    /// </summary>
    [Theory]
    // A nested option a $skip-only link cannot carry.
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Books($filter=Id gt 0)")]
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Books($orderby=Title)")]
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Books($select=Title)")]
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Books($skip=1)")]
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Books($count=true)")]
    // A level WITH nested children: no SQL bound is composable at all (APPLY/LATERAL), so the rows
    // were already fully materialized and a link would advertise a bound that does not exist.
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Books($expand=Chapters)")]
    public async Task EveryNonBareShapeOverTheCeiling_Still400s_AndEmitsNoLinkAnywhere(string url)
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("@odata.nextLink", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// DEPTH >= 2 stays <c>400</c> (O5). Author 5 has one book — under the ceiling at depth 1, so the
    /// depth-1 arm passes — and that book has five chapters, so the breach is unambiguously at depth
    /// 2. Without this isolation the test would pass for the wrong reason (the depth-1 rejection).
    /// </summary>
    [Fact]
    public async Task ADepthTwoLeafOverTheCeiling_Still400s_EvenThoughDepthOneIsUnderIt()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/BeAuthors?$filter=Id eq 5&$expand=Books($expand=Chapters)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Chapters", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@odata.nextLink", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>$skip</c>-ONLY surface. The continuation route is not a second general-purpose
    /// collection route: every other system query option is rejected, including ones the delegate-
    /// backed nav route happily accepts ($select/$orderby/$top/$count) and the root's own
    /// <c>$skiptoken</c>.
    /// </summary>
    [Theory]
    [InlineData("$select=Title")]
    [InlineData("$orderby=Title")]
    [InlineData("$top=2")]
    [InlineData("$count=true")]
    [InlineData("$filter=Id gt 0")]
    [InlineData("$expand=Chapters")]
    [InlineData("$search=x")]
    [InlineData("$apply=identity")]
    [InlineData("$compute=1 as x")]
    [InlineData("$skiptoken=BQAAAA%3d%3d")]
    [InlineData("$levels=2")]
    public async Task TheContinuationRouteRejectsEverythingExceptSkip(string option)
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync($"/odata/BeAuthors(1)/Books?$skip=3&{option}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("UnsupportedQueryOption", body, StringComparison.Ordinal);
        Assert.Contains("accepts '$skip' only", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>$format</c> is the ONE exemption from the rule above, and it is a deliberate deviation from
    /// the brief's "reject every other query option". It is not a data option: §11.2.12 content
    /// negotiation is implemented once on the group filter in <c>MapAll</c>, for every route on the
    /// whole OData surface, and never reaches this handler. Refusing it would make this the only
    /// route in the surface that <c>400</c>s a conformant, already-supported option, and would break
    /// the common client habit of appending it to a server-issued link — at no security or
    /// correctness benefit, since it cannot change a single row.
    /// <para>
    /// An unsupported VALUE is still rejected, by that same group filter — which is the second
    /// assertion, and what shows the exemption forwards to the existing check rather than disabling it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheContinuationRouteAcceptsFormat_BecauseTheGroupFilterOwnsIt()
    {
        HttpResponseMessage json = await _fx.Client.GetAsync("/odata/BeAuthors(1)/Books?$skip=3&$format=json");
        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        Assert.Equal(2, JsonDocument.Parse(await json.Content.ReadAsStringAsync())
            .RootElement.GetProperty("value").GetArrayLength());

        HttpResponseMessage xml = await _fx.Client.GetAsync("/odata/BeAuthors(1)/Books?$skip=3&$format=xml");
        Assert.Equal(HttpStatusCode.BadRequest, xml.StatusCode);
        Assert.Contains("UnsupportedFormat", await xml.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task TheContinuationRouteRejectsAnInvalidSkip(string value)
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync($"/odata/BeAuthors(1)/Books?$skip={value}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("InvalidQueryOption", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>A malformed key is the shared BadKeyError, as on every other keyed route.</summary>
    [Fact]
    public async Task TheContinuationRouteRejectsAMalformedKey()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors(notanint)/Books?$skip=3");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>
    /// A single-valued navigation is never pageable — one related row is not the DoS and there is
    /// nothing to continue — so no route is registered for it and it keeps its ordinary behaviour.
    /// </summary>
    [Fact]
    public async Task ASingleValuedNavigationGetsNoContinuationRoute()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors(1)/Publisher");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        JsonElement root = await _fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeAuthors?$filter=Id eq 1&$expand=Publisher");
        Assert.Empty(BareExpandContinuation.AllNextLinkKeys(root));
    }
}

// ── T5b: $levels keeps its 400 ───────────────────────────────────────────────────────────────────

public sealed class BareExpandContinuationLevelsTests
{
    /// <summary>
    /// <c>$levels</c> keeps its <c>400</c> at every depth even with paging enabled, and byte-identically
    /// to what stage 2 produced — even though <c>Children($levels=1)</c> is a spec-equivalent
    /// restatement of a bare <c>$expand=Children</c>, with an identical response body under the
    /// ceiling. The reason it must not page is mechanical, not stylistic:
    /// <c>BuildLevelsNavAccess</c> composes NO SQL bound for the recursion (it defers paging to JSON
    /// and passes a null ceiling), so the rows are already fully materialized by the time the ceiling
    /// is checked. A link there would advertise a bound that does not exist — the ceiling is a data
    /// ceiling everywhere and a materialization ceiling only at a genuine leaf.
    /// <para>
    /// Rides <c>LevelsOptionsSqliteHarness</c> — $levels is only legal on a self-referential
    /// navigation, and that fixture predates both this stage and stage 2's use of it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Children($levels=1)")]
    [InlineData("Children($levels=2)")]
    [InlineData("Children($levels=2;$select=name)")]
    public async Task LevelsOverTheCeiling_Still400s_AndEmitsNoLink(string expand)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await LevelsOptionsSqliteHarness.BuildAsync(
            connection, new LevelsDelegateCounter(), sink: null,
            defaults: d => { d.MaxExpandTop = 1; d.ExpandPagingEnabled = true; });

        HttpResponseMessage resp = await fx.Client.GetAsync(
            $"/odata/LvNodes?$filter=parentId eq null&$expand={expand}");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("maximum of 1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@odata.nextLink", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// On this fixture even the BARE <c>$expand=Children</c> keeps its <c>400</c>, and the reason is
    /// the shared predicate rather than anything about <c>$levels</c>: <c>LvSecureNodeProfile</c> is a
    /// sibling entity set over the SAME <c>LvNode</c> EDM entity type that declares <c>Children</c>
    /// WITH a delegate, so <c>ResolveNavTreatment</c> over the candidate set is not
    /// <c>ServeRaw</c> and neither a route nor a link may exist for it.
    /// <para>
    /// This is the delegate-safety partition asserted on a fixture NEITHER this stage NOR stage 2
    /// authored — the strongest provenance available for that claim, and the reason this test lives
    /// here rather than beside the purpose-built sibling in the T4 block. Its counterpart, that a
    /// nav with no delegating sibling DOES page at the same ceiling, is
    /// <see cref="BareExpandContinuationWalkTests.FiveChildren_CeilingThree_WalksToExhaustion_InOrder"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnThisFixtureEvenTheBareShapeIsRefused_BecauseASiblingProfileDelegatesTheNav()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await LevelsOptionsSqliteHarness.BuildAsync(
            connection, new LevelsDelegateCounter(), sink: null,
            defaults: d => { d.MaxExpandTop = 1; d.ExpandPagingEnabled = true; });

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.DoesNotContain("@odata.nextLink", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var patterns = fx.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .ToList();
        Assert.DoesNotContain(patterns, p => p.Contains("LvNodes({key})/Children", StringComparison.Ordinal));
    }
}

// ── T6: boundary classification ──────────────────────────────────────────────────────────────────

public sealed class BareExpandContinuationBoundaryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await BareExpandContinuation.BuildAsync(_connection, cap: 3, pagingEnabled: true);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    /// <summary>
    /// <c>$expand=Books()</c> never reaches this framework at all: Microsoft's own URI parser rejects
    /// an empty nested option list. Pinned so the §4.2 classification table is complete — this row is
    /// "someone else's 400", not ours, and the message proves which.
    /// </summary>
    [Fact]
    public async Task EmptyNestedOptionList_Is400FromTheODataUriParser()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$filter=Id eq 1&$expand=Books()");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Missing expand option", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@odata.nextLink", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two no-ops the parser lets through — and the rule they establish: a nested option list that
    /// normalizes to the IDENTITY transform is bare. <c>$skip=0</c> composes nothing (ApplyNavShape
    /// guards <c>sk &gt; 0</c>) and <c>$count=false</c> is literally the same value as absent
    /// (<c>EngagedExpand.Count</c> is <c>CountOption == true</c>), so both page, and the continuation
    /// they produce is faithful because it starts from offset 0 either way.
    /// </summary>
    [Theory]
    [InlineData("$expand=Books($skip=0)")]
    [InlineData("$expand=Books($count=false)")]
    public async Task TheTwoIdentityNoOps_Page_JustLikeATrulyBareExpand(string expand)
    {
        JsonElement root = await _fx.Client.GetFromJsonAsync<JsonElement>(
            $"/odata/BeAuthors?$filter=Id eq 1&{expand}");

        Assert.Equal(3, root.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        string link = Assert.IsType<string>(BareExpandContinuation.NestedLink(root));
        Assert.Contains("/BeAuthors(1)/Books?$skip=3", link, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>$top=0</c> is <c>200</c> with an empty array and NO link. The client asked for zero rows and
    /// got zero rows, so the response is complete with respect to the request — a continuation would
    /// be offering to serve rows the client explicitly declined.
    /// </summary>
    [Fact]
    public async Task NestedTopZero_Is200EmptyArray_WithNoLink()
    {
        JsonElement root = await _fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeAuthors?$filter=Id eq 1&$expand=Books($top=0)");

        Assert.Equal(0, root.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        Assert.Empty(BareExpandContinuation.AllNextLinkKeys(root));
    }

    /// <summary>
    /// An explicit nested <c>$top</c> at or under the ceiling likewise gets no link, for the same
    /// reason: <c>$top</c> wins over the default ceiling bound, so the answer is complete.
    /// </summary>
    [Fact]
    public async Task NestedTopUnderTheCeiling_GetsNoLink()
    {
        JsonElement root = await _fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeAuthors?$filter=Id eq 1&$expand=Books($top=2)");

        Assert.Equal(2, root.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        Assert.Empty(BareExpandContinuation.AllNextLinkKeys(root));
    }
}

// ── Startup route-collision validation ───────────────────────────────────────────────────────────

public sealed class BeCollidingFunctionProfile : EntitySetProfile<int, BeAuthor>
{
    public BeCollidingFunctionProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BeCollidingAuthors";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Authors.AsQueryable());
        HasMany(x => x.Books);
        // An entity-level bound function named exactly like the delegate-less collection navigation
        // (the function name is the method name). Legal on develop — nothing registers
        // GET /{Set}({key})/Books for a delegate-less nav — and a duplicate (template, GET) the
        // moment the continuation route appears.
        BindEntityFunction(Books);
    }

    private Task<int> Books(int key) => Task.FromResult(key);
}

public sealed class BareExpandContinuationCollisionTests
{
    /// <summary>
    /// The collision the existing startup validation cannot see: it compares bound functions against
    /// <c>StructuralProperties</c> ONLY, and <c>BuildStructuralProperties</c> subtracts every declared
    /// navigation. So a bound function sharing a name with a delegate-less collection navigation is
    /// perfectly legal today and becomes an ambiguous-match failure at REQUEST time once the
    /// continuation route exists. Fail at <c>MapOhData()</c> instead.
    /// </summary>
    [Fact]
    public async Task ABoundFunctionNamedLikeAPageableNavigation_ThrowsAtStartup()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using TestFixture fx = await BareExpandContinuation.BuildAsync(
                connection, cap: 3, pagingEnabled: true,
                extraProfiles: b => b.AddEntitySetProfile<BeCollidingFunctionProfile>());
        });

        Assert.Contains("BeCollidingAuthors", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Books", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ExpandPagingEnabled", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contrast: the exact same profile is fine with the knob off, which is what makes the
    /// message's "that route is registered because ExpandPagingEnabled is on" clause true rather than
    /// merely plausible — and proves the collision is INTRODUCED by this stage, not pre-existing.
    /// </summary>
    [Fact]
    public async Task TheSameProfileStartsFine_WithTheKnobOff()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap: 3, pagingEnabled: false,
            extraProfiles: b => b.AddEntitySetProfile<BeCollidingFunctionProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeCollidingAuthors(1)/Books");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}

// ── T0: the brownfield gate, in-process ──────────────────────────────────────────────────────────

public sealed class BareExpandContinuationInertnessTests
{
    private static async Task<string> ProbeAsync(int? cap, bool paging, string url)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap, paging, sink: sink);
        sink.Clear();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        string body = await resp.Content.ReadAsStringAsync();
        return $"{(int)resp.StatusCode}\n{body}\n---SQL---\n{string.Join("\n", sink.Snapshot().Select(NormalizeSql))}";
    }

    /// <summary>
    /// EF's <c>LogTo</c> line carries a wall-clock timestamp and an elapsed-milliseconds figure, both
    /// of which differ between two runs of the same query. Strip exactly those two and nothing else —
    /// the comparison must still fail on any difference in the command text, the parameter list, or
    /// the NUMBER of statements issued.
    /// </summary>
    private static string NormalizeSql(string statement) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(
                statement, @"\d+/\d+/\d+ \d+:\d+:\d+\.\d+", "<ts>"),
            @"\(\d+ms\)", "(<ms>)");

    /// <summary>
    /// With no ceiling — the shipping default after stage 1 — turning the flag on changes NOTHING:
    /// same status, same body, same SQL. The strongest in-process form of T0, since the two arms
    /// differ only in the flag this stage taught the code to read.
    /// </summary>
    [Theory]
    [InlineData("/odata/BeAuthors?$expand=Books")]
    [InlineData("/odata/BeAuthors?$expand=Books($expand=Chapters)")]
    [InlineData("/odata/BeAuthors?$expand=Books($count=true)")]
    [InlineData("/odata/BeAuthors?$expand=Books($top=2)")]
    [InlineData("/odata/BeAuthors?$expand=Books($skip=1)")]
    [InlineData("/odata/BeAuthors?$expand=Publisher")]
    [InlineData("/odata/BeAuthors?$select=Name&$expand=Books")]
    [InlineData("/odata/BeAuthors")]
    public async Task Uncapped_TheFlagChangesNoResponseByteAndNoSqlStatement(string url)
        => Assert.Equal(await ProbeAsync(null, false, url), await ProbeAsync(null, true, url));

    /// <summary>
    /// And with a ceiling in force, the flag changes nothing for every shape OUTSIDE the truly-bare
    /// subset — which is the fail-closed matrix restated as a byte-for-byte equality rather than a
    /// status-code assertion.
    /// </summary>
    [Theory]
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Books($expand=Chapters)")]
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Books($count=true)")]
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Books($top=2)")]
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Books($skip=1)")]
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Books($select=Title)")]
    [InlineData("/odata/BeAuthors?$filter=Id eq 1&$expand=Publisher")]
    [InlineData("/odata/BeAuthors?$filter=Id eq 2&$expand=Books")]
    public async Task Capped_TheFlagChangesNothingOutsideTheTrulyBareSubset(string url)
        => Assert.Equal(await ProbeAsync(3, false, url), await ProbeAsync(3, true, url));

    /// <summary>
    /// The one shape that MUST differ. Without this the two theories above would be satisfied by a
    /// build in which the feature does nothing at all — the classic vacuous-inertness suite.
    /// </summary>
    [Fact]
    public async Task Capped_TheTrulyBareSubsetIsExactlyWhereTheFlagChangesTheAnswer()
    {
        const string url = "/odata/BeAuthors?$filter=Id eq 1&$expand=Books";
        string off = await ProbeAsync(3, false, url);
        string on = await ProbeAsync(3, true, url);

        Assert.NotEqual(off, on);
        Assert.StartsWith("400", off, StringComparison.Ordinal);
        Assert.StartsWith("200", on, StringComparison.Ordinal);
        Assert.Contains("Books@odata.nextLink", on, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>$metadata</c> is untouched by the knob in both directions. The continuation route is a
    /// routing-table fact, not an EDM one — it adds no entity set, no bound operation, and no
    /// annotation — so a client's model must be byte-identical whether or not paging is on.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public async Task Metadata_IsByteIdenticalWithTheFlagOnAndOff(int? cap)
        => Assert.Equal(
            await ProbeAsync(cap, false, "/odata/$metadata"),
            await ProbeAsync(cap, true, "/odata/$metadata"));
}
