using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #359 / #380 / #353 — one mechanism, three faces. An unimplemented or unrecognized
/// <c>$</c>-prefixed system query option must be REJECTED (<c>400 UnsupportedQueryOption</c>),
/// never parsed-and-discarded under a <c>200</c> (OData Part 1 §11.2.5; Minimal-conformance
/// item 7, §13.1.1).
/// <list type="bullet">
/// <item>#359 — the three collection GETs rejected a closed four-name allowlist
///   (<c>$apply</c>/<c>$compute</c>/<c>$index</c>/<c>$deltatoken</c>); everything else beginning
///   with <c>$</c> fell through unexamined and was echoed verbatim into
///   <c>@odata.nextLink</c>.</item>
/// <item>#380 — <c>GET /{Set}({key})</c> implements <c>$select</c>/<c>$expand</c> and rejected
///   nothing at all.</item>
/// <item>#353 — <c>GET /{Set}/$count</c> silently ignored <c>$search</c> and <c>$apply</c>.</item>
/// </list>
/// Controls pin the three directions the rejection must NOT take: a name that is not
/// <c>$</c>-prefixed is a CUSTOM query option (Part 2 §5.2) and is passed through untouched, a
/// <c>@</c>-prefixed name is a parameter alias (§5.3) and likewise, and a mixed-case spelling of a
/// real option (<c>$Select</c>, <c>$TOP</c>) is still HONOURED — matching
/// <c>Microsoft.AspNetCore.OData</c>, whose <c>ODataQueryOptions.IsSystemQueryOption</c> lowercases
/// the name before matching whenever the URI resolver enables case-insensitivity (the default).
/// </summary>
public class UnrecognizedSystemQueryOptionTests
{
    private static Task<TestFixture> BuildAsync() => TestHostBuilder.BuildAsync(o => o
        .AddEntitySetProfile<SqQueryableProfile>()
        .AddEntitySetProfile<SqGetAllProfile>()
        .AddEntitySetProfile<SqODataProfile>());

    private static async Task AssertUnsupportedAsync(HttpResponseMessage resp, string option)
    {
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement error = body.GetProperty("error");
        Assert.Equal("UnsupportedQueryOption", error.GetProperty("code").GetString());
        Assert.Contains(option, error.GetProperty("message").GetString());
    }

    // ── #359: the three collection GETs ──────────────────────────────────────────

    [Theory]
    [InlineData("/odata/SqQueryables", "$unknown")]
    [InlineData("/odata/SqQueryables", "$slect")]
    [InlineData("/odata/SqQueryables", "$fliter")]
    [InlineData("/odata/SqQueryables", "$expandx")]
    [InlineData("/odata/SqQueryables", "$levels")]
    [InlineData("/odata/SqQueryables", "$id")]
    [InlineData("/odata/SqQueryables", "$schemaversion")]
    [InlineData("/odata/SqGetAlls", "$unknown")]
    [InlineData("/odata/SqGetAlls", "$slect")]
    [InlineData("/odata/SqGetAlls", "$fliter")]
    [InlineData("/odata/SqGetAlls", "$expandx")]
    [InlineData("/odata/SqGetAlls", "$levels")]
    [InlineData("/odata/SqODatas", "$unknown")]
    [InlineData("/odata/SqODatas", "$slect")]
    [InlineData("/odata/SqODatas", "$fliter")]
    [InlineData("/odata/SqODatas", "$expandx")]
    [InlineData("/odata/SqODatas", "$levels")]
    public async Task Collection_UnrecognizedDollarOption_Returns400(string url, string option)
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"{url}?{System.Uri.EscapeDataString(option)}=2");
        await AssertUnsupportedAsync(resp, option);
    }

    [Theory]
    [InlineData("/odata/SqQueryables", "$apply", "groupby((Name))")]
    [InlineData("/odata/SqQueryables", "$compute", "1 add 1 as Two")]
    [InlineData("/odata/SqQueryables", "$index", "0")]
    [InlineData("/odata/SqQueryables", "$deltatoken", "abc")]
    public async Task Collection_TheFourAlreadyRejectedNames_KeepTheirExactEnvelope(
        string url, string option, string value)
    {
        // Regression guard on the wire: these four have shipped as 400 UnsupportedQueryOption with
        // this exact message since 1.0.0 and must not be re-worded (or promoted to 501) by the
        // generalisation that now covers every other $-name.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"{url}?{option}={System.Uri.EscapeDataString(value)}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UnsupportedQueryOption", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal($"The query option '{option}' is not supported.",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Collection_GetAll_SkipToken_IsRejected_ItWasNeverApplied()
    {
        // The GetAll path continues with $skip (#201 ApplyGetAllPaging) and never reads
        // $skiptoken; it used to accept and discard one. The Priority-2 path DOES honour it —
        // see the control below. This is the per-route difference the shared helper exists for.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqGetAlls?$skiptoken=MgAAAA%3d%3d");
        await AssertUnsupportedAsync(resp, "$skiptoken");
    }

    [Fact]
    public async Task Collection_GetQueryable_SkipToken_StillHonoured()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqQueryables?$skiptoken=MQAAAA%3d%3d");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── #359 second half: no nextLink is ever built for a rejected request ────────

    [Fact]
    public async Task Collection_PagedRequest_EmitsNextLink_Control()
    {
        await using TestFixture fx = await BuildAsync();
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/SqQueryables");
        Assert.True(json.TryGetProperty("@odata.nextLink", out _),
            "control: MaxTop=2 over 3 rows must page and emit a nextLink");
    }

    [Fact]
    public async Task Collection_UnrecognizedOption_IsNeverEchoedIntoANextLink()
    {
        // #359's second half: the unknown option was echoed verbatim into the generated
        // @odata.nextLink. Once the option is a 400 there is no nextLink to echo into —
        // asserted here rather than assumed.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqQueryables?$unknown=evil%20payload");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string raw = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("nextLink", raw);
        Assert.DoesNotContain("skiptoken", raw);
    }

    // ── Controls: what must NOT be rejected ──────────────────────────────────────

    [Theory]
    [InlineData("/odata/SqQueryables?$Select=Name")]
    [InlineData("/odata/SqQueryables?$TOP=2")]
    [InlineData("/odata/SqQueryables?$SELECT=Name")]
    [InlineData("/odata/SqQueryables?$Count=true")]
    [InlineData("/odata/SqGetAlls?$Select=Name")]
    [InlineData("/odata/SqGetAlls?$TOP=2")]
    [InlineData("/odata/SqODatas?$Select=Name")]
    public async Task MixedCaseSpellingOfARealOption_IsStillHonoured(string url)
    {
        // Alignment with Microsoft.AspNetCore.OData: ODataQueryOptions lowercases the option name
        // before matching whenever the URI resolver enables case-insensitivity, which is the
        // default. $Select being APPLIED is not the defect; $slect being IGNORED was.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData("/odata/SqQueryables?custom=1")]
    [InlineData("/odata/SqQueryables?filter=Name%20eq%20%27A%27")]
    [InlineData("/odata/SqQueryables?ohdata-skiptoken=AQAAAA%3d%3d")]
    [InlineData("/odata/SqQueryables?%40p1=1")]
    [InlineData("/odata/SqQueryables(1)?custom=1")]
    [InlineData("/odata/SqQueryables(1)?%40p1=1")]
    [InlineData("/odata/SqQueryables/$count?custom=1")]
    [InlineData("/odata/SqQueryables(1)/Children?custom=1")]
    [InlineData("/odata/SqQueryables(1)/Children/$count?custom=1")]
    public async Task NonDollarAndAliasQueryKeys_ArePassedThroughUntouched(string url)
    {
        // Part 2 §5.2: a custom query option MUST NOT begin with '$'. §5.3: a parameter alias
        // begins with '@'. Neither is a system query option and neither may be rejected here.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData("/odata/SqQueryables?$format=json")]
    [InlineData("/odata/SqGetAlls?$format=json")]
    [InlineData("/odata/SqODatas?$format=json")]
    [InlineData("/odata/SqQueryables(1)?$format=json")]
    [InlineData("/odata/SqQueryables/$count?$format=json")]
    [InlineData("/odata/SqQueryables(1)/Children?$format=json")]
    [InlineData("/odata/SqQueryables(1)/Children/$count?$format=json")]
    public async Task Format_IsHonouredEverywhereItWasBefore(string url)
    {
        // $format is negotiated once, by the group filter that wraps the whole OData surface, and
        // never reaches these handlers. The new rejection must not swallow it.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── #380: GetById ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("$filter=Name eq 'nope'", "$filter")]
    [InlineData("$orderby=Name", "$orderby")]
    [InlineData("$count=true", "$count")]
    [InlineData("$top=1", "$top")]
    [InlineData("$skip=1", "$skip")]
    [InlineData("$search=x", "$search")]
    [InlineData("$apply=groupby((Name))", "$apply")]
    [InlineData("$skiptoken=abc", "$skiptoken")]
    [InlineData("$unknown=1", "$unknown")]
    [InlineData("$slect=Name", "$slect")]
    public async Task GetById_OptionItDoesNotImplement_Returns400(string query, string option)
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/SqQueryables(1)?{query}");
        await AssertUnsupportedAsync(resp, option);
    }

    [Theory]
    [InlineData("/odata/SqQueryables(1)")]
    [InlineData("/odata/SqQueryables(1)?$select=Name")]
    [InlineData("/odata/SqQueryables(1)?$Select=Name")]
    [InlineData("/odata/SqQueryables(1)?$expand=Children")]
    public async Task GetById_ImplementedOptions_StillSucceed(string url)
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── #353: /{Set}/$count ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("$search=alpha", "$search")]
    [InlineData("$apply=groupby((Name))", "$apply")]
    [InlineData("$compute=1 add 1 as Two", "$compute")]
    [InlineData("$orderby=Name", "$orderby")]
    [InlineData("$select=Name", "$select")]
    [InlineData("$expand=Children", "$expand")]
    [InlineData("$count=true", "$count")]
    [InlineData("$unknown=1", "$unknown")]
    public async Task Count_OptionItDoesNotApply_Returns400(string query, string option)
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/SqQueryables/$count?{query}");
        await AssertUnsupportedAsync(resp, option);
    }

    [Fact]
    public async Task Count_Filter_IsStillApplied()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqQueryables/$count?$filter=Id eq 1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("1", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/odata/SqQueryables/$count", "3")]
    [InlineData("/odata/SqQueryables/$count?$top=1", "3")]
    [InlineData("/odata/SqQueryables/$count?$skip=2", "3")]
    public async Task Count_TopAndSkip_StayAcceptedNoOps(string url, string expected)
    {
        // The /$count segment reports the size of the collection the request addresses after
        // $filter/$search; $top and $skip are not applicable to it and are ignored. Unchanged
        // from 1.0.0, and #353's own control matrix records both as "correctly ignored".
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(expected, await resp.Content.ReadAsStringAsync());
    }

    // ── Navigation collection route and its /$count ──────────────────────────────

    [Theory]
    [InlineData("$unknown=1", "$unknown")]
    [InlineData("$slect=Name", "$slect")]
    [InlineData("$levels=2", "$levels")]
    public async Task NavCollection_UnrecognizedDollarOption_Returns400(string query, string option)
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/SqQueryables(1)/Children?{query}");
        await AssertUnsupportedAsync(resp, option);
    }

    [Fact]
    public async Task NavCollection_ClosedListNames_KeepTheirExactEnvelope()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqQueryables(1)/Children?$filter=Id eq 1");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UnsupportedQueryOption", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "This navigation route does not support $filter. Supported query options " +
            "are $select, $orderby, $skip, $top, and $count.",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/odata/SqQueryables(1)/Children")]
    [InlineData("/odata/SqQueryables(1)/Children?$select=Name")]
    [InlineData("/odata/SqQueryables(1)/Children?$orderby=Name")]
    [InlineData("/odata/SqQueryables(1)/Children?$top=1")]
    [InlineData("/odata/SqQueryables(1)/Children?$skip=1")]
    [InlineData("/odata/SqQueryables(1)/Children?$count=true")]
    [InlineData("/odata/SqQueryables(1)/Children?$TOP=1")]
    public async Task NavCollection_ImplementedOptions_StillSucceed(string url)
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData("$search=x", "$search")]
    [InlineData("$filter=Id eq 1", "$filter")]
    [InlineData("$select=Name", "$select")]
    [InlineData("$unknown=1", "$unknown")]
    public async Task NavCount_OptionItDoesNotApply_Returns400(string query, string option)
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/SqQueryables(1)/Children/$count?{query}");
        await AssertUnsupportedAsync(resp, option);
    }

    [Fact]
    public async Task NavCount_PlainRequest_StillCounts()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqQueryables(1)/Children/$count");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("2", await resp.Content.ReadAsStringAsync());
    }
}

// ── Fixtures ─────────────────────────────────────────────────────────────────────

internal class SqParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<SqChild>? Children { get; set; }
}

internal class SqChild
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal static class SqStore
{
    internal static readonly List<SqParent> Parents = new()
    {
        new() { Id = 1, Name = "Alpha" },
        new() { Id = 2, Name = "Beta" },
        new() { Id = 3, Name = "Gamma" },
    };

    internal static readonly List<SqChild> Children = new()
    {
        new() { Id = 10, Name = "Child1" },
        new() { Id = 11, Name = "Child2" },
    };
}

/// <summary>Priority-2 (<c>GetQueryable</c>) profile: collection GET, <c>/$count</c>, GetById, and
/// a delegate-backed collection navigation with its own <c>/$count</c>.</summary>
internal class SqQueryableProfile : EntitySetProfile<int, SqParent>
{
    public SqQueryableProfile() : base(x => x.Id)
    {
        EntitySetName = "SqQueryables";
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;
        MaxTop = 2;
        GetQueryable = (ct) => Task.FromResult(SqStore.Parents.AsQueryable());
        GetById = (id, ct) => Task.FromResult(SqStore.Parents.FirstOrDefault(p => p.Id == id));
        HasMany(x => x.Children!,
            getAll: (parentId, ct) => Task.FromResult<IEnumerable<SqChild>>(SqStore.Children));
    }
}

/// <summary>Simple-read-path (<c>GetAll</c>) profile.</summary>
internal class SqGetAllProfile : EntitySetProfile<int, SqParent>
{
    public SqGetAllProfile() : base(x => x.Id)
    {
        EntitySetName = "SqGetAlls";
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;
        GetAll = (ct) => Task.FromResult<IEnumerable<SqParent>>(SqStore.Parents);
    }
}

/// <summary>Priority-1 (<c>GetODataQueryable</c>) profile.</summary>
internal class SqODataProfile : ODataEntitySetProfile<int, SqParent>
{
    public SqODataProfile() : base(x => x.Id)
    {
        EntitySetName = "SqODatas";
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;
        GetODataQueryable = (options, ct) =>
            Task.FromResult(ODataQueryResult<SqParent>.FromQueryable(SqStore.Parents.AsQueryable()));
    }
}
