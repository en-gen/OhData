using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.OpenApi.Tests;

/// <summary>
/// Issue #258: after #252 made OhData own its response JSON casing (PascalCase by default,
/// independent of the host's <c>HttpJsonOptions</c>), the generated OpenAPI schema property names
/// must follow the same policy — otherwise the document advertises casing the wire never uses.
/// These tests assert the schema casing exactly (case-sensitive), so they fail if a generator
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
        // [JsonPropertyName("sku_code")] is the wire name the response emits regardless of policy,
        // so the schema key must be "sku_code" under both the PascalCase default and camelCase.
        using JsonDocument defaultDoc = await BuildAndFetchAsync(configurePolicy: null);
        Assert.True(HasExactProperty(CollectionItemSchema(defaultDoc), "sku_code"));

        using JsonDocument camelDoc = await BuildAndFetchAsync(
            configurePolicy: o => o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.CamelCase));
        Assert.True(HasExactProperty(CollectionItemSchema(camelDoc), "sku_code"));
    }

    [Fact]
    public async Task DefaultPolicy_NestedComplexProperty_SchemaIsPascalCase()
    {
        // A nested complex type (Address) reachable only through a property must be renamed too,
        // not just the top-level entity schema (#260).
        using JsonDocument doc = await BuildAndFetchAsync(configurePolicy: null);
        JsonElement address = SchemaWithProperty(doc, "Street");

        Assert.True(HasExactProperty(address, "Street"));
        Assert.True(HasExactProperty(address, "PostalCode"));
        Assert.False(HasExactProperty(address, "street"));
        Assert.False(HasExactProperty(address, "postalCode"));
    }

    [Fact]
    public async Task CamelCaseOptIn_NestedComplexProperty_SchemaIsCamelCase()
    {
        using JsonDocument doc = await BuildAndFetchAsync(
            configurePolicy: o => o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.CamelCase));
        JsonElement address = SchemaWithProperty(doc, "street");

        Assert.True(HasExactProperty(address, "street"));
        Assert.True(HasExactProperty(address, "postalCode"));
        Assert.False(HasExactProperty(address, "Street"));
        Assert.False(HasExactProperty(address, "PostalCode"));
    }

    [Fact]
    public async Task DefaultPolicy_CollectionOfComplexType_SchemaIsPascalCase()
    {
        // A complex type reached through a collection property (List<Tag>) must be renamed too.
        using JsonDocument doc = await BuildAndFetchAsync(configurePolicy: null);
        JsonElement tag = SchemaWithProperty(doc, "Label");

        Assert.True(HasExactProperty(tag, "Label"));
        Assert.False(HasExactProperty(tag, "label"));
    }

    [Fact]
    public async Task CamelCaseOptIn_CollectionOfComplexType_SchemaIsCamelCase()
    {
        using JsonDocument doc = await BuildAndFetchAsync(
            configurePolicy: o => o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.CamelCase));
        JsonElement tag = SchemaWithProperty(doc, "label");

        Assert.True(HasExactProperty(tag, "label"));
        Assert.False(HasExactProperty(tag, "Label"));
    }

    [Fact]
    public async Task DefaultPolicy_InheritedBaseType_SchemaIsPascalCase()
    {
        // A base class reachable only via inheritance (OrderBase) must be renamed too — the base
        // component keeps host casing when the casing map never walks base types (#260).
        using JsonDocument doc = await BuildAndFetchAsync(configurePolicy: null);
        JsonElement orderBase = SchemaWithProperty(doc, "OrderNumber");

        Assert.True(HasExactProperty(orderBase, "OrderNumber"));
        Assert.False(HasExactProperty(orderBase, "orderNumber"));
    }

    [Fact]
    public async Task CamelCaseOptIn_InheritedBaseType_SchemaIsCamelCase()
    {
        using JsonDocument doc = await BuildAndFetchAsync(
            configurePolicy: o => o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.CamelCase));
        JsonElement orderBase = SchemaWithProperty(doc, "orderNumber");

        Assert.True(HasExactProperty(orderBase, "orderNumber"));
        Assert.False(HasExactProperty(orderBase, "OrderNumber"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<JsonDocument> BuildAndFetchAsync(System.Action<OhDataBuilder>? configurePolicy)
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o =>
        {
            configurePolicy?.Invoke(o);
            o.AddEntitySetProfile<CasingProfile>();
        });
        HttpResponseMessage response = await fx.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
        if (SchemaDeclares(resolvedSchema, propertyName)) return true;
        // A generator may model an inherited type as `allOf: [{$ref base}, {properties: own props}]`,
        // so a derived type's own keys can live on an allOf member rather than top-level `properties`.
        if (resolvedSchema.TryGetProperty("allOf", out JsonElement allOf))
        {
            foreach (JsonElement member in allOf.EnumerateArray())
            {
                if (SchemaDeclares(member, propertyName)) return true;
            }
        }
        return false;
    }

    private static bool SchemaDeclares(JsonElement schema, string propertyName)
    {
        return schema.TryGetProperty("properties", out JsonElement props) &&
            props.EnumerateObject().Any(p => string.Equals(p.Name, propertyName, System.StringComparison.Ordinal));
    }

    // Finds the single component schema that declares a property named case-insensitively equal to
    // <paramref name="clrPropertyName"/> — the distinctive property of the nested/base type under
    // test. Case-insensitive so it matches under either the PascalCase or camelCase policy; the
    // exact-casing assertions then run on the schema it returns.
    private static JsonElement SchemaWithProperty(JsonDocument doc, string clrPropertyName)
    {
        JsonElement schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (JsonProperty component in schemas.EnumerateObject())
        {
            if (component.Value.TryGetProperty("properties", out JsonElement props) &&
                props.EnumerateObject().Any(p =>
                    string.Equals(p.Name, clrPropertyName, System.StringComparison.OrdinalIgnoreCase)))
            {
                return component.Value;
            }
        }
        throw new Xunit.Sdk.XunitException($"No component schema declares a '{clrPropertyName}' property.");
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

    private class OrderBase
    {
        public string OrderNumber { get; set; } = "";
    }

    private class Address
    {
        public string Street { get; set; } = "";
        public string PostalCode { get; set; } = "";
    }

    private class Tag
    {
        public string Label { get; set; } = "";
    }

    private class Widget : OrderBase
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";

        [JsonPropertyName("sku_code")]
        public string SkuCode { get; set; } = "";

        public Address HomeAddress { get; set; } = new();
        public List<Tag> Tags { get; set; } = new();
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
