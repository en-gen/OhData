using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// A bag key equal to a declared property's JSON name used to emit that name twice in one JSON
    /// object — invalid OData, and on every .NET reader tested the BAG entry won, making the
    /// declared value unreachable. The declared property wins now and the bag entry is dropped.
    /// </summary>
    [Fact]
    public void Build_ABagKeyShadowingADeclaredProperty_IsOmittedAndTheDeclaredValueWins()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjEntity>());

        string json = JsonSerializer.Serialize(
            new OtjBag
            {
                Region = "declared",
                Kv = new Dictionary<string, object?> { ["Region"] = "fromBag", ["ok"] = 1 },
            },
            options);

        Assert.Equal("""{"Region":"declared","ok":1}""", json);

        // Re-parsing is the point: the old output was not merely ugly, it was a duplicate JSON key.
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("declared", doc.RootElement.GetProperty("Region").GetString());
    }

    /// <summary>
    /// Ordinal, not case-insensitive: suppressing a merely case-differing key would be silent data
    /// loss rather than a fix, and it is the byte-for-byte repeat that produces invalid JSON.
    /// </summary>
    [Fact]
    public void Build_ABagKeyDifferingOnlyByCase_IsNotSuppressed()
    {
        JsonSerializerOptions options =
            OpenTypeJsonOptions.Build(new JsonSerializerOptions(), Containers<OtjEntity>());

        string json = JsonSerializer.Serialize(
            new OtjBag { Region = "declared", Kv = new Dictionary<string, object?> { ["region"] = "kept" } },
            options);

        Assert.Equal("""{"Region":"declared","region":"kept"}""", json);
    }

    /// <summary>
    /// The suppression must preserve the container's runtime type. System.Text.Json resolves the
    /// extension-data converter from the DECLARED property type and casts the getter's return value
    /// back to it, so substituting a plain <c>Dictionary</c> for a container declared as
    /// <c>OtjCustomBag : Dictionary&lt;string, object?&gt;</c> throws <c>InvalidCastException</c>
    /// mid-serialization — i.e. a 500 on a read, which is the outcome this whole guard exists to
    /// avoid.
    /// </summary>
    [Fact]
    public void Build_SuppressionPreservesACustomContainerRuntimeType()
    {
        OpenTypeJsonOptions.OpenComplexTypeContainers containers = Containers<OtjCustomBagHost>();
        // Guard against the test passing vacuously: the builder must actually have inferred the
        // custom subclass as this type's dynamic-property container.
        Assert.Equal(typeof(OtjCustomBag), containers.ByDeclaringType[typeof(OtjCustomBagHolder)].PropertyType);

        JsonSerializerOptions options = OpenTypeJsonOptions.Build(new JsonSerializerOptions(), containers);

        string json = JsonSerializer.Serialize(
            new OtjCustomBagHolder
            {
                Region = "declared",
                Kv = new OtjCustomBag { ["Region"] = "fromBag", ["ok"] = 1 },
            },
            options);

        Assert.Equal("""{"Region":"declared","ok":1}""", json);
    }

    /// <summary>
    /// #389 M1. <c>TryCreateEmptyLike</c> assumed a freshly constructed container was empty, and the
    /// caller then used <c>IDictionary.Add</c>. A container whose parameterless constructor SEEDS an
    /// entry breaks both halves: the clone starts non-empty, <c>Add</c> hits the duplicate, and a
    /// plain <c>GET</c> faults with <c>ArgumentException</c> — a <b>500 on a read</b>, refuting both
    /// the "caller degrades rather than throwing" comment and <c>docs/open-types.md</c>'s "a read is
    /// never faulted over a data condition". Clearing the clone and assigning through the indexer is
    /// what makes those true.
    /// </summary>
    [Fact]
    public void Build_SuppressionSurvivesAContainerWhoseConstructorSeedsEntries()
    {
        OpenTypeJsonOptions.OpenComplexTypeContainers containers = Containers<OtjDefaultingBagHost>();
        // Premise: the seeding subclass really is what the builder inferred as the container.
        Assert.Equal(
            typeof(OtjDefaultingBag),
            containers.ByDeclaringType[typeof(OtjDefaultingBagHolder)].PropertyType);

        JsonSerializerOptions options = OpenTypeJsonOptions.Build(new JsonSerializerOptions(), containers);

        // "Region" shadows the declared property, so the suppressing clone path is taken; "schema"
        // is the key the constructor seeds, so the clone starts out already containing it.
        string json = JsonSerializer.Serialize(
            new OtjDefaultingBagHolder
            {
                Region = "declared",
                Kv = new OtjDefaultingBag { ["Region"] = "fromBag", ["ok"] = 1 },
            },
            options);

        // No fault, the declared property wins, and the seeded key is carried across exactly once.
        Assert.Equal("""{"Region":"declared","schema":"v1","ok":1}""", json);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("declared", doc.RootElement.GetProperty("Region").GetString());
    }

    /// <summary>
    /// The suppressing getter must hand back the SAME dictionary when there is nothing to suppress —
    /// System.Text.Json calls it on the deserialize path too, to find an existing bag to populate.
    /// </summary>
    [Fact]
    public void Build_BindingIsUnaffectedByTheShadowSuppressingGetter()
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
    /// name. Since open types are opt-in, that is a startup failure, not a silent skip.
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

    /// <summary>An unpaired surrogate is not a character in any category, so it is rejected.</summary>
    [Fact]
    public void IsValidDynamicPropertyName_RejectsAnUnpairedSurrogate() =>
        Assert.False(OpenTypeJsonOptions.IsValidDynamicPropertyName("a\uD800b"));

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

/// <summary>A dictionary member the convention builder does NOT treat as a container.</summary>
public class OtjWrongDictionaryType { public IDictionary<string, string>? Kv { get; set; } }

public class OtjWrongDictionaryTypeHost
{
    public int Id { get; set; }
    public OtjWrongDictionaryType? Bag { get; set; }
}

public class OtjGetterOnlyHost { public int Id { get; set; } public OtjGetterOnly? Bag { get; set; } }
public class OtjCollectionHost { public int Id { get; set; } public List<OtjBag>? Bags { get; set; } }
