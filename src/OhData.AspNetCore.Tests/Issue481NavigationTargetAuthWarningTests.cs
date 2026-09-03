using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #481 / #368: authorization is PER PROFILE and does not compose across a navigation. A navigation
// declared on an unprotected parent serves the target set's rows -- and, with $ref/nav-POST handlers,
// WRITES them -- under the DECLARING profile's rule. Measured across 19 route shapes; MS OData does
// the same, containing no authorization code at all.
//
// The owner's ruling is a startup Warning plus documentation. NO request-path behaviour change:
// nothing here asserts a status code.
//
// This suite pins the warning's TARGETING as hard as its content, because one that fires on the
// ordinary scoped-navigation pattern would be ignored:
//   POSITIVE -- a stricter target through each of the three declaration families, the route-less
//     declaration (leaks via $expand with no route at all), the batchGetAll overload (which never
//     sets ChildEntitySetName), and the ambiguous two-sets-over-one-type shape.
//   NEGATIVE -- equal authorization, a LESS strict target, none anywhere, an undeclared
//     convention-discovered navigation, a target with no profile, and a target whose extra
//     requirement lives on a CATEGORY this navigation does not expose.

#region fixtures

public sealed class W481Child
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Secret { get; set; } = "";
}

public sealed class W481Parent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<W481Child>? Children { get; set; }
    public W481Child? Primary { get; set; }
}

/// <summary>A second child CLR type, so a fixture can register a target with no profile at all.</summary>
public sealed class W481Orphan
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class W481OrphanParent
{
    public int Id { get; set; }
    public IEnumerable<W481Orphan>? Orphans { get; set; }
}

internal static class W481Data
{
    internal static readonly List<W481Parent> Parents = new() { new() { Id = 1, Name = "P1" } };
    internal static readonly List<W481Child> Children = new()
    {
        new() { Id = 10, ParentId = 1, Secret = "SECRET" },
    };
}

/// <summary>The PROTECTED target: its own profile requires the "admin" role.</summary>
public sealed class W481AdminChildProfile : EntitySetProfile<int, W481Child>
{
    public W481AdminChildProfile() : base(x => x.Id)
    {
        EntitySetName = "W481Children";
        RequireRoles("admin");
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Child>>(W481Data.Children);
    }
}

/// <summary>An UNPROTECTED sibling set over the same CLR type — the (B) ambiguity shape.</summary>
public sealed class W481PublicChildProfile : EntitySetProfile<int, W481Child>
{
    public W481PublicChildProfile() : base(x => x.Id)
    {
        EntitySetName = "W481PublicChildren";
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Child>>(W481Data.Children);
    }
}

/// <summary>A target with NO authorization at all — the "less strict" control's target.</summary>
public sealed class W481OpenChildProfile : EntitySetProfile<int, W481Child>
{
    public W481OpenChildProfile() : base(x => x.Id)
    {
        EntitySetName = "W481OpenChildren";
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Child>>(W481Data.Children);
    }
}

/// <summary>
/// A target whose extra requirement lives on the WRITE categories only: reads are explicitly
/// anonymous. The false-positive control for the category logic.
/// </summary>
public sealed class W481WriteGuardedChildProfile : EntitySetProfile<int, W481Child>
{
    public W481WriteGuardedChildProfile() : base(x => x.Id)
    {
        EntitySetName = "W481WriteGuardedChildren";
        ConfigureAuthorization(auth => auth
            .Read(r => r.AllowAnonymous())
            .Writes(w => w.RequireRole("admin")));
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Child>>(W481Data.Children);
    }
}

/// <summary>ANONYMOUS parent, full navigation surface into the protected set (probe A).</summary>
public sealed class W481RoutedParentProfile : EntitySetProfile<int, W481Parent>
{
    public W481RoutedParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W481RoutedParents";
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
        GetById = (id, _) => OhDataResult.SuccessTask(W481Data.Parents.FirstOrDefault(p => p.Id == id));

        HasMany<W481Child>(
            navigation: x => x.Children!,
            getAll: (pid, _) => Task.FromResult<IEnumerable<W481Child>>(
                W481Data.Children.Where(c => c.ParentId == pid)),
            post: (_, child, _) => Task.FromResult<W481Child?>(child),
            refTargetEntitySet: "W481Children",
            addRef: (_, _, _) => Task.CompletedTask,
            removeRef: (_, _, _) => Task.CompletedTask);

        HasOptional<W481Child>(
            navigation: x => x.Primary!,
            get: (pid, _) => Task.FromResult(W481Data.Children.FirstOrDefault(c => c.ParentId == pid)),
            refTargetEntitySet: null);
    }
}

/// <summary>
/// Probe B-1: a bare <c>HasMany</c> with NO handler and NO route, which still leaks via
/// <c>$expand</c>. The warning must fire on the DECLARED navigation, not the routed one.
/// </summary>
public sealed class W481BareParentProfile : EntitySetProfile<int, W481Parent>
{
    public W481BareParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W481BareParents";
        ExpandEnabled = true;
        HasMany<W481Child>(x => x.Children!);
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
    }
}

/// <summary>
/// Probe case 7: the <c>batchGetAll</c> overload, which never sets <c>ChildEntitySetName</c> —
/// so a check resolving the target through it would go silent here.
/// </summary>
public sealed class W481BatchParentProfile : EntitySetProfile<int, W481Parent>
{
    public W481BatchParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W481BatchParents";
        ExpandEnabled = true;
        HasMany<W481Child>(
            navigation: x => x.Children!,
            batchGetAll: (ids, _) => Task.FromResult(
                W481Data.Children.Where(c => ids.Contains(c.ParentId)).ToLookup(c => c.ParentId)));
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
    }
}

/// <summary>
/// <c>HasOptional</c>, which never sets <c>NavItemType</c> — so a check resolving the target
/// through it would go silent here too.
/// </summary>
public sealed class W481SingleParentProfile : EntitySetProfile<int, W481Parent>
{
    public W481SingleParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W481SingleParents";
        HasOptional<W481Child>(
            navigation: x => x.Primary!,
            get: (pid, _) => Task.FromResult(W481Data.Children.FirstOrDefault(c => c.ParentId == pid)),
            refTargetEntitySet: null);
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
        GetById = (id, _) => OhDataResult.SuccessTask(W481Data.Parents.FirstOrDefault(p => p.Id == id));
    }
}

/// <summary>EQUAL authorization — the same requirement on both sides. Must stay silent.</summary>
public sealed class W481EqualParentProfile : EntitySetProfile<int, W481Parent>
{
    public W481EqualParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W481EqualParents";
        RequireRoles("admin");
        ExpandEnabled = true;
        HasMany<W481Child>(x => x.Children!);
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
    }
}

/// <summary>
/// #549: a target whose extra protection is <c>RequireResource()</c> — an instance-level (Layer B)
/// check evaluated against the entity loaded from the route's own <c>{key}</c>.
/// </summary>
public sealed class W549ResourceChildProfile : EntitySetProfile<int, W481Child>
{
    public W549ResourceChildProfile() : base(x => x.Id)
    {
        EntitySetName = "W549ResourceChildren";
        ConfigureAuthorization(auth => auth.All(a => a.RequireResource()));
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Child>>(W481Data.Children);
        GetById = (id, _) => OhDataResult.SuccessTask(W481Data.Children.FirstOrDefault(c => c.Id == id));
    }
}

/// <summary>
/// #549's false negative: a declaring profile carrying the IDENTICAL <c>RequireResource()</c>. The
/// two render the same token, so the token subtraction cancelled them and the warning went silent —
/// even though this profile's Layer B filter evaluates a <c>W481Parent</c> and never sees a child
/// row, so the target's instance-level check genuinely is not applied.
/// </summary>
public sealed class W549ResourceParentProfile : EntitySetProfile<int, W481Parent>
{
    public W549ResourceParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W549ResourceParents";
        ConfigureAuthorization(auth => auth.All(a => a.RequireResource()));
        ExpandEnabled = true;
        HasMany<W481Child>(x => x.Children!);
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
        GetById = (id, _) => OhDataResult.SuccessTask(W481Data.Parents.FirstOrDefault(p => p.Id == id));
    }
}

/// <summary>
/// The control for #549: an identical ROLE requirement on both sides. A role is a statement about
/// the CALLER, so token equality really does imply the check is applied and this must stay SILENT.
/// Without this pair the fix could have been "never cancel anything", which would fire on every
/// correctly-configured navigation.
/// </summary>
public sealed class W549RoleChildProfile : EntitySetProfile<int, W481Child>
{
    public W549RoleChildProfile() : base(x => x.Id)
    {
        EntitySetName = "W549RoleChildren";
        ConfigureAuthorization(auth => auth.All(a => a.RequireRole("admin")));
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Child>>(W481Data.Children);
    }
}

public sealed class W549RoleParentProfile : EntitySetProfile<int, W481Parent>
{
    public W549RoleParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W549RoleParents";
        ConfigureAuthorization(auth => auth.All(a => a.RequireRole("admin")));
        ExpandEnabled = true;
        HasMany<W481Child>(x => x.Children!);
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
    }
}

/// <summary>
/// A STRICTER declaring profile over a LESS strict target. Must stay silent — the exposure runs
/// under the stronger rule, which is the direction that is never a hazard.
/// </summary>
public sealed class W481StricterParentProfile : EntitySetProfile<int, W481Parent>
{
    public W481StricterParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W481StricterParents";
        RequireRoles("admin");
        ExpandEnabled = true;
        HasMany<W481Child>(x => x.Children!);
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
    }
}

/// <summary>No authorization anywhere. Must stay silent.</summary>
public sealed class W481OpenParentProfile : EntitySetProfile<int, W481Parent>
{
    public W481OpenParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W481OpenParents";
        ExpandEnabled = true;
        HasMany<W481Child>(x => x.Children!);
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
    }
}

/// <summary>
/// Probe B-2: the navigation is NOT declared — the convention builder discovered it. Measured as
/// genuinely not exposed, so warning about it would be a false positive (#440's warning already
/// names this configuration gap, and says something different about it).
/// </summary>
public sealed class W481UndeclaredParentProfile : EntitySetProfile<int, W481Parent>
{
    public W481UndeclaredParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W481UndeclaredParents";
        ExpandEnabled = true;
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
    }
}

/// <summary>
/// A declared navigation whose target type is exposed by NO entity set at all — EDM case (C),
/// where <c>FindNavigationTarget</c> also returns a placeholder. There is no target rule to fail
/// to apply, so there is nothing to warn about.
/// </summary>
public sealed class W481OrphanParentProfile : EntitySetProfile<int, W481OrphanParent>
{
    public W481OrphanParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W481OrphanParents";
        ExpandEnabled = true;
        HasMany<W481Orphan>(
            navigation: x => x.Orphans!,
            getAll: (_, _) => Task.FromResult<IEnumerable<W481Orphan>>(Array.Empty<W481Orphan>()));
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481OrphanParent>>(Array.Empty<W481OrphanParent>());
    }
}

/// <summary>
/// Read-only exposure of a target guarded only on WRITES. The read categories agree, and this
/// navigation registers no <c>$ref</c> and no nav-POST route, so nothing the target guards is
/// reachable. Must stay silent.
/// </summary>
public sealed class W481ReadOnlyNavParentProfile : EntitySetProfile<int, W481Parent>
{
    public W481ReadOnlyNavParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W481ReadOnlyNavParents";
        ExpandEnabled = true;
        HasMany<W481Child>(
            navigation: x => x.Children!,
            getAll: (pid, _) => Task.FromResult<IEnumerable<W481Child>>(
                W481Data.Children.Where(c => c.ParentId == pid)));
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
    }
}

/// <summary>
/// The SAME write-guarded target, but this navigation DOES register the nav-POST and <c>$ref</c>
/// write routes — which the probe measured executing anonymously. Must warn, and must name the
/// write categories rather than reads.
/// </summary>
public sealed class W481WriteNavParentProfile : EntitySetProfile<int, W481Parent>
{
    public W481WriteNavParentProfile() : base(x => x.Id)
    {
        EntitySetName = "W481WriteNavParents";
        HasMany<W481Child>(
            navigation: x => x.Children!,
            getAll: (pid, _) => Task.FromResult<IEnumerable<W481Child>>(
                W481Data.Children.Where(c => c.ParentId == pid)),
            post: (_, child, _) => Task.FromResult<W481Child?>(child),
            refTargetEntitySet: "W481WriteGuardedChildren",
            addRef: (_, _, _) => Task.CompletedTask,
            removeRef: (_, _, _) => Task.CompletedTask);
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W481Parent>>(W481Data.Parents);
        GetById = (id, _) => OhDataResult.SuccessTask(W481Data.Parents.FirstOrDefault(p => p.Id == id));
    }
}

#endregion

public sealed class Issue481NavigationTargetAuthWarningTests
{
    /// <summary>
    /// The sentence that identifies THIS warning, distinct from #440's undeclared-navigation
    /// warning and #313's unbounded-$expand warning over the same navigations.
    /// </summary>
    private const string Marker = "authorization is not applied across a navigation";

    private static async Task<(WarningCapture Capture, TestFixture Fixture)> BuildAsync(
        Action<OhDataBuilder> configure)
    {
        var capture = new WarningCapture();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            configure,
            configureServices: services => services.AddSingleton<ILoggerProvider>(capture));
        return (capture, fx);
    }

    private static string[] NavAuthWarnings(WarningCapture capture) =>
        capture.Warnings.Where(w => w.Contains(Marker, StringComparison.Ordinal)).ToArray();

    // ── positives ────────────────────────────────────────────────────────────

    /// <summary>
    /// The filed shape: an anonymous parent with a full navigation surface into an admin-gated
    /// child set. One warning per affected DECLARATION — two navigations, two warnings.
    /// </summary>
    [Fact]
    public async Task RoutedNavigationsIntoAStricterTarget_EachWarnOnce()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481RoutedParentProfile>()
            .AddEntitySetProfile<W481AdminChildProfile>());
        await using TestFixture _ = fx;

        string[] hits = NavAuthWarnings(capture);
        Assert.Equal(2, hits.Length);
        Assert.Single(hits, h => h.Contains("'Children'", StringComparison.Ordinal));
        Assert.Single(hits, h => h.Contains("'Primary'", StringComparison.Ordinal));
        foreach (string hit in hits)
        {
            Assert.Contains("'W481RoutedParents'", hit, StringComparison.Ordinal);
            Assert.Contains("'W481Children'", hit, StringComparison.Ordinal);
            Assert.Contains("one of the roles (admin)", hit, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Probe B-1 — the case with the weakest opt-in argument, and the one a route-gated check
    /// would miss: a bare <c>HasMany</c> with no handler and no route still serves the protected
    /// rows through <c>$expand</c>.
    /// </summary>
    [Fact]
    public async Task DeclaredNavigationWithNoRouteAtAll_StillWarns()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481BareParentProfile>()
            .AddEntitySetProfile<W481AdminChildProfile>());
        await using TestFixture _ = fx;

        string hit = Assert.Single(NavAuthWarnings(capture));
        Assert.Contains("'W481BareParents'", hit, StringComparison.Ordinal);
        Assert.Contains("'Children'", hit, StringComparison.Ordinal);
        Assert.Contains("'W481Children'", hit, StringComparison.Ordinal);
    }

    /// <summary>
    /// Probe case 7. <c>HasMany(nav, batchGetAll)</c> never sets <c>ChildEntitySetName</c>, so a
    /// check that resolved the target through it would go silent on exactly the overload the probe
    /// caught leaking.
    /// </summary>
    [Fact]
    public async Task BatchHandlerOverload_WhichNeverSetsChildEntitySetName_StillWarns()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481BatchParentProfile>()
            .AddEntitySetProfile<W481AdminChildProfile>());
        await using TestFixture _ = fx;

        string hit = Assert.Single(NavAuthWarnings(capture));
        Assert.Contains("'W481BatchParents'", hit, StringComparison.Ordinal);
        Assert.Contains("'W481Children'", hit, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>HasOptional</c>/<c>HasRequired</c> never set <c>NavItemType</c> — the other resolution
    /// route the ruling rules out.
    /// </summary>
    [Fact]
    public async Task SingleValuedNavigation_WhichNeverSetsNavItemType_StillWarns()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481SingleParentProfile>()
            .AddEntitySetProfile<W481AdminChildProfile>());
        await using TestFixture _ = fx;

        string hit = Assert.Single(NavAuthWarnings(capture));
        Assert.Contains("'Primary'", hit, StringComparison.Ordinal);
        Assert.Contains("'W481Children'", hit, StringComparison.Ordinal);
    }

    /// <summary>
    /// EDM case (B): a second, unrelated entity set over the child type EMPTIES
    /// <c>NavigationPropertyBindings</c> and turns <c>FindNavigationTarget</c> into a placeholder.
    /// A binding-only check silently stops working here; the union of candidate profiles does not.
    /// The unprotected sibling is not named — only the stricter one is.
    /// </summary>
    [Fact]
    public async Task TwoEntitySetsOverTheTargetType_WarnsForTheStricterOneOnly()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481BareParentProfile>()
            .AddEntitySetProfile<W481AdminChildProfile>()
            .AddEntitySetProfile<W481PublicChildProfile>());
        await using TestFixture _ = fx;

        string hit = Assert.Single(NavAuthWarnings(capture));
        Assert.Contains("'W481Children'", hit, StringComparison.Ordinal);
        Assert.DoesNotContain("'W481PublicChildren'", hit, StringComparison.Ordinal);
    }

    /// <summary>
    /// The write half the probe found — <c>$ref</c> POST/PUT/DELETE and the nav-POST create route
    /// all executed anonymously against an admin-gated set. The message must name the categories
    /// that are actually exposed, not reads (which agree here).
    /// </summary>
    [Fact]
    public async Task WriteRoutesIntoAWriteGuardedTarget_WarnOnTheWriteCategories()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481WriteNavParentProfile>()
            .AddEntitySetProfile<W481WriteGuardedChildProfile>());
        await using TestFixture _ = fx;

        string hit = Assert.Single(NavAuthWarnings(capture));
        Assert.Contains("'W481WriteGuardedChildren'", hit, StringComparison.Ordinal);
        Assert.Contains("one of the roles (admin)", hit, StringComparison.Ordinal);
        Assert.Contains("creates", hit, StringComparison.Ordinal);
        Assert.Contains("updates", hit, StringComparison.Ordinal);
    }

    // ── false-positive control ───────────────────────────────────────────────

    /// <summary>Identical requirements on both sides: nothing goes unapplied.</summary>
    [Fact]
    public async Task EqualAuthorization_IsSilent()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481EqualParentProfile>()
            .AddEntitySetProfile<W481AdminChildProfile>());
        await using TestFixture _ = fx;

        Assert.Empty(NavAuthWarnings(capture));
    }

    /// <summary>
    /// #549: <c>RequireResource()</c> on BOTH sides must still warn. It is the one requirement kind
    /// where token equality does not imply protection equality — every other kind is a statement
    /// about the CALLER and compares soundly, while a resource requirement is a statement about the
    /// RESOURCE, and Layer B evaluates it against the DECLARING set's entity.
    /// </summary>
    [Fact]
    public async Task Issue549_IdenticalRequireResource_StillWarns()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W549ResourceParentProfile>()
            .AddEntitySetProfile<W549ResourceChildProfile>());
        await using TestFixture _ = fx;

        string warning = Assert.Single(NavAuthWarnings(capture));
        Assert.Contains("W549ResourceChildren", warning, StringComparison.Ordinal);
        Assert.Contains("resource-based authorization", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bound for the test above. An identical ROLE on both sides must stay SILENT — otherwise
    /// the fix would be "never cancel anything", which fires on correct configuration and is the
    /// failure mode #440/#481 both establish as worse than no warning.
    /// </summary>
    [Fact]
    public async Task Issue549_IdenticalRole_IsStillSilent()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W549RoleParentProfile>()
            .AddEntitySetProfile<W549RoleChildProfile>());
        await using TestFixture _ = fx;

        Assert.Empty(NavAuthWarnings(capture));
    }

    /// <summary>
    /// The target is LESS strict than the declaring profile. Everything the target requires is
    /// already required here, so nothing is lost — this is not the hazard.
    /// </summary>
    [Fact]
    public async Task LessStrictTarget_IsSilent()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481StricterParentProfile>()
            .AddEntitySetProfile<W481OpenChildProfile>());
        await using TestFixture _ = fx;

        Assert.Empty(NavAuthWarnings(capture));
    }

    /// <summary>The overwhelmingly common registration: no authorization anywhere.</summary>
    [Fact]
    public async Task NoAuthorizationAnywhere_IsSilent()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481OpenParentProfile>()
            .AddEntitySetProfile<W481OpenChildProfile>());
        await using TestFixture _ = fx;

        Assert.Empty(NavAuthWarnings(capture));
    }

    /// <summary>
    /// Probe B-2: an UNDECLARED, convention-discovered navigation into the protected set. #440/#446
    /// already closed that door — it is served by nothing — so warning here would be a false
    /// positive on top of the warning #440 already emits for it.
    /// </summary>
    [Fact]
    public async Task UndeclaredConventionDiscoveredNavigation_IsSilent()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481UndeclaredParentProfile>()
            .AddEntitySetProfile<W481AdminChildProfile>());
        await using TestFixture _ = fx;

        Assert.Empty(NavAuthWarnings(capture));
        // Bounding assertion: the registration really does have the undeclared navigation, so this
        // test cannot pass because the fixture stopped exercising the shape.
        Assert.Contains(capture.Warnings, w =>
            w.Contains("that the OData convention builder discovered on", StringComparison.Ordinal));
    }

    /// <summary>EDM case (C): no entity set exposes the target type, so there is no rule to lose.</summary>
    [Fact]
    public async Task TargetTypeWithNoProfileAtAll_IsSilent()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481OrphanParentProfile>());
        await using TestFixture _ = fx;

        Assert.Empty(NavAuthWarnings(capture));
    }

    /// <summary>
    /// The category control, and the reason the comparison is not a single union: the target guards
    /// only writes, and this navigation exposes only reads. A union-based check would warn about a
    /// requirement no route here can reach — precisely the noise that gets a warning ignored.
    /// </summary>
    [Fact]
    public async Task TargetGuardsOnlyWrites_AndTheNavigationExposesOnlyReads_IsSilent()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481ReadOnlyNavParentProfile>()
            .AddEntitySetProfile<W481WriteGuardedChildProfile>());
        await using TestFixture _ = fx;

        Assert.Empty(NavAuthWarnings(capture));
    }

    // ── content ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The message must state the ruling's four facts — per-profile authorization, the declaration
    /// as the opt-in, that <c>$expand</c> follows the same rule, and the remedy — and must NOT
    /// promise enforcement, which was explicitly rejected.
    /// </summary>
    [Fact]
    public async Task WarningStatesTheRuleAndTheRemedy_AndPromisesNoEnforcement()
    {
        var (capture, fx) = await BuildAsync(b => b
            .AddEntitySetProfile<W481BareParentProfile>()
            .AddEntitySetProfile<W481AdminChildProfile>());
        await using TestFixture _ = fx;

        string hit = Assert.Single(NavAuthWarnings(capture));
        Assert.Contains("$expand", hit, StringComparison.Ordinal);
        Assert.Contains("Microsoft.AspNetCore.OData", hit, StringComparison.Ordinal);
        Assert.Contains("declaration is the opt-in", hit, StringComparison.Ordinal);
        Assert.Contains("split the surface", hit, StringComparison.Ordinal);
        // No placeholder may go unbound: Microsoft.Extensions.Logging binds a template
        // positionally, so a repeated name would consume an argument that is not there and render
        // literally. The only brace that survives is the ESCAPED '{key}' of the URL template.
        foreach (string placeholder in new[] { "{Entity", "{Nav", "{Target", "{Unapplied" })
            Assert.DoesNotContain(placeholder, hit, StringComparison.Ordinal);
        Assert.Equal(1, hit.Split('{').Length - 1);
        Assert.Contains("({key})/Children", hit, StringComparison.Ordinal);
    }
}
