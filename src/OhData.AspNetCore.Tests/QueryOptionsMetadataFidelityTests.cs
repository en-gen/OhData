using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #467: <see cref="OhDataQueryOptionsMetadata"/> is the SINGLE fix site behind three OpenAPI
/// companion packages, which is why the same two defects appeared byte-for-byte in all three
/// generated documents. Each package has its own end-to-end coverage; this suite pins the
/// upstream metadata itself, so a future route shape that gets the fields wrong fails here
/// rather than three packages downstream.
///
/// <para>
/// Every field means "this route honours this option". The two defects were both a field
/// meaning something else: $top/$skip were added by the transformers on metadata *presence*
/// alone (the metadata is on five route shapes, not just the paged collection GETs), and
/// <c>CountEnabled</c> was set on <c>/$count</c> to mean "this route IS a count" while its only
/// consumers read it as "this route documents the $count option".
/// </para>
/// </summary>
public class QueryOptionsMetadataFidelityTests
{
    // ── The two routes that ignore $top/$skip ─────────────────────────────────

    [Fact]
    public async Task GetByIdRoute_MetadataSaysTopSkipUnsupported()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<QomAllOnProfile>());
        OhDataQueryOptionsMetadata meta = MetadataFor(fx, "/odata/QomWidgets({key})", "GET");

        Assert.False(meta.TopSkipSupported);
        Assert.False(meta.CountEnabled);
        Assert.True(meta.SelectEnabled);
        Assert.True(meta.ExpandEnabled);
    }

    [Fact]
    public async Task CountRoute_MetadataSaysTopSkipAndCountUnsupported()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<QomAllOnProfile>());
        OhDataQueryOptionsMetadata meta = MetadataFor(fx, "/odata/QomWidgets/$count", "GET");

        Assert.False(meta.TopSkipSupported);
        Assert.False(meta.CountEnabled);
        // $filter IS live here: this profile has GetQueryable, so the route applies it.
        Assert.True(meta.FilterEnabled);
    }

    // ── The three collection GETs, which do page ──────────────────────────────

    [Fact]
    public async Task CollectionGetQueryableRoute_MetadataSaysTopSkipSupported()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<QomAllOnProfile>());
        Assert.True(MetadataFor(fx, "/odata/QomWidgets/", "GET").TopSkipSupported);
    }

    [Fact]
    public async Task CollectionGetAllRoute_MetadataSaysTopSkipSupported()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<QomGetAllFilterOnProfile>());
        Assert.True(MetadataFor(fx, "/odata/QomGetAllWidgets/", "GET").TopSkipSupported);
    }

    [Fact]
    public async Task CollectionPriority1Route_MetadataSaysTopSkipSupportedAndSearchUnsupported()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ODataWidgetProfile>());
        OhDataQueryOptionsMetadata meta = MetadataFor(fx, "/odata/ODataWidgets/", "GET");

        Assert.True(meta.TopSkipSupported);
        // #465: the Priority-1 route has no $search leg, so it never advertises one.
        Assert.False(meta.SearchEnabled);
    }

    // ── $filter on /$count follows the SOURCE, not the flag ───────────────────

    /// <summary>
    /// #467 (F3): the GetAll fallback branch of the /$count handler answers 501
    /// UnsupportedQueryOption for any $filter regardless of <c>FilterEnabled</c> -- there is no
    /// IQueryable to apply one to. The advertisement must follow the source. The live request is
    /// asserted alongside the metadata so the two can never drift apart again.
    /// </summary>
    [Fact]
    public async Task CountRoute_GetAllOnlySource_MetadataDeniesFilterAndServerRejectsIt()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<QomGetAllFilterOnProfile>());

        OhDataQueryOptionsMetadata meta = MetadataFor(fx, "/odata/QomGetAllWidgets/$count", "GET");
        Assert.False(meta.FilterEnabled);

        using var response = await fx.Client.GetAsync("/odata/QomGetAllWidgets/$count?$filter=Name eq 'Cog'");
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static OhDataQueryOptionsMetadata MetadataFor(TestFixture fx, string routePattern, string httpMethod)
    {
        EndpointDataSource source = fx.App.Services.GetRequiredService<EndpointDataSource>();
        List<RouteEndpoint> matches = source.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => string.Equals(e.RoutePattern.RawText, routePattern, StringComparison.Ordinal)
                        && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(httpMethod) ?? false))
            .ToList();

        if (matches.Count != 1)
        {
            string available = string.Join(", ", source.Endpoints.OfType<RouteEndpoint>()
                .Select(e => e.RoutePattern.RawText));
            throw new Xunit.Sdk.XunitException(
                $"Expected exactly one {httpMethod} endpoint at '{routePattern}', found {matches.Count}. " +
                $"Available: {available}");
        }

        OhDataQueryOptionsMetadata? meta = matches[0].Metadata.GetMetadata<OhDataQueryOptionsMetadata>();
        Assert.NotNull(meta);
        return meta!;
    }
}

// ── Fixtures ─────────────────────────────────────────────────────────────────
//
// Both reuse the shared Widget model; they exist to give this suite one profile per read path
// with the capability flags turned up, rather than to model anything new.

internal class QomAllOnProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new() { new() { Id = 1, Name = "Cog" } };

    public QomAllOnProfile() : base(x => x.Id)
    {
        EntitySetName = "QomWidgets";
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;
        GetQueryable = (ct) => Task.FromResult(_store.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
    }
}

internal class QomGetAllFilterOnProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> _store = new() { new() { Id = 1, Name = "Cog" } };

    public QomGetAllFilterOnProfile() : base(x => x.Id)
    {
        EntitySetName = "QomGetAllWidgets";
        FilterEnabled = true;
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store);
    }
}
