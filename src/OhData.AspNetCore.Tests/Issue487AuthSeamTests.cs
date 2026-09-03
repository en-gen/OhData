using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

// #487 — three individually-documented authorization behaviours that COMPOSE into a quiet fail-open.
// Each was measured against the pre-fix tree before anything changed, and those measurements are the
// tests here.
//
//   SEAM 1 — unbound operations took no authorization configuration at all. Measured: a registration
//     whose only profile declares RequireAuthorization() answered 401 on its collection GET while
//     POST /odata/Mutate returned 204 WITH THE HANDLER EXECUTED. There was no way to state a
//     requirement, so the capability did not exist. CLOSED: AddFunction/AddAction take an `authorize`
//     lambda, and an unstated operation is named at startup.
//
//   SEAM 2 — a ConfigureAuthorization category with no rule fails OPEN. Measured on
//     `.Read(…).Writes(…)`, which reads as a refinement and is a WIDENING: the collection GET answered
//     401 while both action routes answered 204 with the handler executed. Made loud, not changed.
//
//   SEAM 3 — a category-level .AllowAnonymous() defeats a host-applied group requirement.
//     DELIBERATELY UNCHANGED AND UNWARNED: it is ASP.NET Core's own AllowAnonymousAttribute
//     semantics, proven by the framework control test below with no OhData in the picture, and it is
//     the only way to express a deliberate public hole. A warning would fire on correct configuration
//     with no silencer. (#572 later made both warnings say WHICH AllowAnonymous they mean.)

#region fixtures

public sealed class S487Row
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>Seam 1: the registration's only profile requires authorization, all operations.</summary>
internal sealed class S487SecuredProfile : EntitySetProfile<int, S487Row>
{
    public S487SecuredProfile() : base(x => x.Id)
    {
        EntitySetName = "S487Secured";
        RequireAuthorization();
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<S487Row>>(new[] { new S487Row { Id = 1, Name = "r" } });
        GetById = (id, _) => OhDataResult.SuccessTask<S487Row>(new S487Row { Id = id, Name = "r" });
    }
}

/// <summary>
/// The control that must keep every diagnostic silent: a registration that requires authorization
/// NOWHERE is a public service, not a service with a hole.
/// </summary>
internal sealed class S487PublicProfile : EntitySetProfile<int, S487Row>
{
    public S487PublicProfile() : base(x => x.Id)
    {
        EntitySetName = "S487Public";
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<S487Row>>(new[] { new S487Row { Id = 1, Name = "r" } });
        GetById = (id, _) => OhDataResult.SuccessTask<S487Row>(new S487Row { Id = id, Name = "r" });
    }
}

/// <summary>
/// Seam 2: the migration shape. <c>.Read(…)</c> + <c>.Writes(…)</c> reads as a refinement of
/// <c>RequireAuthorization()</c>, names no <c>Invoke</c> rule, and the profile declares bound
/// operations — so every one of them drops to anonymous.
/// </summary>
internal sealed class S487MigratedProfile : EntitySetProfile<int, S487Row>
{
    public static int StampCalls;

    public S487MigratedProfile() : base(x => x.Id)
    {
        EntitySetName = "S487Migrated";
        ConfigureAuthorization(auth => auth
            .Read(r => r.RequireAuthenticatedUser())
            .Writes(w => w.RequireAuthenticatedUser()));
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<S487Row>>(new[] { new S487Row { Id = 1, Name = "r" } });
        GetById = (id, _) => OhDataResult.SuccessTask<S487Row>(new S487Row { Id = id, Name = "r" });
        BindAction(Stamp);
        BindEntityAction(Touch);
    }

    private Task Stamp() { StampCalls++; return Task.CompletedTask; }
    private Task Touch(int key) { StampCalls++; return Task.CompletedTask; }
}

/// <summary>Seam 2's control: the same surface under the legacy all-operations model.</summary>
internal sealed class S487LegacyProfile : EntitySetProfile<int, S487Row>
{
    public S487LegacyProfile() : base(x => x.Id)
    {
        EntitySetName = "S487Legacy";
        RequireAuthorization();
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<S487Row>>(new[] { new S487Row { Id = 1, Name = "r" } });
        GetById = (id, _) => OhDataResult.SuccessTask<S487Row>(new S487Row { Id = id, Name = "r" });
        BindAction(LegacyStamp);
    }

    private Task LegacyStamp() => Task.CompletedTask;
}

/// <summary>Seam 2's remedy, applied: the same profile with the Invoke category stated.</summary>
internal sealed class S487StatedProfile : EntitySetProfile<int, S487Row>
{
    public S487StatedProfile() : base(x => x.Id)
    {
        EntitySetName = "S487Stated";
        ConfigureAuthorization(auth => auth
            .Read(r => r.RequireAuthenticatedUser())
            .Writes(w => w.RequireAuthenticatedUser())
            .Invoke(i => i.AllowAnonymous()));
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<S487Row>>(new[] { new S487Row { Id = 1, Name = "r" } });
        GetById = (id, _) => OhDataResult.SuccessTask<S487Row>(new S487Row { Id = id, Name = "r" });
        BindAction(StatedStamp);
    }

    private Task StatedStamp() => Task.CompletedTask;
}

/// <summary>Seam 3: a category-level AllowAnonymous under a host-applied group requirement.</summary>
internal sealed class S487TunnelProfile : EntitySetProfile<int, S487Row>
{
    public S487TunnelProfile() : base(x => x.Id)
    {
        EntitySetName = "S487Tunnel";
        ConfigureAuthorization(auth => auth
            .Read(r => r.AllowAnonymous())
            .Create(c => c.RequireAuthenticatedUser())
            .Update(u => u.RequireAuthenticatedUser())
            .Delete(d => d.RequireAuthenticatedUser())
            .Invoke(i => i.RequireAuthenticatedUser()));
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<S487Row>>(new[] { new S487Row { Id = 1, Name = "r" } });
        GetById = (id, _) => OhDataResult.SuccessTask<S487Row>(new S487Row { Id = id, Name = "r" });
    }
}

internal static class S487UnboundOps
{
    public static int MutateCalls;

    public static Task Mutate()
    {
        MutateCalls++;
        return Task.CompletedTask;
    }

    public static Task<int> Peek() => Task.FromResult(42);
}

#endregion

public sealed class Issue487AuthSeamTests
{
    private readonly ITestOutputHelper _out;
    public Issue487AuthSeamTests(ITestOutputHelper output) => _out = output;

    /// <summary>Every #487 warning, isolated from the other startup diagnostics.</summary>
    private static string[] SeamWarnings(WarningCapture capture) => capture.Warnings
        .Where(w => w.Contains("Nothing in this registration authorizes it", StringComparison.Ordinal))
        .ToArray();

    private static async Task<TestFixture> BuildAsync(
        WarningCapture capture,
        Action<OhDataBuilder> configure,
        Action<IEndpointConventionBuilder>? groupConfigure = null,
        Action<AuthorizationOptions>? policies = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(l => l.AddProvider(capture));
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
        app.UseAuthentication();
        app.UseAuthorization();
        var group = app.MapOhData();
        groupConfigure?.Invoke(group);
        await app.StartAsync();
        return new TestFixture(app);
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, string? body = null, string? identity = null, string? roles = null)
    {
        var r = new HttpRequestMessage(method, path);
        if (body is not null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (identity is not null) r.Headers.Add(PerOpAuthHandler.IdentityHeader, identity);
        if (roles is not null) r.Headers.Add(PerOpAuthHandler.RolesHeader, roles);
        return r;
    }

    // ── Seam 1 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The measurement, kept as a pin: the REQUEST-PATH behaviour of an unstated unbound operation
    /// is unchanged by this fix. Only the diagnostic is new. Without this the warning tests below
    /// could pass over a silently-broken surface.
    /// </summary>
    [Fact]
    public async Task Seam1_EveryProfileRequiresAuth_UnboundOperationsStillServeAnonymously()
    {
        S487UnboundOps.MutateCalls = 0;
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(capture, o =>
        {
            o.AddEntitySetProfile<S487SecuredProfile>();
            o.AddAction(S487UnboundOps.Mutate);
            o.AddFunction(S487UnboundOps.Peek);
        });

        var control = await fx.Client.GetAsync("/odata/S487Secured");
        var action = await fx.Client.PostAsync("/odata/Mutate", new StringContent("{}", Encoding.UTF8, "application/json"));
        var function = await fx.Client.GetAsync("/odata/Peek");

        _out.WriteLine($"CONTROL GET  /odata/S487Secured -> {(int)control.StatusCode}");
        _out.WriteLine($"SEAM1   POST /odata/Mutate      -> {(int)action.StatusCode}, handler ran {S487UnboundOps.MutateCalls}x");
        _out.WriteLine($"SEAM1   GET  /odata/Peek        -> {(int)function.StatusCode}, body={await function.Content.ReadAsStringAsync()}");

        Assert.Equal(HttpStatusCode.Unauthorized, control.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, action.StatusCode);
        Assert.Equal(1, S487UnboundOps.MutateCalls);
        Assert.Equal(HttpStatusCode.OK, function.StatusCode);
        Assert.Equal("42", await function.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Seam1_UnstatedUnboundOperations_AreNamedByAStartupWarning()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(capture, o =>
        {
            o.AddEntitySetProfile<S487SecuredProfile>();
            o.AddAction(S487UnboundOps.Mutate);
            o.AddFunction(S487UnboundOps.Peek);
        });

        string[] warnings = SeamWarnings(capture);
        foreach (string w in warnings) _out.WriteLine("WARNING " + w);

        Assert.Equal(2, warnings.Length);
        string mutate = Assert.Single(warnings, w => w.Contains("'Mutate'", StringComparison.Ordinal));
        Assert.Contains("the unbound action 'Mutate' (POST /{prefix}/Mutate) is ANONYMOUS.", mutate, StringComparison.Ordinal);
        Assert.Contains("not scoped to an entity set", mutate, StringComparison.Ordinal);
        Assert.Contains("AddAction(Mutate, a => a.RequireAuthenticatedUser())", mutate, StringComparison.Ordinal);
        Assert.Contains("AddAction(Mutate, a => a.AllowAnonymous())", mutate, StringComparison.Ordinal);

        // #572: the remedy must say WHICH AllowAnonymous() this is. On an unbound operation the
        // call deliberately does not emit AllowAnonymousAttribute, so it cannot tunnel out from
        // under a later app.MapOhData().RequireAuthorization(). The category warning prescribes
        // the identically spelled fix and gets the opposite behaviour; before #572 neither message
        // said so, and a developer could not tell them apart.
        Assert.Contains("does NOT", mutate, StringComparison.Ordinal);
        Assert.Contains("remove a host group requirement", mutate, StringComparison.Ordinal);

        string peek = Assert.Single(warnings, w => w.Contains("'Peek'", StringComparison.Ordinal));
        Assert.Contains("the unbound function 'Peek' (GET /{prefix}/Peek) is ANONYMOUS.", peek, StringComparison.Ordinal);
    }

    /// <summary>The new capability: an unbound operation can now carry its own requirement.</summary>
    [Fact]
    public async Task Seam1_AuthorizeOverload_GatesTheUnboundOperation_AndSilencesTheWarning()
    {
        S487UnboundOps.MutateCalls = 0;
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(capture, o =>
        {
            o.AddEntitySetProfile<S487SecuredProfile>();
            o.AddAction(S487UnboundOps.Mutate, a => a.RequireRole("admin"));
            o.AddFunction(S487UnboundOps.Peek, a => a.RequireAuthenticatedUser());
        });

        var anonymousAction = await fx.Client.PostAsync("/odata/Mutate", new StringContent("{}", Encoding.UTF8, "application/json"));
        var wrongRole = await fx.Client.SendAsync(Req(HttpMethod.Post, "/odata/Mutate", "{}", identity: "u", roles: "editor"));
        var rightRole = await fx.Client.SendAsync(Req(HttpMethod.Post, "/odata/Mutate", "{}", identity: "u", roles: "admin"));
        var anonymousFn = await fx.Client.GetAsync("/odata/Peek");
        var authedFn = await fx.Client.SendAsync(Req(HttpMethod.Get, "/odata/Peek", identity: "u"));

        _out.WriteLine($"POST /odata/Mutate anonymous     -> {(int)anonymousAction.StatusCode}");
        _out.WriteLine($"POST /odata/Mutate role=editor   -> {(int)wrongRole.StatusCode}");
        _out.WriteLine($"POST /odata/Mutate role=admin    -> {(int)rightRole.StatusCode}");
        _out.WriteLine($"GET  /odata/Peek   anonymous     -> {(int)anonymousFn.StatusCode}");
        _out.WriteLine($"GET  /odata/Peek   authenticated -> {(int)authedFn.StatusCode}");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousAction.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, rightRole.StatusCode);
        Assert.Equal(1, S487UnboundOps.MutateCalls); // only the authorized call reached it
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousFn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authedFn.StatusCode);
        Assert.Empty(SeamWarnings(capture));
    }

    [Fact]
    public async Task Seam1_AuthorizeOverloadWithPolicy_IsHonoured()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(
            capture,
            o =>
            {
                o.AddEntitySetProfile<S487SecuredProfile>();
                o.AddFunction(S487UnboundOps.Peek, a => a.RequirePolicy("S487Ops"));
            },
            policies: opts => opts.AddPolicy("S487Ops", p => p.RequireClaim("scope", "ops")));

        var anonymous = await fx.Client.GetAsync("/odata/Peek");
        var wrong = await fx.Client.SendAsync(Req(HttpMethod.Get, "/odata/Peek", identity: "u"));
        var right = new HttpRequestMessage(HttpMethod.Get, "/odata/Peek");
        right.Headers.Add(PerOpAuthHandler.IdentityHeader, "u");
        right.Headers.Add(PerOpAuthHandler.ClaimsHeader, "scope=ops");
        var allowed = await fx.Client.SendAsync(right);

        _out.WriteLine($"anonymous -> {(int)anonymous.StatusCode}; no claim -> {(int)wrong.StatusCode}; scope=ops -> {(int)allowed.StatusCode}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Empty(SeamWarnings(capture));
    }

    /// <summary>
    /// <c>AllowAnonymous()</c> on an unbound operation states intent and silences the warning —
    /// and, deliberately, does NOT emit <c>AllowAnonymousAttribute</c>: stating "I am not adding a
    /// requirement" must not become "I am removing the host's". That is seam 3, and an unbound
    /// operation is not going to open it.
    /// </summary>
    [Fact]
    public async Task Seam1_AllowAnonymousOnAnUnboundOperation_StatesIntentWithoutTunnellingOutOfGroupAuth()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(
            capture,
            o =>
            {
                o.AddEntitySetProfile<S487SecuredProfile>();
                o.AddFunction(S487UnboundOps.Peek, a => a.AllowAnonymous());
            },
            groupConfigure: g => g.RequireAuthorization());

        var underGroupAuth = await fx.Client.GetAsync("/odata/Peek");
        _out.WriteLine($"GET /odata/Peek under group auth -> {(int)underGroupAuth.StatusCode}");
        Assert.Equal(HttpStatusCode.Unauthorized, underGroupAuth.StatusCode);
        Assert.Empty(SeamWarnings(capture));
    }

    [Fact]
    public async Task Seam1_AllowAnonymousOnAnUnboundOperation_LeavesItAnonymousWithoutGroupAuth()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(capture, o =>
        {
            o.AddEntitySetProfile<S487SecuredProfile>();
            o.AddFunction(S487UnboundOps.Peek, a => a.AllowAnonymous());
        });

        var response = await fx.Client.GetAsync("/odata/Peek");
        _out.WriteLine($"GET /odata/Peek -> {(int)response.StatusCode}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(SeamWarnings(capture));
    }

    /// <summary>
    /// The mitigation <c>docs/authorization.md</c> recommends, applied: a group-level requirement
    /// really does cover the unbound operations, so there is no hole and nothing is reported. This
    /// is the reason the diagnostic runs from a <c>Finally</c> convention rather than inside
    /// <c>MapOhData()</c> — at map time the host has not applied it yet.
    /// </summary>
    [Fact]
    public async Task Seam1_GroupLevelAuthorization_CoversUnboundOperations_AndSilencesTheWarning()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(
            capture,
            o =>
            {
                o.AddEntitySetProfile<S487SecuredProfile>();
                o.AddAction(S487UnboundOps.Mutate);
                o.AddFunction(S487UnboundOps.Peek);
            },
            groupConfigure: g => g.RequireAuthorization());

        var action = await fx.Client.PostAsync("/odata/Mutate", new StringContent("{}", Encoding.UTF8, "application/json"));
        _out.WriteLine($"POST /odata/Mutate under group auth -> {(int)action.StatusCode}");
        Assert.Equal(HttpStatusCode.Unauthorized, action.StatusCode);
        Assert.Empty(SeamWarnings(capture));
    }

    [Fact]
    public void Seam1_RequireResourceOnAnUnboundOperation_IsRefusedAtRegistration()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddOhData(o =>
                o.AddFunction(S487UnboundOps.Peek, a => a.RequireResource())));

        _out.WriteLine(ex.Message);
        Assert.Contains("RequireResource()", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not supported for an unbound operation", ex.Message, StringComparison.Ordinal);
    }

    // ── Seam 2 ─────────────────────────────────────────────────────────────

    /// <summary>The measurement, kept as a pin — again, no request-path behaviour changed.</summary>
    [Fact]
    public async Task Seam2_OmittedInvokeCategory_LeavesEveryBoundOperationAnonymous()
    {
        S487MigratedProfile.StampCalls = 0;
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(capture, o =>
        {
            o.AddEntitySetProfile<S487MigratedProfile>();
            o.AddEntitySetProfile<S487LegacyProfile>();
        });

        var read = await fx.Client.GetAsync("/odata/S487Migrated");
        var collAction = await fx.Client.PostAsync("/odata/S487Migrated/Stamp", new StringContent("{}", Encoding.UTF8, "application/json"));
        var entityAction = await fx.Client.PostAsync("/odata/S487Migrated(1)/Touch", new StringContent("{}", Encoding.UTF8, "application/json"));
        var legacy = await fx.Client.PostAsync("/odata/S487Legacy/LegacyStamp", new StringContent("{}", Encoding.UTF8, "application/json"));

        _out.WriteLine($"CONTROL GET  /odata/S487Migrated          -> {(int)read.StatusCode}");
        _out.WriteLine($"SEAM2   POST /odata/S487Migrated/Stamp    -> {(int)collAction.StatusCode}");
        _out.WriteLine($"SEAM2   POST /odata/S487Migrated(1)/Touch -> {(int)entityAction.StatusCode}");
        _out.WriteLine($"CONTROL POST /odata/S487Legacy/LegacyStamp -> {(int)legacy.StatusCode} (legacy RequireAuthorization)");
        _out.WriteLine($"handler ran {S487MigratedProfile.StampCalls}x");

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, legacy.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, collAction.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, entityAction.StatusCode);
        Assert.Equal(2, S487MigratedProfile.StampCalls);
    }

    [Fact]
    public async Task Seam2_RuleLessInvokeCategory_IsNamedByAStartupWarning()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(capture, o => o.AddEntitySetProfile<S487MigratedProfile>());

        string[] warnings = SeamWarnings(capture);
        foreach (string w in warnings) _out.WriteLine("WARNING " + w);

        string invoke = Assert.Single(warnings);
        Assert.Contains(
            "the bound function/action invocation routes of entity set 'S487Migrated' are ANONYMOUS.",
            invoke, StringComparison.Ordinal);
        Assert.Contains("names no rule for the Invoke category", invoke, StringComparison.Ordinal);
        Assert.Contains(".Invoke(i => …)", invoke, StringComparison.Ordinal);
        Assert.Contains(".Invoke(i => i.AllowAnonymous())", invoke, StringComparison.Ordinal);

        // #572: the category remedy prescribes AllowAnonymous(), which HERE emits
        // AllowAnonymousAttribute and therefore overrides a host-applied
        // app.MapOhData().RequireAuthorization() — #487's own seam 3, the one it deliberately did
        // not change. The message must say so, and must point at the alternative, or the framework
        // is advising a fail-open in the very configuration docs/authorization.md recommends.
        Assert.Contains("AllowAnonymousAttribute", invoke, StringComparison.Ordinal);
        Assert.Contains("overrides a host-applied", invoke, StringComparison.Ordinal);
        Assert.Contains("name the requirement you intended", invoke, StringComparison.Ordinal);
    }

    /// <summary>
    /// #572's central assertion: the two warnings must no longer be interchangeable. Both prescribe
    /// a call spelled <c>AllowAnonymous()</c>, and the two calls do OPPOSITE things — one emits
    /// <c>AllowAnonymousAttribute</c> and escapes a host group requirement, the other deliberately
    /// does not. This asserts each message carries the half that applies to it and NOT the other's,
    /// so a future edit cannot quietly re-converge them.
    /// </summary>
    [Fact]
    public async Task Issue572_TheTwoAnonymousWarnings_DescribeOppositeBehaviours()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(capture, o =>
        {
            o.AddEntitySetProfile<S487MigratedProfile>();
            o.AddAction(S487UnboundOps.Mutate);
        });

        string[] warnings = SeamWarnings(capture);
        foreach (string w in warnings) _out.WriteLine("WARNING " + w);

        string unbound = Assert.Single(warnings, w => w.Contains("unbound action", StringComparison.Ordinal));
        string category = Assert.Single(warnings, w => w.Contains("routes of entity set", StringComparison.Ordinal));

        // The category half says the attribute is emitted and the host requirement is overridden.
        Assert.Contains("AllowAnonymousAttribute", category, StringComparison.Ordinal);
        Assert.Contains("overrides a host-applied", category, StringComparison.Ordinal);

        // The unbound half says the opposite, and must not claim the attribute is emitted.
        Assert.Contains("does NOT", unbound, StringComparison.Ordinal);
        Assert.Contains("remove a host group requirement", unbound, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnonymousAttribute", unbound, StringComparison.Ordinal);

        // And each points at #572 so the asymmetry is findable from a log line.
        Assert.Contains("#572", category, StringComparison.Ordinal);
        Assert.Contains("#572", unbound, StringComparison.Ordinal);
    }

    /// <summary>
    /// One warning per category, never per route — the Invoke category alone covers the collection-
    /// bound and entity-bound routes of every bound operation on the set.
    /// </summary>
    [Fact]
    public async Task Seam2_TheWarningIsEmittedOncePerCategory_NotOncePerRoute()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(capture, o => o.AddEntitySetProfile<S487MigratedProfile>());

        Assert.Single(SeamWarnings(capture));
    }

    [Fact]
    public async Task Seam2_StatingTheCategory_SilencesTheWarning()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(capture, o => o.AddEntitySetProfile<S487StatedProfile>());

        foreach (string w in SeamWarnings(capture)) _out.WriteLine("UNEXPECTED " + w);
        Assert.Empty(SeamWarnings(capture));

        // …and the stated intent is honoured: the operation really is anonymous.
        var response = await fx.Client.PostAsync("/odata/S487Stated/StatedStamp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Seam2_GroupLevelAuthorization_SilencesTheWarning()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(
            capture,
            o => o.AddEntitySetProfile<S487MigratedProfile>(),
            groupConfigure: g => g.RequireAuthorization());

        var action = await fx.Client.PostAsync("/odata/S487Migrated/Stamp", new StringContent("{}", Encoding.UTF8, "application/json"));
        _out.WriteLine($"POST /odata/S487Migrated/Stamp under group auth -> {(int)action.StatusCode}");

        Assert.Equal(HttpStatusCode.Unauthorized, action.StatusCode);
        Assert.Empty(SeamWarnings(capture));
    }

    /// <summary>The legacy profile-wide model has no categories, so it can have no rule-less one.</summary>
    [Fact]
    public async Task Seam2_LegacyProfileWideModel_IsNeverReported()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(capture, o => o.AddEntitySetProfile<S487LegacyProfile>());
        Assert.Empty(SeamWarnings(capture));
    }

    // ── The silence control ────────────────────────────────────────────────

    /// <summary>
    /// A registration that requires authorization NOWHERE is a public service, not a service with a
    /// hole. Nothing in it is reported — including the unbound operations, which are the loudest
    /// candidate.
    /// </summary>
    [Fact]
    public async Task ARegistrationWithNoAuthorizationAnywhere_IsNeverReported()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(capture, o =>
        {
            o.AddEntitySetProfile<S487PublicProfile>();
            o.AddAction(S487UnboundOps.Mutate);
            o.AddFunction(S487UnboundOps.Peek);
        });

        foreach (string w in SeamWarnings(capture)) _out.WriteLine("UNEXPECTED " + w);
        Assert.Empty(SeamWarnings(capture));
    }

    // ── Seam 3 — confirmed, documented, deliberately unchanged ─────────────

    [Fact]
    public async Task Seam3_CategoryAllowAnonymous_DefeatsHostGroupAuthorization()
    {
        var capture = new WarningCapture();
        await using var fx = await BuildAsync(
            capture,
            o => o.AddEntitySetProfile<S487TunnelProfile>(),
            groupConfigure: g => g.RequireAuthorization());

        var read = await fx.Client.GetAsync("/odata/S487Tunnel");
        var svcDoc = await fx.Client.GetAsync("/odata");

        _out.WriteLine($"SEAM3   GET /odata/S487Tunnel -> {(int)read.StatusCode} (AllowAnonymous tunnels out)");
        _out.WriteLine($"CONTROL GET /odata           -> {(int)svcDoc.StatusCode} (group auth otherwise holds)");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, svcDoc.StatusCode);

        // Pins the ruling: this is not warned about. .AllowAnonymous() is the only way to express a
        // deliberate public hole, so a warning here would fire on correct configuration with no way
        // to silence it. docs/authorization.md carries it instead.
        Assert.Empty(SeamWarnings(capture));
    }

    /// <summary>
    /// The framework control, with NO OhData in the picture. Establishes that seam 3 is
    /// ASP.NET Core's own <c>AllowAnonymousAttribute</c> semantics — an endpoint carrying
    /// <c>IAllowAnonymous</c> short-circuits the authorization middleware regardless of the
    /// <c>IAuthorizeData</c> its group contributed, and regardless of the order the two were
    /// applied in. Changing OhData's half would be a divergence from the platform, not a fix.
    /// </summary>
    [Fact]
    public async Task Seam3_PlainAspNetCore_AllowAnonymousInsideAnAuthorizedGroup_IsFrameworkBehaviour()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services
            .AddAuthentication(PerOpAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PerOpAuthHandler>(PerOpAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        var group = app.MapGroup("/plain");
        group.MapGet("/gated", () => "gated");
        group.MapGet("/open", () => "open").AllowAnonymous();
        // Applied AFTER the routes, exactly as app.MapOhData().RequireAuthorization() is.
        group.RequireAuthorization();

        await app.StartAsync();
        using var client = ((IHost)app).GetTestClient();

        var gated = await client.GetAsync("/plain/gated");
        var open = await client.GetAsync("/plain/open");

        _out.WriteLine($"FRAMEWORK GET /plain/gated -> {(int)gated.StatusCode}");
        _out.WriteLine($"FRAMEWORK GET /plain/open  -> {(int)open.StatusCode} (AllowAnonymous on the endpoint)");

        Assert.Equal(HttpStatusCode.Unauthorized, gated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        await app.DisposeAsync();
    }

    /// <summary>
    /// The mechanism the whole diagnostic rests on: a <c>Finally</c> convention registered on a
    /// group BEFORE the host authorizes it still observes that authorization. If this ever stops
    /// being true, every "group auth silences the warning" test above turns into a false positive
    /// on the correct configuration, so it is pinned directly rather than only through them.
    /// </summary>
    [Fact]
    public async Task Mechanism_FinallyConvention_ObservesGroupAuthAppliedAfterTheRoutesWereMapped()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services
            .AddAuthentication(PerOpAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PerOpAuthHandler>(PerOpAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        var seen = new List<string>();
        var group = app.MapGroup("/probe");
        var nested = group.MapGroup("");
        nested.MapGet("/a", () => "a");
        group.MapGet("/b", () => "b").AllowAnonymous();
        ((IEndpointConventionBuilder)group).Finally(b => seen.Add(
            $"{b.DisplayName}: IAuthorizeData={b.Metadata.OfType<IAuthorizeData>().Any()} " +
            $"IAllowAnonymous={b.Metadata.OfType<IAllowAnonymous>().Any()}"));
        group.RequireAuthorization();

        await app.StartAsync();
        using var client = ((IHost)app).GetTestClient();
        _ = await client.GetAsync("/probe/a");

        foreach (string s in seen) _out.WriteLine("FINALLY " + s);
        // Both endpoints, including the one under a NESTED group — OhData maps every entity-set
        // route on a MapGroup("") beneath the group MapOhData returns. The convention can run more
        // than once per endpoint (the data source is built more than once), which is why the real
        // diagnostic dedupes on the audit's Key rather than trusting a single pass.
        Assert.Equal(2, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.All(seen, s => Assert.Contains("IAuthorizeData=True", s));
        Assert.Contains(seen, s => s.Contains("IAllowAnonymous=True", StringComparison.Ordinal));

        await app.DisposeAsync();
    }
}
