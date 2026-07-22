using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using OhData;
using Xunit;

namespace OhData.AspNetCore.OpenApi.Tests;

/// <summary>
/// Verifies <see cref="OhDataOpenApiOperationTransformer"/> against a real generated OpenAPI
/// document (fetched from /openapi/v1.json on an in-process TestServer), rather than unit-testing
/// the transformer in isolation, so the tests exercise the same ApiExplorer/EndpointMetadata
/// wiring a real host would.
/// </summary>
public sealed class OhDataOpenApiOperationTransformerTests
{
    private static readonly string[] AllODataParams =
        { "$top", "$skip", "$filter", "$orderby", "$select", "$expand", "$count", "$search" };

    // ── 1. All flags on → all 8 parameters present ─────────────────────────────

    [Fact]
    public async Task AllFlagsEnabled_AllODataParametersPresent()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<AllFlagsProfile>());
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/AllFlagsWidgets", "get");
        HashSet<string> names = ParamNames(op);

        foreach (string expected in AllODataParams)
        {
            Assert.Contains(expected, names);
        }
    }

    // ── 2. All flags off → only $top/$skip present ─────────────────────────────

    [Fact]
    public async Task AllFlagsDisabled_OnlyTopAndSkipPresent()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NoFlagsProfile>());
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/NoFlagsWidgets", "get");
        HashSet<string> names = ParamNames(op);

        Assert.Equal(new HashSet<string> { "$top", "$skip" }, names);
    }

    // ── 3. Each flag individually on → exactly that parameter (+ $top/$skip) ───

    [Theory]
    [InlineData(typeof(FilterOnlyProfile), "FilterOnlyWidgets", "$filter")]
    [InlineData(typeof(OrderByOnlyProfile), "OrderByOnlyWidgets", "$orderby")]
    [InlineData(typeof(SelectOnlyProfile), "SelectOnlyWidgets", "$select")]
    [InlineData(typeof(ExpandOnlyProfile), "ExpandOnlyWidgets", "$expand")]
    [InlineData(typeof(CountOnlyProfile), "CountOnlyWidgets", "$count")]
    [InlineData(typeof(SearchOnlyProfile), "SearchOnlyWidgets", "$search")]
    public async Task SingleFlagEnabled_OnlyThatParameterPlusTopSkipPresent(System.Type profileType, string entitySet, string expectedParam)
    {
        await using var fx = await BuildWithProfileAsync(profileType);
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, $"/odata/{entitySet}", "get");
        HashSet<string> names = ParamNames(op);

        var expected = new HashSet<string> { "$top", "$skip", expectedParam };
        Assert.Equal(expected, names);
    }

    // ── 4. MaxTop set → $top description contains the cap value ────────────────

    [Fact]
    public async Task MaxTopSet_TopDescriptionContainsCapValue()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<MaxTopProfile>());
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/MaxTopWidgets", "get");
        JsonElement topParam = FindParam(op, "$top");

        string description = topParam.GetProperty("description").GetString() ?? "";
        Assert.Contains(MaxTopProfile.Cap.ToString(), description);
    }

    // ── 5. Non-OhData endpoint → no OData parameters injected ──────────────────

    [Fact]
    public async Task NonOhDataEndpoint_NoODataParametersInjected()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<NoFlagsProfile>(),
            configureApp: app => app.MapGet("/plain/hello", () => "hi"));
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/plain/hello", "get");
        HashSet<string> names = ParamNames(op);

        Assert.Empty(names);
    }

    // ── 6. GetById (single-entity) route ────────────────────────────────────────
    // OhDataQueryOptionsMetadata IS attached to the GetById route too (FilterEnabled/
    // OrderByEnabled/CountEnabled/SearchEnabled hardcoded false, SelectEnabled/ExpandEnabled
    // mirroring the profile's flags, MaxTop null) — see OhDataEndpointFactory.cs around the
    // GetById route registration. Because the transformer's "$top/$skip always added" rule is
    // keyed only on metadata *presence*, not on which route it's attached to, GetById also
    // picks up $top/$skip (and $select/$expand if those flags are on) even though the route
    // itself does not honor paging. This test documents that actual behavior rather than the
    // "no $top/$skip at all" assumption one might make from route semantics alone.
    [Fact]
    public async Task GetByIdRoute_GetsTopSkipAndSelectExpandButNotFilterOrderByCountSearch()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<AllFlagsProfile>());
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/AllFlagsWidgets({key})", "get");
        HashSet<string> names = ParamNames(op);

        Assert.Equal(new HashSet<string> { "$top", "$skip", "$select", "$expand" }, names);
        Assert.DoesNotContain("$filter", names);
        Assert.DoesNotContain("$orderby", names);
        Assert.DoesNotContain("$count", names);
        Assert.DoesNotContain("$search", names);
    }

    // ── 7. No duplicate parameters when $top is already present ────────────────

    [Fact]
    public async Task PreExistingTopParameter_NotDuplicated()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<NoFlagsProfile>(),
            configureOpenApi: o =>
            {
                o.AddOperationTransformer<PreExistingTopTransformer>();
                o.AddOperationTransformer<OhDataOpenApiOperationTransformer>();
            });
        using JsonDocument doc = await FetchDocumentAsync(fx.Client);

        JsonElement op = GetOperation(doc, "/odata/NoFlagsWidgets", "get");
        int topCount = op.GetProperty("parameters").EnumerateArray()
            .Count(p => p.GetProperty("name").GetString() == "$top");

        Assert.Equal(1, topCount);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<TestFixture> BuildWithProfileAsync(System.Type profileType)
    {
        var addProfile = typeof(OhDataBuilder).GetMethod("AddEntitySetProfile")!.MakeGenericMethod(profileType);
        return await TestHostBuilder.BuildAsync(o => addProfile.Invoke(o, null));
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

    /// <summary>
    /// Names of the operation's query-string parameters only (excludes path parameters like
    /// "key" on GetById, which ASP.NET's OpenAPI generator adds independently of the OhData
    /// transformer and which would otherwise pollute set-equality assertions below).
    /// </summary>
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

    private static JsonElement FindParam(JsonElement operation, string name) =>
        operation.GetProperty("parameters").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == name);

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private static readonly List<Widget> Store = new() { new() { Id = 1, Name = "Alpha" } };

    private class AllFlagsProfile : EntitySetProfile<int, Widget>
    {
        public AllFlagsProfile() : base(x => x.Id)
        {
            EntitySetName = "AllFlagsWidgets";
            FilterEnabled = true;
            OrderByEnabled = true;
            SelectEnabled = true;
            ExpandEnabled = true;
            CountEnabled = true;
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(w => w.Id == id));
            Search = (term, ct) => Task.FromResult<IEnumerable<Widget>>(Store);
        }
    }

    private class NoFlagsProfile : EntitySetProfile<int, Widget>
    {
        public NoFlagsProfile() : base(x => x.Id)
        {
            EntitySetName = "NoFlagsWidgets";
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }

    private class FilterOnlyProfile : EntitySetProfile<int, Widget>
    {
        public FilterOnlyProfile() : base(x => x.Id)
        {
            EntitySetName = "FilterOnlyWidgets";
            FilterEnabled = true;
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }

    private class OrderByOnlyProfile : EntitySetProfile<int, Widget>
    {
        public OrderByOnlyProfile() : base(x => x.Id)
        {
            EntitySetName = "OrderByOnlyWidgets";
            OrderByEnabled = true;
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }

    private class SelectOnlyProfile : EntitySetProfile<int, Widget>
    {
        public SelectOnlyProfile() : base(x => x.Id)
        {
            EntitySetName = "SelectOnlyWidgets";
            SelectEnabled = true;
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }

    private class ExpandOnlyProfile : EntitySetProfile<int, Widget>
    {
        public ExpandOnlyProfile() : base(x => x.Id)
        {
            EntitySetName = "ExpandOnlyWidgets";
            ExpandEnabled = true;
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }

    private class CountOnlyProfile : EntitySetProfile<int, Widget>
    {
        public CountOnlyProfile() : base(x => x.Id)
        {
            EntitySetName = "CountOnlyWidgets";
            CountEnabled = true;
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }

    private class SearchOnlyProfile : EntitySetProfile<int, Widget>
    {
        public SearchOnlyProfile() : base(x => x.Id)
        {
            EntitySetName = "SearchOnlyWidgets";
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            Search = (term, ct) => Task.FromResult<IEnumerable<Widget>>(Store);
        }
    }

    private class MaxTopProfile : EntitySetProfile<int, Widget>
    {
        public const int Cap = 42;

        public MaxTopProfile() : base(x => x.Id)
        {
            EntitySetName = "MaxTopWidgets";
            MaxTop = Cap;
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }

    /// <summary>
    /// Adds a bare-bones $top parameter to every operation before OhDataOpenApiOperationTransformer
    /// runs, simulating a document that already carries a $top parameter from some other source
    /// (e.g. another transformer), to verify the duplicate guard.
    /// </summary>
    private sealed class PreExistingTopTransformer : IOpenApiOperationTransformer
    {
        public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
        {
            operation.Parameters ??= new List<IOpenApiParameter>();
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "$top",
                In = ParameterLocation.Query,
                Description = "pre-existing",
            });
            return Task.CompletedTask;
        }
    }
}
