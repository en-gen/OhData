using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #514 / #510 / #474 / #355 — how a write BODY is read, validated and bounded.
/// <list type="bullet">
/// <item>#514: the <c>JsonDocument</c> parse sites must read the body the way the binder does.</item>
/// <item>#510: client-supplied body keys must not reach a process-wide unbounded cache.</item>
/// <item>#474: a write body must have an OhData-level ceiling on a default configuration.</item>
/// <item>#355: a body that violates the framework's own published EDM is a 400, not a 500.</item>
/// </list>
/// </summary>
public class WriteBodyContractTests
{
    // ── #514: the JsonDocument parse sites read the body the way the binder does ────

    /// <summary>
    /// The collection POST materialises the body with <c>JsonDocument.ParseAsync</c>. With default
    /// <c>JsonDocumentOptions</c> it rejects a comment/trailing comma that the binder — and
    /// therefore PUT — accepts, so the same bytes get different answers per verb.
    /// </summary>
    [Fact]
    public async Task Post_HostRelaxedJsonOptions_AcceptsTheSameBytesPutAccepts()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>(),
            configureServices: services => services.ConfigureHttpJsonOptions(j =>
            {
                j.SerializerOptions.ReadCommentHandling = JsonCommentHandling.Skip;
                j.SerializerOptions.AllowTrailingCommas = true;
            }));

        const string body = "{\"Id\":1,/*hello*/\"Name\":\"Ada\",\"Age\":36,}";

        using var putContent = new StringContent(body, Encoding.UTF8, "application/json");
        var put = await fx.Client.PutAsync("/odata/WbContacts(1)", putContent);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var postContent = new StringContent(body, Encoding.UTF8, "application/json");
        var post = await fx.Client.PostAsync("/odata/WbContacts", postContent);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
    }

    /// <summary>
    /// The same divergence on the open-types branch of PUT and of the navigation-POST create route,
    /// which parse the body into a <c>JsonDocument</c> so the dynamic-key check can read it.
    /// </summary>
    [Fact]
    public async Task OpenTypeRegistration_PutAndNavPost_HostRelaxedJsonOptions_AcceptTheRelaxedToken()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbOpenProfile>(),
            configureServices: services => services.ConfigureHttpJsonOptions(j =>
            {
                j.SerializerOptions.ReadCommentHandling = JsonCommentHandling.Skip;
                j.SerializerOptions.AllowTrailingCommas = true;
            }));

        using var putContent = new StringContent(
            "{\"Id\":1,/*hello*/\"Label\":\"L\",}", Encoding.UTF8, "application/json");
        var put = await fx.Client.PutAsync("/odata/WbOpenThings(1)", putContent);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var navContent = new StringContent(
            "{\"Id\":7,/*hello*/\"Text\":\"N\",}", Encoding.UTF8, "application/json");
        var navPost = await fx.Client.PostAsync("/odata/WbOpenThings(1)/Notes", navContent);
        Assert.Equal(HttpStatusCode.Created, navPost.StatusCode);
    }

    /// <summary>
    /// A raised host <c>MaxDepth</c> is the third derived member, and the one the collection POST
    /// alone could not honour. Bounding: the framework must not become MORE permissive than the
    /// binder either, so a body past the host's own ceiling is still rejected.
    /// </summary>
    [Fact]
    public async Task Post_HostRaisedMaxDepth_AcceptsADeepBodyAndStillRejectsADeeperOne()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>(),
            configureServices: services => services.ConfigureHttpJsonOptions(j =>
            {
                j.SerializerOptions.MaxDepth = 200;
            }));

        using var okContent = new StringContent(
            DeepBody(depth: 120), Encoding.UTF8, "application/json");
        var ok = await fx.Client.PostAsync("/odata/WbContacts", okContent);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        using var tooDeepContent = new StringContent(
            DeepBody(depth: 300), Encoding.UTF8, "application/json");
        var tooDeep = await fx.Client.PostAsync("/odata/WbContacts", tooDeepContent);
        Assert.Equal(HttpStatusCode.BadRequest, tooDeep.StatusCode);
    }

    /// <summary>An unmatched member nested <paramref name="depth"/> objects deep.</summary>
    private static string DeepBody(int depth)
    {
        var sb = new StringBuilder("{\"Id\":1,\"Name\":\"Ada\",\"Unknown\":");
        for (int i = 0; i < depth; i++) sb.Append("{\"a\":");
        sb.Append('1');
        for (int i = 0; i < depth; i++) sb.Append('}');
        sb.Append('}');
        return sb.ToString();
    }

    // ── #510: client-supplied body keys must not reach the memoizing helper ────────

    /// <summary>
    /// <c>FindClrPropertyByEdmName</c> memoizes on <c>(Type, string)</c> in a process-wide cache
    /// keyed by the caller's exact string, and PATCH called it once per BODY PROPERTY NAME. A
    /// caller could therefore grow that cache without bound with a stream of unmatched keys.
    /// </summary>
    [Fact]
    public async Task Patch_UnmatchedBodyKeys_NeverReachTheProcessWideNameCache()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        string marker = "Zq" + Guid.NewGuid().ToString("N");
        var body = new StringBuilder("{");
        for (int i = 0; i < 40; i++)
        {
            if (i > 0) body.Append(',');
            body.Append('"').Append(marker).Append(i).Append("\":1");
        }
        body.Append('}');

        using var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        var response = await fx.Client.PatchAsync("/odata/WbContacts(1)", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(NameCacheKeysContaining(marker));
    }

    /// <summary>
    /// BOUNDING for the test above: PATCH must still resolve every name it resolved before —
    /// a <c>[JsonPropertyName]</c> rename and a case-differing spelling included — so the fix
    /// cannot pass by having stopped binding anything.
    /// </summary>
    [Fact]
    public async Task Patch_StillResolvesRenamedAndCaseDifferingBodyNames()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        WbContactProfile.LastPatchChangedProperties = null;
        using var renamed = new StringContent(
            "{\"nick\":\"Ace\",\"aGe\":41}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PatchAsync("/odata/WbContacts(1)", renamed);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(WbContactProfile.LastPatchChangedProperties);
        Assert.Contains("Nickname", WbContactProfile.LastPatchChangedProperties!);
        Assert.Contains("Age", WbContactProfile.LastPatchChangedProperties!);
    }

    private static List<string> NameCacheKeysContaining(string marker)
    {
        Type naming = typeof(OhDataRegistration).Assembly.GetType("OhData.ODataPropertyNaming")!;
        FieldInfo field = naming.GetField(
            "s_clrPropertyByEdmNameCache", BindingFlags.NonPublic | BindingFlags.Static)!;
        object cache = field.GetValue(null)!;
        var hits = new List<string>();
        foreach (object? entry in (System.Collections.IEnumerable)cache)
        {
            object key = entry!.GetType().GetProperty("Key")!.GetValue(entry)!;
            string edmName = (string)key.GetType().GetField("Item2")!.GetValue(key)!;
            if (edmName.Contains(marker, StringComparison.Ordinal)) hits.Add(edmName);
        }
        return hits;
    }

    // ── #474: a framework-level ceiling on a default configuration ─────────────────

    /// <summary>
    /// On a registration that never sets <c>MaxRequestBodyBytes</c>, nothing in OhData bounded a
    /// write body — the only ceiling was the host's Kestrel <c>MaxRequestBodySize</c>, which a host
    /// is free to raise or disable. The framework now applies its own default.
    /// </summary>
    [Theory]
    [InlineData("POST", "/odata/WbContacts")]
    [InlineData("PUT", "/odata/WbContacts(1)")]
    [InlineData("PATCH", "/odata/WbContacts(1)")]
    public async Task Write_OverTheFrameworkDefaultCeiling_Returns413_OnADefaultConfiguration(
        string method, string path)
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        var response = await SendWithDeclaredLengthAsync(
            fx, method, path, "{\"Id\":1,\"Name\":\"Ada\"}",
            declaredLength: EntitySetDefaults.DefaultMaxRequestBodyBytes + 1);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>An unbound action's body is bounded by the same default.</summary>
    [Fact]
    public async Task UnboundAction_OverTheFrameworkDefaultCeiling_Returns413()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddEntitySetProfile<WbContactProfile>()
            .AddAction((string note) => note.Length, name: "Measure"));

        var response = await SendWithDeclaredLengthAsync(
            fx, "POST", "/odata/Measure", "{\"note\":\"hi\"}",
            declaredLength: EntitySetDefaults.DefaultMaxRequestBodyBytes + 1);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>
    /// BOUNDING: an explicit limit still wins in BOTH directions. A profile that raises the limit
    /// above the framework default must not be capped by it.
    /// </summary>
    [Fact]
    public async Task Write_ProfileRaisedLimit_OverridesTheFrameworkDefault()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbGenerousProfile>());

        var response = await SendWithDeclaredLengthAsync(
            fx, "POST", "/odata/WbGenerousContacts", "{\"Id\":1,\"Name\":\"Ada\"}",
            declaredLength: EntitySetDefaults.DefaultMaxRequestBodyBytes + 1);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// BOUNDING: the framework default is a DEFAULT, not a floor — clearing it server-wide restores
    /// the pre-#474 behaviour for a host that really does want the host's own limit to be the only
    /// one.
    /// </summary>
    [Fact]
    public async Task Write_DefaultsClearedToNull_AppliesNoOhDataLevelLimit()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .WithDefaults(d => d.MaxRequestBodyBytes = null)
            .AddEntitySetProfile<WbContactProfile>());

        var response = await SendWithDeclaredLengthAsync(
            fx, "POST", "/odata/WbContacts", "{\"Id\":1,\"Name\":\"Ada\"}",
            declaredLength: EntitySetDefaults.DefaultMaxRequestBodyBytes + 1);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>An ordinary small write is untouched by the new ceiling.</summary>
    [Fact]
    public async Task Write_OrdinarySmallBody_IsUnaffectedByTheFrameworkDefault()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        var response = await fx.Client.PostAsJsonAsync(
            "/odata/WbContacts", new { Id = 3, Name = "Grace", Age = 45 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendWithDeclaredLengthAsync(
        TestFixture fx, string method, string path, string body, long declaredLength)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new DeclaredLengthContent(Encoding.UTF8.GetBytes(body), declaredLength),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return await fx.Client.SendAsync(request);
    }

    /// <summary>
    /// Sends a small body while DECLARING a large <c>Content-Length</c>. The limit filter
    /// fast-rejects on the declared length before a byte of the body is read, which is the arm
    /// under test — and declaring rather than sending is faithful to the threat, since a caller
    /// declares whatever it likes (the very reason the capacity hint is clamped).
    /// </summary>
    private sealed class DeclaredLengthContent : HttpContent
    {
        private readonly byte[] _payload;
        private readonly long _declared;

        internal DeclaredLengthContent(byte[] payload, long declared)
        {
            _payload = payload;
            _declared = declared;
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(_payload, 0, _payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = _declared;
            return true;
        }
    }

    // ── #355: the body is validated against the framework's own EDM ───────────────

    /// <summary>
    /// Ties the rest of this section to the EDM rather than to a CLR guess: the framework's own
    /// <c>$metadata</c> is what declares <c>Name</c> non-nullable and <c>nick</c> nullable.
    /// </summary>
    [Fact]
    public async Task Metadata_DeclaresTheFixturePropertyNonNullable()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        string csdl = await fx.Client.GetStringAsync("/odata/$metadata");
        XElement entityType = XDocument.Parse(csdl)
            .Descendants(EdmNs + "EntityType")
            .Single(e => (string?)e.Attribute("Name") == "WbContact");

        Assert.False(IsCsdlNullable(entityType, "Name"));
        Assert.True(IsCsdlNullable(entityType, "nick"));
    }

    private static readonly XNamespace EdmNs = "http://docs.oasis-open.org/odata/ns/edm";

    /// <summary>Nullable defaults to true when the attribute is omitted (OData CSDL spec).</summary>
    private static bool IsCsdlNullable(XElement entityType, string propertyName)
    {
        XElement prop = entityType.Elements(EdmNs + "Property")
            .Single(p => (string?)p.Attribute("Name") == propertyName);
        return prop.Attribute("Nullable") is not { } n || (bool)n;
    }

    [Fact]
    public async Task Post_ExplicitNullForANonNullableEdmProperty_Returns400_AndTheHandlerNeverRuns()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        WbContactProfile.LastPosted = null;
        var response = await fx.Client.PostAsJsonAsync(
            "/odata/WbContacts", new { Id = 9, Name = (string?)null, Age = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(WbContactProfile.LastPosted);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Name", json.GetProperty("error").GetProperty("target").GetString());
    }

    /// <summary>
    /// #544 REVERSED THIS. Omission is not a violation on any verb, whatever the CLR declaration
    /// left behind: the omission-<c>400</c> clause is §11.4.3, is PUT-only, and is conditioned on
    /// <i>"no service-generated or default value"</i> — which the framework cannot evaluate — while
    /// §11.4.2, cited by the shipped doc, requires nothing of the kind.
    /// <para>
    /// Both shapes are asserted here together, because the whole point is that they AGREE: a
    /// required property declared <c>= null!</c> (the ordinary EF-entity shape) and one declared
    /// <c>= ""</c> are described identically by <c>$metadata</c> and must answer identically.
    /// <c>Issue544NullabilityOmissionTests</c> carries the full three-row table.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Post_OmittingANonNullableEdmProperty_IsAccepted_WhateverTheClrInitializerLeft()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddEntitySetProfile<WbStampedProfile>()
            .AddEntitySetProfile<WbContactProfile>());

        WbStampedProfile.LastPosted = null;
        var response = await fx.Client.PostAsJsonAsync("/odata/WbStamps", new { Id = 9 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(WbStampedProfile.LastPosted);
        Assert.Null(WbStampedProfile.LastPosted!.Stamp);

        WbContactProfile.LastPosted = null;
        var initialized = await fx.Client.PostAsJsonAsync("/odata/WbContacts", new { Id = 9, Age = 1 });
        Assert.Equal(HttpStatusCode.Created, initialized.StatusCode);
        Assert.Equal("", WbContactProfile.LastPosted!.Name);
    }

    /// <summary>
    /// #544 CONTROL: the same <c>= null!</c> fixture still refuses an explicit <c>null</c>, so the
    /// change above narrows the rule rather than removing it.
    /// </summary>
    [Fact]
    public async Task Post_ExplicitNullForAnUninitializedRequiredProperty_StillReturns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbStampedProfile>());

        WbStampedProfile.LastPosted = null;
        var response = await fx.Client.PostAsJsonAsync(
            "/odata/WbStamps", new { Id = 9, Stamp = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(WbStampedProfile.LastPosted);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Stamp", json.GetProperty("error").GetProperty("target").GetString());
    }

    [Fact]
    public async Task Put_NullForANonNullableEdmProperty_Returns400_AndTheHandlerNeverRuns()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        WbContactProfile.LastPut = null;
        using var content = new StringContent(
            "{\"Id\":1,\"Name\":null,\"Age\":2}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/WbContacts(1)", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(WbContactProfile.LastPut);
    }

    [Fact]
    public async Task Patch_ExplicitNullForANonNullableEdmProperty_Returns400_AndTheHandlerNeverRuns()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        WbContactProfile.LastPatchChangedProperties = null;
        using var content = new StringContent(
            "{\"Name\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PatchAsync("/odata/WbContacts(1)", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(WbContactProfile.LastPatchChangedProperties);
    }

    /// <summary>
    /// BOUNDING: PATCH is a PARTIAL update, so a non-nullable property the body never named is not
    /// a violation — only a property the client actually set to null is.
    /// </summary>
    [Fact]
    public async Task Patch_OmittingTheNonNullableProperty_IsStillAccepted()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        using var content = new StringContent("{\"Age\":51}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PatchAsync("/odata/WbContacts(1)", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>BOUNDING: a nullable EDM property may of course be null.</summary>
    [Fact]
    public async Task Post_NullForANullableEdmProperty_IsAccepted()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        var response = await fx.Client.PostAsJsonAsync(
            "/odata/WbContacts", new { Id = 11, Name = "Ada", nick = (string?)null, Age = 3 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// BOUNDING: an omitted KEY on create is normal (server-generated keys), and the EDM declares
    /// every key non-nullable, so the key must be exempt or every such POST would start failing.
    /// </summary>
    [Fact]
    public async Task Post_OmittingTheKey_IsStillAccepted()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbStringKeyProfile>());

        var response = await fx.Client.PostAsJsonAsync(
            "/odata/WbTickets", new { Title = "Broken" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>The navigation-POST create route is a create route and answers the same way.</summary>
    [Fact]
    public async Task NavigationPost_NullForANonNullableEdmProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        WbContactProfile.LastNoteCreated = null;
        using var content = new StringContent(
            "{\"Id\":4,\"Text\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/WbContacts(1)/Notes", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(WbContactProfile.LastNoteCreated);
    }

    /// <summary>
    /// The structural-property write route already refused a null for a non-nullable property — but
    /// it asked the CLR type, for which every reference type is nullable, so a
    /// <c>Nullable="false"</c> string sailed through. One question, one answer: the EDM's.
    /// </summary>
    [Fact]
    public async Task PropertyWrite_NullForANonNullableEdmProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        WbContactProfile.LastPatchChangedProperties = null;
        using var content = new StringContent("{\"value\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/WbContacts(1)/Name", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(WbContactProfile.LastPatchChangedProperties);
    }

    /// <summary>DELETE on a property is "set it to null", and answers the same way.</summary>
    [Fact]
    public async Task PropertyDelete_OnANonNullableEdmProperty_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbContactProfile>());

        WbContactProfile.LastPatchChangedProperties = null;
        var response = await fx.Client.DeleteAsync("/odata/WbContacts(1)/Name");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(WbContactProfile.LastPatchChangedProperties);
    }

    /// <summary>
    /// The escape hatch, for a service whose handler legitimately supplies a value the client is
    /// not expected to send.
    /// </summary>
    [Fact]
    public async Task Post_WithValidationDisabled_ReachesTheHandlerWithTheNull()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<WbUnvalidatedProfile>());

        WbUnvalidatedProfile.LastPosted = null;
        var response = await fx.Client.PostAsJsonAsync(
            "/odata/WbUnvalidatedContacts", new { Id = 9, Name = (string?)null, Age = 1 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(WbUnvalidatedProfile.LastPosted);
        Assert.Null(WbUnvalidatedProfile.LastPosted!.Name);
    }
}

// ── Fixtures ──────────────────────────────────────────────────────────────────

internal class WbNote
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
}

internal class WbContact
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    [JsonPropertyName("nick")]
    public string? Nickname { get; set; }

    public int Age { get; set; }
    public List<WbNote> Notes { get; set; } = new();
}

internal class WbContactProfile : EntitySetProfile<int, WbContact>
{
    public static WbContact? LastPosted;
    public static WbContact? LastPut;
    public static string[]? LastPatchChangedProperties;
    public static WbNote? LastNoteCreated;

    public WbContactProfile() : base(x => x.Id)
    {
        EntitySetName = "WbContacts";

        HasMany(
            x => x.Notes,
            getAll: (_, _) => Task.FromResult<IEnumerable<WbNote>>(Array.Empty<WbNote>()),
            post: (_, note, _) =>
            {
                LastNoteCreated = note;
                return Task.FromResult<WbNote?>(note);
            });

        GetAll = _ => Task.FromResult<IEnumerable<WbContact>>(Array.Empty<WbContact>());
        GetById = (id, _) => Task.FromResult<WbContact?>(new WbContact { Id = id, Name = "Existing" });

        Post = (contact, _) =>
        {
            LastPosted = contact;
            return Task.FromResult<WbContact?>(contact);
        };

        Put = (id, contact, _) =>
        {
            LastPut = contact;
            contact.Id = id;
            return Task.FromResult(contact);
        };

        Patch = (id, delta, _) =>
        {
            LastPatchChangedProperties = delta.GetChangedPropertyNames().ToArray();
            var contact = new WbContact { Id = id, Name = "Existing" };
            delta.Patch(contact);
            return Task.FromResult<WbContact?>(contact);
        };
    }
}

/// <summary>#474: raises the limit above the framework default.</summary>
internal class WbGenerousProfile : EntitySetProfile<int, WbContact>
{
    public WbGenerousProfile() : base(x => x.Id)
    {
        EntitySetName = "WbGenerousContacts";
        MaxRequestBodyBytes = EntitySetDefaults.DefaultMaxRequestBodyBytes * 4;

        GetAll = _ => Task.FromResult<IEnumerable<WbContact>>(Array.Empty<WbContact>());
        Post = (contact, _) => Task.FromResult<WbContact?>(contact);
    }
}

/// <summary>#355 opt-out.</summary>
internal class WbUnvalidatedProfile : EntitySetProfile<int, WbContact>
{
    public static WbContact? LastPosted;

    public WbUnvalidatedProfile() : base(x => x.Id)
    {
        EntitySetName = "WbUnvalidatedContacts";
        ValidateRequestBodyNullability = false;

        GetAll = _ => Task.FromResult<IEnumerable<WbContact>>(Array.Empty<WbContact>());
        Post = (contact, _) =>
        {
            LastPosted = contact;
            return Task.FromResult<WbContact?>(contact);
        };
    }
}

/// <summary>
/// #355: a required property with NO non-null CLR initializer — the ordinary EF-entity shape, and
/// the one where omitting the property is observable on the bound instance.
/// </summary>
internal class WbStamped
{
    public int Id { get; set; }
    public string Stamp { get; set; } = null!;
}

internal class WbStampedProfile : EntitySetProfile<int, WbStamped>
{
    public static WbStamped? LastPosted;

    public WbStampedProfile() : base(x => x.Id)
    {
        EntitySetName = "WbStamps";

        GetAll = _ => Task.FromResult<IEnumerable<WbStamped>>(Array.Empty<WbStamped>());
        Post = (stamped, _) =>
        {
            LastPosted = stamped;
            return Task.FromResult<WbStamped?>(stamped);
        };
    }
}

internal class WbTicket
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>#355 bounding: a server-generated STRING key, omitted by the client on create.</summary>
internal class WbStringKeyProfile : EntitySetProfile<string, WbTicket>
{
    public WbStringKeyProfile() : base(x => x.Code)
    {
        EntitySetName = "WbTickets";

        GetAll = _ => Task.FromResult<IEnumerable<WbTicket>>(Array.Empty<WbTicket>());
        Post = (ticket, _) =>
        {
            ticket.Code = "T-1";
            return Task.FromResult<WbTicket?>(ticket);
        };
    }
}

internal class WbBag
{
    public string? Note { get; set; }
    public Dictionary<string, object?> Extras { get; set; } = new();
}

internal class WbOpenThing
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public WbBag? Bag { get; set; }
    public List<WbNote> Notes { get; set; } = new();
}

/// <summary>#514: a registration whose EDM really has an open complex type.</summary>
internal class WbOpenProfile : EntitySetProfile<int, WbOpenThing>
{
    public WbOpenProfile() : base(x => x.Id)
    {
        EntitySetName = "WbOpenThings";

        HasMany(
            x => x.Notes,
            getAll: (_, _) => Task.FromResult<IEnumerable<WbNote>>(Array.Empty<WbNote>()),
            post: (_, note, _) => Task.FromResult<WbNote?>(note));

        GetAll = _ => Task.FromResult<IEnumerable<WbOpenThing>>(Array.Empty<WbOpenThing>());
        GetById = (id, _) => Task.FromResult<WbOpenThing?>(new WbOpenThing { Id = id, Label = "L" });
        Put = (id, thing, _) =>
        {
            thing.Id = id;
            return Task.FromResult(thing);
        };
    }
}
