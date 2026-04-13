using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Verifies that query option flags (FilterEnabled, OrderByEnabled, SelectEnabled,
/// ExpandEnabled, CountEnabled, MaxTop) behave as documented.
///
/// Note: The *Enabled flags configure EDM model capabilities (which properties are
/// advertised and which are validated against the EDM). They do NOT gate whether
/// a query option category is accepted at all on the GetQueryable path — only the
/// GetAll path rejects query options wholesale (those tests live in EndpointMappingTests).
/// MaxTop caps results server-side; it does not reject requests with a higher $top.
/// </summary>
public class QueryOptionEnforcementTests
{
    // ── $filter ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Filter_WhenEnabled_ValidProperty_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOnProfile>());
        var response = await fx.Client.GetAsync("/odata/AllOnWidgets?$filter=Name eq 'Alpha'");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Filter_WhenEnabled_UnknownProperty_Returns400()
    {
        // Unknown property is rejected because it is not in the EDM model.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOnProfile>());
        var response = await fx.Client.GetAsync("/odata/AllOnWidgets?$filter=DoesNotExist eq 1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── $orderby ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrderBy_WhenEnabled_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOnProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/AllOnWidgets?$orderby=Name");
        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/odata/AllOnWidgets?$orderby=Name")).StatusCode);
    }

    [Fact]
    public async Task OrderBy_WhenEnabled_SortsResults()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOnProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/AllOnWidgets?$orderby=Name");
        JsonElement values = json.GetProperty("value");
        Assert.Equal("Alpha", values[0].GetProperty("name").GetString());
        Assert.Equal("Beta", values[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task OrderBy_Descending_WhenEnabled_SortsResults()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOnProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/AllOnWidgets?$orderby=Name desc");
        JsonElement values = json.GetProperty("value");
        Assert.Equal("Beta", values[0].GetProperty("name").GetString());
        Assert.Equal("Alpha", values[1].GetProperty("name").GetString());
    }

    // ── $select ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Select_WhenEnabled_OnlySelectedFieldPresent()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOnProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/AllOnWidgets?$select=Name");
        JsonElement first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("name", out _));
        Assert.False(first.TryGetProperty("id", out _));
    }

    // ── $count ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Count_Standalone_WhenEnabled_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOnProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllOnWidgets/$count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(long.TryParse(body, out long count));
        Assert.Equal(2L, count);
    }

    [Fact]
    public async Task Count_Inline_WhenEnabled_ReturnsOdataCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOnProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/AllOnWidgets?$count=true");
        Assert.True(json.TryGetProperty("@odata.count", out JsonElement countEl));
        Assert.Equal(2L, countEl.GetInt64());
    }

    // ── MaxTop ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MaxTop_WithoutTopParam_CapsResultsAtMaxTop()
    {
        // When no $top is specified, MaxTop acts as the default page size.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MaxTopProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/MaxTopWidgets");
        int count = json.GetProperty("value").GetArrayLength();
        Assert.True(count <= 5, $"Expected at most 5 items (MaxTop=5) but got {count}");
    }

    [Fact]
    public async Task MaxTop_WithTopBelowCap_ReturnsRequestedCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MaxTopProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/MaxTopWidgets?$top=3");
        Assert.Equal(3, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task MaxTop_WithTopAtCap_ReturnsCappedCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MaxTopProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/MaxTopWidgets?$top=5");
        Assert.Equal(5, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task MaxTop_WithTopExceedingCap_Returns400()
    {
        // $top > MaxTop is rejected with 400 Bad Request.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MaxTopProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/MaxTopWidgets?$top=20");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MaxTop_WithTopExceedingCap_AddsNextLink()
    {
        // When results are capped a @odata.nextLink is added to signal more pages exist.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MaxTopProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/MaxTopWidgets");
        Assert.True(json.TryGetProperty("@odata.nextLink", out _));
    }

    // ── FilterProperties allowlist ─────────────────────────────────────────────

    [Fact]
    public async Task FilterProperties_AllowedProperty_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<FilterAllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllowlistWidgets?$filter=Name eq 'Alpha'");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private class AllOnProfile : EntitySetProfile<int, Widget>
    {
        private static readonly List<Widget> Store = new()
        {
            new() { Id = 1, Name = "Alpha" },
            new() { Id = 2, Name = "Beta" },
        };
        public AllOnProfile() : base(x => x.Id)
        {
            EntitySetName = "AllOnWidgets";
            FilterEnabled = true;
            OrderByEnabled = true;
            SelectEnabled = true;
            CountEnabled = true;
            GetQueryable = (ct) => System.Threading.Tasks.Task.FromResult(Store.AsQueryable());
        }
    }

    private class MaxTopProfile : EntitySetProfile<int, Widget>
    {
        private static readonly List<Widget> Store;
        static MaxTopProfile()
        {
            Store = new List<Widget>();
            for (int i = 1; i <= 10; i++)
                Store.Add(new Widget { Id = i, Name = $"Widget{i}" });
        }
        public MaxTopProfile() : base(x => x.Id)
        {
            EntitySetName = "MaxTopWidgets";
            MaxTop = 5;
            GetQueryable = (ct) => System.Threading.Tasks.Task.FromResult(Store.AsQueryable());
        }
    }

    private class FilterAllowlistProfile : EntitySetProfile<int, Widget>
    {
        private static readonly List<Widget> Store = new() { new() { Id = 1, Name = "Alpha" } };
        public FilterAllowlistProfile() : base(x => x.Id)
        {
            EntitySetName = "AllowlistWidgets";
            FilterEnabled = true;
            FilterProperties(x => x.Name);
            GetQueryable = (ct) => System.Threading.Tasks.Task.FromResult(Store.AsQueryable());
        }
    }
}
