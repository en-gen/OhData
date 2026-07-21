using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Swashbuckle.Tests;

/// <summary>
/// Issue #258: after #252 made OhData own its response JSON casing (PascalCase by default,
/// independent of the host's <c>HttpJsonOptions</c>), the Swashbuckle-generated schema property
/// names must follow the same policy — otherwise the document advertises casing the wire never
/// uses. These tests assert the schema casing exactly (case-sensitive), so they fail if the filter
/// falls back to the host camelCase default.
/// </summary>
public sealed class SchemaPropertyCasingTests
{
    [Fact]
    public async Task DefaultPolicy_SchemaProperties_ArePascalCase()
    {
        using JsonDocument doc = await BuildAndFetchAsync(configurePolicy: null);
        JsonElement item = CollectionItemSchema(doc);

        Assert.True(HasExactProperty(item, "Id"));
        Assert.True(HasExactProperty(item, "Name"));
        Assert.False(HasExactProperty(item, "id"));
        Assert.False(HasExactProperty(item, "name"));
    }

    [Fact]
    public async Task CamelCaseOptIn_SchemaProperties_AreCamelCase()
    {
        using JsonDocument doc = await BuildAndFetchAsync(
            configurePolicy: o => o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.CamelCase));
        JsonElement item = CollectionItemSchema(doc);

        Assert.True(HasExactProperty(item, "id"));
        Assert.True(HasExactProperty(item, "name"));
        Assert.False(HasExactProperty(item, "Id"));
        Assert.False(HasExactProperty(item, "Name"));
    }

    [Fact]
    public async Task JsonPropertyNameRename_WinsOverPolicy_InBothCasings()
    {
        using JsonDocument defaultDoc = await BuildAndFetchAsync(configurePolicy: null);
        Assert.True(HasExactProperty(CollectionItemSchema(defaultDoc), "sku_code"));

        using JsonDocument camelDoc = await BuildAndFetchAsync(
            configurePolicy: o => o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.CamelCase));
        Assert.True(HasExactProperty(CollectionItemSchema(camelDoc), "sku_code"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<JsonDocument> BuildAndFetchAsync(System.Action<OhDataBuilder>? configurePolicy)
    {
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o =>
        {
            configurePolicy?.Invoke(o);
            o.AddEntitySetProfile<CasingProfile>();
        });
        return await fx.GetDocumentAsync();
    }

    private static JsonElement CollectionItemSchema(JsonDocument doc)
    {
        JsonElement op = doc.RootElement.GetProperty("paths")
            .GetProperty("/odata/CasingWidgets").GetProperty("get");
        JsonElement schema = op.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        JsonElement envelope = Resolve(doc, schema);
        JsonElement items = envelope.GetProperty("properties").GetProperty("value").GetProperty("items");
        return Resolve(doc, items);
    }

    private static bool HasExactProperty(JsonElement resolvedSchema, string propertyName)
    {
        if (!resolvedSchema.TryGetProperty("properties", out JsonElement props)) return false;
        return props.EnumerateObject().Any(p => string.Equals(p.Name, propertyName, System.StringComparison.Ordinal));
    }

    private static JsonElement Resolve(JsonDocument doc, JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out JsonElement refProp))
        {
            string name = refProp.GetString()!.Split('/').Last();
            return doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name);
        }
        return schema;
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";

        [JsonPropertyName("sku_code")]
        public string SkuCode { get; set; } = "";
    }

    private class CasingProfile : EntitySetProfile<int, Widget>
    {
        private readonly List<Widget> _store = new() { new() { Id = 1, Name = "W", SkuCode = "abc" } };

        public CasingProfile() : base(x => x.Id)
        {
            EntitySetName = "CasingWidgets";
            GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
        }
    }
}
