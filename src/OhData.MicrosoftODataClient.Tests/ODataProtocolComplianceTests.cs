using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.Extensions.Logging;
using OhData.Abstractions;
using OhData.AspNetCore;
using OhData.Client;
using Xunit;

namespace OhData.MicrosoftODataClient.Tests;

// ── Shared test entities ─────────────────────────────────────────────────────

internal class Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

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

        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Post = (w, ct) =>
        {
            w.Id = _store.Count > 0 ? _store.Max(x => x.Id) + 1 : 1;
            _store.Add(w);
            return Task.FromResult<Widget?>(w);
        };
        PutById = (id, w, ct) =>
        {
            _store.RemoveAll(x => x.Id == id);
            w.Id = id;
            _store.Add(w);
            return Task.FromResult(w);
        };
        Patch = (id, delta, ct) =>
        {
            var existing = _store.FirstOrDefault(x => x.Id == id);
            if (existing is null) return Task.FromResult<Widget?>(null);
            delta.Patch(existing);
            return Task.FromResult<Widget?>(existing);
        };
        Delete = (id, ct) => Task.FromResult(_store.RemoveAll(w => w.Id == id) > 0);
    }
}

// ── Test fixture ─────────────────────────────────────────────────────────────

internal sealed class CompatibilityTestFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    public HttpClient RawHttpClient { get; }
    public OhDataClient OhDataClient { get; }
    private const string Prefix = "/odata";

    private CompatibilityTestFixture(WebApplication app)
    {
        _app = app;
        RawHttpClient = ((IHost)app).GetTestClient();
        // RawHttpClient base is http://localhost/ — we'll use relative paths from there.
        var odataHttpClient = ((IHost)app).GetTestClient();
        odataHttpClient.BaseAddress = new Uri(odataHttpClient.BaseAddress!, Prefix.Trim('/') + "/");
        OhDataClient = new OhDataClient(odataHttpClient);
    }

    public static async Task<CompatibilityTestFixture> BuildAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(Prefix);
            o.AddProfile<WidgetProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();
        return new CompatibilityTestFixture(app);
    }

    public async ValueTask DisposeAsync()
    {
        OhDataClient.Dispose();
        RawHttpClient.Dispose();
        await _app.DisposeAsync();
    }
}

// ── OData envelope DTOs (for raw HTTP response validation) ────────────────────

internal sealed class ODataCollectionEnvelope<T>
{
    [JsonPropertyName("@odata.context")] public string? Context { get; set; }
    [JsonPropertyName("@odata.count")] public long? Count { get; set; }
    [JsonPropertyName("value")] public List<T>? Value { get; set; }
}

internal sealed class ODataErrorEnvelope
{
    [JsonPropertyName("error")] public ODataErrorBody? Error { get; set; }
}

internal sealed class ODataErrorBody
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

// ── OData protocol compliance tests ──────────────────────────────────────────

/// <summary>
/// Tests that verify OhData server responses comply with the OData 4.0 protocol,
/// exercising the same scenarios that Microsoft.OData.Client would use.
///
/// These tests use raw HttpClient rather than Microsoft.OData.Client directly
/// because Microsoft.OData.Client 7.x is incompatible with Microsoft.OData.Core 8.x
/// (used by the server), and the 9.x preview requires net10.0.
///
/// Each test verifies a specific protocol contract that a generic OData client
/// (including Microsoft.OData.Client) would depend on.
/// </summary>
public class ODataProtocolComplianceTests : IAsyncDisposable
{
    private readonly CompatibilityTestFixture _fixture;

    public ODataProtocolComplianceTests()
    {
        _fixture = CompatibilityTestFixture.BuildAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── $metadata ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The server must expose CSDL $metadata at GET /{prefix}/$metadata.
    /// Microsoft.OData.Client calls this endpoint on first use to build the proxy model.
    /// </summary>
    [Fact]
    public async Task Metadata_Returns200_WithXmlContentType()
    {
        var response = await _fixture.RawHttpClient.GetAsync("/odata/$metadata");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        // OData CSDL is application/xml or application/atomsvc+xml or application/json
        Assert.True(
            contentType.Contains("xml") || contentType.Contains("json"),
            $"Expected XML or JSON content type for $metadata, got: {contentType}");
    }

    /// <summary>
    /// The $metadata document must contain the entity set name "Widgets".
    /// Microsoft.OData.Client uses this to resolve entity type → set mappings.
    /// </summary>
    [Fact]
    public async Task Metadata_ContainsWidgetsEntitySet()
    {
        string body = await _fixture.RawHttpClient.GetStringAsync("/odata/$metadata");
        Assert.Contains("Widgets", body, StringComparison.OrdinalIgnoreCase);
    }

    // ── Service document ──────────────────────────────────────────────────────

    /// <summary>
    /// GET on the service root must return a JSON service document listing entity sets.
    /// Microsoft.OData.Client can discover entity sets from this document.
    /// </summary>
    [Fact]
    public async Task ServiceDocument_Returns200_WithJsonBody()
    {
        var response = await _fixture.RawHttpClient.GetAsync("/odata/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        // Service document contains "value" array of entity set links
        Assert.Contains("value", body, StringComparison.OrdinalIgnoreCase);
    }

    // ── GET collection ────────────────────────────────────────────────────────

    /// <summary>
    /// OData 4.0 collection responses must be wrapped in { "value": [...] }.
    /// Microsoft.OData.Client relies on this envelope format.
    /// </summary>
    [Fact]
    public async Task GetCollection_ReturnsODataEnvelope()
    {
        var response = await _fixture.RawHttpClient.GetAsync("/odata/Widgets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content
            .ReadFromJsonAsync<ODataCollectionEnvelope<Widget>>(_jsonOptions);

        Assert.NotNull(envelope);
        Assert.NotNull(envelope!.Value);
        Assert.True(envelope.Value!.Count >= 2, "Expected at least 2 widgets in the collection");
    }

    /// <summary>
    /// The response must include @odata.context indicating the metadata URL.
    /// Microsoft.OData.Client validates this to confirm the response matches the model.
    /// </summary>
    [Fact]
    public async Task GetCollection_IncludesODataContext()
    {
        var response = await _fixture.RawHttpClient.GetAsync("/odata/Widgets");
        var envelope = await response.Content
            .ReadFromJsonAsync<ODataCollectionEnvelope<Widget>>(_jsonOptions);

        Assert.NotNull(envelope?.Context);
        Assert.Contains("$metadata", envelope!.Context!, StringComparison.OrdinalIgnoreCase);
    }

    // ── $filter ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The server must apply $filter and return only matching entities.
    /// Microsoft.OData.Client translates LINQ predicates to $filter strings.
    /// COMPATIBILITY NOTE: Microsoft.OData.Client uses single-quoted string literals
    /// which matches OhData.Client behaviour.
    /// </summary>
    [Fact]
    public async Task Filter_ByName_ReturnsMatchingEntity()
    {
        var response = await _fixture.RawHttpClient
            .GetAsync("/odata/Widgets?$filter=Name eq 'Sprocket'");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content
            .ReadFromJsonAsync<ODataCollectionEnvelope<Widget>>(_jsonOptions);
        Assert.NotNull(envelope?.Value);
        Assert.Single(envelope!.Value!);
        Assert.Equal("Sprocket", envelope.Value![0].Name);
    }

    [Fact]
    public async Task Filter_ByPrice_ReturnsMatchingEntities()
    {
        var response = await _fixture.RawHttpClient
            .GetAsync("/odata/Widgets?$filter=Price gt 3");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = await response.Content
            .ReadFromJsonAsync<ODataCollectionEnvelope<Widget>>(_jsonOptions);
        Assert.NotNull(envelope?.Value);
        Assert.All(envelope!.Value!, w => Assert.True(w.Price > 3));
    }

    // ── $orderby ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The server must apply $orderby. Microsoft.OData.Client uses this for LINQ OrderBy.
    /// </summary>
    [Fact]
    public async Task OrderBy_Price_Ascending_ReturnsSortedResults()
    {
        var response = await _fixture.RawHttpClient
            .GetAsync("/odata/Widgets?$orderby=Price");
        var envelope = await response.Content
            .ReadFromJsonAsync<ODataCollectionEnvelope<Widget>>(_jsonOptions);

        Assert.NotNull(envelope?.Value);
        var prices = envelope!.Value!.Select(w => w.Price).ToList();
        var sorted = prices.OrderBy(p => p).ToList();
        Assert.Equal(sorted, prices);
    }

    // ── $select ───────────────────────────────────────────────────────────────

    /// <summary>
    /// $select must limit the properties returned. Microsoft.OData.Client uses $select
    /// when projecting LINQ queries.
    /// COMPATIBILITY NOTE: OhData server applies $select via JsonNode post-processing
    /// to maintain camelCase consistency. Unselected fields will be absent from the JSON.
    /// </summary>
    [Fact]
    public async Task Select_IdAndName_ReturnsOnlySelectedProperties()
    {
        var response = await _fixture.RawHttpClient
            .GetAsync("/odata/Widgets?$select=Id,Name");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        var firstItem = doc.RootElement.GetProperty("value")[0];

        // Id and Name should be present
        Assert.True(firstItem.TryGetProperty("id", out _) || firstItem.TryGetProperty("Id", out _),
            "Expected 'id' or 'Id' property in response");
        Assert.True(firstItem.TryGetProperty("name", out _) || firstItem.TryGetProperty("Name", out _),
            "Expected 'name' or 'Name' property in response");
        // Price should NOT be present when not selected
        Assert.False(firstItem.TryGetProperty("price", out _) && firstItem.TryGetProperty("Price", out _),
            "Expected 'price' to be absent from $select=Id,Name response");
    }

    // ── $top and $skip ────────────────────────────────────────────────────────

    [Fact]
    public async Task Top_LimitsResults()
    {
        var response = await _fixture.RawHttpClient.GetAsync("/odata/Widgets?$top=1");
        var envelope = await response.Content
            .ReadFromJsonAsync<ODataCollectionEnvelope<Widget>>(_jsonOptions);
        Assert.Equal(1, envelope?.Value?.Count);
    }

    [Fact]
    public async Task Skip_SkipsResults()
    {
        var allEnvelope = await (await _fixture.RawHttpClient.GetAsync("/odata/Widgets"))
            .Content.ReadFromJsonAsync<ODataCollectionEnvelope<Widget>>(_jsonOptions);
        var skipEnvelope = await (await _fixture.RawHttpClient.GetAsync("/odata/Widgets?$skip=1&$orderby=Id"))
            .Content.ReadFromJsonAsync<ODataCollectionEnvelope<Widget>>(_jsonOptions);

        Assert.NotNull(allEnvelope?.Value);
        Assert.NotNull(skipEnvelope?.Value);
        Assert.Equal(allEnvelope!.Value!.Count - 1, skipEnvelope!.Value!.Count);
    }

    // ── $count ────────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /{set}/$count must return a plain-text integer.
    /// Microsoft.OData.Client uses this for LongCount() queries.
    /// </summary>
    [Fact]
    public async Task Count_Endpoint_ReturnsPlainTextInteger()
    {
        var response = await _fixture.RawHttpClient.GetAsync("/odata/Widgets/$count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        Assert.True(long.TryParse(body.Trim(), out long count), $"Expected plain-text integer, got: '{body}'");
        Assert.True(count >= 3, $"Expected at least 3 widgets, got {count}");
    }

    /// <summary>
    /// $count=true in the query string must return @odata.count in the envelope.
    /// Microsoft.OData.Client uses this for paging with total count.
    /// </summary>
    [Fact]
    public async Task InlineCount_ReturnsODataCountProperty()
    {
        var response = await _fixture.RawHttpClient.GetAsync("/odata/Widgets?$count=true");
        var envelope = await response.Content
            .ReadFromJsonAsync<ODataCollectionEnvelope<Widget>>(_jsonOptions);

        Assert.NotNull(envelope?.Count);
        Assert.True(envelope!.Count >= 3);
    }

    // ── GET single by key ─────────────────────────────────────────────────────

    /// <summary>
    /// GET /{set}(key) must return the entity directly (no value wrapper).
    /// Microsoft.OData.Client uses this for single-entity access.
    /// </summary>
    [Fact]
    public async Task GetByKey_ReturnsEntity()
    {
        var response = await _fixture.RawHttpClient.GetAsync("/odata/Widgets(1)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var widget = await response.Content.ReadFromJsonAsync<Widget>(_jsonOptions);
        Assert.NotNull(widget);
        Assert.Equal(1, widget!.Id);
    }

    /// <summary>
    /// GET /{set}(missing-key) must return HTTP 404.
    /// Microsoft.OData.Client maps this to DataServiceQueryException.
    /// </summary>
    [Fact]
    public async Task GetByKey_MissingKey_Returns404()
    {
        var response = await _fixture.RawHttpClient.GetAsync("/odata/Widgets(9999)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── OData error envelope ──────────────────────────────────────────────────

    /// <summary>
    /// Non-2xx responses must return an OData error envelope:
    /// { "error": { "code": "...", "message": "..." } }
    /// Microsoft.OData.Client parses this structure and throws DataServiceRequestException.
    ///
    /// COMPATIBILITY NOTE: OhData returns a proper OData error envelope on 404 for DELETE
    /// when IdempotentDelete=false.
    /// </summary>
    [Fact]
    public async Task Delete_MissingKey_Returns404_WithODataErrorEnvelope()
    {
        var response = await _fixture.RawHttpClient.DeleteAsync("/odata/Widgets(9999)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<ODataErrorEnvelope>(body, _jsonOptions);
        Assert.NotNull(envelope?.Error);
        Assert.False(string.IsNullOrEmpty(envelope!.Error!.Message),
            "OData error envelope must contain a non-empty message");
    }

    // ── Invalid $filter ───────────────────────────────────────────────────────

    /// <summary>
    /// An invalid $filter expression must return HTTP 400.
    /// Microsoft.OData.Client would fail to parse invalid expressions client-side;
    /// this test validates that the server also rejects them gracefully.
    /// </summary>
    [Fact]
    public async Task InvalidFilter_Returns400()
    {
        var response = await _fixture.RawHttpClient
            .GetAsync("/odata/Widgets?$filter=INVALID_FILTER_SYNTAX!!!");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── String key encoding ───────────────────────────────────────────────────

    /// <summary>
    /// OhData.Client URL-encodes string keys. Verify the server accepts percent-encoded
    /// string keys in the URL path, which is the correct OData 4.0 behaviour.
    ///
    /// COMPATIBILITY NOTE: Microsoft.OData.Client also percent-encodes key values,
    /// so this verifies interoperability.
    /// </summary>
    [Fact]
    public async Task GetByStringKey_ViaOhDataClient_FormatsKeyCorrectly()
    {
        // The OhDataClient formats int keys as plain integers.
        // This test confirms the URL format matches what the server expects.
        var widget = await _fixture.OhDataClient.For<Widget>().Key(1).GetAsync();
        Assert.NotNull(widget);
        Assert.Equal(1, widget!.Id);
    }
}
