using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Swashbuckle.Tests;

/// <summary>
/// Leg 2/3/4 (docs-fidelity): verifies write routes get a real request-body schema
/// (<see cref="OhDataApiDescriptionProvider"/>), collection GET gets a typed envelope response
/// instead of a bare 200, and read-path summaries are present -- against a real
/// Swashbuckle-generated OpenAPI document. Mirrors the equivalent test in
/// OhData.AspNetCore.OpenApi.Tests / OhData.AspNetCore.NSwag.Tests.
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
    public async Task BoundAction_RequestBodySchema_ExposesNamedParameters_NotEmptyObject()
    {
        // Issue #184, item 3: the action body schema is synthesized from the action's parameter
        // list (Rename(string newName)), so it exposes a real "newName" property rather than the
        // typeless {} that BodyType = typeof(object) previously produced.
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceParents/Rename", "post");

        JsonElement schema = RequestSchema(op.GetProperty("requestBody"));
        Assert.True(SchemaHasProperty(doc, schema, "newName"),
            "action body schema must expose the 'newName' parameter, not an empty object");
    }

    [Fact]
    public async Task BoundFunction_HasQueryParameters_WithRequiredFlags()
    {
        // Issue #181: a function's query-string parameters must appear in the generated document
        // (previously "parameters: []"), with the required flag reflecting the C# default value.
        using JsonDocument doc = await BuildAndFetchAsync();
        JsonElement op = GetOperation(doc, "/odata/WriteSurfaceParents/FindParents", "get");

        JsonElement term = GetQueryParameter(op, "term");
        Assert.True(IsRequired(term)); // no C# default -> required

        JsonElement count = GetQueryParameter(op, "count");
        Assert.False(IsRequired(count)); // count = 10 -> optional
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

    private static async Task<JsonDocument> BuildAndFetchAsync()
    {
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o =>
        {
            o.AddEntitySetProfile<WriteSurfaceProfile>();
            o.AddEntitySetProfile<WriteSurfaceQueryableProfile>();
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

    /// <summary>Issue #181: finds a query parameter by name in an operation's parameters array.</summary>
    private static JsonElement GetQueryParameter(JsonElement op, string name)
    {
        JsonElement parameters = op.GetProperty("parameters");
        JsonElement match = parameters.EnumerateArray()
            .Where(p => p.GetProperty("name").GetString() == name &&
                        p.GetProperty("in").GetString() == "query")
            .FirstOrDefault();
        // FirstOrDefault() on a JsonElement sequence yields default(JsonElement) (ValueKind
        // Undefined) when nothing matched.
        if (match.ValueKind == JsonValueKind.Undefined)
        {
            throw new Xunit.Sdk.XunitException($"No query parameter '{name}'. Parameters: {parameters.GetRawText()}");
        }
        return match;
    }

    /// <summary>OpenAPI omits "required" (or sets it false) for optional parameters.</summary>
    private static bool IsRequired(JsonElement parameter) =>
        parameter.TryGetProperty("required", out JsonElement r) && r.GetBoolean();

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
            // #221: property routes are omitted from docs by default; this fixture asserts the
            // documented property-write body schema, so opt them into the generated document.
            PropertyRouteDocsEnabled = true;

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
            BindFunction(FindParents);
        }

        private Task Rename(string newName) => Task.CompletedTask;

        // Issue #181: collection-bound function with a required parameter (term, no default) and
        // an optional one (count = 10) so both required/optional documentation can be asserted.
        private Task<IEnumerable<Parent>> FindParents(string term, int count = 10) =>
            Task.FromResult<IEnumerable<Parent>>(_parents);
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
