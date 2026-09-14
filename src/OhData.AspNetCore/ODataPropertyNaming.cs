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
/// camelCase. Payload property names are the identifiers the EDM declares (OData JSON
/// Format); the number is deliberately not cited, since Part 1 has no §4.4.
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
    // cost is now paid on the hot serialization path.
    //
    // #537: memoized per TYPE, not per (type, edmName) string pair. The previous cache keyed on the
    // caller's exact string, on the stated invariant that every caller passes a name already drawn
    // from the model's own finite vocabulary. That invariant was FALSE for ApplyNavOrderBy: its
    // IsKnownEdmName gate and its FindClrPropertyByEdmName call were two SEPARATE, un-linked lookups
    // over the same raw $orderby token, so the gate bounded which PROPERTIES could pass but not which
    // STRINGS reached the cache as keys — 256 case spellings of one 8-letter property name produced
    // 256 permanent entries (measured on #537), and a single request could add roughly 870 more (one
    // per comma-separated clause) before hitting Kestrel's default request-line limit. Capping the
    // cache the way OpenTypeJsonOptions' sibling is capped was the fallback; this is strictly better,
    // because keying on the TYPE ALONE removes the client-supplied string from the key entirely — the
    // key space is now the app's own registered model types, fixed at startup, never request traffic
    // — for every caller at once, not just the one that broke the invariant. It also removes the
    // duplicate reflection scan a caller like ApplyNavOrderBy used to pay for asking "is this known"
    // and "which property is it" as two separate calls (see TryResolveEdmName below).
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> s_propertiesCache = new();

    private static PropertyInfo[] GetCandidateProperties(Type type) =>
        s_propertiesCache.GetOrAdd(type, t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .ToArray());

    /// <summary>
    /// Finds the CLR property of <paramref name="type"/> whose OData/EDM name
    /// (<see cref="ResolveEdmName"/>) equals <paramref name="edmName"/>, matched case-insensitively
    /// (OData identifiers resolve case-insensitively). Falls back to a direct case-insensitive CLR
    /// name lookup so a caller passing a plain CLR name still resolves. Returns <c>null</c> when no
    /// property matches.
    /// </summary>
    internal static PropertyInfo? FindClrPropertyByEdmName(Type type, string edmName)
    {
        PropertyInfo[] props = GetCandidateProperties(type);

        // Prefer a property whose resolved EDM name matches (covers [JsonPropertyName] renames);
        // fall back to a caller that already holds a CLR name (e.g. an un-renamed property).
        return props.FirstOrDefault(p => string.Equals(ResolveEdmName(p), edmName, StringComparison.OrdinalIgnoreCase))
            ?? props.FirstOrDefault(p => string.Equals(p.Name, edmName, StringComparison.OrdinalIgnoreCase));
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
        GetCandidateProperties(type).Any(p => string.Equals(ResolveEdmName(p), edmName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// #537: the strict EDM-name match <see cref="IsKnownEdmName"/> performs, but in ONE pass over
    /// <paramref name="type"/>'s properties, returning the matched property instead of a bool. For a
    /// caller that needs both "is this a known EDM name" and "which property is it" — the exact shape
    /// <c>ApplyNavOrderBy</c> had, calling <see cref="IsKnownEdmName"/> then
    /// <see cref="FindClrPropertyByEdmName"/> over the same string — this replaces two scans (and,
    /// before #537, two independent cache lookups) with one. Returns <c>false</c>, with
    /// <paramref name="property"/> <c>null</c>, exactly where <see cref="IsKnownEdmName"/> would.
    /// </summary>
    internal static bool TryResolveEdmName(Type type, string edmName, out PropertyInfo? property)
    {
        property = GetCandidateProperties(type)
            .FirstOrDefault(p => string.Equals(ResolveEdmName(p), edmName, StringComparison.OrdinalIgnoreCase));
        return property is not null;
    }
}
