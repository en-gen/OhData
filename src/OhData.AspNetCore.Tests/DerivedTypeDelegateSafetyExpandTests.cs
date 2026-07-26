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

// Regression for issue #293: OhDataEndpointFactory.DelegateBackedNavNamesForClrType matched
// profiles by EXACT type identity (p.ModelType == clrType). A profile declared over a DERIVED
// type that attaches a delegate to a navigation declared on the BASE type was therefore invisible
// to the union: the navigation was treated as delegate-less and folded into an EF Include/JOIN at
// the parent level — a delegate bypass (the filtering/authorization the delegate performs is
// skipped entirely). Fixed by widening the match to assignability in either direction
// (clrType.IsAssignableFrom(p.ModelType) || p.ModelType.IsAssignableFrom(clrType)).
//
// Uses the same EF Core Sqlite + SQL-capture harness as ExpandPushdownSqliteTests /
// MultiLevelExpandPushdownSqliteTests: the assertion is that the delegate-backed navigation's own
// table is ABSENT from the emitted parent SQL (never JOINed), proving the branch deferred off
// pushdown instead of bypassing the delegate.

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
        GetQueryable = _ => Task.FromResult(db.Containers.AsQueryable());
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
        GetQueryable = _ => Task.FromResult(db.Things.AsQueryable());
        HasMany(x => x.Children); // delegate-LESS
    }
}

// Delegate-BACKED DERIVED-type profile attaching a delegate to the BASE-declared Children
// navigation. The #293 bug: DelegateBackedNavNamesForClrType matched profiles by
// `p.ModelType == DtBase` exactly, so this profile (ModelType == DtDerived) was invisible to the
// union computed when descending through Things — Children was (wrongly) treated as delegate-less
// there. This profile's own GetQueryable/handler is never exercised by the test below; only its
// NavigationRoutes registration (the delegate attached to Children) feeds the #293 union check.
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

// Regression for the CRITICAL leak found by adversarial review on top of #293: widening
// DelegateBackedNavNamesForClrType to assignability (above) closed the PUSHDOWN gate
// (TryBuildEngagedExpand), but the DELEGATE-PATH resolver (ExpandLevelAsync's routeMatches-empty
// branch) still only consulted whichever profiles ResolveRequestSourcesForEdmType matched by EXACT
// EDM type name for the current level — which never includes a DERIVED-type profile, since a
// derived CLR type gets its own, differently-named EDM entity type. So a navigation that is
// delegate-backed ONLY by a derived-type profile (delegating a nav declared on the base type)
// legitimately has zero routeMatches at that level, and the pre-fix code simply `continue`d past
// it — leaving whatever EF-fixup/Include data the PARENT delegate's own query happened to populate
// there to leak straight into the JSON, the derived-type profile's delegate (the real
// authorization boundary) never running at all.
//
// DpBase declares Children. DpBaseProfile ("Things") is delegate-less for it. DpDerivedProfile
// ("DerivedThings"), over the DERIVED DpDerived type, is the ONLY profile that route-backs
// Children — the auth boundary. Unlike DtContainer.Things above (delegate-less, pushable),
// DpRootProfile's OWN nav to Things IS delegate-backed, so the request goes through Stage 3
// (ExpandLevelAsync) directly rather than the pushdown gate — the DELEGATE-PATH × DERIVED-TYPE
// intersection the adversarial review flagged.
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
        GetQueryable = _ => Task.FromResult(db.Roots.AsQueryable());
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
        GetQueryable = _ => Task.FromResult(db.Things.AsQueryable());
        HasMany(x => x.Children); // delegate-LESS
    }
}

// Delegate-BACKED DERIVED-type profile — the auth boundary for Children. Its ModelType (DpDerived)
// differs from Things' own EDM entity type (DpBase), so ResolveRequestSourcesForEdmType's
// exact-type-name union for the Things level never includes this profile — routeMatches at the
// Children level is legitimately empty. Only DelegateBackedNavNamesForClrType's #293-widened,
// assignability-aware check (consulted at the no-route site by this fix) can see it.
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
    // The core #293 regression: a nested Things($expand=Children) must NEVER JOIN-load raw
    // Children, because a DERIVED-type profile (DerivedThings) attaches a delegate to the
    // BASE-declared Children navigation. The delegate is the security boundary; the whole branch
    // must defer off pushdown so raw child rows are never JOIN-loaded, bypassing it.
    [Fact]
    public async Task NestedExpand_DerivedProfileDelegate_NeverEfIncludes_BaseDeclaredNav()
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

        // The delegate-backed (via the DERIVED profile) Children nav must be ABSENT from the
        // parent JOIN at any depth — the raw Children table is never EF-included, so the
        // DerivedThings delegate is never bypassed.
        string sql = DerivedTypeSqliteHarness.LastSelectAgainst(sink, "Containers");
        Assert.DoesNotContain("\"Children\"", sql);

        // Whole branch deferred → the delegate-less parent Things stays EDM-only (empty), and the
        // raw "raw-child" body never leaks through a JOIN. (Deferral, not delegate invocation, is
        // the safe outcome here because the parent Container.Things nav is itself delegate-less —
        // mirroring MultiSetDelegateSafetyExpandTests' equivalent same-type scenario.)
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Things\":[]", body);
        Assert.DoesNotContain("raw-child", body);
    }

    // CRITICAL regression (adversarial review, found on top of #293): the DELEGATE-PATH resolver
    // (ExpandLevelAsync's no-route branch) must fail closed exactly like the pushdown gate already
    // does. Roots.Things is delegate-BACKED (unlike Containers.Things above), so this goes through
    // Stage 3 directly; DpDerivedProfile is the ONLY profile that route-backs Children, and it is
    // invisible to ResolveRequestSourcesForEdmType's exact-type union for the Things level (a
    // different EDM type). Before the fix, the no-route branch `continue`d past this, leaving the
    // Root delegate's own Include()-populated Children data (real rows — unlike the pushdown case
    // above, which defers to an empty Things) to leak straight into the JSON; the DerivedThings
    // delegate never runs, so its authorization is silently bypassed. Must hold in BOTH
    // registration orders.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedExpand_DerivedProfileDelegate_DelegatePath_BlanksRatherThanLeaksFixup(
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

        // Things itself IS delegate-backed and genuinely loaded (unlike the pushdown-deferred,
        // empty "Things":[] case above) — the leak under test is specifically in nested Children.
        Assert.Contains("\"Id\":10", body);

        // Fail closed: the DerivedThings delegate never ran, and the raw, Include-populated
        // Children data must never leak through via un-vetted EF fixup.
        Assert.Equal(0, counter.ChildrenCalls);
        Assert.DoesNotContain("dp-raw-child", body);
        Assert.Contains("\"Children\":[]", body);
    }
}
