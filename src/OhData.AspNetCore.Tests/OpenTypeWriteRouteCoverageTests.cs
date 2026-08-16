using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #389 round-2 review. Three separate holes in the write-side dynamic-key check, all end-to-end:
//
//   H1  FindInvalidDynamicKey policed only the FIRST level of bag keys. The value of an accepted
//       dynamic key was never walked, so the stored-@odata.type vector the check exists to close
//       stayed open one level down — measured as a 201 followed by the reserved key being echoed
//       verbatim on every subsequent read. OpenTypeJsonOptionsTests covers the walk in isolation;
//       these pin it on the real routes, which is where it was measured.
//
//   H2  The check was wired into POST/PUT/PATCH and the structural-property write route, but NOT
//       into the navigation-POST create route or the action routes. A body rejected with 400 on
//       POST /odata/{Set} was accepted with 201 on POST /odata/{Set}({key})/{Nav} and persisted.
//
//   L1  WithOpenTypes() on a model with NO open complex type was documented as a byte-identical
//       no-op and was not one: the PUT path gated its JsonDocument buffering on the opt-in flag
//       rather than on whether the EDM actually had an open type, which changed the malformed-body
//       error message.
//
// The models reuse ExternalReferenceMetadata from OpenTypeTests deliberately — it is the exact
// shape docs/open-types.md publishes, and it carries no serialization attributes.

// ── Navigation-POST models ──────────────────────────────────────────────────────────────────────

public sealed class OtwChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public ExternalReferenceMetadata? Meta { get; set; }
}

public sealed class OtwParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<OtwChild> Children { get; set; } = new();
}

/// <summary>Action-parameter host — a distinct entity type so it gets its own entity set.</summary>
public sealed class OtwActionHost
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A model with no dictionary member anywhere: the L1 "nothing to do" case.</summary>
public sealed class OtwPlain
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// #389 round-3 L1. <c>MetaMap</c> is a DICTIONARY-valued declared member whose values are an open
/// complex type. It is not itself a dynamic-property container — its value type is not
/// <c>object</c>, so <c>ODataConventionModelBuilder</c> maps it as an ordinary property — but
/// <c>System.Text.Json</c> binds straight through it into each value's extension data, which is
/// exactly the bag <c>Metadata</c> reaches one member over.
/// </summary>
public sealed class OtwDictHost
{
    public int Id { get; set; }
    public ExternalReferenceMetadata? Metadata { get; set; }
    public IDictionary<string, ExternalReferenceMetadata>? MetaMap { get; set; }
}

internal sealed class OtwStore
{
    internal List<OtwParent> Parents { get; } = new() { new() { Id = 1, Name = "p1" } };
    internal List<OtwChild> Children { get; } = new();

    /// <summary>Non-null only if a create handler actually ran — the "was it persisted?" surface.</summary>
    internal OtwChild? LastChild { get; set; }

    /// <summary>Non-null only if the action handler actually ran.</summary>
    internal ExternalReferenceMetadata? LastActionMeta { get; set; }

    /// <summary>Non-null only if the dictionary-member create handler actually ran.</summary>
    internal OtwDictHost? LastDictHost { get; set; }
}

internal sealed class OtwParentProfile : EntitySetProfile<int, OtwParent>
{
    public OtwParentProfile(OtwStore store) : base(x => x.Id)
    {
        EntitySetName = "OtwParents";
        GetById = (id, ct) => Task.FromResult(store.Parents.FirstOrDefault(p => p.Id == id));
        HasMany(
            navigation: x => x.Children!,
            getAll: (parentId, ct) =>
                Task.FromResult<IEnumerable<OtwChild>>(store.Children.Where(c => c.ParentId == parentId)),
            post: (parentId, child, ct) =>
            {
                if (store.Parents.All(p => p.Id != parentId)) return Task.FromResult<OtwChild?>(null);
                child.Id = 500;
                child.ParentId = parentId;
                store.Children.Add(child);
                store.LastChild = child;
                return Task.FromResult<OtwChild?>(child);
            });
    }
}

internal sealed class OtwActionProfile : EntitySetProfile<int, OtwActionHost>
{
    private readonly OtwStore _store;

    public OtwActionProfile(OtwStore store) : base(x => x.Id)
    {
        _store = store;
        EntitySetName = "OtwActionHosts";
        GetAll = ct => Task.FromResult<IEnumerable<OtwActionHost>>(
            new[] { new OtwActionHost { Id = 1, Name = "a1" } });
        BindAction(Stamp);
    }

    // Action: POST /OtwActionHosts/Stamp  { "meta": { ...open complex value... } }
    private void Stamp(ExternalReferenceMetadata meta) => _store.LastActionMeta = meta;
}

internal sealed class OtwDictHostProfile : EntitySetProfile<int, OtwDictHost>
{
    public OtwDictHostProfile(OtwStore store) : base(x => x.Id)
    {
        EntitySetName = "OtwDictHosts";
        Post = (entity, ct) =>
        {
            store.LastDictHost = entity;
            return Task.FromResult<OtwDictHost?>(entity);
        };
    }
}

/// <summary>
/// The "nothing to do" model: no dictionary member anywhere, so the EDM declares no open complex
/// type and <c>OhDataRegistration.OpenTypesActive</c> stays false whatever the open-types setting is.
/// Every write route is wired (and the structural-property write route rides <c>Patch</c>) because
/// the byte-identical guarantee is asserted per route, not just on <c>PUT</c> — see
/// <see cref="OpenTypeDefaultOnIsByteIdenticalTests"/>. Handlers are pure functions of their input,
/// so two fixtures built from this profile are directly comparable.
/// </summary>
internal sealed class OtwPlainProfile : EntitySetProfile<int, OtwPlain>
{
    public OtwPlainProfile() : base(x => x.Id)
    {
        EntitySetName = "OtwPlains";
        GetAll = ct => Task.FromResult<IEnumerable<OtwPlain>>(new[] { new OtwPlain { Id = 1, Name = "n" } });
        GetById = (id, ct) => Task.FromResult<OtwPlain?>(new OtwPlain { Id = id, Name = "n" });
        Post = (model, ct) => Task.FromResult<OtwPlain?>(model);
        Put = (id, model, ct) => Task.FromResult(model);
        Patch = (id, delta, ct) =>
        {
            var target = new OtwPlain { Id = id, Name = "n" };
            delta.Patch(target);
            return Task.FromResult<OtwPlain?>(target);
        };
        Delete = (id, ct) => Task.FromResult(true);
    }
}

public class OpenTypeWriteRouteCoverageTests
{
    private static async Task<(TestFixture Fx, OtwStore Store)> BuildAsync()
    {
        var store = new OtwStore();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            o =>
            {
                o.WithOpenTypes();
                o.AddEntitySetProfile<OtwParentProfile>();
                o.AddEntitySetProfile<OtwActionProfile>();
                o.AddEntitySetProfile<OtwDictHostProfile>();
            },
            configureServices: s => s.AddSingleton(store));
        return (fx, store);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task AssertInvalidDynamicKeyAsync(HttpResponseMessage resp, string expectedKey)
    {
        string raw = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {(int)resp.StatusCode}: {raw}");
        using JsonDocument doc = JsonDocument.Parse(raw);
        JsonElement error = doc.RootElement.GetProperty("error");
        Assert.Equal("InvalidBody", error.GetProperty("code").GetString());
        Assert.Equal(expectedKey, error.GetProperty("target").GetString());
    }

    // ── H2: the navigation-POST create route ────────────────────────────────────────────────────

    /// <summary>
    /// The measured H2 repro. This is a documented create route, and the identical body on
    /// <c>POST /odata/{Set}</c> already returned 400 — here it returned 201 and the reserved key was
    /// persisted, to be echoed on every later read of the child.
    /// </summary>
    [Fact]
    public async Task NavigationPost_RejectsAReservedDynamicKey()
    {
        (TestFixture fx, OtwStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/OtwParents(1)/Children",
            Json("""{ "Id": 0, "Meta": { "@odata.type": "#Evil" } }"""));

        await AssertInvalidDynamicKeyAsync(resp, "@odata.type");

        // Rejected BEFORE the handler ran, so nothing reached the store.
        Assert.Null(store.LastChild);
        Assert.Empty(store.Children);
    }

    /// <summary>H1 and H2 together: a reserved key nested inside a dynamic value on the nav route.</summary>
    [Fact]
    public async Task NavigationPost_RejectsAReservedKeyNestedInsideADynamicValue()
    {
        (TestFixture fx, OtwStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/OtwParents(1)/Children",
            Json("""{ "Id": 0, "Meta": { "ok": 1, "nested": { "@odata.id": "http://evil/x" } } }"""));

        await AssertInvalidDynamicKeyAsync(resp, "@odata.id");
        Assert.Null(store.LastChild);
    }

    /// <summary>
    /// The check must not have broken the route it was added to: a conformant body still creates,
    /// and its dynamic keys still bind flat into the bag.
    /// </summary>
    [Fact]
    public async Task NavigationPost_StillAcceptsAConformantBodyAndBindsDynamicKeys()
    {
        (TestFixture fx, OtwStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/OtwParents(1)/Children",
            Json("""{ "Id": 0, "Meta": { "tier": 7, "nested": { "deep": true } } }"""));

        Assert.True(
            resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"unexpected {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");

        IDictionary<string, object?> bag = store.LastChild!.Meta!.KeyValuePairs!;
        Assert.Equal(new[] { "nested", "tier" }, bag.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    // ── H2: bound action parameters ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An action parameter is not an entity write, but it binds into the same CLR types and reaches
    /// the same handlers, so a persisted parameter stores the reserved key exactly as an entity body
    /// would. Measured before the fix: 200, and the handler received it.
    /// </summary>
    [Fact]
    public async Task BoundAction_RejectsAReservedDynamicKeyInAParameter()
    {
        (TestFixture fx, OtwStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/OtwActionHosts/Stamp",
            Json("""{ "meta": { "Region": "us", "@odata.type": "#Evil" } }"""));

        await AssertInvalidDynamicKeyAsync(resp, "@odata.type");
        Assert.Null(store.LastActionMeta);
    }

    [Fact]
    public async Task BoundAction_RejectsAReservedKeyNestedInsideADynamicValue()
    {
        (TestFixture fx, OtwStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/OtwActionHosts/Stamp",
            Json("""{ "meta": { "list": [ { "@odata.id": "http://evil/x" } ] } }"""));

        await AssertInvalidDynamicKeyAsync(resp, "@odata.id");
        Assert.Null(store.LastActionMeta);
    }

    /// <summary>
    /// The parameter ENVELOPE is not a bag. Its keys are parameter names matched against the
    /// operation's signature, so they must not be policed as dynamic property names — otherwise
    /// wiring the check in here would have been a behavior change for every action.
    /// <para>
    /// The envelope carries <c>meta@odata.type</c> deliberately (#389 round-3 INFO-3). A plain
    /// <c>{"meta":{…}}</c> envelope does not discriminate: <c>meta</c> is itself a conformant
    /// identifier, so the test passed identically under the buggy logic of checking the whole
    /// envelope against the parameter's declared type. An envelope key that is <i>not</i> an
    /// identifier fails the moment the envelope is policed, which is the claim being pinned.
    /// </para>
    /// </summary>
    [Fact]
    public async Task BoundAction_StillAcceptsAConformantParameterAndBindsDynamicKeys()
    {
        (TestFixture fx, OtwStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/OtwActionHosts/Stamp",
            Json("""{ "meta@odata.type": "#x", "meta": { "Region": "us", "tier": 4 } }"""));

        Assert.True(
            resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"unexpected {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");

        IDictionary<string, object?> bag = store.LastActionMeta!.KeyValuePairs!;
        Assert.Equal(new[] { "Region", "tier" }, bag.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    // ── Round-3 L1: the walk stops at a DICTIONARY-valued declared member ────────────────────────
    //
    // FindInvalidDynamicKey resolved the member's JsonTypeInfo and bailed on anything whose Kind was
    // not Object. An IDictionary<string, TOpenComplex> member is Kind == Dictionary, so the walk
    // stopped there — while System.Text.Json bound straight through it into each value's extension
    // data. Measured: the byte-identical keys were rejected with a 400 through `Metadata` and
    // accepted with a 201 through `MetaMap`, then echoed on every later read.

    /// <summary>The measured repro: a reserved key inside a dictionary-valued declared member.</summary>
    [Fact]
    public async Task Post_RejectsAReservedKeyInsideADictionaryValuedMember()
    {
        (TestFixture fx, OtwStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/OtwDictHosts",
            Json("""{ "Id": 0, "MetaMap": { "one": { "@odata.type": "#Evil", "has space": 1 } } }"""));

        await AssertInvalidDynamicKeyAsync(resp, "@odata.type");
        Assert.Null(store.LastDictHost);
    }

    /// <summary>
    /// The control for the test above — the same keys one member over, through a plain complex
    /// member. Both must give the same answer; that they did not is the whole finding.
    /// </summary>
    [Fact]
    public async Task Post_RejectsTheSameReservedKeyThroughAPlainComplexMember()
    {
        (TestFixture fx, OtwStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/OtwDictHosts",
            Json("""{ "Id": 0, "Metadata": { "@odata.type": "#Evil" } }"""));

        await AssertInvalidDynamicKeyAsync(resp, "@odata.type");
        Assert.Null(store.LastDictHost);
    }

    /// <summary>
    /// The dictionary's own KEYS are declared-member map keys, not dynamic property names, so they
    /// are deliberately NOT held to the identifier grammar — only the VALUES are walked. Without
    /// this the fix would "pass" by rejecting every map key a consumer has always been allowed to
    /// send.
    /// </summary>
    [Fact]
    public async Task Post_DoesNotPoliceTheMapKeysOfADictionaryValuedMember()
    {
        (TestFixture fx, OtwStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/OtwDictHosts",
            Json("""{ "Id": 0, "MetaMap": { "has space": { "tier": 7 } } }"""));

        Assert.True(
            resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"unexpected {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");

        IDictionary<string, object?> bag = store.LastDictHost!.MetaMap!["has space"].KeyValuePairs!;
        Assert.Equal(new[] { "tier" }, bag.Keys);
    }
}

/// <summary>
/// The guarantee that keeps the default-ON flip (#389) from touching anyone who does not have a
/// dictionary member: a model with no open complex type must be <b>byte-identical</b> — status and
/// full response body — between the default and <c>WithOpenTypes(false)</c>. Since the flag now
/// defaults to <c>true</c>, this is no longer a promise made to an opted-in minority; it is the
/// blast-radius bound for every existing registration in the wild, which is why the matrix below
/// covers every write route and every malformed-body shape rather than just <c>PUT</c>.
/// <para>
/// #389 L1 is the reason it is asserted rather than assumed. The write paths originally gated on
/// <c>OpenTypesEnabled</c> (what did the consumer ask for?) rather than on
/// <c>OpenTypesActive</c> (did the EDM actually produce an open complex type?), so such a
/// registration still buffered every <c>PUT</c> body into a <see cref="JsonDocument"/> and still
/// walked every write body looking for keys that could not exist. The buffering was observable,
/// because the two readers word a malformed-body failure differently:
/// <c>JsonDocument.ParseAsync</c> reports no <c>Path</c> where
/// <c>JsonSerializer.DeserializeAsync</c> reports <c>Path: $</c>.
/// </para>
/// </summary>
public class OpenTypeDefaultOnIsByteIdenticalTests
{
    // null = configure nothing, which is the case that matters now: the DEFAULT versus the opt-out.
    private static Task<TestFixture> BuildAsync(bool? openTypes) =>
        TestHostBuilder.BuildAsync(o =>
        {
            if (openTypes is bool explicitly) o.WithOpenTypes(explicitly);
            o.AddEntitySetProfile<OtwPlainProfile>();
        });

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Every body shape a write route can be handed, including the ones that fail before any handler
    /// runs. The malformed cases are the discriminating ones — buffering the body changes the error
    /// wording, which is exactly how the original defect was found.
    /// </summary>
    public static TheoryData<string, string> Bodies() => new()
    {
        { "well-formed", """{ "Id": 1, "Name": "changed" }""" },
        { "malformed", "{ not json" },
        { "trailing-garbage", """{ "Id": 1, "Name": "x" } trailing""" },
        { "empty", "" },
        { "whitespace-only", "   " },
        { "array", """[ { "Id": 1 } ]""" },
        { "bare-null", "null" },
        { "bare-string", "\"just a string\"" },
        { "bare-number", "42" },
        { "bare-bool", "true" },
        { "empty-object", "{}" },
        // 40 levels of nesting: deep enough to be a non-trivial walk, well inside JsonDocument's
        // own 64-level cap so both fixtures take the same branch rather than both 400ing on depth.
        { "deep", Deep(40) },
    };

    private static string Deep(int depth) =>
        """{ "Id": 1, "Name": "x", "Nested": """
        + new string('[', depth) + new string(']', depth)
        + " }";

    /// <summary>
    /// The whole matrix: every write route × every body shape, default versus opt-out, comparing
    /// status <b>and</b> the full response body.
    /// </summary>
    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task EveryWriteRoute_IsByteIdenticalBetweenTheDefaultAndTheOptOut(string label, string body)
    {
        await using TestFixture on = await BuildAsync(openTypes: null);
        await using TestFixture off = await BuildAsync(openTypes: false);

        (string Method, string Url)[] routes =
        {
            ("POST", "/odata/OtwPlains"),
            ("PUT", "/odata/OtwPlains(1)"),
            ("PATCH", "/odata/OtwPlains(1)"),
            // Structural-property write route — rides Patch, and is one of the routes the
            // dynamic-key check is wired into, so it has to be in the comparison.
            ("PUT", "/odata/OtwPlains(1)/Name"),
            ("PATCH", "/odata/OtwPlains(1)/Name"),
        };

        foreach ((string method, string url) in routes)
        {
            HttpResponseMessage onResp = await Send(on, method, url, body);
            HttpResponseMessage offResp = await Send(off, method, url, body);

            string context = $"{method} {url} [{label}]";
            Assert.Equal(offResp.StatusCode, onResp.StatusCode);
            Assert.Equal(
                (context, await offResp.Content.ReadAsStringAsync()),
                (context, await onResp.Content.ReadAsStringAsync()));
        }
    }

    /// <summary>
    /// A non-JSON <c>Content-Type</c> short-circuits with <c>415</c> before the body is read at all,
    /// which is a different branch from the malformed-JSON one above.
    /// </summary>
    [Fact]
    public async Task WrongContentType_IsByteIdenticalBetweenTheDefaultAndTheOptOut()
    {
        await using TestFixture on = await BuildAsync(openTypes: null);
        await using TestFixture off = await BuildAsync(openTypes: false);

        foreach (string contentType in new[] { "text/plain", "application/xml" })
        {
            foreach ((string method, string url) in new[]
                     {
                         ("POST", "/odata/OtwPlains"),
                         ("PUT", "/odata/OtwPlains(1)"),
                         ("PATCH", "/odata/OtwPlains(1)"),
                     })
            {
                HttpResponseMessage onResp = await Send(on, method, url, """{ "Id": 1 }""", contentType);
                HttpResponseMessage offResp = await Send(off, method, url, """{ "Id": 1 }""", contentType);

                string context = $"{method} {url} [{contentType}]";
                Assert.Equal(offResp.StatusCode, onResp.StatusCode);
                Assert.Equal(
                    (context, await offResp.Content.ReadAsStringAsync()),
                    (context, await onResp.Content.ReadAsStringAsync()));
            }
        }
    }

    /// <summary>Reads have no body to walk, but the serializer options they use are the same object
    /// the write paths derive from — so the read side is compared too.</summary>
    [Fact]
    public async Task Reads_AreByteIdenticalBetweenTheDefaultAndTheOptOut()
    {
        await using TestFixture on = await BuildAsync(openTypes: null);
        await using TestFixture off = await BuildAsync(openTypes: false);

        foreach (string url in new[]
                 {
                     "/odata/OtwPlains",
                     "/odata/OtwPlains(1)",
                     "/odata/OtwPlains(1)/Name",
                     "/odata/OtwPlains(1)/Name/$value",
                     "/odata/$metadata",
                     "/odata",
                 })
        {
            HttpResponseMessage onResp = await on.Client.GetAsync(url);
            HttpResponseMessage offResp = await off.Client.GetAsync(url);

            Assert.Equal(offResp.StatusCode, onResp.StatusCode);
            Assert.Equal(
                (url, await offResp.Content.ReadAsStringAsync()),
                (url, await onResp.Content.ReadAsStringAsync()));
        }
    }

    // Awaited rather than returned unawaited, so the `using` disposes the request only after the
    // send has completed. HttpClient buffers the response content by default, so the response
    // stays readable afterwards.
    private static async Task<HttpResponseMessage> Send(
        TestFixture fx, string method, string url, string body, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        return await fx.Client.SendAsync(request);
    }
}
