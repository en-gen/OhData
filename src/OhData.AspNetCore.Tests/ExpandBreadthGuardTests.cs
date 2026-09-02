using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #429: $expand cost was bounded on the DEPTH axis and unbounded on BREADTH -- there was no breadth
// limit of any kind. Translation multiplies by ~3 per level AND by the navigations expanded at each
// level, so capping depth leaves the other factor free. Measured at the DEFAULT depth of 3 on a
// six-navigation model, before the guard:
//
//   navs/level=1  ->   240 ms   len=1440
//   navs/level=4  -> 1,010 ms   len=1696
//   navs/level=6  -> 4,084 ms   len=1952
//
// 4.1 s of single-core CPU for a 1,952-byte response, at defaults, unauthenticated -- and EF's
// compiled-query cache is no defence, since each distinct navigation SUBSET is a distinct key.
//
// The count spans the WHOLE TREE: a per-level cap of B under a depth ceiling of 6 still admits B^6
// (55,986 at B=6). Counting distinct NAMES would be weaker still -- the most expensive shapes reuse
// six names over six levels.
public class ExpandBreadthGuardTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    // The shipped default is 50; these fixtures use 5 so the boundary can be walked cheaply.
    private const int Cap = 5;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<BrdNodeProfile>().AddEntitySetProfile<BrdEnumerableProfile>(),
            configureServices: s => s.AddDbContext<BrdDbContext>(o => o.UseSqlite(_connection)));

        using IServiceScope scope = _fx.App.Services.CreateScope();
        BrdDbContext db = scope.ServiceProvider.GetRequiredService<BrdDbContext>();
        db.Database.EnsureCreated();
        db.BrdNodes.Add(new BrdNode { Id = 1 });
        db.BrdNodes.Add(new BrdNode { Id = 2, FA = 1, FB = 1, FC = 1 });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    // ── The boundary ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExactlyAtTheLimit_IsServed()
    {
        // 5 nodes: A, B, C at the root plus two under A.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/BrdNodes?$expand=A($expand=B,C),B,C");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task OneOverTheLimit_Is400WithAnActionableMessage()
    {
        // 6 nodes: the same shape plus one more leaf.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/BrdNodes?$expand=A($expand=A,B,C),B,C");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains($"more than {Cap} navigations", body);
        // The message must name the knob — a 400 that does not say how to raise the limit is a
        // dead end for the developer who hits it legitimately.
        Assert.Contains("MaxExpandBreadth", body);
    }

    // Breadth is counted across levels, so the SAME node budget is spent whether it is laid out
    // flat or deep. A per-level cap would have let this through.
    [Fact]
    public async Task DeepAndNarrow_CountsAgainstTheSameBudget()
    {
        HttpResponseMessage ok = await _fx.Client.GetAsync(
            "/odata/BrdNodes?$expand=A($expand=A($expand=A))"); // 3 nodes over 3 levels
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // 6 nodes over 3 levels — one per level on two root branches, plus two leaves.
        HttpResponseMessage tooWide = await _fx.Client.GetAsync(
            "/odata/BrdNodes?$expand=A($expand=A($expand=A,B)),B($expand=B($expand=B))");
        Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);
    }

    // A $levels=N item is ONE clause node but N projection levels, and it costs like N. Counting it
    // as 1 would let `$expand=A($levels=3),B($levels=3)` — six levels of nested projection — through
    // a cap of 5.
    [Fact]
    public async Task LevelsCountsAsItsResolvedLevelCount()
    {
        // $levels=3 + $levels=2 = 5 nodes exactly.
        HttpResponseMessage ok = await _fx.Client.GetAsync(
            "/odata/BrdNodes?$expand=A($levels=3),B($levels=2)");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // $levels=3 + $levels=3 = 6.
        HttpResponseMessage over = await _fx.Client.GetAsync(
            "/odata/BrdNodes?$expand=A($levels=3),B($levels=3)");
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
    }

    // ── Reach ───────────────────────────────────────────────────────────────────────────────────

    // The guard is a statement about what the client may ASK for, not about how the server would
    // have served it — the same rule MaxExpandTop's explicit nested $top follows. The GetAll path
    // has no pushdown at all, so this is the pushdown-independence assertion.
    [Fact]
    public async Task AppliesOnTheGetAllPath_WhichHasNoPushdown()
    {
        HttpResponseMessage ok = await _fx.Client.GetAsync(
            "/odata/BrdEnumerables?$expand=A($expand=B,C),B,C");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage over = await _fx.Client.GetAsync(
            "/odata/BrdEnumerables?$expand=A($expand=A,B,C),B,C");
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
    }

    // GetById shares the same $expand pipeline and was wired at the same time — a guard on the
    // collection routes alone would leave the single-entity route as the way around it.
    [Fact]
    public async Task AppliesOnTheGetByIdRoute()
    {
        HttpResponseMessage ok = await _fx.Client.GetAsync(
            "/odata/BrdNodes(1)?$expand=A($expand=B,C),B,C");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage over = await _fx.Client.GetAsync(
            "/odata/BrdNodes(1)?$expand=A($expand=A,B,C),B,C");
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
    }

    // ── Configuration ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Default_Is50() => Assert.Equal(50, new EntitySetDefaults().MaxExpandBreadth);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Defaults_NonPositive_Throws(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntitySetDefaults { MaxExpandBreadth = value });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Profile_NonPositive_Throws(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrdBadBreadthProfile(value));

    [Fact]
    public void Defaults_ValidValue_RoundTrips() =>
        Assert.Equal(7, new EntitySetDefaults { MaxExpandBreadth = 7 }.MaxExpandBreadth);

    // A profile that sets nothing inherits the server-wide value, so lowering it in WithDefaults
    // hardens every entity set at once.
    [Fact]
    public async Task ServerWideDefault_AppliesToAProfileThatSetsNothing()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.WithDefaults(d => d.MaxExpandBreadth = 2).AddEntitySetProfile<BrdInheritingProfile>(),
            configureServices: s => s.AddDbContext<BrdDbContext>(o => o.UseSqlite(connection)));

        using (IServiceScope scope = fx.App.Services.CreateScope())
        {
            BrdDbContext db = scope.ServiceProvider.GetRequiredService<BrdDbContext>();
            db.Database.EnsureCreated();
            db.BrdNodes.Add(new BrdNode { Id = 1 });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK,
            (await fx.Client.GetAsync("/odata/BrdInheriting?$expand=A,B")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await fx.Client.GetAsync("/odata/BrdInheriting?$expand=A,B,C")).StatusCode);
    }
}

public sealed class BrdNode
{
    public int Id { get; set; }
    public int? FA { get; set; }
    public int? FB { get; set; }
    public int? FC { get; set; }
    public List<BrdNode> A { get; set; } = new();
    public List<BrdNode> B { get; set; } = new();
    public List<BrdNode> C { get; set; } = new();
}

public sealed class BrdDbContext : DbContext
{
    public BrdDbContext(DbContextOptions<BrdDbContext> options) : base(options) { }
    public DbSet<BrdNode> BrdNodes => Set<BrdNode>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BrdNode>().HasMany(n => n.A).WithOne().HasForeignKey(n => n.FA);
        b.Entity<BrdNode>().HasMany(n => n.B).WithOne().HasForeignKey(n => n.FB);
        b.Entity<BrdNode>().HasMany(n => n.C).WithOne().HasForeignKey(n => n.FC);
    }
}

// Delegate-LESS: every navigation is pushdown-eligible, which is the expensive shape the guard
// exists for.
public sealed class BrdNodeProfile : EntitySetProfile<int, BrdNode>
{
    public BrdNodeProfile(BrdDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BrdNodes";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true;
        OrderByEnabled = true; CountEnabled = true;
        MaxExpandBreadth = 5;
        GetQueryable = _ => Task.FromResult(db.BrdNodes.AsQueryable());
        GetById = (id, ct) => Task.FromResult(db.BrdNodes.FirstOrDefault(n => n.Id == id));
        HasMany(x => x.A); HasMany(x => x.B); HasMany(x => x.C);
    }
}

// GetAll (IEnumerable): no pushdown on this path at all. The guard applies anyway.
public sealed class BrdEnumerableProfile : EntitySetProfile<int, BrdNode>
{
    public BrdEnumerableProfile(BrdDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BrdEnumerables";
        ExpandEnabled = true; SelectEnabled = true; CountEnabled = true;
        MaxExpandBreadth = 5;
        GetAll = ct => Task.FromResult<IEnumerable<BrdNode>>(db.BrdNodes.ToList());
        HasMany(x => x.A, getAll: (id, ct) => Task.FromResult<IEnumerable<BrdNode>>(Array.Empty<BrdNode>()));
        HasMany(x => x.B, getAll: (id, ct) => Task.FromResult<IEnumerable<BrdNode>>(Array.Empty<BrdNode>()));
        HasMany(x => x.C, getAll: (id, ct) => Task.FromResult<IEnumerable<BrdNode>>(Array.Empty<BrdNode>()));
    }
}

// Sets no MaxExpandBreadth of its own — inherits whatever WithDefaults resolved.
public sealed class BrdInheritingProfile : EntitySetProfile<int, BrdNode>
{
    public BrdInheritingProfile(BrdDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BrdInheriting";
        ExpandEnabled = true; SelectEnabled = true; CountEnabled = true;
        GetQueryable = _ => Task.FromResult(db.BrdNodes.AsQueryable());
        HasMany(x => x.A); HasMany(x => x.B); HasMany(x => x.C);
    }
}

internal sealed class BrdBadBreadthProfile : EntitySetProfile<int, BrdNode>
{
    public BrdBadBreadthProfile(int value) : base(x => x.Id)
    {
        EntitySetName = "BrdBadBreadth";
        MaxExpandBreadth = value;
    }
}
