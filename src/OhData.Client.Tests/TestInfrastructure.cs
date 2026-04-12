using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData.Abstractions;
using OhData.AspNetCore;

namespace OhData.Client.Tests;

// ── Test entities ────────────────────────────────────────────────────────────

internal class Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal class WidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store;

    public WidgetProfile() : base(x => x.Id)
    {
        IdempotentDelete = false;

        _store = new List<Widget>
        {
            new() { Id = 1, Name = "Sprocket" },
            new() { Id = 2, Name = "Cog" },
        };

        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Post = (widget, ct) =>
        {
            widget.Id = _store.Count > 0 ? _store.Max(w => w.Id) + 1 : 1;
            _store.Add(widget);
            return Task.FromResult<Widget?>(widget);
        };
        PutById = (id, w, ct) =>
        {
            _store.RemoveAll(x => x.Id == id);
            w.Id = id;
            _store.Add(w);
            return Task.FromResult(w);
        };
        Patch = (id, w, ct) =>
        {
            var existing = _store.FirstOrDefault(x => x.Id == id);
            if (existing is null) return Task.FromResult<Widget?>(null);
            if (w.Name != "") existing.Name = w.Name;
            return Task.FromResult<Widget?>(existing);
        };
        Delete = (id, ct) => Task.FromResult(_store.RemoveAll(w => w.Id == id) > 0);
    }
}

// ── TestFixture ──────────────────────────────────────────────────────────────

internal sealed class ClientTestFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    public HttpClient HttpClient { get; }
    public OhDataClient Client { get; }

    private ClientTestFixture(WebApplication app, string prefix)
    {
        _app = app;
        HttpClient = ((IHost)app).GetTestClient();
        // Point the base address at the OData prefix so relative URLs like "Widgets" resolve correctly.
        HttpClient.BaseAddress = new Uri(HttpClient.BaseAddress!, prefix.Trim('/') + "/");
        Client = new OhDataClient(HttpClient);
    }

    public static async Task<ClientTestFixture> BuildAsync(string prefix = "/odata")
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
        return new ClientTestFixture(app, prefix);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        HttpClient.Dispose();
        await _app.DisposeAsync();
    }
}
