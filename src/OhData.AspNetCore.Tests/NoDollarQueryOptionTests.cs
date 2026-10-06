using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #714 — OhData declares OData 4.0, under which a query key without <c>$</c> is a CUSTOM option
/// (Part 2 §5.2) and is never applied. <c>Microsoft.AspNetCore.OData</c>' optional-<c>$</c> scheme
/// (a 4.01 feature) must therefore stay off: <c>?filter=…</c> answers exactly what the request
/// without it answers, on every read route that builds options, while <c>$filter</c> and its
/// mixed-case spellings keep working.
/// </summary>
public class NoDollarQueryOptionTests
{
    private static Task<TestFixture> BuildAsync() => TestHostBuilder.BuildAsync(o => o
        .AddEntitySetProfile<SqQueryableProfile>()
        .AddEntitySetProfile<SqGetAllProfile>()
        .AddEntitySetProfile<SqODataProfile>());

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(TestFixture fx, string url)
    {
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        string body = await resp.Content.ReadAsStringAsync();
        // A custom option is passed through untouched, which includes echoing it into the server's
        // own @odata.nextLink; the DATA is what must not change.
        body = Regex.Replace(body, "\"@odata.nextLink\":\"[^\"]*\",?", "");
        return (resp.StatusCode, body);
    }

    public static IEnumerable<object[]> IgnoredCases()
    {
        string[] options =
        {
            "filter=Id eq 3", "top=1", "skip=1", "select=Name", "orderby=Name desc",
            "expand=Children", "count=true", "apply=groupby((Name))", "search=x",
        };
        string[] routes =
        {
            "/odata/SqQueryables", "/odata/SqGetAlls", "/odata/SqODatas",
            "/odata/SqQueryables/$count", "/odata/SqGetAlls/$count",
            "/odata/SqQueryables(1)", "/odata/SqGetAlls(1)",
            "/odata/SqQueryables(1)/Children", "/odata/SqQueryables(1)/Owner",
            "/odata/SqQueryables(1)/Children/$count",
        };
        foreach (string route in routes)
        {
            foreach (string option in options)
            {
                yield return new object[] { route, option };
            }
        }
    }

    [Theory]
    [MemberData(nameof(IgnoredCases))]
    public async Task NoDollarOption_IsACustomOption_AndChangesNothing(string route, string option)
    {
        await using TestFixture fx = await BuildAsync();
        var baseline = await GetAsync(fx, route);
        var withOption = await GetAsync(fx, $"{route}?{option}");
        Assert.Equal(baseline.Status, withOption.Status);
        Assert.Equal(baseline.Body, withOption.Body);
    }

    [Theory]
    [InlineData("$filter=Id eq 3")]
    [InlineData("$Filter=Id eq 3")]
    [InlineData("$FILTER=Id eq 3")]
    public async Task DollarFilter_StillApplies_CaseInsensitively(string query)
    {
        await using TestFixture fx = await BuildAsync();
        var (status, body) = await GetAsync(fx, $"/odata/SqQueryables?{query}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Gamma", body);
        Assert.DoesNotContain("Alpha", body);
    }

    [Theory]
    [InlineData("$top=1")]
    [InlineData("$TOP=1")]
    public async Task DollarTop_StillApplies_CaseInsensitively(string query)
    {
        await using TestFixture fx = await BuildAsync();
        var (status, body) = await GetAsync(fx, $"/odata/SqQueryables?{query}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Alpha", body);
        Assert.DoesNotContain("Beta", body);
    }

    [Fact]
    public async Task DollarSelect_MixedCase_StillApplies_OnGetById()
    {
        await using TestFixture fx = await BuildAsync();
        var (status, body) = await GetAsync(fx, "/odata/SqQueryables(1)?$Select=Name");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("\"id\"", body.ToLowerInvariant().Replace("\"@odata.id\"", ""));
    }

    [Fact]
    public async Task NoDollarKey_DoesNotCollideWith_TheDollarSpelling()
    {
        // `?filter=x&$filter=y` used to answer a generic 400 (both spellings normalized onto one key).
        await using TestFixture fx = await BuildAsync();
        var (status, body) = await GetAsync(fx, "/odata/SqQueryables?filter=Id eq 1&$filter=Id eq 3");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Gamma", body);
        Assert.DoesNotContain("Alpha", body);
    }
}
