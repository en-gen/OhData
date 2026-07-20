using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Leg 2 (docs-fidelity): verifies <see cref="OhDataApiDescriptionProvider"/> at the ApiExplorer
/// level -- below any specific OpenAPI document generator (Microsoft.AspNetCore.OpenApi, NSwag,
/// Swashbuckle) -- since all three are built on top of <see cref="IApiDescriptionGroupCollectionProvider"/>
/// and this is the layer <c>AddOhData</c> actually registers into.
/// </summary>
public class ApiDescriptionProviderTests
{
    private static async Task<(WebApplication App, IApiDescriptionGroupCollectionProvider Provider)> BuildAsync(
        System.Action<OhDataBuilder> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            configure(o);
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();

        var provider = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
        return (app, provider);
    }

    private static ApiDescription FindDescription(
        IApiDescriptionGroupCollectionProvider provider, string method, string relativePathContains) =>
        provider.ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .First(d => string.Equals(d.HttpMethod, method, System.StringComparison.OrdinalIgnoreCase)
                        && (d.RelativePath ?? "").Contains(relativePathContains));

    [Fact]
    public async Task EntityPost_GetsBodyParameterDescription()
    {
        var (app, provider) = await BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        await using var _ = app;

        ApiDescription description = FindDescription(provider, "POST", "Widgets");

        ApiParameterDescription? body = description.ParameterDescriptions
            .FirstOrDefault(p => p.Source == BindingSource.Body);
        Assert.NotNull(body);
        Assert.Equal(typeof(Widget), body!.Type);
        Assert.NotNull(body.ModelMetadata);
        Assert.Contains(description.SupportedRequestFormats, f => f.MediaType == "application/json");
    }

    [Fact]
    public async Task EntityPatch_GetsBodyParameterDescription()
    {
        var (app, provider) = await BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        await using var _ = app;

        ApiDescription description = FindDescription(provider, "PATCH", "Widgets(");

        ApiParameterDescription? body = description.ParameterDescriptions
            .FirstOrDefault(p => p.Source == BindingSource.Body);
        Assert.NotNull(body);
        Assert.Equal(typeof(Widget), body!.Type);
    }

    [Fact]
    public async Task GetById_HasNoBodyParameterDescription()
    {
        var (app, provider) = await BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        await using var _ = app;

        ApiDescription description = FindDescription(provider, "GET", "Widgets(");

        Assert.DoesNotContain(description.ParameterDescriptions, p => p.Source == BindingSource.Body);
    }

    [Fact]
    public async Task BoundFunction_GetsQueryParameterDescriptions_WithRequiredFlags()
    {
        // Issue #181: a bound function's query-string parameters must surface as query
        // ApiParameterDescriptions (with correct Type/ModelMetadata and a required flag driven by
        // whether the delegate parameter has a C# default), mirroring the body-parameter fix above.
        var (app, provider) = await BuildAsync(o => o.AddEntitySetProfile<FunctionParamProfile>());
        await using var _ = app;

        ApiDescription description = FindDescription(provider, "GET", "FnParamWidgets/TopRated");

        ApiParameterDescription? term = description.ParameterDescriptions
            .FirstOrDefault(p => p.Source == BindingSource.Query && p.Name == "term");
        Assert.NotNull(term);
        Assert.Equal(typeof(string), term!.Type);
        Assert.NotNull(term.ModelMetadata);
        Assert.True(term.IsRequired); // no C# default -> required

        ApiParameterDescription? count = description.ParameterDescriptions
            .FirstOrDefault(p => p.Source == BindingSource.Query && p.Name == "count");
        Assert.NotNull(count);
        Assert.Equal(typeof(int), count!.Type);
        Assert.NotNull(count.ModelMetadata);
        Assert.False(count.IsRequired); // count = 10 -> optional
    }

    [Fact]
    public async Task EntityBoundFunction_DocumentsNonKeyQueryParameters_ButNotTheKey()
    {
        // Issue #181: an entity-bound function's leading key parameter is a route/path parameter
        // (documented via BindingSource.Path), so only the parameters AFTER the key are added as
        // query parameters. Exercises the skip-key branch of BuildFunctionQueryParametersMetadata.
        var (app, provider) = await BuildAsync(o => o.AddEntitySetProfile<FunctionParamProfile>());
        await using var _ = app;

        ApiDescription description = FindDescription(provider, "GET", "Describe");

        ApiParameterDescription? suffix = description.ParameterDescriptions
            .FirstOrDefault(p => p.Source == BindingSource.Query && p.Name == "suffix");
        Assert.NotNull(suffix);
        Assert.Equal(typeof(string), suffix!.Type);
        Assert.NotNull(suffix.ModelMetadata);
        Assert.True(suffix.IsRequired); // no C# default -> required

        // The key must NOT be surfaced as a query parameter.
        Assert.DoesNotContain(description.ParameterDescriptions,
            p => p.Source == BindingSource.Query && p.Name == "key");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task KeyWriteStub_DeclaresKeyPathParameter(string method)
    {
        // Issue #184, item 2: the immutable-key write stubs (PUT/PATCH/DELETE
        // /{Set}({key})/{KeyProperty}) return a fixed 400 but previously took no `key` parameter,
        // so their generated operation omitted the {key} path-parameter declaration its sibling GET
        // carries — an OpenAPI document with an undeclared template variable is technically invalid.
        // The stub lambdas now take (string key); assert the {key} path parameter is documented.
        // #221: property routes (including the key-write stubs) are omitted from docs by default,
        // so opt in here — this test is specifically about their documented representation.
        var (app, provider) = await BuildAsync(o =>
        {
            o.WithDefaults(d => d.PropertyRouteDocsEnabled = true);
            o.AddEntitySetProfile<WidgetProfile>();
        });
        await using var _ = app;

        // Widget's key is Id, so the key-write route is /odata/Widgets({key})/Id.
        ApiDescription description = FindDescription(provider, method, "Widgets({key})/Id");

        ApiParameterDescription? keyParam = description.ParameterDescriptions
            .FirstOrDefault(p => p.Source == BindingSource.Path && p.Name == "key");
        Assert.NotNull(keyParam);
        Assert.Equal(typeof(string), keyParam!.Type);
    }

    [Fact]
    public async Task MultipleAddOhDataCalls_ProviderRegisteredOnce_StillWorks()
    {
        // Leg 2: OhDataApiDescriptionProvider is registered via TryAddEnumerable inside
        // AddOhData, so a second named registration must not throw or duplicate the provider --
        // and the first registration's write routes must still get their body description.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            o.AddEntitySetProfile<WidgetProfile>();
        });
        builder.Services.AddOhData("v2", o =>
        {
            o.WithPrefix("/odata/v2");
            o.AddEntitySetProfile<SecondProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        app.MapOhData("v2");
        await app.StartAsync();
        await using var _ = app;

        int providerCount = app.Services.GetServices<IApiDescriptionProvider>()
            .Count(p => p is OhDataApiDescriptionProvider);
        Assert.Equal(1, providerCount);

        var provider = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
        ApiDescription description = FindDescription(provider, "POST", "Widgets");
        Assert.Contains(description.ParameterDescriptions, p => p.Source == BindingSource.Body);
    }

    private static bool HasDescription(
        IApiDescriptionGroupCollectionProvider provider, string method, string relativePathContains) =>
        provider.ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .Any(d => string.Equals(d.HttpMethod, method, System.StringComparison.OrdinalIgnoreCase)
                      && (d.RelativePath ?? "").Contains(relativePathContains));

    [Fact]
    public async Task PropertyRoutes_OmittedFromDocs_ByDefault()
    {
        // #221: the four property routes per structural property (GET value, GET /$value,
        // PUT/PATCH/DELETE writes) plus the key-immutable stub writes are numerous and, by
        // default, excluded from the generated API docs via ExcludeFromDescription. Widget's
        // only non-key structural property is Name.
        var (app, provider) = await BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        await using var _ = app;

        // Non-key property read routes are absent.
        Assert.False(HasDescription(provider, "GET", "Widgets({key})/Name"));
        Assert.False(HasDescription(provider, "GET", "Widgets({key})/Name/$value"));
        // Non-key property write routes are absent.
        Assert.False(HasDescription(provider, "PUT", "Widgets({key})/Name"));
        Assert.False(HasDescription(provider, "PATCH", "Widgets({key})/Name"));
        Assert.False(HasDescription(provider, "DELETE", "Widgets({key})/Name"));
        // Key-immutable stub writes are absent too.
        Assert.False(HasDescription(provider, "PUT", "Widgets({key})/Id"));
        Assert.False(HasDescription(provider, "PATCH", "Widgets({key})/Id"));
        Assert.False(HasDescription(provider, "DELETE", "Widgets({key})/Id"));

        // The primary CRUD surface is still documented.
        Assert.True(HasDescription(provider, "GET", "Widgets"));
        Assert.True(HasDescription(provider, "POST", "Widgets"));
        Assert.True(HasDescription(provider, "PATCH", "Widgets({key})"));
    }

    [Fact]
    public async Task PropertyRoutes_Documented_WhenEnabledViaDefaults()
    {
        // #221: opt in server-wide via WithDefaults — all property routes appear in the docs.
        var (app, provider) = await BuildAsync(o =>
        {
            o.WithDefaults(d => d.PropertyRouteDocsEnabled = true);
            o.AddEntitySetProfile<WidgetProfile>();
        });
        await using var _ = app;

        Assert.True(HasDescription(provider, "GET", "Widgets({key})/Name"));
        Assert.True(HasDescription(provider, "GET", "Widgets({key})/Name/$value"));
        Assert.True(HasDescription(provider, "PUT", "Widgets({key})/Name"));
        Assert.True(HasDescription(provider, "PATCH", "Widgets({key})/Name"));
        Assert.True(HasDescription(provider, "DELETE", "Widgets({key})/Name"));
    }

    [Fact]
    public async Task PropertyRoutes_Documented_WhenEnabledPerProfile()
    {
        // #221: opt in per-profile — the profile-level flag overrides the default-false.
        var (app, provider) = await BuildAsync(o => o.AddEntitySetProfile<PropertyDocsWidgetProfile>());
        await using var _ = app;

        Assert.True(HasDescription(provider, "GET", "PropDocWidgets({key})/Name"));
        Assert.True(HasDescription(provider, "GET", "PropDocWidgets({key})/Name/$value"));
        Assert.True(HasDescription(provider, "PUT", "PropDocWidgets({key})/Name"));
    }

    [Fact]
    public async Task PropertyRoutes_OmittedPerProfile_OverridesEnabledDefault()
    {
        // #221: profile-level false overrides a WithDefaults true, in the other direction.
        var (app, provider) = await BuildAsync(o =>
        {
            o.WithDefaults(d => d.PropertyRouteDocsEnabled = true);
            o.AddEntitySetProfile<NoPropertyDocsWidgetProfile>();
        });
        await using var _ = app;

        Assert.False(HasDescription(provider, "GET", "NoPropDocWidgets({key})/Name"));
        Assert.False(HasDescription(provider, "GET", "NoPropDocWidgets({key})/Name/$value"));
        Assert.False(HasDescription(provider, "PUT", "NoPropDocWidgets({key})/Name"));
    }

    // #221 fixtures: exercise the per-profile PropertyRouteDocsEnabled override in both directions.
    private sealed class PropertyDocsWidgetProfile : EntitySetProfile<int, Widget>
    {
        public PropertyDocsWidgetProfile() : base(x => x.Id)
        {
            EntitySetName = "PropDocWidgets";
            PropertyRouteDocsEnabled = true;
            GetById = (id, ct) => Task.FromResult<Widget?>(null);
            Patch = (id, delta, ct) => Task.FromResult<Widget?>(null);
        }
    }

    private sealed class NoPropertyDocsWidgetProfile : EntitySetProfile<int, Widget>
    {
        public NoPropertyDocsWidgetProfile() : base(x => x.Id)
        {
            EntitySetName = "NoPropDocWidgets";
            PropertyRouteDocsEnabled = false;
            GetById = (id, ct) => Task.FromResult<Widget?>(null);
            Patch = (id, delta, ct) => Task.FromResult<Widget?>(null);
        }
    }

    // Issue #181 fixture: a collection-bound function with one required parameter (no C# default)
    // and one optional parameter (with a default), so the query-parameter documentation and its
    // required/optional flags can both be asserted.
    private sealed class FunctionParamProfile : EntitySetProfile<int, Widget>
    {
        public FunctionParamProfile() : base(x => x.Id)
        {
            EntitySetName = "FnParamWidgets";
            GetAll = (ct) => Task.FromResult<System.Collections.Generic.IEnumerable<Widget>>(System.Array.Empty<Widget>());
            GetById = (id, ct) => Task.FromResult<Widget?>(null);
            BindFunction(TopRated);
            BindEntityFunction(Describe);
        }

        private Task<System.Collections.Generic.IEnumerable<Widget>> TopRated(string term, int count = 10) =>
            Task.FromResult<System.Collections.Generic.IEnumerable<Widget>>(System.Array.Empty<Widget>());

        // Entity-bound function: first parameter is the entity key (a route/path parameter),
        // followed by a real query parameter. Exercises the skip-key documentation path.
        private Task<string> Describe(int key, string suffix) => Task.FromResult("");
    }
}
