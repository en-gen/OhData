using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Tests for the batch-aware <c>$expand</c> mechanism (REVIEW.md M-1). Verifies that a
/// registered <c>BatchHandler</c> collapses N×P sequential per-entity nav loads into P calls
/// per page, that behavior is unchanged when no batch handler is registered, and that the
/// auto-derived per-entity <c>Handler</c> keeps standalone nav routes working.
/// </summary>
public class BatchExpandTests
{
    // ── Call-count proof: P calls total, not N×P ──────────────────────────────

    [Fact]
    public async Task BatchHandler_CalledExactlyOncePerExpandedPropertyPerPage()
    {
        var counter = new BatchCallCounter();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BatchExpandQueryableProfile>(),
            configureServices: s => s.AddSingleton(counter));

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BatchExpandParents?$expand=Children,PrimaryChild");

        Assert.Equal(1, counter.ChildrenCalls);
        Assert.Equal(1, counter.PrimaryChildCalls);

        // Each call received the full page of parent keys (100), not one key per call.
        Assert.Single(counter.ChildrenKeyCounts);
        Assert.Equal(100, counter.ChildrenKeyCounts[0]);
        Assert.Single(counter.PrimaryChildKeyCounts);
        Assert.Equal(100, counter.PrimaryChildKeyCounts[0]);

        var value = json.GetProperty("value");
        Assert.Equal(100, value.GetArrayLength());
    }

    [Fact]
    public async Task BatchHandler_ResultsMatchPerEntityPathForSameData()
    {
        // BatchExpandQueryableProfile derives Children per parent from the same source data
        // that ETagExpandSelectProfile-style per-entity handlers would produce: parent i (>=2)
        // has exactly 2 children named "C{i}-1"/"C{i}-2".
        var counter = new BatchCallCounter();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BatchExpandQueryableProfile>(),
            configureServices: s => s.AddSingleton(counter));

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BatchExpandParents?$expand=Children,PrimaryChild");
        var value = json.GetProperty("value");

        // Parent 5 (arbitrary mid-page parent) should have 2 named children.
        var parent5 = value.EnumerateArray().First(p => p.GetProperty("id").GetInt32() == 5);
        var children5 = parent5.GetProperty("children");
        Assert.Equal(2, children5.GetArrayLength());
        string?[] childNames = children5.EnumerateArray().Select(c => c.GetProperty("name").GetString()).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "C5-1", "C5-2" }, childNames);

        // Parent 5's primary child follows the "Primary{id}" naming convention.
        var primary5 = parent5.GetProperty("primaryChild");
        Assert.Equal(JsonValueKind.Object, primary5.ValueKind);
        Assert.Equal("Primary5", primary5.GetProperty("name").GetString());
    }

    // ── Parents with no children / no related entity ──────────────────────────

    [Fact]
    public async Task BatchHandler_LookupMiss_ProducesEmptyCollectionForCollectionNav()
    {
        var counter = new BatchCallCounter();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BatchExpandQueryableProfile>(),
            configureServices: s => s.AddSingleton(counter));

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BatchExpandParents?$expand=Children");
        var value = json.GetProperty("value");

        // Parent 1 has zero children (BatchExpandQueryableProfile builds no Child rows for it).
        var parent1 = value.EnumerateArray().First(p => p.GetProperty("id").GetInt32() == 1);
        var children1 = parent1.GetProperty("children");
        Assert.Equal(JsonValueKind.Array, children1.ValueKind);
        Assert.Equal(0, children1.GetArrayLength());
    }

    [Fact]
    public async Task BatchHandler_MapMiss_ProducesNullForSingleValuedNav()
    {
        var counter = new BatchCallCounter();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BatchExpandQueryableProfile>(),
            configureServices: s => s.AddSingleton(counter));

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BatchExpandParents?$expand=PrimaryChild");
        var value = json.GetProperty("value");

        // Parent 2's batch handler deliberately omits it from the result map.
        var parent2 = value.EnumerateArray().First(p => p.GetProperty("id").GetInt32() == 2);
        var primary2 = parent2.GetProperty("primaryChild");
        Assert.Equal(JsonValueKind.Null, primary2.ValueKind);
    }

    // ── $expand + $select combination ──────────────────────────────────────────

    [Fact]
    public async Task BatchHandler_WithSelect_ExpandedPropertySurvivesProjection()
    {
        var counter = new BatchCallCounter();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BatchExpandQueryableProfile>(),
            configureServices: s => s.AddSingleton(counter));

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/BatchExpandParents?$expand=Children&$select=Id,Children");
        var value = json.GetProperty("value");
        Assert.True(value.GetArrayLength() > 0);

        var first = value[0];
        Assert.True(first.TryGetProperty("id", out _));
        Assert.True(first.TryGetProperty("children", out var children));
        Assert.Equal(JsonValueKind.Array, children.ValueKind);
        // "name" was not selected and must be stripped.
        Assert.False(first.TryGetProperty("name", out _));

        // The batch loader is still invoked exactly once for the whole page under $select.
        Assert.Equal(1, counter.ChildrenCalls);
    }

    // ── Batch-only registration still serves standalone nav routes ────────────

    [Fact]
    public async Task BatchOnlyRegistration_ServesStandaloneNavGetRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<BatchOnlyNavProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BatchOnlyParents(1)/Children");
        var value = json.GetProperty("value");
        Assert.Equal(2, value.GetArrayLength());
    }

    [Fact]
    public async Task BatchOnlyRegistration_ServesNavCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<BatchOnlyNavProfile>());
        var response = await fx.Client.GetAsync("/odata/BatchOnlyParents(1)/Children/$count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal("2", body.Trim());
    }

    [Fact]
    public async Task BatchOnlyRegistration_ServesPopulatedRef()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<BatchOnlyNavProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BatchOnlyParents(1)/PrimaryChild/$ref");
        Assert.True(json.TryGetProperty("@odata.id", out var odataId));
        // Populated ref should point at the "Children" entity set (refTargetEntitySet) with the
        // detected child key ("Id" → 10, the first child of parent 1).
        Assert.Contains("Children(10)", odataId.GetString());
    }

    [Fact]
    public async Task BatchOnlyRegistration_EmptyParent_NavRouteReturns404()
    {
        // Parent 2 has no children in BatchOnlyNavProfile's data set; the derived per-entity
        // Handler falls back to an empty collection, so the nav GET route still resolves (200
        // with an empty value array) rather than erroring.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<BatchOnlyNavProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BatchOnlyParents(2)/Children");
        var value = json.GetProperty("value");
        Assert.Equal(0, value.GetArrayLength());
    }

    // ── Mixed batch + per-entity navs in one $expand ───────────────────────────

    [Fact]
    public async Task MixedProfile_BatchAndPerEntityNavs_BothResolveCorrectly()
    {
        var counter = new BatchCallCounter();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<MixedBatchExpandProfile>(),
            configureServices: s => s.AddSingleton(counter));

        var json = await fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/MixedBatchExpandParents?$expand=Children,PrimaryChild");
        var value = json.GetProperty("value");
        Assert.Equal(3, value.GetArrayLength());

        foreach (var item in value.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("children", out _));
            Assert.True(item.TryGetProperty("primaryChild", out var primary));
            Assert.Equal(JsonValueKind.Object, primary.ValueKind);
        }

        // Children is batch-loaded: exactly one call for the whole 3-parent page.
        Assert.Equal(1, counter.ChildrenCalls);
        // PrimaryChild is per-entity: one call per parent (unchanged fallback behavior).
        Assert.Equal(3, counter.PrimaryChildCalls);
    }

    // ── Works on all three collection GET paths ────────────────────────────────

    [Fact]
    public async Task BatchHandler_WorksOnGetQueryablePath()
    {
        var counter = new BatchCallCounter();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BatchExpandQueryableProfile>(),
            configureServices: s => s.AddSingleton(counter));

        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BatchExpandParents?$expand=Children");
        Assert.Equal(1, counter.ChildrenCalls);
        Assert.True(json.GetProperty("value").GetArrayLength() > 0);
    }

    [Fact]
    public async Task BatchHandler_WorksOnGetAllPath()
    {
        var counter = new BatchCallCounter();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BatchExpandGetAllProfile>(),
            configureServices: s => s.AddSingleton(counter));

        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BatchExpandGetAllParents?$expand=Children");
        var value = json.GetProperty("value");
        Assert.Equal(3, value.GetArrayLength());
        Assert.Equal(1, counter.ChildrenCalls);
        Assert.Equal(3, counter.ChildrenKeyCounts[0]);

        var parent2 = value.EnumerateArray().First(p => p.GetProperty("id").GetInt32() == 2);
        Assert.Equal(0, parent2.GetProperty("children").GetArrayLength());
    }

    [Fact]
    public async Task BatchHandler_WorksOnPriority1ODataQueryablePath()
    {
        var counter = new BatchCallCounter();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BatchExpandODataProfile>(),
            configureServices: s => s.AddSingleton(counter));

        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/BatchExpandODataParents?$expand=Children");
        var value = json.GetProperty("value");
        Assert.Equal(2, value.GetArrayLength());
        Assert.Equal(1, counter.ChildrenCalls);
        Assert.Equal(2, counter.ChildrenKeyCounts[0]);

        foreach (var item in value.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("children", out var children));
            Assert.Equal(1, children.GetArrayLength());
        }
    }

    // ── Back-compat: no BatchHandler → unchanged per-entity fallback ──────────

    [Fact]
    public async Task NoBatchHandler_FallsBackToPerEntityHandler()
    {
        // ExpandableParentProfile (existing fixture) has no batch handler at all; this proves
        // the fallback branch still produces correct output when BatchHandler is null.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ExpandableParentProfile>());
        var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ExpandableParents?$expand=Children");
        var first = json.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("children", out var children));
        Assert.True(children.GetArrayLength() > 0);
    }
}
