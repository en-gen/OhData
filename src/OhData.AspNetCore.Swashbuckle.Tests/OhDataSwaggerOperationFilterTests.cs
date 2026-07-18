using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OhData.Abstractions;
using OhData.AspNetCore;
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
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o => o.AddProfile<AllFlagsProfile>());
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
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o => o.AddProfile<NoFlagsProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        string[] names = ParameterNames(doc, "/odata/NoFlagsWidgets");
        Assert.Equal(new HashSet<string> { "$top", "$skip" }, names.ToHashSet());
    }

    [Fact]
    public async Task MaxTopSet_TopDescriptionContainsCap()
    {
        await using TestFixture fx = await SwashbuckleTestHostBuilder.BuildAsync(o => o.AddProfile<MaxTopProfile>());
        using JsonDocument doc = await fx.GetDocumentAsync();

        string description = ParameterDescription(doc, "/odata/MaxTopWidgets", "$top");
        Assert.Contains("25", description);
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
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
            Search = (term, ct) => Task.FromResult<IEnumerable<Widget>>(Store);
        }
    }

    private class NoFlagsProfile : EntitySetProfile<int, Widget>
    {
        public NoFlagsProfile() : base(x => x.Id)
        {
            EntitySetName = "NoFlagsWidgets";
            GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(Store);
        }
    }

    private class MaxTopProfile : EntitySetProfile<int, Widget>
    {
        public MaxTopProfile() : base(x => x.Id)
        {
            EntitySetName = "MaxTopWidgets";
            MaxTop = 25;
            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }
}
