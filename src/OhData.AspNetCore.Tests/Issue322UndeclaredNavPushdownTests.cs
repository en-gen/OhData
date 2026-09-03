using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

// #322 REGRESSION SUITE (promoted from the diagnostic probes on
// investigate/322-pushdown-disqualification — same fixtures, same request shapes, now asserted).
//
// The defect: source.StructuralProperties is "every public readable CLR property MINUS every
// PROFILE-DECLARED navigation" (EntitySetProfile.BuildStructuralProperties subtracts only
// _navigationPropertyNames), so a navigation the ODataConventionModelBuilder discovered but the
// profile never declared via HasOptional/HasRequired/HasMany survives as a structural property
// carrying IsComplex = true. TryBuildProjectionInit's complex-member bail then fires for every
// request whose projection member set includes it, abandoning $select and $expand pushdown for the
// whole entity set — silently (Include fallback) until a nested $filter/$orderby turns it into a 400.
//
// Three CLR families, identical shape, differing ONLY in the navigation's declaration/profile
// provenance, so a difference in outcome can only be provenance:
//   Ud* : Publisher UNDECLARED, target type HAS its own root profile
//   Np* : Publisher UNDECLARED, target type has NO profile and NO entity set at all
//   Dc* : Publisher DECLARED via HasOptional (control — the issue's stated workaround)

#region fixtures

public sealed class UdAuthor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? PublisherId { get; set; }
    public UdPublisher? Publisher { get; set; } // never declared in the profile
    public List<UdBook> Books { get; set; } = new();
}

public sealed class UdBook
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public string Title { get; set; } = "";
}

public sealed class UdPublisher
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class NpAuthor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? PublisherId { get; set; }
    public NpPublisher? Publisher { get; set; } // never declared; target has NO profile
    public List<NpBook> Books { get; set; } = new();
}

public sealed class NpBook
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public string Title { get; set; } = "";
}

public sealed class NpPublisher
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class DcAuthor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? PublisherId { get; set; }
    public DcPublisher? Publisher { get; set; } // DECLARED via HasOptional
    public List<DcBook> Books { get; set; } = new();
}

public sealed class DcBook
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public string Title { get; set; } = "";
    // Scope check (#322 diagnosis, correction 3): an undeclared convention-discovered nav on a
    // NESTED element type. DcBook has no profile at all, so the nested projection is built from the
    // EDM (IsMemberInitProjectable / ScalarStructuralClrProps read edmType.StructuralProperties()),
    // where this member is an IEdmNavigationProperty and therefore invisible to the complex bail.
    // Nested types were never affected; this pins that they still aren't.
    public int? OwnerId { get; set; }
    public DcOwner? Owner { get; set; }
}

public sealed class DcOwner
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class DcPublisher
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class UndeclaredNavDbContext : DbContext
{
    public UndeclaredNavDbContext(DbContextOptions<UndeclaredNavDbContext> options) : base(options) { }

    public DbSet<UdAuthor> UdAuthors => Set<UdAuthor>();
    public DbSet<UdBook> UdBooks => Set<UdBook>();
    public DbSet<UdPublisher> UdPublishers => Set<UdPublisher>();
    public DbSet<NpAuthor> NpAuthors => Set<NpAuthor>();
    public DbSet<NpBook> NpBooks => Set<NpBook>();
    public DbSet<NpPublisher> NpPublishers => Set<NpPublisher>();
    public DbSet<DcAuthor> DcAuthors => Set<DcAuthor>();
    public DbSet<DcBook> DcBooks => Set<DcBook>();
    public DbSet<DcPublisher> DcPublishers => Set<DcPublisher>();
    public DbSet<DcOwner> DcOwners => Set<DcOwner>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UdAuthor>().HasMany(a => a.Books).WithOne().HasForeignKey(x => x.AuthorId);
        b.Entity<UdAuthor>().HasOne(a => a.Publisher).WithMany().HasForeignKey(a => a.PublisherId);
        b.Entity<NpAuthor>().HasMany(a => a.Books).WithOne().HasForeignKey(x => x.AuthorId);
        b.Entity<NpAuthor>().HasOne(a => a.Publisher).WithMany().HasForeignKey(a => a.PublisherId);
        b.Entity<DcAuthor>().HasMany(a => a.Books).WithOne().HasForeignKey(x => x.AuthorId);
        b.Entity<DcAuthor>().HasOne(a => a.Publisher).WithMany().HasForeignKey(a => a.PublisherId);
        b.Entity<DcBook>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId);
    }
}

public sealed class UdAuthorProfile : EntitySetProfile<int, UdAuthor>
{
    public UdAuthorProfile(UndeclaredNavDbContext db) : base(x => x.Id)
    {
        EntitySetName = "UdAuthors";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true; CountEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.UdAuthors.AsQueryable());
        GetById = (id, _) => OhDataResult.SuccessTask(db.UdAuthors.FirstOrDefault(a => a.Id == id));
        HasMany(x => x.Books);
        // Publisher deliberately NOT declared.
    }
}

public sealed class UdPublisherProfile : EntitySetProfile<int, UdPublisher>
{
    public UdPublisherProfile(UndeclaredNavDbContext db) : base(x => x.Id)
    {
        EntitySetName = "UdPublishers";
        ExpandEnabled = true; SelectEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.UdPublishers.AsQueryable());
    }
}

public sealed class NpAuthorProfile : EntitySetProfile<int, NpAuthor>
{
    public NpAuthorProfile(UndeclaredNavDbContext db) : base(x => x.Id)
    {
        EntitySetName = "NpAuthors";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true; CountEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.NpAuthors.AsQueryable());
        GetById = (id, _) => OhDataResult.SuccessTask(db.NpAuthors.FirstOrDefault(a => a.Id == id));
        HasMany(x => x.Books);
        // Publisher deliberately NOT declared, and NpPublisher has NO profile.
    }
}

public sealed class DcAuthorProfile : EntitySetProfile<int, DcAuthor>
{
    public DcAuthorProfile(UndeclaredNavDbContext db) : base(x => x.Id)
    {
        EntitySetName = "DcAuthors";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true; CountEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.DcAuthors.AsQueryable());
        GetById = (id, _) => OhDataResult.SuccessTask(db.DcAuthors.FirstOrDefault(a => a.Id == id));
        HasMany(x => x.Books);
        HasOptional<DcPublisher>(x => x.Publisher!); // the issue-stated workaround
    }
}

public sealed class DcPublisherProfile : EntitySetProfile<int, DcPublisher>
{
    public DcPublisherProfile(UndeclaredNavDbContext db) : base(x => x.Id)
    {
        EntitySetName = "DcPublishers";
        ExpandEnabled = true; SelectEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.DcPublishers.AsQueryable());
    }
}

internal static class UndeclaredNavHarness
{
    public static async Task<TestFixture> BuildAsync(SqliteConnection connection, SqlCaptureSink? sink = null)
    {
        var fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<UdAuthorProfile>();
                b.AddEntitySetProfile<UdPublisherProfile>();
                b.AddEntitySetProfile<NpAuthorProfile>();
                b.AddEntitySetProfile<DcAuthorProfile>();
                b.AddEntitySetProfile<DcPublisherProfile>();
            },
            configureServices: services =>
            {
                services.AddDbContext<UndeclaredNavDbContext>(o =>
                {
                    o.UseSqlite(connection);
                    if (sink is not null)
                    {
                        o.LogTo(
                            m => sink.Add(m),
                            (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted);
                    }
                });
            });

        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UndeclaredNavDbContext>();
        db.Database.EnsureCreated();

        db.UdPublishers.Add(new UdPublisher { Id = 100, Name = "Pub-U" });
        db.UdAuthors.Add(new UdAuthor { Id = 1, Name = "A1", PublisherId = 100 });
        db.UdBooks.AddRange(
            new UdBook { Id = 10, AuthorId = 1, Title = "B1" },
            new UdBook { Id = 11, AuthorId = 1, Title = "B2" });

        db.NpPublishers.Add(new NpPublisher { Id = 200, Name = "Pub-N" });
        db.NpAuthors.Add(new NpAuthor { Id = 1, Name = "A1", PublisherId = 200 });
        db.NpBooks.AddRange(
            new NpBook { Id = 20, AuthorId = 1, Title = "B1" },
            new NpBook { Id = 21, AuthorId = 1, Title = "B2" });

        db.DcPublishers.Add(new DcPublisher { Id = 300, Name = "Pub-D" });
        db.DcAuthors.Add(new DcAuthor { Id = 1, Name = "A1", PublisherId = 300 });
        db.DcOwners.Add(new DcOwner { Id = 900, Name = "Own-D" });
        db.DcBooks.AddRange(
            new DcBook { Id = 30, AuthorId = 1, Title = "B1", OwnerId = 900 },
            new DcBook { Id = 31, AuthorId = 1, Title = "B2", OwnerId = 900 });

        db.SaveChanges();
        return fx;
    }
}

#endregion

public sealed class Issue322UndeclaredNavPushdownTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;
    private SqlCaptureSink _sink = null!;

    public Issue322UndeclaredNavPushdownTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _fx = await UndeclaredNavHarness.BuildAsync(_connection, _sink);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    private async Task<(HttpStatusCode Status, string Body, string Sql)> RunAsync(string url)
    {
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync(url);
        string body = await resp.Content.ReadAsStringAsync();
        string sql = string.Join("\n---\n", _sink.Snapshot());
        _out.WriteLine($"### {url}\nSTATUS: {(int)resp.StatusCode}\nBODY: {body}\nSQL:\n{sql}\n");
        return (resp.StatusCode, body, sql);
    }

    /// <summary>
    /// The premise the whole issue rests on: the convention builder emits a NavigationProperty for
    /// the undeclared member on BOTH undeclared families — including Np, whose target type has no
    /// profile and no entity set (the diagnosis' correction 1 — the original issue claimed the
    /// defect needed a root profile on the target).
    /// </summary>
    [Fact]
    public async Task Metadata_AdvertisesTheUndeclaredMemberAsANavigationProperty_RegardlessOfTargetProfile()
    {
        (HttpStatusCode status, string body, _) = await RunAsync("/odata/$metadata");
        Assert.Equal(HttpStatusCode.OK, status);

        foreach (string entity in new[] { "UdAuthor", "NpAuthor", "DcAuthor" })
        {
            int at = body.IndexOf($"<EntityType Name=\"{entity}\"", StringComparison.Ordinal);
            Assert.True(at >= 0, $"{entity} missing from $metadata");
            int end = body.IndexOf("</EntityType>", at, StringComparison.Ordinal);
            string block = body.Substring(at, end - at);
            Assert.Contains("<NavigationProperty Name=\"Publisher\"", block, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// #322's LOUD symptom, and the one that proves pushdown is engaged rather than merely
    /// "not obviously broken": a nested $filter under $expand cannot be carried by the #305 Include
    /// fallback, so the fallback answers 400. 200 here means the member-init projection was built.
    /// Before the fix Ud and Np both returned 400 while Dc returned 200.
    /// </summary>
    [Fact]
    public async Task NestedFilterUnderExpand_IsPushedDown_ForAnUndeclaredConventionNavigation()
    {
        foreach (string set in new[] { "UdAuthors", "NpAuthors", "DcAuthors" })
        {
            (HttpStatusCode status, string body, string sql) =
                await RunAsync($"/odata/{set}?$expand=Books($filter=contains(title,'B'))");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("\"B1\"", body, StringComparison.Ordinal);
            // The nested predicate reached SQL rather than being applied in memory.
            Assert.Contains("instr(", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A nested $orderby is the other option the Include fallback cannot carry — same 400 before the
    /// fix, same 200 after, and the ordering reaches SQL.
    /// </summary>
    [Fact]
    public async Task NestedOrderByUnderExpand_IsPushedDown_ForAnUndeclaredConventionNavigation()
    {
        foreach (string set in new[] { "UdAuthors", "NpAuthors" })
        {
            (HttpStatusCode status, string body, string sql) =
                await RunAsync($"/odata/{set}?$expand=Books($orderby=title desc)");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(
                body.IndexOf("\"B2\"", StringComparison.Ordinal) < body.IndexOf("\"B1\"", StringComparison.Ordinal),
                "nested $orderby=title desc was not applied");
            Assert.Contains("DESC", sql, StringComparison.Ordinal);
        }
    }

    /// <summary>Both nested options together — the combined shape, likewise 400 before the fix.</summary>
    [Fact]
    public async Task NestedFilterAndOrderByTogether_AreServed_ForAnUndeclaredConventionNavigation()
    {
        (HttpStatusCode status, string body, string sql) =
            await RunAsync("/odata/UdAuthors?$expand=Books($filter=contains(title,'B');$orderby=title desc)");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"B1\"", body, StringComparison.Ordinal);
        Assert.Contains("instr(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// A nested $top ALONE was never a 400 and was already windowed in SQL before the fix: the #305
    /// Include fallback uses an EF Core FILTERED include, which carries Take() (but not a nested
    /// $filter/$orderby, hence those two 400s). Pinned so a future change cannot regress the shape
    /// that already worked while fixing the ones that did not.
    /// </summary>
    [Fact]
    public async Task NestedTopAlone_WasAlreadyWindowedInSql_AndStillIs()
    {
        foreach (string set in new[] { "UdAuthors", "NpAuthors" })
        {
            (HttpStatusCode status, string body, string sql) =
                await RunAsync($"/odata/{set}?$expand=Books($top=1)");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("ROW_NUMBER()", sql, StringComparison.Ordinal);

            using JsonDocument doc = JsonDocument.Parse(body);
            Assert.Equal(1, doc.RootElement.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        }
    }

    /// <summary>
    /// The bare $expand — no narrowing $select, so the projection member set is EVERY structural
    /// name and therefore always contained the undeclared navigation. It answered 200 before the fix
    /// too (via the Include fallback), so the recovery here is a query-plan change, not a status
    /// change: what is asserted is that the response is unchanged and correct.
    /// </summary>
    [Fact]
    public async Task BareExpand_ServesTheDeclaredCollection_UnchangedByTheFix()
    {
        foreach (string set in new[] { "UdAuthors", "NpAuthors", "DcAuthors" })
        {
            (HttpStatusCode status, string body, _) = await RunAsync($"/odata/{set}?$expand=Books");
            Assert.Equal(HttpStatusCode.OK, status);

            using JsonDocument doc = JsonDocument.Parse(body);
            Assert.Equal(2, doc.RootElement.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        }
    }

    /// <summary>
    /// $select column pruning, for a select set that DOES name the undeclared navigation — the case
    /// the complex-member bail used to kill. Before the fix this emitted the unpruned
    /// <c>SELECT "Id", "Name", "PublisherId"</c> (projection abandoned); now it emits
    /// <c>SELECT "Name", "Id"</c>. Asserted on the SQL, not the payload: the payload was already
    /// correct either way (the JSON $select trim runs regardless), which is exactly why the loss
    /// was silent.
    /// </summary>
    [Fact]
    public async Task SelectPushdown_PrunesColumns_WhenTheSelectSetNamesTheUndeclaredNavigation()
    {
        foreach (string set in new[] { "UdAuthors", "NpAuthors" })
        {
            (HttpStatusCode status, _, string sql) = await RunAsync($"/odata/{set}?$select=name,publisher");
            Assert.Equal(HttpStatusCode.OK, status);

            string select = sql.Split("\n---\n")
                .First(s => s.Contains($"\"{set}\"", StringComparison.Ordinal));
            Assert.Contains("\"Name\"", select, StringComparison.Ordinal);
            Assert.Contains("\"Id\"", select, StringComparison.Ordinal);
            // The FK column of the undeclared navigation is NOT selected — the projection is live.
            Assert.DoesNotContain("\"PublisherId\"", select, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A $select that does NOT name the undeclared navigation pruned columns even before the fix —
    /// the bail iterates the PROJECTION MEMBER SET, not every structural property, so a navigation
    /// outside the select set was never reached (diagnosis correction 2: "this entity set never
    /// pushes anything" was overstated). Pinned so the already-working case cannot regress.
    /// </summary>
    [Fact]
    public async Task SelectPushdown_PrunedColumnsAlready_WhenTheSelectSetExcludesTheUndeclaredNavigation()
    {
        foreach (string set in new[] { "UdAuthors", "NpAuthors" })
        {
            (HttpStatusCode status, _, string sql) = await RunAsync($"/odata/{set}?$select=name");
            Assert.Equal(HttpStatusCode.OK, status);

            string select = sql.Split("\n---\n")
                .First(s => s.Contains($"\"{set}\"", StringComparison.Ordinal));
            Assert.Contains("\"Name\"", select, StringComparison.Ordinal);
            Assert.DoesNotContain("\"PublisherId\"", select, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The dominant real-world shape, and the one the diagnosis' correction 2 identified as the
    /// actual blast radius: a $select that NAMES the undeclared navigation. It used to put the
    /// complex member into the projection set and kill pushdown; now the navigation is subtracted
    /// from the structural set, so the nested $filter is still pushed.
    /// </summary>
    [Fact]
    public async Task SelectNamingTheUndeclaredNavigation_StillPushesDown()
    {
        (HttpStatusCode status, _, string sql) =
            await RunAsync("/odata/UdAuthors?$select=name,publisher&$expand=Books($filter=contains(title,'B'))");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("instr(", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A narrowing $select that EXCLUDES the undeclared navigation pushed fully even before the fix
    /// (diagnosis correction 2). Pinned so a future change cannot regress the case that already
    /// worked while "fixing" the ones that did not.
    /// </summary>
    [Fact]
    public async Task NarrowSelectExcludingTheUndeclaredNavigation_StillPushesDown()
    {
        (HttpStatusCode status, _, string sql) =
            await RunAsync("/odata/UdAuthors?$select=name&$expand=Books($filter=contains(title,'B'))");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("instr(", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scope pin (diagnosis correction 3): a nested element type carrying its own undeclared
    /// convention navigation (DcBook.Owner, and DcBook has no profile at all) was never affected —
    /// the nested projection reads the EDM, where the member is an IEdmNavigationProperty and
    /// invisible to the complex bail. Still true.
    /// </summary>
    [Fact]
    public async Task NestedElementTypeWithItsOwnUndeclaredNavigation_IsUnaffected()
    {
        (HttpStatusCode status, string body, string sql) =
            await RunAsync("/odata/DcAuthors?$expand=Books($filter=contains(title,'B'))");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"B1\"", body, StringComparison.Ordinal);
        Assert.Contains("instr(", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The wire shape is unchanged by the fix on the plain read: an un-$expanded navigation is
    /// omitted (JSON Format §4.5.1) whether it was declared or convention-discovered.
    /// <para>
    /// Scoped claim, deliberately: on THIS path the fix is a query-plan change only. It is NOT a
    /// query-plan change everywhere — see
    /// <see cref="Issue322NonEfProjectionUnificationTests"/> for the one shape whose payload does
    /// move.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PlainRead_OmitsTheUndeclaredNavigation_LikeADeclaredOne()
    {
        foreach (string set in new[] { "UdAuthors", "NpAuthors", "DcAuthors" })
        {
            (HttpStatusCode status, string body, _) = await RunAsync($"/odata/{set}");
            Assert.Equal(HttpStatusCode.OK, status);

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement row = doc.RootElement.GetProperty("value")[0];
            Assert.False(row.TryGetProperty("publisher", out _), $"{set} leaked 'publisher'");
            Assert.False(row.TryGetProperty("Publisher", out _), $"{set} leaked 'Publisher'");
        }
    }
}

// #322's ONE payload difference, pinned because it is invisible to every other test here and the next
// person cannot otherwise tell whether flipping it back is a fix or a regression.
//
// A NON-EF GetQueryable whose materialized graph already holds the related object, plus $select naming
// the navigation and $expand of it. Pushdown is EF-gated, so nothing loads it and the wire value was
// only ever what the in-memory graph happened to carry:
//   ?$select=note,cust&$expand=Cust   before {"Cust":{"Id":5,…}}   after {"Cust":null}
//
// A UNIFICATION, not a regression, and the declared control below is what makes that visible: a
// DECLARED delegate-less navigation on the same model and request already returned null on BOTH trees,
// because it was never in StructuralProperties. The undeclared one survived only because
// BuildStructuralProperties failed to recognise it as a navigation -- the defect #322 fixes.
//
// Narrow: without the $select there is no projection to drop the value, and on an EF source it is null
// before and after.

#region non-EF fixtures

public sealed class UdMemOrder
{
    public int Id { get; set; }
    public string Note { get; set; } = "";
    public int? CustId { get; set; }
    public UdMemCust? Cust { get; set; }         // convention-discovered, NEVER declared
    public UdMemCust? DeclaredCust { get; set; } // declared delegate-less — the control
}

public sealed class UdMemCust
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// A NON-EF <c>GetQueryable</c>: a plain <c>List&lt;T&gt;.AsQueryable()</c> whose elements already
/// hold both related objects. No <c>DbContext</c>, so <c>ResolveEfCoreAssembly</c> returns null and
/// $expand pushdown never engages — only the $select projection does.
/// </summary>
public sealed class UdMemOrderProfile : EntitySetProfile<int, UdMemOrder>
{
    internal static List<UdMemOrder> NewData() => new()
    {
        new UdMemOrder
        {
            Id = 1,
            Note = "N",
            CustId = 5,
            Cust = new UdMemCust { Id = 5, Name = "IN-MEMORY" },
            DeclaredCust = new UdMemCust { Id = 6, Name = "DECLARED-IN-MEMORY" },
        },
    };

    public UdMemOrderProfile() : base(x => x.Id)
    {
        EntitySetName = "UdMemOrders";
        ExpandEnabled = true;
        SelectEnabled = true;
        List<UdMemOrder> data = NewData();
        GetQueryable = _ => OhDataResult.SuccessTask(data.AsQueryable());
        HasOptional<UdMemCust>(x => x.DeclaredCust!);
        // Cust deliberately NOT declared.
    }
}

#endregion

public sealed class Issue322NonEfProjectionUnificationTests
{
    private readonly ITestOutputHelper _out;

    public Issue322NonEfProjectionUnificationTests(ITestOutputHelper output) => _out = output;

    private static async Task<JsonElement> RowAsync(TestFixture fx, string url, ITestOutputHelper output)
    {
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        string body = await resp.Content.ReadAsStringAsync();
        output.WriteLine($"{url}\n  -> {(int)resp.StatusCode} {body}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("value")[0].Clone();
    }

    /// <summary>
    /// #322's payload difference, RE-SCOPED by #440 symptom 1 — and this is the one place in the
    /// suite where the two fixes disagree about the same request, so it is worth being exact.
    /// <para>
    /// #322 made both provenances project the navigation away and serialize <c>null</c>. #440 then
    /// established that <c>null</c> is the one answer that is definitely wrong for a navigation the
    /// server never loaded (OData JSON Format v4.01 §8.3: an inline navigation value IS the
    /// expanded representation, and a null single-valued one asserts the relationship is empty).
    /// So the UNDECLARED navigation is now <b>omitted</b>, and the declared control keeps its
    /// <c>null</c> — a deliberate divergence, and the rule behind it is that <b>declaring</b> a
    /// navigation is what makes it servable at all. The declared one was projected away by a
    /// mechanism that knows it is a navigation and could have loaded it; the undeclared one was
    /// never in any load path.
    /// </para>
    /// <para>
    /// If the undeclared side ever flips back to <c>{"Id":5,"Name":"IN-MEMORY"}</c> that is still a
    /// REGRESSION of #322 (an undeclared navigation treated as a projectable column again). If it
    /// flips to <c>null</c>, that is a regression of #440.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SelectPushdownOverANonEfQueryable_OmitsAPopulatedUndeclaredNav_WhileTheDeclaredOneStillNulls()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<UdMemOrderProfile>());

        JsonElement undeclared = await RowAsync(fx, "/odata/UdMemOrders?$select=note,cust&$expand=Cust", _out);
        JsonElement declared = await RowAsync(
            fx, "/odata/UdMemOrders?$select=note,declaredCust&$expand=DeclaredCust", _out);

        // #440: not present at all — the server never loaded it, so it asserts nothing about it.
        Assert.False(undeclared.TryGetProperty("Cust", out _));
        // The declared control, unchanged by #440.
        Assert.Equal(JsonValueKind.Null, declared.GetProperty("DeclaredCust").ValueKind);
    }

    /// <summary>
    /// The bare-<c>$expand</c> bound, also re-scoped by #440 symptom 1. With no <c>$select</c>
    /// there is no member-init projection to drop the value, so the DECLARED delegate-less
    /// navigation still serves the in-memory object exactly as it always did.
    /// <para>
    /// The undeclared one no longer does, and this is #440's measured COST rather than a free win:
    /// a non-EF <c>GetQueryable</c>/<c>GetAll</c> whose graph is already populated used to echo
    /// that value, and now omits it. It is accepted because the alternative is worse in both
    /// directions — on an EF-backed source the same profile, same request answered <c>null</c>,
    /// which is wrong data under 200, so the framework's answer for one navigation depended on the
    /// query provider. It no longer does: undeclared means "not served", on every source. The
    /// startup warning names exactly this profile and navigation, and declaring it restores the
    /// value on both.
    /// </para>
    /// </summary>
    [Fact]
    public async Task BareExpandOverANonEfQueryable_StillServesTheInMemoryGraph_ForTheDeclaredProvenanceOnly()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<UdMemOrderProfile>());

        JsonElement undeclared = await RowAsync(fx, "/odata/UdMemOrders?$expand=Cust", _out);
        Assert.False(undeclared.TryGetProperty("Cust", out _));

        JsonElement declared = await RowAsync(fx, "/odata/UdMemOrders?$expand=DeclaredCust", _out);
        Assert.Equal("DECLARED-IN-MEMORY", declared.GetProperty("DeclaredCust").GetProperty("Name").GetString());
    }
}
