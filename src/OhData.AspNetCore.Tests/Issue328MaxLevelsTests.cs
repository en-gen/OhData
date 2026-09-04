using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

// SCRATCH probe for #328: does $levels=max honour the MODEL-BOUND cap
// (entityType.Expand(MaxNestedExpandDepth, ...)) that a numeric $levels=N is rejected by?
//
// MS's SelectExpandQueryValidator.ValidateNestedLevels rejects a numeric $levels=N when
//   N > min(ValidationSettings.MaxExpansionDepth, expandConfiguration.MaxDepth)
// but for IsMaxLevel it only requires that min to be non-zero. OhData then resolves max via
// TryBuildEngagedExpand's `IsMaxLevel ? remainingDepth : ...`, where remainingDepth is
// source.MaxExpansionDepth ALONE — the model-bound MaxDepth is not consulted.
//
// Run this with OhDataEndpointFactory.MaxNestedExpandDepth temporarily lowered (e.g. 5) so the
// two caps differ and the experiment is cheap.
public sealed class MxNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public List<MxNode> Children { get; set; } = new();
}

public sealed class MxDbContext : DbContext
{
    public MxDbContext(DbContextOptions<MxDbContext> options) : base(options) { }
    public DbSet<MxNode> MxNodes => Set<MxNode>();
    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<MxNode>().HasMany(n => n.Children).WithOne().HasForeignKey(n => n.ParentId);
}

public sealed class MxNodeProfile : EntitySetProfile<int, MxNode>
{
    public MxNodeProfile(MxDbContext db) : base(x => x.Id)
    {
        EntitySetName = "MxNodes";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true;
        OrderByEnabled = true; CountEnabled = true;
        // Originally 8 — deliberately ABOVE the model-bound cap under test. #328's ceiling makes 8
        // an ArgumentOutOfRangeException, and #428 tied MaxNestedExpandDepth to that same ceiling,
        // so the divergence this probe measures is no longer representable in a shipped build. To
        // reproduce the original measurement, raise EntitySetDefaults.MaxExpansionDepthCeiling AND
        // lower OhDataEndpointFactory.MaxNestedExpandDepth locally.
        MaxExpansionDepth = EntitySetDefaults.MaxExpansionDepthCeiling;
        GetQueryable = _ => OhDataResult.Success(db.MxNodes.AsQueryable());
        HasMany(x => x.Children);
    }
}

public sealed class Issue328MaxLevelsTests
{
    private readonly ITestOutputHelper _out;
    public Issue328MaxLevelsTests(ITestOutputHelper output) => _out = output;

    [Fact(Skip = "#328 investigation harness — opt-in only; several probes take minutes to hours. Run explicitly by name.")]
    public async Task Probe_MaxLevelsVsModelBoundCap()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var sink = new SqlCaptureSink();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<MxNodeProfile>(),
            configureServices: s =>
            {
                s.AddSingleton(sink);
                s.AddDbContext<MxDbContext>(o =>
                {
                    o.UseSqlite(conn);
                    o.LogTo(m => sink.Add(m),
                        (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted);
                });
            });
        await using (fx)
        {
            using (IServiceScope scope = fx.App.Services.CreateScope())
            {
                MxDbContext db = scope.ServiceProvider.GetRequiredService<MxDbContext>();
                db.Database.EnsureCreated();
                for (int i = 1; i <= 16; i++)
                    db.MxNodes.Add(new MxNode { Id = i, Name = "N" + i, ParentId = i == 1 ? null : i - 1 });
                await db.SaveChangesAsync();
            }

            foreach (string lv in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "max" })
            {
                sink.Clear();
                var sw = Stopwatch.StartNew();
                HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/MxNodes?$expand=Children($levels={lv})");
                string body = await resp.Content.ReadAsStringAsync();
                sw.Stop();
                int joins = sink.Snapshot()
                    .Where(s => s.Contains("\"MxNodes\"", StringComparison.Ordinal))
                    .Select(s => System.Text.RegularExpressions.Regex.Matches(s, @"\bJOIN\b").Count)
                    .DefaultIfEmpty(0).Max();
                _out.WriteLine($"$levels={lv,-4} -> {(int)resp.StatusCode} {sw.ElapsedMilliseconds,7} ms joins={joins,2} len={body.Length}");
            }
        }
    }
}
