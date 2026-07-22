using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NJsonSchema;
using NJsonSchema.Generation;
using OhData;

namespace OhData.AspNetCore.NSwag;

/// <summary>
/// NSwag (NJsonSchema) schema processor that keeps generated schemas faithful to the real wire
/// shape: it omits properties excluded via <c>EntitySetProfile.Ignore(...)</c> (#226, #228), and
/// renames the remaining property keys to the casing OhData's response serializer emits (#258) —
/// PascalCase by default, or whatever <see cref="OhDataBuilder.WithJsonPropertyNamingPolicy"/>
/// selected — instead of the host <c>HttpJsonOptions</c> casing the generator would otherwise use
/// (camelCase by ASP.NET Core default).
/// </summary>
/// <remarks>
/// Both behaviors are keyed by CLR model type. Needs the host's <see cref="IServiceProvider"/> to
/// reach the OhData registrations, so register via the service-provider overload of
/// <c>AddOpenApiDocument</c>:
/// <code>
/// builder.Services.AddOpenApiDocument((s, sp) =&gt;
/// {
///     s.OperationProcessors.Add(new OhDataNSwagOperationProcessor());
///     s.SchemaSettings.SchemaProcessors.Add(new OhDataNSwagSchemaProcessor(sp));
/// });
/// </code>
/// </remarks>
public sealed class OhDataNSwagSchemaProcessor : ISchemaProcessor
{
    private readonly IServiceProvider _services;

    // Built once per processor instance on first use. Cheap (one pass over the registered
    // profiles), and by the time any document request is served every mapped registration has
    // been resolved (app.MapOhData() forces that at startup), so the maps cannot be stale.
    private IReadOnlyDictionary<Type, IReadOnlySet<string>>? _ignoredByType;
    private IReadOnlyDictionary<Type, JsonNamingPolicy?>? _casingByType;

    /// <summary>
    /// Creates the processor. <paramref name="services"/> is the host's service provider, used to
    /// resolve the OhData registrations lazily at document-generation time.
    /// </summary>
    public OhDataNSwagSchemaProcessor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc/>
    public void Process(SchemaProcessorContext context)
    {
        OhDataRegistrationCollection? registrations = _services.GetService<OhDataRegistrationCollection>();
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredMap = _ignoredByType ??=
            IgnoredPropertyDocsMap.Build(registrations);
        IReadOnlyDictionary<Type, JsonNamingPolicy?> casingMap = _casingByType ??=
            SchemaPropertyCasing.Build(registrations);

        Type modelType = context.ContextualType.OriginalType;
        ignoredMap.TryGetValue(modelType, out IReadOnlySet<string>? ignored);
        bool isOhDataType = casingMap.TryGetValue(modelType, out JsonNamingPolicy? policy);
        if (ignored is null && !isOhDataType)
        {
            return;
        }

        // When a type inherits, NJsonSchema models it as `allOf: [{$ref base}, {inline own props}]`,
        // so the type's own properties live on an inline allOf member, not on the top-level schema's
        // Properties (#260). Mutate every schema that carries this type's own keys — the schema itself
        // plus its inline allOf members. Referenced ($ref) allOf members hold the base type's props,
        // which the base's own schema pass renames, so they are skipped here.
        List<JsonSchema> bearers = new() { context.Schema };
        bearers.AddRange(context.Schema.AllOf.Where(member => !member.HasReference));

        foreach (PropertyInfo property in modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;

            string hostName = GetJsonPropertyName(property, context.Settings);

            if (ignored is not null && ignored.Contains(property.Name))
            {
                foreach (JsonSchema bearer in bearers)
                {
                    // Removing a name Properties doesn't contain is a no-op, and a RequiredProperties
                    // entry without a matching property is invalid schema regardless, so nothing to guard.
                    bearer.Properties.Remove(hostName);
                    bearer.RequiredProperties.Remove(hostName);
                }
                continue;
            }

            if (!isOhDataType) continue;

            string responseName = SchemaPropertyCasing.ResolveResponseName(property, policy);
            if (responseName == hostName) continue;

            foreach (JsonSchema bearer in bearers)
            {
                if (bearer.Properties.TryGetValue(hostName, out JsonSchemaProperty? schemaProperty))
                {
                    bearer.Properties.Remove(hostName);
                    bearer.Properties[responseName] = schemaProperty;
                }
                if (bearer.RequiredProperties.Remove(hostName))
                {
                    bearer.RequiredProperties.Add(responseName);
                }
            }
        }
    }

    /// <summary>
    /// Maps a CLR property name ("CostBasis") to the JSON name NSwag generated the schema key
    /// under ("costBasis"), honoring <c>[JsonPropertyName]</c> and the System.Text.Json naming
    /// policy the document generator is configured with. Falls back to the CLR name for
    /// non-System.Text.Json settings (where NSwag preserves the member name unless a serializer
    /// attribute renames it).
    /// </summary>
    private static string GetJsonPropertyName(PropertyInfo property, JsonSchemaGeneratorSettings settings)
    {
        JsonPropertyNameAttribute? attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (attribute is not null) return attribute.Name;

        if (settings is SystemTextJsonSchemaGeneratorSettings systemTextJson &&
            systemTextJson.SerializerOptions.PropertyNamingPolicy is JsonNamingPolicy policy)
        {
            return policy.ConvertName(property.Name);
        }

        return property.Name;
    }
}
