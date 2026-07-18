using System;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Coverage pass: profile-configuration guard/validation paths (constructor-time throws) that no
/// functional test previously exercised. Each asserts the profile rejects an invalid declaration at
/// construction, which is where the framework surfaces developer misconfiguration. (The
/// parameterless double-`RequireAuthorization()` and double-`RequireRoles()` cases are already
/// covered by EndpointMappingTests; this file adds the ones that were not.)
/// </summary>
public class CoverageValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void MaxTop_NonPositive_Throws(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BadMaxTopProfile(value));

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void MaxRequestBodyBytes_NonPositive_Throws(long value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BadMaxBodyProfile(value));

    [Fact]
    public void ComputedKeySelector_Throws() =>
        Assert.Throws<ArgumentException>(() => new ComputedKeyProfile());

    [Fact]
    public void RequireAuthorization_TwiceWithPolicy_Throws() =>
        Assert.Throws<InvalidOperationException>(() => new DoublePolicyProfile());
}

internal class BadMaxTopProfile : EntitySetProfile<int, Widget>
{
    public BadMaxTopProfile(int v) : base(x => x.Id) { MaxTop = v; }
}

internal class BadMaxBodyProfile : EntitySetProfile<int, Widget>
{
    public BadMaxBodyProfile(long v) : base(x => x.Id) { MaxRequestBodyBytes = v; }
}

internal class ComputedKeyProfile : EntitySetProfile<int, Widget>
{
    public ComputedKeyProfile() : base(x => x.Id + 1) { }
}

internal class DoublePolicyProfile : EntitySetProfile<int, Widget>
{
    public DoublePolicyProfile() : base(x => x.Id)
    {
        RequireAuthorization("A");
        RequireAuthorization("B"); // second policy → throws
    }
}
