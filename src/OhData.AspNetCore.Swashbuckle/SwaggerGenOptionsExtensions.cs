using System;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OhData.AspNetCore.Swashbuckle;

/// <summary>
/// One-line registration for OhData's Swashbuckle companion. <see cref="AddOhData"/> is the
/// canonical wiring recipe: it registers both the operation filter (OData query parameters) and the
/// schema filter (schema fidelity for <c>Ignore(...)</c> and response casing) in a single call.
/// </summary>
public static class SwaggerGenOptionsExtensions
{
    /// <summary>
    /// Registers the OhData Swashbuckle filters on the given <see cref="SwaggerGenOptions"/>. This is
    /// the recommended way to wire the companion — you do not need to know the individual filter class
    /// names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registers <see cref="OhDataSwaggerOperationFilter"/> (documents the OData query parameters on
    /// collection endpoints) and <see cref="OhDataSwaggerSchemaFilter"/> (omits <c>Ignore(...)</c>d
    /// properties and matches the response casing):
    /// <code>
    /// builder.Services.AddSwaggerGen(c => c.AddOhData());
    /// </code>
    /// </para>
    /// <para>
    /// To register only one filter à la carte, call
    /// <c>OperationFilter&lt;OhDataSwaggerOperationFilter&gt;()</c> /
    /// <c>SchemaFilter&lt;OhDataSwaggerSchemaFilter&gt;()</c> directly instead.
    /// </para>
    /// </remarks>
    /// <param name="options">The SwaggerGen options to configure.</param>
    /// <returns>The same <paramref name="options"/> instance, for chaining.</returns>
    public static SwaggerGenOptions AddOhData(this SwaggerGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.OperationFilter<OhDataSwaggerOperationFilter>();
        options.SchemaFilter<OhDataSwaggerSchemaFilter>();

        return options;
    }
}
