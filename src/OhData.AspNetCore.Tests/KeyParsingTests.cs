using System.Net;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

public class KeyParsingTests
{
    [Fact]
    public async Task IntKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets(1)");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task IntKey_Missing_Returns404()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets(9999)");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GuidKey_ParsedFromRoute()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<GadgetProfile>());
        var response = await fx.Client.GetAsync($"/odata/Gadgets({GadgetProfile.KnownId})");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StringKey_StrippedOfQuotes()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ThingProfile>());
        var response = await fx.Client.GetAsync("/odata/Things('alpha')");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BadIntKey_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/Widgets(notanint)");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Supporting fixtures ────────────────────────────────────────────────────

    private class Gadget { public Guid Id { get; set; } }

    private class GadgetProfile : EntitySetProfile<Guid, Gadget>
    {
        public static readonly Guid KnownId = Guid.NewGuid();
        private static readonly List<Gadget> Store = new() { new() { Id = KnownId } };

        public GadgetProfile() : base(x => x.Id)
        {
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(g => g.Id == id));
        }
    }

    private class Thing { public string Id { get; set; } = ""; }

    private class ThingProfile : EntitySetProfile<string, Thing>
    {
        private static readonly List<Thing> Store = new() { new() { Id = "alpha" } };

        public ThingProfile() : base(x => x.Id)
        {
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(t => t.Id == id));
        }
    }
}
