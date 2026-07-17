using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OData.Deltas;
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

internal class WidgetStore
{
    public List<Widget> Items { get; } = new()
    {
        new() { Id = 1, Name = "Sprocket" },
        new() { Id = 2, Name = "Cog" },
    };
}

internal class WidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly WidgetStore _store;

    public WidgetProfile(WidgetStore store) : base(x => x.Id)
    {
        _store = store;
        IdempotentDelete = false;
        FilterEnabled = true;
        SelectEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;

        GetQueryable = (ct) => Task.FromResult(_store.Items.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.Items.FirstOrDefault(w => w.Id == id));
        Post = (widget, ct) =>
        {
            widget.Id = _store.Items.Count > 0 ? _store.Items.Max(w => w.Id) + 1 : 1;
            _store.Items.Add(widget);
            return Task.FromResult<Widget?>(widget);
        };
        Put = (id, w, ct) =>
        {
            int removed = _store.Items.RemoveAll(x => x.Id == id);
            if (removed == 0) return Task.FromResult<Widget>(null!);
            w.Id = id;
            _store.Items.Add(w);
            return Task.FromResult(w);
        };
        Patch = (id, delta, ct) =>
        {
            var existing = _store.Items.FirstOrDefault(x => x.Id == id);
            if (existing is null) return Task.FromResult<Widget?>(null);
            delta.Patch(existing);
            return Task.FromResult<Widget?>(existing);
        };
        Delete = (id, ct) => Task.FromResult(_store.Items.RemoveAll(w => w.Id == id) > 0);
    }
}

/// <summary>
/// Profile with ETag support for optimistic concurrency tests.
/// ETag is derived from the widget's Name field.
/// </summary>
internal class ETagWidgetStore
{
    public List<Widget> Items { get; } = new()
    {
        new() { Id = 1, Name = "Sprocket" },
        new() { Id = 2, Name = "Cog" },
    };
}

internal class ETagWidgetProfile : EntitySetProfile<int, Widget>
{
    private readonly ETagWidgetStore _store;

    public ETagWidgetProfile(ETagWidgetStore store) : base(x => x.Id)
    {
        _store = store;
        EntitySetName = "ETagWidgets";
        IdempotentDelete = false;

        GetById = (id, ct) => Task.FromResult(_store.Items.FirstOrDefault(w => w.Id == id));
        Post = (widget, ct) =>
        {
            widget.Id = _store.Items.Count > 0 ? _store.Items.Max(w => w.Id) + 1 : 1;
            _store.Items.Add(widget);
            return Task.FromResult<Widget?>(widget);
        };
        Put = (id, w, ct) =>
        {
            int removed = _store.Items.RemoveAll(x => x.Id == id);
            if (removed == 0) return Task.FromResult<Widget>(null!);
            w.Id = id;
            _store.Items.Add(w);
            return Task.FromResult(w);
        };
        Patch = (id, delta, ct) =>
        {
            var existing = _store.Items.FirstOrDefault(x => x.Id == id);
            if (existing is null) return Task.FromResult<Widget?>(null);
            delta.Patch(existing);
            return Task.FromResult<Widget?>(existing);
        };
        Delete = (id, ct) => Task.FromResult(_store.Items.RemoveAll(w => w.Id == id) > 0);

        UseETag(x => x.Name);
    }
}

/// <summary>
/// Profile with MaxTop set for nextLink pagination tests.
/// Contains 10 items with a MaxTop of 3, so the first page always has a nextLink.
/// </summary>
internal class PaginatedWidgetProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store =
        Enumerable.Range(1, 10).Select(i => new Widget { Id = i, Name = $"Widget{i}" }).ToList();

    public PaginatedWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "PaginatedWidgets";
        MaxTop = 3;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
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
        builder.Services.AddSingleton(new WidgetStore());
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

/// <summary>
/// Fixture that registers <see cref="ETagWidgetProfile"/> in addition to <see cref="WidgetProfile"/>.
/// Used for ETag / If-Match integration tests.
/// </summary>
internal sealed class ETagClientTestFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _httpClient;
    public OhDataClient Client { get; }

    private ETagClientTestFixture(WebApplication app, string prefix)
    {
        _app = app;
        _httpClient = ((IHost)app).GetTestClient();
        _httpClient.BaseAddress = new Uri(_httpClient.BaseAddress!, prefix.Trim('/') + "/");
        Client = new OhDataClient(_httpClient);
    }

    public static async Task<ETagClientTestFixture> BuildAsync(string prefix = "/odata")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddSingleton(new WidgetStore());
        builder.Services.AddSingleton(new ETagWidgetStore());
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            o.AddProfile<WidgetProfile>();
            o.AddProfile<ETagWidgetProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();
        return new ETagClientTestFixture(app, prefix);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _httpClient.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Entity + profile used to verify the B3 fix (DateTimeKind handling in $filter literals)
/// against a real OhData server end-to-end, not just at the translator-unit level.
/// </summary>
internal class TemporalWidget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

internal class TemporalWidgetProfile : EntitySetProfile<int, TemporalWidget>
{
    private static readonly List<TemporalWidget> _store = new()
    {
        new() { Id = 1, Name = "Old", CreatedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        new() { Id = 2, Name = "New", CreatedAt = DateTime.UtcNow },
    };

    public TemporalWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "TemporalWidgets";
        FilterEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
    }
}

/// <summary>
/// Fixture that registers <see cref="TemporalWidgetProfile"/> for live DateTimeKind
/// $filter-literal verification (B3).
/// </summary>
internal sealed class TemporalClientTestFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    public OhDataClient Client { get; }

    private TemporalClientTestFixture(WebApplication app, string prefix)
    {
        _app = app;
        HttpClient httpClient = ((IHost)app).GetTestClient();
        httpClient.BaseAddress = new Uri(httpClient.BaseAddress!, prefix.Trim('/') + "/");
        Client = new OhDataClient(httpClient);
    }

    public static async Task<TemporalClientTestFixture> BuildAsync(string prefix = "/odata")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            o.AddProfile<TemporalWidgetProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();
        return new TemporalClientTestFixture(app, prefix);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Entities + profile used to verify the NEW-1 fix (nav-path $filter rejected by the B1
/// allowlist-validation plumbing) end-to-end via the real client's Any/All ($it) translation
/// from PR #140, not just at the FilterTranslator-unit level.
/// </summary>
internal class TaggedItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<ItemTag> Tags { get; set; } = new();
}

internal class ItemTag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal class TaggedItemProfile : EntitySetProfile<int, TaggedItem>
{
    private static readonly List<TaggedItem> _store = new()
    {
        new() { Id = 1, Name = "Foo", Tags = new() { new() { Id = 1, Name = "Red" } } },
        new() { Id = 2, Name = "Bar", Tags = new() { new() { Id = 2, Name = "Blue" } } },
    };

    public TaggedItemProfile() : base(x => x.Id)
    {
        EntitySetName = "TaggedItems";
        FilterEnabled = true;
        // Deliberately no FilterProperties allowlist -- the NEW-1 repro shape.
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        HasMany(x => x.Tags);
    }
}

/// <summary>
/// Fixture that registers <see cref="TaggedItemProfile"/> for live nav-path Any/All $filter
/// verification (NEW-1).
/// </summary>
internal sealed class TaggedItemClientTestFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    public OhDataClient Client { get; }

    private TaggedItemClientTestFixture(WebApplication app, string prefix)
    {
        _app = app;
        HttpClient httpClient = ((IHost)app).GetTestClient();
        httpClient.BaseAddress = new Uri(httpClient.BaseAddress!, prefix.Trim('/') + "/");
        Client = new OhDataClient(httpClient);
    }

    public static async Task<TaggedItemClientTestFixture> BuildAsync(string prefix = "/odata")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            o.AddProfile<TaggedItemProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();
        return new TaggedItemClientTestFixture(app, prefix);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Fixture that registers <see cref="PaginatedWidgetProfile"/> for nextLink pagination tests.
/// </summary>
internal sealed class PaginatedClientTestFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    public OhDataClient Client { get; }

    private PaginatedClientTestFixture(WebApplication app, string prefix)
    {
        _app = app;
        HttpClient httpClient = ((IHost)app).GetTestClient();
        httpClient.BaseAddress = new Uri(httpClient.BaseAddress!, prefix.Trim('/') + "/");
        Client = new OhDataClient(httpClient);
    }

    public static async Task<PaginatedClientTestFixture> BuildAsync(string prefix = "/odata")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            o.AddProfile<PaginatedWidgetProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();
        return new PaginatedClientTestFixture(app, prefix);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
