using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using NSwag;
using NSwag.Generation.AspNetCore;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace OhData.AspNetCore;

/// <summary>
/// Opt-in NSwag operation processor that reflects OhData's per-operation authorization (#199) into
/// each generated operation: it emits an operation-level <c>security</c> requirement referencing a
/// security scheme the app already defined, and documents the <c>401 Unauthorized</c> /
/// <c>403 Forbidden</c> responses the route can now return (#219).
/// </summary>
/// <remarks>
/// <para>
/// This is off by default: nothing happens until the app adds it to the document's operation
/// processors, and it only references a scheme the app names here. OhData never defines the security
/// <em>scheme</em> (Bearer/JWT, OAuth flows) — that stays the app's identity setup. This processor
/// reflects only the <em>requirement</em> against a scheme the app already declared. Register via:
/// <code>
/// builder.Services.AddOpenApiDocument(s =&gt;
///     s.OperationProcessors.Add(new OhDataNSwagSecurityOperationProcessor("Bearer")));
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
public sealed class OhDataNSwagSecurityOperationProcessor : IOperationProcessor
{
    private readonly string _securitySchemeId;
    private readonly List<string> _requiredScopes;

    /// <summary>
    /// Creates the processor bound to the app-defined security scheme it should reference.
    /// </summary>
    /// <param name="securitySchemeId">
    /// The id of a security scheme the app registered on the document (e.g. <c>"Bearer"</c>). The
    /// emitted requirement references this scheme by name; OhData does not define it.
    /// </param>
    /// <param name="requiredScopes">
    /// Optional scope names to list in the security requirement (meaningful only for OAuth2/OpenID
    /// Connect schemes). Empty for Bearer/JWT or API-key schemes.
    /// </param>
    public OhDataNSwagSecurityOperationProcessor(string securitySchemeId, IEnumerable<string>? requiredScopes = null)
    {
        _securitySchemeId = securitySchemeId;
        _requiredScopes = requiredScopes?.ToList() ?? new List<string>();
    }

    /// <inheritdoc/>
    public bool Process(OperationProcessorContext context)
    {
        if (context is not AspNetCoreOperationProcessorContext aspNetCoreContext)
        {
            return true;
        }

        var operation = context.OperationDescription.Operation;
        var endpointMetadata = aspNetCoreContext.ApiDescription.ActionDescriptor.EndpointMetadata;

        // Standard ASP.NET Core semantics: an explicit IAllowAnonymous anywhere on the endpoint
        // wins over any IAuthorizeData, so those routes stay unsecured in the docs (#219).
        bool secured = endpointMetadata.OfType<IAuthorizeData>().Any()
            && !endpointMetadata.OfType<IAllowAnonymous>().Any();
        if (!secured)
        {
            return true;
        }

        // Operation-level security requirement referencing the app's scheme by name. OhData only
        // names the scheme; the app owns its securityDefinitions/securitySchemes (#219 boundary).
        operation.Security ??= new List<OpenApiSecurityRequirement>();
        if (!operation.Security.Any(req => req.ContainsKey(_securitySchemeId)))
        {
            operation.Security.Add(new OpenApiSecurityRequirement { [_securitySchemeId] = _requiredScopes });
        }

        // Document the auth-failure responses this route can now return. 403 also covers Layer B
        // (resource/instance-level) denials, otherwise not expressible in OpenAPI (#219).
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

        return true;
    }
}
