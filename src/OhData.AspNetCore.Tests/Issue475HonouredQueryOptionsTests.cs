using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Query;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class H475Thing
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal static class H475Data
{
    internal static IQueryable<H475Thing> All() => new[]
    {
        new H475Thing { Id = 1, Name = "Cog" },
        new H475Thing { Id = 2, Name = "Sprocket" },
        new H475Thing { Id = 3, Name = "Bolt" },
    }.AsQueryable();
}

/// <summary>The ordinary shape: the handler just calls <c>ApplyTo</c> and declares nothing.</summary>
public sealed class H475DefaultProfile : ODataEntitySetProfile<int, H475Thing>
{
    public H475DefaultProfile() : base(x => x.Id)
    {
        EntitySetName = "H475Defaults";
        FilterEnabled = true; OrderByEnabled = true; SelectEnabled = true; CountEnabled = true;
        GetODataQueryable = (options, _) => Task.FromResult(new ODataQueryResult<H475Thing>
        {
            Items = options.ApplyTo(H475Data.All()) as IQueryable<H475Thing> ?? H475Data.All(),
        });
    }
}

/// <summary>A handler that really does interpret <c>options.Search</c>, and says so.</summary>
public sealed class H475SearchingProfile : ODataEntitySetProfile<int, H475Thing>
{
    public H475SearchingProfile() : base(x => x.Id)
    {
        EntitySetName = "H475Searchers";
        FilterEnabled = true; OrderByEnabled = true;
        HonouredQueryOptions = OhDataSystemQueryOption.Default | OhDataSystemQueryOption.Search;

        GetODataQueryable = (options, _) =>
        {
            IQueryable<H475Thing> q = H475Data.All();
            if (options.Search?.RawValue is { Length: > 0 } term)
            {
                string needle = term.Trim('"');
                q = q.Where(x => x.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }
            return Task.FromResult(new ODataQueryResult<H475Thing> { Items = q });
        };
    }
}

/// <summary>A handler that reads the options object itself and honours only a subset.</summary>
public sealed class H475NarrowProfile : ODataEntitySetProfile<int, H475Thing>
{
    public H475NarrowProfile() : base(x => x.Id)
    {
        EntitySetName = "H475Narrow";
        FilterEnabled = true; OrderByEnabled = true;
        HonouredQueryOptions = OhDataSystemQueryOption.Filter;

        GetODataQueryable = (options, _) => Task.FromResult(new ODataQueryResult<H475Thing>
        {
            Items = options.Filter is null
                ? H475Data.All()
                : options.Filter.ApplyTo(H475Data.All(), new ODataQuerySettings()) as IQueryable<H475Thing>
                  ?? H475Data.All(),
        });
    }
}

/// <summary>
/// #475 — a Priority-1 route refuses the system query options its profile does not honour.
/// <para>
/// It used to answer <c>200</c> with the full, unfiltered collection for <c>$search</c> when nothing
/// handled it — a failure a client cannot detect, because an unfiltered result is indistinguishable
/// from a search that matched everything. §11.2.5: *"If a data service does not support a system
/// query option, it MUST fail any request that contains the unsupported option"*, and minimal
/// conformance item 7 says the same.
/// </para>
/// <para>
/// <c>Microsoft.AspNetCore.OData</c> ignores it by choice — <c>SearchQueryOption.ApplyTo</c> carries
/// *"If the developer doesn't provide the search binder, let's ignore the $search clause"* — so this
/// is a deliberate divergence, the same one #359/#380/#353 already made where §11.2.5's MUST
/// outweighs aligning with MS.
/// </para>
/// </summary>
public sealed class Issue475HonouredQueryOptionsTests
{
    private static async Task AssertUnsupportedAsync(HttpResponseMessage response, string option)
    {
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        JsonElement error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal("UnsupportedQueryOption", error.GetProperty("code").GetString());
        Assert.Equal($"The query option '{option}' is not supported.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SearchIsRefused_WhenTheProfileDoesNotDeclareIt()
    {
        // The defect: this answered 200 with all three rows.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<H475DefaultProfile>());

        await AssertUnsupportedAsync(
            await fx.Client.GetAsync("/odata/H475Defaults?$search=Cog"), "$search");
    }

    [Fact]
    public async Task TheDefaultDeclaresExactlyWhatApplyToHonours()
    {
        // A profile that just calls ApplyTo sets nothing and still gets the truth: everything ApplyTo
        // applies is accepted, and only $search — which ApplyTo drops without an ISearchBinder — is
        // refused. So the conformance gap closes without anyone enumerating the common case.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<H475DefaultProfile>());

        foreach (string q in new[]
                 {
                     "$filter=Id gt 1", "$orderby=Name", "$top=1", "$skip=1",
                     "$select=Id", "$count=true", "$format=json",
                 })
        {
            var response = await fx.Client.GetAsync("/odata/H475Defaults?" + q);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"'{q}' should be honoured by the default set but answered {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task SearchIsHonoured_WhenTheProfileDeclaresIt()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<H475SearchingProfile>());

        var response = await fx.Client.GetAsync("/odata/H475Searchers?$search=Cog");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement value = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value");
        // Actually filtered, not merely accepted — the whole point of the option.
        Assert.Equal(1, value.GetArrayLength());
        Assert.Equal("Cog", value[0].GetProperty("Name").GetString());
    }

    [Theory]
    [InlineData("$orderby=Name", "$orderby")]
    [InlineData("$top=1", "$top")]
    [InlineData("$select=Id", "$select")]
    [InlineData("$search=Cog", "$search")]
    public async Task ANarrowDeclarationRefusesEverythingElse(string query, string option)
    {
        // Generalises past $search: a profile that honours only $filter has every other option
        // refused, rather than accepted and dropped.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<H475NarrowProfile>());

        await AssertUnsupportedAsync(await fx.Client.GetAsync("/odata/H475Narrow?" + query), option);
    }

    [Fact]
    public async Task ANarrowDeclarationStillHonoursWhatItDeclared()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<H475NarrowProfile>());

        var response = await fx.Client.GetAsync("/odata/H475Narrow?$filter=Id gt 2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task FormatIsAlwaysAccepted_EvenOnTheNarrowestDeclaration()
    {
        // $format is negotiated once on the group filter, never reaches a handler, and cannot change
        // a row — so it is not the profile's to decline.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<H475NarrowProfile>());

        Assert.Equal(HttpStatusCode.OK,
            (await fx.Client.GetAsync("/odata/H475Narrow?$format=json")).StatusCode);
    }

    [Fact]
    public async Task ThePriority2RouteIsUnchanged()
    {
        // The control: this is a Priority-1 change. The GetQueryable route invokes a real Search
        // handler, so $search stays implemented there and its set is still framework-owned.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<SearchableWidgetProfile>());

        var response = await fx.Client.GetAsync("/odata/SearchableWidgets?$search=Cog");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
