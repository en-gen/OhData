using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #462 + #343 — ONE fixture, both defects, because they are ONE defect: per-type configuration
// resolved by the EXACT CLR type (or by the DECLARED EDM type) while both serialization substrates
// resolve the RUNTIME type — the batched collection path hands System.Text.Json an `object`-declared
// element, and the single-entity path calls SerializeToNode(value, value.GetType(), ...). A derived
// instance misses the configuration entirely. #293 (closed) was the first instance of the same class.
//
// WHY NO EXISTING SUITE CAUGHT EITHER. Every serialization suite in this area — ignore, open-type,
// modifier-ordering, nav-suppression — uses fixtures where runtime type == declared type. The ONE
// suite that varies runtime type (PolymorphicExpandSerializationTests) gives its derived types only
// SCALAR members, so neither an inherited withheld property nor a derived-declared navigation was
// ever in the picture. This fixture is exactly that missing shape and nothing else: a derived
// instance carrying an INHERITED Ignore()d member AND a navigation declared only on the derived type.

public class RtBase
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
    public List<RtItem>? Items { get; set; }
}

public class RtDerived : RtBase
{
    public string Extra { get; set; } = "";

    // Declared ONLY on the derived type, so it lands on the DERIVED EDM type (see the $metadata
    // assertion below). #343: never in the declared type's navigation set, so never suppressed.
    public List<RtNote>? Notes { get; set; }
}

public class RtItem
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

public class RtNote
{
    public int Id { get; set; }
    public string Body { get; set; } = "";
}

// #343's cycle shape: two DERIVED instances referencing each other through a derived-declared
// single-valued navigation. Pre-fix this is a 500 on a plain GET with no query string at all — the
// exact failure mode #325/#326 were built to make structurally unreachable.
public class RtCycleBase
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<RtItem>? Items { get; set; }
}

public class RtCycleDerived : RtCycleBase
{
    public RtCycleDerived? Buddy { get; set; }
}

// ── Byte-identity control ───────────────────────────────────────────────────────────────────────
// Nothing in this entity set ever has a derived runtime instance, so the fix must not move a single
// byte of any of its responses. It carries an Ignore()d property, a navigation AND an open complex
// type on purpose: those are the three features whose lookups moved, and the assertions below were
// captured from the PRE-FIX build (see ByteIdentity_*), not written to match the post-fix output.
public class RtPlain
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
    public RtPlainSpec? Spec { get; set; }
    public List<RtItem>? Items { get; set; }
}

public class RtPlainSpec
{
    public string Material { get; set; } = "";
    public IDictionary<string, object?>? Extras { get; set; }
}

internal static class RtData
{
    internal static List<RtBase> Rows()
    {
        var items = new List<RtItem> { new() { Id = 10, Label = "L10" } };
        return new List<RtBase>
        {
            new RtBase { Id = 1, Name = "base", Secret = "S1-LEAK", Items = items },
            new RtDerived
            {
                Id = 2, Name = "derived", Secret = "S2-LEAK", Extra = "e",
                Items = items,
                Notes = new List<RtNote> { new() { Id = 1, Body = "N1" } },
            },
        };
    }

    internal static List<RtCycleBase> CycleRows()
    {
        var a = new RtCycleDerived { Id = 1, Name = "A", Items = new List<RtItem>() };
        var b = new RtCycleDerived { Id = 2, Name = "B", Items = new List<RtItem>() };
        a.Buddy = b;
        b.Buddy = a;
        return new List<RtCycleBase> { a, b };
    }

    internal static List<RtPlain> PlainRows() => new()
    {
        new RtPlain
        {
            Id = 1, Name = "p1", Secret = "P1-LEAK",
            Spec = new RtPlainSpec
            {
                Material = "steel",
                Extras = new Dictionary<string, object?> { ["tier"] = 3 },
            },
            Items = new List<RtItem> { new() { Id = 10, Label = "L10" } },
        },
    };
}

public sealed class RtBaseProfile : EntitySetProfile<int, RtBase>
{
    private readonly List<RtBase> _store = RtData.Rows();

    public RtBaseProfile() : base(x => x.Id)
    {
        EntitySetName = "RtBases";
        Ignore(x => x.Secret);
        ExpandEnabled = true;
        SelectEnabled = true;
        HasMany(x => x.Items!);
        GetQueryable = _ => Task.FromResult(_store.AsQueryable());
        GetById = (id, _) => Task.FromResult(_store.FirstOrDefault(r => r.Id == id));
    }
}

public sealed class RtCycleProfile : EntitySetProfile<int, RtCycleBase>
{
    private readonly List<RtCycleBase> _store = RtData.CycleRows();

    public RtCycleProfile() : base(x => x.Id)
    {
        EntitySetName = "RtCycles";
        ExpandEnabled = true;
        SelectEnabled = true;
        HasMany(x => x.Items!);
        GetQueryable = _ => Task.FromResult(_store.AsQueryable());
        GetById = (id, _) => Task.FromResult(_store.FirstOrDefault(r => r.Id == id));
    }
}

public sealed class RtPlainProfile : EntitySetProfile<int, RtPlain>
{
    private readonly List<RtPlain> _store = RtData.PlainRows();

    public RtPlainProfile() : base(x => x.Id)
    {
        EntitySetName = "RtPlains";
        Ignore(x => x.Secret);
        ExpandEnabled = true;
        SelectEnabled = true;
        HasMany(x => x.Items!);
        GetQueryable = _ => Task.FromResult(_store.AsQueryable());
        GetById = (id, _) => Task.FromResult(_store.FirstOrDefault(r => r.Id == id));
        Post = (model, _) => Task.FromResult<RtPlain?>(model);
    }
}

public sealed class RuntimeTypeConfigResolutionTests
{
    private static Task<TestFixture> BuildAsync() =>
        TestHostBuilder.BuildAsync(b =>
        {
            b.AddEntitySetProfile<RtBaseProfile>();
            b.AddEntitySetProfile<RtCycleProfile>();
            b.AddEntitySetProfile<RtPlainProfile>();
        });

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(TestFixture f, string url)
    {
        HttpResponseMessage r = await f.Client.GetAsync(url);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    // ── The premise both fixes rest on ──────────────────────────────────────────────────────────

    /// <summary>
    /// The derived types really are in the EDM, with their extra members declared on them — which is
    /// what makes "resolve the runtime type's EDM type" a well-defined fix rather than a guess.
    /// </summary>
    [Fact]
    public async Task DerivedTypesAreInTheEdm_WithTheirOwnNavigations()
    {
        await using TestFixture f = await BuildAsync();
        string xml = await f.Client.GetStringAsync("/odata/$metadata");

        Assert.Contains("<EntityType Name=\"RtDerived\" BaseType=\"OhData.AspNetCore.Tests.RtBase\">", xml);
        Assert.Contains(
            "<NavigationProperty Name=\"Notes\" Type=\"Collection(OhData.AspNetCore.Tests.RtNote)\" />", xml);
        Assert.Contains("<EntityType Name=\"RtCycleDerived\" BaseType=\"OhData.AspNetCore.Tests.RtCycleBase\">", xml);

        // And the withheld property is absent from the BASE type, so anything that serves it is
        // serving something $metadata says does not exist.
        Assert.DoesNotContain("Name=\"Secret\"", xml);
    }

    // ── #462: Ignore() is bypassed by a derived runtime instance ────────────────────────────────

    /// <summary>
    /// Pre-fix: <c>{"Extra":"e","Notes":[...],"Id":2,"Name":"derived","Secret":"S2-LEAK"}</c> — the
    /// base row in the SAME page hid the property correctly and the derived one did not.
    /// </summary>
    [Fact]
    public async Task Ignore_AppliesToADerivedInstance_OnTheCollectionRoute()
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, "/odata/RtBases");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Secret", body);
        Assert.DoesNotContain("S2-LEAK", body);
        // The control: the base row in the same page is unaffected, and the derived row still serves
        // its own declared scalar — suppression is about the WITHHELD member, not about derived
        // members in general.
        Assert.Contains("\"Name\":\"base\"", body);
        Assert.Contains("\"Extra\":\"e\"", body);
    }

    /// <summary>Pre-fix the single-entity route leaked it too — a separate substrate, same defect.</summary>
    [Fact]
    public async Task Ignore_AppliesToADerivedInstance_OnGetById()
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, "/odata/RtBases(2)");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Secret", body);
        Assert.DoesNotContain("S2-LEAK", body);
        Assert.Contains("\"Extra\":\"e\"", body);
    }

    // ── #343: a derived-declared navigation bypasses clause suppression ─────────────────────────

    /// <summary>
    /// SUPPRESSED, not served. OData JSON Format §4.5.1 / §11.2.4.2 require a non-expanded
    /// navigation to be omitted, and a derived-declared one can never be expanded in the first place
    /// (the clause binds against the entity set's DECLARED type, and the splice iterates that same
    /// type's navigations), so "serve it" would mean serving it unconditionally.
    /// </summary>
    [Theory]
    [InlineData("/odata/RtBases")]
    [InlineData("/odata/RtBases(2)")]
    [InlineData("/odata/RtBases?$expand=Items")]
    [InlineData("/odata/RtBases(2)?$expand=Items")]
    public async Task DerivedDeclaredNavigation_IsOmitted(string url)
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, url);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Notes", body);
        Assert.DoesNotContain("N1", body);
    }

    /// <summary>
    /// The navigation the clause DID ask for is still served, on the derived instance too — the fix
    /// suppresses what no clause can name, it does not suppress everything.
    /// <para>
    /// <b>This assertion is also the regression test for the FIFTH instance of the defect class,
    /// which this fixture found and neither issue mentions.</b> Pre-fix — and independently of #343
    /// — <c>IsNavVisibleInBaseOptions</c> compared <c>PropertyInfo</c>s with <c>!=</c>, which also
    /// compares <c>ReflectedType</c>; for an INHERITED navigation on a DERIVED instance the two
    /// sides disagree about it (measured: <c>RtDerived</c> vs <c>RtBase</c>, <c>==</c> false,
    /// <c>HasSameMetadataDefinitionAs</c> true), so a navigation the client explicitly
    /// <c>$expand</c>ed was silently DROPPED from every derived row while the base rows in the same
    /// page kept theirs. Both directions of the same mistake, in one fixture.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("/odata/RtBases(2)?$expand=Items")]
    [InlineData("/odata/RtBases?$expand=Items")]
    public async Task DeclaredNavigation_StillExpands_OnADerivedInstance(string url)
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, url);

        Assert.Equal(HttpStatusCode.OK, status);
        // The derived row (Extra) carries Items exactly as the base row does.
        Assert.Contains("\"Extra\":\"e\"", body);
        Assert.Contains("\"Items\":[{\"Id\":10,\"Label\":\"L10\"}]", body);
    }

    /// <summary>
    /// The 500. Two derived instances referencing each other through a derived-declared navigation,
    /// on a plain GET with NO query string — measured pre-fix as
    /// <c>JsonException: A possible object cycle was detected … Path: $.Buddy.Buddy.Buddy…</c> on
    /// both routes.
    /// </summary>
    [Theory]
    [InlineData("/odata/RtCycles")]
    [InlineData("/odata/RtCycles(1)")]
    [InlineData("/odata/RtCycles?$expand=Items")]
    public async Task MutualReferenceThroughADerivedNavigation_DoesNotFault(string url)
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, url);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Buddy", body);
        Assert.DoesNotContain("InternalServerError", body);
        Assert.Contains("\"Name\":\"A\"", body);
    }

    // ── The shared helper is used at ALL FOUR sites ─────────────────────────────────────────────

    /// <summary>
    /// The structural tripwire, and it is a TYPE-level one rather than a test that greps for a
    /// pattern: every consumer of a per-type withheld-name map now receives an
    /// <see cref="InheritedNameSets"/>, whose only lookup member is <c>Resolve</c> — which always
    /// walks. A fifth site cannot re-introduce <c>map.TryGetValue(typeInfo.Type, ...)</c> because
    /// there is no longer a map with that method in scope to call it on.
    /// </summary>
    [Fact]
    public void EveryPerTypeWithheldNameConsumer_TakesTheBaseChainResolver_NotADictionary()
    {
        const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        Assembly asm = typeof(OhDataRegistration).Assembly;
        Type ignoreOpts = asm.GetType("OhData.IgnoredPropertyJsonOptions", throwOnError: true)!;
        Type openTypeOpts = asm.GetType("OhData.OpenTypeJsonOptions", throwOnError: true)!;

        // Site 1 (#462 proper): the Ignore() removal modifier.
        Assert.Equal(
            typeof(InheritedNameSets),
            ignoreOpts.GetMethod("Build", Any)!.GetParameters()[1].ParameterType);

        // Sites 2-4 (#398 stage 1 withheld-name containment): the read-path modifier, the write-body
        // scan and the write-body strip.
        foreach (string name in new[] { "Build", "ScanWriteBody", "RewriteWithoutUnbindableKeys" })
        {
            MethodInfo m = openTypeOpts.GetMethod(name, Any)!;
            Assert.Contains(m.GetParameters(), p => p.ParameterType == typeof(InheritedNameSets));
            Assert.DoesNotContain(
                m.GetParameters(),
                p => p.ParameterType == typeof(IReadOnlyDictionary<Type, IReadOnlySet<string>>));
        }

        // And what carries the map between them.
        Assert.Equal(
            typeof(InheritedNameSets),
            typeof(OhDataRegistration).GetProperty("IgnoredJsonNamesByType", Any)!.PropertyType);

        // The resolver itself offers no exact-type escape hatch. Its whole callable surface — every
        // member visible to any other type, i.e. everything not private — is one walking lookup plus
        // an emptiness flag. `Walk` is private and is the walk itself.
        string[] callable = typeof(InheritedNameSets)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(InheritedNameSets))
            .Where(m => m is MethodInfo { IsSpecialName: false, IsPrivate: false } or PropertyInfo)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "IsEmpty", "Resolve" }, callable);
    }

    /// <summary>
    /// The behaviour behind the type check, for the three sites an HTTP request cannot reach today.
    /// <c>Ignore(...)</c> names a root member of an ENTITY type while containers live on COMPLEX
    /// types, so the withheld set is a documented no-op at complex scope
    /// (<c>OpenTypeIgnoreContainmentTests</c>) — but the LOOKUP is the thing under test, and it is
    /// exercised here directly: the set is keyed by the BASE complex type and consulted for the
    /// DERIVED one. Pre-fix all three missed.
    /// </summary>
    [Fact]
    public void WithheldNameContainment_ResolvesThroughTheBaseChain_AtEveryOpenTypeSite()
    {
        var withheld = new InheritedNameSets(
            new Dictionary<Type, IReadOnlySet<string>>
            {
                [typeof(RtcBaseBag)] = new HashSet<string>(new[] { "Region" }, StringComparer.OrdinalIgnoreCase),
            },
            StringComparer.OrdinalIgnoreCase);

        Assert.NotNull(withheld.Resolve(typeof(RtcBaseBag)));
        // The whole defect, at its smallest: the DERIVED type is not a key in that map.
        Assert.Contains("Region", withheld.Resolve(typeof(RtcDerivedBag))!);
        Assert.Null(withheld.Resolve(typeof(RtItem)));
    }

    /// <summary>
    /// Union, not nearest-wins. A base type's withheld name survives a derived type declaring its
    /// own set — taking only the nearest entry would let an unrelated derived-level
    /// <c>Ignore(...)</c> silently un-withhold the base's, which is the same disclosure one level
    /// down. <c>TryFindContainer</c> keeps the opposite (nearest-wins) policy deliberately, because a
    /// <c>new</c>-shadowed container must flatten the derived member — see both methods' remarks.
    /// </summary>
    [Fact]
    public void WithheldNames_UnionUpTheChain_RatherThanBeingShadowed()
    {
        var withheld = new InheritedNameSets(
            new Dictionary<Type, IReadOnlySet<string>>
            {
                [typeof(RtcBaseBag)] = new HashSet<string>(new[] { "Region" }, StringComparer.Ordinal),
                [typeof(RtcDerivedBag)] = new HashSet<string>(new[] { "Channel" }, StringComparer.Ordinal),
            },
            StringComparer.Ordinal);

        IReadOnlySet<string> resolved = withheld.Resolve(typeof(RtcDerivedBag))!;
        Assert.Contains("Region", resolved);
        Assert.Contains("Channel", resolved);
        // The base is unaffected by the derived declaration — the walk goes UP, never down.
        Assert.DoesNotContain("Channel", withheld.Resolve(typeof(RtcBaseBag))!);
    }

    /// <summary>
    /// The single-contributor case must hand back the very set it was given, never a copy: each set
    /// carries the BINDER's comparer (#398 review HIGH-1) and re-wrapping it on a per-request lookup
    /// would both re-allocate and risk substituting the wrong comparer.
    /// </summary>
    [Fact]
    public void SingleContributor_IsReturnedByReference_PreservingItsComparer()
    {
        IReadOnlySet<string> declared =
            new HashSet<string>(new[] { "Region" }, StringComparer.OrdinalIgnoreCase);
        var withheld = new InheritedNameSets(
            new Dictionary<Type, IReadOnlySet<string>> { [typeof(RtcBaseBag)] = declared },
            StringComparer.OrdinalIgnoreCase);

        Assert.Same(declared, withheld.Resolve(typeof(RtcDerivedBag)));
        Assert.Contains("REGION", withheld.Resolve(typeof(RtcDerivedBag))!);
    }

    // ── Byte identity for non-derived shapes ────────────────────────────────────────────────────
    // Every expected string below was captured from the PRE-FIX build at 6de41d4 and pasted in
    // verbatim; none was read off the post-fix output. RtPlains carries an Ignore()d property, a
    // navigation and an open complex type at once, so it exercises all three moved lookups on a
    // model where runtime type == declared type everywhere.

    [Theory]
    [InlineData(
        "/odata/RtPlains",
        "{\"@odata.context\":\"http://localhost/odata/$metadata#RtPlains\",\"value\":[{\"Id\":1,\"Name\":\"p1\",\"Spec\":{\"Material\":\"steel\",\"tier\":3}}]}")]
    [InlineData(
        "/odata/RtPlains?$expand=Items",
        "{\"@odata.context\":\"http://localhost/odata/$metadata#RtPlains\",\"value\":[{\"Id\":1,\"Name\":\"p1\",\"Spec\":{\"Material\":\"steel\",\"tier\":3},\"Items\":[{\"Id\":10,\"Label\":\"L10\"}]}]}")]
    [InlineData(
        "/odata/RtPlains(1)",
        "{\"@odata.context\":\"http://localhost/odata/$metadata#RtPlains/$entity\",\"@odata.id\":\"http://localhost/odata/RtPlains(1)\",\"Id\":1,\"Name\":\"p1\",\"Spec\":{\"Material\":\"steel\",\"tier\":3}}")]
    [InlineData(
        "/odata/RtPlains(1)?$select=Name",
        "{\"@odata.context\":\"http://localhost/odata/$metadata#RtPlains(Name)/$entity\",\"@odata.id\":\"http://localhost/odata/RtPlains(1)\",\"Name\":\"p1\"}")]
    [InlineData(
        "/odata/RtBases(1)",
        "{\"@odata.context\":\"http://localhost/odata/$metadata#RtBases/$entity\",\"@odata.id\":\"http://localhost/odata/RtBases(1)\",\"Id\":1,\"Name\":\"base\"}")]
    public async Task ByteIdentity_NonDerivedShapesAreUnchanged(string url, string expected)
    {
        await using TestFixture f = await BuildAsync();
        (HttpStatusCode status, string body) = await GetAsync(f, url);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, body);
    }

    /// <summary>
    /// The write path's two lookups (the body scan and the strip) on an unchanged shape: a dynamic
    /// key is accepted and echoed, a key spelled like the withheld property is dropped rather than
    /// bound, and the response bytes match the pre-fix build exactly.
    /// </summary>
    [Fact]
    public async Task ByteIdentity_WritePathOnANonDerivedOpenType()
    {
        await using TestFixture f = await BuildAsync();
        var content = new StringContent(
            "{\"Id\":7,\"Name\":\"posted\",\"Secret\":\"NOPE\",\"Spec\":{\"Material\":\"brass\",\"tier\":9}}",
            Encoding.UTF8, "application/json");
        HttpResponseMessage r = await f.Client.PostAsync("/odata/RtPlains", content);
        string body = await r.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(
            "{\"@odata.context\":\"http://localhost/odata/$metadata#RtPlains/$entity\",\"@odata.id\":\"http://localhost/odata/RtPlains(7)\",\"Id\":7,\"Name\":\"posted\",\"Spec\":{\"Material\":\"brass\",\"tier\":9}}",
            body);
    }
}

/// <summary>Base of a complex-type pair used only by the withheld-name lookup unit tests.</summary>
public class RtcBaseBag
{
    public string? Region { get; set; }
    public IDictionary<string, object?>? Kv { get; set; }
}

public class RtcDerivedBag : RtcBaseBag
{
    public string? Channel { get; set; }
}
