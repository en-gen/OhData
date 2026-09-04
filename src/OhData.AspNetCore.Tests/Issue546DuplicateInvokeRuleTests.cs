using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── #546: two named Invoke(...) rules that collapse onto one operation ───────────────────────
//
// #525 made ResolveOperationRule match OperationAuthRule.BoundOperationName with
// OrdinalIgnoreCase — correctly, because everything the rule governs is matched case-insensitively.
// But the resolution loop keeps LAST-WRITE-WINS (`named = rule`), so two rules differing only in
// case now collapse onto each other and DECLARATION ORDER decides authorization:
//
//     .Invoke("Stamp", i => i.RequireRole("Admin")).Invoke("stamp", i => i.AllowAnonymous())
//         -> anonymous invocation succeeds        (OPEN — protected before #525)
//     .Invoke("stamp", i => i.AllowAnonymous()).Invoke("Stamp", i => i.RequireRole("Admin"))
//         -> anonymous invocation is refused
//
// Both configurations survived #525's startup validation, which asks only "does this name resolve
// to a declared operation?" — both do. It never asked whether TWO rules resolve to the SAME one,
// which is the hazard the case-insensitive comparer introduced.
//
// The refusal below is matched with the SAME comparer ResolveOperationRule uses, for the same
// reason #525's own validation is: a second, independently-derived comparison would reject a
// different set of configurations than the one that actually collapses at resolution time.
public class Issue546DuplicateInvokeRuleTests
{
    private static HttpRequestMessage Req(string path, string? identity = null, string? roles = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, path);
        if (identity is not null) r.Headers.Add(PerOpAuthHandler.IdentityHeader, identity);
        if (roles is not null) r.Headers.Add(PerOpAuthHandler.RolesHeader, roles);
        return r;
    }

    /// <summary>
    /// The measured fail-OPEN order: the protective rule is declared first and the anonymous one
    /// second, so last-write-wins hands the route to AllowAnonymous. Under the pre-#525 Ordinal
    /// comparer this exact configuration was deterministically PROTECTED, which is what makes it a
    /// regression rather than merely an ambiguity.
    /// </summary>
    [Fact]
    public async Task ProtectedThenAnonymous_DifferingOnlyInCase_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PerOpAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar546ProtectedFirstProfile>()));

        Assert.Contains("Oar546ProtectedFirst", ex.Message, StringComparison.Ordinal);
        // Both spellings must be named, or the developer cannot tell which pair collapsed.
        Assert.Contains("\"Stamp\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"stamp\"", ex.Message, StringComparison.Ordinal);
        // …and the operation they both resolve to.
        Assert.Contains("'Stamp'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other order. It happens to resolve the protective way today, which is precisely why it
    /// must be refused too: whether the operation is open is decided by the order two rules were
    /// written in, and the fix is to make the configuration unrepresentable, not to bless the
    /// order that currently fails closed.
    /// </summary>
    [Fact]
    public async Task AnonymousThenProtected_DifferingOnlyInCase_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PerOpAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar546AnonymousFirstProfile>()));

        Assert.Contains("Oar546AnonymousFirst", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"stamp\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"Stamp\"", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same collapse with no case difference at all: two rules spelled identically. It has the
    /// identical mechanism (last-write-wins silently discards the first rule) and the identical
    /// consequence, and it was order-dependent under the pre-#525 Ordinal comparer too.
    /// </summary>
    [Fact]
    public async Task TwoIdenticallySpelledRules_ForOneOperation_ThrowAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PerOpAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar546ExactDuplicateProfile>()));

        Assert.Contains("Oar546ExactDuplicate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Stamp'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Entity-level operations reach the same rule list through a different Bind* family, so the
    /// collapse is reachable there too and must be refused identically.
    /// </summary>
    [Fact]
    public async Task EntityLevelOperation_WithTwoRules_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PerOpAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar546EntityLevelDuplicateProfile>()));

        Assert.Contains("Oar546EntityLevelDuplicate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Seal'", ex.Message, StringComparison.Ordinal);
    }

    // ── controls ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Control, and the one that bounds the refusal: two named rules for two DIFFERENT operations
    /// are the ordinary configuration and must keep working — including when one of them is
    /// miscased, which is what #525 just made resolve. Both rules are asserted to be in force, so a
    /// change that simply stopped applying named rules could not pass this.
    /// </summary>
    [Fact]
    public async Task TwoNamedRules_ForDifferentOperations_StartCleanlyAndBothApply()
    {
        await using TestFixture fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Oar546DistinctOperationsProfile>());

        using HttpResponseMessage stampAnon =
            await fx.Client.SendAsync(Req("/odata/Oar546Distinct/Stamp"));
        Assert.Equal(HttpStatusCode.Unauthorized, stampAnon.StatusCode);

        using HttpResponseMessage stampAdmin =
            await fx.Client.SendAsync(Req("/odata/Oar546Distinct/Stamp", "u", "Admin"));
        Assert.Equal(HttpStatusCode.OK, stampAdmin.StatusCode);

        // The miscased rule for the OTHER operation resolves (that is #525) and is not confused
        // with the first one.
        using HttpResponseMessage markWrongRole =
            await fx.Client.SendAsync(Req("/odata/Oar546Distinct/Mark", "u", "Admin"));
        Assert.Equal(HttpStatusCode.Forbidden, markWrongRole.StatusCode);

        using HttpResponseMessage markOk =
            await fx.Client.SendAsync(Req("/odata/Oar546Distinct/Mark", "u", "Auditor"));
        Assert.Equal(HttpStatusCode.OK, markOk.StatusCode);
    }

    /// <summary>
    /// Control: a generic Invoke rule beside a named one is not a duplicate — they are different
    /// kinds of rule and the named one deliberately wins. #525's own fallback fixture depends on
    /// this shape, so a refusal that caught it would break a shipped, documented configuration.
    /// </summary>
    [Fact]
    public async Task GenericInvokeRule_BesideANamedOne_StartsCleanly()
    {
        await using TestFixture fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Oar546GenericPlusNamedProfile>());

        using HttpResponseMessage named =
            await fx.Client.SendAsync(Req("/odata/Oar546GenericPlusNamed/Stamp", "u", "Admin"));
        Assert.Equal(HttpStatusCode.OK, named.StatusCode);

        using HttpResponseMessage genericGoverned =
            await fx.Client.SendAsync(Req("/odata/Oar546GenericPlusNamed/Other", "u", "Reader"));
        Assert.Equal(HttpStatusCode.OK, genericGoverned.StatusCode);
    }

    /// <summary>
    /// Control: two generic Invoke rules are last-write-wins by design (`generic = rule`), which is
    /// how every category selector behaves — All(…) then Invoke(…) is a documented refinement
    /// idiom. The refusal is scoped to NAMED rules and must not reach this.
    /// </summary>
    [Fact]
    public async Task TwoGenericInvokeRules_StartCleanly()
    {
        await using TestFixture fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Oar546TwoGenericProfile>());

        using HttpResponseMessage last =
            await fx.Client.SendAsync(Req("/odata/Oar546TwoGeneric/Stamp", "u", "Admin"));
        Assert.Equal(HttpStatusCode.OK, last.StatusCode);
    }
}

// ── fixtures ─────────────────────────────────────────────────────────────────────────────────

internal class Oar546Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>#546: the measured fail-OPEN order — protective rule first, anonymous rule second.</summary>
internal sealed class Oar546ProtectedFirstProfile : EntitySetProfile<int, Oar546Widget>
{
    public Oar546ProtectedFirstProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar546ProtectedFirst";
        BindFunction(Stamp);
        ConfigureAuthorization(a => a
            .Invoke("Stamp", i => i.RequireRole("Admin"))
            .Invoke("stamp", i => i.AllowAnonymous()));
    }

    private Task<string> Stamp() => Task.FromResult("stamped");
}

/// <summary>#546: the other order — anonymous rule first, protective rule second.</summary>
internal sealed class Oar546AnonymousFirstProfile : EntitySetProfile<int, Oar546Widget>
{
    public Oar546AnonymousFirstProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar546AnonymousFirst";
        BindFunction(Stamp);
        ConfigureAuthorization(a => a
            .Invoke("stamp", i => i.AllowAnonymous())
            .Invoke("Stamp", i => i.RequireRole("Admin")));
    }

    private Task<string> Stamp() => Task.FromResult("stamped");
}

/// <summary>#546: no case difference at all — the same collapse, spelled identically.</summary>
internal sealed class Oar546ExactDuplicateProfile : EntitySetProfile<int, Oar546Widget>
{
    public Oar546ExactDuplicateProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar546ExactDuplicate";
        BindFunction(Stamp);
        ConfigureAuthorization(a => a
            .Invoke("Stamp", i => i.RequireRole("Admin"))
            .Invoke("Stamp", i => i.AllowAnonymous()));
    }

    private Task<string> Stamp() => Task.FromResult("stamped");
}

/// <summary>#546: the collapse on an ENTITY-level operation.</summary>
internal sealed class Oar546EntityLevelDuplicateProfile : EntitySetProfile<int, Oar546Widget>
{
    public Oar546EntityLevelDuplicateProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar546EntityLevelDuplicate";
        GetById = (id, _) => OhDataResult.Success<Oar546Widget>(new Oar546Widget { Id = id });
        BindEntityAction(Seal);
        ConfigureAuthorization(a => a
            .Invoke("Seal", i => i.RequireRole("Admin"))
            .Invoke("seal", i => i.AllowAnonymous()));
    }

    private Task<string> Seal(int key) => Task.FromResult("sealed");
}

/// <summary>Control: two named rules for two different operations, one of them miscased (#525).</summary>
internal sealed class Oar546DistinctOperationsProfile : EntitySetProfile<int, Oar546Widget>
{
    public Oar546DistinctOperationsProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar546Distinct";
        BindFunction(Stamp);
        BindFunction(Mark);
        ConfigureAuthorization(a => a
            .Invoke("Stamp", i => i.RequireRole("Admin"))
            .Invoke("mark", i => i.RequireRole("Auditor")));
    }

    private Task<string> Stamp() => Task.FromResult("stamped");
    private Task<string> Mark() => Task.FromResult("marked");
}

/// <summary>Control: a generic Invoke rule beside a named one — #525's own fallback shape.</summary>
internal sealed class Oar546GenericPlusNamedProfile : EntitySetProfile<int, Oar546Widget>
{
    public Oar546GenericPlusNamedProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar546GenericPlusNamed";
        BindFunction(Stamp);
        BindFunction(Other);
        ConfigureAuthorization(a => a
            .Invoke(i => i.RequireRole("Reader"))
            .Invoke("Stamp", i => i.RequireRole("Admin")));
    }

    private Task<string> Stamp() => Task.FromResult("stamped");
    private Task<string> Other() => Task.FromResult("other");
}

/// <summary>Control: two GENERIC Invoke rules — last-write-wins by design, not a duplicate.</summary>
internal sealed class Oar546TwoGenericProfile : EntitySetProfile<int, Oar546Widget>
{
    public Oar546TwoGenericProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar546TwoGeneric";
        BindFunction(Stamp);
        ConfigureAuthorization(a => a
            .Invoke(i => i.RequireRole("Reader"))
            .Invoke(i => i.RequireRole("Admin")));
    }

    private Task<string> Stamp() => Task.FromResult("stamped");
}
