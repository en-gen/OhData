using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OhData.Client.Internal;

/// <summary>
/// Materializes a row as the CLR type its <c>@odata.type</c> names. Applies only to a class with
/// known derived types (checked once per type), so every other type keeps System.Text.Json's own
/// converter.
/// </summary>
/// <remarks>
/// The annotation is found by scanning a copy of the reader, because System.Text.Json's built-in
/// polymorphism needs the discriminator first on net8.0. The resolved type is then deserialized from
/// the original reader with options that do not claim that exact type, which is what stops the
/// converter re-entering itself; that type's own members delegate back to the full options, so the
/// exclusion covers the row's own object and nothing nested inside it.
/// </remarks>
internal sealed class ODataTypeJsonConverterFactory : JsonConverterFactory
{
    // The "no exclusion" key: the options instance the caller reads with.
    private static readonly Type NoExclusion = typeof(void);

    private static readonly byte[] s_odataType = "@odata.type"u8.ToArray();
    private static readonly byte[] s_shortType = "@type"u8.ToArray();

    private readonly State _state;
    private readonly Type _excluded;

    private ODataTypeJsonConverterFactory(State state, Type excluded)
    {
        _state = state;
        _excluded = excluded;
    }

    /// <summary>
    /// Returns a private copy of the caller's options with this converter appended. The caller's
    /// instance is never mutated, so it may already be in use or read-only.
    /// </summary>
    internal static JsonSerializerOptions CreateReadOptions(OhDataClientOptions options)
    {
        var state = new State(new JsonSerializerOptions(options.JsonOptions), options.DerivedTypes.Snapshot());
        return state.OptionsExcluding(NoExclusion);
    }

    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert != _excluded && _state.IsPolymorphicBase(typeToConvert);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter?)Activator.CreateInstance(
            typeof(Converter<>).MakeGenericType(typeToConvert), _state);

    /// <summary>
    /// Reads the type annotation off the top-level members of the object <paramref name="reader"/> is
    /// positioned on. The reader is a copy: the caller's position is unaffected.
    /// </summary>
    private static string? ReadODataType(Utf8JsonReader reader, bool ignoreCase)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isType = IsName(ref reader, s_odataType, ignoreCase) || IsName(ref reader, s_shortType, ignoreCase);
            reader.Read();
            if (isType)
                return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

            // TrySkip, not Skip: on a stream the block is not final, though the row itself is buffered.
            if (!reader.TrySkip()) return null;
        }

        return null;
    }

    private static bool IsName(ref Utf8JsonReader reader, byte[] ascii, bool ignoreCase)
    {
        if (!ignoreCase) return reader.ValueTextEquals(ascii);

        if (reader.ValueIsEscaped || reader.HasValueSequence)
        {
            return string.Equals(reader.GetString(), Encoding.UTF8.GetString(ascii), StringComparison.OrdinalIgnoreCase);
        }

        ReadOnlySpan<byte> name = reader.ValueSpan;
        if (name.Length != ascii.Length) return false;
        for (int i = 0; i < name.Length; i++)
        {
            if (LowerAscii(name[i]) != LowerAscii(ascii[i])) return false;
        }

        return true;
    }

    private static byte LowerAscii(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    private sealed class Converter<T>(State state) : JsonConverter<T> where T : class
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Type target = typeof(T);
            string? name = ReadODataType(reader, options.PropertyNameCaseInsensitive);
            if (name is not null && state.TryResolve(target, name, out Type derived))
                target = derived;

            return (T?)JsonSerializer.Deserialize(ref reader, target, state.OptionsExcluding(target));
        }

        // Serializes the declared type exactly as an options instance without this converter would.
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, typeof(T), state.OptionsExcluding(typeof(T)));
    }

    private sealed class State
    {
        private readonly JsonSerializerOptions _template;
        private readonly KeyValuePair<string, Type>[] _registry;
        private readonly ConcurrentDictionary<Type, Dictionary<string, Type>?> _namesByBase = new();
        private readonly ConcurrentDictionary<Type, JsonSerializerOptions> _optionsByExclusion = new();
        private readonly ConcurrentDictionary<Assembly, Type[]> _assemblyTypes = new();

        internal State(JsonSerializerOptions template, KeyValuePair<string, Type>[] registry)
        {
            _template = template;
            _registry = registry;
        }

        internal JsonSerializerOptions OptionsExcluding(Type excluded) =>
            _optionsByExclusion.GetOrAdd(excluded, Create);

        private JsonSerializerOptions Create(Type excluded)
        {
            var options = new JsonSerializerOptions(_template);
            options.Converters.Add(new ODataTypeJsonConverterFactory(this, excluded));
            if (excluded != NoExclusion)
            {
                // Skipping `excluded` in the factory applies to every occurrence of that type in the
                // subtree. Delegating the type's own members back to the full options scopes the skip to
                // the row's own object.
                IJsonTypeInfoResolver inner = options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
                options.TypeInfoResolver = inner.WithAddedModifier(typeInfo =>
                {
                    if (typeInfo.Type != excluded || typeInfo.Kind != JsonTypeInfoKind.Object) return;
                    foreach (JsonPropertyInfo property in typeInfo.Properties)
                    {
                        if (property.CustomConverter is not null || property.IsExtensionData
                            || property.PropertyType.IsValueType || property.PropertyType == typeof(string))
                        {
                            continue;
                        }

                        property.CustomConverter = (JsonConverter)Activator.CreateInstance(
                            typeof(Delegating<>).MakeGenericType(property.PropertyType), this)!;
                    }
                });
            }

            return options;
        }

        private sealed class Delegating<P>(State state) : JsonConverter<P>
        {
            public override P? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                JsonSerializer.Deserialize<P>(ref reader, state.OptionsExcluding(NoExclusion));

            public override void Write(Utf8JsonWriter writer, P value, JsonSerializerOptions options) =>
                JsonSerializer.Serialize(writer, value, state.OptionsExcluding(NoExclusion));
        }

        internal bool IsPolymorphicBase(Type type) => NamesFor(type) is not null;

        internal bool TryResolve(Type baseType, string name, out Type resolved)
        {
            resolved = baseType;
            Dictionary<string, Type>? names = NamesFor(baseType);
            if (names is null || !names.TryGetValue(name.TrimStart('#'), out Type? found)) return false;
            resolved = found;
            return true;
        }

        private Dictionary<string, Type>? NamesFor(Type type) =>
            _namesByBase.GetOrAdd(type, BuildNames);

        private Dictionary<string, Type>? BuildNames(Type type)
        {
            // A type that carries its own JSON handling is left to it.
            if (!type.IsClass || type.IsSealed || type.IsGenericTypeDefinition || type == typeof(object)
                || typeof(IEnumerable).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type)
                || type.IsDefined(typeof(JsonConverterAttribute), inherit: true)
                || type.IsDefined(typeof(JsonPolymorphicAttribute), inherit: true))
            {
                return null;
            }

            Dictionary<string, Type>? names = null;
            if (!IsFrameworkType(type))
            {
                List<Type> found = [];
                foreach (Type candidate in TypesOf(type.Assembly))
                {
                    if (!candidate.IsAbstract && !candidate.IsGenericTypeDefinition && candidate.IsSubclassOf(type))
                        found.Add(candidate);
                }

                // The server names a type by namespace + simple name, which drops the enclosing type of a
                // nested class; the CLR full name is matched too and wins a collision.
                foreach (Type candidate in found)
                {
                    string edmName = candidate.Namespace is { Length: > 0 } ns ? ns + "." + candidate.Name : candidate.Name;
                    (names ??= new Dictionary<string, Type>(StringComparer.Ordinal)).TryAdd(edmName, candidate);
                }

                foreach (Type candidate in found)
                    names![candidate.FullName!] = candidate;
            }

            foreach (KeyValuePair<string, Type> entry in _registry)
            {
                if (entry.Value != type && entry.Value.IsSubclassOf(type))
                    (names ??= new Dictionary<string, Type>(StringComparer.Ordinal))[entry.Key] = entry.Value;
            }

            return names;
        }

        private static bool IsFrameworkType(Type type) =>
            type.Namespace is string ns
            && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
                || ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal));

        private Type[] TypesOf(Assembly assembly) =>
            _assemblyTypes.GetOrAdd(assembly, static a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    return ex.Types.Where(t => t is not null).ToArray()!;
                }
            });
    }
}
