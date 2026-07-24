using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #254 (item 3): MaxExpandTop — the per-navigation ceiling on a NESTED $top inside a $expand, and the
// bound on how many related entities a nested $count may materialize.
//
// Two enforcement points:
//   E1 — an explicit nested $top above the ceiling is rejected with 400 before any query runs, at any
//        depth, on every collection read path, and regardless of whether the navigation would have
//        been pushed down (a delegate-backed nav 400s too — same rule as the root MaxTop).
//   E2 — under a nested $count the SQL materialization is bounded to ceiling + 1 rows; a related
//        collection that exceeds the ceiling is a 400 rather than a silently truncated count, because
//        OData §11.2.4.2 requires Nav@odata.count to report the FULL filtered collection.
//
// An OMITTED nested $top without $count is deliberately left unbounded (E3, explicit non-goal):
// silently windowing an expanded collection with no Nav@odata.nextLink would be a worse spec
// violation than the cost.

// ── Resolution (profile / WithDefaults / null / invalid) ─────────────────────────────────────────

public sealed class MetModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class MaxExpandTopResolutionTests
{
    private sealed class DefaultProfile : EntitySetProfile<int, MetModel>
    {
        public DefaultProfile() : base(x => x.Id) { }
    }

    private sealed class OverrideProfile : EntitySetProfile<int, MetModel>
    {
        public OverrideProfile() : base(x => x.Id) { MaxExpandTop = 25; }
    }

    private sealed class UncappedProfile : EntitySetProfile<int, MetModel>
    {
        public UncappedProfile() : base(x => x.Id) { MaxExpandTop = null; }
    }

    private sealed class ZeroProfile : EntitySetProfile<int, MetModel>
    {
        public ZeroProfile() : base(x => x.Id) { MaxExpandTop = 0; }
    }

    private sealed class NegativeProfile : EntitySetProfile<int, MetModel>
    {
        public NegativeProfile() : base(x => x.Id) { MaxExpandTop = -5; }
    }

    private static void Seal(IVisitModelBuilder profile, EntitySetDefaults? defaults = null) =>
        profile.VisitModelBuilder(
            new Microsoft.OData.ModelBuilder.ODataConventionModelBuilder(),
            defaults ?? new EntitySetDefaults());

    [Fact]
    public void MaxExpandTop_DefaultsTo1000()
    {
        Assert.Equal(1000, new EntitySetDefaults().MaxExpandTop);

        var profile = new DefaultProfile();
        Seal(profile);
        Assert.Equal(1000, ((IEntitySetEndpointSource)profile).MaxExpandTop);
    }

    [Fact]
    public void MaxExpandTop_ProfileOverride_Wins()
    {
        var profile = new OverrideProfile();
        Seal(profile, new EntitySetDefaults { MaxExpandTop = 7 });
        Assert.Equal(25, ((IEntitySetEndpointSource)profile).MaxExpandTop);
    }

    [Fact]
    public void MaxExpandTop_WithDefaultsOverride_AppliesWhenProfileSilent()
    {
        var profile = new DefaultProfile();
        Seal(profile, new EntitySetDefaults { MaxExpandTop = 7 });
        Assert.Equal(7, ((IEntitySetEndpointSource)profile).MaxExpandTop);
    }

    [Fact]
    public void MaxExpandTop_NullProfileValue_InheritsDefault_NotUncapped()
    {
        // `MaxExpandTop = null` on the profile means "inherit", exactly like MaxTop — opting out of the
        // ceiling entirely is done by setting the DEFAULT to null (see the next test).
        var profile = new UncappedProfile();
        Seal(profile);
        Assert.Equal(1000, ((IEntitySetEndpointSource)profile).MaxExpandTop);
    }

    [Fact]
    public void MaxExpandTop_NullDefault_IsUncapped()
    {
        var profile = new DefaultProfile();
        Seal(profile, new EntitySetDefaults { MaxExpandTop = null });
        Assert.Null(((IEntitySetEndpointSource)profile).MaxExpandTop);
    }

    [Fact]
    public void MaxExpandTop_ZeroOrNegative_Throws_OnDefaults()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntitySetDefaults { MaxExpandTop = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new EntitySetDefaults { MaxExpandTop = -1 });
    }

    [Fact]
    public void MaxExpandTop_ZeroOrNegative_Throws_OnProfile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZeroProfile());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NegativeProfile());
    }
}

// ── E1: explicit nested $top ceiling (pre-query, depth- and pushdown-independent) ────────────────

public sealed class NestedTopCeilingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private MultiLevelDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _counter = new MultiLevelDelegateCounter();
        _fx = await MultiLevelSqliteHarness.BuildAsync(
            _connection, _counter, sink: null, defaults: d => d.MaxExpandTop = 2);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NestedTop_AtCeiling_Succeeds()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Authors?$expand=Books($top=2)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task NestedTop_AboveCeiling_Returns400_NamingTheNavigation()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Authors?$expand=Books($top=3)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Books", body);      // the offending navigation is named
        Assert.Contains("(3)", body);        // the requested value
        Assert.Contains("(2)", body);        // the ceiling
    }

    [Fact]
    public async Task NestedTop_AboveCeiling_AtDepthTwo_Returns400()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$expand=Books($expand=Chapters($top=3))");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Chapters", body);
    }

    [Fact]
    public async Task NestedTop_AboveCeiling_AtDepthThree_Returns400()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$expand=Books($expand=Chapters($expand=Pages($top=3)))");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Pages", body);
    }

    [Fact]
    public async Task NestedTop_AboveCeiling_OnDelegateBackedNav_AlsoReturns400()
    {
        // Pushdown-independent by design: Curators is DELEGATE-backed (never pushed down), yet the
        // ceiling still rejects the over-large nested $top — the same way the root MaxTop rejects an
        // over-large $top on every read path regardless of how the collection would be served. The
        // delegate is never invoked, because the rejection happens before any handler runs.
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Catalogs?$expand=Curators($top=3)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Curators", body);
        Assert.Equal(0, _counter.CuratorCalls);
    }
}

// ── E2: the nested $count materialization bound ──────────────────────────────────────────────────

public sealed class NestedCountCeilingAtBoundaryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private MultiLevelDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new MultiLevelDelegateCounter();
        // Author 1 has exactly 2 Books, so a ceiling of 2 puts the collection EXACTLY at the bound.
        _fx = await MultiLevelSqliteHarness.BuildAsync(
            _connection, _counter, _sink, defaults: d => d.MaxExpandTop = 2);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NestedCount_ChildCountEqualToCeiling_Succeeds_WithExactCount()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Authors?$expand=Books($count=true)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Books@odata.count\":2", body);
    }

    [Fact]
    public async Task NestedCount_UnderCeiling_PushesARowBoundIntoSql()
    {
        // The whole point of E2: the count-deferred materialization is BOUNDED in SQL (Take(cap + 1)),
        // not trimmed after the fact. EF Core pages a JOIN'd collection with a ROW_NUMBER window.
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Authors?$orderby=id&$expand=Books($count=true)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Authors");
        Assert.Contains("\"Books\"", sql);
        Assert.True(
            sql.Contains("ROW_NUMBER()", StringComparison.Ordinal) || sql.Contains("LIMIT", StringComparison.Ordinal),
            $"the nested $count materialization must carry a SQL row bound; got:\n{sql}");
    }

    [Fact]
    public async Task NestedCount_WithNestedTop_StillReportsFullCount_AndWindowsArray()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Authors?$expand=Books($count=true;$top=1)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value")[0];
        Assert.Equal(2, author.GetProperty("Books@odata.count").GetInt32()); // full pre-window total
        Assert.Equal(1, author.GetProperty("Books").GetArrayLength());       // windowed by $top
    }
}

public sealed class NestedCountCeilingBreachTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private MultiLevelDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _counter = new MultiLevelDelegateCounter();
        // Ceiling 1, Author 1 has 2 Books → the count is unknowable within the configured budget.
        _fx = await MultiLevelSqliteHarness.BuildAsync(
            _connection, _counter, sink: null, defaults: d => d.MaxExpandTop = 1);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NestedCount_ChildCountAboveCeiling_Returns400_NotATruncatedCount()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Authors?$expand=Books($count=true)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 1", body);
        // §11.2.4.2: a truncated count would be a silent lie, so no count is emitted at all.
        Assert.DoesNotContain("@odata.count", body);
    }

    [Fact]
    public async Task NestedCount_AtDepth_AboveCeiling_Returns400()
    {
        // Book 10 has 2 Chapters — the breach is at level 2, and the ceiling applies at every depth.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/Authors?$expand=Books($filter=year eq 2001;$expand=Chapters($count=true))");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("cannot be computed", body);
    }
}

// ── Uncapped regression: MaxExpandTop = null reproduces the pre-#254 behavior exactly ────────────

public sealed class NestedTopUncappedTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private MultiLevelDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new MultiLevelDelegateCounter();
        _fx = await MultiLevelSqliteHarness.BuildAsync(
            _connection, _counter, _sink, defaults: d => d.MaxExpandTop = null);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task Uncapped_HugeNestedTop_Accepted()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Authors?$expand=Books($top=100000)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Uncapped_NestedCount_EmitsFullCount_WithNoRowBoundInSql()
    {
        // Byte-identical to the pre-#254 shape: no Take is composed onto the count-deferred
        // materialization, so EF emits a plain JOIN with no ROW_NUMBER window for the children.
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Authors?$orderby=id&$expand=Books($count=true)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Books@odata.count\":2", body);

        string sql = MultiLevelSqliteHarness.LastSelectAgainst(_sink, "Authors");
        Assert.Contains("\"Books\"", sql);
        Assert.DoesNotContain("ROW_NUMBER()", sql);
    }

    [Fact]
    public async Task Uncapped_NestedCountWithTop_StillCountsFullAndWindows()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/Authors?$expand=Books($count=true;$top=1)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value")[0];
        Assert.Equal(2, author.GetProperty("Books@odata.count").GetInt32());
        Assert.Equal(1, author.GetProperty("Books").GetArrayLength());
    }
}
