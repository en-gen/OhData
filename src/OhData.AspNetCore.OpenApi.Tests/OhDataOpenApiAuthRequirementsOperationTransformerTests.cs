using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.OpenApi.Tests;

/// <summary>
/// Verifies <see cref="OhDataOpenApiAuthRequirementsOperationTransformer"/> (#220): when opted in,
/// each secured operation's description gains a human-readable requirements section drawn from
/// OhData's structured auth data (roles / claim types + values / policy names). Proves it is off by
/// default, that exact claim VALUES are withheld at the Kinds disclosure level, that named policies
/// stay opaque, and that resource-only (Layer B) rules render nothing.
/// </summary>
public sealed class OhDataOpenApiAuthRequirementsOperationTransformerTests
{
    // ── 1. Opted in at Kinds → requirements rendered, claim VALUE hidden ───────

    [Fact]
    public async Task Kinds_RendersRolesClaimTypesAndPolicyNames_ButHidesClaimValues()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<RichAuthProfile>(), AuthRequirementDisclosure.Kinds);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/RichWidgets", "get");

        Assert.Contains("**Authorization:**", description);
        Assert.Contains("role `editors`", description);
        Assert.Contains("claim `dept`", description);
        Assert.Contains("policy `HrOnly`", description);
        // The sensitive part: the required claim VALUE must NOT appear at Kinds disclosure.
        Assert.DoesNotContain("sales", description);
    }

    // ── 2. Opted in at Full → claim VALUE now rendered ─────────────────────────

    [Fact]
    public async Task Full_RendersClaimValues()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<RichAuthProfile>(), AuthRequirementDisclosure.Full);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/RichWidgets", "get");

        Assert.Contains("claim `dept` = `sales`", description);
    }

    // ── 3. Off by default (not registered) → no requirements section ───────────

    [Fact]
    public async Task NotRegistered_NoRequirementsSection()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<RichAuthProfile>(), disclosure: null);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/RichWidgets", "get");
        Assert.DoesNotContain("**Authorization:**", description);
    }

    // ── 4. Resource-only (Layer B) rule → nothing rendered ─────────────────────

    [Fact]
    public async Task ResourceOnlyRequirement_RendersNothing()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<ResourceOnlyProfile>(), AuthRequirementDisclosure.Full);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/ResourceWidgets", "get");
        Assert.DoesNotContain("**Authorization:**", description);
    }

    // ── 5. Anonymous (no per-op auth) → nothing rendered ───────────────────────

    [Fact]
    public async Task ProfileWithoutPerOperationAuth_RendersNothing()
    {
        await using var fx = await BuildAsync(o => o.AddProfile<PlainProfile>(), AuthRequirementDisclosure.Kinds);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/PlainWidgets", "get");
        Assert.DoesNotContain("**Authorization:**", description);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        AuthRequirementDisclosure? disclosure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();

        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, NoOpAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization(o =>
            o.AddPolicy("HrOnly", p => p.RequireAuthenticatedUser()));

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            configure(o);
        });

        builder.Services.AddOpenApi(o =>
        {
            o.AddOperationTransformer<OhDataOpenApiOperationTransformer>();
            if (disclosure is AuthRequirementDisclosure level)
            {
                o.AddOperationTransformer(new OhDataOpenApiAuthRequirementsOperationTransformer(level));
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

    /// <summary>Operation description, or "" when the operation has none.</summary>
    private static string OperationDescription(JsonDocument doc, string exactPath, string method)
    {
        JsonElement paths = doc.RootElement.GetProperty("paths");
        if (!paths.TryGetProperty(exactPath, out JsonElement pathItem))
        {
            string available = string.Join(", ", paths.EnumerateObject().Select(p => p.Name));
            throw new Xunit.Sdk.XunitException($"No path '{exactPath}' in document. Available: {available}");
        }
        JsonElement op = pathItem.GetProperty(method);
        return op.TryGetProperty("description", out JsonElement d) ? d.GetString() ?? "" : "";
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

    private class RichAuthProfile : EntitySetProfile<int, Widget>
    {
        public RichAuthProfile() : base(x => x.Id)
        {
            EntitySetName = "RichWidgets";
            ConfigureAuthorization(a => a.Read(r => r
                .RequireRole("editors")
                .RequireClaim("dept", "sales")
                .RequirePolicy("HrOnly")));
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(w => w.Id == id));
        }
    }

    private class ResourceOnlyProfile : EntitySetProfile<int, Widget>
    {
        public ResourceOnlyProfile() : base(x => x.Id)
        {
            EntitySetName = "ResourceWidgets";
            ConfigureAuthorization(a => a.Read(r => r.RequireResource()));
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(w => w.Id == id));
        }
    }

    private class PlainProfile : EntitySetProfile<int, Widget>
    {
        public PlainProfile() : base(x => x.Id)
        {
            EntitySetName = "PlainWidgets";
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }
}
