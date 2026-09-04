using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Swashbuckle.Tests;

/// <summary>
/// Verifies <see cref="OhDataSwaggerOperationFilter"/> against a real Swashbuckle-generated
/// OpenAPI document (fetched from /swagger/v1/swagger.json on an in-process TestServer), mirroring
/// OhData.AspNetCore.OpenApi.Tests.OhDataOpenApiOperationTransformerTests and
/// OhData.AspNetCore.NSwag.Tests.OhDataNSwagOperationProcessorTests for the Swashbuckle doc stack.
/// </summary>
public sealed class OhDataSwaggerOperationFilterTests
{
    [Fact]
    public async Task AllFlagsEnabled_AllODataParametersPresent()
    {
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<AllFlagsProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        string[] names = ParameterNames(doc, "/odata/AllFlagsWidgets");
        foreach (string expected in new[] { "$top", "$skip", "$filter", "$orderby", "$select", "$expand", "$count", "$search" })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public async Task NoFlags_OnlyTopAndSkipPresent()
    {
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NoFlagsProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        string[] names = ParameterNames(doc, "/odata/NoFlagsWidgets");
        Assert.Equal(new HashSet<string> { "$top", "$skip" }, names.ToHashSet());
    }

    [Fact]
    public async Task MaxTopSet_TopDescriptionContainsCap()
    {
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<MaxTopProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        string description = ParameterDescription(doc, "/odata/MaxTopWidgets", "$top");
        Assert.Contains("25", description);
    }

    // ── #467: options are documented only where the route honours them ─────────
    //
    // OhDataQueryOptionsMetadata is attached to five route shapes, not just the paged collection
    // GETs. The filter used to add $top/$skip on metadata *presence* alone, so a single-entity
    // read and a /$count both advertised paging they ignore. These three tests mirror
    // OhData.AspNetCore.OpenApi.Tests and OhData.AspNetCore.NSwag.Tests case for case -- the
    // whole point of #467 is that the three packages agree, and they agree because the decision
    // lives upstream in OhDataEndpointFactory's metadata rather than in each transformer.

    [Fact]
    public async Task GetByIdRoute_GetsSelectExpandOnly_NoTopSkip()
    {
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<AllFlagsProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        string[] names = ParameterNamesOrEmpty(doc, "/odata/AllFlagsWidgets({key})");
        Assert.Equal(new HashSet<string> { "$select", "$expand" }, names.ToHashSet());
    }

    [Fact]
    public async Task CountRoute_GetsFilterOnly_NoTopSkipNoCount()
    {
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<AllFlagsProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        string[] names = ParameterNamesOrEmpty(doc, "/odata/AllFlagsWidgets/$count");
        Assert.Equal(new HashSet<string> { "$filter" }, names.ToHashSet());
    }

    [Fact]
    public async Task CountRoute_GetAllOnlySource_DoesNotAdvertiseFilter()
    {
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<GetAllFilterEnabledProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        string[] names = ParameterNamesOrEmpty(doc, "/odata/GetAllFilterWidgets/$count");
        Assert.Empty(names);
    }

    /// <summary>
    /// #467: like <see cref="ParameterNames"/> but tolerates an operation with no parameters at
    /// all -- once a route stops advertising options it does not honour, some operations end up
    /// with an empty (and therefore omitted) "parameters" member.
    /// </summary>
    private static string[] ParameterNamesOrEmpty(JsonDocument doc, string path, string method = "get")
    {
        JsonElement operation = doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);
        if (!operation.TryGetProperty("parameters", out JsonElement parameters))
        {
            return System.Array.Empty<string>();
        }

        return parameters.EnumerateArray()
            .Where(p => p.GetProperty("in").GetString() == "query")
            .Select(p => p.GetProperty("name").GetString()!)
            .ToArray();
    }

    private static string[] ParameterNames(JsonDocument doc, string path, string method = "get") =>
        doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("parameters").EnumerateArray()
            .Where(p => p.GetProperty("in").GetString() == "query")
            .Select(p => p.GetProperty("name").GetString()!)
            .ToArray();

    private static string ParameterDescription(JsonDocument doc, string path, string name, string method = "get") =>
        doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("parameters").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == name)
            .GetProperty("description").GetString()!;

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
            GetQueryable = (ct) => OhDataResult.Success(Store.AsQueryable());
            // #467: GetById added so this fixture also covers the single-entity route, which
            // carries OhDataQueryOptionsMetadata and used to be documented with $top/$skip.
            GetById = (id, ct) => OhDataResult.Success(Store.FirstOrDefault(w => w.Id == id));
            Search = (term, ct) => OhDataResult.Success<IEnumerable<Widget>>(Store);
        }
    }

    private class NoFlagsProfile : EntitySetProfile<int, Widget>
    {
        public NoFlagsProfile() : base(x => x.Id)
        {
            EntitySetName = "NoFlagsWidgets";
            GetAll = (ct) => OhDataResult.Success<IEnumerable<Widget>>(Store);
        }
    }

    // #467 (F3): the same Widget model over the simple GetAll read path, with FilterEnabled on.
    // The flag is honoured on none of this profile's routes -- neither the collection GET nor
    // /$count has an IQueryable to apply a filter to -- so neither may advertise $filter.
    private class GetAllFilterEnabledProfile : EntitySetProfile<int, Widget>
    {
        public GetAllFilterEnabledProfile() : base(x => x.Id)
        {
            EntitySetName = "GetAllFilterWidgets";
            FilterEnabled = true;
            GetAll = (ct) => OhDataResult.Success<IEnumerable<Widget>>(Store);
        }
    }

    private class MaxTopProfile : EntitySetProfile<int, Widget>
    {
        public MaxTopProfile() : base(x => x.Id)
        {
            EntitySetName = "MaxTopWidgets";
            MaxTop = 25;
            GetQueryable = (ct) => OhDataResult.Success(Store.AsQueryable());
        }
    }
}
