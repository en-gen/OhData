using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Mapper.Tests;

/// <summary>
/// What the mapped profile owns that the conformance oracle cannot check: that the query really
/// reached the database, that paging is bounded and continues correctly, and that an option the
/// profile does not honour is refused rather than dropped.
/// </summary>
public sealed class MappedProfileBehaviourTests
{
    private readonly ITestOutputHelper _out;

    public MappedProfileBehaviourTests(ITestOutputHelper output) => _out = output;

    // ── Pushdown: the claim the whole package rests on ────────────────────────────────────────

    [Theory]
    [InlineData("Title eq 'Hammer'", "\"p\".\"Name\" =")]
    [InlineData("CategoryName eq 'Tools'", "\"c\".\"Name\" =")]
    [InlineData("DisplayName eq 'Ada Lovelace'", "||")]
    [InlineData("Tags/any(t: t/Label eq 'sale')", "EXISTS")]
    [InlineData("contains(Title,'amm')", "instr")]
    public void AFilterOverAMappedMember_ReachesSql(string filter, string expectedSql)
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        using MapDb db = MapDb.Seeded(connection);

        string sql = Sql(db, filter);
        _out.WriteLine(sql);

        // The point is not that the request succeeds -- it is that nothing was evaluated on the
        // client. A member the provider could not translate would have thrown here, and a member
        // silently evaluated in memory would leave no trace of itself in the statement.
        Assert.Contains(expectedSql, sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Title", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrderByOverAPathMember_ReachesSqlAsAJoin()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        using MapDb db = MapDb.Seeded(connection);

        MappedQueryComposer<Product, ProductDto> composer = Composer();
        Microsoft.OData.UriParser.OrderByClause clause = OData.ParseOrderBy("CategoryName desc");

        IQueryable<Product> query = composer.ApplyOrderBy(db.Products.AsNoTracking(), clause);
        string sql = query.ToQueryString();
        _out.WriteLine(sql);

        Assert.Contains("JOIN", sql, StringComparison.Ordinal);
        Assert.Contains("DESC", sql, StringComparison.Ordinal);
    }

    private static string Sql(MapDb db, string filter)
    {
        MappedQueryComposer<Product, ProductDto> composer = Composer();
        return composer
            .ApplyFilter(db.Products.AsNoTracking(), OData.ParseFilter(filter))
            .ToQueryString();
    }

    private static MappedQueryComposer<Product, ProductDto> Composer()
    {
        ModelMapRegistry registry = Maps.Registry();
        return new MappedQueryComposer<Product, ProductDto>(
            registry.Find(typeof(ProductDto))!, registry, OData.Model);
    }

    // ── Paging ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APageBeyondTheCeiling_IsCappedAndContinued()
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        JsonObject page = await host.GetJsonAsync($"/odata/{MappedTestHost.Paged}");

        Assert.Equal(2, page["value"]!.AsArray().Count);
        Assert.NotNull(page["@odata.nextLink"]);
        Assert.Contains("%24skip=2", page["@odata.nextLink"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WalkingTheContinuations_VisitsEveryRowExactlyOnce()
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        var seen = new List<int>();
        string? url = $"/odata/{MappedTestHost.Paged}";

        while (url is not null)
        {
            JsonObject page = await host.GetJsonAsync(url);
            foreach (JsonNode? row in page["value"]!.AsArray())
                seen.Add(row!["Id"]!.GetValue<int>());

            url = page["@odata.nextLink"]?.GetValue<string>();
        }

        // Three rows, each once, in key order -- the stabiliser doing its job. Without it a page
        // boundary over an unordered set can repeat a row and skip another.
        Assert.Equal(new[] { 1, 2, 3 }, seen.ToArray());
    }

    [Fact]
    public async Task TheFinalPage_CarriesNoContinuation()
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        JsonObject page = await host.GetJsonAsync($"/odata/{MappedTestHost.Paged}?$skip=2");

        Assert.Single(page["value"]!.AsArray());

        // An exactly-consumed set must not hand the client a link into an empty trailing page.
        Assert.Null(page["@odata.nextLink"]);
    }

    [Fact]
    public async Task AClientTop_LargerThanThePage_IsCarriedForwardReduced()
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        JsonObject page = await host.GetJsonAsync($"/odata/{MappedTestHost.Paged}?$top=3");

        Assert.Equal(2, page["value"]!.AsArray().Count);

        string next = page["@odata.nextLink"]!.GetValue<string>();
        Assert.Contains("%24skip=2", next, StringComparison.Ordinal);
        Assert.Contains("%24top=1", next, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClientTop_WithinThePage_EndsTheWalk()
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        JsonObject page = await host.GetJsonAsync($"/odata/{MappedTestHost.Paged}?$top=1");

        Assert.Single(page["value"]!.AsArray());
        Assert.Null(page["@odata.nextLink"]);
    }

    [Fact]
    public async Task PreferMaxPageSize_NarrowsThePage_AndIsAnnounced()
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, $"/odata/{MappedTestHost.Mapped}");
        request.Headers.Add("Prefer", "maxpagesize=1");

        HttpResponseMessage response = await host.Client.SendAsync(request);
        JsonObject page = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Single(page["value"]!.AsArray());
        Assert.Equal("maxpagesize=1", string.Join(",", response.Headers.GetValues("Preference-Applied")));
        Assert.NotNull(page["@odata.nextLink"]);
    }

    [Fact]
    public async Task PreferMaxPageSize_LargerThanTheCeiling_DoesNotWidenIt()
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, $"/odata/{MappedTestHost.Paged}");
        request.Headers.Add("Prefer", "maxpagesize=100");

        HttpResponseMessage response = await host.Client.SendAsync(request);
        JsonObject page = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        // RFC 7240 makes a preference advisory and forbids claiming one that was not applied, so a
        // preference that would WIDEN the page is neither honoured nor announced.
        Assert.Equal(2, page["value"]!.AsArray().Count);
        Assert.False(response.Headers.Contains("Preference-Applied"));
    }

    [Fact]
    public async Task TheCount_IsOfTheWholeFilteredSet_NotThePage()
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        JsonObject page = await host.GetJsonAsync($"/odata/{MappedTestHost.Paged}?$count=true");

        Assert.Equal(2, page["value"]!.AsArray().Count);
        Assert.Equal(3, page["@odata.count"]!.GetValue<long>());
    }

    // ── Refusals ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("$skiptoken=abc")]
    [InlineData("$apply=groupby((Title))")]
    [InlineData("$compute=1 as X")]
    [InlineData("$unknown=1")]
    public async Task AnOptionTheProfileDoesNotHonour_IsRefused_NotDropped(string option)
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(
            $"/odata/{MappedTestHost.Mapped}?{option}");
        string body = await response.Content.ReadAsStringAsync();
        _out.WriteLine($"{(int)response.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("UnsupportedQueryOption", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATopBeyondMaxTop_Is400_BeforeTheHandlerRuns()
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        HttpResponseMessage response = await host.Client.GetAsync(
            $"/odata/{MappedTestHost.Paged}?$top=99999");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("InvalidQueryOption", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIgnoredMember_IsNeverServedFromTheEntity()
    {
        await using MappedTestHost host = await MappedTestHost.StartAsync();

        JsonObject page = await host.GetJsonAsync($"/odata/{MappedTestHost.Mapped}");

        // Present in the model, so present in the payload -- but at its default, never read from a
        // column. The entity's own InternalCost is nowhere on the wire at all.
        Assert.Equal("0001-01-01T00:00:00", page["value"]![0]!["RenderedAt"]!.GetValue<string>());
        Assert.DoesNotContain("InternalCost", page.ToJsonString(), StringComparison.Ordinal);
    }
}
