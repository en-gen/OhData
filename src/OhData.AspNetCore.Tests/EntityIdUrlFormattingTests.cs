using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// S4: entity-id URLs (POST 201 Location/Content-Location, OData-EntityId, @odata.id, and the
/// same envelope on GetById/PUT/PATCH) must use canonical OData key syntax -- string keys quoted
/// and percent-encoded (Part 2 §4.3.1) -- not the raw/unquoted CLR <c>ToString()</c> the framework
/// previously emitted for POST, nor an un-re-encoded echo of the client's own URL segment for
/// GetById/PUT/PATCH. int/Guid keys are unaffected (no quoting; formatting unchanged).
/// </summary>
public class EntityIdUrlFormattingTests
{
    private const string BaseUrl = "http://localhost/odata";

    // OData key-literal quoting (Part 2 §4.3.1: embedded ' doubled) followed by percent-encoding
    // -- mirrors ODataEntityKeyUrlFormatter.Format for string keys. .NET's Uri.EscapeDataString
    // percent-encodes the quote character itself (there is no unencoded-apostrophe carve-out),
    // so both the wrapping and any doubled embedded quotes become %27.
    private static string QuotedKeySegment(string rawKey) =>
        Uri.EscapeDataString($"'{rawKey.Replace("'", "''")}'");

    private class StringKeyItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    // Backing store injected as a DI singleton (mirrors EntityBoundOpsStore in Fixtures.cs) so
    // state survives across the separate HTTP requests within a test -- profiles are resolved
    // per-request (scoped), so an instance field on the profile itself would reset every request.
    private class StringKeyStore
    {
        public List<StringKeyItem> Items { get; } = new();
    }

    private class StringKeyProfile : EntitySetProfile<string, StringKeyItem>
    {
        private readonly StringKeyStore _store;

        public StringKeyProfile(StringKeyStore store) : base(x => x.Id)
        {
            _store = store;
            EntitySetName = "StringKeyItems";
            GetById = (id, ct) => Task.FromResult(_store.Items.FirstOrDefault(x => x.Id == id));
            Post = (item, ct) =>
            {
                _store.Items.Add(item);
                return Task.FromResult<StringKeyItem?>(item);
            };
            Put = (id, item, ct) =>
            {
                _store.Items.RemoveAll(x => x.Id == id);
                item.Id = id;
                _store.Items.Add(item);
                return Task.FromResult(item);
            };
            Patch = (id, delta, ct) =>
            {
                var existing = _store.Items.FirstOrDefault(x => x.Id == id);
                if (existing is null) return Task.FromResult<StringKeyItem?>(null);
                delta.Patch(existing);
                return Task.FromResult<StringKeyItem?>(existing);
            };
        }
    }

    private class IntKeyItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class IntKeyStore
    {
        public List<IntKeyItem> Items { get; } = new();
    }

    private class IntKeyProfile : EntitySetProfile<int, IntKeyItem>
    {
        private readonly IntKeyStore _store;

        public IntKeyProfile(IntKeyStore store) : base(x => x.Id)
        {
            _store = store;
            EntitySetName = "UrlFormatIntItems";
            GetById = (id, ct) => Task.FromResult(_store.Items.FirstOrDefault(x => x.Id == id));
            Post = (item, ct) =>
            {
                item.Id = 42;
                _store.Items.Add(item);
                return Task.FromResult<IntKeyItem?>(item);
            };
        }
    }

    private class GuidKeyItem
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class GuidKeyStore
    {
        public List<GuidKeyItem> Items { get; } = new();
    }

    private class GuidKeyProfile : EntitySetProfile<Guid, GuidKeyItem>
    {
        public static readonly Guid FixedId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        private readonly GuidKeyStore _store;

        public GuidKeyProfile(GuidKeyStore store) : base(x => x.Id)
        {
            _store = store;
            EntitySetName = "UrlFormatGuidItems";
            GetById = (id, ct) => Task.FromResult(_store.Items.FirstOrDefault(x => x.Id == id));
            Post = (item, ct) =>
            {
                item.Id = FixedId;
                _store.Items.Add(item);
                return Task.FromResult<GuidKeyItem?>(item);
            };
        }
    }

    private static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    // ── POST 201 Location / Content-Location / OData-EntityId / @odata.id ──────────

    [Fact]
    public async Task Post_StringKeyWithSpace_LocationIsQuotedAndEncoded()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<StringKeyProfile>(),
            configureServices: s => s.AddSingleton(new StringKeyStore()));
        var resp = await fx.Client.PostAsync("/odata/StringKeyItems", JsonBody(new { id = "a b", name = "X" }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        string expected = $"{BaseUrl}/StringKeyItems({QuotedKeySegment("a b")})";
        Assert.Equal(expected, resp.Headers.Location!.AbsoluteUri);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, json.GetProperty("@odata.id").GetString());
    }

    [Fact]
    public async Task Post_StringKeyWithEmbeddedQuote_LocationDoublesQuoteThenEncodes()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<StringKeyProfile>(),
            configureServices: s => s.AddSingleton(new StringKeyStore()));
        var resp = await fx.Client.PostAsync("/odata/StringKeyItems", JsonBody(new { id = "it's", name = "X" }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        // OData key escaping doubles the embedded quote ('it''s'), then the whole literal is
        // percent-encoded for the URL.
        string expected = $"{BaseUrl}/StringKeyItems({QuotedKeySegment("it's")})";
        Assert.Equal(expected, resp.Headers.Location!.AbsoluteUri);
    }

    [Fact]
    public async Task Post_StringKeyWithUnicode_LocationIsQuotedAndPercentEncoded_AndRoundTrips()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<StringKeyProfile>(),
            configureServices: s => s.AddSingleton(new StringKeyStore()));
        const string unicodeKey = "café";
        var resp = await fx.Client.PostAsync("/odata/StringKeyItems", JsonBody(new { id = unicodeKey, name = "X" }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        string expected = $"{BaseUrl}/StringKeyItems({QuotedKeySegment(unicodeKey)})";
        Assert.Equal(expected, resp.Headers.Location!.AbsoluteUri);

        // The emitted Location must be directly dereferenceable (round-trips through
        // ODataKeyParser's unescaping/decoding back to the original key).
        var getResp = await fx.Client.GetAsync(resp.Headers.Location!.AbsoluteUri);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var json = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(unicodeKey, json.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Post_IntKey_LocationUnchanged()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<IntKeyProfile>(),
            configureServices: s => s.AddSingleton(new IntKeyStore()));
        var resp = await fx.Client.PostAsync("/odata/UrlFormatIntItems", JsonBody(new { name = "X" }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal($"{BaseUrl}/UrlFormatIntItems(42)", resp.Headers.Location!.AbsoluteUri);
    }

    [Fact]
    public async Task Post_GuidKey_LocationUnchanged()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<GuidKeyProfile>(),
            configureServices: s => s.AddSingleton(new GuidKeyStore()));
        var resp = await fx.Client.PostAsync("/odata/UrlFormatGuidItems", JsonBody(new { name = "X" }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal($"{BaseUrl}/UrlFormatGuidItems({GuidKeyProfile.FixedId})", resp.Headers.Location!.AbsoluteUri);
    }

    // ── GetById/PUT/PATCH @odata.id round trip for string keys ─────────────────────

    [Fact]
    public async Task GetById_StringKeyWithSpace_ODataIdIsCanonical()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<StringKeyProfile>(),
            configureServices: s => s.AddSingleton(new StringKeyStore()));
        var postResp = await fx.Client.PostAsync("/odata/StringKeyItems", JsonBody(new { id = "a b", name = "X" }));
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);

        var resp = await fx.Client.GetAsync(postResp.Headers.Location!.AbsoluteUri);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"{BaseUrl}/StringKeyItems({QuotedKeySegment("a b")})", json.GetProperty("@odata.id").GetString());
    }

    [Fact]
    public async Task Put_StringKeyWithSpace_ODataIdIsCanonical()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<StringKeyProfile>(),
            configureServices: s => s.AddSingleton(new StringKeyStore()));
        var postResp = await fx.Client.PostAsync("/odata/StringKeyItems", JsonBody(new { id = "a b", name = "X" }));
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);

        var resp = await fx.Client.PutAsync(postResp.Headers.Location!.AbsoluteUri, JsonBody(new { id = "a b", name = "Y" }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"{BaseUrl}/StringKeyItems({QuotedKeySegment("a b")})", json.GetProperty("@odata.id").GetString());
    }

    [Fact]
    public async Task Patch_StringKeyWithQuote_ODataIdIsCanonical()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<StringKeyProfile>(),
            configureServices: s => s.AddSingleton(new StringKeyStore()));
        var postResp = await fx.Client.PostAsync("/odata/StringKeyItems", JsonBody(new { id = "it's", name = "X" }));
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);

        var patchReq = new HttpRequestMessage(HttpMethod.Patch, postResp.Headers.Location!.AbsoluteUri)
        {
            Content = JsonBody(new { name = "Y" })
        };
        var resp = await fx.Client.SendAsync(patchReq);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"{BaseUrl}/StringKeyItems({QuotedKeySegment("it's")})", json.GetProperty("@odata.id").GetString());
    }

    [Fact]
    public async Task GetById_IntKey_ODataIdUnchanged()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<IntKeyProfile>(),
            configureServices: s => s.AddSingleton(new IntKeyStore()));
        var postResp = await fx.Client.PostAsync("/odata/UrlFormatIntItems", JsonBody(new { name = "X" }));
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);

        var resp = await fx.Client.GetAsync(postResp.Headers.Location!.AbsoluteUri);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"{BaseUrl}/UrlFormatIntItems(42)", json.GetProperty("@odata.id").GetString());
    }
}
