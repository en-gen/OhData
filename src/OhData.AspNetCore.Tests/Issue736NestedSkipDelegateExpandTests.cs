using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #736: a nested <c>$skip</c> on a delegate-backed navigation windowed the serialized JSON array but
/// not the CLR list, so a deeper <c>$expand</c> paired CLR element j with array element j of a different
/// row and the grandchild delegate was handed the wrong keys.
/// </summary>
public sealed class Issue736NestedSkipDelegateExpandTests
{
    public sealed class VA { public int Id { get; set; } public List<VL> Lines { get; set; } = new(); }
    public sealed class VL
    {
        public int Id { get; set; }
        public VP? Product { get; set; }
        public List<VT> Tags { get; set; } = new();
    }
    public sealed class VP { public int Id { get; set; } }
    public sealed class VT { public int Id { get; set; } }

    // Tests in one class run serially, so these static recorders are safe; each test clears them.
    private static readonly List<int> ProductKeys = new();
    private static readonly List<int> TagKeys = new();
    private static bool s_linesUseBatch;

    private static VL[] NewLines() => new[]
    {
        new VL { Id = 10 }, new VL { Id = 11 }, new VL { Id = 12 },
    };

    public sealed class VAProfile : EntitySetProfile<int, VA>
    {
        public VAProfile() : base(x => x.Id)
        {
            EntitySetName = "VAs";
            ExpandEnabled = SelectEnabled = true;

            if (s_linesUseBatch)
            {
                HasMany<VL>(
                    x => x.Lines,
                    batchGetAll: (keys, ct) => Task.FromResult(
                        keys.SelectMany(k => NewLines().Select(l => new { Key = k, Line = l }))
                            .ToLookup(x => x.Key, x => x.Line)));
            }
            else
            {
                HasMany<VL>(x => x.Lines, (key, ct) => Task.FromResult<IEnumerable<VL>>(NewLines()));
            }

            GetQueryable = () => new[] { new VA { Id = 1 } }.AsQueryable();
            GetById = (id, ct) => OhDataResult.Success<VA?>(id == 1 ? new VA { Id = 1 } : null);
        }
    }

    public sealed class VLProfile : EntitySetProfile<int, VL>
    {
        public VLProfile() : base(x => x.Id)
        {
            EntitySetName = "VLs";
            ExpandEnabled = SelectEnabled = FilterEnabled = OrderByEnabled = CountEnabled = true;

            HasOptional<VP>(
                x => x.Product!,
                (key, ct) =>
                {
                    lock (ProductKeys) ProductKeys.Add(key);
                    return Task.FromResult<VP?>(new VP { Id = key });
                },
                refTargetEntitySet: null);

            HasMany<VT>(
                x => x.Tags,
                batchGetAll: (keys, ct) =>
                {
                    lock (TagKeys) TagKeys.AddRange(keys);
                    return Task.FromResult(
                        keys.Select(k => new { Key = k, Tag = new VT { Id = k } }).ToLookup(x => x.Key, x => x.Tag));
                });

            GetQueryable = () => NewLines().AsQueryable();
        }
    }

    private static Task<TestFixture> HostAsync(bool batchLines = false)
    {
        s_linesUseBatch = batchLines;
        lock (ProductKeys) ProductKeys.Clear();
        lock (TagKeys) TagKeys.Clear();
        return TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<VAProfile>().AddEntitySetProfile<VLProfile>());
    }

    private static async Task<JsonElement> GetOkAsync(TestFixture fx, string url)
    {
        HttpResponseMessage res = await fx.Client.GetAsync(url);
        string body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement LinesOfFirst(JsonElement root) =>
        (root.TryGetProperty("value", out JsonElement v) ? v[0] : root).GetProperty("Lines");

    private static void AssertLinesCarryOwnProduct(JsonElement lines, params int[] ids)
    {
        Assert.Equal(ids, lines.EnumerateArray().Select(l => l.GetProperty("Id").GetInt32()).ToArray());
        foreach (JsonElement l in lines.EnumerateArray())
        {
            Assert.Equal(l.GetProperty("Id").GetInt32(), l.GetProperty("Product").GetProperty("Id").GetInt32());
        }
    }

    [Theory]
    [InlineData("/odata/VAs?$expand=Lines($expand=Product)", new[] { 10, 11, 12 })]
    [InlineData("/odata/VAs?$expand=Lines($skip=1;$expand=Product)", new[] { 11, 12 })]
    [InlineData("/odata/VAs?$expand=Lines($skip=1;$top=1;$expand=Product)", new[] { 11 })]
    public async Task EachLineCarriesItsOwnProduct(string url, int[] ids)
    {
        TestFixture fx = await HostAsync();
        AssertLinesCarryOwnProduct(LinesOfFirst(await GetOkAsync(fx, url)), ids);
    }

    [Theory]
    [InlineData("/odata/VAs?$expand=Lines($skip=1;$expand=Product)", new[] { 11, 12 })]
    [InlineData("/odata/VAs?$expand=Lines($skip=1;$top=1;$expand=Product)", new[] { 11 })]
    public async Task BatchBackedNavigation_EachLineCarriesItsOwnProduct(string url, int[] ids)
    {
        TestFixture fx = await HostAsync(batchLines: true);
        AssertLinesCarryOwnProduct(LinesOfFirst(await GetOkAsync(fx, url)), ids);
    }

    [Theory]
    [InlineData("/odata/VAs?$expand=Lines($skip=1;$expand=Product)", new[] { 11, 12 })]
    [InlineData("/odata/VAs?$expand=Lines($skip=1;$top=1;$expand=Product)", new[] { 11 })]
    [InlineData("/odata/VAs?$expand=Lines($skip=2;$expand=Product)", new[] { 12 })]
    public async Task TheGrandchildDelegate_IsCalledOnlyForRowsInTheResponse(string url, int[] ids)
    {
        TestFixture fx = await HostAsync();
        await GetOkAsync(fx, url);
        lock (ProductKeys) Assert.Equal(ids.OrderBy(i => i), ProductKeys.OrderBy(i => i));
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("2147483647")]
    public async Task AHugeNestedTop_WithASkip_ReturnsTheRestWithoutOverflow(string top)
    {
        TestFixture fx = await HostAsync();
        JsonElement root = await GetOkAsync(fx, $"/odata/VAs?$expand=Lines($skip=1;$top={top};$expand=Product)");
        AssertLinesCarryOwnProduct(LinesOfFirst(root), 11, 12);
    }

    [Fact]
    public async Task ABatchGrandchild_ReceivesOnlyTheKeysOfRowsInTheResponse()
    {
        TestFixture fx = await HostAsync();
        JsonElement lines = LinesOfFirst(await GetOkAsync(fx, "/odata/VAs?$expand=Lines($skip=1;$expand=Tags)"));

        foreach (JsonElement l in lines.EnumerateArray())
        {
            Assert.Equal(l.GetProperty("Id").GetInt32(), l.GetProperty("Tags")[0].GetProperty("Id").GetInt32());
        }
        lock (TagKeys) Assert.Equal(new[] { 11, 12 }, TagKeys.OrderBy(i => i));
    }

    [Fact]
    public async Task AByKeyRead_WithANestedSkipAndExpand_CarriesEachLinesOwnProduct()
    {
        TestFixture fx = await HostAsync();
        JsonElement root = await GetOkAsync(fx, "/odata/VAs(1)?$expand=Lines($skip=1;$expand=Product)");
        AssertLinesCarryOwnProduct(root.GetProperty("Lines"), 11, 12);
        lock (ProductKeys) Assert.Equal(new[] { 11, 12 }, ProductKeys.OrderBy(i => i));
    }

    [Fact]
    public async Task ANestedCount_WithASkip_StillReportsTheFullTotal()
    {
        TestFixture fx = await HostAsync();
        JsonElement root = await GetOkAsync(fx, "/odata/VAs?$expand=Lines($count=true;$skip=1;$expand=Product)");
        JsonElement va = root.GetProperty("value")[0];

        Assert.Equal(3, va.GetProperty("Lines@odata.count").GetInt32());
        AssertLinesCarryOwnProduct(va.GetProperty("Lines"), 11, 12);
    }

    [Fact]
    public async Task ANestedFilterAndOrderBy_WithASkip_KeepTheListAndArrayAligned()
    {
        TestFixture fx = await HostAsync();
        JsonElement root = await GetOkAsync(
            fx, "/odata/VAs?$expand=Lines($filter=Id ge 11;$orderby=Id desc;$count=true;$skip=1;$expand=Product)");
        JsonElement va = root.GetProperty("value")[0];

        Assert.Equal(2, va.GetProperty("Lines@odata.count").GetInt32());
        AssertLinesCarryOwnProduct(va.GetProperty("Lines"), 11);
        lock (ProductKeys) Assert.Equal(new[] { 11 }, ProductKeys.OrderBy(i => i));
    }
}
