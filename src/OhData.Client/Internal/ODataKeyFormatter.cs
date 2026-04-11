using System;
using System.Globalization;

namespace OhData.Client.Internal;

/// <summary>
/// Formats a CLR key value as an OData 4.0 key literal suitable for embedding
/// inside parentheses: <c>Products(42)</c>, <c>Products('foo')</c>, <c>Products(guid-value)</c>.
/// Mirrors the server-side <c>ODataKeyParser</c> so keys round-trip correctly.
/// </summary>
internal static class ODataKeyFormatter
{
    public static string Format(object key) => key switch
    {
        string s          => $"'{s.Replace("'", "''")}'",
        Guid g            => g.ToString(),
        bool b            => b ? "true" : "false",
        char c            => $"'{c}'",
        DateTime dt       => dt.Kind == DateTimeKind.Utc
                                 ? $"'{dt:yyyy-MM-ddTHH:mm:ssZ}'"
                                 : $"'{dt:yyyy-MM-ddTHH:mm:ss}'",
        DateTimeOffset dto => dto.Offset == TimeSpan.Zero
                                 ? $"'{dto:yyyy-MM-ddTHH:mm:ssZ}'"
                                 : $"'{dto:yyyy-MM-ddTHH:mm:sszzz}'",
        DateOnly d        => $"'{d:yyyy-MM-dd}'",
        TimeOnly t        => $"'{t:HH:mm:ss}'",
        // All other numeric types: invariant culture, no quotes
        _ => string.Format(CultureInfo.InvariantCulture, "{0}", key),
    };
}
