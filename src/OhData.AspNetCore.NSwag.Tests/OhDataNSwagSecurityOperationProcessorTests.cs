using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSwag;
using NSwag.Generation.AspNetCore;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.NSwag.Tests;

/// <summary>
/// Verifies <see cref="OhDataNSwagSecurityOperationProcessor"/> (#219) against a real NSwag-generated
/// OpenAPI document: secured operations must carry an operation-level <c>security</c> requirement
/// referencing the app-defined scheme by name plus documented <c>401</c>/<c>403</c> responses, while
/// explicitly anonymous operations must carry none of that.
/// </summary>
public sealed class OhDataNSwagSecurityOperationProcessorTests
{
    private const string SchemeId = "Bearer";

    // ── 1. Secured collection GET → security + 401/403 ─────────────────────────

    [Fact]
    public async Task SecuredOperation_GetsSecurityReferencingAppScheme_And401And403()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<SecuredProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "get");

        Assert.Contains(SchemeId, SecuritySchemeIds(op));
        Assert.True(op.GetProperty("responses").TryGetProperty("401", out _), "401 response missing");
        Assert.True(op.GetProperty("responses").TryGetProperty("403", out _), "403 response missing");
    }

    // ── 2. Secured write (POST) documented too ─────────────────────────────────

    [Fact]
    public async Task SecuredWriteOperation_AlsoGetsSecurityAnd401And403()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<SecuredProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "post");

        Assert.Contains(SchemeId, SecuritySchemeIds(op));
        Assert.True(op.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(op.GetProperty("responses").TryGetProperty("403", out _));
    }

    // ── 3. Unsecured profile → nothing ─────────────────────────────────────────

    [Fact]
    public async Task AnonymousProfile_GetsNoSecurityAndNo401Or403()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<AnonProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        JsonElement op = GetOperation(doc, "/odata/AnonWidgets", "get");

        Assert.False(op.TryGetProperty("security", out JsonElement sec) && sec.GetArrayLength() > 0,
            "anonymous operation should carry no security requirement");
        Assert.False(op.GetProperty("responses").TryGetProperty("401", out _),
            "anonymous operation should not document 401");
        Assert.False(op.GetProperty("responses").TryGetProperty("403", out _),
            "anonymous operation should not document 403");
    }

    // ── 4. Per-operation auth: secured read, explicitly-anonymous create ───────

    [Fact]
    public async Task PerOperationAuth_ReadSecured_CreateAnonymous()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<PerOpProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

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
    public async Task ProcessorNotRegistered_NoSecurityEmittedEvenOnSecuredRoutes()
    {
        await using var fx = await BuildAsync(
            o => o.AddProfile<SecuredProfile>(),
            registerSecurityProcessor: false);
        using JsonDocument doc = await fx.GetDocumentAsync();

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "get");
        Assert.False(op.TryGetProperty("security", out JsonElement sec) && sec.GetArrayLength() > 0,
            "security must be opt-in — nothing emitted when the processor is not registered");
    }

    // ── 6. OhData never injects a security scheme definition (boundary) ────────

    [Fact]
    public async Task Processor_NeverDefinesSecurityScheme()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<SecuredProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        bool hasV3 = doc.RootElement.TryGetProperty("components", out JsonElement components)
            && components.TryGetProperty("securitySchemes", out JsonElement schemes)
            && schemes.EnumerateObject().Any();
        bool hasV2 = doc.RootElement.TryGetProperty("securityDefinitions", out JsonElement defs)
            && defs.EnumerateObject().Any();
        Assert.False(hasV3 || hasV2, "OhData must not define any securityScheme — that stays the app's job");
    }

    // ── 7. An app-defined 401 response is not clobbered ────────────────────────

    [Fact]
    public async Task ExistingResponse_NotClobbered()
    {
        const string existing = "app-defined 401";
        await using var fx = await BuildAsync(
            o => o.AddProfile<SecuredProfile>(),
            configureBeforeSecurity: s => s.OperationProcessors.Add(new PreSeed401Processor(existing)));
        using JsonDocument doc = await fx.GetDocumentAsync();

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "get");
        string? desc = op.GetProperty("responses").GetProperty("401").GetProperty("description").GetString();
        Assert.Equal(existing, desc);
    }

    // ── 8. Registering the processor twice does not duplicate the requirement ──

    [Fact]
    public async Task DuplicateRegistration_SecurityNotDuplicated()
    {
        await using var fx = await BuildAsync(
            o => o.AddProfile<SecuredProfile>(),
            configureBeforeSecurity: s => s.OperationProcessors.Add(new OhDataNSwagSecurityOperationProcessor(SchemeId)));
        using JsonDocument doc = await fx.GetDocumentAsync();

        JsonElement op = GetOperation(doc, "/odata/SecuredWidgets", "get");
        int bearerRefs = op.GetProperty("security").EnumerateArray()
            .Count(req => req.EnumerateObject().Any(scheme => scheme.Name == SchemeId));
        Assert.Equal(1, bearerRefs);
    }

    // ── 9. AllowAnonymous wins over an authorize requirement on the same route ──

    [Fact]
    public async Task EndpointWithBothAuthorizeAndAllowAnonymous_NotSecured()
    {
        await using var fx = await BuildAsync(
            o => o.AddProfile<AnonProfile>(),
            configureApp: app => app.MapGet("/plain/both", () => "hi")
                .RequireAuthorization()
                .AllowAnonymous());
        using JsonDocument doc = await fx.GetDocumentAsync();

        JsonElement op = GetOperation(doc, "/plain/both", "get");
        Assert.False(op.TryGetProperty("security", out JsonElement sec) && sec.GetArrayLength() > 0,
            "AllowAnonymous must win over RequireAuthorization");
        Assert.False(op.GetProperty("responses").TryGetProperty("401", out _));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        bool registerSecurityProcessor = true,
        Action<AspNetCoreOpenApiDocumentGeneratorSettings>? configureBeforeSecurity = null,
        Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddEndpointsApiExplorer();

        // The app owns identity: OhData only references the scheme, it never defines it.
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, NoOpAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();

        builder.Services.AddOpenApiDocument((s, sp) =>
        {
            s.OperationProcessors.Add(new OhDataNSwagOperationProcessor());
            s.SchemaSettings.SchemaProcessors.Add(new OhDataNSwagSchemaProcessor(sp));

            // Runs before the security processor so a test can pre-seed operations first.
            configureBeforeSecurity?.Invoke(s);

            if (registerSecurityProcessor)
            {
                s.OperationProcessors.Add(new OhDataNSwagSecurityOperationProcessor(SchemeId));
            }
        });

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            configure(o);
        });

        var app = builder.Build();
        app.MapOhData();
        configureApp?.Invoke(app);
        app.UseOpenApi();
        await app.StartAsync();
        return new TestFixture(app);
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
    /// processor runs, to prove the processor's <c>ContainsKey("401")</c> guard leaves an
    /// app-defined response untouched.
    /// </summary>
    private sealed class PreSeed401Processor : IOperationProcessor
    {
        private readonly string _description;

        public PreSeed401Processor(string description) => _description = description;

        public bool Process(OperationProcessorContext context)
        {
            context.OperationDescription.Operation.Responses["401"] = new OpenApiResponse { Description = _description };
            return true;
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
}
