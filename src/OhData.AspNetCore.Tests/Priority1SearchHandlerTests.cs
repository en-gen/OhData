using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Query;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #465: the Priority-1 (<c>ODataEntitySetProfile</c> / <c>GetODataQueryable</c>) read path has
/// no <c>$search</c> leg -- no <c>HasSearch</c> gate, no <c>InvokeSearchAsync</c> call -- yet the
/// route's OpenAPI description appended ", $search" whenever the profile happened to have a
/// Search handler. A developer wrote a Search handler, the framework never called it, and the
/// generated documentation told the client search worked: the request returned 200 with the
/// whole, unfiltered set.
///
/// <para>
/// The fix is refusal, not invocation, and the reason is the Priority-1 contract. On the
/// GetQueryable and GetAll paths, Search REPLACES the source collection and the framework then
/// applies $filter/$orderby/$top/$skip on top of the result -- it can, because it owns the
/// pipeline there. Priority-1 inverts that: the profile receives the whole
/// <see cref="ODataQueryOptions{TModel}"/> and applies them itself, so there is no seam to feed
/// a search-derived source into. Honouring $search here would mean bypassing the profile
/// entirely, which would (a) drop $filter/$orderby on exactly the requests that carry $search --
/// the same defect one option over -- and (b) route around any row-level scoping the profile's
/// handler applies, which is a common reason to reach for Priority-1 at all. So $search on this
/// path is the profile's own business, reachable as <c>options.Search</c> inside
/// <c>GetODataQueryable</c>, and a Search handler beside it is dead configuration.
/// </para>
/// </summary>
public class Priority1SearchHandlerTests
{
    [Fact]
    public async Task Priority1ProfileWithSearchHandler_FailsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<P1SearchWidgetProfile>()));

        Assert.Contains("P1SearchWidgets", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Search handler", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GetODataQueryable", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal is scoped to the dead combination: a Priority-1 profile with no Search handler
    /// still maps and serves, and a Search handler on the GetQueryable path is still invoked (the
    /// existing $search coverage in EndpointMappingTests pins that leg).
    /// </summary>
    [Fact]
    public async Task Priority1ProfileWithoutSearchHandler_StartsAndServes()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ODataWidgetProfile>());

        using var response = await fx.Client.GetAsync("/odata/ODataWidgets");
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// #465 fixture: the shared <see cref="Widget"/> model on the Priority-1 read path WITH a Search
/// handler beside it -- the combination the framework now refuses. Deliberately separate from
/// <c>ODataWidgetProfile</c>, which every other Priority-1 test registers and which must keep
/// starting.
/// </summary>
internal class P1SearchWidgetProfile : ODataEntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new()
    {
        new() { Id = 1, Name = "Sprocket" },
        new() { Id = 2, Name = "Cog" },
    };

    public P1SearchWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "P1SearchWidgets";

        GetODataQueryable = (options, ct) =>
        {
            IQueryable<Widget> q = _store.AsQueryable();
            IQueryable<Widget> applied = options.ApplyTo(q) as IQueryable<Widget> ?? q;
            return Task.FromResult(new ODataQueryResult<Widget> { Items = applied });
        };

        Search = (term, ct) => Task.FromResult<IEnumerable<Widget>>(
            _store.Where(w => w.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }
}
