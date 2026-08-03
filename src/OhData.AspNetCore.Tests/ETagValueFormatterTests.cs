using System;
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
            string enUs = WithCulture("en-US", () => ETagValueFormatter.Format(value));
            string other = WithCulture(culture, () => ETagValueFormatter.Format(value));
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

    private static T WithCulture<T>(string culture, Func<T> action)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var target = new CultureInfo(culture);
            CultureInfo.CurrentCulture = target;
            CultureInfo.CurrentUICulture = target;
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
