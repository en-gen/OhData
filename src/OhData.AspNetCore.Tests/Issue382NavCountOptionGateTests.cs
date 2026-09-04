using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #382 — <c>GET /{Set}({key})/{Nav}/$count</c> validates its query options.
/// <para>
/// The reported defect: the route performed <i>no</i> validation at all, so
/// <c>?$filter=…</c> returned the <b>unfiltered</b> count under a <c>200</c> while the sibling
/// navigation route refused the same option one segment away. A wrong number under a success status,
/// the same class as #353 and #354.
/// </para>
/// <para>
/// <b>#359 fixed it, and nothing pinned it.</b> That change narrowed both <c>/$count</c> implemented
/// sets, and <c>s_navCountImplementedOptions</c> excludes <c>$filter</c> because this handler invokes
/// the navigation delegate and counts the result — it can apply neither <c>$filter</c> nor
/// <c>$search</c>, so it refuses both. No test asserted any of that, which is why the issue stayed
/// open against code that had already changed. These are that test.
/// </para>
/// </summary>
public sealed class Issue382NavCountOptionGateTests
{
    private const string Count = "/odata/NavCountParents(1)/Children/$count";

    private static async Task AssertUnsupportedAsync(HttpResponseMessage response, string option)
    {
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        JsonElement error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal("UnsupportedQueryOption", error.GetProperty("code").GetString());
        Assert.Contains(option, error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$filter", "Id gt 10")]   // THE defect: 200 with the unfiltered count
    [InlineData("$search", "ChildA")]     // same reason -- the handler counts a delegate result
    public async Task AnOptionThatWouldChangeTheCount_IsRefused(string option, string value)
    {
        // §11.2.9: the count MUST be "of items matching the request after applying any $filter or
        // $search". This route can apply neither, so ignoring one would answer a confidently wrong
        // number under a 200 -- which is exactly what it did.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());

        var response = await fx.Client.GetAsync($"{Count}?{option}={Uri.EscapeDataString(value)}");

        await AssertUnsupportedAsync(response, option);
    }

    [Theory]
    [InlineData("$top=1")]
    [InlineData("$skip=1")]
    [InlineData("$orderby=Id")]
    [InlineData("$select=Id")]
    [InlineData("$expand=Nothing")]
    [InlineData("$format=json")]
    public async Task AnOptionThatMustNotAffectTheCount_IsAcceptedAndIgnored(string query)
    {
        // The other half of §11.2.9, and the half a status-only assertion would miss: these are
        // accepted AND must leave the number alone. The clause says the count "MUST NOT be affected
        // by $top, $skip, $orderby, or $expand", so asserting 200 without asserting the VALUE would
        // pass for a route that honoured them.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());

        var response = await fx.Client.GetAsync($"{Count}?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2", (await response.Content.ReadAsStringAsync()).Trim());
    }

    [Theory]
    [InlineData("$apply=groupby((Id))", "$apply")]
    [InlineData("$unknown=1", "$unknown")]
    [InlineData("$count=true", "$count")]
    public async Task AnOptionOutsideTheClause_IsRefusedBySigil(string query, string option)
    {
        // #359's sigil rule: anything outside §11.2.9's two classes is refused whether or not any
        // OData version defines it.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());

        await AssertUnsupportedAsync(await fx.Client.GetAsync($"{Count}?{query}"), option);
    }

    [Theory]
    [InlineData("$filter=Id gt 10")]
    [InlineData("$search=ChildA")]
    public async Task TheSiblingNavigationRouteAgrees(string query)
    {
        // The defect was a SPLIT -- the same option, on the same relation, one segment apart, refused
        // on one route and silently ignored on the other. Pinning only the /$count side would let the
        // split reappear from the other direction.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());

        var navRoute = await fx.Client.GetAsync($"/odata/NavCountParents(1)/Children?{query}");
        var countRoute = await fx.Client.GetAsync($"{Count}?{query}");

        Assert.Equal(HttpStatusCode.NotImplemented, navRoute.StatusCode);
        Assert.Equal(navRoute.StatusCode, countRoute.StatusCode);
    }

    [Fact]
    public async Task TheBareCountIsUnchanged()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCountProfile>());

        var response = await fx.Client.GetAsync(Count);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("2", (await response.Content.ReadAsStringAsync()).Trim());
    }
}
