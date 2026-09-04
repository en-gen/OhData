using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #313: a BARE pushed $expand (no nested $count/$top of its own) used to be UNBOUNDED by MaxExpandTop —
// the exact opposite of the ceiling's whole purpose, and the single MOST COMMON $expand shape. Fixed by:
//   - a leaf (no nested $expand children): ApplyNavShape now composes a SQL Take(MaxExpandTop + 1) bound
//     for this shape too (mirrors the existing $count-deferred bound), so it is SQL-windowed, not
//     load-all-then-trim; ShapePushedExpandsInJson enforces the ceiling on the (now-bounded) result.
//   - a level WITH children (nested $expand, no $skip/$top/$count of its own): this shape cannot be
//     SQL-windowed at all (the APPLY/LATERAL constraint #298/#304 document), so it is still materialized
//     in full, but now gets a pure JSON-side ceiling check before recursing into the children.
//   - $skip-only (no $top): the leaf SQL bound covers this for free, closing a pre-existing unbounded
//     surface (a huge $skip with no $top used to fetch and return an unbounded remainder).
//   - single-valued navigations are unaffected (at most one related entity — no bound needed).
//
// Dedicated fixture (BeAuthor/BeBook/BeChapter/BePublisher) rather than reusing MultiLevelSqliteHarness:
// these tests need an author with MORE than a couple of books to exercise "under/at/over a low ceiling"
// cleanly, and a single-valued reference nav to prove that shape is untouched.
public sealed class BeAuthor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? PublisherId { get; set; }
    public BePublisher? Publisher { get; set; }
    public List<BeBook> Books { get; set; } = new();
}

public sealed class BeBook
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public string Title { get; set; } = "";
    public List<BeChapter> Chapters { get; set; } = new();
}

public sealed class BeChapter
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Heading { get; set; } = "";
}

public sealed class BePublisher
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class BareExpandDbContext : DbContext
{
    public BareExpandDbContext(DbContextOptions<BareExpandDbContext> options) : base(options) { }

    public DbSet<BeAuthor> Authors => Set<BeAuthor>();
    public DbSet<BeBook> Books => Set<BeBook>();
    public DbSet<BeChapter> Chapters => Set<BeChapter>();
    public DbSet<BePublisher> Publishers => Set<BePublisher>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BeAuthor>().HasMany(a => a.Books).WithOne().HasForeignKey(x => x.AuthorId);
        b.Entity<BeAuthor>().HasOne(a => a.Publisher).WithMany().HasForeignKey(a => a.PublisherId);
        b.Entity<BeBook>().HasMany(x => x.Chapters).WithOne().HasForeignKey(x => x.BookId);
    }
}

public sealed class BeAuthorProfile : EntitySetProfile<int, BeAuthor>
{
    public BeAuthorProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BeAuthors";
        ExpandEnabled = true;
        OrderByEnabled = true;
        FilterEnabled = true;
        // #313 stage 5: a root $select is what STRIPS THE PARENT KEY from the payload before the
        // shaping pass runs (G6), and that is the case the continuation link's key must survive. One
        // capability flag added to a stage-2 fixture, not a new fixture built around the behaviour.
        SelectEnabled = true;
        GetQueryable = _ => OhDataResult.Success(db.Authors.AsQueryable());
        HasMany(x => x.Books); // delegate-less → pushable, including its own Chapters chain
        HasOptional(x => x.Publisher!); // delegate-less single-valued nav (nullable FK)
    }
}

internal static class BareExpandSqliteHarness
{
    // Author 1 has 5 books (Id 1..5); Book 1 additionally has 2 chapters, so a $expand=Books($expand=
    // Chapters) exercises the "bare children" shape with a non-trivial child graph.
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection,
        SqlCaptureSink? sink,
        Action<EntitySetDefaults>? defaults = null,
        // #313 stage 3: lets a caller add its own services (an ILoggerProvider, for the startup
        // diagnostic) without a second copy of this harness. Additive and optional — every existing
        // call site is unaffected.
        Action<IServiceCollection>? configureExtraServices = null,
        // #313 stage 5: lets a caller seed EXTRA rows (authors with 0/1/7 books, a second author
        // sharing a name) without a second copy of this fixture. The stage-2 seed below is untouched,
        // so every existing call site sees exactly the data it did before; the continuation tests
        // isolate their own authors with a root $filter. Additive and optional, same as above.
        Action<BareExpandDbContext>? seedExtra = null,
        // #313 stage 5: a second profile registered over the SAME BeAuthor EDM entity type, for the
        // delegate-safety partition (a sibling that declares Books WITH a delegate makes the
        // treatment Blank for BOTH sets).
        Action<OhDataBuilder>? configureExtraProfiles = null,
        // #412: render parameter VALUES into the captured command text. The nested page size reaches
        // SQL as a query parameter (deliberately — see ContinuationKeyBox's remarks on the plan
        // cache), so without this every LIMIT logs as `@p2='?'` and the bound is unassertable. Off by
        // default, so every existing call site captures exactly the text it did before.
        bool logSqlParameterValues = false)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                if (defaults is not null) b.WithDefaults(defaults);
                b.AddEntitySetProfile<BeAuthorProfile>();
                configureExtraProfiles?.Invoke(b);
            },
            configureServices: services =>
            {
                configureExtraServices?.Invoke(services);

                // #484: one counter per HOST, so the delegate-safety assertions never share mutable
                // state across test classes that xUnit runs in parallel. Registered unconditionally
                // rather than alongside `configureExtraProfiles`, because the profile that consumes
                // it is added through that hook and a missing registration would be a DI failure at
                // request time rather than a compile error. Costs one object per fixture.
                services.AddSingleton<BeDelegateInvocationCounter>();

                if (sink is not null) services.AddSingleton(sink);
                services.AddDbContext<BareExpandDbContext>(o =>
                {
                    o.UseSqlite(connection);
                    if (logSqlParameterValues) o.EnableSensitiveDataLogging();
                    if (sink is not null)
                    {
                        o.LogTo(
                            message => sink.Add(message),
                            (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted);
                    }
                });
            });

        using IServiceScope scope = fx.App.Services.CreateScope();
        BareExpandDbContext db = scope.ServiceProvider.GetRequiredService<BareExpandDbContext>();
        db.Database.EnsureCreated();

        db.Publishers.Add(new BePublisher { Id = 100, Name = "Pub1" });
        db.Authors.Add(new BeAuthor { Id = 1, Name = "Ann", PublisherId = 100 });
        db.Books.AddRange(
            new BeBook { Id = 1, AuthorId = 1, Title = "Bk1" },
            new BeBook { Id = 2, AuthorId = 1, Title = "Bk2" },
            new BeBook { Id = 3, AuthorId = 1, Title = "Bk3" },
            new BeBook { Id = 4, AuthorId = 1, Title = "Bk4" },
            new BeBook { Id = 5, AuthorId = 1, Title = "Bk5" });
        db.Chapters.AddRange(
            new BeChapter { Id = 1, BookId = 1, Heading = "Intro" },
            new BeChapter { Id = 2, BookId = 1, Heading = "Outro" });

        db.SaveChanges();

        if (seedExtra is not null)
        {
            seedExtra(db);
            db.SaveChanges();
        }
        return fx;
    }

    public static string LastSelectAgainst(SqlCaptureSink sink, string table) => sink.Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal) && s.Contains($"\"{table}\"", StringComparison.Ordinal))
        .Last();
}

// ── Bare LEAF (no nested $expand children of its own) ────────────────────────────────────────────
public sealed class BareLeafCeilingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await BareExpandSqliteHarness.BuildAsync(_connection, sink: null, defaults: d => d.MaxExpandTop = 3);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task BareLeaf_UnderCeiling_Returns200_AllRows()
    {
        // A fresh, higher-ceiling fixture so 5 books is comfortably under.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink: null, defaults: d => d.MaxExpandTop = 10);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors?$expand=Books");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement books = doc.RootElement.GetProperty("value")[0].GetProperty("Books");
        Assert.Equal(5, books.GetArrayLength());
    }

    [Fact]
    public async Task BareLeaf_AtCeiling_Returns200_AllRows()
    {
        // Ceiling exactly equal to the true count (5) — the boundary itself must not trip (arr.Count >
        // cap is a STRICT breach check).
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink: null, defaults: d => d.MaxExpandTop = 5);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors?$expand=Books");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement books = doc.RootElement.GetProperty("value")[0].GetProperty("Books");
        Assert.Equal(5, books.GetArrayLength());
    }

    [Fact]
    public async Task BareLeaf_OverCeiling_Returns400_ActionableCeilingMessage()
    {
        // Ceiling 3, Author 1 has 5 Books — the previously-unbounded shape now 400s.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Books");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Books", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 3", body);
        Assert.Contains("Narrow it with a nested $filter", body);
        // Never the raw provider exception text (could leak schema/SQL details).
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("SQLITE", body);
    }

    [Fact]
    public async Task BareLeaf_Capped_PushesARowBoundIntoSql()
    {
        // The whole point of the fix: the bare-leaf materialization is BOUNDED IN SQL (Take(cap + 1)),
        // not fetched in full then trimmed. Mirrors NestedCount_UnderCeiling_PushesARowBoundIntoSql.
        // MaxTop is ALSO uncapped here: the default MaxTop (1000) already composes an OUTER Skip/Take
        // over the root Authors query (needed to page the parent collection with an included child
        // collection, EF Core paginates via a ROW_NUMBER/LIMIT subquery on the PARENT alone) — that
        // outer bound would make this assertion pass regardless of #313, so it's removed to isolate the
        // proof to the NESTED Books bound specifically.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink, defaults: d => { d.MaxExpandTop = 10; d.MaxTop = null; });
        sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors?$orderby=id&$expand=Books");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = BareExpandSqliteHarness.LastSelectAgainst(sink, "Authors");
        Assert.Contains("\"Books\"", sql);
        Assert.True(
            sql.Contains("ROW_NUMBER()", StringComparison.Ordinal) || sql.Contains("LIMIT", StringComparison.Ordinal),
            $"the bare-leaf materialization must carry a SQL row bound; got:\n{sql}");
    }

    [Fact]
    public async Task Uncapped_BareLeaf_NoRowBoundInSql_AndReturnsAllRows()
    {
        // MaxExpandTop = null preserves the pre-#313 opt-out: no SQL Take is composed at all, and every
        // row comes back (the request never 400s regardless of collection size).
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink, defaults: d => { d.MaxExpandTop = null; d.MaxTop = null; });
        sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors?$orderby=id&$expand=Books");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement books = doc.RootElement.GetProperty("value")[0].GetProperty("Books");
        Assert.Equal(5, books.GetArrayLength());

        string sql = BareExpandSqliteHarness.LastSelectAgainst(sink, "Authors");
        Assert.Contains("\"Books\"", sql);
        Assert.DoesNotContain("ROW_NUMBER()", sql);
        Assert.DoesNotContain("LIMIT", sql);
    }

    [Fact]
    public async Task SkipOnlyLeaf_NoExplicitTop_OverCeiling_Returns400()
    {
        // #313's "$skip-only leaf" closed surface: no $top at all, just $skip — before the fix this
        // fetched Skip(1) with NO Take, an unbounded remainder. Ceiling 3, 5 books, $skip=1 leaves 4
        // remaining (Books 2-5) — still over the ceiling of 3.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Books($skip=1)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Books", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 3", body);
        Assert.Contains("Narrow it with a nested $filter", body);
    }

    [Fact]
    public async Task SkipOnlyLeaf_UnderCeiling_Returns200_CountsThePostSkipRemainder()
    {
        // Review fold-in (F3): the OVER-ceiling $skip case above cannot tell WHICH population the
        // ceiling counts, because 5 books breach a cap of 3 whether you count before or after the
        // $skip. This one can: $skip=3 leaves a remainder of 2, which is UNDER the cap of 3, while
        // the pre-skip collection (5) is over it. A regression to counting the pre-skip population
        // would 400 here — and would have passed the whole suite without this test, because the
        // bound is composed AFTER the Skip (`access.Skip(sk).Take(cap+1)`) so it is the remainder
        // that reaches EnsureWithinExpandCeiling.
        //
        // Deliberately also asserts the emitted UPPER row bound, which is what makes this a revert
        // detector rather than only a future-regression guard. Measured on both trees, since the
        // obvious assertion is wrong: a $skip-only leaf ALREADY composed the ROW_NUMBER window before
        // #313 (Skip alone needs it), so `Assert.Contains("ROW_NUMBER()")` passes with the production
        // change reverted. What #313 adds is the second half of the predicate — pre-#313
        // `WHERE 3 < "b0"."row"`, post-#313 `WHERE 3 < "b0"."row" AND "b0"."row" <= 7`
        // (the $skip of 3 plus the cap+1 probe of 4).
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink, defaults: d => { d.MaxExpandTop = 3; d.MaxTop = null; });

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors?$expand=Books($skip=3)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement books = doc.RootElement.GetProperty("value")[0].GetProperty("Books");
        Assert.Equal(2, books.GetArrayLength());
        Assert.Equal("Bk4", books[0].GetProperty("Title").GetString());
        Assert.Equal("Bk5", books[1].GetProperty("Title").GetString());

        string sql = BareExpandSqliteHarness.LastSelectAgainst(sink, "Authors");
        Assert.Contains("ROW_NUMBER()", sql);
        Assert.Contains("\"row\" <= 7", sql); // Skip(3).Take(cap + 1 = 4) — absent without #313
    }

    [Fact]
    public async Task ExplicitTopUnderCeiling_OverLargeCollection_WindowsToTop_NoCeilingBreach()
    {
        // Review fold-in (F2): ExplicitTop_StillWins_… below names the "$top wins over the default
        // bound" rule but only exercises $top ABOVE the cap, which the pre-existing
        // ValidateNestedTopCeiling rejects before ApplyNavShape ever runs. The case the rule is
        // actually about is $top = N <= cap over a collection that is itself over the cap: the
        // collection breaches, the request does not, and exactly N rows come back.
        //
        // Stated plainly so it is not mistaken for one: this is a GUARD, not a revert detector. The
        // shape is by construction identical on develop — `defaultLeafBound` is null whenever Top is
        // set, and the JSON ceiling arm requires `e.Top is null` — so it passes with the production
        // change reverted, and must. What it pins is that neither gate ever loses its `Top` clause:
        // drop either one and a client's own explicit, under-cap window starts 400ing because the
        // collection behind it is large.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Books($top=2)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement books = doc.RootElement.GetProperty("value")[0].GetProperty("Books");
        Assert.Equal(2, books.GetArrayLength()); // the client's $top, not the cap of 3 and not cap+1
        Assert.Equal("Bk1", books[0].GetProperty("Title").GetString());
        Assert.Equal("Bk2", books[1].GetProperty("Title").GetString());
        Assert.DoesNotContain("cannot be computed", body);
    }

    [Fact]
    public async Task ExplicitTop_StillWins_OverLeafCeiling_NotBoundedByDefault()
    {
        // An explicit nested $top above the DEFAULT ceiling bound is caught by the PRE-EXISTING
        // ValidateNestedTopCeiling check (E1 in MaxExpandTopTests.cs), not the new default-leaf bound —
        // confirms $top still "wins" (defaultLeafBound is null whenever Top is not null) and the request
        // still 400s via the expected, unrelated pre-query rejection, not a different code path.
        //
        // Asserting only "400 + InvalidQueryOption" would NOT test that premise: both rejections carry
        // that code, so such an assertion passes byte-identically whichever one fired, and could not
        // tell the guarded-against path from the intended one. The two are distinguished by their
        // messages, so pin both directions — the pre-query $top wording must be present, and
        // EnsureWithinExpandCeiling's post-materialization wording must be absent.
        //
        // Review fold-in (F2), recorded rather than papered over: this test passes with the
        // production change reverted, and cannot be made to fail — the rejection it asserts is
        // ValidateNestedTopCeiling's, which predates #313 entirely. Its job is to prove #313 did not
        // SHADOW that rejection with a different one, which is a real risk (both are 400
        // InvalidQueryOption, so a bare status assertion could not tell them apart) but is not the
        // "$top wins" case. That case is ExplicitTopUnderCeiling_OverLargeCollection_… above.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Books($top=4)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        // ValidateNestedTopCeiling's message, verbatim in shape: it names $top, its value and the cap.
        Assert.Contains("The value of '$top' (4) on the expanded navigation 'Books' exceeds the maximum allowed value (3).", body);
        // NOT the ceiling breach the new default-leaf bound would have raised.
        Assert.DoesNotContain("cannot be computed", body);
        Assert.DoesNotContain("Narrow it with a nested $filter", body);
    }

    // ── MEDIUM-4: the 200→400 flip covers EVERY nested-option combination except $count and $top ─────
    // The bare-leaf arm is gated `!hasChildren && e.Top is null && !e.Count`, so a nested $select,
    // $orderby or $filter that leaves the collection over the ceiling flips 200→400 just as the truly
    // bare shape does. Only $skip had coverage; these three are the rest of the real blast radius.

    [Fact]
    public async Task BareLeaf_WithNestedSelect_OverCeiling_Returns400()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Books($select=Title)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 3", body);
    }

    [Fact]
    public async Task BareLeaf_WithNestedOrderBy_OverCeiling_Returns400()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Books($orderby=Title desc)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 3", body);
    }

    [Fact]
    public async Task BareLeaf_WithNestedFilter_StillOverCeiling_Returns400()
    {
        // A nested $filter that does NOT narrow below the ceiling (all 5 books match) still breaches.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Books($filter=Id gt 0)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 3", body);
    }

    [Fact]
    public async Task BareLeaf_WithNarrowingNestedFilter_Returns200()
    {
        // The message's advice ("Narrow it with a nested $filter") must actually work: the SAME request
        // with a filter that brings the collection under the ceiling succeeds. Without this, the other
        // three above only prove the rejection is broad, not that it is escapable.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Books($filter=Id le 2)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement books = doc.RootElement.GetProperty("value")[0].GetProperty("Books");
        Assert.Equal(2, books.GetArrayLength());
    }
}

// ── MEDIUM-3: `MaxExpandTop = null` opts out of the STATUS CODE and of the wire ORDERING ────────────
// `paging` in ApplyNavShape now includes `defaultLeafBound is not null`, which appends the nav element's
// key as a final tiebreaker. So a capped registration returns nested collections in child-key order
// (deterministic), while `MaxExpandTop = null` composes no tiebreaker and leaves the order to the
// provider. That is a behavioral difference flipped by a config knob, not just a status-code one — it is
// documented in docs/query-options.md and pinned here from both directions.
public sealed class BareLeafOrderingOptOutTests
{
    [Fact]
    public async Task Capped_BareLeaf_AppendsChildKeyTiebreaker_AndReturnsKeyOrder()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink, defaults: d => { d.MaxExpandTop = 10; d.MaxTop = null; });
        sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors?$expand=Books");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The window function orders by the child key, and the outer ORDER BY carries it too.
        string sql = BareExpandSqliteHarness.LastSelectAgainst(sink, "Authors");
        Assert.Contains("ROW_NUMBER() OVER(PARTITION BY \"b\".\"AuthorId\" ORDER BY \"b\".\"Id\")", sql);
        Assert.Contains("\"b1\".\"Id\"", sql[sql.LastIndexOf("ORDER BY", StringComparison.Ordinal)..]);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement books = doc.RootElement.GetProperty("value")[0].GetProperty("Books");
        int[] ids = books.EnumerateArray().Select(b => b.GetProperty("Id").GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ids);
    }

    [Fact]
    public async Task Uncapped_BareLeaf_ComposesNoChildKeyTiebreaker()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink, defaults: d => { d.MaxExpandTop = null; d.MaxTop = null; });
        sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors?$expand=Books");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // No window function and no child-key term anywhere in the ORDER BY: the nested collection's
        // order is whatever the provider yields. Only the ROOT key orders the query.
        string sql = BareExpandSqliteHarness.LastSelectAgainst(sink, "Authors");
        Assert.DoesNotContain("ROW_NUMBER()", sql);
        string orderBy = sql[sql.LastIndexOf("ORDER BY", StringComparison.Ordinal)..];
        Assert.Contains("\"a\".\"Id\"", orderBy);
        Assert.DoesNotContain("\"b\".\"Id\"", orderBy);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(5, doc.RootElement.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
    }
}

// ── Bare CHILDREN (nested $expand, no $skip/$top/$count of its own) ──────────────────────────────
public sealed class BareChildrenCeilingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await BareExpandSqliteHarness.BuildAsync(_connection, sink: null, defaults: d => d.MaxExpandTop = 3);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task BareChildren_OverCeiling_Returns400()
    {
        // Books($expand=Chapters) — Books carries a nested $expand (Chapters) but no $count/$skip/$top
        // of its own, so it cannot be SQL-windowed (APPLY/LATERAL constraint); it's fully materialized
        // and now ceiling-checked in the JSON pass. Ceiling 3, 5 Books → 400.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Books($expand=Chapters)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Books", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 3", body);
        Assert.Contains("Narrow it with a nested $filter", body);
    }

    [Fact]
    public async Task BareChildren_UnderCeiling_Returns200_WithChildrenPresent()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection, sink: null, defaults: d => d.MaxExpandTop = 10);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors?$orderby=id&$expand=Books($expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement books = doc.RootElement.GetProperty("value")[0].GetProperty("Books");
        Assert.Equal(5, books.GetArrayLength());

        JsonElement book1 = books.EnumerateArray().Single(b => b.GetProperty("Title").GetString() == "Bk1");
        JsonElement chapters = book1.GetProperty("Chapters");
        Assert.Equal(2, chapters.GetArrayLength());
        Assert.Contains(chapters.EnumerateArray(), c => c.GetProperty("Heading").GetString() == "Intro");
        Assert.Contains(chapters.EnumerateArray(), c => c.GetProperty("Heading").GetString() == "Outro");
    }

    // Review fold-in (F7): the two shapes count DIFFERENT populations against the same ceiling, and
    // #313 is what makes the difference reachable from a bare request. The pair below is the whole
    // asymmetry in one place, measured rather than argued, and docs/query-options.md states it.
    //
    // Same author, same 5 books, same ceiling of 3, same $skip=4 — the only difference is whether the
    // level also carries a nested $expand:
    //   leaf     → ApplyNavShape composes Skip(4).Take(cap+1), so EnsureWithinExpandCeiling sees the
    //              POST-skip remainder of 1 → under the ceiling → 200.
    //   children → no SQL window is composable at all (APPLY/LATERAL, #298/#304), so the collection is
    //              materialized in full and WriteNestedWindowOnly checks the ceiling BEFORE applying
    //              the window — the PRE-skip 5 → over the ceiling → 400.
    // Neither is wrong on its own terms; both follow from where the check can physically run. It goes
    // away when #299 removes the unbounded materialization, and not before.
    //
    // Like the other negative guards here, this passes with the production change reverted, and must:
    // the children half is #304 behaviour that predates #313 entirely, and the leaf half returns the
    // same one row either way (develop composes the Skip without the cap+1 Take). #313's contribution
    // is only that a request can now reach the leaf arm without carrying $count or $top.
    [Fact]
    public async Task SkipPastCeiling_LeafCountsPostSkip_ButChildrenCountPreSkip()
    {
        HttpResponseMessage leaf = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Books($skip=4)");
        Assert.Equal(HttpStatusCode.OK, leaf.StatusCode);

        string leafBody = await leaf.Content.ReadAsStringAsync();
        using JsonDocument leafDoc = JsonDocument.Parse(leafBody);
        JsonElement leafBooks = leafDoc.RootElement.GetProperty("value")[0].GetProperty("Books");
        Assert.Equal(1, leafBooks.GetArrayLength());
        Assert.Equal("Bk5", leafBooks[0].GetProperty("Title").GetString());

        HttpResponseMessage children = await _fx.Client.GetAsync(
            "/odata/BeAuthors?$expand=Books($skip=4;$expand=Chapters)");
        Assert.Equal(HttpStatusCode.BadRequest, children.StatusCode);

        string childrenBody = await children.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", childrenBody);
        Assert.Contains("maximum of 3", childrenBody);
    }
}

// ── Single-valued navigations are unaffected (at most one related entity — no bound needed) ──────
public sealed class BareSingleValuedNavUnaffectedTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        // A deliberately tiny ceiling — if a single-valued nav were (wrongly) bounded like a collection,
        // this would matter; it must not, because at most one related entity ever exists.
        _fx = await BareExpandSqliteHarness.BuildAsync(_connection, sink: null, defaults: d => d.MaxExpandTop = 1);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task SingleValuedNav_BareExpand_UnderLowCeiling_Returns200_Unaffected()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Publisher");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Publisher\":", body);
        Assert.Contains("\"Pub1\"", body);
    }
}

// ── Bare $levels: the ceiling must not have a one-parameter bypass ───────────────────────────────
// `Nav($levels=1)` is a spec-equivalent restatement of a bare `$expand=Nav` — identical response bodies
// — so a ceiling that rejects one and not the other is not a ceiling. It was exactly that: a $levels
// expand takes ApplyNavShape's deferPagingToJson path (no SQL bound composed AT ALL, and maxExpandTop
// passed as null), and ShapeLevelsInJson's two arms both required an option the bare shape does not
// carry ($count / $skip>0 / $top), so NEITHER fired. Measured on the pre-fix tree with MaxExpandTop = 1
// (Root has 2 children): `$expand=Children` → 400, `($levels=1)` → 200 with both children,
// `($levels=2)` → 200 with the whole hierarchy, `($levels=2;$select=name)` → 200 likewise.
//
// Reuses the self-referential LvNode fixture from LevelsWithOptionsPushdownSqliteTests.cs ($levels is
// only legal on a self-referential navigation, which the BeAuthor/BeBook fixture above has none of):
//   Root(1) ├─ A(2) ├─ A1(4) ── A1a(8)   └─ B(3) └─ B1(7)
//           │       ├─ A2(5)
//           │       └─ A3(6)
// Root has 2 children, A has 3 — so a cap of 2 is under at level 1 and over at level 2, which is what
// proves the bound applies at EVERY level rather than only the first.
public sealed class BareLevelsCeilingTests
{
    private static Task<TestFixture> BuildAsync(SqliteConnection connection, int? cap) =>
        LevelsOptionsSqliteHarness.BuildAsync(
            connection, new LevelsDelegateCounter(), sink: null, defaults: d => d.MaxExpandTop = cap);

    private static string RootQuery(string expand) =>
        $"/odata/LvNodes?$filter=parentId eq null&$expand={expand}";

    [Fact]
    public async Task Levels1_OverCeiling_Returns400_ByteIdenticallyToBareExpand()
    {
        // THE bypass: `($levels=1)` must now behave exactly like the bare `$expand` it restates —
        // same status, same error code, same message text.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 1);

        HttpResponseMessage bare = await fx.Client.GetAsync(RootQuery("Children"));
        HttpResponseMessage levels1 = await fx.Client.GetAsync(RootQuery("Children($levels=1)"));

        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, levels1.StatusCode);
        Assert.Equal(await bare.Content.ReadAsStringAsync(), await levels1.Content.ReadAsStringAsync());

        string body = await levels1.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Children", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 1", body);
    }

    [Fact]
    public async Task Levels2_BreachAtDeeperLevelOnly_Returns400()
    {
        // Cap 2: level 1 (Root's 2 children) is exactly AT the ceiling and must not trip; level 2
        // (A's 3 children) breaches. Proves the check runs at every level, not just the first.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 2);

        // Level 1 alone is fine.
        HttpResponseMessage shallow = await fx.Client.GetAsync(RootQuery("Children($levels=1)"));
        Assert.Equal(HttpStatusCode.OK, shallow.StatusCode);

        HttpResponseMessage deep = await fx.Client.GetAsync(RootQuery("Children($levels=2)"));
        Assert.Equal(HttpStatusCode.BadRequest, deep.StatusCode);

        string body = await deep.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 2", body);
    }

    [Fact]
    public async Task Levels_WithNestedSelect_OverCeiling_Returns400()
    {
        // A nested $select alone reached ShapeLevelsInJson before the fix (NestedSelect is not null) yet
        // still fired neither arm, so it was unbounded too — same fourth shape, same rejection now.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 1);

        HttpResponseMessage resp = await fx.Client.GetAsync(RootQuery("Children($levels=2;$select=name)"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 1", body);
    }

    [Fact]
    public async Task Levels_UnderCeiling_Returns200_WholeHierarchyIntact()
    {
        // Under the ceiling the response must be byte-for-byte what it always was — the new arm only
        // inspects, it never windows or strips.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 10);

        HttpResponseMessage resp = await fx.Client.GetAsync(RootQuery("Children($levels=2)"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = LevelsOptionsSqliteHarness.Root(doc);
        JsonElement level1 = root.GetProperty("Children");
        Assert.Equal(new[] { "A", "B" }, LevelsOptionsSqliteHarness.Names(level1));
        Assert.Equal(new[] { "A1", "A2", "A3" }, LevelsOptionsSqliteHarness.Names(level1[0].GetProperty("Children")));
        Assert.Equal(new[] { "B1" }, LevelsOptionsSqliteHarness.Names(level1[1].GetProperty("Children")));
    }

    [Fact]
    public async Task Uncapped_Levels_StaysUnbounded()
    {
        // MaxExpandTop = null is still a full opt-out on this path: ShapePushedExpandsInJson's
        // needsLeafCeilingCheck requires `maxExpandTop is int`, so a bare $levels is not even walked.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: null);

        HttpResponseMessage resp = await fx.Client.GetAsync(RootQuery("Children($levels=2)"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement level1 = LevelsOptionsSqliteHarness.Root(doc).GetProperty("Children");
        Assert.Equal(new[] { "A", "B" }, LevelsOptionsSqliteHarness.Names(level1));
        Assert.Equal(3, level1[0].GetProperty("Children").GetArrayLength());
    }

    [Fact]
    public async Task Levels_SingleValuedSelfReference_Unaffected()
    {
        // A single-valued $levels recursion holds at most one related entity per level, so no ceiling
        // applies and the walk is still skipped outright (needsLeafCeilingCheck requires IsCollection).
        // LvRenamedNodes is the [JsonPropertyName]-renamed self-nav set: R-Root(1) → R-A(2) → R-A1(3),
        // one child per level, so even a cap of 1 must leave it untouched.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection, cap: 1);

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/LvRenamedNodes?$filter=parentId eq null&$expand=kids($levels=2)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("R-A1", body);
    }
}
