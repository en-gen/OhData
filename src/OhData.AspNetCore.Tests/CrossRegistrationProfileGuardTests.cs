using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #424: <c>AddEntitySetProfile&lt;T&gt;()</c> consults the cross-registration
/// <c>GlobalProfileRegistry</c> and rejects a profile type already claimed by another named
/// registration (see <see cref="VersioningDocExampleTests.NamedRegistrations_SharedProfileType_ThrowsAtRegistrationTime"/>
/// for that guard's own coverage). <c>AddProfileType</c> — the path every <c>AddProfilesFrom*</c>
/// overload funnels through — never consulted it, so scanning the same assembly into two named
/// registrations silently allowed exactly what the explicit path rejects.
///
/// <para>
/// <c>docs/versioning.md</c> documented this as a known inconsistency, deliberately not as
/// supported behaviour ("Do not rely on that"). This suite pins the chosen resolution: the
/// scanner path now enforces the SAME guard as the explicit path, via one shared method — not a
/// second, parallel check that could drift from it.
/// </para>
/// </summary>
public class CrossRegistrationProfileGuardTests
{
    /// <summary>
    /// The exact shape #424 describes: the same profile type reachable from an assembly scan,
    /// registered into two different named <c>AddOhData</c> registrations. The explicit-path
    /// equivalent (<c>AddEntitySetProfile&lt;ScanTargetProfile&gt;()</c> in both) already throws
    /// (pinned elsewhere); this is that same collision reached through <c>AddProfilesFrom</c>
    /// instead, which must throw identically.
    /// </summary>
    [Fact]
    public void AddProfilesFrom_ProfileTypeAlreadyRegisteredInAnotherRegistration_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("crossA", o => o
            .WithPrefix("/crossA")
            .AddEntitySetProfile<ScanTargetProfile>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData("crossB", o => o
                .WithPrefix("/crossB")
                .AddProfilesFrom(s => s.InAssemblyOf<ScanTargetProfile>())));

        Assert.Contains("cannot be shared across registrations", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mirror shape: the FIRST registration uses the scanner, the SECOND uses the explicit
    /// call. The guard must fire regardless of which side goes through which path, since both
    /// funnel through the same shared method.
    /// </summary>
    [Fact]
    public void AddEntitySetProfile_ProfileTypeAlreadyRegisteredViaScanInAnotherRegistration_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("crossC", o => o
            .WithPrefix("/crossC")
            .AddProfilesFrom(s => s.InAssemblyOf<ScanTargetProfile>()));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData("crossD", o => o
                .WithPrefix("/crossD")
                .AddEntitySetProfile<ScanTargetProfile>()));

        Assert.Contains("cannot be shared across registrations", ex.Message, StringComparison.Ordinal);
    }

    // ── #534: within ONE registration, scan + explicit is order-independent ─────────────────

    /// <summary>
    /// #534. Both orders express the same intent — "scan the assembly, and I also want this type
    /// explicitly" — so both must behave the same way. <c>explicit → scan</c> was already a silent
    /// no-op; <c>scan → explicit</c> threw, blaming a *"duplicate AddEntitySetProfile call"* that
    /// does not exist. Same shape and same fix as #488 item 5(c) on the delta path.
    /// </summary>
    [Fact]
    public void SameRegistration_ScanThenExplicit_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOhData("order534A", o => o
            .WithPrefix("/order534A")
            .AddProfilesFrom(s => s.InAssemblyOf<ScanTargetProfile>())
            .AddEntitySetProfile<ScanTargetProfile>());

        Assert.Single(services, d => d.ServiceType == typeof(ScanTargetProfile));
    }

    /// <summary>The order that already worked, asserted beside it so the pair is the claim.</summary>
    [Fact]
    public void SameRegistration_ExplicitThenScan_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOhData("order534B", o => o
            .WithPrefix("/order534B")
            .AddEntitySetProfile<ScanTargetProfile>()
            .AddProfilesFrom(s => s.InAssemblyOf<ScanTargetProfile>()));

        Assert.Single(services, d => d.ServiceType == typeof(ScanTargetProfile));
    }

    /// <summary>
    /// The bound. A GENUINE duplicate — two explicit calls — must still throw, and the message names
    /// a remedy that now actually applies. Without this the fix would be "never throw", which
    /// removes a real diagnostic.
    /// </summary>
    [Fact]
    public void SameRegistration_TwoExplicitCalls_StillThrows()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData("order534C", o => o
                .WithPrefix("/order534C")
                .AddEntitySetProfile<ScanTargetProfile>()
                .AddEntitySetProfile<ScanTargetProfile>()));

        Assert.Contains("Remove the duplicate AddEntitySetProfile call", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other bound, and the reason #534 was not folded into #488: this method also owns the
    /// CROSS-registration guard, which must keep throwing. Scan in one registration, explicit in
    /// another, is still an error — same two calls as the first test, different registrations.
    /// </summary>
    [Fact]
    public void CrossRegistration_ScanThenExplicit_StillThrows()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("order534D", o => o
            .WithPrefix("/order534D")
            .AddProfilesFrom(s => s.InAssemblyOf<ScanTargetProfile>()));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData("order534E", o => o
                .WithPrefix("/order534E")
                .AddEntitySetProfile<ScanTargetProfile>()));

        Assert.Contains("cannot be shared across registrations", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two SEPARATE scans (no explicit call on either side) of the same assembly into two
    /// registrations — the shape from the issue's own reproduction snippet, verbatim.
    /// </summary>
    [Fact]
    public void AddProfilesFrom_BothRegistrationsScanTheSameAssembly_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOhData("crossE", o => o
            .WithPrefix("/crossE")
            .AddProfilesFrom(s => s.InAssemblyOf<ScanTargetProfile>()));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddOhData("crossF", o => o
                .WithPrefix("/crossF")
                .AddProfilesFrom(s => s.InAssemblyOf<ScanTargetProfile>())));

        Assert.Contains("cannot be shared across registrations", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bounding assertion: scanning the SAME assembly twice into the SAME registration (e.g. two
    /// overlapping <c>AddProfilesFrom</c> calls) must stay a silent, idempotent no-op — only a
    /// DIFFERENT registration is rejected. Without this, a fix that over-widened the guard to
    /// reject same-registration re-scans would pass the tests above vacuously.
    /// </summary>
    [Fact]
    public void AddProfilesFrom_SameAssemblyScannedTwiceInSameRegistration_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOhData("crossG", o => o
            .WithPrefix("/crossG")
            .AddProfilesFrom(s => s.InAssemblyOf<ScanTargetProfile>())
            .AddProfilesFrom(s => s.InAssemblyOf<ScanTargetProfile>()));

        int count = services.Count(d =>
            d.ServiceType == typeof(ScanTargetProfile) &&
            d.Lifetime == ServiceLifetime.Scoped);
        Assert.Equal(1, count);
    }
}
