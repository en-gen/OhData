using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── #690: .RequireResource() on a COLLECTION-bound operation is a silent no-op ────────────────
//
// Resource-based authorization (#199 Layer B) is evaluated by AttachResourceFilter against the
// entity loaded from the route's {key} segment. A collection-bound function or action is mapped as
// /{Set}/{Name} and carries no key, so three gates each declined for their own reason: the filter
// found no "key" route value and called next; ApplyAuthRequirements has no arm for
// AuthRequirementKind.Resource, so a rule carrying only that requirement emitted no endpoint gate;
// and the #487 anonymous-route audit saw a non-null rule and stayed quiet. Anonymous
// GET /odata/{Set}/{Name} answered 200 with the handler executed.
//
// Refused at startup, as #487 refuses the same shape on an unbound operation: there is no
// configuration in which authorizing a nonexistent {key} entity is meaningful. The test is whether
// the RULE reaches a key-based route, never the requirement kind — a generic Invoke(...) covers both
// binding levels and stays legal beside an entity-bound operation, where it is really evaluated.
public class Issue690CollectionBoundResourceAuthTests
{
    private static HttpRequestMessage Req(string path, string? identity = null, string? roles = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, path);
        if (identity is not null) r.Headers.Add(PerOpAuthHandler.IdentityHeader, identity);
        if (roles is not null) r.Headers.Add(PerOpAuthHandler.RolesHeader, roles);
        return r;
    }

    // ── the refusal ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The headline shape: a generic Invoke rule whose only route is a collection-bound function.
    /// It served an anonymous 200 with the handler reached; it must not start at all.
    /// </summary>
    [Fact]
    public async Task GenericInvokeRule_ReachingOnlyACollectionBoundFunction_IsRefusedAtStartup()
    {
        Oar690CollectionOnlyProfile.PeekCalls = 0;

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar690CollectionOnlyProfile>()));

        Assert.Contains("Oar690CollectionOnly", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Invoke(...)", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".RequireResource()", ex.Message, StringComparison.Ordinal);
        // The message has to name the operation and the remedy, not merely the rule.
        Assert.Contains("'Peek'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GET /Oar690CollectionOnly/Peek", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BindEntityFunction/BindEntityAction", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, Oar690CollectionOnlyProfile.PeekCalls);
    }

    /// <summary>
    /// A NAMED Invoke rule reaches the same hole, and is refused through the same resolution — the
    /// comparer ResolveOperationRule uses, so a miscased name is caught rather than passing as a
    /// rule that names nothing.
    /// </summary>
    [Fact]
    public async Task NamedInvokeRule_OnACollectionBoundOperation_IsRefusedAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar690NamedCollectionProfile>()));

        Assert.Contains("Oar690NamedCollection", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Invoke(\"peek\", ...)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Peek'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A collection-bound ACTION is the POST twin and has to be named as one.</summary>
    [Fact]
    public async Task GenericInvokeRule_ReachingOnlyACollectionBoundAction_IsRefusedAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar690CollectionActionProfile>()));

        Assert.Contains("action 'Sweep'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("POST /Oar690CollectionAction/Sweep", ex.Message, StringComparison.Ordinal);
    }

    // ── what stays legal ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The case the refusal must not catch. A generic Invoke rule covers both binding levels, so
    /// beside an entity-bound operation it has a key-based route and the requirement is really
    /// evaluated. Asserted on the wire in both directions, not merely by starting cleanly.
    /// </summary>
    [Fact]
    public async Task GenericInvokeRule_BesideAnEntityBoundOperation_StartsCleanly_AndLayerBFires()
    {
        await using TestFixture fx = await ResourceAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Oar690BothLevelsProfile>());

        // The owner passes the resource check…
        using HttpResponseMessage owner =
            await fx.Client.SendAsync(Req("/odata/Oar690BothLevels(1)/Tag", "alice"));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        // …and a non-owner is refused by it, which is what proves the rule is honoured here.
        using HttpResponseMessage other =
            await fx.Client.SendAsync(Req("/odata/Oar690BothLevels(1)/Tag", "bob"));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    /// <summary>
    /// A rule reaching a keyed CATEGORY keeps its key-based routes whatever its Invoke half reaches,
    /// so All(...) — the documented refinement idiom — starts beside a collection-bound operation.
    /// </summary>
    [Fact]
    public async Task AllSelectorRule_WithOnlyACollectionBoundOperation_StartsCleanly()
    {
        await using TestFixture fx = await ResourceAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Oar690AllSelectorProfile>());

        using HttpResponseMessage owner =
            await fx.Client.SendAsync(Req("/odata/Oar690AllSelector(1)", "alice"));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    /// <summary>
    /// Control, and the bound on the refusal: it targets the unreachable REQUIREMENT, not the
    /// binding level. A collection-bound operation carrying coarse requirements is still gated, and
    /// the handler still runs for the caller who satisfies them.
    /// </summary>
    [Fact]
    public async Task CoarseRequirements_OnACollectionBoundOperation_AreUntouched()
    {
        Oar690CoarseProfile.PeekCalls = 0;
        await using TestFixture fx = await ResourceAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Oar690CoarseProfile>());

        using HttpResponseMessage anon = await fx.Client.SendAsync(Req("/odata/Oar690Coarse/Peek"));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        Assert.Equal(0, Oar690CoarseProfile.PeekCalls);

        using HttpResponseMessage admin =
            await fx.Client.SendAsync(Req("/odata/Oar690Coarse/Peek", "alice", "Admin"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(1, Oar690CoarseProfile.PeekCalls);
    }

    /// <summary>
    /// Control: a named rule on an ENTITY-bound operation has its key-based route and is untouched.
    /// </summary>
    [Fact]
    public async Task NamedInvokeRule_OnAnEntityBoundOperation_StartsCleanly_AndLayerBFires()
    {
        await using TestFixture fx = await ResourceAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<Oar690NamedEntityLevelProfile>());

        using HttpResponseMessage owner =
            await fx.Client.SendAsync(Req("/odata/Oar690NamedEntityLevel(1)/Tag", "alice"));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        using HttpResponseMessage other =
            await fx.Client.SendAsync(Req("/odata/Oar690NamedEntityLevel(1)/Tag", "bob"));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }
}

// ── fixtures ─────────────────────────────────────────────────────────────────────────────────

internal abstract class Oar690ProfileBase : EntitySetProfile<int, ResOwnedItem>
{
    protected Oar690ProfileBase() : base(x => x.Id)
    {
        var store = new List<ResOwnedItem> { new() { Id = 1, Owner = "alice", Name = "A" } };
        GetById = (id, ct) => OhDataResult.Success(store.FirstOrDefault(x => x.Id == id));
    }
}

/// <summary>#690: the reported shape — a rule whose only route is a collection-bound function.</summary>
internal sealed class Oar690CollectionOnlyProfile : Oar690ProfileBase
{
    public static int PeekCalls;

    public Oar690CollectionOnlyProfile()
    {
        EntitySetName = "Oar690CollectionOnly";
        BindFunction(Peek);
        ConfigureAuthorization(a => a.Invoke(i => i.RequireResource()));
    }

    private Task<string> Peek() { PeekCalls++; return Task.FromResult("peeked"); }
}

/// <summary>#690: the named half, spelled in a different case than the declaration.</summary>
internal sealed class Oar690NamedCollectionProfile : Oar690ProfileBase
{
    public Oar690NamedCollectionProfile()
    {
        EntitySetName = "Oar690NamedCollection";
        BindFunction(Peek);
        ConfigureAuthorization(a => a.Invoke("peek", i => i.RequireResource()));
    }

    private Task<string> Peek() => Task.FromResult("peeked");
}

/// <summary>#690: the collection-bound ACTION twin.</summary>
internal sealed class Oar690CollectionActionProfile : Oar690ProfileBase
{
    public Oar690CollectionActionProfile()
    {
        EntitySetName = "Oar690CollectionAction";
        BindAction(Sweep);
        ConfigureAuthorization(a => a.Invoke(i => i.RequireResource()));
    }

    private Task<string> Sweep() => Task.FromResult("swept");
}

/// <summary>Stays legal: a generic Invoke rule with an entity-bound operation to apply to.</summary>
internal sealed class Oar690BothLevelsProfile : Oar690ProfileBase
{
    public Oar690BothLevelsProfile()
    {
        EntitySetName = "Oar690BothLevels";
        BindFunction(Peek);
        BindEntityFunction(Tag);
        ConfigureAuthorization(a => a.Invoke(i => i.RequireResource()));
    }

    private Task<string> Peek() => Task.FromResult("peeked");
    private Task<string> Tag(int key) => Task.FromResult("tag");
}

/// <summary>Stays legal: All(...) reaches keyed categories whatever its Invoke half reaches.</summary>
internal sealed class Oar690AllSelectorProfile : Oar690ProfileBase
{
    public Oar690AllSelectorProfile()
    {
        EntitySetName = "Oar690AllSelector";
        BindFunction(Peek);
        ConfigureAuthorization(a => a.All(c => c.RequireResource()));
    }

    private Task<string> Peek() => Task.FromResult("peeked");
}

/// <summary>Control: coarse requirements on a collection-bound operation are unaffected.</summary>
internal sealed class Oar690CoarseProfile : Oar690ProfileBase
{
    public static int PeekCalls;

    public Oar690CoarseProfile()
    {
        EntitySetName = "Oar690Coarse";
        BindFunction(Peek);
        ConfigureAuthorization(a => a.Invoke(i => i.RequireRole("Admin")));
    }

    private Task<string> Peek() { PeekCalls++; return Task.FromResult("peeked"); }
}

/// <summary>Control: a named rule on an entity-bound operation.</summary>
internal sealed class Oar690NamedEntityLevelProfile : Oar690ProfileBase
{
    public Oar690NamedEntityLevelProfile()
    {
        EntitySetName = "Oar690NamedEntityLevel";
        BindEntityFunction(Tag);
        ConfigureAuthorization(a => a.Invoke("tag", i => i.RequireResource()));
    }

    private Task<string> Tag(int key) => Task.FromResult("tag");
}
