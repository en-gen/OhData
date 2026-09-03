using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.Edm.Vocabularies;
using Microsoft.OData.Edm.Vocabularies.V1;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── Fixtures ─────────────────────────────────────────────────────────────────
//
// Three profiles covering the three shapes the annotation has to get right:
//   CapDefault  — nothing overridden: inherits EntitySetDefaults (MaxExpansionDepth 3,
//                 ExpandEnabled false).
//   CapExpandOn — ExpandEnabled = true, MaxExpandTop = 25. The MaxExpandTop override is the
//                 point of #303 and must produce NO annotation, because no standard term
//                 expresses it.
//   CapCeiling  — MaxExpansionDepth at the #328 ceiling (6), ExpandEnabled = true.

internal class CapItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<CapChild> Children { get; set; } = new();
}

internal class CapChild
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

internal class CapDefaultProfile : EntitySetProfile<int, CapItem>
{
    public CapDefaultProfile() : base(x => x.Id)
    {
        EntitySetName = "CapDefaults";
        GetAll = (ct) => OhDataResult.SuccessTask<IEnumerable<CapItem>>(Array.Empty<CapItem>());
    }
}

internal class CapExpandOnProfile : EntitySetProfile<int, CapItem>
{
    public CapExpandOnProfile() : base(x => x.Id)
    {
        EntitySetName = "CapExpandOns";
        ExpandEnabled = true;
        MaxExpandTop = 25;
        GetAll = (ct) => OhDataResult.SuccessTask<IEnumerable<CapItem>>(Array.Empty<CapItem>());
        HasMany(x => x.Children, (key, ct) => Task.FromResult<IEnumerable<CapChild>>(Array.Empty<CapChild>()));
    }
}

internal class CapCeilingProfile : EntitySetProfile<int, CapItem>
{
    public CapCeilingProfile() : base(x => x.Id)
    {
        EntitySetName = "CapCeilings";
        ExpandEnabled = true;
        MaxExpansionDepth = EntitySetDefaults.MaxExpansionDepthCeiling; // 6, the #328 ceiling
        GetAll = (ct) => OhDataResult.SuccessTask<IEnumerable<CapItem>>(Array.Empty<CapItem>());
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

/// <summary>
/// #303 / #367. What <c>$metadata</c> may and may not say about the runtime query-capability
/// gates, and why. The central claim these tests defend is a NEGATIVE one: the numeric ceilings
/// (<c>MaxExpandTop</c>, <c>MaxExpandBreadth</c>, <c>MaxTop</c>) have no
/// <c>Org.OData.Capabilities.V1</c> term, so they must be advertised NOWHERE rather than
/// approximated. A future change that "helpfully" maps one onto <c>MaxLevels</c> or
/// <c>TopSupported</c> is the regression this file exists to catch.
/// </summary>
public class Issue303CapabilityAnnotationTests
{
    private static readonly XNamespace Edmx = "http://docs.oasis-open.org/odata/ns/edmx";
    private static readonly XNamespace Edm = "http://docs.oasis-open.org/odata/ns/edm";

    private const string ExpandRestrictions = "Org.OData.Capabilities.V1.ExpandRestrictions";

    private static async Task<TestFixture> BuildAsync() =>
        await TestHostBuilder.BuildAsync(o => o
            .AddEntitySetProfile<CapDefaultProfile>()
            .AddEntitySetProfile<CapExpandOnProfile>()
            .AddEntitySetProfile<CapCeilingProfile>());

    private static async Task<XDocument> MetadataAsync(HttpClient client)
    {
        HttpResponseMessage resp = await client.GetAsync("/odata/$metadata");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return XDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static XElement EntitySet(XDocument doc, string name) =>
        doc.Root!.Element(Edmx + "DataServices")!
            .Elements(Edm + "Schema")
            .Elements(Edm + "EntityContainer")
            .Elements(Edm + "EntitySet")
            .Single(e => (string)e.Attribute("Name")! == name);

    /// <summary>
    /// The record properties of the inline ExpandRestrictions annotation on one entity set, as
    /// (name, value) pairs in document order. Reads the CSDL rather than the in-memory model, so
    /// it asserts what a client actually receives.
    /// </summary>
    private static (string Name, string Value)[] ExpandRestrictionsOf(XDocument doc, string entitySet)
    {
        XElement? annotation = EntitySet(doc, entitySet)
            .Elements(Edm + "Annotation")
            .SingleOrDefault(a => (string?)a.Attribute("Term") == ExpandRestrictions);
        if (annotation is null) return Array.Empty<(string, string)>();

        return annotation.Elements(Edm + "Record")
            .Elements(Edm + "PropertyValue")
            .Select(p => (
                Name: (string)p.Attribute("Property")!,
                // The value rides on whichever typed attribute the writer chose (Bool/Int/String).
                Value: p.Attributes()
                    .Where(a => a.Name.LocalName != "Property")
                    .Select(a => a.Value)
                    .Single()))
            .ToArray();
    }

    // ── What IS advertised ────────────────────────────────────────────────────

    /// <summary>
    /// A profile that overrides nothing advertises the RESOLVED EntitySetDefaults values, not the
    /// term's own defaults: MaxLevels = 3, and Expandable = false because ExpandEnabled defaults
    /// to false. Both halves matter — advertising MaxLevels alone (the pre-#303 behaviour) told a
    /// client "expand up to 3 levels" for a set that 400s every $expand.
    /// </summary>
    [Fact]
    public async Task DefaultProfile_AdvertisesResolvedDepth_AndExpandableFalse()
    {
        await using TestFixture fx = await BuildAsync();
        XDocument doc = await MetadataAsync(fx.Client);

        Assert.Equal(
            new[] { ("Expandable", "false"), ("MaxLevels", "3") },
            ExpandRestrictionsOf(doc, "CapDefaults"));
    }

    /// <summary>
    /// With $expand enabled, Expandable is OMITTED rather than emitted as true — `true` is the
    /// vocabulary's own default for that property, so emitting it would add bytes and assert
    /// nothing. This omission is also what keeps every already-correct entity set byte-identical
    /// across #303.
    /// </summary>
    [Fact]
    public async Task ExpandEnabledProfile_OmitsExpandable_AndKeepsMaxLevels()
    {
        await using TestFixture fx = await BuildAsync();
        XDocument doc = await MetadataAsync(fx.Client);

        Assert.Equal(
            new[] { ("MaxLevels", "3") },
            ExpandRestrictionsOf(doc, "CapExpandOns"));
    }

    /// <summary>
    /// A profile-level MaxExpansionDepth override at the #328 ceiling is advertised as the
    /// resolved 6, not the EntitySetDefaults 3 — the whole point of resolving per entity set.
    /// </summary>
    [Fact]
    public async Task CeilingProfile_AdvertisesTheOverriddenDepth_NotTheDefault()
    {
        await using TestFixture fx = await BuildAsync();
        XDocument doc = await MetadataAsync(fx.Client);

        Assert.Equal(
            new[] { ("MaxLevels", "6") },
            ExpandRestrictionsOf(doc, "CapCeilings"));
        // And the three sets really do differ, so this is not vacuously passing on a shared value.
        Assert.NotEqual(
            ExpandRestrictionsOf(doc, "CapCeilings"),
            ExpandRestrictionsOf(doc, "CapDefaults"));
    }

    // ── What is deliberately NOT advertised (#303's actual answer) ────────────

    /// <summary>
    /// #303's headline ask, answered in the negative. CapExpandOns sets MaxExpandTop = 25 and
    /// nothing anywhere in the CSDL says 25 — because Org.OData.Capabilities.V1 has no term for a
    /// maximum result COUNT. Asserting over the whole document (not just the one entity set)
    /// catches an approximation smuggled in under any term.
    /// </summary>
    [Fact]
    public async Task MaxExpandTop_IsAdvertisedNowhere_BecauseNoStandardTermExpressesIt()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/$metadata");
        string csdl = await resp.Content.ReadAsStringAsync();

        Assert.DoesNotContain("25", csdl, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxExpandTop", csdl, StringComparison.Ordinal);
        Assert.DoesNotContain("TopSupported", csdl, StringComparison.Ordinal);
        Assert.DoesNotContain("SkipSupported", csdl, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default MaxExpandBreadth is 50 and the default MaxTop is 1000; neither is expressible,
    /// so neither may appear. Guards the same "helpful approximation" regression from the two
    /// limits #367 will be tempted to map onto MaxLevels.
    /// </summary>
    [Fact]
    public async Task MaxExpandBreadthAndMaxTop_AreAdvertisedNowhere()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/$metadata");
        string csdl = await resp.Content.ReadAsStringAsync();

        Assert.DoesNotContain("50", csdl, StringComparison.Ordinal);
        Assert.DoesNotContain("1000", csdl, StringComparison.Ordinal);
    }

    /// <summary>
    /// No custom vocabulary is minted. Every annotation OhData writes is drawn from a namespace
    /// OASIS owns, so a client that understands standard OData understands all of it.
    /// </summary>
    [Fact]
    public async Task EveryAnnotationTerm_ComesFromAStandardOasisVocabulary()
    {
        await using TestFixture fx = await BuildAsync();
        XDocument doc = await MetadataAsync(fx.Client);

        string[] terms = doc.Descendants(Edm + "Annotation")
            .Select(a => (string)a.Attribute("Term")!)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(terms);
        Assert.All(terms, t => Assert.StartsWith("Org.OData.", t, StringComparison.Ordinal));
    }

    // ── The vocabulary claim itself, asserted rather than assumed ─────────────

    /// <summary>
    /// The load-bearing premise of every "not expressible" decision above, pinned against the
    /// Capabilities vocabulary actually shipping in the Microsoft.OData.Edm this repo resolves.
    /// If a future Edm bump introduces a numeric term that could express a result-count ceiling,
    /// this test fails and #303 becomes implementable — that is exactly the signal we want, and
    /// it is why the claim is a live assertion rather than a comment.
    /// </summary>
    [Fact]
    public void CapabilitiesVocabulary_HasNoNumericTermOtherThanMaxLevels()
    {
        IEnumerable<IEdmStructuredType> structuredTypes = CapabilitiesVocabularyModel.Instance
            .SchemaElements.OfType<IEdmStructuredType>();

        (string Type, string Property)[] numeric = structuredTypes
            .SelectMany(t => t.DeclaredProperties.Select(p => (Type: ((IEdmSchemaType)t).Name, Property: p)))
            .Where(x => x.Property.Type.Definition is IEdmPrimitiveType prim && IsNumeric(prim.PrimitiveKind))
            .Select(x => (x.Type, x.Property.Name))
            .ToArray();

        Assert.NotEmpty(numeric); // the walk really found properties
        Assert.All(numeric, x => Assert.Equal("MaxLevels", x.Property));
    }

    /// <summary>
    /// MaxLevels means DEPTH, in every record type that carries it. This is the reason
    /// MaxExpandTop (a count of entities) cannot borrow it, stated as an assertion over the
    /// vocabulary's own Core.Description rather than as an opinion in a comment.
    /// </summary>
    [Fact]
    public void MaxLevels_IsDocumentedAsADepthLimit_NotAResultCount()
    {
        IEdmModel vocab = CapabilitiesVocabularyModel.Instance;
        IEdmComplexType expandRestrictions = vocab.SchemaElements
            .OfType<IEdmComplexType>()
            .Single(t => t.Name == "ExpandRestrictionsType");

        IEdmProperty maxLevels = expandRestrictions.DeclaredProperties.Single(p => p.Name == "MaxLevels");
        string description = vocab.GetDescriptionAnnotation(maxLevels) ?? "";

        Assert.Contains("levels", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("count", description, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumeric(EdmPrimitiveTypeKind kind) => kind is
        EdmPrimitiveTypeKind.Byte or EdmPrimitiveTypeKind.SByte or
        EdmPrimitiveTypeKind.Int16 or EdmPrimitiveTypeKind.Int32 or EdmPrimitiveTypeKind.Int64 or
        EdmPrimitiveTypeKind.Single or EdmPrimitiveTypeKind.Double or EdmPrimitiveTypeKind.Decimal;

    // ── The CSDL is valid, not merely present ────────────────────────────────

    /// <summary>
    /// The emitted document parses back through the OData CSDL reader with zero errors, and the
    /// annotation survives the round trip as a record with the expected properties. Eyeballing the
    /// XML would not catch a term reference that no reader can resolve.
    /// </summary>
    [Fact]
    public async Task Metadata_ParsesAsValidCsdl_AndTheAnnotationRoundTrips()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/$metadata");
        string csdl = await resp.Content.ReadAsStringAsync();

        using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(csdl));
        bool parsed = CsdlReader.TryParse(reader, out IEdmModel? model, out IEnumerable<Microsoft.OData.Edm.Validation.EdmError> errors);

        Assert.True(parsed, "CSDL failed to parse: " + string.Join("; ", errors.Select(e => e.ErrorMessage)));
        Assert.Empty(errors);

        IEdmEntitySet set = model!.EntityContainer.FindEntitySet("CapDefaults");
        IEdmVocabularyAnnotation annotation = model.FindVocabularyAnnotations(set)
            .Single(a => a.Term.FullName() == ExpandRestrictions);

        var record = Assert.IsAssignableFrom<IEdmRecordExpression>(annotation.Value);
        Assert.Equal(
            new[] { "Expandable", "MaxLevels" },
            record.Properties.Select(p => p.Name).ToArray());
    }
}
