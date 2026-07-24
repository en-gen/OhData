using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #301: ValidateNestedTopCeiling (the #254 MaxExpandTop guard on an explicit nested $top) was enforced
// on all three collection read paths but NOT on GET /{EntitySet}({key}) — even though GetById shares the
// same $expand inlining pipeline as the collection routes (per docs/query-options.md: "$expand on the
// single-entity route inlines the requested navigation properties using the same navigation-route
// handlers... as the collection route"). So GET /Set(1)?$expand=Nav($top=huge) was silently accepted
// where the equivalent collection request GET /Set?$expand=Nav($top=huge) already 400s. Fixed by calling
// ValidateNestedTopCeiling right after ValidatePropertyAllowlists in the GetById route, mirroring the
// three collection-route call sites.
//
// The ceiling check itself is a pure SelectExpandClause walk (ValidateNestedTopCeiling) that runs before
// any handler — it does not require EF Core pushdown to be engaged, so a simple in-memory-backed profile
// (mirroring QueryOptionCoverageFixtures.QueryOptionExpandProfile) is enough; no SQLite/EF harness needed.
public sealed class GbCeilingParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<GbCeilingChild> Children { get; set; } = new();
}

public sealed class GbCeilingChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class GbCeilingProfile : EntitySetProfile<int, GbCeilingParent>
{
    private static readonly List<GbCeilingParent> _parents = new()
    {
        new() { Id = 1, Name = "Root" },
    };

    private static readonly List<GbCeilingChild> _children = new()
    {
        new() { Id = 1, ParentId = 1, Name = "C1" },
        new() { Id = 2, ParentId = 1, Name = "C2" },
        new() { Id = 3, ParentId = 1, Name = "C3" },
    };

    public GbCeilingProfile() : base(x => x.Id)
    {
        EntitySetName = "GbCeilingParents";
        ExpandEnabled = true;
        MaxExpandTop = 2;

        GetQueryable = ct => Task.FromResult(_parents.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(x => x.Children,
            getAll: (parentId, ct) =>
                Task.FromResult<IEnumerable<GbCeilingChild>>(_children.Where(c => c.ParentId == parentId)));
    }
}

public sealed class GetByIdNestedTopCeilingTests : IAsyncLifetime
{
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _fx = await TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<GbCeilingProfile>());
    }

    public async Task DisposeAsync() => await _fx.DisposeAsync();

    [Fact]
    public async Task GetById_NestedTop_AboveCeiling_Returns400_SameAsCollectionRoute()
    {
        HttpResponseMessage collection = await _fx.Client.GetAsync(
            "/odata/GbCeilingParents?$expand=Children($top=3)");
        HttpResponseMessage byId = await _fx.Client.GetAsync(
            "/odata/GbCeilingParents(1)?$expand=Children($top=3)");

        Assert.Equal(HttpStatusCode.BadRequest, collection.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, byId.StatusCode); // was 200 before the #301 fix

        string body = await byId.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Children", body);
        Assert.Contains("(3)", body);
        Assert.Contains("(2)", body);
    }

    [Fact]
    public async Task GetById_NestedTop_AtCeiling_Succeeds()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/GbCeilingParents(1)?$expand=Children($top=2)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetById_NoExpand_Unaffected()
    {
        // The zero-cost "skip ODataQueryOptions entirely" fast path (no $select/$expand) must stay
        // untouched — ValidateNestedTopCeiling only runs inside the hasSelect || hasExpand branch.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/GbCeilingParents(1)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
