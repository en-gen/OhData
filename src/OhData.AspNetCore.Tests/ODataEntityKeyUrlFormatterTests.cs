using System;
using System.ComponentModel;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Coverage for <see cref="ODataEntityKeyUrlFormatter"/>, the writer half of the entity-id round
/// trip that <see cref="ODataKeyParser"/> reads back.
/// </summary>
/// <remarks>
/// Its output is wire contract: a client is expected to GET back the <c>Location</c> /
/// <c>@odata.id</c> the server emits. Each case pins the LITERAL before percent-encoding,
/// because the specifier is the contract — dropping <c>"O"</c> truncates a
/// <see cref="DateTime"/> to second precision without throwing — and the round-trip theory
/// pins that the two halves agree, which neither half can state alone.
/// </remarks>
public class ODataEntityKeyUrlFormatterTests
{
    /// <summary>
    /// The literal, before percent-encoding -- a close model of what routing hands the parser,
    /// which decodes the segment first. (Not identical in every case: <c>%2F</c> is the known
    /// place ASP.NET Core path handling is not a plain unescape.)
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
        // Formatted invariantly. (Not asserted under a comma-decimal culture here; the
        // invariance is stated by the source, not pinned by this input.)
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

    /// <summary>
    /// A <see cref="char"/> key is single-quoted by the writer, and since #677
    /// <see cref="ODataKeyParser"/> strips the quotes back off -- the pair round-trips, so
    /// <c>char</c> is in the theory below alongside every other shape.
    /// </summary>
    [Fact]
    public void CharKey_IsSingleQuoted_AndRoundTrips()
    {
        Assert.Equal("'x'", Literal('x'));
        Assert.Equal('x', ODataKeyParser.Parse(Literal('x'), typeof(char)));
    }

    /// <summary>
    /// The formatter doesn't double an embedded quote inside a char literal the way it does for
    /// a string -- there's only ever one character between the delimiters, quote or not -- so
    /// <c>'''</c> (the literal for the char <c>'</c>) still has to parse back to <c>'</c>.
    /// </summary>
    [Fact]
    public void CharKey_ThatIsItselfAQuote_RoundTrips()
    {
        Assert.Equal("'''", Literal('\''));
        Assert.Equal('\'', ODataKeyParser.Parse(Literal('\''), typeof(char)));
    }

    /// <summary>
    /// A bare, unquoted character is the pre-#677 shape -- it never reaches the new quoted
    /// branch and still falls through to <see cref="TypeDescriptor"/>, exactly as before.
    /// </summary>
    [Fact]
    public void CharKey_UnquotedBareCharacter_StillParses() =>
        Assert.Equal('x', ODataKeyParser.Parse("x", typeof(char)));

    [Fact]
    public void CharKey_EmptyOrMultiCharacterQuotedLiteral_FailsCleanly()
    {
        Assert.Throws<ODataKeyFormatException>(() => ODataKeyParser.Parse("''", typeof(char)));
        Assert.Throws<ODataKeyFormatException>(() => ODataKeyParser.Parse("'ab'", typeof(char)));
    }

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
        'x',
        '\'',
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
