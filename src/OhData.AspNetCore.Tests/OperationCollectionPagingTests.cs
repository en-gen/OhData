using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

internal sealed class OpPagedItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>#357 fixture: an entity set whose <c>MaxTop</c> is 10 and whose bound operations return
/// the whole 25-item store. The ordinary collection GET on this set is capped at 10 with a
/// <c>@odata.nextLink</c>; every bound operation below returns the identical collection.</summary>
internal sealed class OpPagedProfile : EntitySetProfile<int, OpPagedItem>
{
    internal static readonly List<OpPagedItem> Store =
        Enumerable.Range(1, 25).Select(i => new OpPagedItem { Id = i, Name = $"item-{i}" }).ToList();

    public OpPagedProfile() : base(x => x.Id)
    {
        EntitySetName = "OpPagedItems";
        MaxTop = 10;

        GetAll = ct => OhDataResult.SuccessTask<IEnumerable<OpPagedItem>>(Store);
        GetById = (id, ct) => OhDataResult.SuccessTask(Store.FirstOrDefault(x => x.Id == id));

        BindFunction(TopRated);            // GET  /OpPagedItems/TopRated       -> collection
        BindFunction(Headline);            // GET  /OpPagedItems/Headline       -> single entity
        BindFunction(HowMany);             // GET  /OpPagedItems/HowMany        -> Edm primitive
        BindAction(Touch);                 // POST /OpPagedItems/Touch          -> Edm.String collection
        BindEntityFunction(Siblings);      // GET  /OpPagedItems(1)/Siblings    -> collection
    }

    private Task<IEnumerable<OpPagedItem>> TopRated() => Task.FromResult<IEnumerable<OpPagedItem>>(Store);
    private Task<OpPagedItem?> Headline() => Task.FromResult<OpPagedItem?>(Store[0]);
    private Task<int> HowMany() => Task.FromResult(Store.Count);
    // Deliberately NOT TModel, and it now stays that way by choice rather than by necessity. When
    // #357 wrote this fixture, Microsoft.OData.ModelBuilder's ActionConfiguration.Returns /
    // .ReturnsCollection REFUSED a type already declared as an entity type ("Use the method
    // 'ReturnsFromEntitySet'" / "'ReturnsCollectionFromEntitySet'") while the FunctionConfiguration
    // twins accepted it, so a BindAction returning TModel or IEnumerable<TModel> could not be
    // registered at all -- which is what #357 meant by its "bound ACTION returning a collection"
    // half being unreachable. #539 fixed that, and #543 then bounded the action path;
    // BoundActionEntityReturnTests owns both. This profile keeps an Edm.String collection so the
    // "not a collection of TModel, therefore not bounded" arm below stays covered.
    private Task<IEnumerable<string>> Touch() => Task.FromResult<IEnumerable<string>>(new[] { "ok" });
    private Task<IEnumerable<OpPagedItem>> Siblings(int key) =>
        Task.FromResult<IEnumerable<OpPagedItem>>(Store.Where(x => x.Id != key));
}

/// <summary>#357 opt-out fixture: identical shape with <c>MaxTop = null</c>, i.e. the developer has
/// explicitly declared "this set has no ceiling".</summary>
internal sealed class OpUnboundedProfile : EntitySetProfile<int, OpPagedItem>
{
    private static readonly List<OpPagedItem> _store =
        Enumerable.Range(1, 25).Select(i => new OpPagedItem { Id = i, Name = $"item-{i}" }).ToList();

    public OpUnboundedProfile() : base(x => x.Id)
    {
        EntitySetName = "OpUnboundedItems";
        MaxTop = null;

        GetAll = ct => OhDataResult.SuccessTask<IEnumerable<OpPagedItem>>(_store);
        GetById = (id, ct) => OhDataResult.SuccessTask(_store.FirstOrDefault(x => x.Id == id));

        BindFunction(TopRated);
    }

    private Task<IEnumerable<OpPagedItem>> TopRated() => Task.FromResult<IEnumerable<OpPagedItem>>(_store);
}

/// <summary>#357 small-result fixture, used only for the byte-identity control: three items, well
/// under the cap, so the response must not move by one byte.</summary>
internal sealed class OpTinyProfile : EntitySetProfile<int, OpPagedItem>
{
    private static readonly List<OpPagedItem> _store =
        Enumerable.Range(1, 3).Select(i => new OpPagedItem { Id = i, Name = $"item-{i}" }).ToList();

    public OpTinyProfile() : base(x => x.Id)
    {
        EntitySetName = "OpTinyItems";
        MaxTop = 10;

        GetAll = ct => OhDataResult.SuccessTask<IEnumerable<OpPagedItem>>(_store);
        GetById = (id, ct) => OhDataResult.SuccessTask(_store.FirstOrDefault(x => x.Id == id));

        BindFunction(TopRated);
    }

    private Task<IEnumerable<OpPagedItem>> TopRated() => Task.FromResult<IEnumerable<OpPagedItem>>(_store);
}

/// <summary>
/// #357 — a bound operation returning a collection of the entity set's own type bypassed
/// <c>MaxTop</c>, the client's <c>$top</c>/<c>$skip</c>, and server-driven paging entirely. The DoS
/// bound the framework advertises and enforces on every ordinary collection route was fully
/// bypassable through any such operation.
///
/// <para>The fix mirrors #201's <c>ApplyGetAllPaging</c> exactly, because the situation is
/// identical: the framework holds a fully materialized array and owns the whole pipeline from that
/// point on. An omitted <c>$top</c> is capped to <c>MaxTop</c> (or a smaller
/// <c>Prefer: maxpagesize</c>) with a <c>$skip</c> <c>@odata.nextLink</c> for the remainder; an
/// explicit <c>$top</c> is applied and validated against <c>MaxTop</c>; <c>$skip</c> is applied.
/// <c>MaxTop = null</c> opts out.</para>
///
/// <para><b>Continuations are functions-only, but the CEILING is not — see #543.</b> A
/// <c>@odata.nextLink</c> is a URL the client GETs — §11.2.5.7 defines it as a link that "allows
/// retrieving the next partial set of items" — and <c>POST /Set/Action</c> is not GET-addressable,
/// so a continuation link there would be a URL that answers 405. (This is NOT the withdrawn §11.5.4
/// "no representation" claim that #478 leaned on for the ETag gate; see #566.) #357 read that as "actions are excluded" and left the ceiling
/// bypassable through them entirely; #543 separated the two, so an action honours
/// <c>$top</c>/<c>$skip</c> and <c>MaxTop</c> and refuses a result it cannot serve within the
/// ceiling rather than truncating it silently. <c>BoundActionEntityReturnTests</c> owns that half.
/// #357's claim that the exclusion was "moot in practice" — because a bound action could not
/// declare <c>Task&lt;IEnumerable&lt;TModel&gt;&gt;</c> — was refuted by <c>Task&lt;object&gt;</c>
/// and then removed outright by #539.</para>
/// </summary>
public class OperationCollectionPagingTests
{
    private static Task<TestFixture> BuildAsync() =>
        TestHostBuilder.BuildAsync(o =>
        {
            o.AddEntitySetProfile<OpPagedProfile>();
        });

    private static async Task<JsonElement> GetJsonAsync(TestFixture fx, string url)
        => await fx.Client.GetFromJsonAsync<JsonElement>(url);

    private static int[] Ids(JsonElement json) =>
        json.GetProperty("value").EnumerateArray().Select(e => e.GetProperty("Id").GetInt32()).ToArray();

    // ── The bound function's collection result is bounded ────────────────────────

    [Fact]
    public async Task BoundFunction_CollectionResult_IsCappedAtMaxTop_AndEmitsNextLink()
    {
        await using TestFixture fx = await BuildAsync();

        // The ordinary collection route on the same set, for reference: capped at 10 + nextLink.
        JsonElement control = await GetJsonAsync(fx, "/odata/OpPagedItems");
        Assert.Equal(10, control.GetProperty("value").GetArrayLength());
        Assert.True(control.TryGetProperty("@odata.nextLink", out _));

        // Pre-fix: 25 items and no @odata.nextLink.
        JsonElement json = await GetJsonAsync(fx, "/odata/OpPagedItems/TopRated");
        Assert.Equal(10, json.GetProperty("value").GetArrayLength());
        Assert.True(json.TryGetProperty("@odata.nextLink", out JsonElement nl),
            "bound-function collection result carried no @odata.nextLink");
        // BuildNextPageLinkWithSkip percent-encodes the '$' (HttpUtility query serialization),
        // the same shape GetAllCapTests asserts for the #201 continuation.
        Assert.Contains("skip=10", nl.GetString(), StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(1, 10), Ids(json));
    }

    [Fact]
    public async Task BoundFunction_NextLink_WalksTheWholeCollection()
    {
        await using TestFixture fx = await BuildAsync();

        var seen = new List<int>();
        string? relative = "/odata/OpPagedItems/TopRated";
        int guard = 0;
        while (relative is not null && guard++ < 10)
        {
            JsonElement json = await GetJsonAsync(fx, relative);
            seen.AddRange(Ids(json));
            relative = json.TryGetProperty("@odata.nextLink", out JsonElement nl)
                ? new Uri(nl.GetString()!).PathAndQuery
                : null;
        }

        Assert.Equal(Enumerable.Range(1, 25), seen);
        Assert.Equal(25, seen.Distinct().Count());
    }

    [Fact]
    public async Task BoundFunction_ExplicitTop_IsApplied_AndSuppressesTheNextLink()
    {
        await using TestFixture fx = await BuildAsync();

        // Pre-fix: 25 items -- $top silently ignored, neither applied nor rejected.
        JsonElement json = await GetJsonAsync(fx, "/odata/OpPagedItems/TopRated?$top=3");
        Assert.Equal(new[] { 1, 2, 3 }, Ids(json));
        Assert.False(json.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task BoundFunction_Skip_IsApplied()
    {
        await using TestFixture fx = await BuildAsync();

        // Pre-fix: 25 items -- $skip silently ignored.
        JsonElement json = await GetJsonAsync(fx, "/odata/OpPagedItems/TopRated?$skip=20");
        Assert.Equal(new[] { 21, 22, 23, 24, 25 }, Ids(json));
        Assert.False(json.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task BoundFunction_TopAboveMaxTop_Is400_LikeTheOrdinaryCollectionRoute()
    {
        await using TestFixture fx = await BuildAsync();

        HttpResponseMessage collection = await fx.Client.GetAsync("/odata/OpPagedItems?$top=1000");
        Assert.Equal(HttpStatusCode.BadRequest, collection.StatusCode);
        string collectionBody = await collection.Content.ReadAsStringAsync();

        // Pre-fix: 200 with 25 items.
        HttpResponseMessage operation = await fx.Client.GetAsync("/odata/OpPagedItems/TopRated?$top=1000");
        Assert.Equal(HttpStatusCode.BadRequest, operation.StatusCode);
        // The same condition must produce the same envelope, byte for byte.
        Assert.Equal(collectionBody, await operation.Content.ReadAsStringAsync());
        Assert.Equal(
            "{\"error\":{\"code\":\"InvalidQueryOption\",\"message\":\"The value of '$top' (1000) exceeds the maximum allowed value (10).\"}}",
            collectionBody);
    }

    [Theory]
    [InlineData("$top=abc")]
    [InlineData("$top=-1")]
    [InlineData("$skip=abc")]
    [InlineData("$skip=-1")]
    [InlineData("$top=")]
    [InlineData("$skip=")]
    public async Task BoundFunction_MalformedTopOrSkip_Is400_NotSilentlyIgnored(string query)
    {
        await using TestFixture fx = await BuildAsync();

        HttpResponseMessage response = await fx.Client.GetAsync($"/odata/OpPagedItems/TopRated?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task BoundFunction_PreferMaxPageSize_IsHonoured()
    {
        await using TestFixture fx = await BuildAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/odata/OpPagedItems/TopRated");
        request.Headers.TryAddWithoutValidation("Prefer", "maxpagesize=4");
        HttpResponseMessage response = await fx.Client.SendAsync(request);

        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, json.GetProperty("value").GetArrayLength());
        Assert.Equal("maxpagesize=4", Assert.Single(response.Headers.GetValues("Preference-Applied")));
        Assert.True(json.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task EntityLevelBoundFunction_CollectionResult_IsAlsoBounded()
    {
        await using TestFixture fx = await BuildAsync();

        // Pre-fix: 24 items, no nextLink.
        JsonElement json = await GetJsonAsync(fx, "/odata/OpPagedItems(1)/Siblings");
        Assert.Equal(10, json.GetProperty("value").GetArrayLength());
        Assert.True(json.TryGetProperty("@odata.nextLink", out _));
    }

    // ── Deliberate non-changes, pinned ───────────────────────────────────────────

    [Fact]
    public async Task MaxTopNull_OptsOut_FullCollectionAndNoNextLink()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<OpUnboundedProfile>());

        JsonElement json = await GetJsonAsync(fx, "/odata/OpUnboundedItems/TopRated");
        Assert.Equal(25, json.GetProperty("value").GetArrayLength());
        Assert.False(json.TryGetProperty("@odata.nextLink", out _));
    }

    /// <summary>Byte-identity control. A result already under the cap must not move by one byte;
    /// these bytes were captured from the PRE-fix build.</summary>
    [Fact]
    public async Task ResultUnderTheCap_IsByteIdentical()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<OpTinyProfile>());

        HttpResponseMessage response = await fx.Client.GetAsync("/odata/OpTinyItems/TopRated");
        Assert.Equal(
            "{\"@odata.context\":\"http://localhost/odata/$metadata#OpTinyItems\",\"value\":[{\"Id\":1,\"Name\":\"item-1\"},{\"Id\":2,\"Name\":\"item-2\"},{\"Id\":3,\"Name\":\"item-3\"}]}",
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>Non-collection operation results are untouched: no cap, no paging, and no
    /// <c>$top</c>/<c>$skip</c> handling. Bytes captured from the PRE-fix build.</summary>
    [Fact]
    public async Task SingleEntityAndPrimitiveResults_AreByteIdentical()
    {
        await using TestFixture fx = await BuildAsync();

        HttpResponseMessage single = await fx.Client.GetAsync("/odata/OpPagedItems/Headline");
        Assert.Equal(
            "{\"@odata.context\":\"http://localhost/odata/$metadata#OpPagedItems/$entity\",\"Id\":1,\"Name\":\"item-1\"}",
            await single.Content.ReadAsStringAsync());

        HttpResponseMessage primitive = await fx.Client.GetAsync("/odata/OpPagedItems/HowMany");
        Assert.Equal(
            "{\"@odata.context\":\"http://localhost/odata/$metadata#Edm.Int32\",\"value\":25}",
            await primitive.Content.ReadAsStringAsync());
    }

    // ── Advertise what the route serves (#465/#467/#468) ─────────────────────────

    [Fact]
    public async Task CollectionReturningBoundFunction_AdvertisesTopSkip_OthersDoNot()
    {
        await using TestFixture fx = await BuildAsync();

        OhDataQueryOptionsMetadata? collectionFn = MetadataFor(fx, "/odata/OpPagedItems/TopRated", "GET");
        Assert.NotNull(collectionFn);
        Assert.True(collectionFn!.TopSkipSupported);
        Assert.Equal(10, collectionFn.MaxTop);
        Assert.False(collectionFn.FilterEnabled);
        Assert.False(collectionFn.OrderByEnabled);
        Assert.False(collectionFn.SelectEnabled);
        Assert.False(collectionFn.ExpandEnabled);
        Assert.False(collectionFn.CountEnabled);
        Assert.False(collectionFn.SearchEnabled);

        OhDataQueryOptionsMetadata? entityFn = MetadataFor(fx, "/odata/OpPagedItems({key})/Siblings", "GET");
        Assert.NotNull(entityFn);
        Assert.True(entityFn!.TopSkipSupported);

        // A single-entity function, an Edm-primitive function, and the action serve no $top/$skip,
        // so none of them may advertise any.
        Assert.Null(MetadataFor(fx, "/odata/OpPagedItems/Headline", "GET"));
        Assert.Null(MetadataFor(fx, "/odata/OpPagedItems/HowMany", "GET"));
        Assert.Null(MetadataFor(fx, "/odata/OpPagedItems/Touch", "POST"));
    }

    private static OhDataQueryOptionsMetadata? MetadataFor(TestFixture fx, string routePattern, string httpMethod)
    {
        EndpointDataSource source = fx.App.Services.GetRequiredService<EndpointDataSource>();
        List<RouteEndpoint> matches = source.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => string.Equals(e.RoutePattern.RawText, routePattern, StringComparison.Ordinal)
                        && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(httpMethod) ?? false))
            .ToList();

        if (matches.Count != 1)
        {
            string available = string.Join(", ", source.Endpoints.OfType<RouteEndpoint>()
                .Select(e => e.RoutePattern.RawText));
            throw new Xunit.Sdk.XunitException(
                $"Expected exactly one {httpMethod} endpoint at '{routePattern}', found {matches.Count}. " +
                $"Available: {available}");
        }

        return matches[0].Metadata.GetMetadata<OhDataQueryOptionsMetadata>();
    }
}
