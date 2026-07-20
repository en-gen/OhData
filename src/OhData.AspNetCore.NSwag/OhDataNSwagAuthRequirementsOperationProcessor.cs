using System.Linq;
using NSwag.Generation.AspNetCore;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;

namespace OhData.AspNetCore;

/// <summary>
/// Opt-in NSwag operation processor that appends a human-readable authorization-requirements
/// section to each operation's description, drawn from OhData's structured per-operation auth data
/// (#199) — the roles, claims, and named policies a route requires (#220).
/// </summary>
/// <remarks>
/// <para>
/// Off by default and layered on top of the baseline security reflection
/// (<see cref="OhDataNSwagSecurityOperationProcessor"/>): it does nothing until added to the
/// document's operation processors. It only describes what OhData itself configured — it never
/// defines the security scheme or identity. Register via:
/// <code>
/// builder.Services.AddOpenApiDocument(s =&gt;
///     s.OperationProcessors.Add(
///         new OhDataNSwagAuthRequirementsOperationProcessor(AuthRequirementDisclosure.Kinds)));
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
public sealed class OhDataNSwagAuthRequirementsOperationProcessor : IOperationProcessor
{
    private readonly AuthRequirementDisclosure _disclosure;

    /// <summary>
    /// Creates the processor at the given disclosure level.
    /// </summary>
    /// <param name="disclosure">
    /// How much detail to render. Defaults to <see cref="AuthRequirementDisclosure.Kinds"/>, which
    /// never emits exact claim values.
    /// </param>
    public OhDataNSwagAuthRequirementsOperationProcessor(
        AuthRequirementDisclosure disclosure = AuthRequirementDisclosure.Kinds)
    {
        _disclosure = disclosure;
    }

    /// <inheritdoc/>
    public bool Process(OperationProcessorContext context)
    {
        if (context is not AspNetCoreOperationProcessorContext aspNetCoreContext)
        {
            return true;
        }

        OhDataOperationAuthMetadata? metadata = aspNetCoreContext.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<OhDataOperationAuthMetadata>()
            .FirstOrDefault();
        if (metadata is null)
        {
            return true;
        }

        string? requirements = OhDataAuthRequirementsText.Render(metadata.Requirements, _disclosure);
        if (requirements is null)
        {
            return true;
        }

        var operation = context.OperationDescription.Operation;
        operation.Description = OhDataAuthRequirementsText.AppendSection(operation.Description, requirements);
        return true;
    }
}
