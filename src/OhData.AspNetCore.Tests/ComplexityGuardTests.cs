using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using OhData.Abstractions;
using OhData.Abstractions.AspNetCore.OData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #202: tunable query-complexity guards. <c>MaxExpansionDepth</c> (default 12) is now enforced —
/// a <c>$expand</c> nesting deeper than the limit is rejected with 400 rather than silently
/// truncated — and the <c>$filter</c>/<c>$orderby</c> node-count ceilings are configurable per
/// profile (or globally via <c>WithDefaults</c>).
/// </summary>
public class ComplexityGuardTests
{
    private const string Url = "/odata/Nodes";

    [Fact]
    public async Task Expand_WithinDepthLimit_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NodeProfile>());
        // MaxExpansionDepth = 2 → two levels of nesting is allowed.
        var resp = await fx.Client.GetAsync($"{Url}?$expand=Children($expand=Children)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Expand_ExceedsDepthLimit_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NodeProfile>());
        // Three levels exceeds MaxExpansionDepth = 2.
        var resp = await fx.Client.GetAsync($"{Url}?$expand=Children($expand=Children($expand=Children))");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Filter_ExceedsNodeCount_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NodeProfile>());
        // MaxFilterNodeCount = 5; a compound and/or filter has more nodes than that.
        var resp = await fx.Client.GetAsync($"{Url}?$filter=Id eq 1 and Id eq 2 and Id eq 3");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Filter_WithinNodeCount_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<NodeProfile>());
        var resp = await fx.Client.GetAsync($"{Url}?$filter=Id eq 1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxExpansionDepth_NonPositive_Throws(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BadExpansionDepthProfile(value));

    [Fact]
    public void MaxFilterNodeCount_NonPositive_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntitySetDefaults { MaxFilterNodeCount = 0 });
}

internal class TreeNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<TreeNode>? Children { get; set; }
}

internal class NodeProfile : EntitySetProfile<int, TreeNode>
{
    private static readonly List<TreeNode> Store = new() { new() { Id = 1, Name = "root" } };

    public NodeProfile() : base(x => x.Id)
    {
        EntitySetName = "Nodes";
        FilterEnabled = true;
        OrderByEnabled = true;
        ExpandEnabled = true;
        MaxExpansionDepth = 2;
        MaxFilterNodeCount = 5;

        GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(n => n.Id == id));
        HasMany(
            navigation: x => x.Children!,
            getAll: (id, ct) => Task.FromResult<IEnumerable<TreeNode>>(Array.Empty<TreeNode>()));
    }
}

internal class BadExpansionDepthProfile : EntitySetProfile<int, TreeNode>
{
    public BadExpansionDepthProfile(int v) : base(x => x.Id) { MaxExpansionDepth = v; }
}
