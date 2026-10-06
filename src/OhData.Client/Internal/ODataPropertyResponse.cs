using System.Text.Json.Serialization;

namespace OhData.Client.Internal;

/// <summary>
/// Deserialisation envelope for a single-property response: <c>{ "@odata.context": "...", "value": ... }</c>.
/// <c>value</c> is required, so a body without it is a <see cref="System.Text.Json.JsonException"/>.
/// </summary>
internal sealed class ODataPropertyResponse<T>
{
    [JsonPropertyName("value")]
    [JsonRequired]
    public T? Value { get; set; }
}
