using System;
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
        string name,
        string prefix,
        IEdmModel edmModel,
        IReadOnlyList<IEntitySetEndpointSource> profiles,
        IReadOnlyList<UnboundOperationDefinition>? unboundOps = null,
        JsonNamingPolicy? jsonPropertyNamingPolicy = null,
        bool openTypesEnabled = true,
        long? defaultMaxRequestBodyBytes = EntitySetDefaults.DefaultMaxRequestBodyBytes)
    {
        DefaultMaxRequestBodyBytes = defaultMaxRequestBodyBytes;
        Name = name;
        Prefix = prefix;
        EdmModel = edmModel;
        Profiles = profiles;
        UnboundOperations = unboundOps ?? System.Array.Empty<UnboundOperationDefinition>();
        JsonPropertyNamingPolicy = jsonPropertyNamingPolicy;
        OpenTypesEnabled = openTypesEnabled;
    }

    /// <summary>
    /// #499: the keyed-DI registration name this instance was built for (<c>AddOhData(name, ...)</c>
    /// / <c>MapOhData(name)</c>; <see cref="OhDataDefaults.DefaultRegistrationName"/> for the
    /// unnamed overload). Used to scope process-wide static caches keyed by a route identifier
    /// (e.g. <c>ActionBodySchemaTypeFactory</c>) that would otherwise collide between two
    /// registrations declaring the same entity set / operation name.
    /// </summary>
    internal string Name { get; }

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
    /// #474: this registration's server-wide write-body ceiling —
    /// <c>EntitySetDefaults.MaxRequestBodyBytes</c> as configured, defaulting to
    /// <see cref="EntitySetDefaults.DefaultMaxRequestBodyBytes"/>. <c>null</c> when the adopter
    /// cleared it, which means "no OhData-level limit".
    /// </summary>
    /// <remarks>
    /// Read by the group-level body-limit filter as the fallback for a route that carries no
    /// <c>OhDataBodyLimitMetadata</c> of its own. Every entity-set route does carry it (the metadata
    /// is attached whenever the resolved per-profile limit is non-null, which is now the default),
    /// so in practice this covers the routes that belong to no entity set — the <b>unbound</b>
    /// actions. Carried here rather than re-derived in <c>MapAll</c> because <c>EntitySetDefaults</c>
    /// is a builder-time object that <c>MapAll</c> never sees.
    /// </remarks>
    internal long? DefaultMaxRequestBodyBytes { get; }

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

    /// <summary>
    /// #398 stage 1: for each CLR model type, the <b>JSON</b> names of the members that
    /// <c>EntitySetProfile.Ignore(...)</c> withholds. Set once by
    /// <c>OhDataEndpointFactory.MapAll</c>, alongside <see cref="OpenTypesActive"/>; empty for a
    /// registration that ignores nothing.
    /// </summary>
    /// <remarks>
    /// Carried on the registration rather than passed down the mapping call chain for the same reason
    /// <see cref="OpenTypesActive"/> is: the per-request open-type checks live inside route closures
    /// that already capture the registration, and every one of them needs both values together.
    /// <para>
    /// <b>Why it exists at all.</b> <c>Ignore(...)</c> works by removing the member from its
    /// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/>, and an open type's
    /// extension data captures precisely what a resolver modifier removed — so without a separate
    /// record of the withheld names, a write naming one would land in the dynamic bag and a read
    /// would echo it under the exact withheld name. The names are captured before the removal (see
    /// <c>IgnoredPropertyJsonOptions.BuildIgnoredJsonNameMap</c>), because afterwards the JSON name
    /// is not recoverable from the contract.
    /// </para>
    /// <para>
    /// Populated even when the registration has no open type. It costs one startup pass over a map
    /// that is usually empty, and keeping the two concerns independent means the entity-root widening
    /// (#398) does not have to remember to turn this on.
    /// </para>
    /// </remarks>
    /// <para>
    /// <b>Typed as <see cref="InheritedNameSets"/>, not as a dictionary (#462).</b> The three
    /// consumers in <c>OpenTypeJsonOptions</c> all looked this up by EXACT CLR type, which misses
    /// every derived runtime instance. Handing them a type with no exact-type accessor is what makes
    /// a fourth such site impossible to write rather than merely unlikely.
    /// </para>
    internal InheritedNameSets IgnoredJsonNamesByType { get; set; } = InheritedNameSets.Empty;

    /// <summary>The OData entity set names exposed by this registration.</summary>
    public IEnumerable<string> EntitySetNames => Profiles.Select(p => p.EntitySetName);
}
