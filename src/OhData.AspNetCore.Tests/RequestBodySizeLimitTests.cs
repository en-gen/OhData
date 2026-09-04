using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #203: a per-entity-set (or global-default) <c>MaxRequestBodyBytes</c> limit rejects an oversized
/// write body with <c>413 Payload Too Large</c> before the handler deserializes it. The limit is
/// enforced by a group-level filter for body-bearing write methods (POST/PUT/PATCH).
/// <para>
/// #474 changed what "absent limit" means: <c>EntitySetDefaults.MaxRequestBodyBytes</c> now defaults
/// to <c>EntitySetDefaults.DefaultMaxRequestBodyBytes</c> (30,000,000 — Kestrel's own number), so an
/// unconfigured registration has a framework ceiling rather than none. Every body in this file is a
/// few hundred bytes, so nothing here moved; <c>WriteBodyContractTests</c> covers the new default and
/// <c>RequestBodySizeFeatureTests</c> covers the per-request Kestrel assignment, which TestHost
/// cannot reach (it supplies no <c>IHttpMaxRequestBodySizeFeature</c>).
/// </para>
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

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task Write_BodyOverLimit_413SaysItIsClosingTheConnection(string method)
    {
        // #601: the fast-reject answers before reading the body, so the connection cannot be
        // reused and Kestrel closes it. RFC 9110 §7.6.1 requires the sender to say so. Measured on
        // real Kestrel before the fix: 413 with no Connection header, and the client's NEXT request
        // on that socket got either no data or an aborted connection -- never retried for a POST.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<BodyLimitProfile>());
        string url = method == "POST" ? LimitedUrl : $"{LimitedUrl}(1)";
        using var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = Json(LargeBody()) };
        var resp = await fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.Contains("close", resp.Headers.Connection, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Write_BodyUnderLimit_DoesNotCloseTheConnection()
    {
        // The control: only the refusal closes. A normal write must keep keep-alive, or every
        // client would reconnect per request.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<BodyLimitProfile>());
        var resp = await fx.Client.PostAsync(LimitedUrl, Json("{\"name\":\"ok\"}"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.DoesNotContain("close", resp.Headers.Connection, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KestrelThrown413_IsMappedToTheEnvelope_AndAlsoClosesTheConnection()
    {
        // The OTHER 413 site: Kestrel throws BadHttpRequestException(413) when a body without a
        // usable Content-Length (chunked) exceeds the per-request MaxRequestBodySize, which happens
        // while the handler reads the body -- i.e. inside the group filter's try, which is what this
        // fixture reproduces. TestServer cannot produce the real one: Microsoft.AspNetCore.TestHost
        // supplies no IHttpMaxRequestBodySizeFeature at all (see RequestBodySizeFeatureTests), so
        // nothing enforces a limit mid-read.
        //
        // That clause had NO test before this one -- #203 mapped it and nothing exercised the
        // mapping. The body is equally unread here, so #601's Connection: close applies for the same
        // reason it does on the fast-reject.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BodyLimitChunkedProfile>());

        var resp = await fx.Client.GetAsync("/odata/BodyLimitChunkedWidgets");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("RequestEntityTooLarge", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("close", resp.Headers.Connection, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_NoLimitConfigured_LargeBodySucceeds()
    {
        // WidgetProfile sets no MaxRequestBodyBytes, so this ~400-byte body is bounded only by
        // #474's framework default (30,000,000) and is nowhere near it.
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

// Raises the exception Kestrel raises for an over-limit chunked body, from where Kestrel raises it:
// inside the request pipeline, under the group filter's try.
internal class BodyLimitChunkedProfile : EntitySetProfile<int, Widget>
{
    public BodyLimitChunkedProfile() : base(x => x.Id)
    {
        EntitySetName = "BodyLimitChunkedWidgets";
        GetAll = ct => throw new Microsoft.AspNetCore.Http.BadHttpRequestException(
            "Request body too large.", StatusCodes.Status413PayloadTooLarge);
    }
}

internal class BodyLimitProfile : EntitySetProfile<int, Widget>
{
    private readonly List<Widget> _store = new();

    public BodyLimitProfile() : base(x => x.Id)
    {
        EntitySetName = "BodyLimitWidgets";
        MaxRequestBodyBytes = 200;

        GetById = (id, ct) => OhDataResult.Success(_store.FirstOrDefault(w => w.Id == id));
        Post = (widget, ct) =>
        {
            widget.Id = _store.Count > 0 ? _store.Max(w => w.Id) + 1 : 1;
            _store.Add(widget);
            return OhDataResult.Success<Widget>(widget);
        };
        Put = (id, widget, ct) => { widget.Id = id; return OhDataResult.Success(widget); };
        Patch = (id, delta, ct) =>
        {
            var w = new Widget { Id = id };
            delta.Patch(w);
            return OhDataResult.Success<Widget?>(w);
        };
    }
}
