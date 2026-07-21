using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhData.Client.Internal;

/// <summary>
/// #253: resolves the OData query-option name a client emits for a CLR member so it matches the
/// server's EDM name. A member carrying <c>[System.Text.Json.Serialization.JsonPropertyName]</c> is
/// emitted under that exact name (verbatim, ahead of any naming policy — the same precedence the
/// server's EDM and System.Text.Json use); otherwise the configured naming policy converts the CLR
/// name (or the CLR name verbatim when no policy is set). Keeps <c>$filter</c>/<c>$select</c>/
/// <c>$orderby</c>/<c>$expand</c> property names in agreement with the server's <c>$metadata</c>.
/// </summary>
internal static class ODataMemberName
{
    internal static string Resolve(MemberInfo member, JsonNamingPolicy? namingPolicy)
    {
        JsonPropertyNameAttribute? rename = member.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (rename is not null) return rename.Name;
        return namingPolicy?.ConvertName(member.Name) ?? member.Name;
    }

    /// <summary>
    /// Resolves the OData identifier for a member used as a path segment in
    /// <c>$expand</c>/<c>$filter</c>/<c>$orderby</c>. A NAVIGATION segment keeps its CLR name — the
    /// server intentionally does NOT apply the <c>[System.Text.Json.Serialization.JsonPropertyName]</c>
    /// rename to navigation identifiers (only the nav's JSON payload key is renamed — see #184), so a
    /// renamed navigation must be addressed by its CLR name or the server EDM 400s. A STRUCTURAL
    /// (scalar/leaf) segment is renamed via <see cref="Resolve"/> so its query name matches
    /// <c>$metadata</c> (#253).
    /// </summary>
    internal static string ResolveSegment(MemberInfo member, JsonNamingPolicy? namingPolicy)
    {
        if (member is PropertyInfo pi && IsNavigationType(pi.PropertyType))
            return member.Name;
        return Resolve(member, namingPolicy);
    }

    /// <summary>
    /// Approximates the server's <c>PropertyKind.Navigation</c> classification (#184) from a CLR
    /// member type alone: an entity reference, or a collection of entities, is a navigation; a
    /// primitive/string/known-scalar (or a value-type "complex" struct) is structural. The client has
    /// no EDM to consult, so this is the closest signal available.
    /// </summary>
    private static bool IsNavigationType(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;

        // A collection (other than string/byte[]) is a to-many navigation when its element type is a
        // navigation type; inspect the element and fall through to the scalar test below.
        if (t != typeof(string) && t != typeof(byte[]) && TryGetEnumerableElementType(t) is Type element)
            t = Nullable.GetUnderlyingType(element) ?? element;

        if (IsKnownScalar(t)) return false;

        // Reference types that are not known scalars are entity navigations. Non-scalar value types
        // (complex structs) are treated as structural — the safe default, since structural properties
        // keep the [JsonPropertyName] rename.
        return t.IsClass;
    }

    private static bool IsKnownScalar(Type t) =>
        t.IsPrimitive
        || t.IsEnum
        || t == typeof(string)
        || t == typeof(decimal)
        || t == typeof(Guid)
        || t == typeof(DateTime)
        || t == typeof(DateTimeOffset)
        || t == typeof(TimeSpan)
        || t == typeof(DateOnly)
        || t == typeof(TimeOnly)
        || t == typeof(byte[]);

    private static Type? TryGetEnumerableElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();

        return type.GetInterfaces()
            .Concat(type.IsInterface ? new[] { type } : Array.Empty<Type>())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0])
            .FirstOrDefault();
    }
}
