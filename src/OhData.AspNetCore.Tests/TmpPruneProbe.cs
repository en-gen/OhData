using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

public sealed class PrOrder { public int Id { get; set; } public string Code { get; set; } = ""; public List<PrLine> Lines { get; set; } = new(); }
public sealed class PrLine { public int Id { get; set; } public int PrOrderId { get; set; } public string Sku { get; set; } = ""; }
public sealed class PrDto { public int Id { get; set; } public string Code { get; set; } = ""; public List<PrLineDto> Lines { get; set; } = new(); }
public sealed class PrLineDto { public int Id { get; set; } public string Sku { get; set; } = ""; }

public sealed class PrDb : DbContext
{
    public PrDb(DbContextOptions<PrDb> o) : base(o) { }
    public DbSet<PrOrder> Orders => Set<PrOrder>();
    public DbSet<PrLine> Lines => Set<PrLine>();
}

public sealed class TmpPruneProbe
{
    private readonly ITestOutputHelper _o;
    public TmpPruneProbe(ITestOutputHelper o) => _o = o;

    [Fact]
    public async Task DoesAnOuterProjectionPruneTheInnerJoin()
    {
        using SqliteConnection conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        DbContextOptions<PrDb> opts = new DbContextOptionsBuilder<PrDb>().UseSqlite(conn).Options;
        using PrDb db = new PrDb(opts);
        db.Database.EnsureCreated();
        db.Orders.Add(new PrOrder { Id = 1, Code = "A" });
        db.Lines.Add(new PrLine { Id = 10, PrOrderId = 1, Sku = "S1" });
        await db.SaveChangesAsync();

        // The adopter's eager projection: Lines IS bound.
        IQueryable<PrDto> source = db.Orders.Select(o => new PrDto
        {
            Id = o.Id,
            Code = o.Code,
            Lines = o.Lines.Select(l => new PrLineDto { Id = l.Id, Sku = l.Sku }).ToList(),
        });

        _o.WriteLine("=== source as-is (what a plain GET runs today)");
        _o.WriteLine(source.ToQueryString());

        // What the framework WOULD compose if it projected unexpanded navigations away:
        // every structural member, no navigation binding.
        IQueryable<PrDto> pruned = source.Select(d => new PrDto { Id = d.Id, Code = d.Code });

        _o.WriteLine("");
        _o.WriteLine("=== outer projection dropping the nav binding");
        _o.WriteLine(pruned.ToQueryString());

        _o.WriteLine("");
        _o.WriteLine("rows: " + string.Join(",", pruned.Select(x => x.Code)));
    }
}
