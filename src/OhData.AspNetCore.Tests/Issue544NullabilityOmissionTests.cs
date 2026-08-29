using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #544 / #545 — the #355 nullability rule is restricted to a property the body NAMED with an
/// explicit <c>null</c>; the omitted-property leg is gone.
/// <para>
/// The whole point is the table below. Three properties the framework's own <c>$metadata</c>
/// describes <b>identically</b> as <c>Nullable="false"</c> must answer identically, with no
/// dependence on whether the developer wrote <c>= ""</c> or <c>= null!</c> and none on CLR
/// value-versus-reference:
/// </para>
/// <list type="table">
/// <item><term><c>string X { get; set; } = ""</c></term><description>omit → 201, null → 400</description></item>
/// <item><term><c>string X { get; set; } = null!</c></term><description>omit → 201, null → 400</description></item>
/// <item><term><c>int Year</c></term><description>omit → 201, null → 400 (deserializer-worded)</description></item>
/// </list>
/// <para>
/// The omission leg was <b>§11.4.3, PUT-only</b>, and conditioned on <i>"no service-generated or
/// default value"</i> — a condition the framework provably cannot evaluate (the convention builder
/// emits no <c>Core.Computed</c>, and a CLR initializer is invisible to the EDM). §11.4.2, which
/// the shipped XML doc cited, requires nothing of the kind, and
/// <c>Microsoft.AspNetCore.OData</c> accepts omission while rejecting an explicit <c>null</c>.
/// </para>
/// </summary>
public class Issue544NullabilityOmissionTests
{
    private static readonly XNamespace EdmNs = "http://docs.oasis-open.org/odata/ns/edm";

    // ── The control: $metadata really does describe all three the same way ─────────

    /// <summary>
    /// Ties every assertion below to the published contract rather than to a CLR guess. If this
    /// fails, the rest of the file is measuring something else.
    /// </summary>
    [Fact]
    public async Task Metadata_DescribesAllThreeShapes_IdenticallyAsNullableFalse()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        string csdl = await fx.Client.GetStringAsync("/odata/$metadata");
        XElement entityType = XDocument.Parse(csdl)
            .Descendants(EdmNs + "EntityType")
            .Single(e => (string?)e.Attribute("Name") == "N544Thing");

        Assert.False(IsCsdlNullable(entityType, "Initialized"));
        Assert.False(IsCsdlNullable(entityType, "Uninitialized"));
        Assert.False(IsCsdlNullable(entityType, "Year"));
    }

    private static bool IsCsdlNullable(XElement entityType, string propertyName)
    {
        XElement prop = entityType.Elements(EdmNs + "Property")
            .Single(p => (string?)p.Attribute("Name") == propertyName);
        return prop.Attribute("Nullable") is not { } n || (bool)n;
    }

    // ── POST: the three-row table ──────────────────────────────────────────────────

    /// <summary>
    /// THE FIX. A body that names none of the three non-nullable properties is accepted on all
    /// three shapes. Pre-fix the <c>= null!</c> row is a <c>400</c>: the check read the bound
    /// instance unconditionally, so the answer depended on the CLR initializer.
    /// </summary>
    [Fact]
    public async Task Post_OmittingEveryNonNullableProperty_Is201_OnAllThreeShapes()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPosted = null;
        var response = await fx.Client.PostAsJsonAsync("/odata/N544Things", new { Id = 1 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(N544ThingProfile.LastPosted);
        Assert.Equal("", N544ThingProfile.LastPosted!.Initialized);
        Assert.Null(N544ThingProfile.LastPosted!.Uninitialized);
        Assert.Equal(0, N544ThingProfile.LastPosted!.Year);
    }

    /// <summary>CONTROL — #355's own defect must not regress: an explicit null is still a 400.</summary>
    [Fact]
    public async Task Post_ExplicitNull_ForTheInitializedProperty_Is400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPosted = null;
        using var content = new StringContent(
            "{\"Id\":1,\"Initialized\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/N544Things", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPosted);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Initialized", json.GetProperty("error").GetProperty("target").GetString());
    }

    /// <summary>CONTROL — the <c>= null!</c> shape rejects an explicit null exactly as before.</summary>
    [Fact]
    public async Task Post_ExplicitNull_ForTheUninitializedProperty_Is400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPosted = null;
        using var content = new StringContent(
            "{\"Id\":1,\"Uninitialized\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/N544Things", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPosted);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Uninitialized", json.GetProperty("error").GetProperty("target").GetString());
    }

    /// <summary>
    /// CONTROL — the value-type row. It is answered by the deserializer, not by this rule (an
    /// <c>int</c> cannot hold null), which is why the rule excludes it; the wire answer is the same
    /// <c>400</c>, and that is what makes the three rows agree.
    /// </summary>
    [Fact]
    public async Task Post_ExplicitNull_ForTheValueTypeProperty_Is400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPosted = null;
        using var content = new StringContent(
            "{\"Id\":1,\"Year\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/N544Things", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPosted);
    }

    // ── PUT: the same three rows, on the STREAMING branch ──────────────────────────

    /// <summary>
    /// PUT's default branch never materialises the body — it buffers and streams into the binder —
    /// so the "which members did the body name" question is asked of raw UTF-8 there. Same table.
    /// </summary>
    [Fact]
    public async Task Put_OmittingEveryNonNullableProperty_Is200_OnAllThreeShapes()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPut = null;
        using var content = new StringContent("{\"Id\":1}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/N544Things(1)", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(N544ThingProfile.LastPut);
        Assert.Equal("", N544ThingProfile.LastPut!.Initialized);
        Assert.Null(N544ThingProfile.LastPut!.Uninitialized);
        Assert.Equal(0, N544ThingProfile.LastPut!.Year);
    }

    [Fact]
    public async Task Put_ExplicitNull_ForTheInitializedProperty_Is400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPut = null;
        using var content = new StringContent(
            "{\"Id\":1,\"Initialized\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/N544Things(1)", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPut);
    }

    [Fact]
    public async Task Put_ExplicitNull_ForTheUninitializedProperty_Is400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPut = null;
        using var content = new StringContent(
            "{\"Id\":1,\"Uninitialized\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/N544Things(1)", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPut);
    }

    [Fact]
    public async Task Put_ExplicitNull_ForTheValueTypeProperty_Is400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPut = null;
        using var content = new StringContent(
            "{\"Id\":1,\"Year\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/N544Things(1)", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPut);
    }

    // ── PUT on the OPEN-TYPES branch: a different reader, the same answer ──────────

    /// <summary>
    /// A registration whose EDM really has an open complex type takes PUT's other branch, which
    /// materialises a <c>JsonElement</c> and binds the PREPARED body. Both branches must answer the
    /// same way or #456's per-verb divergence comes back one option over.
    /// </summary>
    [Fact]
    public async Task Put_OnTheOpenTypeBranch_OmitsAccepted_ExplicitNullRejected()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544OpenProfile>());

        N544OpenProfile.LastPut = null;
        using var omitted = new StringContent("{\"Id\":1}", Encoding.UTF8, "application/json");
        var omittedResponse = await fx.Client.PutAsync("/odata/N544OpenThings(1)", omitted);
        Assert.Equal(HttpStatusCode.OK, omittedResponse.StatusCode);
        Assert.Null(N544OpenProfile.LastPut!.Uninitialized);

        N544OpenProfile.LastPut = null;
        using var explicitNull = new StringContent(
            "{\"Id\":1,\"Uninitialized\":null}", Encoding.UTF8, "application/json");
        var nullResponse = await fx.Client.PutAsync("/odata/N544OpenThings(1)", explicitNull);
        Assert.Equal(HttpStatusCode.BadRequest, nullResponse.StatusCode);
        Assert.Null(N544OpenProfile.LastPut);
    }

    /// <summary>The collection POST always materialises a <c>JsonElement</c>; same pair.</summary>
    [Fact]
    public async Task Post_OnTheOpenTypeBranch_OmitsAccepted_ExplicitNullRejected()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544OpenProfile>());

        N544OpenProfile.LastPosted = null;
        var omitted = await fx.Client.PostAsJsonAsync("/odata/N544OpenThings", new { Id = 1 });
        Assert.Equal(HttpStatusCode.Created, omitted.StatusCode);
        Assert.Null(N544OpenProfile.LastPosted!.Uninitialized);

        N544OpenProfile.LastPosted = null;
        using var explicitNull = new StringContent(
            "{\"Id\":1,\"Uninitialized\":null}", Encoding.UTF8, "application/json");
        var nullResponse = await fx.Client.PostAsync("/odata/N544OpenThings", explicitNull);
        Assert.Equal(HttpStatusCode.BadRequest, nullResponse.StatusCode);
        Assert.Null(N544OpenProfile.LastPosted);
    }

    // ── The navigation-POST create route ───────────────────────────────────────────

    [Fact]
    public async Task NavigationPost_OmittingTheUninitializedProperty_Is201()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPartCreated = null;
        using var content = new StringContent("{\"Id\":7}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/N544Things(1)/Parts", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(N544ThingProfile.LastPartCreated);
        Assert.Null(N544ThingProfile.LastPartCreated!.Serial);
    }

    [Fact]
    public async Task NavigationPost_ExplicitNull_Is400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPartCreated = null;
        using var content = new StringContent(
            "{\"Id\":7,\"Serial\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/N544Things(1)/Parts", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPartCreated);
    }

    // ── The body-name table follows the BINDER, not a second derivation ────────────

    /// <summary>
    /// #511's rule applied to this gate: the name the body must use is the one the binder matches
    /// against, so a <c>[JsonPropertyName]</c>-renamed required property is still rejected when the
    /// body names it with a null under its WIRE name.
    /// </summary>
    [Fact]
    public async Task Post_ExplicitNull_UnderAJsonPropertyNameRenamedProperty_Is400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPosted = null;
        using var content = new StringContent(
            "{\"Id\":1,\"tag\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/N544Things", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPosted);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tag", json.GetProperty("error").GetProperty("target").GetString());
    }

    /// <summary>
    /// The binder matches body keys case-insensitively (<c>JsonSerializerDefaults.Web</c>), so the
    /// gate must too — a table narrower than the binder would let <c>{"uninitialized":null}</c>
    /// bind to null and slip through, which is the fail-OPEN direction.
    /// </summary>
    [Fact]
    public async Task Post_ExplicitNull_UnderACaseDifferingSpelling_Is400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPosted = null;
        using var content = new StringContent(
            "{\"Id\":1,\"uninitialized\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/N544Things", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPosted);
    }

    /// <summary>
    /// BOUNDING: a required property named inside a NESTED value is not the root's property. Top
    /// level only, exactly as the deep-write gate reads it (#506).
    /// </summary>
    [Fact]
    public async Task Post_ANestedMemberOfTheSameName_DoesNotTripTheGate()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPosted = null;
        using var content = new StringContent(
            "{\"Id\":1,\"Parts\":[{\"Id\":2,\"Uninitialized\":null}]}",
            Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/N544Things", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPosted!.Uninitialized);
    }

    // ── PATCH and the property writes are ALREADY withholding-based: unchanged ─────

    /// <summary>CONTROL — <c>PATCH</c> was already correct and this change does not touch it.</summary>
    [Fact]
    public async Task Patch_OmittingTheUninitializedProperty_IsStillAccepted()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        using var content = new StringContent("{\"Year\":2026}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PatchAsync("/odata/N544Things(1)", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Patch_ExplicitNull_IsStill400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPatchChangedProperties = null;
        using var content = new StringContent(
            "{\"Uninitialized\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PatchAsync("/odata/N544Things(1)", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPatchChangedProperties);
    }

    /// <summary>CONTROL — the structural-property writes were already withholding-based too.</summary>
    [Fact]
    public async Task PropertyWrite_ExplicitNull_IsStill400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPatchChangedProperties = null;
        using var content = new StringContent("{\"value\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/N544Things(1)/Uninitialized", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPatchChangedProperties);
    }

    /// <summary>CONTROL — <c>DELETE</c> on a property is "set it to null", still a 400.</summary>
    [Fact]
    public async Task PropertyDelete_OnANonNullableProperty_IsStill400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544ThingProfile>());

        N544ThingProfile.LastPatchChangedProperties = null;
        var response = await fx.Client.DeleteAsync("/odata/N544Things(1)/Uninitialized");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(N544ThingProfile.LastPatchChangedProperties);
    }

    // ── The opt-out still opts out ─────────────────────────────────────────────────

    [Fact]
    public async Task Post_WithValidationDisabled_StillReachesTheHandlerWithTheNull()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<N544UnvalidatedProfile>());

        N544UnvalidatedProfile.LastPosted = null;
        using var content = new StringContent(
            "{\"Id\":1,\"Initialized\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/N544Unvalidated", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(N544UnvalidatedProfile.LastPosted!.Initialized);
    }
}

// ── Fixtures ──────────────────────────────────────────────────────────────────────

internal class N544Part
{
    public int Id { get; set; }
    public string Serial { get; set; } = null!;
    public string Uninitialized { get; set; } = null!;
}

/// <summary>
/// #545's three shapes on one type. <c>$metadata</c> describes <c>Initialized</c>,
/// <c>Uninitialized</c> and <c>Year</c> identically as <c>Nullable="false"</c>.
/// </summary>
internal class N544Thing
{
    public int Id { get; set; }
    public string Initialized { get; set; } = "";
    public string Uninitialized { get; set; } = null!;
    public int Year { get; set; }

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = null!;

    public List<N544Part> Parts { get; set; } = new();
}

internal class N544ThingProfile : EntitySetProfile<int, N544Thing>
{
    public static N544Thing? LastPosted;
    public static N544Thing? LastPut;
    public static string[]? LastPatchChangedProperties;
    public static N544Part? LastPartCreated;

    public N544ThingProfile() : base(x => x.Id)
    {
        EntitySetName = "N544Things";
        AllowDeepWrites = true;

        HasMany(
            x => x.Parts,
            getAll: (_, _) => Task.FromResult<IEnumerable<N544Part>>(Array.Empty<N544Part>()),
            post: (_, part, _) =>
            {
                LastPartCreated = part;
                return Task.FromResult<N544Part?>(part);
            });

        GetAll = _ => Task.FromResult<IEnumerable<N544Thing>>(Array.Empty<N544Thing>());
        GetById = (id, _) => Task.FromResult<N544Thing?>(
            new N544Thing { Id = id, Initialized = "e", Uninitialized = "e", Tag = "t" });

        Post = (thing, _) =>
        {
            LastPosted = thing;
            return Task.FromResult<N544Thing?>(thing);
        };

        Put = (id, thing, _) =>
        {
            LastPut = thing;
            thing.Id = id;
            return Task.FromResult(thing);
        };

        Patch = (id, delta, _) =>
        {
            LastPatchChangedProperties = delta.GetChangedPropertyNames().ToArray();
            var thing = new N544Thing
            {
                Id = id,
                Initialized = "e",
                Uninitialized = "e",
                Tag = "t",
            };
            delta.Patch(thing);
            return Task.FromResult<N544Thing?>(thing);
        };
    }
}

internal class N544Bag
{
    public string? Note { get; set; }
    public Dictionary<string, object?> Extras { get; set; } = new();
}

/// <summary>A registration whose EDM really has an open complex type — PUT's other branch.</summary>
internal class N544OpenThing
{
    public int Id { get; set; }
    public string Uninitialized { get; set; } = null!;
    public N544Bag? Bag { get; set; }
}

internal class N544OpenProfile : EntitySetProfile<int, N544OpenThing>
{
    public static N544OpenThing? LastPosted;
    public static N544OpenThing? LastPut;

    public N544OpenProfile() : base(x => x.Id)
    {
        EntitySetName = "N544OpenThings";

        GetAll = _ => Task.FromResult<IEnumerable<N544OpenThing>>(Array.Empty<N544OpenThing>());
        GetById = (id, _) => Task.FromResult<N544OpenThing?>(
            new N544OpenThing { Id = id, Uninitialized = "e" });

        Post = (thing, _) =>
        {
            LastPosted = thing;
            return Task.FromResult<N544OpenThing?>(thing);
        };

        Put = (id, thing, _) =>
        {
            LastPut = thing;
            thing.Id = id;
            return Task.FromResult(thing);
        };
    }
}

internal class N544Unvalidated
{
    public int Id { get; set; }
    public string Initialized { get; set; } = "";
}

internal class N544UnvalidatedProfile : EntitySetProfile<int, N544Unvalidated>
{
    public static N544Unvalidated? LastPosted;

    public N544UnvalidatedProfile() : base(x => x.Id)
    {
        EntitySetName = "N544Unvalidated";
        ValidateRequestBodyNullability = false;

        GetAll = _ => Task.FromResult<IEnumerable<N544Unvalidated>>(Array.Empty<N544Unvalidated>());
        Post = (thing, _) =>
        {
            LastPosted = thing;
            return Task.FromResult<N544Unvalidated?>(thing);
        };
    }
}
