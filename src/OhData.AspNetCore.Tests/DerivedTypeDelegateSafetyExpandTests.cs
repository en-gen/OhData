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
}
