using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData.Abstractions;
using OhData.AspNetCore;
using OhData.Client;

namespace OhData.Client.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="EntitySetClient{T}.ToListAsync"/>.
/// Measures the full round-trip: URL build → HTTP → JSON deserialization.
/// Uses an in-process TestHost to isolate from real network overhead.
/// </summary>
[MemoryDiagnoser]
public class ToListAsyncBenchmarks
{
    private WebApplication _serverApp = null!;
    private OhDataClient _client = null!;
    private HttpClient _httpClient = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            o.AddEntitySetProfile<BenchWidgetProfile>();
        });

        _serverApp = builder.Build();
        _serverApp.MapOhData();
        await _serverApp.StartAsync();

        _httpClient = ((IHost)_serverApp).GetTestClient();
        _httpClient.BaseAddress = new Uri(_httpClient.BaseAddress!, "odata/");
        _client = new OhDataClient(_httpClient);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        _httpClient.Dispose();
        await _serverApp.DisposeAsync();
    }

    /// <summary>
    /// GET all entities — measures HTTP + JSON deserialization overhead.
    /// </summary>
    [Benchmark(Baseline = true)]
    public Task<List<BenchWidget>> ToListAsync_NoOptions()
        => _client.For<BenchWidget>().ToListAsync();

    /// <summary>
    /// GET with $filter — measures URL encoding + server-side filtering + deserialization.
    /// </summary>
    [Benchmark]
    public Task<List<BenchWidget>> ToListAsync_WithFilter()
        => _client.For<BenchWidget>()
            .Filter(x => x.Price > 5)
            .ToListAsync();

    /// <summary>
    /// GET $count — measures plain-text response parsing.
    /// </summary>
    [Benchmark]
    public Task<long> CountAsync()
        => _client.For<BenchWidget>().CountAsync();
}

// ── Profile for benchmarks ────────────────────────────────────────────────────

internal sealed class BenchWidgetProfile : EntitySetProfile<int, BenchWidget>
{
    private static readonly List<BenchWidget> _store = Enumerable.Range(1, 10)
        .Select(i => new BenchWidget { Id = i, Name = $"Widget{i}", Price = i * 1.5m, IsActive = i % 2 == 0 })
        .ToList();

    public BenchWidgetProfile() : base(x => x.Id)
    {
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
    }
}
