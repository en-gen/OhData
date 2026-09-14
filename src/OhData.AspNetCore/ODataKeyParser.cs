using System;
using System.ComponentModel;
using System.Globalization;

namespace OhData;

internal static class ODataKeyParser
{
    /// <summary>
    /// Parses a raw OData key string (from the route segment) into the target CLR type.
    /// Supports all primitive CLR types, <see cref="Guid"/>, <see cref="DateTime"/>,
    /// <see cref="DateTimeOffset"/>, <see cref="DateOnly"/>, <see cref="TimeOnly"/>,
    /// enums, and any type with a registered <see cref="TypeConverter"/>.
    /// String keys may be wrapped in single quotes (OData convention) -- the quotes are stripped.
    /// Nullable&lt;T&gt; keys are supported: the literal string "null" returns <c>null</c>,
    /// any other value is parsed as the underlying type T.
    /// </summary>
    public static object? Parse(string rawKey, Type keyType)
    {
        var underlying = Nullable.GetUnderlyingType(keyType);
        if (underlying is not null)
        {
            if (rawKey == "null") return null;
            return Parse(rawKey, underlying);
        }

        // String keys arrive as 'value' -- strip the surrounding single quotes
        if (keyType == typeof(string))
        {
            return IsQuotedLiteral(rawKey)
                ? rawKey[1..^1].Replace("''", "'")
                : rawKey;
        }

        try
        {
            switch (Type.GetTypeCode(keyType))
            {
                // #677: unlike string, a quoted char literal is never unescaped (nothing to
                // double). Stays inside the try -- char.Parse throws on an empty or
                // multi-character literal, which the catch below converts to ODataKeyFormatException.
                case TypeCode.Char when IsQuotedLiteral(rawKey):
                    return char.Parse(rawKey[1..^1]);
                case TypeCode.Int16: return short.Parse(rawKey, CultureInfo.InvariantCulture);
                case TypeCode.Int32: return int.Parse(rawKey, CultureInfo.InvariantCulture);
                case TypeCode.Int64: return long.Parse(rawKey, CultureInfo.InvariantCulture);
                case TypeCode.UInt16: return ushort.Parse(rawKey, CultureInfo.InvariantCulture);
                case TypeCode.UInt32: return uint.Parse(rawKey, CultureInfo.InvariantCulture);
                case TypeCode.UInt64: return ulong.Parse(rawKey, CultureInfo.InvariantCulture);
                case TypeCode.Byte: return byte.Parse(rawKey, CultureInfo.InvariantCulture);
                case TypeCode.SByte: return sbyte.Parse(rawKey, CultureInfo.InvariantCulture);
                case TypeCode.Decimal: return decimal.Parse(rawKey, CultureInfo.InvariantCulture);
                case TypeCode.Double: return double.Parse(rawKey, CultureInfo.InvariantCulture);
                case TypeCode.Single: return float.Parse(rawKey, CultureInfo.InvariantCulture);
                case TypeCode.Boolean: return bool.Parse(rawKey);
                case TypeCode.DateTime:
                    return DateTime.Parse(rawKey, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }

            if (keyType == typeof(Guid)) return Guid.Parse(rawKey);
            if (keyType == typeof(DateTimeOffset))
                return DateTimeOffset.Parse(rawKey, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (keyType == typeof(DateOnly)) return DateOnly.Parse(rawKey, CultureInfo.InvariantCulture);
            if (keyType == typeof(TimeOnly)) return TimeOnly.Parse(rawKey, CultureInfo.InvariantCulture);

            // Enums and custom types with a registered TypeConverter
            var converter = TypeDescriptor.GetConverter(keyType);
            return converter.ConvertFromInvariantString(rawKey)
                ?? throw new FormatException($"Cannot parse '{rawKey}' as {keyType.Name}.");
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException or NotSupportedException)
        {
            // #496 finding 4: ODataKeyFormatException, not a bare FormatException. This is the ONLY
            // throw site every keyed route's `catch (...)` -> BadKeyError clause is meant to serve,
            // and while that clause caught FormatException it also caught one thrown by the
            // profile's own handler -- answering "Invalid key format for X: '1'" for a key that had
            // parsed cleanly. See ODataKeyFormatException's remarks. A derived type keeps every
            // out-of-assembly `catch (FormatException)` working.
            throw new ODataKeyFormatException($"Cannot parse '{rawKey}' as {keyType.Name}.", ex);
        }
    }

    // #682: the delimiter test is one rule shared by the string and char branches, and it must be
    // ordinal. string.StartsWith(string)/EndsWith(string) with no StringComparison default to
    // CurrentCulture, and under ICU a ' followed by a combining mark (U+0300, say) collates as one
    // element, so StartsWith("'") on such a key returned false and the quotes were never stripped.
    // These are wire-format delimiters, not culture-sensitive text; the char overloads used below
    // are ordinal by definition.
    private static bool IsQuotedLiteral(string rawKey) =>
        rawKey.Length >= 2 && rawKey.StartsWith('\'') && rawKey.EndsWith('\'');
}
