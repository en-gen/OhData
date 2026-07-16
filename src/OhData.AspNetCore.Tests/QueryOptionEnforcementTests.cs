using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhData.Abstractions;
using OhData.Abstractions.AspNetCore.OData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Verifies that the query option capability flags (FilterEnabled, OrderByEnabled,
/// SelectEnabled, ExpandEnabled, CountEnabled) and the property allowlists
/// (FilterProperties, OrderByProperties, SelectProperties, ExpandProperties) are ENFORCED
/// at runtime — a disabled option present in the request returns 400 UnsupportedQueryOption,
/// and an option referencing a non-allowlisted property returns 400 (B1 fix, OData 4.0
/// Minimal conformance item 7: "parse the option or reject it").
///
/// Enforcement matrix covered here:
/// - each flag disabled × option present → 400, on both the GetQueryable and the
///   Priority-1 (ODataEntitySetProfile) collection paths, and $select/$expand/$count on GetAll
/// - each flag enabled × option present → 200 (positive controls)
/// - each allowlist: allowlisted property → 200, non-allowlisted property → 400
/// - /$count route: disabled $filter → 400
/// - navigation collection route: $filter (and other unimplemented options) → 400
/// - GetById: $select/$expand gated by SelectEnabled/ExpandEnabled; $expand is implemented
///   (expands via the same pipeline as the collection route, batch handlers included)
/// - MaxTop caps results server-side and rejects $top above the cap with 400
/// </summary>
public class QueryOptionEnforcementTests
{
    // ── Flag disabled × option present → 400 (GetQueryable path) ─────────────────

    [Theory]
    [InlineData("$filter=Name eq 'Alpha'", "$filter")]
    [InlineData("$orderby=Name", "$orderby")]
    [InlineData("$select=Name", "$select")]
    [InlineData("$expand=Children", "$expand")]
    [InlineData("$count=true", "$count")]
    public async Task GetQueryable_FlagDisabled_OptionPresent_Returns400(string query, string optionName)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOffProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync($"/odata/AllOffWidgets?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement error = json.GetProperty("error");
        Assert.Equal("UnsupportedQueryOption", error.GetProperty("code").GetString());
        Assert.Contains(optionName, error.GetProperty("message").GetString());
    }

    // ── Flag disabled × option present → 400 (Priority-1 / ODataEntitySetProfile path) ──

    [Theory]
    [InlineData("$filter=Name eq 'Alpha'", "$filter")]
    [InlineData("$orderby=Name", "$orderby")]
    [InlineData("$select=Name", "$select")]
    [InlineData("$expand=Children", "$expand")]
    [InlineData("$count=true", "$count")]
    public async Task Priority1_FlagDisabled_OptionPresent_Returns400(string query, string optionName)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOffODataProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync($"/odata/AllOffODataWidgets?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement error = json.GetProperty("error");
        Assert.Equal("UnsupportedQueryOption", error.GetProperty("code").GetString());
        Assert.Contains(optionName, error.GetProperty("message").GetString());
    }

    // ── Flag disabled × option present → 400 (GetAll path, $select/$expand/$count) ──
    // ($filter/$orderby/$top/$skip are rejected wholesale on GetAll regardless of flags —
    //  covered in EndpointMappingTests.)

    [Theory]
    [InlineData("$select=Name", "$select")]
    [InlineData("$expand=Children", "$expand")]
    [InlineData("$count=true", "$count")]
    public async Task GetAll_FlagDisabled_OptionPresent_Returns400(string query, string optionName)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOffGetAllProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync($"/odata/AllOffGetAllWidgets?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement error = json.GetProperty("error");
        Assert.Equal("UnsupportedQueryOption", error.GetProperty("code").GetString());
        Assert.Contains(optionName, error.GetProperty("message").GetString());
    }

    // ── Positive controls: flag enabled × option present → 200 ───────────────────

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

    [Fact]
    public async Task Select_WhenEnabled_OnlySelectedFieldPresent()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOnProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/AllOnWidgets?$select=Name");
        JsonElement first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("name", out _));
        Assert.False(first.TryGetProperty("id", out _));
    }

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

    // ── /$count route: disabled $filter → 400 ────────────────────────────────────

    [Fact]
    public async Task CountRoute_FilterDisabled_WithFilter_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOffProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllOffWidgets/$count?$filter=Name eq 'Alpha'");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UnsupportedQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CountRoute_FilterEnabled_WithFilter_ReturnsFilteredCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOnProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllOnWidgets/$count?$filter=Name eq 'Alpha'");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1", await response.Content.ReadAsStringAsync());
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
    public async Task MaxTop_WithoutTop_AddsNextLink()
    {
        // When results are capped a @odata.nextLink is added to signal more pages exist.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MaxTopProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/MaxTopWidgets");
        Assert.True(json.TryGetProperty("@odata.nextLink", out _));
    }

    // ── Property allowlists: allowlisted → 200, non-allowlisted → 400 ────────────

    [Fact]
    public async Task FilterProperties_AllowedProperty_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllowlistWidgets?$filter=Name eq 'Alpha'");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FilterProperties_DisallowedProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllowlistWidgets?$filter=Id eq 1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        string message = json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("Id", message);
        Assert.Contains("$filter", message);
    }

    [Fact]
    public async Task OrderByProperties_AllowedProperty_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllowlistWidgets?$orderby=Id");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OrderByProperties_DisallowedProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllowlistWidgets?$orderby=Name");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        string message = json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("Name", message);
        Assert.Contains("$orderby", message);
    }

    [Fact]
    public async Task SelectProperties_AllowedProperty_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllowlistWidgets?$select=Name");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SelectProperties_DisallowedProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllowlistWidgets?$select=Id");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        string message = json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("Id", message);
        Assert.Contains("$select", message);
    }

    [Fact]
    public async Task ExpandProperties_AllowedProperty_Returns200AndExpands()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ExpandAllowlistProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ExpandAllowlistParents?$expand=Children");
        JsonElement first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("children", out JsonElement children));
        Assert.Equal(1, children.GetArrayLength());
    }

    [Fact]
    public async Task ExpandProperties_DisallowedProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ExpandAllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/ExpandAllowlistParents?$expand=Secrets");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        string message = json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("Secrets", message);
    }

    // ── NEW-1 regression: nav-path $filter/$orderby must not be blocked by the B1 ──
    // ── model-bound-validator plumbing when no allowlist is configured, and must ──
    // ── stay unaffected by a ROOT allowlist that IS configured (root allowlists ──
    // ── have no bearing on nav-target types, which have no allowlist surface of ──
    // ── their own). See OhDataBuilder.MarkNavigationTargetTypesFullyQueryable.  ──

    [Fact]
    public async Task Filter_NavPathAny_NoAllowlist_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavPathFilterProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync(
            "/odata/NavPathFilterParents?$filter=Tags/any(t: t/Name eq 'Red')");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement values = json.GetProperty("value");
        Assert.Equal(1, values.GetArrayLength());
        Assert.Equal("Foo", values[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Filter_NavPathAll_NoAllowlist_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavPathFilterProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync(
            "/odata/NavPathFilterParents?$filter=Tags/all(t: t/Name ne 'Blue')");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Only "Foo" (Tags = [Red]) satisfies "every tag's Name != Blue"; "Bar" (Tags = [Blue]) does not.
        Assert.Equal(1, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task Filter_NavPathAny_OrdersLinesShape_NoAllowlist_Returns200()
    {
        // Mirrors the TestBench's Orders/Lines shape exactly (collection nav, numeric
        // comparison) -- the precise repro reported against the TestBench for NEW-1.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavPathOrderProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync(
            "/odata/NavPathOrders?$filter=Lines/any(l: l/Quantity gt 1)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement values = json.GetProperty("value");
        Assert.Equal(1, values.GetArrayLength());
        Assert.Equal("Alice", values[0].GetProperty("customerName").GetString());
    }

    [Fact]
    public async Task Filter_NavPathAny_WithRootAllowlist_Returns200()
    {
        // The root's FilterProperties(Name) allowlist must not leak onto the Tag type reached
        // through the Tags navigation -- Tag has no allowlist semantics of its own in 1.0.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavPathAllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync(
            "/odata/NavPathAllowlistParents?$filter=Tags/any(t: t/Name eq 'Red')");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Filter_RootAllowlist_NonAllowlistedProperty_StillReturns400()
    {
        // Security-property regression guard for B1: making nav-target types fully permissive
        // must NOT weaken the root allowlist itself -- a non-allowlisted root property is still
        // rejected even though Tags/any(...) on the very same entity set is now allowed.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavPathAllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/NavPathAllowlistParents?$filter=Id eq 1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        string message = json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("Id", message);
        Assert.Contains("$filter", message);
    }

    [Fact]
    public async Task OrderBy_NavPathSingleValued_NoAllowlist_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavPathOrderByProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/NavPathOrderByParents?$orderby=Category/Name");
        JsonElement values = json.GetProperty("value");
        Assert.Equal(2, values.GetArrayLength());
        Assert.Equal("Bar", values[0].GetProperty("name").GetString());  // Category.Name "Alpha" < "Zeta"
        Assert.Equal("Foo", values[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task OrderBy_NavPathSingleValued_WithRootAllowlist_Returns200()
    {
        // Same non-leak guarantee as the $filter case above, for $orderby's own allowlist.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavPathOrderByAllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync(
            "/odata/NavPathOrderByAllowlistParents?$orderby=Category/Name");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OrderBy_RootAllowlist_NonAllowlistedProperty_StillReturns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavPathOrderByAllowlistProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/NavPathOrderByAllowlistParents?$orderby=Id");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        string message = json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("Id", message);
        Assert.Contains("$orderby", message);
    }

    // ── Navigation route: $filter and other unimplemented options → 400 ──────────

    [Theory]
    [InlineData("$filter=Name eq 'Child1'", "$filter")]
    [InlineData("$expand=Nested", "$expand")]
    [InlineData("$search=foo", "$search")]
    public async Task NavRoute_UnsupportedOption_Returns400(string query, string optionName)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavEnforcementProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync($"/odata/NavEnforcementParents(1)/Children?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement error = json.GetProperty("error");
        Assert.Equal("UnsupportedQueryOption", error.GetProperty("code").GetString());
        Assert.Contains(optionName, error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task NavRoute_SupportedOptions_StillWork()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavEnforcementProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/NavEnforcementParents(1)/Children?$orderby=Name desc&$top=1&$count=true&$select=Name");
        Assert.Equal(2, json.GetProperty("@odata.count").GetInt64());
        JsonElement values = json.GetProperty("value");
        Assert.Equal(1, values.GetArrayLength());
        Assert.Equal("Child2", values[0].GetProperty("name").GetString());
        Assert.False(values[0].TryGetProperty("id", out _));
    }

    // ── GetById: $select/$expand flags enforced; $expand implemented ─────────────

    [Fact]
    public async Task GetById_SelectDisabled_WithSelect_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOffProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllOffWidgets(1)?$select=Name");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UnsupportedQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetById_ExpandDisabled_WithExpand_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AllOffProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/AllOffWidgets(1)?$expand=Children");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UnsupportedQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetById_ExpandEnabled_ExpandsNavigationProperty()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavEnforcementProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/NavEnforcementParents(1)?$expand=Children");
        Assert.Equal(1, json.GetProperty("id").GetInt32());
        Assert.True(json.TryGetProperty("children", out JsonElement children),
            "$expand=Children on GetById must inline the navigation property");
        Assert.Equal(2, children.GetArrayLength());
        // Context must be the single-entity shape.
        Assert.EndsWith("$metadata#NavEnforcementParents/$entity", json.GetProperty("@odata.context").GetString());
        Assert.True(json.TryGetProperty("@odata.id", out _));
    }

    [Fact]
    public async Task GetById_ExpandEnabled_WithSelect_ProjectsAndExpands()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavEnforcementProfile>());
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/NavEnforcementParents(1)?$select=Name&$expand=Children");
        Assert.True(json.TryGetProperty("name", out _));
        Assert.False(json.TryGetProperty("id", out _), "Id was not selected and must be stripped");
        Assert.True(json.TryGetProperty("children", out JsonElement children));
        Assert.Equal(2, children.GetArrayLength());
        // Projected single-entity context (JSON §10.8) includes the expanded nav property.
        Assert.EndsWith("$metadata#NavEnforcementParents(Name,Children)/$entity", json.GetProperty("@odata.context").GetString());
    }

    [Fact]
    public async Task GetById_ExpandEnabled_UnknownNavProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NavEnforcementProfile>());
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/NavEnforcementParents(1)?$expand=Nope");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task GetById_ExpandEnabled_BatchHandler_IsUsed()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<BatchNavEnforcementProfile>());
        BatchNavEnforcementProfile.BatchCalls = 0;
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BatchNavParents(1)?$expand=Children");
        Assert.True(json.TryGetProperty("children", out JsonElement children));
        Assert.Equal(2, children.GetArrayLength());
        Assert.Equal(1, BatchNavEnforcementProfile.BatchCalls);
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
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }

    /// <summary>GetQueryable profile relying on the framework defaults: every flag false.</summary>
    private class AllOffProfile : EntitySetProfile<int, Widget>
    {
        private static readonly List<Widget> Store = new()
        {
            new() { Id = 1, Name = "Alpha" },
            new() { Id = 2, Name = "Beta" },
        };
        public AllOffProfile() : base(x => x.Id)
        {
            EntitySetName = "AllOffWidgets";
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(w => w.Id == id));
        }
    }

    /// <summary>Priority-1 profile (direct ODataQueryOptions handler) with every flag false.</summary>
    private class AllOffODataProfile : ODataEntitySetProfile<int, Widget>
    {
        private static readonly List<Widget> Store = new()
        {
            new() { Id = 1, Name = "Alpha" },
            new() { Id = 2, Name = "Beta" },
        };
        public AllOffODataProfile() : base(x => x.Id)
        {
            EntitySetName = "AllOffODataWidgets";
            GetODataQueryable = (options, ct) =>
                Task.FromResult(ODataQueryResult<Widget>.FromQueryable(Store.AsQueryable()));
        }
    }

    /// <summary>GetAll profile with every flag false — $select/$expand/$count must 400.</summary>
    private class AllOffGetAllProfile : EntitySetProfile<int, Widget>
    {
        private static readonly List<Widget> Store = new()
        {
            new() { Id = 1, Name = "Alpha" },
            new() { Id = 2, Name = "Beta" },
        };
        public AllOffGetAllProfile() : base(x => x.Id)
        {
            EntitySetName = "AllOffGetAllWidgets";
            GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(Store);
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
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }

    /// <summary>
    /// One profile carrying all three structural-property allowlists, deliberately disjoint so
    /// each option has both an allowed and a disallowed property:
    /// $filter → Name only, $orderby → Id only, $select → Name only.
    /// </summary>
    private class AllowlistProfile : EntitySetProfile<int, Widget>
    {
        private static readonly List<Widget> Store = new()
        {
            new() { Id = 1, Name = "Alpha" },
            new() { Id = 2, Name = "Beta" },
        };
        public AllowlistProfile() : base(x => x.Id)
        {
            EntitySetName = "AllowlistWidgets";
            FilterEnabled = true;
            OrderByEnabled = true;
            SelectEnabled = true;
            FilterProperties(x => x.Name);
            OrderByProperties(x => x.Id);
            SelectProperties(x => x.Name);
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }

    private class ExpandAllowlistParent
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public IEnumerable<ExpandAllowlistChild>? Children { get; set; }
        public IEnumerable<ExpandAllowlistChild>? Secrets { get; set; }
    }

    private class ExpandAllowlistChild
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class ExpandAllowlistProfile : EntitySetProfile<int, ExpandAllowlistParent>
    {
        private static readonly List<ExpandAllowlistParent> Parents = new()
        {
            new() { Id = 1, Name = "Parent" },
        };
        public ExpandAllowlistProfile() : base(x => x.Id)
        {
            EntitySetName = "ExpandAllowlistParents";
            ExpandEnabled = true;
            ExpandProperties(x => x.Children);
            GetQueryable = (ct) => Task.FromResult(Parents.AsQueryable());
            HasMany(x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<ExpandAllowlistChild>>(
                    new[] { new ExpandAllowlistChild { Id = 10, Name = "Child" } }));
            HasMany(x => x.Secrets!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<ExpandAllowlistChild>>(
                    new[] { new ExpandAllowlistChild { Id = 99, Name = "Secret" } }));
        }
    }

    private class NavEnforcementParent
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public IEnumerable<NavEnforcementChild>? Children { get; set; }
    }

    private class NavEnforcementChild
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class NavEnforcementProfile : EntitySetProfile<int, NavEnforcementParent>
    {
        private static readonly List<NavEnforcementParent> Parents = new()
        {
            new() { Id = 1, Name = "Parent" },
        };
        private static readonly List<NavEnforcementChild> Children = new()
        {
            new() { Id = 1, Name = "Child1" },
            new() { Id = 2, Name = "Child2" },
        };
        public NavEnforcementProfile() : base(x => x.Id)
        {
            EntitySetName = "NavEnforcementParents";
            SelectEnabled = true;
            ExpandEnabled = true;
            GetQueryable = (ct) => Task.FromResult(Parents.AsQueryable());
            GetById = (id, ct) => Task.FromResult(Parents.FirstOrDefault(p => p.Id == id));
            HasMany(x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<NavEnforcementChild>>(Children));
        }
    }

    private class BatchNavEnforcementProfile : EntitySetProfile<int, NavEnforcementParent>
    {
        public static int BatchCalls;

        private static readonly List<NavEnforcementParent> Parents = new()
        {
            new() { Id = 1, Name = "Parent" },
        };
        private static readonly List<NavEnforcementChild> Children = new()
        {
            new() { Id = 1, Name = "Child1" },
            new() { Id = 2, Name = "Child2" },
        };
        public BatchNavEnforcementProfile() : base(x => x.Id)
        {
            EntitySetName = "BatchNavParents";
            ExpandEnabled = true;
            GetQueryable = (ct) => Task.FromResult(Parents.AsQueryable());
            GetById = (id, ct) => Task.FromResult(Parents.FirstOrDefault(p => p.Id == id));
            HasMany(x => x.Children!,
                batchGetAll: (parentIds, ct) =>
                {
                    Interlocked.Increment(ref BatchCalls);
                    return Task.FromResult(Children.ToLookup(_ => parentIds[0]));
                });
        }
    }

    // ── NEW-1 fixtures: nav-path $filter/$orderby regression coverage ────────────

    private class NavPathTag
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class NavPathParent
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<NavPathTag> Tags { get; set; } = new();
    }

    /// <summary>Plain Parent/Tags fixture mirroring the Zorks/Tags repro from the evidence log,
    /// FilterEnabled with deliberately no FilterProperties/OrderByProperties allowlist.</summary>
    private class NavPathFilterProfile : EntitySetProfile<int, NavPathParent>
    {
        private static readonly List<NavPathParent> Store = new()
        {
            new() { Id = 1, Name = "Foo", Tags = new() { new() { Id = 1, Name = "Red" } } },
            new() { Id = 2, Name = "Bar", Tags = new() { new() { Id = 2, Name = "Blue" } } },
        };
        public NavPathFilterProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPathFilterParents";
            FilterEnabled = true;
            OrderByEnabled = true;
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            HasMany(x => x.Tags);
        }
    }

    /// <summary>Same shape, but with a root FilterProperties allowlist configured -- the nav
    /// path through Tags must remain unaffected by it.</summary>
    private class NavPathAllowlistProfile : EntitySetProfile<int, NavPathParent>
    {
        private static readonly List<NavPathParent> Store = new()
        {
            new() { Id = 1, Name = "Foo", Tags = new() { new() { Id = 1, Name = "Red" } } },
            new() { Id = 2, Name = "Bar", Tags = new() { new() { Id = 2, Name = "Blue" } } },
        };
        public NavPathAllowlistProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPathAllowlistParents";
            FilterEnabled = true;
            FilterProperties(x => x.Name);
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            HasMany(x => x.Tags);
        }
    }

    private class NavPathOrderLine
    {
        public int Id { get; set; }
        public int Quantity { get; set; }
    }

    private class NavPathOrder
    {
        public int Id { get; set; }
        public string CustomerName { get; set; } = "";
        public List<NavPathOrderLine> Lines { get; set; } = new();
    }

    /// <summary>Mirrors the TestBench's Orders/Lines entity shape exactly (collection nav +
    /// numeric comparison) -- the second repro reported for NEW-1.</summary>
    private class NavPathOrderProfile : EntitySetProfile<int, NavPathOrder>
    {
        private static readonly List<NavPathOrder> Store = new()
        {
            new() { Id = 1, CustomerName = "Alice", Lines = new() { new() { Id = 1, Quantity = 2 } } },
            new() { Id = 2, CustomerName = "Bob", Lines = new() { new() { Id = 2, Quantity = 1 } } },
        };
        public NavPathOrderProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPathOrders";
            FilterEnabled = true;
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            HasMany(x => x.Lines);
        }
    }

    private class NavPathCategory
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class NavPathOrderByParent
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public NavPathCategory? Category { get; set; }
    }

    /// <summary>Single-valued nav (as opposed to the collection navs above) for the $orderby
    /// nav-path case: `$orderby=Category/Name`, no allowlist configured.</summary>
    private class NavPathOrderByProfile : EntitySetProfile<int, NavPathOrderByParent>
    {
        private static readonly List<NavPathOrderByParent> Store = new()
        {
            new() { Id = 1, Name = "Foo", Category = new() { Id = 1, Name = "Zeta" } },
            new() { Id = 2, Name = "Bar", Category = new() { Id = 2, Name = "Alpha" } },
        };
        public NavPathOrderByProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPathOrderByParents";
            OrderByEnabled = true;
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            HasOptional(x => x.Category!);
        }
    }

    /// <summary>Same shape, but with a root OrderByProperties allowlist configured -- the nav
    /// path through Category must remain unaffected by it.</summary>
    private class NavPathOrderByAllowlistProfile : EntitySetProfile<int, NavPathOrderByParent>
    {
        private static readonly List<NavPathOrderByParent> Store = new()
        {
            new() { Id = 1, Name = "Foo", Category = new() { Id = 1, Name = "Zeta" } },
            new() { Id = 2, Name = "Bar", Category = new() { Id = 2, Name = "Alpha" } },
        };
        public NavPathOrderByAllowlistProfile() : base(x => x.Id)
        {
            EntitySetName = "NavPathOrderByAllowlistParents";
            OrderByEnabled = true;
            OrderByProperties(x => x.Name);
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            HasOptional(x => x.Category!);
        }
    }
}
