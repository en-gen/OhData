using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class I792Thing { public int Id { get; set; } public string Name { get; set; } = ""; }

public sealed class I792Boom : Exception
{
    public I792Boom() : base("boom") { }
}

/// <summary>Scoped service: one Guid per DI scope, i.e. per request.</summary>
public sealed class I792Scoped { public Guid Id { get; } = Guid.NewGuid(); }

public sealed class I792Db : DbContext
{
    public I792Db(DbContextOptions<I792Db> options) : base(options) { }
    public DbSet<I792Thing> Things => Set<I792Thing>();
}

/// <summary>Handlers record the request-scope id they saw; the rule echoes the id it sees.</summary>
public sealed class I792ScopedProfile : EntitySetProfile<int, I792Thing>
{
    public static readonly ConcurrentQueue<(string Op, Guid Id)> Seen = new();

    public I792ScopedProfile(I792Scoped scoped) : base(x => x.Id)
    {
        EntitySetName = "I792Scoped";
        GetById = (id, _) => { Seen.Enqueue(("GET", scoped.Id)); throw new I792Boom(); };
        Post = (m, _) => { Seen.Enqueue(("POST", scoped.Id)); throw new I792Boom(); };
        Put = (id, m, _) => { Seen.Enqueue(("PUT", scoped.Id)); throw new I792Boom(); };
        Patch = (id, d, _) => { Seen.Enqueue(("PATCH", scoped.Id)); throw new I792Boom(); };
        Delete = (id, _) => { Seen.Enqueue(("DELETE", scoped.Id)); throw new I792Boom(); };
        ConfigureExceptions(e => e.Map<I792Boom>((c, ex) =>
            OhDataResult.Conflict("Mapped", "scope=" + scoped.Id)));
    }
}

public sealed class I792DbProfile : EntitySetProfile<int, I792Thing>
{
    public static int PostReached;

    public I792DbProfile(I792Db db) : base(x => x.Id)
    {
        EntitySetName = "I792Db";
        Post = (m, _) => { PostReached++; throw new I792Boom(); };
        ConfigureExceptions(e => e.Map<I792Boom>((c, ex) =>
            OhDataResult.Conflict("Mapped", "entries=" + db.ChangeTracker.Entries().Count())));
    }
}

/// <summary>Priority-1 collection read: the profile owns the query, a handler fault is mapped.</summary>
public sealed class I792P1Profile : ODataEntitySetProfile<int, I792Thing>
{
    public static readonly ConcurrentQueue<Guid> Seen = new();

    public I792P1Profile(I792Scoped scoped) : base(x => x.Id)
    {
        EntitySetName = "I792P1";
        GetODataQueryable = (options, ct) => { Seen.Enqueue(scoped.Id); throw new I792Boom(); };
        ConfigureExceptions(e => e.Map<I792Boom>((c, ex) =>
            OhDataResult.Conflict("Mapped", "scope=" + scoped.Id)));
    }
}

/// <summary>The rule is declared in AdvancedConfigure, which runs only on the startup instance.</summary>
public sealed class I792AdvancedProfile : EntitySetProfile<int, I792Thing>
{
    public I792AdvancedProfile() : base(x => x.Id)
    {
        EntitySetName = "I792Advanced";
        Post = (m, _) => throw new I792Boom();
    }

    protected override void AdvancedConfigure(Microsoft.OData.ModelBuilder.EntitySetConfiguration<I792Thing> configuration)
    {
        ConfigureExceptions(e => e.Map<I792Boom>(_ => OhDataResult.Conflict("Mapped", "advanced")));
    }
}

public sealed class I792GetAllProfile : EntitySetProfile<int, I792Thing>
{
    public static readonly ConcurrentQueue<Guid> Seen = new();

    public I792GetAllProfile(I792Scoped scoped) : base(x => x.Id)
    {
        EntitySetName = "I792GetAll";
        GetAll = _ => { Seen.Enqueue(scoped.Id); throw new I792Boom(); };
        ConfigureExceptions(e => e.Map<I792Boom>((c, ex) =>
            OhDataResult.Conflict("Mapped", "scope=" + scoped.Id)));
    }
}

public sealed class I792StatelessProfile : EntitySetProfile<int, I792Thing>
{
    public I792StatelessProfile() : base(x => x.Id)
    {
        EntitySetName = "I792Stateless";
        Post = (m, _) => throw new I792Boom();
        ConfigureExceptions(e => e.Map<I792Boom>(_ => OhDataResult.Conflict("Mapped", "plain")));
    }
}

/// <summary>
/// #792 - <c>ConfigureExceptions</c> rules run on the REQUEST's profile instance, not the startup
/// instance whose scope is already disposed.
/// </summary>
public sealed class Issue792ExceptionRuleScopeTests
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<(HttpStatusCode Status, string Message)> SendAsync(
        TestFixture fx, HttpMethod method, string url, string? body = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = Json(body);
        using var res = await fx.Client.SendAsync(req);
        string text = await res.Content.ReadAsStringAsync();
        string message = "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String)
            {
                message = m.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // a non-JSON body leaves the message empty; the status assertion reports it
        }
        return (res.StatusCode, message);
    }

    private static Task<TestFixture> BuildScopedAsync() => TestHostBuilder.BuildAsync(
        o => { o.AddEntitySetProfile<I792ScopedProfile>(); o.AddEntitySetProfile<I792P1Profile>(); o.AddEntitySetProfile<I792GetAllProfile>(); },
        configureServices: s => s.AddScoped<I792Scoped>());

    [Fact]
    public async Task RuleSeesTheRequestScope_OnPost_AndIdsDifferBetweenRequests()
    {
        await using var fx = await BuildScopedAsync();
        I792ScopedProfile.Seen.Clear();

        var messages = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var (status, message) = await SendAsync(fx, HttpMethod.Post, "/odata/I792Scoped", "{\"Id\":1,\"Name\":\"n\"}");
            Assert.Equal(HttpStatusCode.Conflict, status);
            Guid handlerId = I792ScopedProfile.Seen.ToArray()[i].Id;
            Assert.Equal("scope=" + handlerId, message);
            messages.Add(message);
        }
        Assert.NotEqual(messages[0], messages[1]);
    }

    [Theory]
    [InlineData("GET", "/odata/I792Scoped(1)", null)]
    [InlineData("PUT", "/odata/I792Scoped(1)", "{\"Id\":1,\"Name\":\"n\"}")]
    [InlineData("PATCH", "/odata/I792Scoped(1)", "{\"Name\":\"n\"}")]
    [InlineData("DELETE", "/odata/I792Scoped(1)", null)]
    public async Task RuleSeesTheRequestScope_OnEveryKeyedSeam(string method, string url, string? body)
    {
        await using var fx = await BuildScopedAsync();
        I792ScopedProfile.Seen.Clear();

        var (status, message) = await SendAsync(fx, new HttpMethod(method), url, body);

        Assert.Equal(HttpStatusCode.Conflict, status);
        var seen = Assert.Single(I792ScopedProfile.Seen.ToArray());
        Assert.Equal(method, seen.Op);
        Assert.Equal("scope=" + seen.Id, message);
    }

    [Fact]
    public async Task RuleSeesTheRequestScope_OnAPriorityOneCollectionRead()
    {
        await using var fx = await BuildScopedAsync();
        I792P1Profile.Seen.Clear();

        var (status, message) = await SendAsync(fx, HttpMethod.Get, "/odata/I792P1");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Guid handlerId = Assert.Single(I792P1Profile.Seen.ToArray());
        Assert.Equal("scope=" + handlerId, message);
    }

    [Fact]
    public async Task RuleTouchingADbContext_MapsInsteadOfAnswering500()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<I792DbProfile>(),
            configureServices: s => s.AddDbContext<I792Db>(b => b.UseSqlite("DataSource=:memory:")));
        I792DbProfile.PostReached = 0;

        var (status, message) = await SendAsync(fx, HttpMethod.Post, "/odata/I792Db", "{\"Id\":1,\"Name\":\"n\"}");

        Assert.Equal(1, I792DbProfile.PostReached);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("entries=0", message);
    }

    [Fact]
    public async Task AStatelessRule_KeepsItsStatusAndBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<I792StatelessProfile>());

        var (status, message) = await SendAsync(fx, HttpMethod.Post, "/odata/I792Stateless", "{\"Id\":1,\"Name\":\"n\"}");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("plain", message);
    }

    [Fact]
    public async Task RuleSeesTheRequestScope_OnAGetAllCollectionRead()
    {
        await using var fx = await BuildScopedAsync();
        I792GetAllProfile.Seen.Clear();

        var (status, message) = await SendAsync(fx, HttpMethod.Get, "/odata/I792GetAll");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Guid handlerId = Assert.Single(I792GetAllProfile.Seen.ToArray());
        Assert.Equal("scope=" + handlerId, message);
    }

    [Fact]
    public async Task ARuleDeclaredInAdvancedConfigure_StillMaps()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<I792AdvancedProfile>());

        var (status, message) = await SendAsync(fx, HttpMethod.Post, "/odata/I792Advanced", "{\"Id\":1,\"Name\":\"n\"}");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("advanced", message);
    }
}
