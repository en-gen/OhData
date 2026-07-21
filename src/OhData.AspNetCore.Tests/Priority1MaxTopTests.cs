using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Query;
using OhData.Abstractions.AspNetCore.OData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #195: the Priority-1 (<c>ODataEntitySetProfile</c> / <c>GetODataQueryable</c>) read path must
/// enforce <c>MaxTop</c> like the Priority-2 path — reject an oversized <c>$top</c>, cap an omitted
/// <c>$top</c> to <c>MaxTop</c> (or a smaller <c>Prefer: maxpagesize</c>), and emit a continuation
/// <c>@odata.nextLink</c> — so a profile can never be coerced into returning an unbounded result set.
/// </summary>
public class Priority1MaxTopTests
{
    private const string Url = "/odata/MaxTopWidgets";

    private static async Task<JsonElement> GetJsonAsync(TestFixture fx, string relativeUrl)
        => await fx.Client.GetFromJsonAsync<JsonElement>(relativeUrl);

    [Fact]
    public async Task NoTop_CapsToMaxTop_AndEmitsNextLink()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Priority1MaxTopProfile>());
        var json = await GetJsonAsync(fx, Url);

        // 25 items in the store, MaxTop = 10 → first page is exactly 10, with a continuation link.
        Assert.Equal(10, json.GetProperty("value").GetArrayLength());
        Assert.True(json.TryGetProperty("@odata.nextLink", out var nextLink));
        // Priority-1 continuation uses $skip (which the profile's ApplyTo honors), not $skiptoken.
        // The link is URL-encoded, so $ appears as %24.
        Assert.Contains("skip=", nextLink.GetString());
    }

    [Fact]
    public async Task TopExceedsMaxTop_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Priority1MaxTopProfile>());
        var resp = await fx.Client.GetAsync($"{Url}?$top=50");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task TopWithinMaxTop_Honored_NoFrameworkNextLink()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Priority1MaxTopProfile>());
        var json = await GetJsonAsync(fx, $"{Url}?$top=5");

        // Client capped explicitly below MaxTop; the framework must not add a continuation link.
        Assert.Equal(5, json.GetProperty("value").GetArrayLength());
        Assert.False(json.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task NextLink_Continuation_WalksTheWholeCollection()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Priority1MaxTopProfile>());

        var seen = new List<int>();
        string? relative = Url;
        int guard = 0;
        while (relative is not null && guard++ < 10)
        {
            var json = await GetJsonAsync(fx, relative);
            foreach (var el in json.GetProperty("value").EnumerateArray())
                seen.Add(el.GetProperty("Id").GetInt32());

            relative = json.TryGetProperty("@odata.nextLink", out var nl)
                ? new Uri(nl.GetString()!).PathAndQuery
                : null;
        }

        // Pages of 10, 10, 5 → every id 1..25 exactly once, no duplicates, no gaps.
        Assert.Equal(Enumerable.Range(1, 25), seen.OrderBy(i => i));
        Assert.Equal(25, seen.Distinct().Count());
    }

    [Fact]
    public async Task PreferMaxPageSize_BelowMaxTop_Clamps_AndEchoesPreferenceApplied()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Priority1MaxTopProfile>());
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("Prefer", "maxpagesize=3");
        var resp = await fx.Client.SendAsync(request);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, json.GetProperty("value").GetArrayLength());
        Assert.True(json.TryGetProperty("@odata.nextLink", out _));
        Assert.Contains(resp.Headers.GetValues("Preference-Applied"), v => v.Contains("maxpagesize=3"));
    }

    [Fact]
    public async Task PreferMaxPageSize_AboveMaxTop_ClampedToMaxTop()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Priority1MaxTopProfile>());
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("Prefer", "maxpagesize=100");
        var resp = await fx.Client.SendAsync(request);

        // maxpagesize must not lift the server's MaxTop ceiling.
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, json.GetProperty("value").GetArrayLength());
        Assert.Contains(resp.Headers.GetValues("Preference-Applied"), v => v.Contains("maxpagesize=10"));
    }

    [Fact]
    public async Task ProfileSuppliedNextLink_IsNotOverriddenByFrameworkCap()
    {
        // A profile that pages itself and returns its own NextLink is trusted — the framework
        // must not re-cap or replace the link.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ODataQueryNextLinkProfile>());
        var json = await GetJsonAsync(fx, "/odata/ODataNextLinkWidgets");
        Assert.Equal("http://next", json.GetProperty("@odata.nextLink").GetString());
    }
}

/// <summary>
/// Priority-1 profile with 25 items and <c>MaxTop = 10</c> that applies the incoming
/// <see cref="ODataQueryOptions{TModel}"/> itself (SQL-pushdown-style), leaving <c>MaxTop</c>
/// enforcement and continuation entirely to the framework (#195).
/// </summary>
internal class Priority1MaxTopProfile : ODataEntitySetProfile<int, Widget>
{
    private static readonly List<Widget> Store = Enumerable.Range(1, 25)
        .Select(i => new Widget { Id = i, Name = $"Widget{i}" }).ToList();

    public Priority1MaxTopProfile() : base(x => x.Id)
    {
        EntitySetName = "MaxTopWidgets";
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        MaxTop = 10;

        GetODataQueryable = (options, ct) =>
        {
            // Profile applies whatever standard options it was handed ($filter/$orderby/$skip/$top).
            // It deliberately does NOT cap by MaxTop or emit a nextLink — that is the framework's job.
            var applied = options.ApplyTo(Store.AsQueryable()) as IQueryable<Widget>
                          ?? Store.AsQueryable();
            return Task.FromResult(new ODataQueryResult<Widget> { Items = applied });
        };
    }
}
