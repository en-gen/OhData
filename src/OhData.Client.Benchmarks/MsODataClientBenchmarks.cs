using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
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

namespace OhData.Client.Benchmarks;

// ── Entity type ───────────────────────────────────────────────────────────────
// Must be public so Microsoft.OData.Client can materialise it via reflection.
// [Key] attribute (not DataServiceKey) is required by MS OData Client 8.x.

[Key("Id")]
public class MsBenchWidget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool IsActive { get; set; }
}

// ── Server profile ────────────────────────────────────────────────────────────

internal sealed class MsBenchWidgetProfile : EntitySetProfile<int, MsBenchWidget>
{
    private static readonly List<MsBenchWidget> _store = Enumerable.Range(1, 10)
        .Select(i => new MsBenchWidget
        {
            Id = i,
            Name = $"Widget{i}",
            Price = i * 1.5m,
            IsActive = i % 2 == 0,
        })
        .ToList();

    public MsBenchWidgetProfile() : base(x => x.Id)
    {
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Post = (widget, ct) =>
        {
            widget.Id = _store.Count > 0 ? _store.Max(w => w.Id) + 1 : 1;
            _store.Add(widget);
            return Task.FromResult(widget);
        };
    }
}

// ── TestServer ↔ Microsoft.OData.Client transport adapter ─────────────────────
//
// Microsoft.OData.Client uses DataServiceClientRequestMessage / IODataResponseMessage
// for all HTTP I/O. These adapters bridge that protocol to HttpClient backed by
// the in-process ASP.NET Core TestServer.

internal sealed class BenchRequestMessage : DataServiceClientRequestMessage
{
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
    private MemoryStream? _bodyStream;

    public BenchRequestMessage(DataServiceClientRequestMessageArgs args, HttpClient httpClient)
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

    public override void Abort() { }

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
        using HttpRequestMessage req = BuildRequest();
        HttpResponseMessage response = _httpClient.SendAsync(req).GetAwaiter().GetResult();
        return new BenchResponseMessage(response);
    }

    public override IAsyncResult BeginGetResponse(AsyncCallback callback, object state)
    {
        HttpRequestMessage req = BuildRequest();
        var tcs = new TaskCompletionSource<IODataResponseMessage>(state);
        _ = _httpClient.SendAsync(req).ContinueWith(t =>
        {
            if (t.IsFaulted)
                tcs.SetException(t.Exception!.InnerExceptions);
            else if (t.IsCanceled)
                tcs.SetCanceled();
            else
                tcs.SetResult(new BenchResponseMessage(t.Result));
            callback?.Invoke(tcs.Task);
        }, TaskScheduler.Default);
        return tcs.Task;
    }

    public override IODataResponseMessage EndGetResponse(IAsyncResult asyncResult) =>
        ((Task<IODataResponseMessage>)asyncResult).GetAwaiter().GetResult();
}

internal sealed class BenchResponseMessage : IODataResponseMessage
{
    private readonly HttpResponseMessage _response;
    private Stream? _stream;

    public BenchResponseMessage(HttpResponseMessage response) => _response = response;

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
        if (_response.Headers.TryGetValues(headerName, out IEnumerable<string>? vals))
            return string.Join(",", vals);
        if (_response.Content.Headers.TryGetValues(headerName, out vals))
            return string.Join(",", vals);
        return null!;
    }

    public Stream GetStream()
    {
        _stream ??= _response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        return _stream;
    }

    public void SetHeader(string headerName, string headerValue) { }
}

// ── Benchmarks ────────────────────────────────────────────────────────────────

/// <summary>
/// Benchmarks for Microsoft.OData.Client 8.x against the OhData server.
/// Covers the same scenarios as <see cref="ToListAsyncBenchmarks"/> so results
/// can be compared side by side.
///
/// Transport: Microsoft.OData.Client → BenchRequestMessage → HttpClient → TestServer → OhData
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class MsODataClientBenchmarks
{
    private const string EntitySetName = "MsBenchWidgets";
    private const string Prefix = "/odata";

    private WebApplication _serverApp = null!;
    private HttpClient _httpClient = null!;
    private DataServiceContext _context = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        // MS OData Client requires PascalCase JSON (OData 4.0 spec).
        // Override ASP.NET Core's default camelCase serialiser.
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.PropertyNamingPolicy = null);
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(Prefix);
            o.AddProfile<MsBenchWidgetProfile>();
        });

        _serverApp = builder.Build();
        _serverApp.MapOhData();
        await _serverApp.StartAsync();

        _httpClient = ((IHost)_serverApp).GetTestClient();
        Uri serviceUri = new Uri(_httpClient.BaseAddress!, Prefix.Trim('/') + "/");

        // Pre-build the EDM model so Format.UseJson(model) doesn't issue a $metadata call.
        ODataConventionModelBuilder modelBuilder = new ODataConventionModelBuilder();
        modelBuilder.EntitySet<MsBenchWidget>(EntitySetName);
        Microsoft.OData.Edm.IEdmModel model = modelBuilder.GetEdmModel();

        _context = new DataServiceContext(serviceUri);
        _context.Configurations.RequestPipeline.OnMessageCreating =
            args => new BenchRequestMessage(args, _httpClient);
        _context.Format.UseJson(model);
        _context.ResolveType = name =>
            name.EndsWith(nameof(MsBenchWidget), StringComparison.Ordinal)
                ? typeof(MsBenchWidget)
                : null;
        _context.ResolveName = type =>
            type == typeof(MsBenchWidget)
                ? $"{typeof(MsBenchWidget).Namespace}.{typeof(MsBenchWidget).Name}"
                : null;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _httpClient.Dispose();
        await _serverApp.DisposeAsync();
    }

    // ── Benchmark methods ─────────────────────────────────────────────────────

    /// <summary>
    /// GET all entities — equivalent to OhData client's GetAll.
    /// Measures HTTP round-trip + OData JSON deserialization + entity materialisation.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task<List<MsBenchWidget>> GetAll()
    {
        DataServiceQuery<MsBenchWidget> query = _context.CreateQuery<MsBenchWidget>(EntitySetName);
        return (await query.ExecuteAsync()).ToList();
    }

    /// <summary>
    /// GET with $filter — equivalent to OhData client's FilterByName.
    /// </summary>
    [Benchmark]
    public async Task<List<MsBenchWidget>> FilterByName()
    {
        DataServiceQuery<MsBenchWidget> query =
            _context.CreateQuery<MsBenchWidget>(EntitySetName)
                .AddQueryOption("$filter", "Price gt 5");
        return (await query.ExecuteAsync()).ToList();
    }

    /// <summary>
    /// GET single entity via $filter — equivalent to OhData client's GetByKey.
    ///
    /// Note: the direct key-lookup path (ExecuteAsync with singleResult:true) is not
    /// supported against OhData's server because the single-entity response omits
    /// @odata.context, which MS OData Client requires to materialise a bare entity.
    /// $filter is used instead — see limitation note in comparison report.
    /// </summary>
    [Benchmark]
    public async Task<MsBenchWidget?> GetByKey_ViaFilter()
    {
        DataServiceQuery<MsBenchWidget> query =
            _context.CreateQuery<MsBenchWidget>(EntitySetName)
                .AddQueryOption("$filter", "Id eq 1");
        return (await query.ExecuteAsync()).FirstOrDefault();
    }

    /// <summary>
    /// GET with $top — equivalent to OhData client's Top.
    /// </summary>
    [Benchmark]
    public async Task<List<MsBenchWidget>> Top()
    {
        DataServiceQuery<MsBenchWidget> query =
            _context.CreateQuery<MsBenchWidget>(EntitySetName)
                .AddQueryOption("$top", "5");
        return (await query.ExecuteAsync()).ToList();
    }

    /// <summary>
    /// GET with inline $count — equivalent to OhData client's Count.
    /// MS OData Client uses IncludeCount() + QueryOperationResponse.Count;
    /// it does not issue a separate GET /$count request.
    /// </summary>
    [Benchmark]
    public async Task<long> Count()
    {
        DataServiceQuery<MsBenchWidget> query =
            _context.CreateQuery<MsBenchWidget>(EntitySetName).IncludeCount();
        QueryOperationResponse<MsBenchWidget> response =
            (QueryOperationResponse<MsBenchWidget>)await query.ExecuteAsync();
        _ = response.ToList(); // materialise to ensure body is fully read
        return response.Count;
    }

    /// <summary>
    /// POST a new entity — equivalent to OhData client's Insert.
    /// Uses DataServiceContext.AddObject + SaveChangesAsync.
    /// A fresh DataServiceContext is created per call so tracking state does not accumulate
    /// across benchmark iterations (AddObject would throw on repeated saves otherwise).
    /// </summary>
    [Benchmark]
    public async Task Insert()
    {
        Uri serviceUri = new Uri(_httpClient.BaseAddress!, Prefix.Trim('/') + "/");
        ODataConventionModelBuilder modelBuilder = new ODataConventionModelBuilder();
        modelBuilder.EntitySet<MsBenchWidget>(EntitySetName);
        Microsoft.OData.Edm.IEdmModel model = modelBuilder.GetEdmModel();

        DataServiceContext ctx = new DataServiceContext(serviceUri);
        ctx.Configurations.RequestPipeline.OnMessageCreating =
            args => new BenchRequestMessage(args, _httpClient);
        ctx.Format.UseJson(model);
        ctx.ResolveType = name =>
            name.EndsWith(nameof(MsBenchWidget), StringComparison.Ordinal)
                ? typeof(MsBenchWidget)
                : null;
        ctx.ResolveName = type =>
            type == typeof(MsBenchWidget)
                ? $"{typeof(MsBenchWidget).Namespace}.{typeof(MsBenchWidget).Name}"
                : null;

        MsBenchWidget widget = new MsBenchWidget { Name = "NewWidget", Price = 9.99m, IsActive = true };
        ctx.AddObject(EntitySetName, widget);
        await ctx.SaveChangesAsync();
    }
}
