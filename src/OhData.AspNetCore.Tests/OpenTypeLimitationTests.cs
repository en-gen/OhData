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
        GetAll = ct => Task.FromResult<IEnumerable<RootBagEntity>>(new[]
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
            o.AddEntitySetProfile<RootBagProfile>());

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
        GetQueryable = _ => Task.FromResult(db.Refs.AsQueryable());
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
            b => b.AddEntitySetProfile<SqlRefProfile>(),
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
    /// The documented limitation, recorded as behavior so <c>docs/open-types.md</c> cannot drift.
    /// <para>
    /// Measured outcome: <b>500</b>. Microsoft's filter binder emits a property-bag indexer access
    /// (<c>IDictionary&lt;string,object&gt;.get_Item</c>) against an <c>object</c>-typed instance,
    /// and building that expression throws <see cref="ArgumentException"/> before any SQL is
    /// generated. The important half is what does NOT happen: no query reaches the database, so
    /// this is not a silent client-side evaluation of the whole table — the request fails outright.
    /// </para>
    /// <para>
    /// The 500 (rather than a 400 <c>InvalidQueryOption</c>) is pre-existing, unrelated to the
    /// open-type serializer work — the fault happens inside <c>ODataQueryOptions.ApplyTo</c>, which
    /// this change does not touch — and is worth its own issue.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DynamicKeyFilter_Faults_AndNeverReachesTheDatabase()
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

    /// <summary>Same fault shape as <c>$filter</c> — see that test's remarks.</summary>
    [Fact]
    public async Task DynamicKeyOrderBy_Faults()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/SqlRefs?$orderby=Metadata/tier");
        _out.WriteLine($"dynamic $orderby: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
    }
}
