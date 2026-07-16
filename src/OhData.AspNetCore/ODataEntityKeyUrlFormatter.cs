using System;
using System.Globalization;

namespace OhData.AspNetCore;

/// <summary>
/// Formats a CLR key value into a canonical OData entity-id URL segment suitable for embedding
/// inside parentheses, e.g. <c>Products(42)</c>, <c>Products('encoded value')</c>,
/// <c>Products(3fa85f64-5717-4562-b3fc-2c963f66afa6)</c> (OData Part 2 §4.3.1 -- Addressing
/// Entities). Mirrors <see cref="ODataKeyParser"/> so entity-id URLs the server emits (POST 201
/// Location/Content-Location, OData-EntityId, @odata.id) round-trip back through key parsing.
/// </summary>
/// <remarks>
/// S4 fix: the previous approach -- <c>string.Format(CultureInfo.InvariantCulture, "{0}", key)</c>
/// with no quoting -- produced non-canonical, unescaped URLs for string keys (e.g. <c>Things(abc)</c>
/// instead of <c>Things('abc')</c>), and broke entirely for a key containing a space, single quote,
/// or reserved URL character. This formatter quotes string keys per OData key syntax, doubles
/// embedded single quotes (matching <see cref="ODataKeyParser"/>'s unescaping), and percent-encodes
/// the result so it is safe to embed directly in a URL path segment.
/// </remarks>
internal static class ODataEntityKeyUrlFormatter
{
    /// <summary>
    /// Formats <paramref name="key"/> as an OData key literal and percent-encodes the result so
    /// it is safe for use in a URL path segment, e.g. <c>{EntitySetName}({Format(key)})</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    public static string Format(object key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key), "OData key value must not be null.");
        string literal = key switch
        {
            // String keys: single-quoted, with embedded single quotes doubled ('') -- the exact
            // inverse of ODataKeyParser.Parse's `rawKey[1..^1].Replace("''", "'")` unescaping.
            string s => $"'{s.Replace("'", "''")}'",
            Guid g => g.ToString(),
            bool b => b ? "true" : "false",
            char c => $"'{c}'",
            // Round-trip ("O") format so ODataKeyParser's DateTimeStyles.RoundtripKind parse
            // recovers the same Kind/offset and full sub-second precision.
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            // Numeric types, enums, and any other TypeConverter-parseable key: ODataKeyParser
            // parses these unquoted via TypeDescriptor conversion (e.g. Enum.Parse doesn't accept
            // OData-quoted literals), so no quoting is applied here either.
            _ => string.Format(CultureInfo.InvariantCulture, "{0}", key),
        };
        // Percent-encode so string keys with reserved chars (space, /, ?, #, ') are safe in the
        // URL path. Integers, GUIDs, and datetimes contain only unreserved/[:] characters, so
        // encoding is a no-op or reversible round-trip for them (routing decodes path segments
        // before ODataKeyParser sees them).
        return Uri.EscapeDataString(literal);
    }
}
