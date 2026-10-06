using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        .AddEntitySetProfile<SqODataProfile>()
        .AddEntitySetProfile<NdBareQueryableProfile>()
        .AddEntitySetProfile<NdApplyODataProfile>());

    private static Task<(HttpStatusCode Status, string Body)> GetAsync(TestFixture fx, string url)
        => GetAsync(fx.Client, url);

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string url)
    {
        HttpResponseMessage resp = await client.GetAsync(url);
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

    // Leading whitespace in a key is trimmed by Microsoft.AspNetCore.OData BEFORE its `$` test, so
    // `%20$filter` would be applied by it while every OhData gate classes the key as custom (#718).
    public static IEnumerable<object[]> WhitespaceCases()
    {
        string[] prefixes = { "%20", "+", "%09" };
        string[] options =
        {
            "$filter=Id%20eq%203", "$top=1", "$skip=1", "$select=Name", "$orderby=Name%20desc",
            "$count=true", "$apply=groupby((Name))", "$expand=Children",
        };
        string[] routes =
        {
            "/odata/NdBares", "/odata/SqQueryables", "/odata/SqGetAlls", "/odata/NdApplyODatas",
            "/odata/NdBares/$count", "/odata/SqGetAlls/$count", "/odata/SqQueryables(1)",
        };
        foreach (string route in routes)
        {
            foreach (string prefix in prefixes)
            {
                foreach (string option in options)
                {
                    yield return new object[] { route, prefix + option };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(WhitespaceCases))]
    public async Task WhitespacePrefixedDollarKey_IsACustomOption_AndChangesNothing(string route, string query)
    {
        await using TestFixture fx = await BuildAsync();
        var baseline = await GetAsync(fx, route);
        var withOption = await GetAsync(fx, $"{route}?{query}");
        Assert.Equal(baseline.Status, withOption.Status);
        Assert.Equal(baseline.Body, withOption.Body);
    }

    [Fact]
    public async Task PriorityOne_ApplyTo_AppliesDollarFilter_AndIgnoresNoDollarAndWhitespaceKeys()
    {
        await using TestFixture fx = await BuildAsync();
        var applied = await GetAsync(fx, "/odata/NdApplyODatas?$filter=Id%20eq%203");
        Assert.Equal(HttpStatusCode.OK, applied.Status);
        Assert.Contains("Gamma", applied.Body);
        Assert.DoesNotContain("Alpha", applied.Body);

        var baseline = await GetAsync(fx, "/odata/NdApplyODatas");
        Assert.Contains("Alpha", baseline.Body);
        foreach (string q in new[] { "filter=Id%20eq%203", "%20$filter=Id%20eq%203", "top=1", "+$top=1" })
        {
            var r = await GetAsync(fx, $"/odata/NdApplyODatas?{q}");
            Assert.Equal(baseline.Body, r.Body);
        }
    }

    [Fact]
    public async Task GetById_NoDollarSelect_IsNotAppliedBesideADollarExpand()
    {
        await using TestFixture fx = await BuildAsync();
        var expandOnly = await GetAsync(fx, "/odata/SqQueryables(1)?$expand=Children");
        var both = await GetAsync(fx, "/odata/SqQueryables(1)?$expand=Children&select=Name");
        Assert.Equal(HttpStatusCode.OK, expandOnly.Status);
        Assert.Equal(expandOnly.Body, both.Body);
    }

    [Theory]
    [InlineData("filter=Id%20eq%203")]
    [InlineData("top=1")]
    [InlineData("%20$filter=Id%20eq%203")]
    public async Task HostThatCallsWithODataOptions_StillGetsFourZeroSemantics(string query)
    {
        // WithODataOptions attaches ODataMiniMetadata, which switches Microsoft.AspNetCore.OData's
        // optional-$ decision to IOptions<ODataMiniOptions> (default: ENABLED).
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOhData(o => o
            .WithPrefix("/odata")
            .AddEntitySetProfile<NdBareQueryableProfile>());
        await using WebApplication app = builder.Build();
        app.MapOhData().WithODataOptions(_ => { });
        await app.StartAsync();
        HttpClient client = ((IHost)app).GetTestClient();
        var baseline = await GetAsync(client, "/odata/NdBares");
        var withOption = await GetAsync(client, $"/odata/NdBares?{query}");
        Assert.Equal(HttpStatusCode.OK, baseline.Status);
        Assert.Equal(baseline.Body, withOption.Body);
    }

    [Fact]
    public async Task CustomKeys_AreEchoedIntoNextLink_Unchanged()
    {
        // The narrowing must not leak into the request: the server's own link still carries the
        // client's original query string, spelled as it was sent.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/NdBares?%20$top=1&filter=x&$skip=0");
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("nextLink", body);
        Assert.Contains("filter=x", body);
    }
}

/// <summary>Priority-2 profile with every capability flag off and a ceiling of two.</summary>
internal class NdBareQueryableProfile : EntitySetProfile<int, SqParent>
{
    public NdBareQueryableProfile() : base(x => x.Id)
    {
        EntitySetName = "NdBares";
        MaxTop = 2;
        GetQueryable = () => SqStore.Parents.AsQueryable();
    }
}

/// <summary>Priority-1 profile that, unlike <c>SqODataProfile</c>, really calls <c>ApplyTo</c>.</summary>
internal class NdApplyODataProfile : ODataEntitySetProfile<int, SqParent>
{
    public NdApplyODataProfile() : base(x => x.Id)
    {
        EntitySetName = "NdApplyODatas";
        FilterEnabled = true;
        OrderByEnabled = true;
        GetODataQueryable = (options, ct) =>
        {
            IQueryable q = options.ApplyTo(SqStore.Parents.AsQueryable(),
                AllowedQueryOptions.Select | AllowedQueryOptions.Expand);
            return Task.FromResult(ODataQueryResult<SqParent>.FromQueryable((IQueryable<SqParent>)q));
        };
    }
}
