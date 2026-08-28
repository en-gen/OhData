using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;

namespace OhData;

/// <summary>
/// #253: single source of truth for the OData/EDM name of a structural property. A property's one
/// OData name — the identifier <c>$metadata</c> advertises and the spelling <c>$select</c>/
/// <c>$filter</c>/<c>$orderby</c> are resolved against — is its
/// <c>[System.Text.Json.Serialization.JsonPropertyName]</c> value when present, otherwise its CLR
/// property name. This mirrors the response serializer (System.Text.Json emits the
/// <c>[JsonPropertyName]</c> verbatim, ahead of any naming policy), so the EDM/query surface and the
/// wire payload agree on exactly one name per property and a <c>$select</c> of a renamed property no
/// longer silently drops it from the response.
/// </summary>
/// <remarks>
/// The EDM name is deliberately NOT run through the response naming policy
/// (<c>OhDataBuilder.WithJsonPropertyNamingPolicy</c>): <c>$metadata</c> always advertises the
/// PascalCase/CLR (or <c>[JsonPropertyName]</c>) identifier regardless of whether payloads are emitted
/// camelCase, matching OData §4.4.
/// </remarks>
internal static class ODataPropertyNaming
{
    /// <summary>
    /// The OData/EDM name of <paramref name="property"/>: its <c>[JsonPropertyName]</c> value when
    /// present, otherwise its CLR name.
    /// </summary>
    internal static string ResolveEdmName(PropertyInfo property)
    {
        JsonPropertyNameAttribute? rename = property.GetCustomAttribute<JsonPropertyNameAttribute>();
        return rename is not null ? rename.Name : property.Name;
    }

    // Fold-in #7 (#325/#326 review, perf hygiene): FindClrPropertyByEdmName is now called per
    // navigation per entity from BOTH SerializeBounded and OmitUnexpandedNavigations (previously it
    // was mostly a startup/validation-time helper), so its GetProperties()+LINQ+attribute-lookup
    // cost is now paid on the hot serialization path. Memoized per (type, edmName), matching this
    // codebase's `s_`-prefixed static-cache convention (e.g. OhDataEndpointFactory's
    // s_navRefKeyAccessorCache). Keyed on the exact string passed in (ordinal tuple equality) rather
    // than a case-normalized key: callers overwhelmingly pass the canonical EDM name (navProp.Name),
    // so this trades a theoretical extra cache entry for a differently-cased caller against not
    // having to normalize (and risk a culture-sensitive ToUpper/ToLower bug) on every lookup —
    // correctness (case-INSENSITIVE matching) is unaffected either way since it happens inside the
    // cached computation, not in the key.
    private static readonly ConcurrentDictionary<(Type Type, string EdmName), PropertyInfo?>
        s_clrPropertyByEdmNameCache = new();

    /// <summary>
    /// Finds the CLR property of <paramref name="type"/> whose OData/EDM name
    /// (<see cref="ResolveEdmName"/>) equals <paramref name="edmName"/>, matched case-insensitively
    /// (OData identifiers resolve case-insensitively). Falls back to a direct case-insensitive CLR
    /// name lookup so a caller passing a plain CLR name still resolves. Returns <c>null</c> when no
    /// property matches.
    /// </summary>
    internal static PropertyInfo? FindClrPropertyByEdmName(Type type, string edmName)
    {
        return s_clrPropertyByEdmNameCache.GetOrAdd((type, edmName), key =>
        {
            (Type t, string name) = key;
            PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0)
                .ToArray();

            // Prefer a property whose resolved EDM name matches (covers [JsonPropertyName] renames);
            // fall back to a caller that already holds a CLR name (e.g. an un-renamed property).
            return props.FirstOrDefault(p => string.Equals(ResolveEdmName(p), name, StringComparison.OrdinalIgnoreCase))
                ?? props.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>
    /// True when some public instance property of <paramref name="type"/> has
    /// <paramref name="edmName"/> as its OData/EDM name (<see cref="ResolveEdmName"/>), matched
    /// case-insensitively. Unlike <see cref="FindClrPropertyByEdmName"/> this does NOT fall back to a
    /// CLR-name match — it is the strict "is this a valid OData property identifier?" test used to
    /// validate a query option, so a <c>[JsonPropertyName]</c>-renamed property's CLR name is rejected
    /// exactly as the main <c>$select</c>/<c>$orderby</c> parser rejects it.
    /// </summary>
    internal static bool IsKnownEdmName(Type type, string edmName) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Any(p => string.Equals(ResolveEdmName(p), edmName, StringComparison.OrdinalIgnoreCase));
}
