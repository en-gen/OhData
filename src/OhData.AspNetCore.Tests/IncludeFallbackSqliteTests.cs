using System;
using System.Linq;
using System.Collections.Generic;
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

// #305 Path A ("serve, not silently drop"): when the ROOT model of a $expand pushdown request has no
// public parameterless constructor (or another TryApplySelectProjection ineligibility reason),
// engagedExpandNavs used to be dropped to EDM-only under a lying 200 — the navigation serialized
// whatever the CLR property's default value already was (typically an empty collection), even though
// the framework had already vetted it as a delegate-less, pushable navigation. This suite proves the
// fix: the SAME engaged navigations are now served via EF Core's own Include (reflection-resolved —
// this package has no compile-time dependency on Microsoft.EntityFrameworkCore), bounded by
// MaxExpandTop exactly like the member-init projection path, or the request fails loud (400) when it
// needs something a plain Include cannot carry.
//
// NoCtorParent is a positional record — its only constructor takes (int Id, string Name), so
// typeof(NoCtorParent).GetConstructor(Type.EmptyTypes) is null and TryApplySelectProjection returns the
// query unchanged for EVERY request against this entity set, forcing Path A on every $expand.

public sealed record NoCtorParent(int Id, string Name)
{
    public List<NoCtorChild> Children { get; set; } = new();
}

public sealed class NoCtorChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

// Include-invalid fixture: FakeNav is CLR-shaped exactly like an eligible delegate-less collection nav
// (a settable, non-cyclic List<T>), so BuildExpandNavBinding (pure CLR reflection, no EF-model
// awareness) treats it as pushable — but OnModelCreating below explicitly Ignore()s it, so EF's OWN
// model does not recognize it as a navigation at all. Calling EF's Include on it must fail loud (400),
// not silently drop the data.
public sealed record IncludeInvalidParent(int Id, string Name)
{
    public List<IncludeInvalidChild> FakeNav { get; set; } = new();
}

public sealed class IncludeInvalidChild
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class IncludeFallbackDbContext : DbContext
{
    public IncludeFallbackDbContext(DbContextOptions<IncludeFallbackDbContext> options) : base(options) { }

    public DbSet<NoCtorParent> NoCtorParents => Set<NoCtorParent>();
    public DbSet<NoCtorChild> NoCtorChildren => Set<NoCtorChild>();
    public DbSet<IncludeInvalidParent> IncludeInvalidParents => Set<IncludeInvalidParent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NoCtorParent>()
            .HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);

        // FakeNav is deliberately NOT a real EF relationship — see the fixture remarks above.
        modelBuilder.Entity<IncludeInvalidParent>().Ignore(p => p.FakeNav);
    }
}

public sealed class NoCtorParentProfile : EntitySetProfile<int, NoCtorParent>
{
    public NoCtorParentProfile(IncludeFallbackDbContext db) : base(x => x.Id)
    {
        EntitySetName = "NoCtorParents";
        ExpandEnabled = true;
        OrderByEnabled = true;
        FilterEnabled = true;
        SelectEnabled = true;
        GetQueryable = _ => Task.FromResult(db.NoCtorParents.AsQueryable());
        HasMany(x => x.Children); // delegate-less → eligible for $expand pushdown
    }
}

public sealed class IncludeInvalidParentProfile : EntitySetProfile<int, IncludeInvalidParent>
{
    public IncludeInvalidParentProfile(IncludeFallbackDbContext db) : base(x => x.Id)
    {
        EntitySetName = "IncludeInvalidParents";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.IncludeInvalidParents.AsQueryable());
        HasMany(x => x.FakeNav); // CLR-eligible, but EF-ignored — Include must fail loud
    }
}

internal static class IncludeFallbackSqliteHarness
{
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection, Action<EntitySetDefaults>? defaults = null)
    {
        var fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                if (defaults is not null) b.WithDefaults(defaults);
                b.AddEntitySetProfile<NoCtorParentProfile>();
                b.AddEntitySetProfile<IncludeInvalidParentProfile>();
            },
            configureServices: services =>
            {
                services.AddDbContext<IncludeFallbackDbContext>(o => o.UseSqlite(connection));
            });

        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IncludeFallbackDbContext>();
        db.Database.EnsureCreated();

        db.NoCtorParents.AddRange(
            new NoCtorParent(1, "P1"),
            new NoCtorParent(2, "P2"));
        db.NoCtorChildren.AddRange(
            new NoCtorChild { Id = 10, ParentId = 1, Name = "C1a" },
            new NoCtorChild { Id = 11, ParentId = 1, Name = "C1b" },
            new NoCtorChild { Id = 12, ParentId = 1, Name = "C1c" },
            new NoCtorChild { Id = 20, ParentId = 2, Name = "C2a" });

        db.IncludeInvalidParents.Add(new IncludeInvalidParent(1, "BadP1"));

        db.SaveChanges();
        return fx;
    }
}

public sealed class IncludeFallbackServeTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await IncludeFallbackSqliteHarness.BuildAsync(_connection);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NoParameterlessCtor_BareExpand_ServesRealChildren_Not200WithEmptyNav()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/NoCtorParents?$orderby=id&$expand=Children");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement value = doc.RootElement.GetProperty("value");
        JsonElement p1 = value.EnumerateArray().Single(p => p.GetProperty("Name").GetString() == "P1");
        JsonElement p2 = value.EnumerateArray().Single(p => p.GetProperty("Name").GetString() == "P2");

        // Before #305: Children came back as [] (the CLR default) for BOTH parents under a 200 — the
        // silent-drop bug. Now: real rows.
        JsonElement p1Children = p1.GetProperty("Children");
        Assert.Equal(3, p1Children.GetArrayLength());
        Assert.Contains(p1Children.EnumerateArray(), c => c.GetProperty("Name").GetString() == "C1a");
        Assert.Contains(p1Children.EnumerateArray(), c => c.GetProperty("Name").GetString() == "C1b");
        Assert.Contains(p1Children.EnumerateArray(), c => c.GetProperty("Name").GetString() == "C1c");

        JsonElement p2Children = p2.GetProperty("Children");
        Assert.Single(p2Children.EnumerateArray());
        Assert.Equal("C2a", p2Children[0].GetProperty("Name").GetString());
    }

    [Fact]
    public async Task NoParameterlessCtor_NestedCountSelectTop_ServedAndShapedCorrectly()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/NoCtorParents?$orderby=id&$expand=Children($count=true;$select=name;$top=2)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement p1 = doc.RootElement.GetProperty("value")[0];

        // Full filtered count (3), independent of the $top window.
        Assert.Equal(3, p1.GetProperty("Children@odata.count").GetInt32());
        JsonElement children = p1.GetProperty("Children");
        Assert.Equal(2, children.GetArrayLength()); // windowed by $top=2

        // $select=name narrowed each child to just Name (plus whatever correlation the shaper keeps).
        foreach (JsonElement child in children.EnumerateArray())
        {
            Assert.True(child.TryGetProperty("Name", out _));
        }
    }

    [Theory]
    [InlineData("$expand=Children($filter=contains(name,'C1'))", "$filter")]
    [InlineData("$expand=Children($orderby=name desc)", "$orderby")]
    public async Task NoParameterlessCtor_NestedFilterOrOrderBy_FailsLoud400(string query, string optionToken)
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync($"/odata/NoCtorParents?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains(optionToken, body);
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("SQLITE", body);
    }
}

// MaxExpandTop must still bound materialization through the Include fallback — mirrors
// NestedCountCeilingBreachTests (MaxExpandTopTests.cs) but for the no-parameterless-ctor model.
public sealed class IncludeFallbackMaxExpandTopTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        // P1 has exactly 3 children; a ceiling of 2 puts the true count above the budget.
        _fx = await IncludeFallbackSqliteHarness.BuildAsync(_connection, defaults: d => d.MaxExpandTop = 2);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NestedCount_ChildCountAboveCeiling_Returns400_NotATruncatedCount()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/NoCtorParents?$orderby=id&$expand=Children($count=true)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("cannot be computed", body);
        Assert.Contains("maximum of 2", body);
        Assert.DoesNotContain("@odata.count", body);
    }

    [Fact]
    public async Task NestedCount_ChildCountAtOrUnderCeiling_Succeeds_WithExactCount()
    {
        // P2 has exactly 1 child — comfortably under the ceiling of 2.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/NoCtorParents?$orderby=id&$expand=Children($count=true)&$filter=name eq 'P2'");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Children@odata.count\":1", body);
    }
}

// Include-invalid model: EF's own model does not recognize the "nav" (explicitly Ignore()d), so
// constructing/executing the Include must fail loud (400), never silently drop the navigation.
public sealed class IncludeFallbackInvalidModelTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await IncludeFallbackSqliteHarness.BuildAsync(_connection);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task EfIgnoredNavigation_Expand_FailsLoud400_NotSilentlyEmpty200()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/IncludeInvalidParents?$expand=FakeNav");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("InvalidQueryOption", body);
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("SQLITE", body);
    }
}
