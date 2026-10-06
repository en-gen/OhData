using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData;

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

        GetQueryable = () => _store.Items.AsQueryable();
        GetById = (id, ct) => OhDataResult.Success(_store.Items.FirstOrDefault(w => w.Id == id));
        Post = (widget, ct) =>
        {
            widget.Id = _store.Items.Count > 0 ? _store.Items.Max(w => w.Id) + 1 : 1;
            _store.Items.Add(widget);
            return OhDataResult.Success<Widget>(widget);
        };
        Put = (id, w, ct) =>
        {
            int removed = _store.Items.RemoveAll(x => x.Id == id);
            if (removed == 0) return OhDataResult.Success<Widget?>(null!);
            w.Id = id;
            _store.Items.Add(w);
            return OhDataResult.Success(w);
        };
        Patch = (id, delta, ct) =>
        {
            var existing = _store.Items.FirstOrDefault(x => x.Id == id);
            if (existing is null) return OhDataResult.Success<Widget?>(null);
            delta.Patch(existing);
            return OhDataResult.Success<Widget?>(existing);
        };
        Delete = (id, ct) => OhDataResult.Success(_store.Items.RemoveAll(w => w.Id == id) > 0);
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

        GetById = (id, ct) => OhDataResult.Success(_store.Items.FirstOrDefault(w => w.Id == id));
        Post = (widget, ct) =>
        {
            widget.Id = _store.Items.Count > 0 ? _store.Items.Max(w => w.Id) + 1 : 1;
            _store.Items.Add(widget);
            return OhDataResult.Success<Widget>(widget);
        };
        Put = (id, w, ct) =>
        {
            int removed = _store.Items.RemoveAll(x => x.Id == id);
            if (removed == 0) return OhDataResult.Success<Widget?>(null!);
            w.Id = id;
            _store.Items.Add(w);
            return OhDataResult.Success(w);
        };
        Patch = (id, delta, ct) =>
        {
            var existing = _store.Items.FirstOrDefault(x => x.Id == id);
            if (existing is null) return OhDataResult.Success<Widget?>(null);
            delta.Patch(existing);
            return OhDataResult.Success<Widget?>(existing);
        };
        Delete = (id, ct) => OhDataResult.Success(_store.Items.RemoveAll(w => w.Id == id) > 0);

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
        GetQueryable = () => _store.AsQueryable();
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
            o.AddEntitySetProfile<WidgetProfile>();
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
            o.AddEntitySetProfile<WidgetProfile>();
            o.AddEntitySetProfile<ETagWidgetProfile>();
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
        GetQueryable = () => _store.AsQueryable();
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
            o.AddEntitySetProfile<TemporalWidgetProfile>();
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
        // #313 stage 4: the existing NEW-1 fixture, reused rather than replaced, so the nested
        // Tags@odata.count regression is pinned against a model and profile this change did not
        // author. Only the flag is new -- no existing test issues an $expand against this set.
        ExpandEnabled = true;
        // Deliberately no FilterProperties allowlist -- the NEW-1 repro shape.
        GetQueryable = () => _store.AsQueryable();
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
            o.AddEntitySetProfile<TaggedItemProfile>();
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
    public HttpClient Http { get; }

    private PaginatedClientTestFixture(WebApplication app, string prefix)
    {
        _app = app;
        HttpClient httpClient = ((IHost)app).GetTestClient();
        httpClient.BaseAddress = new Uri(httpClient.BaseAddress!, prefix.Trim('/') + "/");
        Http = httpClient;
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
            o.AddEntitySetProfile<PaginatedWidgetProfile>();
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

/// <summary>
/// Records every request (URL, method, headers, body) and the response headers, and answers from a
/// script or by forwarding to a real server's <see cref="HttpClient"/>.
/// </summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public sealed class Seen
    {
        public required string Url { get; init; }
        public required HttpMethod Method { get; init; }
        public required Dictionary<string, string[]> Headers { get; init; }
        public string? Body { get; init; }
        public Dictionary<string, string[]> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string[] Prefer => Headers.TryGetValue("Prefer", out string[]? v) ? v : [];
    }

    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _script;

    public RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> script)
        => _script = (req, i) => Task.FromResult(script(req, i));

    private RecordingHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> script) => _script = script;

    public List<Seen> Requests { get; } = [];

    /// <summary>Answers every request from <paramref name="server"/>, recording both directions.</summary>
    public static RecordingHandler Forwarding(HttpClient server)
        => new(async (req, _) =>
        {
            // HttpClient refuses to send one message twice, so forward a copy.
            using var copy = new HttpRequestMessage(req.Method, req.RequestUri);
            foreach (var h in req.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (req.Content is not null)
            {
                copy.Content = new ByteArrayContent(await req.Content.ReadAsByteArrayAsync());
                foreach (var h in req.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            return await server.SendAsync(copy);
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var seen = new Seen
        {
            Url = request.RequestUri!.OriginalString,
            Method = request.Method,
            Headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct),
        };
        Requests.Add(seen);
        HttpResponseMessage response = await _script(request, Requests.Count - 1);
        foreach (var h in response.Headers) seen.ResponseHeaders[h.Key] = h.Value.ToArray();
        return response;
    }

    public static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    public static OhDataClient ClientFor(
        RecordingHandler handler, Action<OhDataClientOptions>? configure = null, Action<HttpClient>? configureHttp = null,
        Uri? baseAddress = null)
    {
        var http = new HttpClient(handler) { BaseAddress = baseAddress ?? new Uri("http://localhost/odata/") };
        configureHttp?.Invoke(http);
        var options = new OhDataClientOptions();
        configure?.Invoke(options);
        return new OhDataClient(http, options);
    }
}

/// <summary>One read-only bench server shared by a test class that never mutates it.</summary>
internal sealed class BenchServerFixture : Xunit.IAsyncLifetime
{
    public OhData.ClientTestBench.BenchServer Server { get; private set; } = null!;
    public OhDataClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Server = await OhData.ClientTestBench.BenchServer.BuildAsync();
        Client = new OhDataClient(Server.Http);
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Server.DisposeAsync();
    }
}
