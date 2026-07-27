using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #296: a nested $top inside $expand against a SELF-REFERENTIAL navigation (the navigation's target
// type is ALSO its own root entity set) used to be rejected outright by Microsoft's
// SelectExpandQueryValidator (model-bound MaxTop defaulted to 0 for such a type), before OhData's own
// logic -- including its MaxExpandTop ceiling -- ever got a chance to run. Fixed in
// OhDataBuilder.MarkNavigationTargetTypesFullyQueryable, which now clears the model-bound MaxTop on a
// root+nav-target type too (previously only pure nav-target-only types got this treatment).
//
// SrnNodeProfile's Children navigation is DELEGATE-BACKED (HasMany(..., getAll:)). #294 (owner
// decision, filed against the silent-wrong-data this exact combination produced during #296's review:
// $top=2 silently returning all 3 children under an unsuspicious 200) means a delegate-backed
// navigation now REJECTS any nested $top/$skip with 400 InvalidQueryOption instead of silently
// ignoring it -- the delegate owns its own query shape and nothing downstream re-windows its answer.
// So on THIS delegate-backed shape, lifting #296's model-bound pre-rejection does NOT make an in-range
// nested $top succeed; it only lets OhData's OWN checks run instead of Microsoft's model-bound one --
// and #294's reject is one of those checks. See SharedNavTargetTypePushdownTests.cs for the
// delegate-LESS (pushdown) shared-type case, where #296 alone is sufficient and the nested $top IS
// honored/windowed with a real 200.
//
// This file proves:
//   1. an in-range nested $top against a delegate-backed self-referential nav 400s via #294's reject
//      (InvalidQueryOption, thrown as Microsoft.OData.ODataException and caught by the route's
//      existing handler) -- not #296's old model-bound 400, and not the silent-wrong-data 200
//      that made #296 unshippable on its own;
//   2. a nested $top ABOVE MaxExpandTop still 400s via the pre-existing ceiling check
//      (ValidateNestedTopCeiling), which runs BEFORE #294's reject and takes precedence, so the
//      over-ceiling case keeps its own "exceeds the maximum allowed value" message rather than
//      #294's "not supported on the delegate-backed navigation" one (both now share the same
//      InvalidQueryOption code, so the message content is what distinguishes them in the test).
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

        GetQueryable = _ => Task.FromResult(_nodes.AsQueryable());
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
    public async Task NestedTop_WithinMaxExpandTop_OnDelegateBackedSelfReferentialNav_Rejected400()
    {
        // #294: before that fix this used to succeed with 200 and return all 3 children regardless of
        // $top=2 -- the silent-wrong-data bug the adversarial review of #296 flagged, which is exactly
        // why #294 and #296 are shipped together. A delegate-backed navigation cannot be safely
        // re-windowed by the framework, so a nested $top/$skip against one now throws
        // Microsoft.OData.ODataException (ExpandLevelAsync), caught by the route's existing handler
        // and surfaced as 400 InvalidQueryOption, instead of silently ignored.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/SrnNodes?$filter=id eq 1&$expand=Children($top=2)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Children", body);
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
