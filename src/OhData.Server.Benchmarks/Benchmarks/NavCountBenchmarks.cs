using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData;

namespace OhData.Server.Benchmarks.Benchmarks;

internal sealed class NavCountChild
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class NavCountParent
{
    public int Id { get; set; }
    public List<NavCountChild> Children { get; set; } = new();
}

/// <summary>Navigation-route fixture: the route is served by a <c>getAll</c> delegate over an in-memory list.</summary>
internal sealed class NavCountParentProfile : EntitySetProfile<int, NavCountParent>
{
    internal static List<NavCountChild> Rows { get; set; } = new();

    public NavCountParentProfile() : base(x => x.Id)
    {
        EntitySetName = "NavCountParents";
        CountEnabled = true;
        OrderByEnabled = true;
        GetQueryable = () => new[] { new NavCountParent { Id = 1 } }.AsQueryable();
        GetById = (id, ct) => OhDataResult.Success<NavCountParent?>(new NavCountParent { Id = id });
        HasMany(x => x.Children, (key, ct) => Task.FromResult<IEnumerable<NavCountChild>>(Rows));
    }
}

/// <summary>
/// #354: cost of <c>$count=true</c> on a navigation-collection read. OhData only: the MS benchmark
/// host has no navigation-property action. Job config is copied from
/// <see cref="ExpandComparisonBenchmarks"/> (pinned invocation count, floored warmup); see its remarks.
/// </summary>
[Config(typeof(NavCountConfig))]
[MemoryDiagnoser]
public class NavCountBenchmarks
{
    private sealed class NavCountConfig : ManualConfig
    {
        public NavCountConfig()
        {
            AddJob(Job.Default
                .WithInvocationCount(32)
                .WithUnrollFactor(1)
                .WithMinWarmupCount(50)
                .WithMaxWarmupCount(100)
                .WithIterationCount(30));
        }
    }

    private const string Url = "NavCountParents(1)/Children?$count=true&$skip=1&$orderby=Name";
    private const int ChildCount = 1000;

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        NavCountParentProfile.Rows = Enumerable.Range(1, ChildCount)
            .Select(i => new NavCountChild { Id = i, Name = $"Child {(i * 7919) % ChildCount:D4}" })
            .ToList();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(BenchmarkHosts.Prefix);
            o.AddEntitySetProfile<NavCountParentProfile>();
        });
        _app = builder.Build();
        _app.MapOhData();
        await _app.StartAsync();

        _client = ((IHost)_app).GetTestClient();
        _client.BaseAddress = new Uri(_client.BaseAddress!, "odata/");

        // Correctness gate: a 404 or empty body would make the measurement meaningless.
        using HttpResponseMessage response = await _client.GetAsync(Url);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode || !body.Contains("\"Child 0", StringComparison.Ordinal))
            throw new InvalidOperationException($"nav read returned {(int)response.StatusCode}: {body}");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Benchmark]
    public async Task<string> OhData_NavCount()
    {
        using HttpResponseMessage response = await _client.GetAsync(Url);
        return await response.Content.ReadAsStringAsync();
    }
}
