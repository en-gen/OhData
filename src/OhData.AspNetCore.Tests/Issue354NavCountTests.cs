using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #354 - on GET /{Set}({key})/{Nav} the @odata.count is the full related set (before $skip/$top,
/// §11.2.6.5) and an invalid $count value is a 400 exactly as on the top-level collection route.
/// </summary>
public sealed class Issue354NavCountTests
{
    private const string Nav = "/odata/Nav354Parents(1)/Children";

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static int[] Ids(JsonElement json) =>
        json.GetProperty("value").EnumerateArray().Select(e => e.GetProperty("Id").GetInt32()).ToArray();

    [Fact]
    public async Task CountWithSkip_ReportsTheFullRelatedCount()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Nav354Profile>());

        var json = await OkAsync(await fx.Client.GetAsync($"{Nav}?$count=true&$skip=3"));

        Assert.Equal(6, json.GetProperty("@odata.count").GetInt64());
        Assert.Equal(new[] { 4, 5, 6 }, Ids(json));
    }

    [Fact]
    public async Task CountWithTop_ReportsTheFullRelatedCountWithTopItems()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Nav354Profile>());

        var json = await OkAsync(await fx.Client.GetAsync($"{Nav}?$count=true&$top=2"));

        Assert.Equal(6, json.GetProperty("@odata.count").GetInt64());
        Assert.Equal(2, Ids(json).Length);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    public async Task CountValue_GetsTheSameStatusAsTheCollectionRoute_AndABadOneNeverReachesTheHandler(string value)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Nav354Profile>());
        Nav354Profile.NavInvocations = 0;

        var top = await fx.Client.GetAsync($"/odata/Nav354Parents?$count={value}");
        Nav354Profile.NavInvocations = 0;
        var nav = await fx.Client.GetAsync($"{Nav}?$count={value}");

        Assert.Equal(top.StatusCode, nav.StatusCode);
        if (nav.StatusCode == HttpStatusCode.BadRequest)
        {
            Assert.Equal(0, Nav354Profile.NavInvocations);
            var error = (await nav.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
            Assert.Equal("InvalidQueryOption", error.GetProperty("code").GetString());
        }
        else
        {
            Assert.Equal(1, Nav354Profile.NavInvocations);
        }
    }

    [Theory]
    [InlineData("?$count=false")]
    [InlineData("")]
    public async Task NoCountRequested_EmitsNoCountMember(string query)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Nav354Profile>());

        var json = await OkAsync(await fx.Client.GetAsync($"{Nav}{query}"));

        Assert.False(json.TryGetProperty("@odata.count", out _));
        Assert.Equal(6, Ids(json).Length);
    }

    [Fact]
    public async Task OrderBySkipAndCount_AreComposedCorrectly()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<Nav354Profile>());

        var json = await OkAsync(await fx.Client.GetAsync($"{Nav}?$orderby=Id desc&$skip=4&$count=true"));

        Assert.Equal(6, json.GetProperty("@odata.count").GetInt64());
        Assert.Equal(new[] { 2, 1 }, Ids(json));
    }
}

internal sealed class Nav354Profile : EntitySetProfile<int, Parent>
{
    internal static int NavInvocations;

    private static readonly List<Parent> _parents = new() { new() { Id = 1, Name = "P1" } };
    private static readonly List<Child> _children =
        Enumerable.Range(1, 6).Select(i => new Child { Id = i, ParentId = 1, Name = "C" + i }).ToList();

    public Nav354Profile() : base(x => x.Id)
    {
        EntitySetName = "Nav354Parents";
        CountEnabled = true;
        GetQueryable = () => _parents.AsQueryable();
        GetById = (id, ct) => OhDataResult.Success(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(x => x.Children!,
            getAll: (parentId, ct) =>
            {
                Interlocked.Increment(ref NavInvocations);
                return Task.FromResult<IEnumerable<Child>>(_children.Where(c => c.ParentId == parentId).ToList());
            });
    }
}
