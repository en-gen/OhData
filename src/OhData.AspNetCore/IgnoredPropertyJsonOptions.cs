using System;
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
    /// <para>
    /// <b>Each set carries <see cref="WithheldNameComparer"/>, never <see cref="StringComparer.Ordinal"/>.</b>
    /// See that method for why; the short version is that an ordinal set is a bypass, because the
    /// binder matches body keys case-insensitively in production.
    /// </para>
    /// </remarks>
    internal static InheritedNameSets BuildIgnoredJsonNameMap(
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredByType,
        JsonSerializerOptions preIgnoreOptions)
    {
        if (ignoredByType.Count == 0) return InheritedNameSets.Empty;

        var probe = new JsonSerializerOptions(preIgnoreOptions);
        IJsonTypeInfoResolver resolver = probe.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        StringComparer comparer = WithheldNameComparer(preIgnoreOptions);

        var result = new Dictionary<Type, IReadOnlySet<string>>();
        foreach (KeyValuePair<Type, IReadOnlySet<string>> entry in ignoredByType)
        {
            JsonTypeInfo? typeInfo;
            try
            {
                typeInfo = resolver.GetTypeInfo(entry.Key, probe);
            }
            // #398 review LOW-1. This is an EAGER contract resolution at MapOhData() that did not
            // exist before stage 1, and it is exposed to the shapes System.Text.Json rejects outright.
            // OpenTypeJsonOptions.ValidateOrThrow wraps the same four types for its own probe, and a
            // bare STJ exception escaping from HERE would report the identical fault with no
            // indication that Ignore() is what forced the resolution.
            //
            // MEASURED against DefaultJsonTypeInfoResolver on .NET 10 — note this is the RESOLVER's
            // GetTypeInfo, not JsonSerializerOptions.GetTypeInfo, and the two do NOT agree:
            //   - InvalidOperationException  — two members whose JSON names collide
            //       ("The JSON property name for '...' collides with another property"), and a Type
            //       that cannot be serialized at all (pointer/ref struct/open generic). The options-
            //       level entry point reports that second case as ArgumentException instead.
            //   - TargetInvocationException  — a [JsonConverter] whose own constructor threw; STJ
            //       instantiates it reflectively, so whatever it threw arrives wrapped.
            //   - NotSupportedException      — a resolver with no metadata for the type. The
            //       source-generated resolvers return null here rather than throwing (handled just
            //       below), but a hand-written IJsonTypeInfoResolver is free to throw it, and the
            //       options-level entry point does.
            //   - ArgumentException          — kept for the same defensive reason ValidateOrThrow
            //       keeps it: not reachable through DefaultJsonTypeInfoResolver, but it is what the
            //       options-level guard raises for the same condition, so a future caller that
            //       reaches it still gets the explanatory message.
            // Deliberately NOT catch(Exception): an arbitrary throw out of consumer-supplied resolver
            // or modifier code is a fault in that code and still fails startup with its own type and
            // stack intact.
            catch (InvalidOperationException ex) { throw ContractResolutionFailed(entry.Key, entry.Value, ex); }
            catch (NotSupportedException ex) { throw ContractResolutionFailed(entry.Key, entry.Value, ex); }
            catch (TargetInvocationException ex) { throw ContractResolutionFailed(entry.Key, entry.Value, ex); }
            catch (ArgumentException ex) { throw ContractResolutionFailed(entry.Key, entry.Value, ex); }

            if (typeInfo is null) continue;

            var jsonNames = new HashSet<string>(comparer);
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
        // #462: handed out as an InheritedNameSets, never as a raw dictionary. That is what stops a
        // future consumer from writing the exact-type lookup this map had at three sites — see
        // InheritedNameSets' remarks. The union comparer is the BINDER's, matching every set inside.
        return result.Count == 0 ? InheritedNameSets.Empty : new InheritedNameSets(result, comparer);
    }

    private static InvalidOperationException ContractResolutionFailed(
        Type modelType, IReadOnlySet<string> ignoredNames, Exception inner) =>
        new(
            $"OhData: System.Text.Json could not resolve a serialization contract for model type " +
            $"'{modelType.FullName}'. The contract has to be resolved at startup because the profile " +
            $"withholds {ignoredNames.Count} propert{(ignoredNames.Count == 1 ? "y" : "ies")} with " +
            "Ignore(...), and the JSON names those members would have been written under must be " +
            "captured BEFORE the members are removed from the contract — afterwards they cannot be " +
            "recovered. Fix the contract on that type (System.Text.Json's own message is the inner " +
            "exception), or drop the Ignore(...) calls for it.",
            inner);

    /// <summary>
    /// The comparer every <b>withheld-name</b> set must be built with: the one the <i>binder</i>
    /// uses to match a request-body key to a declared member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#398 review HIGH-1 — an ordinal set here is a containment bypass.</b> The withheld sets are
    /// consulted only after a body key has already <i>missed</i> the declared-member lookup, and that
    /// lookup is case-insensitive whenever <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/>
    /// is set — which in production is always: <c>OhDataEndpointFactory</c>'s own fallback options set
    /// it explicitly, and a host that supplies its own gets it from
    /// <see cref="JsonSerializerDefaults.Web"/>. So with <c>Secret</c> withheld, a body key spelled
    /// <c>secret</c> misses <c>declared</c> (the member is no longer in the contract), misses an
    /// ORDINAL withheld set, and is classified as an ordinary dynamic key — bagged on the way in and
    /// echoed on the way out. Measured on the pre-fix tree: <c>Secret</c> was contained;
    /// <c>secret</c>, <c>SECRET</c> and <c>sEcReT</c> all round-tripped.
    /// </para>
    /// <para>
    /// <b>This is deliberately NOT the comparer the declared-name collision check uses.</b> That one
    /// is ordinal on purpose (#395): its concern is a <i>duplicate JSON key</i> in the emitted object,
    /// and a case-differing bag key does not produce one, so faulting on it would reject data that
    /// serializes perfectly well. The withheld-name concern is <i>disclosure</i>, and a case-differing
    /// spelling is precisely the bypass rather than a false positive. The two sets therefore cannot
    /// share one <see cref="HashSet{T}"/> — see <c>OpenTypeJsonOptions.ThrowOnKeysThatCannotBeEmitted</c>,
    /// which keeps them apart for this reason. Do not "simplify" them back together.
    /// </para>
    /// </remarks>
    internal static StringComparer WithheldNameComparer(JsonSerializerOptions options) =>
        options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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
        InheritedNameSets ignoredByType)
    {
        if (ignoredByType.IsEmpty) return baseOptions;

        var derived = new JsonSerializerOptions(baseOptions);
        IJsonTypeInfoResolver resolver = derived.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        derived.TypeInfoResolver = resolver.WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
            // #462 (DISCLOSURE). This was `ignoredByType.TryGetValue(typeInfo.Type, ...)` — an EXACT
            // type match with an early return on a miss. `typeInfo.Type` is the RUNTIME type (the
            // batched collection path dispatches on `object`; the single-entity path calls
            // SerializeToNode(value, value.GetType(), ...)), so a handler returning a DERIVED
            // instance of the model type — an ordinary EF Core TPH shape — missed the map entirely
            // and the inherited withheld member was SERVED, on a plain GET, on both the collection
            // and GetById routes. Resolve() walks the CLR base chain; see InheritedNameSets.
            IReadOnlySet<string>? names = ignoredByType.Resolve(typeInfo.Type);
            if (names is null) return;
            for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
            {
                if (typeInfo.Properties[i].AttributeProvider is PropertyInfo prop && names.Contains(prop.Name))
                    typeInfo.Properties.RemoveAt(i);
            }
        });
        return derived;
    }
}
