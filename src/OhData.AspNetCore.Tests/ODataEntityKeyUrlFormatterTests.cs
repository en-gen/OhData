using System;
using System.Globalization;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Coverage for <see cref="ODataEntityKeyUrlFormatter"/>, the writer half of the entity-id round
/// trip that <see cref="ODataKeyParser"/> reads back.
/// </summary>
/// <remarks>
/// <para>
/// A mutation sweep put this file at 5 killed / 10 survived — the worst ratio in the core package.
/// Its output is wire contract: every <c>Location</c>, <c>Content-Location</c>,
/// <c>OData-EntityId</c> and <c>@odata.id</c> the server emits is built here, and a client is
/// expected to GET it back. A wrong format specifier does not throw; it ships a URL that no longer
/// addresses the entity.
/// </para>
/// <para>
/// Each case asserts the LITERAL before percent-encoding and then the round trip through the
/// parser. The literal pins the specifier (dropping <c>"O"</c> silently truncates a
/// <see cref="DateTime"/> to second precision); the round trip pins that the two halves agree,
/// which is the property the pair exists for and which neither half can state alone.
/// </para>
/// </remarks>
public class ODataEntityKeyUrlFormatterTests
{
    /// <summary>
    /// The literal, before percent-encoding. Routing decodes a path segment before the parser sees
    /// it, so this is also exactly what <see cref="ODataKeyParser.Parse"/> is handed.
    /// </summary>
    private static string Literal(object key) =>
        Uri.UnescapeDataString(ODataEntityKeyUrlFormatter.Format(key));

    private static readonly DateTime Instant =
        new DateTime(2026, 9, 12, 10, 30, 0, DateTimeKind.Utc).AddTicks(1_234_567);

    [Fact]
    public void StringKey_IsSingleQuoted_WithEmbeddedQuotesDoubled()
    {
        Assert.Equal("'abc'", Literal("abc"));
        Assert.Equal("'it''s'", Literal("it's"));
        Assert.Equal("''''", Literal("'"));
        Assert.Equal("''", Literal(""));
    }

    [Fact]
    public void IntegralAndDecimalKeys_AreUnquoted_AndInvariant()
    {
        Assert.Equal("42", Literal(42));
        Assert.Equal("-7", Literal(-7));
        Assert.Equal("9007199254740993", Literal(9007199254740993L));
        // Invariant, so a comma-decimal culture cannot change the URL the server emits.
        Assert.Equal("1.5", Literal(1.5m));
    }

    [Fact]
    public void BoolKey_IsLowercase() =>
        Assert.Equal(("true", "false"), (Literal(true), Literal(false)));

    [Fact]
    public void GuidKey_IsUnquotedHyphenated() =>
        Assert.Equal(
            "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            Literal(Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6")));

    [Fact]
    public void CharKey_IsSingleQuoted() => Assert.Equal("'x'", Literal('x'));

    /// <summary>
    /// Round-trip ("O") for the two instant types: the parser reads them with
    /// <c>DateTimeStyles.RoundtripKind</c>, so anything less than full sub-second precision plus
    /// the Kind/offset marker loses information the key needs.
    /// </summary>
    [Fact]
    public void DateTimeKeys_UseTheRoundTripSpecifier()
    {
        Assert.Equal("2026-09-12T10:30:00.1234567Z", Literal(Instant));
        Assert.Equal("2026-09-12T10:30:00.1234567+00:00", Literal(new DateTimeOffset(Instant)));
    }

    [Fact]
    public void DateOnlyAndTimeOnlyKeys_UseTheirOwnPatterns()
    {
        Assert.Equal("2026-09-12", Literal(new DateOnly(2026, 9, 12)));
        Assert.Equal("13:45:30", Literal(new TimeOnly(13, 45, 30)));
    }

    // ── The pairing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Format then Parse returns the original value. This is the only assertion that constrains the
    /// two halves TOGETHER: either alone can be self-consistently wrong.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoundTrippableKeys))]
    public void EveryKeyShape_RoundTripsBackThroughTheParser(object key) =>
        Assert.Equal(key, ODataKeyParser.Parse(Literal(key), key.GetType()));

    public static TheoryData<object> RoundTrippableKeys() => new()
    {
        42,
        -7,
        9007199254740993L,
        1.5m,
        true,
        false,
        Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"),
        "abc",
        "it's",
        "'",
        "",
        "a b/c?d#e",
        new DateOnly(2026, 9, 12),
        new TimeOnly(13, 45, 30),
    };

    /// <summary>
    /// Percent-encoding is what makes the literal safe in a path segment. Asserted on the ENCODED
    /// form, since the reserved characters are exactly the ones that would otherwise split the URL.
    /// </summary>
    [Fact]
    public void ReservedCharacters_ArePercentEncoded()
    {
        string encoded = ODataEntityKeyUrlFormatter.Format("a b/c?d#e");

        foreach (char c in new[] { ' ', '/', '?', '#', '\'' })
        {
            Assert.DoesNotContain(c.ToString(), encoded, StringComparison.Ordinal);
        }

        Assert.Equal("'a b/c?d#e'", Uri.UnescapeDataString(encoded));
    }

    [Fact]
    public void ANullKey_IsRefused_RatherThanFormattedAsEmpty()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => ODataEntityKeyUrlFormatter.Format(null!));
        Assert.Equal("key", ex.ParamName);
    }
}
