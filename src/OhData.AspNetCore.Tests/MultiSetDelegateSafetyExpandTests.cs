using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// Regression for the $expand delegate-safety bypass when the SAME CLR model type is exposed by 2+
// entity sets with DIVERGENT navigation-delegate config. The CHANGELOG [1.5.0] promises the
// delegate-safety invariant holds recursively: "a delegate-backed navigation is never EF-included at
// any depth ... its delegate is never bypassed." Before the union fix, the nested-expand pushdown
// resolved the child element type's delegate config from a SINGLE profile (FirstOrDefault by model
// type), so if a delegate-LESS set over the model registered before the delegate-BACKED one, the
// delegate was folded into an EF Include/ThenInclude JOIN and never invoked — raw rows JOIN-loaded,
// bypassing whatever filter/authorization the delegate applies. The fix unions the delegate-backed
// nav names across ALL profiles for the type, so the branch defers regardless of registration order.
// Uses the same EF Core Sqlite + SQL-capture harness as MultiLevelExpandPushdownSqliteTests.

// Book is exposed by TWO entity sets (Books, FeaturedBooks) with DIVERGENT Reviews config:
//   Books        → HasMany(Reviews)                 (delegate-LESS)
//   FeaturedBooks → HasMany(Reviews, getAll: ...)   (delegate-BACKED — filters/authorizes)
public sealed class MsBook
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string Title { get; set; } = "";
    public List<MsReview> Reviews { get; set; } = new();
    public List<MsTag> Tags { get; set; } = new();
}

public sealed class MsReview
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Body { get; set; } = "";
}

public sealed class MsTag
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Name { get; set; } = "";
}

// Parent set: Library.Books is delegate-less → pushable, so the nested Books($expand=Reviews) rides
// the pushdown path where the child-type delegate decision is made.
public sealed class MsLibrary
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<MsBook> Books { get; set; } = new();
}

// A DIFFERENT model type that also has a nav literally named "Reviews", exposed by its own
// delegate-backed set — the control that the union stays scoped to the SAME CLR type (never widening
// the deferral of an unrelated same-named nav on a different type).
public sealed class MsMagazine
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public string Title { get; set; } = "";
    public List<MsReview> Reviews { get; set; } = new();
}

public sealed class MultiSetDbContext : DbContext
{
    public MultiSetDbContext(DbContextOptions<MultiSetDbContext> options) : base(options) { }

    public DbSet<MsLibrary> Libraries => Set<MsLibrary>();
    public DbSet<MsBook> Books => Set<MsBook>();
    public DbSet<MsReview> Reviews => Set<MsReview>();
    public DbSet<MsTag> Tags => Set<MsTag>();
    public DbSet<MsMagazine> Magazines => Set<MsMagazine>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MsLibrary>().HasMany(l => l.Books).WithOne().HasForeignKey(x => x.LibraryId);
        b.Entity<MsBook>().HasMany(x => x.Reviews).WithOne().HasForeignKey(x => x.BookId);
        b.Entity<MsBook>().HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.BookId);
        b.Entity<MsMagazine>().HasMany(x => x.Reviews).WithOne().HasForeignKey(x => x.BookId);
    }
}

public sealed class MultiSetDelegateCounter
{
    private int _reviewCalls;
    public int ReviewCalls => _reviewCalls;
    public void CountReviewCall() => Interlocked.Increment(ref _reviewCalls);
}

public sealed class MsLibraryProfile : EntitySetProfile<int, MsLibrary>
{
    public MsLibraryProfile(MultiSetDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Libraries";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Libraries.AsQueryable());
        HasMany(x => x.Books); // delegate-less → pushable
    }
}

// Delegate-LESS Books set. On its own it would let Reviews fold into an EF ThenInclude JOIN.
public sealed class MsBooksProfile : EntitySetProfile<int, MsBook>
{
    public MsBooksProfile(MultiSetDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Books";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Books.AsQueryable());
        HasMany(x => x.Reviews); // delegate-LESS
        HasMany(x => x.Tags);    // delegate-less control nav (see the same-name-different-type test)
    }
}

// Delegate-BACKED FeaturedBooks set over the SAME MsBook model. The Reviews delegate is the security
// boundary (imagine it filtering to approved reviews / authorizing the caller); it must never be
// bypassed by a JOIN, regardless of whether this profile registers before or after MsBooksProfile.
public sealed class MsFeaturedBooksProfile : EntitySetProfile<int, MsBook>
{
    public MsFeaturedBooksProfile(MultiSetDbContext db, MultiSetDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "FeaturedBooks";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Books.AsQueryable());
        HasMany(x => x.Reviews,
            getAll: (bookId, ct) =>
            {
                counter.CountReviewCall();
                return Task.FromResult<IEnumerable<MsReview>>(
                    db.Reviews.Where(r => r.BookId == bookId).ToList());
            });
    }
}

// Different model type whose OWN "Reviews" nav is delegate-backed. Proves the union does not defer a
// same-named nav on an unrelated type.
public sealed class MsMagazineProfile : EntitySetProfile<int, MsMagazine>
{
    public MsMagazineProfile(MultiSetDbContext db, MultiSetDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "Magazines";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Magazines.AsQueryable());
        HasMany(x => x.Reviews,
            getAll: (magId, ct) =>
            {
                counter.CountReviewCall();
                return Task.FromResult<IEnumerable<MsReview>>(
                    db.Reviews.Where(r => r.BookId == magId).ToList());
            });
    }
}

internal static class MultiSetSqliteHarness
{
    // booksBeforeFeatured toggles the registration order of the delegate-LESS and delegate-BACKED
    // sets over MsBook, exercising both sides of the FirstOrDefault-by-model-type ambiguity.
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection, MultiSetDelegateCounter counter, SqlCaptureSink? sink,
        bool booksBeforeFeatured)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<MsLibraryProfile>();
                if (booksBeforeFeatured)
                {
                    b.AddEntitySetProfile<MsBooksProfile>();
                    b.AddEntitySetProfile<MsFeaturedBooksProfile>();
                }
                else
                {
                    b.AddEntitySetProfile<MsFeaturedBooksProfile>();
                    b.AddEntitySetProfile<MsBooksProfile>();
                }
                b.AddEntitySetProfile<MsMagazineProfile>();
            },
            configureServices: services =>
            {
                services.AddSingleton(counter);
                if (sink is not null) services.AddSingleton(sink);
                services.AddDbContext<MultiSetDbContext>(o =>
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
        MultiSetDbContext db = scope.ServiceProvider.GetRequiredService<MultiSetDbContext>();
        db.Database.EnsureCreated();

        db.Libraries.Add(new MsLibrary { Id = 1, Name = "Lib" });
        db.Books.Add(new MsBook { Id = 10, LibraryId = 1, Title = "B1" });
        db.Reviews.Add(new MsReview { Id = 100, BookId = 10, Body = "raw-review" });
        db.Tags.Add(new MsTag { Id = 200, BookId = 10, Name = "tag1" });
        db.Magazines.Add(new MsMagazine { Id = 10, LibraryId = 1, Title = "M1" });
        db.SaveChanges();
        return fx;
    }

    public static string LastSelectAgainst(SqlCaptureSink sink, string table) => sink.Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal) && s.Contains($"\"{table}\"", StringComparison.Ordinal))
        .Last();
}

public sealed class MultiSetDelegateSafetyExpandTests
{
    // The core regression: a nested Books($expand=Reviews) must NEVER JOIN-load raw Reviews, because
    // ANOTHER entity set (FeaturedBooks) over the same MsBook type declares Reviews WITH a delegate.
    // The delegate is the security boundary; the whole branch defers off pushdown so the raw rows are
    // never bypassed. This must hold in BOTH registration orders — before the union fix it depended on
    // the delegate-backed set happening to register first.
    [Theory]
    [InlineData(true)]  // delegate-LESS Books registered first (the order that regressed pre-fix)
    [InlineData(false)] // delegate-BACKED FeaturedBooks registered first
    public async Task NestedExpand_DivergentDelegateAcrossSets_NeverEfIncludes_Reviews_EitherOrder(
        bool booksBeforeFeatured)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        var counter = new MultiSetDelegateCounter();
        await using TestFixture fx = await MultiSetSqliteHarness.BuildAsync(
            connection, counter, sink, booksBeforeFeatured);
        sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/Libraries?$orderby=id&$expand=Books($expand=Reviews)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The delegate-backed Reviews nav must be ABSENT from the parent JOIN at any depth — the raw
        // Reviews table is never EF-included, so the FeaturedBooks delegate is never bypassed.
        string sql = MultiSetSqliteHarness.LastSelectAgainst(sink, "Libraries");
        Assert.DoesNotContain("\"Reviews\"", sql);

        // Whole branch deferred → the delegate-less parent Books stays EDM-only (empty), and the raw
        // "raw-review" body never leaks through a JOIN. (Deferral, not delegate invocation, is the safe
        // outcome here because the parent Library.Books nav is itself delegate-less.)
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Books\":[]", body);
        Assert.DoesNotContain("raw-review", body);
    }

    // Adversarial control: MsBook.Tags is delegate-less and NO MsBook profile route-backs it. A nav
    // literally named "Reviews" IS delegate-backed, but on the UNRELATED MsMagazine type. Expanding
    // Books($expand=Tags) must still push (JOIN Tags) — the union is scoped to the same CLR type and
    // same nav name, so neither the same-type Reviews delegate nor the different-type same-named
    // Reviews delegate wrongly defers an unrelated delegate-less nav.
    [Fact]
    public async Task NestedExpand_DelegatelessNav_NotOverDeferred_BySameNameDelegateOnOtherType()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        var counter = new MultiSetDelegateCounter();
        await using TestFixture fx = await MultiSetSqliteHarness.BuildAsync(
            connection, counter, sink, booksBeforeFeatured: true);
        sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/Libraries?$orderby=id&$expand=Books($expand=Tags)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = MultiSetSqliteHarness.LastSelectAgainst(sink, "Libraries");
        Assert.Contains("\"Books\"", sql); // pushed
        Assert.Contains("\"Tags\"", sql);  // delegate-less grandchild JOIN-loaded, not over-deferred

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"tag1\"", body);
    }
}
