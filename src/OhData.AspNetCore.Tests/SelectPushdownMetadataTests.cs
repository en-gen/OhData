using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class PushMetaModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string ComputedLabel => $"{Name} (#{Id})"; // get-only
    public string InitOnly { get; init; } = "";
}

/// <summary>
/// #206 Task 1: metadata plumbing for $select projection pushdown — the
/// SelectPushdownEnabled flag resolution, StructuralPropertyInfo.Property exposure,
/// and ETag property-name capture on UseETag.
/// </summary>
public class SelectPushdownMetadataTests
{
    private sealed class DefaultProfile : EntitySetProfile<int, PushMetaModel>
    {
        public DefaultProfile() : base(x => x.Id) { }
    }

    private sealed class OptOutProfile : EntitySetProfile<int, PushMetaModel>
    {
        public OptOutProfile() : base(x => x.Id)
        {
            SelectPushdownEnabled = false;
        }
    }

    private static void Seal(IVisitModelBuilder profile, EntitySetDefaults? defaults = null) =>
        profile.VisitModelBuilder(
            new Microsoft.OData.ModelBuilder.ODataConventionModelBuilder(),
            defaults ?? new EntitySetDefaults());

    [Fact]
    public void SelectPushdownEnabled_DefaultsTrue_ViaEntitySetDefaults()
    {
        var profile = new DefaultProfile();
        Seal(profile);
        Assert.True(((IEntitySetEndpointSource)profile).SelectPushdownEnabled);
    }

    [Fact]
    public void SelectPushdownEnabled_ProfileOptOut_Wins()
    {
        var profile = new OptOutProfile();
        Seal(profile);
        Assert.False(((IEntitySetEndpointSource)profile).SelectPushdownEnabled);
    }

    [Fact]
    public void SelectPushdownEnabled_ServerDefaultOff_Inherited()
    {
        var profile = new DefaultProfile();
        Seal(profile, new EntitySetDefaults { SelectPushdownEnabled = false });
        Assert.False(((IEntitySetEndpointSource)profile).SelectPushdownEnabled);
    }

    [Fact]
    public void StructuralProperties_ExposePropertyInfo_WithSetterSemantics()
    {
        IEntitySetEndpointSource source = new DefaultProfile();
        var byName = source.StructuralProperties.ToDictionary(p => p.Name);

        Assert.All(byName.Values, p => Assert.NotNull(p.Property));
        // Plain auto-property and init-only both have usable public setters.
        Assert.True(byName["Name"].Property.SetMethod is { IsPublic: true });
        Assert.True(byName["InitOnly"].Property.SetMethod is { IsPublic: true });
        // Get-only computed property has none.
        Assert.Null(byName["ComputedLabel"].Property.SetMethod);
    }

    private sealed class ETagDirectProfile : EntitySetProfile<int, PushMetaModel>
    {
        public ETagDirectProfile() : base(x => x.Id)
        {
            UseETag(x => x.Name, x => x.Price);
        }
    }

    private sealed class ETagComputedProfile : EntitySetProfile<int, PushMetaModel>
    {
        public ETagComputedProfile() : base(x => x.Id)
        {
            UseETag(x => x.Name.Length); // not a direct member — names unknowable
        }
    }

    [Fact]
    public void UseETag_DirectSelectors_CaptureNames()
    {
        IEntitySetEndpointSource source = new ETagDirectProfile();
        Assert.NotNull(source.ETagPropertyNames);
        Assert.Equal(
            new[] { "Name", "Price" },
            source.ETagPropertyNames!.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void UseETag_ComputedSelector_NamesNull()
    {
        IEntitySetEndpointSource source = new ETagComputedProfile();
        Assert.Null(source.ETagPropertyNames);
    }

    [Fact]
    public void UseETag_CachedDelegatePath_StillCapturesNames()
    {
        // The compiled ETag delegate is cached per concrete profile type; the SECOND
        // construction takes the cache early-return path. Names must still be captured there.
        _ = new ETagDirectProfile();
        IEntitySetEndpointSource second = new ETagDirectProfile();
        Assert.NotNull(second.ETagPropertyNames);
        Assert.Contains("Price", second.ETagPropertyNames!);
    }
}
