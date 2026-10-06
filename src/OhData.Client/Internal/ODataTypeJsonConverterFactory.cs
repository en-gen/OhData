using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhData.Client.Internal;

/// <summary>
/// Materializes a row as the CLR type its <c>@odata.type</c> names. Applies only to a class with
/// known derived types (checked once per type), so every other type keeps System.Text.Json's own
/// converter and pays nothing.
/// </summary>
/// <remarks>
/// The object is buffered and the annotation read wherever it sits: System.Text.Json's built-in
/// polymorphism needs the discriminator first on net8.0. The resolved type is deserialized with an
/// options instance whose factory does not claim that exact type, which is what stops the converter
/// re-entering itself; nested types still resolve, except the same type nested directly in itself.
/// </remarks>
internal sealed class ODataTypeJsonConverterFactory : JsonConverterFactory
{
    private const string ODataTypeName = "@odata.type";

    private readonly State _state;
    private readonly Type? _excluded;

    private ODataTypeJsonConverterFactory(State state, Type? excluded)
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
        return state.OptionsExcluding(null);
    }

    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert != _excluded && _state.IsPolymorphicBase(typeToConvert);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter?)Activator.CreateInstance(
            typeof(Converter<>).MakeGenericType(typeToConvert), _state);

    private sealed class Converter<T>(State state) : JsonConverter<T> where T : class
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            JsonElement element = JsonElement.ParseValue(ref reader);
            Type target = typeof(T);
            if (element.ValueKind == JsonValueKind.Object
                && TryReadODataType(element, options.PropertyNameCaseInsensitive, out string? name)
                && state.TryResolve(typeof(T), name!, out Type derived))
            {
                target = derived;
            }

            return (T?)JsonSerializer.Deserialize(element, target, state.OptionsExcluding(target));
        }

        // Serializes the declared type exactly as an options instance without this converter would.
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, typeof(T), state.OptionsExcluding(typeof(T)));

        private static bool TryReadODataType(JsonElement element, bool ignoreCase, out string? name)
        {
            StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(ODataTypeName, comparison))
                {
                    name = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    return name is not null;
                }
            }

            name = null;
            return false;
        }
    }

    private sealed class State
    {
        private readonly JsonSerializerOptions _template;
        private readonly KeyValuePair<string, Type>[] _registry;
        private readonly ConcurrentDictionary<Type, Dictionary<string, Type>?> _namesByBase = new();
        private readonly ConcurrentDictionary<Type, JsonSerializerOptions> _optionsByExclusion = new();
        private readonly ConcurrentDictionary<Assembly, Type[]> _assemblyTypes = new();
        private JsonSerializerOptions? _unrestricted;

        internal State(JsonSerializerOptions template, KeyValuePair<string, Type>[] registry)
        {
            _template = template;
            _registry = registry;
        }

        internal JsonSerializerOptions OptionsExcluding(Type? excluded)
        {
            if (excluded is null)
                return _unrestricted ??= Create(null);
            return _optionsByExclusion.GetOrAdd(excluded, Create);
        }

        private JsonSerializerOptions Create(Type? excluded)
        {
            var options = new JsonSerializerOptions(_template);
            options.Converters.Add(new ODataTypeJsonConverterFactory(this, excluded));
            return options;
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
            if (!type.IsClass || type.IsSealed || type.IsGenericTypeDefinition || type == typeof(object)
                || typeof(IEnumerable).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type)
                || IsFrameworkType(type))
            {
                return null;
            }

            Dictionary<string, Type>? names = null;
            foreach (Type candidate in TypesOf(type.Assembly))
            {
                if (candidate.IsAbstract || candidate.IsGenericTypeDefinition || !candidate.IsSubclassOf(type)
                    || candidate.FullName is not string fullName)
                {
                    continue;
                }

                (names ??= new Dictionary<string, Type>(StringComparer.Ordinal))[fullName] = candidate;
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
