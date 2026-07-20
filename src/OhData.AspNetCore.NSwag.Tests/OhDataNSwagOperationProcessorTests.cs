using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace OhData.AspNetCore.NSwag.Tests;

public class OhDataNSwagOperationProcessorTests
{
    private static string[] ParameterNames(JsonDocument doc, string path, string method = "get")
    {
        var paramsElement = doc.RootElement
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty(method)
            .GetProperty("parameters");

        return paramsElement.EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()!)
            .ToArray();
    }

    private static string ParameterDescription(JsonDocument doc, string path, string name, string method = "get")
    {
        var paramsElement = doc.RootElement
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty(method)
            .GetProperty("parameters");

        return paramsElement.EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == name)
            .GetProperty("description").GetString()!;
    }

    // ── 1. All flags on ─────────────────────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task AllFlagsEnabled_AllODataParametersPresent()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<AllFlagsWidgetProfile>());

        using var doc = await fixture.GetDocumentAsync();
        string[] names = ParameterNames(doc, "/odata/AllFlagsWidgets");

        Assert.Contains("$top", names);
        Assert.Contains("$skip", names);
        Assert.Contains("$filter", names);
        Assert.Contains("$orderby", names);
        Assert.Contains("$select", names);
        Assert.Contains("$expand", names);
        Assert.Contains("$count", names);
        Assert.Contains("$search", names);
        Assert.Equal(8, names.Length);
    }

    // ── 2. All flags off ────────────────────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task NoFlagsEnabled_OnlyTopAndSkipPresent()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<NoFlagsWidgetProfile>());

        using var doc = await fixture.GetDocumentAsync();
        string[] names = ParameterNames(doc, "/odata/NoFlagsWidgets");

        Assert.Equal(new[] { "$top", "$skip" }, names);
    }

    // ── 3. Each flag individually on ───────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task FilterOnly_OnlyFilterPlusPagingPresent()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<FilterOnlyWidgetProfile>());

        using var doc = await fixture.GetDocumentAsync();
        string[] names = ParameterNames(doc, "/odata/FilterOnlyWidgets");

        Assert.Equal(new[] { "$top", "$skip", "$filter" }, names);
    }

    [Fact]
    public async System.Threading.Tasks.Task OrderByOnly_OnlyOrderByPlusPagingPresent()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<OrderByOnlyWidgetProfile>());

        using var doc = await fixture.GetDocumentAsync();
        string[] names = ParameterNames(doc, "/odata/OrderByOnlyWidgets");

        Assert.Equal(new[] { "$top", "$skip", "$orderby" }, names);
    }

    [Fact]
    public async System.Threading.Tasks.Task SelectOnly_OnlySelectPlusPagingPresent()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<SelectOnlyWidgetProfile>());

        using var doc = await fixture.GetDocumentAsync();
        string[] names = ParameterNames(doc, "/odata/SelectOnlyWidgets");

        Assert.Equal(new[] { "$top", "$skip", "$select" }, names);
    }

    [Fact]
    public async System.Threading.Tasks.Task ExpandOnly_OnlyExpandPlusPagingPresent()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ExpandOnlyWidgetProfile>());

        using var doc = await fixture.GetDocumentAsync();
        string[] names = ParameterNames(doc, "/odata/ExpandOnlyWidgets");

        Assert.Equal(new[] { "$top", "$skip", "$expand" }, names);
    }

    [Fact]
    public async System.Threading.Tasks.Task CountOnly_OnlyCountPlusPagingPresent()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<CountOnlyWidgetProfile>());

        using var doc = await fixture.GetDocumentAsync();
        string[] names = ParameterNames(doc, "/odata/CountOnlyWidgets");

        Assert.Equal(new[] { "$top", "$skip", "$count" }, names);
    }

    [Fact]
    public async System.Threading.Tasks.Task SearchOnly_OnlySearchPlusPagingPresent()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<SearchOnlyWidgetProfile>());

        using var doc = await fixture.GetDocumentAsync();
        string[] names = ParameterNames(doc, "/odata/SearchOnlyWidgets");

        Assert.Equal(new[] { "$top", "$skip", "$search" }, names);
    }

    // ── 4. MaxTop reflected in the $top description ───────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task MaxTopSet_TopDescriptionContainsCap()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<AllFlagsWidgetProfile>());

        using var doc = await fixture.GetDocumentAsync();
        string description = ParameterDescription(doc, "/odata/AllFlagsWidgets", "$top");

        Assert.Contains("25", description);
    }

    [Fact]
    public async System.Threading.Tasks.Task NoMaxTop_TopDescriptionHasNoCap()
    {
        // A profile-level `MaxTop = null` only means "not overridden at the profile level" --
        // it still inherits EntitySetDefaults.MaxTop (1000 by default). Genuinely disabling the
        // cap requires nulling it out at the defaults level too.
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(o =>
        {
            o.WithDefaults(d => d.MaxTop = null);
            o.AddEntitySetProfile<NoMaxTopWidgetProfile>();
        });

        using var doc = await fixture.GetDocumentAsync();
        string description = ParameterDescription(doc, "/odata/NoMaxTopWidgets", "$top");

        Assert.DoesNotContain("server cap", description);
    }

    // Leg 1 (docs-fidelity): GetAll's $top is now capped by MaxTop exactly like GetQueryable's,
    // so a GetAll profile that leaves MaxTop at its EntitySetDefaults-provided default (1000)
    // must advertise that cap too, instead of the pre-Leg-1 behavior of always claiming "no cap"
    // for the GetAll path regardless of the profile's actual MaxTop.
    [Fact]
    public async System.Threading.Tasks.Task GetAllWithDefaultMaxTop_TopDescriptionContainsCap()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<NoFlagsWidgetProfile>());

        using var doc = await fixture.GetDocumentAsync();
        string description = ParameterDescription(doc, "/odata/NoFlagsWidgets", "$top");

        Assert.Contains("1000", description);
    }

    // ── 5. Non-OhData endpoint in the same host is untouched ──────────────────

    [Fact]
    public async System.Threading.Tasks.Task PlainMinimalApiEndpoint_NoODataParametersInjected()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<NoFlagsWidgetProfile>(),
            configureExtraRoutes: routes => routes.MapGet("/plain", () => "hi"));

        using var doc = await fixture.GetDocumentAsync();

        Assert.True(doc.RootElement.GetProperty("paths").TryGetProperty("/plain", out var plainPath));
        bool hasParameters = plainPath.GetProperty("get").TryGetProperty("parameters", out var parameters);
        if (hasParameters)
        {
            Assert.Empty(parameters.EnumerateArray());
        }
    }

    // ── 6. GetById-only route: metadata is present but not "paged" ────────────
    //
    // OhDataQueryOptionsMetadata is attached to GET /{Set}({key}) too (it gates $select/
    // $expand on GetById), not only to collection GET routes. Since OhDataNSwagOperationProcessor
    // (matching OhDataSwaggerOperationFilter's own behavior) always adds $top/$skip whenever
    // metadata is present and $top isn't already there, GetById still gets $top/$skip even
    // though paging is meaningless for a single-entity fetch. This test documents that actual
    // behavior rather than the (incorrect) assumption that GetById carries no metadata at all.
    [Fact]
    public async System.Threading.Tasks.Task GetByIdOnlyRoute_GetsTopAndSkipButNoOtherODataParams()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<GetByIdOnlyWidgetProfile>());

        using var doc = await fixture.GetDocumentAsync();

        // No collection GET route was registered at all (GetById was the only handler).
        Assert.False(doc.RootElement.GetProperty("paths").TryGetProperty("/odata/GetByIdOnlyWidgets", out _));

        string[] names = ParameterNames(doc, "/odata/GetByIdOnlyWidgets({key})");

        Assert.Contains("$top", names);
        Assert.Contains("$skip", names);
        Assert.DoesNotContain("$filter", names);
        Assert.DoesNotContain("$orderby", names);
        Assert.DoesNotContain("$select", names);
        Assert.DoesNotContain("$expand", names);
        Assert.DoesNotContain("$count", names);
        Assert.DoesNotContain("$search", names);
    }

    // ── 7. Duplicate-parameter guard ───────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task PreExistingTopParameter_NotDuplicated()
    {
        await using var fixture = await NSwagTestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<DupTopWidgetProfile>(),
            configureDocument: s => s.OperationProcessors.Insert(
                0, new PreExistingTopOperationProcessor("/odata/DupTopWidgets")));

        using var doc = await fixture.GetDocumentAsync();
        string[] names = ParameterNames(doc, "/odata/DupTopWidgets");

        // OhDataNSwagOperationProcessor's guard is "if $top is absent, add both $top and
        // $skip" (mirroring OhDataSwaggerOperationFilter exactly) — so once a prior processor
        // has already added $top, OUR processor adds neither $top nor $skip. Only the single,
        // pre-existing $top parameter remains; no duplicate, and no $skip either.
        Assert.Equal(1, names.Count(n => n == "$top"));
        Assert.DoesNotContain("$skip", names);

        // The pre-existing $top parameter (added by a processor that ran first) survives
        // untouched — proves OhDataNSwagOperationProcessor didn't overwrite or duplicate it.
        string description = ParameterDescription(doc, "/odata/DupTopWidgets", "$top");
        Assert.Equal(PreExistingTopOperationProcessor.MarkerDescription, description);
    }
}
