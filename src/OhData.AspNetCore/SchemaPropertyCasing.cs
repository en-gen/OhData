using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OhData.Abstractions;

namespace OhData.AspNetCore;

/// <summary>
/// Builds the CLR-type → OhData response naming-policy map the OpenAPI companion packages
/// (Microsoft.AspNetCore.OpenApi, NSwag, Swashbuckle) consult to rename generated schema property
/// keys so schema casing matches the <b>response</b> casing OhData emits (#258).
/// </summary>
/// <remarks>
/// #252 made OhData own its response JSON property casing independently of the host's
/// <c>HttpJsonOptions</c> — PascalCase by default. The schema generators, however, still derive
/// property-name casing from the host serializer (camelCase by ASP.NET Core default), so a default
/// app advertised camelCase schema names while emitting PascalCase payloads. This map threads
/// OhData's owned <see cref="OhDataRegistration.JsonPropertyNamingPolicy"/> into the schema
/// generators so the two agree. Exposed to the companion assemblies via <c>InternalsVisibleTo</c>
/// so the core package keeps carrying no doc-stack dependency.
/// <para>
/// The map covers the transitive closure of every entity-set model type's serializable property
/// types (e.g. a parent's nested child type), because OhData reserializes the whole response graph
/// under its owned policy. When a CLR type is exposed by only one registration, or by several that
/// all agree on the policy, its casing is fully deterministic. The one ambiguous case — a shared
/// model type carrying <i>different</i> policies across registrations (v2 opts into camelCase while
/// v1 keeps the PascalCase default) — cannot be represented by the single component schema an
/// OpenAPI document holds per CLR type; one registration's policy wins (unspecified which), the same
/// one-schema-many-registrations ambiguity <see cref="IgnoredPropertyDocsMap"/> already accepts.
/// Configure a shared model type with one casing policy across a host.
/// </para>
/// </remarks>
internal static class SchemaPropertyCasing
{
    private static readonly IReadOnlyDictionary<Type, JsonNamingPolicy?> s_empty =
        new Dictionary<Type, JsonNamingPolicy?>();

    /// <summary>
    /// Maps each OhData model CLR type (and every serializable type reachable from it) to the JSON
    /// property-naming policy its registration applies to responses (<c>null</c> = PascalCase, the
    /// default). Returns an empty map when <paramref name="registrations"/> is <c>null</c> (OhData
    /// not registered in the host).
    /// </summary>
    internal static IReadOnlyDictionary<Type, JsonNamingPolicy?> Build(
        OhDataRegistrationCollection? registrations)
    {
        if (registrations is null) return s_empty;

        Dictionary<Type, JsonNamingPolicy?>? result = null;
        foreach (OhDataRegistration registration in registrations.All)
        {
            foreach (IEntitySetEndpointSource profile in registration.Profiles)
            {
                result ??= new Dictionary<Type, JsonNamingPolicy?>();
                Collect(profile.ModelType, registration.JsonPropertyNamingPolicy, result);
            }
        }

        return result ?? s_empty;
    }

    /// <summary>
    /// Resolves the JSON property name OhData's response serializer emits for
    /// <paramref name="property"/> under <paramref name="policy"/>: an explicit
    /// <c>[JsonPropertyName]</c> wins (same precedence as responses), otherwise the policy converts
    /// the CLR name (a <c>null</c> policy = PascalCase = the CLR name verbatim).
    /// </summary>
    internal static string ResolveResponseName(PropertyInfo property, JsonNamingPolicy? policy)
    {
        JsonPropertyNameAttribute? attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (attribute is not null) return attribute.Name;
        return policy is null ? property.Name : policy.ConvertName(property.Name);
    }

    // Walks the serializable property graph rooted at a model type, recording each complex type
    // under the given policy. First-wins: a type already recorded (by an earlier registration, or
    // an earlier branch of this walk) is not revisited — which also breaks reference cycles.
    private static void Collect(Type type, JsonNamingPolicy? policy, Dictionary<Type, JsonNamingPolicy?> result)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;

        Type? element = GetEnumerableElementType(t);
        if (element is not null)
        {
            Collect(element, policy, result);
            return;
        }

        if (IsLeaf(t) || result.ContainsKey(t)) return;

        // Record before recursing so a cycle (Parent -> Child -> Parent) terminates.
        result[t] = policy;
        foreach (PropertyInfo property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0))
        {
            Collect(property.PropertyType, policy, result);
        }

        // Also record the base class: NSwag emits an inherited base type as its own `allOf`
        // component schema, so the base type needs its own casing entry or that component keeps
        // host casing (#260). The IsLeaf guard stops the climb at object/framework bases, and the
        // ContainsKey guard above breaks any cycle. GetProperties already returned inherited members
        // (so their property types are covered), so this only adds the base type itself.
        if (t.BaseType is Type baseType)
        {
            Collect(baseType, policy, result);
        }
    }

    // A "leaf" is any type whose schema has no OhData-owned property casing to rename: primitives,
    // strings, common BCL value types, enums, and anything in a framework namespace (System.*,
    // Microsoft.*) — OhData response models never live there, so this guard keeps the walk from
    // wandering into framework types. (A model type a consumer deliberately places under a
    // System.*/Microsoft.* namespace is the one blind spot; such a type would be treated as a leaf
    // and left at the generator's host casing.)
    internal static bool IsLeaf(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(object)) return true;
        if (type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
            type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri) ||
            type == typeof(DateOnly) || type == typeof(TimeOnly))
        {
            return true;
        }

        string? ns = type.Namespace;
        return ns is not null && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ||
            ns.StartsWith("Microsoft.", StringComparison.Ordinal));
    }

    // Returns the element type of an array or IEnumerable<T> (never for string, handled as a leaf).
    // A dictionary/keyed collection surfaces as IEnumerable<KeyValuePair<TKey,TValue>>; the value
    // type is what carries model properties, so it is unwrapped (the key is a scalar the serializer
    // stringifies).
    private static Type? GetEnumerableElementType(Type type)
    {
        if (type == typeof(string)) return null;

        Type? element = null;
        if (type.IsArray)
        {
            element = type.GetElementType();
        }
        else if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            Type? enumerableInterface = type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerableInterface is not null)
            {
                element = enumerableInterface.GetGenericArguments()[0];
            }
        }

        if (element is not null && element.IsGenericType &&
            element.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            return element.GetGenericArguments()[1];
        }
        return element;
    }
}
