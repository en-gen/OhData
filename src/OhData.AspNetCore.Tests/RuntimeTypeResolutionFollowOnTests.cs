using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #507 / #497 / #344 — three follow-ons to the #462/#343 defect class ("per-type configuration
// resolved against the wrong type"), in ONE fixture, because they share the property that made the
// class invisible for so long: every suite in this area uses fixtures where the type a lookup is
// keyed by IS the type the lookup is performed with.
//
//   #507  the key is an ENTITY type while the thing being serialized is a COMPLEX type that also
//         declares navigations. RuntimeTypeConfigResolutionTests' universal invariant quantifies
//         over IEdmEntityType, so the gap was structurally invisible to it; the twin below closes
//         that.
//   #497  the key is the DECLARED element type while the runtime element type is derived.
//   #344  the key is a PropertyInfo reflected off the base while the one being compared was
//         reflected off the derived type. Closed by #462's HasSameMetadataDefinitionAs; this
//         fixture is the regression pin for the shape #344 actually reports (an entity set rooted
//         at the DERIVED type, navigations declared on the base EDM type), which no existing suite
//         has — RuntimeTypeConfigResolutionTests roots its set at the BASE type.

// ── #507 fixtures: a COMPLEX type carrying an entity-typed member ───────────────────────────────

public class PxChild
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>
/// Keyless, so <c>ODataConventionModelBuilder</c> models it as a COMPLEX type — and its
/// entity-typed <see cref="Owner"/> member as a navigation ON THAT COMPLEX TYPE
/// (<c>&lt;ComplexType Name="PxMeta"&gt;&lt;NavigationProperty Name="Owner" ...</c>).
/// </summary>
public class PxMeta
{
    public string Note { get; set; } = "";
    public PxEntity? Owner { get; set; }
}

public class PxEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public PxMeta? Meta { get; set; }
    public List<PxChild>? Children { get; set; }
}

public sealed class PxEntityProfile : EntitySetProfile<int, PxEntity>
{
    private static readonly List<PxEntity> _store = Build();
    private static readonly PxEntity _cyclic = BuildCyclic();

    // Id 2: acyclic, so the request itself succeeds and the LEAK is what is observable.
    private static List<PxEntity> Build() => new()
    {
        new PxEntity
        {
            Id = 2,
            Name = "acyclic",
            Children = new List<PxChild>(),
            Meta = new PxMeta
            {
                Note = "y",
                Owner = new PxEntity { Id = 3, Name = "OWNER-LEAK", Children = new List<PxChild>() },
            },
        },
    };

    // Id 1: a self-reference THROUGH the complex member — the 500-on-a-plain-GET half.
    private static PxEntity BuildCyclic()
    {
        var c = new PxEntity { Id = 1, Name = "cyclic", Children = new List<PxChild>() };
        c.Meta = new PxMeta { Note = "x", Owner = c };
        return c;
    }

    public PxEntityProfile() : base(x => x.Id)
    {
        EntitySetName = "PxEntities";
        ExpandEnabled = true;
        HasMany(x => x.Children!);
        GetQueryable = _ => _store.AsQueryable();
        GetById = (id, _) => OhDataResult.Success(
            id == 1 ? _cyclic : _store.FirstOrDefault(r => r.Id == id));
    }
}

// ── #497 fixtures: a bound op returning List<TDerived> for a declared IEnumerable<TModel> ───────

public class WbPart
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

public class WbWidget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<WbPart>? Parts { get; set; }
}

public class WbSpecialWidget : WbWidget
{
    public string Special { get; set; } = "";
}

public sealed class WbWidgetProfile : EntitySetProfile<int, WbWidget>
{
    private static readonly List<WbPart> _parts = new() { new WbPart { Id = 9, Label = "PART-LEAK" } };

    private static readonly List<WbWidget> _plain = new()
    {
        new WbWidget { Id = 1, Name = "plain", Parts = _parts },
    };

    private static readonly List<WbSpecialWidget> _derived = new()
    {
        new WbSpecialWidget { Id = 1, Name = "derived", Special = "s", Parts = _parts },
    };

    public WbWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "WbWidgets";
        UseETag(x => x.Name);
        HasMany(x => x.Parts!);
        GetAll = _ => OhDataResult.Success<IEnumerable<WbWidget>>(_plain);
        GetById = (id, _) => OhDataResult.Success(_plain.FirstOrDefault(w => w.Id == id));
        BindFunction(PlainList);
        BindFunction(DerivedList);
    }

    // Control: declared element type == runtime element type. Its bytes must not move.
    private Task<IEnumerable<WbWidget>> PlainList() => Task.FromResult<IEnumerable<WbWidget>>(_plain);

    // #497: declared IEnumerable<WbWidget>, runtime List<WbSpecialWidget> — the ordinary TPH shape.
    private Task<IEnumerable<WbWidget>> DerivedList() => Task.FromResult<IEnumerable<WbWidget>>(_derived);
}

// ── #344 fixtures: an entity set over a DERIVED type whose navigations are declared on the base ─

public class PbTask
{
    public int Id { get; set; }
    public string Caption { get; set; } = "";
}

public class PbTag
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
}

public class PbBase
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<PbTask>? Tasks { get; set; }
    public PbTag? Tag { get; set; }
}

public class PbDerived : PbBase
{
    public string Extra { get; set; } = "";
}

public sealed class PbDerivedProfile : EntitySetProfile<int, PbDerived>
{
    private static readonly List<PbDerived> _store = new()
    {
        new PbDerived
        {
            Id = 2,
            Name = "DerivedOne",
            Extra = "E",
            Tasks = new List<PbTask> { new() { Id = 7, Caption = "T7" } },
            Tag = new PbTag { Id = 5, Text = "G5" },
        },
    };

    public PbDerivedProfile() : base(x => x.Id)
    {
        EntitySetName = "PbDerivedRoots";
        ExpandEnabled = true;
        HasMany(x => x.Tasks!);   // declared on PbBase, reached through PbDerived
        HasOptional(x => x.Tag!); // ditto, single-valued
        GetQueryable = _ => _store.AsQueryable();
        GetById = (id, _) => OhDataResult.Success(_store.FirstOrDefault(r => r.Id == id));
    }
}

/// <summary>
/// Sibling set over the BASE type. Without it <c>ODataConventionModelBuilder</c> flattens
/// <c>Tasks</c>/<c>Tag</c> onto <c>PbDerived</c> and the fixture tests something else; with it the
/// EDM really carries <c>&lt;EntityType Name="PbDerived" BaseType="…PbBase"&gt;</c> with both
/// navigations declared on <c>PbBase</c> — #344's shape exactly. It is also the control: a
/// non-derived row in a base-rooted set was never affected.
/// </summary>
public sealed class PbBaseProfile : EntitySetProfile<int, PbBase>
{
    private static readonly List<PbBase> _store = new()
    {
        new PbBase { Id = 1, Name = "BaseOne", Tasks = new List<PbTask>(), Tag = null },
    };

    public PbBaseProfile() : base(x => x.Id)
    {
        EntitySetName = "PbBaseRoots";
        ExpandEnabled = true;
        HasMany(x => x.Tasks!);
        HasOptional(x => x.Tag!);
        GetQueryable = _ => _store.AsQueryable();
        GetById = (id, _) => OhDataResult.Success(_store.FirstOrDefault(r => r.Id == id));
    }
}

public sealed class RuntimeTypeResolutionFollowOnTests
{
    private static Task<TestFixture> BuildAsync() =>
        TestHostBuilder.BuildAsync(b =>
        {
            b.AddEntitySetProfile<PxEntityProfile>();
            b.AddEntitySetProfile<WbWidgetProfile>();
            b.AddEntitySetProfile<PbBaseProfile>();
            b.AddEntitySetProfile<PbDerivedProfile>();
        });

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(TestFixture f, string url)
    {
        HttpResponseMessage r = await f.Client.GetAsync(url);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    // ── The premise all three rest on ───────────────────────────────────────────────────────────

    /// <summary>
    /// The EDM really is the three shapes claimed above: a COMPLEX type that declares a navigation,
    /// a derived entity type discovered off the bound operation's model type, and a derived entity
    /// type whose base declares the navigations. Asserted rather than assumed, because each fix is
    /// only well-defined against the shape it names.
    /// </summary>
    [Fact]
    public async Task TheEdmReallyHasTheThreeShapes()
    {
        await using TestFixture f = await BuildAsync();
        string xml = await f.Client.GetStringAsync("/odata/$metadata");

        // #507: a navigation on a COMPLEX type. This is the whole premise — an entity-only walk of
        // the schema cannot see it.
        Assert.Contains("<ComplexType Name=\"PxMeta\">", xml);
        Assert.Contains(
            "<NavigationProperty Name=\"Owner\" Type=\"OhData.AspNetCore.Tests.PxEntity\" />", xml);

        // #497: the derived model type is in the EDM, and the bound function's DECLARED return type
        // is the collection of the base — which is what AddBoundOperationProduces documents.
        Assert.Contains(
            "<EntityType Name=\"WbSpecialWidget\" BaseType=\"OhData.AspNetCore.Tests.WbWidget\">", xml);
        Assert.Contains(
            "<ReturnType Type=\"Collection(OhData.AspNetCore.Tests.WbWidget)\" />", xml);

        // #344: PbDerived derives from PbBase and the navigations live on the BASE type.
        Assert.Contains("<EntityType Name=\"PbDerived\" BaseType=\"OhData.AspNetCore.Tests.PbBase\">", xml);
        int basePos = xml.IndexOf("<EntityType Name=\"PbBase\">", StringComparison.Ordinal);
        int derivedPos = xml.IndexOf("<EntityType Name=\"PbDerived\"", StringComparison.Ordinal);
        Assert.InRange(xml.IndexOf("<NavigationProperty Name=\"Tasks\"", basePos, StringComparison.Ordinal),
            basePos, derivedPos);
    }

    // ── #507: a complex type's own navigation ───────────────────────────────────────────────────

    /// <summary>
    /// Pre-fix (measured on the shipped 1.6.0 tree):
    /// <c>…"Meta":{"Note":"y","Owner":{"Id":3,"Name":"OWNER-LEAK","Meta":null}}}</c> — navigation
    /// data served inline on a plain GET with no query string, which §4.5.1 / §11.2.4.2 forbid.
    /// <para>
    /// Note what WAS already right, because it is what made the gap look closed: the entity reached
    /// THROUGH the member had its own navigation (<c>Children</c>) suppressed correctly. That is
    /// precisely what #491's pre-fix measurement covered.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ComplexTypeNavigation_IsOmitted_OnAPlainGet()
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, "/odata/PxEntities(2)");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Owner", body);
        Assert.DoesNotContain("OWNER-LEAK", body);
        // The complex member itself is still served — suppression is about the navigation, not the
        // complex value that carries it.
        Assert.Contains("\"Meta\":{\"Note\":\"y\"}", body);
    }

    /// <summary>
    /// The other half, and the one that takes the service down: an entity referencing itself through
    /// its own complex member. Pre-fix this was <c>500</c> +
    /// <c>{"error":{"code":"InternalServerError",…}}</c> — the group filter rendering
    /// <c>JsonException: A possible object cycle was detected</c> — on EVERY request, with no query
    /// string involved. #325/#326's premise is that no navigation reaches System.Text.Json unless a
    /// clause asked for it; a complex type's navigation was outside that premise entirely.
    /// </summary>
    [Fact]
    public async Task ACycleThroughAComplexTypesNavigation_IsNotA500()
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, "/odata/PxEntities(1)");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Owner", body);
        Assert.Contains("\"Meta\":{\"Note\":\"x\"}", body);
    }

    /// <summary>
    /// The universal invariant, widened. <c>RuntimeTypeConfigResolutionTests</c> states it over
    /// every EDM <b>entity</b> type; #507 is the proof that the entity-only quantifier is a hole
    /// rather than a simplification, so it is stated here over every EDM <b>complex</b> type as
    /// well. After <c>MapOhData()</c>'s schema walk and with nothing serialized, no complex type's
    /// contract on the nav-suppressed options carries an EDM navigation.
    /// </summary>
    [Fact]
    public void EveryEdmComplexType_ResolvesSuppressed_BeforeAnythingIsSerialized()
    {
        var mb = new ODataConventionModelBuilder();
        mb.EntitySet<PxEntity>("PxEntities");
        mb.EntitySet<PxChild>("PxChildren");
        IEdmModel model = mb.GetEdmModel();

        // The premise: there really is a complex type here, and it really does declare a navigation.
        IEdmComplexType complex = Assert.Single(
            model.SchemaElements.OfType<IEdmComplexType>(), c => c.NavigationProperties().Any());
        Assert.Equal(typeof(PxMeta), model.GetAnnotationValue<ClrTypeAnnotation>(complex)?.ClrType);

        JsonSerializerOptions derived = NavSuppressedOptions(model, typeof(PxChild));

        var leaks = new List<string>();
        foreach (IEdmComplexType c in model.SchemaElements.OfType<IEdmComplexType>())
        {
            Type? clr = model.GetAnnotationValue<ClrTypeAnnotation>(c)?.ClrType;
            if (clr is null) continue;
            foreach (IEdmNavigationProperty nav in c.NavigationProperties())
            {
                if (derived.GetTypeInfo(clr).Properties
                    .Any(p => (p.AttributeProvider as PropertyInfo)?.Name == nav.Name))
                {
                    leaks.Add($"{clr.Name}.{nav.Name}");
                }
            }
        }
        Assert.Empty(leaks);
    }

    /// <summary>
    /// Production's own two steps in production's own order, mirroring
    /// <c>RuntimeTypeConfigResolutionTests.NavSuppressedOptions</c>: <c>MapAll</c>'s schema walk,
    /// then the derived options a route closure would obtain. <paramref name="probeClrType"/> is the
    /// only type ever handed to <c>GetNavSuppressedOptions</c>, so every type in the assertions
    /// above has been reached by nothing at all.
    /// </summary>
    private static JsonSerializerOptions NavSuppressedOptions(IEdmModel model, Type probeClrType)
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Static;
        Type factory = typeof(OhDataRegistration).Assembly.GetType("OhData.OhDataEndpointFactory", true)!;
        var baseOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        factory.GetMethod("PrimeNavSuppression", Any)!.Invoke(null, new object?[] { baseOptions, model });

        IEdmEntityType probeEdmType = model.SchemaElements.OfType<IEdmEntityType>()
            .Single(e => model.GetAnnotationValue<ClrTypeAnnotation>(e)?.ClrType == probeClrType);
        return (JsonSerializerOptions)factory.GetMethod("GetNavSuppressedOptions", Any)!
            .Invoke(null, new object?[] { baseOptions, model, probeEdmType, probeClrType })!;
    }

    // ── #497: a bound op whose runtime element type is derived ──────────────────────────────────

    /// <summary>
    /// Pre-fix (measured): <c>[{"Special":"s","Id":1,"Name":"derived","Parts":[{"Id":9,
    /// "Label":"PART-LEAK"}]}]</c> — a bare array with no <c>@odata.context</c>, no <c>value</c>
    /// envelope, the declared navigation <c>Parts</c> inline, and no <c>@odata.etag</c>. It fell out
    /// of every branch of <c>WrapBoundOpResult</c> because the collection branch tested the element
    /// type with <c>==</c> while the single-entity branch beside it already used
    /// <c>IsAssignableFrom</c>.
    /// </summary>
    [Fact]
    public async Task BoundOpReturningADerivedCollection_GetsTheCollectionEnvelope()
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, "/odata/WbWidgets/DerivedList");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("{\"@odata.context\":\"http://localhost/odata/$metadata#WbWidgets\",\"value\":[", body);
        // §4.5.1: a bound op takes no $expand, so every declared navigation is omitted.
        Assert.DoesNotContain("Parts", body);
        Assert.DoesNotContain("PART-LEAK", body);
        // #179: per-item ETag injection, which the raw-graph branch also skipped.
        Assert.Contains("\"@odata.etag\":", body);
        // The derived instance's own scalar is still served — this is about navigations and the
        // envelope, not about hiding derived members.
        Assert.Contains("\"Special\":\"s\"", body);
    }

    /// <summary>
    /// BYTE-IDENTITY CONTROL, captured from the PRE-FIX build (1.6.0 + the probe fixture, before any
    /// source change) and pasted verbatim. The declared element type equals the runtime element type
    /// here, so widening the test from equality to assignability must not move a single byte.
    /// </summary>
    [Fact]
    public async Task ByteIdentity_BoundOpReturningTheDeclaredCollection_IsUnchanged()
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, "/odata/WbWidgets/PlainList");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            "{\"@odata.context\":\"http://localhost/odata/$metadata#WbWidgets\",\"value\":" +
            "[{\"@odata.etag\":\"\\\"oUTC4t9g0I0Va1FsHgK+dD7fAsng2RVfOh0CxibIYxE=\\\"\"," +
            "\"Id\":1,\"Name\":\"plain\"}]}",
            body);
    }

    // ── #344: an INHERITED navigation under $expand ─────────────────────────────────────────────

    /// <summary>
    /// #344's reported symptom, on #344's reported shape: <c>PbDerivedRoots?$expand=Tasks</c> came
    /// back with no <c>Tasks</c> key at all — HTTP 200, missing data, no diagnostic — because
    /// <c>IsNavVisibleInBaseOptions</c> compared <c>PropertyInfo</c>s with <c>!=</c>, which also
    /// compares <c>ReflectedType</c>, and for an inherited navigation the EDM-side and the
    /// System.Text.Json-side reflection walks disagree about it.
    /// <para>
    /// <b>This is a REGRESSION PIN, not a reproduction against the current tree.</b> #462 closed the
    /// comparison site (<c>HasSameMetadataDefinitionAs</c>) as a follow-on its own issue did not
    /// name, which closed #344 with it. What was missing was coverage of #344's own shape: every
    /// existing fixture roots its entity set at the BASE type, where the two walks agree.
    /// VERIFIED to fail by restoring the single <c>!=</c> at
    /// <c>OhDataEndpointFactory.IsNavVisibleInBaseOptions</c> — both cases below then come back
    /// with the navigation key absent, while <see cref="ByteIdentity_TheBaseRootedSet_IsUnchanged"/>
    /// stays green.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("/odata/PbDerivedRoots?$expand=Tasks", "\"Tasks\":[{\"Id\":7,\"Caption\":\"T7\"}]")]
    [InlineData("/odata/PbDerivedRoots(2)?$expand=Tasks", "\"Tasks\":[{\"Id\":7,\"Caption\":\"T7\"}]")]
    [InlineData("/odata/PbDerivedRoots?$expand=Tag", "\"Tag\":{\"Id\":5,\"Text\":\"G5\"}")]
    [InlineData("/odata/PbDerivedRoots(2)?$expand=Tag", "\"Tag\":{\"Id\":5,\"Text\":\"G5\"}")]
    public async Task InheritedNavigation_IsServed_WhenTheSetIsRootedAtTheDerivedType(
        string url, string expectedFragment)
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, url);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(expectedFragment, body);
        // The derived type's own scalar is unaffected either way — the defect was navigation-only.
        Assert.Contains("\"Extra\":\"E\"", body);
    }

    /// <summary>
    /// The other direction of the same rule, on the same rows: an inherited navigation the clause
    /// did NOT name is still omitted (§4.5.1). A fix that made inherited navigations visible by
    /// disabling suppression for them would pass the test above and fail this one.
    /// </summary>
    [Fact]
    public async Task InheritedNavigation_IsStillOmitted_WithoutAnExpand()
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, "/odata/PbDerivedRoots");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Tasks", body);
        Assert.DoesNotContain("Tag", body);
    }

    /// <summary>
    /// BYTE-IDENTITY CONTROL, captured from the PRE-FIX build and pasted verbatim. Nothing in the
    /// base-rooted set ever has a derived runtime instance, a complex-typed navigation, or a bound
    /// operation, so none of the three fixes may move a byte of it.
    /// </summary>
    [Fact]
    public async Task ByteIdentity_TheBaseRootedSet_IsUnchanged()
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, "/odata/PbBaseRoots?$expand=Tasks");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            "{\"@odata.context\":\"http://localhost/odata/$metadata#PbBaseRoots\",\"value\":" +
            "[{\"Id\":1,\"Name\":\"BaseOne\",\"Tasks\":[]}]}",
            body);
    }

    /// <summary>
    /// BYTE-IDENTITY CONTROL for the derived-rooted set with no query string — the shape #507's map
    /// widening touches (its contract is resolved through the same seeded map) but must not change.
    /// Captured from the PRE-FIX build and pasted verbatim.
    /// </summary>
    [Fact]
    public async Task ByteIdentity_TheDerivedRootedSet_PlainGet_IsUnchanged()
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, "/odata/PbDerivedRoots");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            "{\"@odata.context\":\"http://localhost/odata/$metadata#PbDerivedRoots\",\"value\":" +
            "[{\"Extra\":\"E\",\"Id\":2,\"Name\":\"DerivedOne\"}]}",
            body);
    }
}
