using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class R18Row { public int Id { get; set; } public string Name { get; set; } = ""; }

public sealed class R18MalformedTokenException : Exception
{
    public R18MalformedTokenException(string token) : base("malformed token " + token) => Token = token;
    public string Token { get; }
}

public sealed class R18GoneTokenException : Exception
{
    public R18GoneTokenException(int afterId) : base("row gone " + afterId) => AfterId = afterId;
    public int AfterId { get; }
}

/// <summary>Counts how many times each mapping ran, so a test proves the mapping fired.</summary>
public sealed class R18MappingProbe
{
    public int MalformedMapped;
    public int GoneMapped;
}

/// <summary>
/// A top-level <c>GetODataQueryable</c> handler that owns its keyset <c>$skiptoken</c> and rejects
/// a bad one by throwing. Whether the throw is mapped is the fixture's switch, so the same handler
/// serves as both the red and the green case.
/// </summary>
public abstract class R18KeysetProfileBase : ODataEntitySetProfile<int, R18Row>
{
    private static readonly int[] Ids = { 1, 2, 3, 4, 5 };

    protected R18KeysetProfileBase(R18MappingProbe probe, bool mapExceptions) : base(x => x.Id)
    {
        EntitySetName = "R18Rows";

        GetODataQueryable = (options, _) =>
        {
            string? token = options.RawValues.SkipToken;
            int after = 0;
            if (token is not null)
            {
                if (!int.TryParse(token, out after)) throw new R18MalformedTokenException(token);
                if (!Ids.Contains(after)) throw new R18GoneTokenException(after);
            }

            IQueryable<R18Row> page = Ids.Where(i => i > after)
                .Select(i => new R18Row { Id = i, Name = "row" + i }).AsQueryable();
            return Task.FromResult(new ODataQueryResult<R18Row> { Items = page });
        };

        if (mapExceptions)
        {
            ConfigureExceptions(e => e
                .Map<R18MalformedTokenException>((ctx, ex) =>
                {
                    Interlocked.Increment(ref probe.MalformedMapped);
                    return OhDataResult.BadRequest("InvalidSkipToken", $"Bad token '{ex.Token}' on {ctx.EntitySetName}.");
                })
                .Map<R18GoneTokenException>((ctx, ex) =>
                {
                    Interlocked.Increment(ref probe.GoneMapped);
                    return OhDataResult.Conflict("SkipTokenGone", $"Row {ex.AfterId} no longer exists on {ctx.EntitySetName}.");
                }));
        }
    }
}

public sealed class R18MappedKeysetProfile : R18KeysetProfileBase
{
    public R18MappedKeysetProfile(R18MappingProbe probe) : base(probe, mapExceptions: true) { }
}

public sealed class R18UnmappedKeysetProfile : R18KeysetProfileBase
{
    public R18UnmappedKeysetProfile(R18MappingProbe probe) : base(probe, mapExceptions: false) { }
}

/// <summary>
/// Release 1.8.0: a handler that owns a keyset <c>$skiptoken</c> answers a client error for a bad
/// token via <c>ConfigureExceptions</c>, where 1.7.0 could only answer 500.
/// </summary>
public sealed class Release18SkipTokenMappingTests
{
    private static async Task<(HttpStatusCode Status, JsonElement Error)> GetAsync(
        TestFixture fx, string url)
    {
        var response = await fx.Client.GetAsync(url);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body.GetProperty("error"));
    }

    private static Task<TestFixture> BuildAsync<TProfile>(R18MappingProbe probe)
        where TProfile : class, IEntitySetProfile =>
        TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<TProfile>(),
            configureServices: s => s.AddSingleton(probe));

    [Fact]
    public async Task MalformedSkipToken_AnswersTheMapped400()
    {
        var probe = new R18MappingProbe();
        await using var fx = await BuildAsync<R18MappedKeysetProfile>(probe);

        var (status, error) = await GetAsync(fx, "/odata/R18Rows?$skiptoken=not-a-number");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("InvalidSkipToken", error.GetProperty("code").GetString());
        Assert.Equal("Bad token 'not-a-number' on R18Rows.", error.GetProperty("message").GetString());
        Assert.Equal(1, probe.MalformedMapped);
        Assert.Equal(0, probe.GoneMapped);
    }

    [Fact]
    public async Task SkipTokenForGoneData_AnswersTheMapped409()
    {
        var probe = new R18MappingProbe();
        await using var fx = await BuildAsync<R18MappedKeysetProfile>(probe);

        var (status, error) = await GetAsync(fx, "/odata/R18Rows?$skiptoken=99");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("SkipTokenGone", error.GetProperty("code").GetString());
        Assert.Equal("Row 99 no longer exists on R18Rows.", error.GetProperty("message").GetString());
        Assert.Equal(1, probe.GoneMapped);
        Assert.Equal(0, probe.MalformedMapped);
    }

    [Fact]
    public async Task ValidSkipToken_IsServedAndNothingIsMapped()
    {
        var probe = new R18MappingProbe();
        await using var fx = await BuildAsync<R18MappedKeysetProfile>(probe);

        var response = await fx.Client.GetAsync("/odata/R18Rows?$skiptoken=3");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { 4, 5 }, body.GetProperty("value").EnumerateArray()
            .Select(r => r.GetProperty("Id").GetInt32()).ToArray());
        Assert.Equal(0, probe.MalformedMapped + probe.GoneMapped);
    }

    // The control, and the 1.7.0 behaviour: the same throws with no mapping are an opaque 500.
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("99")]
    public async Task WithoutTheMapping_TheSameBadTokenIsAnOpaque500(string token)
    {
        var probe = new R18MappingProbe();
        await using var fx = await BuildAsync<R18UnmappedKeysetProfile>(probe);

        var (status, error) = await GetAsync(fx, "/odata/R18Rows?$skiptoken=" + token);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("InternalServerError", error.GetProperty("code").GetString());
        Assert.Equal(0, probe.MalformedMapped + probe.GoneMapped);
    }
}
