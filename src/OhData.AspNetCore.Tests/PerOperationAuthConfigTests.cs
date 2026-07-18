using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── #199: ConfigureAuthorization builder validation + policy/claim/override behavior ──

public class PerOperationAuthConfigTests
{
    // ── Builder-level validation (thrown in the profile constructor) ────────

    private sealed class MixConfigureThenLegacyProfile : PerOpProfileBase
    {
        public MixConfigureThenLegacyProfile()
        {
            EntitySetName = "MixCL";
            ConfigureAuthorization(a => a.Read(r => r.AllowAnonymous()));
            RequireAuthorization();
        }
    }

    private sealed class MixLegacyThenConfigureProfile : PerOpProfileBase
    {
        public MixLegacyThenConfigureProfile()
        {
            EntitySetName = "MixLC";
            RequireAuthorization();
            ConfigureAuthorization(a => a.Read(r => r.AllowAnonymous()));
        }
    }

    private sealed class ConfigureTwiceProfile : PerOpProfileBase
    {
        public ConfigureTwiceProfile()
        {
            EntitySetName = "Twice";
            ConfigureAuthorization(a => a.Read(r => r.AllowAnonymous()));
            ConfigureAuthorization(a => a.Read(r => r.AllowAnonymous()));
        }
    }

    private sealed class EmptyCategoryProfile : PerOpProfileBase
    {
        public EmptyCategoryProfile()
        {
            EntitySetName = "Empty";
            ConfigureAuthorization(a => a.Read(_ => { }));
        }
    }

    private sealed class AnonThenRequireProfile : PerOpProfileBase
    {
        public AnonThenRequireProfile()
        {
            EntitySetName = "AnonReq";
            // AllowAnonymous() returns void (it is a terminal), so this must be two statements;
            // the RequireRole call after AllowAnonymous is what trips the exclusivity guard.
            ConfigureAuthorization(a => a.Update(u => { u.AllowAnonymous(); u.RequireRole("x"); }));
        }
    }

    private sealed class RequireThenAnonProfile : PerOpProfileBase
    {
        public RequireThenAnonProfile()
        {
            EntitySetName = "ReqAnon";
            ConfigureAuthorization(a => a.Update(u => u.RequireRole("x").AllowAnonymous()));
        }
    }

    [Fact]
    public void Configure_ThenLegacy_ThrowsInConstructor() =>
        Assert.Throws<InvalidOperationException>(() => new MixConfigureThenLegacyProfile());

    [Fact]
    public void Legacy_ThenConfigure_ThrowsInConstructor() =>
        Assert.Throws<InvalidOperationException>(() => new MixLegacyThenConfigureProfile());

    [Fact]
    public void ConfigureTwice_ThrowsInConstructor() =>
        Assert.Throws<InvalidOperationException>(() => new ConfigureTwiceProfile());

    [Fact]
    public void EmptyCategory_ThrowsInConstructor() =>
        Assert.Throws<InvalidOperationException>(() => new EmptyCategoryProfile());

    [Fact]
    public void AllowAnonymousThenRequire_ThrowsInConstructor() =>
        Assert.Throws<InvalidOperationException>(() => new AnonThenRequireProfile());

    [Fact]
    public void RequireThenAllowAnonymous_ThrowsInConstructor() =>
        Assert.Throws<InvalidOperationException>(() => new RequireThenAnonProfile());

    private sealed class NullConfigureProfile : PerOpProfileBase
    {
        public NullConfigureProfile()
        {
            EntitySetName = "NullC";
            ConfigureAuthorization(null!);
        }
    }

    [Fact]
    public void NullConfigure_ThrowsInConstructor() =>
        Assert.Throws<ArgumentNullException>(() => new NullConfigureProfile());

    // ── PR1 guard: .RequireResource() is not enforced yet → throws at startup ─

    private sealed class ResourceProfile : PerOpProfileBase
    {
        public ResourceProfile()
        {
            EntitySetName = "ResGuard";
            ConfigureAuthorization(a => a.Update(u => u.RequireResource()));
        }
    }

    [Fact]
    public async Task RequireResource_ThrowsAtStartup()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PerOpAuthTestHost.BuildAsync(o => o.AddProfile<ResourceProfile>()));
    }

    // ── RequirePolicy: named policy enforced per operation ───────────────────

    private sealed class PolicyProfile : PerOpProfileBase
    {
        public PolicyProfile()
        {
            EntitySetName = "PolSet";
            ConfigureAuthorization(a => a
                .Read(r => r.AllowAnonymous())
                .Update(u => u.RequirePolicy("EditorsPolicy")));
        }
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.Unauthorized)]   // anonymous → 401
    [InlineData("bob", "user", HttpStatusCode.Forbidden)]   // wrong role → 403
    public async Task RequirePolicy_DeniesUnauthorized(string? identity, string? roles, HttpStatusCode expected)
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddProfile<PolicyProfile>(),
            policies: p => p.AddPolicy("EditorsPolicy", b => b.RequireRole("editor")));

        using var req = new HttpRequestMessage(HttpMethod.Put, "/odata/PolSet(1)")
        {
            Content = new StringContent("{\"id\":1,\"name\":\"U\"}", Encoding.UTF8, "application/json"),
        };
        if (identity is not null) req.Headers.Add(PerOpAuthHandler.IdentityHeader, identity);
        if (roles is not null) req.Headers.Add(PerOpAuthHandler.RolesHeader, roles);

        var resp = await fx.Client.SendAsync(req);
        Assert.Equal(expected, resp.StatusCode);
    }

    [Fact]
    public async Task RequirePolicy_AllowsCorrectRole()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddProfile<PolicyProfile>(),
            policies: p => p.AddPolicy("EditorsPolicy", b => b.RequireRole("editor")));

        using var req = new HttpRequestMessage(HttpMethod.Put, "/odata/PolSet(1)")
        {
            Content = new StringContent("{\"id\":1,\"name\":\"U\"}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(PerOpAuthHandler.IdentityHeader, "ed");
        req.Headers.Add(PerOpAuthHandler.RolesHeader, "editor");

        var resp = await fx.Client.SendAsync(req);
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── RequireClaim: claim + AND-combination with RequireRole ───────────────

    private sealed class ClaimProfile : PerOpProfileBase
    {
        public ClaimProfile()
        {
            EntitySetName = "ClaimSet";
            ConfigureAuthorization(a => a
                .Read(r => r.AllowAnonymous())
                .Update(u => u.RequireRole("editor").RequireClaim("scope", "write")));
        }
    }

    [Theory]
    [InlineData("ed", "editor", "scope=read", HttpStatusCode.Forbidden)]    // role ok, claim wrong → 403
    [InlineData("ed", "user", "scope=write", HttpStatusCode.Forbidden)]     // claim ok, role wrong → 403
    public async Task RequireRoleAndClaim_AreAndCombined(string identity, string roles, string claims, HttpStatusCode expected)
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o => o.AddProfile<ClaimProfile>());
        using var req = new HttpRequestMessage(HttpMethod.Put, "/odata/ClaimSet(1)")
        {
            Content = new StringContent("{\"id\":1,\"name\":\"U\"}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(PerOpAuthHandler.IdentityHeader, identity);
        req.Headers.Add(PerOpAuthHandler.RolesHeader, roles);
        req.Headers.Add(PerOpAuthHandler.ClaimsHeader, claims);

        var resp = await fx.Client.SendAsync(req);
        Assert.Equal(expected, resp.StatusCode);
    }

    [Fact]
    public async Task RequireRoleAndClaim_BothSatisfied_Passes()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o => o.AddProfile<ClaimProfile>());
        using var req = new HttpRequestMessage(HttpMethod.Put, "/odata/ClaimSet(1)")
        {
            Content = new StringContent("{\"id\":1,\"name\":\"U\"}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(PerOpAuthHandler.IdentityHeader, "ed");
        req.Headers.Add(PerOpAuthHandler.RolesHeader, "editor");
        req.Headers.Add(PerOpAuthHandler.ClaimsHeader, "scope=write");

        var resp = await fx.Client.SendAsync(req);
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // RequireClaim with no accepted values → the claim must merely be present (any value).
    private sealed class ClaimPresenceProfile : PerOpProfileBase
    {
        public ClaimPresenceProfile()
        {
            EntitySetName = "ClaimPresence";
            ConfigureAuthorization(a => a
                .Read(r => r.AllowAnonymous())
                .Update(u => u.RequireClaim("scope")));
        }
    }

    [Theory]
    [InlineData("scope=anything", false)] // claim present (any value) → passes
    [InlineData("other=x", true)]         // claim absent → 403
    public async Task RequireClaim_NoValues_ChecksPresenceOnly(string claims, bool forbidden)
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o => o.AddProfile<ClaimPresenceProfile>());
        using var req = new HttpRequestMessage(HttpMethod.Put, "/odata/ClaimPresence(1)")
        {
            Content = new StringContent("{\"id\":1,\"name\":\"U\"}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(PerOpAuthHandler.IdentityHeader, "u");
        req.Headers.Add(PerOpAuthHandler.ClaimsHeader, claims);

        var resp = await fx.Client.SendAsync(req);
        if (forbidden)
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        else
            Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── Composition with group-level (global) auth ───────────────────────────

    private sealed class PartialProfile : PerOpProfileBase
    {
        public PartialProfile()
        {
            EntitySetName = "Partial";
            // Read explicitly anonymous; Delete intentionally left unspecified (no rule).
            ConfigureAuthorization(a => a
                .Read(r => r.AllowAnonymous())
                .Create(c => c.RequireRole("editor")));
        }
    }

    [Fact]
    public async Task AllowAnonymous_OverridesGroupLevelAuth()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddProfile<PartialProfile>(),
            groupConfigure: g => g.RequireAuthorization());

        // Read is explicitly AllowAnonymous → reachable despite the global group requirement.
        var read = await fx.Client.GetAsync("/odata/Partial");
        Assert.NotEqual(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, read.StatusCode);
    }

    [Fact]
    public async Task UnspecifiedCategory_InheritsGroupLevelAuth()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddProfile<PartialProfile>(),
            groupConfigure: g => g.RequireAuthorization());

        // Delete has no per-operation rule → inherits the global group requirement → anonymous 401.
        var del = await fx.Client.DeleteAsync("/odata/Partial(1)");
        Assert.Equal(HttpStatusCode.Unauthorized, del.StatusCode);
    }

    [Fact]
    public async Task UnspecifiedCategory_WithoutGroupAuth_IsAnonymous()
    {
        await using var fx = await PerOpAuthTestHost.BuildAsync(o => o.AddProfile<PartialProfile>());

        // No global auth and no per-operation rule for Delete → anonymous is allowed through.
        var del = await fx.Client.DeleteAsync("/odata/Partial(1)");
        Assert.NotEqual(HttpStatusCode.Unauthorized, del.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, del.StatusCode);
    }
}
