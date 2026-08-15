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

internal sealed class OtwStore
{
    internal List<OtwParent> Parents { get; } = new() { new() { Id = 1, Name = "p1" } };
    internal List<OtwChild> Children { get; } = new();

    /// <summary>Non-null only if a create handler actually ran — the "was it persisted?" surface.</summary>
    internal OtwChild? LastChild { get; set; }

    /// <summary>Non-null only if the action handler actually ran.</summary>
    internal ExternalReferenceMetadata? LastActionMeta { get; set; }
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

internal sealed class OtwPlainProfile : EntitySetProfile<int, OtwPlain>
{
    public OtwPlainProfile() : base(x => x.Id)
    {
        EntitySetName = "OtwPlains";
        GetById = (id, ct) => Task.FromResult<OtwPlain?>(new OtwPlain { Id = id, Name = "n" });
        Put = (id, model, ct) => Task.FromResult(model);
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
    /// </summary>
    [Fact]
    public async Task BoundAction_StillAcceptsAConformantParameterAndBindsDynamicKeys()
    {
        (TestFixture fx, OtwStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/OtwActionHosts/Stamp",
            Json("""{ "meta": { "Region": "us", "tier": 4 } }"""));

        Assert.True(
            resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"unexpected {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");

        IDictionary<string, object?> bag = store.LastActionMeta!.KeyValuePairs!;
        Assert.Equal(new[] { "Region", "tier" }, bag.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }
}

/// <summary>
/// #389 L1. <c>CHANGELOG.md</c> and <c>WithOpenTypes</c>'s own remarks promise that opting in on a
/// model with no dictionary member is a no-op and "every response is byte-identical". It was not:
/// the write paths gated on <c>OpenTypesEnabled</c> (did the consumer opt in?) rather than on
/// whether the EDM actually produced an open complex type, so such a registration still buffered
/// every <c>PUT</c> body into a <see cref="JsonDocument"/> and still walked every write body looking
/// for keys that could not exist. The buffering was observable, because the two readers word a
/// malformed-body failure differently.
/// </summary>
public class OpenTypeNoOpenTypesIsByteIdenticalTests
{
    private static Task<TestFixture> BuildAsync(bool openTypes) =>
        TestHostBuilder.BuildAsync(o =>
        {
            if (openTypes) o.WithOpenTypes();
            o.AddEntitySetProfile<OtwPlainProfile>();
        });

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// The exact delta the reviewer measured: <c>JsonDocument.ParseAsync</c> reports no <c>Path</c>
    /// where <c>JsonSerializer.DeserializeAsync</c> reports <c>Path: $</c>, so a malformed PUT body
    /// produced two different 400 messages depending only on whether <c>WithOpenTypes()</c> had been
    /// called — on a model with no open type at all.
    /// </summary>
    [Fact]
    public async Task MalformedPutBody_ProducesTheSameResponseOnAndOff()
    {
        await using TestFixture on = await BuildAsync(openTypes: true);
        await using TestFixture off = await BuildAsync(openTypes: false);

        const string Malformed = "{ not json";

        HttpResponseMessage onResp = await on.Client.PutAsync("/odata/OtwPlains(1)", Json(Malformed));
        HttpResponseMessage offResp = await off.Client.PutAsync("/odata/OtwPlains(1)", Json(Malformed));

        Assert.Equal(offResp.StatusCode, onResp.StatusCode);
        Assert.Equal(
            await offResp.Content.ReadAsStringAsync(),
            await onResp.Content.ReadAsStringAsync());
    }

    /// <summary>A well-formed PUT is byte-identical too — the ordinary path, not just the error one.</summary>
    [Fact]
    public async Task WellFormedPutBody_ProducesTheSameResponseOnAndOff()
    {
        await using TestFixture on = await BuildAsync(openTypes: true);
        await using TestFixture off = await BuildAsync(openTypes: false);

        const string Body = """{ "Id": 1, "Name": "changed" }""";

        HttpResponseMessage onResp = await on.Client.PutAsync("/odata/OtwPlains(1)", Json(Body));
        HttpResponseMessage offResp = await off.Client.PutAsync("/odata/OtwPlains(1)", Json(Body));

        Assert.Equal(offResp.StatusCode, onResp.StatusCode);
        Assert.Equal(
            await offResp.Content.ReadAsStringAsync(),
            await onResp.Content.ReadAsStringAsync());
    }
}
