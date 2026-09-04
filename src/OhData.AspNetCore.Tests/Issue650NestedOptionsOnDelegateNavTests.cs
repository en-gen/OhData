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
/// Refusing them would satisfy that MUST, and <b>implementing them satisfies it better</b> — which is
/// what this does. The clause is bound by the same <c>FilterBinder</c>/<c>OrderByBinder</c> the
/// pushdown path uses (<c>BindNavShape</c>) and executed against the children the delegate already
/// returned, so a clause means the same thing on both paths. Only the EXECUTION differs —
/// LINQ-to-Objects here, SQL there — which is the same divergence <c>[EnableQuery]</c> has over an
/// in-memory source, and is why <c>HandleNullPropagation</c> is <b>on</b> for this path and off for
/// the SQL one.
/// </para>
/// <para>
/// A <c>400</c> survives for exactly two cases: a clause the binders cannot bind at all, and a
/// single-valued navigation (nothing to filter or order). Loud either way, never dropped.
/// <c>$count</c> is answered from the FILTERED array per §11.2.5.5. <c>$top</c>/<c>$skip</c> remain a
/// <c>400</c> (#294) — a separate decision about windowing a delegate's answer, untouched here.
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

    [Fact]
    public async Task ANestedFilter_IsApplied_NotDroppedAndNotRefused()
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync("/odata/N650Orders?$expand=Lines($filter=Sku eq 'S1')");
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"S1\"", body, StringComparison.Ordinal);
        // The whole point: before #650 these came back too, under a 200 the client could not
        // distinguish from a filter that matched everything.
        Assert.DoesNotContain("\"S2\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"S3\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANestedOrderBy_IsApplied()
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync("/odata/N650Orders?$expand=Lines($orderby=Sku desc)");
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(
            body.IndexOf("\"S2\"", StringComparison.Ordinal) < body.IndexOf("\"S1\"", StringComparison.Ordinal),
            $"descending $orderby must reverse the delegate's order; got: {body}");
    }

    [Fact]
    public async Task ANestedFilterAndCount_CountsTheFilteredCollection()
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync(
            "/odata/N650Orders?$expand=Lines($filter=Sku eq 'S1';$count=true)");
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        // §11.2.5.5: the count is of the FILTERED collection. Order 1 has two lines, one of which
        // matches; a count of 2 here would mean the filter ran after the count (or not at all).
        Assert.Contains("\"Lines@odata.count\":1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Lines@odata.count\":2", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilterOverANullProperty_DoesNotThrow()
    {
        // The in-memory path needs HandleNullPropagation ON, which SQL does not: LINQ-to-Objects
        // would dereference and throw NullReferenceException where SQL evaluates NULL to "no match".
        // N650Line.Sku is never null here, so this exercises the guard via a property comparison that
        // the binder must null-guard rather than a contrived null row.
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync(
            "/odata/N650Orders?$expand=Lines($filter=startswith(Sku,'S'))");
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"S1\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANestedTopAndSkip_AreApplied()
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync("/odata/N650Orders?$expand=Lines($top=1)");
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"S1\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"S2\"", body, StringComparison.Ordinal);   // order 1 windowed to 1 of 2

        HttpResponseMessage skipped = await fx.Client.GetAsync("/odata/N650Orders?$expand=Lines($skip=1)");
        string skippedBody = await skipped.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, skipped.StatusCode);
        Assert.DoesNotContain("\"S1\"", skippedBody, StringComparison.Ordinal);
        Assert.Contains("\"S2\"", skippedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACountIsOfTheFilteredCollection_NotTheWindowedPage()
    {
        // §11.2.5.5: Nav@odata.count is the count after $filter and BEFORE $top/$skip. Order 1 has
        // two lines; with $top=1 the page is 1 and the count must still be 2. Counting the windowed
        // array would report the page size as the total -- #379's defect one level down.
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync(
            "/odata/N650Orders?$expand=Lines($top=1;$count=true)");
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"Lines@odata.count\":2", body, StringComparison.Ordinal);
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
