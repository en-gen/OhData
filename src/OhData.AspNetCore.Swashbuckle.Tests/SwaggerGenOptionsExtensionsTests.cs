using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OhData;
using Swashbuckle.AspNetCore.SwaggerGen;
using Xunit;

namespace OhData.AspNetCore.Swashbuckle.Tests;

/// <summary>
/// Verifies the one-line <see cref="SwaggerGenOptionsExtensions.AddOhData"/> convenience registration
/// wires the same filters as the explicit per-class form: the operation filter (OData query
/// parameters) and the schema filter (ignored-property omission). This package has no auth/security
/// filters, so there are no opt-in parameters to exercise.
/// </summary>
public sealed class SwaggerGenOptionsExtensionsTests
{
    // ── 1. AddOhData() wires both filters ──────────────────────────────────────

    [Fact]
    public async Task AddOhData_WiresOperationAndSchemaFilters()
    {
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<IgnoreProfile>(), c => c.AddOhData());
        using JsonDocument doc = await fx.GetDocumentAsync();

        // Operation filter: OData query params present on the collection GET.
        JsonElement op = GetOperation(doc, "/odata/IgnoreWidgets", "get");
        HashSet<string> names = ParamNames(op);
        Assert.Contains("$top", names);
        Assert.Contains("$filter", names);

        // Schema filter: the Ignore(...)d property is omitted from the response item schema.
        HashSet<string> itemProps = CollectionItemPropertyNames(doc, op);
        Assert.DoesNotContain("Secret", itemProps);
        Assert.Contains("Name", itemProps);
    }

    // ── 2. Byte-identical to the explicit manual registration ──────────────────

    [Fact]
    public async Task AddOhData_ProducesSameDocumentAsManualRegistration()
    {
        await using var manual = await BuildAsync(o => o.AddEntitySetProfile<IgnoreProfile>(), c =>
        {
            c.OperationFilter<OhDataSwaggerOperationFilter>();
            c.SchemaFilter<OhDataSwaggerSchemaFilter>();
        });
        await using var oneLine = await BuildAsync(o => o.AddEntitySetProfile<IgnoreProfile>(), c => c.AddOhData());

        Assert.Equal(await FetchRawAsync(manual.Client), await FetchRawAsync(oneLine.Client));
    }

    // ── 3. Null options → ArgumentNullException ────────────────────────────────

    [Fact]
    public void AddOhData_NullOptions_Throws()
    {
        SwaggerGenOptions options = null!;
        Assert.Throws<ArgumentNullException>(() => options.AddOhData());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        Action<SwaggerGenOptions> configureSwagger)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(configureSwagger);

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            configure(o);
        });

        var app = builder.Build();
        app.MapOhData();
        app.UseSwagger();
        await app.StartAsync();
        return new TestFixture(app);
    }

    private static async Task<string> FetchRawAsync(HttpClient client)
    {
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
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

    private static HashSet<string> ParamNames(JsonElement operation)
    {
        var set = new HashSet<string>();
        if (operation.TryGetProperty("parameters", out JsonElement arr))
        {
            foreach (JsonElement p in arr.EnumerateArray()
                .Where(p => p.TryGetProperty("in", out JsonElement inProp) && inProp.GetString() == "query"))
            {
                set.Add(p.GetProperty("name").GetString()!);
            }
        }
        return set;
    }

    /// <summary>Property names of the collection GET's response item schema (value → items), $refs resolved.</summary>
    private static HashSet<string> CollectionItemPropertyNames(JsonDocument doc, JsonElement op)
    {
        JsonElement schema = op.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        JsonElement envelope = Resolve(doc, schema);
        JsonElement item = Resolve(doc, envelope.GetProperty("properties").GetProperty("value").GetProperty("items"));

        var names = new HashSet<string>();
        if (item.TryGetProperty("properties", out JsonElement props))
        {
            foreach (JsonProperty p in props.EnumerateObject()) names.Add(p.Name);
        }
        return names;
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

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Secret { get; set; } = "";
    }

    private static readonly List<Widget> Store = new() { new() { Id = 1, Name = "Alpha", Secret = "x" } };

    private class IgnoreProfile : EntitySetProfile<int, Widget>
    {
        public IgnoreProfile() : base(x => x.Id)
        {
            EntitySetName = "IgnoreWidgets";
            FilterEnabled = true;
            Ignore(w => w.Secret);
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }
}
