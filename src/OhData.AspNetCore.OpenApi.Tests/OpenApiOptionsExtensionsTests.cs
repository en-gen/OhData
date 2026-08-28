using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using OhData;
using Xunit;

namespace OhData.AspNetCore.OpenApi.Tests;

/// <summary>
/// Verifies the one-line <see cref="OpenApiOptionsExtensions.AddOhData"/> convenience registration
/// wires the same transformers as the explicit per-class form: the operation transformer (OData
/// query parameters) and the schema transformer (ignored-property omission) always, and the opt-in
/// auth-requirements / security transformers when their parameters are supplied.
/// </summary>
public sealed class OpenApiOptionsExtensionsTests
{
    // ── 1. AddOhData() wires both core transformers ────────────────────────────

    [Fact]
    public async Task AddOhData_WiresOperationAndSchemaTransformers()
    {
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<IgnoreProfile>(), o => o.AddOhData());
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        // Operation transformer: OData query params present on the collection GET.
        JsonElement op = GetOperation(doc, "/odata/IgnoreWidgets", "get");
        HashSet<string> names = ParamNames(op);
        Assert.Contains("$top", names);
        Assert.Contains("$filter", names);

        // Schema transformer: the Ignore(...)d property is omitted from the response item schema.
        HashSet<string> itemProps = CollectionItemPropertyNames(doc, op);
        Assert.DoesNotContain("Secret", itemProps);
        Assert.Contains("Name", itemProps);
    }

    // ── 2. Byte-identical to the explicit manual registration ──────────────────

    [Fact]
    public async Task AddOhData_ProducesSameDocumentAsManualRegistration()
    {
        await using var manual = await BuildAsync(o => o.AddEntitySetProfile<IgnoreProfile>(), o =>
        {
            o.AddOperationTransformer<OhDataOpenApiOperationTransformer>();
            o.AddSchemaTransformer<OhDataOpenApiSchemaTransformer>();
        });
        await using var oneLine = await BuildAsync(o => o.AddEntitySetProfile<IgnoreProfile>(), o => o.AddOhData());

        string manualJson = await FetchRawAsync(manual.Client);
        string oneLineJson = await FetchRawAsync(oneLine.Client);
        Assert.Equal(manualJson, oneLineJson);
    }

    // ── 3. authRequirements opt-in wires the requirements transformer ──────────

    [Fact]
    public async Task AddOhData_WithAuthRequirements_AppendsRequirementsSection()
    {
        await using var fx = await BuildAsync(
            o => o.AddEntitySetProfile<SecuredProfile>(),
            o => o.AddOhData(authRequirements: AuthRequirementDisclosure.Kinds),
            withAuth: true);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/SecuredWidgets", "get");
        Assert.Contains("**Authorization:**", description);
        Assert.Contains("role `readers`", description);
    }

    // ── 4. securitySchemeId opt-in wires the security transformer ───────────────

    [Fact]
    public async Task AddOhData_WithSecuritySchemeId_EmitsSecurityRequirementAnd401403()
    {
        await using var fx = await BuildAsync(
            o => o.AddEntitySetProfile<SecuredProfile>(),
            o =>
            {
                o.AddOhData(securitySchemeId: "Bearer");
                // The app owns the scheme definition — OhData only references it by id. Define a
                // "Bearer" scheme so the emitted operation-level $ref resolves in the document.
                o.AddDocumentTransformer((document, context, ct) =>
                {
                    document.Components ??= new OpenApiComponents();
                    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                    document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                    };
                    return Task.CompletedTask;
                });
            },
            withAuth: true);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "get");
        Assert.Contains("Bearer", SecuritySchemeIds(op));
        Assert.True(op.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(op.GetProperty("responses").TryGetProperty("403", out _));
    }

    // ── 5. Neither opt-in given → no auth reflection emitted ───────────────────

    [Fact]
    public async Task AddOhData_WithoutOptIns_EmitsNoSecurityOrRequirements()
    {
        await using var fx = await BuildAsync(
            o => o.AddEntitySetProfile<SecuredProfile>(),
            o => o.AddOhData(),
            withAuth: true);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "get");
        Assert.False(op.TryGetProperty("security", out JsonElement sec) && sec.GetArrayLength() > 0);
        Assert.DoesNotContain("**Authorization:**", OperationDescription(doc, "/odata/SecuredWidgets", "get"));
    }

    // ── 6. Null options → ArgumentNullException ────────────────────────────────

    [Fact]
    public void AddOhData_NullOptions_Throws()
    {
        OpenApiOptions options = null!;
        Assert.Throws<ArgumentNullException>(() => options.AddOhData());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        Action<OpenApiOptions> configureOpenApi,
        bool withAuth = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();

        if (withAuth)
        {
            builder.Services
                .AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, NoOpAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization();
        }

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            configure(o);
        });

        builder.Services.AddOpenApi(configureOpenApi);

        var app = builder.Build();
        app.MapOhData();
        app.MapOpenApi();
        await app.StartAsync();
        return new TestFixture(app);
    }

    private static async Task<JsonDocument> FetchDocumentAsync(HttpClient client) =>
        JsonDocument.Parse(await FetchRawAsync(client));

    private static async Task<string> FetchRawAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
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

    private static string OperationDescription(JsonDocument doc, string exactPath, string method)
    {
        JsonElement op = GetOperation(doc, exactPath, method);
        return op.TryGetProperty("description", out JsonElement d) ? d.GetString() ?? "" : "";
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

    private static HashSet<string> SecuritySchemeIds(JsonElement operation)
    {
        var ids = new HashSet<string>();
        if (operation.TryGetProperty("security", out JsonElement security))
        {
            foreach (JsonElement requirement in security.EnumerateArray())
            {
                foreach (JsonProperty scheme in requirement.EnumerateObject())
                {
                    ids.Add(scheme.Name);
                }
            }
        }
        return ids;
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

    private sealed class NoOpAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public NoOpAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

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

    private class SecuredProfile : EntitySetProfile<int, Widget>
    {
        public SecuredProfile() : base(x => x.Id)
        {
            EntitySetName = "SecuredWidgets";
            ConfigureAuthorization(a => a.Read(r => r.RequireRole("readers")));
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(w => w.Id == id));
        }
    }
}
