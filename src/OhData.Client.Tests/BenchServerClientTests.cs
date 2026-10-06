using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using OhData.ClientTestBench;
using Xunit;

namespace OhData.Client.Tests;

// OhData.Client against the shared bench server: a TPH hierarchy with a collection navigation, typed
// handler rejections and paging. Tests read client output unless marked as a raw-HTTP bench check;
// a Skip'd test is a known client gap, written as the behaviour a conforming client would show.

public sealed class BenchServerClientTests : IAsyncLifetime
{
    private BenchServer _server = null!;
    private OhDataClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = await BenchServer.BuildAsync();
        _client = new OhDataClient(_server.Http);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    // -- TPH ----------------------------------------------------------------------------------

    [Fact]
    public async Task DerivedRow_ByKey_AsItsOwnType_CarriesDerivedMembers()
    {
        AcademyAward? award = await _client.For<AcademyAward>("Awards").Key(1).GetAsync();

        Assert.NotNull(award);
        Assert.Equal("67th Academy Awards", award.Ceremony);
        Assert.True(award.IsWinner);
        Assert.Equal("Best Picture", award.Name);
    }

    [Fact]
    public async Task DerivedRows_UnderExpand_KeepDerivedMembers()
    {
        var rows = await _client.For<AcademyAward>("Awards")
            .Expand("Nominations")
            .OrderBy(a => a.Id)
            .ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Equal("67th Academy Awards", rows[0].Ceremony);
        Assert.Equal(3, rows[0].Nominations.Count);
    }

    [Fact]
    public async Task NestedFilter_UnderExpand_IsApplied()
    {
        var rows = await _client.For<Award>("Awards")
            .Expand("Nominations($filter=contains(Title,'Pulp'))")
            .OrderBy(a => a.Id)
            .ToListAsync();

        Assert.Equal(new[] { 1, 1, 0 }, rows.Select(r => r.Nominations.Count).ToArray());
        Assert.All(rows.SelectMany(r => r.Nominations), n => Assert.Equal("Pulp Fiction", n.Title));
    }

    [Fact]
    public async Task AnnotatedPage_ExposesODataTypeForDerivedRowsOnly()
    {
        var page = await _client.For<Award>("Awards").OrderBy(a => a.Id).ToAnnotatedPageAsync();

        string? TypeOf(int index) =>
            page.Entries[index].Annotations.TryGetValue("@odata.type", out var el) ? el.GetString() : null;

        Assert.Equal("#" + typeof(AcademyAward).FullName, TypeOf(0));
        Assert.Equal("#" + typeof(FestivalAward).FullName, TypeOf(1));
        Assert.Null(TypeOf(2)); // runtime type == declared type, so §4.5.3 does not require it
    }

    [Fact(Skip = "#707: OhData.Client ignores @odata.type")]
    public async Task Collection_OfBaseType_MaterializesEachRowAsItsODataType()
    {
        var rows = await _client.For<Award>("Awards").OrderBy(a => a.Id).ToListAsync();

        Assert.IsType<AcademyAward>(rows[0]);
        Assert.IsType<FestivalAward>(rows[1]);
        Assert.IsType<Award>(rows[2]);
    }

    // -- Typed rejections ----------------------------------------------------------------

    [Fact]
    public async Task Conflict_SurfacesAs409WithItsCodeAndMessage()
    {
        var ex = await Assert.ThrowsAsync<ODataClientException>(
            () => _client.For<Gadget>("Gadgets").InsertAsync(new Gadget { Name = "dup" }));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("DuplicateName", ex.ODataErrorCode);
        Assert.Equal("A gadget named 'dup' exists.", ex.ODataErrorMessage);
    }

    [Fact]
    public async Task PreconditionFailed_SurfacesAs412WithItsCode()
    {
        var ex = await Assert.ThrowsAsync<ODataClientException>(
            () => _client.For<Gadget>("Gadgets").Key(2).PutAsync(new Gadget { Id = 2, Name = "stale" }));

        Assert.Equal(412, ex.StatusCode);
        Assert.Equal("StaleVersion", ex.ODataErrorCode);
        Assert.Equal("The gadget changed.", ex.ODataErrorMessage);
    }

    [Fact]
    public async Task Forbidden_SurfacesAs403WithItsCode()
    {
        var ex = await Assert.ThrowsAsync<ODataClientException>(
            () => _client.For<Gadget>("Gadgets").Key(1).DeleteAsync());

        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("Protected", ex.ODataErrorCode);
        Assert.Equal("Gadget 1 cannot be deleted.", ex.ODataErrorMessage);
    }

    // -- Null property -------------------------------------------------------------------------

    [Fact]
    public async Task EntityWithNullProperty_ReadsFine()
    {
        Gadget? gadget = await _client.For<Gadget>("Gadgets").Key(1).GetAsync();

        Assert.NotNull(gadget);
        Assert.Null(gadget.Note);
    }

    [Fact]
    public async Task NullProperty_RawValueSegment_Is204_AndAPresentOneIs200()
    {
        // Bench smoke check over raw HTTP: the client has no /$value API (client coverage is #709).
        using var absent = await _server.Http.GetAsync("Gadgets(1)/Note/$value");
        using var present = await _server.Http.GetAsync("Gadgets(2)/Note/$value");

        Assert.Equal(HttpStatusCode.NoContent, absent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, present.StatusCode);
        Assert.Equal("note2", await present.Content.ReadAsStringAsync());
    }

    // -- Paging: Prefer: odata.maxpagesize ---------------------------------------------------

    [Fact]
    public async Task MaxPageSize_IsAppliedByTheServer_AndTheClientFollowsTheNextLink()
    {
        // The client has no API to send Prefer; a default header on its HttpClient stands in for one.
        _server.Http.DefaultRequestHeaders.Add("Prefer", "odata.maxpagesize=2");

        using var raw = await _server.Http.GetAsync("Gadgets?$orderby=Id");
        Assert.True(raw.Headers.TryGetValues("Preference-Applied", out var applied));
        Assert.Equal("odata.maxpagesize=2", Assert.Single(applied));

        var page = await _client.For<Gadget>("Gadgets").OrderBy(g => g.Id).ToPageAsync();
        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.NextLink);

        var all = await _client.For<Gadget>("Gadgets").OrderBy(g => g.Id).ToListAsync();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, all.Select(g => g.Id).ToArray());
    }

    // -- 501 for an unimplemented system option ------------------------------------------------------------------

    [Fact]
    public async Task UnimplementedSystemOption_Is501OnTheWire()
    {
        // Raw HTTP bench check; client-side coverage is #710.
        using var raw = await _server.Http.GetAsync("Gadgets?$apply=groupby((Name))");

        Assert.Equal(HttpStatusCode.NotImplemented, raw.StatusCode);
        Assert.Contains("UnsupportedQueryOption", await raw.Content.ReadAsStringAsync());
    }
}
