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

// Model B — declaring-set authority (OWNER DECISION 2026-07-26, FROZEN spec on issue #293): when
// the SAME CLR/EDM model type is exposed by 2+ entity sets, each set's OWN declaration governs its
// OWN navigations — a delegate on a sibling set never retroactively poisons a nav a delegate-less
// sibling serves raw. Fail-closed BLANKING fires only when the candidate sets sharing that exact
// EDM type genuinely DISAGREE (some delegate-less, some delegate-backed, or 2+ distinct delegates)
// — the framework then cannot tell which authoritative declaration applies, so it blanks rather
// than guessing or picking one arbitrarily. This must hold regardless of registration order (a
// deterministic set computation, never a FirstOrDefault). Uses the same EF Core Sqlite +
// SQL-capture harness as MultiLevelExpandPushdownSqliteTests.

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

    // -> a type exposed by EXACTLY ONE entity set (SecureBooks), delegate-backed, no other
    // candidate sharing that exact EDM type to disagree with it. The "single-candidate delegate-
    // backed nested nav" case: RunDelegate, distinct from the now-blanked ambiguous Books above.
    public List<MsSecureBook> SecureBooks { get; set; } = new();
}

// Exposed ONLY by SecureBooksProfile ("SecureBooks") below — no sibling entity set shares this
// exact EDM type, so its candidate set for Notes is a singleton and DL(Notes) is always empty.
public sealed class MsSecureBook
{
    public int Id { get; set; }
    public List<MsNote> Notes { get; set; } = new();
}

public sealed class MsNote
{
    public int Id { get; set; }
    public int SecureBookId { get; set; }
    public string Text { get; set; } = "";
    public bool Secret { get; set; }
}

// Root of a 3-level chain (Groups -> Shelves -> Books -> Reviews) used to exercise a DISAGREEMENT
// at depth 3 rather than depth 2: not EF-mapped at all (GetQueryable returns an in-memory single
// row) — only its Shelves navigation delegate matters, which genuinely loads MsShelf entities so
// Stage 3 recurses into a real (non-empty) next level.
public sealed class MsGroup
{
    public int Id { get; set; }
    public List<MsShelf> Shelves { get; set; } = new();
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
    public DbSet<MsSecureBook> SecureBooks => Set<MsSecureBook>();
    public DbSet<MsNote> Notes => Set<MsNote>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MsLibrary>().HasMany(l => l.Books).WithOne().HasForeignKey(x => x.LibraryId);
        b.Entity<MsBook>().HasMany(x => x.Reviews).WithOne().HasForeignKey(x => x.BookId);
        b.Entity<MsBook>().HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.BookId);
        b.Entity<MsMagazine>().HasMany(x => x.Reviews).WithOne().HasForeignKey(x => x.BookId);
        b.Entity<MsBook2>().HasMany(x => x.Reviews).WithOne().HasForeignKey(x => x.Book2Id);
        b.Entity<MsSecureBook>().HasMany(x => x.Notes).WithOne().HasForeignKey(x => x.SecureBookId);
        // MsShelf.Books/Books2/SecureBooks are served entirely by their profile's own getAll
        // delegate (below) — not a real EF relationship — so EF must not try to auto-discover one.
        b.Entity<MsShelf>().Ignore(x => x.Books);
        b.Entity<MsShelf>().Ignore(x => x.Books2);
        b.Entity<MsShelf>().Ignore(x => x.SecureBooks);
    }
}

public sealed class MultiSetDelegateCounter
{
    private int _reviewCalls;
    private int _book2ReviewCalls;
    private int _secureNoteCalls;
    public int ReviewCalls => _reviewCalls;
    public int Book2ReviewCalls => _book2ReviewCalls;
    public int SecureNoteCalls => _secureNoteCalls;
    public void CountReviewCall() => Interlocked.Increment(ref _reviewCalls);
    public void CountBook2ReviewCall() => Interlocked.Increment(ref _book2ReviewCalls);
    public void CountSecureNoteCall() => Interlocked.Increment(ref _secureNoteCalls);
}

public sealed class MsLibraryProfile : EntitySetProfile<int, MsLibrary>
{
    public MsLibraryProfile(MultiSetDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Libraries";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = () => db.Libraries.AsQueryable();
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
        GetQueryable = () => db.Books.AsQueryable();
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
        GetQueryable = () => db.Books.AsQueryable();
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
        GetQueryable = () => db.Magazines.AsQueryable();
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
        GetQueryable = () => db.Shelves.AsQueryable();

        HasMany(x => x.Books,
            getAll: (shelfId, ct) =>
                Task.FromResult<IEnumerable<MsBook>>(db.Books.Include(bk => bk.Reviews).ToList()));

        HasMany(x => x.Books2,
            getAll: (shelfId, ct) =>
                Task.FromResult<IEnumerable<MsBook2>>(db.Book2s.Include(bk => bk.Reviews).ToList()));

        HasMany(x => x.SecureBooks,
            getAll: (shelfId, ct) =>
                Task.FromResult<IEnumerable<MsSecureBook>>(db.SecureBooks.Include(sb => sb.Notes).ToList()));
    }
}

// The ONLY entity set exposing the MsSecureBook EDM type — its candidate set for Notes is a
// singleton, so DB(Notes)={this route} and DL(Notes)=∅ unconditionally: the "single-candidate
// delegate-backed nested nav" case (RunDelegate), distinct from the now-blanked MsBook/Reviews
// ambiguity above. The delegate filters out Secret notes; the Shelf handler's own incidental
// Include(sb => sb.Notes) loads BOTH secret and non-secret notes, so a response containing the
// secret note would prove the raw Include leaked instead of this delegate having run.
public sealed class MsSecureBooksProfile : EntitySetProfile<int, MsSecureBook>
{
    public MsSecureBooksProfile(MultiSetDbContext db, MultiSetDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "SecureBooks";
        ExpandEnabled = true;
        GetQueryable = () => db.SecureBooks.AsQueryable();
        HasMany(x => x.Notes,
            getAll: (bookId, ct) =>
            {
                counter.CountSecureNoteCall();
                return Task.FromResult<IEnumerable<MsNote>>(
                    db.Notes.Where(n => n.SecureBookId == bookId && !n.Secret).ToList());
            });
    }
}

// Root of the depth-3 disagreement chain: Groups -> Shelves -> Books -> Reviews. Not EF-mapped;
// GetQueryable returns a single in-memory row. Shelves is delegate-backed so Stage 3 (not the
// pushdown gate) resolves it, genuinely loading MsShelf rows so the recursion reaches a real
// (non-empty) depth-2 level, which in turn genuinely loads MsBook rows (with Reviews incidentally
// Include()'d) via Shelf's OWN Books delegate — so the depth-3 Reviews disagreement is evaluated
// against real data, not trivially skipped because an intermediate level was empty.
public sealed class MsGroupProfile : EntitySetProfile<int, MsGroup>
{
    public MsGroupProfile(MultiSetDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Groups";
        ExpandEnabled = true;
        GetQueryable = () => new[] { new MsGroup { Id = 1 } }.AsQueryable();
        HasMany(x => x.Shelves,
            getAll: (groupId, ct) => Task.FromResult<IEnumerable<MsShelf>>(db.Shelves.ToList()));
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
        GetQueryable = () => db.Book2s.AsQueryable();
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
        GetQueryable = () => db.Book2s.AsQueryable();
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
                b.AddEntitySetProfile<MsSecureBooksProfile>();
                b.AddEntitySetProfile<MsGroupProfile>();
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
        db.SecureBooks.Add(new MsSecureBook { Id = 1 });
        db.Notes.Add(new MsNote { Id = 1, SecureBookId = 1, Text = "public-note", Secret = false });
        db.Notes.Add(new MsNote { Id = 2, SecureBookId = 1, Text = "hidden-note", Secret = true });
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

    // Model B flip: Books' candidate set for the exact MsBook EDM type is {Books (delegate-less),
    // FeaturedBooks (delegate-backed)}, so DB and DL DISAGREE and Reviews BLANKS for the Books half
    // too. Both navigations live on the same entity set and Stage 3 resolves each independently, so
    // one request per registration order still covers both.
    //
    // Books is delegate-backed here, so it goes through ExpandLevelAsync rather than pushdown. Its
    // nested Reviews must BLANK rather than run FeaturedBooks' delegate OR let the Shelf handler's own
    // incidental Include(b => b.Reviews) leak through Stage 3.5, which only strips UN-expanded
    // navigations.
    //
    // Books2 targets the DOUBLY-ambiguous MsBook2, where two profiles route Reviews with distinct
    // delegates -- 2+ routes agreeing on nothing, kept BLANK by owner micro-decision C. Must hold in
    // BOTH registration orders; the disagreement computation never reads order.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedExpand_DisagreementAndConflict_BothBlank_RegardlessOfOrder(
        bool booksBeforeFeatured)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new MultiSetDelegateCounter();
        await using TestFixture fx = await MultiSetSqliteHarness.BuildAsync(
            connection, counter, sink: null, booksBeforeFeatured);

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/Shelves?$expand=Books($expand=Reviews),Books2($expand=Reviews)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();

        // Books: DB(Reviews)={FeaturedBooks} vs DL(Reviews)={Books} disagree — fail closed. Neither
        // FeaturedBooks' delegate runs nor the Shelf handler's raw Include-populated Reviews leaks.
        Assert.Equal(0, counter.ReviewCalls);
        Assert.DoesNotContain("raw-review", body);

        // Books2: neither BookAlphas' nor BookBetas' delegate ran — fail closed, the raw,
        // Include-populated Reviews data must never leak through.
        Assert.Equal(0, counter.Book2ReviewCalls);
        Assert.DoesNotContain("book2-raw-review", body);

        // Both navs blank to an empty array — the response contains at least the two occurrences
        // (one per seeded Book/Book2).
        Assert.Contains("\"Reviews\":[]", body);
    }

    // NEW (Model B matrix): dual-exposure public+secure at ROOT. Root-level $expand always reads
    // only the URL-named set's own declaration (unchanged by this fix) — this test demonstrates the
    // pattern Model B is explicitly designed to support: the SAME CLR/EDM type exposed via a
    // public/unfiltered (delegate-less) set AND a secured/filtered (delegate-backed) set, each
    // served according to ITS OWN declaration with no cross-contamination.
    [Fact]
    public async Task RootExpand_DualExposure_DelegatelessServesRaw_DelegateBackedRuns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new MultiSetDelegateCounter();
        await using TestFixture fx = await MultiSetSqliteHarness.BuildAsync(
            connection, counter, sink: null, booksBeforeFeatured: true);

        // Public/unfiltered Books (delegate-less): its own declaration governs, so the raw related
        // Reviews rows are served directly — no delegate exists to run.
        HttpResponseMessage pub = await fx.Client.GetAsync("/odata/Books?$expand=Reviews");
        Assert.Equal(HttpStatusCode.OK, pub.StatusCode);
        string pubBody = await pub.Content.ReadAsStringAsync();
        Assert.Contains("raw-review", pubBody);
        Assert.Equal(0, counter.ReviewCalls);

        // Secured/filtered FeaturedBooks (delegate-backed) over the SAME MsBook type: its OWN
        // declaration routes Reviews through a delegate, which the root path must run.
        HttpResponseMessage sec = await fx.Client.GetAsync("/odata/FeaturedBooks?$expand=Reviews");
        Assert.Equal(HttpStatusCode.OK, sec.StatusCode);
        Assert.Equal(1, counter.ReviewCalls);
    }

    // NEW (Model B matrix): single-candidate delegate-backed nested nav -> RUN. MsSecureBook is
    // exposed by EXACTLY ONE entity set (SecureBooks), so its candidate set for Notes is a
    // singleton: DB(Notes)={SecureBooks' route}, DL(Notes)=∅ unconditionally — no disagreement is
    // even possible. This is the "honored sole route" outcome, now clearly distinct from the
    // MultiSet Books/Reviews case above (which LOOKS similar — "one route among the candidates" —
    // but blanks because a genuine second, delegate-less candidate disagrees).
    [Fact]
    public async Task NestedExpand_SingleCandidateDelegateBacked_Runs()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new MultiSetDelegateCounter();
        await using TestFixture fx = await MultiSetSqliteHarness.BuildAsync(
            connection, counter, sink: null, booksBeforeFeatured: true);

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/Shelves?$expand=SecureBooks($expand=Notes)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();

        // The sole delegate ran exactly once, and its OWN filtered result — not the Shelf handler's
        // raw Include(sb => sb.Notes) (which loaded both notes) — is what the response carries.
        Assert.Equal(1, counter.SecureNoteCalls);
        Assert.Contains("public-note", body);
        Assert.DoesNotContain("hidden-note", body);
    }

    // NEW (Model B matrix): a DEPTH-3 disagreement must blank exactly like the depth-2 cases above.
    // Groups(depth1, delegate-backed Shelves) -> Shelves(depth2, delegate-backed Books, itself
    // incidentally Include()-ing Reviews) -> Books(depth3, Reviews resolves against the ambiguous
    // {Books, FeaturedBooks} candidate set) -> Reviews disagreement. Every intermediate level is
    // genuinely loaded (via real delegates) so the depth-3 disagreement is actually evaluated
    // against non-empty data, not vacuously skipped because an earlier level came back empty.
    [Fact]
    public async Task NestedExpand_DepthThreeDisagreement_Blanks()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new MultiSetDelegateCounter();
        await using TestFixture fx = await MultiSetSqliteHarness.BuildAsync(
            connection, counter, sink: null, booksBeforeFeatured: true);

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/Groups?$expand=Shelves($expand=Books($expand=Reviews))");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();

        // The chain genuinely reached depth 3 (Books is present, non-empty)...
        Assert.Contains("\"Id\":10", body);
        // ...but the depth-3 Reviews disagreement still fails closed: neither FeaturedBooks' delegate
        // ran nor did the Shelf handler's raw Include-populated Reviews leak through.
        Assert.Equal(0, counter.ReviewCalls);
        Assert.DoesNotContain("raw-review", body);
        Assert.Contains("\"Reviews\":[]", body);
    }

    // NEW (Model B matrix): gate/path-agreement probe. The SAME disagreeing nav (MsBook.Reviews,
    // ambiguous between delegate-less Books and delegate-backed FeaturedBooks) is reached two
    // different ways in one test: via a delegate-less PARENT nav (Library.Books — resolved by the
    // PUSHDOWN GATE, which must defer the whole branch rather than EF-include Reviews) and via a
    // delegate-backed PARENT nav (Shelf.Books — resolved directly by the DELEGATE PATH, Stage 3).
    // Both must independently compute the SAME candidate set and the SAME Blank treatment for
    // Reviews — proving the gate and the delegate path can never diverge on the same navigation.
    [Fact]
    public async Task GatePathAgreement_SameDisagreeingNav_NeitherDelegatelessNorDelegateBackedParent_Leaks()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        var counter = new MultiSetDelegateCounter();
        await using TestFixture fx = await MultiSetSqliteHarness.BuildAsync(
            connection, counter, sink, booksBeforeFeatured: true);
        sink.Clear();

        // Via the delegate-less parent: the PUSHDOWN GATE sees the same disagreement and defers the
        // whole branch — Reviews is never EF-included/JOIN'd.
        HttpResponseMessage viaGate = await fx.Client.GetAsync(
            "/odata/Libraries?$orderby=id&$expand=Books($expand=Reviews)");
        Assert.Equal(HttpStatusCode.OK, viaGate.StatusCode);
        string gateSql = MultiSetSqliteHarness.LastSelectAgainst(sink, "Libraries");
        Assert.DoesNotContain("\"Reviews\"", gateSql);
        string gateBody = await viaGate.Content.ReadAsStringAsync();
        Assert.DoesNotContain("raw-review", gateBody);

        // Via the delegate-backed parent: Stage 3 (the DELEGATE PATH) resolves Reviews directly —
        // no pushdown gate involved for a route-backed top nav — and independently computes the
        // SAME Blank treatment from the SAME candidate set.
        HttpResponseMessage viaPath = await fx.Client.GetAsync(
            "/odata/Shelves?$expand=Books($expand=Reviews)");
        Assert.Equal(HttpStatusCode.OK, viaPath.StatusCode);
        string pathBody = await viaPath.Content.ReadAsStringAsync();
        Assert.Equal(0, counter.ReviewCalls);
        Assert.DoesNotContain("raw-review", pathBody);
        Assert.Contains("\"Reviews\":[]", pathBody);
    }
}
