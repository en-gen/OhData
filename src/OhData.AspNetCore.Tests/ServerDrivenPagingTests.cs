using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Query;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #360: server-driven paging correctness across all three collection read paths, for the two
/// shapes every pre-existing paging fixture happened to miss — a dataset whose row count is an
/// EXACT MULTIPLE of the page size (every old fixture ended on a partial page, which is what hid
/// the spurious trailing <c>@odata.nextLink</c>) and a walk that starts at a NON-ZERO explicit
/// <c>$skip</c> (every old walk started at offset 0, which is what hid the continuation rewinding
/// to the client's own offset).
/// <para>
/// Every walk here asserts the same three things, since a paging bug shows up as exactly one of
/// them: the walk TERMINATES, no row is served TWICE, and no row is SKIPPED. <c>@odata.count</c>
/// is asserted alongside because both fixes touch the code that computes the page — and the count
/// is computed independently of it (§11.2.5.5: unaffected by <c>$top</c>/<c>$skip</c>).
/// </para>
/// <para>
/// Each test here was checked against the unfixed code. The three <c>GetAll_*</c> tests are
/// deliberately NOT revert-sensitive: that path was already correct (it materializes its source and
/// compares the page against the pre-paging total), so they are characterization tests pinning the
/// behaviour the other two paths were fixed to match. <c>Priority1_WalkFromExplicitSkip_*</c> and
/// <c>Priority1_ProfileIgnoringOptions_PageIsStillCappedToMaxTop</c> are likewise green both ways —
/// the first because the old <c>$skip</c> continuation did work for a profile that re-applied the
/// options, the second because it guards a hazard the fix itself introduces (the probe row must
/// never reach the client). Every other test in this file goes red when its fix is reverted.
/// </para>
/// </summary>
public class ServerDrivenPagingTests
{
    // Every fixture below holds 20 rows with MaxTop = 5: 20 % 5 == 0, so the fourth page is
    // exactly full and is also the last one. That is the whole point.
    private const int Rows = 20;
    private const int PageSize = 5;

    private static async Task<JsonElement> GetJsonAsync(TestFixture fx, string url)
        => await fx.Client.GetFromJsonAsync<JsonElement>(url);

    /// <summary>
    /// Follows <c>@odata.nextLink</c> to exhaustion, returning the ids in the order served plus the
    /// number of pages it took. The guard is deliberately generous but finite: an unhonoured
    /// continuation (defect B) loops forever, and a test that hangs is worse than one that fails.
    /// </summary>
    private static async Task<(List<int> Ids, int Pages)> WalkAsync(TestFixture fx, string startUrl)
    {
        var ids = new List<int>();
        string? relative = startUrl;
        int pages = 0;
        while (relative is not null)
        {
            Assert.True(++pages <= 25,
                "the nextLink walk did not terminate within 25 pages — the server is emitting a " +
                "continuation it does not honour, or an empty-page loop.");

            var json = await GetJsonAsync(fx, relative);
            JsonElement value = json.GetProperty("value");
            Assert.True(value.GetArrayLength() > 0,
                $"page {pages} came back EMPTY, so the previous page's @odata.nextLink was spurious.");

            foreach (JsonElement el in value.EnumerateArray())
                ids.Add(el.GetProperty("Id").GetInt32());

            relative = json.TryGetProperty("@odata.nextLink", out JsonElement nl)
                ? new Uri(nl.GetString()!).PathAndQuery
                : null;
        }
        return (ids, pages);
    }

    /// <summary>
    /// Every row in <c>[from..to]</c> served exactly once, in ANY order. Sorting before comparing is
    /// deliberate only where the serve ORDER is not part of the contract — Priority-1 establishes no
    /// order of its own (#244, it is the profile's responsibility), so only the SET is pinned there.
    /// Where the order IS guaranteed, use <see cref="AssertServedExactlyOnceInOrder"/> instead: this
    /// assertion would pass a page-boundary defect that served every row once but resequenced them.
    /// </summary>
    private static void AssertServedExactlyOnce(IReadOnlyList<int> ids, int from, int to)
    {
        Assert.Equal(Enumerable.Range(from, to - from + 1), ids.OrderBy(i => i));
        Assert.Equal(ids.Count, ids.Distinct().Count()); // no row served twice
    }

    /// <summary>
    /// The stronger form: exactly <c>[from..to]</c>, in that SEQUENCE. Usable wherever the serve
    /// order is guaranteed — the `GetQueryable` path is ordered by the entity key by the framework,
    /// and `GetAll` pages its source array positionally — so insertion order (ids ascending) is the
    /// expected serve order and a resequencing defect must fail rather than be sorted away.
    /// </summary>
    private static void AssertServedExactlyOnceInOrder(IReadOnlyList<int> ids, int from, int to)
        => Assert.Equal(Enumerable.Range(from, to - from + 1), ids);

    // ---------------------------------------------------------------- GetQueryable (Priority 2)

    [Fact]
    public async Task GetQueryable_ExactMultiple_LastFullPage_HasNoNextLink()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingQueryableProfile>());
        var (ids, pages) = await WalkAsync(fx, "/odata/PagingQueryableWidgets");

        Assert.Equal(Rows / PageSize, pages); // 4 pages, NOT 5 with an empty tail
        AssertServedExactlyOnceInOrder(ids, 1, Rows);
    }

    [Fact]
    public async Task GetQueryable_WalkFromExplicitSkip_DoesNotRewind()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingQueryableProfile>());
        var (ids, _) = await WalkAsync(fx, "/odata/PagingQueryableWidgets?$skip=3");

        // Rows 4..20 exactly once. Before the fix the first continuation pointed back at offset
        // PageSize (ignoring the client's $skip=3), re-serving rows 6..8.
        AssertServedExactlyOnce(ids, 4, Rows);
    }

    [Fact]
    public async Task GetQueryable_ExplicitSkip_FirstContinuation_StartsAfterTheServedPage()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingQueryableProfile>());
        var json = await GetJsonAsync(fx, "/odata/PagingQueryableWidgets?$skip=3");

        Assert.Equal(new[] { 4, 5, 6, 7, 8 },
            json.GetProperty("value").EnumerateArray().Select(e => e.GetProperty("Id").GetInt32()).ToArray());

        string next = new Uri(json.GetProperty("@odata.nextLink").GetString()!).PathAndQuery;
        JsonElement page2 = await GetJsonAsync(fx, next);
        Assert.Equal(new[] { 9, 10, 11, 12, 13 },
            page2.GetProperty("value").EnumerateArray().Select(e => e.GetProperty("Id").GetInt32()).ToArray());
    }

    [Fact]
    public async Task GetQueryable_Count_IsPrePagingTotal_OnEveryPageOfTheWalk()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingQueryableProfile>());

        string? relative = "/odata/PagingQueryableWidgets?$count=true";
        int guard = 0;
        while (relative is not null && guard++ < 25)
        {
            var json = await GetJsonAsync(fx, relative);
            // The probe row fetched to decide the continuation must never leak into @odata.count
            // (computed by its own LongCount, pre-paging) nor into the page itself.
            Assert.Equal((long)Rows, json.GetProperty("@odata.count").GetInt64());
            Assert.True(json.GetProperty("value").GetArrayLength() <= PageSize);
            relative = json.TryGetProperty("@odata.nextLink", out JsonElement nl)
                ? new Uri(nl.GetString()!).PathAndQuery
                : null;
        }
        Assert.Equal(Rows / PageSize, guard);
    }

    // ------------------------------------------------------------------------------- GetAll

    [Fact]
    public async Task GetAll_ExactMultiple_LastFullPage_HasNoNextLink()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingGetAllProfile>());
        var (ids, pages) = await WalkAsync(fx, "/odata/PagingGetAllWidgets");

        Assert.Equal(Rows / PageSize, pages);
        AssertServedExactlyOnceInOrder(ids, 1, Rows);
    }

    [Fact]
    public async Task GetAll_WalkFromExplicitSkip_DoesNotRewind()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingGetAllProfile>());
        var (ids, _) = await WalkAsync(fx, "/odata/PagingGetAllWidgets?$skip=3");

        AssertServedExactlyOnce(ids, 4, Rows);
    }

    [Fact]
    public async Task GetAll_Count_IsPrePagingTotal_OnEveryPageOfTheWalk()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingGetAllProfile>());

        string? relative = "/odata/PagingGetAllWidgets?$count=true";
        int guard = 0;
        while (relative is not null && guard++ < 25)
        {
            var json = await GetJsonAsync(fx, relative);
            Assert.Equal((long)Rows, json.GetProperty("@odata.count").GetInt64());
            relative = json.TryGetProperty("@odata.nextLink", out JsonElement nl)
                ? new Uri(nl.GetString()!).PathAndQuery
                : null;
        }
        Assert.Equal(Rows / PageSize, guard);
    }

    // ----------------------------------------------------------------------- Priority 1

    [Fact]
    public async Task Priority1_ExactMultiple_LastFullPage_HasNoNextLink()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingPriority1Profile>());
        var (ids, pages) = await WalkAsync(fx, "/odata/PagingP1Widgets");

        Assert.Equal(Rows / PageSize, pages);
        AssertServedExactlyOnce(ids, 1, Rows);
    }

    [Fact]
    public async Task Priority1_WalkFromExplicitSkip_DoesNotRewind()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingPriority1Profile>());
        var (ids, _) = await WalkAsync(fx, "/odata/PagingP1Widgets?$skip=3");

        // The client's $skip rides along verbatim on every continuation and is re-applied by the
        // profile each hop; the framework's own offset accumulates on top of it.
        AssertServedExactlyOnce(ids, 4, Rows);
    }

    [Fact]
    public async Task Priority1_Count_IsProfileSuppliedTotal_OnEveryPageOfTheWalk()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingPriority1Profile>());

        string? relative = "/odata/PagingP1Widgets?$count=true";
        int guard = 0;
        while (relative is not null && guard++ < 25)
        {
            var json = await GetJsonAsync(fx, relative);
            Assert.Equal((long)Rows, json.GetProperty("@odata.count").GetInt64());
            Assert.True(json.GetProperty("value").GetArrayLength() <= PageSize);
            relative = json.TryGetProperty("@odata.nextLink", out JsonElement nl)
                ? new Uri(nl.GetString()!).PathAndQuery
                : null;
        }
        Assert.Equal(Rows / PageSize, guard);
    }

    /// <summary>
    /// The defect-B case: a Priority-1 profile that never touches the <see cref="ODataQueryOptions{T}"/>
    /// it is handed. The framework used to emit a <c>$skip=N</c> continuation it did not itself apply,
    /// so this profile served page 1 forever and a nextLink walk never terminated. The framework now
    /// carries and applies its own offset, so the walk is correct WITHOUT the profile cooperating.
    /// </summary>
    [Fact]
    public async Task Priority1_ProfileIgnoringOptions_WalkTerminates_AndServesEveryRowOnce()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingP1IgnoresOptionsProfile>());
        var (ids, pages) = await WalkAsync(fx, "/odata/PagingP1IgnoreWidgets");

        Assert.Equal(Rows / PageSize, pages);
        AssertServedExactlyOnce(ids, 1, Rows);
    }

    [Fact]
    public async Task Priority1_ProfileIgnoringOptions_PageIsStillCappedToMaxTop()
    {
        // #195's safety cap is untouched by the #360 probe row: the client sees MaxTop rows, never
        // MaxTop + 1.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingP1IgnoresOptionsProfile>());
        var json = await GetJsonAsync(fx, "/odata/PagingP1IgnoreWidgets");

        Assert.Equal(PageSize, json.GetProperty("value").GetArrayLength());
    }

    /// <summary>
    /// The framework continuation offset must not be silently discarded when a client grafts a
    /// <c>$top</c> onto a continuation link. The read-and-apply pair used to sit inside the
    /// "<c>$top</c> is absent" guard — which exists only to decide whether to CAP the page — so a
    /// follow-up carrying both the token and a <c>$top</c> forgot where the walk had got to and
    /// re-served the FIRST page: a plausible-looking response, i.e. silently wrong data.
    /// <para>
    /// What the offset cannot do on this path is land ahead of the profile's own <c>$top</c>. The
    /// framework composes its <c>Skip</c> onto whatever the profile's <c>ApplyTo</c> already
    /// returned, so for an options-honouring profile the arithmetic is
    /// <c>Take($top).Skip(offset)</c> and the window is legitimately EMPTY once the offset has
    /// passed <c>$top</c> rows — the client declared the result set to be its first <c>$top</c>
    /// rows and has already consumed more than that many. Getting in front of the profile's
    /// <c>ApplyTo</c> is impossible on a path whose whole premise is that the profile owns query
    /// application, and it is exactly why <c>@odata.nextLink</c> is opaque (§11.2.5.7) and grafting
    /// options onto one is out of contract. (The `GetQueryable` path, where the framework owns
    /// skip/take, does yield the shifted window for the equivalent <c>$skiptoken</c> + <c>$top</c>
    /// request.) The invariant pinned here is the one that matters: honoured, not forgotten.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Priority1_ContinuationTokenWithGraftedTop_DoesNotRewindToTheFirstPage()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingPriority1Profile>());

        var page1 = await GetJsonAsync(fx, "/odata/PagingP1Widgets");
        Assert.Equal(new[] { 1, 2, 3, 4, 5 },
            page1.GetProperty("value").EnumerateArray().Select(e => e.GetProperty("Id").GetInt32()).ToArray());

        // The continuation, with a $top the framework never puts there itself, grafted on.
        string next = new Uri(page1.GetProperty("@odata.nextLink").GetString()!).PathAndQuery;
        var json = await GetJsonAsync(fx, next + "&$top=" + PageSize);

        int[] ids = json.GetProperty("value").EnumerateArray()
            .Select(e => e.GetProperty("Id").GetInt32()).ToArray();
        Assert.DoesNotContain(1, ids); // before the fix this came back as page 1 verbatim
        Assert.Empty(ids);             // Take(5) then Skip(5) — see the remarks above
    }

    /// <summary>
    /// The same grafted-<c>$top</c> request against a profile that ignores the options it is handed
    /// — the shape the framework-carried offset exists for. Here nothing has consumed the queryable
    /// ahead of the framework, so the offset lands where it should and the page starts after the
    /// rows already served instead of at row 1.
    /// </summary>
    [Fact]
    public async Task Priority1_ContinuationTokenWithGraftedTop_StillAdvances_WhenProfileIgnoresOptions()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingP1IgnoresOptionsProfile>());

        var page1 = await GetJsonAsync(fx, "/odata/PagingP1IgnoreWidgets");
        string next = new Uri(page1.GetProperty("@odata.nextLink").GetString()!).PathAndQuery;
        var json = await GetJsonAsync(fx, next + "&$top=3");

        int[] ids = json.GetProperty("value").EnumerateArray()
            .Select(e => e.GetProperty("Id").GetInt32()).ToArray();
        // Only the offset is asserted. This profile also ignores $top, so the page length is its
        // own pre-existing business (#379-adjacent) and is deliberately not pinned here.
        Assert.Equal(PageSize + 1, ids[0]); // 6 — before the fix this walk restarted at 1
        Assert.DoesNotContain(1, ids);
    }

    [Fact]
    public async Task Priority1_CorruptedContinuationToken_WithTop_Returns400()
    {
        // The token is validated on the same terms whether or not $top rides along; it used to be
        // read only on the capping branch, so a corrupted token plus a $top returned 200.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingPriority1Profile>());
        var resp = await fx.Client.GetAsync("/odata/PagingP1Widgets?ohdata-skiptoken=not-base64!!&$top=3");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidSkipToken", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Priority1_CorruptedContinuationToken_Returns400()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingPriority1Profile>());
        var resp = await fx.Client.GetAsync("/odata/PagingP1Widgets?ohdata-skiptoken=not-base64!!");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidSkipToken", body.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// The two corruptions above are both rejected by <c>Convert.FromBase64String</c>, so they say
    /// nothing about the OTHER throw site in the reader. A token that is perfectly well-formed
    /// base64 but decodes SHORT gets past the decode and fails in <c>BitConverter.ToInt32</c>
    /// instead, with an <c>ArgumentException</c> — "AAA=" is 2 bytes, "" is 0 (which is
    /// <c>ArgumentOutOfRangeException</c>, a subclass, from the separate empty-array guard).
    /// Both must still be the client's 400, not an unhandled 500: this is the case a narrowing to
    /// <c>FormatException</c> alone would let escape.
    /// </summary>
    [Theory]
    [InlineData("AAA%3D")] // "AAA=" → 2 bytes
    [InlineData("AA%3D%3D")] // "AA==" → 1 byte
    [InlineData("")] // present but empty → 0 bytes
    public async Task Priority1_ContinuationTokenDecodingShort_Returns400(string token)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<PagingPriority1Profile>());
        var resp = await fx.Client.GetAsync("/odata/PagingP1Widgets?ohdata-skiptoken=" + token);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidSkipToken", body.GetProperty("error").GetProperty("code").GetString());
    }

    // --------------------------------------------------------------------- exact-multiple, maxpagesize

    [Fact]
    public async Task PreferMaxPageSize_ExactMultiple_HasNoTrailingEmptyPage_OnEveryPath()
    {
        // Same exact-multiple arithmetic, but with the page size coming from Prefer: maxpagesize
        // rather than MaxTop — 20 rows at maxpagesize=4 is 5 exactly-full pages.
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddEntitySetProfile<PagingQueryableProfile>()
            .AddEntitySetProfile<PagingGetAllProfile>()
            .AddEntitySetProfile<PagingPriority1Profile>());

        string[] urls =
        {
            "/odata/PagingQueryableWidgets",
            "/odata/PagingGetAllWidgets",
            "/odata/PagingP1Widgets",
        };

        foreach (string url in urls)
        {
            var ids = new List<int>();
            string? relative = url;
            int pages = 0;
            while (relative is not null)
            {
                Assert.True(++pages <= 25, $"{url}: walk did not terminate");
                using var request = new HttpRequestMessage(HttpMethod.Get, relative);
                request.Headers.Add("Prefer", "maxpagesize=4");
                var resp = await fx.Client.SendAsync(request);
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

                Assert.True(json.GetProperty("value").GetArrayLength() > 0,
                    $"{url}: page {pages} came back empty — the previous nextLink was spurious.");
                foreach (JsonElement el in json.GetProperty("value").EnumerateArray())
                    ids.Add(el.GetProperty("Id").GetInt32());

                relative = json.TryGetProperty("@odata.nextLink", out JsonElement nl)
                    ? new Uri(nl.GetString()!).PathAndQuery
                    : null;
            }

            Assert.Equal(5, pages);
            AssertServedExactlyOnce(ids, 1, Rows);
        }
    }
}

internal static class PagingStore
{
    // Shared, read-only, in declaration order — every profile below pages the same 20 rows.
    internal static readonly List<Widget> Rows = Enumerable.Range(1, 20)
        .Select(i => new Widget { Id = i, Name = $"Widget{i}" }).ToList();
}

/// <summary>Priority-2 (<c>GetQueryable</c>): the framework owns $filter/$orderby/$skip/$top.</summary>
internal class PagingQueryableProfile : EntitySetProfile<int, Widget>
{
    public PagingQueryableProfile() : base(x => x.Id)
    {
        EntitySetName = "PagingQueryableWidgets";
        CountEnabled = true;
        OrderByEnabled = true;
        MaxTop = 5;
        GetQueryable = () => PagingStore.Rows.AsQueryable();
    }
}

/// <summary>The simple <c>IEnumerable</c> path: paged post-materialization by ApplyGetAllPaging.</summary>
internal class PagingGetAllProfile : EntitySetProfile<int, Widget>
{
    public PagingGetAllProfile() : base(x => x.Id)
    {
        EntitySetName = "PagingGetAllWidgets";
        CountEnabled = true;
        MaxTop = 5;
        GetAll = (ct) => OhDataResult.Success<IEnumerable<Widget>>(PagingStore.Rows);
    }
}

/// <summary>
/// Priority-1, well-behaved: applies the incoming options itself and reports the pre-paging total,
/// leaving the MaxTop cap and the continuation to the framework.
/// </summary>
internal class PagingPriority1Profile : ODataEntitySetProfile<int, Widget>
{
    public PagingPriority1Profile() : base(x => x.Id)
    {
        EntitySetName = "PagingP1Widgets";
        CountEnabled = true;
        OrderByEnabled = true;
        MaxTop = 5;
        GetODataQueryable = (options, ct) => Task.FromResult(new ODataQueryResult<Widget>
        {
            Items = options.ApplyTo(PagingStore.Rows.AsQueryable()) as IQueryable<Widget>
                    ?? PagingStore.Rows.AsQueryable(),
            TotalCount = PagingStore.Rows.Count,
        });
    }
}

/// <summary>
/// Priority-1, badly behaved: returns the same unfiltered, unskipped queryable no matter what it is
/// handed. The framework must still page it correctly — see
/// <see cref="ServerDrivenPagingTests.Priority1_ProfileIgnoringOptions_WalkTerminates_AndServesEveryRowOnce"/>.
/// </summary>
internal class PagingP1IgnoresOptionsProfile : ODataEntitySetProfile<int, Widget>
{
    public PagingP1IgnoresOptionsProfile() : base(x => x.Id)
    {
        EntitySetName = "PagingP1IgnoreWidgets";
        CountEnabled = true;
        MaxTop = 5;
        GetODataQueryable = (options, ct) => Task.FromResult(new ODataQueryResult<Widget>
        {
            Items = PagingStore.Rows.AsQueryable(),
        });
    }
}
