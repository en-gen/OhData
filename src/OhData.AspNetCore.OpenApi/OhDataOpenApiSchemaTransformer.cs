using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using OhData;

namespace OhData.AspNetCore.OpenApi;

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

        if (ignoredMap.Count == 0 && casingMap.Count == 0)
        {
            return Task.CompletedTask;
        }

        // The runtime hands each schema tree to transformers top-down and *before* it extracts
        // component references (schemas are still fully inline here). It descends into child schemas
        // by looking them up under their host JSON key (JsonPropertyInfo.Name). Renaming a property
        // key in place therefore removes the key the runtime is about to look up, so once we rename a
        // complex property the runtime can no longer descend into it — leaving nested-only component
        // types (e.g. an Address reached only through a property) at host casing (#260). We instead
        // drive the descent ourselves, renaming each node's keys only *after* recursing into its
        // children, so a parent's rename never hides a child the walk still needs to reach.
        RenameTree(schema, context.JsonTypeInfo, ignoredMap, casingMap,
            new HashSet<OpenApiSchema>(ReferenceEqualityComparer.Instance));
        return Task.CompletedTask;
    }

    // Recurses the inline schema tree in lock-step with its JsonTypeInfo, mirroring the runtime's own
    // traversal (array element, dictionary value, object properties). Children are visited before the
    // node's own keys are renamed, so lookups by host key still resolve. The visited set (reference
    // identity) makes the walk idempotent and cycle-safe.
    private static void RenameTree(OpenApiSchema schema, JsonTypeInfo typeInfo,
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredMap,
        IReadOnlyDictionary<Type, JsonNamingPolicy?> casingMap,
        HashSet<OpenApiSchema> visited)
    {
        // Unwrap Nullable<T>: a Nullable<complex-struct> reports Kind None (a converter type), which
        // would stop the walk before its inline object schema, so re-key on the underlying type (whose
        // schema is the same node). Matches SchemaPropertyCasing.Collect, which unwraps Nullable too.
        if (Nullable.GetUnderlyingType(typeInfo.Type) is Type underlying)
        {
            typeInfo = typeInfo.Options.GetTypeInfo(underlying);
        }

        if (!visited.Add(schema)) return;

        switch (typeInfo.Kind)
        {
            case JsonTypeInfoKind.Enumerable:
                // No IsLeaf gate on the element: a scalar element (List<int>) resolves to Kind None
                // and no-ops, while a collection element (List<List<T>>) must be followed to reach T.
                // Runaway into framework object graphs is stopped by the IsLeaf guard on the Object
                // case below.
                if (typeInfo.ElementType is Type elementType && schema.Items is OpenApiSchema itemSchema)
                {
                    RenameTree(itemSchema, typeInfo.Options.GetTypeInfo(elementType), ignoredMap, casingMap, visited);
                }
                break;

            case JsonTypeInfoKind.Dictionary:
                // Same as the Enumerable case: follow the value schema unconditionally; a scalar
                // value resolves to Kind None and no-ops, a complex value is renamed.
                if (typeInfo.ElementType is Type valueType && schema.AdditionalProperties is OpenApiSchema valueSchema)
                {
                    RenameTree(valueSchema, typeInfo.Options.GetTypeInfo(valueType), ignoredMap, casingMap, visited);
                }
                break;

            case JsonTypeInfoKind.Object:
                // Stop at framework object graphs (never OhData response types) so the walk can't
                // wander off into System.*/Microsoft.* types; matches SchemaPropertyCasing.Collect.
                if (SchemaPropertyCasing.IsLeaf(typeInfo.Type)) return;

                if (schema.Properties is { } props)
                {
                    foreach (JsonPropertyInfo property in typeInfo.Properties
                        .Where(property => props.TryGetValue(property.Name, out IOpenApiSchema? child)
                            && child is OpenApiSchema))
                    {
                        RenameTree((OpenApiSchema)props[property.Name],
                            typeInfo.Options.GetTypeInfo(property.PropertyType), ignoredMap, casingMap, visited);
                    }
                }

                RenameNode(schema, typeInfo, ignoredMap, casingMap);
                break;
        }
    }

    // Applies this single schema's ignore-omission and casing rename, keyed by its CLR type.
    // JsonTypeInfo carries both sides of the CLR↔JSON mapping: JsonPropertyInfo.Name is the
    // host-resolved schema key and AttributeProvider is the originating CLR member. So ignored
    // properties (named by their CLR name) are matched immune to the host naming policy, and the
    // surviving keys are renamed from the host casing to OhData's response casing.
    private static void RenameNode(OpenApiSchema schema, JsonTypeInfo typeInfo,
        IReadOnlyDictionary<Type, IReadOnlySet<string>> ignoredMap,
        IReadOnlyDictionary<Type, JsonNamingPolicy?> casingMap)
    {
        ignoredMap.TryGetValue(typeInfo.Type, out IReadOnlySet<string>? ignored);
        bool isOhDataType = casingMap.TryGetValue(typeInfo.Type, out JsonNamingPolicy? policy);
        if (ignored is null && !isOhDataType) return;

        foreach (JsonPropertyInfo property in typeInfo.Properties)
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
    }
}
