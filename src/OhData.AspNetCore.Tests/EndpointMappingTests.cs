using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

public class EndpointMappingTests
{
    // â”€â”€ Collection GET â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ Single GET â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ POST â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ PUT â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task PutById_ExistingKey_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/Widgets(1)", new Widget { Id = 1, Name = "Updated" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // â”€â”€ Routes omitted when handler not configured â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task EmptyProfile_GetAll_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EmptyProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // â”€â”€ Service document â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ $metadata â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ Custom prefix â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task CustomPrefix_RoutesResolveUnderPrefix()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<WidgetProfile>(),
            prefix: "/api/v1");
        var response = await fx.Client.GetAsync("/api/v1/Widgets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // â”€â”€ DELETE â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ PATCH â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ $select response shaping â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Select_SingleProperty_OnlySelectedPropertyPresent()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$select=Name");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("Name", out _));
        Assert.False(firstItem.TryGetProperty("Id", out _));
    }

    [Fact]
    public async Task Select_OtherProperty_OnlySelectedPropertyPresent()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$select=Id");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("Id", out _));
        Assert.False(firstItem.TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task Select_AllProperties_ReturnsAllProperties()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$select=Id,Name");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("Id", out _));
        Assert.True(firstItem.TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task Select_NoSelectParam_ReturnsAllProperties()
    {
        // Without $select, Widget objects are serialized directly by ASP.NET Core JSON
        // (camelCase by default), so check for camelCase property names.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("id", out _) || firstItem.TryGetProperty("Id", out _));
        Assert.True(firstItem.TryGetProperty("name", out _) || firstItem.TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task Select_CorrectValuesPreserved()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$select=Name");
        var values = json.GetProperty("value");
        var names = Enumerable.Range(0, values.GetArrayLength())
            .Select(i => values[i].GetProperty("Name").GetString())
            .ToArray();
        Assert.Contains("Sprocket", names);
        Assert.Contains("Cog", names);
    }

    // $select on the IEnumerable (GetAll) path â€” ExpandoObject post-materialization
    [Fact]
    public async Task Select_GetAllPath_SingleProperty_OnlySelectedPropertyPresent()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets?$select=Name");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("Name", out _));
        Assert.False(firstItem.TryGetProperty("Id", out _));
    }

    [Fact]
    public async Task Select_GetAllPath_CaseInsensitivePropertyName()
    {
        // $select=name (lowercase) should match the C# property "Name"
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets?$select=name");
        var firstItem = json.GetProperty("value")[0];
        // ExpandoObject preserves the C# property name casing in the key
        Assert.True(firstItem.TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task Select_GetAllPath_UnknownProperty_ThrowsODataException()
    {
        // OData validates $select property names against the EDM model at parse time.
        // Requesting a non-existent property throws ODataException.
        // This is a known limitation: the GetAll path uses ODataQueryOptions which validates
        // the model, so unknown property names are rejected rather than silently dropped.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        // The unhandled ODataException propagates through the test host
        await Assert.ThrowsAsync<Microsoft.OData.ODataException>(
            () => fx.Client.GetAsync("/odata/Widgets?$select=Name,DoesNotExist"));
    }

    [Fact]
    public async Task Select_ExpandoSerializesAsObjectNotArray()
    {
        // Result items should serialize as JSON objects (not arrays or strings)
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets?$select=Name");
        var firstItem = json.GetProperty("value")[0];
        Assert.Equal(JsonValueKind.Object, firstItem.ValueKind);
    }

    [Fact]
    public async Task Select_GetAll_PropertyCasingMatchesPascalCase()
    {
        // JsonSerializer default uses PascalCase for C# property names.
        // The resulting JSON key is always PascalCase, not camelCase.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets?$select=Name");
        var first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("Name", out _));   // PascalCase
        Assert.False(first.TryGetProperty("name", out _));  // NOT camelCase
    }
    // â”€â”€ EF Core InMemory + ISelectExpandWrapper â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Select_EfCoreInMemory_SingleProperty_OnlySelectedPropertyPresent()
    {
        // Verifies the ISelectExpandWrapper approach works with EF Core InMemory provider.
        // ISelectExpandWrapper.ToDictionary() uses PascalCase keys matching the EDM property names.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EfCoreWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/odata/EfWidgets?$select=Name");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("Name", out _));
        Assert.False(firstItem.TryGetProperty("Id", out _));
    }

    [Fact]
    public async Task Select_EfCoreInMemory_CorrectValues()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EfCoreWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/odata/EfWidgets?$select=Name");
        var values = json.GetProperty("value");
        var names = System.Linq.Enumerable.Range(0, values.GetArrayLength())
            .Select(i => values[i].GetProperty("Name").GetString())
            .ToArray();
        Assert.Contains("Sprocket", names);
        Assert.Contains("Cog", names);
    }



    // â”€â”€ Authorization â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ Error format â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // -- Navigation routing -------------------------------------------------------
    [Fact]
    public async Task NavigationRoute_HasMany_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ParentWithChildrenProfile>());
        var response = await fx.Client.GetAsync("/odata/Parents(1)/Children");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    [Fact]
    public async Task NavigationRoute_ParentNotFound_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ParentWithChildrenProfile>());
        var response = await fx.Client.GetAsync("/odata/Parents(999)/Children");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
    // -- ETags --------------------------------------------------------------------
    [Fact]
    public async Task GetById_WithETag_ReturnsETagHeader()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/ETagWidgets(1)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
    }
    [Fact]
    public async Task Put_WithCorrectIfMatch_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagWidgetProfile>());
        var getResp = await fx.Client.GetAsync("/odata/ETagWidgets(1)");
        var etag = getResp.Headers.ETag!.Tag;
        var request = new HttpRequestMessage(HttpMethod.Put, "/odata/ETagWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "Updated" }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    [Fact]
    public async Task Put_WithWrongIfMatch_Returns412()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagWidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Put, "/odata/ETagWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "Updated" }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"wrong-etag\"");
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }
    // -- Named registrations ------------------------------------------------------
    [Fact]
    public async Task MultipleRegistrations_BothMapIndependently()
    {
        // Each named registration uses its own prefix and profile
        await using var fx1 = await TestHostBuilder.BuildAsync(o => o.WithPrefix("/v1").AddProfile<WidgetProfile>(), prefix: "/v1");
        await using var fx2 = await TestHostBuilder.BuildAsync(o => o.WithPrefix("/v2").AddProfile<EmptyProfile>(), prefix: "/v2");
        var r1 = await fx1.Client.GetAsync("/v1/Widgets");
        var r2 = await fx2.Client.GetAsync("/v2");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

}
