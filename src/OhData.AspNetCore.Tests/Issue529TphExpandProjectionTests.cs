using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
[JsonDerivedType(typeof(P529Derived), "d")]
public class P529Base
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<P529Child> Children { get; set; } = new();
    // #616: SELF-referential, which is the only shape $levels engages for (BuildLevelsNavBinding
    // requires elementType == ownerType). Without it a $levels request is refused earlier, by the
    // cast check, and FindLevelsExpand is never reached.
    public int? ParentId { get; set; }
    public List<P529Base> Subthings { get; set; } = new();
}

public sealed class P529Derived : P529Base
{
    public string Extra { get; set; } = "";
    public int Rank { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(Q529Derived), "d")]
public class Q529Base
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Computed => "c" + Id;   // get-only -> member-init projection ineligible
    public List<Q529Child> Children { get; set; } = new();
}

public sealed class Q529Derived : Q529Base
{
    public string Extra { get; set; } = "";
}

public sealed class Q529Child
{
    public int Id { get; set; }
    public int BaseId { get; set; }
    public string Body { get; set; } = "";
}

public sealed class P529Child
{
    public int Id { get; set; }
    public int BaseId { get; set; }
    public string Body { get; set; } = "";
    public List<P529Grandchild> Grandkids { get; set; } = new();
    // #616: a SECOND navigation on the child, so a sibling nested $expand exists. EF's ThenInclude
    // continues from the last-included navigation, so siblings cannot share one chain -- each is its
    // own Include(...).ThenInclude(...) with the prefix re-issued. Nothing else here covers that.
    public List<P529Note> Notes { get; set; } = new();
}

public sealed class P529Note
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string Text { get; set; } = "";
}

public sealed class P529Grandchild
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string Tag { get; set; } = "";
}

public sealed class P529DbContext : DbContext
{
    public P529DbContext(DbContextOptions<P529DbContext> options) : base(options) { }
    public DbSet<P529Base> Things => Set<P529Base>();
    public DbSet<P529Child> Children => Set<P529Child>();
    public DbSet<P529Grandchild> Grandkids => Set<P529Grandchild>();
    public DbSet<P529Note> Notes => Set<P529Note>();
    public DbSet<Q529Base> QThings => Set<Q529Base>();
    public DbSet<Q529Child> QChildren => Set<Q529Child>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<P529Base>().HasMany(t => t.Children).WithOne().HasForeignKey(c => c.BaseId);
        b.Entity<P529Base>().HasMany(t => t.Subthings).WithOne().HasForeignKey(t => t.ParentId);
        b.Entity<P529Child>().HasMany(c => c.Grandkids).WithOne().HasForeignKey(g => g.ChildId);
        b.Entity<P529Child>().HasMany(c => c.Notes).WithOne().HasForeignKey(n => n.ChildId);
        b.Entity<P529Derived>(); // TPH: the derived type must be in the model to be mapped
        b.Entity<Q529Base>().Ignore(x => x.Computed);
        b.Entity<Q529Base>().HasMany(t => t.Children).WithOne().HasForeignKey(c => c.BaseId);
        b.Entity<Q529Derived>();
    }
}

public sealed class P529BaseProfile : EntitySetProfile<int, P529Base>
{
    public P529BaseProfile(P529DbContext db) : base(x => x.Id)
    {
        EntitySetName = "P529Things";
        ExpandEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask<IQueryable<P529Base>>(db.Things.AsQueryable());
        GetById = (id, _) => OhDataResult.SuccessTask(db.Things.FirstOrDefault(t => t.Id == id));
        HasMany(x => x.Children); // delegate-less -> pushable
        HasMany(x => x.Subthings);
    }
}

public sealed class Q529BaseProfile : EntitySetProfile<int, Q529Base>
{
    public Q529BaseProfile(P529DbContext db) : base(x => x.Id)
    {
        EntitySetName = "Q529Things";
        ExpandEnabled = true; OrderByEnabled = true; SelectEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask<IQueryable<Q529Base>>(db.QThings.AsQueryable());
        HasMany(x => x.Children);
    }
}

/// <summary>
/// #529 - a TPH root keeps its derived properties under <c>$expand</c>.
/// <para>
/// The expand pushdown folds the engaged navigations into a member-init projection over
/// <c>TModel</c>. A member-init can construct nothing but the DECLARED type, so on a TPH hierarchy
/// every row came back as the base: <c>GET /P529Things</c> emitted
/// <c>SELECT Id, Discriminator, Name, Extra, Rank</c> while <c>?$expand=Children</c> emitted
/// <c>SELECT t0.Id, t0.Name, c.Id, c.BaseId, c.Body</c> - the discriminator not even selected. The
/// derived properties vanished, under a 200, decided by whether the request carried <c>$expand</c>.
/// </para>
/// <para>
/// A polymorphic root is now refused the projection and falls to the Include path (#305 Path A),
/// which loads real entities and so materializes each row as its own runtime type. That path used to
/// answer <c>400</c> for a nested <c>$filter</c>/<c>$orderby</c> on the stated grounds that "a plain
/// EF Include cannot carry a predicate/ordering at all". EF Core's FILTERED Include does, in exactly
/// the Where -&gt; OrderBy/ThenBy -&gt; Skip/Take sequence <c>ApplyNavShape</c> already composes, so
/// those clauses are bound through the same <c>BindNavShape</c> the projection path uses rather than
/// refused.
/// </para>
/// </summary>
public sealed class Issue529TphExpandProjectionTests
{
    private static async Task<TestFixture> BuildAsync(SqliteConnection connection)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b => { b.AddEntitySetProfile<P529BaseProfile>(); b.AddEntitySetProfile<Q529BaseProfile>(); },
            configureServices: services => services.AddDbContext<P529DbContext>(o => o.UseSqlite(connection)));

        using IServiceScope scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<P529DbContext>();
        db.Database.EnsureCreated();
        db.Things.Add(new P529Base { Id = 1, Name = "base" });
        db.Things.Add(new P529Derived { Id = 2, Name = "derived", Extra = "EXTRA", Rank = 7 });
        db.Children.Add(new P529Child { Id = 10, BaseId = 1, Body = "c1" });
        db.Children.Add(new P529Child { Id = 20, BaseId = 2, Body = "c2" });
        db.Children.Add(new P529Child { Id = 21, BaseId = 2, Body = "c3" });
        db.Grandkids.Add(new P529Grandchild { Id = 100, ChildId = 20, Tag = "g1" });
        db.Grandkids.Add(new P529Grandchild { Id = 101, ChildId = 20, Tag = "g2" });
        db.Notes.Add(new P529Note { Id = 200, ChildId = 20, Text = "n1" });
        db.QThings.Add(new Q529Base { Id = 1, Name = "qbase" });
        db.QThings.Add(new Q529Derived { Id = 2, Name = "qderived", Extra = "QEXTRA" });
        db.QChildren.Add(new Q529Child { Id = 30, BaseId = 2, Body = "q2" });
        db.SaveChanges();
        return fx;
    }

    private static async Task<JsonElement> GetValueAsync(TestFixture fx, string url)
    {
        HttpResponseMessage response = await fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value");
    }

    private static JsonElement Row(JsonElement value, int id) =>
        value.EnumerateArray().Single(e => e.GetProperty("Id").GetInt32() == id);

    [Fact]
    public async Task ADerivedRowKeepsItsOwnProperties_UnderExpand()
    {
        // The defect: Extra and Rank were absent here, and present on the same entity without $expand.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement derived = Row(await GetValueAsync(fx, "/odata/P529Things?$expand=Children"), 2);

        Assert.Equal("EXTRA", derived.GetProperty("Extra").GetString());
        Assert.Equal(7, derived.GetProperty("Rank").GetInt32());
        Assert.Equal(2, derived.GetProperty("Children").GetArrayLength());
    }

    [Fact]
    public async Task TheBaseRowIsUnchanged_AndCarriesNoDerivedMembers()
    {
        // The other direction: routing polymorphic roots to Include must not start serving derived
        // members on rows that do not have them.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement baseRow = Row(await GetValueAsync(fx, "/odata/P529Things?$expand=Children"), 1);

        Assert.Equal("base", baseRow.GetProperty("Name").GetString());
        Assert.False(baseRow.TryGetProperty("Extra", out _));
        Assert.Equal(1, baseRow.GetProperty("Children").GetArrayLength());
    }

    [Fact]
    public async Task TheAnswerNoLongerDependsOnWhetherExpandWasAsked()
    {
        // The sharpest statement of the defect: one entity, two URLs, Extra present in exactly one.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        string withoutExpand = Row(await GetValueAsync(fx, "/odata/P529Things"), 2)
            .GetProperty("Extra").GetString()!;
        string withExpand = Row(await GetValueAsync(fx, "/odata/P529Things?$expand=Children"), 2)
            .GetProperty("Extra").GetString()!;

        Assert.Equal(withoutExpand, withExpand);
    }

    [Theory]
    [InlineData("$expand=Children($filter=Id gt 20)", 1)]
    [InlineData("$expand=Children($top=1)", 1)]
    [InlineData("$expand=Children($orderby=Id desc;$top=1)", 1)]
    [InlineData("$expand=Children", 2)]
    public async Task NestedOptionsAreServedOnAPolymorphicRoot_NotRefused(string query, int expected)
    {
        // The nested $filter/$orderby cases answered 400 on ANY projection-ineligible model. A
        // polymorphic root now reaches that path on every request, so the refusal had to go with it -
        // filtered Include carries both.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement derived = Row(await GetValueAsync(fx, "/odata/P529Things?" + query), 2);

        Assert.Equal(expected, derived.GetProperty("Children").GetArrayLength());
        Assert.Equal("EXTRA", derived.GetProperty("Extra").GetString());
    }

    [Fact]
    public async Task ANestedOrderByActuallyOrders()
    {
        // Accepted is not applied - the failure mode #475 is about, one option over.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement kids = Row(
            await GetValueAsync(fx, "/odata/P529Things?$expand=Children($orderby=Id desc)"), 2)
            .GetProperty("Children");

        Assert.Equal(21, kids[0].GetProperty("Id").GetInt32());
        Assert.Equal(20, kids[1].GetProperty("Id").GetInt32());
    }

    [Fact]
    public async Task ANestedExpandUnderAPolymorphicRoot_IsServed()
    {
        // #616 INVERTED this. It answered 400 -- "a nested $expand ... under a plain Include fallback
        // is not supported" -- which #529 had accepted as the loud-beats-wrong trade. Chaining
        // ThenInclude removes the trade: the nested level is served AND the derived properties
        // survive, so the assertion is on the data rather than on the status.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement value = await GetValueAsync(fx, "/odata/P529Things?$expand=Children($expand=Grandkids)");
        JsonElement derived = Row(value, 2);

        Assert.Equal("EXTRA", derived.GetProperty("Extra").GetString());
        JsonElement kids = derived.GetProperty("Children");
        JsonElement child20 = kids.EnumerateArray().Single(c => c.GetProperty("Id").GetInt32() == 20);
        JsonElement child21 = kids.EnumerateArray().Single(c => c.GetProperty("Id").GetInt32() == 21);

        Assert.Equal(2, child20.GetProperty("Grandkids").GetArrayLength());
        Assert.Equal(0, child21.GetProperty("Grandkids").GetArrayLength());
    }

    [Fact]
    public async Task SiblingNestedExpands_AreBothServed()
    {
        // The re-issued-prefix path. EF's ThenInclude continues from the navigation most recently
        // included, so Grandkids and Notes cannot share one chain -- each is its own
        // Include(Children).ThenInclude(...), with the SAME filtered Include(Children) issued twice.
        // Measured on EF Core 10: repeating a filtered Include identically is accepted and the earlier
        // chain survives, while repeating it with a DIFFERENT predicate throws. A navigation appears
        // once in the engaged tree, so only the safe case is reachable.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement child20 = Row(
            await GetValueAsync(fx, "/odata/P529Things?$expand=Children($expand=Grandkids,Notes)"), 2)
            .GetProperty("Children").EnumerateArray()
            .Single(c => c.GetProperty("Id").GetInt32() == 20);

        Assert.Equal(2, child20.GetProperty("Grandkids").GetArrayLength());
        Assert.Equal(1, child20.GetProperty("Notes").GetArrayLength());
    }

    [Fact]
    public async Task ANestedFilterAtBothLevels_IsApplied()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement kids = Row(await GetValueAsync(fx,
            "/odata/P529Things?$expand=Children($filter=Id gt 19;$expand=Grandkids($filter=Id gt 100))"), 2)
            .GetProperty("Children");

        Assert.Equal(2, kids.GetArrayLength());   // 20 and 21, not the base row's 10
        JsonElement child20 = kids.EnumerateArray().Single(c => c.GetProperty("Id").GetInt32() == 20);
        Assert.Equal(1, child20.GetProperty("Grandkids").GetArrayLength());   // 101 only, not 100
        Assert.Equal(101, child20.GetProperty("Grandkids")[0].GetProperty("Id").GetInt32());
    }

    [Fact]
    public async Task LevelsUnderAPolymorphicRoot_StillFailsLoud()
    {
        // The one shape ThenInclude cannot serve: $levels depth is decided per request by the data,
        // so there is no statically-known nesting to build a chain from. Still 400, and the message
        // now names $levels rather than "a nested $expand or $levels".
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/P529Things?$expand=Subthings($levels=2)");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("$levels under an Include fallback", body, StringComparison.Ordinal);
        Assert.DoesNotContain("a nested $expand or $levels", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlreadyIneligibleModelStillGetsItsNestedFilter()
    {
        // Q529Base was ineligible before #529 for an unrelated reason (a get-only member), so it
        // exercises the lifted 400 independently of the polymorphism check that now also catches it.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using TestFixture fx = await BuildAsync(connection);

        JsonElement derived = Row(
            await GetValueAsync(fx, "/odata/Q529Things?$expand=Children($filter=Id gt 1)"), 2);

        Assert.Equal(1, derived.GetProperty("Children").GetArrayLength());
        Assert.Equal("QEXTRA", derived.GetProperty("Extra").GetString());
    }
}
