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

// #292 regression fixtures. MsBook2/MsReview2 are a SEPARATE type pair (not MsBook/MsReview) so
// the two-conflicting-routes scenario below (BookAlphas AND BookBetas both route-back Reviews,
// with DIFFERENT delegates) doesn't disturb the exactly-one-route scenario already exercised by
// MsBook via Books/FeaturedBooks above.
public sealed class MsReview2
{
    public int Id { get; set; }
    public int Book2Id { get; set; }
    public string Body { get; set; } = "";
}

public sealed class MsBook2
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public List<MsReview2> Reviews { get; set; } = new();
}

// Root entity with TWO delegate-backed navigations feeding Stage 3 (ExpandLevelAsync) directly —
// unlike MsLibrary.Books (delegate-less, pushable), these MUST take the delegate expansion path,
// which is exactly where #292's ResolveRequestSourceForEdmType bug lived. Both Books/Books2 are
// EF-unmapped (Ignore()'d below): they are served entirely by their own getAll delegates, which
// deliberately Include() the child's own Reviews as an incidental implementation detail —
// simulating the realistic EF-fixup precondition for the #292 leak (the already-populated CLR
// graph must not survive into the response if the nested $expand=Reviews can't be safely
// resolved to a single legitimate delegate).
public sealed class MsShelf
{
    public int Id { get; set; }
    public List<MsBook> Books { get; set; } = new();   // -> ambiguous MsBook: exactly ONE route (FeaturedBooks)
    public List<MsBook2> Books2 { get; set; } = new(); // -> ambiguous MsBook2: TWO conflicting routes
}

public sealed class MultiSetDbContext : DbContext
{
    public MultiSetDbContext(DbContextOptions<MultiSetDbContext> options) : base(options) { }

    public DbSet<MsLibrary> Libraries => Set<MsLibrary>();
    public DbSet<MsBook> Books => Set<MsBook>();
    public DbSet<MsReview> Reviews => Set<MsReview>();
    public DbSet<MsTag> Tags => Set<MsTag>();
    public DbSet<MsMagazine> Magazines => Set<MsMagazine>();
    public DbSet<MsBook2> Book2s => Set<MsBook2>();
    public DbSet<MsReview2> Review2s => Set<MsReview2>();
    public DbSet<MsShelf> Shelves => Set<MsShelf>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MsLibrary>().HasMany(l => l.Books).WithOne().HasForeignKey(x => x.LibraryId);
        b.Entity<MsBook>().HasMany(x => x.Reviews).WithOne().HasForeignKey(x => x.BookId);
        b.Entity<MsBook>().HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.BookId);
        b.Entity<MsMagazine>().HasMany(x => x.Reviews).WithOne().HasForeignKey(x => x.BookId);
        b.Entity<MsBook2>().HasMany(x => x.Reviews).WithOne().HasForeignKey(x => x.Book2Id);
        // MsShelf.Books/Books2 are served entirely by their profile's own getAll delegate
        // (below) — not a real EF relationship — so EF must not try to auto-discover one.
        b.Entity<MsShelf>().Ignore(x => x.Books);
        b.Entity<MsShelf>().Ignore(x => x.Books2);
    }
}

public sealed class MultiSetDelegateCounter
{
    private int _reviewCalls;
    private int _book2ReviewCalls;
    public int ReviewCalls => _reviewCalls;
    public int Book2ReviewCalls => _book2ReviewCalls;
    public void CountReviewCall() => Interlocked.Increment(ref _reviewCalls);
    public void CountBook2ReviewCall() => Interlocked.Increment(ref _book2ReviewCalls);
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

// #292: delegate-backed root profile. Books resolves through the ambiguous MsBook union with
// EXACTLY one route-backed candidate (FeaturedBooks); Books2 resolves through the ambiguous
// MsBook2 union with TWO conflicting route-backed candidates (BookAlphas AND BookBetas below).
public sealed class MsShelfProfile : EntitySetProfile<int, MsShelf>
{
    public MsShelfProfile(MultiSetDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Shelves";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Shelves.AsQueryable());

        HasMany(x => x.Books,
            getAll: (shelfId, ct) =>
                Task.FromResult<IEnumerable<MsBook>>(db.Books.Include(bk => bk.Reviews).ToList()));

        HasMany(x => x.Books2,
            getAll: (shelfId, ct) =>
                Task.FromResult<IEnumerable<MsBook2>>(db.Book2s.Include(bk => bk.Reviews).ToList()));
    }
}

// One of TWO entity sets exposing MsBook2, both route-backing Reviews with DIFFERENT delegates —
// the genuine "two profiles route-back the same nav name differently" conflict from issue #292
// item 2. Neither this profile's nor MsBookBetaProfile's delegate may be picked arbitrarily.
public sealed class MsBookAlphaProfile : EntitySetProfile<int, MsBook2>
{
    public MsBookAlphaProfile(MultiSetDbContext db, MultiSetDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "BookAlphas";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Book2s.AsQueryable());
        HasMany(x => x.Reviews,
            getAll: (bookId, ct) =>
            {
                counter.CountBook2ReviewCall();
                return Task.FromResult<IEnumerable<MsReview2>>(
                    db.Review2s.Where(r => r.Book2Id == bookId).ToList());
            });
    }
}

public sealed class MsBookBetaProfile : EntitySetProfile<int, MsBook2>
{
    public MsBookBetaProfile(MultiSetDbContext db, MultiSetDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "BookBetas";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Book2s.AsQueryable());
        HasMany(x => x.Reviews,
            getAll: (bookId, ct) =>
            {
                counter.CountBook2ReviewCall();
                return Task.FromResult<IEnumerable<MsReview2>>(
                    db.Review2s.Where(r => r.Book2Id == bookId).ToList());
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
                b.AddEntitySetProfile<MsShelfProfile>();
                // Same order toggle applied to the doubly-ambiguous BookAlpha/BookBeta pair so the
                // #292 "identical result regardless of registration order" tests exercise both.
                if (booksBeforeFeatured)
                {
                    b.AddEntitySetProfile<MsBookAlphaProfile>();
                    b.AddEntitySetProfile<MsBookBetaProfile>();
                }
                else
                {
                    b.AddEntitySetProfile<MsBookBetaProfile>();
                    b.AddEntitySetProfile<MsBookAlphaProfile>();
                }
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
        db.Book2s.Add(new MsBook2 { Id = 10, Title = "B2-1" });
        db.Review2s.Add(new MsReview2 { Id = 100, Book2Id = 10, Body = "book2-raw-review" });
        db.Shelves.Add(new MsShelf { Id = 1 });
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

    // #292 regression: Shelves.Books is delegate-backed (unlike Library.Books), so it goes through
    // Stage 3 (ExpandLevelAsync) directly rather than pushdown — exactly where
    // ResolveRequestSourceForEdmType's registration-order-dependent FirstOrDefault lived. Books
    // targets the SAME ambiguous MsBook type as the tests above (Books/FeaturedBooks), where
    // EXACTLY ONE candidate (FeaturedBooks) route-backs the nested Reviews expand. The pre-fix
    // FirstOrDefault, depending on iteration order, could resolve to the routeless Books profile —
    // silently skipping the FeaturedBooks delegate while the Shelf handler's own incidental
    // `Include(b => b.Reviews)` left raw review rows already populated on the CLR graph, which
    // Stage 3.5 (OmitUnexpandedNavigations) would then keep untouched (it only strips UN-expanded
    // navigations — the raw data leaks straight through with the delegate never having run). The
    // fix must resolve to FeaturedBooks' delegate — and ONLY it — regardless of order.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedExpand_AmbiguousBinding_ResolvesSoleRouteBackedProfile_RegardlessOfOrder(
        bool booksBeforeFeatured)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new MultiSetDelegateCounter();
        await using TestFixture fx = await MultiSetSqliteHarness.BuildAsync(
            connection, counter, sink: null, booksBeforeFeatured);

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/Shelves?$expand=Books($expand=Reviews)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();

        // The ONE legitimate delegate (FeaturedBooks' Reviews handler) must have run — exactly
        // once, for the one seeded book — in BOTH registration orders.
        Assert.Equal(1, counter.ReviewCalls);

        // The delegate's OWN served data reaches the response (not merely "empty" — the whole
        // point is that a genuinely resolvable route is HONORED, not deferred).
        Assert.Contains("raw-review", body);
    }

    // #292 regression (fail-closed side): Shelves.Books2 targets the DOUBLY-ambiguous MsBook2 type,
    // where TWO DIFFERENT profiles (BookAlphas, BookBetas) both route-back Reviews with distinct
    // delegates — a genuine conflict with no way to legitimately choose between them. Neither may
    // be picked arbitrarily; the fix must blank the Reviews node rather than let the Shelf
    // handler's own incidental `Include(b => b.Reviews)` leak raw "book2-raw-review" data through,
    // and neither delegate may run. Must hold in BOTH registration orders.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedExpand_ConflictingRouteBackedProfiles_DefersBlank_NeverPicksEitherDelegate(
        bool booksBeforeFeatured)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new MultiSetDelegateCounter();
        await using TestFixture fx = await MultiSetSqliteHarness.BuildAsync(
            connection, counter, sink: null, booksBeforeFeatured);

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/Shelves?$expand=Books2($expand=Reviews)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();

        // Neither BookAlphas' nor BookBetas' delegate ran.
        Assert.Equal(0, counter.Book2ReviewCalls);

        // Fail closed: the raw, Include-populated Reviews data must never leak through.
        Assert.DoesNotContain("book2-raw-review", body);
        Assert.Contains("\"Reviews\":[]", body);
    }
}
