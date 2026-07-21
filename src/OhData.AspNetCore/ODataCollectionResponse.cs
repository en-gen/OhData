using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhData;

/// <summary>
/// Documentation-only shape of the OData collection response envelope emitted by collection
/// <c>GET</c> routes (OData Protocol §11.2.5, JSON Format §10). This type is never used to
/// actually serialize a response — the real response is built as an ordered
/// <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> so annotations (like
/// <c>@odata.context</c>) can be written before the entity properties they describe (JSON
/// §4.5). It exists purely so OpenAPI document generators (Microsoft.AspNetCore.OpenApi,
/// NSwag, Swashbuckle) can advertise the true wire shape via
/// <c>Produces&lt;ODataCollectionResponse&lt;T&gt;&gt;()</c> instead of a bare, schema-less
/// <c>200</c>.
/// </summary>
/// <typeparam name="T">The entity or navigation-item type carried in <see cref="Value"/>.</typeparam>
public sealed class ODataCollectionResponse<T>
{
    /// <summary>The OData context URL identifying the collection's metadata (§4.5.2).</summary>
    [JsonPropertyName("@odata.context")]
    public string ODataContext { get; set; } = "";

    /// <summary>
    /// The total count of matching entities when <c>$count=true</c> was requested (§11.2.6.5).
    /// Omitted from the wire response (and thus <see langword="null"/> here) otherwise.
    /// </summary>
    [JsonPropertyName("@odata.count")]
    public long? ODataCount { get; set; }

    /// <summary>
    /// A link to the next page of results, present only when the response was paged and more
    /// results remain. Omitted from the wire response (and thus <see langword="null"/> here)
    /// otherwise.
    /// </summary>
    [JsonPropertyName("@odata.nextLink")]
    public string? ODataNextLink { get; set; }

    /// <summary>The page of entities returned by this response.</summary>
    [JsonPropertyName("value")]
    public IReadOnlyList<T> Value { get; set; } = System.Array.Empty<T>();
}
