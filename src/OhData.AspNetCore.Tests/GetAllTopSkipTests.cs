using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Leg 1 (docs-fidelity): $top/$skip are pure post-materialization Skip()/Take() operations on
/// the GetAll (IEnumerable) collection path, the same class of operation as the already-live
/// $select/$expand/$count -- so they are implemented, not rejected. See
/// OhDataEndpointFactory.MapEntitySet's GetAll branch and docs/query-options.md.
/// </summary>
public class GetAllTopSkipTests
{
    [Fact]
    public async Task Top_LimitsResultCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets?$top=1");
        Assert.Equal(1, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task Skip_OffsetsResults()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets?$skip=1");
        var value = json.GetProperty("value");
        Assert.Equal(1, value.GetArrayLength());
        Assert.Equal(2, value[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task TopAndSkip_Combine()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GetAllMaxTopProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/GetAllMaxTopWidgets?$skip=2&$top=3");
        var value = json.GetProperty("value");
        Assert.Equal(3, value.GetArrayLength());
        Assert.Equal(3, value[0].GetProperty("id").GetInt32());
        Assert.Equal(5, value[2].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task TopWithCount_ReturnsPrePagingTotal()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GetAllMaxTopProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/GetAllMaxTopWidgets?$top=2&$count=true");
        Assert.Equal(2, json.GetProperty("value").GetArrayLength());
        Assert.Equal(20, json.GetProperty("@odata.count").GetInt64());
    }

    [Fact]
    public async Task SkipBeyondEnd_ReturnsEmptyValue()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/Widgets?$skip=100");
        Assert.Equal(0, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task Top_WithinMaxTop_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GetAllMaxTopProfile>());
        var response = await fx.Client.GetAsync("/odata/GetAllMaxTopWidgets?$top=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Top_ExceedingMaxTop_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GetAllMaxTopProfile>());
        var response = await fx.Client.GetAsync("/odata/GetAllMaxTopWidgets?$top=6");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Top_OmittedOnMaxTopProfile_CapsToMaxTop_WithNextLink()
    {
        // #201: an omitted $top on the GetAll path is now capped to MaxTop (safe-by-default),
        // with a $skip @odata.nextLink for the remainder — GetAll re-enumerates its source each
        // request, so offset paging is a valid continuation story. (It previously returned the
        // full 20-item set; set MaxTop=null to opt back into that.) See GetAllCapTests for the
        // full continuation/opt-out matrix.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GetAllMaxTopProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/GetAllMaxTopWidgets");
        Assert.Equal(5, json.GetProperty("value").GetArrayLength());
        Assert.True(json.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task Top_NegativeValue_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets?$top=-1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Skip_NonNumericValue_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets?$skip=abc");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Filter_StillUnsupportedOnGetAll_Returns400UnsupportedQueryOption()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets?$filter=Id eq 1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UnsupportedQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task OrderBy_StillUnsupportedOnGetAll_Returns400UnsupportedQueryOption()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets?$orderby=Name");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UnsupportedQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task TopAndSkip_WithSearch_AppliesPagingAfterSearch()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<SearchableWidgetProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/SearchableWidgets?$search=a&$top=1&$count=true");
        // "Alpha", "Beta", "Gamma" all contain 'a' (case-insensitive) -- 3 pre-paging matches.
        Assert.Equal(3, json.GetProperty("@odata.count").GetInt64());
        Assert.Equal(1, json.GetProperty("value").GetArrayLength());
    }
}
