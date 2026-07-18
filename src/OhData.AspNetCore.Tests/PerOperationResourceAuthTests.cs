using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── #199 Layer B: resource-based (instance-level) authorization ──────────────

internal class ResOwnedItem
{
    public int Id { get; set; }
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Owner-check handler using the built-in OhDataOperations requirements.</summary>
internal sealed class ResOwnedItemHandler : AuthorizationHandler<OperationAuthorizationRequirement, ResOwnedItem>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, OperationAuthorizationRequirement req, ResOwnedItem item)
    {
        string? user = ctx.User.Identity?.Name;
        if (user is not null && item.Owner == user)
            ctx.Succeed(req);
        return Task.CompletedTask;
    }
}

/// <summary>Custom requirement + handler used to exercise <c>.RequireResource("PolicyName")</c>.</summary>
internal sealed class SameOwnerRequirement : IAuthorizationRequirement { }

internal sealed class SameOwnerHandler : AuthorizationHandler<SameOwnerRequirement, ResOwnedItem>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, SameOwnerRequirement req, ResOwnedItem item)
    {
        if (ctx.User.Identity?.Name == item.Owner)
            ctx.Succeed(req);
        return Task.CompletedTask;
    }
}

internal abstract class ResProfileBase : EntitySetProfile<int, ResOwnedItem>
{
    protected ResProfileBase() : base(x => x.Id)
    {
        var store = new List<ResOwnedItem> { new() { Id = 1, Owner = "alice", Name = "A" } };
        GetAll = ct => Task.FromResult<IEnumerable<ResOwnedItem>>(store);
        GetById = (id, ct) => Task.FromResult(store.FirstOrDefault(x => x.Id == id));
        Post = (m, ct) => { m.Id = 99; store.Add(m); return Task.FromResult<ResOwnedItem?>(m); };
        Put = (id, m, ct) => { m.Id = id; return Task.FromResult(m); };
        Patch = (id, delta, ct) =>
        {
            var e = store.FirstOrDefault(x => x.Id == id);
            if (e is not null) delta.Patch(e);
            return Task.FromResult(e);
        };
        Delete = (id, ct) => Task.FromResult(store.RemoveAll(x => x.Id == id) > 0);
    }
}

internal sealed class ResourceCrudProfile : ResProfileBase
{
    public ResourceCrudProfile()
    {
        EntitySetName = "ResItems";
        ConfigureAuthorization(a => a
            .Read(r => r.RequireResource())
            .Writes(w => w.RequireResource()));
    }
}

internal sealed class ResourcePolicyProfile : ResProfileBase
{
    public ResourcePolicyProfile()
    {
        EntitySetName = "ResPolItems";
        ConfigureAuthorization(a => a
            .Read(r => r.RequireResource())
            .Update(u => u.RequireResource("OwnerPolicy")));
    }
}

// Resource-checked writes with NO GetById handler → startup guard.
internal sealed class ResourceNoGetByIdProfile : EntitySetProfile<int, ResOwnedItem>
{
    public ResourceNoGetByIdProfile() : base(x => x.Id)
    {
        EntitySetName = "ResNoGet";
        Put = (id, m, ct) => { m.Id = id; return Task.FromResult(m); };
        ConfigureAuthorization(a => a.Update(u => u.RequireResource()));
    }
}

internal static class ResourceAuthTestHost
{
    public static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        bool registerHandler = true,
        Action<AuthorizationOptions>? policies = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services
            .AddAuthentication(PerOpAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PerOpAuthHandler>(PerOpAuthHandler.SchemeName, _ => { });
        if (policies is not null)
            builder.Services.AddAuthorization(policies);
        else
            builder.Services.AddAuthorization();
        if (registerHandler)
        {
            builder.Services.AddScoped<IAuthorizationHandler, ResOwnedItemHandler>();
            builder.Services.AddScoped<IAuthorizationHandler, SameOwnerHandler>();
        }

        builder.Services.AddOhData(o => { o.WithPrefix("/odata"); configure(o); });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOhData();
        await app.StartAsync();
        return new TestFixture(app);
    }
}

public class PerOperationResourceAuthTests
{
    private static HttpRequestMessage Req(HttpMethod method, string path, string? identity, string? body = null)
    {
        var r = new HttpRequestMessage(method, path);
        if (body is not null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (identity is not null) r.Headers.Add(PerOpAuthHandler.IdentityHeader, identity);
        return r;
    }

    private static bool Passed(HttpStatusCode s) =>
        s != HttpStatusCode.Unauthorized && s != HttpStatusCode.Forbidden;

    // ── Read by id: owner passes, non-owner + anonymous 403 ─────────────────

    [Theory]
    [InlineData("alice", true)]
    [InlineData("bob", false)]
    [InlineData(null, false)]
    public async Task GetById_OwnerOnly(string? identity, bool passes)
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddProfile<ResourceCrudProfile>());
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Get, "/odata/ResItems(1)", identity));
        if (passes) Assert.True(Passed(resp.StatusCode), $"got {(int)resp.StatusCode}");
        else Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── Update/Delete: owner passes, non-owner 403 ──────────────────────────

    [Theory]
    [InlineData("alice", false)]
    [InlineData("bob", true)]
    public async Task Put_OwnerOnly(string identity, bool forbidden)
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddProfile<ResourceCrudProfile>());
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/ResItems(1)", identity, "{\"id\":1,\"owner\":\"alice\",\"name\":\"X\"}"));
        if (forbidden) Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        else Assert.True(Passed(resp.StatusCode), $"got {(int)resp.StatusCode}");
    }

    [Theory]
    [InlineData("alice", false)]
    [InlineData("bob", true)]
    public async Task Delete_OwnerOnly(string identity, bool forbidden)
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddProfile<ResourceCrudProfile>());
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Delete, "/odata/ResItems(1)", identity));
        if (forbidden) Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        else Assert.True(Passed(resp.StatusCode), $"got {(int)resp.StatusCode}");
    }

    // ── Create: checked against the incoming (pre-persist) entity's Owner ───

    [Theory]
    [InlineData("alice", false)] // creating a row I will own → allowed
    [InlineData("bob", true)]    // creating a row owned by "alice" → forbidden
    public async Task Post_ChecksIncomingEntity(string identity, bool forbidden)
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddProfile<ResourceCrudProfile>());
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Post, "/odata/ResItems", identity, "{\"owner\":\"alice\",\"name\":\"N\"}"));
        if (forbidden) Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        else Assert.True(Passed(resp.StatusCode), $"got {(int)resp.StatusCode}");
    }

    // ── Named-policy resource: .RequireResource("OwnerPolicy") ──────────────

    [Theory]
    [InlineData("alice", false)]
    [InlineData("bob", true)]
    public async Task RequireResourceNamedPolicy_OwnerOnly(string identity, bool forbidden)
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(
            o => o.AddProfile<ResourcePolicyProfile>(),
            policies: p => p.AddPolicy("OwnerPolicy", b => b.AddRequirements(new SameOwnerRequirement())));
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/ResPolItems(1)", identity, "{\"id\":1,\"owner\":\"alice\",\"name\":\"X\"}"));
        if (forbidden) Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        else Assert.True(Passed(resp.StatusCode), $"got {(int)resp.StatusCode}");
    }

    // ── Fail-closed: no handler registered → even the owner is denied (403) ──

    [Fact]
    public async Task NoHandlerRegistered_FailsClosed()
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(
            o => o.AddProfile<ResourceCrudProfile>(), registerHandler: false);
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Get, "/odata/ResItems(1)", "alice"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── Missing entity → 404 before the auth check can succeed ──────────────

    [Fact]
    public async Task MissingEntity_Returns404()
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddProfile<ResourceCrudProfile>());
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Get, "/odata/ResItems(999)", "alice"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Startup guard: resource write without GetById throws ────────────────

    [Fact]
    public async Task ResourceWrite_WithoutGetById_ThrowsAtStartup()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddProfile<ResourceNoGetByIdProfile>()));
    }
}
