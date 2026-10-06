using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using OhData.ClientTestBench;
using Xunit;

namespace OhData.Client.Tests;

public sealed class WithQueryOptionTests(BenchServerFixture bench) : IClassFixture<BenchServerFixture>
{
    private sealed class Widget { public int Id { get; set; } }

    private static RecordingHandler Empty() => new((_, _) => RecordingHandler.Json("{\"value\":[]}"));

    [Fact]
    public async Task Option_IsAppendedUrlEncoded_AfterTheComposedOptions()
    {
        var handler = Empty();
        using var client = RecordingHandler.ClientFor(handler);

        await client.For<Widget>("Widgets").Top(5)
            .WithQueryOption("$search", "red & blue")
            .WithQueryOption("tenant id", "a/b")
            .ToListAsync();

        Assert.Equal(
            "http://localhost/odata/Widgets?$top=5&$search=red%20%26%20blue&tenant%20id=a%2Fb",
            handler.Requests[0].Url);
    }

    [Fact]
    public async Task Option_AloneOnTheQuery_BuildsTheQueryString()
    {
        var handler = Empty();
        using var client = RecordingHandler.ClientFor(handler);

        await client.For<Widget>("Widgets").WithQueryOption("custom", "1").ToListAsync();

        Assert.Equal("http://localhost/odata/Widgets?custom=1", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Option_IsAlsoSentOnTheCountRoute()
    {
        var handler = new RecordingHandler((_, _) => RecordingHandler.Text("0"));
        using var client = RecordingHandler.ClientFor(handler);

        await client.For<Widget>("Widgets").Filter(w => w.Id > 1).WithQueryOption("$search", "x").CountAsync();

        Assert.Equal("http://localhost/odata/Widgets/$count?$filter=Id%20gt%201&$search=x", handler.Requests[0].Url);
    }

    [Fact]
    public void Option_IsImmutable_TheBaseQueryIsUnchanged()
    {
        using var client = RecordingHandler.ClientFor(Empty());
        var baseQuery = client.For<Widget>("Widgets");
        _ = baseQuery.WithQueryOption("a", "b");

        Assert.Equal("Widgets", baseQuery.BuildCollectionUrl());
    }

    [Theory]
    [InlineData("$filter")]
    [InlineData("$select")]
    [InlineData("$expand")]
    [InlineData("$orderby")]
    [InlineData("$top")]
    [InlineData("$skip")]
    [InlineData("$count")]
    [InlineData("$FILTER")]
    [InlineData("$Top")]
    [InlineData(" $filter")]
    [InlineData("\t$top\n")]
    public void ClientComposedDollarNames_AreRefused_CaseInsensitively_AndTrimmed(string name)
    {
        using var client = RecordingHandler.ClientFor(Empty());
        var ex = Assert.Throws<ArgumentException>(() => client.For<Widget>("Widgets").WithQueryOption(name, "x"));
        Assert.Equal("name", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankName_IsRefused(string name)
    {
        using var client = RecordingHandler.ClientFor(Empty());
        Assert.Throws<ArgumentException>(() => client.For<Widget>("Widgets").WithQueryOption(name, "x"));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        using var client = RecordingHandler.ClientFor(Empty());
        Assert.Throws<ArgumentNullException>(() => client.For<Widget>("Widgets").WithQueryOption(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => client.For<Widget>("Widgets").WithQueryOption("a", null!));
    }

    [Fact]
    public async Task NameIsTrimmed_BeforeItIsSent()
    {
        var handler = Empty();
        using var client = RecordingHandler.ClientFor(handler);

        await client.For<Widget>("Widgets").WithQueryOption(" $search ", "x").ToListAsync();

        Assert.Equal("http://localhost/odata/Widgets?$search=x", handler.Requests[0].Url);
    }

    [Fact]
    public async Task BareSystemNames_AreCustomOptions_AndAreSentAsGiven()
    {
        var handler = Empty();
        using var client = RecordingHandler.ClientFor(handler);

        await client.For<Widget>("Widgets").WithQueryOption("filter", "x").ToListAsync();

        Assert.Equal("http://localhost/odata/Widgets?filter=x", handler.Requests[0].Url);
    }

    // -- carried across Key(...): every keyed request ----------------------------------------------

    private sealed class Doc
    {
        public int Id { get; set; }
        public string? Title { get; set; }
    }

    [Fact]
    public async Task Options_AreCarriedAcrossKey_OnEveryKeyedRequest()
    {
        var handler = new RecordingHandler((req, _) => req.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : req.RequestUri!.AbsolutePath.EndsWith("/$value", StringComparison.Ordinal)
                ? RecordingHandler.Text("raw")
                : req.RequestUri.AbsolutePath.EndsWith("/Title", StringComparison.Ordinal)
                    ? RecordingHandler.Json("{\"value\":\"t\"}")
                    : RecordingHandler.Json("{\"Id\":1,\"Title\":\"t\"}"));
        using var client = RecordingHandler.ClientFor(handler);
        var keyed = client.For<Doc>("Docs").Select("Id").WithQueryOption("tenant", "a b").Key(1);

        await keyed.GetAsync();
        await keyed.GetAnnotatedAsync();
        await keyed.GetWithETagAsync();
        await keyed.GetIfChangedAsync("\"x\"");
        await keyed.GetPropertyAsync(d => d.Title);
        await keyed.GetRawValueAsync(d => d.Title);
        await keyed.PutAsync(new Doc { Id = 1 });
        await keyed.PatchAsync(new { Title = "n" });
        await keyed.DeleteAsync();

        Assert.Equal(
            new[]
            {
                "http://localhost/odata/Docs(1)?$select=Id&tenant=a%20b",
                "http://localhost/odata/Docs(1)?$select=Id&tenant=a%20b",
                "http://localhost/odata/Docs(1)?$select=Id&tenant=a%20b",
                "http://localhost/odata/Docs(1)?$select=Id&tenant=a%20b",
                "http://localhost/odata/Docs(1)/Title?tenant=a%20b",
                "http://localhost/odata/Docs(1)/Title/$value?tenant=a%20b",
                "http://localhost/odata/Docs(1)?$select=Id&tenant=a%20b",
                "http://localhost/odata/Docs(1)?$select=Id&tenant=a%20b",
                "http://localhost/odata/Docs(1)?$select=Id&tenant=a%20b",
            },
            handler.Requests.ConvertAll(r => r.Url));
    }

    [Fact]
    public async Task OptionsAlone_AcrossKey_BuildTheQueryString()
    {
        var handler = new RecordingHandler((_, _) => RecordingHandler.Json("{\"Id\":1}"));
        using var client = RecordingHandler.ClientFor(handler);

        await client.For<Doc>("Docs").WithQueryOption("$search", "x").Key(1).GetAsync();

        Assert.Equal("http://localhost/odata/Docs(1)?$search=x", handler.Requests[0].Url);
    }

    // -- through a server-issued nextLink -------------------------------------------------------------

    [Fact]
    public async Task Option_SurvivesARealServerNextLinkWalk()
    {
        await using var fixture = await PaginatedClientTestFixture.BuildAsync();
        var handler = RecordingHandler.Forwarding(fixture.Http);
        using var client = RecordingHandler.ClientFor(handler, baseAddress: fixture.Http.BaseAddress);

        var all = await client.For<OhData.Client.Tests.Widget>("PaginatedWidgets")
            .OrderBy(w => w.Id).WithQueryOption("ohdata-test", "1").ToListAsync();

        Assert.Equal(10, all.Count);
        Assert.True(handler.Requests.Count >= 4);
        Assert.All(handler.Requests, r => Assert.Contains("ohdata-test=1", r.Url));
    }

    [Fact]
    public async Task Option_IsNotAppendedToAServerIssuedNextLink()
    {
        const string nextLink = "http://localhost/odata/Widgets?$skip=1";
        var handler = new RecordingHandler((_, i) => i == 0
            ? RecordingHandler.Json("{\"value\":[{\"Id\":1}],\"@odata.nextLink\":\"" + nextLink + "\"}")
            : RecordingHandler.Json("{\"value\":[{\"Id\":2}]}"));
        using var client = RecordingHandler.ClientFor(handler);

        var all = await client.For<Widget>("Widgets")
            .WithQueryOption("ohdata-test", "1").ToListAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("http://localhost/odata/Widgets?ohdata-test=1", handler.Requests[0].Url);
        Assert.Equal(nextLink, handler.Requests[1].Url);
    }

    // -- against the bench server -----------------------------------------------------------

    [Fact]
    public async Task UnimplementedSystemOption_ThroughTheClient_Is501WithItsCode()
    {
        var ex = await Assert.ThrowsAsync<ODataClientException>(
            () => bench.Client.For<Gadget>("Gadgets").WithQueryOption("$apply", "groupby((Name))").ToListAsync());

        Assert.Equal(501, ex.StatusCode);
        Assert.Equal("UnsupportedQueryOption", ex.ODataErrorCode);
    }

    [Fact]
    public async Task CustomNonDollarOption_ReachesTheServer_AndIsNotRefused()
    {
        var rows = await bench.Client.For<Gadget>("Gadgets").OrderBy(g => g.Id).Top(2)
            .WithQueryOption("ohdata-test", "1")
            .ToListAsync();

        Assert.Equal(2, rows.Count);
    }
}
