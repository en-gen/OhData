using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #550 — a profile <c>GetETag</c> selector that throws <c>ODataException</c> must be a logged
/// <c>500</c>, never a <c>400</c> carrying the handler's own message (#496 finding 4b).
/// <para>
/// The issue is filed as REASONED, on the reading that <c>InjectETagsIntoJsonArray</c> calls
/// <c>InvokeGetETag</c> unwrapped while <c>GetById</c> wraps it. The method-internal call really is
/// unwrapped — but the collection route's CALLER wraps the whole stage
/// (<c>ApplyCollectionPipelineAsync</c>, "one try per page, not one closure per row"), and the
/// bound-operation route carries no narrow <c>catch (ODataException)</c> at all, only
/// <c>ODataKeyFormatException</c>. So both arrivals are already correct.
/// </para>
/// <para>
/// This suite exists because that is an argument from reading, and #550's whole point is that the
/// two sibling routes must not disagree. It pins the behaviour on every route that computes an ETag,
/// so the seam cannot silently regress to a <c>400</c> if either wrapper moves.
/// </para>
/// </summary>
public class Issue550EtagSeamProbeTests
{
    private const string Marker = "etag-selector-blew-up";

    [Fact]
    public async Task CollectionGet_ThrowingEtagSelector_Is500_AndNeverLeaksTheHandlersMessage()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<X550Profile>());

        using var resp = await fx.Client.GetAsync("/odata/X550Things");

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Marker, body);
    }

    [Fact]
    public async Task GetById_ThrowingEtagSelector_Is500_AndNeverLeaksTheHandlersMessage()
    {
        // The sibling #496 explicitly wrapped. Asserted alongside the collection route so the two
        // are compared here rather than by reading two call sites 4,000 lines apart.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<X550Profile>());

        using var resp = await fx.Client.GetAsync("/odata/X550Things(1)");

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.DoesNotContain(Marker, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BoundFunctionReturningACollection_ThrowingEtagSelector_Is500()
    {
        // The third arrival: WrapBoundOpResult calls InjectETagsIntoJsonArray unwrapped, and is
        // correct only because the operation routes catch ODataKeyFormatException rather than
        // ODataException. Pinned so that narrowing stays deliberate.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<X550Profile>());

        using var resp = await fx.Client.GetAsync("/odata/X550Things/Recent");

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.DoesNotContain(Marker, await resp.Content.ReadAsStringAsync());
    }
}

internal class X550Thing
{
    public int Id { get; set; }
    public string Name { get; set; } = "n";
}

internal class X550Profile : EntitySetProfile<int, X550Thing>
{
    public X550Profile() : base(x => x.Id)
    {
        EntitySetName = "X550Things";

        // ODataException specifically: it is the one type the read routes' narrow catch converts to
        // a 400 with the message passed through verbatim, which is the disclosure #496 4(b) closes.
        UseETag(x => Boom(x));

        GetAll = _ => Task.FromResult<IEnumerable<X550Thing>>(new[] { new X550Thing { Id = 1 } });
        GetById = (id, _) => Task.FromResult<X550Thing?>(new X550Thing { Id = id });

        BindFunction(Recent);
    }

    // A static method, not a captured lambda: #483 caches a selector that reads only its parameter,
    // and a static is read at invocation time, so this stays on the cached path -- the one the
    // production routes actually use.
    private static string Boom(X550Thing _) =>
        throw new Microsoft.OData.ODataException("etag-selector-blew-up");

    private static Task<IEnumerable<X550Thing>> Recent() =>
        Task.FromResult<IEnumerable<X550Thing>>(new[] { new X550Thing { Id = 1 } });
}
