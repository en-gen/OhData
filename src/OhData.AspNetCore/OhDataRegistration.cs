using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.OData.Edm;

namespace OhData;

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
        IReadOnlyList<UnboundOperationDefinition>? unboundOps = null,
        JsonNamingPolicy? jsonPropertyNamingPolicy = null)
    {
        Prefix = prefix;
        EdmModel = edmModel;
        Profiles = profiles;
        UnboundOperations = unboundOps ?? System.Array.Empty<UnboundOperationDefinition>();
        JsonPropertyNamingPolicy = jsonPropertyNamingPolicy;
    }

    /// <summary>The URL prefix under which all entity set routes are mounted, e.g. <c>"/odata"</c>.</summary>
    public string Prefix { get; }

    /// <summary>The compiled OData Entity Data Model (EDM) built from all registered profiles.</summary>
    public IEdmModel EdmModel { get; }

    internal IReadOnlyList<IEntitySetEndpointSource> Profiles { get; }
    internal IReadOnlyList<UnboundOperationDefinition> UnboundOperations { get; }

    /// <summary>
    /// #252: the JSON property-naming policy OhData applies to every response payload in this
    /// registration. <c>null</c> = PascalCase (matches <c>$metadata</c>, the default); a non-null
    /// value (e.g. <see cref="JsonNamingPolicy.CamelCase"/>) is an explicit opt-in. Owned by OhData
    /// rather than inherited from the host's <c>HttpJsonOptions</c>.
    /// </summary>
    internal JsonNamingPolicy? JsonPropertyNamingPolicy { get; }

    /// <summary>The OData entity set names exposed by this registration.</summary>
    public IEnumerable<string> EntitySetNames => Profiles.Select(p => p.EntitySetName);
}
