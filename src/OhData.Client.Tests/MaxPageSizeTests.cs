using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace OhData.Client.Tests;

public class MaxPageSizeTests
{
    private sealed class Widget { public int Id { get; set; } }

    private static RecordingHandler TwoPages() => new((req, i) => i == 0
        ? RecordingHandler.Json("{\"value\":[{\"Id\":1}],\"@odata.nextLink\":\"http://localhost/odata/Widgets?$skip=1\"}")
        : RecordingHandler.Json("{\"value\":[{\"Id\":2}]}"));

    [Fact]
    public async Task MaxPageSize_SendsPreferOnTheFirstRequest_AndKeepsSendingItOnNextLinks()
    {
        var handler = TwoPages();
        using var client = RecordingHandler.ClientFor(handler);

        var all = await client.For<Widget>("Widgets").MaxPageSize(1).ToListAsync();

        Assert.Equal(new[] { 1, 2 }, all.Select(w => w.Id).ToArray());
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(new[] { "odata.maxpagesize=1" }, r.Prefer));
    }

    [Fact]
    public async Task MaxPageSize_AnnotatedWalk_AlsoKeepsSendingIt()
    {
        var handler = TwoPages();
        using var client = RecordingHandler.ClientFor(handler);

        var all = new System.Collections.Generic.List<int>();
        await foreach (var e in client.For<Widget>("Widgets").MaxPageSize(1).ToAnnotatedAsyncEnumerable())
            all.Add(e.Entity.Id);

        Assert.Equal(new[] { 1, 2 }, all);
        Assert.All(handler.Requests, r => Assert.Equal(new[] { "odata.maxpagesize=1" }, r.Prefer));
    }

    [Fact]
    public async Task WithoutMaxPageSize_NoPreferHeaderIsSent()
    {
        var handler = TwoPages();
        using var client = RecordingHandler.ClientFor(handler);

        await client.For<Widget>("Widgets").ToListAsync();

        Assert.All(handler.Requests, r => Assert.Empty(r.Prefer));
    }

    [Fact]
    public async Task MaxPageSize_IsImmutable_TheBaseQueryIsUnchanged()
    {
        var handler = TwoPages();
        using var client = RecordingHandler.ClientFor(handler);
        var baseQuery = client.For<Widget>("Widgets");
        _ = baseQuery.MaxPageSize(5);

        await baseQuery.ToPageAsync();

        Assert.Empty(handler.Requests[0].Prefer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void MaxPageSize_BelowOne_Throws(int n)
    {
        using var client = RecordingHandler.ClientFor(TwoPages());
        Assert.Throws<ArgumentOutOfRangeException>(() => client.For<Widget>("Widgets").MaxPageSize(n));
    }

    [Fact]
    public async Task MaxPageSize_KeepsTheCallersDefaultPreferTokens_AndTheQueryValueWins()
    {
        var handler = TwoPages();
        using var client = RecordingHandler.ClientFor(handler, configureHttp: h =>
        {
            h.DefaultRequestHeaders.TryAddWithoutValidation("Prefer", "odata.include-annotations=\"*\", odata.maxpagesize=9");
            h.DefaultRequestHeaders.TryAddWithoutValidation("Prefer", "return=minimal");
        });

        await client.For<Widget>("Widgets").MaxPageSize(1).ToListAsync();

        Assert.All(handler.Requests, r => Assert.Equal(
            new[] { "odata.include-annotations=\"*\", return=minimal, odata.maxpagesize=1" },
            r.Prefer));
    }

    [Fact]
    public async Task DefaultPreferHeader_IsUntouched_WhenNoMaxPageSizeIsRequested()
    {
        var handler = TwoPages();
        using var client = RecordingHandler.ClientFor(handler, configureHttp: h =>
            h.DefaultRequestHeaders.TryAddWithoutValidation("Prefer", "odata.maxpagesize=9"));

        await client.For<Widget>("Widgets").ToPageAsync();

        Assert.Equal(new[] { "odata.maxpagesize=9" }, handler.Requests[0].Prefer);
    }

    [Fact]
    public async Task MaxPageSize_OnTheAnnotatedPage_SendsIt()
    {
        var handler = TwoPages();
        using var client = RecordingHandler.ClientFor(handler);

        await client.For<Widget>("Widgets").MaxPageSize(7).ToAnnotatedPageAsync();

        Assert.Equal(new[] { "odata.maxpagesize=7" }, handler.Requests[0].Prefer);
    }

    [Fact]
    public async Task MaxPageSize_WithCount_SendsNoPrefer()
    {
        var handler = new RecordingHandler((_, _) => RecordingHandler.Text("3"));
        using var client = RecordingHandler.ClientFor(handler);

        Assert.Equal(3, await client.For<Widget>("Widgets").MaxPageSize(2).CountAsync());

        Assert.Empty(handler.Requests[0].Prefer);
    }

    [Fact]
    public async Task MaxPageSize_ServerReportsPreferenceApplied_ToTheClientsRequest()
    {
        await using var server = await OhData.ClientTestBench.BenchServer.BuildAsync();
        var handler = RecordingHandler.Forwarding(server.Http);
        using var client = RecordingHandler.ClientFor(handler, baseAddress: server.Http.BaseAddress);

        var all = await client.For<OhData.ClientTestBench.Gadget>("Gadgets").OrderBy(g => g.Id).MaxPageSize(2).ToListAsync();

        Assert.Equal(5, all.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(new[] { "odata.maxpagesize=2" }, r.ResponseHeaders["Preference-Applied"]));
    }
}
