using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #795: the create branch of an upsert PUT (Put returned null, AllowUpsert, HasPost) runs the Post
// handler, so it must satisfy the Create rule (coarse + resource) as well as the Update route rule.

internal sealed class UpsertItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class UpsertCreateResourceHandler : AuthorizationHandler<OperationAuthorizationRequirement, UpsertItem>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, OperationAuthorizationRequirement req, UpsertItem item)
    {
        if (req.Name == OhDataOperations.Create.Name && item.Name != "deny") ctx.Succeed(req);
        return Task.CompletedTask;
    }
}

internal abstract class UpsertProfileBase : EntitySetProfile<int, UpsertItem>
{
    protected abstract int[] Counters { get; } // [0] = Post calls, [1] = Put calls

    protected UpsertProfileBase() : base(x => x.Id)
    {
        AllowUpsert = true;
        var store = new List<UpsertItem> { new() { Id = 1, Name = "one" } };
        GetById = (id, ct) => OhDataResult.Success(store.FirstOrDefault(w => w.Id == id));
        Post = (m, ct) => { Interlocked.Increment(ref Counters[0]); store.Add(m); return OhDataResult.Success<UpsertItem>(m); };
        Put = (id, m, ct) =>
        {
            Interlocked.Increment(ref Counters[1]);
            var e = store.FirstOrDefault(w => w.Id == id);
            if (e is null) return OhDataResult.Success<UpsertItem?>(null!);
            e.Name = m.Name;
            return OhDataResult.Success<UpsertItem?>(e);
        };
    }
}

internal sealed class UpsertCreateRoleProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertCreateRoleProfile()
    {
        EntitySetName = "UpsertRole";
        ConfigureAuthorization(a => a
            .Create(c => c.RequireRole("Admin"))
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

internal sealed class UpsertWritesProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertWritesProfile()
    {
        EntitySetName = "UpsertWrites";
        ConfigureAuthorization(a => a
            .Writes(w => w.RequireRole("Admin"))
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

internal sealed class UpsertAllProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertAllProfile()
    {
        EntitySetName = "UpsertAll";
        ConfigureAuthorization(a => a
            .All(w => w.RequireRole("Admin"))
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

internal sealed class UpsertPolicyProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertPolicyProfile()
    {
        EntitySetName = "UpsertPolicy";
        ConfigureAuthorization(a => a
            .Create(c => c.RequirePolicy("AdminOnly"))
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

internal sealed class UpsertResourceProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertResourceProfile()
    {
        EntitySetName = "UpsertRes";
        ConfigureAuthorization(a => a
            .Create(c => c.RequireResource())
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

internal sealed class UpsertAnonUpdateProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertAnonUpdateProfile()
    {
        EntitySetName = "UpsertAnon";
        ConfigureAuthorization(a => a
            .Create(c => c.RequireAuthenticatedUser())
            .Update(u => u.AllowAnonymous()));
    }
}

internal sealed class UpsertAnonNoCreateProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertAnonNoCreateProfile()
    {
        EntitySetName = "UpsertAnonNoCreate";
        ConfigureAuthorization(a => a.Update(u => u.AllowAnonymous()));
    }
}

internal sealed class UpsertAnonAdminCreateProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertAnonAdminCreateProfile()
    {
        EntitySetName = "UpsertAnonAdmin";
        ConfigureAuthorization(a => a
            .Create(c => c.RequireRole("Admin"))
            .Update(u => u.AllowAnonymous()));
    }
}

internal sealed class UpsertNotBlockedProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertNotBlockedProfile()
    {
        EntitySetName = "UpsertNotBlocked";
        ConfigureAuthorization(a => a
            .Create(c => c.RequirePolicy("NotBlocked"))
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

internal sealed class UpsertAllowListProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertAllowListProfile()
    {
        EntitySetName = "UpsertAllowList";
        ConfigureAuthorization(a => a
            .Create(c => c.RequirePolicy("AllowListed"))
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

// Reads the request off the policy's resource, as a middleware-evaluated policy sees it.
internal sealed class UpsertHeaderRequirement : IAuthorizationRequirement
{
    public UpsertHeaderRequirement(string header, bool denyWhenPresent) { Header = header; DenyWhenPresent = denyWhenPresent; }
    public string Header { get; }
    public bool DenyWhenPresent { get; }
}

internal sealed class UpsertHeaderHandler : AuthorizationHandler<UpsertHeaderRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext ctx, UpsertHeaderRequirement req)
    {
        bool present = ctx.Resource is HttpContext http && http.Request.Headers.ContainsKey(req.Header);
        if (present != req.DenyWhenPresent) ctx.Succeed(req);
        return Task.CompletedTask;
    }
}

internal sealed class UpsertTeapotResultHandler : IAuthorizationMiddlewareResultHandler
{
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (!result.Forbidden) return next(context);
        context.Response.StatusCode = 418;
        return Task.CompletedTask;
    }
}

public class Issue795UpsertCreateRuleTests
{
    private static HttpRequestMessage Req(
        HttpMethod m, string path, string? user, string? roles, string body, params (string Name, string Value)[] headers)
    {
        var r = new HttpRequestMessage(m, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (user is not null) r.Headers.Add(PerOpAuthHandler.IdentityHeader, user);
        if (roles is not null) r.Headers.Add(PerOpAuthHandler.RolesHeader, roles);
        foreach (var (name, value) in headers) r.Headers.Add(name, value);
        return r;
    }

    private static string Body(int id, string name = "x") => $"{{\"id\":{id},\"name\":\"{name}\"}}";

    private static Task<TestFixture> Host<T>() where T : class, IEntitySetProfile =>
        PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<T>(),
            policies: p => p.AddPolicy("AdminOnly", b => b.RequireRole("Admin")));

    [Theory]
    [InlineData("UpsertRole")]
    [InlineData("UpsertWrites")]
    [InlineData("UpsertAll")]
    [InlineData("UpsertPolicy")]
    public async Task CreateRuleDenied_UpdateAllowed_MissingKeyIs403_PostNotReached_ExistingKeyUnchanged(string set)
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(
            o =>
            {
                o.AddEntitySetProfile<UpsertCreateRoleProfile>();
                o.AddEntitySetProfile<UpsertWritesProfile>();
                o.AddEntitySetProfile<UpsertAllProfile>();
                o.AddEntitySetProfile<UpsertPolicyProfile>();
            },
            policies: p => p.AddPolicy("AdminOnly", b => b.RequireRole("Admin")));
        int[] c = set switch
        {
            "UpsertRole" => UpsertCreateRoleProfile.C,
            "UpsertWrites" => UpsertWritesProfile.C,
            "UpsertAll" => UpsertAllProfile.C,
            _ => UpsertPolicyProfile.C,
        };
        Array.Clear(c);

        var resp = await fx.Client.SendAsync(Req(HttpMethod.Put, $"/odata/{set}(99999)", "bob", null, Body(99999)));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(0, c[0]);

        // Existing key: Put reached and succeeds, Post never.
        var upd = await fx.Client.SendAsync(Req(HttpMethod.Put, $"/odata/{set}(1)", "bob", null, Body(1, "changed")));
        Assert.True(upd.IsSuccessStatusCode, upd.StatusCode.ToString());
        Assert.Equal(0, c[0]);
        Assert.Equal(2, c[1]);
    }

    [Fact]
    public async Task AdminPassesBoth_MissingKeyCreates201_PostOnce()
    {
        await using var fx = await Host<UpsertCreateRoleProfile>();
        Array.Clear(UpsertCreateRoleProfile.C);
        var resp = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertRole(99999)", "alice", "Admin", Body(99999)));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(1, UpsertCreateRoleProfile.C[0]);
    }

    [Fact]
    public async Task CreateResourceRule_DeniesByModel_AllowsOtherModel()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o =>
        {
            o.AddEntitySetProfile<UpsertResourceProfile>();
            o.Services.AddSingleton<IAuthorizationHandler, UpsertCreateResourceHandler>();
        });
        Array.Clear(UpsertResourceProfile.C);

        var denied = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertRes(99999)", "bob", null, Body(99999, "deny")));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, UpsertResourceProfile.C[0]);

        var ok = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertRes(99998)", "bob", null, Body(99998, "fine")));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(1, UpsertResourceProfile.C[0]);
    }

    [Fact]
    public async Task AnonymousUpdate_CreateRequiresAuth_MissingKeyIs401_PostNotReached()
    {
        await using var fx = await Host<UpsertAnonUpdateProfile>();
        Array.Clear(UpsertAnonUpdateProfile.C);

        var resp = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertAnon(99999)", null, null, Body(99999)));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(0, UpsertAnonUpdateProfile.C[0]);

        // Existing key stays anonymous-updatable.
        var upd = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertAnon(1)", null, null, Body(1, "c")));
        Assert.True(upd.IsSuccessStatusCode, upd.StatusCode.ToString());
        Assert.Equal(0, UpsertAnonUpdateProfile.C[0]);
    }

    // The create branch must reach the same verdict the collection POST gets from the middleware.
    private static async Task AssertSameVerdictAsync(
        TestFixture fx, string set, int[] counters, HttpStatusCode expected,
        string? user, string? roles, params (string Name, string Value)[] headers)
    {
        Array.Clear(counters);
        var post = await fx.Client.SendAsync(Req(HttpMethod.Post, $"/odata/{set}", user, roles, Body(91001), headers));
        int postReached = counters[0];
        Array.Clear(counters);
        var put = await fx.Client.SendAsync(Req(HttpMethod.Put, $"/odata/{set}(91002)", user, roles, Body(91002), headers));
        Assert.Equal(expected, post.StatusCode);
        Assert.Equal(expected, put.StatusCode);
        Assert.Equal(postReached, counters[0]);
    }

    [Fact]
    public async Task P1_GroupRequireAuthorization_AnonymousUpdate_CreateIsStill401()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<UpsertAnonNoCreateProfile>(),
            groupConfigure: g => g.RequireAuthorization());
        await AssertSameVerdictAsync(fx, "UpsertAnonNoCreate", UpsertAnonNoCreateProfile.C, HttpStatusCode.Unauthorized, null, null);
    }

    [Fact]
    public async Task P2_FallbackPolicy_AnonymousUpdate_CreateIsStill401()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<UpsertAnonNoCreateProfile>(),
            policies: p => p.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        await AssertSameVerdictAsync(fx, "UpsertAnonNoCreate", UpsertAnonNoCreateProfile.C, HttpStatusCode.Unauthorized, null, null);
    }

    [Fact]
    public async Task P3_GroupPolicyAndCreateRole_AnonymousUpdate_BothApply()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<UpsertAnonAdminCreateProfile>(),
            policies: p => p.AddPolicy("Tenant", b => b.RequireClaim("tenant")),
            groupConfigure: g => g.RequireAuthorization("Tenant"));
        // Admin without the tenant claim: the group policy refuses the POST, so it must refuse the create.
        await AssertSameVerdictAsync(fx, "UpsertAnonAdmin", UpsertAnonAdminCreateProfile.C, HttpStatusCode.Forbidden, "alice", "Admin");
        // Both satisfied: the create goes through.
        await AssertSameVerdictAsync(fx, "UpsertAnonAdmin", UpsertAnonAdminCreateProfile.C, HttpStatusCode.Created, "alice", "Admin",
            (PerOpAuthHandler.ClaimsHeader, "tenant=t1"));
    }

    private static void HeaderPolicies(AuthorizationOptions p)
    {
        p.AddPolicy("NotBlocked", b => b.AddRequirements(new UpsertHeaderRequirement("X-Blocked", denyWhenPresent: true)));
        p.AddPolicy("AllowListed", b => b.AddRequirements(new UpsertHeaderRequirement("X-Allowed", denyWhenPresent: false)));
    }

    [Fact]
    public async Task P4_PolicyReadsHttpContextResource_DenyList_CreateMatchesPost()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o =>
        {
            o.AddEntitySetProfile<UpsertNotBlockedProfile>();
            o.Services.AddSingleton<IAuthorizationHandler, UpsertHeaderHandler>();
        }, policies: HeaderPolicies);
        await AssertSameVerdictAsync(fx, "UpsertNotBlocked", UpsertNotBlockedProfile.C, HttpStatusCode.Forbidden, "bob", null,
            ("X-Blocked", "1"));
        await AssertSameVerdictAsync(fx, "UpsertNotBlocked", UpsertNotBlockedProfile.C, HttpStatusCode.Created, "bob", null);
    }

    [Fact]
    public async Task P5_PolicyReadsHttpContextResource_AllowList_CreateMatchesPost()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o =>
        {
            o.AddEntitySetProfile<UpsertAllowListProfile>();
            o.Services.AddSingleton<IAuthorizationHandler, UpsertHeaderHandler>();
        }, policies: HeaderPolicies);
        await AssertSameVerdictAsync(fx, "UpsertAllowList", UpsertAllowListProfile.C, HttpStatusCode.Created, "bob", null,
            ("X-Allowed", "1"));
        await AssertSameVerdictAsync(fx, "UpsertAllowList", UpsertAllowListProfile.C, HttpStatusCode.Forbidden, "bob", null);
    }

    [Fact]
    public async Task HostResultHandler_ShapesTheCreateDenialLikeThePost()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o =>
        {
            o.AddEntitySetProfile<UpsertCreateRoleProfile>();
            o.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, UpsertTeapotResultHandler>();
        });
        await AssertSameVerdictAsync(fx, "UpsertRole", UpsertCreateRoleProfile.C, (HttpStatusCode)418, "bob", null);
    }
}
