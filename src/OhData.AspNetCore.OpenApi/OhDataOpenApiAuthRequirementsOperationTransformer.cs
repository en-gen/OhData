using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using OhData;

namespace OhData.AspNetCore.OpenApi;

/// <summary>
/// Opt-in Microsoft.AspNetCore.OpenApi operation transformer that appends a human-readable
/// authorization-requirements section to each operation's description, drawn from OhData's
/// structured per-operation auth data (#199) — the roles, claims, and named policies a route
/// requires (#220).
/// </summary>
/// <remarks>
/// <para>
/// Off by default and layered on top of the baseline security reflection
/// (<see cref="OhDataOpenApiSecurityOperationTransformer"/>): it does nothing until registered.
/// It only describes what OhData itself configured — it never defines the security scheme or
/// identity. The recommended way to opt in is the <c>authRequirements</c> parameter of
/// <c>o.AddOhData(...)</c>:
/// <code>
/// builder.Services.AddOpenApi(o =&gt; o.AddOhData(authRequirements: AuthRequirementDisclosure.Kinds));
/// </code>
/// To register this transformer à la carte instead, add the instance directly:
/// <code>
/// builder.Services.AddOpenApi(o =&gt;
///     o.AddOperationTransformer(
///         new OhDataOpenApiAuthRequirementsOperationTransformer(AuthRequirementDisclosure.Kinds)));
/// </code>
/// </para>
/// <para>
/// Exact required claim <em>values</em> are a mild information-disclosure surface, so they are
/// emitted only at <see cref="AuthRequirementDisclosure.Full"/>; the default
/// <see cref="AuthRequirementDisclosure.Kinds"/> surfaces requirement kinds and their non-secret
/// identifiers (claim types, role names, policy names) but not claim values. Named policies stay
/// opaque (name only) and resource/instance-level (Layer B) rules are not rendered (#220).
/// </para>
/// <para>
/// Register at most one instance: registering several (e.g. one at each disclosure level) appends
/// one section per instance, which at <see cref="AuthRequirementDisclosure.Full"/> would leak the
/// values the default level withholds.
/// </para>
/// </remarks>
public sealed class OhDataOpenApiAuthRequirementsOperationTransformer : IOpenApiOperationTransformer
{
    private readonly AuthRequirementDisclosure _disclosure;

    /// <summary>
    /// Creates the transformer at the given disclosure level.
    /// </summary>
    /// <param name="disclosure">
    /// How much detail to render. Defaults to <see cref="AuthRequirementDisclosure.Kinds"/>, which
    /// never emits exact claim values.
    /// </param>
    public OhDataOpenApiAuthRequirementsOperationTransformer(
        AuthRequirementDisclosure disclosure = AuthRequirementDisclosure.Kinds)
    {
        _disclosure = disclosure;
    }

    /// <inheritdoc/>
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        OhDataOperationAuthMetadata? metadata = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<OhDataOperationAuthMetadata>()
            .FirstOrDefault();
        if (metadata is null)
        {
            return Task.CompletedTask;
        }

        string? requirements = OhDataAuthRequirementsText.Render(metadata.Requirements, _disclosure);
        if (requirements is null)
        {
            return Task.CompletedTask;
        }

        operation.Description = OhDataAuthRequirementsText.AppendSection(operation.Description, requirements);
        return Task.CompletedTask;
    }
}
