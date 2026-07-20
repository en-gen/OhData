using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace OhData.AspNetCore;

/// <summary>
/// Opt-in Microsoft.AspNetCore.OpenApi operation transformer that reflects OhData's per-operation
/// authorization (#199) into each generated operation: it emits an operation-level
/// <c>security</c> requirement referencing a security scheme the app already defined, and documents
/// the <c>401 Unauthorized</c> / <c>403 Forbidden</c> responses the route can now return (#219).
/// </summary>
/// <remarks>
/// <para>
/// This is off by default: nothing happens until the app registers it, and it only references a
/// scheme the app names here. OhData never defines the security <em>scheme</em> (Bearer/JWT, OAuth
/// flows, <c>securitySchemes</c>) — that stays the app's identity setup. This transformer reflects
/// only the <em>requirement</em> against a scheme the app already declared. Register via:
/// <code>
/// builder.Services.AddOpenApi(o =&gt;
///     o.AddOperationTransformer(new OhDataOpenApiSecurityOperationTransformer("Bearer")));
/// </code>
/// </para>
/// <para>
/// An operation is treated as secured when its endpoint carries an authorization requirement —
/// standard ASP.NET Core <see cref="IAuthorizeData"/> metadata, which #199's per-route
/// <c>RequireAuthorization(...)</c> attaches — and is not overridden by <see cref="IAllowAnonymous"/>.
/// Layer B (resource/instance-level) rules are not expressible here beyond the documented
/// <c>403</c> (#219).
/// </para>
/// </remarks>
public sealed class OhDataOpenApiSecurityOperationTransformer : IOpenApiOperationTransformer
{
    private readonly string _securitySchemeId;
    private readonly List<string> _requiredScopes;

    /// <summary>
    /// Creates the transformer bound to the app-defined security scheme it should reference.
    /// </summary>
    /// <param name="securitySchemeId">
    /// The id of a security scheme the app registered under <c>components.securitySchemes</c>
    /// (e.g. <c>"Bearer"</c>). The emitted requirement is a <c>$ref</c> to this scheme; OhData does
    /// not define it.
    /// </param>
    /// <param name="requiredScopes">
    /// Optional scope names to list in the security requirement (meaningful only for OAuth2/OpenID
    /// Connect schemes). Empty for Bearer/JWT or API-key schemes.
    /// </param>
    public OhDataOpenApiSecurityOperationTransformer(string securitySchemeId, IEnumerable<string>? requiredScopes = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(securitySchemeId);
        _securitySchemeId = securitySchemeId;
        _requiredScopes = requiredScopes?.ToList() ?? new List<string>();
    }

    /// <inheritdoc/>
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata;

        // Standard ASP.NET Core semantics: an explicit IAllowAnonymous anywhere on the endpoint
        // wins over any IAuthorizeData, so those routes stay unsecured in the docs (#219).
        bool secured = endpointMetadata.OfType<IAuthorizeData>().Any()
            && !endpointMetadata.OfType<IAllowAnonymous>().Any();
        if (!secured)
        {
            return Task.CompletedTask;
        }

        // Operation-level security requirement referencing the app's scheme by id ($ref). OhData
        // only names the scheme; the app owns its securitySchemes definition (#219 boundary).
        operation.Security ??= new List<OpenApiSecurityRequirement>();
        if (!operation.Security.Any(req => req.Keys.Any(k => k.Reference?.Id == _securitySchemeId)))
        {
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(_securitySchemeId, context.Document)] = _requiredScopes,
            });
        }

        // Document the auth-failure responses this route can now return. 403 also covers Layer B
        // (resource/instance-level) denials, otherwise not expressible in OpenAPI (#219).
        operation.Responses ??= new OpenApiResponses();
        if (!operation.Responses.ContainsKey("401"))
        {
            operation.Responses["401"] = new OpenApiResponse
            {
                Description = "Unauthorized — authentication is required and was missing or invalid.",
            };
        }
        if (!operation.Responses.ContainsKey("403"))
        {
            operation.Responses["403"] = new OpenApiResponse
            {
                Description = "Forbidden — the authenticated principal does not satisfy the operation's authorization requirements.",
            };
        }

        return Task.CompletedTask;
    }
}
