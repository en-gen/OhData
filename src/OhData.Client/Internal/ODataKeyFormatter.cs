using System;
using System.Globalization;

namespace OhData.Client.Internal;

/// <summary>
/// Formats a CLR key value as an OData 4.0 key literal suitable for embedding
/// inside parentheses: <c>Products(42)</c>, <c>Products('foo')</c>, <c>Products(guid-value)</c>.
/// Mirrors the server-side <c>ODataKeyParser</c> so keys round-trip correctly.
/// </summary>
/// <remarks>
/// The returned string is percent-encoded so it is safe to embed directly in a URL path
/// without further encoding (e.g. string keys with <c>/</c> or spaces are encoded as
/// <c>%27foo%2Fbar%27</c>).
/// </remarks>
internal static class ODataKeyFormatter
{
    /// <summary>
    /// Formats <paramref name="key"/> as an OData key literal and percent-encodes the result
    /// so it is safe for use in a URL path segment.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    public static string Format(object key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key), "OData key value must not be null.");
        string literal = key switch
        {
            string s => $"'{s.Replace("'", "''")}'",
            Guid g => g.ToString(),
            bool b => b ? "true" : "false",
            char c => $"'{c}'",
            DateTime dt => dt.Kind == DateTimeKind.Utc
                                      ? dt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                                      : dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.Offset == TimeSpan.Zero
                                      ? dto.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                                      : dto.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            DateOnly d => $"'{d:yyyy-MM-dd}'",
            TimeOnly t => $"'{t:HH:mm:ss}'",
            // All other numeric types: invariant culture, no quotes
            _ => string.Format(CultureInfo.InvariantCulture, "{0}", key),
        };
        // Percent-encode so string keys with reserved chars (/, ?, #, space) are safe in the URL path.
        // Integers and GUIDs contain only [0-9a-f-] so encoding is a no-op for them.
        return Uri.EscapeDataString(literal);
    }
}
