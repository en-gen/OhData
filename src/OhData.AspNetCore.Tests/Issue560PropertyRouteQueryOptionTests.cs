using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #560 — <c>GET /{Set}({key})/{Prop}</c> and its <c>/$value</c> refuse every system query option
/// they do not implement, which is all of them but <c>$format</c>.
/// <para>
/// Measured before the fix, on the same fixture: every option below answered <c>200</c> on both
/// routes while the sibling <c>GET /{Set}({key})</c> answered <c>501</c> — so the same nonsense
/// option got two different answers over one resource, which is the inconsistency #359/#380/#353
/// exist to remove.
/// </para>
/// </summary>
public sealed class Issue560PropertyRouteQueryOptionTests
{
    // Everything the sibling entity route refuses, plus $select/$expand, which it IMPLEMENTS and
    // these routes do not: the handler goes straight from the property accessor to the envelope.
    [Theory]
    [InlineData("$select=Name")]
    [InlineData("$expand=Nope")]
    [InlineData("$filter=Name eq 'nope'")]
    [InlineData("$orderby=Name")]
    [InlineData("$top=1")]
    [InlineData("$skip=1")]
    [InlineData("$count=true")]
    [InlineData("$apply=groupby((Name))")]
    [InlineData("$skiptoken=abc")]
    [InlineData("$unknown=1")]
    [InlineData("$Filter=Name eq 'nope'")]   // OrdinalIgnoreCase, like every other gated route
    public async Task PropertyRead_UnimplementedOption_Is501WithTheSharedEnvelope(string option)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        foreach (string url in new[]
                 {
                     $"/odata/Widgets(1)/Name?{option}",
                     $"/odata/Widgets(1)/Name/$value?{option}",
                 })
        {
            HttpResponseMessage response = await fx.Client.GetAsync(url);

            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
            JsonElement error = body.GetProperty("error");
            Assert.Equal("UnsupportedQueryOption", error.GetProperty("code").GetString());
            // The generic wording shared with every other gated route, naming the option as sent.
            string sent = option.Split('=')[0];
            Assert.Equal($"The query option '{sent}' is not supported.", error.GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task PropertyRead_TheSameOptionNowAnswersTheSameOnBothRoutes()
    {
        // #560's own statement of the defect: GET /Movies(1)?$filter=... was 501 while
        // GET /Movies(1)/Title?$filter=... was 200 with the filter silently dropped.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        HttpResponseMessage entity = await fx.Client.GetAsync("/odata/Widgets(1)?$filter=Name eq 'nope'");
        HttpResponseMessage property = await fx.Client.GetAsync("/odata/Widgets(1)/Name?$filter=Name eq 'nope'");

        Assert.Equal(HttpStatusCode.NotImplemented, entity.StatusCode);
        Assert.Equal(entity.StatusCode, property.StatusCode);
    }

    [Fact]
    public async Task PropertyRead_FormatIsAccepted_AndACustomOptionIsUntouched()
    {
        // $format is negotiated once on the group filter and never reaches a handler, so it is in
        // every route's implemented set. A non-'$' key is a CUSTOM query option (Part 2 §5.2) and
        // is not the gate's business.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        foreach (string url in new[]
                 {
                     "/odata/Widgets(1)/Name?$format=json",
                     "/odata/Widgets(1)/Name?ohdata-custom=1",
                     "/odata/Widgets(1)/Name/$value?$format=json",
                     "/odata/Widgets(1)/Name/$value?ohdata-custom=1",
                 })
        {
            Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task PropertyRead_WithNoQueryString_IsUnchanged()
    {
        // The gate short-circuits on an empty query string, so the zero-cost path these routes are
        // built around is preserved. Bytes captured pre-fix.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

        HttpResponseMessage prop = await fx.Client.GetAsync("/odata/Widgets(1)/Name");
        HttpResponseMessage value = await fx.Client.GetAsync("/odata/Widgets(1)/Name/$value");

        Assert.Equal(HttpStatusCode.OK, prop.StatusCode);
        Assert.Equal(
            "{\"@odata.context\":\"http://localhost/odata/$metadata#Widgets(1)/Name\",\"value\":\"Sprocket\"}",
            await prop.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, value.StatusCode);
        Assert.Equal("Sprocket", await value.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PropertyValue_OnAComplexProperty_RefusesTheOptionBeforeTheComplexCheck()
    {
        // Both are errors; the gate runs first so the answer does not depend on WHICH property was
        // addressed. Without an option the complex 400 is unchanged (the control below).
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PropertyAccessProfile>());

        HttpResponseMessage withOption =
            await fx.Client.GetAsync("/odata/PropertyAccessItems(1)/Size/$value?$unknown=1");
        HttpResponseMessage control =
            await fx.Client.GetAsync("/odata/PropertyAccessItems(1)/Size/$value");

        Assert.Equal(HttpStatusCode.NotImplemented, withOption.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, control.StatusCode);
    }
}
