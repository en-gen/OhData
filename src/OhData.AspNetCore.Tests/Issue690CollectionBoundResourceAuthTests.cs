using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── #690/#696: .RequireResource() is dropped on a COLLECTION-bound operation's route ─────────
//
// Resource-based authorization (#199 Layer B) is evaluated by AttachResourceFilter against the
// entity loaded from the route's {key} segment. A collection-bound function or action is mapped as
// /{Set}/{Name} and carries no key, so on that route three gates each decline: the filter finds no
// "key" route value and calls next; ApplyAuthRequirements has no arm for
// AuthRequirementKind.Resource, so the requirement emits no endpoint gate; and #487's
// anonymous-route audit sees a non-null rule and reads it as authorized.
//
// The question is what the ROUTE is left enforcing, asked per collection-bound operation against
// the rule that governs it — not what the RULE reaches elsewhere. A rule carrying Resource and
// nothing else leaves the route with no requirement at all and is refused, as #487 refuses the same
// shape on an unbound operation. A rule carrying Resource alongside coarse requirements still gates
// the route with those; only the narrowing is dropped, so it warns.
public class Issue690CollectionBoundResourceAuthTests
{
    private static HttpRequestMessage Req(string path, string? identity = null, string? roles = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, path);
        if (identity is not null) r.Headers.Add(PerOpAuthHandler.IdentityHeader, identity);
        if (roles is not null) r.Headers.Add(PerOpAuthHandler.RolesHeader, roles);
        return r;
    }

    private static Task<TestFixture> BuildAsync(Action<OhDataBuilder> configure, WarningCapture capture) =>
        ResourceAuthTestHost.BuildAsync(
            configure, extraServices: s => s.AddSingleton<ILoggerProvider>(capture));

    private static IReadOnlyList<string> ResourceWarnings(WarningCapture capture) =>
        capture.Warnings.Where(w => w.Contains(".RequireResource()", StringComparison.Ordinal)).ToList();

    // ── refused: the route is left enforcing nothing ─────────────────────────────────────────

    /// <summary>
    /// The headline shape. Anonymous <c>GET /odata/{Set}/Peek</c> served 200 with the handler
    /// executed; the host must not start.
    /// </summary>
    [Fact]
    public async Task ResourceOnlyRule_OnACollectionBoundFunction_IsRefusedAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar690CollectionOnlyProfile>()));

        Assert.Contains("Oar690CollectionOnly", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Invoke(...)", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".RequireResource() and nothing else", ex.Message, StringComparison.Ordinal);
        // The message has to name the route and the remedies, not merely the rule.
        Assert.Contains("'Peek'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GET /Oar690CollectionOnly/Peek", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NO requirement is enforced at all", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BindEntityFunction/BindEntityAction", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A NAMED rule reaches the same route, and is resolved through the comparison
    /// <c>ResolveOperationRule</c> uses, so a miscased name is caught rather than read as a rule
    /// that names nothing.
    /// </summary>
    [Fact]
    public async Task NamedResourceOnlyRule_OnACollectionBoundOperation_IsRefusedAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar690NamedCollectionProfile>()));

        Assert.Contains("Oar690NamedCollection", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Invoke(\"peek\", ...)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Peek'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A collection-bound ACTION is the POST twin and has to be named as one.</summary>
    [Fact]
    public async Task ResourceOnlyRule_OnACollectionBoundAction_IsRefusedAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar690CollectionActionProfile>()));

        Assert.Contains("action 'Sweep'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("POST /Oar690CollectionAction/Sweep", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shape that makes the decision per ROUTE rather than per RULE. A generic
    /// <c>Invoke(...)</c> rule beside an entity-bound operation is honoured on the entity-bound
    /// route and dropped on the collection-bound one, so an entity-bound sibling does not redeem it:
    /// anonymous <c>GET /odata/{Set}/Peek</c> served 200 with the handler executed while
    /// <c>…({key})/Tag</c> was resource-checked.
    /// </summary>
    [Fact]
    public async Task ResourceOnlyRule_BesideAnEntityBoundOperation_IsRefusedAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar690BothLevelsProfile>()));

        Assert.Contains("Oar690BothLevels", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Peek'", ex.Message, StringComparison.Ordinal);
        // The entity-bound sibling is honoured and must not be the one named.
        Assert.DoesNotContain("'Tag'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// #696. <c>All(...)</c> covers keyed categories, but the route the rule governs here still has
    /// no key and still enforces nothing, so it lands on the same side by the same test.
    /// </summary>
    [Fact]
    public async Task AllSelectorResourceOnlyRule_WithACollectionBoundOperation_IsRefusedAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<Oar690AllSelectorProfile>()));

        Assert.Contains("Oar690AllSelector", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GET /Oar690AllSelector/Peek", ex.Message, StringComparison.Ordinal);
    }

    // ── warned: the coarse half still gates the route ────────────────────────────────────────

    /// <summary>
    /// The population a refusal would break. The route is gated by the coarse requirement, so the
    /// host starts; what is lost is only the narrowing, and both halves of that are asserted on the
    /// wire: <c>bob</c> is refused on the keyed route by the resource check and admitted on the
    /// collection-bound one, which is precisely what the warning describes.
    /// </summary>
    [Fact]
    public async Task ResourceWithCoarseRule_OnACollectionBoundOperation_StartsAndWarns()
    {
        Oar690WarnProfile.PeekCalls = 0;
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            o => o.AddEntitySetProfile<Oar690WarnProfile>(), capture);

        // The coarse half really gates the collection-bound route.
        using HttpResponseMessage anon = await fx.Client.SendAsync(Req("/odata/Oar690Warn/Peek"));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        Assert.Equal(0, Oar690WarnProfile.PeekCalls);

        // …and the resource half does not: a non-owner satisfies it and reaches the handler.
        using HttpResponseMessage nonOwner = await fx.Client.SendAsync(Req("/odata/Oar690Warn/Peek", "bob"));
        Assert.Equal(HttpStatusCode.OK, nonOwner.StatusCode);
        Assert.Equal(1, Oar690WarnProfile.PeekCalls);

        // The same non-owner under the same rule IS refused on the keyed route, which is what makes
        // the collection-bound 200 above a dropped narrowing rather than a rule that does nothing.
        using HttpResponseMessage keyed = await fx.Client.SendAsync(Req("/odata/Oar690Warn(1)/Tag", "bob"));
        Assert.Equal(HttpStatusCode.Forbidden, keyed.StatusCode);
        using HttpResponseMessage owner = await fx.Client.SendAsync(Req("/odata/Oar690Warn(1)/Tag", "alice"));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        string warning = Assert.Single(ResourceWarnings(capture));
        Assert.Contains("Oar690Warn", warning, StringComparison.Ordinal);
        Assert.Contains("collection-bound function 'Peek'", warning, StringComparison.Ordinal);
        Assert.Contains("GET /Oar690Warn/Peek", warning, StringComparison.Ordinal);
        Assert.Contains("Invoke(...)", warning, StringComparison.Ordinal);
        // It must not read as "this route is open" — the coarse gate is the point of not refusing.
        Assert.Contains("coarse requirements DO gate this route", warning, StringComparison.Ordinal);
        Assert.Contains("BindEntityFunction/BindEntityAction", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// #696's warn side: <c>All(...)</c> with a coarse requirement beside it. One warning names the
    /// collection-bound operation; the keyed routes keep their resource check in both directions.
    /// </summary>
    [Fact]
    public async Task AllSelectorWithCoarseRule_WithACollectionBoundOperation_StartsAndWarns()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            o => o.AddEntitySetProfile<Oar690AllSelectorWarnProfile>(), capture);

        using HttpResponseMessage owner =
            await fx.Client.SendAsync(Req("/odata/Oar690AllWarn(1)", "alice"));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        // Without this direction a 200 proves nothing: the owner passes whether the filter attached
        // or never ran at all.
        using HttpResponseMessage nonOwner =
            await fx.Client.SendAsync(Req("/odata/Oar690AllWarn(1)", "bob"));
        Assert.Equal(HttpStatusCode.Forbidden, nonOwner.StatusCode);

        // The collection-bound route is the one that lost the narrowing.
        using HttpResponseMessage collection =
            await fx.Client.SendAsync(Req("/odata/Oar690AllWarn/Peek", "bob"));
        Assert.Equal(HttpStatusCode.OK, collection.StatusCode);

        string warning = Assert.Single(ResourceWarnings(capture));
        Assert.Contains("GET /Oar690AllWarn/Peek", warning, StringComparison.Ordinal);
    }

    /// <summary>One warning per (entity set, operation) — two operations, two records.</summary>
    [Fact]
    public async Task TwoCollectionBoundOperations_UnderOneRule_WarnOnceEach()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            o => o.AddEntitySetProfile<Oar690TwoOpsWarnProfile>(), capture);

        IReadOnlyList<string> warnings = ResourceWarnings(capture);
        Assert.Equal(2, warnings.Count);
        Assert.Single(warnings, w => w.Contains("function 'Peek'", StringComparison.Ordinal));
        Assert.Single(warnings, w => w.Contains("action 'Sweep'", StringComparison.Ordinal));
    }

    // ── silent: nothing is dropped, so nothing is said ───────────────────────────────────────

    /// <summary>
    /// Control, and the bound on both branches: they target the unreachable REQUIREMENT, not the
    /// binding level. A collection-bound operation carrying coarse requirements alone is gated, runs
    /// its handler for the caller who satisfies them, and is neither refused nor warned about.
    /// </summary>
    [Fact]
    public async Task CoarseRequirementsOnly_OnACollectionBoundOperation_AreUntouched_AndSilent()
    {
        Oar690CoarseProfile.PeekCalls = 0;
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            o => o.AddEntitySetProfile<Oar690CoarseProfile>(), capture);

        using HttpResponseMessage anon = await fx.Client.SendAsync(Req("/odata/Oar690Coarse/Peek"));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        Assert.Equal(0, Oar690CoarseProfile.PeekCalls);

        using HttpResponseMessage admin =
            await fx.Client.SendAsync(Req("/odata/Oar690Coarse/Peek", "alice", "Admin"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(1, Oar690CoarseProfile.PeekCalls);

        Assert.Empty(ResourceWarnings(capture));
    }

    /// <summary>
    /// Control: a rule governing only ENTITY-bound operations has its key on every route it reaches,
    /// so Layer B fires in both directions and nothing is reported.
    /// </summary>
    [Fact]
    public async Task ResourceRule_OnEntityBoundOperationsOnly_StartsCleanly_AndIsSilent()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            o => o.AddEntitySetProfile<Oar690EntityLevelProfile>(), capture);

        using HttpResponseMessage owner =
            await fx.Client.SendAsync(Req("/odata/Oar690EntityLevel(1)/Tag", "alice"));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        using HttpResponseMessage other =
            await fx.Client.SendAsync(Req("/odata/Oar690EntityLevel(1)/Tag", "bob"));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);

        Assert.Empty(ResourceWarnings(capture));
    }

    /// <summary>
    /// Control: a profile with no bound operation at all has no collection-bound route for the
    /// question to be asked of, whatever its rules say.
    /// </summary>
    [Fact]
    public async Task ResourceRule_OnAProfileWithNoBoundOperations_StartsCleanly_AndIsSilent()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            o => o.AddEntitySetProfile<Oar690NoOpsProfile>(), capture);

        using HttpResponseMessage owner = await fx.Client.SendAsync(Req("/odata/Oar690NoOps(1)", "alice"));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);

        using HttpResponseMessage other = await fx.Client.SendAsync(Req("/odata/Oar690NoOps(1)", "bob"));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);

        Assert.Empty(ResourceWarnings(capture));
    }

    /// <summary>
    /// Control: a named rule steals the operation from the generic one, so the governing rule — the
    /// one <c>ResolveOperationRule</c> returns — is the only one either branch asks about.
    /// </summary>
    [Fact]
    public async Task ANamedCoarseRule_DisplacesTheGenericResourceRule_AndIsSilent()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await BuildAsync(
            o => o.AddEntitySetProfile<Oar690NamedDisplacesGenericProfile>(), capture);

        using HttpResponseMessage anon = await fx.Client.SendAsync(Req("/odata/Oar690Displaced/Peek"));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        using HttpResponseMessage admin =
            await fx.Client.SendAsync(Req("/odata/Oar690Displaced/Peek", "alice", "Admin"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        Assert.Empty(ResourceWarnings(capture));
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

/// <summary>#690: Resource and nothing else, on a collection-bound function.</summary>
internal sealed class Oar690CollectionOnlyProfile : Oar690ProfileBase
{
    public Oar690CollectionOnlyProfile()
    {
        EntitySetName = "Oar690CollectionOnly";
        BindFunction(Peek);
        ConfigureAuthorization(a => a.Invoke(i => i.RequireResource()));
    }

    private Task<string> Peek() => Task.FromResult("peeked");
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

/// <summary>#690: an entity-bound sibling does not redeem the collection-bound route.</summary>
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

/// <summary>#696: All(...) with Resource alone still leaves the keyless route enforcing nothing.</summary>
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

/// <summary>#690: Resource alongside a coarse requirement — the warn population.</summary>
internal sealed class Oar690WarnProfile : Oar690ProfileBase
{
    public static int PeekCalls;

    public Oar690WarnProfile()
    {
        EntitySetName = "Oar690Warn";
        BindFunction(Peek);
        BindEntityFunction(Tag);
        ConfigureAuthorization(a => a.Invoke(i => i.RequireAuthenticatedUser().RequireResource()));
    }

    private Task<string> Peek() { PeekCalls++; return Task.FromResult("peeked"); }
    private Task<string> Tag(int key) => Task.FromResult("tag");
}

/// <summary>#696: the warn side of All(...).</summary>
internal sealed class Oar690AllSelectorWarnProfile : Oar690ProfileBase
{
    public Oar690AllSelectorWarnProfile()
    {
        EntitySetName = "Oar690AllWarn";
        BindFunction(Peek);
        ConfigureAuthorization(a => a.All(c => c.RequireAuthenticatedUser().RequireResource()));
    }

    private Task<string> Peek() => Task.FromResult("peeked");
}

/// <summary>One warning per (entity set, operation).</summary>
internal sealed class Oar690TwoOpsWarnProfile : Oar690ProfileBase
{
    public Oar690TwoOpsWarnProfile()
    {
        EntitySetName = "Oar690TwoOps";
        BindFunction(Peek);
        BindAction(Sweep);
        ConfigureAuthorization(a => a.Invoke(i => i.RequireAuthenticatedUser().RequireResource()));
    }

    private Task<string> Peek() => Task.FromResult("peeked");
    private Task<string> Sweep() => Task.FromResult("swept");
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

/// <summary>Control: entity-bound operations only.</summary>
internal sealed class Oar690EntityLevelProfile : Oar690ProfileBase
{
    public Oar690EntityLevelProfile()
    {
        EntitySetName = "Oar690EntityLevel";
        BindEntityFunction(Tag);
        ConfigureAuthorization(a => a.Invoke("tag", i => i.RequireResource()));
    }

    private Task<string> Tag(int key) => Task.FromResult("tag");
}

/// <summary>Control: no bound operation at all, so no keyless route to ask about.</summary>
internal sealed class Oar690NoOpsProfile : Oar690ProfileBase
{
    public Oar690NoOpsProfile()
    {
        EntitySetName = "Oar690NoOps";
        ConfigureAuthorization(a => a.All(c => c.RequireResource()));
    }
}

/// <summary>Control: the named rule is the one that governs, so the generic one is never asked.</summary>
internal sealed class Oar690NamedDisplacesGenericProfile : Oar690ProfileBase
{
    public Oar690NamedDisplacesGenericProfile()
    {
        EntitySetName = "Oar690Displaced";
        BindFunction(Peek);
        BindEntityFunction(Tag);
        ConfigureAuthorization(a => a
            .Invoke(i => i.RequireResource())
            .Invoke("Peek", i => i.RequireRole("Admin")));
    }

    private Task<string> Peek() => Task.FromResult("peeked");
    private Task<string> Tag(int key) => Task.FromResult("tag");
}
