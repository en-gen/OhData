using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.OpenApi;
using OhData;

namespace OhData.AspNetCore.OpenApi;

/// <summary>
/// One-line registration for OhData's Microsoft.AspNetCore.OpenApi companion. <see cref="AddOhData"/>
/// is the canonical wiring recipe: it registers both the operation transformer (OData query
/// parameters) and the schema transformer (schema fidelity for <c>Ignore(...)</c> and response
/// casing) in a single call, and optionally the opt-in per-operation security and
/// authorization-requirements transformers.
/// </summary>
public static class OpenApiOptionsExtensions
{
    /// <summary>
    /// Registers the OhData OpenAPI transformers on the given <see cref="OpenApiOptions"/>. This is
    /// the recommended way to wire the companion — you do not need to know the individual transformer
    /// class names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always registers <see cref="OhDataOpenApiOperationTransformer"/> (documents the OData query
    /// parameters on collection endpoints) and <see cref="OhDataOpenApiSchemaTransformer"/> (omits
    /// <c>Ignore(...)</c>d properties and matches the response casing). The two optional parameters
    /// add the opt-in auth reflection (#219/#220):
    /// <code>
    /// builder.Services.AddOpenApi(o => o.AddOhData(
    ///     authRequirements: AuthRequirementDisclosure.Kinds,
    ///     securitySchemeId: "Bearer"));
    /// </code>
    /// </para>
    /// <para>
    /// To register only one transformer à la carte, call
    /// <c>AddOperationTransformer&lt;OhDataOpenApiOperationTransformer&gt;()</c> /
    /// <c>AddSchemaTransformer&lt;OhDataOpenApiSchemaTransformer&gt;()</c> directly instead.
    /// </para>
    /// </remarks>
    /// <param name="options">The OpenAPI options to configure.</param>
    /// <param name="authRequirements">
    /// When provided, also registers <see cref="OhDataOpenApiAuthRequirementsOperationTransformer"/>
    /// at the given disclosure level, appending a human-readable authorization-requirements section to
    /// each secured operation's description (#220). Off when <see langword="null"/> (the default).
    /// </param>
    /// <param name="securitySchemeId">
    /// When non-<see langword="null"/>, also registers
    /// <see cref="OhDataOpenApiSecurityOperationTransformer"/> referencing the app-defined security
    /// scheme with this id, emitting an operation-level <c>security</c> requirement plus documented
    /// <c>401</c>/<c>403</c> responses on secured operations (#219). Off when <see langword="null"/>
    /// (the default). OhData never defines the scheme itself.
    /// </param>
    /// <param name="requiredScopes">
    /// Optional scope names for the security requirement (meaningful only for OAuth2/OpenID Connect
    /// schemes). Ignored unless <paramref name="securitySchemeId"/> is provided.
    /// </param>
    /// <returns>The same <paramref name="options"/> instance, for chaining.</returns>
    public static OpenApiOptions AddOhData(
        this OpenApiOptions options,
        AuthRequirementDisclosure? authRequirements = null,
        string? securitySchemeId = null,
        IEnumerable<string>? requiredScopes = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddOperationTransformer<OhDataOpenApiOperationTransformer>();
        options.AddSchemaTransformer<OhDataOpenApiSchemaTransformer>();

        if (authRequirements is { } disclosure)
        {
            options.AddOperationTransformer(new OhDataOpenApiAuthRequirementsOperationTransformer(disclosure));
        }

        if (securitySchemeId is not null)
        {
            options.AddOperationTransformer(new OhDataOpenApiSecurityOperationTransformer(securitySchemeId, requiredScopes));
        }

        return options;
    }
}
