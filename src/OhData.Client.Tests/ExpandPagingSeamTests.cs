using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.Client.Tests;

// #313: the stage-4/stage-5 SEAM -- this client reading this server's nested continuation link.
// Neither stage's suite ran the two together (stage 4 binds canned bytes, stage 5 asserts raw HTTP),
// so the one claim the combination makes -- that the annotation name the client looks for is the one
// the server writes -- was untested in both directions.
//
// Lives in this project because the seam is client-against-real-server, which is already its charter,
// and it already references both assemblies. Putting it in the server suite would invert the
// dependency direction.
//
// EF is required, measured not assumed: the pushdown that emits the link only engages over a real
// provider, and the identical profile over List<T>.AsQueryable() returns the whole collection with no
// link -- which would make every test below vacuously green. S0 asserts the fixture really pages, on
// RAW BYTES, before the client is involved.

// ── Fixture ──────────────────────────────────────────────────────────────────

internal class SeamAuthor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<SeamBook> Books { get; set; } = new();
}

internal class SeamBook
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public string Title { get; set; } = "";
}

internal class SeamDbContext : DbContext
{
    public SeamDbContext(DbContextOptions<SeamDbContext> options) : base(options) { }

    public DbSet<SeamAuthor> Authors => Set<SeamAuthor>();
    public DbSet<SeamBook> Books => Set<SeamBook>();

    // Explicit, not by convention: SeamBook has no back-reference to SeamAuthor, so without this the
    // FK is never bound and every Books collection comes back empty — which would make S0 fail loudly
    // (as it did) rather than making the rest of the file pass vacuously.
    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<SeamAuthor>().HasMany(a => a.Books).WithOne().HasForeignKey(x => x.AuthorId);
}

internal class SeamAuthorProfile : EntitySetProfile<int, SeamAuthor>
{
    public SeamAuthorProfile(SeamDbContext db) : base(x => x.Id)
    {
        EntitySetName = "SeamAuthors";
        ExpandEnabled = true;
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Authors.AsQueryable());
        GetById = (id, ct) => Task.FromResult(
            db.Authors.Include(a => a.Books).FirstOrDefault(a => a.Id == id));
        HasMany(x => x.Books); // delegate-less → pushable, so pageable
    }
}

/// <summary>
/// A live OhData server over EF Core / SQLite with both #313 knobs configurable, plus the
/// <see cref="OhDataClient"/> pointed at it.
/// </summary>
internal sealed class ExpandPagingSeamFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SqliteConnection _connection;

    public HttpClient Http { get; }
    public OhDataClient Client { get; }

    private ExpandPagingSeamFixture(
        WebApplication app, SqliteConnection connection, string prefix, OhDataClientOptions? options)
    {
        _app = app;
        _connection = connection;
        Http = ((IHost)app).GetTestClient();
        Http.BaseAddress = new Uri(Http.BaseAddress!, prefix.Trim('/') + "/");
        Client = options is null ? new OhDataClient(Http) : new OhDataClient(Http, options);
    }

    public static async Task<ExpandPagingSeamFixture> BuildAsync(
        int? maxExpandTop = 2,
        bool pagingEnabled = true,
        int? maxTop = null,
        JsonNamingPolicy? namingPolicy = null,
        OhDataClientOptions? clientOptions = null,
        string prefix = "/odata")
    {
        // A shared open in-memory SQLite connection: the schema and rows live as long as it does.
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddDbContext<SeamDbContext>(o => o.UseSqlite(connection));
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            o.WithDefaults(d =>
            {
                d.MaxExpandTop = maxExpandTop;
                d.ExpandPagingEnabled = pagingEnabled;
                if (maxTop is int mt) d.MaxTop = mt;
            });
            if (namingPolicy is not null) o.WithJsonPropertyNamingPolicy(namingPolicy);
            o.AddEntitySetProfile<SeamAuthorProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();

        using (IServiceScope scope = app.Services.CreateScope())
        {
            SeamDbContext db = scope.ServiceProvider.GetRequiredService<SeamDbContext>();
            db.Database.EnsureCreated();
            Seed(db);
        }

        return new ExpandPagingSeamFixture(app, connection, prefix, clientOptions);
    }

    // Ann(1)=5 books, Bob(2)=1, Cal(3)=0, Dee(4)=7, Eve(5)=1 — so at cap 2 exactly two authors page.
    private static void Seed(SeamDbContext db)
    {
        db.Authors.AddRange(
            new SeamAuthor { Id = 1, Name = "Ann" },
            new SeamAuthor { Id = 2, Name = "Bob" },
            new SeamAuthor { Id = 3, Name = "Cal" },
            new SeamAuthor { Id = 4, Name = "Dee" },
            new SeamAuthor { Id = 5, Name = "Eve" });

        for (int i = 1; i <= 5; i++) db.Books.Add(new SeamBook { Id = i, AuthorId = 1, Title = $"Bk{i}" });
        db.Books.Add(new SeamBook { Id = 20, AuthorId = 2, Title = "Solo" });
        for (int i = 0; i < 7; i++) db.Books.Add(new SeamBook { Id = 40 + i, AuthorId = 4, Title = $"D{i}" });
        db.Books.Add(new SeamBook { Id = 50, AuthorId = 5, Title = "Deep" });

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Http.Dispose();
        await _app.DisposeAsync();
        _connection.Dispose();
    }
}

// ── The client's view of the model ───────────────────────────────────────────
//
// Separate POCOs on purpose: a real client owns its own DTOs and never shares the server's entity
// types. That is also what makes the annotation-name assertions meaningful — nothing here is shared
// with the server except the wire.

public sealed class CliSeamAuthor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<CliSeamBook> Books { get; set; } = new();
}

public sealed class CliSeamBook
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}

// ── The seam ─────────────────────────────────────────────────────────────────

public sealed class ExpandPagingSeamTests
{
    // S0 — the fixture itself. If the server does not page here, every test below is vacuous, so it
    // is asserted on the RAW bytes before the client is involved.
    [Fact]
    public async Task S0_TheServerReallyEmitsANestedNextLink()
    {
        await using ExpandPagingSeamFixture fx = await ExpandPagingSeamFixture.BuildAsync();

        string raw = await fx.Http.GetStringAsync("/odata/SeamAuthors?$filter=Id eq 1&$expand=Books");

        Assert.Contains("Books@odata.nextLink", raw);
    }

    // S1 — THE NAME. Does the annotation the client looks for match the one the server writes,
    // exactly, through the client's own expression accessor?
    [Fact]
    public async Task S1_ClientReadsTheServersNestedNextLink()
    {
        await using ExpandPagingSeamFixture fx = await ExpandPagingSeamFixture.BuildAsync();

        ODataAnnotatedPage<CliSeamAuthor> page = await fx.Client.For<CliSeamAuthor>("SeamAuthors")
            .Filter(a => a.Id == 1)
            .Expand(a => a.Books)
            .ToAnnotatedPageAsync();

        ODataAnnotatedEntity<CliSeamAuthor> ann = Assert.Single(page.Entries);
        Assert.Equal(2, ann.Entity.Books.Count);                 // a PREFIX, not the whole 5
        Assert.NotNull(ann.NextLinkFor(a => a.Books));           // expression accessor
        Assert.NotNull(ann.Annotations.NextLinkFor("Books"));    // string accessor
        Assert.Null(ann.CountFor(a => a.Books));                 // no $count asked for
    }

    // S2 — FOLLOWABILITY. The URL the server issues, handed straight back into the client's HttpClient
    // and walked to exhaustion: terminates, no empty page, each child served exactly once, in order.
    [Fact]
    public async Task S2_TheEmittedLinkIsFollowableAndTheWalkTerminatesServingEachChildOnce()
    {
        await using ExpandPagingSeamFixture fx = await ExpandPagingSeamFixture.BuildAsync();

        ODataAnnotatedPage<CliSeamAuthor> page = await fx.Client.For<CliSeamAuthor>("SeamAuthors")
            .Filter(a => a.Id == 1)
            .Expand(a => a.Books)
            .ToAnnotatedPageAsync();

        ODataAnnotatedEntity<CliSeamAuthor> ann = Assert.Single(page.Entries);
        var ids = ann.Entity.Books.Select(b => b.Id).ToList();

        Uri? next = ann.NextLinkFor(a => a.Books);
        int hops = 0;
        while (next is not null)
        {
            Assert.True(++hops <= 10, "the nested nextLink walk did not terminate");
            string body = await fx.Http.GetStringAsync(next);   // followed EXACTLY as issued
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement value = doc.RootElement.GetProperty("value");
            Assert.True(value.GetArrayLength() > 0, "a continuation page came back empty");
            foreach (JsonElement b in value.EnumerateArray()) ids.Add(b.GetProperty("Id").GetInt32());
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out JsonElement nl)
                ? new Uri(nl.GetString()!)
                : null;
        }

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ids);
    }

    // S3 — ToAnnotatedAsyncEnumerable over a PAGED ROOT whose entities also carry nested links:
    // terminates, yields each entity exactly once, and every entry keeps its own nested link.
    [Fact]
    public async Task S3_AnnotatedAsyncEnumerable_OverPagedRoot_WithNestedLinks()
    {
        await using ExpandPagingSeamFixture fx = await ExpandPagingSeamFixture.BuildAsync(maxTop: 2);

        var seen = new List<int>();
        int withLink = 0;
        await foreach (ODataAnnotatedEntity<CliSeamAuthor> e in fx.Client.For<CliSeamAuthor>("SeamAuthors")
            .Expand(a => a.Books)
            .ToAnnotatedAsyncEnumerable())
        {
            Assert.True(seen.Count < 20, "the root walk did not terminate");
            seen.Add(e.Entity.Id);
            if (e.NextLinkFor(a => a.Books) is not null) withLink++;
        }

        // Only Ann (5 books) and Dee (7) are over cap 2.
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, seen.ToArray());
        Assert.Equal(2, withLink);
    }

    // S4 — a root $select strips the parent key server-side. Stage 5 threads the CLR key past the
    // strip; does the CLIENT still get a link, and does that link still work?
    [Fact]
    public async Task S4_RootSelect_StillYieldsAUsableNestedLink()
    {
        await using ExpandPagingSeamFixture fx = await ExpandPagingSeamFixture.BuildAsync();

        ODataAnnotatedPage<CliSeamAuthor> page = await fx.Client.For<CliSeamAuthor>("SeamAuthors")
            .Filter(a => a.Id == 1)
            .Select(a => a.Name)
            .Expand(a => a.Books)
            .ToAnnotatedPageAsync();

        ODataAnnotatedEntity<CliSeamAuthor> ann = Assert.Single(page.Entries);
        Assert.Equal(0, ann.Entity.Id);                          // the key really was stripped
        Uri? link = ann.NextLinkFor(a => a.Books);
        Assert.NotNull(link);

        string body = await fx.Http.GetStringAsync(link!);
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("value").GetArrayLength());
    }

    // S5 — the single-entity read. The #313 ceiling and its continuation link were COLLECTION-ROUTE
    // ONLY: EnsureWithinExpandCeiling and WriteNestedNextLink are reachable only through
    // ShapePushedExpandsInJson, whose sole call site is the GetQueryable collection route. So on the
    // same registration, GET /SeamAuthors(1)?$expand=Books returned ALL FIVE books with no ceiling
    // and no link, while GET /SeamAuthors?$expand=Books returned two plus a link.
    //
    // #418 CLOSED THAT — as a 400, not as a link. This test was rewritten in place rather than
    // deleted, because what it exists to pin is the SEAM: what a client sees on the single-entity
    // route of a registration whose collection route pages. The answer is now an error, and the
    // client is never handed a silently truncated collection that looks complete.
    //
    // WHY NOT A LINK HERE. Page 1's child order on this route comes from the developer's own GetById
    // delegate (SeamAuthorProfile Includes Books, and EF composes no ORDER BY over the child), while
    // the continuation route orders by the child key IN THE DATABASE. The framework composes neither
    // side, so the two orders cannot be shown to agree, and a link over a disagreeing order skips and
    // duplicates rows invisibly. EnforceSingleEntityExpandCeiling's remarks carry the full argument.
    [Fact]
    public async Task S5_GetById_TakesTheCeilingAs400_AndNeverSilentlyTruncates()
    {
        await using ExpandPagingSeamFixture fx = await ExpandPagingSeamFixture.BuildAsync();

        // Raw bytes first: the server, not the client, is what changed.
        HttpResponseMessage raw = await fx.Http.GetAsync("SeamAuthors(1)?$expand=Books");
        string body = await raw.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, raw.StatusCode);
        Assert.Contains("\"code\":\"InvalidQueryOption\"", body);
        Assert.Contains("maximum of 2 entities", body);
        // Neither half of the forbidden outcome: no truncated page, and no link promising one.
        Assert.DoesNotContain("@odata.nextLink", body);
        Assert.DoesNotContain("\"Title\"", body);

        // The collection route on the SAME registration still pages, which is what makes the
        // asymmetry deliberate rather than an oversight.
        ODataAnnotatedPage<CliSeamAuthor> page = await fx.Client.For<CliSeamAuthor>("SeamAuthors")
            .Filter(a => a.Id == 1)
            .Expand(a => a.Books)
            .ToAnnotatedPageAsync();
        ODataAnnotatedEntity<CliSeamAuthor> ann = Assert.Single(page.Entries);
        Assert.Equal(2, ann.Entity.Books.Count);
        Assert.NotNull(ann.NextLinkFor(a => a.Books));
    }

    // S6 — the client's own docs say a nested count is surfaced. Does a real
    // $expand=Books($count=true) reach CountFor()?
    [Fact]
    public async Task S6_NestedCount_FromARealServer()
    {
        await using ExpandPagingSeamFixture fx = await ExpandPagingSeamFixture.BuildAsync(
            maxExpandTop: 10, pagingEnabled: false);

        ODataAnnotatedPage<CliSeamAuthor> page = await fx.Client.For<CliSeamAuthor>("SeamAuthors")
            .Filter(a => a.Id == 1)
            .Expand("Books($count=true)")
            .ToAnnotatedPageAsync();

        ODataAnnotatedEntity<CliSeamAuthor> ann = Assert.Single(page.Entries);
        Assert.Equal(5, ann.CountFor(a => a.Books));
    }
}

// ── The same seam under a NON-DEFAULT naming policy ──────────────────────────
//
// Stage 5's tests all run the PascalCase default; stage 4's canned-byte tests spell camelCase names
// by hand. Neither combination is a real camelCase server read by a real camelCase client — and the
// annotation name is built from the PAYLOAD key (naming policy + [JsonPropertyName]) on the server
// while the client resolves it through its OWN naming policy, so the two derivations have to agree.

public sealed class ExpandPagingSeamCamelCaseTests
{
    private static readonly OhDataClientOptions _camelClient = new()
    {
        JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        },
    };

    [Fact]
    public async Task C0_TheCamelCaseServerEmitsACamelCasedAnnotationName()
    {
        await using ExpandPagingSeamFixture fx = await ExpandPagingSeamFixture.BuildAsync(
            namingPolicy: JsonNamingPolicy.CamelCase, clientOptions: _camelClient);

        string raw = await fx.Http.GetStringAsync("/odata/SeamAuthors?$filter=id eq 1&$expand=Books");

        // The annotation is a sibling of the property it annotates, under the SAME spelling.
        Assert.Contains("books@odata.nextLink", raw);
        Assert.DoesNotContain("Books@odata.nextLink", raw);
    }

    [Fact]
    public async Task C1_CamelCaseServer_ReadByCamelCaseClient()
    {
        await using ExpandPagingSeamFixture fx = await ExpandPagingSeamFixture.BuildAsync(
            namingPolicy: JsonNamingPolicy.CamelCase, clientOptions: _camelClient);

        ODataAnnotatedPage<CliSeamAuthor> page = await fx.Client.For<CliSeamAuthor>("SeamAuthors")
            .Filter(a => a.Id == 1)
            .Expand(a => a.Books)
            .ToAnnotatedPageAsync();

        ODataAnnotatedEntity<CliSeamAuthor> ann = Assert.Single(page.Entries);
        Assert.Equal(2, ann.Entity.Books.Count);

        // The client resolves x => x.Books through its own camelCase policy to "books", which is what
        // the server spelled. This is the assertion the two stages never made together.
        Uri? link = ann.NextLinkFor(a => a.Books);
        Assert.NotNull(link);

        string body = await fx.Http.GetStringAsync(link!);
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("value").GetArrayLength());
    }
}
