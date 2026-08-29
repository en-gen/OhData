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
