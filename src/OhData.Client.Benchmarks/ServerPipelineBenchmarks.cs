using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
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
using Microsoft.AspNetCore.OData.Deltas;

namespace OhData.Client.Benchmarks;

/// <summary>
/// Server-side pipeline benchmarks measuring the full ASP.NET Core request path
/// through OhData: routing → profile resolution → handler → serialization.
/// Uses an in-process TestHost to isolate from network overhead.
///
/// These benchmarks establish a baseline before the scoped-profile architectural change.
/// </summary>
[MemoryDiagnoser]
public class ServerPipelineBenchmarks
{
    private WebApplication _serverApp = null!;
    private HttpClient _http = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            o.AddEntitySetProfile<PipelineWidgetProfile>();
        });

        _serverApp = builder.Build();
        _serverApp.MapOhData();
        await _serverApp.StartAsync();

        _http = ((IHost)_serverApp).GetTestClient();
        _http.BaseAddress = new Uri(_http.BaseAddress!, "odata/");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _http.Dispose();
        await _serverApp.DisposeAsync();
    }

    // ── Collection GET ────────────────────────────────────────────────────────

    /// <summary>
    /// GET /PipelineWidgets — IQueryable collection, no query options.
    /// Measures: routing + ODataQueryOptions construction + ApplyTo + JSON serialization.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task<string> GetCollection()
    {
        using var response = await _http.GetAsync("PipelineWidgets");
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// GET /PipelineWidgets?$filter=price gt 5 — exercises OData $filter parsing + ApplyTo.
    /// </summary>
    [Benchmark]
    public async Task<string> GetCollection_Filter()
    {
        using var response = await _http.GetAsync("PipelineWidgets?$filter=price gt 5");
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// GET /PipelineWidgets?$orderby=name — exercises OData $orderby parsing + ApplyTo.
    /// </summary>
    [Benchmark]
    public async Task<string> GetCollection_OrderBy()
    {
        using var response = await _http.GetAsync("PipelineWidgets?$orderby=name");
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// GET /PipelineWidgets?$select=id,name — exercises $select JSON post-processing.
    /// </summary>
    [Benchmark]
    public async Task<string> GetCollection_Select()
    {
        using var response = await _http.GetAsync("PipelineWidgets?$select=id,name");
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// GET /PipelineWidgets?$top=5&amp;$skip=2&amp;$orderby=id — paging query.
    /// </summary>
    [Benchmark]
    public async Task<string> GetCollection_TopSkip()
    {
        using var response = await _http.GetAsync("PipelineWidgets?$top=5&$skip=2&$orderby=id");
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// GET /PipelineWidgets/$count — plain-text count response.
    /// </summary>
    [Benchmark]
    public async Task<string> GetCollection_Count()
    {
        using var response = await _http.GetAsync("PipelineWidgets/$count");
        return await response.Content.ReadAsStringAsync();
    }

    // ── Single entity GET ─────────────────────────────────────────────────────

    /// <summary>
    /// GET /PipelineWidgets(1) — single entity by key.
    /// Measures: key parsing + handler invocation + JSON serialization.
    /// </summary>
    [Benchmark]
    public async Task<string> GetById()
    {
        using var response = await _http.GetAsync("PipelineWidgets(1)");
        return await response.Content.ReadAsStringAsync();
    }

    // ── Writes ────────────────────────────────────────────────────────────────

    private static readonly StringContent PostBody = new(
        JsonSerializer.Serialize(new { name = "NewWidget", price = 9.99m, isActive = true }),
        Encoding.UTF8,
        "application/json");

    /// <summary>
    /// POST /PipelineWidgets — create entity.
    /// Measures: JSON deserialization + handler + JSON serialization.
    /// </summary>
    [Benchmark]
    public async Task<string> Post()
    {
        using var response = await _http.PostAsync("PipelineWidgets", PostBody);
        return await response.Content.ReadAsStringAsync();
    }

    private static readonly StringContent PutBody = new(
        JsonSerializer.Serialize(new { id = 1, name = "Updated", price = 1.00m, isActive = false }),
        Encoding.UTF8,
        "application/json");

    /// <summary>
    /// PUT /PipelineWidgets(1) — full entity replace.
    /// </summary>
    [Benchmark]
    public async Task<string> Put()
    {
        using var response = await _http.PutAsync("PipelineWidgets(1)", PutBody);
        return await response.Content.ReadAsStringAsync();
    }

    private static readonly StringContent PatchBody = new(
        JsonSerializer.Serialize(new { name = "Patched" }),
        Encoding.UTF8,
        "application/json");

    /// <summary>
    /// PATCH /PipelineWidgets(1) — partial update via Delta.
    /// </summary>
    [Benchmark]
    public async Task<string> Patch()
    {
        using var response = await _http.PatchAsync("PipelineWidgets(1)", PatchBody);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// DELETE /PipelineWidgets(1) — delete entity.
    /// </summary>
    [Benchmark]
    public async Task<string> Delete()
    {
        using var response = await _http.DeleteAsync("PipelineWidgets(1)");
        return await response.Content.ReadAsStringAsync();
    }
}

// ── Profile for server pipeline benchmarks ───────────────────────────────────

internal sealed class PipelineWidget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool IsActive { get; set; }
}

internal sealed class PipelineWidgetProfile : EntitySetProfile<int, PipelineWidget>
{
    private static readonly List<PipelineWidget> _store = Enumerable.Range(1, 20)
        .Select(i => new PipelineWidget
        {
            Id = i,
            Name = $"Widget{i}",
            Price = i * 1.5m,
            IsActive = i % 2 == 0,
        })
        .ToList();

    public PipelineWidgetProfile() : base(x => x.Id)
    {
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;

        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());

        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));

        Post = (widget, ct) =>
        {
            // Don't mutate _store in benchmarks — just return the entity
            widget.Id = 999;
            return Task.FromResult<PipelineWidget?>(widget);
        };

        Put = (id, widget, ct) =>
        {
            widget.Id = id;
            return Task.FromResult(widget);
        };

        Delete = (id, ct) => Task.FromResult(true);

        Patch = (id, delta, ct) =>
        {
            var existing = _store.FirstOrDefault(w => w.Id == id);
            if (existing is null) return Task.FromResult<PipelineWidget?>(null);
            // Don't mutate — return a copy
            var copy = new PipelineWidget
            {
                Id = existing.Id,
                Name = existing.Name,
                Price = existing.Price,
                IsActive = existing.IsActive,
            };
            delta.Patch(copy);
            return Task.FromResult<PipelineWidget?>(copy);
        };
    }
}
