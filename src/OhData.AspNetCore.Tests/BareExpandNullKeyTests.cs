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

// #313 review finding 4: WriteNestedNextLink TRIMMED the expanded array before checking whether it
// could build a link, and returned early on failure -- leaving a truncated collection carrying
// neither Nav@odata.nextLink nor a 400, the single outcome M1 forbids.
//
// Two bail-outs. The index guard is genuinely unreachable through the sole call site and is an
// assertion; nothing here pretends to cover it. The NULL-KEY guard is reachable and this file reaches
// it: TKey is unconstrained, so the OData key may be a nullable string, and
// ODataEntityKeyUrlFormatter.Format throws on null -- a row whose key is null has no addressable
// continuation.
//
// The fix makes trim-and-link one step: both guards run first, the array is untouched when either
// fires, and the caller applies the ceiling's 400. Hence 400 below, not a 200 with a clipped array.
//
// FIXTURE NOTE: the OData key is `Code`, a nullable string, while EF's PK is `Id`. That split is what
// makes a null key representable -- a SQL PK cannot be null, so a model whose OData key IS the EF PK
// can never reach this path.

public sealed class NkAuthor
{
    public int Id { get; set; }
    public string? Code { get; set; }
    public List<NkBook> Books { get; set; } = new();
}

public sealed class NkBook
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public string Title { get; set; } = "";
}

public sealed class NkDbContext : DbContext
{
    public NkDbContext(DbContextOptions<NkDbContext> options) : base(options) { }

    public DbSet<NkAuthor> Authors => Set<NkAuthor>();
    public DbSet<NkBook> Books => Set<NkBook>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<NkAuthor>().HasKey(a => a.Id);
        b.Entity<NkAuthor>().HasMany(a => a.Books).WithOne().HasForeignKey(x => x.AuthorId);
    }
}

public sealed class NkAuthorProfile : EntitySetProfile<string, NkAuthor>
{
    public NkAuthorProfile(NkDbContext db) : base(x => x.Code!)
    {
        EntitySetName = "NkAuthors";
        ExpandEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => OhDataResult.Success(db.Authors.AsQueryable());
        HasMany(x => x.Books); // delegate-less → pushable, so pageable
    }
}

public sealed class BareExpandNullKeyTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;

    public Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private async Task<TestFixture> BuildAsync()
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.WithDefaults(d => { d.MaxExpandTop = 2; d.ExpandPagingEnabled = true; });
                b.AddEntitySetProfile<NkAuthorProfile>();
            },
            configureServices: s => s.AddDbContext<NkDbContext>(o => o.UseSqlite(_connection)));

        using IServiceScope scope = fx.App.Services.CreateScope();
        NkDbContext db = scope.ServiceProvider.GetRequiredService<NkDbContext>();
        db.Database.EnsureCreated();
        // Author 1 has a key; author 2's key is null. Both hold five books, one over the cap of 2.
        db.Authors.Add(new NkAuthor { Id = 1, Code = "ann" });
        db.Authors.Add(new NkAuthor { Id = 2, Code = null });
        for (int i = 1; i <= 5; i++) db.Books.Add(new NkBook { Id = i, AuthorId = 1, Title = $"A{i}" });
        for (int i = 1; i <= 5; i++) db.Books.Add(new NkBook { Id = 10 + i, AuthorId = 2, Title = $"B{i}" });
        db.SaveChanges();
        return fx;
    }

    // The control: a row WITH a key pages exactly as #313 says it should.
    [Fact]
    public async Task AKeyedRow_StillPagesAndLinks()
    {
        await using TestFixture fx = await BuildAsync();

        HttpResponseMessage r = await fx.Client.GetAsync("/odata/NkAuthors?$filter=Code eq 'ann'&$expand=Books");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        JsonElement parent = doc.RootElement.GetProperty("value")[0];
        Assert.Equal(2, parent.GetProperty("Books").GetArrayLength());
        Assert.True(parent.TryGetProperty("Books@odata.nextLink", out _));
    }

    // The finding: a row whose key value is null cannot be linked, so it takes the ceiling's 400
    // rather than a silently truncated array.
    [Fact]
    public async Task ANullKeyedRow_Returns400_AndNeverASilentlyTruncatedArray()
    {
        await using TestFixture fx = await BuildAsync();

        HttpResponseMessage r = await fx.Client.GetAsync("/odata/NkAuthors?$filter=Id eq 2&$expand=Books");
        string body = await r.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("InvalidQueryOption", body);
        Assert.Contains("exceeds the maximum of 2 entities", body);
    }

    // The regression proper, stated as the invariant rather than as a status code: whatever the answer
    // is, a 200 must never carry a bounded array without a continuation link beside it.
    [Fact]
    public async Task NoResponseEverCarriesATrimmedArrayWithoutALink()
    {
        await using TestFixture fx = await BuildAsync();

        HttpResponseMessage r = await fx.Client.GetAsync("/odata/NkAuthors?$expand=Books");
        if (r.StatusCode != HttpStatusCode.OK) return; // a 400 satisfies M1 outright

        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        foreach (JsonElement parent in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            int count = parent.GetProperty("Books").GetArrayLength();
            bool linked = parent.TryGetProperty("Books@odata.nextLink", out _);
            Assert.True(count < 2 || linked,
                "an expanded collection was bounded to MaxExpandTop with no nextLink beside it");
        }
    }
}
