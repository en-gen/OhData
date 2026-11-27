using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(V740SpecialKid), "s")]
public class V740Kid
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public string Name { get; set; } = "";
    public List<V740Gift> Gifts { get; set; } = new();
}

public sealed class V740SpecialKid : V740Kid
{
    public string Sparkle { get; set; } = "";
    public List<V740Toy> Toys { get; set; } = new();
}

public sealed class V740Toy
{
    public int Id { get; set; }
    public int SpecialKidId { get; set; }
    public string Label { get; set; } = "";
}

public sealed class V740Gift
{
    public int Id { get; set; }
    public int KidId { get; set; }
    public string Label { get; set; } = "";
}

public sealed class V740Note
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Text { get; set; } = "";
}

public sealed class V740Holder
{
    public int Id { get; set; }
    public List<V740Parent> Parents { get; set; } = new();
}

public sealed class V740Parent
{
    public int? HolderId { get; set; }
    public int? SuperId { get; set; }
    public List<V740Parent> Subs { get; set; } = new();
    public List<V740Note> Notes { get; set; } = new();
    public int Id { get; set; }
    public int? BestId { get; set; }
    public V740Kid? Best { get; set; }
    public List<V740Kid> Kids { get; set; } = new();
}

// A non-polymorphic target, for the "pushdown is unchanged" control.
public sealed class V740Plain
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class V740Owner
{
    public int Id { get; set; }
    public List<V740Plain> Plains { get; set; } = new();
}

public sealed class V740DbContext : DbContext
{
    public V740DbContext(DbContextOptions<V740DbContext> options) : base(options) { }
    public DbSet<V740Parent> Parents => Set<V740Parent>();
    public DbSet<V740Kid> Kids => Set<V740Kid>();
    public DbSet<V740Toy> Toys => Set<V740Toy>();
    public DbSet<V740Gift> Gifts => Set<V740Gift>();
    public DbSet<V740Holder> Holders => Set<V740Holder>();
    public DbSet<V740Note> Notes => Set<V740Note>();
    public DbSet<V740Owner> Owners => Set<V740Owner>();
    public DbSet<V740Plain> Plains => Set<V740Plain>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<V740Parent>().HasMany(p => p.Kids).WithOne().HasForeignKey(k => k.ParentId);
        b.Entity<V740Parent>().HasOne(p => p.Best).WithMany().HasForeignKey(p => p.BestId);
        b.Entity<V740Holder>().HasMany(h => h.Parents).WithOne().HasForeignKey(p => p.HolderId);
        b.Entity<V740Parent>().HasMany(p => p.Subs).WithOne().HasForeignKey(p => p.SuperId);
        b.Entity<V740Parent>().HasMany(p => p.Notes).WithOne().HasForeignKey(n => n.ParentId);
        b.Entity<V740Kid>().HasMany(k => k.Gifts).WithOne().HasForeignKey(g => g.KidId);
        b.Entity<V740SpecialKid>().HasMany(k => k.Toys).WithOne().HasForeignKey(t => t.SpecialKidId);
        b.Entity<V740Owner>().HasMany(o => o.Plains).WithOne().HasForeignKey(p => p.OwnerId);
    }
}

public sealed class V740ParentProfile : EntitySetProfile<int, V740Parent>
{
    public V740ParentProfile(V740DbContext db) : base(x => x.Id)
    {
        EntitySetName = "Parents";
        ExpandEnabled = true;
        SelectEnabled = true;
        OrderByEnabled = true;
        FilterEnabled = true;
        GetQueryable = () => db.Parents.AsQueryable();
        // GetById never rides the pushdown: the delegate loads the graph it wants served.
        GetById = (id, _) => OhDataResult.Success(
            db.Parents.Include(p => p.Kids).Include(p => p.Best).FirstOrDefault(p => p.Id == id));
        HasMany(x => x.Kids);
        HasMany(x => x.Notes);
        HasMany(x => x.Subs);
        HasOptional(x => x.Best!);
    }
}

public sealed class V740KidProfile : EntitySetProfile<int, V740Kid>
{
    public V740KidProfile(V740DbContext db) : base(x => x.Id)
    {
        EntitySetName = "Kids";
        ExpandEnabled = true;
        SelectEnabled = true;
        OrderByEnabled = true;
        FilterEnabled = true;
        GetQueryable = () => db.Kids.AsQueryable();
        HasMany(x => x.Gifts);
    }
}

public sealed class V740HolderProfile : EntitySetProfile<int, V740Holder>
{
    public V740HolderProfile(V740DbContext db) : base(x => x.Id)
    {
        EntitySetName = "Holders";
        ExpandEnabled = true;
        SelectEnabled = true;
        GetQueryable = () => db.Holders.AsQueryable();
        HasMany(x => x.Parents);
    }
}

public sealed class V740OwnerProfile : EntitySetProfile<int, V740Owner>
{
    public V740OwnerProfile(V740DbContext db) : base(x => x.Id)
    {
        EntitySetName = "Owners";
        ExpandEnabled = true;
        GetQueryable = () => db.Owners.AsQueryable();
        HasMany(x => x.Plains);
    }
}

/// <summary>
/// #740 - an expanded navigation whose TARGET is a TPH hierarchy keeps its derived members and
/// <c>@odata.type</c>. The #529 fix refused the member-init projection for a polymorphic ROOT only;
/// the same projection one level down rebuilt each derived child as the declared type.
/// </summary>
public sealed class Issue740PolymorphicExpandPushdownTests
{
    private const string SpecialType = "#OhData.AspNetCore.Tests.V740SpecialKid";

    private static async Task<TestFixture> BuildAsync(SqliteConnection connection, SqlCaptureSink? sink = null)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<V740ParentProfile>();
                b.AddEntitySetProfile<V740KidProfile>();
                b.AddEntitySetProfile<V740OwnerProfile>();
                b.AddEntitySetProfile<V740HolderProfile>();
            },
            configureServices: services => services.AddDbContext<V740DbContext>(o =>
            {
                o.UseSqlite(connection);
                if (sink is not null)
                {
                    o.LogTo(m => sink.Add(m), (id, _) => id == RelationalEventId.CommandExecuted);
                }
            }));

        using IServiceScope scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<V740DbContext>();
        db.Database.EnsureCreated();
        // The Parent <-> Kid FKs are mutually dependent, so the parent is saved first and Best set after.
        db.Holders.Add(new V740Holder { Id = 1 });
        db.SaveChanges();
        db.Parents.Add(new V740Parent { Id = 1, HolderId = 1 });
        db.SaveChanges();
        db.Parents.Add(new V740Parent { Id = 2, SuperId = 1 });
        db.Parents.Add(new V740Parent { Id = 3, SuperId = 2 });
        db.SaveChanges();
        db.Kids.Add(new V740Kid { Id = 1, ParentId = 1, Name = "plain" });
        db.Kids.Add(new V740SpecialKid { Id = 2, ParentId = 1, Name = "special", Sparkle = "SPARKLE" });
        db.SaveChanges();
        db.Parents.Single(p => p.Id == 1).BestId = 2;
        db.Toys.Add(new V740Toy { Id = 50, SpecialKidId = 2, Label = "robot" });
        db.Toys.Add(new V740Toy { Id = 51, SpecialKidId = 2, Label = "kite" });
        db.Gifts.Add(new V740Gift { Id = 70, KidId = 1, Label = "g-plain" });
        db.Gifts.Add(new V740Gift { Id = 71, KidId = 2, Label = "g-special" });
        db.Notes.Add(new V740Note { Id = 90, ParentId = 1, Text = "n1" });
        db.Owners.Add(new V740Owner { Id = 1 });
        db.Plains.Add(new V740Plain { Id = 5, OwnerId = 1, Name = "pl" });
        db.SaveChanges();
        return fx;
    }

    private static async Task<JsonElement> GetJsonAsync(TestFixture fx, string url)
    {
        HttpResponseMessage response = await fx.Client.GetAsync(url);
        string raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}: {raw}");
        return JsonDocument.Parse(raw).RootElement;
    }

    private static JsonElement ById(JsonElement array, int id) =>
        array.EnumerateArray().Single(e => e.GetProperty("Id").GetInt32() == id);

    private static void AssertSpecial(JsonElement kid)
    {
        Assert.Equal(2, kid.GetProperty("Id").GetInt32());
        Assert.Equal("special", kid.GetProperty("Name").GetString());
        Assert.Equal("SPARKLE", kid.GetProperty("Sparkle").GetString());
        Assert.Equal(SpecialType, kid.GetProperty("@odata.type").GetString());
    }

    private static void AssertPlain(JsonElement kid)
    {
        Assert.Equal(1, kid.GetProperty("Id").GetInt32());
        Assert.Equal("plain", kid.GetProperty("Name").GetString());
        Assert.False(kid.TryGetProperty("Sparkle", out _));
        Assert.False(kid.TryGetProperty("@odata.type", out _));
    }

    [Fact]
    public async Task Control_KidsSet_ReturnsDerivedMembers()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement value = (await GetJsonAsync(fx, "/odata/Kids")).GetProperty("value");

        AssertSpecial(ById(value, 2));
        AssertPlain(ById(value, 1));
    }

    [Fact]
    public async Task ExpandedCollection_KeepsDerivedMembersAndType()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement parent = (await GetJsonAsync(fx, "/odata/Parents?$expand=Kids")).GetProperty("value")[0];
        JsonElement kids = parent.GetProperty("Kids");

        Assert.Equal(2, kids.GetArrayLength());
        AssertSpecial(ById(kids, 2));
        AssertPlain(ById(kids, 1));
    }

    [Fact]
    public async Task ExpandedSingleValuedNavigation_KeepsDerivedMembersAndType()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement parent = (await GetJsonAsync(fx, "/odata/Parents?$expand=Best")).GetProperty("value")[0];

        Assert.Equal(2, parent.GetProperty("BestId").GetInt32());
        AssertSpecial(parent.GetProperty("Best"));
    }

    [Fact]
    public async Task SingleEntityForm_Best_KeepsDerivedMembersAndType()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement parent = await GetJsonAsync(fx, "/odata/Parents(1)?$expand=Best");

        AssertSpecial(parent.GetProperty("Best"));
    }

    [Fact]
    public async Task SingleEntityForm_Kids_KeepsDerivedMembersAndType()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement kids = (await GetJsonAsync(fx, "/odata/Parents(1)?$expand=Kids")).GetProperty("Kids");

        AssertSpecial(ById(kids, 2));
        AssertPlain(ById(kids, 1));
    }

    [Fact]
    public async Task DepthTwo_NestedNavigationUnderCollection_KeepsDerivedMembersAndType()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement kids = (await GetJsonAsync(fx, "/odata/Parents?$expand=Kids($expand=Gifts)"))
            .GetProperty("value")[0].GetProperty("Kids");

        AssertSpecial(ById(kids, 2));
        AssertPlain(ById(kids, 1));
        Assert.Equal("g-special", ById(kids, 2).GetProperty("Gifts")[0].GetProperty("Label").GetString());
        Assert.Equal("g-plain", ById(kids, 1).GetProperty("Gifts")[0].GetProperty("Label").GetString());
    }

    [Fact]
    public async Task DepthTwo_NestedNavigationUnderSingleValued_KeepsDerivedMembersAndType()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement best = (await GetJsonAsync(fx, "/odata/Parents?$expand=Best($expand=Gifts)"))
            .GetProperty("value")[0].GetProperty("Best");

        AssertSpecial(best);
        Assert.Equal("g-special", best.GetProperty("Gifts")[0].GetProperty("Label").GetString());
    }

    [Theory]
    [InlineData("$expand=Kids($filter=Id gt 0)")]
    [InlineData("$expand=Kids($orderby=Id desc)")]
    [InlineData("$expand=Kids($top=5)")]
    [InlineData("$expand=Kids($filter=Id gt 0;$orderby=Id asc;$top=5)")]
    public async Task NestedOptionsUnderPolymorphicTarget_AreServedWithDerivedMembers(string query)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement kids = (await GetJsonAsync(fx, "/odata/Parents?" + query))
            .GetProperty("value")[0].GetProperty("Kids");

        Assert.Equal(2, kids.GetArrayLength());
        AssertSpecial(ById(kids, 2));
        AssertPlain(ById(kids, 1));
    }

    [Fact]
    public async Task NestedFilterAndTop_ActuallyApply_OnPolymorphicTarget()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement filtered = (await GetJsonAsync(fx, "/odata/Parents?$expand=Kids($filter=Id gt 1)"))
            .GetProperty("value")[0].GetProperty("Kids");
        Assert.Equal(1, filtered.GetArrayLength());
        AssertSpecial(filtered[0]);

        JsonElement top = (await GetJsonAsync(fx, "/odata/Parents?$expand=Kids($orderby=Id desc;$top=1)"))
            .GetProperty("value")[0].GetProperty("Kids");
        Assert.Equal(1, top.GetArrayLength());
        AssertSpecial(top[0]);
    }

    [Fact]
    public async Task DepthTwo_NestedFilterAndTopUnderCollection_ApplyAndKeepDerivedMembers()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement kids = (await GetJsonAsync(fx,
            "/odata/Parents?$expand=Kids($filter=Id gt 1;$expand=Gifts($top=1))"))
            .GetProperty("value")[0].GetProperty("Kids");

        Assert.Equal(1, kids.GetArrayLength());
        AssertSpecial(kids[0]);
        JsonElement gifts = kids[0].GetProperty("Gifts");
        Assert.Equal(1, gifts.GetArrayLength());
        Assert.Equal("g-special", gifts[0].GetProperty("Label").GetString());
    }

    [Fact]
    public async Task MixedRequest_PolymorphicAndPlainNavigation_LeavesTheProjection_AndServesBoth()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BuildAsync(connection, sink);
        sink.Clear();

        JsonElement parent = (await GetJsonAsync(fx, "/odata/Parents?$expand=Kids,Notes")).GetProperty("value")[0];

        AssertSpecial(ById(parent.GetProperty("Kids"), 2));
        Assert.Equal("n1", parent.GetProperty("Notes")[0].GetProperty("Text").GetString());
        // Include path: the discriminator and the derived column are selected (a member-init would omit
        // both), and the plain navigation rides the same statement.
        string select = Assert.Single(sink.Snapshot(), x => x.Contains("SELECT", StringComparison.Ordinal));
        Assert.Contains("\"Discriminator\"", select, StringComparison.Ordinal);
        Assert.Contains("\"Sparkle\"", select, StringComparison.Ordinal);
        Assert.Contains("JOIN \"Kids\"", select, StringComparison.Ordinal);
        Assert.Contains("JOIN \"Notes\"", select, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolymorphicTarget_SelectsDiscriminatorAndDerivedColumns_InOneJoinedStatement()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BuildAsync(connection, sink);
        sink.Clear();

        await GetJsonAsync(fx, "/odata/Parents?$expand=Kids");
        string select = Assert.Single(sink.Snapshot(), x => x.Contains("SELECT", StringComparison.Ordinal));
        Assert.Contains("\"Discriminator\"", select, StringComparison.Ordinal);
        Assert.Contains("\"Sparkle\"", select, StringComparison.Ordinal);
        Assert.Contains("JOIN \"Kids\"", select, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$select=Id&$expand=Kids")]
    [InlineData("$select=Id&$expand=Kids($expand=Gifts)")]
    [InlineData("$select=Id&$expand=Kids($select=Name)")]
    public async Task SelectBesideAPolymorphicCollectionExpand_KeepsTheNavigationPopulated(string query)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement parent = (await GetJsonAsync(fx, "/odata/Parents?" + query)).GetProperty("value")[0];
        JsonElement kids = parent.GetProperty("Kids");

        Assert.Equal(2, kids.GetArrayLength());
        JsonElement special = DerivedRow(kids);
        Assert.Equal(SpecialType, special.GetProperty("@odata.type").GetString());
        Assert.Equal("special", special.GetProperty("Name").GetString());
        if (!query.Contains("Kids($select", StringComparison.Ordinal))
        {
            Assert.Equal("SPARKLE", special.GetProperty("Sparkle").GetString());
        }

        if (query.Contains("Gifts", StringComparison.Ordinal))
        {
            Assert.Equal("g-special", special.GetProperty("Gifts")[0].GetProperty("Label").GetString());
        }
    }

    // A nested $select shapes the payload and may strip the key; locate the derived row by its annotation.
    private static JsonElement DerivedRow(JsonElement kids) =>
        kids.EnumerateArray().Single(k => k.TryGetProperty("@odata.type", out _));

    [Theory]
    [InlineData("$select=Id&$expand=Best")]
    [InlineData("$select=Id&$expand=Best($expand=Gifts)")]
    public async Task SelectBesideAPolymorphicSingleValuedExpand_KeepsTheNavigationPopulated(string query)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement parent = (await GetJsonAsync(fx, "/odata/Parents?" + query)).GetProperty("value")[0];

        AssertSpecial(parent.GetProperty("Best"));
    }

    [Fact]
    public async Task SelectBesideANonPolymorphicExpand_StillProjectsToOneJoinedStatement()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BuildAsync(connection, sink);
        sink.Clear();

        JsonElement parent = (await GetJsonAsync(fx, "/odata/Parents?$select=Id&$expand=Notes")).GetProperty("value")[0];

        Assert.Equal("n1", parent.GetProperty("Notes")[0].GetProperty("Text").GetString());
        string select = Assert.Single(sink.Snapshot(), x => x.Contains("SELECT", StringComparison.Ordinal));
        Assert.DoesNotContain("\"Discriminator\"", select, StringComparison.Ordinal);
        Assert.Contains("JOIN \"Notes\"", select, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LevelsOnANonPolymorphicSibling_IsServed_BesideAPolymorphicExpand()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        // A root filter leaves rows 2 and 3 out of the root set, so they can only arrive through the
        // levels branch itself.
        JsonElement parent = (await GetJsonAsync(fx,
            "/odata/Parents?$filter=Id eq 1&$expand=Kids,Subs($levels=3)")).GetProperty("value")[0];

        AssertSpecial(ById(parent.GetProperty("Kids"), 2));
        JsonElement subs = parent.GetProperty("Subs");
        Assert.Equal(1, subs.GetArrayLength());
        Assert.Equal(2, subs[0].GetProperty("Id").GetInt32());
        JsonElement level2 = subs[0].GetProperty("Subs");
        Assert.Equal(1, level2.GetArrayLength());
        Assert.Equal(3, level2[0].GetProperty("Id").GetInt32());
        Assert.Equal(0, level2[0].GetProperty("Subs").GetArrayLength());
    }

    [Fact]
    public async Task PolymorphicTargetAtDepthTwo_UnderANonPolymorphicFirstLevel_KeepsDerivedMembersAndType()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement parents = (await GetJsonAsync(fx, "/odata/Holders?$expand=Parents($expand=Kids($expand=Gifts))"))
            .GetProperty("value")[0].GetProperty("Parents");
        JsonElement kids = ById(parents, 1).GetProperty("Kids");

        AssertSpecial(ById(kids, 2));
        AssertPlain(ById(kids, 1));
        Assert.Equal("g-special", ById(kids, 2).GetProperty("Gifts")[0].GetProperty("Label").GetString());
    }

    [Fact]
    public async Task NonPolymorphicTarget_StillYieldsOneJoinedStatement()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        await using TestFixture fx = await BuildAsync(connection, sink);
        sink.Clear();

        JsonElement owner = (await GetJsonAsync(fx, "/odata/Owners?$expand=Plains")).GetProperty("value")[0];

        Assert.Equal("pl", owner.GetProperty("Plains")[0].GetProperty("Name").GetString());
        string[] selects = sink.Snapshot()
            .Where(s => s.Contains("SELECT", StringComparison.Ordinal)).ToArray();
        string select = Assert.Single(selects);
        Assert.Contains("\"Owners\"", select, StringComparison.Ordinal);
        Assert.Contains("\"Plains\"", select, StringComparison.Ordinal);
        Assert.Contains("JOIN", select, StringComparison.Ordinal);
    }
}
