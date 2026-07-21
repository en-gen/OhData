using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #206 (recursion): close the multi-level $expand pushdown gap. A chain of DELEGATE-LESS navigations
// $expand'd with nested options at each level is folded into ONE JOIN'd query (EF ThenInclude);
// $levels=N / $levels=max recurse a self-referential nav a bounded number of times; MaxExpansionDepth
// (default 3) caps both and is advertised in $metadata. THE SAFETY INVARIANT holds at every depth: a
// delegate-backed navigation is never EF-included and its delegate is never bypassed. Uses the same
// EF Core Sqlite + SQL-capture harness as ExpandPushdownSqliteTests / OptionedExpandPushdownSqliteTests.

// ── Deep delegate-less chain: Author is the only entity set; Book/Chapter/Page/Line are nav-target
//    types (no profiles), so their navigations are inherently delegate-less and pushable per level. ──
public sealed class MlAuthor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<MlBook> Books { get; set; } = new();
}

public sealed class MlBook
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public string Title { get; set; } = "";
    public int Year { get; set; }
    public int? EditorId { get; set; }
    public MlEditor? Editor { get; set; } // single-valued nav (nullable FK exercises the null-guard)
    public List<MlChapter> Chapters { get; set; } = new();
}

public sealed class MlChapter
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Heading { get; set; } = "";
    public int Ordinal { get; set; }
    public List<MlPage> Pages { get; set; } = new();
}

public sealed class MlPage
{
    public int Id { get; set; }
    public int ChapterId { get; set; }
    public int Number { get; set; }
    public List<MlLine> Lines { get; set; } = new();
}

public sealed class MlLine
{
    public int Id { get; set; }
    public int PageId { get; set; }
    public string Text { get; set; } = "";
}

// Self-referential hierarchy for $levels (seeded 3 deep: Root → Mid → Leaf).
public sealed class MlNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public List<MlNode> Children { get; set; } = new();
}

// A second self-referential set with a PER-PROFILE MaxExpansionDepth override (see MlDeptProfile).
public sealed class MlDept
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public List<MlDept> Children { get; set; } = new();
}

// Delegate-safety-at-depth fixture: Catalog exposes a delegate-less pushable chain (Sections → Items)
// AND a delegate-backed navigation (Curators) in the SAME request.
public sealed class MlCatalog
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<MlSection> Sections { get; set; } = new();
    public List<MlCurator> Curators { get; set; } = new();
}

public sealed class MlSection
{
    public int Id { get; set; }
    public int CatalogId { get; set; }
    public string Label { get; set; } = "";
    public List<MlItem> Items { get; set; } = new();
}

public sealed class MlItem
{
    public int Id { get; set; }
    public int SectionId { get; set; }
    public string Sku { get; set; } = "";
}

public sealed class MlCurator
{
    public int Id { get; set; }
    public int CatalogId { get; set; }
    public string FullName { get; set; } = "";
    public List<MlCuratorNote> Notes { get; set; } = new();
}

public sealed class MlCuratorNote
{
    public int Id { get; set; }
    public int CuratorId { get; set; }
    public string Text { get; set; } = "";
}

// Single-valued navigation chain for the reference-with-nested-children path: Book → Editor
// (single-valued) → Credits (collection). Editor is a nav-target type (no profile → delegate-less).
public sealed class MlEditor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<MlCredit> Credits { get; set; } = new();
}

public sealed class MlCredit
{
    public int Id { get; set; }
    public int EditorId { get; set; }
    public string Role { get; set; } = "";
}

// Delegate-safety-at-DEPTH fixture: Store → Aisles is delegate-less (pushable at level 1), but Aisle
// is its OWN entity set whose Products navigation is DELEGATE-backed — so a nested
// Aisles($expand=Products) must defer the WHOLE branch off pushdown (never EF-include Products).
public sealed class MlStore
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<MlAisle> Aisles { get; set; } = new();
}

public sealed class MlAisle
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Code { get; set; } = "";
    public List<MlProduct> Products { get; set; } = new();
}

public sealed class MlProduct
{
    public int Id { get; set; }
    public int AisleId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class MultiLevelDbContext : DbContext
{
    public MultiLevelDbContext(DbContextOptions<MultiLevelDbContext> options) : base(options) { }

    public DbSet<MlAuthor> Authors => Set<MlAuthor>();
    public DbSet<MlBook> Books => Set<MlBook>();
    public DbSet<MlChapter> Chapters => Set<MlChapter>();
    public DbSet<MlPage> Pages => Set<MlPage>();
    public DbSet<MlLine> Lines => Set<MlLine>();
    public DbSet<MlNode> Nodes => Set<MlNode>();
    public DbSet<MlDept> Depts => Set<MlDept>();
    public DbSet<MlCatalog> Catalogs => Set<MlCatalog>();
    public DbSet<MlSection> Sections => Set<MlSection>();
    public DbSet<MlItem> Items => Set<MlItem>();
    public DbSet<MlCurator> Curators => Set<MlCurator>();
    public DbSet<MlCuratorNote> CuratorNotes => Set<MlCuratorNote>();
    public DbSet<MlEditor> Editors => Set<MlEditor>();
    public DbSet<MlCredit> Credits => Set<MlCredit>();
    public DbSet<MlStore> Stores => Set<MlStore>();
    public DbSet<MlAisle> Aisles => Set<MlAisle>();
    public DbSet<MlProduct> Products => Set<MlProduct>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MlAuthor>().HasMany(a => a.Books).WithOne().HasForeignKey(x => x.AuthorId);
        b.Entity<MlBook>().HasMany(x => x.Chapters).WithOne().HasForeignKey(x => x.BookId);
        b.Entity<MlBook>().HasOne(x => x.Editor).WithMany().HasForeignKey(x => x.EditorId);
        b.Entity<MlEditor>().HasMany(x => x.Credits).WithOne().HasForeignKey(x => x.EditorId);
        b.Entity<MlChapter>().HasMany(x => x.Pages).WithOne().HasForeignKey(x => x.ChapterId);
        b.Entity<MlPage>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PageId);
        b.Entity<MlNode>().HasMany(n => n.Children).WithOne().HasForeignKey(n => n.ParentId);
        b.Entity<MlDept>().HasMany(n => n.Children).WithOne().HasForeignKey(n => n.ParentId);
        b.Entity<MlCatalog>().HasMany(c => c.Sections).WithOne().HasForeignKey(x => x.CatalogId);
        b.Entity<MlSection>().HasMany(x => x.Items).WithOne().HasForeignKey(x => x.SectionId);
        b.Entity<MlCatalog>().HasMany(c => c.Curators).WithOne().HasForeignKey(x => x.CatalogId);
        b.Entity<MlCurator>().HasMany(x => x.Notes).WithOne().HasForeignKey(x => x.CuratorId);
        b.Entity<MlStore>().HasMany(s => s.Aisles).WithOne().HasForeignKey(x => x.StoreId);
        b.Entity<MlAisle>().HasMany(x => x.Products).WithOne().HasForeignKey(x => x.AisleId);
    }
}

public sealed class MultiLevelDelegateCounter
{
    private int _curatorCalls;
    public int CuratorCalls => _curatorCalls;
    public void CountCuratorCall() => Interlocked.Increment(ref _curatorCalls);

    private int _productCalls;
    public int ProductCalls => _productCalls;
    public void CountProductCall() => Interlocked.Increment(ref _productCalls);
}

public sealed class MlAuthorProfile : EntitySetProfile<int, MlAuthor>
{
    public MlAuthorProfile(MultiLevelDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Authors";
        ExpandEnabled = true;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Authors.AsQueryable());
        HasMany(x => x.Books); // delegate-less → whole Book/Chapter/Page/Line chain is pushable
    }
}

public sealed class MlNodeProfile : EntitySetProfile<int, MlNode>
{
    public MlNodeProfile(MultiLevelDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Nodes";
        ExpandEnabled = true;
        OrderByEnabled = true;
        FilterEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Nodes.AsQueryable());
        HasMany(x => x.Children); // delegate-less, self-referential → $levels-pushable
    }
}

public sealed class MlDeptProfile : EntitySetProfile<int, MlDept>
{
    public MlDeptProfile(MultiLevelDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Depts";
        ExpandEnabled = true;
        OrderByEnabled = true;
        FilterEnabled = true;
        MaxExpansionDepth = 2; // per-profile override (default is 3) — the ceiling for $expand and $levels
        GetQueryable = _ => Task.FromResult(db.Depts.AsQueryable());
        HasMany(x => x.Children);
    }
}

public sealed class MlCatalogProfile : EntitySetProfile<int, MlCatalog>
{
    public MlCatalogProfile(MultiLevelDbContext db, MultiLevelDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "Catalogs";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Catalogs.AsQueryable());
        HasMany(x => x.Sections); // delegate-less → pushable (with its own Items chain)
        HasMany(x => x.Curators,
            getAll: (catalogId, ct) =>
            {
                counter.CountCuratorCall();
                return Task.FromResult<IEnumerable<MlCurator>>(
                    db.Curators.Where(c => c.CatalogId == catalogId).ToList());
            });
    }
}

// Store → Aisles is delegate-less (pushable); Aisle is its own entity set with a DELEGATE-backed
// Products nav, so a nested Aisles($expand=Products) defers the whole branch off pushdown.
public sealed class MlStoreProfile : EntitySetProfile<int, MlStore>
{
    public MlStoreProfile(MultiLevelDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Stores";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Stores.AsQueryable());
        HasMany(x => x.Aisles); // delegate-less on Store → pushable
    }
}

public sealed class MlAisleProfile : EntitySetProfile<int, MlAisle>
{
    public MlAisleProfile(MultiLevelDbContext db, MultiLevelDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "Aisles";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Aisles.AsQueryable());
        HasMany(x => x.Products,
            getAll: (aisleId, ct) =>
            {
                counter.CountProductCall();
                return Task.FromResult<IEnumerable<MlProduct>>(
                    db.Products.Where(p => p.AisleId == aisleId).ToList());
            });
    }
}

internal static class MultiLevelSqliteHarness
{
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection, MultiLevelDelegateCounter counter, SqlCaptureSink? sink,
        Action<EntitySetDefaults>? defaults = null)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                if (defaults is not null) b.WithDefaults(defaults);
                b.AddEntitySetProfile<MlAuthorProfile>();
                b.AddEntitySetProfile<MlNodeProfile>();
                b.AddEntitySetProfile<MlDeptProfile>();
                b.AddEntitySetProfile<MlCatalogProfile>();
                b.AddEntitySetProfile<MlStoreProfile>();
                b.AddEntitySetProfile<MlAisleProfile>();
            },
            configureServices: services =>
            {
                services.AddSingleton(counter);
                if (sink is not null) services.AddSingleton(sink);
                services.AddDbContext<MultiLevelDbContext>(o =>
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
        MultiLevelDbContext db = scope.ServiceProvider.GetRequiredService<MultiLevelDbContext>();
        db.Database.EnsureCreated();

        db.Authors.Add(new MlAuthor { Id = 1, Name = "Ann" });
        db.Editors.Add(new MlEditor { Id = 50, Name = "Ed" });
        db.Credits.Add(new MlCredit { Id = 500, EditorId = 50, Role = "copyedit" });
        db.Books.AddRange(
            new MlBook { Id = 10, AuthorId = 1, Title = "B1", Year = 2001, EditorId = 50 },
            new MlBook { Id = 11, AuthorId = 1, Title = "B2", Year = 1999, EditorId = null });
        db.Chapters.AddRange(
            new MlChapter { Id = 100, BookId = 10, Heading = "Zeta", Ordinal = 2 },
            new MlChapter { Id = 101, BookId = 10, Heading = "Alpha", Ordinal = 1 });
        db.Pages.AddRange(
            new MlPage { Id = 1000, ChapterId = 101, Number = 1 },
            new MlPage { Id = 1001, ChapterId = 101, Number = 2 });
        db.Lines.Add(new MlLine { Id = 10000, PageId = 1000, Text = "hello" });

        // Node hierarchy 4 deep: Root(1) → N-1(2) → N-2(3) → N-3(4).
        db.Nodes.AddRange(
            new MlNode { Id = 1, Name = "Root", ParentId = null },
            new MlNode { Id = 2, Name = "N-1", ParentId = 1 },
            new MlNode { Id = 3, Name = "N-2", ParentId = 2 },
            new MlNode { Id = 4, Name = "N-3", ParentId = 3 });

        // Dept hierarchy 4 deep (but the profile caps depth at 2), so $levels=max stops before D-C.
        db.Depts.AddRange(
            new MlDept { Id = 1, Name = "D-Root", ParentId = null },
            new MlDept { Id = 2, Name = "D-A", ParentId = 1 },
            new MlDept { Id = 3, Name = "D-B", ParentId = 2 },
            new MlDept { Id = 4, Name = "D-C", ParentId = 3 });

        db.Catalogs.Add(new MlCatalog { Id = 1, Name = "Cat" });
        db.Sections.Add(new MlSection { Id = 10, CatalogId = 1, Label = "S1" });
        db.Items.Add(new MlItem { Id = 100, SectionId = 10, Sku = "SKU1" });
        db.Curators.Add(new MlCurator { Id = 20, CatalogId = 1, FullName = "Cur1" });
        db.CuratorNotes.Add(new MlCuratorNote { Id = 200, CuratorId = 20, Text = "note" });

        db.Stores.Add(new MlStore { Id = 1, Name = "St1" });
        db.Aisles.Add(new MlAisle { Id = 30, StoreId = 1, Code = "A1" });
        db.Products.Add(new MlProduct { Id = 300, AisleId = 30, Name = "Prod1" });

        db.SaveChanges();
        return fx;
    }

    public static string LastSelectAgainst(SqlCaptureSink sink, string table) => sink.Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal) && s.Contains($"\"{table}\"", StringComparison.Ordinal))
        .Last();
}

public sealed class MultiLevelExpandPushdownSqliteTests : IAsyncLifetime
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
    public async Task TwoLevel_NestedExpand_PushesThenIncludeInOneQuery()
    {
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Authors?$orderby=id&$expand=Books($expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // One JOIN-based query: the Authors SELECT references BOTH deeper tables (ThenInclude).
        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Authors");
        Assert.Contains("\"Books\"", sql);
        Assert.Contains("\"Chapters\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Books\":", body);
        Assert.Contains("\"Chapters\":", body);
        Assert.Contains("\"Zeta\"", body);
        Assert.Contains("\"Alpha\"", body);
    }

    [Fact]
    public async Task ThreeLevel_NestedExpand_PushesEveryLevel_PascalCase()
    {
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($expand=Chapters($expand=Pages))");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Authors");
        Assert.Contains("\"Books\"", sql);
        Assert.Contains("\"Chapters\"", sql);
        Assert.Contains("\"Pages\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        // PascalCase preserved 3 levels deep — no camelCase leak from a SelectExpandWrapper.
        Assert.Contains("\"Books\":", body);
        Assert.Contains("\"Chapters\":", body);
        Assert.Contains("\"Pages\":", body);
        Assert.Contains("\"Number\":1", body);
        Assert.DoesNotContain("\"chapters\":", body); // no camelCase nav key
        Assert.DoesNotContain("\"number\":", body);
    }

    [Fact]
    public async Task ThreeLevel_NestedOptionsAtEachLevel_FilterOrderSelectAllApply()
    {
        _sink.Clear();
        // $filter at level 1 (Books.Year), $orderby+$select at level 2 (Chapters), $select at level 3 (Pages).
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($filter=year eq 2001;$expand=Chapters($orderby=ordinal;$select=heading;$expand=Pages($select=number)))");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Authors");
        Assert.Contains("\"Chapters\"", sql);
        Assert.Contains("\"Pages\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        // Level-1 $filter: only B1 (Year 2001), not B2 (1999).
        Assert.Contains("\"B1\"", body);
        Assert.DoesNotContain("\"B2\"", body);
        // Level-2 $orderby=ordinal asc → Alpha (1) precedes Zeta (2); $select=heading prunes ordinal.
        int alpha = body.IndexOf("Alpha", StringComparison.Ordinal);
        int zeta = body.IndexOf("Zeta", StringComparison.Ordinal);
        Assert.True(alpha >= 0 && zeta >= 0 && alpha < zeta, "level-2 nested $orderby must order chapters asc");
        Assert.DoesNotContain("\"Ordinal\":", body); // pruned by level-2 $select
        // Level-3 $select=number keeps number, prunes text.
        Assert.Contains("\"Number\":1", body);
        Assert.DoesNotContain("\"Text\":", body);
    }

    [Fact]
    public async Task DepthExceeded_FourLevels_Returns400WithODataErrorBody()
    {
        // Books(1) → Chapters(2) → Pages(3) → Lines(4) exceeds the default MaxExpansionDepth of 3.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$expand=Books($expand=Chapters($expand=Pages($expand=Lines)))");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body); // OData error envelope, not an empty 500
    }

    [Fact]
    public async Task Levels_Two_HonorsDepth_LoadsExactlyTwoLevels()
    {
        _sink.Clear();
        // Filter to the single root so nesting depth is unambiguous. $levels=2 → Root → N-1 (level 1)
        // → N-2 (level 2); N-2's own children (level 3, N-3) are NOT loaded — depth honored.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Nodes?$filter=parentId eq null&$expand=Children($levels=2)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Self-JOIN loaded the hierarchy.
        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Nodes");
        Assert.True(Regex.Matches(sql, "\"Nodes\"").Count >= 2, "$levels must self-JOIN the Nodes table");

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Root\"", body);
        Assert.Contains("\"N-1\"", body);
        Assert.Contains("\"N-2\"", body);
        Assert.DoesNotContain("\"N-3\"", body); // level 3 beyond $levels=2
    }

    [Fact]
    public async Task LevelsMax_ResolvesToMaxExpansionDepth()
    {
        // Default MaxExpansionDepth is 3, so $levels=max loads 3 levels: Root → N-1 → N-2 → N-3.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Nodes?$filter=parentId eq null&$expand=Children($levels=max)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Root\"", body);
        Assert.Contains("\"N-1\"", body);
        Assert.Contains("\"N-2\"", body);
        Assert.Contains("\"N-3\"", body); // level 3 reached under the default ceiling of 3
    }

    [Fact]
    public async Task Levels_ExceedingCap_Returns400()
    {
        // $levels=99 exceeds the default MaxExpansionDepth (3) → rejected before any handler runs.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Nodes?$expand=Children($levels=99)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
    }

    [Fact]
    public async Task PerProfileOverride_LowersCeiling_ThreeLevelExpandRejected()
    {
        // Depts sets MaxExpansionDepth = 2, so a 3-level $expand is rejected on Depts...
        HttpResponseMessage deep = await _fx.Client.GetAsync(
            "/odata/Depts?$expand=Children($levels=3)");
        Assert.Equal(HttpStatusCode.BadRequest, deep.StatusCode);

        // ...while the SAME 3-level request on default-depth (3) Nodes succeeds — proving the
        // per-profile override, not a global constant, is the ceiling.
        HttpResponseMessage ok = await _fx.Client.GetAsync(
            "/odata/Nodes?$expand=Children($levels=3)");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task PerProfileOverride_LevelsMax_ResolvesToOverriddenCeiling()
    {
        // Filter to the single root so the depth is unambiguous (the collection otherwise returns every
        // node as a top-level row). MaxExpansionDepth=2 → $levels=max loads exactly 2 levels:
        // D-Root → D-A (level 1) → D-B (level 2) → [] ; D-C (level 3) is NOT loaded.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Depts?$filter=parentId eq null&$expand=Children($levels=max)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"D-Root\"", body);
        Assert.Contains("\"D-A\"", body);
        Assert.Contains("\"D-B\"", body);
        Assert.DoesNotContain("\"D-C\"", body); // level 3 beyond the overridden ceiling of 2
    }

    [Fact]
    public async Task DelegateAtDepth_NeverEfIncluded_DelegateInvoked_WhilePushedChainJoins()
    {
        _sink.Clear();
        // Sections($expand=Items): delegate-less 2-level chain → pushed (JOIN Sections + Items).
        // Curators($expand=Notes): Curators is delegate-backed → invoked via its delegate, never a JOIN.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Catalogs?$orderby=id&$expand=Sections($expand=Items),Curators($expand=Notes)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Catalogs");
        Assert.Contains("\"Sections\"", sql);
        Assert.Contains("\"Items\"", sql);
        // The delegate-backed nav's tables must be ABSENT from the parents query at any depth.
        Assert.DoesNotContain("\"Curators\"", sql);
        Assert.DoesNotContain("\"CuratorNotes\"", sql);
        Assert.True(_counter.CuratorCalls > 0, "the delegate-backed Curators nav must expand through its delegate");

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"SKU1\"", body); // pushed grandchild present
        Assert.Contains("\"Cur1\"", body); // delegate-loaded curator present
    }

    [Fact]
    public async Task SingleValuedNav_WithNestedChildren_PushedAndNullGuarded()
    {
        _sink.Clear();
        // Book → Editor (single-valued reference) → Credits (collection): a single-valued nav carrying
        // a deeper nested $expand is projected into a null-guarded member-init and JOIN-loaded.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($expand=Editor($expand=Credits))");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Authors");
        Assert.Contains("\"Editors\"", sql);
        Assert.Contains("\"Credits\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Editor\":", body);
        Assert.Contains("\"copyedit\"", body);   // level-3 credit under the single-valued editor
        Assert.Contains("\"Editor\":null", body); // B2 has no editor → null reference preserved
    }

    [Fact]
    public async Task DelegateBackedNavAtDepth_DefersWholeBranch_NeverEfIncluded()
    {
        _sink.Clear();
        // Store → Aisles is delegate-less (pushable), but Aisle.Products is DELEGATE-backed, so
        // Aisles($expand=Products) must defer the whole branch: no JOIN to either table, the Products
        // delegate is never invoked from this deferred branch, and the request succeeds (no 500).
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Stores?$orderby=id&$expand=Aisles($expand=Products)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Stores");
        Assert.DoesNotContain("\"Aisles\"", sql);   // whole branch deferred → no JOIN at any level
        Assert.DoesNotContain("\"Products\"", sql); // the delegate table is never EF-included

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"St1\"", body);
        Assert.Contains("\"Aisles\":[]", body); // deferred delegate-less parent stays EDM-only (empty)
    }

    [Fact]
    public async Task DelegatelessNavAtDepth_WithoutDeeperDelegate_PushesNormally()
    {
        _sink.Clear();
        // Control for the test above: Store → Aisles WITHOUT the delegate-backed grandchild pushes
        // normally (the whole point of the defer is that the delegate CHILD, not the aisle, blocks it).
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Stores?$orderby=id&$expand=Aisles");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Stores");
        Assert.Contains("\"Aisles\"", sql); // pushed as a JOIN when no delegate-backed level is nested

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"A1\"", body);
    }

    [Fact]
    public async Task NestedCountAtDepth_EmitsInlineCountOnDeeperLevel()
    {
        // $count on a level-2 nested collection emits the inline @odata.count on the JOIN-loaded child.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($filter=year eq 2001;$expand=Chapters($count=true))");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        // B1 has 2 chapters → the nested count is emitted on the deeper level, PascalCase key.
        Assert.Contains("\"Chapters@odata.count\":2", body);
    }
}

// With ExpandPushdownEnabled=false, a multi-level $expand must NOT push (no JOIN); the delegate-less
// chain simply stays EDM-only and the request still succeeds (no 500).
public sealed class MultiLevelExpandDisabledTests : IAsyncLifetime
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
        _fx = await MultiLevelSqliteHarness.BuildAsync(
            _connection, _counter, _sink, defaults: d => d.ExpandPushdownEnabled = false);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task Disabled_MultiLevelExpand_NotPushed_StaysEdmOnly_No500()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$orderby=id&$expand=Books($expand=Chapters)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Authors");
        Assert.DoesNotContain("\"Books\"", sql); // pushdown disabled → no JOIN
        Assert.DoesNotContain("\"Chapters\"", sql);
    }
}

// $metadata advertises the per-set MaxExpansionDepth via Org.OData.Capabilities.V1.ExpandRestrictions.
public sealed class MultiLevelExpandMetadataTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private MultiLevelDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _counter = new MultiLevelDelegateCounter();
        _fx = await MultiLevelSqliteHarness.BuildAsync(_connection, _counter, sink: null);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task Metadata_AdvertisesExpandRestrictionsMaxLevels_PerSet()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/$metadata");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string csdl = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Org.OData.Capabilities.V1.ExpandRestrictions", csdl);
        Assert.Contains("MaxLevels", csdl);
        // Authors inherits the default ceiling (3); Depts overrides it to 2 — both advertised.
        Assert.Contains("\"MaxLevels\" Int=\"3\"", csdl.Replace("'", "\""));
        Assert.Contains("\"MaxLevels\" Int=\"2\"", csdl.Replace("'", "\""));
    }
}
