using System;
using System.Collections.Generic;
using System.Globalization;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Unit-level coverage for the #351 formatter: the exact text each CLR category contributes to
/// the ETag hash. These assertions pin the round-trip specifier chosen per type — including the
/// three types for which the obvious choice, <c>"O"</c>, is not a legal specifier at all.
/// </summary>
public class ETagValueFormatterTests
{
    private static readonly DateTime Instant =
        new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_000_000);

    [Fact]
    public void DateTime_UsesRoundTripPattern_KeepingSubSecondAndKind()
    {
        Assert.Equal("2026-01-01T10:00:00.1000000Z", ETagValueFormatter.Format(Instant));
        Assert.Equal("2026-01-01T10:00:00.1000000",
            ETagValueFormatter.Format(DateTime.SpecifyKind(Instant, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void DateTimeOffset_UsesRoundTripPattern_KeepingSubSecondAndOffset()
    {
        Assert.Equal("2026-01-01T10:00:00.1000000+00:00",
            ETagValueFormatter.Format(new DateTimeOffset(Instant)));
    }

    [Fact]
    public void DateOnly_And_TimeOnly_UseRoundTripPattern()
    {
        Assert.Equal("2026-01-01", ETagValueFormatter.Format(new DateOnly(2026, 1, 1)));
        // The bare ToString() renders this as "2:30 PM" — seconds AND fraction gone.
        Assert.Equal("14:30:00.5000000", ETagValueFormatter.Format(new TimeOnly(14, 30, 0, 500)));
    }

    /// <summary>
    /// <c>"O"</c> throws <see cref="FormatException"/> for <see cref="TimeSpan"/> and
    /// <see cref="Guid"/>, which is why the formatter cannot blanket-apply it. These are the
    /// substitute round-trip specifiers.
    /// </summary>
    [Fact]
    public void TimeSpan_And_Guid_UseTheirOwnRoundTripSpecifiers()
    {
        Assert.Throws<FormatException>(() => TimeSpan.FromTicks(1).ToString("O", CultureInfo.InvariantCulture));
        Assert.Throws<FormatException>(() => Guid.Empty.ToString("O"));

        Assert.Equal("00:00:12.3456789", ETagValueFormatter.Format(TimeSpan.FromTicks(123456789)));
        Assert.Equal("00000000-0000-0000-0000-000000000001",
            ETagValueFormatter.Format(new Guid("00000000-0000-0000-0000-000000000001")));
    }

    /// <summary>
    /// <c>"O"</c> is not a valid numeric specifier either — the numeric categories use invariant
    /// culture with the default (shortest-round-trippable, since .NET Core 3.0) form.
    /// </summary>
    [Theory]
    [InlineData(0.1 + 0.2, "0.30000000000000004")]
    [InlineData(1.0 / 3.0, "0.3333333333333333")]
    [InlineData(double.MaxValue, "1.7976931348623157E+308")]
    [InlineData(double.Epsilon, "5E-324")]
    [InlineData(-0.0, "-0")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NaN, "NaN")]
    public void Double_IsShortestRoundTrippable_UnderInvariantCulture(double value, string expected)
    {
        Assert.Throws<FormatException>(() => (1.5).ToString("O", CultureInfo.InvariantCulture));
        Assert.Equal(expected, ETagValueFormatter.Format(value));
        Assert.Equal(value, double.Parse(ETagValueFormatter.Format(value), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Float_IsShortestRoundTrippable_UnderInvariantCulture()
    {
        Assert.Equal("0.33333334", ETagValueFormatter.Format(1f / 3f));
        Assert.Equal("3.4028235E+38", ETagValueFormatter.Format(float.MaxValue));
    }

    /// <summary>Invariant culture is sufficient for <see cref="decimal"/>: the default form is
    /// already exact and preserves scale, so only the separator needed pinning.</summary>
    [Fact]
    public void Decimal_UsesInvariantCulture_AndPreservesScale()
    {
        Assert.Equal("1234.56", ETagValueFormatter.Format(1234.56m));
        Assert.Equal("1234.560", ETagValueFormatter.Format(1234.560m));
        Assert.Equal("-1234.56", ETagValueFormatter.Format(-1234.56m));
    }

    [Fact]
    public void Integers_Bool_Char_String_Enum_AreExactAndInvariant()
    {
        Assert.Equal("-9876543", ETagValueFormatter.Format(-9876543L));
        Assert.Equal("255", ETagValueFormatter.Format((byte)255));
        Assert.Equal("True", ETagValueFormatter.Format(true));
        Assert.Equal("x", ETagValueFormatter.Format('x'));
        Assert.Equal("hello", ETagValueFormatter.Format("hello"));
        Assert.Equal("Monday", ETagValueFormatter.Format(DayOfWeek.Monday));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("sv-SE")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void EveryCategory_FormatsIdenticallyUnderAnyCulture(string culture)
    {
        object[] values =
        {
            Instant,
            new DateTimeOffset(Instant),
            new DateOnly(2026, 1, 1),
            new TimeOnly(14, 30, 0, 500),
            TimeSpan.FromTicks(123456789),
            Guid.Empty,
            -1234.56d,
            double.PositiveInfinity,
            -1234.56f,
            -1234.56m,
            -9876543L,
            -42,
            true,
            'x',
            "hello",
            DayOfWeek.Monday,
        };

        foreach (object value in values)
        {
            string enUs = CultureScope.Run("en-US", () => ETagValueFormatter.Format(value));
            string other = CultureScope.Run(culture, () => ETagValueFormatter.Format(value));
            Assert.Equal(enUs, other);
        }
    }

    /// <summary>Documents the failure mode the fix removes: the bare <c>ToString()</c> the
    /// formatter replaced really does collapse distinct values.</summary>
    [Fact]
    public void BareToString_LosesInformation_ThatTheFormatterKeeps()
    {
        DateTime a = Instant;                              // .1000000
        DateTime b = Instant.AddTicks(8_000_000);          // .9000000, same second

        Assert.Equal(a.ToString(CultureInfo.InvariantCulture), b.ToString(CultureInfo.InvariantCulture));
        Assert.NotEqual(ETagValueFormatter.Format(a), ETagValueFormatter.Format(b));

        var t1 = new TimeOnly(14, 30, 0, 500);
        var t2 = new TimeOnly(14, 30, 5, 900);             // five seconds apart

        Assert.Equal(t1.ToString(CultureInfo.InvariantCulture), t2.ToString(CultureInfo.InvariantCulture));
        Assert.NotEqual(ETagValueFormatter.Format(t1), ETagValueFormatter.Format(t2));
    }

    // ── DateTimeKind.Local must not import the server's timezone configuration ──────

    /// <summary>
    /// <c>"O"</c> renders a <see cref="DateTimeKind.Local"/> value with
    /// <c>TimeZoneInfo.Local.GetUtcOffset(...)</c> appended, which would put the *server's*
    /// timezone into the hash: a client reading from a <c>TZ=UTC</c> node and writing to a
    /// <c>TZ=America/Chicago</c> node would get 412 forever, and a tzdata DST change would rotate
    /// every outstanding ETag on a single node. The formatter must emit the wall-clock reading
    /// plus a Kind marker instead.
    /// </summary>
    [Fact]
    public void LocalDateTime_DoesNotEmbedTheServerUtcOffset()
    {
        var local = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local).AddTicks(1_000_000);

        string formatted = ETagValueFormatter.Format(local);

        // The "O" output would have been e.g. "2026-01-01T10:00:00.1000000-06:00".
        Assert.Equal("2026-01-01T10:00:00.1000000[Local]", formatted);
        Assert.DoesNotContain(
            TimeZoneInfo.Local.GetUtcOffset(local).ToString("c", CultureInfo.InvariantCulture),
            formatted,
            StringComparison.Ordinal);
    }

    /// <summary>All three <see cref="DateTimeKind"/>s over the same ticks stay distinct — the Kind
    /// is part of the value and dropping it would be a new collision.</summary>
    [Fact]
    public void AllThreeDateTimeKinds_OverTheSameTicks_FormatDistinctly()
    {
        long ticks = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_000_000).Ticks;
        string utc = ETagValueFormatter.Format(new DateTime(ticks, DateTimeKind.Utc));
        string local = ETagValueFormatter.Format(new DateTime(ticks, DateTimeKind.Local));
        string unspecified = ETagValueFormatter.Format(new DateTime(ticks, DateTimeKind.Unspecified));

        Assert.Equal(3, new HashSet<string>(new[] { utc, local, unspecified }, StringComparer.Ordinal).Count);
    }

    /// <summary>A Local value still keeps full sub-second precision — the Kind fix must not
    /// re-open the original #351 hole.</summary>
    [Fact]
    public void LocalDateTime_KeepsSubSecondPrecision()
    {
        var a = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local).AddMilliseconds(100);
        var b = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local).AddMilliseconds(900);

        Assert.NotEqual(ETagValueFormatter.Format(a), ETagValueFormatter.Format(b));
    }

    // ── Type discriminator must not carry assembly versions ────────────────────────

    /// <summary>
    /// <see cref="Type.FullName"/> embeds each generic argument's assembly identity *including its
    /// version*, so the net8.0 and net10.0 builds of this package would mint different ETags for
    /// identical data. The discriminator must be derived from names only.
    /// </summary>
    [Theory]
    [InlineData(typeof(int), "System.Int32")]
    [InlineData(typeof(byte[]), "System.Byte[]")]
    [InlineData(typeof(int?), "System.Nullable`1[System.Int32]")]
    [InlineData(typeof((decimal, decimal)), "System.ValueTuple`2[System.Decimal,System.Decimal]")]
    [InlineData(typeof(System.Collections.Generic.List<string>), "System.Collections.Generic.List`1[System.String]")]
    public void StableTypeName_IsVersionFree(Type type, string expected)
    {
        string name = ETagValueFormatter.StableTypeName(type);

        Assert.Equal(expected, name);
        Assert.DoesNotContain("Version=", name, StringComparison.Ordinal);
        Assert.DoesNotContain("PublicKeyToken", name, StringComparison.Ordinal);

        // The exact thing this replaces, for contrast.
        if (type.IsConstructedGenericType)
        {
            Assert.Contains("Version=", type.FullName!, StringComparison.Ordinal);
        }
    }

    /// <summary>Nested types stay distinct even though <c>Type.Name</c> drops the declaring
    /// type.</summary>
    [Fact]
    public void StableTypeName_KeepsNestedTypesDistinct()
    {
        Assert.NotEqual(
            ETagValueFormatter.StableTypeName(typeof(OuterA.Shared)),
            ETagValueFormatter.StableTypeName(typeof(OuterB.Shared)));
    }

    private static class OuterA { internal sealed class Shared { } }
    private static class OuterB { internal sealed class Shared { } }

    // ── Selector-type allowlist ────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(byte[]))]
    [InlineData(typeof(System.Collections.Immutable.ImmutableArray<byte>))]
    [InlineData(typeof(ReadOnlyMemory<byte>))]
    [InlineData(typeof(Memory<byte>))]
    [InlineData(typeof(ArraySegment<byte>))]
    [InlineData(typeof(string))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(bool?))]
    [InlineData(typeof(char))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long?))]
    [InlineData(typeof(double))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(DateTimeOffset?))]
    [InlineData(typeof(DateOnly))]
    [InlineData(typeof(TimeOnly))]
    [InlineData(typeof(TimeSpan))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(DayOfWeek))]
    [InlineData(typeof(DayOfWeek?))]
    public void SupportedSelectorTypes_AreAccepted(Type type) =>
        Assert.True(ETagValueFormatter.IsSupportedSelectorType(type), type.ToString());

    /// <summary>
    /// Each of these formats to a value that is either constant across every row (no
    /// <c>ToString()</c> override → the type's own name) or culture-dependent
    /// (<see cref="TimeZoneInfo"/> → its UI-culture <c>DisplayName</c>).
    /// </summary>
    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(System.Collections.Generic.List<string>))]
    [InlineData(typeof(System.Collections.Generic.List<byte>))]
    [InlineData(typeof(int[]))]
    [InlineData(typeof(TimeZoneInfo))]
    [InlineData(typeof(ETagValueFormatterTests))]
    public void UnsupportedSelectorTypes_AreRejected(Type type) =>
        Assert.False(ETagValueFormatter.IsSupportedSelectorType(type), type.ToString());

    /// <summary>Demonstrates the failure the allowlist exists to prevent: a type with no
    /// <c>ToString()</c> override formats to its own type name — identical for every row.</summary>
    [Fact]
    public void TypeWithoutToStringOverride_FormatsToAConstant()
    {
        var a = new System.Collections.Generic.List<string> { "a" };
        var b = new System.Collections.Generic.List<string> { "COMPLETELY", "DIFFERENT" };

        Assert.Equal(ETagValueFormatter.Format(a), ETagValueFormatter.Format(b));
        Assert.False(ETagValueFormatter.IsSupportedSelectorType(typeof(System.Collections.Generic.List<string>)));
    }
}
