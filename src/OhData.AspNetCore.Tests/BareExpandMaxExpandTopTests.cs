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
        GetQueryable = _ => Task.FromResult(db.Authors.AsQueryable());
        HasMany(x => x.Books); // delegate-less → pushable, including its own Chapters chain
        HasOptional(x => x.Publisher!); // delegate-less single-valued nav (nullable FK)
    }
}

internal static class BareExpandSqliteHarness
{
    // Author 1 has 5 books (Id 1..5); Book 1 additionally has 2 chapters, so a $expand=Books($expand=
    // Chapters) exercises the "bare children" shape with a non-trivial child graph.
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection, SqlCaptureSink? sink, Action<EntitySetDefaults>? defaults = null)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                if (defaults is not null) b.WithDefaults(defaults);
                b.AddEntitySetProfile<BeAuthorProfile>();
            },
            configureServices: services =>
            {
                if (sink is not null) services.AddSingleton(sink);
                services.AddDbContext<BareExpandDbContext>(o =>
                {
                    o.UseSqlite(connection);
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
    public async Task ExplicitTop_StillWins_OverLeafCeiling_NotBoundedByDefault()
    {
        // An explicit nested $top above the DEFAULT ceiling bound is caught by the PRE-EXISTING
        // ValidateNestedTopCeiling check (E1 in MaxExpandTopTests.cs), not the new default-leaf bound —
        // confirms $top still "wins" (defaultLeafBound is null whenever Top is not null) and the request
        // still 400s via the expected, unrelated pre-query rejection, not a different code path.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/BeAuthors?$expand=Books($top=4)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
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
