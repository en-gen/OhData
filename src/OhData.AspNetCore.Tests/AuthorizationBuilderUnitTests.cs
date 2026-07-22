using System;
using System.Linq;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #199: direct unit tests for the ConfigureAuthorization fluent builder (no host required) —
// covering rule construction, requirement accumulation, flag mapping, and argument validation.
public class AuthorizationBuilderUnitTests
{
    private static AuthorizationRuleBuilder Build(Action<IAuthorizationRuleBuilder> configure)
    {
        var b = new AuthorizationRuleBuilder();
        configure(b);
        return b;
    }

    [Fact]
    public void Read_AllowAnonymous_ProducesAnonymousRuleWithNoRequirements()
    {
        var rule = Assert.Single(Build(a => a.Read(r => r.AllowAnonymous())).Rules);
        Assert.Equal(OhDataOperation.Read, rule.Operations);
        Assert.True(rule.AllowAnonymous);
        Assert.Empty(rule.Requirements);
        Assert.Null(rule.BoundOperationName);
    }

    [Fact]
    public void Writes_MapsToCreateUpdateDelete()
    {
        var rule = Assert.Single(Build(a => a.Writes(w => w.RequireAuthenticatedUser())).Rules);
        Assert.Equal(OhDataOperation.Create | OhDataOperation.Update | OhDataOperation.Delete, rule.Operations);
    }

    [Fact]
    public void All_MapsToAllFlag()
    {
        var rule = Assert.Single(Build(a => a.All(x => x.RequireAuthenticatedUser())).Rules);
        Assert.Equal(OhDataOperation.All, rule.Operations);
    }

    [Fact]
    public void Create_Update_Delete_MapToSingleFlags()
    {
        var b = Build(a => a
            .Create(c => c.RequireAuthenticatedUser())
            .Update(u => u.RequireAuthenticatedUser())
            .Delete(d => d.RequireAuthenticatedUser()));
        Assert.Equal(
            new[] { OhDataOperation.Create, OhDataOperation.Update, OhDataOperation.Delete },
            b.Rules.Select(r => r.Operations).ToArray());
    }

    [Fact]
    public void RequireRole_StoresRolesInOrder()
    {
        var req = Assert.Single(Assert.Single(Build(a => a.Update(u => u.RequireRole("admin", "editor"))).Rules).Requirements);
        Assert.Equal(AuthRequirementKind.Role, req.Kind);
        Assert.Equal(new[] { "admin", "editor" }, req.Values);
    }

    [Fact]
    public void RequireClaim_WithValues_StoresTypeAndValues()
    {
        var req = Assert.Single(Assert.Single(Build(a => a.Update(u => u.RequireClaim("scope", "read", "write"))).Rules).Requirements);
        Assert.Equal(AuthRequirementKind.Claim, req.Kind);
        Assert.Equal("scope", req.Name);
        Assert.Equal(new[] { "read", "write" }, req.Values);
    }

    [Fact]
    public void RequireClaim_WithoutValues_StoresNullValues()
    {
        var req = Assert.Single(Assert.Single(Build(a => a.Update(u => u.RequireClaim("scope"))).Rules).Requirements);
        Assert.Equal("scope", req.Name);
        Assert.Null(req.Values);
    }

    [Fact]
    public void RequirePolicy_And_Resource_And_Authenticated_AccumulateInOrder()
    {
        var rule = Assert.Single(Build(a => a.Update(u => u
            .RequireAuthenticatedUser()
            .RequirePolicy("P")
            .RequireResource("RP"))).Rules);
        Assert.Collection(rule.Requirements,
            r => Assert.Equal(AuthRequirementKind.AuthenticatedUser, r.Kind),
            r => { Assert.Equal(AuthRequirementKind.Policy, r.Kind); Assert.Equal("P", r.Name); },
            r => { Assert.Equal(AuthRequirementKind.Resource, r.Kind); Assert.Equal("RP", r.Name); });
    }

    [Fact]
    public void RequireResource_NoPolicy_StoresNullName()
    {
        var req = Assert.Single(Assert.Single(Build(a => a.Update(u => u.RequireResource())).Rules).Requirements);
        Assert.Equal(AuthRequirementKind.Resource, req.Kind);
        Assert.Null(req.Name);
    }

    [Fact]
    public void InvokeGeneric_HasNoBoundOperationName()
    {
        var rule = Assert.Single(Build(a => a.Invoke(i => i.RequireAuthenticatedUser())).Rules);
        Assert.Equal(OhDataOperation.Invoke, rule.Operations);
        Assert.Null(rule.BoundOperationName);
    }

    [Fact]
    public void InvokeNamed_SetsBoundOperationName()
    {
        var rule = Assert.Single(Build(a => a.Invoke("Approve", i => i.RequirePolicy("Approvers"))).Rules);
        Assert.Equal(OhDataOperation.Invoke, rule.Operations);
        Assert.Equal("Approve", rule.BoundOperationName);
    }

    [Fact]
    public void MultipleCategories_AccumulateInDeclarationOrder()
    {
        var b = Build(a => a
            .Read(r => r.AllowAnonymous())
            .Update(u => u.RequireRole("editor")));
        Assert.Equal(2, b.Rules.Count);
        Assert.Equal(OhDataOperation.Read, b.Rules[0].Operations);
        Assert.Equal(OhDataOperation.Update, b.Rules[1].Operations);
    }

    // ── Argument validation ─────────────────────────────────────────────────

    [Fact]
    public void RequireRole_Empty_Throws() =>
        Assert.Throws<ArgumentException>(() => Build(a => a.Read(r => r.RequireRole())));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RequireClaim_BlankType_Throws(string claimType) =>
        Assert.Throws<ArgumentException>(() => Build(a => a.Read(r => r.RequireClaim(claimType))));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RequirePolicy_Blank_Throws(string policy) =>
        Assert.Throws<ArgumentException>(() => Build(a => a.Read(r => r.RequirePolicy(policy))));

    [Fact]
    public void RequireResource_BlankPolicy_Throws() =>
        Assert.Throws<ArgumentException>(() => Build(a => a.Read(r => r.RequireResource(""))));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InvokeNamed_BlankName_Throws(string name) =>
        Assert.Throws<ArgumentException>(() => Build(a => a.Invoke(name, i => i.RequireAuthenticatedUser())));

    [Fact]
    public void Category_NullConfigure_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Build(a => a.Read(null!)));

    [Fact]
    public void EmptyCategory_NoRequirementNoAnonymous_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Build(a => a.Read(_ => { })));

    [Fact]
    public void AllowAnonymous_ThenRequire_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Build(a => a.Read(r => { r.AllowAnonymous(); r.RequireRole("x"); })));

    [Fact]
    public void Require_ThenAllowAnonymous_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Build(a => a.Read(r => r.RequireRole("x").AllowAnonymous())));
}
