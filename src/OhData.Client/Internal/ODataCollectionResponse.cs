using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhData.Client.Internal;

/// <summary>
/// Deserialisation envelope for OData collection responses:
/// <c>{ "@odata.context": "...", "@odata.count": N, "value": [...] }</c>
/// </summary>
internal sealed class ODataCollectionResponse<T>
{
    [JsonPropertyName("@odata.context")]
    public string? Context { get; set; }

    [JsonPropertyName("@odata.count")]
    public long? Count { get; set; }

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }

    [JsonPropertyName("value")]
    public List<T> Value { get; set; } = [];
}
