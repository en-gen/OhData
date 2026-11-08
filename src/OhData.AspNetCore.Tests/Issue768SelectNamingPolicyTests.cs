using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #768: every $select projecting path must compare the key the serializer wrote, not the EDM name.
// Fixtures vary the naming policy, multi-word property and navigation names, a derived runtime type
// and a [JsonPropertyName] member.

public sealed class Sn768Phone
{
    public int Id { get; set; }
    public string PhoneNumber { get; set; } = "";
    public string CarrierName { get; set; } = "";
}

[JsonDerivedType(typeof(Sn768Employee))]
public class Sn768Person
{
    public int Id { get; set; }
    public string FirstName { get; set; } = "";
    [JsonPropertyName("custom_label")]
    public string Label { get; set; } = "";
    public List<Sn768Phone> MobilePhones { get; set; } = new();
}

public sealed class Sn768Employee : Sn768Person
{
    public string BadgeNumber { get; set; } = "";
}

public sealed class Sn768PersonProfile : EntitySetProfile<int, Sn768Person>
{
    private static readonly List<Sn768Person> Store = new()
    {
        new Sn768Person
        {
            Id = 1, FirstName = "Ada", Label = "L1",
            MobilePhones = { new Sn768Phone { Id = 10, PhoneNumber = "555", CarrierName = "C1" } },
        },
        new Sn768Employee { Id = 2, FirstName = "Alan", Label = "L2", BadgeNumber = "B2" },
    };

    public Sn768PersonProfile() : base(x => x.Id)
    {
        EntitySetName = "Sn768People";
        SelectEnabled = true;
        ExpandEnabled = true;
        GetQueryable = () => Store.AsQueryable();
        GetById = (id, _) => OhDataResult.Success(Store.FirstOrDefault(p => p.Id == id));
        HasMany(
            x => x.MobilePhones,
            getAll: (id, _) => Task.FromResult<IEnumerable<Sn768Phone>>(
                Store.Where(p => p.Id == id).SelectMany(p => p.MobilePhones).ToList()));
    }
}

// EF pushdown: a delegate-less MobilePhones and a delegate-less self-navigation for $levels.
public sealed class Sn768PushPerson
{
    public int Id { get; set; }
    public string FirstName { get; set; } = "";
    public int? ParentPersonId { get; set; }
    public List<Sn768Phone2> MobilePhones { get; set; } = new();
    public List<Sn768PushPerson> ChildPeople { get; set; } = new();
}

public sealed class Sn768Phone2
{
    public int Id { get; set; }
    public int PushPersonId { get; set; }
    public string PhoneNumber { get; set; } = "";
    public string CarrierName { get; set; } = "";
}

public sealed class Sn768DbContext : DbContext
{
    public Sn768DbContext(DbContextOptions<Sn768DbContext> options) : base(options) { }
    public DbSet<Sn768PushPerson> People => Set<Sn768PushPerson>();
    public DbSet<Sn768Phone2> Phones => Set<Sn768Phone2>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Sn768PushPerson>().HasMany(p => p.MobilePhones).WithOne().HasForeignKey(c => c.PushPersonId);
        modelBuilder.Entity<Sn768PushPerson>().HasMany(p => p.ChildPeople).WithOne().HasForeignKey(c => c.ParentPersonId);
    }
}

public sealed class Sn768PushPersonProfile : EntitySetProfile<int, Sn768PushPerson>
{
    public Sn768PushPersonProfile(Sn768DbContext db) : base(x => x.Id)
    {
        EntitySetName = "Sn768PushPeople";
        SelectEnabled = true;
        ExpandEnabled = true;
        GetQueryable = () => db.People.AsQueryable();
        HasMany(x => x.MobilePhones);
        HasMany(x => x.ChildPeople);
    }
}

public sealed class Sn768Owner
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string HomeTown { get; set; } = "";
}

public sealed class Sn768Car
{
    public int Id { get; set; }
    public string ModelName { get; set; } = "";
    public int? OwnerPersonId { get; set; }
    public Sn768Owner? OwnerPerson { get; set; }
}

public sealed class Sn768CarDb : DbContext
{
    public Sn768CarDb(DbContextOptions<Sn768CarDb> o) : base(o) { }
    public DbSet<Sn768Car> Cars => Set<Sn768Car>();
    public DbSet<Sn768Owner> Owners => Set<Sn768Owner>();
}

public sealed class Sn768CarProfile : EntitySetProfile<int, Sn768Car>
{
    public Sn768CarProfile(Sn768CarDb db) : base(x => x.Id)
    {
        EntitySetName = "Sn768Cars";
        SelectEnabled = true;
        ExpandEnabled = true;
        GetQueryable = () => db.Cars.AsQueryable();
        HasOptional<Sn768Owner>(x => x.OwnerPerson!);
    }
}

public class Issue768SelectNamingPolicyTests
{
    private static JsonNamingPolicy Policy(string name) => name switch
    {
        "snake" => JsonNamingPolicy.SnakeCaseLower,
        "kebab" => JsonNamingPolicy.KebabCaseLower,
        _ => JsonNamingPolicy.CamelCase,
    };

    // The keys the serializer writes for the fixture's members under each policy.
    private static (string First, string Phones, string Number, string Badge) Keys(string name) => name switch
    {
        "snake" => ("first_name", "mobile_phones", "phone_number", "badge_number"),
        "kebab" => ("first-name", "mobile-phones", "phone-number", "badge-number"),
        _ => ("firstName", "mobilePhones", "phoneNumber", "badgeNumber"),
    };

    private static Task<TestFixture> Build(string policy) => TestHostBuilder.BuildAsync(o =>
    {
        o.WithJsonPropertyNamingPolicy(Policy(policy));
        o.AddEntitySetProfile<Sn768PersonProfile>();
    });

    private static async Task<string> Ok(TestFixture fx, string url)
    {
        HttpResponseMessage r = await fx.Client.GetAsync(url);
        string body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)r.StatusCode}: {body}");
        return body;
    }

    [Theory]
    [InlineData("snake")]
    [InlineData("kebab")]
    [InlineData("camel")]
    public async Task Collection_SelectMultiWordProperty_KeepsPolicyKey(string policy)
    {
        await using var fx = await Build(policy);
        string body = await Ok(fx, "/odata/Sn768People?$select=FirstName");
        Assert.Contains($"\"{Keys(policy).First}\":\"Ada\"", body);
        Assert.Contains($"\"{Keys(policy).First}\":\"Alan\"", body);
        Assert.DoesNotContain("custom_label", body);
        Assert.DoesNotContain("\"Id\"", body);
        Assert.DoesNotContain("\"id\"", body);
    }

    [Theory]
    [InlineData("snake")]
    [InlineData("kebab")]
    [InlineData("camel")]
    public async Task ByKey_SelectMultiWordProperty_KeepsPolicyKey(string policy)
    {
        await using var fx = await Build(policy);
        string body = await Ok(fx, "/odata/Sn768People(1)?$select=FirstName");
        Assert.Contains($"\"{Keys(policy).First}\":\"Ada\"", body);
        Assert.DoesNotContain("custom_label", body);
        Assert.DoesNotContain("\"id\"", body);
    }

    [Fact]
    public async Task JsonPropertyNameMember_SelectedByItsEdmName_Survives()
    {
        await using var fx = await Build("snake");
        string body = await Ok(fx, "/odata/Sn768People?$select=custom_label");
        Assert.Contains("\"custom_label\":\"L1\"", body);
        Assert.DoesNotContain("first_name", body);
    }

    [Theory]
    [InlineData("snake")]
    [InlineData("kebab")]
    [InlineData("camel")]
    public async Task DerivedRuntimeType_SelectBaseMultiWordProperty_UnselectedDerivedMemberDropped(string policy)
    {
        await using var fx = await Build(policy);
        string body = await Ok(fx, "/odata/Sn768People?$select=FirstName");
        Assert.Contains($"\"{Keys(policy).First}\":\"Alan\"", body);
        Assert.DoesNotContain("B2", body);
    }

    [Theory]
    [InlineData("snake")]
    [InlineData("kebab")]
    [InlineData("camel")]
    public async Task NavigationCollection_SelectMultiWordProperty_KeepsPolicyKey(string policy)
    {
        await using var fx = await Build(policy);
        string body = await Ok(fx, "/odata/Sn768People(1)/MobilePhones?$select=PhoneNumber");
        Assert.Contains($"\"{Keys(policy).Number}\":\"555\"", body);
        Assert.DoesNotContain("arrier", body);
    }

    [Theory]
    [InlineData("snake")]
    [InlineData("kebab")]
    [InlineData("camel")]
    public async Task SelectPlusExpand_KeepsPropertyAndMultiWordNavigation(string policy)
    {
        await using var fx = await Build(policy);
        var k = Keys(policy);
        string body = await Ok(fx, "/odata/Sn768People?$select=FirstName&$expand=MobilePhones");
        Assert.Contains($"\"{k.First}\":\"Ada\"", body);
        Assert.Contains($"\"{k.Phones}\":[", body);
        Assert.Contains($"\"{k.Number}\":\"555\"", body);

        string byKey = await Ok(fx, "/odata/Sn768People(1)?$select=FirstName&$expand=MobilePhones");
        Assert.Contains($"\"{k.First}\":\"Ada\"", byKey);
        Assert.Contains($"\"{k.Phones}\":[", byKey);
    }

    [Theory]
    [InlineData("snake")]
    [InlineData("kebab")]
    [InlineData("camel")]
    public async Task NestedSelectInsideExpand_DelegatePath_KeepsPolicyKey(string policy)
    {
        await using var fx = await Build(policy);
        string body = await Ok(fx, "/odata/Sn768People?$expand=MobilePhones($select=PhoneNumber)");
        Assert.Contains($"\"{Keys(policy).Number}\":\"555\"", body);
        Assert.DoesNotContain("arrier", body);
    }

    private static async Task<TestFixture> BuildPush(SqliteConnection connection, string policy)
    {
        var fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.WithJsonPropertyNamingPolicy(Policy(policy));
                b.AddEntitySetProfile<Sn768PushPersonProfile>();
            },
            configureServices: s => s.AddDbContext<Sn768DbContext>(o => o.UseSqlite(connection)));
        using IServiceScope scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Sn768DbContext>();
        db.Database.EnsureCreated();
        db.People.AddRange(
            new Sn768PushPerson { Id = 1, FirstName = "Ada" },
            new Sn768PushPerson { Id = 2, FirstName = "Kid", ParentPersonId = 1 });
        db.Phones.Add(new Sn768Phone2 { Id = 10, PushPersonId = 1, PhoneNumber = "555", CarrierName = "C1" });
        db.SaveChanges();
        return fx;
    }

    [Theory]
    [InlineData("snake")]
    [InlineData("kebab")]
    [InlineData("camel")]
    public async Task PushdownNestedSelect_KeepsPolicyKey(string policy)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var fx = await BuildPush(connection, policy);
        string body = await Ok(fx, "/odata/Sn768PushPeople?$select=FirstName&$expand=MobilePhones($select=PhoneNumber)");
        Assert.Contains($"\"{Keys(policy).First}\":\"Ada\"", body);
        Assert.Contains($"\"{Keys(policy).Number}\":\"555\"", body);
        Assert.DoesNotContain("arrier", body);
    }

    [Theory]
    [InlineData("snake", "child_people")]
    [InlineData("kebab", "child-people")]
    [InlineData("camel", "childPeople")]
    public async Task PushdownLevels_NestedSelect_KeepsPolicyKey(string policy, string childKey)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var fx = await BuildPush(connection, policy);
        string body = await Ok(fx, "/odata/Sn768PushPeople?$expand=ChildPeople($levels=2;$select=FirstName)");
        string child = $"\"{childKey}\":[{{\"{Keys(policy).First}\":\"Kid\",\"{childKey}\":[]}}]";
        Assert.True(body.Contains(child), body);
    }

    [Fact]
    public async Task PushdownSingleValuedNav_NestedSelect_KeepsPolicyKey()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.WithJsonPropertyNamingPolicy(JsonNamingPolicy.SnakeCaseLower);
                b.AddEntitySetProfile<Sn768CarProfile>();
            },
            configureServices: s => s.AddDbContext<Sn768CarDb>(o => o.UseSqlite(conn)));
        using (IServiceScope scope = fx.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Sn768CarDb>();
            db.Database.EnsureCreated();
            db.Owners.Add(new Sn768Owner { Id = 5, FullName = "Ada L", HomeTown = "London" });
            db.Cars.Add(new Sn768Car { Id = 1, ModelName = "T", OwnerPersonId = 5 });
            db.SaveChanges();
        }

        string body = await Ok(fx, "/odata/Sn768Cars?$select=ModelName&$expand=OwnerPerson($select=FullName)");
        Assert.Contains("\"model_name\":\"T\"", body);
        Assert.Contains("\"owner_person\":{\"full_name\":\"Ada L\"}", body);
        Assert.DoesNotContain("London", body);
    }

    // A host TypeInfoResolver modifier that renames properties: the strip must follow the real
    // contract, not re-derive names from the policy.
    private static Task<TestFixture> BuildWithRenamingModifier(Func<string, string> rename) => TestHostBuilder.BuildAsync(
        o => o.AddEntitySetProfile<Sn768PersonProfile>(),
        configureServices: s => s.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(j =>
            j.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    ti =>
                    {
                        foreach (JsonPropertyInfo p in ti.Properties) p.Name = rename(p.Name);
                    },
                },
            }));

    [Theory]
    [InlineData("lower")]
    [InlineData("snake")]
    public async Task HostModifierRename_SelectKeepsRenamedKeys_OnEveryRoute(string mode)
    {
        Func<string, string> rename = mode == "lower"
            ? n => n.ToLowerInvariant()
            : n => JsonNamingPolicy.SnakeCaseLower.ConvertName(n);
        string first = rename("FirstName");
        string number = rename("PhoneNumber");
        await using var fx = await BuildWithRenamingModifier(rename);

        Assert.Contains($"\"{first}\":\"Ada\"", await Ok(fx, "/odata/Sn768People?$select=FirstName"));
        Assert.Contains($"\"{first}\":\"Ada\"", await Ok(fx, "/odata/Sn768People(1)?$select=FirstName"));
        Assert.Contains($"\"{number}\":\"555\"", await Ok(fx, "/odata/Sn768People(1)/MobilePhones?$select=PhoneNumber"));
        Assert.Contains($"\"{number}\":\"555\"", await Ok(fx, "/odata/Sn768People?$expand=MobilePhones($select=PhoneNumber)"));

        // The expanded navigation must survive a selected + expanded request.
        string both = await Ok(fx, "/odata/Sn768People?$select=FirstName&$expand=MobilePhones");
        Assert.Contains($"\"{first}\":\"Ada\"", both);
        Assert.Contains($"\"{number}\":\"555\"", both);
    }
}
