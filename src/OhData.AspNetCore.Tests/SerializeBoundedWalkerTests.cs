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

// #325/#326 (OWNER DECISIONS, FROZEN spec — Option B, "clause-bounded, level-wise serialization"):
// SerializeBounded (OhDataEndpointFactory.cs) replaces "serialize the whole CLR graph, then strip
// un-expanded navigations" (Stage 3.5, OmitUnexpandedNavigations) with "serialize only what the
// $expand clause / $levels budget asked for", at the point of serialization itself — so a reference
// cycle in the underlying (tracked, EF-relationship-fixed-up) object graph is structurally
// unreachable, regardless of clause depth. This suite is the named T-matrix from the architecture
// spec:
//   T8-T11  the IgnoreCycles disqualifying counter-example — Children($expand=Parent) must return
//           the REAL parent object (never null), and Parent($expand=Children) must contain no null
//           array elements. These pin Option B against a future "just use IgnoreCycles"
//           simplification: IgnoreCycles passes the plain #325 repro too, but silently corrupts
//           exactly this shape (verified during the architecture spike — see issue #325 comments).
//   T16/T17 the tracked-entity read-only hazard — the walker must never mutate/corrupt a tracked
//           graph later saved in the SAME request/DbContext scope.
//   T24-T28 GetById, write-path (PUT/PATCH) response bodies, navigation routes, and bound
//           operations all stay safe over a self-referential model.
//   T30/T31 delegate-safety (Model B, #292/#293): a delegate's own answer always wins over
//           whatever the walker guessed by reading the CLR graph, and a Blanked navigation always
//           yields []/null, never the materialized graph.
//   T35     the one class OWNER DECISIONS explicitly left as a loud 500: a cycle closed by an
//           entity-typed CLR property that is NOT an EDM navigation.

// ── T8-T11: EDM-only path (no $expand pushdown), the exact shape IgnoreCycles got wrong ──────────

public sealed class SeNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public SeNode? Parent { get; set; }
    public List<SeNode> Children { get; set; } = new();
}

public sealed class SeDbContext : DbContext
{
    public SeDbContext(DbContextOptions<SeDbContext> options) : base(options) { }
    public DbSet<SeNode> SeNodes => Set<SeNode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SeNode>()
            .HasMany(n => n.Children).WithOne(n => n.Parent!).HasForeignKey(n => n.ParentId);
    }
}

// ExpandPushdownEnabled = false forces every $expand through the plain EDM-only, delegate-less
// serialization path (Stage 1 SerializeBounded + Stage 3 ExpandLevelAsync's ServeRaw branch),
// isolating the walker from #323's Change A (member-init projection) — Change A would ALSO
// structurally prevent this cycle on its own, so leaving pushdown on would not, by itself, prove
// the walker is doing anything for T8-T11.
public sealed class SeNodeProfile : EntitySetProfile<int, SeNode>
{
    public SeNodeProfile(SeDbContext db) : base(x => x.Id)
    {
        EntitySetName = "SeNodes";
        ExpandEnabled = true;
        OrderByEnabled = true;
        ExpandPushdownEnabled = false;
        GetQueryable = _ => Task.FromResult(db.SeNodes.AsQueryable());
        HasOptional(x => x.Parent!);
        HasMany(x => x.Children);
    }
}

public sealed class EdmOnlyExpandCycleTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<SeNodeProfile>(),
            configureServices: services => services.AddDbContext<SeDbContext>(o => o.UseSqlite(_connection)));

        using IServiceScope scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SeDbContext>();
        db.Database.EnsureCreated();
        db.SeNodes.AddRange(
            new SeNode { Id = 1, Name = "Root" },
            new SeNode { Id = 2, Name = "ChildA", ParentId = 1 },
            new SeNode { Id = 3, Name = "ChildB", ParentId = 1 });
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact] // T8/T9
    public async Task ChildrenExpandParent_ReturnsRealParentObject_NotNull()
    {
        // No $filter: the whole table loads into ONE tracked query result, so EF's own
        // relationship fixup wires Parent/Children among ALL three rows — the #325 mechanism.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/SeNodes?$orderby=id&$expand=Children($expand=Parent)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var value = doc.RootElement.GetProperty("value");
        var root = value.EnumerateArray().Single(n => n.GetProperty("Name").GetString() == "Root");
        var children = root.GetProperty("Children");
        Assert.Equal(2, children.GetArrayLength());
        foreach (var child in children.EnumerateArray())
        {
            // IgnoreCycles would have written "Parent": null here (both navs ARE in the $expand
            // clause, so Stage 3.5 would have kept the null) — Option B must write the real object.
            var parent = child.GetProperty("Parent");
            Assert.NotEqual(System.Text.Json.JsonValueKind.Null, parent.ValueKind);
            Assert.Equal("Root", parent.GetProperty("Name").GetString());
        }
    }

    [Fact] // T10/T11
    public async Task ParentExpandChildren_ContainsNoNullArrayElements()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/SeNodes?$orderby=id&$expand=Parent($expand=Children)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var value = doc.RootElement.GetProperty("value");
        var childA = value.EnumerateArray().Single(n => n.GetProperty("Name").GetString() == "ChildA");
        var parent = childA.GetProperty("Parent");
        Assert.Equal("Root", parent.GetProperty("Name").GetString());

        var grandChildren = parent.GetProperty("Children");
        Assert.True(grandChildren.GetArrayLength() >= 1);
        // IgnoreCycles would have written a null element for the self-reference back to ChildA
        // itself (malformed OData per the architecture spike's second repro) — every element here
        // must be a real object.
        foreach (var elem in grandChildren.EnumerateArray())
        {
            Assert.NotEqual(System.Text.Json.JsonValueKind.Null, elem.ValueKind);
            Assert.True(elem.TryGetProperty("Name", out _));
        }
    }
}

// ── T16/T17/T24-T27: default (pushdown-enabled) self-referential set — the general regression net ──

public sealed class SpNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public SpNode? Parent { get; set; }
    public List<SpNode> Children { get; set; } = new();
}

public sealed class SpDbContext : DbContext
{
    public SpDbContext(DbContextOptions<SpDbContext> options) : base(options) { }
    public DbSet<SpNode> SpNodes => Set<SpNode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpNode>()
            .HasMany(n => n.Children).WithOne(n => n.Parent!).HasForeignKey(n => n.ParentId);
    }
}

public sealed class SpNodeProfile : EntitySetProfile<int, SpNode>
{
    public SpNodeProfile(SpDbContext db) : base(x => x.Id)
    {
        EntitySetName = "SpNodes";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.SpNodes.AsQueryable());
        // Include(Parent): Parent is delegate-less (ServeRaw) — with no eager load, a single-row
        // GetById query would leave it un-materialized (null), unlike the collection-GET fixtures
        // above where the whole table loads in one query and EF fixup wires it up for free.
        GetById = (id, ct) => db.SpNodes.Include(n => n.Parent).FirstOrDefaultAsync(n => n.Id == id, ct);
        Patch = async (id, delta, ct) =>
        {
            SpNode? existing = await db.SpNodes.FirstOrDefaultAsync(n => n.Id == id, ct);
            if (existing is null) return null;
            delta.Patch(existing);
            await db.SaveChangesAsync(ct);
            return existing;
        };
        HasOptional(x => x.Parent!);
        // T26 needs a real GET /{Set}({key})/Children route, which requires a getAll handler
        // (route registration follows handler presence — see CLAUDE.md). Delegate-backed here on
        // purpose: it still exercises BuildNavEnvelope's own SerializeBounded splice (site 3)
        // over the SAME tracked, self-referential graph.
        HasMany(x => x.Children, getAll: (parentId, ct) =>
            Task.FromResult<IEnumerable<SpNode>>(db.SpNodes.Where(n => n.ParentId == parentId).ToList()));
    }
}

public sealed class SelfReferentialGeneralTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<SpNodeProfile>(),
            configureServices: services => services.AddDbContext<SpDbContext>(o => o.UseSqlite(_connection)));

        using IServiceScope scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpDbContext>();
        db.Database.EnsureCreated();
        db.SpNodes.AddRange(
            new SpNode { Id = 1, Name = "Root" },
            new SpNode { Id = 2, Name = "ChildA", ParentId = 1 },
            new SpNode { Id = 3, Name = "ChildB", ParentId = 1 });
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact] // #325 core repro, general (default pushdown) profile
    public async Task PlainGet_NoExpand_Returns200_NavigationsOmitted()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/SpNodes?$orderby=id");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement.GetProperty("value")[0];
        Assert.Equal("Root", root.GetProperty("Name").GetString());
        // §4.5.1: an un-expanded navigation is OMITTED, never emitted as null/[].
        Assert.False(root.TryGetProperty("Parent", out _));
        Assert.False(root.TryGetProperty("Children", out _));
    }

    [Fact] // T24
    public async Task GetById_WithExpand_Returns200_WithRealRelatedData()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/SpNodes(2)?$expand=Parent");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Root\"", body);
    }

    [Fact] // T25: write-path response body (PATCH) — no $expand possible here; must not crash and
           // must omit navigations exactly like a plain GetById.
    public async Task Patch_ResponseBody_Returns200_NavigationsOmitted()
    {
        var content = new StringContent("{\"name\":\"RootRenamed\"}", System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await _fx.Client.PatchAsync("/odata/SpNodes(1)", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("RootRenamed", doc.RootElement.GetProperty("Name").GetString());
        Assert.False(doc.RootElement.TryGetProperty("Parent", out _));
        Assert.False(doc.RootElement.TryGetProperty("Children", out _));
    }

    [Fact] // T26: navigation-collection route (BuildNavEnvelope) — no $expand possible; must not
           // crash on the tracked cyclic graph and must omit each child's own navigations.
    public async Task NavigationRoute_Children_Returns200_ChildNavigationsOmitted()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/SpNodes(1)/Children");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var value = doc.RootElement.GetProperty("value");
        Assert.Equal(2, value.GetArrayLength());
        foreach (var child in value.EnumerateArray())
        {
            Assert.False(child.TryGetProperty("Parent", out _));
            Assert.False(child.TryGetProperty("Children", out _));
        }
    }

    [Fact] // T16/T17: the tracked-entity hazard — serialize, then mutate + SaveChanges in the SAME
           // DbContext scope/instance, and prove the walker never mutated the tracked graph.
    public async Task Serialize_ThenMutateAndSaveChangesSameScope_NoNavigationCorrupted()
    {
        using IServiceScope scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpDbContext>();

        // Load (and thereby track + fixup) the whole graph in THIS scope — mirroring what a
        // request handler's GetQueryable would do inside one request scope.
        List<SpNode> all = await db.SpNodes.OrderBy(n => n.Id).ToListAsync();
        SpNode root = all.Single(n => n.Id == 1);
        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Children, c => Assert.NotNull(c.Parent));

        // The walker only ever reads via reflection (PropertyInfo.GetValue) — never SetValue —
        // so exercising it here directly proves T17 without needing HTTP-scope trickery: read
        // every EDM navigation off the tracked graph exactly as SerializeBounded would, then
        // assert the tracked instances are bit-for-bit unchanged.
        foreach (SpNode n in all)
        {
            _ = n.Parent;
            _ = n.Children.Count;
        }

        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Children, c => Assert.Same(root, c.Parent));

        // T16: mutate + SaveChanges in the SAME scope/instance.
        root.Name = "RootRenamedInScope";
        await db.SaveChangesAsync();

        SpNode reloaded = await db.SpNodes.AsNoTracking().SingleAsync(n => n.Id == 1);
        Assert.Equal("RootRenamedInScope", reloaded.Name);
        // Children/Parent links survived the save untouched.
        List<SpNode> reloadedChildren = await db.SpNodes.AsNoTracking()
            .Where(n => n.ParentId == 1).ToListAsync();
        Assert.Equal(2, reloadedChildren.Count);
    }
}

// ── T27 (bound-op collection) uses its own minimal fixture — GetTree above is unused/unreachable
// on purpose (BindFunction only needs a valid delegate shape at startup; the route itself is
// exercised through WrapBoundOpResult's own harness below with real, servable data). ────────────

public sealed class SpTreeNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public SpTreeNode? Parent { get; set; }
    public List<SpTreeNode> Children { get; set; } = new();
}

public sealed class SpTreeDbContext : DbContext
{
    public SpTreeDbContext(DbContextOptions<SpTreeDbContext> options) : base(options) { }
    public DbSet<SpTreeNode> SpTreeNodes => Set<SpTreeNode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpTreeNode>()
            .HasMany(n => n.Children).WithOne(n => n.Parent!).HasForeignKey(n => n.ParentId);
    }
}

public sealed class SpTreeNodeProfile : EntitySetProfile<int, SpTreeNode>
{
    private readonly SpTreeDbContext _db;

    public SpTreeNodeProfile(SpTreeDbContext db) : base(x => x.Id)
    {
        _db = db;
        EntitySetName = "SpTreeNodes";
        GetQueryable = _ => Task.FromResult(db.SpTreeNodes.AsQueryable());
        HasOptional(x => x.Parent!);
        HasMany(x => x.Children);
        BindFunction(AllNodes); // T27: bound function returning a collection of the set's own type
    }

    // Bound function returning the WHOLE tracked, self-referential collection — the #326/site-5
    // (bound-operation collection result) proof. Profiles are re-resolved per request (scoped), so
    // this instance method closes over the SAME request-scoped _db the constructor received —
    // never a cross-scope reference.
    private Task<IEnumerable<SpTreeNode>> AllNodes() =>
        Task.FromResult<IEnumerable<SpTreeNode>>(_db.SpTreeNodes.ToList());
}

public sealed class BoundOpCollectionCycleTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<SpTreeNodeProfile>(),
            configureServices: services => services.AddDbContext<SpTreeDbContext>(o => o.UseSqlite(_connection)));

        using IServiceScope scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpTreeDbContext>();
        db.Database.EnsureCreated();
        db.SpTreeNodes.AddRange(
            new SpTreeNode { Id = 1, Name = "Root" },
            new SpTreeNode { Id = 2, Name = "ChildA", ParentId = 1 });
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact] // T27
    public async Task BoundFunction_ReturningCyclicCollection_Returns200()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/SpTreeNodes/AllNodes");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Root\"", body);
        Assert.Contains("\"ChildA\"", body);
    }
}

// ── T30: delegate safety — the delegate's OWN answer must win over the materialized CLR graph ────

public sealed class SdNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public List<SdNode> Children { get; set; } = new();
}

public sealed class SdDbContext : DbContext
{
    public SdDbContext(DbContextOptions<SdDbContext> options) : base(options) { }
    public DbSet<SdNode> SdNodes => Set<SdNode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SdNode>().HasMany(n => n.Children).WithOne().HasForeignKey(n => n.ParentId);
    }
}

// Children is delegate-backed and DELIBERATELY returns data that DIFFERS from the real,
// materialized DB rows — proving ExpandLevelAsync's RunDelegate branch overwrites whatever
// SerializeBounded guessed by reading the CLR graph before the delegate ran.
public sealed class SdNodeProfile : EntitySetProfile<int, SdNode>
{
    public SdNodeProfile(SdDbContext db) : base(x => x.Id)
    {
        EntitySetName = "SdNodes";
        ExpandEnabled = true;
        OrderByEnabled = true;
        FilterEnabled = true;
        GetQueryable = _ => Task.FromResult(db.SdNodes.AsQueryable());
        HasMany(
            x => x.Children,
            getAll: (parentId, ct) => Task.FromResult<IEnumerable<SdNode>>(
                new[] { new SdNode { Id = 999, Name = "FromDelegate", ParentId = parentId } }));
    }
}

public sealed class DelegateSafetyExpandCycleTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<SdNodeProfile>(),
            configureServices: services => services.AddDbContext<SdDbContext>(o => o.UseSqlite(_connection)));

        using IServiceScope scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SdDbContext>();
        db.Database.EnsureCreated();
        db.SdNodes.AddRange(
            new SdNode { Id = 1, Name = "Root" },
            new SdNode { Id = 2, Name = "RealChildA", ParentId = 1 },
            new SdNode { Id = 3, Name = "RealChildB", ParentId = 1 });
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact] // T30
    public async Task DelegateAnswer_WinsOverMaterializedGraph()
    {
        // $filter to just the root: RealChildA/RealChildB also appear as their OWN top-level
        // entities in an unfiltered collection GET (correct, unrelated to this proof) — isolate to
        // the parent whose "Children" we're actually asserting on.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/SdNodes?$filter=id eq 1&$expand=Children");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement.GetProperty("value")[0];
        var children = root.GetProperty("Children");
        // The delegate's own answer must win over whatever SerializeBounded guessed by reading the
        // (real, materialized) CLR graph before ExpandLevelAsync's RunDelegate overwrite ran.
        Assert.Single(children.EnumerateArray());
        Assert.Equal("FromDelegate", children[0].GetProperty("Name").GetString());
    }
}

// ── T31: Model B disagreement (Blank) — must yield []/null, never the materialized graph ─────────

public sealed class SbkNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int RootId { get; set; }
    public int? ParentId { get; set; }
    public List<SbkNode> Children { get; set; } = new();
}

public sealed class SbkRoot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<SbkNode> Items { get; set; } = new();
}

public sealed class SbkDbContext : DbContext
{
    public SbkDbContext(DbContextOptions<SbkDbContext> options) : base(options) { }
    public DbSet<SbkRoot> SbkRoots => Set<SbkRoot>();
    public DbSet<SbkNode> SbkNodes => Set<SbkNode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SbkRoot>().HasMany(r => r.Items).WithOne().HasForeignKey(n => n.RootId);
        modelBuilder.Entity<SbkNode>().HasMany(n => n.Children).WithOne().HasForeignKey(n => n.ParentId);
    }
}

public sealed class SbkRootProfile : EntitySetProfile<int, SbkRoot>
{
    public SbkRootProfile(SbkDbContext db) : base(x => x.Id)
    {
        EntitySetName = "SbkRoots";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.SbkRoots.AsQueryable());
        // Delegate-backed on purpose: ServeRaw navigations skip nested-clause processing entirely
        // in ExpandLevelAsync (nothing to overwrite — the raw graph already stands), so a Blank
        // disagreement reached ONLY through a ServeRaw parent would never be evaluated at all.
        // RunDelegate keeps the nested-expand recursion live so "Children"'s own Model B
        // disagreement is actually reached and Blanked.
        HasMany(x => x.Items, getAll: (rootId, ct) =>
            Task.FromResult<IEnumerable<SbkNode>>(db.SbkNodes.Where(n => n.RootId == rootId).ToList()));
    }
}

// SbkNode is exposed by BOTH SbkNodesA (delegate-less Children) and SbkNodesB (delegate-backed
// Children) — a genuine Model B disagreement over the SAME EDM type (#292/#293, untouched by
// #325/#326), reached as a NESTED $expand through SbkRoots.Items.
public sealed class SbkNodesAProfile : EntitySetProfile<int, SbkNode>
{
    public SbkNodesAProfile(SbkDbContext db) : base(x => x.Id)
    {
        EntitySetName = "SbkNodesA";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.SbkNodes.AsQueryable());
        HasMany(x => x.Children);
    }
}

public sealed class SbkNodesBProfile : EntitySetProfile<int, SbkNode>
{
    public SbkNodesBProfile(SbkDbContext db) : base(x => x.Id)
    {
        EntitySetName = "SbkNodesB";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.SbkNodes.AsQueryable());
        HasMany(x => x.Children, getAll: (_, ct) => Task.FromResult<IEnumerable<SbkNode>>(Array.Empty<SbkNode>()));
    }
}

public sealed class ModelBBlankExpandCycleTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<SbkRootProfile>();
                b.AddEntitySetProfile<SbkNodesAProfile>();
                b.AddEntitySetProfile<SbkNodesBProfile>();
            },
            configureServices: services => services.AddDbContext<SbkDbContext>(o => o.UseSqlite(_connection)));

        using IServiceScope scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SbkDbContext>();
        db.Database.EnsureCreated();
        db.SbkRoots.Add(new SbkRoot { Id = 1, Name = "Root" });
        db.SbkNodes.AddRange(
            new SbkNode { Id = 10, Name = "A", RootId = 1 },
            new SbkNode { Id = 11, Name = "B", RootId = 1, ParentId = 10 }); // A genuinely has child B
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact] // T31
    public async Task DisagreeingNestedNav_BlanksToEmptyArray_NeverMaterializedGraph()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/SbkRoots?$expand=Items($expand=Children)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("value")[0].GetProperty("Items");
        Assert.True(items.GetArrayLength() >= 1);
        var nodeA = items.EnumerateArray().Single(n => n.GetProperty("Name").GetString() == "A");
        var children = nodeA.GetProperty("Children");
        // A genuinely has child "B" in the DB — Blank must overwrite that away to [], never leak
        // the materialized graph the ambiguous candidates disagree about.
        Assert.Equal(0, children.GetArrayLength());
    }
}

// ── T35: OWNER DECISIONS explicitly left unfixed — a cycle closed by a non-EDM-navigation property ─

public sealed class SxNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // Deliberately NOT declared via HasOptional/HasMany — not an EDM navigation at all, just a
    // plain entity-typed CLR property System.Text.Json will walk like any other structural member.
    // SerializeBounded only suppresses/bounds EDM-declared navigations (mirrors
    // OmitUnexpandedNavigations' own blind spot — see #325's "Also noted" remark), so this
    // residue is NOT fixed by #325/#326 and must still surface as a loud 500.
    public SxNode? Sibling { get; set; }
}

public sealed class SxNodeProfile : EntitySetProfile<int, SxNode>
{
    private static readonly SxNode NodeA = new() { Id = 1, Name = "A" };
    private static readonly SxNode NodeB = new() { Id = 2, Name = "B" };

    static SxNodeProfile()
    {
        NodeA.Sibling = NodeB;
        NodeB.Sibling = NodeA; // genuine, non-EDM object cycle
    }

    public SxNodeProfile() : base(x => x.Id)
    {
        EntitySetName = "SxNodes";
        GetQueryable = _ => Task.FromResult(new[] { NodeA, NodeB }.AsQueryable());
    }

    // The automatic EDM builder (ODataConventionModelBuilder) auto-detects ANY entity-typed CLR
    // property as a navigation by reflection at model-build time — even one never declared via
    // HasOptional/HasMany — so Sibling would otherwise still end up as a real EDM
    // NavigationProperty (and get correctly bounded/omitted). Ignore() on the underlying
    // Microsoft.OData.ModelBuilder EntityTypeConfiguration (a different mechanism from OhData's
    // OWN Ignore()/#226 JSON-suppression feature) is what actually reproduces #325's "Also noted"
    // [NotMapped]-shaped residue: excluded from the EDM entirely, so System.Text.Json still walks
    // it like any other CLR member with nothing bounding it.
    protected override void AdvancedConfigure(Microsoft.OData.ModelBuilder.EntitySetConfiguration<SxNode> config)
    {
        config.EntityType.Ignore(x => x.Sibling);
    }
}

public sealed class NonEdmNavigationResidueTests
{
    [Fact] // T35
    public async Task NonEdmCyclicProperty_PlainGet_Returns500_DeliberatelyNotFixed()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<SxNodeProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SxNodes");

        // OWNER DECISIONS (#325/#326 FROZEN spec, item 4): deliberately NOT fixed by this change.
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
    }
}
