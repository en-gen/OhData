using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── #253 completion: [JsonPropertyName] uniformly drives the OData name for NAVIGATIONS too ────────
//
// Reverses #184. A navigation carrying [JsonPropertyName("wireName")] is addressed as "wireName" on
// EVERY OData surface: $metadata, the $expand/$filter/$orderby identifier, the nav-path URL segments
// (/{Set}({key})/{wireName}, .../$ref, .../$count, POST), AND the response body key — exactly like a
// renamed structural property. The old CLR name is no longer valid on any of these surfaces. An
// un-renamed navigation is unaffected (CLR name everywhere).

// ── EF Sqlite entities ────────────────────────────────────────────────────────────────────────────

public sealed class UniAuthor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class UniComment
{
    public int Id { get; set; }
    public int BlogId { get; set; }
    public string Text { get; set; } = "";
}

public sealed class UniBlog
{
    public int Id { get; set; }
    public string Title { get; set; } = "";

    public int? AuthorId { get; set; }

    // Substantively-renamed single-valued navigation (delegate-less → $expand Include pushdown).
    // Non-nullable annotation satisfies the HasOptional `class` constraint; the nullable FK
    // (AuthorId) makes the relationship optional at the data layer.
    [JsonPropertyName("writtenBy")]
    public UniAuthor Author { get; set; } = null!;

    // Substantively-renamed collection navigation (delegate-backed → nav-path routes / $ref / $count / POST).
    [JsonPropertyName("remarks")]
    public List<UniComment> Comments { get; set; } = new();
}

public sealed class UniNavDbContext : DbContext
{
    public UniNavDbContext(DbContextOptions<UniNavDbContext> options) : base(options) { }

    public DbSet<UniBlog> Blogs => Set<UniBlog>();
    public DbSet<UniAuthor> Authors => Set<UniAuthor>();
    public DbSet<UniComment> Comments => Set<UniComment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UniBlog>().HasOne(b => b.Author).WithMany().HasForeignKey(b => b.AuthorId);
        modelBuilder.Entity<UniBlog>().HasMany(b => b.Comments).WithOne().HasForeignKey(c => c.BlogId);
    }
}

public sealed class UniBlogProfile : EntitySetProfile<int, UniBlog>
{
    public UniBlogProfile(UniNavDbContext db) : base(x => x.Id)
    {
        EntitySetName = "UniBlogs";
        SelectEnabled = true;
        ExpandEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;

        GetQueryable = _ => Task.FromResult(db.Blogs.AsQueryable());
        GetById = (id, ct) => Task.FromResult(db.Blogs.FirstOrDefault(b => b.Id == id));

        // Delegate-less single nav → $expand=writtenBy folds into a JOIN (pushdown); $filter/$orderby
        // over writtenBy/Name resolve against the EDM navigation (JSON name).
        HasOptional(x => x.Author);

        // Delegate-backed collection nav → nav-path routes registered under the JSON segment "remarks".
        HasMany(
            navigation: x => x.Comments,
            getAll: (blogId, ct) => Task.FromResult<IEnumerable<UniComment>>(
                db.Comments.Where(c => c.BlogId == blogId).ToList()),
            post: (blogId, child, ct) =>
            {
                child.BlogId = blogId;
                child.Id = 0;
                db.Comments.Add(child);
                db.SaveChanges();
                return Task.FromResult<UniComment?>(child);
            },
            refTargetEntitySet: "UniComments",
            addRef: (blogId, relatedId, ct) => Task.CompletedTask,
            removeRef: (blogId, relatedId, ct) => Task.CompletedTask);
    }
}

internal static class UniNavHarness
{
    public static async Task<TestFixture> BuildAsync(SqliteConnection connection)
    {
        var fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<UniBlogProfile>(),
            configureServices: services =>
                services.AddDbContext<UniNavDbContext>(o => o.UseSqlite(connection)));

        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UniNavDbContext>();
        db.Database.EnsureCreated();

        db.Authors.AddRange(
            new UniAuthor { Id = 100, Name = "Ada" },
            new UniAuthor { Id = 101, Name = "Ben" });
        db.Blogs.AddRange(
            new UniBlog { Id = 1, Title = "Alpha", AuthorId = 100 },
            new UniBlog { Id = 2, Title = "Bravo", AuthorId = 101 });
        db.Comments.AddRange(
            new UniComment { Id = 10, BlogId = 1, Text = "nice" },
            new UniComment { Id = 11, BlogId = 1, Text = "great" });
        db.SaveChanges();
        return fx;
    }
}

public sealed class RenamedNavigationUniformNamingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _fx = await UniNavHarness.BuildAsync(_connection);
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    // ── $metadata declares the navigation under its JSON name ─────────────────────

    [Fact]
    public async Task Metadata_DeclaresNavigation_UnderJsonName()
    {
        string metadata = await _fx.Client.GetStringAsync("/odata/$metadata");

        Assert.Contains("<NavigationProperty Name=\"writtenBy\"", metadata);
        Assert.Contains("<NavigationProperty Name=\"remarks\"", metadata);
        Assert.DoesNotContain("Name=\"Author\"", metadata);
        Assert.DoesNotContain("Name=\"Comments\"", metadata);
    }

    // ── $expand resolves the JSON name (and pushdown loads the related rows) ───────

    [Fact]
    public async Task Expand_JsonName_PushdownLoadsRelatedRows()
    {
        // Collection route → the delegate-less single nav folds into a JOIN (Include pushdown). The
        // JSON $expand identifier "writtenBy" maps back to the CLR "Author" member at the pushdown
        // boundary, so the related rows load.
        var json = await _fx.Client.GetFromJsonAsync<JsonElement>("/odata/UniBlogs?$expand=writtenBy");

        JsonElement blog1 = json.GetProperty("value").EnumerateArray().First(b => b.GetProperty("Id").GetInt32() == 1);
        Assert.True(blog1.TryGetProperty("writtenBy", out var author), "expanded single nav present under JSON key");
        Assert.Equal("Ada", author.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Expand_CollectionJsonName_LoadsRelatedRows()
    {
        var json = await _fx.Client.GetFromJsonAsync<JsonElement>("/odata/UniBlogs(1)?$expand=remarks");

        Assert.True(json.TryGetProperty("remarks", out var comments), "expanded collection nav present under JSON key");
        Assert.Equal(2, comments.GetArrayLength());
    }

    [Fact]
    public async Task Expand_OldClrName_Returns400()
    {
        HttpResponseMessage response = await _fx.Client.GetAsync("/odata/UniBlogs(1)?$expand=Author");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── $filter / $orderby over the renamed nav resolve the JSON identifier ────────

    [Fact]
    public async Task Filter_NavHopJsonName_Works()
    {
        var json = await _fx.Client.GetFromJsonAsync<JsonElement>(
            "/odata/UniBlogs?$filter=writtenBy/Name eq 'Ada'");

        Assert.Equal(1, json.GetProperty("value").GetArrayLength());
        Assert.Equal("Alpha", json.GetProperty("value")[0].GetProperty("Title").GetString());
    }

    [Fact]
    public async Task Filter_NavHopOldClrName_Returns400()
    {
        HttpResponseMessage response = await _fx.Client.GetAsync(
            "/odata/UniBlogs?$filter=Author/Name eq 'Ada'");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OrderBy_NavHopJsonName_Works()
    {
        HttpResponseMessage response = await _fx.Client.GetAsync(
            "/odata/UniBlogs?$orderby=writtenBy/Name desc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Nav-path URL segments use the JSON name ───────────────────────────────────

    [Fact]
    public async Task NavPath_JsonSegment_ReturnsRelatedCollection()
    {
        var json = await _fx.Client.GetFromJsonAsync<JsonElement>("/odata/UniBlogs(1)/remarks");
        Assert.Equal(2, json.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task NavPath_OldClrSegment_Returns404()
    {
        // The CLR-named route was never registered — the JSON segment is the only one.
        HttpResponseMessage response = await _fx.Client.GetAsync("/odata/UniBlogs(1)/Comments");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NavPath_JsonSegment_Count_Works()
    {
        string body = await _fx.Client.GetStringAsync("/odata/UniBlogs(1)/remarks/$count");
        Assert.Equal("2", body.Trim());
    }

    [Fact]
    public async Task NavPath_JsonSegment_Ref_Works()
    {
        HttpResponseMessage response = await _fx.Client.GetAsync("/odata/UniBlogs(1)/remarks/$ref");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NavPath_JsonSegment_Post_CreatesRelatedEntity()
    {
        var content = new StringContent("{\"Text\":\"added\"}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await _fx.Client.PostAsync("/odata/UniBlogs(1)/remarks", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string count = await _fx.Client.GetStringAsync("/odata/UniBlogs(1)/remarks/$count");
        Assert.Equal("3", count.Trim());
    }
}

// ── Collision: a navigation renamed onto a structural property's name fails fast at startup ─────────

internal sealed class NavCollisionModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // Renames the navigation onto the structural property "Name" → two properties resolve to the
    // same OData name, which cannot be represented in the EDM or URL space.
    [JsonPropertyName("Name")]
    public NavCollisionOther Related { get; set; } = null!;
}

internal sealed class NavCollisionOther
{
    public int Id { get; set; }
}

internal sealed class NavCollisionProfile : EntitySetProfile<int, NavCollisionModel>
{
    public NavCollisionProfile() : base(x => x.Id)
    {
        EntitySetName = "NavCollisions";
        GetById = (id, ct) => Task.FromResult<NavCollisionModel?>(null);
        HasOptional(x => x.Related);
    }
}

public class RenamedNavigationCollisionTests
{
    [Fact]
    public async Task NavigationRenamedOntoStructuralName_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavCollisionProfile>()));
        Assert.Contains("OData name", ex.Message);
        Assert.Contains("Name", ex.Message);
    }
}
