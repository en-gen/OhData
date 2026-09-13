using System;
using Microsoft.AspNetCore.OData.Deltas;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// The expression-based <see cref="Delta{T}"/> sugar (<c>IsChanged</c>/<c>TryGetChanged</c>).
/// </summary>
/// <remarks>
/// These moved OUT of <c>DeltaMappingTests</c> when #675 moved that file into the Mapper package's
/// own test project. <c>DeltaExtensions</c> is about <see cref="Delta{T}"/> rather than about
/// mapping, so it stayed in the core when #665 moved the delta-MAPPING subsystem out — which means
/// its tests belong here too. The fixture is deliberately local rather than shared with the mapper
/// suite, so neither project's tests depend on the other's types.
/// </remarks>
public class DeltaExtensionsTests
{
    private sealed class DeltaSugarDto
    {
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
    }

    [Fact]
    public void IsChanged_And_TryGetChanged_ReflectPresence()
    {
        var delta = new Delta<DeltaSugarDto>();
        delta.TrySetPropertyValue(nameof(DeltaSugarDto.Name), "x");

        Assert.True(delta.IsChanged(d => d.Name));
        Assert.False(delta.IsChanged(d => d.Price));

        Assert.True(delta.TryGetChanged(d => d.Name, out string? name));
        Assert.Equal("x", name);

        // The out value is default when the property was not set, so a caller cannot read a stale
        // or uninitialised value off a false return.
        Assert.False(delta.TryGetChanged(d => d.Price, out decimal price));
        Assert.Equal(0m, price);
    }

    /// <summary>
    /// The selector must name a member. Anything else has no property to look up, so it is refused
    /// rather than silently answering "not changed".
    /// </summary>
    [Fact]
    public void DeltaExpressionSugar_RejectsNonMemberExpression()
    {
        var delta = new Delta<DeltaSugarDto>();

        Assert.Throws<ArgumentException>(() => delta.IsChanged(d => d.Name.ToUpper()));
        Assert.Throws<ArgumentException>(() => delta.TryGetChanged(d => d.Name.ToUpper(), out string? _));
    }
}
