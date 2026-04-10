using Microsoft.OData.Edm;
using OhData.Abstractions;

namespace OhData.AspNetCore;

/// <summary>
/// Holds the resolved state built at startup: the EDM model, registered profiles,
/// and route prefix. Resolved from DI as a singleton after <c>AddOhData()</c>.
/// </summary>
public sealed class OhDataRegistration
{
    internal OhDataRegistration(string prefix, IEdmModel edmModel, IReadOnlyList<IEntitySetEndpointSource> profiles, OhDataOptions options)
    {
        Prefix = prefix;
        EdmModel = edmModel;
        Profiles = profiles;
        Options = options;
    }

    public string Prefix { get; }
    public IEdmModel EdmModel { get; }
    internal IReadOnlyList<IEntitySetEndpointSource> Profiles { get; }
    public OhDataOptions Options { get; }

    public IEnumerable<string> EntitySetNames => Profiles.Select(p => p.EntitySetName);
}
