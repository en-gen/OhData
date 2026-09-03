using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #358: `$filter=... div 0 ...` / `... mod 0 ...` raised an unhandled DivideByZeroException that
// surfaced as a generic 500 on the LINQ-to-Objects (in-memory) GetQueryable path -- see
// AdversarialQueryOptionTests.Filter_DivByZero_Returns400ODataError for that half of the coverage
// and its rationale. This file covers the OTHER half the issue explicitly calls out: a real EF
// Core provider (SQLite here) translates `div`/`mod` into SQL and lets the DATABASE decide what a
// division by zero means, rather than the CLR ever raising DivideByZeroException.
//
// Empirically confirmed below (int div 0, int mod 0, decimal div 0 all produce the SAME
// behavior): EF Core's Sqlite provider pushes the arithmetic into the generated SQL, SQLite's
// integer/real division-by-zero evaluates to NULL rather than erroring, and `NULL eq 1` is false
// -- so the request completes as an ordinary 200 with zero matching rows. No exception ever
// reaches the application, so the #358 catch clauses added to OhDataEndpointFactory.cs are
// simply never exercised on this path; they exist for the in-memory/EF-InMemory-provider case
// where the CLR itself raises the fault (see AdversarialQueryOptionTests). Both outcomes are
// spec-compliant; the ONLY one #358 forbids -- and the one these tests actually guard against --
// is an unhandled 500.

public sealed class ArithFaultSqliteItem
{
    public int Id { get; set; }
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
}

public sealed class ArithFaultSqliteDbContext : DbContext
{
    public ArithFaultSqliteDbContext(DbContextOptions<ArithFaultSqliteDbContext> options) : base(options) { }

    public DbSet<ArithFaultSqliteItem> Items => Set<ArithFaultSqliteItem>();
}

public sealed class ArithFaultSqliteProfile : EntitySetProfile<int, ArithFaultSqliteItem>
{
    private readonly ArithFaultSqliteDbContext _db;

    public ArithFaultSqliteProfile(ArithFaultSqliteDbContext db) : base(x => x.Id)
    {
        _db = db;
        EntitySetName = "ArithFaultItems";
        FilterEnabled = true;
        CountEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(_db.Items.AsQueryable());
    }
}

public class FilterArithmeticFaultSqliteTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        // Keep-alive in-memory database: lives as long as this connection stays open.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<ArithFaultSqliteProfile>(),
            configureServices: services =>
            {
                services.AddDbContext<ArithFaultSqliteDbContext>(o => o.UseSqlite(_connection));
            });

        using var scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArithFaultSqliteDbContext>();
        db.Database.EnsureCreated();
        db.Items.AddRange(
            new ArithFaultSqliteItem { Id = 1, Quantity = 4, Amount = 10.5m },
            new ArithFaultSqliteItem { Id = 2, Quantity = 7, Amount = 20.0m });
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    /// <summary>
    /// Empirically, EF Core's Sqlite provider pushes `div`/`mod` into SQL and SQLite evaluates
    /// division-by-zero as NULL, so the request completes as a normal 200 with zero matching
    /// rows (`NULL eq 1` is false) -- never a 400 InvalidQueryOption (no exception is ever
    /// raised) and, critically, never the 500 #358 reported. Asserts the full, specific outcome
    /// rather than merely "not 500" so a future EF Core/Sqlite version that started translating
    /// this differently would be caught rather than silently accepted.
    /// </summary>
    private static async Task AssertOkWithNoMatches(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("value").EnumerateArray());
    }

    [Fact]
    public async Task Filter_DivByZero_Int_NeverUnhandled500()
    {
        var response = await _fx.Client.GetAsync(
            "/odata/ArithFaultItems?$filter=" + Uri.EscapeDataString("Quantity div 0 eq 1"));
        await AssertOkWithNoMatches(response);
    }

    [Fact]
    public async Task Filter_ModByZero_Int_NeverUnhandled500()
    {
        var response = await _fx.Client.GetAsync(
            "/odata/ArithFaultItems?$filter=" + Uri.EscapeDataString("Quantity mod 0 eq 1"));
        await AssertOkWithNoMatches(response);
    }

    [Fact]
    public async Task Filter_DivByZero_Decimal_NeverUnhandled500()
    {
        var response = await _fx.Client.GetAsync(
            "/odata/ArithFaultItems?$filter=" + Uri.EscapeDataString("Amount div 0 eq 1"));
        await AssertOkWithNoMatches(response);
    }
}
