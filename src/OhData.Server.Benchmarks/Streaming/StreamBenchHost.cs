using System;
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

namespace OhData.Server.Benchmarks.Streaming;

internal enum StreamBackend { InMemory, EfSqlite }

internal enum StreamRoute { GetQueryable, GetODataQueryable }

/// <summary>SPIKE #867: one OhData host (TestServer or real Kestrel) serving the four stream-bench sets.</summary>
internal sealed class StreamBenchHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SqliteConnection _connection;

    public HttpClient Client { get; }
    public OhDataRegistration Registration { get; }
    public string Kind { get; }

    private StreamBenchHost(WebApplication app, SqliteConnection connection, HttpClient client, string kind)
    {
        _app = app;
        _connection = connection;
        Client = client;
        Kind = kind;
        Registration = app.Services.GetRequiredService<OhDataRegistrationCollection>().Default;
    }

    public static async Task<StreamBenchHost> StartAsync(bool kestrel)
    {
        // Shared-cache named in-memory DB: each scoped DbContext gets its own connection, so concurrent
        // requests do not trip over one shared connection. The keep-alive connection holds the DB open.
        string connectionString = $"Data Source=streambench-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        var builder = WebApplication.CreateBuilder();
        if (kestrel) builder.WebHost.UseUrls("http://127.0.0.1:0");
        else builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<StreamBenchDbContext>(o => o.UseSqlite(connectionString));
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            o.WithDefaults(d => d.MaxTop = null);
            o.AddEntitySetProfile<StreamMemProfile>();
            o.AddEntitySetProfile<StreamMemP1Profile>();
            o.AddEntitySetProfile<StreamEfProfile>();
            o.AddEntitySetProfile<StreamEfP1Profile>();
        });

        WebApplication app = builder.Build();
        app.MapOhData();
        await app.StartAsync();

        using (IServiceScope scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StreamBenchDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Rows.AddRange(StreamBenchData.Create());
            await db.SaveChangesAsync();
        }

        HttpClient client = kestrel
            ? new HttpClient { BaseAddress = new Uri(app.Urls.First()) }
            : ((IHost)app).GetTestClient();
        return new StreamBenchHost(app, connection, client, kestrel ? "Kestrel" : "TestServer");
    }

    public static string Url(StreamBackend backend, StreamRoute route, int rows) =>
        $"/odata/{SetName(backend, route)}?$top={rows}";

    public static string SetName(StreamBackend backend, StreamRoute route) => (backend, route) switch
    {
        (StreamBackend.InMemory, StreamRoute.GetQueryable) => "StreamMem",
        (StreamBackend.InMemory, StreamRoute.GetODataQueryable) => "StreamMemP1",
        (StreamBackend.EfSqlite, StreamRoute.GetQueryable) => "StreamEf",
        _ => "StreamEfP1",
    };

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        _connection.Dispose();
    }
}
