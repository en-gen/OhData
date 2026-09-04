using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #337 follow-up (adversarial-review regression): batching a nested collection hands System.Text.Json
// an `object`-declared element, which is exactly what triggers STJ's POLYMORPHIC RE-ENTRY — and that
// path writes the type discriminator of the nearest [JsonPolymorphic] ancestor. The per-element
// SerializeToNode(element, element.GetType(), ...) call it replaced passed declared type == runtime
// type and wrote no discriminator, so batching leaked a `"$kind"` key into every $expand'ed
// polymorphic collection.
//
// That is not cosmetic:
//   * "$kind" is an arbitrary STJ key in an OData payload — it is NOT the OData "@odata.type"
//     control information.
//   * StripToSelectedProperties/KeepUnderSelect only preserve keys containing '@', so the key
//     appeared or vanished depending on whether $select was present.
//   * The standalone navigation route still serializes per element, so it disagreed with $expand on
//     the very same navigation.
//
// The repo had NO [JsonPolymorphic] fixture at all, which is why the byte-identity matrix could not
// catch this. This suite is that missing fixture: it pins "no discriminator leaks into $expand" for
// both the ServeRaw splice and the delegate-injection splice, and pins the routes agreeing.

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(NpKidA), "a")]
[JsonDerivedType(typeof(NpKidB), "b")]
public class NpKid
{
    public int Id { get; set; }
    public string Tag { get; set; } = "";
}

public sealed class NpKidA : NpKid { public string AVal { get; set; } = ""; }

public sealed class NpKidB : NpKid { public int BVal { get; set; } }

public class NpRoot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<NpKid> Kids { get; set; } = new();
}

internal static class NpData
{
    public static List<NpKid> Kids() => new()
    {
        new NpKidA { Id = 1, Tag = "t1", AVal = "a1" },
        new NpKidB { Id = 2, Tag = "t2", BVal = 9 },
    };

    public static List<NpRoot> Roots() => new()
    {
        new NpRoot { Id = 1, Name = "R1", Kids = Kids() },
        new NpRoot { Id = 2, Name = "R2" },
    };
}

// ServeRaw: no delegate, so $expand is answered from the CLR graph via SpliceKeptNavigations.
public sealed class NpRawRootProfile : EntitySetProfile<int, NpRoot>
{
    private static readonly List<NpRoot> Store = NpData.Roots();

    public NpRawRootProfile() : base(x => x.Id)
    {
        EntitySetName = "NpRawRoots";
        ExpandEnabled = true; SelectEnabled = true; OrderByEnabled = true;
        GetQueryable = _ => Store.AsQueryable();
        GetById = (id, _) => OhDataResult.Success(Store.FirstOrDefault(r => r.Id == id));
        HasMany(x => x.Kids!);
    }
}

// Delegate-backed: $expand goes through ExpandLevelAsync's delegate-injection splice
// (isCollectionValue: true), and the standalone GET /NpDelRoots({key})/Kids route is registered.
public sealed class NpDelRootProfile : EntitySetProfile<int, NpRoot>
{
    private static readonly List<NpRoot> Store = NpData.Roots();

    public NpDelRootProfile() : base(x => x.Id)
    {
        EntitySetName = "NpDelRoots";
        ExpandEnabled = true; SelectEnabled = true; OrderByEnabled = true;
        GetQueryable = _ => Store.AsQueryable();
        GetById = (id, _) => OhDataResult.Success(Store.FirstOrDefault(r => r.Id == id));
        HasMany(x => x.Kids!,
            getAll: (id, _) => Task.FromResult<IEnumerable<NpKid>>(id == 1 ? NpData.Kids() : new List<NpKid>()));
    }
}

// Root-level entity set OVER the polymorphic type — characterization only, see the test.
public sealed class NpKidRootProfile : EntitySetProfile<int, NpKid>
{
    private static readonly List<NpKid> Store = NpData.Kids();

    public NpKidRootProfile() : base(x => x.Id)
    {
        EntitySetName = "NpKidRoots";
        ExpandEnabled = true; SelectEnabled = true; OrderByEnabled = true;
        GetQueryable = _ => Store.AsQueryable();
        GetById = (id, _) => OhDataResult.Success(Store.FirstOrDefault(k => k.Id == id));
    }
}

public sealed class PolymorphicExpandSerializationTests
{
    private static Task<TestFixture> RawAsync() =>
        TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<NpRawRootProfile>());

    private static Task<TestFixture> DelAsync() =>
        TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<NpDelRootProfile>());

    private static async Task<string> BodyAsync(TestFixture fx, string url)
    {
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadAsStringAsync();
    }

    private static void AssertNoDiscriminatorButDerivedMembersPresent(string body)
    {
        // The leak this guards against.
        Assert.DoesNotContain("$kind", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"a\"", body, StringComparison.Ordinal); // the "a" discriminator value
        // ...without regressing the derived-member dispatch #337 exists to preserve.
        Assert.Contains("\"AVal\":\"a1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"BVal\":9", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expand_ServeRawNav_WritesNoTypeDiscriminator()
    {
        await using TestFixture fx = await RawAsync();
        AssertNoDiscriminatorButDerivedMembersPresent(
            await BodyAsync(fx, "/odata/NpRawRoots?$orderby=Id&$expand=Kids"));
    }

    [Fact]
    public async Task ExpandOnGetById_ServeRawNav_WritesNoTypeDiscriminator()
    {
        await using TestFixture fx = await RawAsync();
        AssertNoDiscriminatorButDerivedMembersPresent(
            await BodyAsync(fx, "/odata/NpRawRoots(1)?$expand=Kids"));
    }

    [Fact]
    public async Task Expand_DelegateBackedNav_WritesNoTypeDiscriminator()
    {
        await using TestFixture fx = await DelAsync();
        AssertNoDiscriminatorButDerivedMembersPresent(
            await BodyAsync(fx, "/odata/NpDelRoots?$orderby=Id&$expand=Kids"));
        AssertNoDiscriminatorButDerivedMembersPresent(
            await BodyAsync(fx, "/odata/NpDelRoots(1)?$expand=Kids"));
    }

    [Fact]
    public async Task StandaloneNavRoute_And_Expand_Agree()
    {
        await using TestFixture fx = await DelAsync();
        string navRoute = await BodyAsync(fx, "/odata/NpDelRoots(1)/Kids");
        string expand = await BodyAsync(fx, "/odata/NpDelRoots(1)?$expand=Kids");

        AssertNoDiscriminatorButDerivedMembersPresent(navRoute);
        AssertNoDiscriminatorButDerivedMembersPresent(expand);

        // The two routes must serialize the SAME entities the same way — this is the disagreement
        // the regression introduced (nav route silent, $expand emitting "$kind").
        Assert.Contains("\"AVal\":\"a1\"", navRoute, StringComparison.Ordinal);
        Assert.Contains("\"AVal\":\"a1\"", expand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expand_WithSelect_StaysStableAcrossQueryOptions()
    {
        await using TestFixture fx = await RawAsync();
        // A discriminator would be stripped here (KeepUnderSelect only preserves '@' keys) but not
        // above — i.e. it would appear or vanish based on an unrelated query option.
        string body = await BodyAsync(fx, "/odata/NpRawRoots?$orderby=Id&$select=Name&$expand=Kids");
        Assert.DoesNotContain("$kind", body, StringComparison.Ordinal);
    }

    // CHARACTERIZATION, not a design assertion. A ROOT-level collection was already batched before
    // #337, so develop emits the discriminator here and #337 deliberately does NOT change that:
    // byte-identity with develop is the acceptance criterion, and suppressing it at the root would
    // be a second output change rather than a fix. This test exists so the known inconsistency
    // (root emits, $expand/nav-route/single-entity do not) is recorded and cannot drift silently.
    [Fact]
    public async Task RootLevelCollection_StillEmitsDiscriminator_MatchesDevelop()
    {
        await using TestFixture fx =
            await TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<NpKidRootProfile>());
        string body = await BodyAsync(fx, "/odata/NpKidRoots?$orderby=Id");
        Assert.Contains("$kind", body, StringComparison.Ordinal);
    }
}
