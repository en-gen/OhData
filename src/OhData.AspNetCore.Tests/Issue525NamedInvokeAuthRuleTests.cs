using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── #525: a named Invoke(...) authorization rule that silently matches nothing ───────────────
//
// ResolveOperationRule matched OperationAuthRule.BoundOperationName with StringComparison.Ordinal
// while every route template and operation segment it governs is matched case-insensitively. A rule
// written as Invoke("stamp", …) against an operation declared as `Stamp` therefore resolved to
// NOTHING: the rule was discarded and the route fell back to the generic Invoke rule — or, with no
// generic rule, to no requirement at all. Fails OPEN, on an authorization rule, with no startup
// error and no warning.
//
// Two fixes, because the comparer alone leaves the class open — a MISSPELLED name (not merely
// miscased) still evaporates silently:
//   1. the comparer is OrdinalIgnoreCase, matching the routing it governs;
//   2. every Invoke(name, …) rule must resolve to a bound operation the profile actually declares,
//      or MapOhData() throws. There is no legitimate reason to configure authorization for an
//      operation that does not exist.
public class Issue525NamedInvokeAuthRuleTests
{
    private static HttpRequestMessage Req(string path, string? identity = null, string? roles = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, path);
        if (identity is not null) r.Headers.Add(PerOpAuthHandler.IdentityHeader, identity);
        if (roles is not null) r.Headers.Add(PerOpAuthHandler.RolesHeader, roles);
        return r;
    }

    // ── the comparer half ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The headline case, and the worst one: the ONLY rule on the profile is a miscased named
    /// Invoke rule, so an unmatched rule leaves the route with no requirement whatsoever. Anonymous
    /// must be refused.
    /// </summary>
    [Fact]
    public async Task MiscasedNamedRule_WithNoGenericRule_StillGuardsTheOperation()
    {
        await using TestFixture fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Oar525MiscasedOnlyProfile>());

        using HttpResponseMessage anon = await fx.Client.SendAsync(Req("/odata/Oar525Miscased/Stamp"));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        // Bounding half: the rule really is the miscased one being honoured, not a blanket denial.
        using HttpResponseMessage admin =
            await fx.Client.SendAsync(Req("/odata/Oar525Miscased/Stamp", "u", "Admin"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    /// <summary>
    /// The quieter case: a generic Invoke rule exists, so the discarded named rule does not leave
    /// the route naked — it leaves it governed by the WRONG (looser) rule. Both directions are
    /// asserted, because either one alone could pass for the wrong reason.
    /// </summary>
    [Fact]
    public async Task MiscasedNamedRule_BeatsTheGenericRule_RatherThanFallingBackToIt()
    {
        await using TestFixture fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Oar525FallbackProfile>());

        // The generic rule (Reader) must NOT govern the named operation.
        using HttpResponseMessage reader =
            await fx.Client.SendAsync(Req("/odata/Oar525Fallback/Stamp", "u", "Reader"));
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);

        // …and the named rule (Admin) must.
        using HttpResponseMessage admin =
            await fx.Client.SendAsync(Req("/odata/Oar525Fallback/Stamp", "u", "Admin"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    /// <summary>
    /// Control. An exactly-cased named rule has always worked and must keep working — without this
    /// the two tests above could pass on a change that simply denies everything named.
    /// </summary>
    [Fact]
    public async Task ExactlyCasedNamedRule_StillGuardsTheOperation()
    {
        await using TestFixture fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Oar525ExactProfile>());

        using HttpResponseMessage anon = await fx.Client.SendAsync(Req("/odata/Oar525Exact/Stamp"));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        using HttpResponseMessage admin =
            await fx.Client.SendAsync(Req("/odata/Oar525Exact/Stamp", "u", "Admin"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    // ── the startup-validation half ──────────────────────────────────────────────────────────

    /// <summary>
    /// A MISSPELLED name is the case the comparer fix cannot reach. It is a typo in every realistic
    /// reading, and it evaporates exactly as the miscased one did.
    /// </summary>
    [Fact]
    public async Task MisspelledNamedRule_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PerOpAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar525TypoProfile>()));

        Assert.Contains("Oar525Typo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Stampp", ex.Message, StringComparison.Ordinal);
        // The message must name what IS declared, or it tells the developer nothing actionable.
        Assert.Contains("'Stamp'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The degenerate shape: a named Invoke rule on a profile that declares no bound operation at
    /// all. The message must say so rather than offering an empty candidate list.
    /// </summary>
    [Fact]
    public async Task NamedRule_OnAProfileWithNoBoundOperations_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PerOpAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar525NoOpsProfile>()));

        Assert.Contains("Oar525NoOps", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Stamp", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no bound function or action", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Control. ENTITY-level operations are declared through a different Bind* family and land in
    /// the same two lists; a rule naming one must not be mistaken for a typo. A miscased spelling of
    /// one must not either — the validation has to use the same comparer as the resolution it
    /// guards, or it becomes a second, louder copy of this very bug.
    /// </summary>
    [Fact]
    public async Task NamedRules_ForEntityLevelAndMiscasedOperations_StartCleanly()
    {
        await using TestFixture fx = await PerOpAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Oar525EntityLevelProfile>());

        using HttpResponseMessage metadata = await fx.Client.GetAsync("/odata/$metadata");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
    }
}

// ── fixtures ─────────────────────────────────────────────────────────────────────────────────

internal class Oar525Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>#525: the only rule is a miscased named one — an unmatched rule leaves the route naked.</summary>
internal sealed class Oar525MiscasedOnlyProfile : EntitySetProfile<int, Oar525Widget>
{
    public Oar525MiscasedOnlyProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar525Miscased";
        BindFunction(Stamp);
        ConfigureAuthorization(a => a.Invoke("stamp", i => i.RequireRole("Admin")));
    }

    private Task<string> Stamp() => Task.FromResult("stamped");
}

/// <summary>#525: a generic Invoke rule the discarded named rule silently falls back to.</summary>
internal sealed class Oar525FallbackProfile : EntitySetProfile<int, Oar525Widget>
{
    public Oar525FallbackProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar525Fallback";
        BindFunction(Stamp);
        ConfigureAuthorization(a => a
            .Invoke(i => i.RequireRole("Reader"))
            .Invoke("stamp", i => i.RequireRole("Admin")));
    }

    private Task<string> Stamp() => Task.FromResult("stamped");
}

/// <summary>Control: the exactly-cased spelling, which has always resolved.</summary>
internal sealed class Oar525ExactProfile : EntitySetProfile<int, Oar525Widget>
{
    public Oar525ExactProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar525Exact";
        BindFunction(Stamp);
        ConfigureAuthorization(a => a.Invoke("Stamp", i => i.RequireRole("Admin")));
    }

    private Task<string> Stamp() => Task.FromResult("stamped");
}

/// <summary>#525: a misspelled name — the case the comparer cannot rescue.</summary>
internal sealed class Oar525TypoProfile : EntitySetProfile<int, Oar525Widget>
{
    public Oar525TypoProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar525Typo";
        BindFunction(Stamp);
        ConfigureAuthorization(a => a.Invoke("Stampp", i => i.RequireRole("Admin")));
    }

    private Task<string> Stamp() => Task.FromResult("stamped");
}

/// <summary>#525: a named rule on a profile with no bound operation at all.</summary>
internal sealed class Oar525NoOpsProfile : EntitySetProfile<int, Oar525Widget>
{
    public Oar525NoOpsProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar525NoOps";
        GetById = (id, _) => OhDataResult.Success<Oar525Widget>(new Oar525Widget { Id = id });
        ConfigureAuthorization(a => a.Invoke("Stamp", i => i.RequireRole("Admin")));
    }
}

/// <summary>Control: entity-level operations, one named exactly and one miscased.</summary>
internal sealed class Oar525EntityLevelProfile : EntitySetProfile<int, Oar525Widget>
{
    public Oar525EntityLevelProfile() : base(x => x.Id)
    {
        EntitySetName = "Oar525EntityLevel";
        GetById = (id, _) => OhDataResult.Success<Oar525Widget>(new Oar525Widget { Id = id });
        BindEntityFunction(Tag);
        BindEntityAction(Seal);
        ConfigureAuthorization(a => a
            .Invoke("Tag", i => i.RequireRole("Admin"))
            .Invoke("seal", i => i.RequireRole("Admin")));
    }

    private Task<string> Tag(int key) => Task.FromResult("tag");
    private Task<string> Seal(int key) => Task.FromResult("seal");
}
