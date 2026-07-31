using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
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
        using var content = new StringContent("{\"name\":\"RootRenamed\"}", System.Text.Encoding.UTF8, "application/json");
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

    [Fact] // T16/T17 (fold-in #3 rewrite — the original version here was VACUOUS: it never called
           // SerializeBounded, never issued a request, and passed unmodified on develop, since all
           // it actually asserted was that reading a C# property doesn't mutate it. This version
           // exercises the real production pipeline AND the real production SerializeBounded method
           // directly (via reflection — it is `private static`), then mutates + SaveChanges in the
           // SAME DbContext/scope instance the reads ran against, and proves the tracked graph
           // survived untouched. Verified (per fold-in review) to actually go red if the walker
           // wrote instead of read — see the PR/report for the temporary-SetValue verification.
    public async Task SerializeBounded_ThenMutateAndSaveChangesSameScope_NoNavigationCorrupted()
    {
        // Part 1: a real HTTP request through the full production pipeline (TestServer, not a
        // hand-rolled substitute) proves the end-to-end shape is correct over this tracked,
        // self-referential graph — same repro family as T8-T11.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/SpNodes?$orderby=id&$expand=Children($expand=Parent)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"ChildA\"", await resp.Content.ReadAsStringAsync());

        // Part 2: the actual T16/T17 hazard proof. TestServer disposes the HTTP request's own DI
        // scope (and its SpDbContext) before Client.GetAsync above returns, so there is no way to
        // reach back into THAT exact scope afterward. Instead: create ONE scope here, invoke the
        // SAME production SerializeBounded method Part 1 just exercised — via reflection, since it
        // is `private static` — over a SelectExpandClause built the identical way the framework's
        // own GetQueryable route builds one (ODataQueryContext + ODataQueryOptions<SpNode> straight
        // off an HttpRequest, no OData routing middleware needed), then mutate + SaveChanges in this
        // SAME scope/DbContext instance immediately after. This is the only way to test "read a
        // request-scoped tracked graph, then save in that SAME scope" without fighting TestServer's
        // per-request scope lifetime.
        using IServiceScope scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpDbContext>();
        var registration = scope.ServiceProvider.GetRequiredKeyedService<OhDataRegistration>(
            OhDataDefaults.DefaultRegistrationName);

        List<SpNode> all = await db.SpNodes.OrderBy(n => n.Id).ToListAsync();
        SpNode root = all.Single(n => n.Id == 1);
        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Children, c => Assert.Same(root, c.Parent));

        IEdmEntityType edmType = registration.EdmModel.EntityContainer.FindEntitySet("SpNodes").EntityType;

        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.Request.QueryString = new QueryString("?$expand=Children($expand=Parent)");
        var queryContext = new ODataQueryContext(registration.EdmModel, typeof(SpNode), path: null);
        var options = new ODataQueryOptions<SpNode>(queryContext, httpContext.Request);
        SelectExpandClause clause = options.SelectExpand!.SelectExpandClause;

        MethodInfo serializeBounded = typeof(OhDataEndpointFactory).GetMethod(
            "SerializeBounded", BindingFlags.NonPublic | BindingFlags.Static)!;

        // Invoke the REAL walker directly over every tracked node — same call shape
        // ApplyCollectionPipelineAsync's Stage 1 uses (single item, activeLevels/levelsNavNames
        // null, isCollectionValue false). If SerializeBounded ever called PropertyInfo.SetValue
        // instead of GetValue while reading Parent/Children here, this call is where it would
        // happen — proven below by temporarily flipping the walker to SetValue (see PR notes).
        foreach (SpNode n in all)
        {
            object? result = serializeBounded.Invoke(null, new object?[]
            {
                n, edmType, clause, null, null, OhDataEndpointFactory.MaxNestedExpandDepth, null, false
            });
            Assert.NotNull(result);
        }

        // The proof: the reflection-driven reads above must not have mutated the tracked graph.
        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Children, c => Assert.Same(root, c.Parent));

        // T16: mutate + SaveChanges in the SAME scope/instance, immediately after the reads.
        root.Name = "RootRenamedInScope";
        await db.SaveChangesAsync();

        SpNode reloaded = await db.SpNodes.AsNoTracking().SingleAsync(n => n.Id == 1);
        Assert.Equal("RootRenamedInScope", reloaded.Name);
        // Children/Parent links survived the save untouched.
        List<SpNode> reloadedChildren = await db.SpNodes.AsNoTracking()
            .Where(n => n.ParentId == 1).ToListAsync();
        Assert.Equal(2, reloadedChildren.Count);
        Assert.All(reloadedChildren, c => Assert.Equal(1, c.ParentId));
    }
}

// ── T27 (bound-op collection) uses its own minimal fixture below (SpTreeNodeProfile.AllNodes) —
// BindFunction only needs a valid delegate shape at startup; the route itself is exercised through
// WrapBoundOpResult's own harness with real, servable data over a self-referential collection. ──

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

// ── Fold-in #1 (#325/#326 review — data exposure regression): SerializeBounded's splice must only
// resurrect a kept navigation that System.Text.Json's BASE options would themselves have emitted.
// Measured repro: [JsonIgnore]'d nav — develop -> {"Id":1,"Name":"R"}, pre-fold-in branch ->
// {"Id":1,"Name":"R","HiddenTags":[{"Id":1,"Name":"SECRET"}]} (SECRET data leaked via $expand). ──

public sealed class ZjTag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int ZjRootId { get; set; }
}

public sealed class ZjRoot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // [JsonIgnore]'d on the CLR side but STILL a real EDM navigation (ODataConventionModelBuilder
    // auto-detects it by reflection over the CLR shape — [JsonIgnore] is an STJ-only attribute, the
    // EDM model builder doesn't consult it at all). This is exactly the shape GetNavSuppressedOptions
    // strips from the JsonTypeInfo to keep the graph walk bounded — a splice that ignores what the
    // BASE (un-suppressed) options would themselves have decided about this member is a data
    // exposure bug, not a serialization-cycle fix.
    [System.Text.Json.Serialization.JsonIgnore]
    public List<ZjTag> HiddenTags { get; set; } = new();
}

public sealed class ZjDbContext : DbContext
{
    public ZjDbContext(DbContextOptions<ZjDbContext> options) : base(options) { }
    public DbSet<ZjRoot> ZjRoots => Set<ZjRoot>();
    public DbSet<ZjTag> ZjTags => Set<ZjTag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ZjRoot>().HasMany(r => r.HiddenTags).WithOne().HasForeignKey(t => t.ZjRootId);
    }
}

public sealed class ZjRootProfile : EntitySetProfile<int, ZjRoot>
{
    public ZjRootProfile(ZjDbContext db) : base(x => x.Id)
    {
        EntitySetName = "ZjRoots";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.ZjRoots.AsQueryable());
        HasMany(x => x.HiddenTags);
    }
}

public sealed class JsonIgnoredNavigationTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<ZjRootProfile>(),
            configureServices: services => services.AddDbContext<ZjDbContext>(o => o.UseSqlite(_connection)));

        using IServiceScope scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZjDbContext>();
        db.Database.EnsureCreated();
        db.ZjRoots.Add(new ZjRoot { Id = 1, Name = "R" });
        db.ZjTags.Add(new ZjTag { Id = 1, Name = "SECRET", ZjRootId = 1 });
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task JsonIgnoredNavigation_PlainGet_StaysAbsent()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/ZjRoots");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement.GetProperty("value")[0];
        Assert.False(root.TryGetProperty("HiddenTags", out _));
        Assert.DoesNotContain("SECRET", body);
    }

    [Fact] // The measured regression: $expand must NOT resurrect a [JsonIgnore]'d navigation.
    public async Task JsonIgnoredNavigation_Expanded_StaysAbsent_NotResurrected()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/ZjRoots?$expand=HiddenTags");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement.GetProperty("value")[0];
        Assert.False(root.TryGetProperty("HiddenTags", out _));
        Assert.DoesNotContain("SECRET", body);
    }
}

// ── Fold-in #2 (#325/#326 review — 200→500 regression): SerializeBounded must not force
// `.AsObject()` on a non-object shape, and must not shape-sniff `value is IEnumerable` to decide
// entity-vs-collection cardinality. Measured repro: an entity with [JsonConverter] writing a plain
// string — develop -> 200 {"value":["thing:1"]}, pre-fold-in branch -> 500. ────────────────────────

public sealed class ZcThingJsonConverter : System.Text.Json.Serialization.JsonConverter<ZcThing>
{
    public override ZcThing? Read(
        ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer, ZcThing value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue($"thing:{value.Id}");
}

[System.Text.Json.Serialization.JsonConverter(typeof(ZcThingJsonConverter))]
public sealed class ZcThing
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ZcThingProfile : EntitySetProfile<int, ZcThing>
{
    public ZcThingProfile() : base(x => x.Id)
    {
        EntitySetName = "ZcThings";
        GetQueryable = _ => Task.FromResult(new[] { new ZcThing { Id = 1, Name = "One" } }.AsQueryable());
    }
}

public sealed class EntityCustomConverterTests
{
    [Fact]
    public async Task EntityWithCustomJsonConverter_CollectionGet_Returns200_NotAsObjectCrash()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<ZcThingProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/ZcThings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"thing:1\"", body);
    }
}

// Corollary the reviewer inferred from the pre-fold-in `value is IEnumerable seq and not string`
// shape-sniff: it decided "is this a COLLECTION of entities of edmType?" from the CLR VALUE's own
// shape rather than from EDM cardinality. That misfires for a SINGLE-valued navigation (cardinality
// 1) whose target CLR type happens to implement IEnumerable for an unrelated domain reason: the
// pre-fold-in code would walk it element-by-element from WITHIN our own recursion, reusing the
// WRONG edmType (the nav's, not the element's) per element.
//
// Verification note (honesty about what this test does and doesn't isolate): with the fold-in #2
// `.AsObject()` guard alone (verified separately above, EntityCustomConverterTests — confirmed to
// go red without it), the former per-element crash no longer reproduces even with the OLD shape-sniff
// reinstated, because a boxed `int` element hits the SAME non-object guard on ITS OWN recursive call
// and returns a JSON number instead of throwing. This test therefore does NOT independently regress
// without the isCollectionValue change for THIS fixture — confirmed while verifying fold-in #2 (the
// old sniff + the new guard together still produce a 200, byte-identical to the fixed dispatch for
// this specific element type). The isCollectionValue change is kept as the architecturally correct
// fix regardless (EDM cardinality, not CLR shape, decides "is this a collection of entities" — the
// only sound source of truth once nav-target types are unconstrained), and this test pins that the
// scenario stays a 200 (not a regression net for a shape-sniff-specific crash that no longer exists
// once the AsObject guard is in place). Root-level entities that merely happen to implement
// IEnumerable hit a DIFFERENT, pre-existing System.Text.Json behavior unrelated to this fix (STJ
// itself always treats an IEnumerable-implementing CLR type as enumerable-shaped when handed
// directly to SerializeToNode, matching `develop`'s whole-graph serializer byte-for-byte for the
// same CLR shape — not something #325/#326 could or should change).
public sealed class ZeTarget : IEnumerable<int>
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    public IEnumerator<int> GetEnumerator()
    {
        yield return 7;
        yield return 8;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class ZeRoot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ZeTarget? Target { get; set; }
}

public sealed class ZeRootProfile : EntitySetProfile<int, ZeRoot>
{
    public ZeRootProfile() : base(x => x.Id)
    {
        EntitySetName = "ZeRoots";
        ExpandEnabled = true;
        var target = new ZeTarget { Id = 1, Name = "T" };
        GetQueryable = _ => Task.FromResult(new[] { new ZeRoot { Id = 1, Name = "R", Target = target } }.AsQueryable());
        HasOptional(x => x.Target!);
    }
}

public sealed class SingleValuedNavTargetImplementingIEnumerableTests
{
    [Fact]
    public async Task ExpandedSingleValuedNav_TargetImplementsIEnumerable_Returns200_NotWrongDispatchCrash()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<ZeRootProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/ZeRoots?$expand=Target");
        string body = await resp.Content.ReadAsStringAsync();
        // Pre-fold-in (shape-sniffed cardinality + unconditional .AsObject()): this would recurse
        // into Target's IEnumerable<int> contents using ZeTarget's own edmType per boxed int
        // element, then throw InvalidOperationException on the resulting non-object node — a 500.
        // Post-fold-in: cardinality is EDM-driven (HasOptional -> isCollectionValue: false), so the
        // walker never iterates Target's own enumerable contents as if they were entities at all.
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"{resp.StatusCode}: {body}");
        Assert.Contains("\"R\"", body);
    }
}

// ── Perf-fix regression (2026-07-31): SerializeBoundedCollection batched-splice index pairing ──
// Stage 1 of ApplyCollectionPipelineAsync moved from N per-entity SerializeBounded calls to ONE
// SerializeBoundedCollection call over the whole page (one JsonSerializer.SerializeToNode call
// instead of N), then splices each kept navigation back in per element by pairing the serialized
// array element at index i with the source CLR item at the SAME index i. A misaligned pairing
// would silently attach one entity's navigation data to a DIFFERENT entity instead of throwing —
// this fixture seeds several top-level nodes, each with a DIFFERENT, non-empty, distinctly-named
// set of children, and asserts every node in the SAME page response carries EXACTLY its own
// children (never a neighbour's). ExpandPushdownEnabled = false forces the EDM-only/ServeRaw path
// (Include/pushdown never engaged), so the walker's CLR-reflection splice is what is under test.
public sealed class IxNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public IxNode? Parent { get; set; }
    public List<IxNode> Children { get; set; } = new();
}

public sealed class IxDbContext : DbContext
{
    public IxDbContext(DbContextOptions<IxDbContext> options) : base(options) { }
    public DbSet<IxNode> IxNodes => Set<IxNode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IxNode>()
            .HasMany(n => n.Children).WithOne(n => n.Parent!).HasForeignKey(n => n.ParentId);
    }
}

public sealed class IxNodeProfile : EntitySetProfile<int, IxNode>
{
    public IxNodeProfile(IxDbContext db) : base(x => x.Id)
    {
        EntitySetName = "IxNodes";
        ExpandEnabled = true;
        OrderByEnabled = true;
        ExpandPushdownEnabled = false; // force the ServeRaw/EDM-only SerializeBoundedCollection path
        GetQueryable = _ => Task.FromResult(db.IxNodes.AsQueryable());
        HasOptional(x => x.Parent!);
        HasMany(x => x.Children);
    }
}

public sealed class SerializeBoundedCollectionIndexPairingTests : IAsyncLifetime
{
    private const int ParentCount = 6;
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<IxNodeProfile>(),
            configureServices: services => services.AddDbContext<IxDbContext>(o => o.UseSqlite(_connection)));

        using IServiceScope scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IxDbContext>();
        db.Database.EnsureCreated();

        // ParentCount top-level parents (ids 1..ParentCount), parent p has exactly p children,
        // each distinctly named "P{p}-C{c}". Parent 1 therefore has a non-empty Children array
        // while other parents have progressively larger, entirely different ones — a swapped or
        // off-by-one splice would surface as one parent's array containing another's names, or a
        // wrong element count.
        for (int p = 1; p <= ParentCount; p++)
        {
            db.IxNodes.Add(new IxNode { Id = p, Name = $"P{p}" });
        }
        db.SaveChanges();

        int childId = 1000;
        for (int p = 1; p <= ParentCount; p++)
        {
            for (int c = 1; c <= p; c++)
            {
                db.IxNodes.Add(new IxNode { Id = childId++, Name = $"P{p}-C{c}", ParentId = p });
            }
        }
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task CollectionExpand_EveryEntityCarriesExactlyItsOwnChildren_NoIndexMisalignment()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/IxNodes?$orderby=id&$expand=Children");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var value = doc.RootElement.GetProperty("value");

        // Element order must match source order exactly (ordered by id: parents 1..ParentCount
        // first, then child rows) — the splice pairs by index, so verifying THIS array's shape
        // directly checks the pairing assumption, not just each parent's final content.
        int totalChildren = Enumerable.Range(1, ParentCount).Sum();
        Assert.Equal(ParentCount + totalChildren, value.GetArrayLength());

        for (int p = 1; p <= ParentCount; p++)
        {
            var parent = value.EnumerateArray().Single(e => e.GetProperty("Id").GetInt32() == p);
            Assert.Equal($"P{p}", parent.GetProperty("Name").GetString());

            var children = parent.GetProperty("Children");
            Assert.Equal(p, children.GetArrayLength());
            string[] actualNames = children.EnumerateArray()
                .Select(c => c.GetProperty("Name").GetString()!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            string[] expectedNames = Enumerable.Range(1, p)
                .Select(c => $"P{p}-C{c}")
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedNames, actualNames);
        }

        // $expand=Children applies uniformly to every IxNode at this (root) level, including the
        // child rows themselves — each has no children of its own, so its spliced "Children" must
        // be an EMPTY array, never a leaked/misaligned copy of some OTHER node's children.
        foreach (var child in value.EnumerateArray().Where(e => e.GetProperty("Id").GetInt32() >= 1000))
        {
            var ownChildren = child.GetProperty("Children");
            Assert.Equal(System.Text.Json.JsonValueKind.Array, ownChildren.ValueKind);
            Assert.Equal(0, ownChildren.GetArrayLength());
        }
    }
}
