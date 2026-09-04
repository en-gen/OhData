using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #492 / #416: the set of route-collision validations run at startup was NARROWER than the set of
/// routes that can actually collide. Every gap below has the same signature — startup passes
/// silently, then the request hits <c>AmbiguousMatchException</c>, which is thrown by ROUTING
/// before OhData's group filter runs, so the client gets a raw 500 with no OData error envelope
/// and the log shows a framework exception rather than a configuration problem.
///
/// <para>
/// That inverts the framework's posture: these are configuration errors that <c>MapOhData()</c>
/// (and, for the unbound-operation pass, the registration build behind it) exists to catch. Each
/// test here pins one gap; each is paired with a control proving the adjacent, legal shape still
/// starts, so the fix cannot be "throw more often".
/// </para>
/// </summary>
public class StartupRouteCollisionValidationTests
{
    private static Task<string> Echo(string name) => Task.FromResult(name);

    // ── #492 §1: the unbound-operation collision check missed Priority-1 entity sets ──────────
    //
    // OhDataBuilder.Register()'s unbound-op-vs-entity-set pass asked `p.HasGetAll ||
    // p.HasGetQueryable`, enumerating two of the THREE collection-read paths. A Priority-1 profile
    // (ODataEntitySetProfile with only GetODataQueryable) reports both false, yet MapEntitySet
    // registers its collection GET anyway — so an unbound function sharing that entity set's name
    // registered a second GET /{prefix}/{Name} and every collection read of the set became a raw
    // 500.

    [Fact]
    public void UnboundFunction_CollidingWithAPriority1CollectionGet_ThrowsAtStartup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o
            .AddEntitySetProfile<ODataWidgetProfile>()   // "ODataWidgets", Priority-1 collection GET
            .AddFunction((Func<string, Task<string>>)Echo, "ODataWidgets"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.BuildServiceProvider().GetRequiredService<OhDataRegistration>());

        Assert.Contains("ODataWidgets", ex.Message, StringComparison.Ordinal);
        Assert.Contains("function", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The control: the same Priority-1 profile beside a differently-named unbound function still
    /// registers. The fix must widen the "does this profile register a collection GET" question,
    /// not make every Priority-1 registration illegal.
    /// </summary>
    [Fact]
    public void UnboundFunction_NotCollidingWithAPriority1EntitySet_StillRegisters()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData(o => o
            .AddEntitySetProfile<ODataWidgetProfile>()
            .AddFunction((Func<string, Task<string>>)Echo, "GreetOData"));

        var registration = services.BuildServiceProvider().GetRequiredService<OhDataRegistration>();
        Assert.Single(registration.UnboundOperations);
    }

    // ── #492 §2: three collision checks compared Ordinal; route matching is case-insensitive ──
    //
    // ASP.NET Core literal-segment matching is case-insensitive, which OhDataBuilder.Register()'s
    // own checks already know (they use OrdinalIgnoreCase and say so). The three sibling checks
    // inside MapEntitySet did not.

    [Fact]
    public async Task BoundFunction_DifferingOnlyInCaseFromAStructuralProperty_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<RcvCasePropertyCollisionProfile>()));

        Assert.Contains("RcvCaseProperty", ex.Message, StringComparison.Ordinal);
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BoundAction_DifferingOnlyInCaseFromANavigationPostRoute_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<RcvCaseNavPostCollisionProfile>()));

        Assert.Contains("RcvCaseNavPost", ex.Message, StringComparison.Ordinal);
        Assert.Contains("kids", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── #416 / #492 §3: bound function vs delegate-backed navigation — no check at all ────────
    //
    // A navigation declared with ANY handler registers GET /{Set}({key})/{Nav}; an entity-level
    // bound function registers GET /{Set}({key})/{FnName}. Same template, same method. The three
    // pre-existing checks covered structural properties, nav-POST-vs-action, and the #313
    // continuation route; nothing covered this pair.

    [Fact]
    public async Task BoundFunction_CollidingWithADelegateBackedNavigation_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<RcvNavFunctionCollisionProfile>()));

        Assert.Contains("RcvNavFunction", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Kids", ex.Message, StringComparison.Ordinal);
        Assert.Contains("navigation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same collision one case-fold away — the §2 comparer defect and the §3 missing check
    /// meeting in one configuration.
    /// </summary>
    [Fact]
    public async Task BoundFunction_DifferingOnlyInCaseFromADelegateBackedNavigation_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<RcvCaseNavFunctionCollisionProfile>()));

        Assert.Contains("RcvCaseNavFunction", ex.Message, StringComparison.Ordinal);
        Assert.Contains("kids", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A navigation with a <c>post</c> handler but no <c>getAll</c> STILL registers the GET nav
    /// route (with a null-returning handler that 404s), because <c>MapEntitySet</c> maps a GET for
    /// every <c>NavigationRoutes</c> entry. So a bound FUNCTION collides with it too — the check
    /// must key off the navigation-route list, not off which particular delegate was supplied.
    /// </summary>
    [Fact]
    public async Task BoundFunction_CollidingWithAPostOnlyNavigationRoute_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<RcvPostOnlyNavFunctionCollisionProfile>()));

        Assert.Contains("RcvPostOnlyNav", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Kids", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control that bounds the new check, and the one shape #313 stage 5 deliberately left
    /// legal: a navigation declared with NO handler registers no route at all, so a bound function
    /// of the same name is not a collision and must keep starting.
    /// </summary>
    [Fact]
    public async Task BoundFunction_NamedLikeADelegatelessNavigation_StillStarts()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<RcvDelegatelessNavProfile>());

        using var response = await fx.Client.GetAsync("/odata/RcvDelegatelessNavs(1)/Kids");
        // The bound function owns the template — it is the only endpoint registered for it.
        response.EnsureSuccessStatusCode();
    }

    // ── #492 §4: duplicate bound-operation names within one profile were never validated ──────
    //
    // Neither the Bind* methods, nor VisitModelBuilder's EDM registration, nor MapEntitySet
    // checked for a repeated name. Each operation mapped its own route; request-time dispatch is
    // `BoundFunctions.First(f => f.Name == … && f.IsEntityLevel)`, so the duplicate is ambiguous
    // in the dispatch layer as well as unrouteable.

    [Fact]
    public async Task DuplicateEntityLevelBoundFunctionNames_ThrowAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<RcvDuplicateEntityFunctionProfile>()));

        Assert.Contains("RcvDupEntityFn", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Tally", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntityLevelBoundFunctionNamesDifferingOnlyInCase_ThrowAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<RcvDuplicateEntityFunctionCaseProfile>()));

        Assert.Contains("RcvDupEntityFnCase", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tally", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateCollectionLevelBoundActionNames_ThrowAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<RcvDuplicateCollectionActionProfile>()));

        Assert.Contains("RcvDupCollAction", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Bulk", ex.Message, StringComparison.Ordinal);
    }


    // Codecov flagged ValidateBoundOperationNameIsUnique's four call sites as only half covered:
    // the tests above exercise BindEntityFunction and BindAction, leaving BindFunction and
    // BindEntityAction unproven. The validator takes (isAction, isEntityLevel) and composes the
    // message and the route template from them, so an untested pair is an untested message as well
    // as an untested throw -- and this PR exists to add throws.

    [Fact]
    public async Task DuplicateCollectionLevelBoundFunctionNames_ThrowAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<RcvDuplicateCollectionFunctionProfile>()));

        Assert.Contains("RcvDupCollFn", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Rollup", ex.Message, StringComparison.Ordinal);
        // A collection-bound function is GET /{Set}/{Name} -- no {key} segment.
        Assert.Contains("GET /RcvDupCollFn/Rollup", ex.Message, StringComparison.Ordinal);
        Assert.Contains("function", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateEntityLevelBoundActionNames_ThrowAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(
                o => o.AddEntitySetProfile<RcvDuplicateEntityActionProfile>()));

        Assert.Contains("RcvDupEntityAction", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Stamp", ex.Message, StringComparison.Ordinal);
        // An entity-bound action is POST /{Set}({key})/{Name}.
        Assert.Contains("POST /RcvDupEntityAction({key})/Stamp", ex.Message, StringComparison.Ordinal);
        Assert.Contains("action", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: the same NAME at the two different binding levels claims two different
    /// templates (<c>GET /{Set}/{Name}</c> vs <c>GET /{Set}({key})/{Name}</c>) and dispatch already
    /// discriminates on <c>IsEntityLevel</c>, so it is not a duplicate and must keep starting.
    /// </summary>
    [Fact]
    public async Task SameBoundFunctionNameAtBothBindingLevels_StillStarts()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<RcvCrossLevelSameNameProfile>());

        using var collectionLevel = await fx.Client.GetAsync("/odata/RcvCrossLevels/Stats");
        collectionLevel.EnsureSuccessStatusCode();
        using var entityLevel = await fx.Client.GetAsync("/odata/RcvCrossLevels(1)/Stats");
        entityLevel.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// #492 §2, third site: the #313 bare-$expand continuation route's own collision check. It shares
/// the comparer defect with its two siblings, and rides the fixture #313 stage 5 authored — the
/// only difference from <c>BareExpandContinuationCollisionTests</c> is the case of the bound
/// function's name.
/// </summary>
public sealed class ExpandContinuationCaseCollisionTests
{
    [Fact]
    public async Task BoundFunction_DifferingOnlyInCaseFromAPageableNavigation_ThrowsAtStartup()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using TestFixture fx = await BareExpandContinuation.BuildAsync(
                connection, cap: 3, pagingEnabled: true,
                extraProfiles: b => b.AddEntitySetProfile<RcvCaseCollidingContinuationProfile>());
        });

        Assert.Contains("RcvCaseContinuation", ex.Message, StringComparison.Ordinal);
        Assert.Contains("books", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

// ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

/// <summary>#492 §2: the #313 continuation collision one case-fold away. Mirrors
/// <c>BeCollidingFunctionProfile</c> exactly except for the bound function's name.</summary>
internal sealed class RcvCaseCollidingContinuationProfile : EntitySetProfile<int, BeAuthor>
{
    public RcvCaseCollidingContinuationProfile(BareExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "RcvCaseContinuation";
        ExpandEnabled = true;
        GetQueryable = _ => OhDataResult.Success(db.Authors.AsQueryable());
        HasMany(x => x.Books);
        BindEntityFunction(books);
    }

    private Task<int> books(int key) => Task.FromResult(key);
}

internal class RcvParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<RcvChild> Kids { get; set; } = new();
}

internal class RcvChild
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>#492 §2: an entity-level bound function whose name differs from the structural property
/// <c>Name</c> only by case. Both register <c>GET /{Set}({key})/{segment}</c>.</summary>
internal class RcvCasePropertyCollisionProfile : EntitySetProfile<int, RcvParent>
{
    public RcvCasePropertyCollisionProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvCaseProperty";
        GetById = (id, ct) => OhDataResult.Success<RcvParent>(new RcvParent { Id = id });
        BindEntityFunction(name);
    }

    private Task<int> name(int key) => Task.FromResult(key);
}

/// <summary>#492 §2: an entity-level bound action whose name differs from a navigation property's
/// <c>post</c> route only by case. Both register <c>POST /{Set}({key})/{segment}</c>.</summary>
internal class RcvCaseNavPostCollisionProfile : EntitySetProfile<int, RcvParent>
{
    public RcvCaseNavPostCollisionProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvCaseNavPost";
        GetById = (id, ct) => OhDataResult.Success<RcvParent>(new RcvParent { Id = id });
        HasMany(x => x.Kids,
            getAll: (key, ct) => Task.FromResult<IEnumerable<RcvChild>>(Array.Empty<RcvChild>()),
            post: (key, child, ct) => Task.FromResult<RcvChild?>(child));
        BindEntityAction(kids);
    }

    private Task<int> kids(int key) => Task.FromResult(key);
}

/// <summary>#416: a delegate-backed navigation and an entity-level bound function of the same
/// name — two endpoints claiming <c>GET /{Set}({key})/Kids</c>.</summary>
internal class RcvNavFunctionCollisionProfile : EntitySetProfile<int, RcvParent>
{
    public RcvNavFunctionCollisionProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvNavFunction";
        GetById = (id, ct) => OhDataResult.Success<RcvParent>(new RcvParent { Id = id });
        HasMany(x => x.Kids,
            getAll: (key, ct) => Task.FromResult<IEnumerable<RcvChild>>(Array.Empty<RcvChild>()));
        BindEntityFunction(Kids);
    }

    private Task<int> Kids(int key) => Task.FromResult(key);
}

/// <summary>#416 + #492 §2 together: the same collision one case-fold away.</summary>
internal class RcvCaseNavFunctionCollisionProfile : EntitySetProfile<int, RcvParent>
{
    public RcvCaseNavFunctionCollisionProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvCaseNavFunction";
        GetById = (id, ct) => OhDataResult.Success<RcvParent>(new RcvParent { Id = id });
        HasMany(x => x.Kids,
            getAll: (key, ct) => Task.FromResult<IEnumerable<RcvChild>>(Array.Empty<RcvChild>()));
        BindEntityFunction(kids);
    }

    private Task<int> kids(int key) => Task.FromResult(key);
}

/// <summary>#416: a navigation registered with <c>post</c> only. MapEntitySet still maps a GET for
/// it, so an entity-level bound FUNCTION of the same name collides.</summary>
internal class RcvPostOnlyNavFunctionCollisionProfile : EntitySetProfile<int, RcvParent>
{
    public RcvPostOnlyNavFunctionCollisionProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvPostOnlyNav";
        GetById = (id, ct) => OhDataResult.Success<RcvParent>(new RcvParent { Id = id });
        HasMany(x => x.Kids,
            getAll: null,
            post: (key, child, ct) => Task.FromResult<RcvChild?>(child));
        BindEntityFunction(Kids);
    }

    private Task<int> Kids(int key) => Task.FromResult(key);
}

/// <summary>Control: a navigation declared in the EDM only registers no route, so a bound function
/// of the same name owns the template alone and must keep starting.</summary>
internal class RcvDelegatelessNavProfile : EntitySetProfile<int, RcvParent>
{
    public RcvDelegatelessNavProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvDelegatelessNavs";
        GetById = (id, ct) => OhDataResult.Success<RcvParent>(new RcvParent { Id = id });
        HasMany(x => x.Kids);
        BindEntityFunction(Kids);
    }

    private Task<int> Kids(int key) => Task.FromResult(key);
}

/// <summary>#492 §4: two entity-level bound functions with one name (C# overloads share it).</summary>
internal class RcvDuplicateEntityFunctionProfile : EntitySetProfile<int, RcvParent>
{
    public RcvDuplicateEntityFunctionProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvDupEntityFn";
        GetById = (id, ct) => OhDataResult.Success<RcvParent>(new RcvParent { Id = id });
        BindEntityFunction((Func<int, Task<int>>)Tally);
        BindEntityFunction((Func<int, int, Task<int>>)Tally);
    }

    private Task<int> Tally(int key) => Task.FromResult(key);
    private Task<int> Tally(int key, int extra) => Task.FromResult(key + extra);
}

/// <summary>#492 §4 + §2: two entity-level bound functions whose names differ only by case.</summary>
internal class RcvDuplicateEntityFunctionCaseProfile : EntitySetProfile<int, RcvParent>
{
    public RcvDuplicateEntityFunctionCaseProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvDupEntityFnCase";
        GetById = (id, ct) => OhDataResult.Success<RcvParent>(new RcvParent { Id = id });
        BindEntityFunction(Tally);
        BindEntityFunction(tally);
    }

    private Task<int> Tally(int key) => Task.FromResult(key);
    private Task<int> tally(int key) => Task.FromResult(key);
}

/// <summary>#492 §4: collection-level bound actions share the mechanism.</summary>
internal class RcvDuplicateCollectionActionProfile : EntitySetProfile<int, RcvParent>
{
    public RcvDuplicateCollectionActionProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvDupCollAction";
        BindAction((Func<Task<int>>)Bulk);
        BindAction((Func<int, Task<int>>)Bulk);
    }

    private Task<int> Bulk() => Task.FromResult(0);
    private Task<int> Bulk(int n) => Task.FromResult(n);
}


/// <summary>#492 §4: two COLLECTION-level bound functions sharing a name (GET /{Set}/{Name}).</summary>
internal class RcvDuplicateCollectionFunctionProfile : EntitySetProfile<int, RcvParent>
{
    public RcvDuplicateCollectionFunctionProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvDupCollFn";
        BindFunction((Func<Task<int>>)Rollup);
        BindFunction((Func<int, Task<int>>)Rollup);
    }

    private Task<int> Rollup() => Task.FromResult(0);
    private Task<int> Rollup(int n) => Task.FromResult(n);
}

/// <summary>#492 §4: two ENTITY-level bound actions sharing a name (POST /{Set}({key})/{Name}).</summary>
internal class RcvDuplicateEntityActionProfile : EntitySetProfile<int, RcvParent>
{
    public RcvDuplicateEntityActionProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvDupEntityAction";
        GetById = (id, ct) => OhDataResult.Success<RcvParent>(new RcvParent { Id = id });
        BindEntityAction((Func<int, Task<int>>)Stamp);
        BindEntityAction((Func<int, int, Task<int>>)Stamp);
    }

    private Task<int> Stamp(int key) => Task.FromResult(key);
    private Task<int> Stamp(int key, int extra) => Task.FromResult(key + extra);
}

/// <summary>Control: one name, two binding levels, two distinct templates.</summary>
internal class RcvCrossLevelSameNameProfile : EntitySetProfile<int, RcvParent>
{
    public RcvCrossLevelSameNameProfile() : base(x => x.Id)
    {
        EntitySetName = "RcvCrossLevels";
        GetById = (id, ct) => OhDataResult.Success<RcvParent>(new RcvParent { Id = id });
        BindFunction((Func<Task<int>>)Stats);
        BindEntityFunction((Func<int, Task<int>>)Stats);
    }

    private Task<int> Stats() => Task.FromResult(0);
    private Task<int> Stats(int key) => Task.FromResult(key);
}
