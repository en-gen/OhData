using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class TiRoot
{
    public int Id { get; set; }
    public string Computed => "c" + Id;   // get-only -> member-init projection ineligible -> Include path
    public int? OwnerId { get; set; }
    public TiOwner? Owner { get; set; }
    public List<TiPart> Parts { get; set; } = new();
}

public sealed class TiOwner
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<TiItem> Items { get; set; } = new();
}

public sealed class TiItem
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public string Label { get; set; } = "";
}

public sealed class TiPart
{
    public int Id { get; set; }
    public int RootId { get; set; }
    public int? StampId { get; set; }
    public TiStamp? Stamp { get; set; }
}

public sealed class TiStamp
{
    public int Id { get; set; }
    public string Mark { get; set; } = "";
}

public sealed class TiDbContext : DbContext
{
    public TiDbContext(DbContextOptions<TiDbContext> options) : base(options) { }
    public DbSet<TiRoot> Roots => Set<TiRoot>();
    public DbSet<TiOwner> Owners => Set<TiOwner>();
    public DbSet<TiItem> Items => Set<TiItem>();
    public DbSet<TiPart> Parts => Set<TiPart>();
    public DbSet<TiStamp> Stamps => Set<TiStamp>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TiRoot>().Ignore(r => r.Computed);
        b.Entity<TiRoot>().HasOne(r => r.Owner).WithMany().HasForeignKey(r => r.OwnerId);
        b.Entity<TiRoot>().HasMany(r => r.Parts).WithOne().HasForeignKey(p => p.RootId);
        b.Entity<TiOwner>().HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OwnerId);
        b.Entity<TiPart>().HasOne(p => p.Stamp).WithMany().HasForeignKey(p => p.StampId);
    }
}

public sealed class TiRootProfile : EntitySetProfile<int, TiRoot>
{
    public TiRootProfile(TiDbContext db) : base(x => x.Id)
    {
        EntitySetName = "TiRoots";
        ExpandEnabled = true;
        GetQueryable = () => db.Roots.AsQueryable();
        HasOptional(x => x.Owner!);
        HasMany(x => x.Parts);
    }
}

/// <summary>
/// The Include fallback chains ThenInclude by reflection. EF's two ThenInclude overloads differ by the
/// PREVIOUS navigation's shape (collection vs reference), so the overload must be chosen by the previous
/// navigation, not by the child. Both mixed shapes used to throw an ArgumentException (500).
/// </summary>
public sealed class ThenIncludeOverloadSelectionTests
{
    private static async Task<TestFixture> BuildAsync(SqliteConnection connection)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<TiRootProfile>(),
            configureServices: s => s.AddDbContext<TiDbContext>(o => o.UseSqlite(connection)));
        using IServiceScope scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TiDbContext>();
        db.Database.EnsureCreated();
        db.Owners.Add(new TiOwner { Id = 1, Name = "o" });
        db.Items.Add(new TiItem { Id = 10, OwnerId = 1, Label = "i1" });
        db.Items.Add(new TiItem { Id = 11, OwnerId = 1, Label = "i2" });
        db.Stamps.Add(new TiStamp { Id = 5, Mark = "M" });
        db.Roots.Add(new TiRoot { Id = 1, OwnerId = 1 });
        db.SaveChanges();
        db.Parts.Add(new TiPart { Id = 20, RootId = 1, StampId = 5 });
        db.SaveChanges();
        return fx;
    }

    [Fact]
    public async Task ReferenceParent_WithCollectionChild_IsServed()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        HttpResponseMessage r = await fx.Client.GetAsync("/odata/TiRoots?$expand=Owner($expand=Items)");
        string body = await r.Content.ReadAsStringAsync();

        Assert.True(r.StatusCode == HttpStatusCode.OK, body);
        JsonElement owner = JsonDocument.Parse(body).RootElement.GetProperty("value")[0].GetProperty("Owner");
        Assert.Equal("o", owner.GetProperty("Name").GetString());
        Assert.Equal(new[] { "i1", "i2" },
            owner.GetProperty("Items").EnumerateArray().Select(i => i.GetProperty("Label").GetString()!).OrderBy(x => x));
    }

    [Fact]
    public async Task CollectionParent_WithReferenceChild_IsServed()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        HttpResponseMessage r = await fx.Client.GetAsync("/odata/TiRoots?$expand=Parts($expand=Stamp)");
        string body = await r.Content.ReadAsStringAsync();

        Assert.True(r.StatusCode == HttpStatusCode.OK, body);
        JsonElement part = JsonDocument.Parse(body).RootElement.GetProperty("value")[0].GetProperty("Parts")[0];
        Assert.Equal("M", part.GetProperty("Stamp").GetProperty("Mark").GetString());
    }
}
