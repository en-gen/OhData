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
            b => b.AddProfile<SqliteWideProfile>(),
            configureServices: services =>
            {
                services.AddSingleton(_sink);
                services.AddDbContext<PushSqliteDbContext>(o => o
                    .UseSqlite(_connection)
                    .LogTo(
                        message => _sink.Add(message),
                        (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted));
            });

        using var scope = ((Microsoft.AspNetCore.Builder.WebApplication)SelectPushdownSqliteTestsHelpers.GetApp(_fx)).Services.CreateScope();
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
                b.AddProfile<SqliteWideProfile>();
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

        using var scope = ((Microsoft.AspNetCore.Builder.WebApplication)SelectPushdownSqliteTestsHelpers.GetApp(_fx)).Services.CreateScope();
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

internal static class SelectPushdownSqliteTestsHelpers
{
    internal static object GetApp(TestFixture fx) =>
        typeof(TestFixture).GetField("_app",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(fx)!;
}
