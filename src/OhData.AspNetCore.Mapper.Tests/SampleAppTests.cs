using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Mapper.Tests;

/// <summary>
/// Boots <c>samples/OhData.Sample.EfCoreSqlite</c>'s real <c>Program.cs</c> over a throwaway SQLite
/// file, so the example adopters copy is proved to work against a real provider rather than only
/// to compile.
/// </summary>
public sealed class SampleHost : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ohdata-sample-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> _factory = null!;

    public SqlCapture Sql { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Shop", $"Data Source={_dbPath}");
            b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(Sql)));
        });
        Client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (string f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*"))
            File.Delete(f);
    }

    public async Task<JsonObject> GetAsync(string url)
    {
        HttpResponseMessage r = await Client.GetAsync(url);
        string body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} for {url}: {body}");
        return JsonNode.Parse(body)!.AsObject();
    }

    public sealed class SqlCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _queue = new();

        public string[] Statements => _queue.ToArray();
        public void Clear() => _queue.Clear();

        public ILogger CreateLogger(string categoryName) =>
            categoryName == "Microsoft.EntityFrameworkCore.Database.Command" ? new Sink(_queue) : NullLogger.Instance;

        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<string> queue) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => queue.Enqueue(formatter(state, exception));
        }
    }
}

public sealed class SampleReadTests(SampleHost host) : IClassFixture<SampleHost>
{
    private Task<JsonObject> GetAsync(string url) => host.GetAsync(url);

    // The text after WHERE: the SELECT list already carries the join and the || concat for every
    // Orders query, so only the predicate proves a filter ran in SQL.
    private static string WhereClause(string sql)
    {
        int at = sql.IndexOf("WHERE", StringComparison.Ordinal);
        return at < 0 ? "" : sql[at..];
    }

    [Fact]
    public async Task Get_ServesTheMappedShape_NotTheEntity()
    {
        JsonObject order = await GetAsync("/odata/Orders(1)");

        Assert.Equal("SO-1001", (string?)order["OrderNumber"]);      // renamed from Order.Number
        Assert.Equal("Ada Lovelace", (string?)order["CustomerName"]); // path through Customer
        Assert.Equal("Ada Lovelace", (string?)order["ShipTo"]);       // format of ShipFirst + ShipLast
        Assert.Equal(4999, (int?)order["TotalCents"]);
        Assert.False(order.ContainsKey("Number"));
        Assert.False(order.ContainsKey("Customer")); // a reference is served only when expanded
    }

    [Fact]
    public async Task Filter_ThroughAPath_PutsTheCustomerNameInTheWhereClause()
    {
        host.Sql.Clear();
        JsonObject page = await GetAsync("/odata/Orders?$filter=CustomerName eq 'Grace Hopper'");

        JsonObject row = Assert.Single(page["value"]!.AsArray())!.AsObject();
        Assert.Equal("SO-1002", (string?)row["OrderNumber"]);

        Assert.Contains(host.Sql.Statements, s => WhereClause(s).Contains("\"c\".\"Name\" ="));
    }

    [Fact]
    public async Task Filter_OnAFormattedMember_RunsInSql()
    {
        host.Sql.Clear();
        JsonObject page = await GetAsync("/odata/Orders?$filter=ShipTo eq 'Byron Lovelace'");

        JsonObject row = Assert.Single(page["value"]!.AsArray())!.AsObject();
        Assert.Equal("SO-1003", (string?)row["OrderNumber"]);
        Assert.Contains(host.Sql.Statements, s => WhereClause(s).Contains("||"));
    }

    [Fact]
    public async Task Expand_Reference_NestsTheMappedCustomer()
    {
        JsonObject page = await GetAsync("/odata/Orders?$expand=Customer&$orderby=OrderNumber");

        string?[] names = page["value"]!.AsArray()
            .Select(r => (string?)r!["Customer"]!["Name"]).ToArray();
        Assert.Equal(new[] { "Ada Lovelace", "Grace Hopper", "Ada Lovelace" }, names);
    }
}

public sealed class SampleWriteTests : IAsyncLifetime
{
    private readonly SampleHost _host = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _host.InitializeAsync();
        _client = _host.Client;
    }

    public Task DisposeAsync() => _host.DisposeAsync();

    private Task<JsonObject> GetAsync(string url) => _host.GetAsync(url);

    [Fact]
    public async Task Patch_PersistsOnlyWhatTheClientSent_ThroughTheDeltaMapping()
    {
        HttpResponseMessage r = await _client.PatchAsJsonAsync(
            "/odata/Orders(1)", new { OrderNumber = "SO-1001-R", TotalCents = 5500, CustomerName = "ignored" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        JsonObject echoed = JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("SO-1001-R", (string?)echoed["OrderNumber"]);

        JsonObject reread = await GetAsync("/odata/Orders(1)");
        Assert.Equal("SO-1001-R", (string?)reread["OrderNumber"]);   // renamed member reached Order.Number
        Assert.Equal(5500, (int?)reread["TotalCents"]);
        Assert.Equal("Ada Lovelace", (string?)reread["CustomerName"]); // Ignore()d: not writable
        Assert.Equal("Ada", (string?)reread["ShipFirst"]);             // untouched by the PATCH
    }

    [Fact]
    public async Task Post_CreatesAnEntity_AndRespondsWithTheDerivedMembers()
    {
        HttpResponseMessage r = await _client.PostAsJsonAsync("/odata/Orders", new
        {
            OrderNumber = "SO-2000",
            CustomerId = 2,
            ShipFirst = "Alan",
            ShipLast = "Turing",
            TotalCents = 100,
        });

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        JsonObject created = JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();
        int id = (int)created["Id"]!;
        Assert.True(id > 3);
        Assert.Equal("Grace Hopper", (string?)created["CustomerName"]);
        Assert.Equal("Alan Turing", (string?)created["ShipTo"]);

        JsonObject reread = await GetAsync($"/odata/Orders({id})");
        Assert.Equal("SO-2000", (string?)reread["OrderNumber"]);
    }

    [Fact]
    public async Task Put_ReplacesTheWritableMembers()
    {
        HttpResponseMessage r = await _client.PutAsJsonAsync("/odata/Orders(3)", new
        {
            Id = 3, // PUT carries the key; the delta mapping ignores it when writing
            OrderNumber = "SO-1003-P",
            CustomerId = 2,
            ShipFirst = "Grace",
            ShipLast = "Hopper",
            TotalCents = 1,
        });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        JsonObject reread = await GetAsync("/odata/Orders(3)");
        Assert.Equal("SO-1003-P", (string?)reread["OrderNumber"]);
        Assert.Equal("Grace Hopper", (string?)reread["CustomerName"]);
        Assert.Equal("Grace Hopper", (string?)reread["ShipTo"]);
        Assert.Equal(1, (int?)reread["TotalCents"]);
    }

    [Fact]
    public async Task Post_WithAnUnknownCustomer_Is400NamingCustomerId_NotAForeignKey500()
    {
        HttpResponseMessage r = await _client.PostAsJsonAsync("/odata/Orders", new
        {
            OrderNumber = "SO-3000",
            CustomerId = 999,
            ShipFirst = "A",
            ShipLast = "B",
            TotalCents = 1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        JsonObject error = JsonNode.Parse(await r.Content.ReadAsStringAsync())!["error"]!.AsObject();
        Assert.Equal("CustomerId", (string?)error["target"]);
    }

    [Fact]
    public async Task Patch_ToAnUnknownCustomer_Is400AndChangesNothing()
    {
        HttpResponseMessage r = await _client.PatchAsJsonAsync("/odata/Orders(1)", new { CustomerId = 999 });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(1, (int?)(await GetAsync("/odata/Orders(1)"))["CustomerId"]);
    }

    [Fact]
    public async Task Patch_OfADerivedMember_IsIgnored()
    {
        HttpResponseMessage r = await _client.PatchAsJsonAsync("/odata/Orders(1)", new { ShipTo = "Someone Else" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Ada Lovelace", (string?)(await GetAsync("/odata/Orders(1)"))["ShipTo"]);
    }
}
