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

    /// <summary>
    /// Applies simple English pluralisation rules to <paramref name="name"/>:
    /// <list type="bullet">
    ///   <item>Consonant + <c>y</c> ending → replace <c>y</c> with <c>ies</c>
    ///         (e.g. <c>Category</c> → <c>Categories</c>, <c>Entry</c> → <c>Entries</c>).</item>
    ///   <item>Vowel + <c>y</c> ending → append <c>s</c>
    ///         (e.g. <c>Key</c> → <c>Keys</c>, <c>Day</c> → <c>Days</c>).</item>
    ///   <item><c>s</c>, <c>sh</c>, <c>ch</c>, <c>x</c>, or <c>z</c> ending → append <c>es</c>
    ///         (e.g. <c>Box</c> → <c>Boxes</c>, <c>Match</c> → <c>Matches</c>,
    ///          <c>Status</c> → <c>Statuses</c>).</item>
    ///   <item>All other endings → append <c>s</c>
    ///         (e.g. <c>Product</c> → <c>Products</c>).</item>
    /// </list>
    /// <para>
    /// These rules cover the most common English type names used in software.
    /// Irregular nouns (e.g. <c>Person</c> → <c>People</c>, <c>Datum</c> → <c>Data</c>)
    /// are not handled — apply <see cref="ODataEntitySetAttribute"/> on the entity type
    /// to specify the exact entity set name when the convention produces a wrong result.
    /// </para>
    /// </summary>
    /// <summary>
    /// Applies simple English pluralisation rules to <paramref name="name"/>:
    /// <list type="bullet">
    ///   <item>Consonant + <c>y</c> ending → replace <c>y</c> with <c>ies</c>
    ///         (e.g. <c>Category</c> → <c>Categories</c>, <c>Entry</c> → <c>Entries</c>).</item>
    ///   <item>Vowel + <c>y</c> ending → append <c>s</c>
    ///         (e.g. <c>Key</c> → <c>Keys</c>, <c>Day</c> → <c>Days</c>).</item>
    ///   <item><c>s</c>, <c>sh</c>, <c>ch</c>, <c>x</c>, or <c>z</c> ending → append <c>es</c>
    ///         (e.g. <c>Box</c> → <c>Boxes</c>, <c>Match</c> → <c>Matches</c>,
    ///          <c>Status</c> → <c>Statuses</c>).</item>
    ///   <item>All other endings → append <c>s</c>
    ///         (e.g. <c>Product</c> → <c>Products</c>).</item>
    /// </list>
    /// <para>
    /// These rules cover the most common English type names used in software.
    /// Irregular nouns (e.g. <c>Person</c> → <c>People</c>, <c>Datum</c> → <c>Data</c>)
    /// are not handled — apply <see cref="ODataEntitySetAttribute"/> on the entity type
    /// to specify the exact entity set name when the convention produces a wrong result.
    /// </para>
    /// </summary>
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
