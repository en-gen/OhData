using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using Microsoft.OData.ModelBuilder.Annotations;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// Hostless unit tests against OpenTypeJsonOptions itself, mirroring the precedent
// IgnoredPropertyJsonOptionsTests set for the sibling modifier. Everything here builds its EDM
// straight from ODataConventionModelBuilder rather than through TestHostBuilder: these assertions
// are about the container map, the derived JsonSerializerOptions and the startup validation, none
// of which need an HTTP surface. The end-to-end behavior lives in OpenTypeTests /
// OpenTypeCompositionTests / OpenTypeLimitationTests.

// ── Models ──────────────────────────────────────────────────────────────────────────────────────

public class OtjEntity
{
    public int Id { get; set; }
    public OtjBag? Bag { get; set; }
}

public class OtjBag
{
    public string? Region { get; set; }
    public IDictionary<string, object?>? Kv { get; set; }
}

/// <summary>Container declared on the base — one map entry covers the chain.</summary>
public class OtjDerived : OtjBag
{
    public string? Channel { get; set; }
}

/// <summary>Shadows the container with <c>new</c>. See the flattening test for what really happens.</summary>
public class OtjShadow : OtjBag
{
    public new IDictionary<string, object?>? Kv { get; set; }
}

/// <summary>Getter-only container: the idiomatic collection-initializer shape STJ cannot bind into.</summary>
public class OtjGetterOnly
{
    public string? Region { get; set; }
    public IDictionary<string, object?> Kv { get; } = new Dictionary<string, object?>();
}

/// <summary>
/// Already carries its own extension-data member; the container would be a second.
/// <c>JsonObject</c> is deliberate: <c>System.Text.Json</c> accepts it as extension data, but it is
/// <c>IDictionary&lt;string, JsonNode?&gt;</c> and so is invisible to the model builder's container
/// inference — which is what makes this shape reachable at all. Two
/// <c>IDictionary&lt;string, object&gt;</c> members would instead be refused by
/// <c>ODataConventionModelBuilder</c> outright
/// (<c>ArgumentException: Found more than one dynamic property container</c>).
/// </summary>
public class OtjCompetingExtensionData
{
    public string? Region { get; set; }
    [JsonExtensionData] public JsonObject? Other { get; set; }
    public IDictionary<string, object?>? Kv { get; set; }
}

public class OtjCompetingHost
{
    public int Id { get; set; }
    public OtjCompetingExtensionData? Bag { get; set; }
}

public class OtjNoOpenType
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

public class OpenTypeJsonOptionsTests
{
    // The EDM is what drives everything in OpenTypeJsonOptions, so it is built here directly —
    // no profiles, no host. Complex types are registered explicitly because the entity root is
    // what the convention builder is handed.
    private static IEdmModel BuildModel<TEntity>() where TEntity : class
    {
        var builder = new ODataConventionModelBuilder();
        builder.EntitySet<TEntity>("Things");
        return builder.GetEdmModel();
    }

    private static OpenTypeJsonOptions.OpenComplexTypeContainers Containers<TEntity>() where TEntity : class =>
        OpenTypeJsonOptions.BuildOpenComplexTypeContainerMap(BuildModel<TEntity>());

    // ── Container map ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModelWithNoOpenComplexType_ProducesNoContainers()
    {
        OpenTypeJsonOptions.OpenComplexTypeContainers containers = Containers<OtjNoOpenType>();
        Assert.True(containers.IsEmpty);
        Assert.Empty(containers.ByDeclaringType);
        Assert.Empty(containers.OpenClrTypes);
    }

    /// <summary>
    /// One entry per <i>declaring</i> type, not per open type. <c>ODataConventionModelBuilder</c>
    /// discovers a referenced complex type's derived types across the whole assembly, so naming
    /// <c>OtjBag</c> alone brings <c>OtjDerived</c> and <c>OtjShadow</c> into the model too:
    /// <c>OtjDerived</c> inherits the base's container and collapses onto its entry, while
    /// <c>OtjShadow</c> declares its own with <c>new</c> and gets one of its own.
    /// </summary>
    [Fact]
    public void ModelWithAnOpenComplexType_ProducesOneEntryPerDeclaringType()
    {
        OpenTypeJsonOptions.OpenComplexTypeContainers containers = Containers<OtjEntity>();

        Assert.Equal(
            new[] { typeof(OtjBag), typeof(OtjShadow) }.OrderBy(t => t.Name),
            containers.ByDeclaringType.Keys.OrderBy(t => t.Name));
        Assert.Equal("Kv", containers.ByDeclaringType[typeof(OtjBag)].Name);
        Assert.Equal(typeof(OtjBag), containers.ByDeclaringType[typeof(OtjBag)].DeclaringType);
    }

    // ── Zero delta ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The structural half of "existing suites unchanged": with no open complex type the base
    /// options are threaded through reference-identical, so nothing about the serialization
    /// pipeline moves. (The other half — that the map is not even built unless the registration
    /// opted in — is <c>OhDataEndpointFactory.MapAll</c>'s, exercised by <c>OpenTypeOptInTests</c>.)
    /// </summary>
    [Fact]
    public void Build_WithNoContainers_ReturnsTheBaseOptionsReferenceEqual()
    {
        var baseOptions = new JsonSerializerOptions();
        Assert.Same(
            baseOptions,
            OpenTypeJsonOptions.Build(baseOptions, OpenTypeJsonOptions.OpenComplexTypeContainers.Empty));
        Assert.Same(baseOptions, OpenTypeJsonOptions.Build(baseOptions, Containers<OtjNoOpenType>()));
    }

    [Fact]
    public void Build_WithContainers_ReturnsADerivedOptionsInstance()
    {
        var baseOptions = new JsonSerializerOptions();
        JsonSerializerOptions derived = OpenTypeJsonOptions.Build(baseOptions, Containers<OtjEntity>());
        Assert.NotSame(baseOptions, derived);
    }

    // ── The modifier ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_MarksTheContainerAsExtensionData_SoTheBagSerializesFlat()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjEntity>());

        string json = JsonSerializer.Serialize(
            new OtjBag { Region = "eu", Kv = new Dictionary<string, object?> { ["tier"] = 3 } }, options);

        Assert.Equal("""{"Region":"eu","tier":3}""", json);
    }

    /// <summary>
    /// #389 finding 8. The docs and the modifier comment used to say a <c>new</c>-shadowed container
    /// was "not matched … left serializing as it does today". Measured, that is the opposite of what
    /// happens: <c>ODataConventionModelBuilder</c> records the DERIVED member as the derived EDM
    /// type's container, so the derived type gets its own map entry and its shadowing member is what
    /// gets flattened. Pinned here so the (better) real behavior cannot drift back unnoticed.
    /// </summary>
    [Fact]
    public void Build_FlattensAContainerShadowedWithNewOnTheDerivedType()
    {
        OpenTypeJsonOptions.OpenComplexTypeContainers containers = Containers<OtjEntity>();

        // The shadowing member gets its OWN map entry, keyed by the derived type that declares it.
        Assert.Equal(
            typeof(OtjShadow).GetProperty("Kv"),
            containers.ByDeclaringType[typeof(OtjShadow)]);

        JsonSerializerOptions options = OpenTypeJsonOptions.Build(new JsonSerializerOptions(), containers);
        string json = JsonSerializer.Serialize(
            new OtjShadow { Region = "eu", Kv = new Dictionary<string, object?> { ["shadowed"] = 1 } },
            options);

        Assert.Equal("""{"Region":"eu","shadowed":1}""", json);
    }

    [Fact]
    public void Build_DerivedTypeInheritsTheBaseContainer()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjEntity>());

        string json = JsonSerializer.Serialize(
            new OtjDerived
            {
                Region = "eu",
                Channel = "web",
                Kv = new Dictionary<string, object?> { ["dyn"] = "v" },
            },
            options);

        Assert.Contains("\"dyn\":\"v\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Kv", json, StringComparison.Ordinal);
    }

    // ── Declared-name shadowing on the way OUT (#389 finding 3) ─────────────────────────────────

    /// <summary>
    /// A bag key equal to a declared property's JSON name would emit that name twice in one JSON
    /// object — on every .NET reader tested the BAG entry wins, making the declared value
    /// unreachable. It is a hard error, matching <c>Microsoft.AspNetCore.OData</c>'s
    /// <c>DynamicPropertyNameAlreadyUsedAsDeclaredPropertyName</c>. The message names the type and
    /// the key and carries no values.
    /// </summary>
    [Fact]
    public void Build_ABagKeyShadowingADeclaredProperty_Throws()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjEntity>());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(
                new OtjBag
                {
                    Region = "declared",
                    Kv = new Dictionary<string, object?> { ["Region"] = "fromBag", ["ok"] = 1 },
                },
                options));

        Assert.Contains("'Region'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(OtjBag).FullName!, ex.Message, StringComparison.Ordinal);
        // Names, never values: this message reaches logs.
        Assert.DoesNotContain("fromBag", ex.Message, StringComparison.Ordinal);
    }

    // ── Keys that are not odataIdentifiers, on the way OUT (#389) ───────────────────────────────

    /// <summary>
    /// A key that is not an <c>odataIdentifier</c> (CSDL 4.01 §4.1) produces a property no conforming
    /// OData reader can address. Measured before the fix, the empty case serialized straight through
    /// as <c>{"Region":"declared","":"x"}</c>.
    /// <para>
    /// <b>Deliberate divergence from <c>Microsoft.AspNetCore.OData</c></b>, which silently skips the
    /// empty key (<c>ODataResourceSerializer.cs:820</c>) and polices nothing else. Matching that skip
    /// would mean reintroducing the clone-and-substitute machinery deleted in <c>e0edaac</c> — the
    /// getter wrapper now only inspects and returns the same reference — and a silent drop is not
    /// worth resurrecting it.
    /// </para>
    /// </summary>
    [Theory]
    // The names that are not names at all — the whole of what this check used to reject.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    // NBSP + EM SPACE. Written as escapes deliberately — the literal characters are
    // invisible in source and an editor or a copy/paste silently turns them back into ASCII spaces.
    [InlineData("\u00A0\u2003")]
    // ...and the names that ARE names but are still illegal identifiers, which this check now covers
    // too. Every one of these serialized happily before the widening.
    [InlineData("has space")]
    [InlineData("@odata.type")]
    [InlineData("kebab-case")]
    [InlineData("1leading")]
    // A key of only FORMAT characters (U+200B ZERO WIDTH SPACE, category Cf). Not whitespace, so
    // string.IsNullOrWhiteSpace passed it; the ABNF's leading rune must be L or Nl, so this does not.
    [InlineData("\u200B")]
    public void Build_ABagKeyThatIsNotAnIdentifier_Throws(string key)
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjEntity>());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(
                new OtjBag
                {
                    Region = "declared",
                    Kv = new Dictionary<string, object?> { [key] = "namelessValue", ["ok"] = 1 },
                },
                options));

        Assert.Contains(typeof(OtjBag).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a valid OData identifier", ex.Message, StringComparison.Ordinal);
        // Names and causes, never values.
        Assert.DoesNotContain("namelessValue", ex.Message, StringComparison.Ordinal);
        // The offending KEY is not quoted either, unlike the collision message: a key rejected by the
        // grammar is by definition not an identifier, so it can carry newlines or control characters
        // into a log line. "has space" is the readable member of the corpus above to pin that with.
        if (key == "has space") Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The inspection pass used to bail out when the type had no declared property to be shadowed, so
    /// an open complex type that is <i>only</i> a container never got a wrapped getter at all. The
    /// identifier check does not depend on the declared set, so that bail is gone — this is the
    /// regression pin for its removal.
    /// </summary>
    [Fact]
    public void Build_ABagKeyThatIsNotAnIdentifier_ThrowsEvenOnATypeWithNoDeclaredProperty()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjBagOnlyHost>());

        // Premise: this type really does have a container and nothing else declared.
        Assert.DoesNotContain(
            options.GetTypeInfo(typeof(OtjBagOnly)).Properties, p => !p.IsExtensionData);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(
                new OtjBagOnly { Kv = new Dictionary<string, object?> { ["   "] = 1 } },
                options));

        Assert.Contains("not a valid OData identifier", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The widened check must not become a blanket rejection: a key that <i>is</i> a conformant
    /// identifier still serializes flat. The non-ASCII rows matter most — each has a character
    /// outside <c>[A-Za-z0-9_]</c>, so each exercises the rune-walk fallback rather than the
    /// <c>SearchValues</c> fast path, and an over-eager fast path would surface here first.
    /// </summary>
    [Theory]
    [InlineData("tier")]
    [InlineData("_leading")]
    [InlineData("with_1_digit")]
    // Mc (Devanagari), Mn (Thai), Nl (a letter-number), and an astral-plane Lu.
    [InlineData("नाम")]
    [InlineData("ชื่อ")]
    [InlineData("Ⅸ")]
    [InlineData("\U0001D400bc")]
    public void Build_AConformantBagKey_SerializesFlat(string key)
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjEntity>());

        string json = JsonSerializer.Serialize(
            new OtjBag { Region = "declared", Kv = new Dictionary<string, object?> { [key] = 1 } },
            options);

        Assert.Contains("declared", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordinal, not case-insensitive — and this is a recorded decision, not an oversight. Only a
    /// byte-for-byte repeat is a duplicate JSON key, so <c>region</c> beside a declared
    /// <c>Region</c> serializes fine and must not fault. The consequence is documented in
    /// <c>docs/open-types.md</c>: OhData binds request bodies case-insensitively, so a
    /// case-differing key can still round-trip into a corrupting write.
    /// </summary>
    [Fact]
    public void Build_ABagKeyDifferingOnlyByCase_DoesNotThrow()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjEntity>());

        string json = JsonSerializer.Serialize(
            new OtjBag { Region = "declared", Kv = new Dictionary<string, object?> { ["region"] = "kept" } },
            options);

        Assert.Equal("""{"Region":"declared","region":"kept"}""", json);
    }

    /// <summary>
    /// The check sees through a container declared as a custom dictionary subclass rather than as the
    /// interface, and the happy path still serializes flat through that same custom type. (The old
    /// drop implementation had to CLONE the container here, which is what made the runtime type
    /// load-bearing; nothing is substituted any more, so the getter is a pure inspection.)
    /// </summary>
    [Fact]
    public void Build_ACustomContainerRuntimeType_SerializesFlatAndStillDetectsAShadow()
    {
        OpenTypeJsonOptions.OpenComplexTypeContainers containers = Containers<OtjCustomBagHost>();
        // Guard against the test passing vacuously: the builder must actually have inferred the
        // custom subclass as this type's dynamic-property container.
        Assert.Equal(typeof(OtjCustomBag), containers.ByDeclaringType[typeof(OtjCustomBagHolder)].PropertyType);

        JsonSerializerOptions options = OpenTypeJsonOptions.Build(new JsonSerializerOptions(), containers);

        string json = JsonSerializer.Serialize(
            new OtjCustomBagHolder { Region = "declared", Kv = new OtjCustomBag { ["ok"] = 1 } },
            options);
        Assert.Equal("""{"Region":"declared","ok":1}""", json);

        Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(
                new OtjCustomBagHolder
                {
                    Region = "declared",
                    Kv = new OtjCustomBag { ["Region"] = "fromBag", ["ok"] = 1 },
                },
                options));
    }

    /// <summary>
    /// A container whose parameterless constructor SEEDS an entry. This shape used to be a hazard in
    /// its own right (#389 M1): the drop path cloned the container via <c>Activator.CreateInstance</c>
    /// and assumed the clone was empty, so the seeded key collided on copy-in. With no clone there is
    /// no special case left — the seeded key is just another dynamic key and serializes normally.
    /// Kept as the regression pin for that removal.
    /// </summary>
    [Fact]
    public void Build_AContainerWhoseConstructorSeedsEntries_IsNoLongerASpecialCase()
    {
        OpenTypeJsonOptions.OpenComplexTypeContainers containers = Containers<OtjDefaultingBagHost>();
        // Premise: the seeding subclass really is what the builder inferred as the container.
        Assert.Equal(
            typeof(OtjDefaultingBag),
            containers.ByDeclaringType[typeof(OtjDefaultingBagHolder)].PropertyType);

        JsonSerializerOptions options = OpenTypeJsonOptions.Build(new JsonSerializerOptions(), containers);

        string json = JsonSerializer.Serialize(
            new OtjDefaultingBagHolder { Region = "declared", Kv = new OtjDefaultingBag { ["ok"] = 1 } },
            options);

        // "schema" is the constructor-seeded key; it is emitted once, alongside the explicit one.
        Assert.Equal("""{"Region":"declared","schema":"v1","ok":1}""", json);
    }

    /// <summary>
    /// #389 M2, reversed. A container PRE-SEEDED with one of its own declared property names used to
    /// be the feature's worst corner: the getter always saw a collision, so on the DESERIALIZE path
    /// System.Text.Json populated the discarded clone and every dynamic key in the request was lost
    /// while the write still reported success. The getter no longer substitutes anything, so the same
    /// shape now fails loudly on first contact instead of silently discarding writes.
    /// </summary>
    [Fact]
    public void Build_AContainerPreSeededWithADeclaredName_ThrowsInsteadOfSilentlyLosingWrites()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjPreSeededBagHost>());

        // DESERIALIZE, not serialize: this is the path the old implementation corrupted. STJ calls
        // the container getter to find an existing dictionary to populate, the getter sees the
        // pre-seeded declared name, and the request fails instead of quietly dropping alpha/beta.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Deserialize<OtjPreSeededBagHolder>("""{"alpha":1,"beta":2}""", options));

        Assert.Contains("'Region'", ex.Message, StringComparison.Ordinal);

        // And the read side of the same shape fails too, rather than rendering a clean-looking echo.
        Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(new OtjPreSeededBagHolder { Region = "declared" }, options));
    }

    /// <summary>
    /// The checking getter must hand back the SAME dictionary — System.Text.Json calls it on the
    /// deserialize path too, to find an existing bag to populate, so substituting anything would
    /// corrupt binding. It only ever inspects now, which is what makes that structural.
    /// </summary>
    [Fact]
    public void Build_BindingIsUnaffectedByTheShadowCheckingGetter()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjEntity>());

        OtjBag? bound = JsonSerializer.Deserialize<OtjBag>("""{"Region":"eu","tier":3,"note":"n"}""", options);

        Assert.Equal("eu", bound!.Region);
        Assert.Equal(new[] { "note", "tier" }, bound.Kv!.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    // ── Unusable containers fail loudly (#389 finding 6) ────────────────────────────────────────

    /// <summary>
    /// A getter-only container is exactly the idiomatic
    /// <c>public IDictionary&lt;string, object?&gt; Bag { get; } = new();</c>. The convention builder
    /// still marks the type open and drops the member from the CSDL, but System.Text.Json cannot bind
    /// into it — measured, the incoming dynamic keys are silently discarded — and skipping the type
    /// leaves the CSDL claiming <c>OpenType="true"</c> while the wire nests the bag under its own
    /// name. That is a startup failure naming the member and the fix, not a silent skip — and the
    /// message also names <c>WithOpenTypes(false)</c>, since turning open types off for the whole
    /// registration is the other way out.
    /// </summary>
    [Fact]
    public void BuildContainerMap_GetterOnlyContainer_Throws()
    {
        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(() => Containers<OtjGetterOnlyHost>());

        Assert.Contains("OtjGetterOnly", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Kv", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no accessible setter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithOpenTypes", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bound on the container guard: <c>ODataConventionModelBuilder</c> never infers a container
    /// from a member that is not <c>IDictionary&lt;string, object&gt;</c>-assignable — such a member
    /// is mapped as an ordinary <c>Collection(KeyValuePair)</c> property and its type is not marked
    /// open at all. That is why <c>ThrowIfUnusableAsExtensionData</c>'s type check has no test: it is
    /// unreachable (the annotation cannot be written by hand either — <c>EdmAnnotationExtensions</c>
    /// exposes only a getter for it, and <c>DynamicPropertyDictionaryAnnotation</c> is internal), and
    /// exists so a future widening of the builder's inference fails at startup rather than
    /// mid-request. This pins the premise.
    /// </summary>
    [Fact]
    public void ADictionaryMemberOfTheWrongValueType_IsNotAContainerAtAll()
    {
        IEdmModel model = BuildModel<OtjWrongDictionaryTypeHost>();
        IEdmComplexType complexType = Assert.Single(
            model.SchemaElements.OfType<IEdmComplexType>(),
            t => t.Name == nameof(OtjWrongDictionaryType));

        Assert.False(complexType.IsOpen);
        Assert.Null(model.GetDynamicPropertyDictionary(complexType));
        Assert.True(OpenTypeJsonOptions.BuildOpenComplexTypeContainerMap(model).IsEmpty);
    }

    // ── Startup validation (#389 finding 5) ─────────────────────────────────────────────────────

    [Fact]
    public void ValidateOrThrow_WithNoContainers_IsANoOp()
    {
        OpenTypeJsonOptions.ValidateOrThrow(
            new JsonSerializerOptions(), OpenTypeJsonOptions.OpenComplexTypeContainers.Empty);
    }

    [Fact]
    public void ValidateOrThrow_WithAWellFormedContract_DoesNotThrow()
    {
        OpenTypeJsonOptions.OpenComplexTypeContainers containers = Containers<OtjEntity>();
        JsonSerializerOptions options = OpenTypeJsonOptions.Build(new JsonSerializerOptions(), containers);
        OpenTypeJsonOptions.ValidateOrThrow(options, containers);
    }

    /// <summary>
    /// The failure mode <c>ValidateOrThrow</c> was written for, and the one its original
    /// <c>GetTypeInfo</c>-only probe did NOT catch: <c>GetTypeInfo</c> resolves a two-extension-member
    /// contract without complaint and the <c>InvalidOperationException</c> only appears from
    /// <c>JsonSerializer.Serialize</c> — i.e. as a 500 on the first request. The explicit
    /// extension-member count is what turns it back into a startup failure.
    /// </summary>
    [Fact]
    public void ValidateOrThrow_CompetingJsonExtensionDataMember_ThrowsAtStartup()
    {
        OpenTypeJsonOptions.OpenComplexTypeContainers containers = Containers<OtjCompetingHost>();
        // Premise: the builder really did designate Kv, leaving the [JsonExtensionData] JsonObject
        // as a second, competing extension-data member once the modifier runs.
        Assert.Equal("Kv", containers.ByDeclaringType[typeof(OtjCompetingExtensionData)].Name);

        JsonSerializerOptions options = OpenTypeJsonOptions.Build(new JsonSerializerOptions(), containers);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => OpenTypeJsonOptions.ValidateOrThrow(options, containers));

        Assert.Contains("OtjCompetingExtensionData", ex.Message, StringComparison.Ordinal);
        Assert.Contains("extension-data members", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[JsonExtensionData]", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Probing must cover DERIVED open complex types, not only the container-declaring ones: the map
    /// collapses a derived type onto its base's entry, but the derived type has its own
    /// <see cref="JsonTypeInfo"/> contract — and its own chance to carry a competing extension-data
    /// member.
    /// </summary>
    [Fact]
    public void BuildContainerMap_RecordsDerivedOpenTypesSeparatelyForProbing()
    {
        OpenTypeJsonOptions.OpenComplexTypeContainers containers = Containers<OtjEntity>();

        // OtjDerived has no entry of its own in the container map (it shares OtjBag's), but it is
        // still an open complex type with its own JsonTypeInfo contract, so it must be probed.
        Assert.DoesNotContain(typeof(OtjDerived), containers.ByDeclaringType.Keys);
        Assert.Contains(typeof(OtjDerived), containers.OpenClrTypes);
        Assert.Contains(typeof(OtjBag), containers.OpenClrTypes);
        Assert.Contains(typeof(OtjShadow), containers.OpenClrTypes);
    }

    // ── Dynamic-key validation (#389 finding 2) ─────────────────────────────────────────────────

    [Theory]
    [InlineData("tier", true)]
    [InlineData("organizationCreatedDate", true)]
    [InlineData("_leading", true)]
    [InlineData("with_1_digit", true)]
    [InlineData("", false)]
    [InlineData("@odata.type", false)]
    [InlineData("@odata.id", false)]
    [InlineData("Meta@odata.count", false)]
    [InlineData("has.dot", false)]
    [InlineData("has space", false)]
    [InlineData("1leading", false)]
    [InlineData("kebab-case", false)]
    public void IsValidDynamicPropertyName_MatchesTheODataSimpleIdentifierGrammar(string name, bool expected) =>
        Assert.Equal(expected, OpenTypeJsonOptions.IsValidDynamicPropertyName(name));

    // ── #389 M3: the grammar is the ABNF's Unicode categories, not char.IsLetter ─────────────────
    //
    // odataIdentifier permits categories L/Nl leading and L/Nl/Nd/Mn/Mc/Pc/Cf following. The
    // char.IsLetter/IsLetterOrDigit spelling this used to carry excluded the combining marks
    // (Mn/Mc) and Nl, so it rejected legitimate client keys with a 400. The NFC/NFD pair is the
    // case that forced the widening: macOS normalises to NFD and Windows to NFC, so the SAME key
    // typed on two machines got two different HTTP status codes.

    [Theory]
    // Mc — Devanagari spacing combining mark ("naam").
    [InlineData("नाम", true)]
    // Mn — Thai tone marks ("chue").
    [InlineData("ชื่อ", true)]
    // Mn — NFD-decomposed "naïve": i + COMBINING DIAERESIS.
    [InlineData("naïve", true)]
    // The NFC spelling of the SAME word (precomposed U+00EF). These two rows look identical and are
    // different strings — that is the point: macOS hands back the row above, Windows this one, and
    // before M3 the two got different HTTP status codes for the same key.
    [InlineData("naïve", true)]
    // Mn — NFD-decomposed "Tiếng".
    [InlineData("Tiếng", true)]
    // Nl — ROMAN NUMERAL NINE, a letter-number, valid as a LEADING character.
    [InlineData("Ⅸ", true)]
    // Lu on the astral plane — MATHEMATICAL BOLD CAPITAL A, a surrogate pair in UTF-16.
    [InlineData("\U0001D400bc", true)]
    // Cf — ZERO WIDTH NON-JOINER is a valid FOLLOWING character.
    [InlineData("a‌b", true)]
    // ...but not a leading one: Cf is absent from identifierLeadingCharacter.
    [InlineData("‌ab", false)]
    // Mn is likewise following-only — a name may not OPEN with a combining mark.
    [InlineData("́abc", false)]
    // Nd is following-only, which is what "1leading" already pins for ASCII; here in Devanagari.
    [InlineData("१abc", false)]
    // Still rejected: So (SNOWMAN) is in neither set, so widening to the ABNF did not degenerate
    // into "any non-ASCII character is fine".
    [InlineData("☃snowman", false)]
    public void IsValidDynamicPropertyName_FollowsTheUnicodeCategoriesOfTheAbnf(string name, bool expected) =>
        Assert.Equal(expected, OpenTypeJsonOptions.IsValidDynamicPropertyName(name));

    [Fact]
    public void IsValidDynamicPropertyName_RejectsNamesLongerThan128Chars()
    {
        Assert.True(OpenTypeJsonOptions.IsValidDynamicPropertyName(new string('a', 128)));
        Assert.False(OpenTypeJsonOptions.IsValidDynamicPropertyName(new string('a', 129)));
    }

    /// <summary>
    /// #389 M3: the 128 cap counts CODE POINTS, not UTF-16 code units. An astral-plane identifier is
    /// stored as a surrogate pair, so a length-based cap charged it double and rejected a name half
    /// the permitted size — an artefact of the CLR's string encoding, not of the grammar.
    /// </summary>
    [Fact]
    public void IsValidDynamicPropertyName_CountsCodePointsNotUtf16CodeUnits()
    {
        string astral128 = string.Concat(Enumerable.Repeat("\U0001D400", 128));
        Assert.Equal(256, astral128.Length);          // premise: 128 code points, 256 code units
        Assert.True(OpenTypeJsonOptions.IsValidDynamicPropertyName(astral128));

        Assert.False(OpenTypeJsonOptions.IsValidDynamicPropertyName(
            string.Concat(Enumerable.Repeat("\U0001D400", 129))));
    }

    /// <summary>
    /// #389 round-3 INFO-2. NFC and NFD spellings of the same name agree — but only <i>within the
    /// length limit</i>. Decomposition adds code points, so a name already at the 128 cap in NFC
    /// exceeds it once decomposed. That is the grammar's own boundary (it defines the limit in
    /// characters), not a normalisation inconsistency; the point of pinning it is that the docs used
    /// to state the agreement as absolute.
    /// </summary>
    [Fact]
    public void IsValidDynamicPropertyName_NfcAndNfdCanDivergeOnlyAtTheLengthCap()
    {
        string nfc = new('ï', 128);                       // 128 × 'ï'
        string nfd = nfc.Normalize(NormalizationForm.FormD);
        Assert.Equal(256, nfd.Length);                          // premise: 'i' + U+0308 per character

        Assert.True(OpenTypeJsonOptions.IsValidDynamicPropertyName(nfc));
        Assert.False(OpenTypeJsonOptions.IsValidDynamicPropertyName(nfd));

        // One character below the cap the two forms agree, which is every name a client will send.
        string shortNfc = new('ï', 64);
        Assert.True(OpenTypeJsonOptions.IsValidDynamicPropertyName(shortNfc));
        Assert.True(OpenTypeJsonOptions.IsValidDynamicPropertyName(
            shortNfc.Normalize(NormalizationForm.FormD)));
    }

    /// <summary>An unpaired surrogate is not a character in any category, so it is rejected.</summary>
    [Fact]
    public void IsValidDynamicPropertyName_RejectsAnUnpairedSurrogate() =>
        Assert.False(OpenTypeJsonOptions.IsValidDynamicPropertyName("a\uD800b"));

    // ── The ASCII fast path is an accelerator, never a second opinion ────────────────────────────
    //
    // IsValidDynamicPropertyName decides an all-[A-Za-z0-9_] name with one vectorized
    // ContainsAnyExcept pass plus a length and a leading-digit test, and only falls back to
    // IsValidDynamicPropertyNameByRuneWalk — the normative rune-enumeration implementation, unchanged
    // — when a character outside that set is present. That is a real risk of divergence: the fast
    // path re-derives the length cap and the leading rule from the ASCII subset instead of reading
    // them from the Unicode tables, and a wrong SearchValues membership (adding '-', or missing '_')
    // would silently change the accept/reject set rather than fail to compile.
    //
    // So the two are held against a SHARED CORPUS and required to agree exactly, key by key. The
    // corpus is the union of everything the theories above cover — Devanagari, Thai, both NFC and NFD
    // spellings, Nl, astral-plane, ZWNJ, leading-mark rejection, unpaired surrogates, the 128-cap
    // boundary from both sides, '@' '.' '-' space tab newline NUL, emoji.
    //
    // The exhaustive sweeps — every ASCII code point, and the lone surrogates — live in
    // AgreeOnEveryAsciiCodePointAndLoneSurrogate below rather than in this corpus. As theory rows
    // they would add ~512 near-identical cases to the suite, and two of them (a lone high surrogate
    // and a lone low one) collide on xUnit's test ID, which SKIPS one of the pair silently rather
    // than failing — the worst possible outcome for a test whose whole job is to be exhaustive.

    public static TheoryData<string> EquivalenceCorpus()
    {
        // Deduplicated: the NFC/NFD pair is spelled both literally and via Normalize, so several
        // entries coincide by construction. Two identical rows would give two theory cases with the
        // same display name — an unstable test count rather than extra coverage.
        var names = new List<string>
        {
            // Ordinary ASCII, the fast path's own territory.
            "", "a", "A", "_", "z9", "tier", "region", "_leading", "with_1_digit",
            "organizationCreatedDate", "PascalCase", "camelCase", "snake_case_name",
            // Rejected ASCII.
            "1leading", "9", "kebab-case", "has space", "has.dot", "@odata.type", "@odata.id",
            "Meta@odata.count", "has\ttab", "has\nnewline", "has\0nul", "a-b", "a+b", "a/b", "a:b",
            "$select", "#hash", "a b c", " leading", "trailing ",
            // Non-ASCII whitespace — NBSP and EM SPACE, which the old IsNullOrWhiteSpace check
            // rejected and which must stay rejected.
            " ", " ", "  ", "a b",
            // Marks and letter-numbers the ABNF admits (Mc, Mn, Nl), and NFC/NFD pairs.
            "नाम", "ชื่อ", "Tiếng", "Ⅸ", "Ⅸ9", "naïve", "naïve",
            "naïve".Normalize(NormalizationForm.FormD), "naïve".Normalize(NormalizationForm.FormC),
            // Astral plane, and a surrogate half embedded in an otherwise valid name.
            "\U0001D400", "\U0001D400bc", "a\U0001D400", "a\uD800b", "a\uDC00",
            // Format characters (Cf): ZWNJ/ZWJ/ZWSP are legal FOLLOWING and illegal LEADING.
            "a‌b", "a‍b", "a​b", "‌ab", "​", "​​",
            // Leading marks and digits outside ASCII.
            "́abc", "१abc", "٣abc",
            // In neither category set — So, Sc, emoji (including a surrogate-pair one).
            "☃snowman", "€uro", "\U0001F600", "a\U0001F600", "🎉party",
            // The 128-code-point cap from both sides, in ASCII and in astral code points.
            new string('a', 127), new string('a', 128), new string('a', 129),
            string.Concat(Enumerable.Repeat("\U0001D400", 128)),
            string.Concat(Enumerable.Repeat("\U0001D400", 129)),
            new string('ï', 128), new string('ï', 128).Normalize(NormalizationForm.FormD),
            // A long name whose ONLY non-ASCII character is at the very end, so the fast path is
            // declined on the last code unit rather than the first.
            new string('a', 127) + "ï",
        };

        var corpus = new TheoryData<string>();
        foreach (string name in names.Distinct(StringComparer.Ordinal))
        {
            corpus.Add(name);
        }
        return corpus;
    }

    [Theory]
    [MemberData(nameof(EquivalenceCorpus))]
    public void IsValidDynamicPropertyName_AgreesWithTheRuneWalkOnEveryCorpusEntry(string name) =>
        Assert.Equal(
            OpenTypeJsonOptions.IsValidDynamicPropertyNameByRuneWalk(name),
            OpenTypeJsonOptions.IsValidDynamicPropertyName(name));

    /// <summary>
    /// The exhaustive half of the equivalence proof: every ASCII code point in <b>leading</b> position
    /// and in <b>following</b> position, plus every lone surrogate, compared against the rune walk.
    /// This is precisely the boundary the <c>SearchValues</c> set draws, so an off-by-one in it — an
    /// extra <c>'-'</c>, a missing <c>'_'</c>, <c>'@'</c> slipping in — cannot hide.
    /// <para>
    /// A single <c>[Fact]</c> rather than a theory: as rows these would be ~2,600 near-identical test
    /// cases, and two of them (a lone high surrogate and a lone low one) collide on xUnit's test ID,
    /// which silently <i>skips</i> one of the pair. Disagreements are collected and reported together
    /// so a failure names every offending code point at once instead of only the first.
    /// </para>
    /// </summary>
    [Fact]
    public void IsValidDynamicPropertyName_AgreesWithTheRuneWalkOnEveryAsciiCodePointAndLoneSurrogate()
    {
        var disagreements = new List<string>();
        void Compare(string name, string description)
        {
            if (OpenTypeJsonOptions.IsValidDynamicPropertyNameByRuneWalk(name)
                != OpenTypeJsonOptions.IsValidDynamicPropertyName(name))
            {
                disagreements.Add(description);
            }
        }

        for (int c = 0; c < 128; c++)
        {
            Compare(((char)c).ToString(), $"leading U+{c:X4}");
            Compare("a" + (char)c, $"following U+{c:X4}");
        }
        // The whole surrogate range in isolation: neither half is a scalar value, so each decodes to
        // U+FFFD and must be rejected by both implementations.
        for (int c = 0xD800; c <= 0xDFFF; c++)
        {
            Compare(((char)c).ToString(), $"lone surrogate U+{c:X4}");
        }

        Assert.Empty(disagreements);
    }

    /// <summary>
    /// The memoised wrapper used on the serialize path must be indistinguishable from the function it
    /// memoises — including on the second call, where the answer comes from the cache. Run over the
    /// same corpus, twice, so a miss and a hit are both compared.
    /// </summary>
    [Theory]
    [MemberData(nameof(EquivalenceCorpus))]
    public void IsValidDynamicPropertyNameCached_AgreesWithTheUncachedFunction(string name)
    {
        bool expected = OpenTypeJsonOptions.IsValidDynamicPropertyNameByRuneWalk(name);
        Assert.Equal(expected, OpenTypeJsonOptions.IsValidDynamicPropertyNameCached(name));
        // Second call: for a valid name this is now a cache hit, for an invalid one it is a second
        // miss (invalid names are deliberately never cached).
        Assert.Equal(expected, OpenTypeJsonOptions.IsValidDynamicPropertyNameCached(name));
    }

    /// <summary>
    /// The cache is bounded and stops inserting once full — it never evicts, and above all it never
    /// starts answering wrongly. This drives well past the 1024 capacity with distinct <b>non-ASCII</b>
    /// keys — the only ones that reach the cache at all, since an all-<c>[A-Za-z0-9_]</c> key is
    /// decided by the fast path and never looked up — and then re-checks a mixture of cached,
    /// uncached and invalid names.
    /// <para>
    /// The cache is <c>static</c> and process-wide by design (validity is a pure function of the
    /// string), so this test cannot assert an exact entry count without depending on what every other
    /// test in the assembly happened to validate first, or on the order they ran in. It asserts the
    /// property that matters instead: saturation changes throughput, never answers.
    /// </para>
    /// </summary>
    [Fact]
    public void IsValidDynamicPropertyNameCached_StaysCorrectPastCapacity()
    {
        // 'ä' keeps each of these off the ASCII fast path, so every one is a cache insert attempt.
        for (int i = 0; i < 5000; i++)
        {
            Assert.True(OpenTypeJsonOptions.IsValidDynamicPropertyNameCached(
                $"sättigung_{i}_{Guid.NewGuid():N}"));
        }

        // Cached before saturation, never seen, and outright invalid — in both character regimes.
        Assert.True(OpenTypeJsonOptions.IsValidDynamicPropertyNameCached("tier"));
        Assert.True(OpenTypeJsonOptions.IsValidDynamicPropertyNameCached("नाम"));
        Assert.True(OpenTypeJsonOptions.IsValidDynamicPropertyNameCached(
            "aKeyThisAssemblyHasNeverValidatedBefore"));
        Assert.True(OpenTypeJsonOptions.IsValidDynamicPropertyNameCached("ключНеВиданныйРанее"));
        Assert.False(OpenTypeJsonOptions.IsValidDynamicPropertyNameCached("@odata.type"));
        Assert.False(OpenTypeJsonOptions.IsValidDynamicPropertyNameCached("has space"));
        Assert.False(OpenTypeJsonOptions.IsValidDynamicPropertyNameCached("☃snowman"));
        Assert.False(OpenTypeJsonOptions.IsValidDynamicPropertyNameCached(""));
    }

    private static string? FindInvalidKey(string json)
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjEntity>());
        using JsonDocument doc = JsonDocument.Parse(json);
        return OpenTypeJsonOptions.FindInvalidDynamicKey(doc.RootElement, typeof(OtjEntity), options);
    }

    [Fact]
    public void FindInvalidDynamicKey_AcceptsAConformantBody() =>
        Assert.Null(FindInvalidKey("""{"Id":1,"Bag":{"Region":"eu","tier":3}}"""));

    /// <summary>
    /// A reserved key at the TOP level of a bag — <c>Bag</c> is a declared complex member of the
    /// entity, so <c>@odata.type</c> is a first-level dynamic key of <c>OtjBag</c>. (This test was
    /// named "…NestedInAComplexValue", which described the case below that it did not actually
    /// cover; #389 H1 renamed it to what it asserts and added the real nested coverage.)
    /// </summary>
    [Fact]
    public void FindInvalidDynamicKey_FindsAReservedKeyAtTheTopLevelOfABag() =>
        Assert.Equal("@odata.type", FindInvalidKey("""{"Id":1,"Bag":{"@odata.type":"#Evil"}}"""));

    // ── #389 H1: the VALUE of an accepted dynamic key is walked too ─────────────────────────────
    //
    // The check stopped at the first level of bag keys, so the stored-@odata.type vector it exists
    // to close stayed open one level down: `{"Meta":{"Region":"us","nested":{"@odata.type":"#Evil"}}}`
    // was accepted with a 201 and echoed verbatim on every later read. Everything below a dynamic
    // key is stored as given, so every object key at every depth has to satisfy the same rule.

    [Fact]
    public void FindInvalidDynamicKey_FindsAReservedKeyInsideADynamicValue() =>
        Assert.Equal(
            "@odata.type",
            FindInvalidKey("""{"Id":1,"Bag":{"Region":"us","nested":{"@odata.type":"#Evil","a":1}}}"""));

    [Fact]
    public void FindInvalidDynamicKey_FindsAReservedKeyInsideAnArrayUnderADynamicValue() =>
        Assert.Equal(
            "@odata.id",
            FindInvalidKey("""{"Id":1,"Bag":{"list":[{"ok":1},{"@odata.id":"http://evil/x"}]}}"""));

    /// <summary>Arbitrarily deep, and through alternating objects and arrays.</summary>
    [Fact]
    public void FindInvalidDynamicKey_FindsAReservedKeyManyLevelsBelowADynamicKey() =>
        Assert.Equal(
            "has space",
            FindInvalidKey("""{"Id":1,"Bag":{"a":{"b":[{"c":{"d":[{"has space":1}]}}]}}}"""));

    /// <summary>
    /// The walk must not become a blanket rejection: a dynamic value whose own keys are all
    /// conformant identifiers is still accepted at every depth. Without this the H1 fix would
    /// "pass" by rejecting every nested object.
    /// </summary>
    [Fact]
    public void FindInvalidDynamicKey_AcceptsAConformantNestedDynamicValue() =>
        Assert.Null(FindInvalidKey(
            """{"Id":1,"Bag":{"Region":"us","nested":{"inner":{"deep":[1,2,{"ok":true}]}}}}"""));

    /// <summary>
    /// Scalars and arrays of scalars under a dynamic key terminate the walk rather than tripping it.
    /// </summary>
    [Fact]
    public void FindInvalidDynamicKey_AcceptsScalarAndScalarArrayDynamicValues() =>
        Assert.Null(FindInvalidKey(
            """{"Id":1,"Bag":{"n":1,"s":"x","b":true,"nul":null,"arr":[1,"two",false,null]}}"""));

    [Fact]
    public void FindInvalidDynamicKey_FindsAnEmptyKey() =>
        Assert.Equal("", FindInvalidKey("""{"Id":1,"Bag":{"":1}}"""));

    /// <summary>
    /// Unknown members of a type that is NOT open are ignored on binding, exactly as they were
    /// before — only a key that will actually land in a dynamic bag is policed. The entity root is
    /// the case that matters: <c>@odata.context</c> and friends are legal for a client to send.
    /// </summary>
    [Fact]
    public void FindInvalidDynamicKey_IgnoresUnknownMembersOfANonOpenType() =>
        Assert.Null(FindInvalidKey("""{"@odata.context":"…","Id":1,"unknown member":true}"""));

    [Fact]
    public void FindInvalidDynamicKey_WalksIntoCollectionsOfOpenComplexTypes()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjCollectionHost>());
        using JsonDocument doc = JsonDocument.Parse("""{"Id":1,"Bags":[{"Region":"eu"},{"a b":1}]}""");
        Assert.Equal(
            "a b",
            OpenTypeJsonOptions.FindInvalidDynamicKey(doc.RootElement, typeof(OtjCollectionHost), options));
    }

    // ── #389 round-3 L1: a DICTIONARY-valued declared member is walked through ───────────────────
    //
    // The walk resolved the member's JsonTypeInfo and bailed on anything whose Kind was not Object.
    // IDictionary<string, TOpenComplex> is Kind == Dictionary, so it stopped one member short of the
    // bag that System.Text.Json binds straight into.

    [Fact]
    public void FindInvalidDynamicKey_WalksThroughADictionaryValuedMemberIntoItsValues()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjDictionaryHost>());
        using JsonDocument doc =
            JsonDocument.Parse("""{"Id":1,"Bags":{"one":{"Region":"eu"},"two":{"@odata.type":"#Evil"}}}""");
        Assert.Equal(
            "@odata.type",
            OpenTypeJsonOptions.FindInvalidDynamicKey(doc.RootElement, typeof(OtjDictionaryHost), options));
    }

    /// <summary>
    /// The dictionary's own keys are map keys of a DECLARED property, not dynamic property names, so
    /// they are deliberately not held to the identifier grammar. Only the values are walked.
    /// </summary>
    [Fact]
    public void FindInvalidDynamicKey_DoesNotPoliceTheMapKeysOfADictionaryValuedMember()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjDictionaryHost>());
        using JsonDocument doc =
            JsonDocument.Parse("""{"Id":1,"Bags":{"has space":{"Region":"eu","tier":3}}}""");
        Assert.Null(
            OpenTypeJsonOptions.FindInvalidDynamicKey(doc.RootElement, typeof(OtjDictionaryHost), options));
    }

    /// <summary>Without the opt-in no member is extension data, so nothing is ever a dynamic key.</summary>
    [Fact]
    public void FindInvalidDynamicKey_WithoutTheOpenTypeModifier_FindsNothing()
    {
        var options = new JsonSerializerOptions();
        using JsonDocument doc = JsonDocument.Parse("""{"Id":1,"Bag":{"@odata.type":"#Evil"}}""");
        Assert.Null(OpenTypeJsonOptions.FindInvalidDynamicKey(doc.RootElement, typeof(OtjEntity), options));
    }
}

// Hosts exist only to give ODataConventionModelBuilder an entity root to reach each complex type
// from; the convention builder discovers complex types through an entity set, never standalone.
/// <summary>A container declared as a custom dictionary subclass rather than the interface.</summary>
public sealed class OtjCustomBag : Dictionary<string, object?> { }

public class OtjCustomBagHolder
{
    public string? Region { get; set; }
    public OtjCustomBag? Kv { get; set; }
}

public class OtjCustomBagHost { public int Id { get; set; } public OtjCustomBagHolder? Bag { get; set; } }

/// <summary>
/// An open complex type with a container and <b>no other declared property</b>. This is the shape the
/// nameless-key check newly has to reach: the inspection pass used to bail out when the declared-name
/// set was empty, because shadowing was then the only condition it tested.
/// </summary>
public class OtjBagOnly
{
    public IDictionary<string, object?>? Kv { get; set; }
}

public class OtjBagOnlyHost { public int Id { get; set; } public OtjBagOnly? Bag { get; set; } }

/// <summary>
/// A container whose parameterless constructor SEEDS an entry — so <c>Activator.CreateInstance</c>
/// does not hand back an empty dictionary (#389 M1).
/// </summary>
public sealed class OtjDefaultingBag : Dictionary<string, object?>
{
    public OtjDefaultingBag() => this["schema"] = "v1";
}

public class OtjDefaultingBagHolder
{
    public string? Region { get; set; }
    public OtjDefaultingBag? Kv { get; set; }
}

public class OtjDefaultingBagHost
{
    public int Id { get; set; }
    public OtjDefaultingBagHolder? Bag { get; set; }
}

/// <summary>
/// #389 M2. A container PRE-SEEDED with one of the type's own declared property names — the shape
/// that used to lose every dynamic key on write while reporting success, and that now throws.
/// </summary>
public class OtjPreSeededBagHolder
{
    public string? Region { get; set; }
    public IDictionary<string, object?>? Kv { get; set; } =
        new Dictionary<string, object?> { ["Region"] = "preset" };
}

public class OtjPreSeededBagHost
{
    public int Id { get; set; }
    public OtjPreSeededBagHolder? Bag { get; set; }
}

/// <summary>A dictionary member the convention builder does NOT treat as a container.</summary>
public class OtjWrongDictionaryType { public IDictionary<string, string>? Kv { get; set; } }

public class OtjWrongDictionaryTypeHost
{
    public int Id { get; set; }
    public OtjWrongDictionaryType? Bag { get; set; }
}

public class OtjGetterOnlyHost { public int Id { get; set; } public OtjGetterOnly? Bag { get; set; } }
public class OtjCollectionHost { public int Id { get; set; } public List<OtjBag>? Bags { get; set; } }

/// <summary>
/// A DICTIONARY-valued declared member whose values are an open complex type. Its own type is
/// <c>JsonTypeInfoKind.Dictionary</c>, which is what the walk used to stop on (#389 round-3 L1).
/// <para>
/// <c>Bag</c> is not decoration. <c>ODataConventionModelBuilder</c> does <b>not</b> discover a
/// complex type through an <c>IDictionary&lt;string, T&gt;</c> member (measured: with <c>Bags</c>
/// alone the container map is empty and <c>OtjBag.Kv</c> stays an ordinary declared property, so
/// nothing under <c>Bags</c> is a bag and there is correctly nothing to police). A plain member of
/// the same type is what puts it in the EDM as open — after which the dictionary member reaches the
/// very same bag, which is the asymmetry this pins.
/// </para>
/// </summary>
public class OtjDictionaryHost
{
    public int Id { get; set; }
    public OtjBag? Bag { get; set; }
    public IDictionary<string, OtjBag>? Bags { get; set; }
}
