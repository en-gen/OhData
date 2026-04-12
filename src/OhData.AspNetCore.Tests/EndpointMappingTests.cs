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
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(long.TryParse(body, out _));
    }

    [Fact]
    public async Task Count_Queryable_WithFilter_ReturnsFilteredCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/QueryableWidgets/$count?$filter=Name eq 'Sprocket'");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(long.TryParse(body, out long count));
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task Count_Queryable_NoFilter_ReturnsTotalCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/QueryableWidgets/$count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(long.TryParse(body, out long count));
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
        string xml = await fx.Client.GetStringAsync("/odata/$metadata");
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
        // $select uses JsonSerializer with camelCase to match the rest of the OData response.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$select=Name");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("name", out _));
        Assert.False(firstItem.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Select_OtherProperty_OnlySelectedPropertyPresent()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$select=Id");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("id", out _));
        Assert.False(firstItem.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Select_AllProperties_ReturnsAllProperties()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$select=Id,Name");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("id", out _));
        Assert.True(firstItem.TryGetProperty("name", out _));
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
        string?[] names = Enumerable.Range(0, values.GetArrayLength())
            .Select(i => values[i].GetProperty("name").GetString())
            .ToArray();
        Assert.Contains("Sprocket", names);
        Assert.Contains("Cog", names);
    }

    // $select on the IEnumerable (GetAll) path — JsonNode post-materialization
    [Fact]
    public async Task Select_GetAllPath_SingleProperty_OnlySelectedPropertyPresent()
    {
        // $select uses camelCase serialization so the output matches the rest of the response.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets?$select=Name");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("name", out _));
        Assert.False(firstItem.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Select_GetAllPath_LowercaseInput_NormalizedToEdmName()
    {
        // The OData parser normalizes $select=name to EDM identifier "Name" — behaves like $select=Name.
        // Output uses camelCase ("name") to be consistent with non-$select responses.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets?$select=name");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("name", out _));
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
    public async Task Select_GetAll_PropertyCasingMatchesCamelCase()
    {
        // $select uses JsonSerializer with camelCase so the output is consistent with
        // non-$select OData responses (which ASP.NET Core serializes as camelCase).
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets?$select=Name");
        var first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("name", out _));   // camelCase
        Assert.False(first.TryGetProperty("Name", out _));  // NOT PascalCase
    }
    // â"€â"€ EF Core InMemory + ISelectExpandWrapper â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [Fact]
    public async Task Select_EfCoreInMemory_SingleProperty_OnlySelectedPropertyPresent()
    {
        // Verifies that $select works with the GetQueryable (EF Core) path.
        // Output uses camelCase to be consistent with non-$select responses.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EfCoreWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/odata/EfWidgets?$select=Name");
        var firstItem = json.GetProperty("value")[0];
        Assert.True(firstItem.TryGetProperty("name", out _));
        Assert.False(firstItem.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Select_EfCoreInMemory_CorrectValues()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EfCoreWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/odata/EfWidgets?$select=Name");
        var values = json.GetProperty("value");
        string?[] names = System.Linq.Enumerable.Range(0, values.GetArrayLength())
            .Select(i => values[i].GetProperty("name").GetString())
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
        string etag = getResp.Headers.ETag!.Tag;
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
        // Gap 1: bound function returning IEnumerable<TModel> is wrapped in OData envelope
        string body = await response.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.True(json.TryGetProperty("@odata.context", out _), $"Expected @odata.context in envelope. Body was: {body}");
        Assert.True(json.TryGetProperty("value", out var value), $"Expected 'value' key. Body was: {body}");
        Assert.Equal(1, value.GetArrayLength());
        // Items serialized using ASP.NET Core's camelCase serializer
        Assert.Equal("Alpha", value[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task BoundFunction_WithIntParam_ReturnsScalar()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<BoundOpsProfile>());
        var response = await fx.Client.GetAsync("/odata/BoundWidgets/DoubleCount?factor=3");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        int count = await response.Content.ReadFromJsonAsync<int>();
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
        string?[] names = listResp.GetProperty("value").EnumerateArray()
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
        string? location = response.Headers.Location?.ToString();
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
        string? returned = await response.Content.ReadFromJsonAsync<string>();
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
        int count = json.GetProperty("value").GetArrayLength();
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
        string? etag = getResp.Headers.ETag?.Tag?.Trim('"');
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
        // $select=Name returns only the name property (in camelCase to be consistent
        // with the rest of the OData response serialization).
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$select=Name");
        var items = json.GetProperty("value");
        Assert.True(items.GetArrayLength() > 0);
        var first = items[0];
        Assert.True(first.TryGetProperty("name", out _), "Expected 'name' property to be present (camelCase)");
        Assert.False(first.TryGetProperty("id", out _), "Expected 'id' to be excluded");
    }

    [Fact]
    public async Task Select_LowercaseInput_NormalizedToEdmName_PropertyIncluded()
    {
        // The Microsoft.OData parser normalizes $select=name to EDM identifier "Name",
        // so the result is the same as $select=Name — output is camelCase "name".
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets?$select=name");
        var items = json.GetProperty("value");
        Assert.True(items.GetArrayLength() > 0);
        var first = items[0];
        Assert.True(first.TryGetProperty("name", out _), "Expected 'name' present — OData parser normalizes to EDM name, output is camelCase");
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

    [Fact]
    public async Task Patch_MalformedJson_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/Widgets(1)",
            new StringContent("{broken", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    // ── Gap 1: OData-Version header ──────────────────────────────────────────────

    [Fact]
    public async Task AllResponses_IncludeODataVersionHeader()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var resp = await fx.Client.GetAsync("/odata/Widgets");
        Assert.Equal("4.0", resp.Headers.GetValues("OData-Version").FirstOrDefault());
    }

    [Fact]
    public async Task AllResponses_Metadata_IncludesODataVersionHeader()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var resp = await fx.Client.GetAsync("/odata/$metadata");
        Assert.Equal("4.0", resp.Headers.GetValues("OData-Version").FirstOrDefault());
    }

    // ── Gap 2: If-None-Match → 304 Not Modified ──────────────────────────────────

    [Fact]
    public async Task GetById_IfNoneMatch_Matching_Returns304()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagWidgetProfile>());
        var first = await fx.Client.GetAsync("/odata/ETagWidgets(1)");
        string etag = first.Headers.ETag!.Tag;
        var req = new HttpRequestMessage(HttpMethod.Get, "/odata/ETagWidgets(1)");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, resp.StatusCode);
    }

    [Fact]
    public async Task GetById_IfNoneMatch_NotMatching_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagWidgetProfile>());
        var req = new HttpRequestMessage(HttpMethod.Get, "/odata/ETagWidgets(1)");
        req.Headers.TryAddWithoutValidation("If-None-Match", "\"stale-etag\"");
        var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Gap 3: @odata.nextLink + $skiptoken ──────────────────────────────────────

    [Fact]
    public async Task GetQueryable_MaxTop_AddsNextLink()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o =>
            o.WithDefaults(d => d.MaxTop = 1).AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets");
        Assert.True(json.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task GetQueryable_FollowNextLink_ReturnsNextPage()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o =>
            o.WithDefaults(d => d.MaxTop = 1).AddProfile<QueryableWidgetProfile>());
        var first = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets");
        string nextLink = first.GetProperty("@odata.nextLink").GetString()!;
        // nextLink is absolute; strip host for TestClient
        string path = new Uri(nextLink).PathAndQuery;
        var second = await fx.Client.GetFromJsonAsync<JsonElement>(path);
        Assert.True(second.TryGetProperty("value", out var vals));
        Assert.Equal(1, vals.GetArrayLength());
    }

    [Fact]
    public async Task GetQueryable_NoNextLink_WhenPageNotFull()
    {
        // MaxTop=10 but only 2 items — page is not full, so no nextLink
        await using var fx = await TestHostBuilder.BuildAsync(o =>
            o.WithDefaults(d => d.MaxTop = 10).AddProfile<QueryableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/QueryableWidgets");
        Assert.False(json.TryGetProperty("@odata.nextLink", out _));
    }

    // ── Gap 4: Prefer: return=minimal ─────────────────────────────────────────────

    [Fact]
    public async Task Post_PreferMinimal_Returns204WithLocation()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var req = new HttpRequestMessage(HttpMethod.Post, "/odata/Widgets")
        {
            Content = JsonContent.Create(new Widget { Name = "X" })
        };
        req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
        var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.NotNull(resp.Headers.Location);
    }

    [Fact]
    public async Task Put_PreferMinimal_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var req = new HttpRequestMessage(HttpMethod.Put, "/odata/Widgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "X" })
        };
        req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
        var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_PreferMinimal_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var req = new HttpRequestMessage(HttpMethod.Patch, "/odata/Widgets(1)")
        {
            Content = new StringContent("{\"name\":\"X\"}", System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Prefer", "return=minimal");
        var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Post_NoPreferHeader_Returns201WithBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var resp = await fx.Client.PostAsJsonAsync("/odata/Widgets", new Widget { Name = "X" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.NotEmpty(body);
    }

    // ── Gap 5: @odata.id entity self-link ─────────────────────────────────────────

    [Fact]
    public async Task GetById_ResponseContainsOdataId()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets(1)");
        Assert.True(json.TryGetProperty("@odata.id", out var id));
        Assert.Contains("Widgets(1)", id.GetString());
    }

    [Fact]
    public async Task Put_ResponseContainsOdataId()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var resp = await fx.Client.PutAsJsonAsync("/odata/Widgets(1)", new Widget { Id = 1, Name = "Updated" });
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.id", out var id));
        Assert.Contains("Widgets(1)", id.GetString());
    }

    [Fact]
    public async Task Post_ResponseContainsOdataId()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var resp = await fx.Client.PostAsJsonAsync("/odata/Widgets", new Widget { Name = "New" });
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.id", out var id));
        Assert.NotNull(id.GetString());
        Assert.Contains("Widgets", id.GetString());
    }

    // ── Gap 6: error.target + error.details ───────────────────────────────────────

    [Fact]
    public async Task PutById_KeyMismatch_ErrorHasTarget()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var resp = await fx.Client.PutAsync("/odata/Widgets(1)",
            new StringContent("{\"id\":2,\"name\":\"X\"}", System.Text.Encoding.UTF8, "application/json"));
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        string? target = json.GetProperty("error").GetProperty("target").GetString();
        Assert.Equal("key", target);
    }

    [Fact]
    public async Task PatchById_KeyMismatch_ErrorHasTarget()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var resp = await fx.Client.PatchAsync("/odata/Widgets(1)",
            new StringContent("{\"id\":2,\"name\":\"X\"}", System.Text.Encoding.UTF8, "application/json"));
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        string? target = json.GetProperty("error").GetProperty("target").GetString();
        Assert.Equal("key", target);
    }

    [Fact]
    public async Task GetById_BadKeyFormat_ErrorHasTarget()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var resp = await fx.Client.GetAsync("/odata/Widgets(notanint)");
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("error").TryGetProperty("target", out var target));
        Assert.Equal("key", target.GetString());
    }

    // ── Gap 7: Entity-level bound functions and actions ───────────────────────────

    [Fact]
    public async Task EntityBoundFunction_ReturnsEntityData()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EntityBoundOpsProfile>());
        var resp = await fx.Client.GetAsync("/odata/EntityBoundWidgets(1)/GetNameForKey");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string? name = await resp.Content.ReadFromJsonAsync<string>();
        Assert.Equal("Alpha", name);
    }

    [Fact]
    public async Task EntityBoundFunction_UnknownKey_ReturnsOk_WithEmptyString()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EntityBoundOpsProfile>());
        var resp = await fx.Client.GetAsync("/odata/EntityBoundWidgets(999)/GetNameForKey");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task EntityBoundAction_MutatesEntity()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EntityBoundOpsProfile>());
        var renameResp = await fx.Client.PostAsync("/odata/EntityBoundWidgets(1)/RenameWidget",
            new StringContent("{\"newName\":\"Renamed\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, renameResp.StatusCode);

        var getResp = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/EntityBoundWidgets");
        string?[] names = getResp.GetProperty("value").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()).ToArray();
        Assert.Contains("Renamed", names);
    }

    [Fact]
    public async Task EntityBoundFunction_BadKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<EntityBoundOpsProfile>());
        var resp = await fx.Client.GetAsync("/odata/EntityBoundWidgets(notanint)/GetNameForKey");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Gap 8: $expand data loading ───────────────────────────────────────────────

    [Fact]
    public async Task Expand_InlinesNavigationData()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ExpandableParentProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ExpandableParents?$expand=Children");
        var first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("Children", out var children));
        Assert.Equal(JsonValueKind.Array, children.ValueKind);
    }

    [Fact]
    public async Task Expand_WithChildren_ChildrenDataPresent()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ExpandableParentProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ExpandableParents?$expand=Children");
        var first = json.GetProperty("value")[0];
        var children = first.GetProperty("Children");
        Assert.True(children.GetArrayLength() > 0);
    }

    // ── Batch 2, Gap 1: @odata.context on function/action responses ───────────────

    [Fact]
    public async Task BoundFunction_ReturnsCollectionOfTModel_IncludesOdataContext()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ContextFunctionProfile>());
        var resp = await fx.Client.GetAsync("/odata/ContextFnWidgets/GetAll2");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var ctx));
        Assert.Contains("ContextFnWidgets", ctx.GetString()!);
        Assert.True(json.TryGetProperty("value", out var val));
        Assert.Equal(JsonValueKind.Array, val.ValueKind);
    }

    [Fact]
    public async Task BoundFunction_ReturnsSingleTModel_IncludesOdataContext()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ContextFunctionProfile>());
        var resp = await fx.Client.GetAsync("/odata/ContextFnWidgets/GetFirst");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out var ctx));
        Assert.Contains("/$entity", ctx.GetString()!);
    }

    // ── Batch 2, Gap 2: @odata.etag in response body ──────────────────────────────

    [Fact]
    public async Task GetById_WithETag_ResponseBodyIncludesOdataEtag()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagBodyProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ETagBodyWidgets(1)");
        Assert.True(json.TryGetProperty("@odata.etag", out _), "Expected @odata.etag in GetById body");
    }

    [Fact]
    public async Task Post_WithETag_ResponseBodyIncludesOdataEtag()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagBodyProfile>());
        var resp = await fx.Client.PostAsJsonAsync("/odata/ETagBodyWidgets", new Widget { Name = "New" });
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.etag", out _), "Expected @odata.etag in POST body");
    }

    [Fact]
    public async Task Put_WithETag_ResponseBodyIncludesOdataEtag()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagBodyProfile>());
        var resp = await fx.Client.PutAsJsonAsync("/odata/ETagBodyWidgets(1)", new Widget { Id = 1, Name = "Updated" });
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.etag", out _), "Expected @odata.etag in PUT body");
    }

    [Fact]
    public async Task Patch_WithETag_ResponseBodyIncludesOdataEtag()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagBodyProfile>());
        var resp = await fx.Client.PatchAsync("/odata/ETagBodyWidgets(1)",
            new StringContent("{\"name\":\"Patched\"}", System.Text.Encoding.UTF8, "application/json"));
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.etag", out _), "Expected @odata.etag in PATCH body");
    }

    // ── Batch 2, Gap 3: Upsert via PUT ────────────────────────────────────────────

    [Fact]
    public async Task Put_AllowUpsert_NonExistingKey_Returns201()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<UpsertProfile>());
        var resp = await fx.Client.PutAsJsonAsync("/odata/UpsertWidgets(99)", new Widget { Id = 99, Name = "NewViaUpsert" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Put_NoAllowUpsert_NonExistingKey_Returns404()
    {
        // Default WidgetProfile does not set AllowUpsert — PUT to missing key returns 404
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NullPutProfile>());
        var resp = await fx.Client.PutAsJsonAsync("/odata/NullPutWidgets(99)", new Widget { Id = 99, Name = "X" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Put_AllowUpsert_ExistingKey_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<UpsertProfile>());
        var resp = await fx.Client.PutAsJsonAsync("/odata/UpsertWidgets(1)", new Widget { Id = 1, Name = "UpdatedExisting" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Batch 2, Gap 4: $search query option ──────────────────────────────────────

    [Fact]
    public async Task Search_WithHandler_ReturnsFilteredResults()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<SearchableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/SearchableWidgets?$search=Alpha");
        var value = json.GetProperty("value");
        Assert.Equal(1, value.GetArrayLength());
    }

    [Fact]
    public async Task Search_WithHandler_NoMatch_ReturnsEmptyCollection()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<SearchableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/SearchableWidgets?$search=NoMatch");
        var value = json.GetProperty("value");
        Assert.Equal(0, value.GetArrayLength());
    }

    [Fact]
    public async Task Search_WithoutHandler_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NoSearchProfile>());
        var resp = await fx.Client.GetAsync("/odata/NoSearchWidgets?$search=anything");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UnsupportedQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ── Batch 2, Gap 5: Query options on navigation collection results ─────────────

    [Fact]
    public async Task NavigationCollection_WithTop_ReturnsLimitedItems()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavQueryProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavQueryParents(1)/Children?$top=1");
        var value = json.GetProperty("value");
        Assert.Equal(1, value.GetArrayLength());
    }

    [Fact]
    public async Task NavigationCollection_WithSkip_SkipsItems()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavQueryProfile>());
        var all = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavQueryParents(1)/Children");
        int allCount = all.GetProperty("value").GetArrayLength();

        var skipped = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavQueryParents(1)/Children?$skip=1");
        int skippedCount = skipped.GetProperty("value").GetArrayLength();
        Assert.Equal(allCount - 1, skippedCount);
    }

    [Fact]
    public async Task NavigationCollection_WithCountTrue_ReturnsOdataCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavQueryProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavQueryParents(1)/Children?$count=true");
        Assert.True(json.TryGetProperty("@odata.count", out var count));
        Assert.True(count.GetInt64() > 0);
    }

    // ── Batch 2, Gap 6: $ref endpoints for navigation ─────────────────────────────

    [Fact]
    public async Task NavigationRef_Get_Returns200WithContext()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavQueryProfile>());
        var resp = await fx.Client.GetAsync("/odata/NavQueryParents(1)/Children/$ref");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("@odata.context", out _));
    }

    [Fact]
    public async Task NavigationRef_Post_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavQueryProfile>());
        var resp = await fx.Client.PostAsync("/odata/NavQueryParents(1)/Children/$ref",
            new StringContent("{\"@odata.id\":\"http://localhost/odata/Children(99)\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task NavigationRef_Delete_Returns204()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavQueryProfile>());
        var resp = await fx.Client.DeleteAsync("/odata/NavQueryParents(1)/Children/$ref?$id=http://localhost/odata/Children(1)");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    // ── Batch 2, Gap 7: Unbound functions and actions ─────────────────────────────

    [Fact]
    public async Task UnboundFunction_ReturnsResult()
    {
        Func<string, Task<string>> greet = name => Task.FromResult($"Hello, {name}!");
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddFunction(greet, "Greet"));
        var resp = await fx.Client.GetAsync("/odata/Greet?name=World");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string? body = await resp.Content.ReadFromJsonAsync<string>();
        Assert.Equal("Hello, World!", body);
    }

    [Fact]
    public async Task UnboundFunction_MissingParam_Returns400()
    {
        Func<string, Task<string>> greet2 = name2 => Task.FromResult($"Hi, {name2}!");
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddFunction(greet2, "Greet2"));
        var resp = await fx.Client.GetAsync("/odata/Greet2");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MissingParameter", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnboundAction_ExecutesSideEffect()
    {
        bool called = false;
        Func<string, Task> echoDelegate = message => { called = true; return Task.CompletedTask; };
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddAction(echoDelegate, "Echo"));
        var resp = await fx.Client.PostAsync("/odata/Echo",
            new StringContent("{\"message\":\"test\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.True(called);
    }

    [Fact]
    public async Task UnboundAction_WithReturnValue_Returns200()
    {
        Func<int, int, Task<int>> addDelegate = (a, b) => Task.FromResult(a + b);
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddAction(addDelegate, "AddNumbers"));
        var resp = await fx.Client.PostAsync("/odata/AddNumbers",
            new StringContent("{\"a\":3,\"b\":4}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        int result = await resp.Content.ReadFromJsonAsync<int>();
        Assert.Equal(7, result);
    }

    // ── Batch 2, Gap 8: $expand on GetAll path ────────────────────────────────────

    [Fact]
    public async Task Expand_OnGetAllPath_InlinesNavigationData()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ExpandableGetAllProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ExpandableGetAllParents?$expand=Children");
        var value = json.GetProperty("value");
        Assert.True(value.GetArrayLength() > 0);
        var first = value[0];
        Assert.True(first.TryGetProperty("Children", out var children));
        Assert.Equal(JsonValueKind.Array, children.ValueKind);
    }

    [Fact]
    public async Task Expand_OnGetAllPath_ChildrenDataPresent()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ExpandableGetAllProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ExpandableGetAllParents?$expand=Children");
        var value = json.GetProperty("value");
        // Both parents should have their children expanded
        for (int i = 0; i < value.GetArrayLength(); i++)
        {
            Assert.True(value[i].TryGetProperty("Children", out var children));
            Assert.Equal(JsonValueKind.Array, children.ValueKind);
        }
    }

    // ── Batch 3: Navigation /$count standalone endpoint (§11.2.3) ─────────────

    [Fact]
    public async Task NavigationCollection_CountEndpoint_Returns200WithInteger()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavCountProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/NavCountParents(1)/Children/$count");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(int.TryParse(body, out int count), $"Expected integer body, got: {body}");
        Assert.Equal(2, count); // Parent 1 has ChildA + ChildB
    }

    [Fact]
    public async Task NavigationCollection_CountEndpoint_ReturnsCorrectCountForParent2()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavCountProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/NavCountParents(2)/Children/$count");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(int.TryParse(body, out int count));
        Assert.Equal(1, count); // Parent 2 has only ChildC
    }

    [Fact]
    public async Task NavigationCollection_CountEndpoint_BadKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavCountProfile>());
        var response = await fx.Client.GetAsync("/odata/NavCountParents(notanint)/Children/$count");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Batch 3: $select on navigation collection results ─────────────────────

    [Fact]
    public async Task NavigationCollection_WithSelect_FiltersProperties()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavCountProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavCountParents(1)/Children?$select=Name");
        var value = json.GetProperty("value");
        Assert.True(value.GetArrayLength() > 0);
        var first = value[0];
        Assert.True(first.TryGetProperty("name", out _), "Expected 'name' property (camelCase) to be present");
        Assert.False(first.TryGetProperty("id", out _), "Expected 'id' to be excluded by $select");
    }

    [Fact]
    public async Task NavigationCollection_WithSelect_MultipleProperties()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavCountProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavCountParents(1)/Children?$select=Id,Name");
        var value = json.GetProperty("value");
        Assert.True(value.GetArrayLength() > 0);
        var first = value[0];
        Assert.True(first.TryGetProperty("id", out _), "Expected 'id' to be present");
        Assert.True(first.TryGetProperty("name", out _), "Expected 'name' to be present");
        Assert.False(first.TryGetProperty("parentId", out _), "Expected 'parentId' to be excluded");
    }

    // ── Batch 3: IODataEntitySetEndpointSource (Priority-1 handler) ───────────

    [Fact]
    public async Task ODataProfile_CollectionGet_Returns200WithValueArray()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ODataWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ODataWidgets");
        Assert.True(json.TryGetProperty("value", out var value));
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        Assert.Equal(3, value.GetArrayLength());
    }

    [Fact]
    public async Task ODataProfile_Filter_AppliedByProfile()
    {
        // The Priority-1 handler receives ODataQueryOptions and applies them itself.
        // $filter=Name eq 'Cog' should return only Cog.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ODataWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ODataWidgets?$filter=Name%20eq%20'Cog'");
        var value = json.GetProperty("value");
        Assert.Equal(1, value.GetArrayLength());
        Assert.Equal("Cog", value[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ODataProfile_Top_LimitsResults()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ODataWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ODataWidgets?$top=1");
        Assert.Equal(1, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task ODataProfile_Skip_SkipsResults()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ODataWidgetProfile>());
        var all = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ODataWidgets");
        var skipped = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ODataWidgets?$skip=1");
        Assert.Equal(
            all.GetProperty("value").GetArrayLength() - 1,
            skipped.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task ODataProfile_OrderBy_SortsResults()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ODataWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ODataWidgets?$orderby=Name");
        var value = json.GetProperty("value");
        string?[] names = Enumerable.Range(0, value.GetArrayLength())
            .Select(i => value[i].GetProperty("name").GetString())
            .ToArray();
        Assert.Equal(names.OrderBy(n => n).ToArray(), names);
    }

    [Fact]
    public async Task ODataProfile_CountInline_ReturnsOdataCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ODataWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ODataWidgets?$count=true");
        Assert.True(json.TryGetProperty("@odata.count", out var count));
        Assert.Equal(3, count.GetInt32());
    }

    [Fact]
    public async Task ODataProfile_CountStandalone_ReturnsInteger()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ODataWidgetProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/ODataWidgets/$count");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(int.TryParse(body, out int count));
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task ODataProfile_GetById_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ODataWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ODataWidgets(1)");
        Assert.Equal("Sprocket", json.GetProperty("name").GetString());
    }

    // ── Batch 3: Delta<TModel> PATCH via PatchDelta ───────────────────────────

    [Fact]
    public async Task DeltaPatch_PartialUpdate_OnlyChangesSpecifiedProperties()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeltaPatchWidgetProfile>());
        // Patch only the Name — Id should be unchanged
        var response = await fx.Client.PatchAsJsonAsync(
            "/odata/DeltaWidgets(1)",
            new { Name = "Modified" });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Modified", json.GetProperty("name").GetString());
        Assert.Equal(1, json.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task DeltaPatch_MissingEntity_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeltaPatchWidgetProfile>());
        var response = await fx.Client.PatchAsJsonAsync(
            "/odata/DeltaWidgets(99)",
            new { Name = "Ghost" });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeltaPatch_EntityStillReadableAfterPatch()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<DeltaPatchWidgetProfile>());
        await fx.Client.PatchAsJsonAsync("/odata/DeltaWidgets(2)", new { Name = "PatchedCog" });
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/DeltaWidgets(2)");
        Assert.Equal("PatchedCog", json.GetProperty("name").GetString());
    }

    // ── Batch 4: 406 Not Acceptable ──────────────────────────────────────────

    [Fact]
    public async Task Accept_ApplicationJson_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/Widgets");
        request.Headers.Add("Accept", "application/json");
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Accept_Star_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/Widgets");
        request.Headers.Add("Accept", "*/*");
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Accept_TextXml_Returns406()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/Widgets");
        request.Headers.Add("Accept", "text/xml");
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task Accept_TextHtml_Returns406()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/Widgets");
        request.Headers.Add("Accept", "text/html");
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task Accept_Metadata_ApplicationXml_Returns200()
    {
        // $metadata returns XML — should not be blocked by the 406 filter
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/$metadata");
        request.Headers.Add("Accept", "application/xml");
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Batch 4: @odata.etag in collection responses ──────────────────────────

    [Fact]
    public async Task ETagCollection_GetAll_ContainsOdataEtagPerItem()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagCollectionProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ETagCollWidgets");
        JsonElement value = json.GetProperty("value");
        Assert.True(value.GetArrayLength() > 0);
        foreach (JsonElement item in value.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("@odata.etag", out JsonElement etag),
                "@odata.etag should be present on each collection item");
            string? etagStr = etag.GetString();
            Assert.NotNull(etagStr);
            Assert.StartsWith("\"", etagStr); // ETags are always quoted
        }
    }

    [Fact]
    public async Task ETagCollection_SelectFiltered_OdataEtagSurvivesSelect()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagCollectionProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ETagCollWidgets?$select=name");
        JsonElement value = json.GetProperty("value");
        Assert.True(value.GetArrayLength() > 0);
        foreach (JsonElement item in value.EnumerateArray())
        {
            // $select=name should not strip @odata.etag (metadata properties are immune)
            Assert.True(item.TryGetProperty("@odata.etag", out _),
                "@odata.etag must not be removed by $select");
            // id should be stripped by $select
            Assert.False(item.TryGetProperty("id", out _),
                "id should be removed by $select=name");
        }
    }

    // ── Batch 4: If-Match ETag list (CheckETagAsync uses ParseETagList) ───────

    [Fact]
    public async Task IfMatch_MultipleEtags_MatchingOneAllowsPut()
    {
        // If-Match: "bogus-etag", "<actual-etag>" — second entry matches → 200
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagIfMatchProfile>());
        // Obtain the current ETag via GET.
        string etag = (await fx.Client.GetAsync("/odata/IfMatchWidgets(1)")).Headers.ETag!.Tag;
        var request = new HttpRequestMessage(HttpMethod.Put, "/odata/IfMatchWidgets(1)");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"bogus-tag\", {etag}");
        request.Content = JsonContent.Create(new { Id = 1, Name = "Updated" });
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task IfMatch_MultipleEtags_NoneMatchingReturns412()
    {
        // If-Match: "bogus1", "bogus2" — neither matches → 412
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagIfMatchProfile>());
        var request = new HttpRequestMessage(HttpMethod.Put, "/odata/IfMatchWidgets(1)");
        request.Headers.TryAddWithoutValidation("If-Match", "\"bogus1\", \"bogus2\"");
        request.Content = JsonContent.Create(new { Id = 1, Name = "Updated" });
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task IfMatch_WeakEtagInList_IsStripped()
    {
        // W/"<actual-etag>" should match (W/ stripped before comparison) → 200
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ETagIfMatchProfile>());
        string etag = (await fx.Client.GetAsync("/odata/IfMatchWidgets(1)")).Headers.ETag!.Tag;
        var request = new HttpRequestMessage(HttpMethod.Put, "/odata/IfMatchWidgets(1)");
        request.Headers.TryAddWithoutValidation("If-Match", $"W/{etag}");
        request.Content = JsonContent.Create(new { Id = 1, Name = "Updated" });
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Batch 4: Prefer: maxpagesize=N ───────────────────────────────────────

    [Fact]
    public async Task MaxPageSize_LimitsResultCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MaxPageSizeProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/MaxPageWidgets");
        response.Headers.TryGetValues("Prefer", out _); // Prefer not echoed
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // No maxpagesize sent — should return all 20
        Assert.Equal(20, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task MaxPageSize_WithPrefer_LimitsToRequestedSize()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MaxPageSizeProfile>());
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/MaxPageWidgets");
        request.Headers.Add("Prefer", "maxpagesize=5");
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, json.GetProperty("value").GetArrayLength());
        // @odata.nextLink should be present since page is full
        Assert.True(json.TryGetProperty("@odata.nextLink", out _), "@odata.nextLink should be set");
        // Preference-Applied header
        Assert.True(response.Headers.TryGetValues("Preference-Applied", out IEnumerable<string>? applied));
        Assert.Contains(applied!, v => v.Contains("maxpagesize=5"));
    }

    [Fact]
    public async Task MaxPageSize_ExplicitTopOverridesPrefer()
    {
        // $top=3 should take precedence over Prefer: maxpagesize=10
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MaxPageSizeProfile>());
        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/MaxPageWidgets?$top=3");
        request.Headers.Add("Prefer", "maxpagesize=10");
        HttpResponseMessage response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, json.GetProperty("value").GetArrayLength());
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
