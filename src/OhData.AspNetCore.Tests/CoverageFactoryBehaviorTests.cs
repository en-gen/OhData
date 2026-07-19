using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Coverage pass: assorted factory behavior branches not previously exercised — literal-<c>null</c>
/// write bodies (400 InvalidBody), PATCH key-in-body vs key-in-URL mismatch, and the Priority-1
/// (<c>GetODataQueryable</c>) malformed-query and unsupported-<c>$search</c> rejections.
/// </summary>
public class CoverageFactoryBehaviorTests
{
    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Post_LiteralNullBody_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var resp = await fx.Client.PostAsync("/odata/Widgets", Json("null"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Put_LiteralNullBody_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var resp = await fx.Client.PutAsync("/odata/Widgets(1)", Json("null"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_KeyInBodyMismatchesUrl_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var resp = await fx.Client.PatchAsync("/odata/Widgets(1)", Json("{\"id\":2,\"name\":\"x\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Priority1_MalformedFilter_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ODataWidgetProfile>());
        var resp = await fx.Client.GetAsync("/odata/ODataWidgets?$filter=name eq");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetQueryable_SearchWithoutHandler_Returns400()
    {
        // UnimplQueryableProfile exposes GetQueryable but no Search handler.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<UnimplQueryableProfile>());
        var resp = await fx.Client.GetAsync("/odata/UnimplQueryableWidgets?$search=foo");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UnsupportedQueryOption", body.GetProperty("error").GetProperty("code").GetString());
    }
}
