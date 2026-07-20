using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Composition coverage: $filter + $orderby + $top + $skip + $count used together,
/// multi-key $orderby with nulls, $select combined with $filter, and $expand combined
/// with $select and $filter — on the GetQueryable path. Also covers edge semantics
/// ($top=0, $skip beyond collection size) and the camelCase casing contract under
/// combined query options.
/// </summary>
public class QueryOptionCompositionTests
{
    private const string Url = "/odata/QueryOptionItems";
    private const string ExpandUrl = "/odata/QueryOptionExpandParents";

    private static async Task<TestFixture> BuildAsync() =>
        await TestHostBuilder.BuildAsync(o => o
            .AddEntitySetProfile<QueryOptionProfile>()
            .AddEntitySetProfile<QueryOptionExpandProfile>());

    private static async Task<int[]> GetOrderedIdsAsync(HttpClient client, string query)
    {
        JsonElement json = await client.GetFromJsonAsync<JsonElement>(query);
        return json.GetProperty("value").EnumerateArray()
            .Select(e => e.GetProperty("id").GetInt32())
            .ToArray();
    }

    // ── 7a. $filter + $orderby + $top + $skip + $count together ────────────────

    [Fact]
    public async Task Composition_FilterOrderByTopSkipCount_PagesCorrectly()
    {
        await using TestFixture fx = await BuildAsync();

        List<QueryOptionItem> filtered = QueryOptionData.Items
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.Price)
            .ToList();
        int[] expectedPage = filtered.Skip(2).Take(3).Select(x => x.Id).ToArray();
        long expectedCount = filtered.Count;

        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>(
            $"{Url}?$filter=IsActive eq true&$orderby=Price desc&$skip=2&$top=3&$count=true");

        int[] actualPage = json.GetProperty("value").EnumerateArray()
            .Select(e => e.GetProperty("id").GetInt32()).ToArray();

        Assert.Equal(expectedPage, actualPage); // exact order, exact rows
        Assert.Equal(expectedCount, json.GetProperty("@odata.count").GetInt64());
        Assert.True(expectedPage.Length > 0, "fixture must yield a non-empty page for this test to be meaningful");
    }

    // ── 7b. $orderby multi-key, mixed asc/desc ──────────────────────────────────

    [Fact]
    public async Task Composition_OrderBy_MultiKey_MixedAscDesc_MatchesExpectedOrder()
    {
        await using TestFixture fx = await BuildAsync();

        int[] expected = QueryOptionData.Items
            .OrderBy(x => x.Category)      // asc; nulls sort first under default comparer
            .ThenByDescending(x => x.Price) // desc
            .Select(x => x.Id).ToArray();

        int[] actual = await GetOrderedIdsAsync(fx.Client, $"{Url}?$orderby=Category asc,Price desc&$top=50");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Composition_OrderBy_WithNulls_Ascending_NullsSortFirst()
    {
        await using TestFixture fx = await BuildAsync();

        int[] expected = QueryOptionData.Items
            .OrderBy(x => x.Rank)
            .Select(x => x.Id).ToArray();

        int[] actual = await GetOrderedIdsAsync(fx.Client, $"{Url}?$orderby=Rank asc&$top=50");

        Assert.Equal(expected, actual);
        // Sanity: fixture actually has null Rank rows, otherwise this test proves nothing.
        Assert.Contains(QueryOptionData.Items, x => x.Rank is null);
        Assert.True(QueryOptionData.Items.First(x => x.Id == actual[0]).Rank is null,
            "expected a null-Rank row to sort first in ascending order");
    }

    [Fact]
    public async Task Composition_OrderBy_WithNulls_Descending_NullsSortLast()
    {
        await using TestFixture fx = await BuildAsync();

        int[] expected = QueryOptionData.Items
            .OrderByDescending(x => x.Rank)
            .Select(x => x.Id).ToArray();

        int[] actual = await GetOrderedIdsAsync(fx.Client, $"{Url}?$orderby=Rank desc&$top=50");

        Assert.Equal(expected, actual);
        Assert.True(QueryOptionData.Items.First(x => x.Id == actual[^1]).Rank is null,
            "expected a null-Rank row to sort last in descending order");
    }

    // ── 7c. $select combined with $filter — selected-away property still filters ─

    [Fact]
    public async Task Composition_Select_CombinedWithFilter_SelectedAwayPropertyStillFilters()
    {
        await using TestFixture fx = await BuildAsync();

        int[] expected = QueryOptionData.Items
            .Where(x => x.IsActive)
            .Select(x => x.Id).ToArray();

        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>(
            $"{Url}?$filter=IsActive eq true&$select=Id,Name&$top=50");

        JsonElement[] values = json.GetProperty("value").EnumerateArray().ToArray();
        int[] actual = values.Select(e => e.GetProperty("id").GetInt32()).ToArray();

        Assert.Equal(expected.OrderBy(x => x), actual.OrderBy(x => x));
        Assert.NotEmpty(actual);

        // IsActive was used to filter but was not selected, so it must be absent from the body.
        foreach (JsonElement item in values)
        {
            Assert.False(item.TryGetProperty("isActive", out _));
            Assert.True(item.TryGetProperty("id", out _));
            Assert.True(item.TryGetProperty("name", out _));
        }
    }

    // ── 7d. $expand combined with $select and $filter ───────────────────────────

    [Fact]
    public async Task Composition_Expand_CombinedWithSelectAndFilter()
    {
        await using TestFixture fx = await BuildAsync();

        // Only "Parent Alpha" (Id 1) and "Parent Gamma" (Id 3) are active; "Parent Beta" (Id 2,
        // inactive) is filtered out even though it has a child ("Child2a").
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>(
            $"{ExpandUrl}?$filter=IsActive eq true&$select=Id,Name&$expand=Children");

        JsonElement[] values = json.GetProperty("value").EnumerateArray().ToArray();
        int[] ids = values.Select(e => e.GetProperty("id").GetInt32()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 1, 3 }, ids);

        JsonElement parentAlpha = values.First(e => e.GetProperty("id").GetInt32() == 1);
        Assert.True(parentAlpha.TryGetProperty("children", out JsonElement childrenAlpha),
            "$expand=Children must be honored even though 'Children' was not in $select");
        Assert.Equal(2, childrenAlpha.GetArrayLength());
        Assert.False(parentAlpha.TryGetProperty("isActive", out _), "IsActive was not selected and must be absent");

        JsonElement parentGamma = values.First(e => e.GetProperty("id").GetInt32() == 3);
        Assert.True(parentGamma.TryGetProperty("children", out JsonElement childrenGamma));
        Assert.Equal(0, childrenGamma.GetArrayLength()); // Parent Gamma has no children in the fixture
    }

    // ── 8. Edge semantics ────────────────────────────────────────────────────────

    [Fact]
    public async Task Edge_TopZero_ReturnsEmptyValueArray()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage response = await fx.Client.GetAsync($"{Url}?$top=0&$count=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("value").GetArrayLength());
        // $count reflects the total matching (unfiltered) set, independent of $top.
        Assert.Equal((long)QueryOptionData.Items.Count, json.GetProperty("@odata.count").GetInt64());
    }

    [Fact]
    public async Task Edge_SkipBeyondCollectionSize_ReturnsEmptyValueArray()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage response = await fx.Client.GetAsync($"{Url}?$skip=1000&$count=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("value").GetArrayLength());
        Assert.Equal((long)QueryOptionData.Items.Count, json.GetProperty("@odata.count").GetInt64());
    }

    [Fact]
    public async Task Edge_Filter_UnsupportedFunction_Returns400WithODataErrorBody()
    {
        // `unknownfunc(...)` is not part of the OData canonical function set, so the
        // ODataQueryOptions constructor should reject it with a 400 and an OData error
        // envelope rather than a 500.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage response = await fx.Client.GetAsync($"{Url}?$filter=unknownfunc(Name,'x')");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _), "expected an OData error envelope, not a raw 500");
    }

    // ── 9. Casing contract under composition ─────────────────────────────────────

    [Fact]
    public async Task Composition_FilterOrderBySelect_ResponseBody_IsCamelCase()
    {
        await using TestFixture fx = await BuildAsync();
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>(
            $"{Url}?$filter=Quantity gt 0&$orderby=Price desc&$select=Id,Name,Price,CreatedUtc&$top=5");
        foreach (JsonElement item in json.GetProperty("value").EnumerateArray())
        {
            foreach (JsonProperty prop in item.EnumerateObject())
            {
                Assert.False(char.IsUpper(prop.Name[0]), $"property '{prop.Name}' leaked PascalCase casing");
            }
            Assert.True(item.TryGetProperty("createdUtc", out _));
        }
    }

    [Fact]
    public async Task Composition_ExpandSelectFilter_ResponseBody_IsCamelCase()
    {
        await using TestFixture fx = await BuildAsync();
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>(
            $"{ExpandUrl}?$filter=IsActive eq true&$select=Id,Name&$expand=Children");
        foreach (JsonElement item in json.GetProperty("value").EnumerateArray())
        {
            foreach (JsonProperty prop in item.EnumerateObject())
            {
                Assert.False(char.IsUpper(prop.Name[0]), $"property '{prop.Name}' leaked PascalCase casing");
            }
            if (item.TryGetProperty("children", out JsonElement children) && children.GetArrayLength() > 0)
            {
                foreach (JsonProperty childProp in children[0].EnumerateObject())
                {
                    Assert.False(char.IsUpper(childProp.Name[0]), $"child property '{childProp.Name}' leaked PascalCase casing");
                }
            }
        }
    }
}
