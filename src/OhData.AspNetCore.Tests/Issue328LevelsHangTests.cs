using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

// SCRATCH probes for #328 — investigation only, not shipped tests.
public sealed class HangNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public List<HangNode> Children { get; set; } = new();
}

public sealed class HangDbContext : DbContext
{
    public HangDbContext(DbContextOptions<HangDbContext> options) : base(options) { }
    public DbSet<HangNode> HangNodes => Set<HangNode>();
    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<HangNode>().HasMany(n => n.Children).WithOne().HasForeignKey(n => n.ParentId);
}

public sealed class HangNodeProfile : EntitySetProfile<int, HangNode>
{
    public HangNodeProfile(HangDbContext db) : base(x => x.Id)
    {
        EntitySetName = "HangNodes";
        ExpandEnabled = true;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        MaxExpansionDepth = 6;  // #328: was 15; the ceiling is now 6. To reproduce the depth 8-13 curve, raise EntitySetDefaults.MaxExpansionDepthCeiling locally.
        GetQueryable = _ => Task.FromResult(db.HangNodes.AsQueryable());
        HasMany(x => x.Children);
    }
}

// Same CLR type / same DbContext, but DEFAULT MaxExpansionDepth (3).
public sealed class HangDefaultProfile : EntitySetProfile<int, HangNode>
{
    public HangDefaultProfile(HangDbContext db) : base(x => x.Id)
    {
        EntitySetName = "DefaultNodes";
        ExpandEnabled = true;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        GetQueryable = _ => Task.FromResult(db.HangNodes.AsQueryable());
        HasMany(x => x.Children);
    }
}

// Breadth probe: TWO self-referential collection navigations on one type.
public sealed class BrNode
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public int? PeerOfId { get; set; }
    public List<BrNode> Children { get; set; } = new();
    public List<BrNode> Peers { get; set; } = new();
}

public sealed class BrDbContext : DbContext
{
    public BrDbContext(DbContextOptions<BrDbContext> options) : base(options) { }
    public DbSet<BrNode> BrNodes => Set<BrNode>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BrNode>().HasMany(n => n.Children).WithOne().HasForeignKey(n => n.ParentId);
        b.Entity<BrNode>().HasMany(n => n.Peers).WithOne().HasForeignKey(n => n.PeerOfId);
    }
}

public sealed class BrNodeProfile : EntitySetProfile<int, BrNode>
{
    public BrNodeProfile(BrDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BrNodes";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true;
        OrderByEnabled = true; CountEnabled = true;
        MaxExpansionDepth = 6;  // #328: was 15; the ceiling is now 6. To reproduce the depth 8-13 curve, raise EntitySetDefaults.MaxExpansionDepthCeiling locally.
        GetQueryable = _ => Task.FromResult(db.BrNodes.AsQueryable());
        HasMany(x => x.Children);
        HasMany(x => x.Peers);
    }
}

// MaxExpansionDepth=15 but pushdown OFF — is that a usable interim workaround?
public sealed class HangNoPushProfile : EntitySetProfile<int, HangNode>
{
    public HangNoPushProfile(HangDbContext db) : base(x => x.Id)
    {
        EntitySetName = "NoPushNodes";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true;
        OrderByEnabled = true; CountEnabled = true;
        MaxExpansionDepth = 6;  // #328: was 15; the ceiling is now 6. To reproduce the depth 8-13 curve, raise EntitySetDefaults.MaxExpansionDepthCeiling locally.
        ExpandPushdownEnabled = false;
        GetQueryable = _ => Task.FromResult(db.HangNodes.AsQueryable());
        HasMany(x => x.Children);
    }
}

public sealed class Issue328BreadthTests
{
    private readonly ITestOutputHelper _out;
    public Issue328BreadthTests(ITestOutputHelper output) => _out = output;

    // Cost model check: does BREADTH multiply the same way DEPTH does?
    // b navigations per level, d levels  =>  expect ~(3b)^d, not 3^d.
    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_Breadth()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<BrNodeProfile>(),
            configureServices: services => services.AddDbContext<BrDbContext>(o => o.UseSqlite(conn)));
        await using (fx)
        {
            using (IServiceScope scope = fx.App.Services.CreateScope())
            {
                BrDbContext db = scope.ServiceProvider.GetRequiredService<BrDbContext>();
                db.Database.EnsureCreated();
                for (int i = 1; i <= 16; i++)
                    db.BrNodes.Add(new BrNode { Id = i, ParentId = i == 1 ? null : i - 1 });
                await db.SaveChangesAsync();
            }

            foreach (int navs in new[] { 1, 2 })
            {
                for (int depth = 1; depth <= 6; depth++)
                {
                    string clause = Chain(depth, navs);
                    var sw = Stopwatch.StartNew();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    try
                    {
                        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/BrNodes?$expand={clause}", cts.Token);
                        string body = await resp.Content.ReadAsStringAsync();
                        sw.Stop();
                        _out.WriteLine($"navs/level={navs} depth={depth} -> {(int)resp.StatusCode} {sw.ElapsedMilliseconds,8} ms len={body.Length}");
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        _out.WriteLine($"navs/level={navs} depth={depth} -> ABORTED after {sw.ElapsedMilliseconds} ms ({ex.GetType().Name})");
                        break;
                    }
                }
            }
        }
    }

    private static string Chain(int depth, int navs)
    {
        string[] names = navs == 1 ? new[] { "Children" } : new[] { "Children", "Peers" };
        if (depth == 1) return string.Join(",", names);
        string inner = Chain(depth - 1, navs);
        return string.Join(",", names.Select(n => $"{n}($expand={inner})"));
    }
}

public sealed class Issue328LevelsHangTests
{
    private readonly ITestOutputHelper _out;
    public Issue328LevelsHangTests(ITestOutputHelper output) => _out = output;

    private sealed class Counter { public int N; }

    private static async Task<TestFixture> BuildAsync(
        SqliteConnection conn, SqlCaptureSink? sink, Counter? rowLimitWarnings = null)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => { b.AddEntitySetProfile<HangNodeProfile>(); b.AddEntitySetProfile<HangDefaultProfile>(); b.AddEntitySetProfile<HangNoPushProfile>(); },
            configureServices: services =>
            {
                if (sink is not null) services.AddSingleton(sink);
                services.AddDbContext<HangDbContext>(o =>
                {
                    o.UseSqlite(conn);
                    if (sink is not null)
                    {
                        o.LogTo(m => sink.Add(m),
                            (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted);
                    }
                    if (rowLimitWarnings is not null)
                    {
                        o.LogTo(_ => Interlocked.Increment(ref rowLimitWarnings.N),
                            (eventId, _) => eventId == CoreEventId.RowLimitingOperationWithoutOrderByWarning);
                    }
                });
            });

        using IServiceScope scope = fx.App.Services.CreateScope();
        HangDbContext db = scope.ServiceProvider.GetRequiredService<HangDbContext>();
        db.Database.EnsureCreated();
        for (int i = 1; i <= 16; i++)
            db.HangNodes.Add(new HangNode { Id = i, Name = "N" + i, ParentId = i == 1 ? null : i - 1 });
        await db.SaveChangesAsync();
        return fx;
    }

    // ── 1. How many times does EF translate the innermost Take(0) leaf? ────────────────────────
    // Each translation of the leaf logs RowLimitingOperationWithoutOrderByWarning exactly once,
    // so the warning count IS the re-translation multiplier for that node.
    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_RetranslationCount()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var warn = new Counter();
        await using TestFixture fx = await BuildAsync(conn, null, warn);

        int prev = 0;
        for (int levels = 1; levels <= 9; levels++)
        {
            warn.N = 0;
            var sw = Stopwatch.StartNew();
            HttpResponseMessage resp = await fx.Client.GetAsync(
                $"/odata/HangNodes?$expand=Children($levels={levels})");
            await resp.Content.ReadAsStringAsync();
            sw.Stop();
            string ratio = prev == 0 ? "" : $"  x{(double)warn.N / prev:F2}";
            _out.WriteLine($"levels={levels,2} leafTranslations={warn.N,9} {sw.ElapsedMilliseconds,7} ms{ratio}");
            prev = warn.N;
        }
    }

    // ── 2. SQL shape: statement count and join levels at N vs N+1 (#335 N+1 claim) ─────────────
    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_SqlShape()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BuildAsync(conn, sink);

        foreach (int levels in new[] { 1, 2, 3, 4 })
        {
            sink.Clear();
            HttpResponseMessage resp = await fx.Client.GetAsync(
                $"/odata/HangNodes?$expand=Children($levels={levels})");
            await resp.Content.ReadAsStringAsync();
            foreach (string s in sink.Snapshot())
            {
                int idx = s.IndexOf("SELECT", StringComparison.Ordinal);
                string sql = idx >= 0 ? s.Substring(idx) : s;
                int joins = System.Text.RegularExpressions.Regex.Matches(sql, @"\bJOIN\b").Count;
                int selects = System.Text.RegularExpressions.Regex.Matches(sql, @"\bSELECT\b").Count;
                _out.WriteLine($"levels={levels}: statements=1 joins={joins} selects={selects} len={sql.Length}");
                _out.WriteLine(sql.Replace("\r\n", " ").Replace("\n", " "));
            }
        }
    }

    // ── 3. Is the blow-up reachable at the DEFAULT MaxExpansionDepth (3)? ──────────────────────
    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_DefaultDepthReachability()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using TestFixture fx = await BuildAsync(conn, null);

        string[] urls =
        {
            "/odata/DefaultNodes?$expand=Children($levels=3)",
            "/odata/DefaultNodes?$expand=Children($levels=4)",
            "/odata/DefaultNodes?$expand=Children($levels=12)",
            "/odata/DefaultNodes?$expand=Children($levels=max)",
            "/odata/DefaultNodes?$expand=Children($expand=Children($expand=Children))",
            "/odata/DefaultNodes?$expand=Children($expand=Children($expand=Children($expand=Children)))",
            "/odata/DefaultNodes?$expand=Children($levels=3;$expand=Children($levels=3))",
        };
        foreach (string url in urls)
        {
            var sw = Stopwatch.StartNew();
            HttpResponseMessage resp = await fx.Client.GetAsync(url);
            string body = await resp.Content.ReadAsStringAsync();
            sw.Stop();
            _out.WriteLine($"{(int)resp.StatusCode} {sw.ElapsedMilliseconds,6} ms len={body.Length,6}  {url}");
            if ((int)resp.StatusCode >= 400) _out.WriteLine("      " + body);
        }
    }

    // ── 4. Smallest reproduction: plain EF Core, no OhData in the picture at all. ──────────────
    // Builds exactly the projection shape BuildLevelsNavAccess emits, by hand.
    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_PureEfCore_NoOhData()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<HangDbContext>().UseSqlite(conn).Options;
        using var db = new HangDbContext(options);
        db.Database.EnsureCreated();
        for (int i = 1; i <= 16; i++)
            db.HangNodes.Add(new HangNode { Id = i, Name = "N" + i, ParentId = i == 1 ? null : i - 1 });
        await db.SaveChangesAsync();

        foreach (LeafKind kind in new[] { LeafKind.TakeZero, LeafKind.NewEmptyList, LeafKind.Unbound })
        {
            for (int levels = 1; levels <= 10; levels++)
            {
                using var fresh = new HangDbContext(
                    new DbContextOptionsBuilder<HangDbContext>().UseSqlite(conn).Options);
                s_leaf = kind;
                IQueryable<HangNode> q = fresh.HangNodes.AsNoTracking().Select(BuildRootProjection(levels));
                var sw = Stopwatch.StartNew();
                List<HangNode> rows = await q.ToListAsync();
                sw.Stop();
                _out.WriteLine($"pure-EF leaf={kind,-13} levels={levels,2} {sw.ElapsedMilliseconds,7} ms rows={rows.Count}");
            }
        }
    }

    // SMALLEST REPRODUCTION: no database, no connection, no data, no OhData.
    // ToQueryString() runs the full EF translation pipeline and issues no SQL.
    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public void Probe_Smallest_NoDatabase()
    {
        s_leaf = LeafKind.TakeZero;
        for (int levels = 1; levels <= 9; levels++)
        {
            using var db = new HangDbContext(new DbContextOptionsBuilder<HangDbContext>()
                .UseSqlite("DataSource=file:nonexistent?mode=memory").Options);
            var sw = Stopwatch.StartNew();
            string sql = db.HangNodes.Select(BuildRootProjection(levels)).ToQueryString();
            sw.Stop();
            _out.WriteLine($"no-DB levels={levels,2} translate={sw.ElapsedMilliseconds,7} ms sqlLen={sql.Length}");
        }
    }

    public enum LeafKind { TakeZero, NewEmptyList, Unbound }
    private static LeafKind s_leaf = LeafKind.TakeZero;

    private static readonly PropertyInfo ChildrenProp = typeof(HangNode).GetProperty(nameof(HangNode.Children))!;
    private static readonly PropertyInfo[] Scalars =
    {
        typeof(HangNode).GetProperty(nameof(HangNode.Id))!,
        typeof(HangNode).GetProperty(nameof(HangNode.Name))!,
        typeof(HangNode).GetProperty(nameof(HangNode.ParentId))!,
    };
    private static readonly MethodInfo EnumSelect = typeof(Enumerable).GetMethods()
        .First(m => m.Name == "Select" && m.GetParameters().Length == 2 &&
                    m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2)
        .MakeGenericMethod(typeof(HangNode), typeof(HangNode));
    private static readonly MethodInfo EnumToList = typeof(Enumerable).GetMethod("ToList")!
        .MakeGenericMethod(typeof(HangNode));
    private static readonly MethodInfo EnumTake = typeof(Enumerable).GetMethods()
        .First(m => m.Name == "Take" && m.GetParameters()[1].ParameterType == typeof(int))
        .MakeGenericMethod(typeof(HangNode));

    private static Expression<Func<HangNode, HangNode>> BuildRootProjection(int levels)
    {
        ParameterExpression x = Expression.Parameter(typeof(HangNode), "x");
        return Expression.Lambda<Func<HangNode, HangNode>>(Init(x, levels), x);
    }

    private static Expression Init(Expression owner, int remaining)
    {
        var bindings = new List<MemberBinding>();
        foreach (PropertyInfo p in Scalars) bindings.Add(Expression.Bind(p, Expression.Property(owner, p)));
        if (remaining > 0 || s_leaf != LeafKind.Unbound)
            bindings.Add(Expression.Bind(ChildrenProp, Nav(owner, remaining)));
        return Expression.MemberInit(Expression.New(typeof(HangNode)), bindings);
    }

    private static Expression Nav(Expression owner, int remaining)
    {
        Expression access = Expression.Property(owner, ChildrenProp);
        if (remaining <= 0)
        {
            return s_leaf == LeafKind.NewEmptyList
                ? Expression.New(typeof(List<HangNode>))
                : Expression.Call(EnumToList, Expression.Call(EnumTake, access, Expression.Constant(0)));
        }
        ParameterExpression n = Expression.Parameter(typeof(HangNode), "n");
        LambdaExpression proj = Expression.Lambda(Init(n, remaining - 1), n);
        return Expression.Call(EnumToList, Expression.Call(EnumSelect, access, proj));
    }

    // ── 5. Cost is one-time query compilation, not per-request work. ───────────────────────────
    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_RepeatSameLevels()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using TestFixture fx = await BuildAsync(conn, null);
        foreach (int levels in new[] { 9, 9, 9 })
        {
            var sw = Stopwatch.StartNew();
            HttpResponseMessage resp = await fx.Client.GetAsync(
                $"/odata/HangNodes?$expand=Children($levels={levels})");
            string body = await resp.Content.ReadAsStringAsync();
            sw.Stop();
            _out.WriteLine($"levels={levels} run -> {(int)resp.StatusCode} {sw.ElapsedMilliseconds} ms len={body.Length}");
        }
    }

    // Does it TERMINATE at the depths the issue calls "never completes"? Plus alloc/RSS growth.
    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_Terminates11And12()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using TestFixture fx = await BuildAsync(conn, null);
        fx.Client.Timeout = TimeSpan.FromMinutes(60);
        foreach (int levels in new[] { 11, 12 })
        {
            long alloc0 = GC.GetTotalAllocatedBytes(false);
            var sw = Stopwatch.StartNew();
            HttpResponseMessage resp = await fx.Client.GetAsync(
                $"/odata/HangNodes?$expand=Children($levels={levels})");
            string body = await resp.Content.ReadAsStringAsync();
            sw.Stop();
            long alloc = GC.GetTotalAllocatedBytes(false) - alloc0;
            using var p = Process.GetCurrentProcess();
            _out.WriteLine($"levels={levels} -> {(int)resp.StatusCode} in {sw.Elapsed.TotalSeconds:F1}s len={body.Length} allocated={alloc / 1024 / 1024}MB peakWS={p.PeakWorkingSet64 / 1024 / 1024}MB currentWS={p.WorkingSet64 / 1024 / 1024}MB gcHeap={GC.GetTotalMemory(false) / 1024 / 1024}MB");
        }
    }

    // Where is the ACTUAL ceiling on a profile with MaxExpansionDepth=15?
    // Hypothesis: EntitySetProfile's model-bound entityType.Expand(MaxNestedExpandDepth=12, ...)
    // caps it at 12, so 13+ is a clean 400 and 12 is the worst reachable case.
    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_UpperCeiling()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using TestFixture fx = await BuildAsync(conn, null);
        foreach (string lv in new[] { "13", "14", "15", "16", "max" })
        {
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                HttpResponseMessage resp = await fx.Client.GetAsync(
                    $"/odata/HangNodes?$expand=Children($levels={lv})", cts.Token);
                string body = await resp.Content.ReadAsStringAsync();
                sw.Stop();
                _out.WriteLine($"ceiling $levels={lv,-4} -> {(int)resp.StatusCode} {sw.ElapsedMilliseconds,6} ms len={body.Length}  {(resp.IsSuccessStatusCode ? "" : body)}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _out.WriteLine($"ceiling $levels={lv,-4} -> STILL RUNNING at {sw.ElapsedMilliseconds} ms ({ex.GetType().Name})");
            }
        }
        // And an explicit 13-deep nested chain, to see whether the same ceiling covers it.
        string clause = "Children";
        for (int i = 1; i < 13; i++) clause = "Children($expand=" + clause + ")";
        HttpResponseMessage r2 = await fx.Client.GetAsync($"/odata/HangNodes?$expand={clause}");
        _out.WriteLine($"ceiling explicit depth=13 -> {(int)r2.StatusCode} {(await r2.Content.ReadAsStringAsync()).Length}");
    }

    // Workaround check: MaxExpansionDepth=15 with ExpandPushdownEnabled=false.
    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_PushdownDisabled()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using TestFixture fx = await BuildAsync(conn, null);
        foreach (int levels in new[] { 3, 8, 12, 15 })
        {
            var sw = Stopwatch.StartNew();
            HttpResponseMessage resp = await fx.Client.GetAsync(
                $"/odata/NoPushNodes?$expand=Children($levels={levels})");
            string body = await resp.Content.ReadAsStringAsync();
            sw.Stop();
            _out.WriteLine($"no-pushdown levels={levels,2} -> {(int)resp.StatusCode} {sw.ElapsedMilliseconds,7} ms len={body.Length}");
        }
    }

    // Is the blow-up specific to $levels, or does an EXPLICIT nested $expand chain hit it too?
    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_ExplicitNestedExpandChain()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var warn = new Counter();
        await using TestFixture fx = await BuildAsync(conn, null, warn);

        for (int depth = 1; depth <= 9; depth++)
        {
            string clause = "Children";
            for (int i = 1; i < depth; i++) clause = "Children($expand=" + clause + ")";
            warn.N = 0;
            var sw = Stopwatch.StartNew();
            HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/HangNodes?$expand={clause}");
            string body = await resp.Content.ReadAsStringAsync();
            sw.Stop();
            _out.WriteLine($"explicit depth={depth,2} -> {(int)resp.StatusCode} {sw.ElapsedMilliseconds,7} ms len={body.Length} leafWarn={warn.N}");
        }
    }

    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_LevelsLadder()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BuildAsync(conn, sink);

        for (int levels = 1; levels <= 13; levels++)
        {
            sink.Clear();
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                HttpResponseMessage resp = await fx.Client.GetAsync(
                    $"/odata/HangNodes?$expand=Children($levels={levels})", cts.Token);
                string body = await resp.Content.ReadAsStringAsync();
                sw.Stop();
                IReadOnlyList<string> sql = sink.Snapshot();
                _out.WriteLine($"levels={levels,2}  {(int)resp.StatusCode}  {sw.ElapsedMilliseconds,7} ms  bodyLen={body.Length,8}  sqlStmts={sql.Count}  sqlLen={sql.Sum(s => s.Length)}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _out.WriteLine($"levels={levels,2}  EXCEPTION after {sw.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }
    }
}
