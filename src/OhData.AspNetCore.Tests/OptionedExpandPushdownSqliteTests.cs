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

// #206 phase 2 (optioned expand): nested $expand options — $select / $filter / $orderby / $top /
// $skip / $count — on a DELEGATE-LESS navigation are honored. Filter/orderby/paging push to SQL as a
// filtered Include (proven via captured command text); $count and $select shape the serialized
// (camelCase) JSON. THE SAFETY INVARIANT still holds: a delegate-backed navigation is never pushed
// down — it always expands through its delegate, whatever nested options the request carries. Uses
// EF Core Sqlite over a keep-alive in-memory connection with command-text capture (same
// SqlCaptureSink harness as SelectPushdownSqliteTests / ExpandPushdownSqliteTests).

// ── Entity types ────────────────────────────────────────────────────────────────────────────────

public sealed class OeParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    // A second structural column so the select-pushdown decoupling test can prove the root SELECT is
    // NOT column-pruned (Note survives) when SelectPushdownEnabled=false but $expand pushdown runs.
    public string Note { get; set; } = "";
    public List<OeChild> Children { get; set; } = new();
}

public sealed class OeChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public int Rank { get; set; }
}

// A parent carrying BOTH a delegate-less nav (Pushed) and a delegate-backed nav (Delegated) so a
// single $expand=Pushed,Delegated request proves the first JOINs and the second uses its delegate.
public sealed class MixParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<MixPushChild> Pushed { get; set; } = new();
    public List<MixDelChild> Delegated { get; set; } = new();
}

public sealed class MixPushChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
    // A grandchild navigation so a valid 2-level $expand=Pushed($expand=Subs) path exists for the
    // multi-level-deferral test (the parser needs Subs to be a real navigation in the EDM).
    public List<MixGrand> Subs { get; set; } = new();
}

public sealed class MixGrand
{
    public int Id { get; set; }
    public int PushChildId { get; set; }
    public string Label { get; set; } = "";
}

public sealed class MixDelChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

// Single-valued delegate-less nav (HasOptional) for nested-$select-on-a-reference coverage.
public sealed class OeRefHolder
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? TargetId { get; set; }
    public OeRefTarget Target { get; set; } = null!;
}

public sealed class OeRefTarget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
}

public sealed class OptionedExpandDbContext : DbContext
{
    public OptionedExpandDbContext(DbContextOptions<OptionedExpandDbContext> options) : base(options) { }

    public DbSet<OeParent> OeParents => Set<OeParent>();
    public DbSet<OeChild> OeChildren => Set<OeChild>();
    public DbSet<MixParent> MixParents => Set<MixParent>();
    public DbSet<MixPushChild> MixPushChildren => Set<MixPushChild>();
    public DbSet<MixGrand> MixGrands => Set<MixGrand>();
    public DbSet<MixDelChild> MixDelChildren => Set<MixDelChild>();
    public DbSet<OeRefHolder> OeRefHolders => Set<OeRefHolder>();
    public DbSet<OeRefTarget> OeRefTargets => Set<OeRefTarget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OeParent>().HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
        modelBuilder.Entity<MixParent>().HasMany(p => p.Pushed).WithOne().HasForeignKey(c => c.ParentId);
        modelBuilder.Entity<MixParent>().HasMany(p => p.Delegated).WithOne().HasForeignKey(c => c.ParentId);
        modelBuilder.Entity<MixPushChild>().HasMany(c => c.Subs).WithOne().HasForeignKey(g => g.PushChildId);
        modelBuilder.Entity<OeRefHolder>().HasOne(h => h.Target).WithMany().HasForeignKey(h => h.TargetId);
    }
}

public sealed class OptionedExpandDelegateCounter
{
    private int _delegatedCalls;
    public int DelegatedCalls => _delegatedCalls;
    public void CountDelegatedCall() => Interlocked.Increment(ref _delegatedCalls);
}

// ── Profiles ────────────────────────────────────────────────────────────────────────────────────

// Bare HasMany + every nested option enabled → delegate-less nav is optioned-pushdown-eligible.
public sealed class OeParentProfile : EntitySetProfile<int, OeParent>
{
    public OeParentProfile(OptionedExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "OeParents";
        SelectEnabled = true;
        ExpandEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        GetQueryable = _ => Task.FromResult(db.OeParents.AsQueryable());
        HasMany(x => x.Children); // delegate-less → optioned SQL-JOIN expansion
    }
}

public sealed class MixParentProfile : EntitySetProfile<int, MixParent>
{
    public MixParentProfile(OptionedExpandDbContext db, OptionedExpandDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "MixParents";
        SelectEnabled = true;
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.MixParents.AsQueryable());
        HasMany(x => x.Pushed); // delegate-less → pushed
        HasMany(x => x.Delegated,
            getAll: (parentId, ct) =>
            {
                counter.CountDelegatedCall();
                return Task.FromResult<IEnumerable<MixDelChild>>(
                    db.MixDelChildren.Where(c => c.ParentId == parentId).ToList());
            });
    }
}

public sealed class OeRefHolderProfile : EntitySetProfile<int, OeRefHolder>
{
    public OeRefHolderProfile(OptionedExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "OeRefHolders";
        SelectEnabled = true;
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.OeRefHolders.AsQueryable());
        HasOptional(x => x.Target); // delegate-less single-valued → pushed
    }
}

internal static class OptionedExpandSqliteHarness
{
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection,
        OptionedExpandDelegateCounter counter,
        SqlCaptureSink? sink,
        Action<EntitySetDefaults>? defaults = null)
    {
        var fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                if (defaults is not null) b.WithDefaults(defaults);
                b.AddEntitySetProfile<OeParentProfile>();
                b.AddEntitySetProfile<MixParentProfile>();
                b.AddEntitySetProfile<OeRefHolderProfile>();
            },
            configureServices: services =>
            {
                services.AddSingleton(counter);
                if (sink is not null) services.AddSingleton(sink);
                services.AddDbContext<OptionedExpandDbContext>(o =>
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
        var db = scope.ServiceProvider.GetRequiredService<OptionedExpandDbContext>();
        db.Database.EnsureCreated();

        db.OeParents.AddRange(
            new OeParent { Id = 1, Name = "P1", Note = "note-1" },
            new OeParent { Id = 2, Name = "P2", Note = "note-2" });
        db.OeChildren.AddRange(
            new OeChild { Id = 10, ParentId = 1, Name = "Alpha", Active = true, Rank = 3 },
            new OeChild { Id = 11, ParentId = 1, Name = "Bravo", Active = false, Rank = 1 },
            new OeChild { Id = 12, ParentId = 1, Name = "Charlie", Active = true, Rank = 2 },
            new OeChild { Id = 13, ParentId = 1, Name = "Delta", Active = true, Rank = 4 },
            new OeChild { Id = 20, ParentId = 2, Name = "Echo", Active = true, Rank = 1 });

        db.MixParents.Add(new MixParent { Id = 1, Name = "MP1" });
        db.MixPushChildren.AddRange(
            new MixPushChild { Id = 100, ParentId = 1, Name = "Push-A" },
            new MixPushChild { Id = 101, ParentId = 1, Name = "Push-B" });
        db.MixGrands.Add(new MixGrand { Id = 300, PushChildId = 100, Label = "Grand-A" });
        db.MixDelChildren.AddRange(
            new MixDelChild { Id = 200, ParentId = 1, Name = "Del-A" });

        db.OeRefTargets.Add(new OeRefTarget { Id = 500, Name = "T500", Code = "XYZ" });
        db.OeRefHolders.AddRange(
            new OeRefHolder { Id = 1, Name = "H1", TargetId = 500 },
            new OeRefHolder { Id = 2, Name = "H2", TargetId = null });

        db.SaveChanges();
        return fx;
    }

    public static string LastSelectAgainst(SqlCaptureSink sink, string table) => sink.Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal) && s.Contains($"\"{table}\"", StringComparison.Ordinal))
        .Last();
}

public sealed class OptionedExpandPushdownSqliteTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private OptionedExpandDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new OptionedExpandDelegateCounter();
        _fx = await OptionedExpandSqliteHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NestedFilter_PushedToSql_FiltersExpandedCollection()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/OeParents?$orderby=id&$expand=Children($filter=active eq true)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // The expanded collection is JOIN-loaded in the parents query (single query, no delegate).
        string sql = OptionedExpandSqliteHarness.LastSelectAgainst(_sink, "OeParents");
        Assert.Contains("\"OeChildren\"", sql);

        // The nested $filter predicate reached SQL (not applied in memory): the JOIN'd child query
        // references the Active column it filters on.
        Assert.Contains("\"Active\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        // Active children only: Alpha, Charlie, Delta present; the inactive Bravo filtered out.
        Assert.Contains("\"Alpha\"", body);
        Assert.Contains("\"Charlie\"", body);
        Assert.Contains("\"Delta\"", body);
        Assert.DoesNotContain("\"Bravo\"", body);
    }

    [Fact]
    public async Task NestedOrderByDescTop_PushedToSql_OrdersAndPages()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/OeParents?$orderby=id&$expand=Children($orderby=name desc;$top=1)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // Ordering + paging pushed to SQL: EF emits a ROW_NUMBER window (not an in-memory Take) to
        // page the JOIN'd children per parent.
        string sql = OptionedExpandSqliteHarness.LastSelectAgainst(_sink, "OeParents");
        Assert.Contains("ROW_NUMBER()", sql);
        Assert.Contains("\"OeChildren\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        // P1 names desc: Delta, Charlie, Bravo, Alpha → top 1 → only Delta (Echo is P2's only child).
        Assert.Contains("\"Delta\"", body);
        Assert.DoesNotContain("\"Alpha\"", body);
        Assert.DoesNotContain("\"Charlie\"", body);
        Assert.DoesNotContain("\"Bravo\"", body);
    }

    [Fact]
    public async Task NestedOrderBySkipTop_PushedToSql_Pages()
    {
        var resp = await _fx.Client.GetAsync("/odata/OeParents?$orderby=id&$expand=Children($orderby=name;$skip=1;$top=1)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        // P1 names asc: Alpha, Bravo, Charlie, Delta → skip 1, take 1 → only Bravo (P2's single Echo
        // is skipped away, leaving P2 with an empty children page).
        Assert.Contains("\"Bravo\"", body);
        Assert.DoesNotContain("\"Alpha\"", body);
        Assert.DoesNotContain("\"Charlie\"", body);
        Assert.DoesNotContain("\"Delta\"", body);
    }

    [Fact]
    public async Task NestedTop_NoNestedOrderBy_StabilizedByChildKey()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/OeParents?$orderby=id&$expand=Children($top=2)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // Paging pushed to SQL as a ROW_NUMBER window even without a nested $orderby (the child key is
        // appended as a deterministic tiebreaker), so the page is stable rather than provider-arbitrary.
        string sql = OptionedExpandSqliteHarness.LastSelectAgainst(_sink, "OeParents");
        Assert.Contains("ROW_NUMBER()", sql);

        string body = await resp.Content.ReadAsStringAsync();
        // P1's first two children by key (Id 10, 11) → Alpha, Bravo; Charlie/Delta paged out.
        Assert.Contains("\"Alpha\"", body);
        Assert.Contains("\"Bravo\"", body);
        Assert.DoesNotContain("\"Charlie\"", body);
        Assert.DoesNotContain("\"Delta\"", body);
    }

    [Fact]
    public async Task NestedCount_EmitsInlineCountOfFullFilteredCollection()
    {
        var resp = await _fx.Client.GetAsync("/odata/OeParents?$orderby=id&$expand=Children($count=true)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        // camelCase nav key → children@odata.count. P1 has 4 children, P2 has 1.
        Assert.Contains("\"children@odata.count\":4", body);
        Assert.Contains("\"children@odata.count\":1", body);
    }

    [Fact]
    public async Task NestedCountWithTop_CountsFullSet_ButPagesItems()
    {
        var resp = await _fx.Client.GetAsync("/odata/OeParents?$orderby=id&$expand=Children($count=true;$orderby=name;$top=2)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        // P1 count is the full filtered set (4), not the page.
        Assert.Contains("\"children@odata.count\":4", body);
        // Only P1's first 2 by name asc: Alpha, Bravo.
        Assert.Contains("\"Alpha\"", body);
        Assert.Contains("\"Bravo\"", body);
        Assert.DoesNotContain("\"Charlie\"", body);
        Assert.DoesNotContain("\"Delta\"", body);
    }

    [Fact]
    public async Task NestedCountWithTop_NoNestedOrderBy_PagesDeterministicallyByChildKey()
    {
        _sink.Clear();
        // $count defers paging to the JSON window; without a nested $orderby the SQL order must still be
        // stabilized by the child key (adversarial-review hardening, same class as #241) so WHICH rows
        // land in the page are deterministic rather than provider-arbitrary.
        var resp = await _fx.Client.GetAsync("/odata/OeParents?$orderby=id&$expand=Children($count=true;$top=2)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // The stabilizing ORDER BY is emitted even though paging is deferred to JSON under $count.
        string sql = OptionedExpandSqliteHarness.LastSelectAgainst(_sink, "OeParents");
        Assert.Contains("ORDER BY", sql);

        string body = await resp.Content.ReadAsStringAsync();
        // Full filtered count is reported; the page is P1's first two children by key (Id 10, 11).
        Assert.Contains("\"children@odata.count\":4", body);
        Assert.Contains("\"Alpha\"", body);  // Id 10
        Assert.Contains("\"Bravo\"", body);  // Id 11
        Assert.DoesNotContain("\"Charlie\"", body); // Id 12 — paged out deterministically
        Assert.DoesNotContain("\"Delta\"", body);   // Id 13 — paged out deterministically
    }

    [Fact]
    public async Task NestedSelect_ProjectsExpandedElements_CamelCase()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/OeParents?$orderby=id&$expand=Children($select=name)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        // camelCase nav element property preserved; non-selected structural props pruned.
        Assert.Contains("\"children\":", body);
        Assert.Contains("\"name\":\"Alpha\"", body);
        Assert.DoesNotContain("\"Name\":", body);  // no PascalCase leak from a SelectExpandWrapper
        Assert.DoesNotContain("\"active\":", body); // pruned by nested $select
        Assert.DoesNotContain("\"rank\":", body);   // pruned by nested $select
    }

    [Fact]
    public async Task RootPagingAndExpandTogether_BothApply()
    {
        var resp = await _fx.Client.GetAsync("/odata/OeParents?$orderby=id&$top=1&$expand=Children($filter=active eq true)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        // Root $top=1 → only P1; its Active children present, P2/Echo excluded by the root page.
        Assert.Contains("\"P1\"", body);
        Assert.DoesNotContain("\"P2\"", body);
        Assert.Contains("\"Alpha\"", body);
        Assert.DoesNotContain("\"Bravo\"", body); // nested filter still applied under paging
    }

    [Fact]
    public async Task SingleValuedNestedSelect_ProjectsReference_CamelCase()
    {
        var resp = await _fx.Client.GetAsync("/odata/OeRefHolders?$orderby=id&$expand=Target($select=name)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"T500\"", body); // selected, camelCase
        Assert.DoesNotContain("\"XYZ\"", body);     // Code pruned by nested $select
        Assert.Contains("\"target\":null", body);   // H2 has no target → null parity preserved
    }
}

// The safety invariant under nested options: a delegate-backed navigation is never pushed down even
// when the $expand carries nested options — the delegate loads it and the parents query has no JOIN.
public sealed class OptionedExpandSafetyTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private OptionedExpandDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new OptionedExpandDelegateCounter();
        _fx = await OptionedExpandSqliteHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task DelegateNavWithNestedSelect_ResolvesThroughDelegate_NoJoin()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/MixParents?$orderby=id&$expand=Delegated($select=name)");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // The delegate-backed nav must expand through its delegate, never a JOIN.
        string sql = OptionedExpandSqliteHarness.LastSelectAgainst(_sink, "MixParents");
        Assert.DoesNotContain("\"MixDelChildren\"", sql);
        Assert.True(_counter.DelegatedCalls > 0, "a delegate-backed nav must expand through its delegate");

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Del-A\"", body);
    }

    [Fact]
    public async Task MixedExpand_PushesFirstJoinsDelegatesSecond()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/MixParents?$orderby=id&$expand=Pushed,Delegated");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        // The pushed nav JOINs into the parents query; the delegate nav does not.
        string sql = OptionedExpandSqliteHarness.LastSelectAgainst(_sink, "MixParents");
        Assert.Contains("\"MixPushChildren\"", sql);
        Assert.DoesNotContain("\"MixDelChildren\"", sql);
        Assert.True(_counter.DelegatedCalls > 0, "the delegate nav must still use its delegate");

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Push-A\"", body); // pushed via JOIN
        Assert.Contains("\"Del-A\"", body);  // delegated
    }
}

// #206: multi-level nested $expand under a delegate-less pushed nav is now pushed as a deeper JOIN
// (EF ThenInclude), while a delegate-backed sibling still resolves through its delegate — proving the
// two paths coexist in one request and the delegate is never EF-included at any depth.
public sealed class OptionedExpandMultiLevelTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private OptionedExpandDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new OptionedExpandDelegateCounter();
        _fx = await OptionedExpandSqliteHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task MixedNav_NestedExpandUnderPushedNav_PushesGrandchildJoinsDelegatesSibling()
    {
        // Pushed($expand=Subs) is a delegate-less 2-level chain → pushed as MixPushChildren JOIN
        // MixGrands (ThenInclude). Delegated is delegate-backed → still resolves through its delegate,
        // never a JOIN. The whole request succeeds (no 500).
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/MixParents?$orderby=id&$expand=Pushed($expand=Subs),Delegated");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = OptionedExpandSqliteHarness.LastSelectAgainst(_sink, "MixParents");
        // Both delegate-less levels JOIN into the parents query; the delegate nav does not.
        Assert.Contains("\"MixPushChildren\"", sql);
        Assert.Contains("\"MixGrands\"", sql);
        Assert.DoesNotContain("\"MixDelChildren\"", sql);
        Assert.True(_counter.DelegatedCalls > 0, "the delegate nav must still use its delegate");

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"MP1\"", body);
        Assert.Contains("\"Push-A\"", body);   // level-1 pushed child
        Assert.Contains("\"Grand-A\"", body);  // level-2 pushed grandchild (multi-level ThenInclude)
        Assert.Contains("\"Del-A\"", body);    // delegate sibling resolved through its delegate
    }
}

// The expand-pushdown / select-pushdown decoupling: with SelectPushdownEnabled=false, an
// $expand + $select request must still JOIN the expanded nav (expand pushdown independent) while
// NOT column-pruning the root SELECT (select pushdown honored-off).
public sealed class OptionedExpandSelectDecouplingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private OptionedExpandDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new OptionedExpandDelegateCounter();
        _fx = await OptionedExpandSqliteHarness.BuildAsync(
            _connection, _counter, _sink, defaults: d => d.SelectPushdownEnabled = false);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task SelectPushdownDisabled_ExpandStillJoins_RootColumnsNotPruned()
    {
        var resp = await _fx.Client.GetAsync("/odata/OeParents?$orderby=id&$select=name&$expand=Children");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = OptionedExpandSqliteHarness.LastSelectAgainst(_sink, "OeParents");
        // Expand pushdown independent of select pushdown → the children table is still JOIN-loaded.
        Assert.Contains("\"OeChildren\"", sql);
        // Select pushdown disabled → the root SELECT is NOT column-pruned to Name: the non-selected
        // Note column survives (with the decoupling bug it would be pruned away by the expand path).
        Assert.Contains("\"Note\"", sql);
    }
}
