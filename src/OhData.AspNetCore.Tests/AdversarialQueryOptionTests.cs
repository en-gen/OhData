using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Adversarial coverage for OData query options ($top, $skip, $filter, $orderby, $select,
/// $expand) and the <c>Prefer: maxpagesize</c> header on GET collection endpoints. This package
/// is about to be published — consumers' clients will send garbage query strings, and the
/// framework must degrade gracefully (400 + OData error envelope, or documented fallback
/// behavior) with reasonable latency, never a 500 or a hang.
///
/// All fixtures here use the <c>GetQueryable</c> handler path with query options explicitly
/// enabled, because the <c>GetAll</c> path rejects every query option wholesale (see
/// QueryOptionEnforcementTests) and would not exercise the adversarial-input parsing this suite
/// targets.
/// </summary>
public class AdversarialQueryOptionTests
{
    private const string Url = "/odata/AdversarialWidgets";

    // ── $top / $skip ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Top_NegativeValue_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        var response = await fx.Client.GetAsync(Url + "?$top=-1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Top_NonNumericValue_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        var response = await fx.Client.GetAsync(Url + "?$top=notanumber");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Skip_NegativeValue_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        var response = await fx.Client.GetAsync(Url + "?$skip=-5");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    // ── $filter ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Filter_UnbalancedParens_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        var response = await fx.Client.GetAsync(Url + "?$filter=" + Uri.EscapeDataString("(Name eq 'x'"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Filter_MissingOperand_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        var response = await fx.Client.GetAsync(Url + "?$filter=" + Uri.EscapeDataString("Name eq"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Filter_NonexistentProperty_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        var response = await fx.Client.GetAsync(Url + "?$filter=" + Uri.EscapeDataString("DoesNotExist eq 'x'"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Filter_ExtremelyLong_10kChars_DoesNotHang_ReturnsQuickly()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        string longFilter = "Name eq '" + new string('x', 10_000) + "'";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await fx.Client.GetAsync(Url + "?$filter=" + Uri.EscapeDataString(longFilter));
        sw.Stop();

        // Syntactically valid (just an absurdly long literal) — matches nothing, but must not
        // hang or 500.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 200 or 400, got {(int)response.StatusCode}");
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Expected a fast response, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Filter_100NestedNot_Returns400ODataError_NeverHangsOrCrashes()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        string nestedNot = string.Concat(Enumerable.Repeat("not(", 100)) + "Name eq 'x'" + string.Concat(Enumerable.Repeat(")", 100));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await fx.Client.GetAsync(Url + "?$filter=" + Uri.EscapeDataString(nestedNot));
        sw.Stop();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Expected a fast response, took {sw.ElapsedMilliseconds}ms");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    // ── $orderby / $select / $expand ────────────────────────────────────────────────

    [Fact]
    public async Task OrderBy_NonexistentProperty_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        var response = await fx.Client.GetAsync(Url + "?$orderby=DoesNotExist");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Select_NonexistentProperty_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        var response = await fx.Client.GetAsync(Url + "?$select=DoesNotExist");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Expand_NonexistentNavigation_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        var response = await fx.Client.GetAsync(Url + "?$expand=DoesNotExist");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    // ── Prefer: maxpagesize abuse ────────────────────────────────────────────────────

    [Fact]
    public async Task Prefer_MaxPageSizeNegative_IgnoredGracefully_Returns200WithFullResults()
    {
        // Documented behavior: an invalid maxpagesize preference is ignored rather than rejected —
        // the server falls back to returning the full (unpaged) result set.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("Prefer", "maxpagesize=-1");

        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AdversarialQueryProfile.TotalCount, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task Prefer_MaxPageSizeNonNumeric_IgnoredGracefully_Returns200WithFullResults()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("Prefer", "maxpagesize=notanumber");

        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AdversarialQueryProfile.TotalCount, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task Prefer_MaxPageSizeOverflowsInt32_IgnoredGracefully_NeverA500()
    {
        // 2147483648 = int.MaxValue + 1.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("Prefer", "maxpagesize=2147483648");

        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Combined adversarial query strings never hang or 500 ───────────────────────

    [Fact]
    public async Task CombinedAdversarialQueryOptions_NeverHangsOrCrashes()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<AdversarialQueryProfile>());
        string query = "?$top=-1&$skip=-5&$filter=" + Uri.EscapeDataString("DoesNotExist eq 'x' and (Name eq")
            + "&$orderby=AlsoMissing&$select=Nope&$expand=Nada";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await fx.Client.GetAsync(Url + query);
        sw.Stop();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Expected a fast response, took {sw.ElapsedMilliseconds}ms");

        // Connection must remain usable afterward.
        var followUp = await fx.Client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────

    private class AdversarialWidget { public int Id { get; set; } public string Name { get; set; } = ""; }

    /// <summary>
    /// GetQueryable-backed profile with $filter/$orderby/$select/$expand/$count all enabled so
    /// ODataQueryOptions parsing (and its exception handling) is fully exercised. $expand has no
    /// actual navigation properties configured, which is exactly what we want for the
    /// nonexistent-navigation test.
    /// </summary>
    private class AdversarialQueryProfile : EntitySetProfile<int, AdversarialWidget>
    {
        public const int TotalCount = 5;

        private static readonly List<AdversarialWidget> Store = Enumerable.Range(1, TotalCount)
            .Select(i => new AdversarialWidget { Id = i, Name = $"Widget{i}" })
            .ToList();

        public AdversarialQueryProfile() : base(x => x.Id)
        {
            EntitySetName = "AdversarialWidgets";
            FilterEnabled = true;
            OrderByEnabled = true;
            SelectEnabled = true;
            ExpandEnabled = true;
            CountEnabled = true;

            GetQueryable = (ct) => Task.FromResult(Store.AsQueryable());
        }
    }
}
