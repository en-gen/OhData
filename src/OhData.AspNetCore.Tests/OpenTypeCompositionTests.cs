using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #389 integration risk 1 & 2. Two TypeInfoResolver modifiers now run against the same
// JsonTypeInfo chain: the nav-suppression modifier REMOVES every EDM navigation before
// System.Text.Json can walk it (that removal is what makes a reference cycle structurally
// unreachable — #325/#326), and the open-type modifier MUTATES a member into extension data.
// WithAddedModifier composes them, but "composes" has to be demonstrated on a type that exercises
// both at once, because a silent failure (a lost expansion, or a bag that quietly went back to
// being nested) would look like ordinary output and pass every other suite in the repo.
//
// The models here deliberately put the open complex type on the SAME entity that carries the
// navigation, and the navigation TARGET carries one too.

public sealed class CompTag
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public string Label { get; set; } = "";
    public ExternalReferenceMetadata? Meta { get; set; }
}

public sealed class CompOwner
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
    public ExternalReferenceMetadata? Meta { get; set; }
    public List<CompTag> Tags { get; set; } = new();
}

/// <summary>Self-referential so the cycle-safety invariant is exercised, not just asserted about.</summary>
public sealed class CompNode
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public CompNode? Parent { get; set; }
    public List<CompNode> Children { get; set; } = new();
    public ExternalReferenceMetadata? Meta { get; set; }
}

internal sealed class CompStore
{
    internal List<CompOwner> Owners { get; }
    internal List<CompNode> Nodes { get; }

    public CompStore()
    {
        var tag = new CompTag
        {
            Id = 10,
            OwnerId = 1,
            Label = "t1",
            Meta = new ExternalReferenceMetadata
            {
                KeyValuePairs = new Dictionary<string, object?> { ["tagDynamic"] = "tv" },
            },
        };
        Owners = new List<CompOwner>
        {
            new()
            {
                Id = 1,
                Name = "o1",
                Secret = "hidden",
                Meta = new ExternalReferenceMetadata
                {
                    KeyValuePairs = new Dictionary<string, object?> { ["ownerDynamic"] = "ov" },
                },
                Tags = new List<CompTag> { tag },
            },
        };

        // Bidirectional, fully wired: root <-> leaf. Handing this graph to System.Text.Json
        // unbounded throws on the cycle, so a 200 here IS the proof the walker never did.
        var root = new CompNode
        {
            Id = 1,
            Meta = new ExternalReferenceMetadata
            {
                KeyValuePairs = new Dictionary<string, object?> { ["nodeDynamic"] = "nv" },
            },
        };
        var leaf = new CompNode { Id = 2, ParentId = 1, Parent = root };
        root.Children.Add(leaf);
        Nodes = new List<CompNode> { root, leaf };
    }
}

internal sealed class CompOwnerProfile : EntitySetProfile<int, CompOwner>
{
    public CompOwnerProfile(CompStore store) : base(x => x.Id)
    {
        EntitySetName = "CompOwners";
        ExpandEnabled = true;
        SelectEnabled = true;
        // Third modifier in the chain (#226 Ignore()): proves the open-type modifier composes with
        // the ignored-property one too, not only with nav suppression.
        Ignore(x => x.Secret);
        GetAll = ct => Task.FromResult<IEnumerable<CompOwner>>(store.Owners);
        GetById = (id, ct) => Task.FromResult(store.Owners.FirstOrDefault(o => o.Id == id));
        HasMany(x => x.Tags, getAll: (ownerId, ct) => Task.FromResult<IEnumerable<CompTag>>(
            store.Owners.Where(o => o.Id == ownerId).SelectMany(o => o.Tags).ToList()));
    }
}

internal sealed class CompNodeProfile : EntitySetProfile<int, CompNode>
{
    public CompNodeProfile(CompStore store) : base(x => x.Id)
    {
        EntitySetName = "CompNodes";
        ExpandEnabled = true;
        GetAll = ct => Task.FromResult<IEnumerable<CompNode>>(store.Nodes);
        HasOptional(x => x.Parent!);
        HasMany(x => x.Children);
    }
}

public class OpenTypeCompositionTests
{
    private static async Task<TestFixture> BuildAsync() =>
        await TestHostBuilder.BuildAsync(
            o =>
            {
                o.AddEntitySetProfile<CompOwnerProfile>();
                o.AddEntitySetProfile<CompNodeProfile>();
            },
            configureServices: s => s.AddSingleton<CompStore>());

    [Fact]
    public async Task ExpandedNavigationAndOpenComplexTypeBothHoldAtOnce()
    {
        await using TestFixture fx = await BuildAsync();
        string body = await fx.Client.GetStringAsync("/odata/CompOwners?$expand=Tags");

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement owner = doc.RootElement.GetProperty("value")[0];

        // (a) nav-suppression modifier: the navigation was removed from the contract and spliced
        //     back only because the clause asked for it.
        JsonElement tags = owner.GetProperty("Tags");
        Assert.Equal(1, tags.GetArrayLength());
        Assert.Equal("t1", tags[0].GetProperty("Label").GetString());

        // (b) open-type modifier on the PARENT entity's complex property...
        JsonElement ownerMeta = owner.GetProperty("Meta");
        Assert.Equal("ov", ownerMeta.GetProperty("ownerDynamic").GetString());
        Assert.False(ownerMeta.TryGetProperty("KeyValuePairs", out _));

        // (c) ...and on the SPLICED navigation target's, which is serialized by the recursive
        //     SerializeBounded call, i.e. a different call site than (b).
        JsonElement tagMeta = tags[0].GetProperty("Meta");
        Assert.Equal("tv", tagMeta.GetProperty("tagDynamic").GetString());
        Assert.False(tagMeta.TryGetProperty("KeyValuePairs", out _));

        // (d) ignored-property modifier still wins.
        Assert.False(owner.TryGetProperty("Secret", out _));
    }

    [Fact]
    public async Task UnexpandedNavigationIsStillOmittedWhileTheBagStaysFlat()
    {
        await using TestFixture fx = await BuildAsync();
        string body = await fx.Client.GetStringAsync("/odata/CompOwners");

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement owner = doc.RootElement.GetProperty("value")[0];
        Assert.False(owner.TryGetProperty("Tags", out _));
        Assert.Equal("ov", owner.GetProperty("Meta").GetProperty("ownerDynamic").GetString());
    }

    [Fact]
    public async Task CyclicGraphStillSerializes_ClauseBoundedInvariantIntact()
    {
        await using TestFixture fx = await BuildAsync();

        // No $expand: the walker must not touch Parent/Children at all. Unbounded serialization of
        // this graph throws (cycle), so a 200 is the assertion.
        string plain = await fx.Client.GetStringAsync("/odata/CompNodes");
        using (JsonDocument doc = JsonDocument.Parse(plain))
        {
            JsonElement root = doc.RootElement.GetProperty("value")[0];
            Assert.False(root.TryGetProperty("Parent", out _));
            Assert.False(root.TryGetProperty("Children", out _));
            Assert.Equal("nv", root.GetProperty("Meta").GetProperty("nodeDynamic").GetString());
        }

        // One level of $expand: exactly one level appears, and the child's own navigations do not.
        string expanded = await fx.Client.GetStringAsync("/odata/CompNodes?$expand=Children");
        using (JsonDocument doc = JsonDocument.Parse(expanded))
        {
            JsonElement root = doc.RootElement.GetProperty("value")[0];
            JsonElement children = root.GetProperty("Children");
            Assert.Equal(1, children.GetArrayLength());
            Assert.False(children[0].TryGetProperty("Parent", out _));
            Assert.False(children[0].TryGetProperty("Children", out _));
            Assert.Equal("nv", root.GetProperty("Meta").GetProperty("nodeDynamic").GetString());
        }
    }

    [Fact]
    public async Task NavigationRoute_TargetOpenComplexTypeIsFlat()
    {
        await using TestFixture fx = await BuildAsync();
        string body = await fx.Client.GetStringAsync("/odata/CompOwners(1)/Tags");

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement meta = doc.RootElement.GetProperty("value")[0].GetProperty("Meta");
        Assert.Equal("tv", meta.GetProperty("tagDynamic").GetString());
        Assert.False(meta.TryGetProperty("KeyValuePairs", out _));
    }
}
