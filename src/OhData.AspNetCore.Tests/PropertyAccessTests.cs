using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Tests for individual structural property read routes (I-6, OData §11.2.6 / Part 2 §4.6-4.7):
/// <c>GET /{Set}({key})/{Property}</c> and <c>GET /{Set}({key})/{Property}/$value</c>.
/// Write support (PUT/PATCH/DELETE on a property) is out of scope for this PR.
/// </summary>
public class PropertyAccessTests
{
    // ── Happy path: envelope shape + primitive types ───────────────────────────

    [Fact]
    public async Task GetProperty_String_Returns200WithEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var response = await fx.Client.GetAsync("/odata/PropertyAccessItems(1)/Name");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var context));
        Assert.EndsWith("$metadata#PropertyAccessItems(1)/Name", context.GetString());
        Assert.True(json.TryGetProperty("value", out var value));
        Assert.Equal("Widget", value.GetString());
    }

    [Fact]
    public async Task GetProperty_Decimal_ReturnsCorrectValue()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyAccessItems(1)/Price");
        Assert.Equal(9.99m, json.GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task GetProperty_DateTime_ReturnsCorrectValue()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyAccessItems(1)/ReleasedAt");
        var expected = PropertyAccessProfile.Store[0].ReleasedAt;
        Assert.Equal(expected, json.GetProperty("value").GetDateTime());
    }

    [Fact]
    public async Task GetProperty_Complex_ReturnsNestedObjectRespectingNamingPolicy()
    {
        // The default ASP.NET Core JsonOptions naming policy is camelCase — verifying the nested
        // complex value's keys are lowercased proves the envelope goes through the standard
        // Results.Ok/JsonOptions pipeline rather than a hand-rolled serializer.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/PropertyAccessItems(1)/Size");
        var sizeValue = json.GetProperty("value");
        Assert.True(sizeValue.TryGetProperty("width", out var width));
        Assert.Equal(10.5m, width.GetDecimal());
        Assert.True(sizeValue.TryGetProperty("height", out _));
    }

    // ── Null property → 204 ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetProperty_NullValue_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var response = await fx.Client.GetAsync("/odata/PropertyAccessItems(2)/Description");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── Unknown property / missing entity ───────────────────────────────────────

    [Fact]
    public async Task GetProperty_UnknownPropertyName_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var response = await fx.Client.GetAsync("/odata/PropertyAccessItems(1)/NotAProperty");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetProperty_EntityMissing_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var response = await fx.Client.GetAsync("/odata/PropertyAccessItems(999)/Name");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── /$value ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPropertyValue_String_ReturnsRawTextPlain()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var response = await fx.Client.GetAsync("/odata/PropertyAccessItems(1)/Name/$value");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Widget", body);
    }

    [Fact]
    public async Task GetPropertyValue_Decimal_ReturnsInvariantCultureRaw()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var response = await fx.Client.GetAsync("/odata/PropertyAccessItems(1)/Price/$value");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal("9.99", body);
    }

    [Fact]
    public async Task GetPropertyValue_NullProperty_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var response = await fx.Client.GetAsync("/odata/PropertyAccessItems(2)/Description/$value");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPropertyValue_ComplexProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var response = await fx.Client.GetAsync("/odata/PropertyAccessItems(1)/Size/$value");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetPropertyValue_EntityMissing_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessProfile>());
        var response = await fx.Client.GetAsync("/odata/PropertyAccessItems(999)/Name/$value");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PropertyAccessEnabled = false → routes absent ───────────────────────────

    [Fact]
    public async Task PropertyAccessDisabled_RouteAbsent_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<PropertyAccessDisabledProfile>());
        var response = await fx.Client.GetAsync("/odata/PropertyAccessDisabledItems(1)/Name");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Profile without GetById → routes absent ─────────────────────────────────

    [Fact]
    public async Task ProfileWithoutGetById_RouteAbsent_Returns404()
    {
        // EmptyProfile configures no handlers at all, so GetById is absent and no property
        // routes should be registered regardless of PropertyAccessEnabled's resolved value.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EmptyProfile>());
        var response = await fx.Client.GetAsync("/odata/EmptyWidgets(1)/Name");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Navigation-name precedence ───────────────────────────────────────────────

    [Fact]
    public async Task NavigationRoute_StillServesNavigationContent_NotPropertyEnvelope()
    {
        // ParentWithChildrenProfile registers a nav route for "Children" (a collection nav).
        // Structural properties are computed as CLR properties minus navigation names, so
        // "Children" is never a candidate for a property route — the existing nav route keeps
        // serving its collection envelope unchanged.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ParentWithChildrenProfile>());
        var navResponse = await fx.Client.GetAsync("/odata/Parents(1)/Children");
        Assert.Equal(HttpStatusCode.OK, navResponse.StatusCode);
        var navJson = await navResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, navJson.GetProperty("value").ValueKind);

        // The structural "Name" property on the same entity set still gets its own property route.
        var propResponse = await fx.Client.GetAsync("/odata/Parents(1)/Name");
        Assert.Equal(HttpStatusCode.OK, propResponse.StatusCode);
        var propJson = await propResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Parent1", propJson.GetProperty("value").GetString());
    }

    // ── Startup collision validation ─────────────────────────────────────────────

    [Fact]
    public void PropertyCollision_WithBoundFunction_ThrowsAtMapOhData()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddOhData(o => o.AddProfile<PropertyCollisionProfile>());
        var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapOhData());
        Assert.Contains("Name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PropertyCollisionWidgets", ex.Message, StringComparison.Ordinal);
    }

    // ── ETag interaction ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProperty_WithETag_SetsETagHeaderAndHonorsIfNoneMatch()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagWidgetProfile>());
        var first = await fx.Client.GetAsync("/odata/ETagWidgets(1)/Name");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);

        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/ETagWidgets(1)/Name");
        request.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.Tag);
        var second = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    // ── Authorization ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProperty_Unauthenticated_Returns401()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<AuthorizedWidgetProfile>(),
            addAuth: true);
        var response = await fx.Client.GetAsync("/odata/AuthorizedWidgets(1)/Name");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
