using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(Z740Special), "s")]
public class Z740Kid
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public int? BuddyOfId { get; set; }
    public string Name { get; set; } = "";
    public List<Z740Kid> Buddies { get; set; } = new();
}

public sealed class Z740Special : Z740Kid
{
    public string Sparkle { get; set; } = "";
}

public sealed class Z740Parent
{
    public int Id { get; set; }
    public int? SuperId { get; set; }
    public string Tag { get; set; } = "";
    public List<Z740Parent> Subs { get; set; } = new();
    public List<Z740Kid> Kids { get; set; } = new();
}

public sealed class Z740Db : DbContext
{
    public Z740Db(DbContextOptions<Z740Db> o) : base(o) { }
    public DbSet<Z740Parent> Parents => Set<Z740Parent>();
    public DbSet<Z740Kid> Kids => Set<Z740Kid>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Z740Parent>().HasMany(p => p.Kids).WithOne().HasForeignKey(k => k.ParentId);
        b.Entity<Z740Parent>().HasMany(p => p.Subs).WithOne().HasForeignKey(p => p.SuperId);
        b.Entity<Z740Kid>().HasMany(k => k.Buddies).WithOne().HasForeignKey(k => k.BuddyOfId);
        b.Entity<Z740Special>();
    }
}

public sealed class Z740ParentProfile : EntitySetProfile<int, Z740Parent>
{
    public Z740ParentProfile(Z740Db db) : base(x => x.Id)
    {
        EntitySetName = "ZParents";
        ExpandEnabled = true;
        SelectEnabled = true;
        OrderByEnabled = true;
        FilterEnabled = true;
        GetQueryable = () => db.Parents.AsQueryable();
        HasMany(x => x.Kids);
        HasMany(x => x.Subs);
    }
}

public sealed class Z740KidProfile : EntitySetProfile<int, Z740Kid>
{
    public Z740KidProfile(Z740Db db) : base(x => x.Id)
    {
        EntitySetName = "ZKids";
        ExpandEnabled = true;
        SelectEnabled = true;
        OrderByEnabled = true;
        FilterEnabled = true;
        GetQueryable = () => db.Kids.AsQueryable();
        HasMany(x => x.Buddies);
    }
}

/// <summary>
/// #740 - a self-referential <c>$levels</c> branch beside an expand that reaches a polymorphic target is served
/// through the Include path. The projection path (the same request without the polymorphic sibling) is the
/// oracle: the rows must match it exactly.
/// </summary>
public sealed class Issue740LevelsBesidePolymorphicTests
{
    private static async Task<TestFixture> BuildAsync(SqliteConnection connection)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<Z740ParentProfile>();
                b.AddEntitySetProfile<Z740KidProfile>();
            },
            configureServices: s => s.AddDbContext<Z740Db>(o => o.UseSqlite(connection)));

        using IServiceScope scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Z740Db>();
        db.Database.EnsureCreated();

        // Chain 1 -> 2 -> 3 -> 4 -> 5 through Subs, and a second root, 10.
        db.Parents.Add(new Z740Parent { Id = 1, Tag = "root" });
        db.SaveChanges();
        for (int i = 2; i <= 5; i++)
        {
            db.Parents.Add(new Z740Parent { Id = i, SuperId = i - 1, Tag = "L" + (i - 1) });
            db.SaveChanges();
        }

        db.Parents.Add(new Z740Parent { Id = 10, Tag = "root2" });
        db.SaveChanges();

        db.Kids.Add(new Z740Kid { Id = 1, ParentId = 1, Name = "plain" });
        db.Kids.Add(new Z740Special { Id = 2, ParentId = 1, Name = "special", Sparkle = "SP" });
        db.SaveChanges();
        db.Kids.Add(new Z740Special { Id = 3, BuddyOfId = 2, Name = "buddy", Sparkle = "B3" });
        db.Kids.Add(new Z740Kid { Id = 4, BuddyOfId = 3, Name = "buddy2" });
        db.SaveChanges();
        return fx;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(TestFixture fx, string url)
    {
        HttpResponseMessage r = await fx.Client.GetAsync("/odata/" + url);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    // The rows with property names sorted, the polymorphic sibling dropped, and OData control information
    // at the envelope ignored, so the two responses compare byte for byte on everything they share.
    private static string Canonical(string body)
    {
        JsonElement value = JsonDocument.Parse(body).RootElement.GetProperty("value");
        var sb = new StringBuilder();
        foreach (JsonElement row in value.EnumerateArray())
        {
            Write(sb, row, dropKids: true);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonElement e, bool dropKids)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                foreach (JsonProperty p in e.EnumerateObject()
                             .Where(p => !(dropKids && (p.Name == "Kids" || p.Name == "Kids@odata.count")))
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    sb.Append(p.Name).Append(':');
                    Write(sb, p.Value, dropKids: false);
                    sb.Append(',');
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                foreach (JsonElement item in e.EnumerateArray())
                {
                    Write(sb, item, dropKids: false);
                    sb.Append(',');
                }

                sb.Append(']');
                break;
            default:
                sb.Append(e.GetRawText());
                break;
        }
    }

    [Theory]
    [InlineData("$filter=Id eq 1", "Kids", "Subs($levels=2)")]
    [InlineData("$filter=Id eq 1", "Kids", "Subs($levels=3)")]
    [InlineData("$filter=Id eq 1", "Kids", "Subs($levels=max)")]
    [InlineData("$filter=Id eq 2", "Kids", "Subs($levels=2)")]
    [InlineData("$top=1", "Kids", "Subs($levels=2)")]
    [InlineData("$filter=Id gt 0", "Kids", "Subs($levels=2)")]
    [InlineData("$filter=Id eq 1", "Kids", "Subs($levels=2;$filter=Tag ne 'L2')")]
    [InlineData("$filter=Id gt 0", "Kids", "Subs($levels=2;$filter=Tag ne 'L2')")]
    [InlineData("$filter=Id eq 1", "Kids", "Subs($levels=3;$filter=Id ne 3)")]
    [InlineData("$filter=Id gt 0", "Kids", "Subs($levels=max;$filter=Tag ne 'L2')")]
    [InlineData("$filter=Id eq 1", "Kids", "Subs($levels=2;$top=1)")]
    [InlineData("$filter=Id eq 1", "Kids", "Subs($levels=3;$orderby=Id desc;$top=1)")]
    [InlineData("$filter=Id eq 1", "Kids", "Subs($levels=2;$select=Tag)")]
    [InlineData("$filter=Id eq 1&$select=Tag", "Kids($select=Name)", "Subs($levels=2)")]
    [InlineData("$filter=Id eq 1", "Kids", "Subs($expand=Subs($levels=2))")]
    public async Task LevelsBesideAPolymorphicExpand_MatchesTheSameRequestWithoutIt(
        string root, string kids, string subs)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        (HttpStatusCode withStatus, string withBody) = await GetAsync(fx, $"ZParents?{root}&$orderby=Id&$expand={kids},{subs}");
        (HttpStatusCode oracleStatus, string oracleBody) = await GetAsync(fx, $"ZParents?{root}&$orderby=Id&$expand={subs}");

        Assert.True(oracleStatus == HttpStatusCode.OK, "oracle: " + oracleBody);
        Assert.True(withStatus == HttpStatusCode.OK, withBody);
        Assert.Equal(Canonical(oracleBody), Canonical(withBody));
    }

    [Fact]
    public async Task LevelsBesideAPolymorphicExpand_StillCarriesTheDerivedKid()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        (HttpStatusCode status, string body) = await GetAsync(fx, "ZParents?$filter=Id eq 1&$expand=Kids,Subs($levels=2)");

        Assert.Equal(HttpStatusCode.OK, status);
        JsonElement kids = JsonDocument.Parse(body).RootElement.GetProperty("value")[0].GetProperty("Kids");
        JsonElement special = kids.EnumerateArray().Single(k => k.GetProperty("Id").GetInt32() == 2);
        Assert.Equal("SP", special.GetProperty("Sparkle").GetString());
        Assert.Equal("#OhData.AspNetCore.Tests.Z740Special", special.GetProperty("@odata.type").GetString());
    }

    [Fact]
    public async Task LevelsUnderAPolymorphicTarget_ReachedFromANonPolymorphicRoot_StillAnswers400()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        (HttpStatusCode status, string body) = await GetAsync(
            fx, "ZParents?$filter=Id eq 1&$expand=Kids($expand=Buddies($levels=2))");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("$levels", body, StringComparison.Ordinal);
    }
}
