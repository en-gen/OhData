using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

// SCRATCH probe for #328 severity: is the DEFAULT MaxExpansionDepth of 3 safe on a WIDE model
// (many collection navigations), given that translation cost is ~(3 x breadth)^depth?
public sealed class WideNode
{
    public int Id { get; set; }
    public int? F1 { get; set; }
    public int? F2 { get; set; }
    public int? F3 { get; set; }
    public int? F4 { get; set; }
    public int? F5 { get; set; }
    public int? F6 { get; set; }
    public List<WideNode> N1 { get; set; } = new();
    public List<WideNode> N2 { get; set; } = new();
    public List<WideNode> N3 { get; set; } = new();
    public List<WideNode> N4 { get; set; } = new();
    public List<WideNode> N5 { get; set; } = new();
    public List<WideNode> N6 { get; set; } = new();
}

public sealed class WideDbContext : DbContext
{
    public WideDbContext(DbContextOptions<WideDbContext> options) : base(options) { }
    public DbSet<WideNode> WideNodes => Set<WideNode>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<WideNode>().HasMany(n => n.N1).WithOne().HasForeignKey(n => n.F1);
        b.Entity<WideNode>().HasMany(n => n.N2).WithOne().HasForeignKey(n => n.F2);
        b.Entity<WideNode>().HasMany(n => n.N3).WithOne().HasForeignKey(n => n.F3);
        b.Entity<WideNode>().HasMany(n => n.N4).WithOne().HasForeignKey(n => n.F4);
        b.Entity<WideNode>().HasMany(n => n.N5).WithOne().HasForeignKey(n => n.F5);
        b.Entity<WideNode>().HasMany(n => n.N6).WithOne().HasForeignKey(n => n.F6);
    }
}

// DEFAULT MaxExpansionDepth (3). No override.
public sealed class WideNodeProfile : EntitySetProfile<int, WideNode>
{
    public WideNodeProfile(WideDbContext db) : base(x => x.Id)
    {
        EntitySetName = "WideNodes";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true;
        OrderByEnabled = true; CountEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.WideNodes.AsQueryable());
        HasMany(x => x.N1); HasMany(x => x.N2); HasMany(x => x.N3);
        HasMany(x => x.N4); HasMany(x => x.N5); HasMany(x => x.N6);
    }
}

public sealed class Issue328WideModelTests
{
    private readonly ITestOutputHelper _out;
    public Issue328WideModelTests(ITestOutputHelper output) => _out = output;

    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_WideModelAtDefaultDepth()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<WideNodeProfile>(),
            configureServices: s => s.AddDbContext<WideDbContext>(o => o.UseSqlite(conn)));
        await using (fx)
        {
            using (IServiceScope scope = fx.App.Services.CreateScope())
            {
                WideDbContext db = scope.ServiceProvider.GetRequiredService<WideDbContext>();
                db.Database.EnsureCreated();
                for (int i = 1; i <= 16; i++) db.WideNodes.Add(new WideNode { Id = i });
                await db.SaveChangesAsync();
            }

            for (int navs = 1; navs <= 6; navs++)
            {
                string clause = Chain(3, navs); // depth 3 = the DEFAULT MaxExpansionDepth
                var sw = Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                try
                {
                    HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/WideNodes?$expand={clause}", cts.Token);
                    string body = await resp.Content.ReadAsStringAsync();
                    sw.Stop();
                    _out.WriteLine($"DEFAULT depth=3 navs/level={navs} -> {(int)resp.StatusCode} {sw.ElapsedMilliseconds,8} ms len={body.Length}");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _out.WriteLine($"DEFAULT depth=3 navs/level={navs} -> ABORTED at {sw.ElapsedMilliseconds} ms ({ex.GetType().Name})");
                    break;
                }
            }
        }
    }

    private static string Chain(int depth, int navs)
    {
        string[] names = Enumerable.Range(1, navs).Select(i => "N" + i).ToArray();
        if (depth == 1) return string.Join(",", names);
        string inner = Chain(depth - 1, navs);
        return string.Join(",", names.Select(n => $"{n}($expand={inner})"));
    }
}

// Deep variant of WideNodeProfile: same 6-navigation model, MaxExpansionDepth raised to the
// #328 ceiling (6) so the depth x breadth cost surface can be measured over its whole legal
// domain. Used only by Probe_BreadthCalibration.
public sealed class WideDeepNodeProfile : EntitySetProfile<int, WideNode>
{
    public WideDeepNodeProfile(WideDbContext db) : base(x => x.Id)
    {
        EntitySetName = "WideDeepNodes";
        MaxExpansionDepth = 6;
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true;
        OrderByEnabled = true; CountEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.WideNodes.AsQueryable());
        HasMany(x => x.N1); HasMany(x => x.N2); HasMany(x => x.N3);
        HasMany(x => x.N4); HasMany(x => x.N5); HasMany(x => x.N6);
    }
}

public sealed class Issue328BreadthCalibrationTests
{
    private readonly ITestOutputHelper _out;
    public Issue328BreadthCalibrationTests(ITestOutputHelper output) => _out = output;

    // #429 calibration: wall-clock translation cost as a function of the TOTAL number of
    // navigation-expansion nodes in the $expand tree, across shapes that reach the same node
    // count by different (depth, breadth) trade-offs. This is the measurement that picks
    // EntitySetDefaults.MaxExpandBreadth. Skipped in CI - run explicitly.
    [Fact(Skip = "#328/#429 calibration harness - opt-in only; minutes. Run explicitly by name.")]
    public async Task Probe_BreadthCalibration()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<WideDeepNodeProfile>(),
            configureServices: s => s.AddDbContext<WideDbContext>(o => o.UseSqlite(conn)));
        await using (fx)
        {
            using (IServiceScope scope = fx.App.Services.CreateScope())
            {
                WideDbContext db = scope.ServiceProvider.GetRequiredService<WideDbContext>();
                db.Database.EnsureCreated();
                for (int i = 1; i <= 16; i++) db.WideNodes.Add(new WideNode { Id = i });
                await db.SaveChangesAsync();
            }

            // Warm the host, EF model, and JIT so the first measured shape is not charged for them.
            await fx.Client.GetAsync("/odata/WideDeepNodes?$expand=N1");

            (int Depth, int Navs)[] shapes =
            {
                (1, 1), (2, 1), (3, 1), (4, 1), (5, 1), (6, 1),
                (1, 6),
                (2, 2), (2, 3), (2, 4), (2, 5), (2, 6),
                (3, 2), (3, 3), (3, 4),
                (4, 2), (5, 2), (6, 2),
                (3, 5), (3, 6),
            };

            foreach ((int depth, int navs) in shapes)
            {
                string clause = Chain(depth, navs);
                int nodes = NodeCount(depth, navs);
                var sw = Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
                try
                {
                    HttpResponseMessage resp = await fx.Client.GetAsync(
                        $"/odata/WideDeepNodes?$expand={clause}", cts.Token);
                    string body = await resp.Content.ReadAsStringAsync();
                    sw.Stop();
                    _out.WriteLine($"depth={depth} navs={navs} nodes={nodes,6} -> {(int)resp.StatusCode} {sw.ElapsedMilliseconds,8} ms len={body.Length}");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _out.WriteLine($"depth={depth} navs={navs} nodes={nodes,6} -> ABORTED at {sw.ElapsedMilliseconds} ms ({ex.GetType().Name})");
                }
            }
        }
    }

    internal static int NodeCount(int depth, int navs)
    {
        int total = 0, level = 1;
        for (int i = 0; i < depth; i++) { level *= navs; total += level; }
        return total;
    }

    internal static string Chain(int depth, int navs)
    {
        string[] names = Enumerable.Range(1, navs).Select(i => "N" + i).ToArray();
        if (depth == 1) return string.Join(",", names);
        string inner = Chain(depth - 1, navs);
        return string.Join(",", names.Select(n => $"{n}($expand={inner})"));
    }
}

public sealed class Issue328WorstShapeTests
{
    private readonly ITestOutputHelper _out;
    public Issue328WorstShapeTests(ITestOutputHelper output) => _out = output;

    // #429 calibration, part 2: which $expand SHAPE is most expensive for a given total node
    // budget, once depth is capped at the #328 ceiling of 6? Each shape is a per-level branching
    // vector. Skipped in CI - run explicitly.
    [Fact(Skip = "#328/#429 calibration harness - opt-in only; minutes. Run explicitly by name.")]
    public async Task Probe_WorstShapeUnderNodeBudget()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<WideDeepNodeProfile>(),
            configureServices: s => s.AddDbContext<WideDbContext>(o => o.UseSqlite(conn)));
        await using (fx)
        {
            using (IServiceScope scope = fx.App.Services.CreateScope())
            {
                WideDbContext db = scope.ServiceProvider.GetRequiredService<WideDbContext>();
                db.Database.EnsureCreated();
                for (int i = 1; i <= 16; i++) db.WideNodes.Add(new WideNode { Id = i });
                await db.SaveChangesAsync();
            }
            await fx.Client.GetAsync("/odata/WideDeepNodes?$expand=N1");

            int[][] shapes =
            {
                new[] { 1, 1, 1, 1, 2, 6 },
                new[] { 1, 1, 1, 1, 3, 6 },
                new[] { 1, 1, 1, 1, 4, 6 },
                new[] { 1, 1, 1, 1, 6, 6 },
                new[] { 1, 1, 1, 2, 6, 6 },
                new[] { 1, 1, 2, 2, 6, 6 },
                new[] { 2, 1, 1, 1, 2, 6 },
                new[] { 6, 1, 1, 1, 2, 6 },
                new[] { 6, 6, 6, 1, 1, 1 },
                new[] { 6, 6, 6, 6 },
                new[] { 6, 6, 6, 6, 1 },
            };

            foreach (int[] shape in shapes)
            {
                string clause = Vector(shape, 0);
                int nodes = VectorNodes(shape);
                var sw = Stopwatch.StartNew();
                HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/WideDeepNodes?$expand={clause}");
                string body = await resp.Content.ReadAsStringAsync();
                sw.Stop();
                _out.WriteLine($"shape=[{string.Join(",", shape)}] nodes={nodes,5} -> {(int)resp.StatusCode} {sw.ElapsedMilliseconds,7} ms len={body.Length}");
            }
        }
    }

    private static int VectorNodes(int[] shape)
    {
        int total = 0, level = 1;
        foreach (int b in shape) { level *= b; total += level; }
        return total;
    }

    private static string Vector(int[] shape, int i)
    {
        string[] names = Enumerable.Range(1, shape[i]).Select(k => "N" + k).ToArray();
        if (i == shape.Length - 1) return string.Join(",", names);
        string inner = Vector(shape, i + 1);
        return string.Join(",", names.Select(n => $"{n}($expand={inner})"));
    }
}

public sealed class Issue429PostBundleWorstCaseTests
{
    private readonly ITestOutputHelper _out;
    public Issue429PostBundleWorstCaseTests(ITestOutputHelper output) => _out = output;

    // #429: search the WHOLE legal domain after the bundle lands - depth <= the #328 ceiling of 6
    // and total expansion nodes <= the shipped MaxExpandBreadth default of 50 - for the most
    // expensive shape a client can still get served. Enumerates every per-level branching vector
    // over {1,2,3,6} at depth 6 whose node count is within the budget (289 shapes), and reports the
    // worst. Skipped in CI - run explicitly.
    [Fact(Skip = "#429 worst-case search - opt-in only; several minutes. Run explicitly by name.")]
    public async Task Probe_PostBundleWorstCase()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<WideDeepNodeProfile>(),
            configureServices: s => s.AddDbContext<WideDbContext>(o => o.UseSqlite(conn)));
        await using (fx)
        {
            using (IServiceScope scope = fx.App.Services.CreateScope())
            {
                WideDbContext db = scope.ServiceProvider.GetRequiredService<WideDbContext>();
                db.Database.EnsureCreated();
                for (int i = 1; i <= 16; i++) db.WideNodes.Add(new WideNode { Id = i });
                await db.SaveChangesAsync();
            }
            await fx.Client.GetAsync("/odata/WideDeepNodes?$expand=N1");

            int[] alphabet = { 1, 2, 3, 6 };
            var shapes = new List<int[]>();
            foreach (int a in alphabet)
            {
                foreach (int b in alphabet)
                {
                    foreach (int c in alphabet)
                    {
                        foreach (int d in alphabet)
                        {
                            foreach (int e in alphabet)
                            {
                                foreach (int f in alphabet)
                                {
                                    int[] v = { a, b, c, d, e, f };
                                    if (Nodes(v) <= 50) shapes.Add(v);
                                }
                            }
                        }
                    }
                }
            }

            long worst = -1;
            string worstShape = "";
            int worstNodes = 0;
            foreach (int[] shape in shapes)
            {
                string clause = Shape(shape, 0);
                var sw = Stopwatch.StartNew();
                HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/WideDeepNodes?$expand={clause}");
                await resp.Content.ReadAsStringAsync();
                sw.Stop();
                if ((int)resp.StatusCode != 200)
                {
                    _out.WriteLine($"NON-200 [{string.Join(",", shape)}] nodes={Nodes(shape)} -> {(int)resp.StatusCode}");
                    continue;
                }
                if (sw.ElapsedMilliseconds > worst)
                {
                    worst = sw.ElapsedMilliseconds;
                    worstShape = string.Join(",", shape);
                    worstNodes = Nodes(shape);
                    _out.WriteLine($"new worst [{worstShape}] nodes={worstNodes} {worst} ms");
                }
            }
            _out.WriteLine($"WORST LEGAL REQUEST: shape=[{worstShape}] nodes={worstNodes} {worst} ms over {shapes.Count} shapes");

            // And the cost of the first REJECTED request, one node over the budget.
            var rejected = Stopwatch.StartNew();
            // 89 nodes - one shape past the shipped MaxExpandBreadth default of 50.
            HttpResponseMessage over = await fx.Client.GetAsync(
                $"/odata/WideDeepNodes?$expand={Shape(new[] { 1, 1, 1, 2, 6, 6 }, 0)}");
            string overBody = await over.Content.ReadAsStringAsync();
            rejected.Stop();
            _out.WriteLine($"OVER BUDGET -> {(int)over.StatusCode} {rejected.ElapsedMilliseconds} ms len={overBody.Length}");
        }
    }

    private static int Nodes(int[] shape)
    {
        int total = 0, level = 1;
        foreach (int b in shape) { level *= b; total += level; }
        return total;
    }

    private static string Shape(int[] shape, int i)
    {
        string[] names = Enumerable.Range(1, shape[i]).Select(k => "N" + k).ToArray();
        if (i == shape.Length - 1) return string.Join(",", names);
        string inner = Shape(shape, i + 1);
        return string.Join(",", names.Select(n => $"{n}($expand={inner})"));
    }
}
