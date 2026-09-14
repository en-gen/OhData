using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    // Keyed by TYPE, never by the caller's string: the key space is the registered model types, so
    // no request value can grow it (#537). Both name maps are resolved once per type, which keeps
    // lookups O(1) and calls ResolveEdmName's attribute walk once per property rather than per call.
    private static readonly ConcurrentDictionary<Type, PropertyLookup> s_lookupCache = new();

    private sealed record PropertyLookup(
        IReadOnlyDictionary<string, PropertyInfo> ByEdmName,
        IReadOnlyDictionary<string, PropertyInfo> ByClrName);

    private static PropertyLookup GetLookup(Type type) => s_lookupCache.GetOrAdd(type, static t =>
    {
        PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .ToArray();

        // First declaration wins in both maps, matching FirstOrDefault over GetProperties() order.
        var byEdm = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        var byClr = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyInfo p in props)
        {
            byEdm.TryAdd(ResolveEdmName(p), p);
            byClr.TryAdd(p.Name, p);
        }
        return new PropertyLookup(byEdm, byClr);
    });

    /// <summary>
    /// Finds the CLR property of <paramref name="type"/> whose OData/EDM name
    /// (<see cref="ResolveEdmName"/>) equals <paramref name="edmName"/>, matched case-insensitively
    /// (OData identifiers resolve case-insensitively). Falls back to a direct case-insensitive CLR
    /// name lookup so a caller passing a plain CLR name still resolves. Returns <c>null</c> when no
    /// property matches.
    /// </summary>
    internal static PropertyInfo? FindClrPropertyByEdmName(Type type, string edmName)
    {
        PropertyLookup lookup = GetLookup(type);

        // Prefer a property whose resolved EDM name matches (covers [JsonPropertyName] renames);
        // fall back to a caller that already holds a CLR name (e.g. an un-renamed property).
        return lookup.ByEdmName.TryGetValue(edmName, out PropertyInfo? byEdm) ? byEdm
            : lookup.ByClrName.TryGetValue(edmName, out PropertyInfo? byClr) ? byClr
            : null;
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
        GetLookup(type).ByEdmName.ContainsKey(edmName);

    /// <summary>
    /// <see cref="IsKnownEdmName"/>'s strict EDM-name match, returning the property instead of a
    /// bool, for a caller that needs both answers. Refuses exactly what
    /// <see cref="IsKnownEdmName"/> refuses.
    /// </summary>
    internal static bool TryResolveEdmName(Type type, string edmName, out PropertyInfo? property) =>
        GetLookup(type).ByEdmName.TryGetValue(edmName, out property);
}
