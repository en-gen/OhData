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
            return rawKey.StartsWith("'") && rawKey.EndsWith("'") && rawKey.Length >= 2
                ? rawKey[1..^1].Replace("''", "'")
                : rawKey;
        }

        try
        {
            switch (Type.GetTypeCode(keyType))
            {
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
            throw new FormatException($"Cannot parse '{rawKey}' as {keyType.Name}.", ex);
        }
    }
}
