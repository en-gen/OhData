using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #196: the main collection GET routes must <em>reject</em> system query options the framework
/// does not implement (<c>$apply</c>, <c>$compute</c>, <c>$index</c>, <c>$deltatoken</c>) with
/// <c>400 UnsupportedQueryOption</c>, rather than parsing the request and silently ignoring them.
/// The navigation-collection route already did this; the main route did not. Ignoring a known
/// option violates OData Minimal-conformance item 7 ("parse the option or reject the request").
/// The rejection lives in the shared capability gate, so it applies uniformly to all three
/// collection read paths: <c>GetAll</c>, <c>GetQueryable</c>, and Priority-1 (<c>GetODataQueryable</c>).
/// </summary>
public class UnimplementedQueryOptionTests
{
    // GetAll → /odata/Widgets, GetQueryable → /odata/UnimplQueryableWidgets, Priority-1 → /odata/ODataWidgets.
    [Theory]
    [InlineData("/odata/Widgets", "$apply", "groupby((Name))")]
    [InlineData("/odata/Widgets", "$compute", "1 add 1 as Two")]
    [InlineData("/odata/Widgets", "$index", "0")]
    [InlineData("/odata/Widgets", "$deltatoken", "abc")]
    [InlineData("/odata/UnimplQueryableWidgets", "$apply", "groupby((Name))")]
    [InlineData("/odata/UnimplQueryableWidgets", "$compute", "1 add 1 as Two")]
    [InlineData("/odata/UnimplQueryableWidgets", "$index", "0")]
    [InlineData("/odata/UnimplQueryableWidgets", "$deltatoken", "abc")]
    [InlineData("/odata/ODataWidgets", "$apply", "groupby((Name))")]
    [InlineData("/odata/ODataWidgets", "$compute", "1 add 1 as Two")]
    [InlineData("/odata/ODataWidgets", "$index", "0")]
    [InlineData("/odata/ODataWidgets", "$deltatoken", "abc")]
    public async Task UnimplementedOption_OnAnyCollectionPath_Returns400(string url, string option, string value)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddEntitySetProfile<WidgetProfile>()
            .AddEntitySetProfile<UnimplQueryableProfile>()
            .AddEntitySetProfile<ODataWidgetProfile>());

        var resp = await fx.Client.GetAsync($"{url}?{option}={System.Uri.EscapeDataString(value)}");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UnsupportedQueryOption", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(option, body.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/odata/Widgets")]
    [InlineData("/odata/UnimplQueryableWidgets")]
    [InlineData("/odata/ODataWidgets")]
    public async Task ImplementedOptions_StillSucceed_NotOverBlocked(string url)
    {
        // Control: an implemented option ($top) on the same routes must not be caught by the
        // unimplemented-option gate — the guard is scoped to the four unsupported options only.
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddEntitySetProfile<WidgetProfile>()
            .AddEntitySetProfile<UnimplQueryableProfile>()
            .AddEntitySetProfile<ODataWidgetProfile>());

        var resp = await fx.Client.GetAsync($"{url}?$top=1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}

/// <summary>Minimal <c>GetQueryable</c> (Priority-2) profile for the #196 collection-path matrix.</summary>
internal class UnimplQueryableProfile : EntitySetProfile<int, Widget>
{
    private static readonly List<Widget> Store = new()
    {
        new() { Id = 1, Name = "Sprocket" },
        new() { Id = 2, Name = "Cog" },
    };

    public UnimplQueryableProfile() : base(x => x.Id)
    {
        EntitySetName = "UnimplQueryableWidgets";
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
    }
}
