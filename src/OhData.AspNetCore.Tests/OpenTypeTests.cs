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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #389: OData open complex types — an arbitrary caller-supplied key/value bag whose keys are
// SIBLINGS of the declared properties on the wire, never nested under the container property's
// own name.
//
// THE POINT OF THIS FILE: the models below carry NO serialization attributes whatsoever. They are
// verbatim the shape a consumer publishes in a shared contract package. Support is driven purely
// by the EDM annotation ODataConventionModelBuilder already writes when it infers a dynamic
// property container (see OpenTypeJsonOptions). If any test here starts passing only after an
// attribute is added to a model in this file, the feature has regressed to the thing #389
// explicitly rejects.

// ── Models: EXACTLY the motivating shape from #389, unmodified ──────────────────────────────────

public record ExternalReferenceMetadata
{
    public IDictionary<string, object?>? KeyValuePairs { get; set; }
}

public record ExternalReference
{
    public Guid Id { get; set; }
    public string Source { get; set; } = "";
    public string Xref { get; set; } = "";
    public ExternalReferenceMetadata? Metadata { get; set; }
}

/// <summary>Derived open complex type — the container is declared on the BASE type.</summary>
public record ExternalReferenceMetadataV2 : ExternalReferenceMetadata
{
    public string? Channel { get; set; }
}

public record OpenTypeHost
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ExternalReferenceMetadataV2? Meta { get; set; }
}

/// <summary>
/// Singleton backing store. Profiles are registered <b>scoped</b>, so a per-profile
/// <c>List&lt;T&gt;</c> would be re-seeded on every request and no write would ever be visible to
/// a later read — the state has to outlive the request for a write test to mean anything.
/// </summary>
internal sealed class ExternalReferenceStore
{
    internal static readonly Guid Seed = Guid.Parse("11111111-1111-1111-1111-111111111111");

    internal List<ExternalReference> Items { get; } = new()
    {
        new()
        {
            Id = Seed,
            Source = "LeaseAccounting",
            Xref = "xref-1",
            Metadata = new ExternalReferenceMetadata
            {
                KeyValuePairs = new Dictionary<string, object?>
                {
                    ["organizationCreatedDate"] = "2026-01-01T00:00:00.0000000+00:00",
                    ["tier"] = 3,
                },
            },
        },
    };

    /// <summary>The last entity a write handler received — the write-side assertion surface.</summary>
    internal ExternalReference? LastWritten { get; set; }
}

internal sealed class ExternalReferenceProfile : EntitySetProfile<Guid, ExternalReference>
{
    public ExternalReferenceProfile(ExternalReferenceStore store) : base(x => x.Id)
    {
        EntitySetName = "ExternalReferences";
        SelectEnabled = true;
        FilterEnabled = true;

        GetAll = ct => Task.FromResult<IEnumerable<ExternalReference>>(store.Items);
        GetById = (id, ct) => Task.FromResult(store.Items.FirstOrDefault(x => x.Id == id));

        Post = (entity, ct) =>
        {
            store.LastWritten = entity;
            if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();
            store.Items.Add(entity);
            return Task.FromResult<ExternalReference?>(entity);
        };

        Put = (id, entity, ct) =>
        {
            store.LastWritten = entity;
            entity.Id = id;
            store.Items.RemoveAll(x => x.Id == id);
            store.Items.Add(entity);
            return Task.FromResult(entity);
        };

        Patch = (id, delta, ct) =>
        {
            ExternalReference? existing = store.Items.FirstOrDefault(x => x.Id == id);
            if (existing is null) return Task.FromResult<ExternalReference?>(null);
            delta.Patch(existing);
            store.LastWritten = existing;
            return Task.FromResult<ExternalReference?>(existing);
        };
    }
}

internal sealed class OpenTypeHostProfile : EntitySetProfile<int, OpenTypeHost>
{
    public OpenTypeHostProfile() : base(x => x.Id)
    {
        EntitySetName = "OpenTypeHosts";
        GetAll = ct => Task.FromResult<IEnumerable<OpenTypeHost>>(new[]
        {
            new OpenTypeHost
            {
                Id = 1,
                Name = "h1",
                Meta = new ExternalReferenceMetadataV2
                {
                    Channel = "web",
                    KeyValuePairs = new Dictionary<string, object?> { ["inherited"] = "yes" },
                },
            },
        });
    }
}

public class OpenTypeReadTests
{
    private static async Task<TestFixture> BuildAsync() =>
        await TestHostBuilder.BuildAsync(
            o =>
            {
                o.WithOpenTypes();
                o.AddEntitySetProfile<ExternalReferenceProfile>();
                o.AddEntitySetProfile<OpenTypeHostProfile>();
            },
            configureServices: s => s.AddSingleton<ExternalReferenceStore>());

    [Fact]
    public async Task CollectionGet_WritesDynamicKeysFlat()
    {
        await using TestFixture fx = await BuildAsync();
        string body = await fx.Client.GetStringAsync("/odata/ExternalReferences");

        // Asserted FIRST and on the raw body, so a regression to the nested shape fails with a
        // message that names the actual defect rather than an incidental missing-key lookup.
        Assert.False(
            body.Contains("KeyValuePairs", StringComparison.OrdinalIgnoreCase),
            "dynamic properties are nested under the container name instead of flattened: " + body);

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement meta = doc.RootElement.GetProperty("value")[0].GetProperty("Metadata");

        // Dynamic keys are SIBLINGS of the declared properties of the complex value...
        Assert.Equal(
            "2026-01-01T00:00:00.0000000+00:00",
            meta.GetProperty("organizationCreatedDate").GetString());
        Assert.Equal(3, meta.GetProperty("tier").GetInt32());
        // ...and the container itself never appears on the wire.
        Assert.False(meta.TryGetProperty("KeyValuePairs", out _));
    }

    [Fact]
    public async Task GetById_WritesDynamicKeysFlat()
    {
        await using TestFixture fx = await BuildAsync();
        string body = await fx.Client.GetStringAsync(
            $"/odata/ExternalReferences({ExternalReferenceStore.Seed})");

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement meta = doc.RootElement.GetProperty("Metadata");
        Assert.Equal(3, meta.GetProperty("tier").GetInt32());
        Assert.False(meta.TryGetProperty("KeyValuePairs", out _));
    }

    [Fact]
    public async Task DerivedOpenComplexType_FlattensContainerDeclaredOnBase()
    {
        await using TestFixture fx = await BuildAsync();
        string body = await fx.Client.GetStringAsync("/odata/OpenTypeHosts");

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement meta = doc.RootElement.GetProperty("value")[0].GetProperty("Meta");
        Assert.Equal("web", meta.GetProperty("Channel").GetString());   // declared, on the derived type
        Assert.Equal("yes", meta.GetProperty("inherited").GetString()); // dynamic, container on the base
        Assert.False(meta.TryGetProperty("KeyValuePairs", out _));
    }

    [Fact]
    public async Task Csdl_DeclaresOpenTypeAndOmitsContainerProperty()
    {
        await using TestFixture fx = await BuildAsync();
        string csdl = await fx.Client.GetStringAsync("/odata/$metadata");

        // The CSDL half is already produced by ODataConventionModelBuilder; asserted here so a
        // change that silently stopped marking the type open (and thus stopped driving the
        // serializer) fails loudly.
        Assert.Contains(
            "<ComplexType Name=\"ExternalReferenceMetadata\" OpenType=\"true\" />",
            csdl, StringComparison.Ordinal);
        Assert.DoesNotContain("KeyValuePairs", csdl, StringComparison.Ordinal);
        Assert.Contains(
            "<ComplexType Name=\"ExternalReferenceMetadataV2\" BaseType=" +
            "\"OhData.AspNetCore.Tests.ExternalReferenceMetadata\" OpenType=\"true\">",
            csdl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Select_Container_PreservesDynamicKeys()
    {
        await using TestFixture fx = await BuildAsync();
        string body = await fx.Client.GetStringAsync("/odata/ExternalReferences?$select=Metadata");

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement entity = doc.RootElement.GetProperty("value")[0];
        Assert.False(entity.TryGetProperty("Source", out _)); // proves the strip actually ran
        JsonElement meta = entity.GetProperty("Metadata");
        Assert.Equal(3, meta.GetProperty("tier").GetInt32());
        Assert.Equal(
            "2026-01-01T00:00:00.0000000+00:00",
            meta.GetProperty("organizationCreatedDate").GetString());
    }

    [Fact]
    public async Task PropertyRoute_ReadsContainerFlat()
    {
        await using TestFixture fx = await BuildAsync();
        string body = await fx.Client.GetStringAsync(
            $"/odata/ExternalReferences({ExternalReferenceStore.Seed})/Metadata");

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement value = doc.RootElement.GetProperty("value");
        Assert.Equal(3, value.GetProperty("tier").GetInt32());
        Assert.False(value.TryGetProperty("KeyValuePairs", out _));
    }
}

public class OpenTypeWriteTests
{
    private static async Task<(TestFixture Fx, ExternalReferenceStore Store)> BuildAsync()
    {
        var store = new ExternalReferenceStore();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            o =>
            {
                o.WithOpenTypes();
                o.AddEntitySetProfile<ExternalReferenceProfile>();
            },
            configureServices: s => s.AddSingleton(store));
        return (fx, store);
    }

    private static StringContent Json(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Post_UndeclaredKeysBindAndEcho()
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync("/odata/ExternalReferences", Json(
            """
            { "Source": "LeaseAccounting", "Xref": "new-1",
              "Metadata": { "organizationCreatedDate": "2026-02-02T00:00:00.0000000+00:00", "tier": 9 } }
            """));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement meta = doc.RootElement.GetProperty("Metadata");
        // The echo is serialized from the entity the HANDLER received, so a value present here
        // round-tripped through binding into the dictionary and back out flat.
        Assert.Equal(9, meta.GetProperty("tier").GetInt32());
        Assert.Equal(
            "2026-02-02T00:00:00.0000000+00:00",
            meta.GetProperty("organizationCreatedDate").GetString());
        Assert.False(meta.TryGetProperty("KeyValuePairs", out _));

        // ...and the CLR side: the undeclared keys actually REACHED the handler, in the bag.
        IDictionary<string, object?> bag = store.LastWritten!.Metadata!.KeyValuePairs!;
        Assert.Equal(new[] { "organizationCreatedDate", "tier" }, bag.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(9, ((JsonElement)bag["tier"]!).GetInt32());
    }

    [Fact]
    public async Task Put_UndeclaredKeysBindAndEcho()
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PutAsync(
            $"/odata/ExternalReferences({ExternalReferenceStore.Seed})", Json(
            $$"""
            { "Id": "{{ExternalReferenceStore.Seed}}", "Source": "S", "Xref": "X",
              "Metadata": { "replaced": true } }
            """));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement meta = doc.RootElement.GetProperty("Metadata");
        Assert.True(meta.GetProperty("replaced").GetBoolean());
        Assert.False(meta.TryGetProperty("KeyValuePairs", out _));
        Assert.Equal(new[] { "replaced" }, store.LastWritten!.Metadata!.KeyValuePairs!.Keys);
    }

    /// <summary>
    /// <b>PATCH of a complex member REPLACES the whole complex value — it does not merge.</b> The
    /// seeded dynamic keys (<c>organizationCreatedDate</c>, <c>tier</c>) are gone afterwards, and so
    /// is every declared property of the complex value the request did not restate.
    /// <para>
    /// This is pre-existing behavior for any complex member (<c>OhDataEndpointFactory</c>'s PATCH
    /// delta loop deserializes each present body member wholesale into the CLR property type), not
    /// something #389 introduced — but open types widen its blast radius from "one nullable member"
    /// to "the entire caller-supplied bag", so it is pinned here rather than left implied. The
    /// earlier name for this test claimed the keys <i>survived</i>; they never did.
    /// <c>docs/open-types.md</c> carries the read-modify-write recipe for touching one dynamic key.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Patch_ReplacesTheWholeComplexValue_SeededDynamicKeysAreLost()
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        // Precondition: the seed really does carry two dynamic keys before the PATCH.
        Assert.Equal(
            new[] { "organizationCreatedDate", "tier" },
            store.Items[0].Metadata!.KeyValuePairs!.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var req = new HttpRequestMessage(HttpMethod.Patch,
            $"/odata/ExternalReferences({ExternalReferenceStore.Seed})")
        {
            Content = Json(
                """
                { "Source": "Patched", "Metadata": { "patchedKey": "patchedValue" } }
                """),
        };
        HttpResponseMessage resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        // A declared property of the ENTITY that the body did restate is patched normally...
        Assert.Equal("Patched", doc.RootElement.GetProperty("Source").GetString());
        JsonElement meta = doc.RootElement.GetProperty("Metadata");
        Assert.Equal("patchedValue", meta.GetProperty("patchedKey").GetString());
        Assert.False(meta.TryGetProperty("KeyValuePairs", out _));

        // ...but the complex value itself was REPLACED: the keys the request did not restate are
        // gone, from the response and from the store alike.
        Assert.False(meta.TryGetProperty("tier", out _));
        Assert.False(meta.TryGetProperty("organizationCreatedDate", out _));
        Assert.Equal(new[] { "patchedKey" }, store.LastWritten!.Metadata!.KeyValuePairs!.Keys);
    }

    /// <summary>
    /// The other half of the replace contract: a PATCH that OMITS the complex member entirely
    /// leaves it — and every dynamic key in it — untouched. This is the only reading under which
    /// "undeclared keys survive a PATCH" is true.
    /// </summary>
    [Fact]
    public async Task Patch_OmittingTheComplexMemberLeavesItsDynamicKeysIntact()
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        var req = new HttpRequestMessage(HttpMethod.Patch,
            $"/odata/ExternalReferences({ExternalReferenceStore.Seed})")
        {
            Content = Json("""{ "Source": "Patched" }"""),
        };
        HttpResponseMessage resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement meta = doc.RootElement.GetProperty("Metadata");
        Assert.Equal(3, meta.GetProperty("tier").GetInt32());
        Assert.Equal(
            new[] { "organizationCreatedDate", "tier" },
            store.LastWritten!.Metadata!.KeyValuePairs!.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    /// A dynamic key is persisted verbatim and echoed on every later read, so an unconstrained one
    /// is a <b>stored</b> fault against other consumers, not a one-request nuisance:
    /// <c>@odata.type</c> inside a complex value is what a conforming reader (Microsoft.OData.Client
    /// among them) uses to resolve that value's type, and <c>@odata.id</c> is an entity reference.
    /// Nested under a declared container these are inert payload; flattened, they are control
    /// information — so flattening is exactly what creates the need to police them.
    /// </summary>
    [Theory]
    [InlineData("@odata.type", "\"#Evil.Type\"")]
    [InlineData("@odata.id", "\"http://evil/x\"")]
    [InlineData("", "1")]
    [InlineData("has.dot", "1")]
    [InlineData("has space", "1")]
    [InlineData("Meta@odata.count", "1")]
    public async Task Post_RejectsANonConformantDynamicKey(string key, string value)
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync("/odata/ExternalReferences", Json(
            $$"""{ "Source": "S", "Xref": "X", "Metadata": { {{JsonSerializer.Serialize(key)}}: {{value}} } }"""));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement error = doc.RootElement.GetProperty("error");
        Assert.Equal("InvalidBody", error.GetProperty("code").GetString());
        Assert.Equal(key, error.GetProperty("target").GetString());
        Assert.Contains(key, error.GetProperty("message").GetString()!, StringComparison.Ordinal);

        // Nothing was persisted: the request is rejected before the handler runs.
        Assert.Null(store.LastWritten);
    }

    [Fact]
    public async Task Put_RejectsANonConformantDynamicKey()
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PutAsync(
            $"/odata/ExternalReferences({ExternalReferenceStore.Seed})", Json(
            $$"""
            { "Id": "{{ExternalReferenceStore.Seed}}", "Source": "S", "Xref": "X",
              "Metadata": { "@odata.type": "#Evil.Type" } }
            """));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(store.LastWritten);
    }

    [Fact]
    public async Task Patch_RejectsANonConformantDynamicKey()
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        var req = new HttpRequestMessage(HttpMethod.Patch,
            $"/odata/ExternalReferences({ExternalReferenceStore.Seed})")
        {
            Content = Json("""{ "Metadata": { "@odata.id": "http://evil/x" } }"""),
        };
        HttpResponseMessage resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(store.LastWritten);
    }

    [Fact]
    public async Task PropertyWrite_RejectsANonConformantDynamicKey()
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        var req = new HttpRequestMessage(HttpMethod.Put,
            $"/odata/ExternalReferences({ExternalReferenceStore.Seed})/Metadata")
        {
            Content = Json("""{ "value": { "@odata.type": "#Evil.Type" } }"""),
        };
        HttpResponseMessage resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(store.LastWritten);
    }

    /// <summary>
    /// A well-formed body still binds after the check — the validation must not reject the ordinary
    /// case, and the check itself must not consume the body before the deserializer sees it (PUT
    /// buffers into a JsonDocument under the opt-in, which is where that could go wrong).
    /// </summary>
    [Fact]
    public async Task Put_ConformantDynamicKeysStillBindAfterValidation()
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PutAsync(
            $"/odata/ExternalReferences({ExternalReferenceStore.Seed})", Json(
            $$"""
            { "Id": "{{ExternalReferenceStore.Seed}}", "Source": "S", "Xref": "X",
              "Metadata": { "goodKey": 1, "_alsoGood": 2 } }
            """));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(
            new[] { "_alsoGood", "goodKey" },
            store.LastWritten!.Metadata!.KeyValuePairs!.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    /// The null/empty asymmetry handlers have to live with: a body that carries NO undeclared keys
    /// leaves the container <c>null</c>, not an empty dictionary — <c>System.Text.Json</c> only
    /// materialises the extension-data dictionary when the first unmatched member arrives. Every
    /// bag read therefore needs a null check.
    /// </summary>
    [Fact]
    public async Task Post_WithNoUndeclaredKeys_LeavesTheContainerNullRatherThanEmpty()
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync("/odata/ExternalReferences",
            Json("""{ "Source": "S", "Xref": "X", "Metadata": {} }"""));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotNull(store.LastWritten!.Metadata);
        Assert.Null(store.LastWritten.Metadata!.KeyValuePairs);
    }

    [Fact]
    public async Task PropertyWrite_ReplacesTheWholeComplexValueFlat()
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

        var req = new HttpRequestMessage(HttpMethod.Put,
            $"/odata/ExternalReferences({ExternalReferenceStore.Seed})/Metadata")
        {
            Content = Json("""{ "value": { "onlyKey": 42 } }"""),
        };
        HttpResponseMessage resp = await fx.Client.SendAsync(req);
        Assert.True(
            resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"unexpected {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");

        string after = await fx.Client.GetStringAsync(
            $"/odata/ExternalReferences({ExternalReferenceStore.Seed})/Metadata");
        using JsonDocument doc = JsonDocument.Parse(after);
        JsonElement value = doc.RootElement.GetProperty("value");
        Assert.Equal(42, value.GetProperty("onlyKey").GetInt32());
        Assert.False(value.TryGetProperty("tier", out _));
    }
}

// ── Unusable container fails at MapOhData (#389) ────────────────────────────────────────────────

/// <summary>
/// The idiomatic collection-initializer shape. <c>ODataConventionModelBuilder</c> infers it as a
/// dynamic-property container and marks the type open, but <c>System.Text.Json</c> cannot bind into
/// a member with no setter.
/// </summary>
public record UnbindableBag
{
    public string? Region { get; set; }
    public IDictionary<string, object?> Entries { get; } = new Dictionary<string, object?>();
}

public record UnbindableBagHost
{
    public int Id { get; set; }
    public UnbindableBag? Meta { get; set; }
}

internal sealed class UnbindableBagProfile : EntitySetProfile<int, UnbindableBagHost>
{
    public UnbindableBagProfile() : base(x => x.Id)
    {
        EntitySetName = "UnbindableBags";
        GetAll = ct => Task.FromResult<IEnumerable<UnbindableBagHost>>(Array.Empty<UnbindableBagHost>());
    }
}

public class OpenTypeUnusableContainerTests
{
    /// <summary>
    /// Silently skipping would leave the CSDL saying <c>OpenType="true"</c> with the container
    /// omitted while the wire still nests it under its own name — exactly the EDM/wire mismatch this
    /// feature declines to ship for entity roots. So it is a startup failure naming the type, the
    /// member and the fix. Open types being ON by default, this now fires without the registration
    /// mentioning them at all.
    /// </summary>
    [Fact]
    public async Task GetterOnlyContainer_FailsAtMapOhData_ByDefault()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestHostBuilder.BuildAsync(o =>
                o.AddEntitySetProfile<UnbindableBagProfile>()));

        Assert.Contains("UnbindableBag", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Entries", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no accessible setter", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The escape hatch is also the way out of this startup failure: with open types off the model is
    /// never inspected and the registration starts fine, exactly as it did pre-#389.
    /// </summary>
    [Fact]
    public async Task GetterOnlyContainer_IsNotEvenLookedAt_UnderTheOptOut()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o =>
            o.WithOpenTypes(false).AddEntitySetProfile<UnbindableBagProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/UnbindableBags");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}

// ── Bag key shadowing a declared property (#389) ────────────────────────────────────────────────

/// <summary>
/// Server-side data — not a client body — whose bag carries a key equal to one of the complex
/// type's own declared property names. Reachable in practice: the motivating adopter merges
/// caller-supplied metadata dictionaries into the bag on the server.
/// </summary>
internal sealed class ShadowedKeyHostProfile : EntitySetProfile<int, OpenTypeHost>
{
    public ShadowedKeyHostProfile() : base(x => x.Id)
    {
        EntitySetName = "ShadowedHosts";
        GetAll = ct => Task.FromResult<IEnumerable<OpenTypeHost>>(new[]
        {
            new OpenTypeHost
            {
                Id = 1,
                Name = "h1",
                Meta = new ExternalReferenceMetadataV2
                {
                    Channel = "declared",
                    KeyValuePairs = new Dictionary<string, object?>
                    {
                        ["Channel"] = "fromBag",
                        ["ok"] = 1,
                    },
                },
            },
        });
    }
}

/// <summary>Captures every warning-or-worse record emitted during a test.</summary>
internal sealed class WarningCapture : ILoggerProvider
{
    /// <summary>The formatted MESSAGE of each record — never the attached exception's own text.</summary>
    internal List<string> Warnings { get; } = new();

    /// <summary>
    /// The attached <see cref="Exception"/> of each record that carried one. Separate from
    /// <see cref="Warnings"/> because <c>formatter(state, exception)</c> renders only the message
    /// template: a handler fault logged as "unhandled exception processing {Method} {Path}" puts the
    /// actual cause here and nowhere else, so a test asserting "the real exception was logged" has to
    /// look at this list.
    /// </summary>
    internal List<Exception> Exceptions { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Sink(this);
    public void Dispose() { }

    private sealed class Sink : ILogger
    {
        private readonly WarningCapture _owner;
        internal Sink(WarningCapture owner) => _owner = owner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;
            _owner.Warnings.Add(formatter(state, exception));
            if (exception is not null) _owner.Exceptions.Add(exception);
        }
    }
}

public class OpenTypeShadowedKeyTests
{
    /// <summary>
    /// Emitting both would produce <c>{"Channel":"declared","Channel":"fromBag",…}</c> — a duplicate
    /// JSON property name, which every .NET reader tested resolves in the BAG's favour, making the
    /// declared value unreachable, and which Microsoft's <c>ODataWriter</c> runs an explicit
    /// duplicate-name check rather than emitting.
    /// <para>
    /// The contract is now a hard failure, matching <c>Microsoft.AspNetCore.OData</c>
    /// (<c>DynamicPropertyNameAlreadyUsedAsDeclaredPropertyName</c>, also an
    /// <c>InvalidOperationException</c>). The spec does not decide this — CSDL §6.3/§9.3 say only
    /// that dynamic properties are "uniquely named", and JSON Format defers to RFC 8259's SHOULD —
    /// so the deciding argument is that the condition is SYSTEMATIC: a client cannot cause it, only
    /// server-side code can, and if it fires at all it fires for every row carrying the key.
    /// </para>
    /// <para>
    /// The accepted cost is that a collection endpoint faults rather than serving the remaining rows.
    /// What must NOT happen is a bare/empty 500 — this asserts the OData error envelope and that the
    /// real exception reached the log.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ABagKeyShadowingADeclaredProperty_Fails500WithTheODataErrorEnvelope()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ShadowedKeyHostProfile>(),
            configureServices: s => s.AddSingleton<ILoggerProvider>(capture));

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/ShadowedHosts");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        // The OData error envelope, not an empty body.
        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement error = doc.RootElement.GetProperty("error");
        Assert.Equal("InternalServerError", error.GetProperty("code").GetString());

        // The framework never leaks the exception text to the client...
        Assert.DoesNotContain("fromBag", body, StringComparison.Ordinal);

        // ...but it does log it. The group filter's own message is a generic "unhandled exception
        // processing {Method} {Path}"; the cause travels as the record's attached Exception, which is
        // what has to name the type and the colliding key.
        Assert.Contains(
            capture.Exceptions,
            e => e is InvalidOperationException
                 && e.Message.Contains("ExternalReferenceMetadataV2", StringComparison.Ordinal)
                 && e.Message.Contains("'Channel'", StringComparison.Ordinal));
    }
}

// ── Nameless bag key: empty or whitespace-only (#389) ───────────────────────────────────────────

/// <summary>
/// Server-side data whose bag carries a key with no non-whitespace character. Same provenance as the
/// shadowing case — a handler merging a caller-supplied dictionary — and unreachable from a request
/// body, which is rejected with a <c>400</c> before binding.
/// </summary>
internal sealed class NamelessKeyHostProfile : EntitySetProfile<int, OpenTypeHost>
{
    /// <summary>
    /// Set by the test before the host is built. Static because a profile is resolved per request
    /// from DI and takes no arguments; each test method builds its own host, so the value is read
    /// back on the very next request.
    /// </summary>
    internal static string Key { get; set; } = "";

    public NamelessKeyHostProfile() : base(x => x.Id)
    {
        EntitySetName = "NamelessKeyHosts";
        GetAll = ct => Task.FromResult<IEnumerable<OpenTypeHost>>(new[]
        {
            new OpenTypeHost
            {
                Id = 1,
                Name = "h1",
                Meta = new ExternalReferenceMetadataV2
                {
                    Channel = "web",
                    KeyValuePairs = new Dictionary<string, object?>
                    {
                        [Key] = "namelessValue",
                        ["tier"] = 3,
                    },
                },
            },
        });
    }
}

public class OpenTypeNamelessKeyTests
{
    /// <summary>
    /// Measured before the fix, the empty case emitted
    /// <c>"Meta":{"Channel":"web","":"emptyKey","tier":3}</c> — a payload carrying a property no
    /// conforming OData reader can address, since a name with no non-whitespace character is not an
    /// <c>odataIdentifier</c> (CSDL 4.01 §4.1).
    /// <para>
    /// <b>This is a deliberate divergence from <c>Microsoft.AspNetCore.OData</c>, which silently
    /// skips the empty key</b> (<c>ODataResourceSerializer.cs:820</c>,
    /// <c>if (string.IsNullOrEmpty(dynamicProperty.Key)) continue;</c>). Matching that skip would mean
    /// reintroducing the clone-and-substitute machinery deleted in <c>e0edaac</c>, because the getter
    /// wrapper no longer produces a filtered copy — it inspects and returns the same reference.
    /// Resurrecting deleted code to produce a <i>silent</i> drop is the wrong trade, and throwing is
    /// consistent with the declared-name collision directly above: both have the same cause, that
    /// server-side code put a key in the container that cannot be a valid dynamic property name.
    /// </para>
    /// <para>
    /// The line is now the <b>full identifier grammar</b>, so <c>"has space"</c> and
    /// <c>"@odata.type"</c> fail here too — not only names that are not names at all. It used to be
    /// <c>string.IsNullOrWhiteSpace</c> on the grounds that full validation costs rune enumeration
    /// and a Unicode-category lookup per key per instance; the ASCII <c>SearchValues</c> fast path
    /// and the bounded validated-key cache in <c>OpenTypeJsonOptions</c> are what removed that cost.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    // Names that ARE names, but are not identifiers — newly rejected, and the reason this test is no
    // longer only about nameless keys.
    [InlineData("has space")]
    [InlineData("@odata.type")]
    // Unicode whitespace, not just ASCII: this case is literally NBSP (U+00A0) followed by EM SPACE
    // (U+2003). It pins that nobody narrows the check to an ASCII space/tab/newline test later.
    [InlineData("\u00A0\u2003")]
    public async Task ABagKeyThatIsNotAnIdentifier_Fails500WithTheODataErrorEnvelope(string key)
    {
        NamelessKeyHostProfile.Key = key;
        var capture = new WarningCapture();
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<NamelessKeyHostProfile>(),
            configureServices: s => s.AddSingleton<ILoggerProvider>(capture));

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/NamelessKeyHosts");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        // The OData error envelope, not an empty body.
        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement error = doc.RootElement.GetProperty("error");
        Assert.Equal("InternalServerError", error.GetProperty("code").GetString());

        // The framework never leaks the bag's contents to the client.
        Assert.DoesNotContain("namelessValue", body, StringComparison.Ordinal);

        // ...but the real exception is logged, naming the CLR type. The group filter's own message is
        // a generic "unhandled exception processing {Method} {Path}", so the cause travels as the
        // record's attached Exception.
        InvalidOperationException thrown = Assert.IsType<InvalidOperationException>(
            capture.Exceptions.First(e => e is InvalidOperationException));
        Assert.Contains("ExternalReferenceMetadataV2", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("not a valid OData identifier", thrown.Message, StringComparison.Ordinal);

        // Names and causes, never values: this message reaches log aggregators.
        Assert.DoesNotContain("namelessValue", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The widened check must not become a blanket rejection of every key: an ordinary conformant bag
    /// still serializes flat. Without this the fix could "pass" by faulting on everything.
    /// </summary>
    [Fact]
    public async Task AConformantBagKey_IsUnaffected()
    {
        NamelessKeyHostProfile.Key = "region";
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<NamelessKeyHostProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/NamelessKeyHosts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // Bag keys are emitted verbatim — extension data bypasses the naming policy — so this holds
        // regardless of how the registration cases its declared properties.
        Assert.Contains(
            """region":"namelessValue","tier":3""",
            await resp.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }
}

/// <summary>
/// Open types are <b>on by default</b>, and <c>WithOpenTypes(false)</c> is the escape hatch back to
/// the pre-#389 shape. These tests pin both ends of that: the default flattens without anyone asking,
/// and the opt-OUT restores the container to an ordinary nested declared property in both directions
/// with none of the write-side validation running.
/// <para>
/// This class was <c>OpenTypeOptInTests</c> and asserted the reverse. The reversal is the point: a
/// complex type with a dictionary member <i>is</i> an open type, the CSDL always said so, and the
/// developer should get the conformant shape without having to know the spec.
/// </para>
/// <para>
/// The zero-delta guarantee for models with <i>no</i> open complex type — the thing that keeps this
/// default flip from touching anyone else — lives in
/// <c>OpenTypeDefaultOnIsByteIdenticalTests</c>. The reference-equality half is asserted directly
/// against the builder in <c>OpenTypeJsonOptionsTests</c>.
/// </para>
/// </summary>
public class OpenTypeOptOutTests
{
    private static async Task<(TestFixture Fx, ExternalReferenceStore Store)> BuildAsync(bool? openTypes)
    {
        var store = new ExternalReferenceStore();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            o =>
            {
                // null = say nothing at all, which is the case that matters: the DEFAULT has to
                // flatten, not merely `WithOpenTypes(true)`.
                if (openTypes is bool explicitly) o.WithOpenTypes(explicitly);
                o.AddEntitySetProfile<ExternalReferenceProfile>();
            },
            configureServices: s => s.AddSingleton(store));
        return (fx, store);
    }

    /// <summary>
    /// The headline of the reversal: nothing in this registration mentions open types, and the
    /// container is flattened anyway.
    /// </summary>
    [Fact]
    public async Task ByDefault_TheContainerIsFlattened()
    {
        (TestFixture fx, ExternalReferenceStore _) = await BuildAsync(openTypes: null);
        await using TestFixture _fx = fx;

        string body = await fx.Client.GetStringAsync("/odata/ExternalReferences");
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement meta = doc.RootElement.GetProperty("value")[0].GetProperty("Metadata");
        Assert.Equal(3, meta.GetProperty("tier").GetInt32());
        Assert.False(meta.TryGetProperty("KeyValuePairs", out _));
    }

    /// <summary>Write-side dynamic-key validation is part of the default, not of an opt-in.</summary>
    [Fact]
    public async Task ByDefault_ReservedKeysAreRejected()
    {
        (TestFixture fx, ExternalReferenceStore _) = await BuildAsync(openTypes: null);
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync("/odata/ExternalReferences",
            new StringContent(
                """{ "Source": "S", "Xref": "X", "Metadata": { "@odata.type": "#Evil" } }""",
                Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary><c>WithOpenTypes(true)</c> is still accepted and means what it says.</summary>
    [Fact]
    public async Task ExplicitTrue_IsTheSameAsTheDefault()
    {
        (TestFixture fx, ExternalReferenceStore _) = await BuildAsync(openTypes: true);
        await using TestFixture _fx = fx;

        string body = await fx.Client.GetStringAsync("/odata/ExternalReferences");
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement meta = doc.RootElement.GetProperty("value")[0].GetProperty("Metadata");
        Assert.Equal(3, meta.GetProperty("tier").GetInt32());
        Assert.False(meta.TryGetProperty("KeyValuePairs", out _));
    }

    [Fact]
    public async Task WithOptOut_TheContainerIsStillANestedDeclaredProperty()
    {
        (TestFixture fx, ExternalReferenceStore _) = await BuildAsync(openTypes: false);
        await using TestFixture _fx = fx;

        string body = await fx.Client.GetStringAsync("/odata/ExternalReferences");
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement meta = doc.RootElement.GetProperty("value")[0].GetProperty("Metadata");

        // Pre-#389 shape: nested under the container's own name, NOT flattened.
        Assert.Equal(3, meta.GetProperty("KeyValuePairs").GetProperty("tier").GetInt32());
        Assert.False(meta.TryGetProperty("tier", out _));
    }

    /// <summary>
    /// The reason the escape hatch exists, and the reason the default flip gets a startup warning.
    /// Under the opt-out this body binds the caller's dictionary to the DECLARED
    /// <c>KeyValuePairs</c> property; by default the same body binds a dynamic key literally named
    /// <c>KeyValuePairs</c> whose value is that dictionary — and the response echo of the two is
    /// byte-identical, so an adopter cannot tell them apart from the wire. Both halves are asserted
    /// here, against the store rather than against the response, because the response is exactly
    /// what cannot discriminate.
    /// </summary>
    [Fact]
    public async Task WithOptOut_AnExistingNestedWriteBodyStillBindsToTheDeclaredContainer()
    {
        const string NestedBody =
            """{ "Source": "S", "Xref": "X", "Metadata": { "KeyValuePairs": { "a": 1 } } }""";

        (TestFixture optedOut, ExternalReferenceStore outStore) = await BuildAsync(openTypes: false);
        await using TestFixture _out = optedOut;
        HttpResponseMessage outResp = await optedOut.Client.PostAsync("/odata/ExternalReferences",
            new StringContent(NestedBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, outResp.StatusCode);
        Assert.Equal(new[] { "a" }, outStore.LastWritten!.Metadata!.KeyValuePairs!.Keys);

        // Same body, default settings: the dictionary is now the VALUE of a dynamic key that is
        // itself named "KeyValuePairs". This is the silent re-bind the startup warning exists for.
        (TestFixture onByDefault, ExternalReferenceStore onStore) = await BuildAsync(openTypes: null);
        await using TestFixture _on = onByDefault;
        HttpResponseMessage onResp = await onByDefault.Client.PostAsync("/odata/ExternalReferences",
            new StringContent(NestedBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, onResp.StatusCode);
        Assert.Equal(new[] { "KeyValuePairs" }, onStore.LastWritten!.Metadata!.KeyValuePairs!.Keys);
    }

    /// <summary>Write-side dynamic-key validation goes away with the opt-out, as it did pre-#389.</summary>
    [Fact]
    public async Task WithOptOut_ReservedKeysAreNotRejected()
    {
        (TestFixture fx, ExternalReferenceStore _) = await BuildAsync(openTypes: false);
        await using TestFixture _fx = fx;

        HttpResponseMessage resp = await fx.Client.PostAsync("/odata/ExternalReferences",
            new StringContent(
                """{ "Source": "S", "Xref": "X", "Metadata": { "KeyValuePairs": { "@odata.type": "#Evil" } } }""",
                Encoding.UTF8, "application/json"));

        // Nested under a declared property they are inert payload, never control information.
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }
}

/// <summary>
/// The startup warning that mitigates the default flip. It is the only signal an existing adopter
/// gets before their stored data is already wrong, because the mis-bound response echo is
/// byte-identical to the correct one — a normal breaking change shows up in a staging diff and this
/// one does not.
/// </summary>
public class OpenTypeStartupWarningTests
{
    // WarningCapture sinks EVERY category, not just "OhData", so an unrelated host-startup warning
    // would make a bare Assert.Empty flaky. Select on the phrase unique to this warning instead —
    // which also means "logs nothing" is asserted about THIS warning specifically, which is the
    // claim being made.
    private static List<string> WireShapeWarnings(WarningCapture capture) =>
        capture.Warnings
            .Where(w => w.Contains("is an OData open complex type", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Fires by default, names the CLR type AND the container member, and points at the escape hatch.
    /// </summary>
    [Fact]
    public async Task AnAffectedComplexType_IsNamedInAStartupWarning()
    {
        var capture = new WarningCapture();
        await using TestFixture _ = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ExternalReferenceProfile>(),
            configureServices: s => s
                .AddSingleton<ExternalReferenceStore>()
                .AddSingleton<ILoggerProvider>(capture));

        List<string> warnings = WireShapeWarnings(capture);
        Assert.NotEmpty(warnings);

        // The full CLR type name, not a substring that a derived type would also satisfy.
        Assert.Contains(
            warnings,
            w => w.Contains(typeof(ExternalReferenceMetadata).FullName!, StringComparison.Ordinal)
                 && w.Contains("KeyValuePairs", StringComparison.Ordinal));

        // The escape hatch has to be in every one of them: naming the problem without naming the
        // remedy just sends the reader to the release notes.
        Assert.All(warnings, w => Assert.Contains("WithOpenTypes(false)", w, StringComparison.Ordinal));

        // One per affected type, not one per anything else — no duplicates.
        Assert.Equal(warnings.Count, warnings.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A model with no open complex type must log NOTHING — the untouched-app guarantee covers the
    /// log as well as the wire. <c>OtwPlain</c> has no dictionary member anywhere.
    /// </summary>
    [Fact]
    public async Task AModelWithNoOpenComplexTypes_LogsNothingAtAll()
    {
        var capture = new WarningCapture();
        await using TestFixture _ = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<OtwPlainProfile>(),
            configureServices: s => s.AddSingleton<ILoggerProvider>(capture));

        Assert.Empty(WireShapeWarnings(capture));
    }

    /// <summary>Taking the escape hatch silences it too — there is nothing left to warn about.</summary>
    [Fact]
    public async Task TheOptOut_SilencesTheWarning()
    {
        var capture = new WarningCapture();
        await using TestFixture _ = await TestHostBuilder.BuildAsync(
            o => o.WithOpenTypes(false).AddEntitySetProfile<ExternalReferenceProfile>(),
            configureServices: s => s
                .AddSingleton<ExternalReferenceStore>()
                .AddSingleton<ILoggerProvider>(capture));

        Assert.Empty(WireShapeWarnings(capture));
    }
}
