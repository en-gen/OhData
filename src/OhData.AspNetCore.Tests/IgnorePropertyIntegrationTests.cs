using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// Parent entity: ignores a primitive (CostBasis) and a complex property (Audit).
//
// #398: THIS FIXTURE CARRIES AN OPEN COMPLEX TYPE ON PURPOSE (IgnSpec, below), and that is the
// point of it. #395's open-type suite was green while shipping a CRITICAL defect because every
// fixture in it was green-field — a fresh model built around a bag, asserting the bag worked. None
// asserted that an EXISTING feature still worked in the PRESENCE of a bag. So the rule is now: an
// open-type fixture is an existing feature's fixture with a container added, and the original
// assertions stay. Every Ignore() assertion in this file therefore runs against a registration
// where OpenTypesActive is TRUE, which is what makes them meaningful as containment tests: the
// mechanism Ignore() is built on (removing a member in a TypeInfoResolver modifier) is exactly the
// mechanism extension data uses to CAPTURE a removed member.
public sealed class IgnProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal CostBasis { get; set; }
    public IgnAudit? Audit { get; set; }
    public IgnSpec? Spec { get; set; }
    public List<IgnTag>? Tags { get; set; }
}

public sealed class IgnAudit
{
    public string CreatedBy { get; set; } = "";
}

// The container. ODataConventionModelBuilder infers a dynamic-property dictionary from the
// IDictionary<string, object?> member, marks IgnSpec OpenType="true" and omits Extras from the
// declared properties — so this type is an OData open complex type with no attribute anywhere.
public sealed class IgnSpec
{
    public string Material { get; set; } = "";
    public IDictionary<string, object?>? Extras { get; set; }
}

// Navigation child with its own profile ignoring InternalCode — proves $expand-nested hiding.
public sealed class IgnTag
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public string InternalCode { get; set; } = "";
}

// Control entity in the same registration: no ignores; has a property whose name matches an
// ignored name on IgnProduct to prove suppression is per-type, not global.
public sealed class IgnControl
{
    public int Id { get; set; }
    public decimal CostBasis { get; set; }
}

internal static class IgnData
{
    internal static List<IgnProduct> Products() => new()
    {
        new IgnProduct
        {
            Id = 1,
            Name = "Widget",
            CostBasis = 8.5m,
            Audit = new IgnAudit { CreatedBy = "internal-user" },
            Spec = new IgnSpec
            {
                Material = "steel",
                Extras = new Dictionary<string, object?> { ["finish"] = "matte" },
            },
        },
        new IgnProduct { Id = 2, Name = "Gadget", CostBasis = 12.0m },
    };

    internal static List<IgnTag> Tags() => new()
    {
        new IgnTag { Id = 10, Label = "blue", InternalCode = "SECRET-B" },
        new IgnTag { Id = 11, Label = "round", InternalCode = "SECRET-R" },
    };
}

/// <summary>
/// #515: what <see cref="IgnProductProfile"/>'s write handlers actually received — on <b>this
/// host</b>. Registered as a singleton by <see cref="IgnProductHost"/>, so every
/// <c>TestFixture</c> gets its own and no two test classes can see each other's captures.
/// </summary>
/// <remarks>
/// <para>
/// These used to be three <c>static</c> fields on the profile, set to <c>null</c> and then asserted
/// against by <c>IgnorePropertyIntegrationTests</c> AND by <c>OpenTypeIgnoreContainmentTests</c> —
/// the latter clearing two of them from its own <c>InitializeAsync</c>, so its SETUP could land
/// inside the other's reset-to-assert window. That was #484's race exactly, and it was papered over
/// with a shared <c>[Collection]</c>: xUnit then serialised the two classes, which schedules around
/// the shared state rather than removing it and costs parallelism between two classes with no
/// reason to be serialised.
/// </para>
/// <para>
/// There is deliberately no <c>Reset</c>, for #484's reason: a capture that cannot be reset cannot
/// be reset at the wrong moment. Nothing needs one — xUnit constructs a fresh test-class instance
/// per test, so <c>IAsyncLifetime.InitializeAsync</c> builds a fresh host, and a fresh host means a
/// fresh capture.
/// </para>
/// <para>
/// The dependency is <b>constructor-required</b> on the profile rather than resolved lazily, so a
/// host that registers <c>IgnProductProfile</c> without this singleton cannot silently no-op: DI
/// throws while <c>MapOhData()</c> resolves the profile, i.e. at <c>TestHostBuilder.BuildAsync</c>,
/// before a single request. That is what makes <see cref="IgnProductHost"/> the only viable
/// registration route, which is what makes "a missed site" impossible to miss.
/// </para>
/// </remarks>
public sealed class IgnProductWriteCaptures
{
    /// <summary>The model the <c>Post</c> handler last received on this host.</summary>
    internal IgnProduct? LastPosted { get; set; }

    /// <summary>The model the <c>Put</c> handler last received on this host.</summary>
    internal IgnProduct? LastPut { get; set; }

    /// <summary>The delta's changed-property names from the last <c>Patch</c> on this host.</summary>
    internal IReadOnlyList<string>? LastPatchChangedNames { get; set; }
}

public sealed class IgnProductProfile : EntitySetProfile<int, IgnProduct>
{
    private readonly List<IgnProduct> _store = IgnData.Products();

    public IgnProductProfile(IgnProductWriteCaptures captures) : base(x => x.Id)
    {
        Ignore(x => x.CostBasis, x => x.Audit);
        SelectEnabled = true;
        FilterEnabled = true;
        OrderByEnabled = true;
        ExpandEnabled = true;

        HasMany(x => x.Tags!, (int key, CancellationToken ct) =>
            Task.FromResult<IEnumerable<IgnTag>>(IgnData.Tags()));

        GetQueryable = ct => OhDataResult.Success(_store.AsQueryable());
        GetById = (id, ct) => OhDataResult.Success(_store.FirstOrDefault(p => p.Id == id));
        Post = (model, ct) =>
        {
            captures.LastPosted = model;
            model.Id = 99;
            _store.Add(model);
            return OhDataResult.Success<IgnProduct>(model);
        };
        Put = (id, model, ct) =>
        {
            captures.LastPut = model;
            return OhDataResult.Success(model);
        };
        Patch = (id, delta, ct) =>
        {
            captures.LastPatchChangedNames = delta.GetChangedPropertyNames().ToList();
            var existing = _store.FirstOrDefault(p => p.Id == id);
            if (existing is null) return OhDataResult.Success<IgnProduct>(null);
            delta.Patch(existing);
            return OhDataResult.Success<IgnProduct>(existing);
        };
    }
}

public sealed class IgnTagProfile : EntitySetProfile<int, IgnTag>
{
    public IgnTagProfile() : base(x => x.Id)
    {
        Ignore(x => x.InternalCode);
        GetById = (id, ct) => OhDataResult.Success(IgnData.Tags().FirstOrDefault(t => t.Id == id));
    }
}

public sealed class IgnControlProfile : EntitySetProfile<int, IgnControl>
{
    public IgnControlProfile() : base(x => x.Id)
    {
        GetById = (id, ct) => OhDataResult.Success<IgnControl>(new IgnControl { Id = id, CostBasis = 5m });
    }
}

/// <summary>
/// #515: the ONE place a host carrying <see cref="IgnProductProfile"/> is built. The profile takes
/// <see cref="IgnProductWriteCaptures"/> as a required constructor parameter, so a host built any
/// other way throws out of <c>MapOhData()</c> — a loud startup failure rather than the request-time
/// DI failure that made the six scattered registration sites too risky to convert by hand.
/// </summary>
/// <remarks>
/// The six sites this replaced: <c>IgnorePropertyIntegrationTests.InitializeAsync</c>,
/// <c>OpenTypeIgnoreContainmentTests.InitializeAsync</c> and its
/// <c>StartupStillRejectsTwoProfilesDisagreeingAboutWhatIsIgnored</c>, and the three inline hosts in
/// <c>OpenTypeModifierOrderingTests</c> — the last of which never touches the captures at all, which
/// is exactly why a per-site registration would have been easy to miss.
/// </remarks>
internal static class IgnProductHost
{
    /// <summary>The standard trio every capture-asserting test uses.</summary>
    internal static Task<TestFixture> BuildAsync() =>
        BuildAsync(b => b
            .AddEntitySetProfile<IgnProductProfile>()
            .AddEntitySetProfile<IgnTagProfile>()
            .AddEntitySetProfile<IgnControlProfile>());

    /// <summary>A caller-chosen profile set, with the capture singleton registered regardless.</summary>
    internal static Task<TestFixture> BuildAsync(Action<OhDataBuilder> registerProfiles) =>
        TestHostBuilder.BuildAsync(
            registerProfiles,
            configureServices: s => s.AddSingleton<IgnProductWriteCaptures>());
}

/// <summary>
/// #515: the safety net that makes <see cref="IgnProductHost"/> the only viable route. The issue's
/// stated reason the capture object was NOT injected the first time was that
/// <c>IgnProductProfile</c> is registered from three files across six call sites and "a missed site
/// is a request-time DI failure rather than a compile error". Measured here: it is neither silent
/// nor request-time — <c>MapOhData()</c> resolves every profile in a temporary scope while building
/// the registration, so the host fails to start.
/// </summary>
public class IgnProductCaptureRegistrationTests
{
    [Fact]
    public async Task AHostThatSkipsTheCaptureSingleton_FailsToStart_RatherThanFailingPerRequest()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using TestFixture fx = await TestHostBuilder.BuildAsync(
                b => b.AddEntitySetProfile<IgnProductProfile>());
        });

        Assert.Contains(nameof(IgnProductWriteCaptures), ex.ToString(), StringComparison.Ordinal);
    }
}

public class IgnorePropertyIntegrationTests : IAsyncLifetime
{
    private TestFixture _fx = null!;

    public async Task InitializeAsync() => _fx = await IgnProductHost.BuildAsync();

    /// <summary>
    /// #515: the captures belong to <b>this</b> host. xUnit builds a fresh test-class instance — and
    /// therefore a fresh host — per test, so there is nothing to reset and nothing another class can
    /// reset underneath an assertion here.
    /// </summary>
    private IgnProductWriteCaptures Captures =>
        _fx.App.Services.GetRequiredService<IgnProductWriteCaptures>();

    public async Task DisposeAsync() => await _fx.DisposeAsync();

    // ---- $metadata ----

    [Fact]
    public async Task Metadata_OmitsIgnoredProperties_PerType()
    {
        string xml = await _fx.Client.GetStringAsync("/odata/$metadata");

        // Suppression is per entity type: IgnProduct/IgnTag lose their ignored properties while
        // IgnControl — which has its own, un-ignored property named CostBasis — keeps it.
        string product = EntityTypeElement(xml, nameof(IgnProduct));
        Assert.DoesNotContain("CostBasis", product);
        Assert.DoesNotContain("Audit", product);
        Assert.Contains("Name", product);

        string tag = EntityTypeElement(xml, nameof(IgnTag));
        Assert.DoesNotContain("InternalCode", tag);
        Assert.Contains("Label", tag);

        string control = EntityTypeElement(xml, nameof(IgnControl));
        Assert.Contains("CostBasis", control);
    }

    private static string EntityTypeElement(string csdl, string typeName)
    {
        int start = csdl.IndexOf($"<EntityType Name=\"{typeName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"EntityType '{typeName}' not found in $metadata");
        int end = csdl.IndexOf("</EntityType>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"EntityType '{typeName}' element not terminated");
        return csdl[start..end];
    }

    // ---- response bodies ----

    [Fact]
    public async Task CollectionGet_OmitsIgnoredMembers()
    {
        string json = await _fx.Client.GetStringAsync("/odata/IgnProducts");
        Assert.Contains("\"Name\"", json);
        Assert.DoesNotContain("CostBasis", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Audit", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SingleGet_OmitsIgnoredMembers()
    {
        string json = await _fx.Client.GetStringAsync("/odata/IgnProducts(1)");
        Assert.Contains("\"Name\"", json);
        Assert.DoesNotContain("CostBasis", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Audit", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpandedChild_HidesItsOwnIgnoredMembers()
    {
        string json = await _fx.Client.GetStringAsync("/odata/IgnProducts?$expand=Tags");
        Assert.Contains("\"Label\"", json);
        Assert.DoesNotContain("InternalCode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-", json);
    }

    [Fact]
    public async Task NavigationGet_HidesChildIgnoredMembers()
    {
        string json = await _fx.Client.GetStringAsync("/odata/IgnProducts(1)/Tags");
        Assert.Contains("\"Label\"", json);
        Assert.DoesNotContain("InternalCode", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ControlEntity_SameNamedProperty_NotSuppressed()
    {
        string json = await _fx.Client.GetStringAsync("/odata/IgnControls(1)");
        Assert.Contains("CostBasis", json); // per-type suppression only
    }

    // ---- query options ----

    [Theory]
    [InlineData("/odata/IgnProducts?$select=CostBasis")]
    [InlineData("/odata/IgnProducts?$filter=CostBasis gt 1")]
    [InlineData("/odata/IgnProducts?$orderby=CostBasis")]
    public async Task QueryOption_NamingIgnoredProperty_Returns400(string url)
    {
        var resp = await _fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- property routes ----

    [Fact]
    public async Task PropertyRoute_ForIgnoredProperty_NotRegistered()
    {
        var resp = await _fx.Client.GetAsync("/odata/IgnProducts(1)/CostBasis");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var respValue = await _fx.Client.GetAsync("/odata/IgnProducts(1)/CostBasis/$value");
        Assert.Equal(HttpStatusCode.NotFound, respValue.StatusCode);

        var respOk = await _fx.Client.GetAsync("/odata/IgnProducts(1)/Name");
        Assert.Equal(HttpStatusCode.OK, respOk.StatusCode);
    }

    // ---- request binding ----

    [Fact]
    public async Task Post_IgnoredMembersInBody_NotBound()
    {
        using var body = new StringContent(
            "{\"name\":\"New\",\"costBasis\":42.5,\"audit\":{\"createdBy\":\"attacker\"}}",
            Encoding.UTF8, "application/json");
        var resp = await _fx.Client.PostAsync("/odata/IgnProducts", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotNull(Captures.LastPosted);
        IgnProduct lastPosted = Captures.LastPosted!;
        Assert.Equal("New", lastPosted.Name);
        Assert.Equal(0m, lastPosted.CostBasis);
        Assert.Null(lastPosted.Audit);
    }

    [Fact]
    public async Task Put_IgnoredMembersInBody_NotBound()
    {
        using var body = new StringContent(
            "{\"id\":1,\"name\":\"Renamed\",\"costBasis\":42.5}",
            Encoding.UTF8, "application/json");
        var resp = await _fx.Client.PutAsync("/odata/IgnProducts(1)", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(Captures.LastPut);
        IgnProduct lastPut = Captures.LastPut!;
        Assert.Equal("Renamed", lastPut.Name);
        Assert.Equal(0m, lastPut.CostBasis);
    }

    [Fact]
    public async Task Patch_IgnoredMemberInBody_NotInDelta()
    {
        using var body = new StringContent(
            "{\"name\":\"Patched\",\"costBasis\":99.9}",
            Encoding.UTF8, "application/json");
        var resp = await _fx.Client.PatchAsync("/odata/IgnProducts(2)", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(Captures.LastPatchChangedNames);
        Assert.Contains("Name", Captures.LastPatchChangedNames!);
        Assert.DoesNotContain("CostBasis", Captures.LastPatchChangedNames!);
    }
}

// ---- startup validation (separate hosts that must FAIL to build) ----

public sealed class IgnConflictA : EntitySetProfile<int, IgnProduct>
{
    public IgnConflictA() : base(x => x.Id)
    {
        EntitySetName = "ConflictA";
        Ignore(x => x.CostBasis);
        GetById = (id, ct) => OhDataResult.Success<IgnProduct>(null);
    }
}

public sealed class IgnConflictB : EntitySetProfile<int, IgnProduct>
{
    public IgnConflictB() : base(x => x.Id)
    {
        EntitySetName = "ConflictB"; // same TModel, DIFFERENT ignore set (none)
        GetById = (id, ct) => OhDataResult.Success<IgnProduct>(null);
    }
}

public sealed class IgnNavConflictProfile : EntitySetProfile<int, IgnProduct>
{
    public IgnNavConflictProfile() : base(x => x.Id)
    {
        Ignore(x => x.Tags);
        HasMany(x => x.Tags!); // same property declared as navigation — seal-time conflict
    }
}

public sealed class IgnNavConflictReversedProfile : EntitySetProfile<int, IgnProduct>
{
    public IgnNavConflictReversedProfile() : base(x => x.Id)
    {
        HasMany(x => x.Tags!); // declaration order reversed — must still throw
        Ignore(x => x.Tags);
    }
}

public class IgnorePropertyStartupValidationTests
{
    [Fact]
    public async Task SameModelType_DifferentIgnoreSets_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(b => b
                .AddEntitySetProfile<IgnConflictA>()
                .AddEntitySetProfile<IgnConflictB>()));
        Assert.Contains("ConflictA", ex.Message);
        Assert.Contains("ConflictB", ex.Message);
        Assert.Contains(nameof(IgnProduct), ex.Message);
    }

    [Fact]
    public async Task IgnoreThenHasMany_SameProperty_ThrowsAtStartup()
    {
        // OhDataBuilder wraps seal-time (VisitModelBuilder) failures in an "OhData: failed to
        // build EDM for profile ..." InvalidOperationException; the conflict detail is inner.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<IgnNavConflictProfile>()));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Tags", ex.InnerException!.Message);
        Assert.Contains("Ignore()", ex.InnerException.Message);
    }

    [Fact]
    public async Task HasManyThenIgnore_SameProperty_ThrowsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestHostBuilder.BuildAsync(b => b.AddEntitySetProfile<IgnNavConflictReversedProfile>()));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Tags", ex.InnerException!.Message);
        Assert.Contains("Ignore()", ex.InnerException.Message);
    }
}
