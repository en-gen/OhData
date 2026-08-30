using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
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
/// delegate.
/// <para>
/// #421 CHANGED WHAT THIS FIXTURE PROVES. It was authored to demonstrate a hole a per-profile
/// registration predicate would supposedly leave: the sibling made the old union-based
/// <c>ResolveNavTreatment</c> call return <c>Blank</c> for BOTH sets, and a per-profile predicate
/// "would still register a continuation route on <c>BeAuthors</c> serving those rows raw". The second
/// half was measured false — <c>/BeAuthors?$expand=Books</c> serves those rows raw ANYWAY, because the
/// root read path resolves from the URL-named set alone (Model B, FROZEN on #293). So the sibling was
/// suppressing a route and a link for rows the very same request already served. It now suppresses
/// neither. What it still proves is the invariant that always mattered: the SIBLING's own
/// <c>Books</c> is delegate-backed on its own set, so IT gets no continuation route.
/// </para>
/// </summary>
public sealed class BeDelegatedAuthorProfile : EntitySetProfile<int, BeAuthor>
{
    private readonly BeDelegateInvocationCounter _counter;

    public BeDelegatedAuthorProfile(BareExpandDbContext db, BeDelegateInvocationCounter counter)
        : base(x => x.Id)
    {
        _counter = counter;
        EntitySetName = "BeDelegatedAuthors";
        ExpandEnabled = true;
        FilterEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Authors.AsQueryable());
        HasMany(x => x.Books, (key, _) =>
        {
            _counter.Record();
            return Task.FromResult<IEnumerable<BeBook>>(db.Books.Where(b => b.AuthorId == key).ToList());
        });
    }
}

/// <summary>
/// #484: how many times <see cref="BeDelegatedAuthorProfile"/>'s <c>Books</c> delegate ran — on
/// <b>this host</b>. Registered as a singleton by <c>BareExpandSqliteHarness</c>, so every
/// <c>TestFixture</c> gets its own and no two tests can see each other's count.
/// </summary>
/// <remarks>
/// <para>
/// It used to be a process-wide <c>static int</c> that each test set to <c>0</c> and then asserted
/// against. Two classes did that — <c>BareExpandContinuationDelegateSafetyTests</c> and
/// <c>ExpandPagingStartupDiagnosticTests</c> — and neither carries a <c>[Collection]</c>, so xUnit
/// put them in separate collections and ran them <b>in parallel</b>. <c>Interlocked.Increment</c>
/// made each increment atomic and did nothing at all for the reset-then-assert window: class A's
/// reset could land while class B sat between its own reset and its assertion. It failed three times
/// in solution-wide runs (where seven projects compete for cores) and never in isolation.
/// </para>
/// <para>
/// A per-fixture instance is preferred over serialising the two classes into one <c>[Collection]</c>
/// because it removes the shared mutable state rather than scheduling around it, and costs no
/// parallelism. There is no <c>Reset</c> deliberately: a counter that cannot be reset cannot be
/// reset at the wrong moment, and each host starts at zero anyway. Assertions are therefore
/// cumulative within a test, which is what they already were between their reset and their
/// assertion.
/// </para>
/// <para>
/// Why this is worth fixing rather than re-running: #384's intermittent <c>ConcurrencyTests</c>
/// failure was written off as a flaky test and turned out to be #426, a live production race.
/// </para>
/// </remarks>
public sealed class BeDelegateInvocationCounter
{
    private int _invocations;

    /// <summary>Invocations recorded on this host so far.</summary>
    internal int Invocations => Volatile.Read(ref _invocations);

    internal void Record() => Interlocked.Increment(ref _invocations);
}

public sealed class BareExpandContinuationDelegateSafetyTests
{
    /// <summary>
    /// STRUCTURAL, and this is the invariant that actually carries the delegate safety: no
    /// continuation route exists for a navigation THIS profile routes through a delegate. Its own
    /// candidate set puts the route in <c>DB</c>, so <c>ResolveNavTreatment</c> answers
    /// <c>RunDelegate</c> and the predicate rejects it. Read off the live route table rather than off
    /// the predicate, so it fails if registration ever stops consulting <c>ResolveNavTreatment</c>.
    /// <para>
    /// The <c>Single</c> is load-bearing: <c>BeDelegatedAuthors({key})/Books</c> is already claimed by
    /// the delegate-backed navigation route, so a continuation route registered over it would be a
    /// duplicate <c>(template, GET)</c> pair — an ambiguous-match failure at request time. Counting
    /// endpoints catches that where a <c>Contains</c> could not.
    /// </para>
    /// <para>
    /// WHAT THIS TEST USED TO ASSERT (#421). The opposite of its first assertion: that
    /// <c>BeAuthors({key})/Books</c> must NOT exist, because the sibling's delegate made the old
    /// union-based treatment <c>Blank</c> for both sets. It passed for the wrong reason — the rows it
    /// was protecting are served raw by <c>/BeAuthors?$expand=Books</c> regardless, so the withheld
    /// route protected nothing and only removed the paging escape hatch.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NoContinuationRouteExists_ForANavigationTHISProfileDelegates()
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

        // The sibling declares Books WITH a delegate, so on ITS OWN set the treatment is RunDelegate:
        // exactly one endpoint on that template, and it is the delegate-backed navigation route.
        Assert.Single(patterns, p => string.Equals(p, "/odata/BeDelegatedAuthors({key})/Books", StringComparison.Ordinal));

        // And BeAuthors, which declares the same navigation delegate-LESS, DOES get its continuation
        // route — the sibling's declaration does not govern BeAuthors' navigation (#293 Model B).
        Assert.Contains(patterns, p => p.Contains("BeAuthors({key})/Books", StringComparison.Ordinal));

        // The bound: the delegate really is what answers on the sibling's template. #484: the count
        // is this HOST's, so no reset is needed and none can race another class's.
        BeDelegateInvocationCounter counter = fx.App.Services.GetRequiredService<BeDelegateInvocationCounter>();
        JsonElement viaDelegate = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BeDelegatedAuthors(1)/Books");
        Assert.Equal(5, viaDelegate.GetProperty("value").GetArrayLength());
        Assert.Equal(1, counter.Invocations);
    }

    /// <summary>
    /// BEHAVIOURAL, and the whole of #421: with the sibling present, the delegate-less set pages its
    /// OWN raw rows — <c>200</c> + <c>Books@odata.nextLink</c>, and the continuation serves the rest.
    /// The sibling's delegate is never invoked, on either hop.
    /// <para>
    /// WHAT THIS TEST ASSERTED BEFORE, AND WHY IT WAS THE WRONG THING. It asserted the exact opposite
    /// — <c>400</c>, no link, and a <c>404</c> on the continuation URL — under the heading "the shape
    /// keeps its stage-two 400". Its own remarks already recorded the measurement that refutes the
    /// premise: <c>ApplyCollectionPipelineAsync</c> calls <c>ExpandLevelAsync</c> with
    /// <c>new[] { requestSource }</c>, the URL-named profile ALONE, so at the root
    /// <c>ResolveNavTreatment</c> never sees the sibling and answers <c>ServeRaw</c>. The stage-2
    /// <c>400</c> it was pinning is itself proof the rows were materialized and counted — a blanked
    /// array would have been empty and passed under the ceiling. So the test was pinning a
    /// continuation route withheld from rows the SAME request served raw: no protection, and the
    /// paging escape hatch gone, on a navigation <c>BeAuthorProfile</c> itself declared delegate-less.
    /// </para>
    /// <para>
    /// The delegate safety is unchanged and is asserted here too, in the only place it ever lived:
    /// the sibling's own <c>Books</c> is delegate-backed on the sibling's own set, so the sibling
    /// answers from its delegate and nothing on <c>BeAuthors</c> reaches it. Nothing crosses an
    /// entity-set boundary — the continuation route reads <c>BeAuthorProfile</c>'s own
    /// <c>GetQueryable</c>, under <c>BeAuthors</c>' own authorization.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WithASiblingDelegate_TheDeclaringSetPagesItsOwnRawRows_AndTheSiblingIsUntouched()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap: 3, pagingEnabled: true,
            extraProfiles: b => b.AddEntitySetProfile<BeDelegatedAuthorProfile>());

        BeDelegateInvocationCounter counter = fx.App.Services.GetRequiredService<BeDelegateInvocationCounter>();
        JsonElement page1 = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeAuthors?$filter=Id eq 1&$expand=Books");

        JsonElement author = page1.GetProperty("value")[0];
        Assert.Equal(3, author.GetProperty("Books").GetArrayLength());
        string link = author.GetProperty("Books@odata.nextLink").GetString()!;
        Assert.Contains("/BeAuthors(1)/Books?$skip=3", link, StringComparison.Ordinal);

        JsonElement rest = await fx.Client.GetFromJsonAsync<JsonElement>(new Uri(link).PathAndQuery);
        Assert.Equal(2, rest.GetProperty("value").GetArrayLength());
        Assert.False(rest.TryGetProperty("@odata.nextLink", out _));

        // Neither hop touched the sibling's delegate: BeAuthors is served entirely by its own profile.
        Assert.Equal(0, counter.Invocations);

        // And the sibling's own path is still the only thing serving the sibling's rows.
        JsonElement viaDelegate = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BeDelegatedAuthors(1)/Books");
        Assert.Equal(5, viaDelegate.GetProperty("value").GetArrayLength());
        Assert.Equal(1, counter.Invocations);
    }

    /// <summary>
    /// THE DELEGATE-SAFETY BOUND on #421, stated as an equality rather than an absence: the rows the
    /// continuation route serves are a strict SUBSET of the rows the root <c>$expand</c> beside it
    /// already serves raw to the same caller. Page 1's three books plus the continuation's two are
    /// exactly the five the uncapped root expand returns, in the same order — so the route opened no
    /// new surface, it only windowed an existing one.
    /// <para>
    /// This is the assertion the withheld route was supposedly buying, made directly instead of
    /// inferred from a <c>404</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheContinuationServesNoRowTheRootExpandDoesNotAlreadyServeRaw()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        // The same registration, uncapped: what /BeAuthors?$expand=Books serves raw with the sibling
        // present and no ceiling at all.
        await using (TestFixture uncapped = await BareExpandContinuation.BuildAsync(
            connection, cap: null, pagingEnabled: false,
            extraProfiles: b => b.AddEntitySetProfile<BeDelegatedAuthorProfile>()))
        {
            JsonElement raw = await uncapped.Client.GetFromJsonAsync<JsonElement>(
                "/odata/BeAuthors?$filter=Id eq 1&$expand=Books");
            List<int> rawIds = raw.GetProperty("value")[0].GetProperty("Books").EnumerateArray()
                .Select(b => b.GetProperty("Id").GetInt32()).ToList();
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, rawIds);
        }

        using var connection2 = new SqliteConnection("Data Source=:memory:");
        connection2.Open();
        await using TestFixture capped = await BareExpandContinuation.BuildAsync(
            connection2, cap: 3, pagingEnabled: true,
            extraProfiles: b => b.AddEntitySetProfile<BeDelegatedAuthorProfile>());

        JsonElement page1 = await capped.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeAuthors?$filter=Id eq 1&$expand=Books");
        JsonElement author = page1.GetProperty("value")[0];
        var walked = author.GetProperty("Books").EnumerateArray()
            .Select(b => b.GetProperty("Id").GetInt32()).ToList();

        string link = author.GetProperty("Books@odata.nextLink").GetString()!;
        JsonElement rest = await capped.Client.GetFromJsonAsync<JsonElement>(new Uri(link).PathAndQuery);
        walked.AddRange(rest.GetProperty("value").EnumerateArray().Select(b => b.GetProperty("Id").GetInt32()));

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, walked);
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

    // ── #415: the ROOT level's candidate set is the URL-named set ALONE, and that is CORRECT ──────
    //
    // #415 proposed that ApplyCollectionPipelineAsync's depth-1 `ExpandLevelAsync(..., new[] {
    // requestSource }, ...)` was DRIFT from the #292 union used at depth >= 2, and should be changed
    // to ResolveRequestSourcesForEdmType. It is not drift. The FROZEN Model B implementation spec
    // (owner decision 2026-07-26, recorded on issue #293) settles it in as many words:
    //
    //     "Root (depth 1): KEEP as-is — already reads only the URL-named set (correct under Model B)."
    //
    // and lists among the tests that must STAY GREEN:
    //
    //     "the entire $levels suite (LvNodes/LvShallowNodes/LvSecureNodes resolve from the URL-named
    //      set only ...)"
    //
    // The union at depth >= 2 is AMBIGUITY RESOLUTION, not delegate contagion. It exists because when
    // 2+ entity sets expose a navigation's target type the EDM has NO binding to say which set the
    // path resolves to (measured cases (B)/(C) in ResolveRequestSourcesForEdmType's remarks), so the
    // framework cannot tell whose declaration governs and fails closed. At the root there is nothing
    // to disambiguate: the URL names the entity set. Model B's declaring-set authority then applies
    // in full — "a delegate on a sibling/derived set never retroactively poisons a nav that ANOTHER
    // set legitimately serves raw".
    //
    // MEASURED blast radius of making the root use the union anyway (the #415 proposal, implemented
    // and reverted): 32 of 2125 tests in this project fail. The bulk is the $levels suite the frozen
    // spec named — LevelsWithOptionsPushdownSqliteTests, BareLevelsCeilingTests,
    // LevelsSkipTopPushdownTests, LevelsWithCountCeilingTests — whose harness ALWAYS registers
    // LvSecureNodeProfile (delegate-backed Children) beside LvNodeProfile (delegate-less Children)
    // over the same LvNode EDM type. Every one of those tests stops exercising anything, because
    // /LvNodes?$expand=Children($levels=N) blanks. It also deletes the dual-exposure pattern outright
    // (MultiSetDelegateSafetyExpandTests.RootExpand_DualExposure_DelegatelessServesRaw_DelegateBackedRuns):
    // a public unfiltered set would be permanently blanked by the mere existence of a secured sibling.
    //
    // The three tests below pin that root semantics directly, on #415's own reproduction, so the
    // proposal cannot be re-applied silently.

    /// <summary>
    /// #415, half 1. With the delegate-backed sibling registered, the delegate-LESS set still serves
    /// its OWN raw rows at the root — declaring-set authority — and the sibling's delegate is not run.
    /// </summary>
    [Fact]
    public async Task RootExpand_WithASiblingDelegate_StillServesTheDeclaringSetsOwnRawRows()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap: null, pagingEnabled: false,
            extraProfiles: b => b.AddEntitySetProfile<BeDelegatedAuthorProfile>());

        BeDelegateInvocationCounter counter = fx.App.Services.GetRequiredService<BeDelegateInvocationCounter>();
        JsonElement root = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeAuthors?$filter=Id eq 1&$expand=Books");

        // BeAuthors declares Books delegate-less; its own declaration governs, so the five raw books
        // are served. Under #415's proposal this array would be empty.
        Assert.Equal(5, root.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        Assert.Equal(0, counter.Invocations);
    }

    /// <summary>
    /// #415, half 2 — the other side of the dual-exposure pattern, in the same registration: the
    /// sibling's OWN root $expand over the SAME EDM type routes through ITS OWN delegate. This is what
    /// makes half 1 "each set served by its own declaration" rather than "the delegate is ignored".
    /// </summary>
    [Fact]
    public async Task RootExpand_TheDelegateBackedSiblingRunsItsOwnDelegate()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandContinuation.BuildAsync(
            connection, cap: null, pagingEnabled: false,
            extraProfiles: b => b.AddEntitySetProfile<BeDelegatedAuthorProfile>());

        BeDelegateInvocationCounter counter = fx.App.Services.GetRequiredService<BeDelegateInvocationCounter>();
        JsonElement root = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BeDelegatedAuthors?$filter=Id eq 1&$expand=Books");

        Assert.Equal(5, root.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        Assert.Equal(1, counter.Invocations);
    }

    /// <summary>
    /// #415, half 3. Adding the sibling changes the delegate-less set's root $expand response NOT AT
    /// ALL — byte-for-byte. This is the control that gives halves 1 and 2 their meaning: it pins that
    /// registering a second profile over the same EDM type is invisible to the first one's root read
    /// path, which is precisely the property the union proposal would remove.
    /// </summary>
    [Fact]
    public async Task RootExpand_AddingTheSiblingIsByteIdenticalForTheDelegatelessSet()
    {
        const string Url = "/odata/BeAuthors?$filter=Id eq 1&$expand=Books";

        using var soloConnection = new SqliteConnection("Data Source=:memory:");
        soloConnection.Open();
        await using TestFixture solo = await BareExpandContinuation.BuildAsync(
            soloConnection, cap: null, pagingEnabled: false);
        string aloneBody = await (await solo.Client.GetAsync(Url)).Content.ReadAsStringAsync();

        using var pairConnection = new SqliteConnection("Data Source=:memory:");
        pairConnection.Open();
        await using TestFixture pair = await BareExpandContinuation.BuildAsync(
            pairConnection, cap: null, pagingEnabled: false,
            extraProfiles: b => b.AddEntitySetProfile<BeDelegatedAuthorProfile>());
        string withSiblingBody = await (await pair.Client.GetAsync(Url)).Content.ReadAsStringAsync();

        Assert.Equal(aloneBody, withSiblingBody);
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
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);

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
    /// <summary>
    /// #359: this route's own inline sigil loop compared with <c>StringComparison.Ordinal</c>, so
    /// <c>$SKIP</c> and <c>$FORMAT</c> were <c>400</c>. Sharing the framework-wide matcher makes the
    /// comparison <c>OrdinalIgnoreCase</c> -- alignment with <c>Microsoft.AspNetCore.OData</c>, which
    /// lowercases an option name before matching whenever the URI resolver enables
    /// case-insensitivity (the default), and with every other read route. This is a real behaviour
    /// change on this route and is pinned here rather than described as "unchanged".
    /// </summary>
    [Theory]
    [InlineData("$SKIP=3")]
    [InlineData("$Skip=3")]
    [InlineData("$skip=3&$FORMAT=json")]
    public async Task MixedCaseSkipAndFormat_AreHonoured_NotRejected(string query)
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync($"/odata/BeAuthors(1)/Books?{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("value").GetArrayLength());
    }

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
    /// #421, on a BROWNFIELD fixture — neither this stage nor stage 2 authored it. <c>LvNodes</c>
    /// declares <c>Children</c> delegate-less; <c>LvSecureNodeProfile</c> is a sibling entity set over
    /// the SAME <c>LvNode</c> EDM entity type that declares <c>Children</c> WITH a delegate. The bare
    /// <c>$expand=Children</c> on <c>LvNodes</c> pages, because <c>LvNodes</c>' own declaration
    /// governs its own navigation, and the secure sibling keeps running its own delegate on its own
    /// set — the dual-exposure pattern Model B exists to support, now paging on the public half.
    /// <para>
    /// WHAT THIS TEST ASSERTED BEFORE (#421): a <c>400</c>, no link, and no
    /// <c>LvNodes({key})/Children</c> route, "because a sibling profile delegates the nav". It was
    /// pinning the union, and the same registration's <c>/LvNodes?$expand=Children</c> serves those
    /// children RAW at every ceiling — this suite's own <c>$levels</c> tests depend on exactly that,
    /// which is why the FROZEN Model B spec lists them under "tests that STAY GREEN". So the withheld
    /// route was protecting rows that were never protected.
    /// </para>
    /// <para>
    /// Its counterpart, that a nav with no delegating sibling pages at the same ceiling, is
    /// <see cref="BareExpandContinuationWalkTests.FiveChildren_CeilingThree_WalksToExhaustion_InOrder"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnThisBrownfieldFixture_TheDelegateLessSetPages_AndItsDelegatingSiblingDoesNot()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new LevelsDelegateCounter();
        await using TestFixture fx = await LevelsOptionsSqliteHarness.BuildAsync(
            connection, counter, sink: null,
            defaults: d => { d.MaxExpandTop = 1; d.ExpandPagingEnabled = true; });

        // Root(1) has two children (A, B), so a ceiling of 1 puts it exactly one row over.
        JsonElement root = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/LvNodes?$filter=parentId eq null&$expand=Children");
        JsonElement node = root.GetProperty("value")[0];
        Assert.Equal(1, node.GetProperty("Children").GetArrayLength());
        string link = node.GetProperty("Children@odata.nextLink").GetString()!;
        Assert.Contains("/LvNodes(1)/Children?$skip=1", link, StringComparison.Ordinal);

        JsonElement rest = await fx.Client.GetFromJsonAsync<JsonElement>(new Uri(link).PathAndQuery);
        Assert.Equal(1, rest.GetProperty("value").GetArrayLength());

        // Not one hop reached the secure sibling's delegate.
        Assert.Equal(0, counter.ChildCalls);

        var patterns = fx.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .ToList();
        Assert.Contains(patterns, p => p.Contains("LvNodes({key})/Children", StringComparison.Ordinal));

        // THE INVARIANT: the secure set delegates Children on its OWN set, so it gets exactly one
        // endpoint on that template — its delegate-backed navigation route, not a raw continuation.
        Assert.Single(patterns, p => string.Equals(p, "/odata/LvSecureNodes({key})/Children", StringComparison.Ordinal));
        JsonElement secure = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/LvSecureNodes?$filter=parentId eq null&$expand=Children");
        Assert.Equal(2, secure.GetProperty("value")[0].GetProperty("Children").GetArrayLength());
        Assert.True(counter.ChildCalls > 0);
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
