namespace OhData.AspNetCore;

internal static class ODataKeyParser
{
    /// <summary>
    /// Parses a raw OData key string (from the route segment) into the target CLR type.
    /// Handles string keys wrapped in single quotes, Guid, int, long, short, and
    /// falls back to <see cref="Convert.ChangeType(object, Type)"/> for other types.
    /// </summary>
    public static object Parse(string rawKey, Type keyType)
    {
        // String keys arrive as 'value' — strip the quotes
        if (rawKey.StartsWith("'") && rawKey.EndsWith("'") && rawKey.Length >= 2)
            rawKey = rawKey[1..^1];

        try
        {
            if (keyType == typeof(Guid)) return Guid.Parse(rawKey);
            if (keyType == typeof(int)) return int.Parse(rawKey);
            if (keyType == typeof(long)) return long.Parse(rawKey);
            if (keyType == typeof(short)) return short.Parse(rawKey);
            if (keyType == typeof(string)) return rawKey;

            var converter = System.ComponentModel.TypeDescriptor.GetConverter(keyType);
            return converter.ConvertFromInvariantString(rawKey)
                ?? throw new FormatException($"Cannot parse '{rawKey}' as {keyType.Name}.");
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new FormatException($"Cannot parse '{rawKey}' as {keyType.Name}.", ex);
        }
    }
}
