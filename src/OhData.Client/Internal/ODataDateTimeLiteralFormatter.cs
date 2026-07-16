using System;
using System.Globalization;

namespace OhData.Client.Internal;

/// <summary>
/// Formats <see cref="DateTime"/>/<see cref="DateTimeOffset"/> values as OData 4.0 literal
/// strings, preserving full sub-second precision (trimmed of trailing zeros) and always emitting
/// an explicit UTC/offset designator. Shared by <see cref="FilterTranslator"/> ($filter literals)
/// and <see cref="ODataKeyFormatter"/> (entity-id key literals) so both contexts stay in sync --
/// a key literal and a filter literal for the same value must format identically, since both are
/// parsed by the same server-side <c>DateTimeStyles.RoundtripKind</c> logic.
/// </summary>
internal static class ODataDateTimeLiteralFormatter
{
    /// <summary>
    /// Formats a <see cref="DateTime"/> as an OData <c>Edm.DateTimeOffset</c> literal
    /// (Part 2 §5.1.1.9, dateTimeOffsetValue requires an explicit "Z" or numeric offset).
    /// </summary>
    /// <remarks>
    ///   Kind.Utc:         emit as-is with "Z".
    ///   Kind.Local:       convert to UTC first, then emit with "Z". A local wall-clock time
    ///                     (e.g. from DateTime.Now) is ambiguous off-machine -- the timezone
    ///                     that produced it isn't part of the value -- so the only literal
    ///                     that means the same instant everywhere is its UTC equivalent.
    ///                     Preserving the local offset would only be correct on the machine
    ///                     that captured the value and would silently drift under DST.
    ///   Kind.Unspecified: treated as UTC (not rejected). This matches the convention most
    ///                     ORMs/serializers use for "no timezone info" values (e.g.
    ///                     System.Text.Json's own DateTime round-tripping), and keeps the
    ///                     common case -- comparing a DB column mapped to Kind.Unspecified but
    ///                     stored in UTC -- working without forcing every caller to Kind-tag
    ///                     values. It is always spec-legal since a "Z" suffix is always emitted.
    /// </remarks>
    public static string FormatDateTime(DateTime dt)
    {
        DateTime utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
        string s = utc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        return TrimFractionalZeros(s, "Z");
    }

    /// <summary>Formats a <see cref="DateTimeOffset"/> as an OData <c>Edm.DateTimeOffset</c> literal.</summary>
    public static string FormatDateTimeOffset(DateTimeOffset dto)
    {
        // Format date+time with fractional seconds (no offset/Z in pattern), then trim,
        // then append the correct suffix to avoid trimming "0" digits from the offset.
        string s = dto.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        string suffix = dto.Offset == TimeSpan.Zero
            ? "Z"
            : dto.ToString("zzz", CultureInfo.InvariantCulture);
        return TrimFractionalZeros(s, suffix);
    }

    private static string TrimFractionalZeros(string s, string suffix)
    {
        // Find the fractional seconds dot (search starts after "yyyy-MM-ddT" = 11 chars)
        int dotIdx = s.IndexOf('.', 10);
        if (dotIdx < 0) return s + suffix;
        // Trim trailing zeros from the fractional digits.
        int trimEnd = s.Length;
        while (trimEnd > dotIdx + 1 && s[trimEnd - 1] == '0') trimEnd--;
        if (trimEnd == dotIdx + 1) trimEnd = dotIdx; // all zeros — remove dot too
        return s[..trimEnd] + suffix;
    }
}
