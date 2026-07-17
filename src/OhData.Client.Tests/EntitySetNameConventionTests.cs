using OhData.Client;
using OhData.Client.Internal;
using Xunit;

namespace OhData.Client.Tests;

/// <summary>
/// Tests for <see cref="EntitySetNameConvention.Pluralize"/> and
/// <see cref="EntitySetNameConvention.Resolve"/>.
/// </summary>
public class EntitySetNameConventionTests
{
    // ── s / sh / ch / x / z endings → es ────────────────────────────────────

    [Fact] public void Pluralize_Box_Boxes() => Assert.Equal("Boxes", EntitySetNameConvention.Pluralize("Box"));
    [Fact] public void Pluralize_Match_Matches() => Assert.Equal("Matches", EntitySetNameConvention.Pluralize("Match"));
    [Fact] public void Pluralize_Wish_Wishes() => Assert.Equal("Wishes", EntitySetNameConvention.Pluralize("Wish"));
    [Fact] public void Pluralize_Status_Statuses() => Assert.Equal("Statuses", EntitySetNameConvention.Pluralize("Status"));
    [Fact] public void Pluralize_Fizz_Fizzes() => Assert.Equal("Fizzes", EntitySetNameConvention.Pluralize("Fizz"));

    // ── consonant + y → ies ─────────────────────────────────────────────────

    [Fact] public void Pluralize_Category_Categories() => Assert.Equal("Categories", EntitySetNameConvention.Pluralize("Category"));
    [Fact] public void Pluralize_Entry_Entries() => Assert.Equal("Entries", EntitySetNameConvention.Pluralize("Entry"));

    // ── vowel + y → s (NOT ies) ─────────────────────────────────────────────

    [Fact] public void Pluralize_Key_Keys() => Assert.Equal("Keys", EntitySetNameConvention.Pluralize("Key"));
    [Fact] public void Pluralize_Day_Days() => Assert.Equal("Days", EntitySetNameConvention.Pluralize("Day"));

    // ── default: append s ───────────────────────────────────────────────────

    [Fact] public void Pluralize_Product_Products() => Assert.Equal("Products", EntitySetNameConvention.Pluralize("Product"));
    [Fact] public void Pluralize_Widget_Widgets() => Assert.Equal("Widgets", EntitySetNameConvention.Pluralize("Widget"));
    [Fact] public void Pluralize_Order_Orders() => Assert.Equal("Orders", EntitySetNameConvention.Pluralize("Order"));

    // ── [ODataEntitySet] attribute takes precedence ──────────────────────────

    [ODataEntitySet("CustomSetName")]
    private sealed class AnnotatedEntity { }

    private sealed class UnannotatedEntity { }

    [Fact]
    public void Resolve_AttributePresent_UsesAttributeName() =>
        Assert.Equal("CustomSetName", EntitySetNameConvention.Resolve(typeof(AnnotatedEntity)));

    [Fact]
    public void Resolve_NoAttribute_UsesPluralisedTypeName() =>
        Assert.Equal("UnannotatedEntities", EntitySetNameConvention.Resolve(typeof(UnannotatedEntity)));
}
