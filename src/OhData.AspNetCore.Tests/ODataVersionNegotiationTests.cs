using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// §8.2.7: a service MUST honor the OData-MaxVersion request header or reject the request.
/// OhData emits 4.0 payloads, so any ceiling of 4.0 or higher (including future minor/major
/// versions) can be honored; anything below 4.0, or an unparseable value, cannot and is
/// rejected with 400 and the standard OData error envelope.
/// </summary>
public class ODataVersionNegotiationTests
{
    private static HttpRequestMessage Request(HttpMethod method, string url, string? maxVersion = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (maxVersion is not null)
        {
            request.Headers.Add("OData-MaxVersion", maxVersion);
        }

        return request;
    }

    // ── GET collection ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_NoMaxVersionHeader_Proceeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/Widgets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_MaxVersion40_Proceeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata/Widgets", "4.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_MaxVersion401_Proceeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata/Widgets", "4.01"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_MaxVersion50_Proceeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata/Widgets", "5.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_MaxVersion30_Returns400WithErrorEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata/Widgets", "3.0"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("UnsupportedODataVersion", body);
        Assert.Contains("\"error\"", body);
    }

    [Fact]
    public async Task Get_MaxVersionGarbage_Returns400WithErrorEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata/Widgets", "abc"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("UnsupportedODataVersion", body);
        Assert.Contains("\"error\"", body);
    }

    [Fact]
    public async Task Get_MaxVersionWithSurroundingWhitespace_Proceeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata/Widgets", "  4.0  "));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_MaxVersion20_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata/Widgets", "2.0"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Response_DoesNotEchoODataMaxVersionHeader_EvenWhenAccepted()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata/Widgets", "4.0"));
        Assert.False(response.Headers.Contains("OData-MaxVersion"));
    }

    // ── Writes (POST) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_MaxVersion40_Proceeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var request = Request(HttpMethod.Post, "/odata/Widgets", "4.0");
        request.Content = JsonContent.Create(new Widget { Name = "New Widget" });
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Post_MaxVersion30_Returns400_BodyNotProcessed()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var request = Request(HttpMethod.Post, "/odata/Widgets", "3.0");
        request.Content = JsonContent.Create(new Widget { Name = "Should Not Be Created" });
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("UnsupportedODataVersion", body);
    }

    [Fact]
    public async Task Put_MaxVersion30_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var request = Request(HttpMethod.Put, "/odata/Widgets(1)", "3.0");
        request.Content = JsonContent.Create(new Widget { Id = 1, Name = "Updated" });
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Service document & $metadata ────────────────────────────────────────────

    [Fact]
    public async Task ServiceDocument_MaxVersion40_Proceeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata", "4.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ServiceDocument_MaxVersion30_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata", "3.0"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Metadata_MaxVersion40_Proceeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata/$metadata", "4.0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Metadata_MaxVersion30_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata/$metadata", "3.0"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Metadata_MaxVersionGarbage_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        HttpResponseMessage response = await fx.Client.SendAsync(Request(HttpMethod.Get, "/odata/$metadata", "not-a-version"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
