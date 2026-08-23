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
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

// #322 DIAGNOSTIC PROBE — the Model B (#292/#293) delegate-safety question.
//
// The shape that option (1b) ("source NavigationPropertyNames from the EDM") would change:
//
//   SdDoc is exposed by TWO entity sets ->  candidate set S = { PublicDocs, SecureDocs }
//     PublicDocs : declares NOTHING about Owner (the ODataConventionModelBuilder discovers it)
//     SecureDocs : declares Owner WITH a get delegate (delegate-BACKED)
//
//   TODAY:            DB(Owner) = { SecureDocs' route },  DL(Owner) = {}  -> RunDelegate
//                     ("A candidate that neither routes nor declares the nav has no opinion
//                       on it and is ignored." — the FROZEN Model B spec on #293.)
//   UNDER (1b):       PublicDocs' NavigationPropertyNames would contain Owner (convention-
//                     discovered), so DL(Owner) = { PublicDocs } -> Blank.
//
// Owner is reached NESTED (via a delegate-backed Shelf.Docs) so the candidate set really is the
// exact-EDM-type union, not the root's URL-named-set-only special case.

#region fixtures

public sealed class SdShelf
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<SdDoc> Docs { get; set; } = new();
}

public sealed class SdDoc
{
    public int Id { get; set; }
    public int ShelfId { get; set; }
    public string Title { get; set; } = "";
    public int? OwnerId { get; set; }
    public SdOwner? Owner { get; set; }
}

public sealed class SdOwner
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ModelBProbeCounter
{
    private int _ownerCalls;
    public int OwnerCalls => Volatile.Read(ref _ownerCalls);
    public void Owner() => Interlocked.Increment(ref _ownerCalls);
}

public sealed class ModelBProbeDbContext : DbContext
{
    public ModelBProbeDbContext(DbContextOptions<ModelBProbeDbContext> options) : base(options) { }

    public DbSet<SdShelf> SdShelves => Set<SdShelf>();
    public DbSet<SdDoc> SdDocs => Set<SdDoc>();
    public DbSet<SdOwner> SdOwners => Set<SdOwner>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SdShelf>().HasMany(s => s.Docs).WithOne().HasForeignKey(d => d.ShelfId);
        b.Entity<SdDoc>().HasOne(d => d.Owner).WithMany().HasForeignKey(d => d.OwnerId);
    }
}

public sealed class SdShelfProfile : EntitySetProfile<int, SdShelf>
{
    public SdShelfProfile(ModelBProbeDbContext db) : base(x => x.Id)
    {
        EntitySetName = "SdShelves";
        ExpandEnabled = true; SelectEnabled = true; OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.SdShelves.AsQueryable());
        // Delegate-BACKED so the Docs level runs through the delegate expansion path, which is where
        // the nested candidate set (all profiles over the SdDoc EDM type) is resolved.
        HasMany(x => x.Docs, (id, _) => Task.FromResult<IEnumerable<SdDoc>>(
            db.SdDocs.Where(d => d.ShelfId == id).ToList()));
    }
}

// Candidate 1 over SdDoc: says NOTHING about Owner. Under Model B today it has "no opinion".
public sealed class SdPublicDocsProfile : EntitySetProfile<int, SdDoc>
{
    public SdPublicDocsProfile(ModelBProbeDbContext db) : base(x => x.Id)
    {
        EntitySetName = "PublicDocs";
        ExpandEnabled = true; SelectEnabled = true;
        GetQueryable = _ => Task.FromResult(db.SdDocs.AsQueryable());
        // Owner deliberately NOT declared at all.
    }
}

// Candidate 2 over SdDoc: delegate-BACKED Owner. Its delegate rewrites the name so a raw
// (Include/fixup) leak is distinguishable from the delegate's own result.
public sealed class SdSecureDocsProfile : EntitySetProfile<int, SdDoc>
{
    public SdSecureDocsProfile(ModelBProbeDbContext db, ModelBProbeCounter counter) : base(x => x.Id)
    {
        EntitySetName = "SecureDocs";
        ExpandEnabled = true; SelectEnabled = true;
        GetQueryable = _ => Task.FromResult(db.SdDocs.AsQueryable());
        Func<int, CancellationToken, Task<SdOwner?>> getOwner = (id, _) =>
        {
            counter.Owner();
            SdDoc? doc = db.SdDocs.AsNoTracking().FirstOrDefault(d => d.Id == id);
            SdOwner? raw = doc?.OwnerId is { } oid
                ? db.SdOwners.AsNoTracking().FirstOrDefault(o => o.Id == oid)
                : null;
            return Task.FromResult<SdOwner?>(raw is null
                ? null
                : new SdOwner { Id = raw.Id, Name = "delegate-" + raw.Name });
        };
        HasOptional<SdOwner>(x => x.Owner!, getOwner, refTargetEntitySet: null);
    }
}

internal static class ModelBProbeHarness
{
    public static async Task<TestFixture> BuildAsync(SqliteConnection connection, ModelBProbeCounter counter)
    {
        var fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<SdShelfProfile>();
                b.AddEntitySetProfile<SdPublicDocsProfile>();
                b.AddEntitySetProfile<SdSecureDocsProfile>();
            },
            configureServices: services =>
            {
                services.AddSingleton(counter);
                services.AddDbContext<ModelBProbeDbContext>(o => o.UseSqlite(connection));
            });

        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModelBProbeDbContext>();
        db.Database.EnsureCreated();
        db.SdOwners.Add(new SdOwner { Id = 500, Name = "raw-owner" });
        db.SdShelves.Add(new SdShelf { Id = 1, Name = "S1" });
        db.SdDocs.Add(new SdDoc { Id = 10, ShelfId = 1, Title = "D1", OwnerId = 500 });
        db.SaveChanges();
        return fx;
    }
}

#endregion

public sealed class Issue322ModelBProbeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;
    private ModelBProbeCounter _counter = null!;

    public Issue322ModelBProbeTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _counter = new ModelBProbeCounter();
        _fx = await ModelBProbeHarness.BuildAsync(_connection, _counter);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    // BASELINE for the (1b) delegate-safety argument: with PublicDocs contributing NO opinion, the
    // sole delegate is honored (RunDelegate). Under (1b) this becomes Blank — data silently lost.
    [Fact(Skip = "#322 diagnostic probe - investigation only; run manually")]
    public async Task Probe_NestedUndeclaredSibling_HonorsSoleDelegateToday()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/SdShelves?$expand=Docs($expand=Owner)");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"STATUS {(int)resp.StatusCode}  OwnerCalls={_counter.OwnerCalls}\n{body}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, _counter.OwnerCalls);            // RunDelegate, not Blank
        Assert.Contains("delegate-raw-owner", body);     // the delegate's own value was served
    }
}
