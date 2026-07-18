using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace OhData.AspNetCore;

/// <summary>
/// Microsoft.AspNetCore.OpenApi schema transformer that omits properties excluded via
/// <c>EntitySetProfile.Ignore(...)</c> (#226) from generated schemas, so documents match the real
/// wire shape (#228) — an ignored property never appears in a response and is never bound from a
/// request body.
/// </summary>
/// <remarks>
/// Suppression is keyed by CLR model type (all profiles sharing a model type declare identical
/// ignore sets — validated at startup). Register alongside
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
    // been resolved (app.MapOhData() forces that at startup), so the map cannot be stale.
    private IReadOnlyDictionary<Type, IReadOnlySet<string>>? _ignoredByType;

    /// <inheritdoc/>
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Type, IReadOnlySet<string>> map = _ignoredByType ??=
            IgnoredPropertyDocsMap.Build(context.ApplicationServices.GetService<OhDataRegistrationCollection>());

        if (map.Count == 0 || !map.TryGetValue(context.JsonTypeInfo.Type, out IReadOnlySet<string>? ignored))
        {
            return Task.CompletedTask;
        }

        // The ignored names are CLR property names ("CostBasis") while the schema keys follow the
        // serializer's naming policy ("costBasis"). JsonTypeInfo carries both sides of that
        // mapping: JsonPropertyInfo.Name is the resolved JSON name and AttributeProvider is the
        // originating CLR member, so matching here is immune to the configured naming policy.
        foreach (JsonPropertyInfo property in context.JsonTypeInfo.Properties
            .Where(p => p.AttributeProvider is PropertyInfo clrProperty && ignored.Contains(clrProperty.Name)))
        {
            schema.Properties?.Remove(property.Name);
            schema.Required?.Remove(property.Name);
        }

        return Task.CompletedTask;
    }
}
