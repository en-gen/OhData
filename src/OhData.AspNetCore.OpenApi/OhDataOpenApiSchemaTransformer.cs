using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace OhData.AspNetCore;

/// <summary>
/// Microsoft.AspNetCore.OpenApi schema transformer that keeps generated schemas faithful to the
/// real wire shape: it omits properties excluded via <c>EntitySetProfile.Ignore(...)</c> (#226,
/// #228), and renames the remaining property keys to the casing OhData's response serializer emits
/// (#258) — PascalCase by default, or whatever
/// <see cref="OhDataBuilder.WithJsonPropertyNamingPolicy"/> selected — instead of the host
/// <c>HttpJsonOptions</c> casing the generator would otherwise use (camelCase by ASP.NET Core
/// default).
/// </summary>
/// <remarks>
/// Both behaviors are keyed by CLR model type. Register alongside
/// <see cref="OhDataOpenApiOperationTransformer"/>:
/// <code>
/// builder.Services.AddOpenApi(o =&gt;
/// {
///     o.AddOperationTransformer&lt;OhDataOpenApiOperationTransformer&gt;();
///     o.AddSchemaTransformer&lt;OhDataOpenApiSchemaTransformer&gt;();
/// });
/// </code>
/// </remarks>
public sealed class OhDataOpenApiSchemaTransformer : IOpenApiSchemaTransformer
{
    // Built once per transformer instance on first use. Cheap (one pass over the registered
    // profiles), and by the time any document request is served every mapped registration has
    // been resolved (app.MapOhData() forces that at startup), so the maps cannot be stale.
    private IReadOnlyDictionary<Type, IReadOnlySet<string>>? _ignoredByType;
    private IReadOnlyDictionary<Type, JsonNamingPolicy?>? _casingByType;

    /// <inheritdoc/>
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        OhDataRegistrationCollection? registrations =
            context.ApplicationServices.GetService<OhDataRegistrationCollection>();
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredMap = _ignoredByType ??=
            IgnoredPropertyDocsMap.Build(registrations);
        IReadOnlyDictionary<Type, JsonNamingPolicy?> casingMap = _casingByType ??=
            SchemaPropertyCasing.Build(registrations);

        Type modelType = context.JsonTypeInfo.Type;
        ignoredMap.TryGetValue(modelType, out IReadOnlySet<string>? ignored);
        bool isOhDataType = casingMap.TryGetValue(modelType, out JsonNamingPolicy? policy);
        if (ignored is null && !isOhDataType)
        {
            return Task.CompletedTask;
        }

        // JsonTypeInfo carries both sides of the CLR↔JSON mapping: JsonPropertyInfo.Name is the
        // host-resolved schema key and AttributeProvider is the originating CLR member. So ignored
        // properties (named by their CLR name) are matched immune to the host naming policy, and
        // the surviving keys are renamed from the host casing to OhData's response casing.
        foreach (JsonPropertyInfo property in context.JsonTypeInfo.Properties)
        {
            if (property.AttributeProvider is not PropertyInfo clrProperty) continue;

            if (ignored is not null && ignored.Contains(clrProperty.Name))
            {
                schema.Properties?.Remove(property.Name);
                schema.Required?.Remove(property.Name);
                continue;
            }

            if (!isOhDataType) continue;

            string responseName = SchemaPropertyCasing.ResolveResponseName(clrProperty, policy);
            if (responseName == property.Name) continue;

            if (schema.Properties is { } properties &&
                properties.TryGetValue(property.Name, out IOpenApiSchema? value))
            {
                properties.Remove(property.Name);
                properties[responseName] = value;
            }
            if (schema.Required is { } required && required.Remove(property.Name))
            {
                required.Add(responseName);
            }
        }

        return Task.CompletedTask;
    }
}
