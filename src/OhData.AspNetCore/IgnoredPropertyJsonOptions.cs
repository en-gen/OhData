using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OhData;

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
    /// Re-expresses <paramref name="ignoredByType"/> — which is keyed by CLR property name — as the
    /// <b>JSON</b> names those members would have been written and bound under, had they not been
    /// ignored. Empty in, empty out (reference-equal to the shared empty map), so a registration
    /// that ignores nothing allocates nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed because <see cref="Build"/> <i>removes</i> the ignored members from their
    /// <see cref="JsonTypeInfo"/>, and everything downstream that has to reason about them —
    /// <c>OpenTypeJsonOptions</c>'s declared-name collision set on the read path, and its
    /// dynamic-key walk on the write path — sees only the post-removal contract, where the JSON
    /// name is no longer recoverable. It has to be captured here, before the removal.
    /// </para>
    /// <para>
    /// <b>Read off the real pre-ignore contract, never re-derived.</b> The obvious alternative —
    /// apply <see cref="JsonSerializerOptions.PropertyNamingPolicy"/> to the CLR name and special-case
    /// <c>[JsonPropertyName]</c> — is a hand-written re-implementation of System.Text.Json's own
    /// naming rules, and any drift between the two would silently mis-name a <i>withheld</i> member,
    /// which is the one place a naming bug turns into a disclosure. Resolving
    /// <paramref name="preIgnoreOptions"/>' own <see cref="JsonTypeInfo"/> and matching members by
    /// <see cref="JsonPropertyInfo.AttributeProvider"/> asks the authority instead.
    /// </para>
    /// <para>
    /// Probes a throwaway copy: resolving a <see cref="JsonTypeInfo"/> marks the options instance
    /// read-only, and <paramref name="preIgnoreOptions"/> is about to be derived from by
    /// <see cref="Build"/> and by the open-type modifier. The resolver is taken with the same
    /// <see cref="DefaultJsonTypeInfoResolver"/> fallback the rest of the pipeline uses, because an
    /// options instance that has never been handed to <c>JsonSerializer</c> and carries no explicit
    /// resolver throws <see cref="NotSupportedException"/> from <c>GetTypeInfo</c> —
    /// <c>OhDataEndpointFactory</c>'s own <c>_pascalCaseSerializerOptions</c> fallback is exactly
    /// such an instance.
    /// </para>
    /// <para>
    /// A CLR name with no matching member in the contract is simply absent from the result. That is
    /// not silent data loss: such a member cannot be bound or emitted by
    /// <see cref="JsonSerializer"/> under any name, so there is no JSON name to withhold.
    /// </para>
    /// </remarks>
    internal static IReadOnlyDictionary<Type, IReadOnlySet<string>> BuildIgnoredJsonNameMap(
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredByType,
        JsonSerializerOptions preIgnoreOptions)
    {
        if (ignoredByType.Count == 0) return EmptyNameMap;

        var probe = new JsonSerializerOptions(preIgnoreOptions);
        IJsonTypeInfoResolver resolver = probe.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();

        var result = new Dictionary<Type, IReadOnlySet<string>>();
        foreach (KeyValuePair<Type, IReadOnlySet<string>> entry in ignoredByType)
        {
            JsonTypeInfo? typeInfo = resolver.GetTypeInfo(entry.Key, probe);
            if (typeInfo is null) continue;

            var jsonNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                if (property.AttributeProvider is PropertyInfo clrProperty
                    && entry.Value.Contains(clrProperty.Name))
                {
                    jsonNames.Add(property.Name);
                }
            }
            if (jsonNames.Count > 0) result[entry.Key] = jsonNames;
        }
        return result.Count == 0 ? EmptyNameMap : result;
    }

    /// <summary>
    /// The shared "nothing is ignored" map. Handed to every consumer that takes an ignored-name map
    /// so none of them needs a nullable parameter or a null check on a hot path.
    /// </summary>
    // Frozen rather than an empty Dictionary behind the interface: this instance is shared by every
    // registration that ignores nothing, and IReadOnlyDictionary is not a guarantee — a caller can
    // cast it back and mutate it. FrozenDictionary is available on net8.0, which this assembly also
    // targets.
    internal static readonly IReadOnlyDictionary<Type, IReadOnlySet<string>> EmptyNameMap =
        FrozenDictionary<Type, IReadOnlySet<string>>.Empty;

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
