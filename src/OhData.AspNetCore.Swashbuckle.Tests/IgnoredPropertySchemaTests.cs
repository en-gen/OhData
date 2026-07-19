using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Swashbuckle.Tests;

/// <summary>
/// Issue #228: properties excluded via <c>EntitySetProfile.Ignore(...)</c> (#226) never cross the
/// wire, so <see cref="OhDataSwaggerSchemaFilter"/> must omit them from generated schemas —
/// otherwise the document advertises members no response contains and no request binds.
/// </summary>
public sealed class IgnoredPropertySchemaTests
{
    [Fact]
    public async Task RequestBodySchema_OmitsIgnoredProperties()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/IgnoredSchemaProducts", "post");

        JsonElement schema = RequestSchema(op.GetProperty("requestBody"));
        Assert.False(SchemaHasProperty(doc, schema, "costBasis"));
        Assert.False(SchemaHasProperty(doc, schema, "internalNotes"));
    }

    [Fact]
    public async Task RequestBodySchema_KeepsRemainingProperties()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/IgnoredSchemaProducts", "post");

        JsonElement schema = RequestSchema(op.GetProperty("requestBody"));
        Assert.True(SchemaHasProperty(doc, schema, "id"));
        Assert.True(SchemaHasProperty(doc, schema, "name"));
    }

    [Fact]
    public async Task CollectionResponseItemSchema_OmitsIgnoredProperties()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/IgnoredSchemaProducts", "get");

        JsonElement item = CollectionItemSchema(doc, op);
        Assert.False(HasProperty(item, "costBasis"));
        Assert.True(HasProperty(item, "name"));
    }

    [Fact]
    public async Task ControlType_SameNamedProperty_IsKept()
    {
        // AuditEntry has its own CostBasis property that no profile ignores — suppression is
        // keyed by CLR type, so a same-named property on another type must survive.
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/IgnoredSchemaAudits", "post");

        JsonElement schema = RequestSchema(op.GetProperty("requestBody"));
        Assert.True(SchemaHasProperty(doc, schema, "costBasis"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<JsonDocument> BuildAndFetchAsync()
    {
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o =>
        {
            o.AddProfile<ProductProfile>();
            o.AddProfile<AuditEntryProfile>();
        });
        return await fx.GetDocumentAsync();
    }

    private static JsonElement GetOperation(JsonDocument doc, string exactPath, string method)
    {
        JsonElement paths = doc.RootElement.GetProperty("paths");
        if (!paths.TryGetProperty(exactPath, out JsonElement pathItem))
        {
            string available = string.Join(", ", paths.EnumerateObject().Select(p => p.Name));
            throw new Xunit.Sdk.XunitException($"No path '{exactPath}' in document. Available: {available}");
        }
        return pathItem.GetProperty(method);
    }

    private static JsonElement RequestSchema(JsonElement requestBody) =>
        requestBody.GetProperty("content").GetProperty("application/json").GetProperty("schema");

    /// <summary>Resolves the entity item schema out of the collection envelope (value → items).</summary>
    private static JsonElement CollectionItemSchema(JsonDocument doc, JsonElement op)
    {
        JsonElement schema = op.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        JsonElement envelope = Resolve(doc, schema);
        JsonElement items = envelope.GetProperty("properties").GetProperty("value").GetProperty("items");
        return Resolve(doc, items);
    }

    private static bool SchemaHasProperty(JsonDocument doc, JsonElement schema, string propertyName) =>
        HasProperty(Resolve(doc, schema), propertyName);

    /// <summary>Case-insensitive, since OpenAPI schema property names follow the response's JSON casing.</summary>
    private static bool HasProperty(JsonElement resolvedSchema, string propertyName)
    {
        if (!resolvedSchema.TryGetProperty("properties", out JsonElement props)) return false;
        return props.EnumerateObject().Any(p => string.Equals(p.Name, propertyName, System.StringComparison.OrdinalIgnoreCase));
    }

    private static JsonElement Resolve(JsonDocument doc, JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out JsonElement refProp))
        {
            string pointer = refProp.GetString()!; // "#/components/schemas/Foo"
            string name = pointer.Split('/').Last();
            return doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name);
        }
        return schema;
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public decimal CostBasis { get; set; }
        public string InternalNotes { get; set; } = "";
    }

    /// <summary>Control type: carries its own (un-ignored) CostBasis property.</summary>
    private class AuditEntry
    {
        public int Id { get; set; }
        public decimal CostBasis { get; set; }
    }

    private class ProductProfile : EntitySetProfile<int, Product>
    {
        private readonly List<Product> _store = new() { new() { Id = 1, Name = "Widget", CostBasis = 4.2m } };

        public ProductProfile() : base(x => x.Id)
        {
            EntitySetName = "IgnoredSchemaProducts";
            Ignore(x => x.CostBasis, x => x.InternalNotes);
            GetAll = (ct) => Task.FromResult<IEnumerable<Product>>(_store);
            Post = (p, ct) => { _store.Add(p); return Task.FromResult<Product?>(p); };
        }
    }

    private class AuditEntryProfile : EntitySetProfile<int, AuditEntry>
    {
        private readonly List<AuditEntry> _store = new() { new() { Id = 1, CostBasis = 4.2m } };

        public AuditEntryProfile() : base(x => x.Id)
        {
            EntitySetName = "IgnoredSchemaAudits";
            GetAll = (ct) => Task.FromResult<IEnumerable<AuditEntry>>(_store);
            Post = (a, ct) => { _store.Add(a); return Task.FromResult<AuditEntry?>(a); };
        }
    }
}
