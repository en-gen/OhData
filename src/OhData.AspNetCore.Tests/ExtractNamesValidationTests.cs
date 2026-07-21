using System;
using System.Linq;
using System.Reflection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

public sealed class ExtNamesModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ExtNamesChild Child { get; set; } = new();
}

public sealed class ExtNamesChild
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// #227: the expression overloads of FilterProperties/OrderByProperties/SelectProperties/
/// ExpandProperties document that only direct property access (x =&gt; x.Name) is supported,
/// but ExtractNames accepted ANY member access — x =&gt; x.Name.Length silently allowlisted
/// "Length" and x =&gt; x.Child.Name silently allowlisted "Name". These tests pin the
/// documented contract: nested member access throws ArgumentException from the constructor.
/// </summary>
public class ExtractNamesValidationTests
{
    private sealed class NestedSelectProfile : EntitySetProfile<int, ExtNamesModel>
    {
        public NestedSelectProfile() : base(x => x.Id)
        {
            SelectProperties(x => x.Name.Length);
        }
    }

    [Fact]
    public void SelectProperties_NestedMemberAccess_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new NestedSelectProfile());
        Assert.Contains("Nested access", ex.Message);
    }

    private sealed class NestedFilterProfile : EntitySetProfile<int, ExtNamesModel>
    {
        public NestedFilterProfile() : base(x => x.Id)
        {
            FilterProperties(x => x.Child.Name);
        }
    }

    [Fact]
    public void FilterProperties_NestedMemberAccess_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new NestedFilterProfile());
        Assert.Contains("Nested access", ex.Message);
    }

    private sealed class NestedOrderByProfile : EntitySetProfile<int, ExtNamesModel>
    {
        public NestedOrderByProfile() : base(x => x.Id)
        {
            OrderByProperties(x => x.Child.Id);
        }
    }

    [Fact]
    public void OrderByProperties_NestedMemberAccess_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new NestedOrderByProfile());
        Assert.Contains("Nested access", ex.Message);
    }

    private sealed class NestedExpandProfile : EntitySetProfile<int, ExtNamesModel>
    {
        public NestedExpandProfile() : base(x => x.Id)
        {
            ExpandProperties(x => x.Name.Length);
        }
    }

    [Fact]
    public void ExpandProperties_NestedMemberAccess_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new NestedExpandProfile());
        Assert.Contains("Nested access", ex.Message);
    }

    private sealed class ComputedSelectorProfile : EntitySetProfile<int, ExtNamesModel>
    {
        public ComputedSelectorProfile() : base(x => x.Id)
        {
            // Not a member access at all after Convert-stripping (BinaryExpression) — covers
            // the first arm of GetDirectMember's compound check via the allowlist path.
            OrderByProperties(x => x.Id + 1);
        }
    }

    [Fact]
    public void OrderByProperties_ComputedExpression_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ComputedSelectorProfile());
        Assert.Contains("direct property access", ex.Message);
    }

    private sealed class ValidSelectorsProfile : EntitySetProfile<int, ExtNamesModel>
    {
        public ValidSelectorsProfile() : base(x => x.Id)
        {
            // x.Id is a value type, so the lambda body carries a boxing Convert node —
            // stripping it must still land on the lambda parameter and extract "Id".
            SelectProperties(x => x.Id, x => x.Name);
        }
    }

    [Fact]
    public void SelectProperties_DirectAccessIncludingBoxedValueType_ExtractsNames()
    {
        var profile = new ValidSelectorsProfile();

        // The allowlist is private configuration state (only handed to the EDM at
        // VisitModelBuilder time), so read it via reflection to pin the extracted names.
        FieldInfo field = typeof(EntitySetProfile<int, ExtNamesModel>).GetField(
            "_selectProperties", BindingFlags.NonPublic | BindingFlags.Instance)!;
        string[]? names = (string[]?)field.GetValue(profile);

        Assert.NotNull(names);
        Assert.Equal(new[] { "Id", "Name" }, names);
    }
}
