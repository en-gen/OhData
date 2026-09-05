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
/// #664 — a nested <c>$filter</c>/<c>$orderby</c> on a delegate-backed navigation answered
/// <c>500</c> whenever <b>any</b> entity on the page had no related rows.
/// </summary>
/// <remarks>
/// <para>
/// #650's shaper opened with <c>Expression.Convert(src, typeof(IEnumerable&lt;elem&gt;))</c>, but
/// <c>ExpandLevelAsync</c> substitutes <c>Array.Empty&lt;object&gt;()</c> for a parent with no
/// children — on the batch branch (the key misses the dictionary) and on the per-entity branch (a
/// null key) alike. The compiled shaper then cast <c>object[]</c> to <c>IEnumerable&lt;TNav&gt;</c>
/// and threw, so one childless parent anywhere in the page failed the whole request.
/// </para>
/// <para>
/// Every pre-existing nested-option fixture gives each parent at least one child, which is why the
/// suite was green. These fixtures deliberately do not, and the assertion is the ordinary one — the
/// childless parent gets <c>[]</c> and its siblings get the shaped collection.
/// </para>
/// </remarks>
public sealed class Issue664ChildlessParentNestedOptionTests
{
    public sealed class N664Order
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public List<N664Line> Lines { get; set; } = new();
    }

    public sealed class N664Line
    {
        public int Id { get; set; }
        public string Sku { get; set; } = "";
    }

    // Order 2 has no lines at all. That is the whole fixture.
    private static readonly Dictionary<int, N664Line[]> LinesByOrder = new()
    {
        [1] = new[] { new N664Line { Id = 10, Sku = "S2" }, new N664Line { Id = 11, Sku = "S1" } },
        [2] = Array.Empty<N664Line>(),
    };

    private static readonly N664Order[] Store =
    {
        new() { Id = 1, Code = "A" },
        new() { Id = 2, Code = "B" },
    };

    /// <summary>Registered with the BATCH overload — the dictionary simply has no entry for order 2.</summary>
    public sealed class N664BatchProfile : EntitySetProfile<int, N664Order>
    {
        public N664BatchProfile() : base(x => x.Id)
        {
            EntitySetName = "N664Batch";
            ExpandEnabled = SelectEnabled = true;
            GetQueryable = () => Store.AsQueryable();

            HasMany<N664Line>(
                x => x.Lines,
                batchGetAll: (keys, ct) => Task.FromResult(
                    keys.SelectMany(k => LinesByOrder[k].Select(l => new { Key = k, Line = l }))
                        .ToLookup(x => x.Key, x => x.Line)));
        }
    }

    /// <summary>Registered per entity — the delegate returns an empty sequence for order 2.</summary>
    public sealed class N664PerEntityProfile : EntitySetProfile<int, N664Order>
    {
        public N664PerEntityProfile() : base(x => x.Id)
        {
            EntitySetName = "N664PerEntity";
            ExpandEnabled = SelectEnabled = true;
            GetQueryable = () => Store.AsQueryable();

            HasMany<N664Line>(
                x => x.Lines,
                getAll: (key, ct) => Task.FromResult<IEnumerable<N664Line>>(LinesByOrder[key]));
        }
    }

    [Theory]
    [InlineData("N664Batch", "$filter=Sku eq 'S1'")]
    [InlineData("N664Batch", "$orderby=Sku")]
    [InlineData("N664Batch", "$filter=Sku ne 'nope';$orderby=Sku desc")]
    [InlineData("N664PerEntity", "$filter=Sku eq 'S1'")]
    [InlineData("N664PerEntity", "$orderby=Sku")]
    public async Task AChildlessParent_DoesNotFailTheRequest(string set, string nested)
    {
        await using TestFixture fixture = await TestHostBuilder.BuildAsync(o =>
        {
            o.AddEntitySetProfile<N664BatchProfile>();
            o.AddEntitySetProfile<N664PerEntityProfile>();
        });

        HttpResponseMessage response = await fixture.Client.GetAsync(
            $"/odata/{set}?$expand=Lines({nested})");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The childless parent renders an empty array, not an error and not a missing member.
        Assert.Contains("\"Code\":\"B\",\"Lines\":[]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheShapedSibling_IsStillShaped()
    {
        await using TestFixture fixture = await TestHostBuilder.BuildAsync(o =>
            o.AddEntitySetProfile<N664BatchProfile>());

        HttpResponseMessage response = await fixture.Client.GetAsync(
            "/odata/N664Batch?$expand=Lines($orderby=Sku)");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Ordering really was applied -- the fixture stores S2 before S1 -- so the fix restores the
        // request rather than merely stopping it throwing.
        Assert.Contains("\"Sku\":\"S1\"},{\"Id\":10,\"Sku\":\"S2\"}", body, StringComparison.Ordinal);
    }
}
