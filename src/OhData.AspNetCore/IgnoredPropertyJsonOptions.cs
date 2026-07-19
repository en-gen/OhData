using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OhData.Abstractions;

namespace OhData.AspNetCore;

/// <summary>
/// Builds the registration-wide <see cref="JsonSerializerOptions"/> that suppress properties
/// excluded via <c>EntitySetProfile.Ignore(...)</c> (#226) from response serialization and
/// request binding.
/// </summary>
/// <remarks>
/// Mechanism chosen by A/B benchmark (see issue #226): a <c>TypeInfoResolver</c> modifier removes
/// each ignored member from its type's <see cref="JsonTypeInfo"/>. The modifier runs once per
/// type — the resulting <see cref="JsonTypeInfo"/> is cached on the options instance — so steady
/// state simply has fewer members to emit/bind (measured 0.82× baseline time, 0.81× allocations
/// on a 100-item page). Do NOT replace this with post-serialization <c>JsonNode</c> key-stripping
/// for stylistic consistency with the <c>$select</c> pipeline: that alternative measured 1.83×
/// time and 4.32× allocations.
/// </remarks>
internal static class IgnoredPropertyJsonOptions
{
    /// <summary>
    /// Collects the ignored-property map for a registration, keyed by CLR model type. Throws
    /// <see cref="InvalidOperationException"/> when two profiles expose the same model type with
    /// different ignore sets — the derived options are keyed by CLR type, so a silent union
    /// would over-hide one set and taking either side alone would leak the other's secrets.
    /// Identical sets (including both-empty) are fine. Only types with at least one ignored
    /// name appear in the result.
    /// </summary>
    internal static IReadOnlyDictionary<Type, IReadOnlySet<string>> BuildIgnoredPropertyMap(
        IEnumerable<IEntitySetEndpointSource> profiles)
    {
        var firstSeen = new Dictionary<Type, (string EntitySetName, HashSet<string> Names)>();
        var result = new Dictionary<Type, IReadOnlySet<string>>();

        foreach (IEntitySetEndpointSource profile in profiles)
        {
            var names = new HashSet<string>(profile.IgnoredPropertyNames, StringComparer.Ordinal);
            if (firstSeen.TryGetValue(profile.ModelType, out (string EntitySetName, HashSet<string> Names) first))
            {
                if (!first.Names.SetEquals(names))
                {
                    throw new InvalidOperationException(
                        $"Entity sets '{first.EntitySetName}' and '{profile.EntitySetName}' both expose " +
                        $"model type '{profile.ModelType.Name}' but declare different Ignore() sets. " +
                        "Ignored properties are suppressed per CLR type across the whole registration, " +
                        "so the sets must match exactly (or the entity sets must use distinct CLR types).");
                }
                continue;
            }

            firstSeen[profile.ModelType] = (profile.EntitySetName, names);
            if (names.Count > 0) result[profile.ModelType] = names;
        }

        return result;
    }

    /// <summary>
    /// Returns <paramref name="baseOptions"/> unchanged (reference-equal) when
    /// <paramref name="ignoredByType"/> is empty — zero delta when the feature is unused.
    /// Otherwise returns one derived options instance whose resolver modifier removes the mapped
    /// members. Matching uses the CLR property name (via
    /// <see cref="JsonPropertyInfo.AttributeProvider"/>), so the map is immune to the
    /// configured naming policy.
    /// </summary>
    internal static JsonSerializerOptions Build(
        JsonSerializerOptions baseOptions,
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredByType)
    {
        if (ignoredByType.Count == 0) return baseOptions;

        var derived = new JsonSerializerOptions(baseOptions);
        IJsonTypeInfoResolver resolver = derived.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        derived.TypeInfoResolver = resolver.WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
            if (!ignoredByType.TryGetValue(typeInfo.Type, out IReadOnlySet<string>? names)) return;
            for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
            {
                if (typeInfo.Properties[i].AttributeProvider is PropertyInfo prop && names.Contains(prop.Name))
                    typeInfo.Properties.RemoveAt(i);
            }
        });
        return derived;
    }
}
