using System;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Unit-level coverage for <see cref="ODataKeyParser"/>'s string branch and its single throw site.
/// </summary>
/// <remarks>
/// <c>KeyParsingTests</c> reaches the parser only through a route and only with well-formed
/// keys, so the malformed shapes are here. <c>''</c> earns its place specifically: it is the
/// only input that separates <c>&gt;= 2</c> from <c>&gt; 2</c>. A key that opens a quote without
/// closing it is not an error — OData quotes are transport, so a raw value is returned verbatim
/// and the key TYPE decides whether it parses.
/// </remarks>
public class ODataKeyParserTests
{
    [Theory]
    // Well-formed: the quotes are transport, not data.
    [InlineData("'abc'", "abc")]
    // Unquoted -- returned verbatim. Forcing the condition true strips the first and last char.
    [InlineData("abc", "abc")]
    // One quote only. Neither shape is a quoted key, and each fails a different conjunct.
    [InlineData("'abc", "'abc")]
    [InlineData("abc'", "abc'")]
    // A lone quote: both ends match the SAME character, so only the length test rejects it.
    [InlineData("'", "'")]
    // The empty quoted string. Two characters, so this is the one input separating >= 2 from > 2.
    [InlineData("''", "")]
    // Doubled quotes are an escaped quote -- the inverse of what ODataEntityKeyUrlFormatter writes.
    [InlineData("'it''s'", "it's")]
    [InlineData("'''", "'")]
    public void StringKey_StripsOnlyAMatchedSurroundingPair(string raw, string expected) =>
        Assert.Equal(expected, ODataKeyParser.Parse(raw, typeof(string)));

    // The formatter/parser round trip lives in ODataEntityKeyUrlFormatterTests, which owns both
    // halves and covers a strict superset of the string shapes.

    [Fact]
    public void TimeOnlyKey_Parses() =>
        Assert.Equal(new TimeOnly(13, 45, 30), ODataKeyParser.Parse("13:45:30", typeof(TimeOnly)));

    [Fact]
    public void DateOnlyKey_Parses() =>
        Assert.Equal(new DateOnly(2026, 9, 12), ODataKeyParser.Parse("2026-09-12", typeof(DateOnly)));

    /// <summary>
    /// The documented <see cref="System.ComponentModel.TypeConverter"/> fallback, which nothing
    /// else reached.
    /// </summary>
    /// <remarks>
    /// It is also the only way to observe the <c>TimeOnly</c> test above: every other special-cased
    /// type is matched on an EARLIER line, so a custom key is the one input that must fall PAST the
    /// <c>TimeOnly</c> check and still parse. Without it, inverting that check changes nothing any
    /// test can see.
    /// </remarks>
    [Fact]
    public void ACustomKeyType_ParsesThroughItsRegisteredTypeConverter() =>
        Assert.Equal(new Sku("ABC-123"), ODataKeyParser.Parse("ABC-123", typeof(Sku)));

    /// <summary>
    /// #496 finding 4: one throw site, one exception type, and a message naming both the value and
    /// the target type. Every keyed route's <c>catch</c> is written against exactly this.
    /// </summary>
    [Fact]
    public void AnUnparseableKey_ThrowsODataKeyFormatException_NamingTheValueAndType()
    {
        var ex = Assert.Throws<ODataKeyFormatException>(
            () => ODataKeyParser.Parse("nope", typeof(Guid)));

        // Contains, not Equal: BadKeyError discards this message and writes its own envelope,
        // so the sentence is internal prose. What is contractual is that it names the value and
        // the target type, because that is what reaches the log line.
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Guid", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
        // Derived, so an out-of-assembly `catch (FormatException)` still works.
        Assert.IsAssignableFrom<FormatException>(ex);
    }

    [Fact]
    public void NullableKey_TreatsTheLiteralNullAsNull_AndParsesAnythingElseAsTheUnderlyingType()
    {
        Assert.Null(ODataKeyParser.Parse("null", typeof(int?)));
        Assert.Equal(7, ODataKeyParser.Parse("7", typeof(int?)));
    }
}

/// <summary>A key type that parses only through a registered converter.</summary>
[System.ComponentModel.TypeConverter(typeof(SkuConverter))]
public readonly record struct Sku(string Value);

public sealed class SkuConverter : System.ComponentModel.TypeConverter
{
    public override bool CanConvertFrom(
        System.ComponentModel.ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(
        System.ComponentModel.ITypeDescriptorContext? context,
        System.Globalization.CultureInfo? culture,
        object value) =>
        value is string s ? new Sku(s) : base.ConvertFrom(context, culture, value);
}
