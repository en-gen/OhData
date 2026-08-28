using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #358: the TestBench's own reported repro (`Rating div 0` against OhData.TestBench.AspNetCore,
// which uses Microsoft.EntityFrameworkCore.InMemory) surfaced as an unhandled 500. Unlike a real
// relational provider (see FilterArithmeticFaultSqliteTests, where SQLite decides division by
// zero is NULL and the SQL never faults), EF Core's InMemory provider evaluates LINQ predicates
// against plain CLR objects -- the same execution model as LINQ-to-Objects -- so `div`/`mod` by
// zero raises a real DivideByZeroException at query-enumeration time, exactly like the
// GetQueryable(List<T>.AsQueryable()) fixtures in AdversarialQueryOptionTests. This file is the
// closest automated reproduction of the exact provider (EF Core InMemory) the issue was filed
// against.

internal sealed class ArithFaultEfItem
{
    public int Id { get; set; }
    public int Quantity { get; set; }
}

internal sealed class ArithFaultEfDbContext : DbContext
{
    public ArithFaultEfDbContext(DbContextOptions<ArithFaultEfDbContext> options) : base(options) { }

    public DbSet<ArithFaultEfItem> Items => Set<ArithFaultEfItem>();
}

internal sealed class ArithFaultEfProfile : EntitySetProfile<int, ArithFaultEfItem>
{
    public ArithFaultEfProfile() : base(x => x.Id)
    {
        EntitySetName = "ArithFaultEfItems";
        FilterEnabled = true;

        GetQueryable = (ct) =>
        {
            var opts = new DbContextOptionsBuilder<ArithFaultEfDbContext>()
                .UseInMemoryDatabase("ArithFaultEfItems")
                .Options;
            var db = new ArithFaultEfDbContext(opts);
            if (!db.Items.Any())
            {
                db.Items.AddRange(
                    new ArithFaultEfItem { Id = 1, Quantity = 4 },
                    new ArithFaultEfItem { Id = 2, Quantity = 7 });
                db.SaveChanges();
            }
            return Task.FromResult(db.Items.AsQueryable());
        };
    }
}

public class FilterArithmeticFaultEfInMemoryTests
{
    private const string Url = "/odata/ArithFaultEfItems";

    [Fact]
    public async Task Filter_DivByZero_EfCoreInMemoryProvider_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ArithFaultEfProfile>());
        var response = await fx.Client.GetAsync(Url + "?$filter=" + Uri.EscapeDataString("Quantity div 0 eq 1"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Filter_ModByZero_EfCoreInMemoryProvider_Returns400ODataError()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ArithFaultEfProfile>());
        var response = await fx.Client.GetAsync(Url + "?$filter=" + Uri.EscapeDataString("Quantity mod 0 eq 1"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
    }
}
