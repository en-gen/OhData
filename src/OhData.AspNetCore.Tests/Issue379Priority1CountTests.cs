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

public sealed class C379Row
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal static class C379Data
{
    internal static IQueryable<C379Row> All() =>
        Enumerable.Range(1, 25).Select(i => new C379Row { Id = i, Name = "r" + i }).AsQueryable();
}

/// <summary>The canonical Priority-1 shape: ApplyTo, and no TotalCount.</summary>
public sealed class C379NoTotalProfile : ODataEntitySetProfile<int, C379Row>
{
    public C379NoTotalProfile() : base(x => x.Id)
    {
        EntitySetName = "C379NoTotal";
        FilterEnabled = OrderByEnabled = CountEnabled = SelectEnabled = true;

        GetODataQueryable = (options, _) => Task.FromResult(new ODataQueryResult<C379Row>
        {
            Items = options.ApplyTo(C379Data.All()) as IQueryable<C379Row> ?? C379Data.All(),
        });
    }
}

/// <summary>The same, but supplying the pre-paging total — the documented remedy.</summary>
public sealed class C379WithTotalProfile : ODataEntitySetProfile<int, C379Row>
{
    public C379WithTotalProfile() : base(x => x.Id)
    {
        EntitySetName = "C379WithTotal";
        FilterEnabled = OrderByEnabled = CountEnabled = SelectEnabled = true;

        GetODataQueryable = (options, _) => Task.FromResult(new ODataQueryResult<C379Row>
        {
            Items = options.ApplyTo(C379Data.All()) as IQueryable<C379Row> ?? C379Data.All(),
            TotalCount = C379Data.All().LongCount(),
        });
    }
}

/// <summary>The other remedy: declare that this resource does not honour $count at all.</summary>
public sealed class C379NoCountProfile : ODataEntitySetProfile<int, C379Row>
{
    public C379NoCountProfile() : base(x => x.Id)
    {
        EntitySetName = "C379NoCount";
        FilterEnabled = OrderByEnabled = SelectEnabled = true;
        HonouredQueryOptions = OhDataSystemQueryOption.Filter | OhDataSystemQueryOption.OrderBy |
                               OhDataSystemQueryOption.Top | OhDataSystemQueryOption.Skip |
                               OhDataSystemQueryOption.Select;

        GetODataQueryable = (options, _) => Task.FromResult(new ODataQueryResult<C379Row>
        {
            Items = options.ApplyTo(C379Data.All()) as IQueryable<C379Row> ?? C379Data.All(),
        });
    }
}

/// <summary>
/// #379 — a Priority-1 profile that omits <c>TotalCount</c> no longer reports the PAGE length as
/// <c>@odata.count</c>.
/// <para>
/// The fallback was <c>TotalCount ?? items.Length</c>, and <c>items</c> is the page — measured after
/// the framework's own <c>Take</c> cap. On <c>MaxTop = 50</c> over 10,000 rows that reported
/// <c>"@odata.count": 50</c> under a <c>200</c>, so a paging UI computed one page instead of 200.
/// §11.2.6.5 wants the count of items matching the request, explicitly unaffected by
/// <c>$top</c>/<c>$skip</c>.
/// </para>
/// <para>
/// The condition is MEASURED rather than refused wholesale, which is why it went unnoticed for so
/// long: with no <c>$top</c>, no <c>$skip</c> and no cap, <c>items</c> IS the filtered set and the
/// number is right. Refusing every countless profile would have broken the canonical Priority-1
/// shape — <c>ApplyTo</c> + <c>Items</c>, no <c>TotalCount</c> — on every <c>$count</c> request.
/// </para>
/// </summary>
public sealed class Issue379Priority1CountTests
{
    private static async Task<JsonElement> OkAsync(TestFixture fx, string url)
    {
        var response = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData("$count=true&$top=5")]
    [InlineData("$count=true&$skip=20")]
    public async Task PagedWithoutTotalCount_FailsLoud_InsteadOfReportingThePageLength(string query)
    {
        // The defect: $top=5 reported "@odata.count": 5 over 25 rows, under a 200.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<C379NoTotalProfile>());

        var response = await fx.Client.GetAsync("/odata/C379NoTotal?" + query);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"@odata.count\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnpagedWithoutTotalCount_IsUnchanged()
    {
        // The preserved case, and the reason this is narrowed rather than blanket: nothing paged, so
        // items IS the filtered set and its length is the honest total.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<C379NoTotalProfile>());

        JsonElement body = await OkAsync(fx, "/odata/C379NoTotal?$count=true");

        Assert.Equal(25L, body.GetProperty("@odata.count").GetInt64());
    }

    [Fact]
    public async Task SupplyingTotalCount_ReportsTheTrueTotalUnderPaging()
    {
        // Remedy 1, and the assertion that matters: the count is the PRE-paging total, not the page.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<C379WithTotalProfile>());

        JsonElement body = await OkAsync(fx, "/odata/C379WithTotal?$count=true&$top=5");

        Assert.Equal(25L, body.GetProperty("@odata.count").GetInt64());
        Assert.Equal(5, body.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task DeclaringCountUnhonoured_Refuses501_RatherThanFailing()
    {
        // Remedy 2, which only exists because of #475: a profile that genuinely cannot count says so,
        // and $count is then refused before it ever reaches the count site.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<C379NoCountProfile>());

        var response = await fx.Client.GetAsync("/odata/C379NoCount?$count=true&$top=5");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        JsonElement error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal("UnsupportedQueryOption", error.GetProperty("code").GetString());
    }
}
