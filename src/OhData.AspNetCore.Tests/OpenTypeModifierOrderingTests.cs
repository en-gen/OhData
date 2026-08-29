using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #398 stage 0, test T1.3 — <b>the modifier-ordering tripwire.</b>
/// </summary>
/// <remarks>
/// <para>
/// <c>OhDataEndpointFactory.MapAll</c> composes three <c>TypeInfoResolver</c> modifiers, in this
/// order and no other:
/// </para>
/// <code>
/// startupJsonOptions
///   -> IgnoredPropertyJsonOptions.Build   (REMOVES Ignore()d members)
///   -> OpenTypeJsonOptions.Build          (MARKS the container as extension data)
///   -> CreateNavSuppressionState          (REMOVES unrequested navigations, per request)
/// </code>
/// <para>
/// Until now that order was an <b>emergent property of two call sites about forty lines apart</b>,
/// not an asserted invariant — and the whole delegate-safety argument for open types rests on it.
/// The open-type modifier snapshots its declared-name collision set from
/// <see cref="JsonTypeInfo.Properties"/> at the moment it runs, so:
/// </para>
/// <list type="bullet">
/// <item>
/// It must run <b>before</b> nav suppression, so every EDM navigation is still on the contract when
/// the snapshot is taken. A bag key spelled like a navigation is then a hard
/// <see cref="InvalidOperationException"/> rather than a key that silently occupies that
/// navigation's JSON name — which is the difference between a 500 and a
/// <i>navigation-shadowing leak</i> in which data reaches the wire under a navigation's name without
/// that navigation's delegate ever running.
/// </item>
/// <item>
/// It must run <b>after</b> the ignored-property modifier, whose removals are precisely what make
/// <c>Ignore()</c>d names invisible to it — which is why those names are threaded into
/// <c>OpenTypeJsonOptions.Build</c> as data instead of being read off the contract (#398 stage 1).
/// </item>
/// </list>
/// <para>
/// A refactor deriving nav suppression from <c>startupJsonOptions</c> rather than from the
/// open-type-modified options would silently undo both. These tests are the tripwire for it.
/// </para>
/// </remarks>
public class OpenTypeModifierOrderingTests
{
    // ── Half 1: the mechanism, in isolation ─────────────────────────────────────────────────────
    //
    // A pair, run against raw System.Text.Json with OpenTypeJsonOptions.Build in the middle. The two
    // differ ONLY in which side of the open-type modifier the removal sits on, so the pair isolates
    // the ordering itself rather than any surrounding behaviour. This is the executable form of the
    // argument above: the correct order hard-fails on a shadowing key, the flipped order does not.

    private static OpenTypeJsonOptions.OpenComplexTypeContainers Containers()
    {
        var builder = new ODataConventionModelBuilder();
        builder.EntitySet<OrdHost>("Hosts");
        IEdmModel model = builder.GetEdmModel();
        return OpenTypeJsonOptions.BuildOpenComplexTypeContainerMap(model);
    }

    private static IJsonTypeInfoResolver RemovingRegion() =>
        new DefaultJsonTypeInfoResolver().WithAddedModifier(t =>
        {
            if (t.Type != typeof(OrdBag)) return;
            for (int i = t.Properties.Count - 1; i >= 0; i--)
                if (t.Properties[i].Name == "Region") t.Properties.RemoveAt(i);
        });

    /// <summary>
    /// <b>The order MapAll uses.</b> The removal runs first, the open-type modifier snapshots the
    /// contract afterwards — so the removed name is <i>not</i> in its declared-name set and a bag key
    /// spelled like it is not caught by the collision rule. That is exactly the gap #398 stage 1
    /// closes by threading the withheld names in as data, and it is asserted here so the reason the
    /// threading exists cannot be forgotten.
    /// </summary>
    [Fact]
    public void RemovalBeforeTheOpenTypeModifier_LeavesTheRemovedNameOutOfTheCollisionSet()
    {
        var baseOptions = new JsonSerializerOptions { TypeInfoResolver = RemovingRegion() };
        JsonSerializerOptions options = OpenTypeJsonOptions.Build(
            baseOptions, Containers(), InheritedNameSets.Empty);

        // No throw: "Region" is gone from Properties, so the snapshot never saw it.
        string json = JsonSerializer.Serialize(
            new OrdBag { Kv = new Dictionary<string, object?> { ["Region"] = "shadow" } }, options);
        Assert.Contains("\"Region\":\"shadow\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The flipped order, for contrast: the open-type modifier snapshots the contract <i>first</i>,
    /// so the name is still declared when the set is built and the same bag key hard-fails. This is
    /// what makes the ordering load-bearing rather than incidental — the identical data gets
    /// opposite treatment depending on which side of the modifier the removal is on.
    /// </summary>
    [Fact]
    public void RemovalAfterTheOpenTypeModifier_KeepsTheNameInTheCollisionSet()
    {
        JsonSerializerOptions options = OpenTypeJsonOptions.Build(
            new JsonSerializerOptions(), Containers(), InheritedNameSets.Empty);
        var flipped = new JsonSerializerOptions(options)
        {
            TypeInfoResolver = options.TypeInfoResolver!.WithAddedModifier(t =>
            {
                if (t.Type != typeof(OrdBag)) return;
                for (int i = t.Properties.Count - 1; i >= 0; i--)
                    if (t.Properties[i].Name == "Region") t.Properties.RemoveAt(i);
            }),
        };

        Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(
                new OrdBag { Kv = new Dictionary<string, object?> { ["Region"] = "shadow" } },
                flipped));
    }

    /// <summary>
    /// A later modifier observes what an earlier one did — the property the whole chain's semantics
    /// rest on, pinned directly so a <c>System.Text.Json</c> change to composition semantics is
    /// caught here rather than as a mysterious failure three files away.
    /// </summary>
    [Fact]
    public void WithAddedModifier_RunsModifiersInOrderAndLaterOnesSeeEarlierMutations()
    {
        var seen = new List<string>();
        IJsonTypeInfoResolver resolver = new DefaultJsonTypeInfoResolver()
            .WithAddedModifier(t =>
            {
                if (t.Type != typeof(OrdBag)) return;
                for (int i = t.Properties.Count - 1; i >= 0; i--)
                    if (t.Properties[i].Name == "Region") t.Properties.RemoveAt(i);
            })
            .WithAddedModifier(t =>
            {
                if (t.Type != typeof(OrdBag)) return;
                seen.AddRange(t.Properties.Select(p => p.Name));
            });

        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        options.GetTypeInfo(typeof(OrdBag));

        Assert.DoesNotContain("Region", seen);
        Assert.Contains("Kv", seen);
    }

    // ── Half 2: the production chain, end to end ────────────────────────────────────────────────

    /// <summary>
    /// The real tripwire on <c>MapAll</c>. Deliberately built on the <b>existing <c>Ignore()</c>
    /// fixture</b> (<c>IgnProduct</c>, which has an <c>Ignore()</c>d primitive, an <c>Ignore()</c>d
    /// complex member, a delegate-backed navigation <b>and</b> an open complex member) rather than on
    /// a fresh model built around a bag — that green-field habit is exactly why #395's suite was
    /// green while shipping a critical defect.
    /// <para>
    /// All three modifiers are asserted <b>on one response</b>, which is what makes this an ordering
    /// test rather than three feature tests. The collection GET serializes through the
    /// <i>nav-suppressed</i> options, which are derived from the open-type-modified options, which
    /// are derived from the ignore-modified options. Rebase nav suppression onto
    /// <c>startupJsonOptions</c> and the flat-container assertion fails <i>and</i> the withheld
    /// members reappear — from one edit, in one test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheWholeChainSurvivesIntoTheNavSuppressedSerializationPath()
    {
        await using TestFixture fx = await IgnProductHost.BuildAsync();

        string body = await fx.Client.GetStringAsync("/odata/IgnProducts");
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement first = doc.RootElement.GetProperty("value")[0];

        // (1) The ignored-property modifier ran.
        Assert.False(first.TryGetProperty("CostBasis", out _));
        Assert.False(first.TryGetProperty("Audit", out _));

        // (2) The open-type modifier ran, AND survived into the options nav suppression derives
        //     from — the container is absent and its dynamic key is a sibling of Material.
        JsonElement spec = first.GetProperty("Spec");
        Assert.False(spec.TryGetProperty("Extras", out _));
        Assert.Equal("matte", spec.GetProperty("finish").GetString());
        Assert.Equal("steel", spec.GetProperty("Material").GetString());

        // (3) The nav-suppression modifier ran: no $expand was asked for, so the navigation is gone.
        Assert.False(first.TryGetProperty("Tags", out _));
    }

    /// <summary>
    /// The same three, on the single-entity route — a different serialization entry point, so a
    /// refactor that fixed only the collection path is still caught.
    /// </summary>
    [Fact]
    public async Task TheWholeChainSurvivesOnTheSingleEntityRoute()
    {
        await using TestFixture fx = await IgnProductHost.BuildAsync();

        string body = await fx.Client.GetStringAsync("/odata/IgnProducts(1)");
        using JsonDocument doc = JsonDocument.Parse(body);

        Assert.False(doc.RootElement.TryGetProperty("CostBasis", out _));
        Assert.False(doc.RootElement.TryGetProperty("Tags", out _));

        JsonElement spec = doc.RootElement.GetProperty("Spec");
        Assert.False(spec.TryGetProperty("Extras", out _));
        Assert.Equal("matte", spec.GetProperty("finish").GetString());
    }

    /// <summary>
    /// And with <c>$expand</c>, because that is the path where nav suppression genuinely does work
    /// (it splices the requested navigation back in) rather than merely removing everything. The
    /// container must still be flat on both the parent and the expanded child's own contract.
    /// </summary>
    [Fact]
    public async Task TheWholeChainSurvivesUnderExpand()
    {
        await using TestFixture fx = await IgnProductHost.BuildAsync();

        string body = await fx.Client.GetStringAsync("/odata/IgnProducts?$expand=Tags");
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement first = doc.RootElement.GetProperty("value")[0];

        // The navigation came back...
        Assert.True(first.TryGetProperty("Tags", out JsonElement tags));
        Assert.Equal("blue", tags[0].GetProperty("Label").GetString());
        // ...its own Ignore() still holds...
        Assert.False(tags[0].TryGetProperty("InternalCode", out _));
        // ...and the container is still flat.
        Assert.Equal("matte", first.GetProperty("Spec").GetProperty("finish").GetString());
    }
}

// Models for half 1. Separate from the shared fixtures so the isolation tests cannot be perturbed by
// an unrelated change to a model another file also uses.
public class OrdBag
{
    public string? Region { get; set; }
    public IDictionary<string, object?>? Kv { get; set; }
}

public class OrdHost
{
    public int Id { get; set; }
    public OrdBag? Bag { get; set; }
}
