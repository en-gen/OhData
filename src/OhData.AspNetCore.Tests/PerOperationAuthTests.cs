using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── #199 Layer C: per-operation authorization ───────────────────────────────
//
// These tests assert on the AUTHORIZATION OUTCOME — 401 (anonymous), 403 (authenticated but
// unauthorized), or "passed through" (neither, i.e. the handler ran) — rather than the exact
// success status code, so they are robust to per-route handler details and prove only the auth
// behavior across every wired route site.

/// <summary>
/// Auth handler that reads identity (<c>X-Test-Identity</c>), roles (<c>X-Test-Roles</c>,
/// comma-separated) and arbitrary claims (<c>X-Test-Claims</c>, <c>type=value;type=value</c>).
/// Absent identity → anonymous.
/// </summary>
internal sealed class PerOpAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "PerOpTest";
    public const string IdentityHeader = "X-Test-Identity";
    public const string RolesHeader = "X-Test-Roles";
    public const string ClaimsHeader = "X-Test-Claims";

    public PerOpAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(IdentityHeader, out var identity) || string.IsNullOrEmpty(identity))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.Name, identity.ToString()) };
        if (Request.Headers.TryGetValue(RolesHeader, out var roles) && !string.IsNullOrEmpty(roles))
        {
            foreach (string role in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim(ClaimTypes.Role, role.Trim()));
        }
        if (Request.Headers.TryGetValue(ClaimsHeader, out var rawClaims) && !string.IsNullOrEmpty(rawClaims))
        {
            foreach (string pair in rawClaims.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                    claims.Add(new Claim(pair[..eq].Trim(), pair[(eq + 1)..].Trim()));
            }
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

internal static class PerOpAuthTestHost
{
    public static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        Action<AuthorizationOptions>? policies = null,
        Action<IEndpointConventionBuilder>? groupConfigure = null,
        string prefix = "/odata")
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

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
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
}

// ── Model + full-matrix profile ─────────────────────────────────────────────

internal class PerOpChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

internal class PerOpParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public IEnumerable<PerOpChild>? Children { get; set; }
}

/// <summary>
/// Base wiring for every route kind (reads, writes, navigation, $ref, property access, bound ops).
/// Subclasses only supply the entity set name + the <c>ConfigureAuthorization</c> block.
/// </summary>
internal abstract class PerOpProfileBase : EntitySetProfile<int, PerOpParent>
{
    protected PerOpProfileBase() : base(x => x.Id)
    {
        var parents = new List<PerOpParent> { new() { Id = 1, Name = "P1", Description = "D1" } };
        var children = new List<PerOpChild> { new() { Id = 10, ParentId = 1, Name = "C1" } };

        GetAll = (ct) => Task.FromResult<IEnumerable<PerOpParent>>(parents);
        GetById = (id, ct) => Task.FromResult(parents.FirstOrDefault(p => p.Id == id));
        Post = (model, ct) =>
        {
            model.Id = parents.Count > 0 ? parents.Max(p => p.Id) + 1 : 1;
            parents.Add(model);
            return Task.FromResult<PerOpParent?>(model);
        };
        Put = (id, model, ct) =>
        {
            parents.RemoveAll(p => p.Id == id);
            model.Id = id;
            parents.Add(model);
            return Task.FromResult(model);
        };
        Patch = (id, delta, ct) =>
        {
            var existing = parents.FirstOrDefault(p => p.Id == id);
            if (existing is null) return Task.FromResult<PerOpParent?>(null);
            delta.Patch(existing);
            return Task.FromResult<PerOpParent?>(existing);
        };
        Delete = (id, ct) => Task.FromResult(parents.RemoveAll(p => p.Id == id) > 0);

        HasMany(
            navigation: x => x.Children!,
            getAll: (parentId, ct) => Task.FromResult<IEnumerable<PerOpChild>>(children.Where(c => c.ParentId == parentId)),
            refTargetEntitySet: "PerOpChildren",
            addRef: (parentId, relatedId, ct) => Task.CompletedTask,
            removeRef: (parentId, relatedId, ct) => Task.CompletedTask,
            post: (parentId, child, ct) => { child.Id = 99; return Task.FromResult<PerOpChild?>(child); });

        BindFunction(GetCount);
        BindAction(ResetAll);
        BindEntityFunction(GetLabel);
        BindEntityAction(Rename);
    }

    private Task<int> GetCount() => Task.FromResult(1);
    private Task ResetAll() => Task.CompletedTask;
    private Task<string> GetLabel(int key) => Task.FromResult("L");
    private Task Rename(int key, string newName) => Task.CompletedTask;
}

/// <summary>
/// Per-operation split: reads anonymous, create/update require role <c>editor</c>, delete requires
/// role <c>admin</c>, bound operations require any authenticated user.
/// </summary>
internal sealed class PerOpSplitProfile : PerOpProfileBase
{
    public PerOpSplitProfile()
    {
        EntitySetName = "PerOpSplit";
        ConfigureAuthorization(auth => auth
            .Read(r => r.AllowAnonymous())
            .Create(c => c.RequireRole("editor"))
            .Update(u => u.RequireRole("editor"))
            .Delete(d => d.RequireRole("admin"))
            .Invoke(i => i.RequireAuthenticatedUser()));
    }
}

// ── The route → category matrix ─────────────────────────────────────────────

public class PerOperationAuthTests
{
    private const string Set = "PerOpSplit";

    // Description, HTTP method, path (with {set}), JSON body, operation category.
    private static readonly (string Desc, HttpMethod Method, string Path, string? Body, OhDataOperation Category)[] Matrix =
    {
        ("GetCollection",   HttpMethod.Get,          "/odata/{set}",                     null,                                   OhDataOperation.Read),
        ("GetCount",        HttpMethod.Get,          "/odata/{set}/$count",              null,                                   OhDataOperation.Read),
        ("GetById",         HttpMethod.Get,          "/odata/{set}(1)",                  null,                                   OhDataOperation.Read),
        ("NavGet",          HttpMethod.Get,          "/odata/{set}(1)/Children",         null,                                   OhDataOperation.Read),
        ("NavCount",        HttpMethod.Get,          "/odata/{set}(1)/Children/$count",  null,                                   OhDataOperation.Read),
        ("RefGet",          HttpMethod.Get,          "/odata/{set}(1)/Children/$ref",    null,                                   OhDataOperation.Read),
        ("PropGet",         HttpMethod.Get,          "/odata/{set}(1)/Name",             null,                                   OhDataOperation.Read),
        ("PropValue",       HttpMethod.Get,          "/odata/{set}(1)/Name/$value",      null,                                   OhDataOperation.Read),
        ("Post",            HttpMethod.Post,         "/odata/{set}",                     "{\"name\":\"N\"}",                     OhDataOperation.Create),
        ("NavPost",         HttpMethod.Post,         "/odata/{set}(1)/Children",         "{\"name\":\"C\"}",                     OhDataOperation.Create),
        ("Put",             HttpMethod.Put,          "/odata/{set}(1)",                  "{\"id\":1,\"name\":\"U\"}",            OhDataOperation.Update),
        ("Patch",           new HttpMethod("PATCH"), "/odata/{set}(1)",                  "{\"name\":\"P\"}",                     OhDataOperation.Update),
        ("PropPut",         HttpMethod.Put,          "/odata/{set}(1)/Name",             "{\"value\":\"X\"}",                    OhDataOperation.Update),
        ("PropPatch",       new HttpMethod("PATCH"), "/odata/{set}(1)/Description",      "{\"value\":\"X\"}",                    OhDataOperation.Update),
        ("PropDelete",      HttpMethod.Delete,       "/odata/{set}(1)/Description",      null,                                   OhDataOperation.Update),
        ("RefAdd",          HttpMethod.Post,         "/odata/{set}(1)/Children/$ref",    "{\"@odata.id\":\"http://localhost/odata/PerOpChildren(10)\"}", OhDataOperation.Update),
        ("RefRemove",       HttpMethod.Delete,       "/odata/{set}(1)/Children/$ref?$id=http://localhost/odata/PerOpChildren(10)", null,     OhDataOperation.Update),
        ("Delete",          HttpMethod.Delete,       "/odata/{set}(1)",                  null,                                   OhDataOperation.Delete),
        ("BoundFnColl",     HttpMethod.Get,          "/odata/{set}/GetCount",            null,                                   OhDataOperation.Invoke),
        ("BoundActColl",    HttpMethod.Post,         "/odata/{set}/ResetAll",            null,                                   OhDataOperation.Invoke),
        ("BoundFnEntity",   HttpMethod.Get,          "/odata/{set}(1)/GetLabel",         null,                                   OhDataOperation.Invoke),
        ("BoundActEntity",  HttpMethod.Post,         "/odata/{set}(1)/Rename",           "{\"newName\":\"X\"}",                  OhDataOperation.Invoke),
    };

    public static IEnumerable<object[]> Cases() =>
        Matrix.Select(c => new object[] { c.Desc, c.Method, c.Path.Replace("{set}", Set), c.Body ?? "", (int)c.Category });

    private static HttpRequestMessage Req(HttpMethod method, string path, string body, string? identity, string? roles)
    {
        var r = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(body))
            r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (identity is not null) r.Headers.Add(PerOpAuthHandler.IdentityHeader, identity);
        if (roles is not null) r.Headers.Add(PerOpAuthHandler.RolesHeader, roles);
        return r;
    }

    private static bool Passed(HttpStatusCode s) =>
        s != HttpStatusCode.Unauthorized && s != HttpStatusCode.Forbidden;

    // ── Anonymous: reads pass, everything else 401 ──────────────────────────

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Anonymous_ReadsPass_OthersReturn401(string desc, HttpMethod method, string path, string body, int categoryInt)
    {
        var category = (OhDataOperation)categoryInt;
        await using var fx = await PerOpAuthTestHost.BuildAsync(o => o.AddProfile<PerOpSplitProfile>());
        var response = await fx.Client.SendAsync(Req(method, path, body, identity: null, roles: null));

        if (category == OhDataOperation.Read)
            Assert.True(Passed(response.StatusCode), $"{desc}: expected read to pass anonymously, got {(int)response.StatusCode}");
        else
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Editor: reads + create/update pass, delete 403, invoke passes ───────

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Editor_ReadCreateUpdateInvokePass_DeleteForbidden(string desc, HttpMethod method, string path, string body, int categoryInt)
    {
        var category = (OhDataOperation)categoryInt;
        await using var fx = await PerOpAuthTestHost.BuildAsync(o => o.AddProfile<PerOpSplitProfile>());
        var response = await fx.Client.SendAsync(Req(method, path, body, identity: "ed", roles: "editor"));

        if (category == OhDataOperation.Delete)
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        else
            Assert.True(Passed(response.StatusCode), $"{desc}: expected pass for editor, got {(int)response.StatusCode}");
    }

    // ── Admin: reads + delete + invoke pass, create/update 403 ──────────────

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Admin_ReadDeleteInvokePass_CreateUpdateForbidden(string desc, HttpMethod method, string path, string body, int categoryInt)
    {
        var category = (OhDataOperation)categoryInt;
        await using var fx = await PerOpAuthTestHost.BuildAsync(o => o.AddProfile<PerOpSplitProfile>());
        var response = await fx.Client.SendAsync(Req(method, path, body, identity: "ad", roles: "admin"));

        if (category is OhDataOperation.Create or OhDataOperation.Update)
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        else
            Assert.True(Passed(response.StatusCode), $"{desc}: expected pass for admin, got {(int)response.StatusCode}");
    }
}
