using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhData.AspNetCore;

/// <summary>
/// Documentation-only shape of the single structural-property response envelope emitted by
/// <c>GET /{EntitySet}({key})/{Property}</c> (OData Protocol §11.2.6, Part 2 §4.6). Never used
/// to actually serialize a response — see <see cref="ODataCollectionResponse{T}"/> for why.
/// </summary>
/// <typeparam name="T">The property's CLR type.</typeparam>
public sealed class ODataPropertyResponse<T>
{
    /// <summary>The OData context URL identifying the property's metadata (§4.5.2).</summary>
    [JsonPropertyName("@odata.context")]
    public string ODataContext { get; set; } = "";

    /// <summary>The property's value.</summary>
    [JsonPropertyName("value")]
    public T? Value { get; set; }
}

/// <summary>
/// Documentation-only shape of a single entity reference, as returned inside the <c>value</c>
/// array of a collection-navigation <c>$ref</c> response (§11.4.6.1).
/// </summary>
public sealed class ODataRef
{
    /// <summary>The canonical URL of the referenced entity.</summary>
    [JsonPropertyName("@odata.id")]
    public string ODataId { get; set; } = "";
}

/// <summary>
/// Documentation-only shape of the response returned by
/// <c>GET /{EntitySet}({key})/{Nav}/$ref</c> for a single-valued navigation property
/// (§11.4.6.1). Never used to actually serialize a response — see
/// <see cref="ODataCollectionResponse{T}"/> for why.
/// </summary>
public sealed class ODataRefResponse
{
    /// <summary>The OData context URL, <c>#$ref</c> (Protocol §10.12, JSON Format §14).</summary>
    [JsonPropertyName("@odata.context")]
    public string ODataContext { get; set; } = "";

    /// <summary>
    /// The canonical URL of the referenced entity. Omitted when the navigation target is
    /// <see langword="null"/> or the related entity set/key is not configured.
    /// </summary>
    [JsonPropertyName("@odata.id")]
    public string? ODataId { get; set; }
}

/// <summary>
/// Documentation-only shape of the response returned by
/// <c>GET /{EntitySet}({key})/{Nav}/$ref</c> for a collection-valued navigation property
/// (§11.4.6.1). Never used to actually serialize a response — see
/// <see cref="ODataCollectionResponse{T}"/> for why.
/// </summary>
public sealed class ODataRefCollectionResponse
{
    /// <summary>
    /// The OData context URL, <c>#Collection($ref)</c> (Protocol §10.12, JSON Format §14).
    /// </summary>
    [JsonPropertyName("@odata.context")]
    public string ODataContext { get; set; } = "";

    /// <summary>The referenced entities' canonical URLs.</summary>
    [JsonPropertyName("value")]
    public IReadOnlyList<ODataRef> Value { get; set; } = System.Array.Empty<ODataRef>();
}
