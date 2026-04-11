using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData.Abstractions;
using OhData.AspNetCore;
using OhData.Client;

namespace OhData.ClientTestBench;

// ── Entity ────────────────────────────────────────────────────────────────────

internal class Widget
{
    public int    Id    { get; set; }
    public string Name  { get; set; } = "";
    public decimal Price { get; set; }
}

// ── Profile ───────────────────────────────────────────────────────────────────

internal class WidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store;

    public WidgetProfile() : base(x => x.Id)
    {
        IdempotentDelete = false;

        _store = new List<Widget>
        {
            new() { Id = 1, Name = "Sprocket", Price = 4.99m  },
            new() { Id = 2, Name = "Cog",      Price = 2.50m  },
            new() { Id = 3, Name = "Bracket",  Price = 12.00m },
        };

        GetQueryable = (ct)       => Task.FromResult(_store.AsQueryable());
        GetById      = (id, ct)   => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Post         = (w, ct)    =>
        {
            w.Id = _store.Count > 0 ? _store.Max(x => x.Id) + 1 : 1;
            _store.Add(w);
            return Task.FromResult(w);
        };
        PutById      = (id, w, ct) =>
        {
            _store.RemoveAll(x => x.Id == id);
            w.Id = id;
            _store.Add(w);
            return Task.FromResult(w);
        };
        Patch        = (id, w, ct) =>
        {
            var existing = _store.FirstOrDefault(x => x.Id == id);
            if (existing is null) return Task.FromResult<Widget?>(null);
            if (w.Name  != "") existing.Name  = w.Name;
            if (w.Price != 0)  existing.Price = w.Price;
            return Task.FromResult<Widget?>(existing);
        };
        Delete       = (id, ct)   => Task.FromResult(_store.RemoveAll(w => w.Id == id) > 0);
    }
}

// ── Test bench ────────────────────────────────────────────────────────────────

internal static class Program
{
    private static readonly JsonSerializerOptions _prettyPrint = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    public static async Task Main(string[] args)
    {
        // Build both the OData server and a minimal web host with /health in one process.
        var serverApp = await BuildServerAsync("/odata");

        // Create the OhDataClient pointing at the in-process test server.
        using var httpClient = ((IHost)serverApp).GetTestClient();
        httpClient.BaseAddress = new Uri(httpClient.BaseAddress!, "odata/");
        using var client = new OhDataClient(httpClient);

        // Build the outer web application that exposes /health and runs the demo.
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://localhost:5198");
        builder.Services.AddLogging(b => b.ClearProviders().AddConsole());

        var app = builder.Build();

        app.MapGet("/health", () => new { status = "ok", timestamp = DateTime.UtcNow });

        app.MapGet("/run-demo", async ctx =>
        {
            await RunDemoAsync(client, ctx.Response.Body);
        });

        Console.WriteLine("=== OhData.Client TestBench ===");
        Console.WriteLine("Running demo directly on startup...");
        Console.WriteLine();

        await RunDemoAsync(client, Console.OpenStandardOutput());

        Console.WriteLine();
        Console.WriteLine("Server running at http://localhost:5198");
        Console.WriteLine("  GET /health      — health check");
        Console.WriteLine("  GET /run-demo    — run all OData client operations");
        Console.WriteLine();

        await app.RunAsync();

        await serverApp.DisposeAsync();
    }

    private static async Task<WebApplication> BuildServerAsync(string prefix)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            o.AddProfile<WidgetProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();
        return app;
    }

    // ReSharper disable once UnusedParameter.Local — stream used for output
    private static async Task RunDemoAsync(OhDataClient client, System.IO.Stream output)
    {
        var writer = new System.IO.StreamWriter(output, leaveOpen: true);
        writer.AutoFlush = true;

        await writer.WriteLineAsync("--- GET collection (all widgets) ---");
        var all = await client.For<Widget>().ToListAsync();
        await writer.WriteLineAsync(Serialize(all));

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- GET with $filter (Price > 3) ---");
        var filtered = await client.For<Widget>()
            .Filter(x => x.Price > 3)
            .ToListAsync();
        await writer.WriteLineAsync(Serialize(filtered));

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- GET with $select (Id, Name) ---");
        var selected = await client.For<Widget>()
            .Select(x => new { x.Id, x.Name })
            .ToListAsync();
        await writer.WriteLineAsync(Serialize(selected));

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- GET with $orderby (Price desc) ---");
        var ordered = await client.For<Widget>()
            .OrderByDescending(x => x.Price)
            .ToListAsync();
        await writer.WriteLineAsync(Serialize(ordered));

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- GET with $top=1 $skip=1 ---");
        var paged = await client.For<Widget>()
            .OrderBy(x => x.Id)
            .Top(1)
            .Skip(1)
            .ToListAsync();
        await writer.WriteLineAsync(Serialize(paged));

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- GET $count ---");
        var count = await client.For<Widget>().CountAsync();
        await writer.WriteLineAsync($"Total: {count}");

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- AnyAsync ---");
        var any = await client.For<Widget>().AnyAsync();
        await writer.WriteLineAsync($"Any: {any}");

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- ToPageAsync ($count=true, $top=2) ---");
        var page = await client.For<Widget>().Top(2).ToPageAsync();
        await writer.WriteLineAsync($"Items: {page.Items.Count}, TotalCount: {page.TotalCount}");
        await writer.WriteLineAsync(Serialize(page.Items));

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- GET single by key (id=1) ---");
        var single = await client.For<Widget>().Key(1).GetAsync();
        await writer.WriteLineAsync(Serialize(single));

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- GET missing key (id=9999) ---");
        var missing = await client.For<Widget>().Key(9999).GetAsync();
        await writer.WriteLineAsync($"Result: {(missing is null ? "null (404)" : Serialize(missing))}");

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- POST (insert new widget) ---");
        var inserted = await client.For<Widget>().InsertAsync(new Widget { Name = "NewWidget", Price = 7.77m });
        await writer.WriteLineAsync(Serialize(inserted));

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- PUT (replace widget id=1) ---");
        var replaced = await client.For<Widget>().Key(1).PutAsync(new Widget { Id = 1, Name = "Sprocket-Replaced", Price = 5.55m });
        await writer.WriteLineAsync(Serialize(replaced));

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- PATCH (partial update widget id=2) ---");
        var patched = await client.For<Widget>().Key(2).PatchAsync(new { Name = "Cog-Patched" });
        await writer.WriteLineAsync(Serialize(patched));

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- DELETE (widget id=3) ---");
        await client.For<Widget>().Key(3).DeleteAsync();
        await writer.WriteLineAsync("Deleted id=3");

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- DELETE non-existent (id=9999) — expects ODataClientException ---");
        try
        {
            await client.For<Widget>().Key(9999).DeleteAsync();
            await writer.WriteLineAsync("ERROR: Expected ODataClientException but none was thrown!");
        }
        catch (ODataClientException ex)
        {
            await writer.WriteLineAsync($"Got expected ODataClientException: HTTP {ex.StatusCode} [{ex.ODataErrorCode}] {ex.ODataErrorMessage}");
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("--- FirstOrDefaultAsync with filter ---");
        var first = await client.For<Widget>()
            .Filter(x => x.Price > 3)
            .OrderBy(x => x.Price)
            .FirstOrDefaultAsync();
        await writer.WriteLineAsync(Serialize(first));

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("=== Demo complete ===");
    }

    private static string Serialize(object? value)
        => JsonSerializer.Serialize(value, _prettyPrint);
}
