using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OData;
using Microsoft.OData.Client;
using Microsoft.OData.ModelBuilder;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

// Entity type must be public so Microsoft.OData.Client can instantiate it via reflection
// from a different assembly. Uses a distinct namespace to avoid clash with internal Widget
// defined in ODataProtocolComplianceTests.cs.
namespace OhData.MicrosoftClientCompatibility.MsClient;

// ── Entity type ───────────────────────────────────────────────────────────────

[Key("Id")]
public class WidgetDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

// ── Server profile ────────────────────────────────────────────────────────────

internal class WidgetDtoProfile : EntitySetProfile<int, WidgetDto>
{
    private readonly List<WidgetDto> _store;

    public WidgetDtoProfile() : base(x => x.Id)
    {
        IdempotentDelete = false;

        _store = new List<WidgetDto>
        {
            new() { Id = 1, Name = "Sprocket", Price = 4.99m  },
            new() { Id = 2, Name = "Cog",      Price = 2.50m  },
            new() { Id = 3, Name = "Bracket",  Price = 12.00m },
        };

        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
    }
}

// ── TestServer ↔ Microsoft.OData.Client transport adapter ────────────────────
//
// Microsoft.OData.Client uses the abstract DataServiceClientRequestMessage /
// IODataResponseMessage protocol for all HTTP I/O. These two lightweight adapters
// bridge that protocol to a plain HttpClient backed by the in-process TestServer.

internal sealed class TestServerRequestMessage : DataServiceClientRequestMessage
{
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
    private MemoryStream? _bodyStream;

    public TestServerRequestMessage(DataServiceClientRequestMessageArgs args, HttpClient httpClient)
        : base(args.Method)
    {
        _httpClient = httpClient;
        Method = args.Method;
        Url = args.RequestUri;
        foreach (var kv in args.Headers)
            SetHeader(kv.Key, kv.Value);
    }

    public override IEnumerable<KeyValuePair<string, string>> Headers => _headers;
    public override Uri Url { get; set; }
    public override string Method { get; set; }
    public override int Timeout { get; set; } = 30_000;
    public override bool SendChunked { get; set; }

    public override string GetHeader(string headerName) =>
        _headers.TryGetValue(headerName, out string? v) ? v : null!;

    public override void SetHeader(string headerName, string headerValue) =>
        _headers[headerName] = headerValue;

    public override void Abort() { /* TestServer requests can't be aborted mid-flight */ }

    public override Stream GetStream()
    {
        _bodyStream ??= new MemoryStream();
        return _bodyStream;
    }

    public override IAsyncResult BeginGetRequestStream(AsyncCallback callback, object state)
    {
        var tcs = new TaskCompletionSource<Stream>(state);
        tcs.SetResult(GetStream());
        callback?.Invoke(tcs.Task);
        return tcs.Task;
    }

    public override Stream EndGetRequestStream(IAsyncResult asyncResult) =>
        ((Task<Stream>)asyncResult).GetAwaiter().GetResult();

    private HttpRequestMessage BuildRequest()
    {
        var req = new HttpRequestMessage(new HttpMethod(Method), Url);
        foreach (var kv in _headers)
        {
            if (kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            if (kv.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        if (_bodyStream is { Length: > 0 })
        {
            _bodyStream.Position = 0;
            var content = new StreamContent(_bodyStream);
            if (_headers.TryGetValue("Content-Type", out string? ct))
                content.Headers.TryAddWithoutValidation("Content-Type", ct);
            req.Content = content;
        }
        return req;
    }

    public override IODataResponseMessage GetResponse()
    {
        using var req = BuildRequest();
        var response = _httpClient.SendAsync(req).GetAwaiter().GetResult();
        return new TestServerResponseMessage(response);
    }

    public override IAsyncResult BeginGetResponse(AsyncCallback callback, object state)
    {
        var req = BuildRequest();
        var tcs = new TaskCompletionSource<IODataResponseMessage>(state);
        _ = _httpClient.SendAsync(req).ContinueWith(t =>
        {
            if (t.IsFaulted)
                tcs.SetException(t.Exception!.InnerExceptions);
            else if (t.IsCanceled)
                tcs.SetCanceled();
            else
                tcs.SetResult(new TestServerResponseMessage(t.Result));
            callback?.Invoke(tcs.Task);
        }, TaskScheduler.Default);
        return tcs.Task;
    }

    public override IODataResponseMessage EndGetResponse(IAsyncResult asyncResult) =>
        ((Task<IODataResponseMessage>)asyncResult).GetAwaiter().GetResult();
}

internal sealed class TestServerResponseMessage : IODataResponseMessage
{
    private readonly HttpResponseMessage _response;
    private Stream? _stream;

    public TestServerResponseMessage(HttpResponseMessage response) => _response = response;

    public IEnumerable<KeyValuePair<string, string>> Headers =>
        _response.Headers
            .Concat(_response.Content.Headers)
            .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(",", h.Value)));

    public int StatusCode
    {
        get => (int)_response.StatusCode;
        set => throw new NotSupportedException();
    }

    public string GetHeader(string headerName)
    {
        if (_response.Headers.TryGetValues(headerName, out var vals)) return string.Join(",", vals);
        if (_response.Content.Headers.TryGetValues(headerName, out vals)) return string.Join(",", vals);
        return null!;
    }

    public Stream GetStream()
    {
        _stream ??= _response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        return _stream;
    }

    public void SetHeader(string headerName, string headerValue) { }
}

// ── Test fixture ──────────────────────────────────────────────────────────────

internal sealed class MsClientFixture : IAsyncDisposable
{
    private const string Prefix = "/odata";

    private readonly WebApplication _app;
    private readonly HttpClient _httpClient;
    public DataServiceContext Context { get; }

    private MsClientFixture(WebApplication app)
    {
        _app = app;
        _httpClient = ((IHost)app).GetTestClient();
        var serviceUri = new Uri(_httpClient.BaseAddress!, Prefix.Trim('/') + "/");

        // Build the EDM model locally — Format.UseJson() with no argument fetches $metadata
        // via its own internal HttpClient (bypassing OnMessageCreating), so we pre-build it
        // instead to avoid a real network call.
        var modelBuilder = new ODataConventionModelBuilder();
        modelBuilder.EntitySet<WidgetDto>("WidgetDtos");
        var model = modelBuilder.GetEdmModel();

        Context = new DataServiceContext(serviceUri);
        // Wire the MS OData client to use the TestServer HttpClient for all requests.
        Context.Configurations.RequestPipeline.OnMessageCreating =
            args => new TestServerRequestMessage(args, _httpClient);
        Context.Format.UseJson(model);
        // Map EDM type names to the CLR type.
        Context.ResolveType = name =>
            name.EndsWith(nameof(WidgetDto), StringComparison.Ordinal) ? typeof(WidgetDto) : null;
        Context.ResolveName = type =>
            type == typeof(WidgetDto)
                ? $"{typeof(WidgetDto).Namespace}.{typeof(WidgetDto).Name}"
                : null;
    }

    public static async Task<MsClientFixture> BuildAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        // MS OData Client expects PascalCase property names (per OData 4.0 spec).
        // Override ASP.NET Core's default camelCase to produce a spec-compliant server.
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.PropertyNamingPolicy = null);
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(Prefix);
            o.AddProfile<WidgetDtoProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();
        return new MsClientFixture(app);
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        await _app.DisposeAsync();
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Integration tests that exercise the OhData server through the Microsoft.OData.Client
/// library, verifying OData 4.0 protocol compatibility from the perspective of the
/// industry-standard client.
///
/// Transport: Microsoft.OData.Client → TestServerRequestMessage → HttpClient → TestServer → OhData
/// </summary>
public class MsODataClientIntegrationTests : IAsyncDisposable
{
    private readonly MsClientFixture _fixture;
    private DataServiceContext Context => _fixture.Context;

    public MsODataClientIntegrationTests()
    {
        _fixture = MsClientFixture.BuildAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    // ── GET collection ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_ReturnsAllWidgets()
    {
        var query = Context.CreateQuery<WidgetDto>("WidgetDtos");
        var widgets = (await query.ExecuteAsync()).ToList();
        Assert.Equal(3, widgets.Count);
    }

    // ── $filter ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Filter_ByName_ReturnsMatchingWidget()
    {
        var query = Context.CreateQuery<WidgetDto>("WidgetDtos")
            .AddQueryOption("$filter", "Name eq 'Sprocket'");
        var widgets = (await query.ExecuteAsync()).ToList();
        Assert.Single(widgets);
        Assert.Equal("Sprocket", widgets[0].Name);
    }

    [Fact]
    public async Task Filter_ByPrice_ReturnsMatchingWidgets()
    {
        var query = Context.CreateQuery<WidgetDto>("WidgetDtos")
            .AddQueryOption("$filter", "Price gt 3");
        var widgets = (await query.ExecuteAsync()).ToList();
        Assert.True(widgets.Count >= 2);
        Assert.All(widgets, w => Assert.True(w.Price > 3));
    }

    // ── $orderby ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrderBy_Price_ReturnsSortedWidgets()
    {
        var query = Context.CreateQuery<WidgetDto>("WidgetDtos")
            .AddQueryOption("$orderby", "Price asc");
        var widgets = (await query.ExecuteAsync()).ToList();
        Assert.Equal(3, widgets.Count);
        var prices = widgets.Select(w => w.Price).ToList();
        Assert.Equal(prices.OrderBy(p => p).ToList(), prices);
    }

    [Fact]
    public async Task OrderByDescending_Name_ReturnsSortedWidgets()
    {
        var query = Context.CreateQuery<WidgetDto>("WidgetDtos")
            .AddQueryOption("$orderby", "Name desc");
        var widgets = (await query.ExecuteAsync()).ToList();
        Assert.Equal(3, widgets.Count);
        var names = widgets.Select(w => w.Name).ToList();
        Assert.Equal(names.OrderByDescending(n => n).ToList(), names);
    }

    // ── $top and $skip ────────────────────────────────────────────────────────

    [Fact]
    public async Task Top_LimitsResults()
    {
        var query = Context.CreateQuery<WidgetDto>("WidgetDtos")
            .AddQueryOption("$top", "1");
        var widgets = (await query.ExecuteAsync()).ToList();
        Assert.Single(widgets);
    }

    [Fact]
    public async Task Skip_SkipsResults()
    {
        var query = Context.CreateQuery<WidgetDto>("WidgetDtos")
            .AddQueryOption("$orderby", "Id asc")
            .AddQueryOption("$skip", "1");
        var widgets = (await query.ExecuteAsync()).ToList();
        Assert.Equal(2, widgets.Count);
        Assert.DoesNotContain(widgets, w => w.Id == 1);
    }

    // ── $count inline ─────────────────────────────────────────────────────────

    [Fact]
    public async Task InlineCount_ReturnsODataCountInResponse()
    {
        var query = Context.CreateQuery<WidgetDto>("WidgetDtos")
            .IncludeCount()
            .AddQueryOption("$filter", "Price gt 3");
        var response = (QueryOperationResponse<WidgetDto>)await query.ExecuteAsync();
        var widgets = response.ToList();
        Assert.True(widgets.Count >= 2);
        Assert.True(response.Count >= 2);
    }

    // ── GET by key ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByKey_ReturnsCorrectWidget()
    {
        var uri = new Uri(Context.BaseUri, "WidgetDtos(1)");
        var results = await Context.ExecuteAsync<WidgetDto>(uri, "GET", singleResult: true);
        var widget = results.Single();
        Assert.Equal(1, widget.Id);
        Assert.Equal("Sprocket", widget.Name);
    }

    [Fact]
    public async Task GetByKey_NonExistentId_ThrowsDataServiceQueryException()
    {
        var uri = new Uri(Context.BaseUri, "WidgetDtos(9999)");
        await Assert.ThrowsAsync<DataServiceQueryException>(
            () => Context.ExecuteAsync<WidgetDto>(uri, "GET", singleResult: true));
    }

    [Fact]
    public async Task GetByKey_ViaFilter_ReturnsCorrectWidget()
    {
        var query = Context.CreateQuery<WidgetDto>("WidgetDtos")
            .AddQueryOption("$filter", "Id eq 1");
        var widgets = (await query.ExecuteAsync()).ToList();
        Assert.Single(widgets);
        Assert.Equal(1, widgets[0].Id);
        Assert.Equal("Sprocket", widgets[0].Name);
    }

    [Fact]
    public async Task GetByKey_ViaFilter_NonExistentId_ReturnsEmpty()
    {
        var query = Context.CreateQuery<WidgetDto>("WidgetDtos")
            .AddQueryOption("$filter", "Id eq 9999");
        var widgets = (await query.ExecuteAsync()).ToList();
        Assert.Empty(widgets);
    }
}
