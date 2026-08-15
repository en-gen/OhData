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
        JsonNamingPolicy? jsonPropertyNamingPolicy = null,
        bool openTypesEnabled = true)
    {
        Prefix = prefix;
        EdmModel = edmModel;
        Profiles = profiles;
        UnboundOperations = unboundOps ?? System.Array.Empty<UnboundOperationDefinition>();
        JsonPropertyNamingPolicy = jsonPropertyNamingPolicy;
        OpenTypesEnabled = openTypesEnabled;
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

    /// <summary>
    /// #389: whether open complex types are enabled for this registration. <c>true</c> (the default —
    /// a complex type with a dictionary member <i>is</i> an open type, and the CSDL has always said
    /// so). <c>OhDataBuilder.WithOpenTypes(false)</c> sets it <c>false</c>, which means the open-type
    /// resolver modifier is never built and the write-side dynamic-key validation never runs, so the
    /// registration behaves exactly as it did before #389.
    /// </summary>
    internal bool OpenTypesEnabled { get; }

    /// <summary>
    /// #389 L1: whether open-type handling actually has anything to do — <see cref="OpenTypesEnabled"/>
    /// <b>and</b> the EDM really declares at least one open complex type. Set once by
    /// <c>OhDataEndpointFactory.MapAll</c>, which is the first place the container map exists.
    /// </summary>
    /// <remarks>
    /// <b>This gate is what makes default-on safe, and it is now load-bearing for every registration
    /// rather than for an opted-in minority.</b> Since <see cref="OpenTypesEnabled"/> defaults to
    /// <c>true</c>, the second conjunct — does the EDM actually declare an open complex type? — is the
    /// only thing standing between a model with no dictionary member anywhere and a changed response.
    /// It has to keep every such model byte-identical to a pre-#389 build, error responses included.
    /// <para>
    /// <see cref="OpenTypesEnabled"/> alone is the wrong gate for the per-request work, and the
    /// difference was observable. Gating the write paths on the flag meant a registration with no
    /// dictionary member still buffered every <c>PUT</c> body into a
    /// <see cref="System.Text.Json.JsonDocument"/> before deserializing — which changes the
    /// malformed-JSON error message, since <c>JsonDocument.ParseAsync</c> reports no <c>Path</c>
    /// where <c>JsonSerializer.DeserializeAsync</c> reports <c>Path: $</c>. Measured on a model with
    /// no open types, that was the entire delta from "byte-identical". Gating on this instead
    /// restores the claim and skips the buffering plus a full body walk on every write.
    /// <c>OpenTypeDefaultOnIsByteIdenticalTests</c> pins it across every write route and body shape.
    /// </para>
    /// </remarks>
    internal bool OpenTypesActive { get; set; }

    /// <summary>The OData entity set names exposed by this registration.</summary>
    public IEnumerable<string> EntitySetNames => Profiles.Select(p => p.EntitySetName);
}
