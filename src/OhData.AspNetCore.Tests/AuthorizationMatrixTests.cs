using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── Header-driven auth infrastructure ───────────────────────────────────────
//
// TestHostBuilder's NoOpAuthHandler only ever produces "no result" (anonymous), which is
// enough to prove 401 but cannot exercise the 403 (wrong role) or success (correct role)
// paths. This handler lets each request declare its identity/roles via headers, without
// modifying the shared TestHostBuilder.cs.

/// <summary>
/// Authentication handler that authenticates the caller based on request headers:
/// <c>X-Test-Identity</c> (presence = authenticated; value becomes the name claim) and
/// <c>X-Test-Roles</c> (comma-separated role claims). Absence of the identity header
/// produces <see cref="AuthenticateResult.NoResult"/> (anonymous).
/// </summary>
internal sealed class HeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "HeaderTest";
    public const string IdentityHeader = "X-Test-Identity";
    public const string RolesHeader = "X-Test-Roles";

    public HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(IdentityHeader, out var identity) || string.IsNullOrEmpty(identity))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, identity.ToString()) };
        if (Request.Headers.TryGetValue(RolesHeader, out var roles) && !string.IsNullOrEmpty(roles))
        {
            foreach (string role in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim(ClaimTypes.Role, role.Trim()));
        }

        var claimsIdentity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(claimsIdentity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Builds a <see cref="TestFixture"/> wired with <see cref="HeaderAuthHandler"/> so tests
/// can control identity/roles per-request via headers. Self-contained (does not modify
/// the shared TestHostBuilder.cs).
/// </summary>
internal static class HeaderAuthTestHost
{
    public static async Task<TestFixture> BuildAsync(Action<OhDataBuilder> configure, string prefix = "/odata")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();

        builder.Services
            .AddAuthentication(HeaderAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            configure(o);
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOhData();
        await app.StartAsync();
        return new TestFixture(app);
    }
}

// ── Fixtures: full route-matrix profiles ────────────────────────────────────

internal class AuthMatrixChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

internal class AuthMatrixParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<AuthMatrixChild>? Children { get; set; }
}

/// <summary>
/// Profile exercising every route kind the factory maps (collection GET/$count, GET-by-key,
/// POST/PUT/PATCH/DELETE, navigation GET/$count, $ref GET/POST/DELETE, collection- and
/// entity-level bound function/action) with <see cref="EntitySetProfile{TKey,TModel}.RequireAuthorization()"/>
/// (no roles) — used to prove the per-profile-all-operations rule for anonymous (401) access.
/// </summary>
internal class AuthMatrixFullProfile : EntitySetProfile<int, AuthMatrixParent>
{
    private readonly List<AuthMatrixParent> _parents = new() { new() { Id = 1, Name = "P1" } };
    private readonly List<AuthMatrixChild> _children = new() { new() { Id = 10, ParentId = 1, Name = "C1" } };

    public AuthMatrixFullProfile() : base(x => x.Id)
    {
        EntitySetName = "AuthMatrixParents";
        RequireAuthorization();

        GetAll = (ct) => Task.FromResult<IEnumerable<AuthMatrixParent>>(_parents);
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));
        Post = (model, ct) =>
        {
            model.Id = _parents.Count > 0 ? _parents.Max(p => p.Id) + 1 : 1;
            _parents.Add(model);
            return Task.FromResult<AuthMatrixParent?>(model);
        };
        Put = (id, model, ct) =>
        {
            _parents.RemoveAll(p => p.Id == id);
            model.Id = id;
            _parents.Add(model);
            return Task.FromResult(model);
        };
        Patch = (id, delta, ct) =>
        {
            var existing = _parents.FirstOrDefault(p => p.Id == id);
            if (existing is null) return Task.FromResult<AuthMatrixParent?>(null);
            delta.Patch(existing);
            return Task.FromResult<AuthMatrixParent?>(existing);
        };
        Delete = (id, ct) => Task.FromResult(_parents.RemoveAll(p => p.Id == id) > 0);

        HasMany(
            navigation: x => x.Children!,
            getAll: (parentId, ct) => Task.FromResult<IEnumerable<AuthMatrixChild>>(_children.Where(c => c.ParentId == parentId)),
            refTargetEntitySet: "Children",
            addRef: (parentId, relatedId, ct) => Task.CompletedTask,
            removeRef: (parentId, relatedId, ct) => Task.CompletedTask);

        BindFunction(GetCount);
        BindAction(ResetAll);
        BindEntityFunction(GetLabel);
        BindEntityAction(Rename);
    }

    private Task<int> GetCount() => Task.FromResult(_parents.Count);
    private Task ResetAll() => Task.CompletedTask;
    private Task<string> GetLabel(int key) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == key)?.Name ?? "");
    private Task Rename(int key, string newName) => Task.CompletedTask;
}

internal class RoleMatrixChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

internal class RoleMatrixParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<RoleMatrixChild>? Children { get; set; }
}

/// <summary>
/// Same full route matrix as <see cref="AuthMatrixFullProfile"/>, but protected with
/// <c>RequireRoles("admin")</c> — used to prove 403 (wrong role) and success (correct role)
/// across every route kind.
/// </summary>
internal class RoleMatrixFullProfile : EntitySetProfile<int, RoleMatrixParent>
{
    private readonly List<RoleMatrixParent> _parents = new() { new() { Id = 1, Name = "P1" } };
    private readonly List<RoleMatrixChild> _children = new() { new() { Id = 10, ParentId = 1, Name = "C1" } };

    public RoleMatrixFullProfile() : base(x => x.Id)
    {
        EntitySetName = "RoleMatrixParents";
        RequireRoles("admin");

        GetAll = (ct) => Task.FromResult<IEnumerable<RoleMatrixParent>>(_parents);
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));
        Post = (model, ct) =>
        {
            model.Id = _parents.Count > 0 ? _parents.Max(p => p.Id) + 1 : 1;
            _parents.Add(model);
            return Task.FromResult<RoleMatrixParent?>(model);
        };
        Put = (id, model, ct) =>
        {
            _parents.RemoveAll(p => p.Id == id);
            model.Id = id;
            _parents.Add(model);
            return Task.FromResult(model);
        };
        Patch = (id, delta, ct) =>
        {
            var existing = _parents.FirstOrDefault(p => p.Id == id);
            if (existing is null) return Task.FromResult<RoleMatrixParent?>(null);
            delta.Patch(existing);
            return Task.FromResult<RoleMatrixParent?>(existing);
        };
        Delete = (id, ct) => Task.FromResult(_parents.RemoveAll(p => p.Id == id) > 0);

        HasMany(
            navigation: x => x.Children!,
            getAll: (parentId, ct) => Task.FromResult<IEnumerable<RoleMatrixChild>>(_children.Where(c => c.ParentId == parentId)),
            refTargetEntitySet: "Children",
            addRef: (parentId, relatedId, ct) => Task.CompletedTask,
            removeRef: (parentId, relatedId, ct) => Task.CompletedTask);

        BindFunction(GetCount);
        BindAction(ResetAll);
        BindEntityFunction(GetLabel);
        BindEntityAction(Rename);
    }

    private Task<int> GetCount() => Task.FromResult(_parents.Count);
    private Task ResetAll() => Task.CompletedTask;
    private Task<string> GetLabel(int key) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == key)?.Name ?? "");
    private Task Rename(int key, string newName) => Task.CompletedTask;
}

// ── Tests ────────────────────────────────────────────────────────────────────

public class AuthorizationMatrixTests
{
    // Route kind → (method, path template with {set} placeholder, JSON body or null,
    // expected success status code).
    private static readonly (string Description, HttpMethod Method, string PathTemplate, string? Body, HttpStatusCode SuccessStatus)[] RouteMatrix =
    {
        ("GetCollection",        HttpMethod.Get,              "/odata/{set}",                                   null,                                                HttpStatusCode.OK),
        ("GetCount",             HttpMethod.Get,              "/odata/{set}/$count",                            null,                                                HttpStatusCode.OK),
        ("GetById",              HttpMethod.Get,              "/odata/{set}(1)",                                null,                                                HttpStatusCode.OK),
        ("Post",                 HttpMethod.Post,             "/odata/{set}",                                   "{\"id\":0,\"name\":\"New\"}",                        HttpStatusCode.Created),
        ("Put",                  HttpMethod.Put,              "/odata/{set}(1)",                                "{\"id\":1,\"name\":\"Updated\"}",                    HttpStatusCode.OK),
        ("Patch",                new HttpMethod("PATCH"),     "/odata/{set}(1)",                                "{\"name\":\"Patched\"}",                             HttpStatusCode.OK),
        ("Delete",               HttpMethod.Delete,           "/odata/{set}(1)",                                null,                                                HttpStatusCode.NoContent),
        ("NavigationGet",        HttpMethod.Get,              "/odata/{set}(1)/Children",                       null,                                                HttpStatusCode.OK),
        ("NavigationCount",      HttpMethod.Get,              "/odata/{set}(1)/Children/$count",                null,                                                HttpStatusCode.OK),
        ("RefGet",               HttpMethod.Get,              "/odata/{set}(1)/Children/$ref",                  null,                                                HttpStatusCode.OK),
        ("RefAdd",               HttpMethod.Post,             "/odata/{set}(1)/Children/$ref",                  "{\"@odata.id\":\"http://localhost/odata/Children(10)\"}", HttpStatusCode.NoContent),
        ("RefRemove",            HttpMethod.Delete,           "/odata/{set}(1)/Children/$ref?$id=http://localhost/odata/Children(10)", null,                        HttpStatusCode.NoContent),
        ("BoundFunctionCollection", HttpMethod.Get,           "/odata/{set}/GetCount",                          null,                                                HttpStatusCode.OK),
        ("BoundActionCollection",   HttpMethod.Post,          "/odata/{set}/ResetAll",                          null,                                                HttpStatusCode.NoContent),
        ("BoundFunctionEntity",     HttpMethod.Get,           "/odata/{set}(1)/GetLabel",                       null,                                                HttpStatusCode.OK),
        ("BoundActionEntity",       HttpMethod.Post,          "/odata/{set}(1)/Rename",                         "{\"newName\":\"X\"}",                                HttpStatusCode.NoContent),
    };

    public static IEnumerable<object[]> AuthMatrixCases() =>
        RouteMatrix.Select(c => new object[] { c.Description, c.Method, c.PathTemplate.Replace("{set}", "AuthMatrixParents") });

    public static IEnumerable<object[]> RoleMatrixCases() =>
        RouteMatrix.Select(c => new object[] { c.Description, c.Method, c.PathTemplate.Replace("{set}", "RoleMatrixParents") });

    private static string? BodyFor(string description) =>
        RouteMatrix.First(c => c.Description == description).Body;

    private static HttpStatusCode SuccessStatusFor(string description) =>
        RouteMatrix.First(c => c.Description == description).SuccessStatus;

    private static HttpRequestMessage BuildRequest(HttpMethod method, string path, string? jsonBody)
    {
        var request = new HttpRequestMessage(method, path);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return request;
    }

    // ── A.1: RequireAuthorization() — anonymous → 401 on every route kind ────

    [Theory]
    [MemberData(nameof(AuthMatrixCases))]
    public async Task RequireAuthorization_Anonymous_Returns401_OnEveryRouteKind(string description, HttpMethod method, string path)
    {
        await using var fx = await HeaderAuthTestHost.BuildAsync(o => o.AddProfile<AuthMatrixFullProfile>());
        var request = BuildRequest(method, path, BodyFor(description));
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── A.2: RequireRoles("admin") — wrong role → 403 on every route kind ────

    [Theory]
    [MemberData(nameof(RoleMatrixCases))]
    public async Task RequireRoles_WrongRole_Returns403_OnEveryRouteKind(string description, HttpMethod method, string path)
    {
        await using var fx = await HeaderAuthTestHost.BuildAsync(o => o.AddProfile<RoleMatrixFullProfile>());
        var request = BuildRequest(method, path, BodyFor(description));
        request.Headers.Add(HeaderAuthHandler.IdentityHeader, "alice");
        request.Headers.Add(HeaderAuthHandler.RolesHeader, "user"); // not "admin"
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── A.2b: RequireRoles("admin") — correct role → success on every route kind ─

    [Theory]
    [MemberData(nameof(RoleMatrixCases))]
    public async Task RequireRoles_CorrectRole_Succeeds_OnEveryRouteKind(string description, HttpMethod method, string path)
    {
        await using var fx = await HeaderAuthTestHost.BuildAsync(o => o.AddProfile<RoleMatrixFullProfile>());
        var request = BuildRequest(method, path, BodyFor(description));
        request.Headers.Add(HeaderAuthHandler.IdentityHeader, "bob");
        request.Headers.Add(HeaderAuthHandler.RolesHeader, "admin");
        var response = await fx.Client.SendAsync(request);
        Assert.Equal(SuccessStatusFor(description), response.StatusCode);
    }

    // ── A.3: Mixed registration — no auth bleed between profiles ─────────────

    [Fact]
    public async Task MixedRegistration_UnprotectedSet_StaysAnonymousAccessible_WhileProtectedSet401s()
    {
        await using var fx = await HeaderAuthTestHost.BuildAsync(o => o
            .AddProfile<AuthMatrixFullProfile>()   // RequireAuthorization()
            .AddProfile<WidgetProfile>());          // no auth configured

        var unprotected = await fx.Client.GetAsync("/odata/Widgets");
        Assert.Equal(HttpStatusCode.OK, unprotected.StatusCode);

        var protectedResponse = await fx.Client.GetAsync("/odata/AuthMatrixParents");
        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
    }

    [Fact]
    public async Task MixedRegistration_ProtectedSet_AllowsAuthenticatedAccess_WhileUnprotectedSetIgnoresHeaders()
    {
        await using var fx = await HeaderAuthTestHost.BuildAsync(o => o
            .AddProfile<AuthMatrixFullProfile>()
            .AddProfile<WidgetProfile>());

        var request = new HttpRequestMessage(HttpMethod.Get, "/odata/AuthMatrixParents");
        request.Headers.Add(HeaderAuthHandler.IdentityHeader, "carol");
        var protectedResponse = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);

        // Unprotected set is unaffected by the presence of auth headers.
        var unprotectedRequest = new HttpRequestMessage(HttpMethod.Get, "/odata/Widgets");
        unprotectedRequest.Headers.Add(HeaderAuthHandler.IdentityHeader, "carol");
        var unprotectedResponse = await fx.Client.SendAsync(unprotectedRequest);
        Assert.Equal(HttpStatusCode.OK, unprotectedResponse.StatusCode);
    }

    // ── A.4: Service document and $metadata remain reachable without auth ────

    [Fact]
    public async Task ServiceDocument_ReachableWithoutAuth_EvenWhenAllProfilesRequireAuth()
    {
        await using var fx = await HeaderAuthTestHost.BuildAsync(o => o
            .AddProfile<AuthMatrixFullProfile>()
            .AddProfile<RoleMatrixFullProfile>());

        var response = await fx.Client.GetAsync("/odata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Metadata_ReachableWithoutAuth_EvenWhenAllProfilesRequireAuth()
    {
        // Documents actual behavior: $metadata and the service document are mapped directly
        // on the top-level route group, before any per-profile RequireAuthorization() is
        // applied to the nested per-entity-set auth group — so they are never protected,
        // regardless of profile configuration. This may be a design question worth raising:
        // should $metadata be protectable when every entity set requires auth?
        await using var fx = await HeaderAuthTestHost.BuildAsync(o => o
            .AddProfile<AuthMatrixFullProfile>()
            .AddProfile<RoleMatrixFullProfile>());

        var response = await fx.Client.GetAsync("/odata/$metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
