using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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

namespace OhData.ClientTestBench;

// Shared: Program.cs runs it as a demo and the client test projects link it as a <Compile> item.
// Types are public because Microsoft.OData.Client instantiates entity types by reflection.
// The bench is single-caller: one shared SqliteConnection and an unlocked GadgetStore.

// -- Awards: a TPH hierarchy with a collection navigation (mirrors src/OhData.TestBench.AspNetCore/Models.cs) --

public class Award
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Year { get; set; }
    public List<AwardNomination> Nominations { get; set; } = new();
}

public class AcademyAward : Award
{
    public string Ceremony { get; set; } = "";
    public bool IsWinner { get; set; }
}

public class FestivalAward : Award
{
    public string Festival { get; set; } = "";
    public string Jury { get; set; } = "";
}

/// <summary>Unidirectional on purpose: a two-way relationship creates a cyclic graph via EF fixup.</summary>
public class AwardNomination
{
    public int Id { get; set; }
    public int AwardId { get; set; }
    public string Title { get; set; } = "";
}

public class BenchDbContext(DbContextOptions<BenchDbContext> options) : DbContext(options)
{
    public DbSet<Award> Awards => Set<Award>();
    public DbSet<AwardNomination> AwardNominations => Set<AwardNomination>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Award>().HasMany(a => a.Nominations).WithOne().HasForeignKey(n => n.AwardId);
        modelBuilder.Entity<AcademyAward>();
        modelBuilder.Entity<FestivalAward>();
    }
}

public class AwardProfile : EntitySetProfile<int, Award>
{
    public AwardProfile(BenchDbContext db) : base(x => x.Id)
    {
        EntitySetName = "Awards";
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;

        GetQueryable = () => db.Awards;
        GetById = (id, _) => OhDataResult.Success(db.Awards.FirstOrDefault(a => a.Id == id));

        HasMany(x => x.Nominations); // delegate-less: engages the $expand pushdown
    }
}

// -- Gadgets: typed rejections, a null structural property, and paging ----------------------------

public class Gadget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Note { get; set; }
}

public sealed class GadgetStore
{
    public List<Gadget> Items { get; } = Enumerable.Range(1, 5)
        .Select(i => new Gadget { Id = i, Name = $"Gadget{i}", Note = i == 1 ? null : $"note{i}" })
        .ToList();
}

/// <summary>Magic names make a handler reject: "dup" is 409, "stale" is 412; deleting id 1 is 403.</summary>
public class GadgetProfile : EntitySetProfile<int, Gadget>
{
    private static Task<OhDataResult<T>> Reject<T>(OhDataResult rejection) =>
        Task.FromResult<OhDataResult<T>>(rejection);

    public GadgetProfile(GadgetStore store) : base(x => x.Id)
    {
        EntitySetName = "Gadgets";
        IdempotentDelete = false;
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;

        GetQueryable = () => store.Items.AsQueryable();
        GetById = (id, _) => OhDataResult.Success(store.Items.FirstOrDefault(g => g.Id == id));
        Post = (g, _) =>
        {
            if (g.Name == "dup")
                return Reject<Gadget>(OhDataResult.Conflict("DuplicateName", "A gadget named 'dup' exists.", "Name"));
            g.Id = store.Items.Max(x => x.Id) + 1;
            store.Items.Add(g);
            return OhDataResult.Success<Gadget>(g);
        };
        Put = (id, g, _) =>
        {
            if (g.Name == "stale")
                return Reject<Gadget?>(OhDataResult.PreconditionFailed("StaleVersion", "The gadget changed.", "Name"));
            store.Items.RemoveAll(x => x.Id == id);
            g.Id = id;
            store.Items.Add(g);
            return OhDataResult.Success(g);
        };
        Delete = (id, _) => id == 1
            ? Reject<bool>(OhDataResult.Forbidden("Protected", "Gadget 1 cannot be deleted.", "Id"))
            : OhDataResult.Success(store.Items.RemoveAll(g => g.Id == id) > 0);
    }
}

// -- Hosting ---------------------------------------------------------------------------------------

/// <summary>An in-process OhData server over the bench's models, plus an HttpClient addressing its prefix.</summary>
public sealed class BenchServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SqliteConnection _connection;

    /// <summary>Base address already includes the OData prefix.</summary>
    public HttpClient Http { get; }

    private BenchServer(WebApplication app, SqliteConnection connection, string prefix)
    {
        _app = app;
        _connection = connection;
        Http = ((IHost)app).GetTestClient();
        Http.BaseAddress = new Uri(Http.BaseAddress!, prefix.Trim('/') + "/");
    }

    public static async Task<BenchServer> BuildAsync(
        string prefix = "/odata", Action<OhDataBuilder>? configure = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddDbContext<BenchDbContext>(o => o.UseSqlite(connection));
        builder.Services.AddSingleton(new GadgetStore());
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            o.AddEntitySetProfile<AwardProfile>();
            o.AddEntitySetProfile<GadgetProfile>();
            configure?.Invoke(o);
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();

        using (IServiceScope scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BenchDbContext>();
            db.Database.EnsureCreated();
            db.Awards.AddRange(
                new AcademyAward { Id = 1, Name = "Best Picture", Year = 1994, Ceremony = "67th Academy Awards", IsWinner = true },
                new FestivalAward { Id = 2, Name = "Palme d'Or", Year = 1994, Festival = "Cannes", Jury = "Clint Eastwood" },
                new Award { Id = 3, Name = "Audience Choice", Year = 1994 });
            db.AwardNominations.AddRange(
                new AwardNomination { Id = 1, AwardId = 1, Title = "Forrest Gump" },
                new AwardNomination { Id = 2, AwardId = 1, Title = "The Shawshank Redemption" },
                new AwardNomination { Id = 3, AwardId = 1, Title = "Pulp Fiction" },
                new AwardNomination { Id = 4, AwardId = 2, Title = "Pulp Fiction" },
                new AwardNomination { Id = 5, AwardId = 3, Title = "The Lion King" });
            db.SaveChanges();
        }

        return new BenchServer(app, connection, prefix);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
