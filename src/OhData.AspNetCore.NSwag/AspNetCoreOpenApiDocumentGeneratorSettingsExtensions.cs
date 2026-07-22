using System;
using System.Collections.Generic;
using NSwag.Generation.AspNetCore;
using OhData;

namespace OhData.AspNetCore.NSwag;

/// <summary>
/// One-line registration for OhData's NSwag companion. <see cref="AddOhData"/> is the canonical
/// wiring recipe: it registers both the operation processor (OData query parameters) and the schema
/// processor (schema fidelity for <c>Ignore(...)</c> and response casing) in a single call, and
/// optionally the opt-in per-operation security and authorization-requirements processors.
/// </summary>
public static class AspNetCoreOpenApiDocumentGeneratorSettingsExtensions
{
    /// <summary>
    /// Registers the OhData NSwag processors on the given document generator settings. This is the
    /// recommended way to wire the companion — you do not need to know the individual processor class
    /// names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always registers <see cref="OhDataNSwagOperationProcessor"/> (documents the OData query
    /// parameters on collection endpoints) and <see cref="OhDataNSwagSchemaProcessor"/> (omits
    /// <c>Ignore(...)</c>d properties and matches the response casing). The schema processor needs the
    /// host's <see cref="IServiceProvider"/>, so call this from the service-provider overload of
    /// <c>AddOpenApiDocument</c>:
    /// <code>
    /// builder.Services.AddOpenApiDocument((s, sp) => s.AddOhData(sp));
    /// </code>
    /// The two optional parameters add the opt-in auth reflection (#219/#220):
    /// <code>
    /// builder.Services.AddOpenApiDocument((s, sp) => s.AddOhData(sp,
    ///     authRequirements: AuthRequirementDisclosure.Kinds,
    ///     securitySchemeId: "Bearer"));
    /// </code>
    /// </para>
    /// <para>
    /// To register only one processor à la carte, add
    /// <c>new OhDataNSwagOperationProcessor()</c> to <c>OperationProcessors</c> /
    /// <c>new OhDataNSwagSchemaProcessor(sp)</c> to <c>SchemaSettings.SchemaProcessors</c> directly instead.
    /// </para>
    /// </remarks>
    /// <param name="settings">The NSwag document generator settings to configure.</param>
    /// <param name="serviceProvider">
    /// The host's service provider (the <c>sp</c> parameter of the <c>AddOpenApiDocument((s, sp) =&gt; ...)</c>
    /// overload), used by the schema processor to reach the OhData registrations.
    /// </param>
    /// <param name="authRequirements">
    /// When provided, also registers <see cref="OhDataNSwagAuthRequirementsOperationProcessor"/> at
    /// the given disclosure level, appending a human-readable authorization-requirements section to
    /// each secured operation's description (#220). Off when <see langword="null"/> (the default).
    /// </param>
    /// <param name="securitySchemeId">
    /// When non-<see langword="null"/>, also registers
    /// <see cref="OhDataNSwagSecurityOperationProcessor"/> referencing the app-defined security scheme
    /// with this id, emitting an operation-level <c>security</c> requirement plus documented
    /// <c>401</c>/<c>403</c> responses on secured operations (#219). Off when <see langword="null"/>
    /// (the default). OhData never defines the scheme itself.
    /// </param>
    /// <param name="requiredScopes">
    /// Optional scope names for the security requirement (meaningful only for OAuth2/OpenID Connect
    /// schemes). Ignored unless <paramref name="securitySchemeId"/> is provided.
    /// </param>
    /// <returns>The same <paramref name="settings"/> instance, for chaining.</returns>
    public static AspNetCoreOpenApiDocumentGeneratorSettings AddOhData(
        this AspNetCoreOpenApiDocumentGeneratorSettings settings,
        IServiceProvider serviceProvider,
        AuthRequirementDisclosure? authRequirements = null,
        string? securitySchemeId = null,
        IEnumerable<string>? requiredScopes = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        settings.OperationProcessors.Add(new OhDataNSwagOperationProcessor());
        settings.SchemaSettings.SchemaProcessors.Add(new OhDataNSwagSchemaProcessor(serviceProvider));

        if (authRequirements is { } disclosure)
        {
            settings.OperationProcessors.Add(new OhDataNSwagAuthRequirementsOperationProcessor(disclosure));
        }

        if (securitySchemeId is not null)
        {
            settings.OperationProcessors.Add(new OhDataNSwagSecurityOperationProcessor(securitySchemeId, requiredScopes));
        }

        return settings;
    }
}
