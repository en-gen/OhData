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
using OhData;
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
        GetAll = ct => OhDataResult.Success<IEnumerable<ResOwnedItem>>(store);
        GetById = (id, ct) => OhDataResult.Success(store.FirstOrDefault(x => x.Id == id));
        Post = (m, ct) => { m.Id = 99; store.Add(m); return OhDataResult.Success<ResOwnedItem>(m); };
        Put = (id, m, ct) => { m.Id = id; return OhDataResult.Success(m); };
        Patch = (id, delta, ct) =>
        {
            var e = store.FirstOrDefault(x => x.Id == id);
            if (e is not null) delta.Patch(e);
            return OhDataResult.Success(e);
        };
        Delete = (id, ct) => OhDataResult.Success(store.RemoveAll(x => x.Id == id) > 0);
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

internal sealed class ResourceInvokeProfile : ResProfileBase
{
    public ResourceInvokeProfile()
    {
        EntitySetName = "ResInvoke";
        BindEntityFunction(GetTag);
        ConfigureAuthorization(a => a.Invoke(i => i.RequireResource()));
    }

    private Task<string> GetTag(int key) => Task.FromResult("tag");
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
        Put = (id, m, ct) => { m.Id = id; return OhDataResult.Success(m); };
        ConfigureAuthorization(a => a.Update(u => u.RequireResource()));
    }
}

// ── #526: the KEY-BASED navigation-POST create route (POST /{Set}({key})/{Nav}) still runs
// its Layer B resource filter after `ApplyOperationAuth`'s `keyBased` parameter was corrected
// to `false` on the (non-key-based) COLLECTION POST call site. Separate fixtures from
// ResOwnedItem/ResOwnedItemHandler above: a nav-post-capable parent, and its own handler, so
// this test cannot pass by accident through a fixture some other test already exercises.

internal sealed class NavCreateParent
{
    public int Id { get; set; }
    public string Owner { get; set; } = "";
    public List<NavCreateChild> Notes { get; set; } = new();
}

internal sealed class NavCreateChild
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
}

internal sealed class NavCreateParentHandler : AuthorizationHandler<OperationAuthorizationRequirement, NavCreateParent>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, OperationAuthorizationRequirement req, NavCreateParent item)
    {
        string? user = ctx.User.Identity?.Name;
        if (user is not null && item.Owner == user)
            ctx.Succeed(req);
        return Task.CompletedTask;
    }
}

internal sealed class ResourceNavCreateProfile : EntitySetProfile<int, NavCreateParent>
{
    public ResourceNavCreateProfile() : base(x => x.Id)
    {
        EntitySetName = "ResNavCreate";
        var store = new List<NavCreateParent> { new() { Id = 1, Owner = "alice" } };
        GetById = (id, ct) => OhDataResult.Success(store.FirstOrDefault(x => x.Id == id));
        HasMany(x => x.Notes,
            getAll: null,
            post: (key, child, ct) => Task.FromResult<NavCreateChild?>(child));
        ConfigureAuthorization(a => a.Create(c => c.RequireResource()));
    }
}

internal static class ResourceAuthTestHost
{
    public static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        bool registerHandler = true,
        Action<AuthorizationOptions>? policies = null,
        Action<IServiceCollection>? extraServices = null)
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
        extraServices?.Invoke(builder.Services);

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
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<ResourceCrudProfile>());
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
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<ResourceCrudProfile>());
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/ResItems(1)", identity, "{\"id\":1,\"owner\":\"alice\",\"name\":\"X\"}"));
        if (forbidden) Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        else Assert.True(Passed(resp.StatusCode), $"got {(int)resp.StatusCode}");
    }

    [Theory]
    [InlineData("alice", false)]
    [InlineData("bob", true)]
    public async Task Delete_OwnerOnly(string identity, bool forbidden)
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<ResourceCrudProfile>());
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
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<ResourceCrudProfile>());
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
            o => o.AddEntitySetProfile<ResourcePolicyProfile>(),
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
            o => o.AddEntitySetProfile<ResourceCrudProfile>(), registerHandler: false);
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Get, "/odata/ResItems(1)", "alice"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── Missing entity → 404 before the auth check can succeed ──────────────

    [Fact]
    public async Task MissingEntity_Returns404()
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<ResourceCrudProfile>());
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Get, "/odata/ResItems(999)", "alice"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Bad key format on a resource-checked route → 400 (filter parse guard) ─

    [Fact]
    public async Task BadKeyFormat_Returns400()
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<ResourceCrudProfile>());
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Get, "/odata/ResItems(not-an-int)", "alice"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Entity-bound Invoke is resource-checked (built-in Invoke requirement) ─

    [Theory]
    [InlineData("alice", true)]
    [InlineData("bob", false)]
    public async Task EntityBoundInvoke_OwnerOnly(string identity, bool passes)
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<ResourceInvokeProfile>());
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Get, "/odata/ResInvoke(1)/GetTag", identity));
        if (passes) Assert.True(Passed(resp.StatusCode), $"got {(int)resp.StatusCode}");
        else Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── Startup guard: resource write without GetById throws ────────────────

    [Fact]
    public async Task ResourceWrite_WithoutGetById_ThrowsAtStartup()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<ResourceNoGetByIdProfile>()));
    }

    // ── #526: the collection POST's Create resource check is unaffected ─────
    //
    // #526 corrected `ApplyOperationAuth`'s `keyBased` argument to `false` on the collection
    // POST call site. That argument only controls whether AttachResourceFilter's endpoint
    // filter is attached, and the filter is a provable per-request no-op on this route in
    // EITHER case: it reads RouteValues["key"], which a route with no {key} segment never
    // populates, so it always falls through to `next` regardless of keyBased. The collection
    // POST's actual Create check is `CheckResourceAuthAsync`, called inline against the
    // deserialized model further up in the handler, and is untouched by this fix.
    //
    // Post_ChecksIncomingEntity above already exercises that inline check end to end (alice
    // creating her own row passes, bob creating alice's row is forbidden) and is unchanged by
    // the fix -- there is no behavior for a black-box HTTP test to distinguish before/after,
    // so no new collection-POST test is added here. What IS worth pinning is the route this
    // fix does NOT touch: the KEY-BASED navigation-POST create route, which still needs
    // AttachResourceFilter's real endpoint filter to work. See
    // NavigationPostCreate_OwnerOnly below.

    // ── #526 regression guard: the KEY-BASED nav-POST create route (unaffected by this fix,
    // keyBased stays true there) still resource-checks the PARENT loaded by
    // AttachResourceFilter's endpoint filter before the handler runs ──────────

    [Theory]
    [InlineData("alice", true)]
    [InlineData("bob", false)]
    [InlineData(null, false)]
    public async Task NavigationPostCreate_OwnerOnly(string? identity, bool passes)
    {
        await using var fx = await ResourceAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<ResourceNavCreateProfile>(),
            extraServices: s => s.AddScoped<IAuthorizationHandler, NavCreateParentHandler>());
        var resp = await fx.Client.SendAsync(
            Req(HttpMethod.Post, "/odata/ResNavCreate(1)/Notes", identity, "{\"text\":\"hi\"}"));
        if (passes) Assert.True(Passed(resp.StatusCode), $"got {(int)resp.StatusCode}");
        else Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
