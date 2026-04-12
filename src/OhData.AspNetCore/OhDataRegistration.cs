using System.Collections.Generic;
using System.Linq;
using Microsoft.OData.Edm;
using OhData.Abstractions;

namespace OhData.AspNetCore;

/// <summary>
/// Holds the resolved state built at startup: the EDM model, registered profiles,
/// and route prefix. Resolved from DI as a singleton after <c>AddOhData()</c>.
/// </summary>
public sealed class OhDataRegistration
{
    internal OhDataRegistration(
        string prefix,
        IEdmModel edmModel,
        IReadOnlyList<IEntitySetEndpointSource> profiles,
        IReadOnlyList<UnboundOperationDefinition>? unboundOps = null)
    {
        Prefix = prefix;
        EdmModel = edmModel;
        Profiles = profiles;
        UnboundOperations = unboundOps ?? System.Array.Empty<UnboundOperationDefinition>();
    }

    public string Prefix { get; }
    public IEdmModel EdmModel { get; }
    internal IReadOnlyList<IEntitySetEndpointSource> Profiles { get; }
    internal IReadOnlyList<UnboundOperationDefinition> UnboundOperations { get; }

    public IEnumerable<string> EntitySetNames => Profiles.Select(p => p.EntitySetName);
}
