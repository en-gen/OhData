using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder.Annotations;

namespace OhData;

/// <summary>
/// Builds the registration-wide <see cref="JsonSerializerOptions"/> that make an OData
/// <b>open complex type</b>'s dynamic-property container serialize and bind <b>flat</b> — dynamic
/// keys as siblings of the declared properties, never nested under the container property's own
/// name (#389).
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven by the EDM, never by attributes and never by a name convention.</b>
/// <c>ODataConventionModelBuilder</c> already infers a dynamic-property container from an
/// <see cref="IDictionary{TKey,TValue}"/> member, marks the containing type
/// <c>OpenType="true"</c> in the CSDL, omits the container from the declared properties, and
/// records the backing <see cref="PropertyInfo"/> as a
/// <c>DynamicPropertyDictionaryAnnotation</c>. That annotation — read back through
/// <c>EdmAnnotationExtensions.GetDynamicPropertyDictionary</c> — is the single source of truth
/// here, so the consumer's model needs no <c>[JsonExtensionData]</c> (or any other) attribute:
/// the exact same registration that produces the CSDL produces the wire shape.
/// </para>
/// <para>
/// Mechanism: the same <c>TypeInfoResolver</c>-modifier hook
/// <see cref="IgnoredPropertyJsonOptions"/> and <c>OhDataEndpointFactory</c>'s nav-suppression
/// state already use — except this one <i>mutates</i> a member
/// (<see cref="JsonPropertyInfo.IsExtensionData"/>) rather than removing one.
/// <c>WithAddedModifier</c> chains, so this modifier composes with both of those: it is added
/// last (after the ignored-property modifier), and the nav-suppression modifier is layered on
/// top of the result per-request. The three never touch the same member — ignores and
/// nav-suppression only ever <i>remove</i> members, and this one only ever mutates a member of an
/// open <b>complex</b> type, which by construction carries no EDM navigation properties.
/// </para>
/// <para>
/// <b>Scope: complex types only.</b> Entity-root dynamic containers are deliberately not handled
/// (see <c>docs/open-types.md</c>): the PATCH delta loop resolves body members through
/// <c>FindClrPropertyByEdmName</c> and skips what it cannot resolve, so a root-level undeclared
/// key would be silently dropped on write — a half-working feature is worse than an absent one.
/// </para>
/// <para>
/// <b>Clause-bounded serialization (#325/#326) is not widened.</b> The values inside the bag
/// already reached <c>System.Text.Json</c> before this change — they were simply written one
/// level deeper, nested under the container property's name. This modifier changes only where
/// the keys are placed in the emitted JSON; it adds no new object to the graph the serializer
/// walks, and it never touches an entity type or a navigation property.
/// </para>
/// </remarks>
internal static class OpenTypeJsonOptions
{
    /// <summary>
    /// Maps each CLR type that <i>declares</i> a dynamic-property container to that container's
    /// <see cref="PropertyInfo"/>, for every <b>open complex type</b> in
    /// <paramref name="model"/>.
    /// </summary>
    /// <remarks>
    /// Keyed by <see cref="MemberInfo.DeclaringType"/> rather than by the EDM type's CLR type
    /// because a derived open complex type reports the <i>base</i> type's container
    /// <see cref="PropertyInfo"/>, so one entry covers a whole inheritance chain and the modifier
    /// resolves it with a short base-type walk. (There is no public EDM-to-CLR type accessor on
    /// <c>EdmAnnotationExtensions</c> — only a setter — but none is needed: the declaring type
    /// comes straight off the annotation, so no name-based or convention-based mapping is
    /// involved anywhere in this file.)
    /// <para>
    /// A container whose CLR type <c>System.Text.Json</c> cannot use as extension data is
    /// skipped: <see cref="JsonPropertyInfo.IsExtensionData"/> requires the member to be
    /// assignable to <c>IDictionary&lt;string, object&gt;</c> (or
    /// <c>IDictionary&lt;string, JsonElement&gt;</c>) and to be readable and writable. Skipping
    /// leaves that type serializing exactly as it does today (nested) rather than failing the
    /// whole registration over one member.
    /// </para>
    /// </remarks>
    internal static IReadOnlyDictionary<Type, PropertyInfo> BuildOpenComplexTypeContainerMap(IEdmModel model)
    {
        var result = new Dictionary<Type, PropertyInfo>();
        foreach (IEdmComplexType complexType in model.SchemaElements.OfType<IEdmComplexType>())
        {
            if (!complexType.IsOpen) continue;
            PropertyInfo? container = model.GetDynamicPropertyDictionary(complexType);
            if (container?.DeclaringType is null) continue;
            if (!IsUsableAsExtensionData(container)) continue;
            result[container.DeclaringType] = container;
        }
        return result;
    }

    /// <summary>
    /// Returns <paramref name="baseOptions"/> unchanged (reference-equal) when
    /// <paramref name="containers"/> is empty — zero delta for a model with no open complex
    /// types. Otherwise returns one derived options instance whose resolver modifier marks each
    /// mapped container as <see cref="JsonPropertyInfo.IsExtensionData"/>.
    /// </summary>
    internal static JsonSerializerOptions Build(
        JsonSerializerOptions baseOptions,
        IReadOnlyDictionary<Type, PropertyInfo> containers)
    {
        if (containers.Count == 0) return baseOptions;

        var derived = new JsonSerializerOptions(baseOptions);
        IJsonTypeInfoResolver resolver = derived.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        derived.TypeInfoResolver = resolver.WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
            if (!TryFindContainer(containers, typeInfo.Type, out PropertyInfo? container)) return;
            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                // Identity match on the exact CLR member the EDM designated — never a name match.
                // A derived type that SHADOWS the container with `new` therefore does not match,
                // and is left serializing as it does today rather than guessed at.
                //
                // HasSameMetadataDefinitionAs (module + metadata token), NOT `==`/ReferenceEquals:
                // PropertyInfo equality also compares ReflectedType, and the two PropertyInfo
                // instances here come from independent reflection walks that disagree on it. The
                // model builder discovers a complex type's DERIVED types too, and the annotation it
                // stores can carry the derived type as ReflectedType while declaring the member on
                // the base — measured, not assumed: with `ExternalReferenceMetadataV2 :
                // ExternalReferenceMetadata` present in the assembly, the annotation's
                // ReflectedType is V2 while System.Text.Json's AttributeProvider for the base
                // contract reports the base. Same DeclaringType, same token, `==` false.
                if (property.AttributeProvider is not PropertyInfo candidate ||
                    !candidate.HasSameMetadataDefinitionAs(container))
                {
                    continue;
                }
                // Idempotent: a member already carrying [JsonExtensionData] is simply reaffirmed.
                property.IsExtensionData = true;
                break;
            }
        });
        return derived;
    }

    /// <summary>
    /// Forces resolution of every mapped container type's <see cref="JsonTypeInfo"/> so a
    /// contract <c>System.Text.Json</c> rejects (most plausibly: the model already declares a
    /// <i>different</i> member as <c>[JsonExtensionData]</c>, and a type may have only one)
    /// surfaces once from <c>MapOhData()</c> instead of as a 500 on the first request that
    /// touches the type.
    /// </summary>
    /// <remarks>
    /// Probes a throwaway copy of <paramref name="options"/>: resolving a
    /// <see cref="JsonTypeInfo"/> marks the options instance read-only, and the registration's own
    /// options must stay free to be copied and re-derived (nav suppression does exactly that, per
    /// request path). The copy shares the same resolver, so it exercises the same modifier chain.
    /// </remarks>
    internal static void ValidateOrThrow(
        JsonSerializerOptions options,
        IReadOnlyDictionary<Type, PropertyInfo> containers)
    {
        if (containers.Count == 0) return;
        var probe = new JsonSerializerOptions(options);
        foreach ((Type declaringType, PropertyInfo container) in containers)
        {
            try
            {
                probe.GetTypeInfo(declaringType);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"OhData: '{declaringType.FullName}' is an OData open complex type whose dynamic-property " +
                    $"container is '{container.Name}', but System.Text.Json rejected that contract: {ex.Message} " +
                    "A type can carry only one extension-data member, so remove any competing " +
                    "[JsonExtensionData] attribute on this type.", ex);
            }
        }
    }

    // System.Text.Json's own requirements for an extension-data member (JsonPropertyInfo.
    // IsExtensionData): the member must be assignable to IDictionary<string, object> or
    // IDictionary<string, JsonElement>, and must be both readable and writable (it is populated
    // on read and enumerated on write). Checked here rather than left to throw from the modifier
    // so a model the convention builder happens to mark open, but STJ cannot flatten, keeps its
    // current (nested) behavior instead of failing the registration.
    private static bool IsUsableAsExtensionData(PropertyInfo container)
    {
        if (!container.CanRead || !container.CanWrite) return false;
        Type type = container.PropertyType;
        return typeof(IDictionary<string, object>).IsAssignableFrom(type)
            || typeof(IDictionary<string, JsonElement>).IsAssignableFrom(type);
    }

    // Walks the base-type chain so a DERIVED open complex type resolves the container its base
    // declares (the convention builder reports the base's PropertyInfo for the derived EDM type,
    // and System.Text.Json surfaces the same member — same declaring type, same metadata token —
    // on the derived contract). Bounded by the CLR inheritance depth and runs once per type per
    // options instance: JsonTypeInfo is cached on the options after first resolution.
    private static bool TryFindContainer(
        IReadOnlyDictionary<Type, PropertyInfo> containers,
        Type type,
        [NotNullWhen(true)] out PropertyInfo? container)
    {
        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (containers.TryGetValue(t, out container)) return true;
        }
        container = null;
        return false;
    }
}
