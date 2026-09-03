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
/// <c>$</c>-prefixed system query option must be REJECTED, never parsed-and-discarded under a
/// <c>200</c>, and the status is <c>501 Not Implemented</c> with code
/// <c>UnsupportedQueryOption</c>: §9.3.1 makes 501 a MUST for "functionality not implemented by
/// the OData Service", and §13.1.1 item 7 puts that same 501 inside the Minimal-conformance MUST
/// list. <c>400</c> is reserved for functionality the service DOES implement and this resource has
/// switched off — a false capability flag, a property allowlist, a <c>Search</c> handler that was
/// never supplied — and for a malformed option VALUE (#402).
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

    private static Task<TestFixture> BuildOpsAsync() => TestHostBuilder.BuildAsync(o => o
        .AddEntitySetProfile<SqOpsProfile>());

    private static async Task AssertUnsupportedAsync(HttpResponseMessage resp, string option)
    {
        // 501, not 400 — §9.3.1's MUST, and §13.1.1 item 7's Minimal-conformance MUST list. The
        // error code and message are unchanged from the 400 these used to answer.
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
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
    public async Task Collection_UnrecognizedDollarOption_Returns501(string url, string option)
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
    public async Task Collection_TheFourAlreadyRejectedNames_Move400To501_KeepTheirExactBody(
        string url, string option, string value)
    {
        // BREAKING, and the largest wire change on this branch. These four have shipped as
        // 400 UnsupportedQueryOption since 1.0.0 and now answer 501 — §9.3.1's MUST for
        // functionality the service does not implement. The error CODE and the message BYTES are
        // deliberately unchanged, so a client matching on the envelope keeps working and only
        // status-code branching moves; that byte-identity is what this test pins.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"{url}?{option}={System.Uri.EscapeDataString(value)}");
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
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
        // @odata.nextLink. Once the option is refused there is no nextLink to echo into —
        // asserted here rather than assumed.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqQueryables?$unknown=evil%20payload");
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        string raw = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("nextLink", raw);
        Assert.DoesNotContain("skiptoken", raw);
    }

    // ── Controls: what must NOT be rejected ──────────────────────────────────────

    [Theory]
    [InlineData("/odata/SqQueryables?$Select=Name", "SqQueryables(Name)")]
    [InlineData("/odata/SqQueryables?$SELECT=Name", "SqQueryables(Name)")]
    [InlineData("/odata/SqGetAlls?$Select=Name", "SqGetAlls(Name)")]
    [InlineData("/odata/SqODatas?$Select=Name", "SqODatas(Name)")]
    public async Task MixedCaseSelect_IsStillHonoured_AndReallyProjects(string url, string context)
    {
        // Alignment with Microsoft.AspNetCore.OData: ODataQueryOptions lowercases the option name
        // before matching whenever the URI resolver enables case-insensitivity, which is the
        // default. $Select being APPLIED is not the defect; $slect being IGNORED was.
        //
        // Asserting the EFFECT, not the status: "200" alone is satisfied by an ablation that makes
        // a mixed-case option accepted-and-ignored, which is precisely the failure mode this whole
        // change exists to remove. The projected @odata.context suffix (JSON SS10.8) and the absent
        // Id member are what only an APPLIED $select produces.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.EndsWith(context, body.GetProperty("@odata.context").GetString()!);
        JsonElement first = body.GetProperty("value")[0];
        Assert.True(first.TryGetProperty("Name", out _));
        Assert.False(first.TryGetProperty("Id", out _));
    }

    [Theory]
    [InlineData("/odata/SqQueryables?$TOP=1")]
    [InlineData("/odata/SqGetAlls?$TOP=1")]
    public async Task MixedCaseTop_IsStillHonoured_AndReallyWindows(string url)
    {
        // Same rule for $top: the page really is one row, not the whole (or MaxTop-capped) set.
        await using TestFixture fx = await BuildAsync();
        JsonElement body = await fx.Client.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal(1, body.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task MixedCaseCount_IsStillHonoured_AndReallyAnnotates()
    {
        await using TestFixture fx = await BuildAsync();
        JsonElement body = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/SqQueryables?$Count=true");
        Assert.Equal(3, body.GetProperty("@odata.count").GetInt32());
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
    public async Task GetById_OptionItDoesNotImplement_Returns501(string query, string option)
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/SqQueryables(1)?{query}");
        await AssertUnsupportedAsync(resp, option);
    }

    [Fact]
    public async Task GetById_NoOptions_StillSucceeds()
    {
        await using TestFixture fx = await BuildAsync();
        JsonElement body = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/SqQueryables(1)");
        Assert.Equal(1, body.GetProperty("Id").GetInt32());
        Assert.Equal("Alpha", body.GetProperty("Name").GetString());
    }

    [Theory]
    [InlineData("$select=Name")]
    [InlineData("$Select=Name")]
    [InlineData("$SELECT=Name")]
    public async Task GetById_Select_IsHonoured_AndReallyProjects(string query)
    {
        // Effect, not status: a mixed-case $select that were accepted-and-ignored would still be a
        // 200 with the whole entity, which is the exact defect #380 names.
        await using TestFixture fx = await BuildAsync();
        JsonElement body = await fx.Client.GetFromJsonAsync<JsonElement>($"/odata/SqQueryables(1)?{query}");
        Assert.EndsWith("SqQueryables(Name)/$entity", body.GetProperty("@odata.context").GetString()!);
        Assert.True(body.TryGetProperty("Name", out _));
        Assert.False(body.TryGetProperty("Id", out _));
    }

    [Fact]
    public async Task GetById_Expand_IsHonoured_AndReallyExpands()
    {
        await using TestFixture fx = await BuildAsync();
        JsonElement body = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/SqQueryables(1)?$expand=Children");
        Assert.Equal(2, body.GetProperty("Children").GetArrayLength());
    }

    // ── #353: /{Set}/$count ──────────────────────────────────────────────────────

    [Theory]
    // $search AFFECTS the count under §11.2.9 ("after applying any $filter or $search") and this
    // route has no $search leg, so ignoring it would answer a wrong number under a 200 -- #353's
    // headline, and it stands. $apply/$compute/$count/$unknown fall outside the clause entirely:
    // unimplemented here, refused under §9.3.1.
    [InlineData("$search=alpha", "$search")]
    [InlineData("$apply=groupby((Name))", "$apply")]
    [InlineData("$compute=1 add 1 as Two", "$compute")]
    [InlineData("$count=true", "$count")]
    [InlineData("$unknown=1", "$unknown")]
    public async Task Count_OptionItDoesNotApply_Returns501(string query, string option)
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
    [InlineData("$top=1")]
    [InlineData("$skip=2")]
    [InlineData("$orderby=Name")]
    [InlineData("$expand=Children")]
    [InlineData("$select=Name")]
    public async Task Count_OptionsThatCannotAffectACount_AreAcceptedAndIgnored(string query)
    {
        // §11.2.9, verbatim: "The returned count MUST NOT be affected by $top, $skip, $orderby, or
        // $expand." That is a specification of BEHAVIOUR -- present and ignored -- not silence, so
        // under §13.1.1 item 7's "either follow the specification or return 501 ... for any
        // unsupported functionality" these four are the FOLLOW arm. Refusing them claimed
        // non-implementation of something this route has done correctly since 1.0.0.
        //
        // $select is not named by that sentence and is the same answer by the clause's positive
        // half: the count is of "items matching the request after applying any $filter or
        // $search", and $select changes an item's shape rather than its membership. The response
        // is a bare text/plain scalar besides -- there is nothing to project out of it.
        //
        // The number asserted is the WHOLE-collection total, so this fails if any of the five is
        // ever actually APPLIED as well as if it is refused.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/SqQueryables/$count?{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("3", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Count_FilterBesideIgnoredOptions_AppliesOnlyTheFilter()
    {
        // The whole §11.2.9 partition on one request, which is the shape a paging client sends:
        // $filter narrows the count and the window/ordering options leave it alone. Two rows match
        // Id gt 1, and neither $top=1 nor $skip=1 nor $orderby moves the answer off 2.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/SqQueryables/$count?$filter=Id gt 1&$orderby=Name&$skip=1&$top=1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("2", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Count_Bare_StillReturnsTheTotal()
    {
        // The control the refusal above must not break: a /$count with no query string at all
        // still answers the whole-collection total as text/plain.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqQueryables/$count");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("3", await resp.Content.ReadAsStringAsync());
    }

    // ── Navigation collection route and its /$count ──────────────────────────────

    [Theory]
    [InlineData("$unknown=1", "$unknown")]
    [InlineData("$slect=Name", "$slect")]
    [InlineData("$levels=2", "$levels")]
    public async Task NavCollection_UnrecognizedDollarOption_Returns501(string query, string option)
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
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
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
    [InlineData("$count=true", "$count")]
    [InlineData("$unknown=1", "$unknown")]
    public async Task NavCount_OptionItDoesNotApply_Returns501(string query, string option)
    {
        // BOTH options §11.2.9 says a count is taken after are refused here, where the entity-set
        // /$count refuses only $search: this handler calls the navigation delegate and counts what
        // comes back, so it cannot apply $filter either, and ignoring it would answer a wrong
        // number under a 200. Same rule, different answer, because the route applies less.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/SqQueryables(1)/Children/$count?{query}");
        await AssertUnsupportedAsync(resp, option);
    }

    [Theory]
    [InlineData("$top=1")]
    [InlineData("$skip=1")]
    [InlineData("$orderby=Name")]
    [InlineData("$expand=Children")]
    [InlineData("$select=Name")]
    public async Task NavCount_OptionsThatCannotAffectACount_AreAcceptedAndIgnored(string query)
    {
        // §11.2.9 governs "a collection of entities or items of a collection-valued property", so
        // a navigation /$count is squarely inside it and gets the same treatment as the entity-set
        // one. The related collection has two members and none of these may move that.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/SqQueryables(1)/Children/$count?{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("2", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NavCount_PlainRequest_StillCounts()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqQueryables(1)/Children/$count");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("2", await resp.Content.ReadAsStringAsync());
    }

    // -- Single-valued navigation route (HasOptional/HasRequired) --------------------

    [Theory]
    [InlineData("$select=Name", "$select")]
    [InlineData("$orderby=Name", "$orderby")]
    [InlineData("$top=1", "$top")]
    [InlineData("$skip=1", "$skip")]
    [InlineData("$count=true", "$count")]
    [InlineData("$expand=Children", "$expand")]
    [InlineData("$filter=Id eq 1", "$filter")]
    [InlineData("$unknown=1", "$unknown")]
    public async Task NavSingle_OptionItDoesNotImplement_Returns501(string query, string option)
    {
        // The single-valued navigation branch serializes the related entity through
        // ODataEntityNode and reads NO query option at all -- not even $select, which its
        // collection sibling applies in BuildNavEnvelope. Gating it with the COLLECTION nav's
        // implemented set therefore accepted-and-dropped $select/$orderby/$top/$count here, which
        // is #380's defect statement verbatim ("known, implemented-elsewhere options being
        // silently dropped on a route that does not implement them") one route over.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/SqQueryables(1)/Owner?{query}");
        await AssertUnsupportedAsync(resp, option);
    }

    [Fact]
    public async Task NavSingle_Message_ListsWhatThisRouteActuallyAccepts()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqQueryables(1)/Owner?$select=Name");
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "This navigation route does not support $select. A single-valued navigation " +
            "route supports no data query options.",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("/odata/SqQueryables(1)/Owner")]
    [InlineData("/odata/SqQueryables(1)/Owner?$format=json")]
    [InlineData("/odata/SqQueryables(1)/Owner?custom=1")]
    [InlineData("/odata/SqQueryables(1)/Owner?%40p1=1")]
    public async Task NavSingle_WhatItDoesAccept_StillSucceeds(string url)
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task NavCollection_And_NavSingle_AreGatedIndependently()
    {
        // The two branches share one route template shape and used to share one implemented set.
        // $select is real on the collection branch and absent from the single-valued one, so the
        // sets must differ -- this is the pair that fails if the gate is hoisted back above the
        // navIsCollection branch.
        await using TestFixture fx = await BuildAsync();
        Assert.Equal(HttpStatusCode.OK,
            (await fx.Client.GetAsync("/odata/SqQueryables(1)/Children?$select=Name")).StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented,
            (await fx.Client.GetAsync("/odata/SqQueryables(1)/Owner?$select=Name")).StatusCode);
    }

    // -- Bound operation routes: #359's nextLink half -------------------------------

    [Theory]
    [InlineData("$unknown=evil", "$unknown")]
    [InlineData("$apply=groupby((Name))", "$apply")]
    [InlineData("$filter=Id eq 1", "$filter")]
    [InlineData("$select=Name", "$select")]
    [InlineData("$orderby=Name", "$orderby")]
    [InlineData("$skiptoken=abc", "$skiptoken")]
    public async Task BoundFunction_OptionItDoesNotImplement_Returns501(string query, string option)
    {
        await using TestFixture fx = await BuildOpsAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/SqOps/TopRated?{query}");
        await AssertUnsupportedAsync(resp, option);
    }

    [Fact]
    public async Task BoundFunction_UnrecognizedOption_IsNeverEchoedIntoANextLink()
    {
        // #359's second half, on the route #357/#543 gave a nextLink in this very release.
        // TryApplyOperationCollectionPaging emits BuildNextPageLinkWithSkip, which copies the
        // WHOLE incoming query string -- so before this fix ?$unknown=evil came back inside the
        // server's own continuation link under a 200.
        await using TestFixture fx = await BuildOpsAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqOps/TopRated?$unknown=evil");
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        string raw = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("nextLink", raw);
        Assert.DoesNotContain("unknown=evil", raw);
    }

    [Fact]
    public async Task BoundFunction_PagedRequest_EmitsNextLink_Control()
    {
        // The control the test above needs: this route really does page and really does emit a
        // link, so "no nextLink" is a consequence of the rejection and not of the fixture.
        await using TestFixture fx = await BuildOpsAsync();
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/SqOps/TopRated");
        Assert.Equal(2, json.GetProperty("value").GetArrayLength());
        Assert.Contains("skip=2", json.GetProperty("@odata.nextLink").GetString()!);
    }

    [Theory]
    [InlineData("/odata/SqOps/TopRated?$top=1")]
    [InlineData("/odata/SqOps/TopRated?$skip=2")]
    [InlineData("/odata/SqOps/TopRated?$TOP=1")]
    [InlineData("/odata/SqOps/TopRated?$format=json")]
    [InlineData("/odata/SqOps/TopRated?minRating=1")]
    public async Task BoundFunction_TopSkipFormatAndItsOwnParameters_StillAccepted(string url)
    {
        // $top/$skip are the route's implemented set per #357/#543, and they must STAY implemented
        // even when the declared return type is not a collection: the framework's own continuation
        // link carries "$skip=N", so refusing $skip would make the server emit a link it then
        // rejects. A bound function's own parameters are non-'$' keys (Part 2 5.2) and are never
        // examined by the sigil rule.
        await using TestFixture fx = await BuildOpsAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task BoundFunction_TopAboveMaxTop_KeepsItsOwnEnvelope()
    {
        // The sigil gate runs before parameter binding and must not shadow the #357 ceiling
        // message, which is shared verbatim with the collection routes. It also stays a 400: an
        // out-of-range VALUE for an option the route implements is a bad request, not
        // unimplemented functionality, so it does not follow the sigil refusals to 501.
        await using TestFixture fx = await BuildOpsAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqOps/TopRated?$top=999");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string raw = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("is not supported", raw);
    }

    [Theory]
    [InlineData("$unknown=evil", "$unknown")]
    [InlineData("$apply=groupby((Name))", "$apply")]
    [InlineData("$select=Name", "$select")]
    public async Task BoundAction_OptionItDoesNotImplement_Returns501_AndNeverRuns(
        string query, string option)
    {
        // The gate is up front, before the body is read and before the handler delegate runs, so a
        // refused invocation provably has no side effect -- the same placement CheckETagAsync uses.
        await using TestFixture fx = await BuildOpsAsync();
        SqOpsProfile.ActionInvocations = 0;
        HttpResponseMessage resp = await fx.Client.PostAsync(
            $"/odata/SqOps/Dump?{query}", JsonContent.Create(new { }));
        await AssertUnsupportedAsync(resp, option);
        Assert.Equal(0, SqOpsProfile.ActionInvocations);
    }

    [Fact]
    public async Task BoundAction_TopAndSkip_StillAccepted()
    {
        await using TestFixture fx = await BuildOpsAsync();
        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/SqOps/Dump?$top=1", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // -- Characterization: the route families still OUTSIDE the rule ---------------

    [Theory]
    [InlineData("/odata?$unknown=1")]
    [InlineData("/odata/$metadata?$unknown=1")]
    public async Task DeliberateResiduals_StillIgnoreEveryQueryOption(string url)
    {
        // Named in docs/query-options.md so its per-route table is not read as the whole URL
        // surface. Neither generates a link, so neither carries #359's echo. Characterization, not
        // endorsement -- this test is what makes closing one a deliberate act rather than an
        // accident, and #560 was exactly that for the two structural-property READ routes that
        // used to be listed here (see Issue560PropertyRouteQueryOptionTests). The property WRITE
        // routes are still ungated, consistently with the entity PUT/PATCH/DELETE routes.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData("/odata/SqQueryables(1)/Name?$unknown=1")]
    [InlineData("/odata/SqQueryables(1)/Name/$value?$unknown=1")]
    public async Task PropertyReadRoutes_AreNoLongerResidual_AndRefuse(string url)
    {
        // Moved out of the residual list by #560, on this suite's own fixture so the change is
        // visible where the old expectation lived rather than only in the new suite.
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        await AssertUnsupportedAsync(resp, "$unknown");
    }

    [Theory]
    [InlineData("$unknown=1", "$unknown")]
    [InlineData("$apply=groupby((Name))", "$apply")]
    public async Task EntityBoundFunction_OptionItDoesNotImplement_Returns501(
        string query, string option)
    {
        await using TestFixture fx = await BuildOpsAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/SqOps(1)/Stamp?{query}");
        await AssertUnsupportedAsync(resp, option);
    }

    [Fact]
    public async Task EntityBoundAction_OptionItDoesNotImplement_Returns501_AndNeverRuns()
    {
        await using TestFixture fx = await BuildOpsAsync();
        SqOpsProfile.EntityActionInvocations = 0;
        HttpResponseMessage resp = await fx.Client.PostAsync(
            "/odata/SqOps(1)/Touch?$unknown=1", JsonContent.Create(new { }));
        await AssertUnsupportedAsync(resp, "$unknown");
        Assert.Equal(0, SqOpsProfile.EntityActionInvocations);
    }
}

// ── Fixtures ─────────────────────────────────────────────────────────────────────

internal class SqParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<SqChild>? Children { get; set; }
    public SqChild? Owner { get; set; }
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
        GetQueryable = (ct) => OhDataResult.SuccessTask(SqStore.Parents.AsQueryable());
        GetById = (id, ct) => OhDataResult.SuccessTask(SqStore.Parents.FirstOrDefault(p => p.Id == id));
        HasMany(x => x.Children!,
            getAll: (parentId, ct) => Task.FromResult<IEnumerable<SqChild>>(SqStore.Children));
        // A SINGLE-VALUED navigation route. Its handler branch reads no query option at all, so
        // its implemented set is not the collection branch's -- see NavSingle_* above.
        HasOptional(x => x.Owner!,
            get: (parentId, ct) => Task.FromResult<SqChild?>(SqStore.Children[0]),
            refTargetEntitySet: null);
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
        GetAll = (ct) => OhDataResult.SuccessTask<IEnumerable<SqParent>>(SqStore.Parents);
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

/// <summary>
/// #359's <c>@odata.nextLink</c> half lands on the BOUND OPERATION collection route, which #357
/// (function) and #543 (action) gave a <c>$skip</c> continuation in this same release while it had
/// no query-option gate at all: <c>BuildNextPageLinkWithSkip</c> copies the whole incoming query
/// string, so an unrecognized option came back inside the server's own link under a <c>200</c>.
/// </summary>
internal class SqOpsProfile : EntitySetProfile<int, SqParent>
{
    internal static int ActionInvocations;
    internal static int EntityActionInvocations;

    public SqOpsProfile() : base(x => x.Id)
    {
        EntitySetName = "SqOps";
        MaxTop = 2;
        GetById = (id, ct) => OhDataResult.SuccessTask(SqStore.Parents.FirstOrDefault(p => p.Id == id));
        BindFunction(TopRated);
        BindAction(Dump);
        BindEntityFunction(Stamp);
        BindEntityAction(Touch);
    }

    private static Task<IEnumerable<SqParent>> TopRated(int minRating = 0) =>
        Task.FromResult<IEnumerable<SqParent>>(SqStore.Parents);

    private static Task<IEnumerable<SqParent>> Dump()
    {
        ActionInvocations++;
        return Task.FromResult<IEnumerable<SqParent>>(SqStore.Parents);
    }

    private static Task<string> Stamp(int key) => Task.FromResult($"stamp-{key}");

    private static Task<string> Touch(int key)
    {
        EntityActionInvocations++;
        return Task.FromResult($"touched-{key}");
    }
}
