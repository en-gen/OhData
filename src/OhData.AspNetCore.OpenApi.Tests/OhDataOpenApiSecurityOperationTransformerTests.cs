using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Encodings.Web;
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
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.OpenApi.Tests;

/// <summary>
/// Verifies <see cref="OhDataOpenApiSecurityOperationTransformer"/> (#219) against a real generated
/// OpenAPI document: secured operations must carry an operation-level <c>security</c> requirement
/// that references the app-defined scheme by id (OhData never defines the scheme) plus documented
/// <c>401</c>/<c>403</c> responses, while explicitly anonymous operations must carry none of that.
/// </summary>
public sealed class OhDataOpenApiSecurityOperationTransformerTests
{
    private const string SchemeId = "Bearer";

    // ── 1. A secured collection GET gets security + 401/403 ────────────────────

    [Fact]
    public async Task SecuredOperation_GetsSecurityReferencingAppScheme_And401And403()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<SecuredProfile>());
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "get");

        Assert.Contains(SchemeId, SecuritySchemeIds(op));
        Assert.True(op.GetProperty("responses").TryGetProperty("401", out _), "401 response missing");
        Assert.True(op.GetProperty("responses").TryGetProperty("403", out _), "403 response missing");
    }

    // ── 2. The secured write (POST) is also documented ─────────────────────────

    [Fact]
    public async Task SecuredWriteOperation_AlsoGetsSecurityAnd401And403()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<SecuredProfile>());
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "post");

        Assert.Contains(SchemeId, SecuritySchemeIds(op));
        Assert.True(op.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(op.GetProperty("responses").TryGetProperty("403", out _));
    }

    // ── 3. An unsecured (no-auth) profile gets nothing ─────────────────────────

    [Fact]
    public async Task AnonymousProfile_GetsNoSecurityAndNo401Or403()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<AnonProfile>());
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/AnonWidgets", "get");

        Assert.False(op.TryGetProperty("security", out JsonElement sec) && sec.GetArrayLength() > 0,
            "anonymous operation should carry no security requirement");
        Assert.False(op.GetProperty("responses").TryGetProperty("401", out _),
            "anonymous operation should not document 401");
        Assert.False(op.GetProperty("responses").TryGetProperty("403", out _),
            "anonymous operation should not document 403");
    }

    // ── 4. Per-operation auth: secured read, explicitly-anonymous create ───────
    //
    // Proves the transformer keys off standard endpoint auth metadata, not merely the presence of
    // an OhData profile: a route the profile marked AllowAnonymous must not be treated as secured.

    [Fact]
    public async Task PerOperationAuth_ReadSecured_CreateAnonymous()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<PerOpProfile>());
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement get = GetOperation(doc, "/odata/PerOpWidgets", "get");
        Assert.Contains(SchemeId, SecuritySchemeIds(get));
        Assert.True(get.GetProperty("responses").TryGetProperty("403", out _));

        JsonElement post = GetOperation(doc, "/odata/PerOpWidgets", "post");
        Assert.False(post.TryGetProperty("security", out JsonElement sec) && sec.GetArrayLength() > 0,
            "AllowAnonymous create should carry no security requirement");
        Assert.False(post.GetProperty("responses").TryGetProperty("401", out _));
    }

    // ── 5. Not registered → off by default ─────────────────────────────────────

    [Fact]
    public async Task TransformerNotRegistered_NoSecurityEmittedEvenOnSecuredRoutes()
    {
        await using var fx = await BuildAsync(
            o => o.AddProfile<SecuredProfile>(),
            registerSecurityTransformer: false);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "get");
        Assert.False(op.TryGetProperty("security", out JsonElement sec) && sec.GetArrayLength() > 0,
            "security must be opt-in — nothing emitted when the transformer is not registered");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        bool registerSecurityTransformer = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();

        // The app owns identity: OhData only references the scheme, it never defines it.
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, NoOpAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            configure(o);
        });

        builder.Services.AddOpenApi(o =>
        {
            o.AddOperationTransformer<OhDataOpenApiOperationTransformer>();

            // The app owns the scheme definition — OhData only references it. Simulate the app's
            // identity setup by defining a "Bearer" scheme in components (a real app would do this
            // in its own document transformer), so the operation-level $ref resolves.
            o.AddDocumentTransformer((document, context, ct) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[SchemeId] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                };
                return Task.CompletedTask;
            });

            if (registerSecurityTransformer)
            {
                o.AddOperationTransformer(new OhDataOpenApiSecurityOperationTransformer(SchemeId));
            }
        });

        var app = builder.Build();
        app.MapOhData();
        app.MapOpenApi();
        await app.StartAsync();
        return new TestFixture(app);
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

    /// <summary>Scheme ids referenced across every security requirement on the operation.</summary>
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

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private sealed class NoOpAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public NoOpAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    private class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private static readonly List<Widget> Store = new() { new() { Id = 1, Name = "Alpha" } };

    private class SecuredProfile : EntitySetProfile<int, Widget>
    {
        public SecuredProfile() : base(x => x.Id)
        {
            EntitySetName = "SecuredWidgets";
            RequireAuthorization();
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(w => w.Id == id));
            Post = (model, ct) => Task.FromResult<Widget?>(model);
        }
    }

    private class AnonProfile : EntitySetProfile<int, Widget>
    {
        public AnonProfile() : base(x => x.Id)
        {
            EntitySetName = "AnonWidgets";
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }

    private class PerOpProfile : EntitySetProfile<int, Widget>
    {
        public PerOpProfile() : base(x => x.Id)
        {
            EntitySetName = "PerOpWidgets";
            ConfigureAuthorization(a => a
                .Read(r => r.RequireRole("reader"))
                .Create(c => c.AllowAnonymous()));
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            Post = (model, ct) => Task.FromResult<Widget?>(model);
        }
    }
}
