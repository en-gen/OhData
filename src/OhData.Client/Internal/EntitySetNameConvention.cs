using System;
using System.Reflection;

namespace OhData.Client.Internal;

internal static class EntitySetNameConvention
{
    /// <summary>
    /// Returns the OData entity set name for <paramref name="entityType"/>:
    /// uses <see cref="ODataEntitySetAttribute"/> when present, otherwise applies
    /// simple English pluralisation to the type name.
    /// </summary>
    public static string Resolve(Type entityType)
    {
        var attr = entityType.GetCustomAttribute<ODataEntitySetAttribute>();
        if (attr is not null) return attr.Name;
        return Pluralize(entityType.Name);
    }

    internal static string Pluralize(string name)
    {
        if (name.Length == 0) return name;

        // ends in consonant + y  →  replace y with ies  (Category → Categories)
        if (name.EndsWith('y') && name.Length > 1 && !"aeiouAEIOU".Contains(name[^2]))
            return name[..^1] + "ies";

        // ends in s, sh, ch, x, z  →  append es  (Status → Statuses)
        if (name.EndsWith("sh", StringComparison.Ordinal) ||
            name.EndsWith("ch", StringComparison.Ordinal) ||
            name.EndsWith('s') || name.EndsWith('x') || name.EndsWith('z'))
            return name + "es";

        // default: append s  (Product → Products, Order → Orders)
        return name + "s";
    }
}
