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
using Microsoft.OpenApi;
using OhData;
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
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<RichAuthProfile>(), AuthRequirementDisclosure.Kinds);
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
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<RichAuthProfile>(), AuthRequirementDisclosure.Full);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/RichWidgets", "get");

        Assert.Contains("claim `dept` = `sales`", description);
    }

    // ── 3. Off by default (not registered) → no requirements section ───────────

    [Fact]
    public async Task NotRegistered_NoRequirementsSection()
    {
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<RichAuthProfile>(), disclosure: null);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/RichWidgets", "get");
        Assert.DoesNotContain("**Authorization:**", description);
    }

    // ── 4. Resource-only (Layer B) rule → nothing rendered ─────────────────────

    [Fact]
    public async Task ResourceOnlyRequirement_RendersNothing()
    {
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<ResourceOnlyProfile>(), AuthRequirementDisclosure.Full);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/ResourceWidgets", "get");
        Assert.DoesNotContain("**Authorization:**", description);
    }

    // ── 5. No per-operation auth → nothing rendered ────────────────────────────

    [Fact]
    public async Task ProfileWithoutPerOperationAuth_RendersNothing()
    {
        await using var fx = await BuildAsync(o => o.AddEntitySetProfile<PlainProfile>(), AuthRequirementDisclosure.Kinds);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/PlainWidgets", "get");
        Assert.DoesNotContain("**Authorization:**", description);
    }

    // ── 6. Registering twice does not double-append the section ─────────────────

    [Fact]
    public async Task RegisteredTwice_SectionAppendedOnce()
    {
        await using var fx = await BuildAsync(
            o => o.AddEntitySetProfile<RichAuthProfile>(), AuthRequirementDisclosure.Kinds, registerTwice: true);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/RichWidgets", "get");
        int occurrences = CountOccurrences(description, "**Authorization:**");
        Assert.Equal(1, occurrences);
    }

    // ── 7. An existing operation description is preserved, not replaced ─────────

    [Fact]
    public async Task ExistingDescription_PreservedAndSectionAppended()
    {
        const string baseText = "Base operation description.";
        await using var fx = await BuildAsync(
            o => o.AddEntitySetProfile<RichAuthProfile>(), AuthRequirementDisclosure.Kinds,
            configureBefore: o => o.AddOperationTransformer(new PreSeedDescriptionTransformer(baseText)));
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        string description = OperationDescription(doc, "/odata/RichWidgets", "get");
        Assert.StartsWith(baseText, description);
        Assert.Contains("**Authorization:** Requires", description);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        AuthRequirementDisclosure? disclosure,
        bool registerTwice = false,
        Action<OpenApiOptions>? configureBefore = null)
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
            // Runs first so a test can pre-seed a base description before the requirements filter.
            configureBefore?.Invoke(o);

            o.AddOperationTransformer<OhDataOpenApiOperationTransformer>();
            if (disclosure is AuthRequirementDisclosure level)
            {
                o.AddOperationTransformer(new OhDataOpenApiAuthRequirementsOperationTransformer(level));
                if (registerTwice)
                {
                    o.AddOperationTransformer(new OhDataOpenApiAuthRequirementsOperationTransformer(level));
                }
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

    /// <summary>Sets a base description on every operation before the requirements filter runs.</summary>
    private sealed class PreSeedDescriptionTransformer : IOpenApiOperationTransformer
    {
        private readonly string _description;

        public PreSeedDescriptionTransformer(string description) => _description = description;

        public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, System.Threading.CancellationToken cancellationToken)
        {
            operation.Description = _description;
            return Task.CompletedTask;
        }
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
