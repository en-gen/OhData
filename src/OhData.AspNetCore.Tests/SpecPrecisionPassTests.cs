using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Regression tests for the spec-precision audit pass: context URLs (M1/M2/M3), the sole
/// remaining bare-404 error envelope (M4), conditional-request edge cases (m6/m7), nav-route
/// $top/$skip validation (m8), and the bound-operation individual-value envelope (m5, covered
/// primarily via updated tests in EndpointMappingTests.cs / CoverageGapTests.cs).
/// </summary>
public class SpecPrecisionPassTests
{
    // ── M1: single-valued navigation GET must carry @odata.context ────────────────

    [Fact]
    public async Task SingleValuedNav_Get_IncludesODataContext()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavRefSingleProfile>());
        var resp = await fx.Client.GetAsync("/odata/NavRefSingleParents(1)/PrimaryChild");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var context));
        Assert.Contains("$metadata#NavRefSingleParents(1)/PrimaryChild/$entity", context.GetString());
        Assert.Equal("OnlyChild", json.GetProperty("name").GetString());
    }

    // ── M4: nav-collection /$count on a missing parent uses the OData error envelope ──

    [Fact]
    public async Task NavCollectionCount_MissingParent_ReturnsODataErrorEnvelope()
    {
        // ParentWithChildrenProfile's HasMany handler returns null (not an empty collection)
        // when the parent key doesn't exist, exercising the null-result branch of the
        // standalone nav /$count route.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ParentWithChildrenProfile>());
        var resp = await fx.Client.GetAsync("/odata/Parents(999)/Children/$count");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out var error), "Expected the OData error envelope, not a bare 404.");
        Assert.Equal("NotFound", error.GetProperty("code").GetString());
        Assert.True(error.TryGetProperty("message", out _));
    }

    // ── M2: $ref context URL uses #$ref / #Collection($ref), not a path shape ─────

    [Fact]
    public async Task RefCollection_Get_ContextIsCollectionRef()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavQueryProfile>());
        var resp = await fx.Client.GetAsync("/odata/NavQueryParents(1)/Children/$ref");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var context));
        Assert.EndsWith("$metadata#Collection($ref)", context.GetString());
    }

    [Fact]
    public async Task RefSingleValued_Get_ContextIsRef()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavRefSingleProfile>());
        var resp = await fx.Client.GetAsync("/odata/NavRefSingleParents(1)/PrimaryChild/$ref");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var context));
        Assert.EndsWith("$metadata#$ref", context.GetString());
    }

    // ── M3: $select narrows the context URL to the projected form ─────────────────

    [Fact]
    public async Task CollectionGet_WithSelect_ContextIncludesProjection()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.GetAsync("/odata/Widgets?$select=Id,Name");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var context));
        Assert.EndsWith("$metadata#Widgets(Id,Name)", context.GetString());
    }

    [Fact]
    public async Task CollectionGet_WithSelect_ContextPreservesRequestOrder()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.GetAsync("/odata/Widgets?$select=Name,Id");
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var context));
        Assert.EndsWith("$metadata#Widgets(Name,Id)", context.GetString());
    }

    [Fact]
    public async Task CollectionGet_NoSelect_ContextHasNoProjectionSuffix()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.GetAsync("/odata/Widgets");
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var context));
        Assert.EndsWith("$metadata#Widgets", context.GetString());
    }

    [Fact]
    public async Task GetById_WithSelect_ContextIncludesProjectionAndBodyIsFiltered()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.GetAsync("/odata/Widgets(1)?$select=Name");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var context));
        Assert.EndsWith("$metadata#Widgets(Name)/$entity", context.GetString());
        Assert.True(json.TryGetProperty("name", out _));
        Assert.False(json.TryGetProperty("id", out _), "Unselected 'id' should be stripped from the body.");
    }

    [Fact]
    public async Task GetById_NoSelect_ContextHasNoProjectionSuffix()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        var resp = await fx.Client.GetAsync("/odata/Widgets(1)");
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var context));
        Assert.EndsWith("$metadata#Widgets/$entity", context.GetString());
        Assert.True(json.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task NavCollection_WithSelect_ContextIncludesProjection()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());
        var resp = await fx.Client.GetAsync("/odata/NavCountParents(1)/Children?$select=Name");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var context));
        Assert.EndsWith("$metadata#NavCountParents(1)/Children(Name)", context.GetString());
    }

    // ── m6: If-Match (incl. "*") on a missing resource -> 412, not 404 ─────────────

    [Fact]
    public async Task IfMatch_SpecificEtag_MissingEntity_Put_Returns412()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ETagWidgetProfile>());
        using var req = new HttpRequestMessage(HttpMethod.Put, "/odata/ETagWidgets(999)")
        {
            Content = JsonContent.Create(new Widget { Id = 999, Name = "Nope" })
        };
        req.Headers.TryAddWithoutValidation("If-Match", "\"some-etag\"");
        using var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
    }

    [Fact]
    public async Task IfMatch_Wildcard_MissingEntity_Patch_Returns412()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ETagWidgetProfile>());
        using var req = new HttpRequestMessage(HttpMethod.Patch, "/odata/ETagWidgets(999)")
        {
            Content = JsonContent.Create(new { Name = "Nope" })
        };
        req.Headers.TryAddWithoutValidation("If-Match", "*");
        using var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
    }

    [Fact]
    public async Task IfMatch_SpecificEtag_MissingEntity_Patch_Returns412()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ETagWidgetProfile>());
        using var req = new HttpRequestMessage(HttpMethod.Patch, "/odata/ETagWidgets(999)")
        {
            Content = JsonContent.Create(new { Name = "Nope" })
        };
        req.Headers.TryAddWithoutValidation("If-Match", "\"some-etag\"");
        using var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
    }

    [Fact]
    public async Task IfMatch_Wildcard_MissingEntity_Delete_Returns412()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ETagWidgetProfile>());
        using var req = new HttpRequestMessage(HttpMethod.Delete, "/odata/ETagWidgets(999)");
        req.Headers.TryAddWithoutValidation("If-Match", "*");
        using var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
    }

    [Fact]
    public async Task IfMatch_SpecificEtag_MissingEntity_Delete_Returns412()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ETagWidgetProfile>());
        using var req = new HttpRequestMessage(HttpMethod.Delete, "/odata/ETagWidgets(999)");
        req.Headers.TryAddWithoutValidation("If-Match", "\"some-etag\"");
        using var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
    }

    // ── m7: If-None-Match: * on PUT is a create-guard when AllowUpsert is on ──────

    [Fact]
    public async Task IfNoneMatchWildcard_Put_ExistingEntity_Returns412()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<UpsertProfile>());
        using var req = new HttpRequestMessage(HttpMethod.Put, "/odata/UpsertWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "ShouldNotApply" })
        };
        req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
    }

    [Fact]
    public async Task IfNoneMatchWildcard_Put_NewEntity_ProceedsAsInsert()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<UpsertProfile>());
        using var req = new HttpRequestMessage(HttpMethod.Put, "/odata/UpsertWidgets(42)")
        {
            Content = JsonContent.Create(new Widget { Id = 42, Name = "Created" })
        };
        req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Created", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task IfNoneMatchAbsent_Put_ExistingEntity_StillSucceeds()
    {
        // No-op check: If-None-Match absent must not change existing PUT/upsert behavior.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<UpsertProfile>());
        var resp = await fx.Client.PutAsJsonAsync("/odata/UpsertWidgets(1)", new Widget { Id = 1, Name = "Updated" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── m8: nav-route $top/$skip with invalid values -> 400, not a silent full set ─

    [Fact]
    public async Task NavCollection_InvalidSkip_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());
        var resp = await fx.Client.GetAsync("/odata/NavCountParents(1)/Children?$skip=abc");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task NavCollection_NegativeSkip_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());
        var resp = await fx.Client.GetAsync("/odata/NavCountParents(1)/Children?$skip=-1");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task NavCollection_InvalidTop_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());
        var resp = await fx.Client.GetAsync("/odata/NavCountParents(1)/Children?$top=abc");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task NavCollection_NegativeTop_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());
        var resp = await fx.Client.GetAsync("/odata/NavCountParents(1)/Children?$top=-1");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task NavCollection_ValidTopAndSkip_StillWork()
    {
        // Regression guard: valid numeric $top/$skip must keep working after the m8 fix.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());
        var resp = await fx.Client.GetAsync("/odata/NavCountParents(1)/Children?$top=1&$skip=1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("value").GetArrayLength());
    }
}
