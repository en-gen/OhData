using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #203: a per-entity-set (or global-default) <c>MaxRequestBodyBytes</c> limit rejects an oversized
/// write body with <c>413 Payload Too Large</c> before the handler deserializes it. The limit is
/// enforced by a group-level filter for body-bearing write methods (POST/PUT/PATCH). Absent limit =
/// no OhData-level cap (Kestrel's global still applies).
/// </summary>
public class RequestBodySizeLimitTests
{
    private const string LimitedUrl = "/odata/BodyLimitWidgets";   // MaxRequestBodyBytes = 200
    private const string UnlimitedUrl = "/odata/Widgets";          // no limit

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    // A ~400-byte JSON object — comfortably over the 200-byte limit.
    private static string LargeBody() =>
        "{\"name\":\"" + new string('x', 380) + "\"}";

    [Fact]
    public async Task Post_BodyUnderLimit_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<BodyLimitProfile>());
        var resp = await fx.Client.PostAsync(LimitedUrl, Json("{\"name\":\"ok\"}"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task Write_BodyOverLimit_Returns413(string method)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<BodyLimitProfile>());
        string url = method == "POST" ? LimitedUrl : $"{LimitedUrl}(1)";
        using var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = Json(LargeBody()) };
        var resp = await fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode); // 413
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RequestEntityTooLarge", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_NoLimitConfigured_LargeBodySucceeds()
    {
        // WidgetProfile sets no MaxRequestBodyBytes, so a large body is not rejected by OhData.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.PostAsync(UnlimitedUrl, Json(LargeBody()));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task GlobalDefault_AppliesWhenProfileDoesNotOverride()
    {
        // The global default limit is inherited by a profile that sets no per-profile value.
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .WithDefaults(d => d.MaxRequestBodyBytes = 200)
            .AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.PostAsync(UnlimitedUrl, Json(LargeBody()));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
    }

    [Fact]
    public async Task ProfileLimit_OverridesGlobalDefault()
    {
        // Global default is tiny (50), but the profile raises its own limit to 200, so a ~150-byte
        // body (under 200, over 50) is accepted — the profile value wins.
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .WithDefaults(d => d.MaxRequestBodyBytes = 50)
            .AddEntitySetProfile<BodyLimitProfile>());
        string midBody = "{\"name\":\"" + new string('x', 130) + "\"}"; // ~150 bytes
        var resp = await fx.Client.PostAsync(LimitedUrl, Json(midBody));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }
}

internal class BodyLimitProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new();

    public BodyLimitProfile() : base(x => x.Id)
    {
        EntitySetName = "BodyLimitWidgets";
        MaxRequestBodyBytes = 200;

        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
        Post = (widget, ct) =>
        {
            widget.Id = _store.Count > 0 ? _store.Max(w => w.Id) + 1 : 1;
            _store.Add(widget);
            return Task.FromResult<Widget?>(widget);
        };
        Put = (id, widget, ct) => { widget.Id = id; return Task.FromResult(widget); };
        Patch = (id, delta, ct) =>
        {
            var w = new Widget { Id = id };
            delta.Patch(w);
            return Task.FromResult<Widget?>(w);
        };
    }
}
