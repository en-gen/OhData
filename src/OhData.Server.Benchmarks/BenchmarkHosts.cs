using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using OhData;
using OhData.Server.Benchmarks.Model;
using OhData.Server.Benchmarks.MsODataHost;
using OhData.Server.Benchmarks.OhDataHost;

namespace OhData.Server.Benchmarks;

/// <summary>
/// Builds the two in-process TestServer hosts under comparison. Both serve the identical deterministic
/// datasets so the two pipelines are exercised against an identical EDM shape and identical data, and
/// both are addressed at <c>/odata</c>.
/// <para>
/// Two data sources coexist per host: the original <see cref="BenchWidget"/> set is a plain
/// <c>List&lt;T&gt;</c>-backed store (no EF, no database — isolates the HTTP + OData pipeline from any
/// database noise) and remains unchanged. The newer <see cref="BenchDepartment"/>/<see cref="BenchEmployee"/>
/// navigation fixture is deliberately backed by EF Core Sqlite instead: OhData's <c>$expand</c> pushdown
/// (the "one JOIN per page" mechanism) is gated to an EF Core-backed <c>IQueryable</c> — a delegate-less
/// navigation over a plain <c>List&lt;T&gt;.AsQueryable()</c> would silently fall back to the non-pushdown
/// EDM-only path (see <c>OhDataEndpointFactory.ResolveEfCoreAssembly</c>) and the benchmark would measure
/// the wrong code path entirely. Each host opens its own keep-alive in-memory Sqlite connection and seeds
/// it independently (<see cref="BenchOrgData.Seed"/>) — no shared mutable state between the two servers,
/// matching the widget store's "own instance" discipline.
/// </para>
/// </summary>
internal static class BenchmarkHosts
{
    public const string Prefix = "/odata";
    public const string EntitySet = "BenchWidgets";

    /// <summary>OhData minimal-API pipeline host.</summary>
    public static async Task<(WebApplication App, HttpClient Client, SqliteConnection NavConnection)> StartOhDataAsync(int seed)
    {
        SqliteConnection connection = OpenNavConnection();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddDbContext<BenchOrgDbContext>(o => o.UseSqlite(connection));
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(Prefix);
            o.AddEntitySetProfile<BenchWidgetProfile>();
            o.AddEntitySetProfile<BenchDepartmentProfile>();
            o.AddEntitySetProfile<BenchEmployeeProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();
        await SeedNavDataAsync(app, seed);

        var client = ((IHost)app).GetTestClient();
        client.BaseAddress = new Uri(client.BaseAddress!, "odata/");
        return (app, client, connection);
    }

    /// <summary>Microsoft.AspNetCore.OData ODataController + [EnableQuery] pipeline host.</summary>
    public static async Task<(WebApplication App, HttpClient Client, SqliteConnection NavConnection)> StartMsODataAsync(int seed)
    {
        SqliteConnection connection = OpenNavConnection();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddDbContext<BenchOrgDbContext>(o => o.UseSqlite(connection));
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(BenchWidgetsController).Assembly)
            .AddOData(options => options
                .EnableQueryFeatures(maxTopValue: BenchmarkData.PageSize)
                .AddRouteComponents(Prefix.TrimStart('/'), BuildEdmModel()));

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        await SeedNavDataAsync(app, seed);

        var client = ((IHost)app).GetTestClient();
        client.BaseAddress = new Uri(client.BaseAddress!, "odata/");
        return (app, client, connection);
    }

    private static SqliteConnection OpenNavConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static async Task SeedNavDataAsync(WebApplication app, int seed)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BenchOrgDbContext>();
        await db.Database.EnsureCreatedAsync();
        BenchOrgData.Seed(db, seed);
    }

    private static IEdmModel BuildEdmModel()
    {
        var modelBuilder = new ODataConventionModelBuilder();
        // PascalCase wire format to match OhData's default JSON casing (1.5.0 flipped OhData's
        // default to PascalCase) — ODataConventionModelBuilder is already PascalCase by default,
        // so both hosts emit PascalCase and stay symmetric without any casing override here.
        modelBuilder.EntitySet<BenchWidget>(EntitySet);
        modelBuilder.EntitySet<BenchDepartment>("BenchDepartments");
        modelBuilder.EntitySet<BenchEmployee>("BenchEmployees");
        return modelBuilder.GetEdmModel();
    }
}
