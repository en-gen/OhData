using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #650 — nested <c>$filter</c>/<c>$orderby</c> inside <c>$expand</c> were <b>silently ignored</b> on
/// a delegate-backed navigation, while <c>$top</c>/<c>$skip</c> on the same path correctly answered
/// <c>400</c>. A filtered expansion came back with every row under a <c>200</c>, which is
/// indistinguishable from a filter that matched everything.
/// <para>
/// The spec settles both halves. Nested <c>$filter</c>/<c>$orderby</c>/<c>$count</c>/<c>$top</c> on
/// expanded collections are <b>Advanced</b> conformance (§13.1.3 item 9.2/9.4/9.5/9.6) and this
/// service claims Minimal, so it need not support them — but §11.2.5 is a MUST-fail on an
/// unsupported option and §9.3.1's <c>501</c> sits inside the Minimal MUST list (§13.1.1 item 7), so
/// dropping them silently violates the level actually claimed.
/// </para>
/// <para>
/// <c>400</c> and not <c>501</c>, over-determined three ways: the framework's own test (<i>could any
/// setting on the profile make this request succeed on this route?</i> — declaring the navigation
/// delegate-less is exactly that), <c>Microsoft.AspNetCore.OData</c>'s precedent (a nested option it
/// will not honour throws <c>ODataException</c> from <c>SelectExpandQueryValidator</c> and
/// <c>EnableQueryAttribute</c> turns it into <c>CreateBadRequestResult</c>, never a 501), and
/// <c>$top</c>/<c>$skip</c> one option over on this very path.
/// </para>
/// <para>
/// <c>$count</c> goes the other way and is <b>implemented</b>: the delegate's answer is never
/// windowed, so the materialized array is the full related collection and its length is the count.
/// Refusing something free would be gratuitous.
/// </para>
/// </summary>
public sealed class Issue650NestedOptionsOnDelegateNavTests
{
    public sealed class N650Order
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public List<N650Line> Lines { get; set; } = new();
    }

    public sealed class N650Line
    {
        public int Id { get; set; }
        public string Sku { get; set; } = "";
    }

    private static readonly N650Order[] Store =
    {
        new() { Id = 1, Code = "A" },
        new() { Id = 2, Code = "B" },
    };

    private static readonly Dictionary<int, N650Line[]> LinesByOrder = new()
    {
        [1] = new[] { new N650Line { Id = 10, Sku = "S1" }, new N650Line { Id = 11, Sku = "S2" } },
        [2] = new[] { new N650Line { Id = 20, Sku = "S3" } },
    };

    /// <summary>Delegate-BACKED: the navigation is resolved by a batch handler, never by pushdown.</summary>
    public sealed class N650DelegateProfile : EntitySetProfile<int, N650Order>
    {
        public N650DelegateProfile() : base(x => x.Id)
        {
            EntitySetName = "N650Orders";
            ExpandEnabled = SelectEnabled = true;

            HasMany<N650Line>(
                x => x.Lines,
                batchGetAll: (keys, ct) => Task.FromResult(
                    keys.SelectMany(k => LinesByOrder[k].Select(l => new { Key = k, Line = l }))
                        .ToLookup(x => x.Key, x => x.Line)));

            GetQueryable = () => Store.AsQueryable();
        }
    }

    private static Task<TestFixture> HostAsync() =>
        TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<N650DelegateProfile>());

    [Theory]
    [InlineData("$filter=Sku eq 'S1'", "$filter", "server-side filtering")]
    [InlineData("$orderby=Sku desc", "$orderby", "server-side ordering")]
    public async Task ANestedClauseOption_IsRefused_NotDropped(string nested, string option, string capability)
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync($"/odata/N650Orders?$expand=Lines({nested})");
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("InvalidQueryOption", body, StringComparison.Ordinal);
        // Names the option, the navigation, and the remedy -- the shape $top/$skip already uses and
        // the shape Microsoft.AspNetCore.OData uses ("set the '{1}' property ...").
        Assert.Contains($"A nested {option} is not supported on the delegate-backed navigation 'Lines'",
            body, StringComparison.Ordinal);
        Assert.Contains($"to enable {capability}", body, StringComparison.Ordinal);
        // Never served under a 200 with the option dropped.
        Assert.DoesNotContain("\"S2\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTopSkipMessageIsUnchanged()
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync("/odata/N650Orders?$expand=Lines($top=1)");
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        // Byte-for-byte what #294 shipped: it is quoted in docs and #650 must not move it.
        Assert.Contains(
            "A nested $top/$skip is not supported on the delegate-backed navigation 'Lines'; " +
            "declare it delegate-less (no Handler/BatchHandler) to enable server-side windowing, " +
            "or remove the option.",
            body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANestedCount_IsAnswered_NotRefused()
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync("/odata/N650Orders?$expand=Lines($count=true)");
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"Lines@odata.count\":2", body, StringComparison.Ordinal);
        Assert.Contains("\"Lines@odata.count\":1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANestedSelect_StillApplies_AndAPlainExpandIsUnchanged()
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage sel = await fx.Client.GetAsync("/odata/N650Orders?$expand=Lines($select=Sku)");
        string selBody = await sel.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, sel.StatusCode);
        Assert.Contains("\"Sku\":\"S1\"", selBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Id\":10", selBody, StringComparison.Ordinal);

        HttpResponseMessage plain = await fx.Client.GetAsync("/odata/N650Orders?$expand=Lines");
        string plainBody = await plain.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Contains("\"S1\"", plainBody, StringComparison.Ordinal);
        Assert.Contains("\"S3\"", plainBody, StringComparison.Ordinal);
        // No count unless it was asked for.
        Assert.DoesNotContain("@odata.count", plainBody, StringComparison.Ordinal);
    }
}
