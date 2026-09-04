using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #580 — <c>/$count</c> negotiates nothing.
/// <para>
/// §11.2.9 says the body "MUST … [be] a simple scalar integer value with media type
/// <c>text/plain</c>" and that "Content negotiation using the Accept request header or the
/// <c>$format</c> system query option is not allowed with the path segment <c>/$count</c>". A
/// segment that may not be negotiated has no business refusing a client that tried.
/// </para>
/// <para>
/// This reverses an earlier ruling that <c>Accept: application/xml</c> should keep 406-ing here.
/// <c>Microsoft.AspNetCore.OData</c> settles it the other way: <c>ODataCountMediaTypeMapping</c>
/// matches every <c>/$count</c> path at quality 1 and <c>ODataOutputFormatter</c> then overrides the
/// content type, so MS never negotiates and never 406s on this segment.
/// </para>
/// </summary>
public sealed class Issue580CountNegotiationTests
{
    private static async Task AssertPlainCountAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.True(int.TryParse((await response.Content.ReadAsStringAsync()).Trim(), out _),
            "the /$count body must be a bare scalar integer");
    }

    [Theory]
    [InlineData("application/json")]   // the common blanket header — 200 before and after
    [InlineData("text/plain")]         // what Microsoft.OData.Client actually sends
    [InlineData("*/*")]
    [InlineData("application/xml")]    // was 406; §11.2.9 says this segment does not negotiate
    [InlineData("application/octet-stream")]
    [InlineData("application/json;q=0")]
    public async Task EveryAcceptHeader_GetsThePlainTextCount(string accept)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/odata/Widgets/$count");
        request.Headers.TryAddWithoutValidation("Accept", accept);

        await AssertPlainCountAsync(await fx.Client.SendAsync(request));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("application/json")]
    [InlineData("xml")]                // was 400 UnsupportedFormat
    [InlineData("application/xml")]
    public async Task EveryFormatValue_GetsThePlainTextCount(string format)
    {
        // §11.2.9 names $format alongside Accept, so the same rule governs both. Refusing one while
        // ignoring the other would give a single disallowed act two different answers.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        await AssertPlainCountAsync(
            await fx.Client.GetAsync($"/odata/Widgets/$count?$format={Uri.EscapeDataString(format)}"));
    }

    [Fact]
    public async Task TheNavigationCountSegment_BehavesTheSameWay()
    {
        // Both /$count routes end with the same segment and the same clause governs them.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<NavCountProfile>());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/odata/NavCountParents(1)/Children/$count");
        request.Headers.TryAddWithoutValidation("Accept", "application/xml");

        await AssertPlainCountAsync(await fx.Client.SendAsync(request));
    }

    [Fact]
    public async Task OtherRoutesStill406_OnAnUnacceptableType()
    {
        // The control: this change is scoped to the segment §11.2.9 exempts. Everything else still
        // negotiates, so a client that refuses JSON on an ordinary read is still told so.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/odata/Widgets");
        request.Headers.TryAddWithoutValidation("Accept", "application/xml");

        Assert.Equal(HttpStatusCode.NotAcceptable, (await fx.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task AnUnsupportedFormatIsStillRefused_OnOtherRoutes()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        var response = await fx.Client.GetAsync("/odata/Widgets?$format=xml");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
