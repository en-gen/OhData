using System;
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

// #296 generalization: the bug is not limited to SELF-referential navigations. It applies to any
// "shared type" -- a navigation's target CLR type that is ALSO exposed as its own root entity set,
// self-referential or not. SnChild here is a plain, non-self-referential nav target of SnParent that
// ALSO has its own root profile (SnChildProfile / "/odata/SnChildren"). Before the fix, its model-bound
// MaxTop defaulted to 0 (a side effect of SnChildProfile's own root config), 400ing any
// $expand=Children($top=N) against SnParent even though the navigation itself is a completely ordinary,
// delegate-less, non-cyclic, pushdown-eligible one-to-many.
//
// Unlike the self-referential case (SelfReferentialNavMaxTopTests.cs), this shape genuinely reaches EF
// pushdown WITHOUT needing $levels (SnChild != SnParent, so BuildExpandNavBinding never considers it
// cyclic) -- so this suite verifies the actual windowed data, not just the status code.
//
// This navigation is delegate-LESS (HasMany(x => x.Children) with no getAll/get), so it is unaffected
// by #294's reject (that reject only fires for a delegate-BACKED nav — see SelfReferentialNavMaxTopTests.cs
// for the delegate-backed counterpart, which now 400s instead of windowing).
public sealed class SnParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<SnChild> Children { get; set; } = new();
}

public sealed class SnChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class SnDbContext : DbContext
{
    public SnDbContext(DbContextOptions<SnDbContext> options) : base(options) { }

    public DbSet<SnParent> SnParents => Set<SnParent>();
    public DbSet<SnChild> SnChildren => Set<SnChild>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SnParent>()
            .HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
    }
}

public sealed class SnParentProfile : EntitySetProfile<int, SnParent>
{
    public SnParentProfile(SnDbContext db) : base(x => x.Id)
    {
        EntitySetName = "SnParents";
        ExpandEnabled = true;
        FilterEnabled = true;
        MaxExpandTop = 2; // nested-expand ceiling: governs $expand=Children($top=N)
        GetQueryable = _ => db.SnParents.AsQueryable();
        HasMany(x => x.Children); // delegate-less, non-cyclic → pushdown-eligible without $levels
    }
}

// SnChild is ALSO its own root entity set -- the "shared type" half of #296. Its own root-level MaxTop
// (10) is a completely different setting from SnParentProfile.MaxExpandTop (2) above.
public sealed class SnChildProfile : EntitySetProfile<int, SnChild>
{
    public SnChildProfile(SnDbContext db) : base(x => x.Id)
    {
        EntitySetName = "SnChildren";
        FilterEnabled = true;
        MaxTop = 10;
        GetQueryable = _ => db.SnChildren.AsQueryable();
    }
}

public sealed class SharedNavTargetTypePushdownTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<SnParentProfile>();
                b.AddEntitySetProfile<SnChildProfile>();
            },
            configureServices: services =>
            {
                services.AddDbContext<SnDbContext>(o => o.UseSqlite(_connection));
            });

        using IServiceScope scope = _fx.App.Services.CreateScope();
        SnDbContext db = scope.ServiceProvider.GetRequiredService<SnDbContext>();
        db.Database.EnsureCreated();

        db.SnParents.Add(new SnParent { Id = 1, Name = "P1" });
        db.SnChildren.AddRange(
            new SnChild { Id = 10, ParentId = 1, Name = "C1" },
            new SnChild { Id = 11, ParentId = 1, Name = "C2" },
            new SnChild { Id = 12, ParentId = 1, Name = "C3" });
        db.SaveChanges();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NestedTop_WithinMaxExpandTop_OnSharedNavTargetType_ActuallyWindows()
    {
        // #296: this used to 400 with "limit of '0' for Top query has been exceeded" -- SnChild's
        // model-bound MaxTop defaulted to 0 purely because SnChildProfile also gives it its own root
        // entity set. The navigation itself is ordinary/delegate-less/non-cyclic, so once the
        // model-bound pre-rejection is lifted it reaches real EF pushdown (ApplyNavShape) and the
        // nested $top is genuinely honored -- not just accepted.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/SnParents?$filter=id eq 1&$expand=Children($top=2)");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement children = doc.RootElement.GetProperty("value")[0].GetProperty("Children");
        Assert.Equal(2, children.GetArrayLength());
        Assert.Equal(new[] { "C1", "C2" },
            children.EnumerateArray().Select(e => e.GetProperty("Name").GetString()).ToArray());
    }

    [Fact]
    public async Task NestedTop_AboveMaxExpandTop_OnSharedNavTargetType_Returns400()
    {
        // OhData's own MaxExpandTop ceiling (ValidateNestedTopCeiling) must still bite once the
        // model-bound validator is no longer pre-empting it.
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/SnParents?$filter=id eq 1&$expand=Children($top=3)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("Children", body);
        Assert.Contains("(3)", body);
        Assert.Contains("(2)", body);
    }

    [Theory]
    [InlineData(11, HttpStatusCode.BadRequest)] // SnChild's OWN root MaxTop (10) is unrelated to
                                                // SnParentProfile.MaxExpandTop (2) above -- proves
                                                // the fix did not weaken the shared type's own
                                                // request-level $top as a root set.
    [InlineData(3, HttpStatusCode.OK)]
    public async Task RootTop_OnSharedType_RespectsOwnMaxTop(int top, HttpStatusCode expectedStatus)
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync($"/odata/SnChildren?$top={top}");
        Assert.Equal(expectedStatus, resp.StatusCode);
    }
}
