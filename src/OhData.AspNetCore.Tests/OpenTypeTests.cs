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
            o => o.AddEntitySetProfile<ExternalReferenceProfile>(),
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

    [Fact]
    public async Task Patch_UndeclaredKeysSurviveAlongsideADeclaredProperty()
    {
        (TestFixture fx, ExternalReferenceStore store) = await BuildAsync();
        await using TestFixture _fx = fx;

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
        Assert.Equal("Patched", doc.RootElement.GetProperty("Source").GetString());
        JsonElement meta = doc.RootElement.GetProperty("Metadata");
        Assert.Equal("patchedValue", meta.GetProperty("patchedKey").GetString());
        Assert.False(meta.TryGetProperty("KeyValuePairs", out _));
        Assert.Equal(new[] { "patchedKey" }, store.LastWritten!.Metadata!.KeyValuePairs!.Keys);
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

/// <summary>
/// Zero-delta guarantee: a registration whose model declares no open complex type must never get a
/// derived <see cref="JsonSerializerOptions"/> at all — the serialization pipeline stays
/// reference-identical to what it was before #389, which is what makes "existing suites unchanged"
/// a structural property rather than a hope.
/// </summary>
public class OpenTypeZeroDeltaTests
{
    [Fact]
    public async Task ModelWithNoOpenComplexType_ProducesNoContainersAndNoDerivedOptions()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o =>
            o.AddEntitySetProfile<WidgetProfile>());
        OhDataRegistration reg =
            fx.App.Services.GetRequiredKeyedService<OhDataRegistration>("__default__");

        IReadOnlyDictionary<Type, PropertyInfo> containers =
            OpenTypeJsonOptions.BuildOpenComplexTypeContainerMap(reg.EdmModel);
        Assert.Empty(containers);

        var baseOptions = new JsonSerializerOptions();
        Assert.Same(baseOptions, OpenTypeJsonOptions.Build(baseOptions, containers));
    }

    [Fact]
    public async Task ModelWithAnOpenComplexType_ProducesExactlyOneContainerPerDeclaringType()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o =>
            {
                o.AddEntitySetProfile<ExternalReferenceProfile>();
                o.AddEntitySetProfile<OpenTypeHostProfile>();
            },
            configureServices: s => s.AddSingleton<ExternalReferenceStore>());
        OhDataRegistration reg =
            fx.App.Services.GetRequiredKeyedService<OhDataRegistration>("__default__");

        IReadOnlyDictionary<Type, PropertyInfo> containers =
            OpenTypeJsonOptions.BuildOpenComplexTypeContainerMap(reg.EdmModel);

        // Both ExternalReferenceMetadata and its derived ExternalReferenceMetadataV2 are open in
        // the EDM, but the container is DECLARED once — one entry covers the whole chain.
        PropertyInfo container = Assert.Single(containers).Value;
        Assert.Equal("KeyValuePairs", container.Name);
        Assert.Equal(typeof(ExternalReferenceMetadata), container.DeclaringType);
    }
}
