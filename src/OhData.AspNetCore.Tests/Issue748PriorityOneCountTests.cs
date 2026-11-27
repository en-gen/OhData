using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Query;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class C748Row
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>Priority-1 profile whose handler applies the options itself and counts its calls.</summary>
public sealed class C748P1Profile : ODataEntitySetProfile<int, C748Row>
{
    public static int Calls;

    public C748P1Profile() : base(x => x.Id)
    {
        EntitySetName = "C748P1";
        FilterEnabled = OrderByEnabled = CountEnabled = true;

        GetODataQueryable = (options, _) =>
        {
            Interlocked.Increment(ref Calls);
            var q = new[]
            {
                new C748Row { Id = 1, Name = "a" }, new C748Row { Id = 2, Name = "b" }, new C748Row { Id = 3, Name = "c" },
            }.AsQueryable();
            return Task.FromResult(new ODataQueryResult<C748Row> { Items = options.ApplyTo(q) as IQueryable<C748Row> ?? q });
        };
    }
}

/// <summary>Hard-casts the ApplyTo result, as a profile that assumes ApplyTo always returns an IQueryable of its model.</summary>
public sealed class C748CastProfile : ODataEntitySetProfile<int, C748Row>
{
    public C748CastProfile() : base(x => x.Id)
    {
        EntitySetName = "C748Cast";
        FilterEnabled = OrderByEnabled = CountEnabled = SelectEnabled = ExpandEnabled = true;

        GetODataQueryable = (options, _) =>
        {
            var q = new[]
            {
                new C748Row { Id = 1, Name = "a" }, new C748Row { Id = 2, Name = "b" }, new C748Row { Id = 3, Name = "c" },
            }.AsQueryable();
            return Task.FromResult(new ODataQueryResult<C748Row> { Items = (IQueryable<C748Row>)options.ApplyTo(q) });
        };
    }
}

/// <summary>Validates with settings that do not allow $count, as a profile restricting its own options might.</summary>
public sealed class C748ValidatingProfile : ODataEntitySetProfile<int, C748Row>
{
    public C748ValidatingProfile() : base(x => x.Id)
    {
        EntitySetName = "C748Validating";
        FilterEnabled = OrderByEnabled = CountEnabled = true;

        GetODataQueryable = (options, _) =>
        {
            options.Validate(new Microsoft.AspNetCore.OData.Query.Validator.ODataValidationSettings
            {
                AllowedQueryOptions = AllowedQueryOptions.Filter | AllowedQueryOptions.Top | AllowedQueryOptions.Skip,
            });
            return Task.FromResult(new ODataQueryResult<C748Row> { Items = options.ApplyTo(new[] { new C748Row { Id = 1 } }.AsQueryable()) as IQueryable<C748Row> ?? Enumerable.Empty<C748Row>().AsQueryable() });
        };
    }
}

/// <summary>
/// #748 -- on a Priority-1 profile, /$count must ignore $top/$skip/$orderby (Part 1 §11.2.9) while
/// still applying $filter, even though the profile's own options.ApplyTo would window the result.
/// </summary>
public sealed class Issue748PriorityOneCountTests
{
    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(TestFixture fx, string url)
    {
        var r = await fx.Client.GetAsync(url);
        return (r, await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("$top=2")]
    [InlineData("$skip=1")]
    [InlineData("$top=1&$skip=1")]
    [InlineData("$orderby=Name desc")]
    [InlineData("$top=2&$orderby=Name")]
    public async Task Count_IgnoresWindowAndOrder_EqualsBareCount(string query)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<C748P1Profile>());
        var (bare, bareBody) = await GetAsync(fx, "/odata/C748P1/$count");
        int before = Volatile.Read(ref C748P1Profile.Calls);

        var (r, body) = await GetAsync(fx, "/odata/C748P1/$count?" + query);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("3", body);
        Assert.Equal(bareBody, body);
        Assert.Equal(bare.Content.Headers.ContentType?.ToString(), r.Content.Headers.ContentType?.ToString());
        Assert.StartsWith("text/plain", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bare.Headers.Select(h => h.Key).OrderBy(k => k), r.Headers.Select(h => h.Key).OrderBy(k => k));
        Assert.Equal(before + 1, Volatile.Read(ref C748P1Profile.Calls)); // handler was reached
    }

    [Fact]
    public async Task Count_StillAppliesFilter()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<C748P1Profile>());
        var (r, body) = await GetAsync(fx, "/odata/C748P1/$count?$filter=Id gt 1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("2", body);
    }

    [Fact]
    public async Task Count_FilterWithTop_CountsFilteredTotal()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<C748P1Profile>());
        var (_, body) = await GetAsync(fx, "/odata/C748P1/$count?$filter=Id gt 1&$top=1");
        Assert.Equal("2", body);
    }

    [Fact]
    public async Task Collection_Top_StillWindows()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<C748P1Profile>());
        var (r, body) = await GetAsync(fx, "/odata/C748P1?$top=2");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("value").GetArrayLength());
    }

    [Theory]
    [InlineData("$select=Name")]
    [InlineData("$expand=*")]
    [InlineData("$top=-1")]
    [InlineData("$top=abc")]
    public async Task Count_HardCastProfile_IgnoresOptionsThatCannotChangeTheCount(string query)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<C748CastProfile>());
        var (r, body) = await GetAsync(fx, "/odata/C748Cast/$count?" + query);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("3", body);
    }

    [Fact]
    public async Task Count_ProfileValidationDisallowingCount_SeesACountRequest()
    {
        // Pins current behaviour: the profile's own Validate sees IsCountRequest (ODataQueryValidator.cs:78).
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<C748ValidatingProfile>());
        var (r, _) = await GetAsync(fx, "/odata/C748Validating/$count");
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
    }
}
