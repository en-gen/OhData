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
    // â"€â"€ Collection GET â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

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

    [Fact]
    public async Task Count_Queryable_WithFilter_ReturnsFilteredCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/QueryableWidgets/$count?$filter=Name eq 'Sprocket'");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(long.TryParse(body, out var count));
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task Count_Queryable_NoFilter_ReturnsTotalCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/QueryableWidgets/$count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(long.TryParse(body, out var count));
        Assert.Equal(2L, count);
    }

    [Fact]
    public async Task GetAll_WithCountTrue_ReturnsOdataCountInEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$count=true");
        Assert.True(json.TryGetProperty("@odata.count", out var countEl));
        Assert.Equal(2L, countEl.GetInt64());
    }

    [Fact]
    public async Task GetAll_WithCountTrueAndFilter_OdataCountReflectsFilteredTotal()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/QueryableWidgets?$count=true&$filter=Name eq 'Cog'");
        Assert.True(json.TryGetProperty("@odata.count", out var countEl));
        Assert.Equal(1L, countEl.GetInt64());
        Assert.Equal(1, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task GetAll_InvalidFilterProperty_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/QueryableWidgets?$filter=DoesNotExist eq 'x'");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out var err));
        Assert.Equal("InvalidQueryOption", err.GetProperty("code").GetString());
    }

    // â"€â"€ Single GET â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

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

    // â"€â"€ POST â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

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

    // â"€â"€ PUT â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [Fact]
    public async Task PutById_ExistingKey_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/Widgets(1)", new Widget { Id = 1, Name = "Updated" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // â"€â"€ Routes omitted when handler not configured â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [Fact]
    public async Task EmptyProfile_GetAll_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EmptyProfile>());
        var response = await fx.Client.GetAsync("/odata/EmptyWidgets");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // â"€â"€ Service document â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

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

    // â"€â"€ $metadata â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

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

    // â"€â"€ Custom prefix â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [Fact]
    public async Task CustomPrefix_RoutesResolveUnderPrefix()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<WidgetProfile>(),
            prefix: "/api/v1");
        var response = await fx.Client.GetAsync("/api/v1/Widgets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // â"€â"€ DELETE â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [Fact]
    public async Task Delete_ExistingKey_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.DeleteAsync("/odata/Widgets(1)");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_MissingKey_Idempotent_Returns204()
    {
        // Default: IdempotentDelete = true — missing key returns 204 (no-op success)
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.DeleteAsync("/odata/Widgets(9999)");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RouteOmitted_WhenHandlerNotConfigured()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EmptyProfile>());
        var response = await fx.Client.DeleteAsync("/odata/EmptyWidgets(1)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // â"€â"€ PATCH â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [Fact]
    public async Task Patch_ExistingKey_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/Widgets(1)",
            new StringContent("{\"name\":\"Changed\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Patch_MissingKey_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/Widgets(999)",
            new StringContent("{\"name\":\"Changed\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_UpdatesField()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/Widgets(1)",
            new StringContent("{\"name\":\"Changed\"}", System.Text.Encoding.UTF8, "application/json"));
        var widget = await response.Content.ReadFromJsonAsync<Widget>();
        Assert.Equal("Changed", widget?.Name);
    }

    [Fact]
    public async Task Patch_RouteOmitted_WhenHandlerNotConfigured()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EmptyProfile>());
        var request = new HttpRequestMessage(HttpMethod.Patch, "/odata/EmptyWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Name = "Changed" })
        };
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // â"€â"€ $select response shaping â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

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

    // $select on the IEnumerable (GetAll) path â€" ExpandoObject post-materialization
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
    public async Task Select_GetAllPath_LowercaseInput_NormalizedToEdmName()
    {
        // The OData parser normalizes $select=name to EDM identifier "Name" — behaves like $select=Name.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets?$select=name");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task Select_GetAllPath_UnknownProperty_Returns400()
    {
        // OData validates $select property names against the EDM model at parse time.
        // Requesting a non-existent property should return 400 with an OData error body.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets?$select=Name,DoesNotExist");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out var err));
        Assert.Equal("InvalidQueryOption", err.GetProperty("code").GetString());
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
    // â"€â"€ EF Core InMemory + ISelectExpandWrapper â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

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



    // â"€â"€ Authorization â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

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

    // -- C1: Auth consistency -- policy + roles applied to all route types ------

    [Fact]
    public async Task Auth_PolicyAndRoles_CollectionGet_Returns401()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<PolicyAndRolesWidgetProfile>(),
            addAuth: true);
        var response = await fx.Client.GetAsync("/odata/PolicyRoleWidgets");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_PolicyAndRoles_Post_Returns401()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<PolicyAndRolesWidgetProfile>(),
            addAuth: true);
        var response = await fx.Client.PostAsJsonAsync("/odata/PolicyRoleWidgets", new Widget { Name = "X" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_PolicyAndRoles_GetById_Returns401()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<PolicyAndRolesWidgetProfile>(),
            addAuth: true);
        var response = await fx.Client.GetAsync("/odata/PolicyRoleWidgets(1)");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // â"€â"€ Error format â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

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

    // ── Bound functions & actions ──────────────────────────────────────────────

    [Fact]
    public async Task BoundFunction_WithStringParam_ReturnsFilteredResult()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<BoundOpsProfile>());
        var response = await fx.Client.GetAsync("/odata/BoundWidgets/GetByName?name=Alpha");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<Widget[]>();
        Assert.Single(items!);
        Assert.Equal("Alpha", items![0].Name);
    }

    [Fact]
    public async Task BoundFunction_WithIntParam_ReturnsScalar()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<BoundOpsProfile>());
        var response = await fx.Client.GetAsync("/odata/BoundWidgets/DoubleCount?factor=3");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var count = await response.Content.ReadFromJsonAsync<int>();
        Assert.Equal(6, count); // 2 items × factor 3
    }

    [Fact]
    public async Task BoundFunction_MissingRequiredParam_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<BoundOpsProfile>());
        var response = await fx.Client.GetAsync("/odata/BoundWidgets/DoubleCount");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MissingParameter", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task BoundAction_NoParams_Returns204()
    {
        // ClearAll removes all widgets; subsequent GetAll should return empty
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<BoundOpsProfile>());
        var clearResponse = await fx.Client.PostAsync("/odata/BoundWidgets/ClearAll",
            new StringContent("", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, clearResponse.StatusCode);
    }

    [Fact]
    public async Task BoundAction_WithBodyParam_ExecutesSideEffect()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<BoundOpsProfile>());
        // AddSuffix mutates the store; call then verify names changed
        var addResp = await fx.Client.PostAsync("/odata/BoundWidgets/AddSuffix",
            new StringContent("{\"suffix\":\"!\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, addResp.StatusCode);
        var listResp = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BoundWidgets");
        var names = listResp.GetProperty("value").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()).ToArray();
        Assert.All(names, n => Assert.EndsWith("!", n));
    }

    // ── H4: POST Location header ──────────────────────────────────────────────
    [Fact]
    public async Task Post_ReturnsLocationHeaderWithKey()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PostAsJsonAsync("/odata/Widgets", new Widget { Name = "New" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        // Location must contain the key in OData format: /odata/Widgets(N)
        Assert.Matches(@"Widgets\(\d+\)$", location);
    }

    // ── M2: PUT null result returns 404 ──────────────────────────────────────
    [Fact]
    public async Task Put_NullResult_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NullPutProfile>());
        var response = await fx.Client.PutAsJsonAsync("/odata/NullPutWidgets(999)", new Widget { Id = 999, Name = "X" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── H9: GetAll rejects unsupported query options ──────────────────────────
    [Fact]
    public async Task GetAll_WithFilter_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets?$filter=Name eq 'Sprocket'");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UnsupportedQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetAll_WithTop_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets?$top=1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── H3: $count on GetAll rejects $filter ─────────────────────────────────
    [Fact]
    public async Task Count_GetAllPath_WithFilter_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets/$count?$filter=Name eq 'Sprocket'");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── H5: Bound function with Guid parameter ────────────────────────────────
    [Fact]
    public async Task BoundFunction_GuidParam_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GuidFunctionProfile>());
        var id = Guid.NewGuid();
        var response = await fx.Client.GetAsync($"/odata/GuidFnWidgets/EchoGuid?id={id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await response.Content.ReadFromJsonAsync<string>();
        Assert.Equal(id.ToString(), returned);
    }

    // ── H2: void Task bound action returns 204 ────────────────────────────────
    [Fact]
    public async Task BoundAction_VoidReturn_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<VoidActionProfile>());
        var response = await fx.Client.PostAsync("/odata/VoidActionWidgets/DoNothing",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── H8: duplicate AddOhData name throws ──────────────────────────────────
    [Fact]
    public void AddOhData_DuplicateName_Throws()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOhData("v1", o => o.AddProfile<WidgetProfile>());
        // Second registration with the same name must throw immediately at call time
        Assert.Throws<InvalidOperationException>(() =>
            builder.Services.AddOhData("v1", o => o.AddProfile<EmptyProfile>()));
    }

    // ── C3: MaxTop enforced on GetQueryable path ──────────────────────────────
    [Fact]
    public async Task GetQueryable_MaxTop_IsEnforced()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MaxTopProfile>());
        // Store has 20 items; MaxTop = 5 on the profile
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/MaxTopWidgets");
        var count = json.GetProperty("value").GetArrayLength();
        Assert.True(count <= 5, $"Expected at most 5 items (MaxTop=5) but got {count}");
    }

    // ── M4: Navigation collection routes wrap in OData envelope ─────────────
    [Fact]
    public async Task Navigation_Collection_WrappedInOdataEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ParentWithChildrenProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Parents(1)/Children");
        Assert.True(json.TryGetProperty("@odata.context", out _), "Expected @odata.context in navigation collection response");
        Assert.True(json.TryGetProperty("value", out var value), "Expected value array in navigation collection response");
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
    }

    // ── M6: Weak ETag prefix W/"..." handled correctly ───────────────────────
    [Fact]
    public async Task ETag_WeakPrefix_IsStrippedBeforeComparison()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagWidgetProfile>());
        // First GET to obtain the strong ETag value
        var getResp = await fx.Client.GetAsync("/odata/ETagWidgets(1)");
        var etag = getResp.Headers.ETag?.Tag?.Trim('"');
        Assert.NotNull(etag);

        // PUT with W/"<etag>" — should NOT return 412 (weak prefix must be stripped)
        var req = new HttpRequestMessage(HttpMethod.Put, "/odata/ETagWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "Sprocket" }),
        };
        req.Headers.TryAddWithoutValidation("If-Match", $"W/\"{etag}\"");
        var putResp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
    }

    // ── M9: AuthorizationConfig.Roles is IReadOnlyList (immutable) ───────────
    [Fact]
    public async Task RoleAuth_RouteRequiresAuthorization()
    {
        // Smoke test: role-protected profile produces 401 on unauthenticated request.
        // This verifies the IReadOnlyList<string> Roles path still wires auth correctly.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<RoleAuthProfile>(), addAuth: true);
        var response = await fx.Client.GetAsync("/odata/RoleWidgets");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Test gap: multiple named registrations in a single host ──────────────
    [Fact]
    public async Task MultipleRegistrations_SingleHost_BothRoute()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddOhData("v1", o => o.WithPrefix("/v1").AddProfile<WidgetProfile>());
        builder.Services.AddOhData("v2", o => o.WithPrefix("/v2").AddProfile<SecondProfile>());
        await using var app = builder.Build();
        app.MapOhData("v1");
        app.MapOhData("v2");
        await app.StartAsync();
        using var client = ((Microsoft.Extensions.Hosting.IHost)app).GetTestClient();

        var r1 = await client.GetAsync("/v1/Widgets");
        var r2 = await client.GetAsync("/v2/SecondWidgets");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    // ── H1: GetAll null result returns empty collection ──────────────────────
    [Fact]
    public async Task GetAll_NullResult_ReturnsEmptyCollection()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NullGetAllProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NullGetAllWidgets");
        Assert.True(json.TryGetProperty("value", out var value));
        Assert.Equal(0, value.GetArrayLength());
    }

    // ── H3: Decimal key type parsed correctly ────────────────────────────────
    [Fact]
    public async Task DecimalKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DecimalKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/DecimalItems(1.5)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── H4: MaxTop zero/negative throws ──────────────────────────────────────
    [Fact]
    public void MaxTop_Zero_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OhData.Abstractions.EntitySetDefaults { MaxTop = 0 });
    }

    [Fact]
    public void MaxTop_Negative_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OhData.Abstractions.EntitySetDefaults { MaxTop = -1 });
    }

    [Fact]
    public void MaxTop_Null_IsAllowed()
    {
        var defaults = new OhData.Abstractions.EntitySetDefaults { MaxTop = null };
        Assert.Null(defaults.MaxTop);
    }

    // ── H6: Bound action with case-insensitive param name ────────────────────
    [Fact]
    public async Task BoundAction_CaseInsensitiveParamName_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<BoundOpsProfile>());
        // Send "Suffix" (PascalCase) when the C# param is "suffix" (camelCase)
        var response = await fx.Client.PostAsync("/odata/BoundWidgets/AddSuffix",
            new StringContent("{\"Suffix\": \"!\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── M1: $select — OData parser normalizes identifiers to EDM names ────────

    [Fact]
    public async Task Select_PascalCase_PropertyIncluded_OthersExcluded()
    {
        // $select=Name (EDM PascalCase) returns only the Name property
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$select=Name");
        var items = json.GetProperty("value");
        Assert.True(items.GetArrayLength() > 0);
        var first = items[0];
        Assert.True(first.TryGetProperty("Name", out _), "Expected 'Name' property to be present");
        Assert.False(first.TryGetProperty("Id", out _), "Expected 'Id' to be excluded");
    }

    [Fact]
    public async Task Select_LowercaseInput_NormalizedToEdmName_PropertyIncluded()
    {
        // The Microsoft.OData parser normalizes $select=name to EDM identifier "Name",
        // so the result is the same as $select=Name.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$select=name");
        var items = json.GetProperty("value");
        Assert.True(items.GetArrayLength() > 0);
        var first = items[0];
        Assert.True(first.TryGetProperty("Name", out _), "Expected 'Name' present — OData parser normalizes to EDM name");
    }

    // ── M2: Authorization double-configure guards ─────────────────────────────

    [Fact]
    public void RequireAuthorization_CalledTwice_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new DoubleAuthProfile();
        });
    }

    [Fact]
    public void RequireRoles_CalledTwice_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new DoubleRolesProfile();
        });
    }

    [Fact]
    public void RequireAuthorization_Policy_ThenRoles_Combines()
    {
        // Should not throw — policy + roles is a valid combination
        var profile = new PolicyAndRolesProfile();
        var source = (OhData.Abstractions.IEntitySetEndpointSource)profile;
        Assert.NotNull(source.Authorization);
        Assert.Equal("MyPolicy", source.Authorization!.Policy);
        Assert.Contains("Admin", source.Authorization.Roles!);
    }

    // ── M4: PUT/PATCH key mismatch returns 400 ────────────────────────────────

    [Fact]
    public async Task PutById_KeyMismatch_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PutAsync("/odata/Widgets(1)",
            new StringContent("{\"id\":2,\"name\":\"X\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_KeyMismatch_Returns400()
    {
        // PATCH with an explicit wrong key in the body is still rejected
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/Widgets(1)",
            new StringContent("{\"id\":2,\"name\":\"X\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_OmittedKey_Succeeds()
    {
        // PATCH without a key property in the body is valid — URL key is authoritative
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/Widgets(1)",
            new StringContent("{\"name\":\"NoKey\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── M5: WithPrefix normalization ──────────────────────────────────────────

    [Fact]
    public async Task WithPrefix_NoLeadingSlash_NormalizesPrefix()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.WithPrefix("api").AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/api/Widgets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithPrefix_TrailingSlash_NormalizesPrefix()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.WithPrefix("/api/").AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/api/Widgets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── M6: WithDefaults ──────────────────────────────────────────────────────

    [Fact]
    public async Task WithDefaults_MaxTop_AppliedToProfiles()
    {
        // Default MaxTop = 1 via builder; no per-profile override.
        // QueryableWidgetProfile has 2 items, so MaxTop=1 should cap results to 1.
        await using var fx = await TestHostBuilder.BuildAsync(o =>
            o.WithDefaults(d => d.MaxTop = 1)
             .AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets");
        Assert.Equal(1, json.GetProperty("value").GetArrayLength());
    }

    // ── H3 revisit: temporal key types ────────────────────────────────────────

    [Fact]
    public async Task KeyParser_DateTimeOffset_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DateTimeOffsetKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/DateTimeOffsetItems(2024-01-15T12:00:00%2B00:00)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task KeyParser_DateTime_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DateTimeKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/DateTimeItems(2024-06-01T00:00:00Z)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task KeyParser_DateOnly_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DateOnlyKeyProfile>());
        var response = await fx.Client.GetAsync("/odata/DateOnlyItems(2024-03-20)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── DELETE idempotency ────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_NotFound_Idempotent_Returns204()
    {
        // Default IdempotentDelete = true — deleting a non-existent key returns 204
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.DeleteAsync("/odata/Widgets(999)");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_NotFound_NonIdempotent_Returns404()
    {
        // IdempotentDelete = false on profile — returns 404
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NonIdempotentDeleteProfile>());
        var response = await fx.Client.DeleteAsync("/odata/NonIdempotentWidgets(999)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

}

// ── Auth test helpers (not registered as profiles — instantiated directly) ───

internal class DoubleAuthProfile : OhData.Abstractions.EntitySetProfile<int, Widget>
{
    public DoubleAuthProfile() : base(x => x.Id)
    {
        RequireAuthorization();
        RequireAuthorization(); // should throw
    }
}

internal class DoubleRolesProfile : OhData.Abstractions.EntitySetProfile<int, Widget>
{
    public DoubleRolesProfile() : base(x => x.Id)
    {
        RequireRoles("Admin");
        RequireRoles("SuperAdmin"); // should throw
    }
}

internal class PolicyAndRolesProfile : OhData.Abstractions.EntitySetProfile<int, Widget>
{
    public PolicyAndRolesProfile() : base(x => x.Id)
    {
        RequireAuthorization("MyPolicy");
        RequireRoles("Admin"); // should combine, not throw
    }
}
