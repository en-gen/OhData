using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NJsonSchema.Generation;

namespace OhData.AspNetCore;

/// <summary>
/// NSwag (NJsonSchema) schema processor that omits properties excluded via
/// <c>EntitySetProfile.Ignore(...)</c> (#226) from generated schemas, so documents match the real
/// wire shape (#228) — an ignored property never appears in a response and is never bound from a
/// request body.
/// </summary>
/// <remarks>
/// Suppression is keyed by CLR model type (all profiles sharing a model type declare identical
/// ignore sets — validated at startup). Needs the host's <see cref="IServiceProvider"/> to reach
/// the OhData registrations, so register via the service-provider overload of
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
    // been resolved (app.MapOhData() forces that at startup), so the map cannot be stale.
    private IReadOnlyDictionary<Type, IReadOnlySet<string>>? _ignoredByType;

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
        IReadOnlyDictionary<Type, IReadOnlySet<string>> map = _ignoredByType ??=
            IgnoredPropertyDocsMap.Build(_services.GetService<OhDataRegistrationCollection>());

        Type modelType = context.ContextualType.OriginalType;
        if (map.Count == 0 || !map.TryGetValue(modelType, out IReadOnlySet<string>? ignored))
        {
            return;
        }

        foreach (string jsonName in ignored
            .Select(clrName => modelType.GetProperty(clrName))
            .OfType<PropertyInfo>()
            .Select(property => GetJsonPropertyName(property, context.Settings)))
        {
            // Unconditional on both collections: removing a name Properties doesn't contain is a
            // no-op, and a RequiredProperties entry without a matching property is invalid schema
            // regardless, so there is nothing to guard.
            context.Schema.Properties.Remove(jsonName);
            context.Schema.RequiredProperties.Remove(jsonName);
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
