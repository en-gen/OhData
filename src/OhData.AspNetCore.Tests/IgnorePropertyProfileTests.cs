using System;
using System.Collections.Generic;
using System.Linq;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class IgnProfileModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal CostBasis { get; set; }
    public string InternalNotes { get; set; } = "";
    public List<IgnProfileChild>? Children { get; set; }
}

public sealed class IgnProfileChild
{
    public int Id { get; set; }
}

public class IgnorePropertyProfileTests
{
    private sealed class BasicIgnoreProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public BasicIgnoreProfile() : base(x => x.Id)
        {
            Ignore(x => x.CostBasis, x => x.InternalNotes);
            Ignore(x => x.CostBasis); // duplicate — set semantics, harmless
        }
    }

    [Fact]
    public void Ignore_AccumulatesNames_ExposedViaEndpointSource()
    {
        IEntitySetEndpointSource source = new BasicIgnoreProfile();
        Assert.Equal(
            new[] { "CostBasis", "InternalNotes" },
            source.IgnoredPropertyNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Ignore_ExcludesNamesFromStructuralProperties()
    {
        IEntitySetEndpointSource source = new BasicIgnoreProfile();
        var names = source.StructuralProperties.Select(p => p.Name).ToList();
        Assert.DoesNotContain("CostBasis", names);
        Assert.DoesNotContain("InternalNotes", names);
        Assert.Contains("Name", names);
        Assert.Contains("Id", names);
    }

    private sealed class NoIgnoreProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public NoIgnoreProfile() : base(x => x.Id) { }
    }

    [Fact]
    public void NoIgnore_ExposesEmptyCollection_NotNull()
    {
        IEntitySetEndpointSource source = new NoIgnoreProfile();
        Assert.NotNull(source.IgnoredPropertyNames);
        Assert.Empty(source.IgnoredPropertyNames);
    }

    private sealed class KeyIgnoreProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public KeyIgnoreProfile() : base(x => x.Id)
        {
            Ignore(x => x.Id);
        }
    }

    [Fact]
    public void Ignore_KeyProperty_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new KeyIgnoreProfile());
        Assert.Contains("Id", ex.Message);
        Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EmptyIgnoreProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public EmptyIgnoreProfile() : base(x => x.Id)
        {
            Ignore();
        }
    }

    [Fact]
    public void Ignore_NoSelectors_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new EmptyIgnoreProfile());
    }

    private sealed class NestedExpressionProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public NestedExpressionProfile() : base(x => x.Id)
        {
            Ignore(x => x.Name.Length);
        }
    }

    [Fact]
    public void Ignore_NestedExpression_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new NestedExpressionProfile());
    }

    private sealed class NullArrayIgnoreProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public NullArrayIgnoreProfile() : base(x => x.Id)
        {
            Ignore(null!);
        }
    }

    [Fact]
    public void Ignore_NullArray_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new NullArrayIgnoreProfile());
    }

    private sealed class ComputedExpressionProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public ComputedExpressionProfile() : base(x => x.Id)
        {
            Ignore(x => x.Id + 1);
        }
    }

    [Fact]
    public void Ignore_ComputedExpression_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new ComputedExpressionProfile());
    }

    private sealed class FieldIgnoreProfile : EntitySetProfile<int, IgnFieldModel>
    {
        public FieldIgnoreProfile() : base(x => x.Id)
        {
            Ignore(x => x.Code);
        }
    }

    [Fact]
    public void Ignore_Field_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new FieldIgnoreProfile());
        Assert.Contains("field", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LateIgnoreProfile : EntitySetProfile<int, IgnProfileModel>
    {
        public LateIgnoreProfile() : base(x => x.Id) { }
        public void IgnoreLate() => Ignore(x => x.CostBasis);
    }

    [Fact]
    public void Ignore_AfterSeal_ThrowsInvalidOperationException()
    {
        var profile = new LateIgnoreProfile();
        ((IVisitModelBuilder)profile).VisitModelBuilder(
            new Microsoft.OData.ModelBuilder.ODataConventionModelBuilder(), new EntitySetDefaults());
        Assert.Throws<InvalidOperationException>(profile.IgnoreLate);
    }
}

public sealed class IgnFieldModel
{
    public int Id { get; set; }
    public string Code = "";
}
