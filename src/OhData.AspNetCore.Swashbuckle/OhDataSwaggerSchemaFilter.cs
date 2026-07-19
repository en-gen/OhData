using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OhData.AspNetCore;

/// <summary>
/// Swashbuckle schema filter that omits properties excluded via
/// <c>EntitySetProfile.Ignore(...)</c> (#226) from generated schemas, so documents match the real
/// wire shape (#228) — an ignored property never appears in a response and is never bound from a
/// request body.
/// </summary>
/// <remarks>
/// Suppression is keyed by CLR model type (all profiles sharing a model type declare identical
/// ignore sets — validated at startup). Register alongside
/// <see cref="OhDataSwaggerOperationFilter"/>:
/// <code>
/// builder.Services.AddSwaggerGen(c =&gt;
/// {
///     c.OperationFilter&lt;OhDataSwaggerOperationFilter&gt;();
///     c.SchemaFilter&lt;OhDataSwaggerSchemaFilter&gt;();
/// });
/// </code>
/// </remarks>
public sealed class OhDataSwaggerSchemaFilter : ISchemaFilter
{
    private readonly IServiceProvider _services;
    private readonly ISerializerDataContractResolver _dataContractResolver;

    // Built once per filter instance on first use. Cheap (one pass over the registered
    // profiles), and by the time any document request is served every mapped registration has
    // been resolved (app.MapOhData() forces that at startup), so the map cannot be stale.
    private IReadOnlyDictionary<Type, IReadOnlySet<string>>? _ignoredByType;

    /// <summary>
    /// Creates the filter. Both parameters are DI-injected when the filter is registered via
    /// <c>c.SchemaFilter&lt;OhDataSwaggerSchemaFilter&gt;()</c>: <paramref name="services"/>
    /// resolves the OhData registrations lazily at document-generation time, and
    /// <paramref name="dataContractResolver"/> is Swashbuckle's own serializer contract resolver,
    /// used to map CLR property names to the JSON names the schema keys use.
    /// </summary>
    public OhDataSwaggerSchemaFilter(IServiceProvider services, ISerializerDataContractResolver dataContractResolver)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataContractResolver);
        _services = services;
        _dataContractResolver = dataContractResolver;
    }

    /// <inheritdoc/>
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        IReadOnlyDictionary<Type, IReadOnlySet<string>> map = _ignoredByType ??=
            IgnoredPropertyDocsMap.Build(_services.GetService<OhDataRegistrationCollection>());

        // Only the concrete schema type is mutable; references ($ref placeholders) resolve to a
        // concrete schema that gets its own Apply call.
        if (schema is not OpenApiSchema concreteSchema) return;

        if (map.Count == 0 || !map.TryGetValue(context.Type, out IReadOnlySet<string>? ignored))
        {
            return;
        }

        // The ignored names are CLR property names ("CostBasis") while the schema keys follow the
        // serializer's naming policy ("costBasis"). Swashbuckle's data contract carries both sides
        // of that mapping (DataProperty.MemberInfo → DataProperty.Name), so matching here is
        // immune to the configured naming policy.
        DataContract contract = _dataContractResolver.GetDataContractForType(context.Type);
        if (contract.ObjectProperties is null) return;

        foreach (DataProperty property in contract.ObjectProperties
            .Where(p => p.MemberInfo is not null && ignored.Contains(p.MemberInfo.Name)))
        {
            concreteSchema.Properties?.Remove(property.Name);
            concreteSchema.Required?.Remove(property.Name);
        }
    }
}
