using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #206 phase 2 (Option A1): SQL-shape proof for $expand Include pushdown under the provenance-auto
// safety model. THE INVARIANT UNDER TEST: the framework pushes a navigation down (folds it into the
// parents query as a JOIN) ONLY when it was declared WITHOUT a custom expand delegate; a navigation
// declared WITH a delegate always expands through the delegate and never JOINs. Uses EF Core Sqlite
// over a keep-alive in-memory connection with command-text capture — the same harness as
// SelectPushdownSqliteTests / ExpandPushdownSqliteHarness below.

// ── Entity types ────────────────────────────────────────────────────────────────────────────────

// Delegate-less collection nav: PushChild has no back-reference to PushParent → acyclic → the
// pushdown projection can materialize x.Children.ToList() without a serialization cycle.
public sealed class PushParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<PushChild> Children { get; set; } = new();
}

public sealed class PushChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

// Delegate-BACKED collection nav: identical shape, but the profile supplies a getAll delegate, so
// the nav must expand through that delegate (no JOIN).
public sealed class DelParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<DelChild> Children { get; set; } = new();
}

public sealed class DelChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

// Single-valued delegate-less nav (HasOptional): RefHolder → RefTarget, nullable FK so a holder
// with no target exercises the null-related-entity path.
public sealed class RefHolder
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? TargetId { get; set; }
    // Non-nullable annotation so the HasOptional selector satisfies the `class` constraint; the
    // nullable FK (TargetId) makes the relationship optional, and the value is null at runtime for
    // a holder with no target (exercised by the null-parity assertion).
    public RefTarget Target { get; set; } = null!;
}

public sealed class RefTarget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

// Cyclic delegate-less nav: CycChild has a typed back-reference to CycParent, so the startup static
// guard (TypeHasNavigationTo) excludes it from pushdown — expanding it must gracefully stay
// EDM-only (no JOIN, no 500), never materialize a parent<->child cycle.
public sealed class CycParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<CycChild> Kids { get; set; } = new();
}

public sealed class CycChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
    public CycParent? Parent { get; set; }
}

// Self-referential (recursive) delegate-less hierarchy: only a recursive relationship makes the
// OData parser accept $expand=Children($levels=N). A self-referential nav is inherently cyclic, so
// the startup static guard excludes it — its purpose here is to prove a $levels expand is NOT
// pushed down and does not 500 (it stays EDM-only).
public sealed class OrgNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public List<OrgNode> Children { get; set; } = new();
}

public sealed class ExpandPushDbContext : DbContext
{
    public ExpandPushDbContext(DbContextOptions<ExpandPushDbContext> options) : base(options) { }

    public DbSet<PushParent> PushParents => Set<PushParent>();
    public DbSet<PushChild> PushChildren => Set<PushChild>();
    public DbSet<DelParent> DelParents => Set<DelParent>();
    public DbSet<DelChild> DelChildren => Set<DelChild>();
    public DbSet<RefHolder> RefHolders => Set<RefHolder>();
    public DbSet<RefTarget> RefTargets => Set<RefTarget>();
    public DbSet<CycParent> CycParents => Set<CycParent>();
    public DbSet<CycChild> CycChildren => Set<CycChild>();
    public DbSet<OrgNode> OrgNodes => Set<OrgNode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Each child's FK is "ParentId" (not the EF-convention "PushParentId"), so declare the
        // relationship explicitly or EF would treat ParentId as an unrelated scalar.
        modelBuilder.Entity<PushParent>().HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
        modelBuilder.Entity<DelParent>().HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
        modelBuilder.Entity<RefHolder>().HasOne(h => h.Target).WithMany().HasForeignKey(h => h.TargetId);
        modelBuilder.Entity<CycParent>().HasMany(p => p.Kids).WithOne(c => c.Parent!).HasForeignKey(c => c.ParentId);
        modelBuilder.Entity<OrgNode>().HasMany(n => n.Children).WithOne().HasForeignKey(n => n.ParentId);
    }
}

/// <summary>Counts delegate invocations so tests can prove the delegate path was (or was not) taken.</summary>
public sealed class ExpandDelegateCounter
{
    private int _childrenCalls;
    public int ChildrenCalls => _childrenCalls;
    public void CountChildrenCall() => Interlocked.Increment(ref _childrenCalls);
}

// ── Profiles ────────────────────────────────────────────────────────────────────────────────────

// Bare HasMany — NO delegate → opts IN to $expand pushdown.
public sealed class PushParentProfile : EntitySetProfile<int, PushParent>
{
    public PushParentProfile(ExpandPushDbContext db) : base(x => x.Id)
    {
        EntitySetName = "PushParents";
        SelectEnabled = true;
        ExpandEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.PushParents.AsQueryable());
        HasMany(x => x.Children); // delegate-less → SQL-JOIN expansion
    }
}

// HasMany WITH a getAll delegate → opts OUT of pushdown (delegate owns expansion).
public sealed class DelParentProfile : EntitySetProfile<int, DelParent>
{
    public DelParentProfile(ExpandPushDbContext db, ExpandDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "DelParents";
        SelectEnabled = true;
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.DelParents.AsQueryable());
        HasMany(x => x.Children,
            getAll: (parentId, ct) =>
            {
                counter.CountChildrenCall();
                return Task.FromResult<IEnumerable<DelChild>>(
                    db.DelChildren.Where(c => c.ParentId == parentId).ToList());
            });
    }
}

// Bare HasOptional — NO delegate → single-valued pushdown.
public sealed class RefHolderProfile : EntitySetProfile<int, RefHolder>
{
    public RefHolderProfile(ExpandPushDbContext db) : base(x => x.Id)
    {
        EntitySetName = "RefHolders";
        SelectEnabled = true;
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.RefHolders.AsQueryable());
        HasOptional(x => x.Target); // delegate-less → SQL-JOIN expansion
    }
}

// Bare HasMany on a bidirectional (cyclic) relationship → excluded by the static guard.
public sealed class CycParentProfile : EntitySetProfile<int, CycParent>
{
    public CycParentProfile(ExpandPushDbContext db) : base(x => x.Id)
    {
        EntitySetName = "CycParents";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.CycParents.AsQueryable());
        HasMany(x => x.Kids); // delegate-less BUT cyclic → stays EDM-only
    }
}

// Bare HasMany on a self-referential nav → the only shape the parser accepts $levels on.
public sealed class OrgNodeProfile : EntitySetProfile<int, OrgNode>
{
    public OrgNodeProfile(ExpandPushDbContext db) : base(x => x.Id)
    {
        EntitySetName = "OrgNodes";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.OrgNodes.AsQueryable());
        HasMany(x => x.Children); // delegate-less, self-referential (cyclic)
    }
}

/// <summary>Test-local helper: seed a keep-alive Sqlite host wired to a SQL-capture sink.</summary>
internal static class ExpandPushdownSqliteHarness
{
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection,
        ExpandDelegateCounter counter,
        SqlCaptureSink? sink,
        Action<EntitySetDefaults>? defaults = null)
    {
        var fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                if (defaults is not null) b.WithDefaults(defaults);
                b.AddEntitySetProfile<PushParentProfile>();
                b.AddEntitySetProfile<DelParentProfile>();
                b.AddEntitySetProfile<RefHolderProfile>();
                b.AddEntitySetProfile<CycParentProfile>();
                b.AddEntitySetProfile<OrgNodeProfile>();
            },
            configureServices: services =>
            {
                services.AddSingleton(counter);
                if (sink is not null) services.AddSingleton(sink);
                services.AddDbContext<ExpandPushDbContext>(o =>
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

        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpandPushDbContext>();
        db.Database.EnsureCreated();

        db.PushParents.AddRange(
            new PushParent { Id = 1, Name = "P1" },
            new PushParent { Id = 2, Name = "P2" });
        db.PushChildren.AddRange(
            new PushChild { Id = 10, ParentId = 1, Name = "C1a" },
            new PushChild { Id = 11, ParentId = 1, Name = "C1b" },
            new PushChild { Id = 20, ParentId = 2, Name = "C2a" });

        db.DelParents.AddRange(
            new DelParent { Id = 1, Name = "P1" },
            new DelParent { Id = 2, Name = "P2" });
        db.DelChildren.AddRange(
            new DelChild { Id = 10, ParentId = 1, Name = "C1a" },
            new DelChild { Id = 11, ParentId = 1, Name = "C1b" },
            new DelChild { Id = 20, ParentId = 2, Name = "C2a" });

        db.RefTargets.AddRange(
            new RefTarget { Id = 100, Name = "T100" });
        db.RefHolders.AddRange(
            new RefHolder { Id = 1, Name = "H1", TargetId = 100 },
            new RefHolder { Id = 2, Name = "H2", TargetId = null }); // null related entity

        db.CycParents.AddRange(new CycParent { Id = 1, Name = "CP1" });
        db.CycChildren.AddRange(new CycChild { Id = 10, ParentId = 1, Name = "K1" });

        db.OrgNodes.AddRange(
            new OrgNode { Id = 1, Name = "Root", ParentId = null },
            new OrgNode { Id = 2, Name = "Leaf", ParentId = 1 });

        db.SaveChanges();
        return fx;
    }

    /// <summary>The most recent executed SELECT against <paramref name="table"/>.</summary>
    public static string LastSelectAgainst(SqlCaptureSink sink, string table) => sink.Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal) && s.Contains($"\"{table}\"", StringComparison.Ordinal))
        .Last();
}

public sealed class ExpandPushdownSqliteTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private ExpandDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new ExpandDelegateCounter();
        _fx = await ExpandPushdownSqliteHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task DelegatelessNav_Expand_JoinsChildrenInSingleParentsQuery()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/PushParents?$expand=Children&$orderby=id");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // Single JOIN'd query: the parents SELECT references the children table.
        string sql = ExpandPushdownSqliteHarness.LastSelectAgainst(_sink, "PushParents");
        Assert.Contains("\"PushChildren\"", sql);

        // Wire: each parent carries its expanded children, PascalCase (#179).
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Children\"", body);
        Assert.Contains("\"C1a\"", body);
        Assert.Contains("\"C2a\"", body);
    }

    [Fact]
    public async Task DelegatelessNav_ExpandComposesWithSelect_PrunesColumnsAndJoins()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/PushParents?$select=name&$expand=Children&$orderby=id");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = ExpandPushdownSqliteHarness.LastSelectAgainst(_sink, "PushParents");
        // $select pruning still pushed (Name projected) AND the $expand JOIN folded into one query.
        Assert.Contains("\"Name\"", sql);
        Assert.Contains("\"PushChildren\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Children\"", body);
        Assert.Contains("\"Name\":\"P1\"", body);
    }

    [Fact]
    public async Task DelegatelessNav_NoExpand_OmitsNavigation()
    {
        // #176/#179: a navigation that was not $expand'd must be omitted from the payload entirely.
        var resp = await _fx.Client.GetAsync("/odata/PushParents?$orderby=id");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"Children\"", body);
    }

    [Fact]
    public async Task SingleValuedDelegatelessNav_Expand_PushedDownWithNullParity()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/RefHolders?$expand=Target&$orderby=id");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // Single query joining the target table (single-valued pushdown).
        string sql = ExpandPushdownSqliteHarness.LastSelectAgainst(_sink, "RefHolders");
        Assert.Contains("\"RefTargets\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        // H1 → its target; H2 → null related entity.
        Assert.Contains("\"T100\"", body);
        Assert.Contains("\"Target\":null", body);
    }

    [Fact]
    public async Task Levels_OnSelfReferentialNav_PushedAsBoundedRecursion()
    {
        // #206: $expand=Children($levels=2) on a delegate-less self-referential nav is now pushed as a
        // BOUNDED (cycle-free) projection — the OrgNodes query self-JOINs OrgNodes to load each level.
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/OrgNodes?$expand=Children($levels=2)&$orderby=id");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // Self-JOIN: the OrgNodes SELECT references the OrgNodes table more than once (parent + child).
        string sql = ExpandPushdownSqliteHarness.LastSelectAgainst(_sink, "OrgNodes");
        Assert.True(
            System.Text.RegularExpressions.Regex.Matches(sql, "\"OrgNodes\"").Count >= 2,
            "a $levels self-referential expand must self-JOIN the OrgNodes table");

        string body = await resp.Content.ReadAsStringAsync();
        // Root's children were JOIN-loaded (PascalCase): Root → [Leaf], and Leaf (level 2) → [] (bounded).
        Assert.Contains("\"Name\":\"Root\"", body);
        Assert.Contains("\"Name\":\"Leaf\"", body);
        Assert.Contains("\"Children\":[]", body); // the deepest loaded level terminates as an empty page
    }
}

// A delegate-backed navigation must NEVER be pushed down: the delegate is invoked and the parents
// query has no JOIN.
public sealed class ExpandPushdownDelegateNavTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private ExpandDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new ExpandDelegateCounter();
        _fx = await ExpandPushdownSqliteHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task DelegateBackedNav_Expand_UsesDelegate_NoJoin()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/DelParents?$expand=Children&$orderby=id");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // The parents query must be a plain SELECT with no JOIN to the children table.
        string sql = ExpandPushdownSqliteHarness.LastSelectAgainst(_sink, "DelParents");
        Assert.DoesNotContain("\"DelChildren\"", sql);
        // The delegate loaded the children instead — proving the safety property (never bypassed).
        Assert.True(_counter.ChildrenCalls > 0, "a delegate-backed nav must expand through its delegate");

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Children\"", body);
        Assert.Contains("\"C1a\"", body);
    }
}

// A cyclic (bidirectional) delegate-less navigation is excluded by the startup static guard: the
// $expand must gracefully stay EDM-only — no JOIN, no 500, no materialized parent<->child cycle.
public sealed class ExpandPushdownCyclicFallbackTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private ExpandDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new ExpandDelegateCounter();
        _fx = await ExpandPushdownSqliteHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task CyclicNav_Expand_GracefulFallback_NoJoinNo500()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/CycParents?$expand=Kids&$orderby=id");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = ExpandPushdownSqliteHarness.LastSelectAgainst(_sink, "CycParents");
        Assert.DoesNotContain("\"CycChildren\"", sql);
    }
}

// The ExpandPushdownEnabled=false opt-out keeps a delegate-less nav unexpandable (EDM-only): no
// JOIN, no delegate (there is none) — the behavior before pushdown existed.
public sealed class ExpandPushdownDisabledTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private ExpandDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new ExpandDelegateCounter();
        _fx = await ExpandPushdownSqliteHarness.BuildAsync(
            _connection, _counter, _sink, defaults: d => d.ExpandPushdownEnabled = false);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task Disabled_DelegatelessNav_NotPushed_NoJoin()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/PushParents?$expand=Children&$orderby=id");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = ExpandPushdownSqliteHarness.LastSelectAgainst(_sink, "PushParents");
        Assert.DoesNotContain("\"PushChildren\"", sql);
    }
}

// Non-EF sources must never engage pushdown (a projection over LINQ-to-objects would read
// un-populated navigations). This in-memory profile has an eligible delegate-less collection nav,
// but its GetQueryable is a plain List.AsQueryable() — the IsEfCoreBacked gate keeps it EDM-only.
public sealed class InMemoryPushParentProfile : EntitySetProfile<int, PushParent>
{
    private static readonly List<PushParent> _parents = new()
    {
        new PushParent { Id = 1, Name = "P1", Children = { new PushChild { Id = 10, ParentId = 1, Name = "C1a" } } },
        new PushParent { Id = 2, Name = "P2" },
    };

    public InMemoryPushParentProfile() : base(x => x.Id)
    {
        EntitySetName = "InMemoryPushParents";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(_parents.AsQueryable());
        HasMany(x => x.Children); // delegate-less, but non-EF source → cannot be pushed down
    }
}

public sealed class ExpandPushdownNonEfFallbackTests
{
    [Fact]
    public async Task NonEfSource_DelegatelessNav_StaysEdmOnly_NoCrash()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<InMemoryPushParentProfile>());

        var resp = await fx.Client.GetAsync("/odata/InMemoryPushParents?$expand=Children");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // The non-EF source cannot be pushed down; without a delegate the nav simply stays
        // EDM-only. The request must still succeed (no 500).
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"P1\"", body);
    }
}
