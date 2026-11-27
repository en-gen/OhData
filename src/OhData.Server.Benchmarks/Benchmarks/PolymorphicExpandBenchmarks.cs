using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData;

namespace OhData.Server.Benchmarks.Benchmarks;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(PolyDerivedKid), "d")]
internal class PolyKid
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class PolyDerivedKid : PolyKid
{
    public string Sparkle { get; set; } = "";
}

internal sealed class PolyPlainKid
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class PolyParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<PolyKid> Kids { get; set; } = new();
    public List<PolyPlainKid> PlainKids { get; set; } = new();
}

internal sealed class PolyDbContext : DbContext
{
    public PolyDbContext(DbContextOptions<PolyDbContext> options) : base(options) { }
    public DbSet<PolyParent> PolyParents => Set<PolyParent>();
    public DbSet<PolyKid> PolyKids => Set<PolyKid>();
    public DbSet<PolyPlainKid> PolyPlainKids => Set<PolyPlainKid>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<PolyParent>().HasMany(p => p.Kids).WithOne().HasForeignKey(k => k.ParentId);
        b.Entity<PolyParent>().HasMany(p => p.PlainKids).WithOne().HasForeignKey(k => k.ParentId);
        b.Entity<PolyDerivedKid>();
    }
}

internal sealed class PolyParentProfile : EntitySetProfile<int, PolyParent>
{
    public PolyParentProfile(PolyDbContext db) : base(x => x.Id)
    {
        EntitySetName = "PolyParents";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = () => db.PolyParents.AsQueryable();
        HasMany(x => x.Kids);
        HasMany(x => x.PlainKids);
    }
}

/// <summary>
/// #740: what serving an expanded navigation whose TARGET is a TPH hierarchy through the Include path
/// costs, against the same shape on a non-polymorphic target (member-init pushdown). Run the class on
/// the pre-fix commit for the "before" number: there the polymorphic arm used the member-init too, and
/// returned derived rows as the declared type.
/// Config is copied from <see cref="BareExpandCeilingBenchmarks"/>; read its remarks first.
/// </summary>
[Config(typeof(PolyBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[MemoryDiagnoser]
public class PolymorphicExpandBenchmarks
{
    private sealed class PolyBenchmarkConfig : ManualConfig
    {
        public PolyBenchmarkConfig()
        {
            AddJob(Job.Default
                .WithInvocationCount(32)
                .WithUnrollFactor(1)
                .WithMinWarmupCount(50)
                .WithMaxWarmupCount(100)
                .WithIterationCount(30));
        }
    }

    private const int ParentCount = 20;

    [Params(5, 50)]
    public int KidsPerParent { get; set; }

    private const string PolyUrl = "PolyParents?$orderby=Id&$top=20&$expand=Kids";
    private const string PlainUrl = "PolyParents?$orderby=Id&$top=20&$expand=PlainKids";

    private WebApplication _app = null!;
    private SqliteConnection _connection = null!;
    private HttpClient _client = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddDbContext<PolyDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(BenchmarkHosts.Prefix);
            o.AddEntitySetProfile<PolyParentProfile>();
        });
        _app = builder.Build();
        _app.MapOhData();
        await _app.StartAsync();

        using (IServiceScope scope = _app.Services.CreateScope())
        {
            PolyDbContext db = scope.ServiceProvider.GetRequiredService<PolyDbContext>();
            await db.Database.EnsureCreatedAsync();
            int id = 1;
            for (int p = 1; p <= ParentCount; p++)
            {
                db.PolyParents.Add(new PolyParent { Id = p, Name = $"Parent {p}" });
                for (int k = 0; k < KidsPerParent; k++, id++)
                {
                    db.PolyKids.Add(k % 2 == 0
                        ? new PolyKid { Id = id, ParentId = p, Name = $"Kid {id}" }
                        : new PolyDerivedKid { Id = id, ParentId = p, Name = $"Kid {id}", Sparkle = $"S{id}" });
                    db.PolyPlainKids.Add(new PolyPlainKid { Id = id, ParentId = p, Name = $"Kid {id}" });
                }
            }
            await db.SaveChangesAsync();
        }

        _client = ((IHost)_app).GetTestClient();
        _client.BaseAddress = new Uri(_client.BaseAddress!, "odata/");

        // Both arms must return every kid row; the derived members are deliberately NOT gated here,
        // so the same class can be run on the pre-fix commit.
        foreach (string url in new[] { PolyUrl, PlainUrl })
        {
            using HttpResponseMessage response = await _client.GetAsync(url);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{url} returned {(int)response.StatusCode}: {body}");
            int actual = System.Text.RegularExpressions.Regex.Matches(body, "\"ParentId\":").Count;
            if (actual != ParentCount * KidsPerParent)
                throw new InvalidOperationException($"{url} returned {actual} kid rows, expected {ParentCount * KidsPerParent}.");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _connection.Dispose();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PolymorphicExpand")]
    public Task<string> NonPolymorphicTarget() => GetAsync(PlainUrl);

    [Benchmark, BenchmarkCategory("PolymorphicExpand")]
    public Task<string> PolymorphicTarget() => GetAsync(PolyUrl);

    private async Task<string> GetAsync(string url)
    {
        using HttpResponseMessage response = await _client.GetAsync(url);
        return await response.Content.ReadAsStringAsync();
    }
}
