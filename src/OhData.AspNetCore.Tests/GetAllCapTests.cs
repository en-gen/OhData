using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #201: the <c>GetAll</c> (simple/<c>IEnumerable</c>) read path is now safe-by-default — an omitted
/// <c>$top</c> is capped to <c>MaxTop</c> (or a smaller <c>Prefer: maxpagesize</c>) with a <c>$skip</c>
/// <c>@odata.nextLink</c> for the remainder, so it can no longer be coerced into returning an unbounded
/// result set. Opt out with <c>MaxTop = null</c>. An explicit <c>$top</c> suppresses the default cap.
/// </summary>
public class GetAllCapTests
{
    private const string Capped = "/odata/GetAllCapWidgets";     // 25 items, MaxTop = 10
    private const string Unbounded = "/odata/GetAllUnboundedWidgets"; // 25 items, MaxTop = null

    private static async Task<JsonElement> GetJsonAsync(TestFixture fx, string url)
        => await fx.Client.GetFromJsonAsync<JsonElement>(url);

    [Fact]
    public async Task NoTop_CapsToMaxTop_EmitsNextLink()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GetAllCapProfile>());
        var json = await GetJsonAsync(fx, Capped);

        Assert.Equal(10, json.GetProperty("value").GetArrayLength());
        Assert.True(json.TryGetProperty("@odata.nextLink", out var nl));
        Assert.Contains("skip=", nl.GetString());
    }

    [Fact]
    public async Task NextLink_Continuation_WalksWholeCollection()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GetAllCapProfile>());

        var seen = new List<int>();
        string? relative = Capped;
        int guard = 0;
        while (relative is not null && guard++ < 10)
        {
            var json = await GetJsonAsync(fx, relative);
            foreach (var el in json.GetProperty("value").EnumerateArray())
                seen.Add(el.GetProperty("id").GetInt32());
            relative = json.TryGetProperty("@odata.nextLink", out var nl)
                ? new Uri(nl.GetString()!).PathAndQuery
                : null;
        }

        Assert.Equal(Enumerable.Range(1, 25), seen.OrderBy(i => i));
        Assert.Equal(25, seen.Distinct().Count());
    }

    [Fact]
    public async Task ExplicitTop_SuppressesDefaultCap_NoNextLink()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GetAllCapProfile>());
        var json = await GetJsonAsync(fx, $"{Capped}?$top=5");

        Assert.Equal(5, json.GetProperty("value").GetArrayLength());
        Assert.False(json.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task Count_ReflectsPrePagingTotal_NotPageSize()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GetAllCapProfile>());
        var json = await GetJsonAsync(fx, $"{Capped}?$count=true");

        Assert.Equal(10, json.GetProperty("value").GetArrayLength());
        Assert.Equal(25L, json.GetProperty("@odata.count").GetInt64());
    }

    [Fact]
    public async Task PreferMaxPageSize_ClampsBelowMaxTop_AndEchoes()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GetAllCapProfile>());
        using var request = new HttpRequestMessage(HttpMethod.Get, Capped);
        request.Headers.Add("Prefer", "maxpagesize=3");
        var resp = await fx.Client.SendAsync(request);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, json.GetProperty("value").GetArrayLength());
        Assert.True(json.TryGetProperty("@odata.nextLink", out _));
        Assert.Contains(resp.Headers.GetValues("Preference-Applied"), v => v.Contains("maxpagesize=3"));
    }

    [Fact]
    public async Task MaxTopNull_OptsOut_ReturnsFullSet_NoNextLink()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GetAllUnboundedProfile>());
        var json = await GetJsonAsync(fx, Unbounded);

        // MaxTop = null is the explicit opt-out: the whole set comes back with no continuation link.
        Assert.Equal(25, json.GetProperty("value").GetArrayLength());
        Assert.False(json.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task SmallSet_UnderMaxTop_Unchanged_NoNextLink()
    {
        // WidgetProfile has 2 items and the default MaxTop (1000); the page isn't full so no
        // continuation link is emitted — existing small-collection behavior is preserved.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var json = await GetJsonAsync(fx, "/odata/Widgets");

        Assert.Equal(2, json.GetProperty("value").GetArrayLength());
        Assert.False(json.TryGetProperty("@odata.nextLink", out _));
    }
}

internal class GetAllCapProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> Store = Enumerable.Range(1, 25)
        .Select(i => new Widget { Id = i, Name = $"Widget{i}" }).ToList();

    public GetAllCapProfile() : base(x => x.Id)
    {
        EntitySetName = "GetAllCapWidgets";
        CountEnabled = true;
        MaxTop = 10;
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(Store);
    }
}

internal class GetAllUnboundedProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> Store = Enumerable.Range(1, 25)
        .Select(i => new Widget { Id = i, Name = $"Widget{i}" }).ToList();

    public GetAllUnboundedProfile() : base(x => x.Id)
    {
        EntitySetName = "GetAllUnboundedWidgets";
        CountEnabled = true;
        MaxTop = null; // #201 opt-out: return the full set, no cap, no nextLink.
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(Store);
    }
}
