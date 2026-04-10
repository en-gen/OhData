using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

public class EndpointMappingTests
{
    // ── Collection GET ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_ReturnsExpectedItems()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets");
        Assert.Equal(JsonValueKind.Array, json.GetProperty("value").ValueKind);
        Assert.Equal(2, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task GetAll_ResponseWrappedInOdataEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets");
        Assert.True(json.TryGetProperty("@odata.context", out _));
        Assert.True(json.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task Count_Endpoint_ReturnsInteger()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets/$count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(long.TryParse(body, out _));
    }

    // ── Single GET ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_ExistingKey_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets(1)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetById_MissingKey_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets(999)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── POST ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_ValidBody_Returns201()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PostAsJsonAsync("/odata/Widgets", new Widget { Name = "Sprocket Jr." });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Post_ResponseBodyContainsNewEntity()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var widget = await fx.Client
            .PostAsJsonAsync("/odata/Widgets", new Widget { Name = "New" })
            .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<Widget>())
            .Unwrap();
        Assert.Equal("New", widget?.Name);
    }

    // ── PUT ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutById_ExistingKey_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/Widgets(1)", new Widget { Id = 1, Name = "Updated" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Routes omitted when handler not configured ─────────────────────────────

    [Fact]
    public async Task EmptyProfile_GetAll_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EmptyProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Service document ───────────────────────────────────────────────────────

    [Fact]
    public async Task ServiceDocument_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ServiceDocument_ListsEntitySet()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata");
        var values = json.GetProperty("value");
        Assert.Equal(JsonValueKind.Array, values.ValueKind);
        Assert.Equal("Widgets", values[0].GetProperty("name").GetString());
    }

    // ── $metadata ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Metadata_Returns200WithXml()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/$metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Metadata_ContainsEntitySetName()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var xml = await fx.Client.GetStringAsync("/odata/$metadata");
        Assert.Contains("Widgets", xml);
    }

    // ── Custom prefix ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CustomPrefix_RoutesResolveUnderPrefix()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<WidgetProfile>(),
            prefix: "/api/v1");
        var response = await fx.Client.GetAsync("/api/v1/Widgets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── DELETE ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingKey_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.DeleteAsync("/odata/Widgets(1)");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_MissingKey_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.DeleteAsync("/odata/Widgets(9999)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RouteOmitted_WhenHandlerNotConfigured()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EmptyProfile>());
        var response = await fx.Client.DeleteAsync("/odata/Widgets(1)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PATCH ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_ExistingKey_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Patch, "/odata/Widgets(1)")
        {
            Content = JsonContent.Create(new Widget { Name = "Changed" })
        };
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Patch_MissingKey_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Patch, "/odata/Widgets(999)")
        {
            Content = JsonContent.Create(new Widget { Name = "Changed" })
        };
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_UpdatesField()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Patch, "/odata/Widgets(1)")
        {
            Content = JsonContent.Create(new Widget { Name = "Changed" })
        };
        var response = await fx.Client.SendAsync(request);
        var widget = await response.Content.ReadFromJsonAsync<Widget>();
        Assert.Equal("Changed", widget?.Name);
    }

    [Fact]
    public async Task Patch_RouteOmitted_WhenHandlerNotConfigured()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EmptyProfile>());
        var request = new HttpRequestMessage(HttpMethod.Patch, "/odata/Widgets(1)")
        {
            Content = JsonContent.Create(new Widget { Name = "Changed" })
        };
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Authorization ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Auth_UnauthenticatedRequest_Returns401()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<AuthorizedWidgetProfile>(),
            addAuth: true);
        var response = await fx.Client.GetAsync("/odata/AuthorizedWidgets");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_UnauthenticatedGetById_Returns401()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<AuthorizedWidgetProfile>(),
            addAuth: true);
        var response = await fx.Client.GetAsync("/odata/AuthorizedWidgets(1)");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Error format ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ErrorResponse_HasOdataErrorShape()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets(notanint)");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.True(error.TryGetProperty("code", out _));
        Assert.True(error.TryGetProperty("message", out _));
    }
}
