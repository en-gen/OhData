using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

internal sealed class UpsertAnonCreateProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertAnonCreateProfile()
    {
        EntitySetName = "UpsertAnonCreate";
        ConfigureAuthorization(a => a
            .Create(c => c.AllowAnonymous())
            .Update(u => u.AllowAnonymous()));
    }
}

internal sealed class UpsertAnonNoCreateOtherProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertAnonNoCreateOtherProfile()
    {
        EntitySetName = "UpsertAnonNoCreate";
        ConfigureAuthorization(a => a.Update(u => u.AllowAnonymous()));
    }
}

// Decides on the endpoint the HttpContext carries, as a handler reading endpoint metadata does.
internal sealed class UpsertEndpointMethodRequirement : IAuthorizationRequirement { }

internal static class UpsertEndpointProbe
{
    public static string? Method(HttpContext http) =>
        http.GetEndpoint()?.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault();
}

// Per-host record of the endpoint method seen at a point in a request.
internal sealed class UpsertEndpointRecorder
{
    public readonly List<string> AtResourceCheck = [];
    public readonly List<string> AfterRequest = [];
}

// Reads the endpoint while the policy provider resolves a dynamic policy, as the middleware's
// provider call does with the endpoint being authorized on the context.
internal sealed class UpsertEndpointAwarePolicyProvider : DefaultAuthorizationPolicyProvider
{
    private readonly IHttpContextAccessor _accessor;

    public UpsertEndpointAwarePolicyProvider(
        Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options, IHttpContextAccessor accessor) : base(options) =>
        _accessor = accessor;

    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName != "DynCreate") return base.GetPolicyAsync(policyName);
        var builder = new AuthorizationPolicyBuilder();
        if (UpsertEndpointProbe.Method(_accessor.HttpContext!) == "POST") builder.RequireAuthenticatedUser();
        else builder.RequireRole("Nobody");
        return Task.FromResult<AuthorizationPolicy?>(builder.Build());
    }
}

// Authenticates only while the endpoint on the context is the collection POST.
internal sealed class UpsertEndpointAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "UpsertEndpointScheme";

    public UpsertEndpointAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder) : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (UpsertEndpointProbe.Method(Context) != "POST") return Task.FromResult(AuthenticateResult.NoResult());
        var id = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(id), SchemeName)));
    }
}

// A scheme whose ticket carries a property, and a handler that reads it back through the
// authenticate-result feature, the way a tenant-aware requirement handler would.
internal sealed class UpsertTicketAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "UpsertTicketScheme";
    public static readonly DateTimeOffset Expiry = new(2090, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public UpsertTicketAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder) : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "tenant-user")], SchemeName);
        var props = new AuthenticationProperties { ExpiresUtc = Expiry };
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(id), props, SchemeName)));
    }
}

internal sealed class UpsertTicketRequirement : IAuthorizationRequirement { }

internal sealed class UpsertTicketHandler : AuthorizationHandler<UpsertTicketRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext ctx, UpsertTicketRequirement req)
    {
        if (ctx.Resource is HttpContext http
            && http.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc
                == UpsertTicketAuthHandler.Expiry)
        {
            ctx.Succeed(req);
        }
        return Task.CompletedTask;
    }
}

internal sealed class UpsertTicketProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertTicketProfile()
    {
        EntitySetName = "UpsertTicket";
        ConfigureAuthorization(a => a
            .Create(c => c.RequirePolicy("TicketPolicy"))
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

internal sealed class UpsertTicketAnonUpdateProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertTicketAnonUpdateProfile()
    {
        EntitySetName = "UpsertTicketAnon";
        ConfigureAuthorization(a => a
            .Create(c => c.RequirePolicy("TicketPolicy"))
            .Update(u => u.AllowAnonymous()));
    }
}

internal sealed class UpsertEndpointMethodHandler : AuthorizationHandler<UpsertEndpointMethodRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext ctx, UpsertEndpointMethodRequirement req)
    {
        if (ctx.Resource is HttpContext http && UpsertEndpointProbe.Method(http) == "POST") ctx.Succeed(req);
        return Task.CompletedTask;
    }
}

// Records the endpoint seen when the Create resource check runs, which is after the coarse gate.
internal sealed class UpsertEndpointRecordingResourceHandler : AuthorizationHandler<OperationAuthorizationRequirement, UpsertItem>
{
    private readonly IHttpContextAccessor _accessor;
    private readonly UpsertEndpointRecorder _recorder;

    public UpsertEndpointRecordingResourceHandler(IHttpContextAccessor accessor, UpsertEndpointRecorder recorder)
    {
        _accessor = accessor;
        _recorder = recorder;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, OperationAuthorizationRequirement req, UpsertItem item)
    {
        _recorder.AtResourceCheck.Add(UpsertEndpointProbe.Method(_accessor.HttpContext!) ?? "none");
        ctx.Succeed(req);
        return Task.CompletedTask;
    }
}

internal sealed class UpsertEndpointResultHandler : IAuthorizationMiddlewareResultHandler
{
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (!result.Forbidden) return next(context);
        context.Response.StatusCode = UpsertEndpointProbe.Method(context) == "POST" ? 418 : 419;
        return Task.CompletedTask;
    }
}

internal sealed class UpsertJsonFaultRequirement : IAuthorizationRequirement { }

internal sealed class UpsertJsonFaultHandler : AuthorizationHandler<UpsertJsonFaultRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext ctx, UpsertJsonFaultRequirement req) =>
        throw new System.Text.Json.JsonException("policy dependency failed");
}

internal sealed class UpsertEndpointPolicyProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertEndpointPolicyProfile()
    {
        EntitySetName = "UpsertEndpointPolicy";
        ConfigureAuthorization(a => a
            .Create(c => c.RequirePolicy("EndpointIsPost"))
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

internal sealed class UpsertEndpointResourceProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertEndpointResourceProfile()
    {
        EntitySetName = "UpsertEndpointRes";
        // A coarse requirement is needed too: with RequireResource() alone the gate has no policy and never swaps.
        ConfigureAuthorization(a => a
            .Create(c => c.RequireAuthenticatedUser().RequireResource())
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

internal sealed class UpsertDynPolicyProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertDynPolicyProfile()
    {
        EntitySetName = "UpsertDynPolicy";
        ConfigureAuthorization(a => a
            .Create(c => c.RequirePolicy("DynCreate"))
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

internal sealed class UpsertSchemePolicyProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertSchemePolicyProfile()
    {
        EntitySetName = "UpsertSchemePolicy";
        ConfigureAuthorization(a => a
            .Create(c => c.RequirePolicy("EndpointScheme"))
            .Update(u => u.RequireAuthenticatedUser()));
    }
}

internal sealed class UpsertJsonFaultProfile : UpsertProfileBase
{
    public static readonly int[] C = new int[2];
    protected override int[] Counters => C;
    public UpsertJsonFaultProfile()
    {
        EntitySetName = "UpsertJsonFault";
        ConfigureAuthorization(a => a
            .Create(c => c.RequirePolicy("JsonFault"))
            .Update(u => u.RequireAuthenticatedUser()));
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

    // AuthorizationMiddleware evaluates a policy with the endpoint being authorized on the
    // HttpContext, so a handler reading endpoint metadata must see the collection POST's.
    [Fact]
    public async Task P6_PolicyReadsEndpointMetadata_CreateSeesThePostEndpoint()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o =>
        {
            o.AddEntitySetProfile<UpsertEndpointPolicyProfile>();
            o.Services.AddSingleton<IAuthorizationHandler, UpsertEndpointMethodHandler>();
        }, policies: p => p.AddPolicy("EndpointIsPost", b => b.AddRequirements(new UpsertEndpointMethodRequirement())));
        await AssertSameVerdictAsync(fx, "UpsertEndpointPolicy", UpsertEndpointPolicyProfile.C, HttpStatusCode.Created, "bob", null);
    }

    [Fact]
    public async Task ResultHandlerReadsEndpointMetadata_CreateDenialSeesThePostEndpoint()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o =>
        {
            o.AddEntitySetProfile<UpsertCreateRoleProfile>();
            o.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, UpsertEndpointResultHandler>();
        });
        await AssertSameVerdictAsync(fx, "UpsertRole", UpsertCreateRoleProfile.C, (HttpStatusCode)418, "bob", null);
    }

    // The gate swaps the endpoint only for its own evaluation; the resource check and the Post
    // handler that follow still run on the PUT endpoint.
    [Fact]
    public async Task EndpointIsRestoredToThePut_AfterTheGate()
    {
        var recorder = new UpsertEndpointRecorder();
        await using var fx = await BuildRecordingHostAsync(o =>
        {
            o.AddEntitySetProfile<UpsertEndpointResourceProfile>();
            o.Services.AddSingleton<IAuthorizationHandler, UpsertEndpointRecordingResourceHandler>();
        }, recorder);
        Array.Clear(UpsertEndpointResourceProfile.C);

        var put = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertEndpointRes(93001)", "bob", null, Body(93001)));

        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal(1, UpsertEndpointResourceProfile.C[0]);
        Assert.Equal(["PUT"], recorder.AtResourceCheck);
        Assert.Equal(["PUT"], recorder.AfterRequest);
    }

    // The gate throws mid-evaluation (the POST swap is in place); the request must leave on the PUT endpoint.
    [Fact]
    public async Task EndpointIsRestoredToThePut_WhenTheGateThrows()
    {
        var recorder = new UpsertEndpointRecorder();
        await using var fx = await BuildRecordingHostAsync(
            o =>
            {
                o.AddEntitySetProfile<UpsertJsonFaultProfile>();
                o.Services.AddSingleton<IAuthorizationHandler, UpsertJsonFaultHandler>();
            },
            recorder,
            policies: p => p.AddPolicy("JsonFault", b => b.AddRequirements(new UpsertJsonFaultRequirement())));
        Array.Clear(UpsertJsonFaultProfile.C);

        var put = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertJsonFault(93002)", "bob", null, Body(93002)));

        Assert.Equal(HttpStatusCode.InternalServerError, put.StatusCode);
        Assert.Equal(0, UpsertJsonFaultProfile.C[0]);
        Assert.Equal(["PUT"], recorder.AfterRequest);
    }

    // A denial short-circuits through the result handler; the PUT endpoint must still come back.
    [Fact]
    public async Task EndpointIsRestoredToThePut_WhenTheGateDenies()
    {
        var recorder = new UpsertEndpointRecorder();
        await using var fx = await BuildRecordingHostAsync(o => o.AddEntitySetProfile<UpsertCreateRoleProfile>(), recorder);
        Array.Clear(UpsertCreateRoleProfile.C);

        var put = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertRole(93003)", "bob", null, Body(93003)));

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(["PUT"], recorder.AfterRequest);
    }

    // The middleware resolves the policy with the endpoint being authorized on the context, so a
    // provider that reads it (a dynamic policy) must see the collection POST's.
    [Fact]
    public async Task DynamicPolicyProviderReadsEndpoint_CreateSeesThePostEndpoint()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o =>
        {
            o.AddEntitySetProfile<UpsertDynPolicyProfile>();
            o.Services.AddHttpContextAccessor();
            o.Services.AddSingleton<IAuthorizationPolicyProvider, UpsertEndpointAwarePolicyProvider>();
        });
        await AssertSameVerdictAsync(fx, "UpsertDynPolicy", UpsertDynPolicyProfile.C, HttpStatusCode.Created, "bob", null);
    }

    // The Create policy names its own scheme; authentication runs with the POST endpoint on the context.
    [Fact]
    public async Task AuthenticationHandlerReadsEndpoint_CreateAuthenticatesAsThePost()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(
            o =>
            {
                o.AddEntitySetProfile<UpsertSchemePolicyProfile>();
                o.Services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, UpsertEndpointAuthHandler>(UpsertEndpointAuthHandler.SchemeName, _ => { });
            },
            policies: p => p.AddPolicy("EndpointScheme", b => b
                .AddAuthenticationSchemes(UpsertEndpointAuthHandler.SchemeName).RequireRole("Admin")));
        await AssertSameVerdictAsync(fx, "UpsertSchemePolicy", UpsertSchemePolicyProfile.C, HttpStatusCode.Created, "bob", null);
    }

    private static void AddTicketScheme(OhDataBuilder o)
    {
        o.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, UpsertTicketAuthHandler>(UpsertTicketAuthHandler.SchemeName, _ => { });
        o.Services.AddSingleton<IAuthorizationHandler, UpsertTicketHandler>();
    }

    private static void TicketPolicy(AuthorizationOptions p) =>
        p.AddPolicy("TicketPolicy", b => b
            .AddAuthenticationSchemes(UpsertTicketAuthHandler.SchemeName)
            .AddRequirements(new UpsertTicketRequirement()));

    // UseAuthentication ran, so the authenticate-result feature already exists when the gate runs.
    [Fact]
    public async Task CreateTicketReadViaAuthenticateResultFeature_FeaturePresent_MatchesPost()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(
            o => { o.AddEntitySetProfile<UpsertTicketProfile>(); AddTicketScheme(o); }, policies: TicketPolicy);
        await AssertSameVerdictAsync(fx, "UpsertTicket", UpsertTicketProfile.C, HttpStatusCode.Created, "bob", null);
    }

    // No UseAuthentication: nothing registered the feature, so the gate must create it as the middleware does.
    [Fact]
    public async Task CreateTicketReadViaAuthenticateResultFeature_FeatureAbsent_MatchesPost()
    {
        await using var fx = await BuildRecordingHostAsync(
            o => { o.AddEntitySetProfile<UpsertTicketAnonUpdateProfile>(); AddTicketScheme(o); },
            new UpsertEndpointRecorder(), policies: TicketPolicy, useAuthenticationMiddleware: false);
        await AssertSameVerdictAsync(fx, "UpsertTicketAnon", UpsertTicketAnonUpdateProfile.C, HttpStatusCode.Created, null, null);
    }

    // PerOpAuthTestHost plus a middleware that records the endpoint the request leaves with.
    private static async Task<TestFixture> BuildRecordingHostAsync(
        Action<OhDataBuilder> configure, UpsertEndpointRecorder recorder, Action<AuthorizationOptions>? policies = null,
        bool useAuthenticationMiddleware = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(recorder);
        builder.Services
            .AddAuthentication(PerOpAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PerOpAuthHandler>(PerOpAuthHandler.SchemeName, _ => { });
        if (policies is not null) builder.Services.AddAuthorization(policies);
        else builder.Services.AddAuthorization();
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            configure(o);
        });
        var app = builder.Build();
        if (useAuthenticationMiddleware) app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (ctx, next) =>
        {
            await next();
            recorder.AfterRequest.Add(UpsertEndpointProbe.Method(ctx) ?? "none");
        });
        app.MapOhData();
        await app.StartAsync();
        return new TestFixture(app);
    }

    // A JsonException from a policy handler is a server fault on the POST (the middleware does not
    // catch it); on the PUT create it must not be reported as a malformed body.
    [Fact]
    public async Task PolicyHandlerThrowingJsonException_PutCreateIsAServerFault_NotInvalidBody()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o =>
        {
            o.AddEntitySetProfile<UpsertJsonFaultProfile>();
            o.Services.AddSingleton<IAuthorizationHandler, UpsertJsonFaultHandler>();
        }, policies: p => p.AddPolicy("JsonFault", b => b.AddRequirements(new UpsertJsonFaultRequirement())));
        Array.Clear(UpsertJsonFaultProfile.C);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            fx.Client.SendAsync(Req(HttpMethod.Post, "/odata/UpsertJsonFault", "bob", null, Body(94001))));

        var put = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertJsonFault(94002)", "bob", null, Body(94002)));
        string text = await put.Content.ReadAsStringAsync();
        Assert.True(put.StatusCode == HttpStatusCode.InternalServerError, $"{(int)put.StatusCode} {text}");
        Assert.DoesNotContain("InvalidBody", text);
        Assert.Equal(0, UpsertJsonFaultProfile.C[0]);
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

    [Fact]
    public async Task TwoProfiles_EachUsesOwnPostGate()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o =>
        {
            o.AddEntitySetProfile<UpsertAnonUpdateProfile>();
            o.AddEntitySetProfile<UpsertAnonAdminCreateProfile>();
        });
        var a = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertAnon(5001)", "bob", null, Body(5001)));
        var b = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertAnonAdmin(5002)", "bob", null, Body(5002)));
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, b.StatusCode);
    }

    [Fact]
    public async Task NamedRegistrations_EachUsesOwnGroup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(PerOpAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PerOpAuthHandler>(PerOpAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddOhData("a", o => { o.WithPrefix("/a"); o.AddEntitySetProfile<UpsertAnonNoCreateProfile>(); });
        builder.Services.AddOhData("b", o => { o.WithPrefix("/b"); o.AddEntitySetProfile<UpsertAnonNoCreateOtherProfile>(); });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOhData("a").RequireAuthorization();
        app.MapOhData("b");
        await app.StartAsync();
        await using var fx = new TestFixture(app);
        var putB = await fx.Client.SendAsync(Req(HttpMethod.Put, "/b/UpsertAnonNoCreate(6002)", null, null, Body(6002)));
        var putA = await fx.Client.SendAsync(Req(HttpMethod.Put, "/a/UpsertAnonNoCreate(6004)", null, null, Body(6004)));
        Assert.Equal(HttpStatusCode.Created, putB.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, putA.StatusCode);
    }

    [Fact]
    public async Task CreateAllowAnonymous_GroupAuth_MatchesPost()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<UpsertAnonCreateProfile>(), groupConfigure: g => g.RequireAuthorization());
        var post = await fx.Client.SendAsync(Req(HttpMethod.Post, "/odata/UpsertAnonCreate", null, null, Body(7001)));
        var put = await fx.Client.SendAsync(Req(HttpMethod.Put, "/odata/UpsertAnonCreate(7002)", null, null, Body(7002)));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
    }

    // UseAuthorization before UseRouting never sets the endpoint-invoked key, so the PUT create
    // must fail closed the way EndpointMiddleware does for the POST instead of skipping the gate.
    [Fact]
    public async Task Misordered_AuthBeforeRouting_CreateFailsClosed_PostNotReached()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(PerOpAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PerOpAuthHandler>(PerOpAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddOhData(o => { o.WithPrefix("/odata"); o.AddEntitySetProfile<UpsertAnonAdminCreateProfile>(); });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRouting();
        app.MapOhData();
        await app.StartAsync();
        await using var fx = new TestFixture(app);
        Array.Clear(UpsertAnonAdminCreateProfile.C);

        async Task<HttpStatusCode?> Send(HttpMethod m, string path, int id)
        {
            try { return (await fx.Client.SendAsync(Req(m, path, null, null, Body(id)))).StatusCode; }
            catch (InvalidOperationException) { return null; }
        }

        HttpStatusCode? post = await Send(HttpMethod.Post, "/odata/UpsertAnonAdmin", 9101);
        HttpStatusCode? put = await Send(HttpMethod.Put, "/odata/UpsertAnonAdmin(9102)", 9102);
        Assert.NotEqual(HttpStatusCode.Created, post);
        Assert.NotEqual(HttpStatusCode.Created, put);
        Assert.Equal(0, UpsertAnonAdminCreateProfile.C[0]);
    }

    private sealed class ExceptionCapture : ILoggerProvider, ILogger
    {
        public readonly List<string> Messages = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) lock (Messages) Messages.Add(exception.Message);
        }
    }

    private static async Task<TestFixture> BuildMisorderedAsync<TProfile>(
        Action<RouteGroupBuilder>? group = null, bool suppressUnhandledSecurityCheck = false,
        ExceptionCapture? capture = null)
        where TProfile : class, IEntitySetProfile
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(PerOpAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PerOpAuthHandler>(PerOpAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        if (capture is not null) builder.Logging.AddProvider(capture);
        if (suppressUnhandledSecurityCheck)
            builder.Services.Configure<RouteOptions>(o => o.SuppressCheckForUnhandledSecurityMetadata = true);
        builder.Services.AddOhData(o => { o.WithPrefix("/odata"); o.AddEntitySetProfile<TProfile>(); });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRouting();
        group?.Invoke(app.MapOhData());
        if (group is null) app.MapOhData();
        await app.StartAsync();
        return new TestFixture(app);
    }

    private static async Task<(string? Message, HttpStatusCode? Status)> SendCatchingAsync(
        TestFixture fx, HttpMethod m, string path, int id)
    {
        try
        {
            var r = await fx.Client.SendAsync(Req(m, path, null, null, Body(id)));
            return (await r.Content.ReadAsStringAsync(), r.StatusCode);
        }
        catch (InvalidOperationException ex) { return (ex.Message, null); }
    }

    // Upstream EndpointMiddleware checks IAuthorizeData only; the gate also fails closed on an
    // AuthorizationPolicy-only endpoint, which WithMetadata(policy) produces (no IAuthorizeData).
    [Fact]
    public async Task Misordered_PolicyOnlyMetadata_CreateFailsClosed_PostNotReached()
    {
        var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        var capture = new ExceptionCapture();
        await using var fx = await BuildMisorderedAsync<UpsertAnonNoCreateProfile>(g => g.WithMetadata(policy), capture: capture);
        Array.Clear(UpsertAnonNoCreateProfile.C);

        var put = await SendCatchingAsync(fx, HttpMethod.Put, "/odata/UpsertAnonNoCreate(9202)", 9202);

        Assert.Equal(HttpStatusCode.InternalServerError, put.Status);
        Assert.Contains(capture.Messages, m => m.Contains("contains authorization metadata, but a middleware was not found"));
        Assert.Equal(0, UpsertAnonNoCreateProfile.C[0]);
    }

    [Fact]
    public async Task Misordered_ThrowMessageMatchesEndpointMiddleware()
    {
        var capture = new ExceptionCapture();
        await using var fx = await BuildMisorderedAsync<UpsertAnonAdminCreateProfile>(capture: capture);
        var put = await SendCatchingAsync(fx, HttpMethod.Put, "/odata/UpsertAnonAdmin(9302)", 9302);

        Assert.Equal(HttpStatusCode.InternalServerError, put.Status);
        string thrown = Assert.Single(capture.Messages, m => m.Contains("contains authorization metadata"));
        Assert.EndsWith(
            " contains authorization metadata, but a middleware was not found that supports authorization."
            + Environment.NewLine
            + "Configure your application startup by adding app.UseAuthorization() in the application startup code. "
            + "If there are calls to app.UseRouting() and app.UseEndpoints(...), the call to app.UseAuthorization() must go between them.",
            thrown);
    }

    // With the host's opt-out set, EndpointMiddleware lets the POST through, and the PUT create follows it.
    [Fact]
    public async Task Misordered_SuppressUnhandledSecurityCheck_PutCreateMatchesPost()
    {
        await using var fx = await BuildMisorderedAsync<UpsertAnonAdminCreateProfile>(suppressUnhandledSecurityCheck: true);

        var post = await SendCatchingAsync(fx, HttpMethod.Post, "/odata/UpsertAnonAdmin", 9401);
        var put = await SendCatchingAsync(fx, HttpMethod.Put, "/odata/UpsertAnonAdmin(9402)", 9402);

        Assert.Equal(HttpStatusCode.Created, post.Status);
        Assert.Equal(post.Status, put.Status);
    }
}
