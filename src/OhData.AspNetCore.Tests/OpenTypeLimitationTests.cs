using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

// #389 — the DOCUMENTED limits, locked down as executable behavior so `docs/open-types.md` cannot
// drift away from what the framework actually does. Nothing here is a feature; each test records a
// boundary the issue deliberately placed out of scope.

// ── Limit 1: entity-ROOT dynamic containers are not supported ───────────────────────────────────
//
// ODataConventionModelBuilder infers a dynamic container on an ENTITY type too, and removes it from
// the CSDL — but OhData does not flatten it, so the CSDL and the wire disagree (the container is
// still written as a declared property). Flattening the write side would need the PATCH delta loop
// to route unresolvable body members somewhere, which it has no mechanism for today. Asserted as-is
// rather than "fixed" half-way.

public sealed class RootBagEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IDictionary<string, object?>? Extras { get; set; }
}

internal sealed class RootBagProfile : EntitySetProfile<int, RootBagEntity>
{
    public RootBagProfile() : base(x => x.Id)
    {
        EntitySetName = "RootBags";
        GetAll = ct => OhDataResult.SuccessTask<IEnumerable<RootBagEntity>>(new[]
        {
            new RootBagEntity
            {
                Id = 1,
                Name = "n",
                Extras = new Dictionary<string, object?> { ["dyn"] = "v" },
            },
        });
    }
}

public class OpenTypeEntityRootLimitationTests
{
    [Fact]
    public async Task EntityRootContainer_IsOmittedFromCsdlButStillNestedOnTheWire()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o =>
            o.WithOpenTypes().AddEntitySetProfile<RootBagProfile>());

        string csdl = await fx.Client.GetStringAsync("/odata/$metadata");
        Assert.Contains("<EntityType Name=\"RootBagEntity\" OpenType=\"true\">", csdl, StringComparison.Ordinal);
        Assert.DoesNotContain("Name=\"Extras\"", csdl, StringComparison.Ordinal);

        string body = await fx.Client.GetStringAsync("/odata/RootBags");
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement entity = doc.RootElement.GetProperty("value")[0];

        // OUT OF SCOPE (#389): the container is still written under its own name, and the dynamic
        // key is NOT a sibling of Name. This is the EDM/wire mismatch the issue calls out; it is
        // recorded here, not fixed.
        Assert.True(entity.TryGetProperty("Extras", out JsonElement extras));
        Assert.Equal("v", extras.GetProperty("dyn").GetString());
        Assert.False(entity.TryGetProperty("dyn", out _));
    }
}

// ── Limit 2: $filter over an INDIVIDUAL dynamic key on an EF-backed queryable ────────────────────

public sealed class SqlRef
{
    public int Id { get; set; }
    public string Source { get; set; } = "";
    public SqlMeta? Metadata { get; set; }
}

public sealed class SqlMeta
{
    public string? Region { get; set; }
    // Not mapped to any column — a dynamic bag has no relational shape. Populated in memory only.
    public IDictionary<string, object?>? KeyValuePairs { get; set; }
}

public sealed class OpenTypeSqlDbContext : DbContext
{
    public OpenTypeSqlDbContext(DbContextOptions<OpenTypeSqlDbContext> options) : base(options) { }

    public DbSet<SqlRef> Refs => Set<SqlRef>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqlRef>().OwnsOne(x => x.Metadata, b => b.Ignore(m => m.KeyValuePairs));
    }
}

internal sealed class SqlRefProfile : EntitySetProfile<int, SqlRef>
{
    public SqlRefProfile(OpenTypeSqlDbContext db) : base(x => x.Id)
    {
        EntitySetName = "SqlRefs";
        FilterEnabled = true;
        SelectEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.Refs.AsQueryable());
    }
}

public sealed class OpenTypeDynamicKeyFilterSqliteTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;

    public OpenTypeDynamicKeyFilterSqliteTests(ITestOutputHelper output) => _out = output;

    public Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private async Task<TestFixture> BuildAsync()
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.WithOpenTypes().AddEntitySetProfile<SqlRefProfile>(),
            configureServices: services =>
            {
                services.AddSingleton(_sink);
                services.AddDbContext<OpenTypeSqlDbContext>(o =>
                {
                    o.UseSqlite(_connection);
                    o.LogTo(
                        message => _sink.Add(message),
                        (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted);
                });
            });

        using IServiceScope scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenTypeSqlDbContext>();
        db.Database.EnsureCreated();
        db.Refs.AddRange(
            new SqlRef { Id = 1, Source = "a", Metadata = new SqlMeta { Region = "eu" } },
            new SqlRef { Id = 2, Source = "b", Metadata = new SqlMeta { Region = "us" } });
        db.SaveChanges();
        return fx;
    }

    /// <summary>Control: a DECLARED property of the complex type translates and pushes down.</summary>
    [Fact]
    public async Task DeclaredComplexProperty_Filter_TranslatesToSql()
    {
        await using TestFixture fx = await BuildAsync();
        _sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqlRefs?$filter=Metadata/Region eq 'eu'");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"declared: {(int)resp.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("value").GetArrayLength());

        string sql = string.Join("\n", _sink.Snapshot());
        _out.WriteLine("SQL:\n" + sql);
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>PINS KNOWN-WRONG BEHAVIOR (#390): the status code asserted here is 500, and it SHOULD be
    /// 400.</b> When #390 is fixed this test goes red and must be updated to expect
    /// <c>400 InvalidQueryOption</c> — that is the point of it. Do not "fix" the assertion without
    /// fixing #390.
    /// <para>
    /// Measured outcome: <b>500</b>. Microsoft's filter binder emits a property-bag indexer access
    /// (<c>IDictionary&lt;string,object&gt;.get_Item</c>) against an <c>object</c>-typed instance,
    /// and building that expression throws <see cref="ArgumentException"/> before any SQL is
    /// generated. The important half is what does NOT happen: no query reaches the database, so
    /// this is not a silent client-side evaluation of the whole table — the request fails outright.
    /// </para>
    /// <para>
    /// The 500 (rather than a 400 <c>InvalidQueryOption</c>) is pre-existing and unrelated to the
    /// open-type serializer work — the fault happens inside <c>ODataQueryOptions.ApplyTo</c>, which
    /// that change does not touch. Tracked as #390.
    /// </para>
    /// <para>
    /// The provider is load-bearing: the SAME request over an in-memory <c>IQueryable</c> returns
    /// <c>200</c> with correctly filtered data — see
    /// <see cref="OpenTypeDynamicKeyReadPathTests.InMemoryQueryable_FiltersDynamicKeyCorrectly"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DynamicKeyFilter_PinsKnownWrong500_AndNeverReachesTheDatabase()
    {
        await using TestFixture fx = await BuildAsync();
        _sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqlRefs?$filter=Metadata/tier eq 3");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"dynamic $filter: {(int)resp.StatusCode} {body}");

        IReadOnlyList<string> sql = _sink.Snapshot();
        _out.WriteLine("SQL:\n" + string.Join("\n", sql));

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        // No SELECT against the Refs table: nothing was client-evaluated.
        Assert.DoesNotContain(sql, s => s.Contains("FROM \"Refs\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>$select</c> over an individual dynamic key is silently treated as selecting the whole
    /// container: 200, with the complex value emitted in full. No error, no per-key projection.
    /// </summary>
    [Fact]
    public async Task DynamicKeySelect_SilentlySelectsTheWholeContainer()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqlRefs?$select=Metadata/tier");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"dynamic $select: {(int)resp.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement first = doc.RootElement.GetProperty("value")[0];
        Assert.False(first.TryGetProperty("Source", out _));
        Assert.Equal("eu", first.GetProperty("Metadata").GetProperty("Region").GetString());
    }

    /// <summary>
    /// <b>PINS KNOWN-WRONG BEHAVIOR (#390)</b> — same fault shape as <c>$filter</c>; see
    /// <see cref="DynamicKeyFilter_PinsKnownWrong500_AndNeverReachesTheDatabase"/> for the full
    /// remarks and for why this must be updated to <c>400</c> when #390 is fixed.
    /// </summary>
    [Fact]
    public async Task DynamicKeyOrderBy_PinsKnownWrong500()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqlRefs?$orderby=Metadata/tier");
        _out.WriteLine($"dynamic $orderby: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
    }
}

// ── Limit 3: the READ PATH decides whether a dynamic-key $filter works (#401) ────────────────────
//
// The same URL against the same model returns correctly filtered data, a 400, or a 500 — decided by
// what is behind the collection GET. docs/open-types.md states the split; this section is what
// stops that prose drifting again. The EF Core leg is the two tests above (#390).
//
// Isolating the axis matters: it is NOT "GetAll vs GetQueryable" (issue #401's original framing) and
// it is NOT the CLR model. It is the LINQ PROVIDER behind GetQueryable — PathRef/PathMeta below and
// SqlRef/SqlMeta above are structurally identical types, and each is served from both a
// List<T>.AsQueryable() and (for SqlRef) an EF Core DbSet, with opposite outcomes.

public sealed class PathRef
{
    public int Id { get; set; }
    public string Source { get; set; } = "";
    public PathMeta? Metadata { get; set; }
}

public sealed class PathMeta
{
    public string? Region { get; set; }
    public IDictionary<string, object?>? KeyValuePairs { get; set; }
}

internal static class PathRefData
{
    public static List<PathRef> Rows() =>
    [
        new PathRef
        {
            Id = 1, Source = "a",
            Metadata = new PathMeta
            {
                Region = "eu",
                KeyValuePairs = new Dictionary<string, object?> { ["tier"] = 3 },
            },
        },
        new PathRef
        {
            Id = 2, Source = "b",
            Metadata = new PathMeta
            {
                Region = "us",
                KeyValuePairs = new Dictionary<string, object?> { ["tier"] = 9 },
            },
        },
    ];
}

/// <summary>Collection GET backed by <c>GetAll</c> — an <c>IEnumerable</c>, no queryable at all.</summary>
internal sealed class PathRefGetAllProfile : EntitySetProfile<int, PathRef>
{
    public PathRefGetAllProfile() : base(x => x.Id)
    {
        EntitySetName = "MemRefs";
        FilterEnabled = true;
        OrderByEnabled = true;
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<PathRef>>(PathRefData.Rows());
    }
}

/// <summary>Collection GET backed by an in-memory <c>IQueryable</c> (LINQ to Objects).</summary>
internal sealed class PathRefQueryableProfile : EntitySetProfile<int, PathRef>
{
    public PathRefQueryableProfile() : base(x => x.Id)
    {
        EntitySetName = "LinqRefs";
        FilterEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(PathRefData.Rows().AsQueryable());
    }
}

public class OpenTypeDynamicKeyReadPathTests
{
    private readonly ITestOutputHelper _out;

    public OpenTypeDynamicKeyReadPathTests(ITestOutputHelper output) => _out = output;

    private static int[] Ids(string body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => e.GetProperty("Id").GetInt32())
            .ToArray();
    }

    /// <summary>
    /// <c>GetAll</c> rejects <c>$filter</c>/<c>$orderby</c> outright — and this is <b>not</b>
    /// dynamic-key-specific, which is the whole point of asserting the declared property alongside
    /// the dynamic key. That path implements neither option, so it rejects every one of them
    /// identically. Issue #401 originally reported this row as "in-memory works"; it does not, and
    /// the working case is the in-memory <i>queryable</i> in the next test.
    /// </summary>
    [Theory]
    [InlineData("$filter=Metadata/tier eq 3")]      // dynamic key
    [InlineData("$filter=Metadata/Region eq 'eu'")] // declared property of the complex type
    [InlineData("$filter=Source eq 'a'")]           // declared property of the entity itself
    [InlineData("$orderby=Metadata/tier")]          // dynamic key
    [InlineData("$orderby=Source")]                 // declared property of the entity itself
    public async Task GetAll_RejectsFilterAndOrderBy_DynamicKeyAndDeclaredAlike(string query)
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o =>
            o.WithOpenTypes().AddEntitySetProfile<PathRefGetAllProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/MemRefs?{query}");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"GetAll {query}: {(int)resp.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        Assert.Contains("UnsupportedQueryOption", body, StringComparison.Ordinal);
        Assert.Contains("Configure GetQueryable", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Over an in-memory <c>IQueryable</c> a dynamic-key <c>$filter</c> <b>works</b>: 200, correctly
    /// filtered. The same request over EF Core is a 500 —
    /// <see cref="OpenTypeDynamicKeyFilterSqliteTests.DynamicKeyFilter_PinsKnownWrong500_AndNeverReachesTheDatabase"/>.
    /// <para>
    /// This is recorded as the measured truth, <b>not</b> as a supported feature. docs/open-types.md
    /// says so in as many words: a profile that moves from <c>List&lt;T&gt;.AsQueryable()</c> to an
    /// EF Core <c>DbSet</c> — otherwise a pure performance improvement — turns this 200 into a 500.
    /// </para>
    /// </summary>
    [Fact]
    public async Task InMemoryQueryable_FiltersDynamicKeyCorrectly()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o =>
            o.WithOpenTypes().AddEntitySetProfile<PathRefQueryableProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/LinqRefs?$filter=Metadata/tier eq 3");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"in-memory queryable $filter: {(int)resp.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(new[] { 1 }, Ids(body));
    }

    /// <summary>
    /// <c>$orderby</c> over a dynamic key likewise succeeds over an in-memory <c>IQueryable</c>,
    /// against a 500 on EF Core. Descending order puts tier 9 (Id 2) first.
    /// </summary>
    [Fact]
    public async Task InMemoryQueryable_OrdersByDynamicKeyCorrectly()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o =>
            o.WithOpenTypes().AddEntitySetProfile<PathRefQueryableProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/LinqRefs?$orderby=Metadata/tier desc");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"in-memory queryable $orderby: {(int)resp.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(new[] { 2, 1 }, Ids(body));
    }
}

// ── Limit 4: dynamic keys sit OUTSIDE the query-option property allowlists (#401) ────────────────
//
// FilterProperties/OrderByProperties/SelectProperties are enforced through the EDM's model-bound
// NotFilterable/NotSortable/NotSelectable annotations. A dynamic property is not in the EDM, so it
// carries no annotation and is not gated. Microsoft.AspNetCore.OData behaves identically — this is
// not a divergence, it is an undocumented (now documented) exception to an enforcement mechanism
// people reasonably read as a security boundary.
//
// The $select half of this — $select=Meta/<dynamicKey> returning the whole container including
// DECLARED sub-properties the allowlist denies — is a data-exposure bug filed as #403 and is
// deliberately NOT pinned here: asserting today's behavior would assert that the leak is correct.

/// <summary>Allowlists deny <c>Metadata</c> outright; served from an in-memory queryable so the
/// dynamic-key requests reach a result rather than the EF fault of #390.</summary>
internal sealed class PathRefAllowlistProfile : EntitySetProfile<int, PathRef>
{
    public PathRefAllowlistProfile() : base(x => x.Id)
    {
        EntitySetName = "AllowRefs";
        FilterEnabled = true;
        OrderByEnabled = true;
        FilterProperties(x => x.Source);
        OrderByProperties(x => x.Source);
        GetQueryable = _ => OhDataResult.SuccessTask(PathRefData.Rows().AsQueryable());
    }
}

/// <summary>Control: identical shape with a CLOSED complex type — no dictionary member.</summary>
public sealed class ClosedRef
{
    public int Id { get; set; }
    public string Source { get; set; } = "";
    public ClosedMeta? Metadata { get; set; }
}

public sealed class ClosedMeta
{
    public string? Region { get; set; }
}

internal sealed class ClosedRefAllowlistProfile : EntitySetProfile<int, ClosedRef>
{
    public ClosedRefAllowlistProfile() : base(x => x.Id)
    {
        EntitySetName = "ClosedRefs";
        FilterEnabled = true;
        OrderByEnabled = true;
        FilterProperties(x => x.Source);
        OrderByProperties(x => x.Source);
        GetQueryable = _ => OhDataResult.SuccessTask(new List<ClosedRef>
        {
            new() { Id = 1, Source = "a", Metadata = new ClosedMeta { Region = "eu" } },
            new() { Id = 2, Source = "b", Metadata = new ClosedMeta { Region = "us" } },
        }.AsQueryable());
    }
}

/// <summary>Entity-ROOT container, allowlists denying everything but the key.</summary>
internal sealed class RootBagAllowlistProfile : EntitySetProfile<int, RootBagEntity>
{
    public RootBagAllowlistProfile() : base(x => x.Id)
    {
        EntitySetName = "RootAllow";
        FilterEnabled = true;
        OrderByEnabled = true;
        FilterProperties(x => x.Id);
        OrderByProperties(x => x.Id);
        GetQueryable = _ => OhDataResult.SuccessTask(new List<RootBagEntity>
        {
            new() { Id = 1, Name = "n1", Extras = new Dictionary<string, object?> { ["tier"] = 3 } },
            new() { Id = 2, Name = "n2", Extras = new Dictionary<string, object?> { ["tier"] = 9 } },
        }.AsQueryable());
    }
}

public class OpenTypeAllowlistBypassTests
{
    private readonly ITestOutputHelper _out;

    public OpenTypeAllowlistBypassTests(ITestOutputHelper output) => _out = output;

    private async Task<(HttpStatusCode Status, string Body)> GetAsync<TProfile>(string url)
        where TProfile : class, IEntitySetProfile
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(o =>
            o.WithOpenTypes().AddEntitySetProfile<TProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"{url}: {(int)resp.StatusCode} {body}");
        return (resp.StatusCode, body);
    }

    /// <summary>
    /// Control: the allowlist genuinely works for anything the EDM knows about. Without this the
    /// bypass tests below would be indistinguishable from a profile whose allowlist was never wired
    /// up at all.
    /// </summary>
    [Theory]
    [InlineData("$filter=Metadata/Region eq 'eu'")]
    [InlineData("$orderby=Metadata/Region")]
    public async Task Allowlist_IsEnforced_ForDeclaredComplexSubProperty(string query)
    {
        (HttpStatusCode status, string body) = await GetAsync<PathRefAllowlistProfile>($"/odata/AllowRefs?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("InvalidQueryOption", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bypass: <c>FilterProperties(x =&gt; x.Source)</c> denies <c>Metadata</c>, yet a filter
    /// over a dynamic key beneath it succeeds and filters. The allowlist is never consulted, because
    /// there is no EDM property to carry <c>NotFilterable</c>.
    /// </summary>
    [Fact]
    public async Task Allowlist_DoesNotGate_DynamicKeyFilter()
    {
        (HttpStatusCode status, string body) = await GetAsync<PathRefAllowlistProfile>(
            "/odata/AllowRefs?$filter=Metadata/tier eq 3");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement rows = doc.RootElement.GetProperty("value");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal(1, rows[0].GetProperty("Id").GetInt32());
    }

    /// <summary>Same bypass for <c>$orderby</c> and <c>NotSortable</c>.</summary>
    [Fact]
    public async Task Allowlist_DoesNotGate_DynamicKeyOrderBy()
    {
        (HttpStatusCode status, string body) = await GetAsync<PathRefAllowlistProfile>(
            "/odata/AllowRefs?$orderby=Metadata/tier desc");

        Assert.Equal(HttpStatusCode.OK, status);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement rows = doc.RootElement.GetProperty("value");
        Assert.Equal(2, rows[0].GetProperty("Id").GetInt32());
        Assert.Equal(1, rows[1].GetProperty("Id").GetInt32());
    }

    /// <summary>
    /// An entity-ROOT container is the same story without the complex-type hop: with
    /// <c>FilterProperties(x =&gt; x.Id)</c>, a filter over a root dynamic key still runs.
    /// </summary>
    [Theory]
    [InlineData("$filter=tier eq 3")]
    [InlineData("$orderby=tier desc")]
    public async Task Allowlist_DoesNotGate_EntityRootDynamicKey(string query)
    {
        (HttpStatusCode status, _) = await GetAsync<RootBagAllowlistProfile>($"/odata/RootAllow?{query}");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>
    /// <b>The control that proves open-type-ness is the cause.</b> Against a CLOSED complex type the
    /// byte-identical requests are rejected with <c>400 InvalidQueryOption</c> — the path cannot even
    /// be parsed, because <c>tier</c> is not a property of the type and the type is not open. So the
    /// bypass above is not "OhData forgets to check paths"; it is specifically that an open type
    /// makes an undeclared path legal while the EDM has nothing to annotate.
    /// </summary>
    [Theory]
    [InlineData("$filter=Metadata/tier eq 3")]
    [InlineData("$orderby=Metadata/tier")]
    public async Task ClosedComplexType_RejectsUndeclaredPath_Control(string query)
    {
        (HttpStatusCode status, string body) = await GetAsync<ClosedRefAllowlistProfile>($"/odata/ClosedRefs?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("InvalidQueryOption", body, StringComparison.Ordinal);
        Assert.Contains("Could not find a property named 'tier'", body, StringComparison.Ordinal);
    }
}
