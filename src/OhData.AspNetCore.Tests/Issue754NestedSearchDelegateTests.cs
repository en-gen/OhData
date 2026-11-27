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

// #754: a nested $search inside $expand was silently dropped on a delegate navigation (every child
// returned under a 200). It is refused with 501 before any handler runs, on every read route.
public sealed class Issue754NestedSearchDelegateTests
{
    public sealed class N754Order
    {
        public int Id { get; set; }
        public List<N754Line> Lines { get; set; } = new();
    }

    public sealed class N754Line
    {
        public int Id { get; set; }
        public string Sku { get; set; } = "";
    }

    private static readonly N754Order[] Store = { new() { Id = 1 }, new() { Id = 2 } };

    public sealed class N754Node
    {
        public int Id { get; set; }
        public List<N754Node> Children { get; set; } = new();
    }

    private static int s_delegateCalls;
    private static int s_handlerCalls;

    private static Task<ILookup<int, N754Line>> Lines(IReadOnlyCollection<int> keys, CancellationToken ct)
    {
        Interlocked.Increment(ref s_delegateCalls);
        return Task.FromResult(keys.Select(k => new { Key = k, Line = new N754Line { Id = k, Sku = "S" + k } })
            .ToLookup(x => x.Key, x => x.Line));
    }

    public sealed class QueryableProfile : EntitySetProfile<int, N754Order>
    {
        public QueryableProfile() : base(x => x.Id)
        {
            EntitySetName = "N754Queryables";
            ExpandEnabled = true;
            HasMany<N754Line>(x => x.Lines, batchGetAll: (keys, ct) => Lines(keys, ct));
            GetQueryable = () => Store.AsQueryable();
            GetById = (id, ct) => OhDataResult.Success<N754Order?>(Store.FirstOrDefault(o => o.Id == id));
        }
    }

    public sealed class GetAllProfile : EntitySetProfile<int, N754Order>
    {
        public GetAllProfile() : base(x => x.Id)
        {
            EntitySetName = "N754GetAlls";
            ExpandEnabled = true;
            HasMany<N754Line>(x => x.Lines, batchGetAll: (keys, ct) => Lines(keys, ct));
            GetAll = ct => OhDataResult.Success<IEnumerable<N754Order>>(Store);
        }
    }

    public sealed class NodeProfile : EntitySetProfile<int, N754Node>
    {
        public NodeProfile() : base(x => x.Id)
        {
            EntitySetName = "N754Nodes";
            ExpandEnabled = true;
            HasMany<N754Node>(
                x => x.Children,
                batchGetAll: (keys, ct) =>
                {
                    Interlocked.Increment(ref s_delegateCalls);
                    return Task.FromResult(keys.Select(k => new N754Node { Id = k + 100 }).ToLookup(n => n.Id - 100));
                });
            GetQueryable = () => new[] { new N754Node { Id = 1 } }.AsQueryable();
        }
    }

    public sealed class PriorityOneProfile : ODataEntitySetProfile<int, N754Order>
    {
        public PriorityOneProfile() : base(x => x.Id)
        {
            EntitySetName = "N754P1s";
            ExpandEnabled = true;
            HasMany(x => x.Lines);
            GetODataQueryable = (options, _) =>
            {
                Interlocked.Increment(ref s_handlerCalls);
                return Task.FromResult(new ODataQueryResult<N754Order> { Items = Store.AsQueryable() });
            };
        }
    }

    private static Task<TestFixture> HostAsync()
    {
        Interlocked.Exchange(ref s_delegateCalls, 0);
        Interlocked.Exchange(ref s_handlerCalls, 0);
        return TestHostBuilder.BuildAsync(b =>
        {
            b.AddEntitySetProfile<QueryableProfile>();
            b.AddEntitySetProfile<GetAllProfile>();
            b.AddEntitySetProfile<NodeProfile>();
            b.AddEntitySetProfile<PriorityOneProfile>();
        });
    }

    [Theory]
    [InlineData("/odata/N754Queryables?$expand=Lines($search=zzz)")]
    [InlineData("/odata/N754Queryables(1)?$expand=Lines($search=zzz)")]
    [InlineData("/odata/N754GetAlls?$expand=Lines($search=zzz)")]
    public async Task ANestedSearch_Is501_AndTheDelegateIsNeverReached(string url)
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync(url);
        string body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        Assert.Contains("UnsupportedQueryOption", body, StringComparison.Ordinal);
        Assert.Contains("$search", body, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref s_delegateCalls));
        Assert.Equal(0, Volatile.Read(ref s_handlerCalls));
    }

    [Fact]
    public async Task ANestedSearch_AtDepthTwo_Is501_AndNoDelegateRuns()
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync(
            "/odata/N754Nodes?$expand=Children($expand=Children($search=zzz))");

        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        Assert.Equal(0, Volatile.Read(ref s_delegateCalls));
    }

    [Fact]
    public async Task ANestedSearch_OnAPriorityOneRoute_Is501_AndTheProfileHandlerIsNotReached()
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync("/odata/N754P1s?$expand=Lines($search=zzz)");

        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        Assert.Equal(0, Volatile.Read(ref s_handlerCalls));
    }

    [Theory]
    [InlineData("/odata/N754GetAlls?$expand=Lines($filter=Sku eq 'S1')")]
    [InlineData("/odata/N754Queryables(1)?$expand=Lines($filter=Sku eq 'S1')")]
    public async Task ANestedFilter_OnTheOtherRoutes_StillAnswers200_AndReachesTheDelegate(string url)
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(Volatile.Read(ref s_delegateCalls) > 0);
    }

    [Fact]
    public async Task ANestedFilter_OnTheSameNavigation_StillAnswers200_AndReachesTheDelegate()
    {
        TestFixture fx = await HostAsync();

        HttpResponseMessage res = await fx.Client.GetAsync("/odata/N754Queryables?$expand=Lines($filter=Sku eq 'S1')");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(Volatile.Read(ref s_delegateCalls) > 0);
    }
}
