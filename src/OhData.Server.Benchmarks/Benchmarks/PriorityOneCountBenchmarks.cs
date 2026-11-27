using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData;

namespace OhData.Server.Benchmarks.Benchmarks;

internal sealed class P1CountRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class P1CountDbContext : DbContext
{
    public P1CountDbContext(DbContextOptions<P1CountDbContext> options) : base(options) { }
    public DbSet<P1CountRow> Rows => Set<P1CountRow>();
}

internal sealed class P1CountProfile : ODataEntitySetProfile<int, P1CountRow>
{
    public P1CountProfile(P1CountDbContext db) : base(x => x.Id)
    {
        EntitySetName = "P1CountRows";
        FilterEnabled = OrderByEnabled = CountEnabled = true;
        GetODataQueryable = (options, ct) => Task.FromResult(new ODataQueryResult<P1CountRow>
        {
            Items = options.ApplyTo(db.Rows.AsNoTracking()) as IQueryable<P1CountRow> ?? db.Rows.AsNoTracking(),
        });
    }
}

/// <summary>
/// #748: what a Priority-1 <c>/$count</c> costs, bare and with <c>?$top=2</c> (the request whose
/// answer changed), plus a <c>$filter</c> arm. Rows live in in-memory Sqlite so the count is a real
/// <c>COUNT(*)</c>. Run on the pre-fix commit for the "before" number. Config copied from
/// <c>PolymorphicExpandBenchmarks</c>.
/// </summary>
[Config(typeof(P1CountConfig))]
[MemoryDiagnoser]
public class PriorityOneCountBenchmarks
{
    private sealed class P1CountConfig : ManualConfig
    {
        public P1CountConfig()
        {
            AddJob(Job.Default
                .WithInvocationCount(32)
                .WithUnrollFactor(1)
                .WithMinWarmupCount(50)
                .WithMaxWarmupCount(100)
                .WithIterationCount(30));
        }
    }

    private const int RowCount = 1000;

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
        builder.Services.AddDbContext<P1CountDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(BenchmarkHosts.Prefix);
            o.AddEntitySetProfile<P1CountProfile>();
        });
        _app = builder.Build();
        _app.MapOhData();
        await _app.StartAsync();

        using (IServiceScope scope = _app.Services.CreateScope())
        {
            P1CountDbContext db = scope.ServiceProvider.GetRequiredService<P1CountDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Rows.AddRange(Enumerable.Range(1, RowCount).Select(i => new P1CountRow { Id = i, Name = "Row " + i }));
            await db.SaveChangesAsync();
        }

        _client = ((IHost)_app).GetTestClient();
        _client.BaseAddress = new Uri(_client.BaseAddress!, "odata/");

        // Status only, not the number: the same class must run on the pre-fix commit, where $top=2 answers 2.
        foreach (string url in new[] { "P1CountRows/$count", "P1CountRows/$count?$top=2", "P1CountRows/$count?$filter=Id gt 500" })
        {
            using HttpResponseMessage response = await _client.GetAsync(url);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{url} returned {(int)response.StatusCode}: {body}");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _connection.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task<string> BareCount() => GetAsync("P1CountRows/$count");

    [Benchmark]
    public Task<string> CountWithTop() => GetAsync("P1CountRows/$count?$top=2");

    [Benchmark]
    public Task<string> CountWithFilter() => GetAsync("P1CountRows/$count?$filter=Id gt 500");

    private async Task<string> GetAsync(string url)
    {
        using HttpResponseMessage response = await _client.GetAsync(url);
        return await response.Content.ReadAsStringAsync();
    }
}
