using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.OpenApi.Tests;

/// <summary>
/// Leg 2/3/4 (docs-fidelity): verifies write routes get a real request-body schema
/// (<see cref="OhDataApiDescriptionProvider"/>), collection GET gets a typed envelope response
/// instead of a bare 200, and read-path summaries are present -- against a real generated
/// OpenAPI document, the same way <see cref="OhDataOpenApiOperationTransformerTests"/> does for
/// query-option parameters.
/// </summary>
public sealed class OhDataRequestBodyAndResponseTests
{
    [Fact]
    public async Task EntityPost_HasRequestBodyWithSchemaAndDescription()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceParents", "post");

        JsonElement requestBody = op.GetProperty("requestBody");
        JsonElement schema = RequestSchema(requestBody);
        Assert.True(SchemaHasProperty(doc, schema, "id"));
        Assert.True(SchemaHasProperty(doc, schema, "name"));
        Assert.False(string.IsNullOrWhiteSpace(requestBody.GetProperty("description").GetString()));
    }

    [Fact]
    public async Task EntityPut_HasRequestBody()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceParents({key})", "put");

        Assert.True(op.TryGetProperty("requestBody", out JsonElement requestBody));
        JsonElement schema = RequestSchema(requestBody);
        Assert.True(SchemaHasProperty(doc, schema, "name"));
    }

    [Fact]
    public async Task EntityPatch_HasRequestBody_DescriptionMentionsPartialUpdate()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceParents({key})", "patch");

        JsonElement requestBody = op.GetProperty("requestBody");
        string description = requestBody.GetProperty("description").GetString() ?? "";
        Assert.Contains("partial", description, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NavPost_HasRequestBodyWithChildSchema()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceParents({key})/Children", "post");

        JsonElement requestBody = op.GetProperty("requestBody");
        JsonElement schema = RequestSchema(requestBody);
        Assert.True(SchemaHasProperty(doc, schema, "name"));
        Assert.True(SchemaHasProperty(doc, schema, "parentId"));
    }

    [Fact]
    public async Task PropertyPut_HasRequestBodyWithValueWrapper()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceParents({key})/Name", "put");

        JsonElement requestBody = op.GetProperty("requestBody");
        JsonElement schema = RequestSchema(requestBody);
        Assert.True(SchemaHasProperty(doc, schema, "value"));
    }

    [Fact]
    public async Task RefPost_HasRequestBodyWithODataIdWrapper()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceParents({key})/Children/$ref", "post");

        JsonElement requestBody = op.GetProperty("requestBody");
        JsonElement schema = RequestSchema(requestBody);
        Assert.True(SchemaHasProperty(doc, schema, "@odata.id"));
    }

    [Fact]
    public async Task BoundAction_HasRequestBody_DescriptionListsParameters()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceParents/Rename", "post");

        JsonElement requestBody = op.GetProperty("requestBody");
        string description = requestBody.GetProperty("description").GetString() ?? "";
        Assert.Contains("newName", description);
    }

    [Fact]
    public async Task CollectionGet_HasTypedEnvelopeResponse_NotBareTwoHundred()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceParents", "get");

        JsonElement ok = op.GetProperty("responses").GetProperty("200");
        JsonElement schema = ok.GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.True(SchemaHasProperty(doc, schema, "value"));
    }

    [Fact]
    public async Task CollectionGet_HasReadPathSummary()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceParents", "get");

        Assert.True(op.TryGetProperty("summary", out JsonElement summary));
        Assert.Contains("WriteSurfaceParents", summary.GetString());
        Assert.True(op.TryGetProperty("description", out JsonElement description));
        Assert.False(string.IsNullOrWhiteSpace(description.GetString()));
    }

    [Fact]
    public async Task GetQueryableCollection_HasQueryableSummary()
    {
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceQueryableWidgets", "get");

        string summary = op.GetProperty("summary").GetString() ?? "";
        Assert.Contains("queryable", summary, System.StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<TestFixture> BuildAsync() => await TestHostBuilder.BuildAsync(o =>
    {
        o.AddProfile<WriteSurfaceProfile>();
        o.AddProfile<WriteSurfaceQueryableProfile>();
    });

    private static async Task<JsonDocument> BuildAndFetchAsync()
    {
        await using TestFixture fx = await BuildAsync();
        return await FetchDocumentAsync(fx.Client);
    }

    private static async Task<JsonDocument> FetchDocumentAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
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

    /// <summary>
    /// Resolves <paramref name="schema"/> — following one "$ref" hop into components.schemas if
    /// present — and checks whether it declares a property named <paramref name="propertyName"/>
    /// (case-insensitive, since OpenAPI schema property names follow the response's JSON casing).
    /// </summary>
    private static bool SchemaHasProperty(JsonDocument doc, JsonElement schema, string propertyName)
    {
        JsonElement resolved = Resolve(doc, schema);
        if (!resolved.TryGetProperty("properties", out JsonElement props)) return false;
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

    private class Parent
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Child>? Children { get; set; }
    }

    private class Child
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
        public string Name { get; set; } = "";
    }

    private class WriteSurfaceProfile : EntitySetProfile<int, Parent>
    {
        private readonly List<Parent> _parents = new() { new() { Id = 1, Name = "P1" } };
        private readonly List<Child> _children = new();

        public WriteSurfaceProfile() : base(x => x.Id)
        {
            EntitySetName = "WriteSurfaceParents";

            GetAll = (ct) => Task.FromResult<IEnumerable<Parent>>(_parents);
            GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

            Post = (p, ct) => { _parents.Add(p); return Task.FromResult<Parent?>(p); };
            Put = (id, p, ct) => { p.Id = id; return Task.FromResult(p); };
            Patch = (id, delta, ct) =>
            {
                Parent? existing = _parents.FirstOrDefault(x => x.Id == id);
                if (existing is null) return Task.FromResult<Parent?>(null);
                delta.Patch(existing);
                return Task.FromResult<Parent?>(existing);
            };

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) => Task.FromResult<IEnumerable<Child>>(_children.Where(c => c.ParentId == parentId)),
                post: (parentId, child, ct) =>
                {
                    child.ParentId = parentId;
                    _children.Add(child);
                    return Task.FromResult<Child?>(child);
                },
                addRef: (parentId, relatedId, ct) => Task.CompletedTask,
                removeRef: (parentId, relatedId, ct) => Task.CompletedTask,
                refTargetEntitySet: "WriteSurfaceChildren");

            BindAction(Rename);
        }

        private Task Rename(string newName) => Task.CompletedTask;
    }

    private class WriteSurfaceQueryableProfile : EntitySetProfile<int, Parent>
    {
        private readonly List<Parent> _parents = new() { new() { Id = 1, Name = "P1" } };

        public WriteSurfaceQueryableProfile() : base(x => x.Id)
        {
            EntitySetName = "WriteSurfaceQueryableWidgets";
            GetQueryable = (ct) => Task.FromResult(_parents.AsQueryable());
        }
    }
}
