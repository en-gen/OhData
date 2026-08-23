using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.OData.Edm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

// #322 / #293 BOUNDARY PIN.
//
// #322's fix makes ONE thing EDM-aware: the structural member set the $select/$expand member-init
// projection is built from. It deliberately does NOT re-source IEntitySetEndpointSource
// .NavigationPropertyNames from the EDM, because THAT set is Model B's input:
// ResolveNavTreatment partitions a level's candidate profiles into DB (routes the nav) and DL
// (declares it with no route), and the FROZEN spec on #293 says
//
//     "A candidate that neither routes nor declares the nav has no opinion on it and is ignored."
//
// Convention-sourcing NavigationPropertyNames would make EVERY candidate declare EVERY EDM
// navigation of its type, emptying that category: the honored-sole-route case (DB = one route,
// DL = empty -> RunDelegate) would become DB = one route, DL = {the silent sibling} -> Blank. The
// delegate stops running and its data is replaced by null under a 200 — fail-closed, so not a
// security weakening, but silent data loss and a break of a frozen decision table.
//
// These two changes look similar and have opposite verdicts, so the boundary is PINNED here rather
// than assumed: the decision table is asserted directly (reflection onto the private
// ResolveNavTreatment, the single shared authority for both the pushdown gate and the delegate
// expansion path) for every multi-candidate shape, and end-to-end for the honored-sole-route case
// that (1b) was measured to regress.

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

/// <summary>
/// Candidate 1 over <c>SdDoc</c>: says NOTHING about <c>Owner</c>. Under Model B it has "no opinion",
/// even though the convention builder discovers the navigation and <c>$metadata</c> advertises it.
/// </summary>
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

/// <summary>
/// Candidate 2 over <c>SdDoc</c>: delegate-BACKED <c>Owner</c>. Its delegate rewrites the name so a
/// raw (Include/fixup) leak is distinguishable from the delegate's own result.
/// </summary>
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

/// <summary>Candidate 3 over <c>SdDoc</c>: DECLARES <c>Owner</c> delegate-less (a DL member).</summary>
public sealed class SdRawDocsProfile : EntitySetProfile<int, SdDoc>
{
    public SdRawDocsProfile(ModelBProbeDbContext db) : base(x => x.Id)
    {
        EntitySetName = "RawDocs";
        ExpandEnabled = true; SelectEnabled = true;
        GetQueryable = _ => Task.FromResult(db.SdDocs.AsQueryable());
        HasOptional<SdOwner>(x => x.Owner!);
    }
}

/// <summary>Candidate 4 over <c>SdDoc</c>: a SECOND, distinct delegate route for <c>Owner</c>.</summary>
public sealed class SdOtherSecureDocsProfile : EntitySetProfile<int, SdDoc>
{
    public SdOtherSecureDocsProfile(ModelBProbeDbContext db) : base(x => x.Id)
    {
        EntitySetName = "OtherSecureDocs";
        ExpandEnabled = true; SelectEnabled = true;
        GetQueryable = _ => Task.FromResult(db.SdDocs.AsQueryable());
        HasOptional<SdOwner>(
            x => x.Owner!,
            (id, _) => Task.FromResult<SdOwner?>(new SdOwner { Id = -1, Name = "other" }),
            refTargetEntitySet: null);
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

public sealed class Issue322ModelBClassificationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;
    private ModelBProbeCounter _counter = null!;

    public Issue322ModelBClassificationTests(ITestOutputHelper output) => _out = output;

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

    /// <summary>
    /// END-TO-END: the exact shape (1b) was measured to regress. <c>Owner</c> is reached NESTED (via a
    /// delegate-backed <c>Shelf.Docs</c>), so the candidate set really is the exact-EDM-type union
    /// { PublicDocs, SecureDocs } and not the root's URL-named-set-only special case. PublicDocs
    /// contributes no opinion, so the sole delegate is honoured. Under EDM-sourced
    /// NavigationPropertyNames this becomes Blank: <c>"Owner": null</c>, delegate never invoked.
    /// </summary>
    [Fact]
    public async Task NestedNavWithASilentSibling_StillHonorsTheSoleDelegate()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/SdShelves?$expand=Docs($expand=Owner)");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"STATUS {(int)resp.StatusCode}  OwnerCalls={_counter.OwnerCalls}\n{body}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, _counter.OwnerCalls);        // RunDelegate, not Blank
        Assert.Contains("delegate-raw-owner", body); // the delegate's own value was served
    }

    /// <summary>
    /// THE DECISION TABLE ITSELF, asserted directly against the private
    /// <c>OhDataEndpointFactory.ResolveNavTreatment</c> — the single authority shared by the pushdown
    /// gate and the delegate expansion path, so pinning it pins both. Every row of the FROZEN Model B
    /// table on #293 is covered, including the two multi-candidate disagreement rows, and the
    /// "silent sibling has no opinion" row that EDM-sourcing would delete.
    /// <para>
    /// Candidates are real registered profiles (not stubs) so the sets under test are the ones the
    /// framework actually builds; the order of the candidate list is varied where it could matter,
    /// since Model B is specified as order-independent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResolveNavTreatment_DecisionTable_IsUnchanged()
    {
        var counter = new ModelBProbeCounter();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<SdShelfProfile>();
                b.AddEntitySetProfile<SdPublicDocsProfile>();      // no opinion on Owner
                b.AddEntitySetProfile<SdSecureDocsProfile>();      // DB: route A
                b.AddEntitySetProfile<SdRawDocsProfile>();         // DL: declared, no route
                b.AddEntitySetProfile<SdOtherSecureDocsProfile>(); // DB: route B
            },
            configureServices: services =>
            {
                services.AddSingleton(counter);
                services.AddDbContext<ModelBProbeDbContext>(o => o.UseSqlite(connection));
            });

        var registration = fx.App.Services.GetRequiredService<OhDataRegistration>();
        IEntitySetEndpointSource Set(string name) =>
            registration.Profiles.Single(p => p.EntitySetName == name);

        IEntitySetEndpointSource silent = Set("PublicDocs");
        IEntitySetEndpointSource routeA = Set("SecureDocs");
        IEntitySetEndpointSource routeB = Set("OtherSecureDocs");
        IEntitySetEndpointSource declaredLess = Set("RawDocs");

        // The undeclared navigation IS in the EDM but NOT in the silent candidate's declared set —
        // the whole premise of the boundary. If this flips, NavigationPropertyNames has been
        // re-sourced from the EDM and the rows below are no longer measuring what they claim to.
        Assert.Contains(
            registration.EdmModel.EntityContainer!.FindEntitySet("PublicDocs")!.EntityType
                .NavigationProperties(),
            n => n.Name == "Owner");
        Assert.DoesNotContain("Owner", silent.NavigationPropertyNames);

        void Row(string label, string expected, params IEntitySetEndpointSource[] candidates)
        {
            string actual = InvokeResolveNavTreatment("Owner", candidates);
            _out.WriteLine($"{label,-58} -> {actual}");
            Assert.Equal(expected, actual);
        }

        // DB = empty  ->  ServeRaw
        Row("{} (no candidates)", "ServeRaw");
        Row("{silent}", "ServeRaw", silent);
        Row("{declared-less}", "ServeRaw", declaredLess);
        Row("{silent, declared-less}", "ServeRaw", silent, declaredLess);

        // DB = one route, DL = empty  ->  RunDelegate  (the honored-sole-route case)
        Row("{routeA}", "RunDelegate", routeA);
        Row("{routeA, silent}", "RunDelegate", routeA, silent);
        Row("{silent, routeA}", "RunDelegate", silent, routeA); // order-independent

        // DB non-empty AND DL non-empty  ->  Blank
        Row("{routeA, declared-less}", "Blank", routeA, declaredLess);
        Row("{declared-less, routeA}", "Blank", declaredLess, routeA); // order-independent
        Row("{routeA, declared-less, silent}", "Blank", routeA, declaredLess, silent);

        // DB >= 2 distinct routes  ->  Blank
        Row("{routeA, routeB}", "Blank", routeA, routeB);
        Row("{routeB, routeA}", "Blank", routeB, routeA); // order-independent
        Row("{routeA, routeB, silent}", "Blank", routeA, routeB, silent);
    }

    /// <summary>
    /// Calls the private <c>OhDataEndpointFactory.ResolveNavTreatment</c> and returns the
    /// <c>NavTreatment</c> enum member's name. Reflection because both the method and its
    /// <c>NavTreatmentResult</c> return type are private — deliberately so, since it is the shared
    /// authority and must not grow a second caller inside the library.
    /// </summary>
    private static string InvokeResolveNavTreatment(string navName, IReadOnlyList<IEntitySetEndpointSource> candidates)
    {
        MethodInfo method = typeof(OhDataEndpointFactory).GetMethod(
            "ResolveNavTreatment", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "OhDataEndpointFactory.ResolveNavTreatment not found — Model B's shared decision " +
                "authority was renamed or removed; this pin must be updated deliberately, not deleted.");

        object result = method.Invoke(null, new object[] { navName, candidates })!;
        object treatment = result.GetType()
            .GetProperty("Treatment", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(result)!;
        return treatment.ToString()!;
    }
}
