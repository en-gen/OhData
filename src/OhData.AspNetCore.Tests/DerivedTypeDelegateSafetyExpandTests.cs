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

// Model B — declaring-set authority (OWNER DECISION 2026-07-26, FROZEN spec on issue #293):
// a profile declared over a DERIVED type is NOT a candidate for navigations reached through the
// BASE type's own entity set — a derived type gets its own, differently-named EDM entity type
// (exact-match, never CLR-type assignability), so the base set's candidate list is exactly the
// profile(s) exposing that base EDM type. A delegate the derived-type profile attaches to a
// nav declared on the base type therefore does NOT retroactively poison that nav for the base
// set: the base set's OWN declaration (here, delegate-less) is authoritative, and the navigation
// SERVES RAW when reached through it. This was previously mis-modeled (#293 widened matching to
// assignability, treating the derived profile's delegate as poisoning the base-set nav — over-
// blanking a legitimately delegate-less base-set declaration); Model B settles it the other way.
//
// Uses the same EF Core Sqlite + SQL-capture harness as ExpandPushdownSqliteTests /
// MultiLevelExpandPushdownSqliteTests: the assertion is that the base-declared navigation's own
// table IS present in the emitted parent SQL (JOIN'd/pushed), proving the base set's delegate-less
// declaration governs and the unrelated derived-type profile's delegate is never consulted.

// DtBase declares the Children navigation. DtDerived — a profile over the DERIVED type — is the
// one that attaches a getAll delegate to that same (inherited) navigation.
public class DtBase
{
    public int Id { get; set; }
    public int ContainerId { get; set; }
    public string Name { get; set; } = "";
    public List<DtChild> Children { get; set; } = new();
}

public sealed class DtDerived : DtBase
{
    public string Extra { get; set; } = "";
}

public sealed class DtChild
{
    public int Id { get; set; }
    public int BaseId { get; set; }
    public string Body { get; set; } = "";
}

// Parent whose OWN navigation to DtBase is delegate-less — Things($expand=Children) is therefore
// a pushdown CANDIDATE unless Children is correctly recognized as delegate-backed via the
// derived-type profile's registration.
public sealed class DtContainer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<DtBase> Things { get; set; } = new();
}

public sealed class DerivedTypeDbContext : DbContext
{
    public DerivedTypeDbContext(DbContextOptions<DerivedTypeDbContext> options) : base(options) { }

    public DbSet<DtContainer> Containers => Set<DtContainer>();
    public DbSet<DtBase> Things => Set<DtBase>();
    public DbSet<DtChild> Children => Set<DtChild>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DtContainer>().HasMany(c => c.Things).WithOne().HasForeignKey(t => t.ContainerId);
        b.Entity<DtBase>().HasMany(t => t.Children).WithOne().HasForeignKey(c => c.BaseId);
    }
}

public sealed class DerivedTypeDelegateCounter
{
    private int _childrenCalls;
    public int ChildrenCalls => _childrenCalls;
    public void CountChildrenCall() => Interlocked.Increment(ref _childrenCalls);
}

public sealed class DtContainerProfile : EntitySetProfile<int, DtContainer>
{
    public DtContainerProfile(DerivedTypeDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Containers";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => db.Containers.AsQueryable();
        HasMany(x => x.Things); // delegate-less → pushable
    }
}

// Delegate-LESS base-type profile. On its own, Children would fold into an EF ThenInclude JOIN.
public sealed class DtBaseProfile : EntitySetProfile<int, DtBase>
{
    public DtBaseProfile(DerivedTypeDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Things";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => db.Things.AsQueryable();
        HasMany(x => x.Children); // delegate-LESS
    }
}

// Delegate-BACKED DERIVED-type profile attaching a delegate to the BASE-declared Children
// navigation. Under Model B this profile is simply NOT a candidate for the "Things" level (exact
// EDM type DtBase) — DtDerived is a different exact EDM type — so its delegate never governs a nav
// reached through Things; DtBaseProfile's own delegate-less declaration does. This profile's own
// GetQueryable/handler is never exercised by the test below; it exists purely to prove its
// NavigationRoutes registration (the delegate attached to Children) does NOT leak into the
// candidate set for a DIFFERENT exact EDM type.
public sealed class DtDerivedProfile : EntitySetProfile<int, DtDerived>
{
    public DtDerivedProfile(DerivedTypeDbContext db, DerivedTypeDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "DerivedThings";
        ExpandEnabled = true;
        HasMany(x => x.Children,
            getAll: (baseId, ct) =>
            {
                counter.CountChildrenCall();
                return Task.FromResult<IEnumerable<DtChild>>(
                    db.Children.Where(c => c.BaseId == baseId).ToList());
            });
    }
}

internal static class DerivedTypeSqliteHarness
{
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection, DerivedTypeDelegateCounter counter, SqlCaptureSink? sink)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<DtContainerProfile>();
                b.AddEntitySetProfile<DtBaseProfile>();
                b.AddEntitySetProfile<DtDerivedProfile>();
            },
            configureServices: services =>
            {
                services.AddSingleton(counter);
                if (sink is not null) services.AddSingleton(sink);
                services.AddDbContext<DerivedTypeDbContext>(o =>
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
        DerivedTypeDbContext db = scope.ServiceProvider.GetRequiredService<DerivedTypeDbContext>();
        db.Database.EnsureCreated();

        db.Containers.Add(new DtContainer { Id = 1, Name = "Cont" });
        db.Things.Add(new DtBase { Id = 10, ContainerId = 1, Name = "T1" });
        db.Children.Add(new DtChild { Id = 100, BaseId = 10, Body = "raw-child" });
        db.SaveChanges();
        return fx;
    }

    public static string LastSelectAgainst(SqlCaptureSink sink, string table) => sink.Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal) && s.Contains($"\"{table}\"", StringComparison.Ordinal))
        .Last();
}

// Model B on the DELEGATE PATH (Stage 3 / ExpandLevelAsync) rather than the pushdown gate: proves
// the SAME "exact-EDM-type candidate set" rule holds regardless of which path resolves the nav.
//
// DpBase declares Children. DpBaseProfile ("Things") is delegate-less for it — under Model B that
// makes DpBaseProfile alone the "Things" level's candidate set (DpDerivedProfile is a DIFFERENT
// exact EDM type and is never a candidate here), so Children SERVES RAW: whatever the Root
// delegate's own Include(t => t.Children) already populated is the raw, authoritative answer.
// DpDerivedProfile's Children delegate is simply not the authority for a nav reached through the
// base set — it never runs. Unlike DtContainer.Things above (delegate-less, pushable),
// DpRootProfile's OWN nav to Things IS delegate-backed, so the request goes through Stage 3
// (ExpandLevelAsync) directly rather than the pushdown gate — proving the delegate path and the
// pushdown gate agree on the SAME candidate-resolution rule.
public class DpBase
{
    public int Id { get; set; }
    public int RootId { get; set; }
    public string Name { get; set; } = "";
    public List<DpChild> Children { get; set; } = new();
}

public sealed class DpDerived : DpBase
{
    public string Extra { get; set; } = "";
}

public sealed class DpChild
{
    public int Id { get; set; }
    public int BaseId { get; set; }
    public string Body { get; set; } = "";
}

public sealed class DpRoot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<DpBase> Things { get; set; } = new();
}

public sealed class DerivedPathDbContext : DbContext
{
    public DerivedPathDbContext(DbContextOptions<DerivedPathDbContext> options) : base(options) { }

    public DbSet<DpRoot> Roots => Set<DpRoot>();
    public DbSet<DpBase> Things => Set<DpBase>();
    public DbSet<DpChild> Children => Set<DpChild>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Roots.Things is served entirely by DpRootProfile's own getAll delegate below — not a
        // real EF relationship — so EF must not try to auto-discover one.
        b.Entity<DpRoot>().Ignore(r => r.Things);
        b.Entity<DpBase>().HasMany(t => t.Children).WithOne().HasForeignKey(c => c.BaseId);
    }
}

public sealed class DerivedPathDelegateCounter
{
    private int _childrenCalls;
    public int ChildrenCalls => _childrenCalls;
    public void CountChildrenCall() => Interlocked.Increment(ref _childrenCalls);
}

public sealed class DpRootProfile : EntitySetProfile<int, DpRoot>
{
    public DpRootProfile(DerivedPathDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Roots";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => db.Roots.AsQueryable();
        // Delegate-BACKED: Include()s Children as an incidental implementation detail, simulating
        // the realistic EF-fixup precondition — the already-populated CLR graph must not survive
        // into the response just because the nested $expand=Children can't resolve a route here.
        HasMany(x => x.Things,
            getAll: (rootId, ct) => Task.FromResult<IEnumerable<DpBase>>(
                db.Things.Include(t => t.Children).Where(t => t.RootId == rootId).ToList()));
    }
}

// Delegate-LESS base-type profile. Things' own NavigationRoutes has no entry for Children at all.
public sealed class DpBaseProfile : EntitySetProfile<int, DpBase>
{
    public DpBaseProfile(DerivedPathDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Things";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => db.Things.AsQueryable();
        HasMany(x => x.Children); // delegate-LESS
    }
}

// Delegate-BACKED DERIVED-type profile. Its ModelType (DpDerived) differs from Things' own EDM
// entity type (DpBase), so it is never in the "Things" level's exact-EDM-type candidate set — under
// Model B its delegate on Children simply does not apply to a nav reached through the base-typed
// Things set; DpBaseProfile's own delegate-less declaration is authoritative there instead.
public sealed class DpDerivedProfile : EntitySetProfile<int, DpDerived>
{
    public DpDerivedProfile(DerivedPathDbContext db, DerivedPathDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "DerivedThings";
        ExpandEnabled = true;
        HasMany(x => x.Children,
            getAll: (baseId, ct) =>
            {
                counter.CountChildrenCall();
                return Task.FromResult<IEnumerable<DpChild>>(
                    db.Children.Where(c => c.BaseId == baseId).ToList());
            });
    }
}

internal static class DerivedPathSqliteHarness
{
    // thingsBeforeDerived toggles registration order — the leak must close regardless.
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection, DerivedPathDelegateCounter counter, bool thingsBeforeDerived)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<DpRootProfile>();
                if (thingsBeforeDerived)
                {
                    b.AddEntitySetProfile<DpBaseProfile>();
                    b.AddEntitySetProfile<DpDerivedProfile>();
                }
                else
                {
                    b.AddEntitySetProfile<DpDerivedProfile>();
                    b.AddEntitySetProfile<DpBaseProfile>();
                }
            },
            configureServices: services =>
            {
                services.AddSingleton(counter);
                services.AddDbContext<DerivedPathDbContext>(o => o.UseSqlite(connection));
            });

        using IServiceScope scope = fx.App.Services.CreateScope();
        DerivedPathDbContext db = scope.ServiceProvider.GetRequiredService<DerivedPathDbContext>();
        db.Database.EnsureCreated();

        db.Roots.Add(new DpRoot { Id = 1, Name = "R1" });
        db.Things.Add(new DpBase { Id = 10, RootId = 1, Name = "T1" });
        db.Children.Add(new DpChild { Id = 100, BaseId = 10, Body = "dp-raw-child" });
        db.SaveChanges();
        return fx;
    }
}

public sealed class DerivedTypeDelegateSafetyExpandTests
{
    // Model B flip (was: NeverEfIncludes_BaseDeclaredNav / defer+empty): a nested
    // Things($expand=Children) now SERVES RAW (pushed / EF-Included) — DtBaseProfile ("Things") is
    // the ONLY candidate for the exact DtBase EDM type, its OWN Children declaration is
    // delegate-less, and DtDerivedProfile ("DerivedThings") — a DIFFERENT exact EDM type — is never
    // consulted for it. The derived-type profile's delegate is simply not the authority here.
    [Fact]
    public async Task NestedExpand_BaseDeclaredNav_ServesRaw_DerivedProfileDelegateNotAuthoritative()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        var counter = new DerivedTypeDelegateCounter();
        await using TestFixture fx = await DerivedTypeSqliteHarness.BuildAsync(connection, counter, sink);
        sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/Containers?$orderby=id&$expand=Things($expand=Children)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Children is pushed (JOIN'd) into the parent SQL — served raw, not deferred — because
        // DtBaseProfile's own exact-EDM-type candidate set has DB(Children) = ∅.
        string sql = DerivedTypeSqliteHarness.LastSelectAgainst(sink, "Containers");
        Assert.Contains("\"Children\"", sql);

        // Things is genuinely loaded (pushed), and its raw Children survive into the response —
        // correctly, per Model B — while DerivedThings' delegate never runs (it isn't the
        // authority for a nav reached through the base-typed Things set).
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Id\":10", body);
        Assert.Contains("raw-child", body);
        Assert.Equal(0, counter.ChildrenCalls);
    }

    // Model B flip (was: DelegatePath_BlanksRatherThanLeaksFixup / blank): on the DELEGATE PATH
    // (Roots.Things is itself delegate-backed, so this goes through Stage 3/ExpandLevelAsync
    // directly rather than the pushdown gate), Children now SERVES RAW too — the derived-type
    // profile is simply not the authority for a nav reached through the base-typed Things set, so
    // whatever the Root delegate's own Include(t => t.Children) already populated is correctly
    // exposed rather than blanked. Must hold in BOTH registration orders (the candidate-set
    // resolution never reads registration order).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedExpand_DelegatePath_BaseDeclaredNav_ServesRaw_RegardlessOfOrder(
        bool thingsBeforeDerived)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var counter = new DerivedPathDelegateCounter();
        await using TestFixture fx = await DerivedPathSqliteHarness.BuildAsync(connection, counter, thingsBeforeDerived);

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/Roots?$expand=Things($expand=Children)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();

        // Things itself IS delegate-backed and genuinely loaded.
        Assert.Contains("\"Id\":10", body);

        // Served raw: the DerivedThings delegate never runs (it isn't the authority here), and the
        // Root delegate's own Include-populated Children data is exactly what the response shows.
        Assert.Equal(0, counter.ChildrenCalls);
        Assert.Contains("dp-raw-child", body);
    }
}
