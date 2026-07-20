using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #206: SQL-shape proof — with pushdown, the provider-issued SELECT lists only the projection
// set's columns; without it (or when ineligible), all columns. Uses EF Core Sqlite over a
// keep-alive in-memory connection with command-text capture.

public sealed class SqliteWide
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string Description { get; set; } = "";
    public string Sku { get; set; } = "";
    public int Stock { get; set; }
}

public sealed class PushSqliteDbContext : DbContext
{
    public PushSqliteDbContext(DbContextOptions<PushSqliteDbContext> options) : base(options) { }

    public DbSet<SqliteWide> Wides => Set<SqliteWide>();
}

/// <summary>Collects executed command text; registered via <c>LogTo</c> on the DbContext.</summary>
public sealed class SqlCaptureSink
{
    private readonly List<string> _statements = new();

    public void Add(string statement)
    {
        lock (_statements) _statements.Add(statement);
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_statements) return _statements.ToList();
    }

    public void Clear()
    {
        lock (_statements) _statements.Clear();
    }

    /// <summary>The most recent SELECT against the Wides table.</summary>
    public string LastWidesSelect() => Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal) && s.Contains("\"Wides\"", StringComparison.Ordinal))
        .Last();
}

public sealed class SqliteWideProfile : EntitySetProfile<int, SqliteWide>
{
    private readonly PushSqliteDbContext _db;

    public SqliteWideProfile(PushSqliteDbContext db) : base(x => x.Id)
    {
        _db = db;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        GetQueryable = _ => Task.FromResult(_db.Wides.AsQueryable());
    }
}

public class SelectPushdownSqliteTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        // Keep-alive in-memory database: lives as long as this connection stays open.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();

        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<SqliteWideProfile>(),
            configureServices: services =>
            {
                services.AddSingleton(_sink);
                services.AddDbContext<PushSqliteDbContext>(o => o
                    .UseSqlite(_connection)
                    .LogTo(
                        message => _sink.Add(message),
                        (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted));
            });

        using var scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PushSqliteDbContext>();
        db.Database.EnsureCreated();
        db.Wides.AddRange(
            new SqliteWide { Id = 1, Name = "A", Price = 10m, Description = "d1", Sku = "S1", Stock = 5 },
            new SqliteWide { Id = 2, Name = "B", Price = 20m, Description = "d2", Sku = "S2", Stock = 6 });
        db.SaveChanges();
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task Select_Pushdown_PrunesSelectColumnList()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/SqliteWides?$select=name,price");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = _sink.LastWidesSelect();
        // Projection set = Name, Price + key (Id).
        Assert.Contains("\"Id\"", sql);
        Assert.Contains("\"Name\"", sql);
        Assert.Contains("\"Price\"", sql);
        Assert.DoesNotContain("\"Description\"", sql);
        Assert.DoesNotContain("\"Sku\"", sql);
        Assert.DoesNotContain("\"Stock\"", sql);
    }

    [Fact]
    public async Task NoSelect_FetchesAllColumns()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/SqliteWides");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = _sink.LastWidesSelect();
        Assert.Contains("\"Description\"", sql);
        Assert.Contains("\"Sku\"", sql);
        Assert.Contains("\"Stock\"", sql);
    }

    [Fact]
    public async Task Select_WithFilterAndOrderBy_PrunedAndPushedTogether()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync(
            "/odata/SqliteWides?$select=name&$filter=price%20gt%2015&$orderby=name");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = _sink.LastWidesSelect();
        Assert.Contains("WHERE", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.DoesNotContain("\"Description\"", sql);
    }
}

// #206 review finding (HIGH): projecting an EF-OWNED complex property under a TRACKING
// queryable throws inside EF ("owned entity without a corresponding owner") — the projection
// must fall back for complex members so this stays a 200 full-fetch, never a 500.

public sealed class SqliteOwnedItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public SqliteOwnedDims? Dims { get; set; }
}

public sealed class SqliteOwnedDims
{
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class PushOwnedDbContext : DbContext
{
    public PushOwnedDbContext(DbContextOptions<PushOwnedDbContext> options) : base(options) { }

    public DbSet<SqliteOwnedItem> Items => Set<SqliteOwnedItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<SqliteOwnedItem>().OwnsOne(x => x.Dims);
}

public sealed class SqliteOwnedProfile : EntitySetProfile<int, SqliteOwnedItem>
{
    private readonly PushOwnedDbContext _db;

    public SqliteOwnedProfile(PushOwnedDbContext db) : base(x => x.Id)
    {
        _db = db;
        SelectEnabled = true;
        // Deliberately a TRACKING queryable — the vanilla profile shape that triggered the 500.
        GetQueryable = _ => Task.FromResult(_db.Items.AsQueryable());
    }
}

public class SelectPushdownOwnedTypeTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<SqliteOwnedProfile>(),
            configureServices: services =>
                services.AddDbContext<PushOwnedDbContext>(o => o.UseSqlite(_connection)));

        using var scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PushOwnedDbContext>();
        db.Database.EnsureCreated();
        db.Items.Add(new SqliteOwnedItem
        {
            Id = 1,
            Name = "Crate",
            Dims = new SqliteOwnedDims { Width = 2.5, Height = 4.0 },
        });
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task SelectOwnedComplex_TrackingQueryable_Returns200ViaFallback()
    {
        var resp = await _fx.Client.GetAsync("/odata/SqliteOwnedItems?$select=dims");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"width\":2.5", body);
        Assert.DoesNotContain("\"name\"", body); // $select trim still applies
    }

    [Fact]
    public async Task SelectScalarOnOwnedModel_TrackingQueryable_StillWorks()
    {
        // Scalar-only $select on the same model: dims isn't in the projection set. Under
        // tracking, EF also rejects a bare scalar member-init on an owner type in some shapes —
        // what matters here is the request contract: 200 with the selected data, never a 500.
        var resp = await _fx.Client.GetAsync("/odata/SqliteOwnedItems?$select=name");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"name\":\"Crate\"", await resp.Content.ReadAsStringAsync());
    }
}

/// <summary>Opt-out host: identical profile shape but SelectPushdownEnabled=false server-wide.</summary>
public class SelectPushdownSqliteOptOutTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();

        _fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.WithDefaults(d => d.SelectPushdownEnabled = false);
                b.AddEntitySetProfile<SqliteWideProfile>();
            },
            configureServices: services =>
            {
                services.AddSingleton(_sink);
                services.AddDbContext<PushSqliteDbContext>(o => o
                    .UseSqlite(_connection)
                    .LogTo(
                        message => _sink.Add(message),
                        (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted));
            });

        using var scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PushSqliteDbContext>();
        db.Database.EnsureCreated();
        db.Wides.Add(new SqliteWide { Id = 1, Name = "A", Price = 10m, Description = "d1", Sku = "S1", Stock = 5 });
        db.SaveChanges();
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task OptOut_SelectStillFetchesAllColumns()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/SqliteWides?$select=name");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = _sink.LastWidesSelect();
        Assert.Contains("\"Description\"", sql); // full fetch despite $select
        Assert.Contains("\"Stock\"", sql);
    }
}

