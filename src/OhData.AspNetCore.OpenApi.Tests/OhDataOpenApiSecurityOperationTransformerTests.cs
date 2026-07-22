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
using OhData;
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
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<SecuredProfile>());
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
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<SecuredProfile>());
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
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<AnonProfile>());
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
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<PerOpProfile>());
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
            o => o.AddEntitySetProfile<SecuredProfile>(),
            registerSecurityTransformer: false);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "get");
        Assert.False(op.TryGetProperty("security", out JsonElement sec) && sec.GetArrayLength() > 0,
            "security must be opt-in — nothing emitted when the transformer is not registered");
    }

    // ── 6. OhData never injects a security scheme definition (boundary) ────────
    //
    // The #219 boundary: OhData reflects the *requirement* but never defines the *scheme*. With no
    // app-provided scheme, components.securitySchemes must stay empty even though the transformer
    // still emits the operation-level requirement (a $ref the app is responsible for satisfying).

    [Fact]
    public async Task Transformer_NeverDefinesSecurityScheme()
    {
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<SecuredProfile>(), defineScheme: false);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        bool hasSchemes = doc.RootElement.TryGetProperty("components", out JsonElement components)
            && components.TryGetProperty("securitySchemes", out JsonElement schemes)
            && schemes.EnumerateObject().Any();
        Assert.False(hasSchemes, "OhData must not define any securityScheme — that stays the app's job");
    }

    // ── 7. An app-defined 401 response is not clobbered ────────────────────────

    [Fact]
    public async Task ExistingResponse_NotClobbered()
    {
        const string existing = "app-defined 401";
        await using var fx = await BuildAsync(
            o => o.AddEntitySetProfile<SecuredProfile>(),
            extraOpenApi: o => o.AddOperationTransformer(new PreSeed401Transformer(existing)));
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "get");
        string? desc = op.GetProperty("responses").GetProperty("401").GetProperty("description").GetString();
        Assert.Equal(existing, desc);
    }

    // ── 8. Registering the transformer twice does not duplicate the requirement ─

    [Fact]
    public async Task DuplicateRegistration_SecurityNotDuplicated()
    {
        await using var fx = await BuildAsync(
            o => o.AddEntitySetProfile<SecuredProfile>(),
            extraOpenApi: o => o.AddOperationTransformer(new OhDataOpenApiSecurityOperationTransformer(SchemeId)));
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "get");
        int bearerRefs = op.GetProperty("security").EnumerateArray()
            .Count(req => req.EnumerateObject().Any(s => s.Name == SchemeId));
        Assert.Equal(1, bearerRefs);
    }

    // ── 9. AllowAnonymous wins over an authorize requirement on the same route ──
    //
    // Exercises the `&& !IAllowAnonymous` clause directly: a non-OhData endpoint that carries both
    // RequireAuthorization() and AllowAnonymous() must be treated as unsecured, matching ASP.NET
    // Core's own precedence.

    [Fact]
    public async Task EndpointWithBothAuthorizeAndAllowAnonymous_NotSecured()
    {
        await using var fx = await BuildAsync(
            o => o.AddEntitySetProfile<AnonProfile>(),
            configureApp: app => app.MapGet("/plain/both", () => "hi")
                .RequireAuthorization()
                .AllowAnonymous());
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/plain/both", "get");
        Assert.False(op.TryGetProperty("security", out JsonElement sec) && sec.GetArrayLength() > 0,
            "AllowAnonymous must win over RequireAuthorization");
        Assert.False(op.GetProperty("responses").TryGetProperty("401", out _));
    }

    // ── 10. Pure resource-only route: 403 documented, but no scheme + no 401 ───
    //
    // A category gated ONLY by RequireResource() (Layer B, instance-level) attaches
    // OhDataOperationAuthMetadata but no IAuthorizeData — yet the runtime still returns 403 from
    // the in-handler resource check. The transformer must document that 403, while emitting no
    // security scheme requirement (there is no scheme for a pure resource check) and no 401 (a
    // resource-only route has no endpoint gate to challenge on, so it never returns 401).

    [Fact]
    public async Task ResourceOnlyOperation_Documents403_ButNoSchemeAndNo401()
    {
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<ResourceOnlyProfile>());
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/ResourceOnlyWidgets({key})", "patch");

        Assert.True(op.GetProperty("responses").TryGetProperty("403", out _),
            "resource-only route must document the 403 its Layer B check returns");
        Assert.False(op.TryGetProperty("security", out JsonElement sec) && sec.GetArrayLength() > 0,
            "pure resource check has no scheme — no security requirement must be emitted");
        Assert.False(op.GetProperty("responses").TryGetProperty("401", out _),
            "resource-only route returns 403 not 401 at runtime, so 401 must not be documented");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        bool registerSecurityTransformer = true,
        bool defineScheme = true,
        Action<OpenApiOptions>? extraOpenApi = null,
        Action<WebApplication>? configureApp = null)
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
            // Runs first so a test can pre-seed operations before the security transformer sees them.
            extraOpenApi?.Invoke(o);

            o.AddOperationTransformer<OhDataOpenApiOperationTransformer>();

            // The app owns the scheme definition — OhData only references it. Simulate the app's
            // identity setup by defining a "Bearer" scheme in components (a real app would do this
            // in its own document transformer), so the operation-level $ref resolves.
            if (defineScheme)
            {
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
            }

            if (registerSecurityTransformer)
            {
                o.AddOperationTransformer(new OhDataOpenApiSecurityOperationTransformer(SchemeId));
            }
        });

        var app = builder.Build();
        app.MapOhData();
        app.MapOpenApi();
        configureApp?.Invoke(app);
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

    /// <summary>
    /// Pre-seeds every operation with a distinctively-described <c>401</c> before the security
    /// transformer runs, to prove the transformer's <c>ContainsKey("401")</c> guard leaves an
    /// app-defined response untouched.
    /// </summary>
    private sealed class PreSeed401Transformer : IOpenApiOperationTransformer
    {
        private readonly string _description;

        public PreSeed401Transformer(string description) => _description = description;

        public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, System.Threading.CancellationToken cancellationToken)
        {
            operation.Responses ??= new OpenApiResponses();
            operation.Responses["401"] = new OpenApiResponse { Description = _description };
            return Task.CompletedTask;
        }
    }

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

    // Update is gated purely by RequireResource() (no coarse gate) — attaches
    // OhDataOperationAuthMetadata with a Resource requirement but no IAuthorizeData.
    private class ResourceOnlyProfile : EntitySetProfile<int, Widget>
    {
        public ResourceOnlyProfile() : base(x => x.Id)
        {
            EntitySetName = "ResourceOnlyWidgets";
            ConfigureAuthorization(a => a.Update(u => u.RequireResource()));
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(w => w.Id == id));
            Patch = (id, delta, ct) =>
            {
                Widget? existing = Store.FirstOrDefault(w => w.Id == id);
                if (existing is null) return Task.FromResult<Widget?>(null);
                delta.Patch(existing);
                return Task.FromResult<Widget?>(existing);
            };
        }
    }
}
