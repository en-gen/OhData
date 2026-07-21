using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #241: server paging (MaxTop-bounded Take) must ride a deterministic total order, else the
// provider emits LIMIT with no ORDER BY (EF warning 10102) and page boundaries across
// @odata.nextLink are undefined. Reuses SqliteWide / PushSqliteDbContext / SqlCaptureSink from
// SelectPushdownSqliteTests. Profiles below use MaxTop=2 to force paging over the seeded rows.

public sealed class PagingWideProfile : EntitySetProfile<int, SqliteWide>
{
    public PagingWideProfile(PushSqliteDbContext db) : base(x => x.Id)
    {
        OrderByEnabled = true;
        MaxTop = 2;
        GetQueryable = _ => Task.FromResult(db.Wides.AsQueryable());
    }
}

/// <summary>Profile whose own IQueryable is pre-ordered (Id descending) — the framework must not
/// override that order with the stabilizing key ascending when the client omits $orderby.</summary>
public sealed class PagingPreOrderedProfile : EntitySetProfile<int, SqliteWide>
{
    public PagingPreOrderedProfile(PushSqliteDbContext db) : base(x => x.Id)
    {
        OrderByEnabled = true;
        MaxTop = 2;
        GetQueryable = _ => Task.FromResult(db.Wides.OrderByDescending(x => x.Id).AsQueryable());
    }
}

/// <summary>Profile whose only ordering is buried inside a $filter-predicate subquery. That
/// ordering does not govern the top-level result order, so the key stabilizer must still inject an
/// ORDER BY (regression guard for the whole-tree-scan bug found in review).</summary>
public sealed class PagingBuriedOrderProfile : EntitySetProfile<int, SqliteWide>
{
    public PagingBuriedOrderProfile(PushSqliteDbContext db) : base(x => x.Id)
    {
        MaxTop = 2;
        // Stock >= (min Stock, ordered by Name) — every seeded row has Stock=1, so all match; the
        // OrderBy(Name) lives only inside the correlated subquery, not on the outer sequence.
        GetQueryable = _ => Task.FromResult(
            db.Wides.Where(w => w.Stock >= db.Wides.OrderBy(x => x.Name).Select(x => x.Stock).First()));
    }
}

/// <summary>Unbounded profile (MaxTop resolves to null via defaults) — with no client paging the
/// framework must NOT add a whole-table ORDER BY, since no row-limiting operator runs.</summary>
public sealed class PagingUnboundedProfile : EntitySetProfile<int, SqliteWide>
{
    public PagingUnboundedProfile(PushSqliteDbContext db) : base(x => x.Id)
    {
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Wides.AsQueryable());
    }
}

file static class PagingTestData
{
    // Non-unique Name column so a client $orderby=name has ties the key tiebreaker must resolve.
    public static void Seed(PushSqliteDbContext db)
    {
        db.Database.EnsureCreated();
        db.Wides.AddRange(
            new SqliteWide { Id = 1, Name = "B", Price = 10m, Description = "d", Sku = "S", Stock = 1 },
            new SqliteWide { Id = 2, Name = "A", Price = 10m, Description = "d", Sku = "S", Stock = 1 },
            new SqliteWide { Id = 3, Name = "A", Price = 10m, Description = "d", Sku = "S", Stock = 1 },
            new SqliteWide { Id = 4, Name = "C", Price = 10m, Description = "d", Sku = "S", Stock = 1 },
            new SqliteWide { Id = 5, Name = "A", Price = 10m, Description = "d", Sku = "S", Stock = 1 });
        db.SaveChanges();
    }

    /// <summary>Follows @odata.nextLink to the end, returning the ids of every page in order.</summary>
    public static async Task<List<int>> PageAllIds(System.Net.Http.HttpClient client, string startPathAndQuery)
    {
        var ids = new List<int>();
        string? path = startPathAndQuery;
        for (int guard = 0; path is not null && guard < 50; guard++)
        {
            var resp = await client.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var el in doc.RootElement.GetProperty("value").EnumerateArray())
                ids.Add(el.GetProperty("Id").GetInt32());
            path = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl)
                ? new Uri(nl.GetString()!).PathAndQuery
                : null;
        }
        return ids;
    }
}

public class PagingStableOrderSqliteTests : IAsyncLifetime
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
            b => b.AddEntitySetProfile<PagingWideProfile>(),
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
        PagingTestData.Seed(scope.ServiceProvider.GetRequiredService<PushSqliteDbContext>());
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NoOrderBy_PagedQuery_EmitsOrderByAlongsideLimit()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/SqliteWides");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = _sink.LastWidesSelect();
        // The stabilizing key order + the paging LIMIT must appear together — the exact condition
        // whose absence raises EF warning 10102.
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("\"Id\"", sql);
    }

    [Fact]
    public async Task NoOrderBy_PaginationIsCompleteAndNonOverlapping()
    {
        var ids = await PagingTestData.PageAllIds(_fx.Client, "/odata/SqliteWides");
        // Every row exactly once, in key order — deterministic paging.
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ids);
    }

    [Fact]
    public async Task ClientOrderByOnNonUniqueColumn_TiebreaksByKey_StableAndComplete()
    {
        _sink.Clear();
        var ids = await PagingTestData.PageAllIds(_fx.Client, "/odata/SqliteWides?$orderby=name");

        // Complete, no duplicates/gaps despite the ties on Name.
        Assert.Equal(5, ids.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ids.OrderBy(i => i).ToArray());
        // Ties broken by ascending key: the three Name="A" rows (2,3,5) come out in id order,
        // then Name="B" (1), then Name="C" (4).
        Assert.Equal(new[] { 2, 3, 5, 1, 4 }, ids);

        string sql = _sink.LastWidesSelect();
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("\"Name\"", sql);
        Assert.Contains("\"Id\"", sql); // key appended as tiebreaker
    }
}

public class PagingPreOrderedSourceSqliteTests : IAsyncLifetime
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
            b => b.AddEntitySetProfile<PagingPreOrderedProfile>(),
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
        PagingTestData.Seed(scope.ServiceProvider.GetRequiredService<PushSqliteDbContext>());
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task ProfilePreOrder_IsPreserved_NotOverriddenByStabilizingKeyOrder()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/SqliteWides");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        int[] firstPage = doc.RootElement.GetProperty("value")
            .EnumerateArray().Select(e => e.GetProperty("Id").GetInt32()).ToArray();

        // The profile ordered by Id DESC; the first page must be [5, 4], not the ascending [1, 2].
        Assert.Equal(new[] { 5, 4 }, firstPage);

        string sql = _sink.LastWidesSelect();
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("DESC", sql); // the profile's descending order still drives the query
    }

    [Fact]
    public async Task ProfilePreOrder_PaginationCompleteInProfileOrder()
    {
        var ids = await PagingTestData.PageAllIds(_fx.Client, "/odata/SqliteWides");
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, ids);
    }
}

public class PagingBuriedOrderSqliteTests : IAsyncLifetime
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
            b => b.AddEntitySetProfile<PagingBuriedOrderProfile>(),
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
        PagingTestData.Seed(scope.ServiceProvider.GetRequiredService<PushSqliteDbContext>());
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task OrderingBuriedInPredicate_DoesNotSuppressKeyStabilizer()
    {
        // The buried OrderBy(Name) must not count as the result order; paging stays key-ordered.
        var ids = await PagingTestData.PageAllIds(_fx.Client, "/odata/SqliteWides");
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ids);
    }

    [Fact]
    public async Task OrderingBuriedInPredicate_TopLevelQueryStillOrdersByKey()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/SqliteWides");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = _sink.LastWidesSelect();
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("\"Id\"", sql);
    }
}

public class PagingUnboundedSqliteTests : IAsyncLifetime
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
                b.WithDefaults(d => d.MaxTop = null); // unbounded: no server paging cap
                b.AddEntitySetProfile<PagingUnboundedProfile>();
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
        PagingTestData.Seed(scope.ServiceProvider.GetRequiredService<PushSqliteDbContext>());
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task Unbounded_NoClientPaging_EmitsNeitherLimitNorOrderBy()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/SqliteWides");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = _sink.LastWidesSelect();
        // No row-limiting operator runs, so there is no undefined-page problem to solve — the
        // framework must not burden an unbounded query with a whole-table sort.
        Assert.DoesNotContain("LIMIT", sql);
        Assert.DoesNotContain("ORDER BY", sql);
    }

    [Fact]
    public async Task Unbounded_ClientTopWithoutOrderBy_StillStabilizes()
    {
        // A client $top triggers a row limit, so the stabilizer must engage even when MaxTop is null.
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/SqliteWides?$top=2");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        string sql = _sink.LastWidesSelect();
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("\"Id\"", sql);
    }
}
