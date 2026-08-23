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

// #322 DIAGNOSTIC PROBE (investigation only — not a shipped regression suite).
//
// Claim under test: a single-valued navigation that the ODataConventionModelBuilder discovers, but
// which the profile never declared via HasOptional/HasRequired, is left in the profile's
// StructuralProperties (BuildStructuralProperties subtracts only _navigationPropertyNames), is
// therefore flagged IsComplex (its CLR type is not an OData primitive), and trips
// TryApplySelectProjection's complex-member bail — abandoning $expand pushdown for the request.
//
// Three CLR families, identical shape, differing only in declaration/profile provenance:
//   Ud* : Publisher UNDECLARED, target type HAS its own root profile
//   Np* : Publisher UNDECLARED, target type has NO profile at all
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
    // Scope check (issue #322, second half): an undeclared convention-discovered nav on a NESTED
    // element type. DcBook has no profile at all, so the nested projection is built from the EDM
    // (IsMemberInitProjectable / ScalarStructuralClrProps read edmType.StructuralProperties()),
    // where this member is an IEdmNavigationProperty and therefore invisible to the complex bail.
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
        GetQueryable = _ => Task.FromResult(db.UdAuthors.AsQueryable());
        GetById = (id, _) => Task.FromResult(db.UdAuthors.FirstOrDefault(a => a.Id == id));
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
        GetQueryable = _ => Task.FromResult(db.UdPublishers.AsQueryable());
    }
}

public sealed class NpAuthorProfile : EntitySetProfile<int, NpAuthor>
{
    public NpAuthorProfile(UndeclaredNavDbContext db) : base(x => x.Id)
    {
        EntitySetName = "NpAuthors";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true; CountEnabled = true;
        GetQueryable = _ => Task.FromResult(db.NpAuthors.AsQueryable());
        GetById = (id, _) => Task.FromResult(db.NpAuthors.FirstOrDefault(a => a.Id == id));
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
        GetQueryable = _ => Task.FromResult(db.DcAuthors.AsQueryable());
        GetById = (id, _) => Task.FromResult(db.DcAuthors.FirstOrDefault(a => a.Id == id));
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
        GetQueryable = _ => Task.FromResult(db.DcPublishers.AsQueryable());
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

public sealed class Issue322UndeclaredNavProbeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;
    private SqlCaptureSink _sink = null!;

    public Issue322UndeclaredNavProbeTests(ITestOutputHelper output) => _out = output;

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

    [Fact(Skip = "#322 diagnostic probe - investigation only; run manually")]
    public async Task Probe_Metadata_ShowsBothPublishersAsNavigationProperties()
    {
        var r = await RunAsync("/odata/$metadata");
        Assert.Equal(HttpStatusCode.OK, r.Status);
    }

    [Fact(Skip = "#322 diagnostic probe - investigation only; run manually")]
    public async Task Probe_BareExpand_UndeclaredVsDeclared()
    {
        await RunAsync("/odata/DcAuthors?$expand=Books");
        await RunAsync("/odata/UdAuthors?$expand=Books");
        await RunAsync("/odata/NpAuthors?$expand=Books");
    }

    // Scope confirmation (issue #322, second half): DcBook now carries its own undeclared,
    // convention-discovered nav (Owner) and has NO profile. If nested types were affected the same
    // way roots are, this would 400 like the Ud/Np roots do.
    [Fact(Skip = "#322 diagnostic probe - investigation only; run manually")]
    public async Task Probe_NestedElementTypeUndeclaredNav_IsUnaffected()
    {
        await RunAsync("/odata/DcAuthors?$expand=Books($filter=contains(title,'B'))");
        await RunAsync("/odata/DcAuthors?$expand=Books");
    }

    [Fact(Skip = "#322 diagnostic probe - investigation only; run manually")]
    public async Task Probe_NestedFilter_LoudSymptom()
    {
        await RunAsync("/odata/DcAuthors?$expand=Books($filter=contains(title,'B'))");
        await RunAsync("/odata/UdAuthors?$expand=Books($filter=contains(title,'B'))");
        await RunAsync("/odata/NpAuthors?$expand=Books($filter=contains(title,'B'))");
    }

    [Fact(Skip = "#322 diagnostic probe - investigation only; run manually")]
    public async Task Probe_NarrowSelect_RestoresPushdown()
    {
        // Blast-radius scoping: does a $select that excludes the undeclared nav restore pushdown?
        await RunAsync("/odata/UdAuthors?$select=name&$expand=Books($filter=contains(title,'B'))");
        await RunAsync("/odata/NpAuthors?$select=name&$expand=Books($filter=contains(title,'B'))");
        // ...and does a $select that NAMES it kill it again?
        await RunAsync("/odata/UdAuthors?$select=name,publisher&$expand=Books($filter=contains(title,'B'))");
    }

    [Fact(Skip = "#322 diagnostic probe - investigation only; run manually")]
    public async Task Probe_SelectOnlyPushdown()
    {
        await RunAsync("/odata/DcAuthors?$select=name");
        await RunAsync("/odata/UdAuthors?$select=name");
        await RunAsync("/odata/UdAuthors?$select=name,publisher");
    }

    [Fact(Skip = "#322 diagnostic probe - investigation only; run manually")]
    public async Task Probe_PlainReadPayload_DoesUndeclaredNavLeakIntoJson()
    {
        await RunAsync("/odata/UdAuthors");
        await RunAsync("/odata/NpAuthors");
        await RunAsync("/odata/DcAuthors");
        await RunAsync("/odata/UdAuthors?$expand=Publisher");
        await RunAsync("/odata/NpAuthors?$expand=Publisher");
        await RunAsync("/odata/DcAuthors?$expand=Publisher");
    }

    [Fact(Skip = "#322 diagnostic probe - investigation only; run manually")]
    public async Task Probe_NestedExpand_LoudSymptom()
    {
        await RunAsync("/odata/DcAuthors?$expand=Books($expand=Author)");
        await RunAsync("/odata/UdAuthors?$expand=Books($top=1)");
        await RunAsync("/odata/UdAuthors?$expand=Books($orderby=title desc)");
    }

    // Side effect of the same root cause: because BuildStructuralProperties keeps the undeclared nav,
    // PropertyAccessEnabled (default true) registers structural PROPERTY routes over a navigation.
    [Fact(Skip = "#322 diagnostic probe - investigation only; run manually")]
    public async Task Probe_StructuralPropertyRoutesOverAnUndeclaredNav()
    {
        foreach (string url in new[]
        {
            "/odata/UdAuthors(1)/Publisher",
            "/odata/UdAuthors(1)/Publisher/$value",
            "/odata/NpAuthors(1)/Publisher",
            "/odata/DcAuthors(1)/Publisher",
        })
        {
            HttpResponseMessage r = await _fx.Client.GetAsync(url);
            _out.WriteLine($"GET {url} -> {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        }
    }
}
