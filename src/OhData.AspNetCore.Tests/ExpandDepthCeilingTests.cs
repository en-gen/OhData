using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #328: MaxExpansionDepth now has a hard ceiling, validated at CONFIGURATION time on both entry
// points (EntitySetDefaults and EntitySetProfile) rather than at request time.
//
// The ceiling exists because relational query translation for a pushed nested projection is
// O(3^depth): EF Core re-translates each nested-collection subtree three times with no memoization,
// so every extra level triples the CPU spent building the query before a single row is read.
// Measured on a 16-node chain returning ~6 KB, one navigation per level, no database round trip
// required: depth 6 = 0.24 s, depth 8 = 3.8 s, depth 10 = 32 s, depth 12 = 291 s of single-core CPU
// for one unauthenticated request.
//
// The value is 6 and not the default of 3 because the blow-up is at 10+, not at 5. Depth 5 costs
// ~90 ms, and this project's own docs/query-options.md and two of its own test fixtures already use
// MaxExpansionDepth = 5 — capping at 3 would invalidate a documented example for a shape that is
// not expensive.
public class ExpandDepthCeilingTests
{
    [Fact]
    public void Ceiling_IsSixAndIsPublic() =>
        Assert.Equal(6, EntitySetDefaults.MaxExpansionDepthCeiling);

    [Theory]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(int.MaxValue)]
    public void Defaults_AboveCeiling_Throws(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new EntitySetDefaults { MaxExpansionDepth = value });
        Assert.Equal(value, ex.ActualValue);
        AssertMessageExplainsWhy(ex.Message, value);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(int.MaxValue)]
    public void Profile_AboveCeiling_Throws(int value)
    {
        // A profile-level value never passes through the EntitySetDefaults setter, so the two
        // entry points are validated independently. This assertion is what catches a fix applied
        // to only one of them.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new CeilingProbeProfile(value));
        Assert.Equal(value, ex.ActualValue);
        AssertMessageExplainsWhy(ex.Message, value);
    }

    [Fact]
    public void Defaults_AtCeiling_IsAccepted() =>
        Assert.Equal(
            EntitySetDefaults.MaxExpansionDepthCeiling,
            new EntitySetDefaults { MaxExpansionDepth = EntitySetDefaults.MaxExpansionDepthCeiling }.MaxExpansionDepth);

    [Fact]
    public void Profile_AtCeiling_IsAccepted()
    {
        Exception? boom = Record.Exception(
            () => new CeilingProbeProfile(EntitySetDefaults.MaxExpansionDepthCeiling));
        Assert.Null(boom);
    }

    // The documented example in docs/query-options.md, and two shipped test fixtures, use 5. The
    // ceiling must not invalidate them — that is the entire reason it is 6 and not 3.
    [Fact]
    public void Five_TheValueThisProjectsOwnDocsAndTestsUse_StaysLegal()
    {
        Assert.Equal(5, new EntitySetDefaults { MaxExpansionDepth = 5 }.MaxExpansionDepth);
        Assert.Null(Record.Exception(() => new CeilingProbeProfile(5)));
    }

    // A ceiling that only fired on the setter would still let a too-deep request through if the
    // runtime cap had drifted, so the boundary is also pinned end-to-end: at MaxExpansionDepth = 6
    // a six-level $expand is served and a seven-level one is 400.
    [Fact]
    public async Task AtCeiling_SixLevelsServed_SevenLevelsRejected()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<CeilingChainProfile>());

        HttpResponseMessage ok = await fx.Client.GetAsync($"/odata/CeilingChain?$expand={Chain(6)}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage tooDeep = await fx.Client.GetAsync($"/odata/CeilingChain?$expand={Chain(7)}");
        Assert.Equal(HttpStatusCode.BadRequest, tooDeep.StatusCode);
    }

    private static string Chain(int depth) =>
        depth == 1 ? "Children" : $"Children($expand={Chain(depth - 1)})";

    // The message must say WHY, not just what: an implementor who configured 15 did so on purpose
    // and is owed the reason it is refused, or the obvious reading is "arbitrary new limit".
    private static void AssertMessageExplainsWhy(string message, int requested)
    {
        Assert.Contains(requested.ToString(System.Globalization.CultureInfo.InvariantCulture), message);
        Assert.Contains("O(3^depth)", message);
        Assert.Contains("291 s", message);
        Assert.Contains("re-translates", message);
    }
}

internal sealed class CeilingProbeProfile : EntitySetProfile<int, TreeNode>
{
    public CeilingProbeProfile(int depth) : base(x => x.Id)
    {
        EntitySetName = "CeilingProbe";
        MaxExpansionDepth = depth;
    }
}

internal sealed class CeilingChainProfile : EntitySetProfile<int, TreeNode>
{
    private static readonly List<TreeNode> Store = new() { new TreeNode { Id = 1, Name = "root" } };

    public CeilingChainProfile() : base(x => x.Id)
    {
        EntitySetName = "CeilingChain";
        ExpandEnabled = true;
        MaxExpansionDepth = EntitySetDefaults.MaxExpansionDepthCeiling;
        GetQueryable = ct => OhDataResult.SuccessTask(Store.AsQueryable());
        HasMany(
            navigation: x => x.Children!,
            getAll: (id, ct) => Task.FromResult<IEnumerable<TreeNode>>(Array.Empty<TreeNode>()));
    }
}
