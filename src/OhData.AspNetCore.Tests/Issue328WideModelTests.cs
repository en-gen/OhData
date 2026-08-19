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
        GetQueryable = _ => Task.FromResult(db.WideNodes.AsQueryable());
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
