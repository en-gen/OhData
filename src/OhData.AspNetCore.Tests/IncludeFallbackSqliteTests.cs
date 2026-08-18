using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #305 Path A ("serve, not silently drop"): when the ROOT model of a $expand pushdown request has no
// public parameterless constructor (or another TryApplySelectProjection ineligibility reason),
// engagedExpandNavs used to be dropped to EDM-only under a lying 200 — the navigation serialized
// whatever the CLR property's default value already was (typically an empty collection), even though
// the framework had already vetted it as a delegate-less, pushable navigation. This suite proves the
// fix: the SAME engaged navigations are now served via EF Core's own Include (reflection-resolved —
// this package has no compile-time dependency on Microsoft.EntityFrameworkCore), bounded by
// MaxExpandTop exactly like the member-init projection path, or the request fails loud (400) when it
// needs something a plain Include cannot carry.
//
// NoCtorParent is a positional record — its only constructor takes (int Id, string Name), so
// typeof(NoCtorParent).GetConstructor(Type.EmptyTypes) is null and TryApplySelectProjection returns the
// query unchanged for EVERY request against this entity set, forcing Path A on every $expand.

public sealed record NoCtorParent(int Id, string Name)
{
    public List<NoCtorChild> Children { get; set; } = new();
}

public sealed class NoCtorChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

// Include-invalid fixture: FakeNav is CLR-shaped exactly like an eligible delegate-less collection nav
// (a settable List<T> that passes BuildExpandNavBinding's structural checks regardless of the #323
// back-reference guard — pure CLR reflection, no EF-model awareness), so BuildExpandNavBinding treats
// it as pushable — but OnModelCreating below explicitly Ignore()s it, so EF's OWN model does not
// recognize it as a navigation at all. Calling EF's Include on it must fail loud (400), not silently
// drop the data.
public sealed record IncludeInvalidParent(int Id, string Name)
{
    public List<IncludeInvalidChild> FakeNav { get; set; } = new();
}

public sealed class IncludeInvalidChild
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

// Fixture: a root model that can't support the member-init projection (no parameterless ctor —
// forces Path A / Include fallback) whose delegate-less collection nav's related type carries a
// typed back-reference to the root. Unlike the projection path (fresh POCOs, never cyclic — see
// ExpandPushdownSqliteTests's CycParent/CycChild), Include populates TRACKED entities. #323 (Change
// C) used to reject this with 400 before Include ever ran, rather than risk a 500; #325/#326
// (Option B) relaxed that — SerializeBounded now serves the tracked, cyclic graph safely instead.
public sealed record NoCtorCyclicParent(int Id, string Name)
{
    public List<NoCtorCyclicChild> Children { get; set; } = new();
}

public sealed class NoCtorCyclicChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
    public NoCtorCyclicParent? Parent { get; set; }
}

public sealed class IncludeFallbackDbContext : DbContext
{
    public IncludeFallbackDbContext(DbContextOptions<IncludeFallbackDbContext> options) : base(options) { }

    public DbSet<NoCtorParent> NoCtorParents => Set<NoCtorParent>();
    public DbSet<NoCtorChild> NoCtorChildren => Set<NoCtorChild>();
    public DbSet<IncludeInvalidParent> IncludeInvalidParents => Set<IncludeInvalidParent>();
    public DbSet<NoCtorCyclicParent> NoCtorCyclicParents => Set<NoCtorCyclicParent>();
    public DbSet<NoCtorCyclicChild> NoCtorCyclicChildren => Set<NoCtorCyclicChild>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NoCtorParent>()
            .HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);

        // FakeNav is deliberately NOT a real EF relationship — see the fixture remarks above.
        modelBuilder.Entity<IncludeInvalidParent>().Ignore(p => p.FakeNav);

        modelBuilder.Entity<NoCtorCyclicParent>()
            .HasMany(p => p.Children).WithOne(c => c.Parent!).HasForeignKey(c => c.ParentId);
    }
}

public sealed class NoCtorParentProfile : EntitySetProfile<int, NoCtorParent>
{
    public NoCtorParentProfile(IncludeFallbackDbContext db) : base(x => x.Id)
    {
        EntitySetName = "NoCtorParents";
        ExpandEnabled = true;
        OrderByEnabled = true;
        FilterEnabled = true;
        SelectEnabled = true;
        GetQueryable = _ => Task.FromResult(db.NoCtorParents.AsQueryable());
        HasMany(x => x.Children); // delegate-less → eligible for $expand pushdown
    }
}

public sealed class IncludeInvalidParentProfile : EntitySetProfile<int, IncludeInvalidParent>
{
    public IncludeInvalidParentProfile(IncludeFallbackDbContext db) : base(x => x.Id)
    {
        EntitySetName = "IncludeInvalidParents";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.IncludeInvalidParents.AsQueryable());
        HasMany(x => x.FakeNav); // CLR-eligible, but EF-ignored — Include must fail loud
    }
}

// TRACKING (default) — proves Change C fails loud regardless of the query's own tracking behavior.
public sealed class NoCtorCyclicParentProfile : EntitySetProfile<int, NoCtorCyclicParent>
{
    public NoCtorCyclicParentProfile(IncludeFallbackDbContext db) : base(x => x.Id)
    {
        EntitySetName = "NoCtorCyclicParents";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.NoCtorCyclicParents.AsQueryable());
        HasMany(x => x.Children); // delegate-less, bidirectional — CLR-eligible for pushdown
    }
}

// AsNoTracking() variant, same CLR/EDM shape — Change C's guard is purely type-based (it never
// inspects the query's tracking behavior), so this must ALSO fail loud with 400, not silently
// succeed just because the query happens not to track entities.
public sealed class NoCtorCyclicParentNoTrackingProfile : EntitySetProfile<int, NoCtorCyclicParent>
{
    public NoCtorCyclicParentNoTrackingProfile(IncludeFallbackDbContext db) : base(x => x.Id)
    {
        EntitySetName = "NoCtorCyclicParentsNoTracking";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.NoCtorCyclicParents.AsNoTracking().AsQueryable());
        HasMany(x => x.Children);
    }
}

internal static class IncludeFallbackSqliteHarness
{
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection, Action<EntitySetDefaults>? defaults = null, SqlCaptureSink? sink = null)
    {
        var fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                if (defaults is not null) b.WithDefaults(defaults);
                b.AddEntitySetProfile<NoCtorParentProfile>();
                b.AddEntitySetProfile<IncludeInvalidParentProfile>();
                b.AddEntitySetProfile<NoCtorCyclicParentProfile>();
                b.AddEntitySetProfile<NoCtorCyclicParentNoTrackingProfile>();
            },
            configureServices: services =>
            {
                services.AddDbContext<IncludeFallbackDbContext>(o =>
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
        var db = scope.ServiceProvider.GetRequiredService<IncludeFallbackDbContext>();
        db.Database.EnsureCreated();

        db.NoCtorParents.AddRange(
            new NoCtorParent(1, "P1"),
            new NoCtorParent(2, "P2"));
        db.NoCtorChildren.AddRange(
            new NoCtorChild { Id = 10, ParentId = 1, Name = "C1a" },
            new NoCtorChild { Id = 11, ParentId = 1, Name = "C1b" },
            new NoCtorChild { Id = 12, ParentId = 1, Name = "C1c" },
            new NoCtorChild { Id = 20, ParentId = 2, Name = "C2a" });

        db.IncludeInvalidParents.Add(new IncludeInvalidParent(1, "BadP1"));

        db.NoCtorCyclicParents.Add(new NoCtorCyclicParent(1, "CyP1"));
        db.NoCtorCyclicChildren.Add(new NoCtorCyclicChild { Id = 10, ParentId = 1, Name = "CyC1a" });

        db.SaveChanges();
        return fx;
    }
}

public sealed class IncludeFallbackServeTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await IncludeFallbackSqliteHarness.BuildAsync(_connection);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NoParameterlessCtor_BareExpand_ServesRealChildren_Not200WithEmptyNav()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/NoCtorParents?$orderby=id&$expand=Children");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement value = doc.RootElement.GetProperty("value");
        JsonElement p1 = value.EnumerateArray().Single(p => p.GetProperty("Name").GetString() == "P1");
        JsonElement p2 = value.EnumerateArray().Single(p => p.GetProperty("Name").GetString() == "P2");

        // Before #305: Children came back as [] (the CLR default) for BOTH parents under a 200 — the
        // silent-drop bug. Now: real rows.
        JsonElement p1Children = p1.GetProperty("Children");
        Assert.Equal(3, p1Children.GetArrayLength());
        Assert.Contains(p1Children.EnumerateArray(), c => c.GetProperty("Name").GetString() == "C1a");
        Assert.Contains(p1Children.EnumerateArray(), c => c.GetProperty("Name").GetString() == "C1b");
        Assert.Contains(p1Children.EnumerateArray(), c => c.GetProperty("Name").GetString() == "C1c");

        JsonElement p2Children = p2.GetProperty("Children");
        Assert.Single(p2Children.EnumerateArray());
        Assert.Equal("C2a", p2Children[0].GetProperty("Name").GetString());
    }

    [Fact]
    public async Task NoParameterlessCtor_NestedCountSelectTop_ServedAndShapedCorrectly()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/NoCtorParents?$orderby=id&$expand=Children($count=true;$select=name;$top=2)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement p1 = doc.RootElement.GetProperty("value")[0];

        // Full filtered count (3), independent of the $top window.
        Assert.Equal(3, p1.GetProperty("Children@odata.count").GetInt32());
        JsonElement children = p1.GetProperty("Children");
        Assert.Equal(2, children.GetArrayLength()); // windowed by $top=2

        // $select=name narrowed each child to just Name (plus whatever correlation the shaper keeps).
        foreach (JsonElement child in children.EnumerateArray())
        {
            Assert.True(child.TryGetProperty("Name", out _));
        }
    }

    [Theory]
    [InlineData("$expand=Children($filter=contains(name,'C1'))", "$filter")]
    [InlineData("$expand=Children($orderby=name desc)", "$orderby")]
    public async Task NoParameterlessCtor_NestedFilterOrOrderBy_FailsLoud400(string query, string optionToken)
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync($"/odata/NoCtorParents?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains(optionToken, body);
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("SQLITE", body);
    }
}

// MaxExpandTop must still bound materialization through the Include fallback — mirrors
// NestedCountCeilingBreachTests (MaxExpandTopTests.cs) but for the no-parameterless-ctor model.
public sealed class IncludeFallbackMaxExpandTopTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        // P1 has exactly 3 children; a ceiling of 2 puts the true count above the budget.
        _fx = await IncludeFallbackSqliteHarness.BuildAsync(_connection, defaults: d => d.MaxExpandTop = 2);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NestedCount_ChildCountAboveCeiling_Returns400_NotATruncatedCount()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/NoCtorParents?$orderby=id&$expand=Children($count=true)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 2", body);
        Assert.DoesNotContain("@odata.count", body);
    }

    [Fact]
    public async Task NestedCount_ChildCountAtOrUnderCeiling_Succeeds_WithExactCount()
    {
        // P2 has exactly 1 child — comfortably under the ceiling of 2.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/NoCtorParents?$orderby=id&$expand=Children($count=true)&$filter=name eq 'P2'");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Children@odata.count\":1", body);
    }

    // #313 parity: the SAME bare-leaf bound ApplyNavShape now composes on the member-init projection
    // path (BareLeafCeilingTests) must flow through the #305 Include fallback too — both call the same
    // ApplyNavShape (see ApplyIncludeFallback's remarks).
    [Fact]
    public async Task BareExpand_NoCountNoTop_ChildCountAboveCeiling_Returns400()
    {
        // P1 has exactly 3 children; a ceiling of 2 puts the true count above the budget — a BARE
        // $expand=Children (no $count, no $top) used to be entirely unbounded through this fallback too.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/NoCtorParents?$orderby=id&$expand=Children");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Children", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 2", body);
        Assert.Contains("Narrow it with a nested $filter", body);
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("SQLITE", body);
    }

    [Fact]
    public async Task BareExpand_NoCountNoTop_ChildCountUnderCeiling_Returns200_WithChildren()
    {
        // P2 has exactly 1 child — comfortably under the ceiling of 2. Isolate to P2 so the OVERALL
        // request doesn't 400 on P1's own over-ceiling Children array.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/NoCtorParents?$orderby=id&$expand=Children&$filter=name eq 'P2'");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement children = doc.RootElement.GetProperty("value")[0].GetProperty("Children");
        Assert.Single(children.EnumerateArray());
        Assert.Equal("C2a", children[0].GetProperty("Name").GetString());
    }

    // Review fold-in (F8): the two tests above assert only a status code, so neither can fail for the
    // RIGHT reason — a 400 that arrived some other way, or a 200 whose children were fetched in full
    // and trimmed afterwards, would satisfy both. #313's claim on this path is specifically that the
    // bound is composed INTO the SQL through ApplyIncludeFallback, so assert the emitted statement:
    // a capped registration carries EF Core's top-N-per-group window, an uncapped one carries none.
    [Fact]
    public async Task BareExpand_Capped_PushesARowBoundIntoSql_OnTheIncludeFallbackPath()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        // Ceiling 10 (over P1's 3 children, so nothing 400s) and MaxTop = null so the ONLY row bound
        // in the statement is the nested one — the default MaxTop composes an outer LIMIT over the
        // parent query that would satisfy a naive assertion regardless of #313.
        await using TestFixture fx = await IncludeFallbackSqliteHarness.BuildAsync(
            connection, defaults: d => { d.MaxExpandTop = 10; d.MaxTop = null; }, sink: sink);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/NoCtorParents?$orderby=id&$expand=Children");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = LastSelectAgainst(sink, "NoCtorParents");
        Assert.Contains("ROW_NUMBER()", sql);
        Assert.Contains("\"NoCtorChildren\"", sql);
    }

    [Fact]
    public async Task BareExpand_Uncapped_ComposesNoRowBoundInSql_OnTheIncludeFallbackPath()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await IncludeFallbackSqliteHarness.BuildAsync(
            connection, defaults: d => { d.MaxExpandTop = null; d.MaxTop = null; }, sink: sink);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/NoCtorParents?$orderby=id&$expand=Children");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = LastSelectAgainst(sink, "NoCtorParents");
        Assert.Contains("\"NoCtorChildren\"", sql);
        Assert.DoesNotContain("ROW_NUMBER()", sql);
        Assert.DoesNotContain("LIMIT", sql);
    }

    private static string LastSelectAgainst(SqlCaptureSink sink, string table) => sink.Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal) && s.Contains($"\"{table}\"", StringComparison.Ordinal))
        .Last();
}

// Include-invalid model: EF's own model does not recognize the "nav" (explicitly Ignore()d), so
// constructing/executing the Include must fail loud (400), never silently drop the navigation.
public sealed class IncludeFallbackInvalidModelTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await IncludeFallbackSqliteHarness.BuildAsync(_connection);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task EfIgnoredNavigation_Expand_FailsLoud400_NotSilentlyEmpty200()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/IncludeInvalidParents?$expand=FakeNav");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InvalidQueryOption", body);
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("SQLITE", body);
    }
}

// #323 (Change C) formerly rejected (400) a leaf expand under the Include fallback whose related
// type navigates back to the root model, rather than risk EF's own tracked-entity fixup closing a
// serialization cycle — unlike the member-init projection path, which structurally forecloses the
// same cycle via Change A (see ExpandPushdownCyclicFallbackTests.BidirectionalNav_Expand_PushesDown_WithJoin).
//
// #325/#326 (OWNER DECISIONS, FROZEN spec — Option B) RELAXED Change C: SerializeBounded
// (OhDataEndpointFactory.cs) makes the Include fallback's tracked-entity graph safe to serialize
// regardless of which two instances a cycle closes between, so this suite now asserts SERVED data
// (200) instead of the former 400. NOTE (fold-in review correction): the fixture below
// (NoCtorCyclicParent/Child) is the root-back-reference shape only — #326's own two reported shapes
// (a sibling cross-reference, and a self-referential LEAF element type, neither referencing the
// root) are covered separately by SiblingCrossReferenceIncludeFallbackTests and
// SelfReferentialLeafIncludeFallbackTests below.
public sealed class IncludeFallbackCyclicLeafTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await IncludeFallbackSqliteHarness.BuildAsync(_connection);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task CyclicLeaf_BareExpand_Returns200_WithRealChildren()
    {
        // Tracking (default) query — formerly rejected by Change C before Include ever ran.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/NoCtorCyclicParents?$expand=Children");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"CyC1a\"", body);
    }

    // Note: a nested $expand under the Include-fallback path (e.g. Children($expand=Parent)) is
    // untouched by #325/#326 either way — it is pre-existing Path-A engagement/eligibility
    // plumbing unrelated to serialization-cycle safety, out of this fix's scope. The IgnoreCycles-
    // disqualifying counter-example (Children($expand=Parent) returning the REAL parent, not null)
    // IS proven, on the member-init-projection-ineligible-but-pushdown-disabled EDM-only path: see
    // SerializeBoundedWalkerTests.EdmOnlyExpandCycleTests (T8-T11).

    [Fact]
    public async Task CyclicLeaf_AsNoTracking_AlsoReturns200_WithRealChildren()
    {
        // Change C's former guard was purely type-based — it never inspected the query's own
        // tracking behavior — so an AsNoTracking() query over the SAME cyclic shape must serve the
        // same way as the tracking query above.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/NoCtorCyclicParentsNoTracking?$expand=Children");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"CyC1a\"", body);
    }

    [Fact]
    public async Task UnidirectionalLeaf_BareExpand_StillServes200()
    {
        // Control: NoCtorParents/NoCtorChildren has NO back-reference (unidirectional), so it must
        // keep serving 200 with real data through the SAME Include fallback path exactly as before.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/NoCtorParents?$orderby=id&$expand=Children");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"C1a\"", body);
    }
}

// #326's ACTUAL two reported shapes (fold-in review, #4) — neither is the root-back-reference shape
// IncludeFallbackCyclicLeafTests above covers. Both use a positional-record root (no parameterless
// ctor) to force the #305 Path A Include fallback, exactly like NoCtorParent/NoCtorCyclicParent
// above, so the Include machinery (not the member-init projection path) is what's under test.

// ── Shape 1: sibling cross-reference — Invoice.Customers / Invoice.Orders are two independent leaf
// expands off the SAME root; InvCustomer.Orders and InvOrder.Customer cross-reference EACH OTHER,
// and NEITHER references the root Invoice at all. EF's automatic relationship fixup wires up that
// cross-reference among ANY entities tracked in the same DbContext/query regardless of which
// Include loaded them, so expanding Customers AND Orders together tracks a genuinely cyclic pair of
// leaf element types that #323's old root-only guard would never have caught (develop: 500). ───────

public sealed record Invoice(int Id, string Name)
{
    public List<InvCustomer> Customers { get; set; } = new();
    public List<InvOrder> Orders { get; set; } = new();
}

public sealed class InvCustomer
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public string Name { get; set; } = "";
    public List<InvOrder> Orders { get; set; } = new(); // sibling cross-reference, not to the root
}

public sealed class InvOrder
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public int? CustomerId { get; set; }
    public string Name { get; set; } = "";
    public InvCustomer? Customer { get; set; } // sibling cross-reference, not to the root
}

public sealed class SiblingCrossReferenceDbContext : DbContext
{
    public SiblingCrossReferenceDbContext(DbContextOptions<SiblingCrossReferenceDbContext> options) : base(options) { }

    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvCustomer> InvCustomers => Set<InvCustomer>();
    public DbSet<InvOrder> InvOrders => Set<InvOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>().HasMany(i => i.Customers).WithOne().HasForeignKey(c => c.InvoiceId);
        modelBuilder.Entity<Invoice>().HasMany(i => i.Orders).WithOne().HasForeignKey(o => o.InvoiceId);
        modelBuilder.Entity<InvCustomer>().HasMany(c => c.Orders).WithOne(o => o.Customer!).HasForeignKey(o => o.CustomerId);
    }
}

public sealed class InvoiceProfile : EntitySetProfile<int, Invoice>
{
    public InvoiceProfile(SiblingCrossReferenceDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Invoices";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Invoices.AsQueryable());
        HasMany(x => x.Customers); // delegate-less -> CLR-eligible for pushdown, forced to Path A
        HasMany(x => x.Orders);
    }
}

public sealed class SiblingCrossReferenceIncludeFallbackTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<InvoiceProfile>(),
            configureServices: services =>
                services.AddDbContext<SiblingCrossReferenceDbContext>(o => o.UseSqlite(_connection)));

        using var scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SiblingCrossReferenceDbContext>();
        db.Database.EnsureCreated();
        db.Invoices.Add(new Invoice(1, "Inv1"));
        db.InvCustomers.Add(new InvCustomer { Id = 1, InvoiceId = 1, Name = "Cust1" });
        db.InvOrders.Add(new InvOrder { Id = 1, InvoiceId = 1, CustomerId = 1, Name = "Ord1" });
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact] // #326's sibling-cross-reference repro: develop 500s here (Change C never caught it —
           // it only guarded a related type navigating back to the ROOT, and neither InvCustomer nor
           // InvOrder does; EF's fixup still wires up their mutual cross-reference once both are
           // tracked in the same context, closing a cycle whole-graph serialization can't handle).
    public async Task SiblingCrossReference_BothExpanded_Returns200_WithRealCustomerAndOrderData()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Invoices?$expand=Customers,Orders");
        string body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{resp.StatusCode}: {body}");
        Assert.Contains("\"Cust1\"", body);
        Assert.Contains("\"Ord1\"", body);
    }

    [Fact] // Control: Customers expanded alone never tracks any InvOrder, so no cross-reference cycle
           // is even possible — must serve 200 on develop and after the fix alike.
    public async Task CustomersExpandedAlone_Returns200()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Invoices?$expand=Customers");
        string body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{resp.StatusCode}: {body}");
        Assert.Contains("\"Cust1\"", body);
    }

    [Fact] // Control: Orders expanded alone never tracks any InvCustomer — same reasoning.
    public async Task OrdersExpandedAlone_Returns200()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Invoices?$expand=Orders");
        string body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{resp.StatusCode}: {body}");
        Assert.Contains("\"Ord1\"", body);
    }
}

// ── Shape 2: self-referential leaf element type — Org.Employees is a single leaf expand off the
// root; OrgEmployee.Manager/Reports self-reference AMONG the employees loaded for that one expand,
// and OrgEmployee has no navigation back to Org at all. Neither references the root, matching #326's
// second reported class (develop: 500). ──────────────────────────────────────────────────────────

public sealed record Org(int Id, string Name)
{
    public List<OrgEmployee> Employees { get; set; } = new();
}

public sealed class OrgEmployee
{
    public int Id { get; set; }
    public int OrgId { get; set; }
    public string Name { get; set; } = "";
    public int? ManagerId { get; set; }
    public OrgEmployee? Manager { get; set; }
    public List<OrgEmployee> Reports { get; set; } = new();
}

public sealed class SelfReferentialLeafDbContext : DbContext
{
    public SelfReferentialLeafDbContext(DbContextOptions<SelfReferentialLeafDbContext> options) : base(options) { }

    public DbSet<Org> Orgs => Set<Org>();
    public DbSet<OrgEmployee> OrgEmployees => Set<OrgEmployee>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Org>().HasMany(o => o.Employees).WithOne().HasForeignKey(e => e.OrgId);
        modelBuilder.Entity<OrgEmployee>()
            .HasMany(e => e.Reports).WithOne(e => e.Manager!).HasForeignKey(e => e.ManagerId);
    }
}

public sealed class OrgProfile : EntitySetProfile<int, Org>
{
    public OrgProfile(SelfReferentialLeafDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Orgs";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Orgs.AsQueryable());
        HasMany(x => x.Employees); // delegate-less -> CLR-eligible for pushdown, forced to Path A
    }
}

public sealed class SelfReferentialLeafIncludeFallbackTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<OrgProfile>(),
            configureServices: services =>
                services.AddDbContext<SelfReferentialLeafDbContext>(o => o.UseSqlite(_connection)));

        using var scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfReferentialLeafDbContext>();
        db.Database.EnsureCreated();
        db.Orgs.Add(new Org(1, "Acme"));
        db.OrgEmployees.Add(new OrgEmployee { Id = 1, OrgId = 1, Name = "Boss" });
        db.OrgEmployees.Add(new OrgEmployee { Id = 2, OrgId = 1, Name = "Report", ManagerId = 1 });
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact] // #326's self-referential-leaf repro: develop 500s here (Change C only guarded a
           // back-reference to the ROOT; OrgEmployee has none — its self-reference is entirely among
           // the leaf-expanded employees themselves, wired up by EF's own fixup once both rows are
           // tracked in the same context).
    public async Task Employees_Expanded_Returns200_WithBothEmployees()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Orgs?$expand=Employees");
        string body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{resp.StatusCode}: {body}");
        Assert.Contains("\"Boss\"", body);
        Assert.Contains("\"Report\"", body);
    }
}
