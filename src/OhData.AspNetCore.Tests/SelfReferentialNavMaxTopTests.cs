using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #296: a nested $top against a SELF-REFERENTIAL navigation was rejected by MS's
// SelectExpandQueryValidator (model-bound MaxTop defaults to 0 for such a type) before any of OhData's
// own logic ran. Fixed in MarkNavigationTargetTypesFullyQueryable, which now clears model-bound MaxTop
// on root+nav-target types too.
//
// SrnNodeProfile's Children is DELEGATE-BACKED, and #294 makes a delegate-backed navigation REJECT a
// nested $top/$skip with 400 rather than silently ignoring it -- the delegate owns its query shape.
// So on THIS shape lifting #296's pre-rejection does not make an in-range $top succeed; it lets
// OhData's own checks run instead of MS's, and #294's reject is one of them. See
// SharedNavTargetTypePushdownTests for the delegate-LESS case, where #296 alone is sufficient.
//
// Pins that an in-range nested $top 400s via #294 (not #296's old model-bound 400, and not the
// silent-wrong-data 200 that made #296 unshippable alone), and that an over-ceiling one still 400s via
// ValidateNestedTopCeiling, which runs first -- so the two keep distinct messages under one code.
public sealed class SrnNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public List<SrnNode> Children { get; set; } = new();
}

public sealed class SrnNodeProfile : EntitySetProfile<int, SrnNode>
{
    private static readonly List<SrnNode> _nodes = new()
    {
        new() { Id = 1, Name = "Root", ParentId = null },
        new() { Id = 2, Name = "A", ParentId = 1 },
        new() { Id = 3, Name = "B", ParentId = 1 },
        new() { Id = 4, Name = "C", ParentId = 1 },
    };

    public SrnNodeProfile() : base(x => x.Id)
    {
        EntitySetName = "SrnNodes";
        ExpandEnabled = true;
        FilterEnabled = true;
        // Root-level MaxTop: governs GET /SrnNodes?$top=N. Deliberately different from MaxExpandTop
        // below so the two ceilings can't be confused with each other in the assertions.
        MaxTop = 10;
        // Nested-expand ceiling: governs $expand=Children($top=N). Deliberately small (2) so an
        // over-ceiling nested $top (3) can be distinguished from a within-ceiling one (2).
        MaxExpandTop = 2;

        GetQueryable = () => _nodes.AsQueryable();
        // Delegate-backed (getAll:) — deliberately so, to exercise #294's reject on a
        // self-referential navigation (RootTop_*/pushdown coverage for the delegate-LESS shared-type
        // case lives in SharedNavTargetTypePushdownTests.cs; duplicating it here against the identical
        // branch would just be the same invariant twice).
        HasMany(x => x.Children,
            getAll: (parentId, ct) =>
                Task.FromResult<IEnumerable<SrnNode>>(
                    _nodes.Where(n => n.ParentId == parentId).OrderBy(n => n.Id)));
    }
}

public sealed class SelfReferentialNavMaxTopTests : IAsyncLifetime
{
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _fx = await TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<SrnNodeProfile>());
    }

    public async Task DisposeAsync() => await _fx.DisposeAsync();

    [Fact]
    public async Task NestedTop_WithinMaxExpandTop_OnDelegateBackedSelfReferentialNav_IsApplied()
    {
        // HISTORY, because this assertion has now been three things. Before #294: 200 with all 3
        // children and $top=2 silently ignored -- the wrong-data bug. #294: 400, because a delegate's
        // answer could not be re-windowed by the framework. #650: 200 with the window APPLIED, because
        // it can now -- ExpandLevelAsync windows the materialized children after counting them.
        //
        // The $top=2 is WITHIN MaxExpandTop, so the ceiling check above it does not fire; that
        // over-ceiling case is still a 400 and is pinned by the test below, unchanged.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/SrnNodes?$filter=id eq 1&$expand=Children($top=2)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement children = doc.RootElement.GetProperty("value")[0].GetProperty("Children");
        Assert.Equal(2, children.GetArrayLength());   // 3 available, windowed to the requested 2
    }

    [Fact]
    public async Task NestedTop_AboveMaxExpandTop_OnSelfReferentialNav_Returns400()
    {
        // OhData's own MaxExpandTop ceiling (ValidateNestedTopCeiling) runs BEFORE #294's delegate
        // reject in the request pipeline and still bites here -- an over-ceiling nested $top 400s for
        // the pre-existing reason (InvalidQueryOption, "exceeds the maximum allowed value"), not
        // #294's "not supported on the delegate-backed navigation" message (both share the
        // InvalidQueryOption code now), since the ceiling check short-circuits first regardless of
        // whether the navigation is delegate-backed.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/SrnNodes?$filter=id eq 1&$expand=Children($top=3)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Children", body);
        Assert.Contains("(3)", body);
        Assert.Contains("(2)", body);
    }
}
