using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #398 stage 1 — <c>Ignore()</c> <b>containment</b> for open types, asserted end to end.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard.</b> <c>Ignore()</c> (#226) works by <i>removing</i> a member in a
/// <c>TypeInfoResolver</c> modifier, and <c>System.Text.Json</c> extension data captures exactly
/// what a modifier removed. Measured at raw STJ level on .NET 10.0.11 — one modifier removing
/// <c>Removed</c>, a second marking <c>Bag</c> as extension data:
/// </para>
/// <code>
/// Deserialize({"Id":1,"Removed":"leak","Hidden":"leak2","x":1})
///   -> bag: Removed=leak, x=1        // the REMOVED member landed in the bag
///   -> Removed property itself: ""   // still withheld, as intended
///   -> reserialize -> {"Id":1,...,"Removed":"leak","x":1}
/// </code>
/// <para>
/// <c>Hidden</c>, which carries <c>[JsonIgnore]</c> and therefore stays <i>present</i> in
/// <c>JsonTypeInfo.Properties</c>, did not leak. Only a modifier-removed member does. So the
/// mechanism <c>Ignore()</c> is built on is precisely the mechanism that would defeat it, and the
/// result is a write-then-read oracle <b>under the exact withheld name</b>.
/// </para>
/// <para>
/// <b>At complex scope this cannot happen, and that is the point of this file.</b>
/// <c>Ignore(...)</c> names a root member of a profile's <i>entity</i> type; a dynamic-property
/// container lives on a <i>complex</i> type, which is never a profile's model type. The two sets are
/// disjoint, so the containment is a <b>no-op today</b> — it ships with full coverage <i>before</i>
/// the entity-root widening (#398 stages 3–5) makes it load-bearing, rather than landing the
/// security-critical half and its first real exercise in the same change. These tests assert the
/// no-op explicitly, so that "it does nothing" is a recorded, checked claim instead of an
/// assumption.
/// </para>
/// <para>
/// The mechanism itself — the walk's withheld-name branch, the rewriter's strip, and the read-side
/// collision throw — is exercised directly, against a synthetic withheld-name map, by
/// <c>OpenTypeJsonOptionsTests</c>. That is the only place it <i>can</i> be exercised while the
/// scope is complex-only.
/// </para>
/// <para>
/// <b>Fixture discipline.</b> Everything here runs on <c>IgnProduct</c> — the <i>existing</i>
/// <c>Ignore()</c> fixture, to which #398 added an open complex member (<c>IgnSpec.Extras</c>) — not
/// on a fresh model built around a bag. #395's open-type suite was green while shipping a critical
/// defect precisely because every fixture in it was green-field.
/// </para>
/// </remarks>
// #515: this class and IgnorePropertyIntegrationTests both used to reset-then-assert
// IgnProductProfile's STATIC write captures, and this one cleared two of them from InitializeAsync —
// so its setup could land inside the other's assertion window. The [Collection] that serialised the
// two classes is gone with the statics: the captures are now a per-host singleton
// (IgnProductWriteCaptures), so the two classes run in parallel again and neither can see the
// other's writes. See IgnProductHost.
public class OpenTypeIgnoreContainmentTests : IAsyncLifetime
{
    private TestFixture _fx = null!;

    public async Task InitializeAsync() => _fx = await IgnProductHost.BuildAsync();

    /// <summary>#515: this host's captures. Nothing to clear — a fresh host means a fresh capture.</summary>
    private IgnProductWriteCaptures Captures =>
        _fx.App.Services.GetRequiredService<IgnProductWriteCaptures>();

    public async Task DisposeAsync() => await _fx.DisposeAsync();

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    // ── The premise ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fixture really does have both features at once. Without this the rest of the file could
    /// pass against a registration where open types were never active, which is the exact way a
    /// green-field suite goes green while asserting nothing.
    /// </summary>
    [Fact]
    public async Task Premise_TheRegistrationHasBothAnIgnoreSetAndAnOpenComplexType()
    {
        string xml = await _fx.Client.GetStringAsync("/odata/$metadata");

        // IgnSpec is an open complex type: OpenType="true", container omitted from the declared
        // properties. This is what makes OpenTypesActive true for the whole registration.
        int specStart = xml.IndexOf("<ComplexType Name=\"IgnSpec\"", StringComparison.Ordinal);
        Assert.True(specStart >= 0, "IgnSpec is not in $metadata: " + xml);
        int specEnd = xml.IndexOf("</ComplexType>", specStart, StringComparison.Ordinal);
        string spec = xml[specStart..specEnd];
        Assert.Contains("OpenType=\"true\"", spec, StringComparison.Ordinal);
        Assert.DoesNotContain("Extras", spec, StringComparison.Ordinal);

        // And the Ignore() set is real.
        int prodStart = xml.IndexOf("<EntityType Name=\"IgnProduct\"", StringComparison.Ordinal);
        int prodEnd = xml.IndexOf("</EntityType>", prodStart, StringComparison.Ordinal);
        Assert.DoesNotContain("CostBasis", xml[prodStart..prodEnd], StringComparison.Ordinal);
    }

    // ── The no-op, stated as behaviour ──────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The sharp form of the no-op.</b> <c>CostBasis</c> is withheld on the <i>entity</i>. Sent
    /// into the <i>complex</i> type's bag it is an ordinary dynamic key with no special meaning at
    /// all — accepted, stored, echoed. If a later change keyed the withheld-name map by anything
    /// looser than the declaring CLR type (or unioned every profile's names across all types), this
    /// is the test that would notice: the key would start being dropped.
    /// </summary>
    [Fact]
    public async Task AnIgnoredEntityNameIsAnOrdinaryDynamicKeyOnAComplexTypesBag()
    {
        HttpResponseMessage resp = await _fx.Client.PostAsync("/odata/IgnProducts",
            Json("""{ "Name": "n", "Spec": { "Material": "brass", "CostBasis": 42 } }"""));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // It reached the handler, in the complex type's bag.
        IDictionary<string, object?> bag = Captures.LastPosted!.Spec!.Extras!;
        Assert.Equal(new[] { "CostBasis" }, bag.Keys.OrderBy(k => k, StringComparer.Ordinal));

        // And it is echoed back from there, flat — while the ENTITY's own CostBasis stays withheld.
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(42, doc.RootElement.GetProperty("Spec").GetProperty("CostBasis").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("CostBasis", out _));
    }

    /// <summary>
    /// The other half of the no-op: an <c>Ignore()</c>d name at the <b>entity root</b> is still
    /// silently discarded, exactly as it was before #398 — the root is a closed type, so the key
    /// reaches no bag and the withheld-name branch is never consulted for it.
    /// <para>
    /// This is also the behaviour the drop-versus-400 decision preserves. OData Protocol §11.4.2's
    /// "MUST fail if unable to persist all property values specified in the request" reads like it
    /// governs, and does not: "property value" is a term of art that already excludes things present
    /// in the body but not properties of the type, and no clause contemplates a server that
    /// deliberately conceals a <i>declared</i> property. Ambiguous, so this follows
    /// <c>Microsoft.AspNetCore.OData</c>, which clears
    /// <c>ThrowOnUndeclaredPropertyForNonOpenType</c> deliberately
    /// (<c>ODataInputFormatter.cs:203</c>) — and which matches what OhData's closed-type path already
    /// did for unknown members.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnIgnoredNameAtTheEntityRootIsStillSilentlyDropped()
    {
        HttpResponseMessage resp = await _fx.Client.PostAsync("/odata/IgnProducts",
            Json("""{ "Name": "n", "CostBasis": 99, "Audit": { "CreatedBy": "attacker" } }"""));

        // Silently: 201, no error envelope, no target.
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // The handler never saw either withheld value.
        Assert.Equal(0m, Captures.LastPosted!.CostBasis);
        Assert.Null(Captures.LastPosted!.Audit);

        // And neither is echoed.
        string body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("CostBasis", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attacker", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_IgnoredNamesAreStillDroppedWithAContainerPresent()
    {
        HttpResponseMessage resp = await _fx.Client.PutAsync("/odata/IgnProducts(1)",
            Json("""
            { "Id": 1, "Name": "renamed", "CostBasis": 99,
              "Spec": { "Material": "brass", "finish": "gloss" } }
            """));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(0m, Captures.LastPut!.CostBasis);
        // The bag still binds — the containment must not have cost the feature it rides on.
        Assert.Equal("gloss", Captures.LastPut!.Spec!.Extras!["finish"]!.ToString());
    }

    /// <summary>
    /// The read side, with the bag populated by SERVER-SIDE data rather than by a request. A handler
    /// that puts a withheld entity name into a complex type's bag is doing nothing wrong at complex
    /// scope, because that name means nothing on that type — so this must serve normally rather than
    /// throwing the declared-name collision. The collision applies to names declared (or withheld)
    /// on the <i>bag's own type</i>, which is what keeps the rule from over-reaching.
    /// </summary>
    [Fact]
    public async Task Read_ABagKeyNamedAfterAnIgnoredEntityMemberServesNormally()
    {
        HttpResponseMessage post = await _fx.Client.PostAsync("/odata/IgnProducts",
            Json("""{ "Name": "n", "Spec": { "Material": "brass", "Audit": "not-the-entitys" } }"""));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Assert.Equal("not-the-entitys", doc.RootElement.GetProperty("Spec").GetProperty("Audit").GetString());
        // The entity's own Audit is still withheld — same JSON name, different type, opposite answer.
        Assert.False(doc.RootElement.TryGetProperty("Audit", out _));
    }

    /// <summary>
    /// The startup consistency check (#226) still fires with an open type in the model. It is the
    /// guard that stops two profiles over one CLR type from disagreeing about what is withheld, and
    /// the withheld-name map is built from the same source — so if that check ever stopped running,
    /// the map would be built from whichever profile was seen first.
    /// </summary>
    [Fact]
    public async Task StartupStillRejectsTwoProfilesDisagreeingAboutWhatIsIgnored()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using TestFixture fx = await IgnProductHost.BuildAsync(b => b
                .AddEntitySetProfile<IgnProductProfile>()
                .AddEntitySetProfile<IgnProductConflictingProfile>());
            await fx.Client.GetAsync("/odata/$metadata");
        });

        Assert.Contains("Ignore()", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// A second profile over <c>IgnProduct</c> with a DIFFERENT ignore set, for the startup-conflict
/// test above. Never registered alongside the others in a passing scenario.
/// </summary>
public sealed class IgnProductConflictingProfile : EntitySetProfile<int, IgnProduct>
{
    public IgnProductConflictingProfile() : base(x => x.Id)
    {
        EntitySetName = "IgnProductsAlt";
        Ignore(x => x.Name);
        GetById = (id, ct) => OhDataResult.Success<IgnProduct>(null);
    }
}
