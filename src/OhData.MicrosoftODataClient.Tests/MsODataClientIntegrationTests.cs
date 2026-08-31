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
using OhData;
using Xunit;

// Entity type must be public so Microsoft.OData.Client can instantiate it via reflection
// from a different assembly. Uses a distinct namespace to avoid clash with internal Widget
// defined in ODataProtocolComplianceTests.cs.
namespace OhData.MicrosoftODataClient.Tests.MsClient;

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
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        CountEnabled = true;

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
        foreach (var kv in _headers.Where(kv =>
            !kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
            !kv.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
        {
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

    /// <summary>
    /// Every request URI Microsoft.OData.Client built, in order, recorded by the same
    /// <c>OnMessageCreating</c> hook that supplies the transport. This is what lets a test assert
    /// the URL the client PRODUCES rather than only the answer it got back: the difference matters
    /// on <c>/$count</c>, where the client appends the segment to an already-built option string
    /// and strips nothing, so a server that refuses those options breaks standard pagination.
    /// </summary>
    public List<Uri> RequestedUris { get; } = new();

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
            args =>
            {
                RequestedUris.Add(args.RequestUri);
                return new TestServerRequestMessage(args, _httpClient);
            };
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
        // MS OData Client expects PascalCase property names (per OData 4.0 spec). Since #252 that
        // is OhData's default — payloads match $metadata (OData §4.4) with no host JSON config —
        // so the former ConfigureHttpJsonOptions(PropertyNamingPolicy = null) override is redundant
        // and has been removed. This fixture now proves the server is spec-compliant out of the box.
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(Prefix);
            o.AddEntitySetProfile<WidgetDtoProfile>();
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

    // -- /$count through the real client: the LINQ shapes that build the URL -------------------
    //
    // These exist because the entity-set /$count route was once narrowed to an implemented set of
    // $filter + $format, refusing $top/$skip/$orderby/$expand with 501. That narrowing passed the
    // whole server suite: this project -- which exists for exactly this compatibility question --
    // covered only a bare GET /odata/Widgets/$count, so nothing failed.
    //
    // Microsoft.OData.Client translates LongCount() by appending the /$count segment to the query
    // it has ALREADY built and stripping nothing from the option string, so OrderBy/Take/Skip
    // before a LongCount() all ride along into the request. Refusing them therefore breaks the
    // standard pagination shape of the industry-standard OData client, not merely a hand-built
    // grid URL. OData v4.0 Part 1 §11.2.9 settles the behaviour independently: the count is taken
    // "after applying any $filter or $search" and "MUST NOT be affected by $top, $skip, $orderby,
    // or $expand" -- present and ignored, which is what these assert.
    //
    // Each test asserts BOTH halves: the URL the client produced (so a future narrowing cannot be
    // waved off as "no client sends that") and the count that came back (so accept-and-ignore is
    // proved, rather than merely accept).

    private string LastCountUri()
    {
        Uri uri = Assert.Single(
            _fixture.RequestedUris,
            u => u.AbsolutePath.EndsWith("/$count", StringComparison.Ordinal));
        return Uri.UnescapeDataString(uri.ToString());
    }

    [Fact]
    public void Count_BareLongCount_HitsCountSegment_AndReturnsTotal()
    {
        var q = Context.CreateQuery<WidgetDto>("WidgetDtos");
        long count = q.LongCount();

        Assert.EndsWith("/odata/WidgetDtos/$count", LastCountUri(), StringComparison.Ordinal);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Count_WhereThenLongCount_AppliesTheFilter()
    {
        // §11.2.9's positive half: $filter is one of the two options the count IS taken after.
        var q = Context.CreateQuery<WidgetDto>("WidgetDtos");
        long count = q.Where(w => w.Id > 1).LongCount();

        Assert.EndsWith("/odata/WidgetDtos/$count?$filter=Id gt 1", LastCountUri(), StringComparison.Ordinal);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Count_OrderByThenLongCount_IgnoresTheOrderBy()
    {
        var q = Context.CreateQuery<WidgetDto>("WidgetDtos");
        long count = q.OrderBy(w => w.Name).LongCount();

        Assert.EndsWith("/odata/WidgetDtos/$count?$orderby=Name", LastCountUri(), StringComparison.Ordinal);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Count_TakeThenLongCount_IgnoresTheTop()
    {
        // Take(2) would leave 2 rows on the collection route; on /$count the total stays 3.
        var q = Context.CreateQuery<WidgetDto>("WidgetDtos");
        long count = q.Take(2).LongCount();

        Assert.EndsWith("/odata/WidgetDtos/$count?$top=2", LastCountUri(), StringComparison.Ordinal);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Count_SkipThenLongCount_IgnoresTheSkip()
    {
        var q = Context.CreateQuery<WidgetDto>("WidgetDtos");
        long count = q.Skip(1).LongCount();

        Assert.EndsWith("/odata/WidgetDtos/$count?$skip=1", LastCountUri(), StringComparison.Ordinal);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Count_FilterAndWindowTogether_AppliesOnlyTheFilter()
    {
        // The whole §11.2.9 partition on one request: $filter narrows the count to 2, and neither
        // $orderby nor $skip nor $top moves it off 2. This is the shape a paging client actually
        // emits -- "how many match, ignoring which page I am on".
        var q = Context.CreateQuery<WidgetDto>("WidgetDtos");
        long count = q.Where(w => w.Id > 1).OrderBy(w => w.Name).Skip(1).Take(1).LongCount();

        string uri = LastCountUri();
        Assert.Contains("$filter=Id gt 1", uri, StringComparison.Ordinal);
        Assert.Contains("$orderby=Name", uri, StringComparison.Ordinal);
        Assert.Contains("$skip=1", uri, StringComparison.Ordinal);
        Assert.Contains("$top=1", uri, StringComparison.Ordinal);
        Assert.Equal(2, count);
    }
}
