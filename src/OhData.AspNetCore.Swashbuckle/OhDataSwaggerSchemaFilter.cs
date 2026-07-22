using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using OhData;

namespace OhData.AspNetCore.Swashbuckle;

/// <summary>
/// Swashbuckle schema filter that keeps generated schemas faithful to the real wire shape: it omits
/// properties excluded via <c>EntitySetProfile.Ignore(...)</c> (#226, #228), and renames the
/// remaining property keys to the casing OhData's response serializer emits (#258) — PascalCase by
/// default, or whatever <see cref="OhDataBuilder.WithJsonPropertyNamingPolicy"/> selected — instead
/// of the host <c>HttpJsonOptions</c> casing the generator would otherwise use (camelCase by
/// ASP.NET Core default).
/// </summary>
/// <remarks>
/// Both behaviors are keyed by CLR model type. Register alongside
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
    // been resolved (app.MapOhData() forces that at startup), so the maps cannot be stale.
    private IReadOnlyDictionary<Type, IReadOnlySet<string>>? _ignoredByType;
    private IReadOnlyDictionary<Type, JsonNamingPolicy?>? _casingByType;

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
        OhDataRegistrationCollection? registrations = _services.GetService<OhDataRegistrationCollection>();
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredMap = _ignoredByType ??=
            IgnoredPropertyDocsMap.Build(registrations);
        IReadOnlyDictionary<Type, JsonNamingPolicy?> casingMap = _casingByType ??=
            SchemaPropertyCasing.Build(registrations);

        // Only the concrete schema type is mutable; references ($ref placeholders) resolve to a
        // concrete schema that gets its own Apply call.
        if (schema is not OpenApiSchema concreteSchema) return;

        ignoredMap.TryGetValue(context.Type, out IReadOnlySet<string>? ignored);
        bool isOhDataType = casingMap.TryGetValue(context.Type, out JsonNamingPolicy? policy);
        if (ignored is null && !isOhDataType)
        {
            return;
        }

        // Swashbuckle's data contract carries both sides of the CLR↔JSON mapping
        // (DataProperty.MemberInfo → DataProperty.Name), so ignored properties are matched immune
        // to the host naming policy and the surviving keys are renamed from the host casing to
        // OhData's response casing.
        DataContract contract = _dataContractResolver.GetDataContractForType(context.Type);
        if (contract.ObjectProperties is null) return;

        foreach (DataProperty property in contract.ObjectProperties)
        {
            if (property.MemberInfo is null) continue;

            if (ignored is not null && ignored.Contains(property.MemberInfo.Name))
            {
                concreteSchema.Properties?.Remove(property.Name);
                concreteSchema.Required?.Remove(property.Name);
                continue;
            }

            if (!isOhDataType || property.MemberInfo is not PropertyInfo clrProperty) continue;

            string responseName = SchemaPropertyCasing.ResolveResponseName(clrProperty, policy);
            if (responseName == property.Name) continue;

            if (concreteSchema.Properties is { } properties &&
                properties.TryGetValue(property.Name, out IOpenApiSchema? value))
            {
                properties.Remove(property.Name);
                properties[responseName] = value;
            }
            if (concreteSchema.Required is { } required && required.Remove(property.Name))
            {
                required.Add(responseName);
            }
        }
    }
}
